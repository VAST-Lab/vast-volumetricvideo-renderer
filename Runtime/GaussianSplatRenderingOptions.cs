// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public enum TransparencyMode
    {
        SortedBlended,
        Stochastic,
    }

    public enum RenderMode
    {
        Splats,
        DebugPoints,
    }

    public enum StochasticTemporalFilter
    {
        None,
        SimpleAccumulation,
        Reprojected,
    }

    public enum SortingMode
    {
        Auto,
        GpuRadixSort,
        OctreeCpuSort,
    }

#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    [ExecuteInEditMode] // so that Awake is called in edit mode
    [DefaultExecutionOrder(-100)]
    public class GaussianSplatRenderingOptions : MonoBehaviour
    {
        public static GaussianSplatRenderingOptions instance
        {
            get
            {
                if (ms_Instance == null)
                    ms_Instance = FindAnyObjectByType<GaussianSplatRenderingOptions>();
                if (ms_Instance == null)
                {
                    var go = new GameObject($"{nameof(GaussianSplatRenderingOptions)} (Defaults)")
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    ms_Instance = go.AddComponent<GaussianSplatRenderingOptions>();
                }
                ms_Instance.EnsureResources();
                return ms_Instance;
            }
        }
        static GaussianSplatRenderingOptions ms_Instance;

        [Header("Options")]
        [Tooltip("Gaussian splat transparency rendering algorithm")]
        public TransparencyMode m_Transparency = TransparencyMode.SortedBlended;
        [Tooltip("Stochastic temporal filtering")]
        public StochasticTemporalFilter m_TemporalFilter = StochasticTemporalFilter.Reprojected;
        [Tooltip("Splat sorting backend (Auto uses GPU Radix Sort when compute shaders are available, or Octree CPU/WASM Sort otherwise)")]
        public SortingMode m_SortingMode = SortingMode.Auto;

        [Header("Octree Culling & CPU/WASM Sort Settings")]
        [Tooltip("Also use Octree frustum culling + front-to-back ordering in Stochastic transparency mode when OctreeCpuSort is selected to reduce overdraw")]
        public bool m_OctreeCullInStochastic = false;
        [Range(3, 8)]
        [Tooltip("Maximum octree depth for spatial subdivision (higher = more precise culling, more memory)")]
        public int m_OctreeMaxDepth = 5;
        [Range(32, 4096)]
        [Tooltip("Maximum splats per octree leaf node (lower = more precise culling, deeper tree)")]
        public int m_OctreeMaxSplatsPerLeaf = 2048;
        [Range(1, 10)]
        [Tooltip("Update octree culling every N frames (1 = every frame, higher = better performance)")]
        public int m_OctreeCullingUpdateInterval = 1;
        [Range(0.5f, 1.0f)]
        [Tooltip("Fraction of closest splats used to build octree root bounds (remaining are treated as outliers)")]
        public float m_OctreeSplatRatio = 0.9f;
        [Tooltip("Draw octree leaf node bounds in Scene view when Gizmos are enabled")]
        public bool m_DrawOctreeGizmos = false;

        [Header("Debugging Tweaks")]
        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f, 15.0f)] public float m_PointDisplaySize = 3.0f;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;

        internal StochasticTemporalFilter temporalFilter => m_Transparency == TransparencyMode.Stochastic && m_RenderMode == RenderMode.Splats
            ? m_TemporalFilter : StochasticTemporalFilter.None;

        internal bool needSorting => m_Transparency == TransparencyMode.SortedBlended && m_RenderMode == RenderMode.Splats;

        internal bool useOctreeSorting =>
            m_RenderMode == RenderMode.Splats &&
            (m_SortingMode == SortingMode.OctreeCpuSort || !supportsComputeViewData) &&
            (m_Transparency == TransparencyMode.SortedBlended || m_OctreeCullInStochastic);

        internal bool needGpuSorting => needSorting && !useOctreeSorting && supportsComputeViewData;

        internal bool resourcesFound { get; private set; }
        internal bool supportsComputeViewData { get; private set; }
        bool resourcesLoadAttempted;
        internal Shader shaderSplats { get; private set; }
        internal Shader shaderComposite { get; private set; }
        internal Shader shaderDebugPoints { get; private set; }
        internal ComputeShader csUtilities { get; private set; }
        internal ComputeShader csSortWave { get; private set; }

        void Awake()
        {
            if (ms_Instance != null && ms_Instance != this && ms_Instance.gameObject.hideFlags == HideFlags.HideAndDontSave)
                DestroyImmediate(ms_Instance.gameObject);
            ms_Instance = this;
            EnsureResources();
        }

        void EnsureResources()
        {
            if (resourcesLoadAttempted)
                return;
            resourcesLoadAttempted = true;

            shaderSplats = Resources.Load<Shader>("GaussianSplats");
            shaderComposite = Resources.Load<Shader>("GaussianComposite");
            shaderDebugPoints = Resources.Load<Shader>("GaussianDebugRenderPoints");
            csUtilities = Resources.Load<ComputeShader>("GaussianSplatUtilities");
            csSortWave = Resources.Load<ComputeShader>("GaussianSplatSortWave");

            supportsComputeViewData = SystemInfo.supportsComputeShaders && csUtilities != null && csUtilities.IsSupported(0);
            resourcesFound = shaderSplats != null && shaderComposite != null && shaderDebugPoints != null;
        }

        void OnDestroy()
        {
            if (ms_Instance == this) ms_Instance = null;
        }
    }
}
