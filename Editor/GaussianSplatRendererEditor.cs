// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using GaussianSplatRenderer = GaussianSplatting.Runtime.GaussianSplatRenderer;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    [CanEditMultipleObjects]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropSourceType;
        SerializedProperty m_PropSourcePath;
        SerializedProperty m_PropSplatAsset;
        SerializedProperty m_PropTextAsset;
        SerializedProperty m_PropRenderOrder;
        SerializedProperty m_PropSplatScale;
        SerializedProperty m_PropOpacityScale;
        SerializedProperty m_PropSHOrder;

        readonly Utils.FilePickerControl m_FilePicker = new();
        int m_CameraIndex = 0;
        bool m_WasLoading;

        public void OnEnable()
        {
            m_PropSourceType = serializedObject.FindProperty("m_SourceType");
            m_PropSourcePath = serializedObject.FindProperty("m_SourcePath");
            m_PropSplatAsset = serializedObject.FindProperty("m_SplatAsset");
            m_PropTextAsset = serializedObject.FindProperty("m_TextAsset");
            m_PropRenderOrder = serializedObject.FindProperty("m_RenderOrder");
            m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
            m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
            m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
        }

        public override bool RequiresConstantRepaint() => ((GaussianSplatRenderer)target).isLoading || m_WasLoading;

        public override void OnInspectorGUI()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs)
                return;

            serializedObject.Update();

            GUILayout.Label("Source Configuration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropSourceType, new GUIContent("Source Type"));

            var sourceType = (GaussianSplatSourceType)m_PropSourceType.enumValueIndex;
            var source = m_PropSourcePath;
            string sourcePath = source.stringValue;

            switch (sourceType)
            {
                case GaussianSplatSourceType.FileOrUrl:
                {
                    Rect rect = EditorGUILayout.GetControlRect();
                    EditorGUI.showMixedValue = source.hasMultipleDifferentValues;
                    string path = m_FilePicker.PathFieldGUI(rect, new GUIContent("PLY / SPZ"), sourcePath);
                    EditorGUI.showMixedValue = false;
                    if (path != sourcePath)
                    {
                        sourcePath = path;
                        source.stringValue = sourcePath;
                    }
                    if (!source.hasMultipleDifferentValues && string.IsNullOrWhiteSpace(sourcePath))
                        EditorGUILayout.HelpBox("Gaussian Splat source file is not assigned", MessageType.Error);
                    else if (!source.hasMultipleDifferentValues)
                    {
                        try
                        {
                            string full = GaussianSplatReader.ResolvePath(Application.streamingAssetsPath, sourcePath);
                            if (!GaussianSplatPaths.IsUrl(full) && !File.Exists(full))
                                EditorGUILayout.HelpBox("Source file is missing: " + full, MessageType.Warning);
                        }
                        catch (Exception e) { EditorGUILayout.HelpBox(e.Message, MessageType.Error); }
                    }
                    break;
                }
                case GaussianSplatSourceType.SplatAsset:
                {
                    EditorGUILayout.PropertyField(m_PropSplatAsset, new GUIContent("Splat Asset"));
                    if (!m_PropSplatAsset.hasMultipleDifferentValues && m_PropSplatAsset.objectReferenceValue == null)
                        EditorGUILayout.HelpBox("Assign a baked/imported GaussianSplatAsset (.spz or .ply asset).", MessageType.Info);
                    break;
                }
                case GaussianSplatSourceType.TextAssetBytes:
                {
                    EditorGUILayout.PropertyField(m_PropTextAsset, new GUIContent("TextAsset (.bytes)"));
                    if (!m_PropTextAsset.hasMultipleDifferentValues && m_PropTextAsset.objectReferenceValue == null)
                        EditorGUILayout.HelpBox("Assign a TextAsset containing raw bytes (.spz.bytes or .ply.bytes).", MessageType.Info);
                    break;
                }
                case GaussianSplatSourceType.ExternalStreamOr4D:
                {
                    var p4d = gs.GetComponent<GaussianSplat4DPlayer>();
                    var pvid = gs.GetComponent<GaussianSplatVideoPlayer>();
                    var precv = gs.GetComponent<GaussianSplatStreamReceiver>();

                    if (p4d == null && pvid == null && precv == null)
                    {
                        EditorGUILayout.HelpBox("No 4D player or stream receiver attached to this GameObject. Add one below to feed dynamic splats:", MessageType.Warning);
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("Add 4D Player"))
                        {
                            Undo.AddComponent<GaussianSplat4DPlayer>(gs.gameObject);
                        }
                        if (GUILayout.Button("Add Video Player"))
                        {
                            Undo.AddComponent<GaussianSplatVideoPlayer>(gs.gameObject);
                        }
                        if (GUILayout.Button("Add Stream Receiver"))
                        {
                            Undo.AddComponent<GaussianSplatStreamReceiver>(gs.gameObject);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    else
                    {
                        string activeComp = p4d != null ? "GaussianSplat4DPlayer" : (pvid != null ? "GaussianSplatVideoPlayer" : "GaussianSplatStreamReceiver");
                        EditorGUILayout.HelpBox($"Dynamic splat buffers are controlled by {activeComp}.", MessageType.Info);
                    }
                    break;
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Render Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderOrder);
            EditorGUILayout.PropertyField(m_PropSplatScale);
            EditorGUILayout.PropertyField(m_PropOpacityScale);
            EditorGUILayout.PropertyField(m_PropSHOrder);

            EditorGUILayout.Space();
            bool hasValidSource = sourceType switch
            {
                GaussianSplatSourceType.FileOrUrl => !string.IsNullOrWhiteSpace(sourcePath),
                GaussianSplatSourceType.SplatAsset => m_PropSplatAsset.objectReferenceValue != null,
                GaussianSplatSourceType.TextAssetBytes => m_PropTextAsset.objectReferenceValue != null,
                GaussianSplatSourceType.ExternalStreamOr4D => true,
                _ => false
            };

            bool validAndEnabled = gs && gs.enabled && gs.gameObject.activeInHierarchy && hasValidSource;
            if (validAndEnabled && !gs.HasValidRenderResources)
            {
                EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);
                validAndEnabled = false;
            }

            bool sourceChanged = false;
            foreach (var obj in targets)
            {
                var targetGs = (GaussianSplatRenderer)obj;
                sourceChanged |= targetGs.m_SourceType != sourceType;
                if (sourceType == GaussianSplatSourceType.FileOrUrl)
                    sourceChanged |= !source.hasMultipleDifferentValues && targetGs.m_SourcePath != sourcePath;
                else if (sourceType == GaussianSplatSourceType.SplatAsset)
                    sourceChanged |= !m_PropSplatAsset.hasMultipleDifferentValues && targetGs.m_SplatAsset != m_PropSplatAsset.objectReferenceValue;
                else if (sourceType == GaussianSplatSourceType.TextAssetBytes)
                    sourceChanged |= !m_PropTextAsset.hasMultipleDifferentValues && targetGs.m_TextAsset != m_PropTextAsset.objectReferenceValue;
            }

            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("Reload") || sourceChanged)
                foreach (var obj in targets) ((GaussianSplatRenderer)obj).Reload();
            if (validAndEnabled && targets.Length == 1)
            {
                if (gs.isLoading) EditorGUILayout.LabelField("Loading...");
                else if (!string.IsNullOrEmpty(gs.loadError)) EditorGUILayout.HelpBox(gs.loadError, MessageType.Error);
                else if (gs.HasValidRenderSetup)
                {
                    EditCameras(gs);
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Splats", $"{gs.splatCount:N0}");
                    EditorGUILayout.LabelField("SH Degree", gs.sourceSHDegree.ToString());
                    int coefficients = (gs.sourceSHDegree + 1) * (gs.sourceSHDegree + 1) - 1;
                    int stride = 28 + GaussianUtils.GetSHDataSize(coefficients);
                    EditorGUILayout.LabelField("Splat Data", $"{stride} bytes/splat ({(long)stride * gs.splatCount / (1024.0 * 1024):N1} MiB)");
                    EditorGUILayout.LabelField("Bounds", $"{gs.localBounds.size.x:F2} x {gs.localBounds.size.y:F2} x {gs.localBounds.size.z:F2}");
                    if (gs.octreeBuilt && gs.octree != null)
                    {
                        gs.octree.GetDebugInfo(out int leafNodes, out int maxDepth, out int maxSplatsInLeaf);
                        EditorGUILayout.LabelField("Octree", $"{gs.octree.nodeCount:N0} nodes ({leafNodes:N0} leaves, depth {maxDepth}, max {maxSplatsInLeaf:N0}/leaf)");
                        EditorGUILayout.LabelField("Visible Splats", $"{gs.octree.visibleSplatCount:N0} / {gs.splatCount:N0}");
                    }
                }
            }
            // Keep requesting a repaint until the completed state has replaced "Loading...".
            if (Event.current.type == EventType.Repaint)
                m_WasLoading = gs.isLoading;
        }


        void EditCameras(GaussianSplatRenderer gs)
        {
            var cameras = gs.cameras;
            if (cameras != null && cameras.Length != 0)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Cameras", EditorStyles.boldLabel);
                var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
                camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
                if (camIndex != m_CameraIndex)
                {
                    m_CameraIndex = camIndex;
                    gs.ActivateCamera(camIndex);
                }
            }
        }

        bool HasFrameBounds()
        {
            return true;
        }

        Bounds OnGetFrameBounds()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs || !gs.HasValidRenderSetup)
                return new Bounds(Vector3.zero, Vector3.one);
            Bounds bounds = gs.localBounds;
            bounds.extents *= 0.7f;
            return TransformBounds(gs.transform, bounds);
        }

        static Bounds TransformBounds(Transform tr, Bounds bounds)
        {
            var center = tr.TransformPoint(bounds.center);

            var ext = bounds.extents;
            var axisX = tr.TransformVector(ext.x, 0, 0);
            var axisY = tr.TransformVector(0, ext.y, 0);
            var axisZ = tr.TransformVector(0, 0, ext.z);

            // sum their absolute value to get the world extents
            ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            return new Bounds { center = center, extents = ext };
        }
    }
}
