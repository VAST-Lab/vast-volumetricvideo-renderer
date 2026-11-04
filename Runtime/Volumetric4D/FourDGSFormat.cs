// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public static class FourDGSFormat
    {
        public const uint kMagic = 0x53474434; // "4DGS" in little-endian ASCII
        public const uint kVersion = 1;

        [Flags]
        public enum Flags : uint
        {
            None = 0,
            DeltaEncoded = 1 << 0,
            Loopable = 1 << 1,
            HasAudio = 1 << 2,
            HasNormals = 1 << 3
        }

        public enum FrameType : uint
        {
            KeyframeSpz = 0,    // Full SPZ v4 Keyframe (I-Frame)
            DeltaPFrame = 1,    // Temporal delta from previous frame (P-Frame)
            IndependentSpz = 2  // Topology-varying independent SPZ frame
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
        public struct Header
        {
            public uint magic;                  // 4 bytes: 0x53474434 ("4DGS")
            public uint version;                // 4 bytes: 1
            public uint flags;                  // 4 bytes: FourDGSFormat.Flags
            public uint maxSplatCount;          // 4 bytes: Max splats in clip
            public byte shDegree;               // 1 byte: 0..3
            public byte fractionalBits;         // 1 byte: default 12
            public ushort gopSize;              // 2 bytes: e.g. 30
            public float frameRate;             // 4 bytes: e.g. 30.0f
            public uint totalFrames;            // 4 bytes
            public Vector3 boundsMin;           // 12 bytes
            public Vector3 boundsMax;           // 12 bytes
            public ulong frameIndexTableOffset; // 8 bytes
            public uint reserved;               // 4 bytes (exactly 64 bytes total)

            public Bounds bounds
            {
                get
                {
                    var b = new Bounds();
                    b.SetMinMax(boundsMin, boundsMax);
                    return b;
                }
                set
                {
                    boundsMin = value.min;
                    boundsMax = value.max;
                }
            }

            public bool isValid => magic == kMagic && version == kVersion && totalFrames > 0;
            public bool isDeltaEncoded => (flags & (uint)Flags.DeltaEncoded) != 0;
            public bool isLoopable => (flags & (uint)Flags.Loopable) != 0;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 24)]
        public struct FrameIndexEntry
        {
            public ulong frameOffset;        // 8 bytes: offset in file to payload chunk
            public uint compressedSize;      // 4 bytes: compressed byte size
            public uint uncompressedSize;    // 4 bytes: uncompressed byte size
            public uint splatCount;          // 4 bytes: splat count for this frame
            public uint frameType;           // 4 bytes: FourDGSFormat.FrameType

            public FrameType type => (FrameType)frameType;
        }

        public static Header ReadHeader(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var header = new Header
            {
                magic = reader.ReadUInt32(),
                version = reader.ReadUInt32(),
                flags = reader.ReadUInt32(),
                maxSplatCount = reader.ReadUInt32(),
                shDegree = reader.ReadByte(),
                fractionalBits = reader.ReadByte(),
                gopSize = reader.ReadUInt16(),
                frameRate = reader.ReadSingle(),
                totalFrames = reader.ReadUInt32(),
                boundsMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                boundsMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                frameIndexTableOffset = reader.ReadUInt64(),
                reserved = reader.ReadUInt32()
            };

            if (header.magic != kMagic)
                throw new InvalidDataException($"Invalid .4dgs magic: 0x{header.magic:X8} (expected 0x{kMagic:X8})");
            if (header.version != kVersion)
                throw new InvalidDataException($"Unsupported .4dgs version: {header.version} (expected {kVersion})");

            return header;
        }

        public static FrameIndexEntry[] ReadIndexTable(Stream stream, Header header)
        {
            stream.Position = (long)header.frameIndexTableOffset;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var table = new FrameIndexEntry[header.totalFrames];
            for (int i = 0; i < header.totalFrames; ++i)
            {
                table[i] = new FrameIndexEntry
                {
                    frameOffset = reader.ReadUInt64(),
                    compressedSize = reader.ReadUInt32(),
                    uncompressedSize = reader.ReadUInt32(),
                    splatCount = reader.ReadUInt32(),
                    frameType = reader.ReadUInt32()
                };
            }
            return table;
        }

        public static void WriteHeader(Stream stream, Header header)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(header.magic);
            writer.Write(header.version);
            writer.Write(header.flags);
            writer.Write(header.maxSplatCount);
            writer.Write(header.shDegree);
            writer.Write(header.fractionalBits);
            writer.Write(header.gopSize);
            writer.Write(header.frameRate);
            writer.Write(header.totalFrames);
            writer.Write(header.boundsMin.x);
            writer.Write(header.boundsMin.y);
            writer.Write(header.boundsMin.z);
            writer.Write(header.boundsMax.x);
            writer.Write(header.boundsMax.y);
            writer.Write(header.boundsMax.z);
            writer.Write(header.frameIndexTableOffset);
            writer.Write(header.reserved);
        }

        public static void WriteIndexTable(Stream stream, FrameIndexEntry[] table)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            for (int i = 0; i < table.Length; ++i)
            {
                writer.Write(table[i].frameOffset);
                writer.Write(table[i].compressedSize);
                writer.Write(table[i].uncompressedSize);
                writer.Write(table[i].splatCount);
                writer.Write(table[i].frameType);
            }
        }
    }
}
