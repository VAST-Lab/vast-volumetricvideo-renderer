// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Editor
{
	[ScriptedImporter(1, new[] { "4dgs" })]
	public class FourDGSImporter : ScriptedImporter
	{
		public override void OnImportAsset(AssetImportContext ctx)
		{
			byte[] fileBytes = File.ReadAllBytes(ctx.assetPath);
			if (fileBytes == null || fileBytes.Length < 64)
			{
				ctx.LogImportError($"Invalid or truncated .4dgs file: {ctx.assetPath}");
				return;
			}

			try
			{
				using var ms = new MemoryStream(fileBytes, writable: false);
				var header = FourDGSFormat.ReadHeader(ms);

				var asset = ScriptableObject.CreateInstance<FourDGSAsset>();
				asset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
				asset.totalFrames = (int)header.totalFrames;
				asset.maxSplatCount = (int)header.maxSplatCount;
				asset.shDegree = header.shDegree;
				asset.gopSize = header.gopSize;
				asset.frameRate = header.frameRate;
				asset.bounds = header.bounds;
				asset.data = fileBytes;

				ctx.AddObjectToAsset("main", asset);
				ctx.SetMainObject(asset);
			}
			catch (Exception e)
			{
				ctx.LogImportError($"Failed to import .4dgs file '{ctx.assetPath}': {e.Message}");
			}
		}
	}
}
