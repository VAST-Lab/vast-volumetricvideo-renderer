// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace GaussianSplatting.Runtime
{
	[ExecuteInEditMode]
	[RequireComponent(typeof(GaussianSplatRenderer))]
	public class GaussianSplat4DPlayer : MonoBehaviour
	{
		public enum SourceMode
		{
			FourDGSFile,
			FourDGSAsset,
			SequenceSpzAssets,
			SequenceFileFolder
		}

		[Header("Source")]
		public SourceMode m_SourceMode = SourceMode.FourDGSFile;

		[Tooltip("Path to .4dgs file or sequence folder (relative to StreamingAssets, project root, or absolute/URL)")]
		public string m_SourcePath;

		[Tooltip("Imported .4dgs asset (FourDGSAsset)")]
		public FourDGSAsset m_FourDGSAsset;

		[Tooltip("Optional raw .4dgs.bytes TextAsset fallback")]
		public TextAsset m_FourDGSTextAsset;

		[Tooltip("Sequence of pre-baked GaussianSplatAsset frames")]
		public List<GaussianSplatAsset> m_SpzSequence = new();

		[Header("Playback")]
		public bool m_PlayOnAwake = true;
		public bool m_Loop = true;
		[Range(0.0f, 4.0f)]
		public float m_PlaybackSpeed = 1.0f;
		[Range(1.0f, 120.0f)]
		public float m_SequenceFrameRate = 30.0f;

		[Header("State")]
		[SerializeField] int m_CurrentFrame = 0;

		public int currentFrame => m_CurrentFrame;
		public int totalFrames => m_TotalFrames;
		public float frameRate => m_FrameRate;
		public bool isPlaying => m_IsPlaying;
		public bool isInitialized => m_IsInitialized;
		public string lastError => m_LastError;
		public int activeSplatCount => m_Renderer != null ? m_Renderer.splatCount : 0;
		public double duration => m_TotalFrames > 0 && m_FrameRate > 0 ? m_TotalFrames / (double)m_FrameRate : 0.0;

		public double time
		{
			get => m_CurrentFrame / (double)Mathf.Max(1.0f, m_FrameRate);
			set => Seek(Mathf.Clamp((int)Math.Round(value * m_FrameRate), 0, Math.Max(0, m_TotalFrames - 1)));
		}

		GaussianSplatRenderer m_Renderer;
		FourDGSFileReader m_Reader;
		UnityWebRequest m_DownloadRequest;
		string[] m_SequenceFiles;
		int m_TotalFrames = 0;
		float m_FrameRate = 30.0f;
		float m_AccumulatedTime = 0.0f;
		bool m_IsPlaying = false;
		bool m_IsInitialized = false;
		int m_LastRenderedFrame = -1;
		string m_LastError;
#if UNITY_EDITOR
		double m_LastEditorUpdateTime;
#endif

		void Awake()
		{
			m_Renderer = GetComponent<GaussianSplatRenderer>();
		}

		void OnEnable()
		{
			m_Renderer = GetComponent<GaussianSplatRenderer>();
			Initialize();
			if (Application.isPlaying && m_PlayOnAwake)
				Play();
#if UNITY_EDITOR
			UnityEditor.EditorApplication.update += OnEditorUpdate;
			m_LastEditorUpdateTime = UnityEditor.EditorApplication.timeSinceStartup;
#endif
		}

		void OnDisable()
		{
#if UNITY_EDITOR
			UnityEditor.EditorApplication.update -= OnEditorUpdate;
#endif
			Cleanup();
		}

		public void Play()
		{
			if (!m_IsInitialized)
				Initialize();
			m_IsPlaying = true;
#if UNITY_EDITOR
			m_LastEditorUpdateTime = UnityEditor.EditorApplication.timeSinceStartup;
#endif
		}

		public void Pause() => m_IsPlaying = false;

		public void Stop()
		{
			m_IsPlaying = false;
			Seek(0);
		}

		public void StepForward()
		{
			if (m_TotalFrames <= 0) return;
			m_IsPlaying = false;
			Seek((m_CurrentFrame + 1) % m_TotalFrames);
		}

		public void StepBackward()
		{
			if (m_TotalFrames <= 0) return;
			m_IsPlaying = false;
			Seek((m_CurrentFrame - 1 + m_TotalFrames) % m_TotalFrames);
		}

		public void Seek(int targetFrame)
		{
			if (!m_IsInitialized || m_TotalFrames <= 0) return;
			m_CurrentFrame = Mathf.Clamp(targetFrame, 0, m_TotalFrames - 1);
			m_AccumulatedTime = m_CurrentFrame / Mathf.Max(1.0f, m_FrameRate);
			RenderFrame(m_CurrentFrame);
		}

		public void SeekNormalized(float t)
		{
			Seek(Mathf.RoundToInt(Mathf.Clamp01(t) * Mathf.Max(0, m_TotalFrames - 1)));
		}

		public void Initialize()
		{
			Cleanup();
			if (!m_Renderer)
				m_Renderer = GetComponent<GaussianSplatRenderer>();
			if (!m_Renderer)
				return;

			m_Renderer.SetExternalOr4DMode();
			m_LastError = null;

			try
			{
				switch (m_SourceMode)
				{
					case SourceMode.FourDGSAsset:
						if (m_FourDGSAsset != null && m_FourDGSAsset.isValid)
							LoadFromBytes(m_FourDGSAsset.data);
						else if (m_FourDGSTextAsset != null && m_FourDGSTextAsset.bytes != null && m_FourDGSTextAsset.bytes.Length > 0)
							LoadFromBytes(m_FourDGSTextAsset.bytes);
						break;

					case SourceMode.FourDGSFile:
						if (!string.IsNullOrWhiteSpace(m_SourcePath))
							LoadFromFileOrUrl(m_SourcePath);
						break;

					case SourceMode.SequenceSpzAssets:
						if (m_SpzSequence != null && m_SpzSequence.Count > 0)
							InitSpzSequence();
						break;

					case SourceMode.SequenceFileFolder:
						if (!string.IsNullOrWhiteSpace(m_SourcePath))
							InitSequenceFromFolder(m_SourcePath);
						break;
				}
			}
			catch (Exception e)
			{
				m_LastError = e.Message;
				Debug.LogError($"[GaussianSplat4DPlayer] Initialization error: {e.Message}", this);
			}
		}

		void LoadFromBytes(byte[] bytes)
		{
			m_Reader = new FourDGSFileReader(bytes);
			m_TotalFrames = m_Reader.totalFrames;
			m_FrameRate = m_Reader.header.frameRate > 0 ? m_Reader.header.frameRate : m_SequenceFrameRate;
			m_Renderer.AllocateDynamicBuffers(m_Reader.splatCount, m_Reader.header.shDegree);
			m_IsInitialized = true;
			m_LastRenderedFrame = -1;
			Seek(Mathf.Clamp(m_CurrentFrame, 0, m_TotalFrames - 1));
		}

		void LoadFromFileOrUrl(string path)
		{
			string resolved = GaussianSplatReader.Resolve4DPath(Application.streamingAssetsPath, path);
			if (GaussianSplatPaths.IsUrl(resolved))
			{
				m_DownloadRequest = UnityWebRequest.Get(resolved);
				m_DownloadRequest.SendWebRequest();
			}
			else if (File.Exists(resolved))
			{
				string ext = Path.GetExtension(resolved).ToLowerInvariant();
				if (ext == ".spz" || ext == ".ply")
				{
					// Single-file fallback
					m_SequenceFiles = new[] { resolved };
					m_TotalFrames = 1;
					m_FrameRate = m_SequenceFrameRate;
					m_IsInitialized = true;
					m_LastRenderedFrame = -1;
					Seek(0);
					return;
				}

				var fs = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
				m_Reader = new FourDGSFileReader(fs, ownsStream: true);
				m_TotalFrames = m_Reader.totalFrames;
				m_FrameRate = m_Reader.header.frameRate > 0 ? m_Reader.header.frameRate : m_SequenceFrameRate;
				m_Renderer.AllocateDynamicBuffers(m_Reader.splatCount, m_Reader.header.shDegree);
				m_IsInitialized = true;
				m_LastRenderedFrame = -1;
				Seek(Mathf.Clamp(m_CurrentFrame, 0, m_TotalFrames - 1));
			}
			else
			{
				m_LastError = $"File not found: {resolved}";
				Debug.LogWarning($"[GaussianSplat4DPlayer] .4dgs source file not found: {resolved}", this);
			}
		}

		void InitSpzSequence()
		{
			if (m_SpzSequence == null || m_SpzSequence.Count == 0) return;
			int maxSplats = 0;
			int maxDegree = 0;
			foreach (var item in m_SpzSequence)
			{
				if (item != null && item.isValid)
				{
					maxSplats = Math.Max(maxSplats, item.splatCount);
					maxDegree = Math.Max(maxDegree, item.shDegree);
				}
			}
			if (maxSplats <= 0)
			{
				m_LastError = "No valid GaussianSplatAssets in sequence list.";
				return;
			}
			m_TotalFrames = m_SpzSequence.Count;
			m_FrameRate = m_SequenceFrameRate;
			m_Renderer.AllocateDynamicBuffers(maxSplats, maxDegree);
			m_IsInitialized = true;
			m_LastRenderedFrame = -1;
			Seek(Mathf.Clamp(m_CurrentFrame, 0, m_TotalFrames - 1));
		}

		void InitSequenceFromFolder(string folderPath)
		{
			string resolved = GaussianSplatReader.ResolveFolderPath(Application.streamingAssetsPath, folderPath);
			if (!Directory.Exists(resolved))
			{
				m_LastError = $"Sequence folder not found: {resolved}";
				Debug.LogWarning($"[GaussianSplat4DPlayer] {m_LastError}", this);
				return;
			}

			var files = Directory.GetFiles(resolved, "*.spz");
			Array.Sort(files, StringComparer.OrdinalIgnoreCase);
			if (files.Length == 0)
			{
				files = Directory.GetFiles(resolved, "*.ply");
				Array.Sort(files, StringComparer.OrdinalIgnoreCase);
			}

			if (files.Length == 0)
			{
				m_LastError = $"No .spz or .ply files found in folder: {resolved}";
				Debug.LogWarning($"[GaussianSplat4DPlayer] {m_LastError}", this);
				return;
			}

			m_SequenceFiles = files;
			m_TotalFrames = files.Length;
			m_FrameRate = m_SequenceFrameRate;
			m_IsInitialized = true;
			m_LastRenderedFrame = -1;
			Seek(Mathf.Clamp(m_CurrentFrame, 0, m_TotalFrames - 1));
		}

#if UNITY_EDITOR
		void OnEditorUpdate()
		{
			if (Application.isPlaying) return;
			double now = UnityEditor.EditorApplication.timeSinceStartup;
			float dt = (float)(now - m_LastEditorUpdateTime);
			m_LastEditorUpdateTime = now;

			if (m_DownloadRequest != null && m_DownloadRequest.isDone)
				ProcessDownload();

			if (m_IsPlaying && m_IsInitialized && m_TotalFrames > 0)
			{
				AdvancePlayback(dt);
				UnityEditor.SceneView.RepaintAll();
			}
		}
#endif

		void Update()
		{
			if (m_DownloadRequest != null && m_DownloadRequest.isDone)
				ProcessDownload();

			if (!Application.isPlaying) return;
			if (!m_IsInitialized || m_TotalFrames <= 0 || !m_IsPlaying) return;

			AdvancePlayback(Time.deltaTime);
		}

		void ProcessDownload()
		{
			if (m_DownloadRequest == null) return;
			if (m_DownloadRequest.result == UnityWebRequest.Result.Success)
			{
				try
				{
					LoadFromBytes(m_DownloadRequest.downloadHandler.data);
				}
				catch (Exception e)
				{
					m_LastError = e.Message;
					Debug.LogError($"[GaussianSplat4DPlayer] Failed to decode downloaded .4dgs: {e.Message}", this);
				}
			}
			else
			{
				m_LastError = m_DownloadRequest.error;
				Debug.LogError($"[GaussianSplat4DPlayer] Failed to download .4dgs clip: {m_DownloadRequest.error}", this);
			}
			m_DownloadRequest.Dispose();
			m_DownloadRequest = null;
		}

		void AdvancePlayback(float deltaTime)
		{
			m_AccumulatedTime += deltaTime * m_PlaybackSpeed;
			int frame = Mathf.FloorToInt(m_AccumulatedTime * m_FrameRate);
			if (frame >= m_TotalFrames)
			{
				if (m_Loop)
				{
					m_AccumulatedTime = (m_AccumulatedTime * m_FrameRate % m_TotalFrames) / m_FrameRate;
					frame = Mathf.FloorToInt(m_AccumulatedTime * m_FrameRate);
				}
				else
				{
					frame = m_TotalFrames - 1;
					m_IsPlaying = false;
				}
			}
			if (frame != m_CurrentFrame)
			{
				m_CurrentFrame = frame;
				RenderFrame(m_CurrentFrame);
			}
		}

		void RenderFrame(int frameIndex)
		{
			if (frameIndex == m_LastRenderedFrame) return;
			m_LastRenderedFrame = frameIndex;

			if (m_Reader != null)
			{
				m_Reader.DecodeFrame(frameIndex);
				int frameSplats = (int)m_Reader.indexTable[frameIndex].splatCount;
				bool isKeyframe = m_Reader.indexTable[frameIndex].type != FourDGSFormat.FrameType.DeltaPFrame;
				m_Renderer.UpdateDynamicFrame(
					frameSplats,
					m_Reader.bounds,
					m_Reader.positions,
					m_Reader.geometry,
					m_Reader.colors,
					m_Reader.sh,
					updateSH: isKeyframe);
			}
			else if (m_SourceMode == SourceMode.SequenceSpzAssets && frameIndex >= 0 && frameIndex < m_SpzSequence.Count)
			{
				var asset = m_SpzSequence[frameIndex];
				if (asset != null && asset.isValid)
				{
					DecodeAndUploadRawBytes(asset.compressedData);
				}
			}
			else if (m_SequenceFiles != null && frameIndex >= 0 && frameIndex < m_SequenceFiles.Length)
			{
				string filePath = m_SequenceFiles[frameIndex];
				if (File.Exists(filePath))
				{
					byte[] rawBytes = File.ReadAllBytes(filePath);
					DecodeAndUploadRawBytes(rawBytes);
				}
			}
		}

		void DecodeAndUploadRawBytes(byte[] rawBytes)
		{
			using var op = new GaussianSplatLoadOperation(rawBytes);
			while (!op.isDone) op.Update();
			using var data = op.TakeData();
			if (data != null)
			{
				if (m_Renderer.gpuPosData == null || m_Renderer.gpuPosData.count < data.count * 3)
				{
					m_Renderer.AllocateDynamicBuffers(data.count, data.shDegree);
				}
				m_Renderer.UpdateDynamicFrame(
					data.count,
					data.bounds,
					data.positions,
					data.geometry,
					data.colors,
					data.sh,
					updateSH: true);
			}
		}

		void Cleanup()
		{
			m_IsPlaying = false;
			m_DownloadRequest?.Dispose();
			m_DownloadRequest = null;
			m_Reader?.Dispose();
			m_Reader = null;
			m_SequenceFiles = null;
			m_IsInitialized = false;
			m_LastRenderedFrame = -1;
		}
	}
}
