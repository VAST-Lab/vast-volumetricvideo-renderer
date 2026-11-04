# Unity 6000.6 Gaussian Splatting (WebGPU + v2.0.0 Unified Package)

A unified Unity 6000.6 package combining **[aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) v2.0.0** with **WebGPU compatibility**, **Subgroup-Free WebGPU Compute Radix Sorting**, and **Hierarchical Octree + Native WASM/CPU Cached Sorting**.

## Key Features
- **Direct Runtime & Edit-Mode Loading (`GaussianSplatLoadOperation`)**:
  - Loads `.ply` and `.spz` (versions 1–4, including Zstd-compressed SPZ v4 and GZip-compressed SPZ v1–3) directly from `StreamingAssets`, local file paths, or remote `http://`/`https://` URLs without requiring offline ScriptableObject baking.
  - Uses time-sliced main-thread GZip decompression on WebGL/WebGPU (`UNITY_WEBGL`) and Burst-compiled data preparation (`GaussianSplatPrepareDataJob`).
- **Ultra-Compact GPU Layout (`GaussianSplatData`)**:
  - 28 bytes/splat base data + 0/8/16/32 bytes/splat for SH degrees 0–3 (28–60 bytes/splat total).
- **Dual Sorting Backends (`SortingMode` in `GaussianSplatRenderingOptions`)**:
  - **`GpuRadixSort` (Default via `Auto`)**:
    - Uses fast SM 6.0 Wave-Intrinsic `DeviceRadixSort` (`GaussianSplatSortWave.compute`) on supported desktop GPUs.
    - Automatically falls back to **Subgroup-Free Compute Radix Sort** (`WebGpuRadixSort.hlsl` in `GaussianSplatUtilities.compute`, using `[numthreads(256, 1, 1)]` and `groupshared` memory scans with zero wave intrinsics) on **WebGPU**.
  - **`OctreeCpuSort`**:
    - Hierarchical local-space octree frustum culling (`GaussianSplatOctree`) with outlier filtering, cached per-leaf distance sorting, and non-blocking native Emscripten `pthread` worker sorting (`NativeSorting.c`) on WebGL/WebGPU or C# `Task` workers on desktop.
- **SteadySplats Reprojected Stochastic Transparency (`GaussianSplatTemporalAccumulator`)**:
  - Sort-free stochastic rendering with depth-guided neighborhood clamping and camera reprojection, plus Built-in RP, URP 17 (`RenderGraph`), and HDRP 17 support.

## Quick Start
1. Add a **`GaussianSplatRenderer`** component to a GameObject and assign `m_SourcePath` to a `.ply` or `.spz` file (relative to `Assets/StreamingAssets`, a local path, or a URL).
2. *(Optional)* Add a **`GaussianSplatRenderingOptions`** component to your scene to configure:
   - **Transparency Mode**: `SortedBlended` or `Stochastic` (with `Reprojected` temporal accumulation).
   - **Sorting Mode**: `Auto`, `GpuRadixSort`, or `OctreeCpuSort`.
   - **Octree Settings**: `m_OctreeMaxDepth`, `m_OctreeMaxSplatsPerLeaf`, `m_OctreeCullingUpdateInterval`, `m_OctreeSplatRatio`, and `m_DrawOctreeGizmos`.

## WebGPU Threading Setup (When Using `OctreeCpuSort`)
For multi-threaded WASM sorting in `OctreeCpuSort` mode on WebGPU:
1. **Unity Settings**: Enable **"Native C++ Threads Support"** in Project Settings > Player > WebGL/WebGPU Publishing Settings.
2. **Web Server**: Serve with COOP/COEP headers for `SharedArrayBuffer` support:
   ```
   Cross-Origin-Opener-Policy: same-origin
   Cross-Origin-Embedder-Policy: require-corp
   ```

## License
See `LICENSE.md`.
