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
			files.Sort(StringComparer.OrdinalIgnoreCase);

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
				int[] currentPermutation = null;

				for (int f = 0; f < files.Count; ++f)
				{
					m_Progress = (float)f / files.Count;
					m_StatusMessage = $"Encoding frame {f + 1}/{files.Count}: {Path.GetFileName(files[f])}";
					Repaint();

					byte[] fileBytes = File.ReadAllBytes(files[f]);
					int count = 0;
					int degree = 0;
					Bounds frameBounds;

					// Load frame splat data via GaussianSplatLoadOperation
					float3[] currPositions;
					uint[] currRotations;

					using (var op = new GaussianSplatLoadOperation(fileBytes))
					{
						while (!op.isDone) op.Update();
						using var data = op.TakeData();
						if (data == null || data.count <= 0)
							throw new InvalidDataException($"Failed to load frame {f}: {files[f]}");

						count = data.count;
						degree = Math.Min(data.shDegree, m_MaxSHDegree);
						frameBounds = data.bounds;

						currPositions = new float3[count];
						currRotations = new uint[count];
						for (int i = 0; i < count; ++i)
						{
							currPositions[i] = new float3(
								math.asfloat(data.positions[i * 3 + 0]),
								math.asfloat(data.positions[i * 3 + 1]),
								math.asfloat(data.positions[i * 3 + 2])
							);
							currRotations[i] = data.geometry[i * 3 + 0];
						}
					}

					maxSplats = Math.Max(maxSplats, count);
					globalMin = math.min(globalMin, (float3)frameBounds.min);
					globalMax = math.max(globalMax, (float3)frameBounds.max);

					bool isKeyframe = (f % m_GopSize) == 0 || prevPositions == null || prevPositions.Length != count;
					ulong chunkOffset = (ulong)outStream.Position;

					if (isKeyframe)
					{
						// Compute Morton curve sorting permutation if enabled
						if (m_EnableMortonSort)
						{
							currentPermutation = new int[count];
							uint[] morton = new uint[count];
							float3 bMin = (float3)frameBounds.min;
							float3 bSize = math.max(new float3(1e-4f), (float3)frameBounds.size);
							for (int i = 0; i < count; ++i)
							{
								currentPermutation[i] = i;
								float3 norm = math.saturate((currPositions[i] - bMin) / bSize);
								uint3 grid = (uint3)math.round(norm * 1023.0f);
								morton[i] = Morton3D(grid.x, grid.y, grid.z);
							}
							Array.Sort(morton, currentPermutation);

							// Apply permutation to currPositions and currRotations
							var sortedPos = new float3[count];
							var sortedRot = new uint[count];
							for (int i = 0; i < count; ++i)
							{
								int orig = currentPermutation[i];
								sortedPos[i] = currPositions[orig];
								sortedRot[i] = currRotations[orig];
							}
							currPositions = sortedPos;
							currRotations = sortedRot;
						}
						else
						{
							currentPermutation = null;
						}

						// Convert frame to SPZ v4 keyframe
						byte[] spzBytes;
						bool isPly = !(fileBytes.Length >= 2 && ((fileBytes[0] == 0x1f && fileBytes[1] == 0x8b) || (fileBytes[0] == 'N' && fileBytes[1] == 'G')));
						if (isPly)
						{
							spzBytes = SPZTranscoder.ConvertPlyToSpzV4(fileBytes, m_MaxSHDegree, out _, out _, out _);
						}
						else
						{
							spzBytes = fileBytes;
						}

						outStream.Write(spzBytes, 0, spzBytes.Length);

						indexTable[f] = new FourDGSFormat.FrameIndexEntry
						{
							frameOffset = chunkOffset,
							compressedSize = (uint)spzBytes.Length,
							uncompressedSize = (uint)spzBytes.Length,
							splatCount = (uint)count,
							frameType = (uint)FourDGSFormat.FrameType.KeyframeSpz
						};

						prevPositions = (float3[])currPositions.Clone();
						prevRotations = (uint[])currRotations.Clone();
					}
					else
					{
						// Apply permutation if active
						if (currentPermutation != null && currentPermutation.Length == count)
						{
							var permutedPos = new float3[count];
							var permutedRot = new uint[count];
							for (int i = 0; i < count; ++i)
							{
								int orig = currentPermutation[i];
								permutedPos[i] = currPositions[orig];
								permutedRot[i] = currRotations[orig];
							}
							currPositions = permutedPos;
							currRotations = permutedRot;
						}

						// Compute real quantized deltas relative to prevPositions
						byte[] deltaChunk = EncodeRealDeltaChunk(prevPositions, currPositions, prevRotations, currRotations, out float deltaPosScale);
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

		static byte[] EncodeRealDeltaChunk(
			float3[] prevPos,
			float3[] currPos,
			uint[] prevRot,
			uint[] currRot,
			out float deltaScale)
		{
			int count = currPos.Length;
			float maxAbsDelta = 0.0001f;
			for (int i = 0; i < count; ++i)
			{
				float3 d = math.abs(currPos[i] - prevPos[i]);
				maxAbsDelta = math.max(maxAbsDelta, math.cmax(d));
			}

			// Scale so max displacement maps to 32767 (int16 max)
			deltaScale = maxAbsDelta / 32767.0f;
			float invScale = 1.0f / deltaScale;

			int maskBytes = (count + 7) >> 3;
			byte[] mask = new byte[maskBytes];
			short[] dxArr = new short[count];
			short[] dyArr = new short[count];
			short[] dzArr = new short[count];
			uint[] rotDeltaArr = new uint[count];
			bool anyRotDelta = false;

			for (int i = 0; i < count; ++i)
			{
				float3 diff = currPos[i] - prevPos[i];
				short dx = (short)Mathf.Clamp(Mathf.RoundToInt(diff.x * invScale), -32767, 32767);
				short dy = (short)Mathf.Clamp(Mathf.RoundToInt(diff.y * invScale), -32767, 32767);
				short dz = (short)Mathf.Clamp(Mathf.RoundToInt(diff.z * invScale), -32767, 32767);

				dxArr[i] = dx;
				dyArr[i] = dy;
				dzArr[i] = dz;

				// Reconstruct quantized position so errors don't accumulate over the GOP!
				prevPos[i] = prevPos[i] + new float3(dx, dy, dz) * deltaScale;

				uint rXor = currRot[i] ^ prevRot[i];
				rotDeltaArr[i] = rXor;
				prevRot[i] = currRot[i];
				if (rXor != 0) anyRotDelta = true;

				if (dx != 0 || dy != 0 || dz != 0 || rXor != 0)
					mask[i >> 3] |= (byte)(1 << (i & 7));
			}

			byte flags = (byte)(anyRotDelta ? 1 : 0);
			int totalBytes = 8 + maskBytes + (count * 6) + (anyRotDelta ? count * 4 : 0);
			byte[] deltaChunk = new byte[totalBytes];
			using var ms = new MemoryStream(deltaChunk);
			using var w = new BinaryWriter(ms);
			w.Write(deltaScale);
			w.Write(flags);
			w.Write((byte)0);
			w.Write((byte)0);
			w.Write((byte)0);
			w.Write(mask);
			for (int i = 0; i < count; ++i) w.Write(dxArr[i]);
			for (int i = 0; i < count; ++i) w.Write(dyArr[i]);
			for (int i = 0; i < count; ++i) w.Write(dzArr[i]);
			if (anyRotDelta)
			{
				for (int i = 0; i < count; ++i) w.Write(rotDeltaArr[i]);
			}
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
