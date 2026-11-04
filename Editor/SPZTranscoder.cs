// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    public static class SPZTranscoder
    {
        const uint kZstdMagic = 0xFD2FB528;
        const int kZstdMaxBlockSize = 128 * 1024; // 128 KB standard Zstd block limit

        /// <summary>
        /// Wraps an uncompressed byte buffer into a standard Zstandard frame containing raw blocks.
        /// Fully compliant with RFC 8878 and decodable by ZstdDecoder.cs and libzstd.
        /// </summary>
        public static byte[] WrapZstdFrame(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // 1. Zstandard Magic Number (0xFD2FB528)
            writer.Write(kZstdMagic);

            // 2. Frame Header Descriptor:
            // Single_Segment_Flag = 1 (bit 5: 32)
            // Frame_Content_Size_Flag = 2 (bits 6-7: 2 << 6 = 128 -> 4-byte size)
            // Flags = 32 | 128 = 160 (0xA0)
            writer.Write((byte)0xA0);

            // Frame Content Size (4 bytes, little-endian)
            writer.Write((uint)data.Length);

            // 3. Data Blocks (each at most 128 KB)
            int offset = 0;
            while (offset < data.Length)
            {
                int remaining = data.Length - offset;
                int blockSize = Math.Min(remaining, kZstdMaxBlockSize);
                bool isLast = (offset + blockSize) >= data.Length;

                // Block Header (3 bytes, little-endian):
                // Bit 0: Last_Block
                // Bits 1-2: Block_Type (0 = Raw_Block)
                // Bits 3-23: Block_Size (21 bits)
                int header = (isLast ? 1 : 0) | (0 << 1) | (blockSize << 3);
                writer.Write((byte)(header & 0xFF));
                writer.Write((byte)((header >> 8) & 0xFF));
                writer.Write((byte)((header >> 16) & 0xFF));

                // Raw block payload
                writer.Write(data, offset, blockSize);
                offset += blockSize;
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Transcodes a raw PLY file byte array into a compact SPZ v4 binary payload.
        /// </summary>
        public static byte[] ConvertPlyToSpzV4(byte[] plyBytes, int maxSHDegree, out int splatCount, out int outDegree, out Bounds bounds)
        {
            using var stream = new MemoryStream(plyBytes, writable: false);
            var plyHeader = PLYFileReader.ReadHeader(stream);
            try
            {
                splatCount = plyHeader.count;
                outDegree = Math.Min(plyHeader.degree, Math.Max(0, maxSHDegree));
                int fractionalBits = 12; // Standard 1/4096 precision for fixed-24 coordinates
                float fractionScale = 1 << fractionalBits;

                int shCoeffs = (outDegree + 1) * (outDegree + 1) - 1;
                int plyCoeffs = (plyHeader.degree + 1) * (plyHeader.degree + 1) - 1;

                // Allocate uncompressed stream buffers
                byte[] posStream = new byte[splatCount * 9];
                byte[] alphaStream = new byte[splatCount];
                byte[] colStream = new byte[splatCount * 3];
                byte[] scaleStream = new byte[splatCount * 3];
                byte[] rotStream = new byte[splatCount * 4];
                byte[] shStream = shCoeffs > 0 ? new byte[splatCount * shCoeffs * 3] : null;

                float3 minPos = new float3(float.PositiveInfinity);
                float3 maxPos = new float3(float.NegativeInfinity);

                byte[] rowBuffer = new byte[plyHeader.stride];
                for (int i = 0; i < splatCount; ++i)
                {
                    int bytesRead = stream.Read(rowBuffer, 0, plyHeader.stride);
                    if (bytesRead < plyHeader.stride)
                        break;

                    // Read PLY floats
                    float px = ReadFloat(rowBuffer, plyHeader.properties[0]);
                    float py = ReadFloat(rowBuffer, plyHeader.properties[1]);
                    float pz = ReadFloat(rowBuffer, plyHeader.properties[2]);

                    float3 pos = new float3(px, py, pz);
                    minPos = math.min(minPos, pos);
                    maxPos = math.max(maxPos, pos);

                    // Fixed-24 Positions
                    WriteFixed24(posStream, i * 9 + 0, px, fractionScale);
                    WriteFixed24(posStream, i * 9 + 3, py, fractionScale);
                    WriteFixed24(posStream, i * 9 + 6, pz, fractionScale);

                    // Scale: PLY log-scale -> SPZ byte: byte = (log_scale + 10) * 16
                    float sx = ReadFloat(rowBuffer, plyHeader.properties[3]);
                    float sy = ReadFloat(rowBuffer, plyHeader.properties[4]);
                    float sz = ReadFloat(rowBuffer, plyHeader.properties[5]);
                    scaleStream[i * 3 + 0] = (byte)Mathf.Clamp(Mathf.RoundToInt((sx + 10f) * 16f), 0, 255);
                    scaleStream[i * 3 + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt((sy + 10f) * 16f), 0, 255);
                    scaleStream[i * 3 + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt((sz + 10f) * 16f), 0, 255);

                    // Rotation: normalized quaternion -> SPZ smallest-3 uint32
                    float rx = ReadFloat(rowBuffer, plyHeader.properties[6]);
                    float ry = ReadFloat(rowBuffer, plyHeader.properties[7]);
                    float rz = ReadFloat(rowBuffer, plyHeader.properties[8]);
                    float rw = ReadFloat(rowBuffer, plyHeader.properties[9]);
                    float4 quat = math.normalize(new float4(rx, ry, rz, rw));
                    uint packedRot = GaussianUtils.EncodeSpzSmallest3Rotation(quat);
                    rotStream[i * 4 + 0] = (byte)(packedRot & 0xFF);
                    rotStream[i * 4 + 1] = (byte)((packedRot >> 8) & 0xFF);
                    rotStream[i * 4 + 2] = (byte)((packedRot >> 16) & 0xFF);
                    rotStream[i * 4 + 3] = (byte)((packedRot >> 24) & 0xFF);

                    // Color: f_dc_0..2 -> SPZ color byte: (dc * 0.15 + 0.5) * 255
                    float cr = ReadFloat(rowBuffer, plyHeader.properties[10]);
                    float cg = ReadFloat(rowBuffer, plyHeader.properties[11]);
                    float cb = ReadFloat(rowBuffer, plyHeader.properties[12]);
                    colStream[i * 3 + 0] = (byte)Mathf.Clamp(Mathf.RoundToInt((cr * 0.15f + 0.5f) * 255f), 0, 255);
                    colStream[i * 3 + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt((cg * 0.15f + 0.5f) * 255f), 0, 255);
                    colStream[i * 3 + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt((cb * 0.15f + 0.5f) * 255f), 0, 255);

                    // Opacity: logit -> Sigmoid -> 0..255 byte
                    float logit = ReadFloat(rowBuffer, plyHeader.properties[13]);
                    float op = float.IsFinite(logit) ? GaussianUtils.Sigmoid(logit) : 0f;
                    alphaStream[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(op * 255f), 0, 255);

                    // Spherical Harmonics (if degree > 0)
                    if (shCoeffs > 0 && shStream != null)
                    {
                        for (int c = 0; c < shCoeffs; ++c)
                        {
                            float shR = ReadFloat(rowBuffer, plyHeader.properties[14 + c]);
                            float shG = ReadFloat(rowBuffer, plyHeader.properties[14 + c + plyCoeffs]);
                            float shB = ReadFloat(rowBuffer, plyHeader.properties[14 + c + plyCoeffs * 2]);
                            int baseDst = i * shCoeffs * 3 + c * 3;
                            shStream[baseDst + 0] = (byte)Mathf.Clamp(Mathf.RoundToInt(shR * 128f + 128f), 0, 255);
                            shStream[baseDst + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(shG * 128f + 128f), 0, 255);
                            shStream[baseDst + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(shB * 128f + 128f), 0, 255);
                        }
                    }
                }

                bounds = new Bounds();
                bounds.SetMinMax(minPos, maxPos);

                // Compress attribute streams into Zstd frames
                byte[][] rawStreams = outDegree == 0
                    ? new[] { posStream, alphaStream, colStream, scaleStream, rotStream }
                    : new[] { posStream, alphaStream, colStream, scaleStream, rotStream, shStream };

                byte[][] compressedStreams = new byte[rawStreams.Length][];
                for (int s = 0; s < rawStreams.Length; ++s)
                {
                    compressedStreams[s] = WrapZstdFrame(rawStreams[s]);
                }

                // Write SPZ v4 Container
                using var outMs = new MemoryStream();
                using var writer = new BinaryWriter(outMs);

                // 1. Header (32 bytes)
                writer.Write(0x5053474Eu); // Magic: "NGSP"
                writer.Write(4u);          // Version: 4
                writer.Write((uint)splatCount);
                writer.Write((byte)outDegree);
                writer.Write((byte)fractionalBits);
                writer.Write((byte)0);     // Flags
                writer.Write((byte)rawStreams.Length); // Stream count (5 or 6)
                writer.Write(32u);         // TocOffset = 32 (immediately follows header)
                writer.Write(0u);          // Reserved 0
                writer.Write(0u);          // Reserved 1
                writer.Write(0u);          // Reserved 2

                // 2. TOC (rawStreams.Length * 16 bytes)
                for (int s = 0; s < rawStreams.Length; ++s)
                {
                    writer.Write((ulong)compressedStreams[s].Length);
                    writer.Write((ulong)rawStreams[s].Length);
                }

                // 3. Compressed Streams Data
                for (int s = 0; s < rawStreams.Length; ++s)
                {
                    writer.Write(compressedStreams[s]);
                }

                return outMs.ToArray();
            }
            finally
            {
                plyHeader.Dispose();
            }
        }

        static float ReadFloat(byte[] buffer, int offset)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        static void WriteFixed24(byte[] dst, int offset, float value, float scale)
        {
            int fixedVal = Mathf.RoundToInt(value * scale);
            dst[offset + 0] = (byte)(fixedVal & 0xFF);
            dst[offset + 1] = (byte)((fixedVal >> 8) & 0xFF);
            dst[offset + 2] = (byte)((fixedVal >> 16) & 0xFF);
        }
    }
}
