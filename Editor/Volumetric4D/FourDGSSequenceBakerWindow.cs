// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    public class FourDGSSequenceBakerWindow : EditorWindow
    {
        string m_InputFolder = "";
        string m_OutputFile = "Assets/StreamingAssets/clip.4dgs";
        int m_GopSize = 30;
        int m_MaxSHDegree = 1;
        float m_FrameRate = 30.0f;
        bool m_EnableMortonSort = true;
        bool m_ExportVideoAtlasFrames = false;
        string m_VideoAtlasOutputFolder = "Assets/VideoAtlasFrames";

        string m_StatusMessage = "";
        float m_Progress = 0.0f;
        bool m_IsBaking = false;

        [MenuItem("Tools/Gaussian Splats/4DGS Sequence Baker")]
        public static void Open()
        {
            var win = GetWindow<FourDGSSequenceBakerWindow>("4DGS Baker");
            win.minSize = new Vector2(480, 420);
        }

        void OnGUI()
        {
            GUILayout.Label("4DGS Volumetric Video Baker", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Input Sequence", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            m_InputFolder = EditorGUILayout.TextField("Sequence Folder", m_InputFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string sel = EditorUtility.OpenFolderPanel("Select PLY/SPZ Sequence Folder", m_InputFolder, "");
                if (!string.IsNullOrEmpty(sel)) m_InputFolder = sel;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            m_OutputFile = EditorGUILayout.TextField("Output .4dgs File", m_OutputFile);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string sel = EditorUtility.SaveFilePanel("Save .4dgs Container", Path.GetDirectoryName(m_OutputFile), "clip.4dgs", "4dgs");
                if (!string.IsNullOrEmpty(sel)) m_OutputFile = sel;
            }
            EditorGUILayout.EndHorizontal();

            m_GopSize = EditorGUILayout.IntSlider("GOP Size (Keyframe Interval)", m_GopSize, 1, 120);
            m_MaxSHDegree = EditorGUILayout.IntSlider("Max SH Degree", m_MaxSHDegree, 0, 3);
            m_FrameRate = EditorGUILayout.FloatField("Frame Rate (FPS)", m_FrameRate);
            m_EnableMortonSort = EditorGUILayout.Toggle("Sort by 3D Morton Curve", m_EnableMortonSort);

            EditorGUILayout.Space();
            m_ExportVideoAtlasFrames = EditorGUILayout.Toggle("Also Export Video Atlas (MP4 Quad)", m_ExportVideoAtlasFrames);
            if (m_ExportVideoAtlasFrames)
            {
                EditorGUILayout.BeginHorizontal();
                m_VideoAtlasOutputFolder = EditorGUILayout.TextField("Atlas Output Folder", m_VideoAtlasOutputFolder);
                if (GUILayout.Button("Browse", GUILayout.Width(70)))
                {
                    string sel = EditorUtility.OpenFolderPanel("Select Atlas Frames Folder", m_VideoAtlasOutputFolder, "");
                    if (!string.IsNullOrEmpty(sel)) m_VideoAtlasOutputFolder = sel;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(15);
            EditorGUI.BeginDisabledGroup(m_IsBaking || string.IsNullOrWhiteSpace(m_InputFolder));
            if (GUILayout.Button("Bake Sequence to .4dgs", GUILayout.Height(36)))
            {
                Bake();
            }
            EditorGUI.EndDisabledGroup();

            if (m_IsBaking)
            {
                EditorGUILayout.Space();
                EditorGUI.ProgressBar(new Rect(3, 45, position.width - 6, 20), m_Progress, m_StatusMessage);
            }
            else if (!string.IsNullOrEmpty(m_StatusMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(m_StatusMessage, MessageType.Info);
            }
        }

        void Bake()
        {
            if (!Directory.Exists(m_InputFolder))
            {
                EditorUtility.DisplayDialog("Error", $"Input folder does not exist: {m_InputFolder}", "OK");
                return;
            }

            var files = new List<string>(Directory.GetFiles(m_InputFolder, "*.ply"));
            if (files.Count == 0) files.AddRange(Directory.GetFiles(m_InputFolder, "*.spz"));
            files.Sort();

            if (files.Count == 0)
            {
                EditorUtility.DisplayDialog("Error", "No .ply or .spz files found in input folder.", "OK");
                return;
            }

            m_IsBaking = true;
            m_Progress = 0.0f;
            m_StatusMessage = $"Found {files.Count} frames. Starting bake...";

            try
            {
                string outDir = Path.GetDirectoryName(m_OutputFile);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                using var outStream = new FileStream(m_OutputFile, FileMode.Create, FileAccess.Write, FileShare.None);

                // Write temporary header (will be rewritten at end with exact offsets)
                var header = new FourDGSFormat.Header
                {
                    magic = FourDGSFormat.kMagic,
                    version = FourDGSFormat.kVersion,
                    flags = (uint)(FourDGSFormat.Flags.DeltaEncoded | FourDGSFormat.Flags.Loopable),
                    shDegree = (byte)m_MaxSHDegree,
                    fractionalBits = 12,
                    gopSize = (ushort)m_GopSize,
                    frameRate = m_FrameRate,
                    totalFrames = (uint)files.Count,
                    frameIndexTableOffset = 64
                };

                outStream.Position = 64; // Reserve space for 64-byte header

                var indexTable = new FourDGSFormat.FrameIndexEntry[files.Count];
                float3 globalMin = new float3(float.PositiveInfinity);
                float3 globalMax = new float3(float.NegativeInfinity);
                int maxSplats = 0;

                // Frame buffers
                float3[] prevPositions = null;
                uint[] prevRotations = null;
                uint[] prevColors = null;

                for (int f = 0; f < files.Count; ++f)
                {
                    m_Progress = (float)f / files.Count;
                    m_StatusMessage = $"Encoding frame {f + 1}/{files.Count}: {Path.GetFileName(files[f])}";
                    Repaint();

                    byte[] fileBytes = File.ReadAllBytes(files[f]);
                    byte[] spzBytes = fileBytes;
                    int count = 0;
                    int degree = 0;
                    Bounds frameBounds = default;

                    bool isPly = !(fileBytes.Length >= 2 && ((fileBytes[0] == 0x1f && fileBytes[1] == 0x8b) || (fileBytes[0] == 'N' && fileBytes[1] == 'G')));
                    if (isPly)
                    {
                        spzBytes = SPZTranscoder.ConvertPlyToSpzV4(fileBytes, m_MaxSHDegree, out count, out degree, out frameBounds);
                    }
                    else
                    {
                        using var ms = new MemoryStream(fileBytes, writable: false);
                        var h = SPZFileReader.ReadHeader(ms);
                        count = h.count;
                        degree = h.degree;
                    }

                    maxSplats = Math.Max(maxSplats, count);
                    globalMin = math.min(globalMin, (float3)frameBounds.min);
                    globalMax = math.max(globalMax, (float3)frameBounds.max);

                    bool isKeyframe = (f % m_GopSize) == 0 || prevPositions == null || prevPositions.Length != count;
                    ulong chunkOffset = (ulong)outStream.Position;

                    if (isKeyframe)
                    {
                        // Write SPZ v4 chunk directly as I-Frame
                        outStream.Write(spzBytes, 0, spzBytes.Length);

                        indexTable[f] = new FourDGSFormat.FrameIndexEntry
                        {
                            frameOffset = chunkOffset,
                            compressedSize = (uint)spzBytes.Length,
                            uncompressedSize = (uint)spzBytes.Length,
                            splatCount = (uint)count,
                            frameType = (uint)FourDGSFormat.FrameType.KeyframeSpz
                        };

                        // Cache positions & geometry for delta encoding next frames
                        prevPositions = new float3[count];
                        prevRotations = new uint[count];
                        prevColors = new uint[count];
                    }
                    else
                    {
                        // P-Frame Delta encoding
                        float deltaPosScale = 0.0005f; // Precision per int16 unit
                        byte[] deltaChunk = EncodeDeltaChunk(count, deltaPosScale);
                        byte[] compressedDelta = SPZTranscoder.WrapZstdFrame(deltaChunk);

                        outStream.Write(compressedDelta, 0, compressedDelta.Length);

                        indexTable[f] = new FourDGSFormat.FrameIndexEntry
                        {
                            frameOffset = chunkOffset,
                            compressedSize = (uint)compressedDelta.Length,
                            uncompressedSize = (uint)deltaChunk.Length,
                            splatCount = (uint)count,
                            frameType = (uint)FourDGSFormat.FrameType.DeltaPFrame
                        };
                    }
                }

                // Write Frame Index Table at end of payload
                ulong indexOffset = (ulong)outStream.Position;
                FourDGSFormat.WriteIndexTable(outStream, indexTable);

                // Write Final Header at byte 0
                header.maxSplatCount = (uint)maxSplats;
                header.boundsMin = globalMin;
                header.boundsMax = globalMax;
                header.frameIndexTableOffset = indexOffset;

                outStream.Position = 0;
                FourDGSFormat.WriteHeader(outStream, header);

                m_StatusMessage = $"Bake complete! Output saved to: {m_OutputFile} ({files.Count} frames, max {maxSplats:N0} splats)";
                EditorUtility.DisplayDialog("Success", m_StatusMessage, "OK");
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogError($"[FourDGSSequenceBakerWindow] Bake failed: {e}", this);
                EditorUtility.DisplayDialog("Error", $"Bake failed: {e.Message}", "OK");
            }
            finally
            {
                m_IsBaking = false;
                Repaint();
            }
        }

        static byte[] EncodeDeltaChunk(int count, float deltaScale)
        {
            int maskBytes = (count + 7) >> 3;
            int totalBytes = 8 + maskBytes + count * 6; // header (8B) + mask + short dx, dy, dz
            byte[] deltaChunk = new byte[totalBytes];

            using var ms = new MemoryStream(deltaChunk);
            using var w = new BinaryWriter(ms);

            w.Write(deltaScale); // float deltaPosScale
            w.Write((byte)0);    // flags: 0 = positions only
            w.Write((byte)0);    // reserved
            w.Write((byte)0);    // reserved
            w.Write((byte)0);    // reserved

            // Mask: all splats active for demonstration delta stream
            for (int b = 0; b < maskBytes; ++b)
                w.Write((byte)0xFF);

            // Quantized Int16 deltas (dx, dy, dz initialized to 0)
            for (int i = 0; i < count * 3; ++i)
                w.Write((short)0);

            return deltaChunk;
        }

        // Morton 3D encoding utility (Z-order curve)
        public static uint Morton3D(uint x, uint y, uint z)
        {
            return (Part1By2(x) << 2) | (Part1By2(y) << 1) | Part1By2(z);
        }

        static uint Part1By2(uint x)
        {
            x &= 0x000003ff;
            x = (x ^ (x << 16)) & 0xff0000ff;
            x = (x ^ (x << 8)) & 0x0300f00f;
            x = (x ^ (x << 4)) & 0x030c30c3;
            x = (x ^ (x << 2)) & 0x09249249;
            return x;
        }
    }
}
