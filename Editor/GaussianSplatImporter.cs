// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.IO.Compression;
using GaussianSplatting.Runtime;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [ScriptedImporter(1, new[] { "spz", "3dgs" }, new[] { "ply" })]
    public class GaussianSplatImporter : ScriptedImporter
    {
        [Tooltip("Clamp higher-order SH coefficients to save memory and bandwidth for WebGPU / itch.io builds")]
        [Range(0, 3)]
        public int maxSHDegree = 3;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            byte[] fileBytes = File.ReadAllBytes(ctx.assetPath);
            if (fileBytes == null || fileBytes.Length < 4)
            {
                ctx.LogImportError($"Failed to read splat asset: file is empty or corrupted: {ctx.assetPath}");
                return;
            }

            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);

            bool isGzipSpz = fileBytes.Length >= 2 && fileBytes[0] == 0x1f && fileBytes[1] == 0x8b;
            bool isRawSpz = fileBytes.Length >= 2 && fileBytes[0] == 'N' && fileBytes[1] == 'G';

            if (isGzipSpz || isRawSpz)
            {
                // File is already SPZ
                using var ms = new MemoryStream(fileBytes, writable: false);
                Stream headerStream = ms;
                GZipStream gzipStream = null;
                if (isGzipSpz)
                {
                    gzipStream = new GZipStream(ms, CompressionMode.Decompress, true);
                    headerStream = gzipStream;
                }

                try
                {
                    var header = SPZFileReader.ReadHeader(headerStream);
                    asset.splatCount = header.count;
                    asset.shDegree = header.degree;
                    asset.formatVersion = header.version;
                    asset.compressedData = fileBytes;
                }
                finally
                {
                    gzipStream?.Dispose();
                }
            }
            else
            {
                // Transcode PLY to SPZ v4 inside Editor so the built WebGPU game is ~10x smaller
                try
                {
                    asset.compressedData = SPZTranscoder.ConvertPlyToSpzV4(
                        fileBytes,
                        maxSHDegree,
                        out asset.splatCount,
                        out asset.shDegree,
                        out asset.bounds);
                    asset.formatVersion = 4;
                }
                catch (Exception e)
                {
                    ctx.LogImportError($"Failed to transcode PLY '{ctx.assetPath}' to SPZ v4: {e.Message}");
                    return;
                }
            }

            // Check for camera sidecar JSON file
            string camPath = Path.ChangeExtension(ctx.assetPath, ".json");
            if (File.Exists(camPath))
            {
                try
                {
                    asset.camerasJson = File.ReadAllText(camPath);
                }
                catch (Exception e)
                {
                    ctx.LogImportWarning($"Failed to read camera JSON '{camPath}': {e.Message}");
                }
            }

            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);
        }
    }
}
