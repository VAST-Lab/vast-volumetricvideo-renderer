// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Editor
{
	[CustomEditor(typeof(GaussianSplat4DPlayer))]
	public class GaussianSplat4DPlayerEditor : UnityEditor.Editor
	{
		SerializedProperty m_PropSourceMode;
		SerializedProperty m_PropSourcePath;
		SerializedProperty m_PropFourDGSAsset;
		SerializedProperty m_PropFourDGSTextAsset;
		SerializedProperty m_PropSpzSequence;
		SerializedProperty m_PropPlayOnAwake;
		SerializedProperty m_PropLoop;
		SerializedProperty m_PropPlaybackSpeed;
		SerializedProperty m_PropSequenceFrameRate;
		SerializedProperty m_PropCurrentFrame;

		readonly Utils.FilePickerControl m_FilePicker = new();

		void OnEnable()
		{
			m_PropSourceMode = serializedObject.FindProperty("m_SourceMode");
			m_PropSourcePath = serializedObject.FindProperty("m_SourcePath");
			m_PropFourDGSAsset = serializedObject.FindProperty("m_FourDGSAsset");
			m_PropFourDGSTextAsset = serializedObject.FindProperty("m_FourDGSTextAsset");
			m_PropSpzSequence = serializedObject.FindProperty("m_SpzSequence");
			m_PropPlayOnAwake = serializedObject.FindProperty("m_PlayOnAwake");
			m_PropLoop = serializedObject.FindProperty("m_Loop");
			m_PropPlaybackSpeed = serializedObject.FindProperty("m_PlaybackSpeed");
			m_PropSequenceFrameRate = serializedObject.FindProperty("m_SequenceFrameRate");
			m_PropCurrentFrame = serializedObject.FindProperty("m_CurrentFrame");
		}

		public override bool RequiresConstantRepaint()
		{
			var player = target as GaussianSplat4DPlayer;
			return player != null && player.isPlaying;
		}

		public override void OnInspectorGUI()
		{
			var player = target as GaussianSplat4DPlayer;
			if (!player) return;

			serializedObject.Update();

			EditorGUILayout.LabelField("4DGS Volumetric Source", EditorStyles.boldLabel);
			EditorGUI.BeginChangeCheck();
			EditorGUILayout.PropertyField(m_PropSourceMode, new GUIContent("Source Mode"));
			bool modeChanged = EditorGUI.EndChangeCheck();

			var mode = (GaussianSplat4DPlayer.SourceMode)m_PropSourceMode.enumValueIndex;
			bool sourceChanged = modeChanged;

			switch (mode)
			{
				case GaussianSplat4DPlayer.SourceMode.FourDGSFile:
				{
					Rect rect = EditorGUILayout.GetControlRect();
					string oldPath = m_PropSourcePath.stringValue;
					string newPath = m_FilePicker.PathFieldGUI(
						rect,
						new GUIContent(".4DGS File / URL"),
						oldPath,
						new[] { "4dgs", "spz", "ply" },
						"4D Gaussian Splats");
					if (newPath != oldPath)
					{
						m_PropSourcePath.stringValue = newPath;
						sourceChanged = true;
					}
					break;
				}

				case GaussianSplat4DPlayer.SourceMode.FourDGSAsset:
				{
					EditorGUI.BeginChangeCheck();
					EditorGUILayout.PropertyField(m_PropFourDGSAsset, new GUIContent(".4DGS Asset"));
					EditorGUILayout.PropertyField(m_PropFourDGSTextAsset, new GUIContent("Fallback .bytes Asset"));
					if (EditorGUI.EndChangeCheck())
						sourceChanged = true;
					break;
				}

				case GaussianSplat4DPlayer.SourceMode.SequenceSpzAssets:
				{
					EditorGUI.BeginChangeCheck();
					EditorGUILayout.PropertyField(m_PropSequenceFrameRate, new GUIContent("Sequence FPS"));
					EditorGUILayout.PropertyField(m_PropSpzSequence, new GUIContent("SPZ Asset Frames"), true);
					if (EditorGUI.EndChangeCheck())
						sourceChanged = true;
					break;
				}

				case GaussianSplat4DPlayer.SourceMode.SequenceFileFolder:
				{
					EditorGUI.BeginChangeCheck();
					EditorGUILayout.PropertyField(m_PropSequenceFrameRate, new GUIContent("Sequence FPS"));
					if (EditorGUI.EndChangeCheck())
						sourceChanged = true;

					Rect rect = EditorGUILayout.GetControlRect();
					string oldFolder = m_PropSourcePath.stringValue;
					string newFolder = m_FilePicker.FolderFieldGUI(rect, new GUIContent("Sequence Folder"), oldFolder);
					if (newFolder != oldFolder)
					{
						m_PropSourcePath.stringValue = newFolder;
						sourceChanged = true;
					}
					break;
				}
			}

			EditorGUILayout.Space(6);
			EditorGUILayout.LabelField("Playback Settings", EditorStyles.boldLabel);
			EditorGUILayout.PropertyField(m_PropPlayOnAwake);
			EditorGUILayout.PropertyField(m_PropLoop);
			EditorGUILayout.PropertyField(m_PropPlaybackSpeed);

			serializedObject.ApplyModifiedProperties();

			if (sourceChanged)
			{
				player.Initialize();
				SceneView.RepaintAll();
			}

			EditorGUILayout.Space(8);
			DrawTransportAndScrubberGUI(player);

			EditorGUILayout.Space(6);
			DrawDiagnosticsAndQuickToolsGUI(player);
		}

		void DrawTransportAndScrubberGUI(GaussianSplat4DPlayer player)
		{
			EditorGUILayout.BeginVertical(EditorStyles.helpBox);
			EditorGUILayout.LabelField("Timeline & Preview Controls (Edit & Play Mode)", EditorStyles.boldLabel);

			using (new EditorGUI.DisabledScope(!player.isInitialized || player.totalFrames <= 0))
			{
				// Scrubber Slider
				EditorGUI.BeginChangeCheck();
				int maxFrame = Mathf.Max(0, player.totalFrames - 1);
				int newFrame = EditorGUILayout.IntSlider("Frame", player.currentFrame, 0, maxFrame);
				if (EditorGUI.EndChangeCheck())
				{
					player.Pause();
					player.Seek(newFrame);
					SceneView.RepaintAll();
				}

				// Time & Metadata Readout
				EditorGUILayout.LabelField(
					"Time",
					$"{player.time:F2}s / {player.duration:F2}s  ({player.frameRate:F1} FPS, {player.totalFrames} frames)");
				EditorGUILayout.LabelField("Active Splats", $"{player.activeSplatCount:N0}");

				// Transport Buttons
				EditorGUILayout.BeginHorizontal();
				if (GUILayout.Button("|<", GUILayout.Width(36)))
				{
					player.Pause();
					player.Seek(0);
				}
				if (GUILayout.Button("< Step", GUILayout.Width(60)))
				{
					player.StepBackward();
				}
				string playLabel = player.isPlaying ? "Pause ||" : "Play >";
				if (GUILayout.Button(playLabel, GUILayout.Height(24)))
				{
					if (player.isPlaying) player.Pause();
					else player.Play();
				}
				if (GUILayout.Button("Step >", GUILayout.Width(60)))
				{
					player.StepForward();
				}
				if (GUILayout.Button("Stop []", GUILayout.Width(60)))
				{
					player.Stop();
				}
				EditorGUILayout.EndHorizontal();
			}

			EditorGUILayout.EndVertical();
		}

		void DrawDiagnosticsAndQuickToolsGUI(GaussianSplat4DPlayer player)
		{
			if (!string.IsNullOrEmpty(player.lastError))
			{
				EditorGUILayout.HelpBox(player.lastError, MessageType.Error);
			}
			else if (!player.isInitialized)
			{
				EditorGUILayout.HelpBox("No 4DGS clip loaded. Assign a source above or click 'Reload / Initialize'.", MessageType.Info);
			}

			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button("Reload / Initialize"))
			{
				player.Initialize();
				SceneView.RepaintAll();
			}
			if (GUILayout.Button("Open 4DGS Baker..."))
			{
				FourDGSSequenceBakerWindow.Open();
			}
			EditorGUILayout.EndHorizontal();

			// Test Asset Loader helper
			if (!player.isInitialized && File.Exists("test_data~/animated_sphere.4dgs"))
			{
				EditorGUILayout.Space(2);
				if (GUILayout.Button("Load Test .4dgs Sphere"))
				{
					string targetDir = Application.streamingAssetsPath;
					if (!Directory.Exists(targetDir))
						Directory.CreateDirectory(targetDir);
					string targetFile = Path.Combine(targetDir, "animated_sphere.4dgs");
					File.Copy("test_data~/animated_sphere.4dgs", targetFile, overwrite: true);
					m_PropSourceMode.enumValueIndex = (int)GaussianSplat4DPlayer.SourceMode.FourDGSFile;
					m_PropSourcePath.stringValue = "animated_sphere.4dgs";
					serializedObject.ApplyModifiedProperties();
					player.Initialize();
					SceneView.RepaintAll();
				}
			}
		}
	}
}
