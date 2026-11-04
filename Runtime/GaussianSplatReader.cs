// SPDX-License-Identifier: MIT
using System;
using System.IO;

namespace GaussianSplatting.Runtime
{
	public static class GaussianSplatReader
	{
		static readonly string[] kStaticExtensions = { ".ply", ".spz", ".3dgs", ".bytes" };
		static readonly string[] kFourDExtensions = { ".4dgs", ".spz", ".ply", ".bytes" };
		static readonly string[] kVideoExtensions = { ".mp4", ".webm", ".mov", ".m4v" };

		public static string ResolvePath(string root, string sourcePath)
		{
			return ResolveFileWithExtensions(root, sourcePath, kStaticExtensions, "Source must be a .ply or .spz file");
		}

		public static string Resolve4DPath(string root, string sourcePath)
		{
			return ResolveFileWithExtensions(root, sourcePath, kFourDExtensions, "4D source must be a .4dgs, .spz, .ply, or .bytes file");
		}

		public static string ResolveVideoPath(string root, string sourcePath)
		{
			return ResolveFileWithExtensions(root, sourcePath, kVideoExtensions, "Video source must be a .mp4, .webm, or .mov file");
		}

		public static string ResolveFolderPath(string root, string folderPath)
		{
			string path = (folderPath ?? "").Trim();
			if (string.IsNullOrWhiteSpace(path))
				throw new ArgumentException("Choose a sequence folder path");

			path = GaussianSplatPaths.ToForwardSlashes(path);
			if (Path.IsPathRooted(path))
				return Path.GetFullPath(path);

			string streamingPath = GaussianSplatPaths.Combine(root, path);
			if (Directory.Exists(streamingPath))
				return Path.GetFullPath(streamingPath);

			return Path.GetFullPath(path);
		}

		public static string ResolveFileWithExtensions(string root, string sourcePath, string[] allowedExtensions, string errorMsg)
		{
			string path = (sourcePath ?? "").Trim();
			if (string.IsNullOrWhiteSpace(path))
				throw new ArgumentException("Source file path or URL is empty");

			bool url = GaussianSplatPaths.IsUrl(path);
			string filePath = path;
			if (url)
			{
				if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
					throw new ArgumentException("Invalid source URL");
				filePath = uri.AbsolutePath;
			}
			else
			{
				path = GaussianSplatPaths.ToForwardSlashes(path);
				filePath = path;
			}

			if (allowedExtensions != null && allowedExtensions.Length > 0)
			{
				string ext = Path.GetExtension(filePath).ToLowerInvariant();
				bool matched = false;
				for (int i = 0; i < allowedExtensions.Length; ++i)
				{
					if (ext == allowedExtensions[i])
					{
						matched = true;
						break;
					}
				}
				if (!matched)
					throw new ArgumentException(errorMsg);
			}

			if (url)
				return path;
			if (Path.IsPathRooted(path))
				return Path.GetFullPath(path);

			string streamingPath = GaussianSplatPaths.Combine(root, path);
			if (GaussianSplatPaths.IsUrl(streamingPath) || File.Exists(streamingPath))
				return streamingPath;

			return Path.GetFullPath(path);
		}
	}
}
