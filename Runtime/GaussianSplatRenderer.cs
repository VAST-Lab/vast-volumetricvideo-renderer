// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();
        readonly RenderTargetIdentifier[] m_ReprojectionTargets =
        {
            new(GaussianSplatRenderer.Props.GaussianSplatRT),
            new(GaussianSplatRenderer.Props.GaussianSplatDepthRT)
        };

        CommandBuffer m_CommandBuffer;
        GraphicsBuffer m_QuadIndexBuffer;
        GraphicsBuffer m_GlobalUniforms;
        Material m_MatSplats, m_MatComposite, m_MatDebugPoints;
        uint m_FrameOffset;
        GraphicsBuffer m_GpuView, m_GpuSortDistances, m_GpuSortKeys;
        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;
        readonly MaterialPropertyBlock m_DrawProperties = new();
        int m_SplatCount;
        string m_BufferError;
        const int kGpuViewDataSize = 28;
        GaussianSplatTemporalAccumulator m_TemporalAccumulator;
        int m_SceneVersion;
#if UNITY_EDITOR
        readonly HashSet<Camera> m_RenderedSceneViews = new();
#endif

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 16)]
        struct SplatGlobalUniforms // match cbuffer SplatGlobalUniforms in shaders
        {
            // ReSharper disable once NotAccessedField.Local - used on shader side
            public uint transparencyMode;
            // ReSharper disable once NotAccessedField.Local - used on shader side
            public uint frameOffset;
        }

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
#if UNITY_EDITOR
                UnityEditor.ObjectChangeEvents.changesPublished += OnSceneChanged;
                UnityEditor.SceneView.beforeSceneGui += OnBeforeSceneGUI;
#endif
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
            InvalidateTemporalHistory();
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            InvalidateTemporalHistory();
            if (m_Splats.Count == 0)
                CleanupAfterAllSplatsDeleted();
        }

        void CleanupAfterAllSplatsDeleted()
        {
            if (m_CameraCommandBuffersDone != null)
            {
                if (m_CommandBuffer != null)
                {
                    foreach (var cam in m_CameraCommandBuffersDone)
                    {
                        if (cam)
                            cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                    }
                }
                m_CameraCommandBuffersDone.Clear();
            }

            m_ActiveSplats.Clear();
            DisposeSplatBuffers();
            GaussianSplatRenderer.DisposeDummyBuffers();
            if (NativeSorting.IsAvailable)
                NativeSorting.Shutdown();
            m_Sorter = null;
            m_QuadIndexBuffer?.Dispose();
            m_QuadIndexBuffer = null;
            m_CommandBuffer?.Dispose();
            m_CommandBuffer = null;
            m_GlobalUniforms?.Dispose();
            m_GlobalUniforms = null;
            UnityEngine.Object.DestroyImmediate(m_MatSplats);
            UnityEngine.Object.DestroyImmediate(m_MatComposite);
            UnityEngine.Object.DestroyImmediate(m_MatDebugPoints);
            Camera.onPreCull -= OnPreCullCamera;
            m_TemporalAccumulator?.Dispose();
            m_TemporalAccumulator = null;
#if UNITY_EDITOR
            UnityEditor.ObjectChangeEvents.changesPublished -= OnSceneChanged;
            UnityEditor.SceneView.beforeSceneGui -= OnBeforeSceneGUI;
            m_RenderedSceneViews.Clear();
#endif
        }

        public void InvalidateTemporalHistory() => ++m_SceneVersion;

        void DisposeSplatBuffers()
        {
            m_GpuView?.Dispose(); m_GpuView = null;
            m_GpuSortDistances?.Dispose(); m_GpuSortDistances = null;
            m_GpuSortKeys?.Dispose(); m_GpuSortKeys = null;
            m_SorterArgs.resources.Dispose();
            m_SorterArgs = default;
        }

        void EnsureSplatBuffers()
        {
            var options = GaussianSplatRenderingOptions.instance;
            bool useComputeView = options.supportsComputeViewData;
            bool gpuSorting = options.needGpuSorting;
            int viewCount = useComputeView ? Mathf.Max(1, m_SplatCount) : 1;
            // note: view compute kernel and vertex shader still need valid bindings on WebGPU,
            // even when not writing or reading global sort keys
            int sortCount = gpuSorting ? Mathf.Max(1, m_SplatCount) : 1;
            if (m_GpuView != null && m_GpuView.count == viewCount &&
                m_GpuSortKeys != null && m_GpuSortKeys.count == sortCount &&
                (m_SorterArgs.resources.altBuffer != null) == gpuSorting)
                return;
            DisposeSplatBuffers();
            try
            {
                m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, viewCount, kGpuViewDataSize) {name = "GaussianSplatViewData"};
                m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sortCount, 4) {name = "GaussianSplatSortDistances"};
                m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sortCount, 4) {name = "GaussianSplatSortIndices"};
                if (!gpuSorting)
                    return;
                m_Sorter ??= new GpuSorting(options.csUtilities, options.csSortWave);
                m_SorterArgs = new GpuSorting.Args
                {
                    count = (uint)m_SplatCount,
                    inputKeys = m_GpuSortDistances,
                    inputValues = m_GpuSortKeys,
                    resources = GpuSorting.SupportResources.Load((uint)m_SplatCount)
                };
            }
            catch
            {
                DisposeSplatBuffers();
                throw;
            }
        }

#if UNITY_EDITOR
        // Invalidate temporal history upon scene edits. Ignore camera changes,
        // since e.g. reprojected temporal filtering can handle camera movement; for
        // non-reprojected case this will be handled elsewhere.
        void OnSceneChanged(ref UnityEditor.ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; ++i)
            {
                if (stream.GetEventType(i) == UnityEditor.ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                {
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var change);
#if UNITY_6000_6_OR_NEWER
                    var obj = UnityEditor.EditorUtility.EntityIdToObject(change.entityId);
#elif UNITY_6000_3_OR_NEWER
                    var obj = UnityEditor.EditorUtility.EntityIdToObject(change.instanceId);
#else
                    var obj = UnityEditor.EditorUtility.InstanceIDToObject(change.instanceId);
#endif
                    if (obj is Camera || obj is Transform tr && tr.GetComponent<Camera>() != null)
                        continue;
                }
                InvalidateTemporalHistory();
                break;
            }
        }
        void OnBeforeSceneGUI(UnityEditor.SceneView view) => m_RenderedSceneViews.Remove(view.camera);
#endif

        internal int GetTemporalSceneHash()
        {
            var hash = new HashCode();
            hash.Add(m_SceneVersion);
            var options = GaussianSplatRenderingOptions.instance;
            hash.Add(options.m_SHOnly);
            foreach (var (gs, _) in m_ActiveSplats)
            {
                hash.Add(gs);
                hash.Add(gs.transform.localToWorldMatrix);
                hash.Add(gs.m_SplatScale);
                hash.Add(gs.m_OpacityScale);
                hash.Add(gs.m_SHOrder);
            }
            return hash.ToHashCode();
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // Sort active splat objects by render order and distance from camera (front-to-back for OneMinusDstAlpha blending)
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.m_RenderOrder;
                var orderB = b.Item1.m_RenderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            var options = GaussianSplatRenderingOptions.instance;
            // points read source buffers directly; no need for shared view/sort buffers
            if (options.m_RenderMode == RenderMode.DebugPoints)
                return true;

            long count = 0;
            string error = null;
            // view preparation dispatch is 1D, with 256 group size (WebGPU maxComputeInvocationsPerWorkgroup = 256)
            int splatLimit = 65535 * 256;
            foreach (var (gs, _) in m_ActiveSplats)
            {
                count += gs.splatCount;
                if (options.supportsComputeViewData && gs.splatCount > splatLimit)
                    error = $"Can't render '{gs.name}': view preparation supports at most {splatLimit:N0} splats per object";
            }

            if (options.supportsComputeViewData)
            {
                // check view data buffer against maximum supported by system
                long bytes = count * kGpuViewDataSize;
                long limit = SystemInfo.maxGraphicsBufferSize;
                if (count > int.MaxValue || bytes > limit)
                    error = $"Cannot render all {count:N0} splats: shared view buffer needs {bytes:N0} bytes (max graphics buffer size {limit:N0})";
            }

            // radix sort dispatches one group per 3840 keys
            int sortLimit = 65535 * 3840;
            if (options.m_RenderMode == RenderMode.Splats && options.needGpuSorting && count > sortLimit)
                error = $"Cannot sort all {count:N0} splats: we support at most {sortLimit:N0} splats";
            if (error != null)
            {
                if (m_BufferError != error) Debug.LogError(error);
                m_BufferError = error;
                return false;
            }
            m_BufferError = null;
            m_SplatCount = (int)count;
            EnsureSplatBuffers();

            return true;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void SortAllSplats(Camera cam, CommandBuffer cmb)
        {
            if (cam.cameraType == CameraType.Preview)
                return;
            var options = GaussianSplatRenderingOptions.instance;
            if (!options.needGpuSorting || m_Sorter == null || !m_Sorter.Valid)
                return;
            cmb.BeginSample(GaussianSplatRenderer.s_ProfSort);
            m_SorterArgs.count = (uint)m_SplatCount;
            m_Sorter.Dispatch(cmb, m_SorterArgs);
            cmb.EndSample(GaussianSplatRenderer.s_ProfSort);
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void CacheViewDataForAllSplats(Camera cam, CommandBuffer cmb, Matrix4x4 projection, Matrix4x4 view, int width, int height)
        {
            var options = GaussianSplatRenderingOptions.instance;
            if (options.m_RenderMode == RenderMode.DebugPoints || !options.supportsComputeViewData)
                return;
            bool writeSortKeys = options.needGpuSorting;
            int offset = 0;
            foreach (var (gs, _) in m_ActiveSplats)
            {
                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam, m_GpuView, m_GpuSortDistances, m_GpuSortKeys, offset, writeSortKeys, projection, view, width, height);
                cmb.EndSample(s_ProfCalcView);
                offset += gs.splatCount;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void RenderAllSplats(Camera cam, CommandBuffer cmb, Matrix4x4 projection, Matrix4x4 view, int width, int height)
        {
            EnsureMaterials();
            GaussianSplatRenderingOptions settings = GaussianSplatRenderingOptions.instance;
            if (m_MatSplats == null)
                return;

            EnsureQuadIndexBuffer();
            if (settings.m_RenderMode == RenderMode.DebugPoints)
            {
                RenderDebugPoints(cmb, settings, projection * view, width, height);
                return;
            }

            bool stochastic = settings.m_Transparency == TransparencyMode.Stochastic;
            bool reprojected = stochastic && settings.m_RenderMode == RenderMode.Splats &&
                               settings.m_TemporalFilter == StochasticTemporalFilter.Reprojected;
            if (reprojected) m_MatSplats.EnableKeyword("SPLAT_REPROJECTED");
            else m_MatSplats.DisableKeyword("SPLAT_REPROJECTED");

            bool useComputeView = settings.supportsComputeViewData;
            if (!useComputeView) m_MatSplats.EnableKeyword("SPLAT_VERTEX_PROJECT");
            else m_MatSplats.DisableKeyword("SPLAT_VERTEX_PROJECT");

            m_GlobalUniforms ??= new GraphicsBuffer(GraphicsBuffer.Target.Constant, 1, UnsafeUtility.SizeOf<SplatGlobalUniforms>());
            NativeArray<SplatGlobalUniforms> sgu = new(1, Allocator.Temp);
            sgu[0] = new SplatGlobalUniforms
            {
                transparencyMode = (uint)settings.m_Transparency,
                frameOffset = m_FrameOffset
            };
            using (sgu)
                cmb.SetBufferData(m_GlobalUniforms, sgu);
            m_FrameOffset++;

            // Use SetGlobalConstantBuffer for WebGPU binding compliance
            cmb.SetGlobalConstantBuffer(m_GlobalUniforms, GaussianSplatRenderer.Props.SplatGlobalUniforms, 0, m_GlobalUniforms.stride);

            m_MatSplats.SetInt(GaussianSplatRenderer.Props.SrcBlend, (int)(stochastic ? BlendMode.One : BlendMode.OneMinusDstAlpha));
            m_MatSplats.SetInt(GaussianSplatRenderer.Props.DstBlend, (int)(stochastic ? BlendMode.Zero : BlendMode.One));
            m_MatSplats.SetInt(GaussianSplatRenderer.Props.ZWrite, stochastic ? 1 : 0);

            if (useComputeView && !settings.useOctreeSorting)
            {
                // Single global draw call for all splats (either GPU Radix Sorted or Stochastic)
                var mpb = m_DrawProperties;
                mpb.Clear();
                mpb.SetVector(GaussianSplatRenderer.Props.SplatScreenSize, new Vector4(width, height, 0, 0));
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, m_GpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, m_GpuSortKeys);
                mpb.SetInteger(GaussianSplatRenderer.Props.SplatOffset, 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.UseOrderBuffer, settings.needGpuSorting ? 1 : 0);
                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(m_QuadIndexBuffer, Matrix4x4.identity, m_MatSplats, 0, MeshTopology.Triangles, 6, m_SplatCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
            else
            {
                // Per-object draw calls (Octree CPU/WASM culling + sorting, or SPLAT_VERTEX_PROJECT fallback)
                Vector4 screenPar = new Vector4(width, height, 0, 0);
                Vector4 camPos = view.inverse.GetColumn(3);
                int offset = 0;

                foreach (var (gs, mpb) in m_ActiveSplats)
                {
                    int instanceCount = gs.splatCount;
                    GraphicsBuffer orderBuffer = m_GpuSortKeys;
                    bool useOrderBuffer = false;

                    if (settings.useOctreeSorting)
                    {
                        int visibleCount = gs.PerformOctreeCulling(cam);
                        if (visibleCount <= 0)
                        {
                            offset += gs.splatCount;
                            continue;
                        }
                        instanceCount = visibleCount;
                        orderBuffer = gs.octreeVisibleIndicesBuffer ?? m_GpuSortKeys;
                        useOrderBuffer = gs.octreeVisibleIndicesBuffer != null;
                    }

                    mpb.Clear();
                    mpb.SetVector(GaussianSplatRenderer.Props.SplatScreenSize, screenPar);
                    mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, m_GpuView);
                    mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, orderBuffer);
                    mpb.SetInteger(GaussianSplatRenderer.Props.SplatOffset, useComputeView ? offset : 0);
                    mpb.SetInteger(GaussianSplatRenderer.Props.UseOrderBuffer, useOrderBuffer ? 1 : 0);

                    if (!useComputeView)
                    {
                        var tr = gs.transform;
                        Matrix4x4 matO2W = tr.localToWorldMatrix;
                        Matrix4x4 matW2O = tr.worldToLocalMatrix;
                        gs.SetDataOnMaterial(mpb);
                        mpb.SetInteger(GaussianSplatRenderer.Props.SplatCount, gs.splatCount);
                        mpb.SetMatrix(GaussianSplatRenderer.Props.MatrixMV, view * matO2W);
                        mpb.SetMatrix(GaussianSplatRenderer.Props.MatrixP, projection);
                        mpb.SetMatrix(GaussianSplatRenderer.Props.MatrixVP, projection * view);
                        mpb.SetMatrix(GaussianSplatRenderer.Props.MatrixObjectToWorld, matO2W);
                        mpb.SetMatrix(GaussianSplatRenderer.Props.MatrixWorldToObject, matW2O);
                        mpb.SetVector(GaussianSplatRenderer.Props.VecScreenParams, screenPar);
                        mpb.SetVector(GaussianSplatRenderer.Props.VecWorldSpaceCameraPos, camPos);
                        mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                        mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                        mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                        mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, settings.m_SHOnly ? 1 : 0);
                    }

                    cmb.BeginSample(s_ProfDraw);
                    cmb.DrawProcedural(m_QuadIndexBuffer, Matrix4x4.identity, m_MatSplats, 0, MeshTopology.Triangles, 6, instanceCount, mpb);
                    cmb.EndSample(s_ProfDraw);

                    offset += gs.splatCount;
                }
            }
        }

        void RenderDebugPoints(CommandBuffer cmb, GaussianSplatRenderingOptions settings, Matrix4x4 viewProjection, int width, int height)
        {
            foreach (var (gs, mpb) in m_ActiveSplats)
            {
                mpb.Clear();
                gs.SetDataOnMaterial(mpb);
                mpb.SetMatrix(GaussianSplatRenderer.Props.SplatMatrixVP, viewProjection);
                mpb.SetVector(GaussianSplatRenderer.Props.SplatScreenSize, new Vector4(width, height, 0, 0));
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, settings.m_PointDisplaySize);
                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(m_QuadIndexBuffer, gs.transform.localToWorldMatrix, m_MatDebugPoints, 0,
                    MeshTopology.Triangles, 6, gs.splatCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
        }

        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb, Matrix4x4? projection = null, int width = 0, int height = 0, Matrix4x4? view = null)
        {
            Matrix4x4 gpuProjection = projection ?? GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 viewMatrix = view ?? cam.worldToCameraMatrix;
            width = width > 0 ? width : (XRSettings.eyeTextureWidth != 0 ? XRSettings.eyeTextureWidth : cam.pixelWidth);
            height = height > 0 ? height : (XRSettings.eyeTextureHeight != 0 ? XRSettings.eyeTextureHeight : cam.pixelHeight);
            CacheViewDataForAllSplats(cam, cmb, gpuProjection, viewMatrix, width, height);
            SortAllSplats(cam, cmb);
            RenderAllSplats(cam, cmb, gpuProjection, viewMatrix, width, height);
            return m_MatComposite;
        }

        void EnsureQuadIndexBuffer()
        {
            if (m_QuadIndexBuffer != null)
                return;
            m_QuadIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, 2);
            m_QuadIndexBuffer.SetData(new ushort[] {0, 1, 2, 1, 3, 2});
        }

        void EnsureMaterials()
        {
            GaussianSplatRenderingOptions settings = GaussianSplatRenderingOptions.instance;
            if (m_MatSplats == null && settings.resourcesFound)
            {
                m_MatSplats = new Material(settings.shaderSplats) {name = "GaussianSplats"};
                m_MatComposite = new Material(settings.shaderComposite) {name = "GaussianClearDstAlpha"};
                m_MatDebugPoints = new Material(settings.shaderDebugPoints) {name = "GaussianDebugPoints"};
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
#if UNITY_EDITOR
            // camera callbacks are also invoked for outline passes etc.; only advance
            // temporal history once per actual repaint
            if (cam.cameraType == CameraType.SceneView &&
                GaussianSplatRenderingOptions.instance.temporalFilter != StochasticTemporalFilter.None &&
                !m_RenderedSceneViews.Add(cam))
                return;
#endif
            if (!GatherSplatsForCamera(cam))
            {
                m_CommandBuffer?.Clear();
                m_TemporalAccumulator?.Reset(cam);
                return;
            }

            InitialClearCmdBuffer(cam);

            var options = GaussianSplatRenderingOptions.instance;
            bool temporal = options.temporalFilter != StochasticTemporalFilter.None;
            bool reprojected = options.temporalFilter == StochasticTemporalFilter.Reprojected;
            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            if (reprojected)
            {
                // reprojection also writes splat's depth into another MRT target
                m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatDepthRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R32_SFloat);
                m_CommandBuffer.SetRenderTarget(m_ReprojectionTargets, BuiltinRenderTextureType.CurrentActive);
            }
            else
                m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // Prepare per-object ranges, then globally sort and draw.
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            if (temporal)
            {
                m_TemporalAccumulator ??= new GaussianSplatTemporalAccumulator();
                var filtered = m_TemporalAccumulator.Accumulate(m_CommandBuffer, cam, matComposite, GetTemporalSceneHash(),
                    options.m_TemporalFilter, cam.pixelWidth, cam.pixelHeight,
                    GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix);
                m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatRT, filtered);
            }
            else
            {
                m_TemporalAccumulator?.Dispose();
                m_TemporalAccumulator = null;
            }
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
            if (reprojected)
                m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatDepthRT);
        }
    }

    public enum GaussianSplatSourceType
    {
        FileOrUrl,          // StreamingAssets, local disk path, or HTTP/HTTPS URL
        SplatAsset,         // ScriptableObject GaussianSplatAsset containing compressed SPZ v4
        TextAssetBytes,     // Unity TextAsset containing raw .spz or .ply bytes (.spz.bytes)
        ExternalStreamOr4D  // Controlled by GaussianSplat4DPlayer, VideoPlayer, or StreamReceiver
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        [Tooltip("Source loading mode for static 3DGS or 4DGS volumetric video")]
        public GaussianSplatSourceType m_SourceType = GaussianSplatSourceType.FileOrUrl;

        [Tooltip("Path relative to StreamingAssets or current directory, an absolute path, or a URL")]
        public string m_SourcePath;

        [Tooltip("Pre-baked GaussianSplatAsset ScriptableObject (SPZ v4 compressed)")]
        public GaussianSplatAsset m_SplatAsset;

        [Tooltip("TextAsset binary file (.spz.bytes or .ply.bytes)")]
        public TextAsset m_TextAsset;

        // Call after any runtime changes that affect splat visibility / history
        public void InvalidateTemporalHistory() => GaussianSplatRenderSystem.instance.InvalidateTemporalHistory();

        [Tooltip("Rendering order compared to other splats when using Octree/per-object sorting. Higher order splats render in front of lower order splats.")]
        public int m_RenderOrder;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;

        static GraphicsBuffer s_DummyIndexBuffer;

        int m_SplatCount;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        GraphicsBuffer m_GpuColorData;
        NativeArray<float3> m_LocalPositions;
        string m_PrevSourcePath;
        GaussianSplatSourceType m_PrevSourceType;
        GaussianSplatAsset m_PrevSplatAsset;
        TextAsset m_PrevTextAsset;
        GaussianSplatLoadOperation m_LoadOperation;
        int m_SHCoefficients;
        bool m_Registered;

        public GraphicsBuffer gpuPosData => m_GpuPosData;
        public GraphicsBuffer gpuOtherData => m_GpuOtherData;
        public GraphicsBuffer gpuColorData => m_GpuColorData;
        public GraphicsBuffer gpuSHData => m_GpuSHData;

        // Octree culling and CPU/WASM cached sorting state
        internal GaussianSplatOctree m_Octree;
        internal bool m_OctreeBuilt;
        int m_LastCullingFrame = -1;
        int m_BuiltOctreeMaxDepth;
        int m_BuiltOctreeMaxSplatsPerLeaf;
        float m_BuiltOctreeSplatRatio;

        public bool isLoading { get; private set; }
        public string loadError { get; private set; }
        public Bounds localBounds { get; private set; }
        public int sourceSHDegree { get; private set; }
        public GaussianSplatCameras.CameraInfo[] cameras { get; private set; } = Array.Empty<GaussianSplatCameras.CameraInfo>();
        public bool octreeBuilt => m_OctreeBuilt;
        public GaussianSplatOctree octree => m_Octree;
        internal GraphicsBuffer octreeVisibleIndicesBuffer => m_OctreeBuilt && m_Octree != null ? m_Octree.visibleIndicesBuffer : null;

        internal static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
            public static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
            public static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
            public static readonly int SplatGlobalUniforms = Shader.PropertyToID("SplatGlobalUniforms");
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SHCoefficients = Shader.PropertyToID("_SHCoefficients");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int SplatOffset = Shader.PropertyToID("_SplatOffset");
            public static readonly int UseOrderBuffer = Shader.PropertyToID("_UseOrderBuffer");
            public static readonly int WriteSortKeys = Shader.PropertyToID("_WriteSortKeys");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int GaussianSplatDepthRT = Shader.PropertyToID("_GaussianSplatDepthRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SplatScreenSize = Shader.PropertyToID("_SplatScreenSize");
            public static readonly int SplatMatrixVP = Shader.PropertyToID("_SplatMatrixVP");
            public static readonly int CameraTargetTextureTexelSize = Shader.PropertyToID("_CameraTargetTexture_TexelSize");
            public static readonly int MatrixP = Shader.PropertyToID("_MatrixP");
            public static readonly int MatrixVP = Shader.PropertyToID("_MatrixVP");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
        }

        public int splatCount => m_SplatCount;

        const int kKernelCalcViewData = 0;

        public bool HasValidRenderResources => resourcesAreSetUp;
        public bool HasValidRenderSetup => m_SplatCount > 0 && m_GpuPosData != null && m_GpuColorData != null;

        static GraphicsBuffer Upload(NativeArray<uint> data, string name)
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, data.Length, 4) { name = name };
            try { buffer.SetData(data); return buffer; }
            catch { buffer.Dispose(); throw; }
        }

        void CreateResources(GaussianSplatData data)
        {
            m_SplatCount = data.count;
            sourceSHDegree = data.shDegree;
            m_SHCoefficients = data.shCoefficients;
            localBounds = data.bounds;
            m_GpuPosData = Upload(data.positions, "GaussianPosData");
            m_GpuOtherData = Upload(data.geometry, "GaussianOtherData");
            m_GpuColorData = Upload(data.colors, "GaussianColorData");
            m_GpuSHData = Upload(data.sh, "GaussianSHData");

            if (m_LocalPositions.IsCreated)
                m_LocalPositions.Dispose();
            m_LocalPositions = new NativeArray<float3>(m_SplatCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_LocalPositions.CopyFrom(data.positions.Reinterpret<float3>(4));

            if (GaussianSplatRenderingOptions.instance.useOctreeSorting)
                EnsureOctreeBuilt();

            EnsureRegistered();
            GaussianSplatRenderSystem.instance.InvalidateTemporalHistory();
        }

        internal void EnsureOctreeBuilt()
        {
            if (!m_LocalPositions.IsCreated || m_SplatCount <= 0)
                return;

            var options = GaussianSplatRenderingOptions.instance;
            if (m_OctreeBuilt && m_Octree != null &&
                m_BuiltOctreeMaxDepth == options.m_OctreeMaxDepth &&
                m_BuiltOctreeMaxSplatsPerLeaf == options.m_OctreeMaxSplatsPerLeaf &&
                Mathf.Approximately(m_BuiltOctreeSplatRatio, options.m_OctreeSplatRatio))
            {
                return;
            }

            try
            {
                m_Octree ??= new GaussianSplatOctree();
                m_Octree.Initialize(options.m_OctreeMaxDepth, options.m_OctreeMaxSplatsPerLeaf);
                m_Octree.Build(m_LocalPositions, localBounds, options.m_OctreeSplatRatio);
                m_OctreeBuilt = true;
                m_LastCullingFrame = -1;
                m_BuiltOctreeMaxDepth = options.m_OctreeMaxDepth;
                m_BuiltOctreeMaxSplatsPerLeaf = options.m_OctreeMaxSplatsPerLeaf;
                m_BuiltOctreeSplatRatio = options.m_OctreeSplatRatio;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to build octree for {name}: {e.Message}", this);
                m_OctreeBuilt = false;
            }
        }

        public int PerformOctreeCulling(Camera camera)
        {
            var options = GaussianSplatRenderingOptions.instance;
            if (!options.useOctreeSorting)
                return m_SplatCount;

            EnsureOctreeBuilt();
            if (!m_OctreeBuilt || m_Octree == null)
                return m_SplatCount;

            int currentFrame = Time.frameCount;
            if (Application.isPlaying && m_LastCullingFrame >= 0 &&
                (currentFrame - m_LastCullingFrame) < options.m_OctreeCullingUpdateInterval)
            {
                return m_Octree.visibleSplatCount;
            }

            m_LastCullingFrame = currentFrame;
            m_Octree.SortVisibleSplatsByDepth(camera, transform);
            return m_Octree.visibleSplatCount;
        }

        public void OnDrawGizmos()
        {
            if (!m_OctreeBuilt || m_Octree == null)
                return;
            var options = GaussianSplatRenderingOptions.instance;
            if (options == null || !options.m_DrawOctreeGizmos)
                return;

            Color col = options.m_RenderMode == RenderMode.DebugPoints ? Color.cyan : Color.yellow;
            m_Octree.DrawLeafBoundsGizmos(col, transform.localToWorldMatrix);
        }

        public static void DisposeDummyBuffers()
        {
            DisposeBuffer(ref s_DummyIndexBuffer);
        }

        bool resourcesAreSetUp => GaussianSplatRenderingOptions.instance.resourcesFound;

        public void EnsureRegistered()
        {
            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        public void OnEnable()
        {
            Reload();
        }

        public void SetExternalOr4DMode()
        {
            FinishLoading();
            m_SourceType = GaussianSplatSourceType.ExternalStreamOr4D;
            m_PrevSourceType = GaussianSplatSourceType.ExternalStreamOr4D;
            loadError = null;
            EnsureRegistered();
        }

        public void AllocateDynamicBuffers(int maxSplatCount, int maxSHDegree)
        {
            if (maxSplatCount <= 0)
                return;
            SetExternalOr4DMode();
            DisposeResources();
            m_SplatCount = maxSplatCount;
            sourceSHDegree = maxSHDegree;
            m_SHCoefficients = (maxSHDegree + 1) * (maxSHDegree + 1) - 1;
            int shWords = m_SHCoefficients == 0 ? 1 : maxSplatCount * (GaussianUtils.GetSHDataSize(m_SHCoefficients) / 4);

            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, maxSplatCount * 3, 4) { name = "GaussianPosData_4D" };
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, maxSplatCount * 3, 4) { name = "GaussianOtherData_4D" };
            m_GpuColorData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, maxSplatCount, 4) { name = "GaussianColorData_4D" };
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, shWords, 4) { name = "GaussianSHData_4D" };

            if (m_LocalPositions.IsCreated)
                m_LocalPositions.Dispose();
            m_LocalPositions = new NativeArray<float3>(maxSplatCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            EnsureRegistered();
        }

        public void UpdateDynamicFrame(
            int activeSplatCount,
            Bounds frameBounds,
            NativeArray<uint> pos,
            NativeArray<uint> other,
            NativeArray<uint> color,
            NativeArray<uint> sh,
            bool updateSH = true)
        {
            if (activeSplatCount <= 0 || m_GpuPosData == null)
                return;

            m_SplatCount = activeSplatCount;
            localBounds = frameBounds;

            m_GpuPosData.SetData(pos, 0, 0, activeSplatCount * 3);
            m_GpuOtherData.SetData(other, 0, 0, activeSplatCount * 3);
            m_GpuColorData.SetData(color, 0, 0, activeSplatCount);
            if (updateSH && sh.IsCreated && m_SHCoefficients > 0 && m_GpuSHData != null)
            {
                int shWords = activeSplatCount * (GaussianUtils.GetSHDataSize(m_SHCoefficients) / 4);
                m_GpuSHData.SetData(sh, 0, 0, shWords);
            }

            if (m_LocalPositions.IsCreated && pos.IsCreated)
            {
                var floatPositions = pos.Reinterpret<float3>(4);
                m_LocalPositions.GetSubArray(0, activeSplatCount).CopyFrom(floatPositions.GetSubArray(0, activeSplatCount));
                if (m_OctreeBuilt && m_Octree != null)
                    m_Octree.InvalidateAllSorts();
            }

            GaussianSplatRenderSystem.instance.InvalidateTemporalHistory();
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
            }
#endif
        }

        public void Reload()
        {
            FinishLoading();
            m_PrevSourcePath = m_SourcePath;
            m_PrevSourceType = m_SourceType;
            m_PrevSplatAsset = m_SplatAsset;
            m_PrevTextAsset = m_TextAsset;
            loadError = null;
            cameras = Array.Empty<GaussianSplatCameras.CameraInfo>();

            if (!isActiveAndEnabled)
            {
                DisposeResources();
                return;
            }

            if (m_SourceType == GaussianSplatSourceType.ExternalStreamOr4D)
            {
                EnsureRegistered();
                var player4D = GetComponent<GaussianSplat4DPlayer>();
                if (player4D != null && player4D.enabled)
                {
                    player4D.Initialize();
                    return;
                }
                var videoPlayer = GetComponent<GaussianSplatVideoPlayer>();
                if (videoPlayer != null && videoPlayer.enabled)
                {
                    videoPlayer.Initialize();
                    return;
                }
                return;
            }

            DisposeResources();
            try
            {
                if (!resourcesAreSetUp) throw new InvalidOperationException("Shader resources are missing");
                EnsureRegistered();

                switch (m_SourceType)
                {
                    case GaussianSplatSourceType.FileOrUrl:
                        if (string.IsNullOrWhiteSpace(m_SourcePath)) return;
                        m_LoadOperation = new GaussianSplatLoadOperation(Application.streamingAssetsPath, m_SourcePath);
                        break;
                    case GaussianSplatSourceType.SplatAsset:
                        if (m_SplatAsset == null || !m_SplatAsset.isValid) return;
                        m_LoadOperation = new GaussianSplatLoadOperation(m_SplatAsset.compressedData, m_SplatAsset.camerasJson);
                        break;
                    case GaussianSplatSourceType.TextAssetBytes:
                        if (m_TextAsset == null || m_TextAsset.bytes == null || m_TextAsset.bytes.Length == 0) return;
                        m_LoadOperation = new GaussianSplatLoadOperation(m_TextAsset.bytes);
                        break;
                }

                isLoading = true;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.update += EditorUpdateLoading;
#endif
            }
            catch (Exception e) { LoadFailed(e); }
        }

        void LoadFailed(Exception e)
        {
            DisposeResources();
            loadError = e.Message;
            Debug.LogError($"Cannot load splats '{m_SourcePath}': {e.Message}", this);
            FinishLoading();
        }

        void FinishLoading()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorUpdateLoading;
#endif
            m_LoadOperation?.Dispose();
            m_LoadOperation = null;
            isLoading = false;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditor.SceneView.RepaintAll();
#endif
        }

        void SetDataOnCS(CommandBuffer cmb)
        {
            ComputeShader cs = GaussianSplatRenderingOptions.instance.csUtilities;
            int kernelIndex = kKernelCalcViewData;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);

            cmb.SetComputeIntParam(cs, Props.SHCoefficients, m_SHCoefficients);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
        }

        internal void SetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetBuffer(Props.SplatColor, m_GpuColorData);
            mat.SetInteger(Props.SHCoefficients, m_SHCoefficients);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResources()
        {
            DisposeBuffer(ref m_GpuColorData);
            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);

            if (m_LocalPositions.IsCreated)
                m_LocalPositions.Dispose();
            m_LocalPositions = default;

            m_Octree?.Clear();
            m_Octree = null;
            m_OctreeBuilt = false;
            m_LastCullingFrame = -1;

            m_SplatCount = 0;
        }

        public void OnDisable()
        {
            FinishLoading();
            DisposeResources();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam, GraphicsBuffer view, GraphicsBuffer distances,
            GraphicsBuffer indices, int offset, bool writeSortKeys, Matrix4x4 projection, Matrix4x4 matView, int width, int height)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            var tr = transform;

            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            Vector4 screenPar = new Vector4(width, height, 0, 0);
            Vector4 camPos = matView.inverse.GetColumn(3);

            // calculate view dependent data for each splat
            SetDataOnCS(cmb);

            GaussianSplatRenderingOptions options = GaussianSplatRenderingOptions.instance;
            ComputeShader cs = options.csUtilities;
            cmb.SetComputeBufferParam(cs, kKernelCalcViewData, Props.SplatViewData, view);
            cmb.SetComputeBufferParam(cs, kKernelCalcViewData, Props.SplatSortDistances, distances);
            cmb.SetComputeBufferParam(cs, kKernelCalcViewData, Props.SplatSortKeys, indices);
            cmb.SetComputeIntParam(cs, Props.SplatOffset, offset);
            cmb.SetComputeIntParam(cs, Props.WriteSortKeys, writeSortKeys ? 1 : 0);
            cmb.SetComputeMatrixParam(cs, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixP, projection);
            cmb.SetComputeMatrixParam(cs, Props.MatrixVP, projection * matView);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(cs, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(cs, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(cs, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(cs, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(cs, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(cs, Props.SHOnly, options.m_SHOnly ? 1 : 0);

            cs.GetKernelThreadGroupSizes(kKernelCalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(cs, kKernelCalcViewData, (m_SplatCount + (int)gsX - 1)/(int)gsX, 1, 1);
        }

        public void Update()
        {
            bool sourceChanged = m_PrevSourceType != m_SourceType;
            if (m_SourceType == GaussianSplatSourceType.FileOrUrl)
                sourceChanged |= m_PrevSourcePath != m_SourcePath;
            else if (m_SourceType == GaussianSplatSourceType.SplatAsset)
                sourceChanged |= m_PrevSplatAsset != m_SplatAsset;
            else if (m_SourceType == GaussianSplatSourceType.TextAssetBytes)
                sourceChanged |= m_PrevTextAsset != m_TextAsset;

            if (sourceChanged) Reload();
#if UNITY_EDITOR
            if (!Application.isPlaying) return;
#endif
            UpdateLoading();
        }

#if UNITY_EDITOR
        void EditorUpdateLoading()
        {
            if (!Application.isPlaying) UpdateLoading();
        }
#endif

        void UpdateLoading()
        {
            if (m_LoadOperation == null) return;
            try
            {
                m_LoadOperation.Update();
                using var data = m_LoadOperation.TakeData();
                if (data != null)
                {
                    CreateResources(data);
#if UNITY_EDITOR
                    UnityEditor.SceneView.RepaintAll();
#endif
                }
                if (m_LoadOperation.isDone)
                {
                    cameras = m_LoadOperation.cameras;
                    if (m_LoadOperation.cameraError != null) Debug.LogWarning(m_LoadOperation.cameraError, this);
                    FinishLoading();
                }
            }
            catch (Exception e) { LoadFailed(e); }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (cameras == null || index < 0 || index >= cameras.Length)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.position;
            camTr.localRotation = cam.rotation;
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }
    }
}
