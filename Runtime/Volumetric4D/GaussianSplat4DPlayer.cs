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
            FourDGSFile,        // .4dgs file on StreamingAssets / local disk / URL
            FourDGSAsset,       // .4dgs binary TextAsset
            SequenceSpzAssets,  // List of pre-baked GaussianSplatAsset (.spz)
            SequenceFileFolder  // StreamingAssets folder containing frame_0000.spz, etc.
        }

        [Header("Source")]
        public SourceMode m_SourceMode = SourceMode.FourDGSFile;
        [Tooltip("Path to .4dgs file or sequence folder (relative to StreamingAssets, or absolute/URL)")]
        public string m_SourcePath;
        [Tooltip(".4dgs binary TextAsset")]
        public TextAsset m_FourDGSAsset;
        [Tooltip("Sequence of pre-baked GaussianSplatAsset frames")]
        public List<GaussianSplatAsset> m_SpzSequence = new();

        [Header("Playback")]
        public bool m_PlayOnAwake = true;
        public bool m_Loop = true;
        [Range(0.0f, 4.0f)]
        public float m_PlaybackSpeed = 1.0f;

        [Header("State")]
        [SerializeField] int m_CurrentFrame = 0;
        public int currentFrame => m_CurrentFrame;
        public int totalFrames => m_TotalFrames;
        public float frameRate => m_FrameRate;
        public bool isPlaying => m_IsPlaying;
        public double duration => m_TotalFrames > 0 && m_FrameRate > 0 ? m_TotalFrames / (double)m_FrameRate : 0.0;
        public double time
        {
            get => m_CurrentFrame / (double)Mathf.Max(1.0f, m_FrameRate);
            set => Seek(Mathf.Clamp((int)Math.Round(value * m_FrameRate), 0, Math.Max(0, m_TotalFrames - 1)));
        }

        GaussianSplatRenderer m_Renderer;
        FourDGSFileReader m_Reader;
        UnityWebRequest m_DownloadRequest;

        int m_TotalFrames = 0;
        float m_FrameRate = 30.0f;
        float m_AccumulatedTime = 0.0f;
        bool m_IsPlaying = false;
        bool m_IsInitialized = false;
        int m_LastRenderedFrame = -1;

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
        }

        void OnDisable()
        {
            Cleanup();
        }

        public void Play() => m_IsPlaying = true;
        public void Pause() => m_IsPlaying = false;
        public void Stop()
        {
            m_IsPlaying = false;
            Seek(0);
        }

        public void Seek(int targetFrame)
        {
            if (m_TotalFrames <= 0) return;
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

            m_Renderer.m_SourceType = GaussianSplatSourceType.ExternalStreamOr4D;

            switch (m_SourceMode)
            {
                case SourceMode.FourDGSAsset:
                    if (m_FourDGSAsset != null && m_FourDGSAsset.bytes != null && m_FourDGSAsset.bytes.Length > 0)
                        LoadFromBytes(m_FourDGSAsset.bytes);
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

        void LoadFromBytes(byte[] bytes)
        {
            try
            {
                m_Reader = new FourDGSFileReader(bytes);
                m_TotalFrames = m_Reader.totalFrames;
                m_FrameRate = m_Reader.header.frameRate > 0 ? m_Reader.header.frameRate : 30.0f;
                m_Renderer.AllocateDynamicBuffers(m_Reader.splatCount, m_Reader.header.shDegree);
                m_IsInitialized = true;
                Seek(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GaussianSplat4DPlayer] Failed to load .4dgs bytes: {e.Message}", this);
            }
        }

        void LoadFromFileOrUrl(string path)
        {
            string resolved = GaussianSplatReader.ResolvePath(Application.streamingAssetsPath, path);
            if (GaussianSplatPaths.IsUrl(resolved))
            {
                m_DownloadRequest = UnityWebRequest.Get(resolved);
                m_DownloadRequest.SendWebRequest();
            }
            else if (File.Exists(resolved))
            {
                var fs = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
                m_Reader = new FourDGSFileReader(fs, ownsStream: true);
                m_TotalFrames = m_Reader.totalFrames;
                m_FrameRate = m_Reader.header.frameRate > 0 ? m_Reader.header.frameRate : 30.0f;
                m_Renderer.AllocateDynamicBuffers(m_Reader.splatCount, m_Reader.header.shDegree);
                m_IsInitialized = true;
                Seek(0);
            }
            else
            {
                Debug.LogWarning($"[GaussianSplat4DPlayer] .4dgs source file not found: {resolved}", this);
            }
        }

        void InitSpzSequence()
        {
            if (m_SpzSequence == null || m_SpzSequence.Count == 0) return;
            m_TotalFrames = m_SpzSequence.Count;
            m_FrameRate = 30.0f;
            var first = m_SpzSequence[0];
            if (first != null && first.isValid)
            {
                m_Renderer.AllocateDynamicBuffers(first.splatCount, first.shDegree);
                m_IsInitialized = true;
                Seek(0);
            }
        }

        void InitSequenceFromFolder(string folderPath)
        {
            string resolved = GaussianSplatReader.ResolvePath(Application.streamingAssetsPath, folderPath);
            if (!Directory.Exists(resolved))
            {
                Debug.LogWarning($"[GaussianSplat4DPlayer] Sequence folder not found: {resolved}", this);
                return;
            }

            var files = Directory.GetFiles(resolved, "*.spz");
            Array.Sort(files);
            if (files.Length == 0)
            {
                files = Directory.GetFiles(resolved, "*.ply");
                Array.Sort(files);
            }

            if (files.Length > 0)
            {
                m_TotalFrames = files.Length;
                m_FrameRate = 30.0f;
                m_IsInitialized = true;
                Seek(0);
            }
        }

        void Update()
        {
            // Handle active WebRequest download
            if (m_DownloadRequest != null)
            {
                if (!m_DownloadRequest.isDone) return;
                if (m_DownloadRequest.result == UnityWebRequest.Result.Success)
                {
                    LoadFromBytes(m_DownloadRequest.downloadHandler.data);
                }
                else
                {
                    Debug.LogError($"[GaussianSplat4DPlayer] Failed to download .4dgs clip: {m_DownloadRequest.error}", this);
                }
                m_DownloadRequest.Dispose();
                m_DownloadRequest = null;
            }

            if (!m_IsInitialized || m_TotalFrames <= 0) return;

            if (m_IsPlaying && Application.isPlaying)
            {
                float dt = Time.deltaTime * m_PlaybackSpeed;
                m_AccumulatedTime += dt;
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
        }

        void RenderFrame(int frameIndex)
        {
            if (frameIndex == m_LastRenderedFrame) return;
            m_LastRenderedFrame = frameIndex;

            if (m_Reader != null)
            {
                m_Reader.DecodeFrame(frameIndex);
                bool isKeyframe = m_Reader.indexTable[frameIndex].type != FourDGSFormat.FrameType.DeltaPFrame;
                m_Renderer.UpdateDynamicFrame(
                    m_Reader.splatCount,
                    m_Reader.bounds,
                    m_Reader.positions,
                    m_Reader.geometry,
                    m_Reader.colors,
                    m_Reader.sh,
                    updateSH: isKeyframe);
            }
            else if (m_SourceMode == SourceMode.SequenceSpzAssets && frameIndex < m_SpzSequence.Count)
            {
                var asset = m_SpzSequence[frameIndex];
                if (asset != null && asset.isValid)
                {
                    using var op = new GaussianSplatLoadOperation(asset.compressedData);
                    while (!op.isDone) op.Update();
                    using var data = op.TakeData();
                    if (data != null)
                    {
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
            }
        }

        void Cleanup()
        {
            m_DownloadRequest?.Dispose();
            m_DownloadRequest = null;
            m_Reader?.Dispose();
            m_Reader = null;
            m_IsInitialized = false;
            m_LastRenderedFrame = -1;
        }
    }
}
