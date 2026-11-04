// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
	public class FourDGSAsset : ScriptableObject
	{
		public int totalFrames;
		public int maxSplatCount;
		public int shDegree;
		public int gopSize;
		public float frameRate = 30.0f;
		public Bounds bounds;

		[HideInInspector]
		[SerializeField]
		public byte[] data;

		public bool isValid => data != null && data.Length >= 64 && totalFrames > 0 && maxSplatCount > 0;
		public int dataSize => data != null ? data.Length : 0;
	}
}
