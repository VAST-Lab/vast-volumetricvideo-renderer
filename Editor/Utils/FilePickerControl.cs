// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor.Utils
{
    // path field + browse button + project-local recent path history
#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public class FilePickerControl
    {
        const int kRecentCount = 20;
        static readonly string kPreference = "nesnausk.GaussianSplatSource." + Hash128.Compute(Application.dataPath);
        static List<string> s_RecentPaths;
        static GUIContent[] s_RecentLabels;

        static void LoadHistory()
        {
            if (s_RecentPaths != null)
                return;
            s_RecentPaths = new List<string>();
            for (int i = 0; i < kRecentCount; ++i)
            {
                string path = EditorPrefs.GetString($"{kPreference}.{i}", "");
                if (!string.IsNullOrEmpty(path))
                    s_RecentPaths.Add(path);
            }
            UpdateLabels();
        }

        static void UpdateLabels()
        {
            s_RecentLabels = new GUIContent[s_RecentPaths.Count];
            for (int i = 0; i < s_RecentPaths.Count; ++i)
            {
                string path = s_RecentPaths[i];
                string file = GaussianSplatPaths.IsUrl(path) ? new Uri(path).AbsolutePath : path;
                // note: relative paths or URLs often have "/", which when displayed
                // as a menu, will cause it to have sub-menus. So just display the filename only.
                s_RecentLabels[i] = new GUIContent(Path.GetFileName(file), path);
            }
        }

        static void RememberPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            s_RecentPaths.Remove(path);
            s_RecentPaths.Insert(0, path);
            if (s_RecentPaths.Count > kRecentCount)
                s_RecentPaths.RemoveAt(kRecentCount);
            for (int i = 0; i < s_RecentPaths.Count; ++i)
                EditorPrefs.SetString($"{kPreference}.{i}", s_RecentPaths[i]);
            UpdateLabels();
        }

        public static string MakeRelativePath(string full)
        {
            if (string.IsNullOrWhiteSpace(full)) return "";
            full = Path.GetFullPath(full);
            string relStreaming = GaussianSplatPaths.ToForwardSlashes(Path.GetRelativePath(Application.streamingAssetsPath, full));
            if (!relStreaming.StartsWith("../", StringComparison.Ordinal) && relStreaming != ".." && !Path.IsPathRooted(relStreaming))
                return relStreaming;

            string relCurrent = GaussianSplatPaths.ToForwardSlashes(Path.GetRelativePath(Directory.GetCurrentDirectory(), full));
            if (!relCurrent.StartsWith("../", StringComparison.Ordinal) && relCurrent != ".." && !Path.IsPathRooted(relCurrent))
                return relCurrent;

            return GaussianSplatPaths.ToForwardSlashes(full);
        }

        static bool TrySourcePath(string file, out string source)
        {
            return TrySourcePath(file, new[] { "ply", "spz" }, out source);
        }

        static bool TrySourcePath(string file, string[] allowedExtensions, out string source)
        {
            source = null;
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                return false;
            string extension = Path.GetExtension(file).ToLowerInvariant();
            bool matched = false;
            if (allowedExtensions != null && allowedExtensions.Length > 0)
            {
                for (int i = 0; i < allowedExtensions.Length; i++)
                {
                    string ext = allowedExtensions[i];
                    if (!ext.StartsWith('.')) ext = "." + ext;
                    if (string.Equals(extension, ext, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }
            }
            else
            {
                matched = extension == ".ply" || extension == ".spz";
            }
            if (!matched)
                return false;

            source = MakeRelativePath(file);
            return true;
        }

        string Browse(string value, string[] allowedExtensions = null, string filterTitle = "Gaussian splats")
        {
            LoadHistory();
            string current = string.IsNullOrEmpty(value) && s_RecentPaths.Count > 0 ? s_RecentPaths[0] : value;
            string directory = Application.streamingAssetsPath;
            if (!string.IsNullOrEmpty(current) && !GaussianSplatPaths.IsUrl(current))
            {
                try
                {
                    string candidate = Path.GetDirectoryName(GaussianSplatReader.Resolve4DPath(directory, current));
                    if (Directory.Exists(candidate))
                        directory = candidate;
                }
                catch (ArgumentException) { } // browse from StreamingAssets if the current text is invalid
            }

            var exts = allowedExtensions ?? new[] { "ply", "spz" };
            string extList = string.Join(",", exts).Replace(".", "");
            string file = EditorUtility.OpenFilePanelWithFilters("Select splat file", directory, new[] { filterTitle, extList });
            if (string.IsNullOrEmpty(file))
                return null;
            if (TrySourcePath(file, exts, out string relative))
            {
                RememberPath(relative);
                return relative;
            }
            EditorUtility.DisplayDialog($"Choose a {filterTitle} file", $"Choose a valid file ({extList})", "OK");
            return null;
        }

        public string PathFieldGUI(Rect position, GUIContent label, string value)
        {
            return PathFieldGUI(position, label, value, new[] { "ply", "spz" }, "Gaussian splats");
        }

        public string PathFieldGUI(Rect position, GUIContent label, string value, string[] allowedExtensions, string filterTitle = "Gaussian splats")
        {
            LoadHistory();
            Rect field = EditorGUI.PrefixLabel(position, label);
            var dropdown = new Rect(field.xMax - 18, field.y, 18, field.height);
            var browse = new Rect(dropdown.x - 22, field.y, 22, field.height);
            var text = new Rect(field.x, field.y, browse.x - field.x, field.height);

            EditorGUI.BeginChangeCheck();
            string typed = EditorGUI.DelayedTextField(text, value ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                value = GaussianSplatPaths.IsUrl(typed) ? typed : GaussianSplatPaths.ToForwardSlashes(typed);
                try
                {
                    string full = GaussianSplatReader.ResolveFileWithExtensions(Application.streamingAssetsPath, value, allowedExtensions, "");
                    if (GaussianSplatPaths.IsUrl(full) || File.Exists(full))
                        RememberPath(value);
                }
                catch (ArgumentException) { } // inspector displays invalid/missing path messages
            }
            if (GUI.Button(browse, EditorGUIUtility.IconContent("Folder Icon", "|Choose a file"), EditorStyles.miniButton))
            {
                string selected = Browse(value, allowedExtensions, filterTitle);
                if (selected != null)
                {
                    value = selected;
                    GUI.changed = true;
                }
            }
            using (new EditorGUI.DisabledScope(s_RecentPaths.Count == 0))
            {
                int selected = EditorGUI.Popup(dropdown, -1, s_RecentLabels, EditorStyles.popup);
                if (selected >= 0)
                {
                    value = s_RecentPaths[selected];
                    RememberPath(value);
                    GUI.changed = true;
                }
            }

            Event evt = Event.current;
            if (field.Contains(evt.mousePosition) && GUI.enabled && evt.type is EventType.DragUpdated or EventType.DragPerform)
            {
                string relative = null;
                bool valid = DragAndDrop.paths.Length == 1 && TrySourcePath(DragAndDrop.paths[0], allowedExtensions, out relative);
                DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
                if (valid && evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    value = relative;
                    RememberPath(value);
                    GUI.changed = true;
                }
                evt.Use();
            }
            return value;
        }

        public string FolderFieldGUI(Rect position, GUIContent label, string value)
        {
            Rect field = EditorGUI.PrefixLabel(position, label);
            var browse = new Rect(field.xMax - 22, field.y, 22, field.height);
            var text = new Rect(field.x, field.y, browse.x - field.x - 2, field.height);

            EditorGUI.BeginChangeCheck();
            string typed = EditorGUI.DelayedTextField(text, value ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                value = GaussianSplatPaths.ToForwardSlashes(typed);
                GUI.changed = true;
            }

            if (GUI.Button(browse, EditorGUIUtility.IconContent("Folder Icon", "|Choose a Sequence Folder"), EditorStyles.miniButton))
            {
                string selected = EditorUtility.OpenFolderPanel("Select Sequence Folder (.spz / .ply)", Application.streamingAssetsPath, "");
                if (!string.IsNullOrEmpty(selected))
                {
                    value = MakeRelativePath(selected);
                    GUI.changed = true;
                }
            }

            Event evt = Event.current;
            if (field.Contains(evt.mousePosition) && GUI.enabled && evt.type is EventType.DragUpdated or EventType.DragPerform)
            {
                bool valid = DragAndDrop.paths.Length == 1 && Directory.Exists(DragAndDrop.paths[0]);
                DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
                if (valid && evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    value = MakeRelativePath(DragAndDrop.paths[0]);
                    GUI.changed = true;
                }
                evt.Use();
            }
            return value;
        }
    }
}
