# Unity 6000.6 Gaussian Splatting & 4DGS Volumetric Video (v3.0.0 WebGPU)

A unified Unity 6000.6+ package combining **3D Gaussian Splatting** and **4D Volumetric Video** with full **WebGPU compatibility**, **Subgroup-Free GPU Radix Sorting**, **Multi-Source Asset Packaging**, and **Temporal GOP Delta Streaming**.

---

## What's New in v3.0.0 (4DGS & itch.io Ready)

### 1. 4DGS Volumetric Video Playback (`GaussianSplat4DPlayer`)
- **Custom `.4dgs` Container**: Group-of-Pictures (GOP) temporal delta compression combining SPZ v4 keyframes with Burst-compiled `int16` position and quaternion deltas. Achieves **15x–40x compression** (~3–8 MB/sec at 30 FPS) with zero visual ringing.
- **In-Place Dynamic GPU Updates**: Eliminates per-frame `GraphicsBuffer` reallocation and garbage collection. Dynamically updates GPU VRAM at 30–60 FPS.
- **Sequence Playback**: Direct playback support for sequences of `.spz` or `.ply` files for rapid iteration during authoring.

### 2. MP4 Video-Texture Splatting ("SplatVideo") (`GaussianSplatVideoPlayer`)
- Offloads decompression entirely to the browser's native hardware video decoder (H.264/VP9/AV1).
- Samples a 4-quadrant video atlas ($2W \times 2H$) directly in a compute shader (`CSDecodeVideoSplats`) on WebGPU, updating splat buffers directly in VRAM with **zero CPU/WASM overhead**.

### 3. Live Host-to-Web Streaming (`GaussianSplatStreamReceiver`)
- Real-time binary WebSocket streaming protocol (`"SGST"`) with jitter buffer for streaming volumetric sequences from desktop/cloud capture rigs to WebGPU clients.
- Dual-platform implementation: `ClientWebSocket` on Desktop/Editor and native browser bridge (`WebSocketBridge.jslib`) on WebGL/WebGPU.
- In-Editor server (`Tools > Gaussian Splats > 4DGS Stream Server`) and standalone Python server (`tools/stream_ply_sequence.py`).

### 4. Multi-Source Static 3DGS Loading (`GaussianSplatAsset`)
- **Mode A: `GaussianSplatAsset` (`ScriptableObject`)**: Embeds SPZ v4 compressed bytes directly into Unity scenes, prefabs, or Addressable bundles.
- **Mode B: `TextAsset` (`.spz.bytes` / `.ply.bytes`)**: Zero-config binary bundling.
- **Mode C: `StreamingAssets` / File / URL**: Existing file path workflow.
- **Mode D: Browser Drag-and-Drop (`WebFileUpload.jslib`)**: Drag `.ply`, `.spz`, or `.4dgs` files directly onto the WebGPU canvas in the browser.

### 5. Unified 3D Mesh + Static 3DGS + Animated 4DGS Composition
- Static environments and animated 4D actors share the global radix sorting buffer (`GaussianSplatRenderSystem`) and test against the scene hardware depth buffer.
- Dynamic actors interpenetrate cleanly with static splat rooms and 3D meshes with zero sorting seams!

---

## Core Features
- **Dual Sorting Backends (`SortingMode` in `GaussianSplatRenderingOptions`)**:
  - **`GpuRadixSort` (Default via `Auto`)**: Fast wave-intrinsic sorting on desktop GPUs; automatically falls back to **Subgroup-Free Compute Radix Sort** (`WebGpuRadixSort.hlsl`) on **WebGPU**.
  - **`OctreeCpuSort`**: Frustum culling and distance sorting with multi-threaded WASM workers (`NativeSorting.c`) on WebGL/WebGPU.
- **SteadySplats Reprojected Stochastic Transparency (`GaussianSplatTemporalAccumulator`)**: Sort-free stochastic rendering with depth-guided neighborhood clamping and reprojection across Built-in RP, URP 17, and HDRP 17.

---

## Editor Tools
- **4DGS Sequence Baker** (`Tools > Gaussian Splats > 4DGS Sequence Baker`):
  Bakes a folder of PLY/SPZ frames into a `.4dgs` container file with 3D Morton curve sorting and temporal deltas, or exports MP4 Video Atlas frames.
- **4DGS Stream Server** (`Tools > Gaussian Splats > 4DGS Stream Server`):
  Hosts a local WebSocket server broadcasting frames at 30 FPS.
- **Validator** (`Tools > Gaussian Splats > Debug > Validate 4DGS Decoder Pipeline`):
  Automated testing of the 4DGS container reader, Zstd decompression, and Burst delta pipeline.

---

## License
See `LICENSE.txt`.
