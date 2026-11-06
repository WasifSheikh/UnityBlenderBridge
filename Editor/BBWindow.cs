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
        enum Tab { Sync, Objects, Settings }
        enum Filter { All, Conflicts, Warnings }

        // ---------------------------------------------------------------- state
        Tab tab;
        Filter filter;
        BBPlan plan;
        Vector2 scroll;
        string message, rootSearch = "";
        MessageType messageType;
        readonly HashSet<string> closedGroups = new HashSet<string>();

        /// <summary>What the window shows about the active scene, refreshed when the hierarchy or the session files
        /// change (not on every repaint: GlobalObjectIds and file reads are too slow for that).</summary>
        class SceneInfo
        {
            public string path, key, name, guid;
            public List<(GameObject go, string id)> roots = new List<(GameObject, string)>();
            public HashSet<string> editable = new HashSet<string>();
            public bool autoPicked, hasBlend, hasResult;
            public DateTime resultTime, filesTime;
            public BBApplied applied;
            public BBStatus status;
        }
        SceneInfo info;
        bool hierarchyDirty = true;
        double nextPoll;

        [MenuItem("Tools/Blender Bridge")]
        public static void Open()
        {
            var w = GetWindow<BBWindow>();
            w.titleContent = new GUIContent("Blender Bridge", EditorGUIUtility.IconContent("SceneAsset Icon").image);
            w.minSize = new Vector2(360, 300);
            w.Show();
        }

        internal static void ShowPlan(BBPlan p)
        {
            Open();
            var w = GetWindow<BBWindow>();
            w.plan = p;
            w.tab = Tab.Sync;
            w.Message(p.error, MessageType.Warning);
            w.Repaint();
        }

        internal static void Applied(string text)
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<BBWindow>()) { w.plan = null; w.Message("Applied: " + text, MessageType.Info); w.Refresh(); }
            if (SceneView.lastActiveSceneView) SceneView.lastActiveSceneView.ShowNotification(new GUIContent("Blender: " + text), 4);
        }

        internal static void RepaintAll()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<BBWindow>()) w.Refresh();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Blender Bridge", EditorGUIUtility.IconContent("SceneAsset Icon").image);
            EditorApplication.hierarchyChanged += OnHierarchy;
            EditorSceneManager.activeSceneChangedInEditMode += OnSceneChanged;
        }

        void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchy;
            EditorSceneManager.activeSceneChangedInEditMode -= OnSceneChanged;
        }

        void OnHierarchy() { hierarchyDirty = true; Repaint(); }
        void OnSceneChanged(Scene a, Scene b) { plan = null; message = null; info = null; Repaint(); }
        void OnFocus() => Refresh();
        void Refresh() { hierarchyDirty = true; if (info != null) info.filesTime = default; Repaint(); }
        void Message(string text, MessageType type) { message = text; messageType = type; }

        void OnInspectorUpdate()
        {
            if (EditorApplication.timeSinceStartup < nextPoll || info == null) return;
            nextPoll = EditorApplication.timeSinceStartup + 1.0;
            if (FilesTime(info.key) != info.filesTime) Repaint();
        }

        static DateTime FilesTime(string key)
        {
            DateTime t = default;
            foreach (var p in new[] { BBPaths.ResultJson(key), BBPaths.AppliedJson(key), BBPaths.StatusJson(key), BBPaths.Blend(key) })
                if (File.Exists(p)) { var w = File.GetLastWriteTimeUtc(p); if (w > t) t = w; }
            return t;
        }

        void UpdateInfo(Scene scene)
        {
            if (info == null || info.path != scene.path) { info = new SceneInfo { path = scene.path }; hierarchyDirty = true; }
            if (info.key == null)
            {
                info.guid = AssetDatabase.AssetPathToGUID(scene.path);
                info.name = scene.name;
                info.key = BBPaths.Key(scene.name, info.guid);
            }
            if (hierarchyDirty)
            {
                hierarchyDirty = false;
                var roots = scene.GetRootGameObjects();
                var ids = BBUtil.Goids(roots);
                info.roots = roots.Select((g, i) => (g, ids[i])).ToList();
                info.editable = EditableRoots(scene, roots, ids, out info.autoPicked);
            }
            var ft = FilesTime(info.key);
            if (ft != info.filesTime)
            {
                info.filesTime = ft;
                info.hasBlend = File.Exists(BBPaths.Blend(info.key)) && File.Exists(BBPaths.SessionJson(info.key));
                info.hasResult = BBPlanner.HasResult(info.key, out info.resultTime);
                info.applied = BBUtil.Read<BBApplied>(BBPaths.AppliedJson(info.key));
                info.status = BBUtil.Read<BBStatus>(BBPaths.StatusJson(info.key));
                if (!info.hasResult) plan = null;
                else if (plan == null || plan.key != info.key || plan.result?.written != BBUtil.Read<BBResult>(BBPaths.ResultJson(info.key))?.written)
                    plan = BBPlanner.Build(info.key);
            }
        }

        // ================================================================ layout
        void OnGUI()
        {
            BBStyles.Init();
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                Header(null, "No scene", BBStyles.Grey);
                EditorGUILayout.HelpBox("Save the scene first: Blender Bridge works on saved scenes.", MessageType.Info);
                return;
            }
            UpdateInfo(scene);
            var (state, color) = State();
            Header(scene, state, color);
            if (!string.IsNullOrEmpty(message))
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(message, messageType);
                    if (GUILayout.Button(new GUIContent("×", "Dismiss"), EditorStyles.miniButton, GUILayout.Width(20))) message = null;
                }

            tab = (Tab)GUILayout.Toolbar((int)tab, new[]
            {
                new GUIContent(info.hasResult ? $"Sync  ({plan?.items.Count(i => i.kind != BBKind.Warning) ?? 0})" : "Sync"),
                new GUIContent($"Editable objects  ({info.editable.Count}/{info.roots.Count})"),
                new GUIContent("Settings"),
            }, GUILayout.Height(24));
            EditorGUILayout.Space(4);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            switch (tab)
            {
                case Tab.Sync: DrawSync(scene); break;
                case Tab.Objects: DrawRoots(scene); break;
                case Tab.Settings: DrawSettings(); break;
            }
            EditorGUILayout.EndScrollView();
        }

        (string, Color) State()
        {
            if (info.status?.state == BBStatus.Undone) return ("Out of sync", BBStyles.Red);
            if (info.hasResult) return (plan?.HasConflicts == true ? "Conflicts" : "Changes waiting", BBStyles.Orange);
            if (BBLauncher.IsOpen(info.key)) return ("Blender open", BBStyles.Green);
            if (info.hasBlend) return ("Ready", BBStyles.Blue);
            return ("Not opened", BBStyles.Grey);
        }

        void Header(Scene? scene, string state, Color color)
        {
            using (new EditorGUILayout.HorizontalScope(BBStyles.HeaderBox))
            {
                GUILayout.Label(EditorGUIUtility.IconContent("SceneAsset Icon"), GUILayout.Width(20), GUILayout.Height(20));
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(scene?.name ?? "Blender Bridge", BBStyles.Title);
                    if (scene != null) GUILayout.Label(scene.Value.path, EditorStyles.miniLabel);
                }
                GUILayout.FlexibleSpace();
                BBStyles.Badge(state, color);
            }
        }

        // ================================================================ sync tab
        void DrawSync(Scene scene)
        {
            using (new EditorGUILayout.VerticalScope(BBStyles.Card))
            {
                GUILayout.Label("Blender", BBStyles.Section);
                if (info.status?.state == BBStatus.Undone)
                    EditorGUILayout.HelpBox("You undid the last Blender sync in Unity, so the Blender file no longer matches the scene. Start fresh to rebuild it from the scene.", MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool fresh = !info.hasBlend || info.status?.state == BBStatus.Undone;
                    using (new BBStyles.Tint(BBStyles.Accent))
                        if (GUILayout.Button(new GUIContent(fresh ? "  Open in Blender" : "  Continue in Blender", EditorGUIUtility.IconContent("SceneAsset Icon").image,
                                fresh ? "Build a Blender file from this scene and the project's asset library" : "Open the existing Blender file of this scene"), GUILayout.Height(30)))
                            OpenInBlender(scene, fresh ? OpenMode.Fresh : OpenMode.Continue);
                    if (GUILayout.Button(new GUIContent("▾", "More"), GUILayout.Width(26), GUILayout.Height(30))) OpenMenu(scene);
                }
                string last = info.applied != null ? $"Last sync {info.applied.applied?.Replace('T', ' ')}: {info.applied.summary}"
                            : info.hasBlend ? "Blender file ready; nothing synced from it yet." : "No Blender file for this scene yet.";
                GUILayout.Label(last, BBStyles.Hint);
            }

            if (!info.hasResult)
            {
                if (info.hasBlend)
                    using (new EditorGUILayout.VerticalScope(BBStyles.Card))
                        GUILayout.Label("Waiting for Blender. In Blender: sidebar (N) > Unity > Sync to Unity. " +
                                        (BBSettings.instance.autoApply ? "Clean syncs are applied automatically." : "You'll review each sync here."), BBStyles.Hint);
                return;
            }
            if (plan == null) plan = BBPlanner.Build(info.key);
            DrawPlan();
        }

        enum OpenMode { Continue, Fresh }

        void OpenMenu(Scene scene)
        {
            var m = new GenericMenu();
            if (info.hasBlend) m.AddItem(new GUIContent("Continue the existing Blender file"), false, () => OpenInBlender(scene, OpenMode.Continue));
            else m.AddDisabledItem(new GUIContent("Continue the existing Blender file"));
            m.AddItem(new GUIContent("Start fresh from Unity"), false, () => OpenInBlender(scene, OpenMode.Fresh));
            m.AddSeparator("");
            m.AddItem(new GUIContent("Show session folder"), false, () => Reveal(BBPaths.SessionDir(info.key)));
            m.AddItem(new GUIContent("Show scene backups"), false, () => Reveal(Path.Combine(BBPaths.Backups, info.key)));
            m.AddItem(new GUIContent("Show library build log"), false, () => Reveal(BBPaths.LibraryLog));
            m.ShowAsContext();
        }

        static void Reveal(string path)
        {
            if (File.Exists(path) || Directory.Exists(path)) EditorUtility.RevealInFinder(path);
            else EditorUtility.DisplayDialog("Blender Bridge", "Nothing there yet:\n" + path, "OK");
        }

        static readonly (string title, Func<BBItem, bool> match)[] Groups =
        {
            ("Conflicts", i => i.kind == BBKind.Conflict),
            ("Warnings", i => i.kind == BBKind.Warning),
            ("Assets", i => i.kind == BBKind.Asset),
            ("Added", i => i.kind == BBKind.Add),
            ("Duplicated", i => i.kind == BBKind.Duplicate),
            ("Geometry and moves", i => i.kind == BBKind.Move),
            ("Re-parented", i => i.kind == BBKind.Reparent),
            ("Renamed", i => i.kind == BBKind.Rename),
            ("Deleted", i => i.kind == BBKind.Delete),
        };

        void DrawPlan()
        {
            using (new EditorGUILayout.VerticalScope(BBStyles.Card))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Changes from Blender", BBStyles.Section);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(info.resultTime.ToLocalTime().ToString("HH:mm:ss"), EditorStyles.miniLabel);
                }
                if (plan.error != null) { EditorGUILayout.HelpBox(plan.error, MessageType.Warning); PlanButtons(); return; }
                GUILayout.Label(plan.Summary(), BBStyles.Hint);
                if (plan.HasConflicts || plan.HasWarnings)
                    filter = (Filter)GUILayout.Toolbar((int)filter, new[] { "All", $"Conflicts ({plan.Count(BBKind.Conflict)})", $"Warnings ({plan.Count(BBKind.Warning)})" }, EditorStyles.miniButton);
                if (plan.HasConflicts)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("Conflicts:", EditorStyles.miniLabel, GUILayout.Width(60));
                        if (GUILayout.Button("Take Blender's for all", EditorStyles.miniButtonLeft)) SetConflicts(true);
                        if (GUILayout.Button("Keep Unity's for all", EditorStyles.miniButtonRight)) SetConflicts(false);
                    }
                EditorGUILayout.Space(2);
                foreach (var (title, match) in Groups)
                {
                    if (filter == Filter.Conflicts && title != "Conflicts" || filter == Filter.Warnings && title != "Warnings") continue;
                    var items = plan.items.Where(match).ToList();
                    if (items.Count == 0) continue;
                    bool openGroup = !closedGroups.Contains(title);
                    bool now = EditorGUILayout.Foldout(openGroup, $"{title}  ({items.Count})", true, BBStyles.GroupFoldout);
                    if (now != openGroup) { if (now) closedGroups.Remove(title); else closedGroups.Add(title); }
                    if (!now) continue;
                    foreach (var i in items.Take(300)) Row(i);
                    if (items.Count > 300) GUILayout.Label($"… and {items.Count - 300} more", BBStyles.Hint);
                }
                EditorGUILayout.Space(4);
                PlanButtons();
            }
        }

        void SetConflicts(bool apply) { foreach (var i in plan.items.Where(i => i.kind == BBKind.Conflict)) i.apply = apply; }

        void Row(BBItem i)
        {
            var r = EditorGUILayout.BeginHorizontal(BBStyles.Row);
            if (i.kind == BBKind.Conflict) EditorGUI.DrawRect(r, BBStyles.ConflictBg);
            else if (i.kind == BBKind.Warning) EditorGUI.DrawRect(r, BBStyles.WarningBg);
            if (i.kind == BBKind.Warning) GUILayout.Label(EditorGUIUtility.IconContent("console.infoicon.sml"), GUILayout.Width(18));
            else using (new EditorGUI.DisabledScope(!i.choice))
                i.apply = EditorGUILayout.Toggle(i.apply || !i.choice, GUILayout.Width(18));
            if (i.kind == BBKind.Conflict) GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18));
            GUILayout.Label(i.text, BBStyles.RowText);
            if (i.target && GUILayout.Button(new GUIContent("Select", "Select and ping it in the Hierarchy"), EditorStyles.miniButton, GUILayout.Width(48)))
            { Selection.activeGameObject = i.target; EditorGUIUtility.PingObject(i.target); }
            EditorGUILayout.EndHorizontal();
        }

        void PlanButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                int n = plan.items.Count(i => i.kind != BBKind.Warning && i.apply);
                using (new EditorGUI.DisabledScope(plan.error != null))
                using (new BBStyles.Tint(BBStyles.Accent))
                    if (GUILayout.Button(plan.error != null ? "Apply" : $"Apply {n} change{(n == 1 ? "" : "s")}", GUILayout.Height(28)))
                    {
                        if (BBApply.Apply(plan, true)) Applied(plan.Summary());
                        else { Message("Not applied: " + plan.error, MessageType.Error); plan = null; }
                        GUIUtility.ExitGUI();
                    }
                if (GUILayout.Button(new GUIContent("Refresh", "Compare again with the scene as it is now"), GUILayout.Width(70), GUILayout.Height(28)))
                    plan = BBPlanner.Build(info.key);
                if (GUILayout.Button(new GUIContent("Discard", "Ignore this sync from Blender (Blender keeps its changes and can send them again)"), GUILayout.Width(70), GUILayout.Height(28))
                    && EditorUtility.DisplayDialog("Discard this sync?", "Unity ignores these changes. Blender still has them and sends them again with its next sync.", "Discard", "Cancel"))
                {
                    string rj = BBPaths.ResultJson(info.key), to = rj.Replace("result.json", "result.discarded.json");
                    if (File.Exists(to)) File.Delete(to);
                    if (File.Exists(rj)) File.Move(rj, to);
                    plan = null;
                    Refresh();
                }
            }
        }

        // ================================================================ editable objects tab
        void DrawRoots(Scene scene)
        {
            using (new EditorGUILayout.VerticalScope(BBStyles.Card))
            {
                GUILayout.Label("Root objects Blender may edit", BBStyles.Section);
                GUILayout.Label("Ticked roots (and everything under them) can be moved, copied, deleted and modelled in Blender. The rest comes along as locked context. " +
                                "Saved for the whole team in ProjectSettings/BlenderBridge.asset." + (info.autoPicked ? " Not chosen yet: these are picked automatically." : ""), BBStyles.Hint);
                using (new EditorGUILayout.HorizontalScope())
                {
                    rootSearch = EditorGUILayout.TextField(rootSearch, EditorStyles.toolbarSearchField);
                    if (GUILayout.Button("All", EditorStyles.miniButtonLeft, GUILayout.Width(40))) SetRoots(scene, info.roots.Select(r => r.id));
                    if (GUILayout.Button("None", EditorStyles.miniButtonMid, GUILayout.Width(44))) SetRoots(scene, Enumerable.Empty<string>());
                    if (GUILayout.Button(new GUIContent("Auto", "Objects with renderers, without cameras, UI or the Player tag"), EditorStyles.miniButtonRight, GUILayout.Width(44)))
                    {
                        var entry = BBSettings.instance.For(info.guid, false);
                        if (entry != null) { BBSettings.instance.scenes.Remove(entry); BBSettings.instance.SaveNow(); }
                        Refresh();
                    }
                }
                EditorGUILayout.Space(2);
                foreach (var (go, id) in info.roots)
                {
                    if (!go || (rootSearch.Length > 0 && go.name.IndexOf(rootSearch, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    using (new EditorGUILayout.HorizontalScope(BBStyles.Row))
                    {
                        bool on = info.editable.Contains(id);
                        bool now = EditorGUILayout.Toggle(on, GUILayout.Width(18));
                        var icon = EditorGUIUtility.IconContent(PrefabUtility.IsAnyPrefabInstanceRoot(go) ? "Prefab Icon" : "GameObject Icon").image;
                        if (GUILayout.Button(new GUIContent(" " + go.name, icon), BBStyles.RowLink)) EditorGUIUtility.PingObject(go);
                        GUILayout.FlexibleSpace();
                        int kids = go.transform.childCount;
                        if (kids > 0) GUILayout.Label(kids + (kids == 1 ? " child" : " children"), EditorStyles.miniLabel);
                        if (now != on)
                        {
                            var ids = new HashSet<string>(info.editable);
                            if (now) ids.Add(id); else ids.Remove(id);
                            SetRoots(scene, ids);
                        }
                    }
                }
            }
        }

        void SetRoots(Scene scene, IEnumerable<string> ids)
        {
            var entry = BBSettings.instance.For(info.guid, true);
            entry.rootIds = ids.ToList();
            BBSettings.instance.SaveNow();
            Refresh();
        }

        // ================================================================ settings tab
        void DrawSettings()
        {
            var s = BBSettings.instance;
            using (new EditorGUILayout.VerticalScope(BBStyles.Card))
            {
                GUILayout.Label("This PC", BBStyles.Section);
                using (new EditorGUILayout.HorizontalScope())
                {
                    string exe = EditorGUILayout.DelayedTextField("Blender", BBSettings.BlenderExe);
                    if (exe != BBSettings.BlenderExe) BBSettings.BlenderExe = exe;
                    if (GUILayout.Button("…", GUILayout.Width(26)))
                    {
                        string p = EditorUtility.OpenFilePanel("Blender executable", Path.GetDirectoryName(BBSettings.BlenderExe) ?? "", Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                        if (!string.IsNullOrEmpty(p)) BBSettings.BlenderExe = p;
                    }
                }
                GUILayout.Label(BBLauncher.VersionLabel(BBSettings.BlenderExe) + (BBSettings.BlenderExeIsDetected && File.Exists(BBSettings.BlenderExe) ? " (found automatically)" : ""), BBStyles.Hint);
            }

            using (new EditorGUILayout.VerticalScope(BBStyles.Card))
            {
                GUILayout.Label("Project (shared with the team)", BBStyles.Section);
                bool auto = EditorGUILayout.ToggleLeft(new GUIContent("Apply clean syncs automatically", "Syncs without conflicts or warnings are applied as soon as Blender sends them. Otherwise every sync waits here for review."), s.autoApply);
                if (auto != s.autoApply) { s.autoApply = auto; s.SaveNow(); }

                EditorGUILayout.Space(4);
                string root = EditorGUILayout.DelayedTextField(new GUIContent("Asset root", "Where assets modelled in Blender are written. {scene} = scene name"), s.assetRoot);
                if (root != s.assetRoot) { s.assetRoot = root; s.SaveNow(); }
                GUILayout.Label("This scene: " + BBPaths.AssetRoot(info?.name ?? "{scene}") + "/Meshes, Materials, Prefabs…", BBStyles.Hint);

                EditorGUILayout.Space(6);
                FolderList("Asset library folders", "Prefabs under these folders appear in Blender's Asset Browser.", s.libraryFolders, true);
                EditorGUILayout.Space(4);
                FolderList("Leave out paths containing", "e.g. /_Archive/ or /Deprecated/", s.libraryExcludes, false);
            }

            using (new EditorGUILayout.VerticalScope(BBStyles.Card))
            {
                GUILayout.Label("Working files", BBStyles.Section);
                GUILayout.Label("BlenderSessions/ is throwaway (git-ignored): converted meshes, the library .blend and each scene's Blender file.", BBStyles.Hint);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Show folder")) Reveal(BBPaths.Root);
                    if (GUILayout.Button(new GUIContent("Clear caches", "Delete converted meshes, textures and the library .blend. They are rebuilt on the next Open in Blender."))
                        && EditorUtility.DisplayDialog("Clear Blender Bridge caches?", "Converted meshes, textures and the library .blend are deleted and rebuilt on the next Open in Blender (slower once). Scene Blender files are kept.", "Clear", "Cancel"))
                    {
                        foreach (var d in new[] { BBPaths.Root + "/_cache", BBPaths.LibraryDir })
                            if (Directory.Exists(d)) Directory.Delete(d, true);
                        Message("Caches cleared.", MessageType.Info);
                    }
                }
            }
        }

        static void FolderList(string title, string hint, List<string> list, bool folders)
        {
            var s = BBSettings.instance;
            GUILayout.Label(title, EditorStyles.boldLabel);
            GUILayout.Label(hint, BBStyles.Hint);
            for (int i = 0; i < list.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    string f = EditorGUILayout.DelayedTextField(list[i]);
                    if (f != list[i]) { list[i] = f; s.SaveNow(); }
                    if (folders && !AssetDatabase.IsValidFolder(f.Trim().TrimEnd('/')))
                        GUILayout.Label(new GUIContent(EditorGUIUtility.IconContent("console.warnicon.sml").image, "Not a folder in this project"), GUILayout.Width(18));
                    if (GUILayout.Button(new GUIContent("−", "Remove"), GUILayout.Width(24))) { list.RemoveAt(i); s.SaveNow(); GUIUtility.ExitGUI(); }
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (folders)
                {
                    var sel = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath).Where(AssetDatabase.IsValidFolder).ToList();
                    using (new EditorGUI.DisabledScope(sel.Count == 0))
                        if (GUILayout.Button(new GUIContent("Add selected folder", "Folders selected in the Project window"), EditorStyles.miniButton, GUILayout.Width(130)))
                        { foreach (var p in sel) if (!list.Contains(p)) list.Add(p); s.SaveNow(); }
                }
                if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(50))) { list.Add(folders ? "Assets/" : "/_Archive/"); s.SaveNow(); }
                GUILayout.FlexibleSpace();
            }
        }

        // ================================================================ actions
        internal static HashSet<string> EditableRoots(Scene scene, out bool auto)
        {
            var roots = scene.GetRootGameObjects();
            return EditableRoots(scene, roots, BBUtil.Goids(roots), out auto);
        }

        static HashSet<string> EditableRoots(Scene scene, GameObject[] roots, string[] ids, out bool auto)
        {
            var entry = BBSettings.instance.For(AssetDatabase.AssetPathToGUID(scene.path), false);
            auto = entry == null;
            if (!auto) { var present = new HashSet<string>(ids); return new HashSet<string>(entry.rootIds.Where(present.Contains)); }
            var set = new HashSet<string>();
            for (int i = 0; i < roots.Length; i++)
            {
                var g = roots[i];
                if (g.GetComponentInChildren<Renderer>(true) && !g.GetComponentInChildren<Camera>(true) && !g.GetComponentInChildren<Canvas>(true) && !g.CompareTag("Player"))
                    set.Add(ids[i]);
            }
            return set;
        }

        void OpenInBlender(Scene scene, OpenMode mode)
        {
            string key = BBPaths.Key(scene);
            bool hasBlend = File.Exists(BBPaths.Blend(key)) && File.Exists(BBPaths.SessionJson(key));
            bool fresh = mode == OpenMode.Fresh || !hasBlend;
            if (BBLauncher.IsOpen(key) &&
                !EditorUtility.DisplayDialog("Open in Blender", "Blender already has this scene open (started from here). Open another Blender window anyway?" +
                                             (fresh ? "\n\nStarting fresh makes the open one out of date: its syncs will be refused." : ""), "Open anyway", "Cancel"))
                return;
            if (fresh && BBPlanner.HasResult(key, out _) &&
                !EditorUtility.DisplayDialog("Open in Blender", "Blender sent changes that are not applied yet. Starting fresh drops them.", "Start fresh", "Cancel"))
                return;
            if (!BBLauncher.Ready(out var err)) { Message(err, MessageType.Error); tab = Tab.Settings; return; }
            if (!fresh)
            {
                if (!BBLauncher.OpenSession(key, true, out err)) Message(err, MessageType.Error);
                else Message("Blender is opening the existing file.", MessageType.Info);
                return;
            }
            if (scene.isDirty && (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo() || scene.isDirty))
            { Message("Save the scene first: Blender gets the saved scene.", MessageType.Warning); return; }
            var roots = EditableRoots(scene, out _);
            if (roots.Count == 0) { Message("Pick at least one editable root object.", MessageType.Warning); tab = Tab.Objects; return; }

            var t0 = DateTime.Now;
            var x = BBExport.Run(scene, roots);
            x.Save();
            foreach (var p in new[] { BBPaths.ResultJson(key), BBPaths.AppliedJson(key), BBPaths.ImportProgress(key) }) if (File.Exists(p)) File.Delete(p);
            foreach (var w in x.warnings.Take(20)) Debug.LogWarning("[Blender Bridge] " + w);
            if (x.warnings.Count > 20) Debug.LogWarning($"[Blender Bridge] … and {x.warnings.Count - 20} more warnings");
            if (!BBLauncher.BuildLibrary(out err)) { Message("Asset library: " + err, MessageType.Error); return; }
            if (!BBLauncher.OpenSession(key, false, out err)) { Message(err, MessageType.Error); return; }
            Message($"Blender is building the scene: {x.Summary} ({(DateTime.Now - t0).TotalSeconds:0.0}s on the Unity side).", MessageType.Info);
            Debug.Log("[Blender Bridge] " + message);
            Refresh();
        }
    }
}
