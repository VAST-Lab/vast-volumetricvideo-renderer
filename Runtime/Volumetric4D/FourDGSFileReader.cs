// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class FourDGSFileReader : IDisposable
    {
        public FourDGSFormat.Header header { get; private set; }
        public FourDGSFormat.FrameIndexEntry[] indexTable { get; private set; }

        Stream m_Stream;
        readonly bool m_OwnsStream;
        byte[] m_FileBytes;

        // Persistent decode buffers for current frame state
        NativeArray<uint> m_Positions;
        NativeArray<uint> m_Geometry;
        NativeArray<uint> m_Colors;
        NativeArray<uint> m_SH;
        NativeArray<byte> m_ZstdScratch;
        NativeArray<byte> m_DeltaScratch;

        int m_CurrentDecodedFrame = -1;
        bool m_Disposed;

        public bool isLoaded => header.isValid && indexTable != null;
        public int currentFrame => m_CurrentDecodedFrame;
        public int totalFrames => (int)header.totalFrames;
        public int splatCount => (int)header.maxSplatCount;
        public Bounds bounds => header.bounds;

        public NativeArray<uint> positions => m_Positions;
        public NativeArray<uint> geometry => m_Geometry;
        public NativeArray<uint> colors => m_Colors;
        public NativeArray<uint> sh => m_SH;

        public FourDGSFileReader(Stream stream, bool ownsStream = true)
        {
            m_Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            m_OwnsStream = ownsStream;
            ReadMetadata();
            AllocateBuffers();
        }

        public FourDGSFileReader(byte[] bytes)
        {
            m_FileBytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
            m_Stream = new MemoryStream(bytes, writable: false);
            m_OwnsStream = true;
            ReadMetadata();
            AllocateBuffers();
        }

        void ReadMetadata()
        {
            header = FourDGSFormat.ReadHeader(m_Stream);
            indexTable = FourDGSFormat.ReadIndexTable(m_Stream, header);
        }

        void AllocateBuffers()
        {
            int maxSplats = (int)header.maxSplatCount;
            int shCoeffs = (header.shDegree + 1) * (header.shDegree + 1) - 1;
            int shWords = shCoeffs == 0 ? 1 : maxSplats * (GaussianUtils.GetSHDataSize(shCoeffs) / 4);

            m_Positions = new NativeArray<uint>(maxSplats * 3, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_Geometry = new NativeArray<uint>(maxSplats * 3, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_Colors = new NativeArray<uint>(maxSplats, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_SH = new NativeArray<uint>(shWords, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            m_ZstdScratch = new NativeArray<byte>(6 * ZstdDecoder.ScratchSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            // Delta stream scratch buffer: header (8B) + mask + int16 deltas (6B/splat) + rot (4B) + col (4B)
            int maxDeltaBytes = 64 + (maxSplats + 7) / 8 + maxSplats * 16;
            m_DeltaScratch = new NativeArray<byte>(Math.Max(maxDeltaBytes, 1024 * 1024), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        public byte[] ReadChunk(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= indexTable.Length)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));

            var entry = indexTable[frameIndex];
            byte[] chunk = new byte[entry.compressedSize];
            m_Stream.Position = (long)entry.frameOffset;
            int read = m_Stream.Read(chunk, 0, (int)entry.compressedSize);
            if (read != entry.compressedSize)
                throw new EndOfStreamException($"Truncated chunk at frame {frameIndex}");
            return chunk;
        }

        public unsafe void DecodeFrame(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= totalFrames)
                return;

            // Sequential P-frame optimization
            if (frameIndex == m_CurrentDecodedFrame + 1 && indexTable[frameIndex].type == FourDGSFormat.FrameType.DeltaPFrame)
            {
                ApplyPFrame(frameIndex);
                m_CurrentDecodedFrame = frameIndex;
                return;
            }

            // Seek to preceding keyframe
            int keyframe = 0;
            if (header.gopSize > 0)
                keyframe = (frameIndex / header.gopSize) * header.gopSize;

            while (keyframe > 0 && indexTable[keyframe].type == FourDGSFormat.FrameType.DeltaPFrame)
                --keyframe;

            LoadKeyframe(keyframe);
            m_CurrentDecodedFrame = keyframe;

            // Sequentially apply deltas up to target frame
            for (int f = keyframe + 1; f <= frameIndex; ++f)
            {
                if (indexTable[f].type == FourDGSFormat.FrameType.DeltaPFrame)
                    ApplyPFrame(f);
                else
                    LoadKeyframe(f);
                m_CurrentDecodedFrame = f;
            }
        }

        unsafe void LoadKeyframe(int frameIndex)
        {
            byte[] chunk = ReadChunk(frameIndex);
            using var ms = new MemoryStream(chunk, writable: false);
            var spzHeader = SPZFileReader.ReadHeader(ms);
            var streamInfos = SPZFileReader.ReadStreams(ms, spzHeader);

            int compressedDataSize = checked((int)(ms.Length - ms.Position));
            using var compressedNative = new NativeArray<byte>(compressedDataSize, Allocator.TempJob);
            fixed (byte* pChunk = chunk)
            {
                UnsafeUtility.MemCpy(compressedNative.GetUnsafePtr(), pChunk + ms.Position, compressedDataSize);
            }

            int uncompressedTotal = spzHeader.dataSize;
            using var uncompressedNative = new NativeArray<byte>(uncompressedTotal, Allocator.TempJob);

            var decompressJob = new SPZFileReader.DecompressJob
            {
                input = compressedNative,
                output = uncompressedNative,
                scratch = m_ZstdScratch,
                streams = streamInfos
            };
            decompressJob.Schedule(streamInfos.Length, 1).Complete();

            // Prepare data job
            int batches = (spzHeader.count + GaussianSplatPrepareDataJob.kBatchSize - 1) / GaussianSplatPrepareDataJob.kBatchSize;
            using var batchResults = new NativeArray<GaussianSplatPrepareDataJob.BatchResult>(batches, Allocator.TempJob);
            using var dummyPlyProps = new NativeArray<int>(0, Allocator.TempJob);

            var prepJob = new GaussianSplatPrepareDataJob
            {
                input = uncompressedNative,
                plyPropOffsets = dummyPlyProps,
                stride = 0,
                count = spzHeader.count,
                outputStart = 0,
                shCoefficients = (spzHeader.degree + 1) * (spzHeader.degree + 1) - 1,
                spzVersion = spzHeader.version,
                spzTotalCount = spzHeader.count,
                spzFractionalBits = spzHeader.fractionalBits,
                positions = m_Positions,
                geometry = m_Geometry,
                colors = m_Colors,
                sh = m_SH,
                results = batchResults
            };
            prepJob.Schedule(batches, 1).Complete();
            streamInfos.Dispose();
        }

        unsafe void ApplyPFrame(int frameIndex)
        {
            byte[] chunk = ReadChunk(frameIndex);
            var entry = indexTable[frameIndex];

            using var compressedNative = new NativeArray<byte>(chunk.Length, Allocator.TempJob);
            fixed (byte* pChunk = chunk)
            {
                UnsafeUtility.MemCpy(compressedNative.GetUnsafePtr(), pChunk, chunk.Length);
            }

            var err = ZstdDecoder.Decompress(compressedNative, m_DeltaScratch, m_ZstdScratch, out int written);
            if (err != ZstdDecoder.Error.None)
                throw new InvalidDataException($"Failed to decompress 4DGS delta frame {frameIndex}: {err}");

            byte* pDelta = (byte*)m_DeltaScratch.GetUnsafeReadOnlyPtr();
            float deltaPosScale = *(float*)pDelta;
            byte flags = *(pDelta + 4);

            int splats = (int)entry.splatCount;
            int batches = (splats + Apply4DGSDeltaJob.kBatchSize - 1) / Apply4DGSDeltaJob.kBatchSize;

            var job = new Apply4DGSDeltaJob
            {
                decompressedDeltaStream = m_DeltaScratch,
                splatCount = splats,
                deltaPosScale = deltaPosScale,
                flags = flags,
                positions = m_Positions,
                geometry = m_Geometry,
                colors = m_Colors
            };
            job.Schedule(batches, 1).Complete();
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;

            if (m_Positions.IsCreated) m_Positions.Dispose();
            if (m_Geometry.IsCreated) m_Geometry.Dispose();
            if (m_Colors.IsCreated) m_Colors.Dispose();
            if (m_SH.IsCreated) m_SH.Dispose();
            if (m_ZstdScratch.IsCreated) m_ZstdScratch.Dispose();
            if (m_DeltaScratch.IsCreated) m_DeltaScratch.Dispose();

            if (m_OwnsStream)
                m_Stream?.Dispose();
            m_Stream = null;
        }

        [BurstCompile(CompileSynchronously = true)]
        public unsafe struct Apply4DGSDeltaJob : IJobParallelFor
        {
            public const int kBatchSize = 1024;

            [ReadOnly] public NativeArray<byte> decompressedDeltaStream;
            public int splatCount;
            public float deltaPosScale;
            public byte flags; // bit 0: hasRot, bit 1: hasCol, bit 2: hasScale

            [NativeDisableParallelForRestriction] public NativeArray<uint> positions;
            [NativeDisableParallelForRestriction] public NativeArray<uint> geometry;
            [NativeDisableParallelForRestriction] public NativeArray<uint> colors;

            public void Execute(int batchIndex)
            {
                int start = batchIndex * kBatchSize;
                int end = math.min(splatCount, start + kBatchSize);
                int total = splatCount;

                byte* src = (byte*)decompressedDeltaStream.GetUnsafeReadOnlyPtr();
                // Offset 8: Start of active motion bitmask
                int headerBytes = 8;
                int maskBytes = (total + 7) >> 3;
                byte* maskPtr = src + headerBytes;

                // Stream 1: Quantized Int16 deltas
                short* dxPtr = (short*)(maskPtr + maskBytes);
                short* dyPtr = dxPtr + total;
                short* dzPtr = dyPtr + total;

                // Stream 2: Rotation XORs (optional)
                uint* rotPtr = (flags & 1) != 0 ? (uint*)(dzPtr + total) : null;
                // Stream 3: Color updates (optional)
                uint* colPtr = (flags & 2) != 0 ? (uint*)((byte*)(rotPtr != null ? (byte*)(rotPtr + total) : (byte*)(dzPtr + total))) : null;

                for (int i = start; i < end; ++i)
                {
                    byte mask = maskPtr[i >> 3];
                    if ((mask & (1 << (i & 7))) == 0)
                        continue; // Splat did not move

                    float3 pos = new float3(
                        math.asfloat(positions[i * 3 + 0]),
                        math.asfloat(positions[i * 3 + 1]),
                        math.asfloat(positions[i * 3 + 2])
                    );

                    pos.x += dxPtr[i] * deltaPosScale;
                    pos.y += dyPtr[i] * deltaPosScale;
                    pos.z += dzPtr[i] * deltaPosScale;

                    positions[i * 3 + 0] = math.asuint(pos.x);
                    positions[i * 3 + 1] = math.asuint(pos.y);
                    positions[i * 3 + 2] = math.asuint(pos.z);

                    if (rotPtr != null)
                        geometry[i * 3 + 0] ^= rotPtr[i];

                    if (colPtr != null)
                        colors[i] = colPtr[i];
                }
            }
        }
    }
}
