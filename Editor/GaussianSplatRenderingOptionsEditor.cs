// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderingOptions))]
    [CanEditMultipleObjects]
    public class GaussianSplatRenderingOptionsEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropScript;
        SerializedProperty m_PropTransparency;
        SerializedProperty m_PropTemporalFilter;
        SerializedProperty m_PropSortingMode;
        SerializedProperty m_PropOctreeCullInStochastic;
        SerializedProperty m_PropOctreeMaxDepth;
        SerializedProperty m_PropOctreeMaxSplatsPerLeaf;
        SerializedProperty m_PropOctreeCullingUpdateInterval;
        SerializedProperty m_PropOctreeSplatRatio;
        SerializedProperty m_PropDrawOctreeGizmos;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointSize;
        SerializedProperty m_PropSHOnly;

        void OnEnable()
        {
            m_PropScript = serializedObject.FindProperty("m_Script");
            m_PropTransparency = serializedObject.FindProperty("m_Transparency");
            m_PropTemporalFilter = serializedObject.FindProperty("m_TemporalFilter");
            m_PropSortingMode = serializedObject.FindProperty("m_SortingMode");
            m_PropOctreeCullInStochastic = serializedObject.FindProperty("m_OctreeCullInStochastic");
            m_PropOctreeMaxDepth = serializedObject.FindProperty("m_OctreeMaxDepth");
            m_PropOctreeMaxSplatsPerLeaf = serializedObject.FindProperty("m_OctreeMaxSplatsPerLeaf");
            m_PropOctreeCullingUpdateInterval = serializedObject.FindProperty("m_OctreeCullingUpdateInterval");
            m_PropOctreeSplatRatio = serializedObject.FindProperty("m_OctreeSplatRatio");
            m_PropDrawOctreeGizmos = serializedObject.FindProperty("m_DrawOctreeGizmos");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointSize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(m_PropScript);
            EditorGUILayout.PropertyField(m_PropTransparency);
            if (!m_PropTransparency.hasMultipleDifferentValues && m_PropTransparency.intValue == (int)TransparencyMode.Stochastic)
                EditorGUILayout.PropertyField(m_PropTemporalFilter);

            EditorGUILayout.PropertyField(m_PropSortingMode);
            if (!m_PropSortingMode.hasMultipleDifferentValues && m_PropSortingMode.intValue == (int)SortingMode.OctreeCpuSort)
            {
                EditorGUILayout.PropertyField(m_PropOctreeCullInStochastic);
                EditorGUILayout.PropertyField(m_PropOctreeMaxDepth);
                EditorGUILayout.PropertyField(m_PropOctreeMaxSplatsPerLeaf);
                EditorGUILayout.PropertyField(m_PropOctreeCullingUpdateInterval);
                EditorGUILayout.PropertyField(m_PropOctreeSplatRatio);
                EditorGUILayout.PropertyField(m_PropDrawOctreeGizmos);

                if (Application.isPlaying)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Octree Culling Statistics", EditorStyles.boldLabel);
                    var renderers = Object.FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
                    foreach (var renderer in renderers)
                    {
                        if (renderer.isActiveAndEnabled && renderer.octreeBuilt && renderer.octree != null)
                        {
                            renderer.octree.GetDebugInfo(out int leafNodes, out int maxDepth, out int maxSplatsInLeaf);
                            int visibleSplats = renderer.octree.visibleSplatCount;
                            int totalSplats = renderer.splatCount;
                            float cullRatio = totalSplats > 0 ? (float)visibleSplats / totalSplats * 100f : 0f;
                            EditorGUILayout.LabelField($"{renderer.name}:",
                                $"Visible: {visibleSplats:N0}/{totalSplats:N0} ({cullRatio:F1}%), Nodes: {renderer.octree.nodeCount:N0}, Leaves: {leafNodes:N0}");
                        }
                    }
                }
            }

            EditorGUILayout.PropertyField(m_PropRenderMode);
            if (!m_PropRenderMode.hasMultipleDifferentValues && m_PropRenderMode.intValue == (int)RenderMode.DebugPoints)
                EditorGUILayout.PropertyField(m_PropPointSize);
            EditorGUILayout.PropertyField(m_PropSHOnly);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
