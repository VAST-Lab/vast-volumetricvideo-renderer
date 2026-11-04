// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// ScriptableObject holding a compressed 3DGS/4DGS payload (such as SPZ v4) and metadata.
    /// Can be embedded directly in scenes, prefabs, or Addressable asset bundles.
    /// </summary>
    public class GaussianSplatAsset : ScriptableObject
    {
        [Tooltip("Format version: 4 = SPZ v4 (Zstd compressed streams)")]
        public int formatVersion = 4;

        [Tooltip("Total number of splats in this asset")]
        public int splatCount;

        [Tooltip("Spherical Harmonics degree (0 to 3)")]
        [Range(0, 3)]
        public int shDegree;

        [Tooltip("Local bounding box of all splats")]
        public Bounds bounds;

        [HideInInspector]
        [SerializeField]
        public byte[] compressedData;

        [TextArea(2, 6)]
        [Tooltip("Optional cameras JSON sidecar data")]
        public string camerasJson;

        public bool isValid => compressedData != null && compressedData.Length > 0 && splatCount > 0;

        public int dataSize => compressedData != null ? compressedData.Length : 0;
    }
}
