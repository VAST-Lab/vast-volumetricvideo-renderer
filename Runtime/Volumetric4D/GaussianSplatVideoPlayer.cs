// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using UnityEngine.Video;

namespace GaussianSplatting.Runtime
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [RequireComponent(typeof(VideoPlayer))]
    public class GaussianSplatVideoPlayer : MonoBehaviour
    {
        [Serializable]
        public struct SidecarMetadata
        {
            public int splatCount;
            public int gridWidth;
            public int gridHeight;
            public Vector3 boundsMin;
            public Vector3 boundsMax;
            public Vector2 scaleMinMax;
        }

        [Header("Video Source")]
        [Tooltip("Direct VideoClip reference")]
        public VideoClip m_VideoClip;
        [Tooltip("StreamingAssets relative path or HTTP/HTTPS video URL (.mp4 / .webm)")]
        public string m_VideoUrl;
        [Tooltip("Optional sidecar JSON metadata (clip.splatvideo.json)")]
        public TextAsset m_SidecarJson;

        [Header("Atlas Geometry & Bounds")]
        public int m_SplatCount = 65536;
        public int m_GridWidth = 256;
        public int m_GridHeight = 256;
        public Vector3 m_BoundsMin = -Vector3.one;
        public Vector3 m_BoundsMax = Vector3.one;
        public Vector2 m_ScaleMinMax = new Vector2(-7.0f, 0.0f);

        [Header("Playback")]
        public bool m_PlayOnAwake = true;
        public bool m_Loop = true;

        GaussianSplatRenderer m_Renderer;
        VideoPlayer m_VideoPlayer;
        RenderTexture m_VideoTexture;
        int m_KernelDecode = -1;
        bool m_IsInitialized = false;

        void Awake()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
            m_VideoPlayer = GetComponent<VideoPlayer>();
        }

        void OnEnable()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
            m_VideoPlayer = GetComponent<VideoPlayer>();
            Initialize();
        }

        void OnDisable()
        {
            Cleanup();
        }

        public void Initialize()
        {
            Cleanup();

            if (!m_Renderer) m_Renderer = GetComponent<GaussianSplatRenderer>();
            if (!m_VideoPlayer) m_VideoPlayer = GetComponent<VideoPlayer>();

            ParseSidecarJson();

            m_Renderer.m_SourceType = GaussianSplatSourceType.ExternalStreamOr4D;
            m_Renderer.AllocateDynamicBuffers(m_SplatCount, 0);

            // Compute texture dimensions (2*W x 2*H quadrant atlas)
            int texWidth = Mathf.Max(2, m_GridWidth * 2);
            int texHeight = Mathf.Max(2, m_GridHeight * 2);

            m_VideoTexture = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "GaussianSplatVideoTexture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                enableRandomWrite = false
            };
            m_VideoTexture.Create();

            m_VideoPlayer.renderMode = VideoRenderMode.RenderTexture;
            m_VideoPlayer.targetTexture = m_VideoTexture;
            m_VideoPlayer.isLooping = m_Loop;
            m_VideoPlayer.playOnAwake = m_PlayOnAwake;
            m_VideoPlayer.skipOnDrop = false;
            m_VideoPlayer.sendFrameReadyEvents = true;

            if (m_VideoClip != null)
            {
                m_VideoPlayer.source = VideoSource.VideoClip;
                m_VideoPlayer.clip = m_VideoClip;
            }
            else if (!string.IsNullOrWhiteSpace(m_VideoUrl))
            {
                m_VideoPlayer.source = VideoSource.Url;
                m_VideoPlayer.url = GaussianSplatReader.ResolvePath(Application.streamingAssetsPath, m_VideoUrl);
            }

            m_VideoPlayer.frameReady += OnFrameReady;

            var cs = GaussianSplatRenderingOptions.instance.csUtilities;
            if (cs != null && cs.HasKernel("CSDecodeVideoSplats"))
            {
                m_KernelDecode = cs.FindKernel("CSDecodeVideoSplats");
            }

            m_IsInitialized = true;
            if (Application.isPlaying && m_PlayOnAwake)
                m_VideoPlayer.Play();
        }

        void ParseSidecarJson()
        {
            if (m_SidecarJson == null || string.IsNullOrWhiteSpace(m_SidecarJson.text))
                return;

            try
            {
                var meta = JsonUtility.FromJson<SidecarMetadata>(m_SidecarJson.text);
                if (meta.splatCount > 0)
                {
                    m_SplatCount = meta.splatCount;
                    m_GridWidth = meta.gridWidth > 0 ? meta.gridWidth : m_GridWidth;
                    m_GridHeight = meta.gridHeight > 0 ? meta.gridHeight : m_GridHeight;
                    m_BoundsMin = meta.boundsMin;
                    m_BoundsMax = meta.boundsMax;
                    if (meta.scaleMinMax != Vector2.zero)
                        m_ScaleMinMax = meta.scaleMinMax;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GaussianSplatVideoPlayer] Failed to parse sidecar JSON: {e.Message}", this);
            }
        }

        void OnFrameReady(VideoPlayer source, long frameIdx)
        {
            DecodeCurrentFrame();
        }

        void Update()
        {
            if (!m_IsInitialized) return;

            // In Edit Mode or platforms where frameReady event does not fire, poll decode each frame
            if (!Application.isPlaying || m_VideoPlayer.isPlaying)
            {
                DecodeCurrentFrame();
            }
        }

        public void DecodeCurrentFrame()
        {
            if (!m_IsInitialized || m_VideoTexture == null || m_KernelDecode < 0)
                return;
            if (m_Renderer.gpuPosData == null)
                return;

            var cs = GaussianSplatRenderingOptions.instance.csUtilities;
            cs.SetTexture(m_KernelDecode, "_SplatVideoTex", m_VideoTexture);
            cs.SetBuffer(m_KernelDecode, "_SplatPos", m_Renderer.gpuPosData);
            cs.SetBuffer(m_KernelDecode, "_SplatOther", m_Renderer.gpuOtherData);
            cs.SetBuffer(m_KernelDecode, "_SplatColor", m_Renderer.gpuColorData);

            cs.SetInt("_SplatCount", m_SplatCount);
            cs.SetInt("_VideoGridWidth", m_GridWidth);
            cs.SetInt("_VideoGridHeight", m_GridHeight);
            cs.SetVector("_BoundsMin", m_BoundsMin);
            cs.SetVector("_BoundsMax", m_BoundsMax);
            cs.SetVector("_ScaleMinMax", m_ScaleMinMax);

            int threadGroups = (m_SplatCount + 255) / 256;
            cs.Dispatch(m_KernelDecode, threadGroups, 1, 1);

            m_Renderer.InvalidateTemporalHistory();
        }

        void Cleanup()
        {
            if (m_VideoPlayer != null)
                m_VideoPlayer.frameReady -= OnFrameReady;

            if (m_VideoTexture != null)
            {
                m_VideoTexture.Release();
                DestroyImmediate(m_VideoTexture);
                m_VideoTexture = null;
            }

            m_IsInitialized = false;
        }
    }
}
