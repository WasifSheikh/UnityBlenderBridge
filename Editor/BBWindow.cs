using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Tools > Blender Bridge. Projects can add their own menu entry that calls <see cref="Open"/>.</summary>
    public class BBWindow : EditorWindow
    {
        BBPlan plan;
        Vector2 scroll;
        bool showRoots, showSettings;
        string message;
        MessageType messageType;

        [MenuItem("Tools/Blender Bridge")]
        public static void Open() => GetWindow<BBWindow>("Blender Bridge").Show();

        internal static void ShowPlan(BBPlan p)
        {
            var w = GetWindow<BBWindow>("Blender Bridge");
            w.plan = p;
            w.message = p.error;
            w.messageType = MessageType.Warning;
            w.Show();
            w.Repaint();
        }

        internal static void Applied(string text)
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<BBWindow>()) { w.plan = null; w.message = text; w.messageType = MessageType.Info; w.Repaint(); }
            if (SceneView.lastActiveSceneView) SceneView.lastActiveSceneView.ShowNotification(new GUIContent("Blender: " + text), 5);
        }

        void OnFocus() => Repaint();

        void OnGUI()
        {
            var scene = SceneManager.GetActiveScene();
            EditorGUILayout.LabelField("Scene", string.IsNullOrEmpty(scene.path) ? "(unsaved)" : scene.path, EditorStyles.boldLabel);
            if (string.IsNullOrEmpty(scene.path)) { EditorGUILayout.HelpBox("Save the scene first.", MessageType.Info); return; }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, messageType);
            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawSettings();
            DrawRoots(scene);

            EditorGUILayout.Space(8);
            string sceneName = scene.name;
            bool hasBlend = File.Exists(BBPaths.Blend(sceneName)) && File.Exists(BBPaths.SessionJson(sceneName));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Open in Blender", "Build a Blender file from this scene (or continue the existing one) with the project's asset library"), GUILayout.Height(32)))
                    OpenInBlender(scene, hasBlend);
                using (new EditorGUI.DisabledScope(!hasBlend))
                    if (GUILayout.Button(new GUIContent("Fresh", "Throw away the current Blender file and build a new one from the scene as it is now"), GUILayout.Width(60), GUILayout.Height(32)))
                        OpenInBlender(scene, false, true);
            }
            var applied = BBUtil.Read<BBApplied>(BBPaths.AppliedJson(sceneName));
            if (hasBlend)
                EditorGUILayout.LabelField(applied != null ? $"Last sync from Blender: {applied.applied?.Replace('T', ' ')} ({applied.summary})" : "Blender file ready; no sync from it yet.", EditorStyles.miniLabel);

            EditorGUILayout.Space(8);
            if (BBApply.HasResult(sceneName, out _))
            {
                if (plan == null || plan.scene != sceneName) plan = BBApply.BuildPlan(sceneName);
                DrawPlan(scene);
            }
            else if (plan != null && plan.scene == sceneName) plan = null;
            EditorGUILayout.EndScrollView();
        }

        void DrawSettings()
        {
            showSettings = EditorGUILayout.Foldout(showSettings, "Settings", true);
            if (!showSettings) return;
            using (new EditorGUI.IndentLevelScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string exe = EditorGUILayout.TextField("Blender (this PC)", BBSettings.BlenderExe);
                    if (exe != BBSettings.BlenderExe) BBSettings.BlenderExe = exe;
                    if (GUILayout.Button("…", GUILayout.Width(24)))
                    {
                        string p = EditorUtility.OpenFilePanel("blender.exe", Path.GetDirectoryName(BBSettings.BlenderExe), "exe");
                        if (!string.IsNullOrEmpty(p)) BBSettings.BlenderExe = p;
                    }
                }
                var s = BBSettings.instance;
                bool auto = EditorGUILayout.ToggleLeft(new GUIContent("Apply Blender syncs automatically when nothing conflicts",
                    "Otherwise every sync waits here for you to press Apply"), s.autoApply);
                if (auto != s.autoApply) { s.autoApply = auto; s.SaveNow(); }
                EditorGUILayout.LabelField("Asset library folders (prefabs you can drag in from Blender's Asset Browser)", EditorStyles.miniLabel);
                for (int i = 0; i < s.libraryFolders.Count; i++)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string f = EditorGUILayout.TextField(s.libraryFolders[i]);
                        if (f != s.libraryFolders[i]) { s.libraryFolders[i] = f; s.SaveNow(); }
                        if (GUILayout.Button("-", GUILayout.Width(24))) { s.libraryFolders.RemoveAt(i); s.SaveNow(); GUIUtility.ExitGUI(); }
                    }
                if (GUILayout.Button("Add folder", GUILayout.Width(90))) { s.libraryFolders.Add("Assets/"); s.SaveNow(); }
                string root = EditorGUILayout.TextField(new GUIContent("Asset root", "Where assets modelled in Blender are written. {scene} = scene name"), s.assetRoot);
                if (root != s.assetRoot) { s.assetRoot = root; s.SaveNow(); }
            }
        }

        void DrawRoots(Scene scene)
        {
            var rootObjs = scene.GetRootGameObjects();
            var ids = EditableRoots(scene, out bool auto);
            showRoots = EditorGUILayout.Foldout(showRoots, $"Editable in Blender: {ids.Count} of {rootObjs.Length} root objects{(auto ? " (auto-picked)" : "")}", true);
            if (!showRoots) return;
            EditorGUILayout.LabelField("Everything else goes to Blender as locked context.", EditorStyles.miniLabel);
            string guid = AssetDatabase.AssetPathToGUID(scene.path);
            foreach (var r in rootObjs)
            {
                string id = BBUtil.Goid(r);
                bool on = ids.Contains(id);
                bool now = EditorGUILayout.ToggleLeft(r.name, on);
                if (now == on) continue;
                var entry = BBSettings.instance.For(guid, true);
                if (auto) entry.rootIds = ids.ToList();
                if (now) entry.rootIds.Add(id); else entry.rootIds.Remove(id);
                BBSettings.instance.SaveNow();
            }
        }

        void DrawPlan(Scene scene)
        {
            EditorGUILayout.LabelField("Changes from Blender", EditorStyles.boldLabel);
            if (plan.error != null) { EditorGUILayout.HelpBox(plan.error, MessageType.Warning); }
            else
            {
                EditorGUILayout.LabelField(plan.Summary(), EditorStyles.wordWrappedMiniLabel);
                foreach (var i in plan.items)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!i.choice))
                            i.apply = EditorGUILayout.Toggle(i.apply || (!i.choice && i.kind != BBKind.Warning), GUILayout.Width(16));
                        string icon = i.kind == BBKind.Conflict ? "console.warnicon.sml" : i.kind == BBKind.Warning ? "console.infoicon.sml" : "";
                        var gc = icon != "" ? new GUIContent(" " + i.text, EditorGUIUtility.IconContent(icon).image) : new GUIContent(i.text);
                        EditorGUILayout.LabelField(gc, EditorStyles.wordWrappedLabel);
                        if (i.target && GUILayout.Button("Select", GUILayout.Width(50))) { Selection.activeGameObject = i.target; EditorGUIUtility.PingObject(i.target); }
                    }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(plan.error != null))
                    if (GUILayout.Button("Apply", GUILayout.Height(26)))
                    {
                        if (BBApply.Apply(plan, true)) Applied(plan.Summary());
                        else { message = "Not applied: " + plan.error; messageType = MessageType.Error; plan = null; }
                        GUIUtility.ExitGUI();
                    }
                if (GUILayout.Button("Refresh", GUILayout.Width(70), GUILayout.Height(26))) plan = BBApply.BuildPlan(scene.name);
                if (GUILayout.Button(new GUIContent("Discard", "Ignore this sync from Blender"), GUILayout.Width(70), GUILayout.Height(26)))
                {
                    string rj = BBPaths.ResultJson(scene.name);
                    if (File.Exists(rj)) File.Move(rj, rj.Replace("result.json", "result.discarded.json"));
                    plan = null;
                }
            }
        }

        // ================================================================ actions
        internal static HashSet<string> EditableRoots(Scene scene, out bool auto)
        {
            var entry = BBSettings.instance.For(AssetDatabase.AssetPathToGUID(scene.path), false);
            var roots = scene.GetRootGameObjects();
            var present = new HashSet<string>(roots.Select(BBUtil.Goid));
            auto = entry == null;
            if (!auto) return new HashSet<string>(entry.rootIds.Where(present.Contains));
            return new HashSet<string>(roots.Where(g => g.GetComponentInChildren<Renderer>(true) && !g.GetComponentInChildren<Camera>(true)
                                                        && !g.GetComponentInChildren<Canvas>(true) && !g.CompareTag("Player")).Select(BBUtil.Goid));
        }

        void OpenInBlender(Scene scene, bool hasBlend, bool forceFresh = false)
        {
            string sn = scene.name;
            bool fresh = !hasBlend || forceFresh;
            if (hasBlend && !forceFresh)
            {
                int c = EditorUtility.DisplayDialogComplex("Open in Blender", $"A Blender file for {sn} already exists.",
                                                          "Continue it", "Cancel", "Start fresh from Unity");
                if (c == 1) return;
                fresh = c == 2;
            }
            if (fresh && BBApply.HasResult(sn, out _) &&
                !EditorUtility.DisplayDialog("Open in Blender", "Blender sent changes that are not applied yet. Starting fresh drops them.", "Start fresh", "Cancel"))
                return;
            if (!BBLauncher.Ready(out var err)) { message = err; messageType = MessageType.Error; return; }
            if (!fresh)
            {
                if (!BBLauncher.OpenSession(sn, true, out err)) { message = err; messageType = MessageType.Error; }
                else { message = "Blender is opening the existing file."; messageType = MessageType.Info; }
                return;
            }
            if (scene.isDirty && (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo() || scene.isDirty))
            { message = "Save the scene first: Blender gets the saved scene."; messageType = MessageType.Warning; return; }
            var roots = EditableRoots(scene, out _);
            if (roots.Count == 0) { message = "Pick at least one editable root object."; messageType = MessageType.Warning; showRoots = true; return; }

            var t0 = DateTime.Now;
            var x = BBExport.Run(scene, roots);
            x.Save();
            foreach (var p in new[] { BBPaths.ResultJson(sn), BBPaths.AppliedJson(sn) }) if (File.Exists(p)) File.Delete(p);
            foreach (var w in x.warnings.Take(20)) Debug.LogWarning("[Blender Bridge] " + w);
            if (!BBLauncher.BuildLibrary(out err)) { message = "Asset library: " + err; messageType = MessageType.Error; return; }
            if (!BBLauncher.OpenSession(sn, false, out err)) { message = err; messageType = MessageType.Error; return; }
            message = $"Blender is building {sn}.blend: {x.Summary} ({(DateTime.Now - t0).TotalSeconds:0}s on the Unity side).";
            messageType = MessageType.Info;
            Debug.Log("[Blender Bridge] " + message);
        }
    }

    /// <summary>Picks up "Sync to Unity" from Blender: applies it straight away when it is clean (setting), otherwise
    /// opens the window with the preview.</summary>
    [InitializeOnLoad]
    static class BBWatcher
    {
        static double next;
        static DateTime lastSeen;

        static BBWatcher() => EditorApplication.update += Tick;

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1.0;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || !BBApply.HasResult(scene.name, out var t) || t == lastSeen) return;
            lastSeen = t;
            var plan = BBApply.BuildPlan(scene.name);
            if (plan.error == null && BBSettings.instance.autoApply && !scene.isDirty && !plan.HasConflicts && plan.items.All(i => i.kind != BBKind.Warning))
            {
                if (BBApply.Apply(plan, false)) { BBWindow.Applied(plan.Summary()); return; }
            }
            BBWindow.ShowPlan(plan);
        }
    }
}
