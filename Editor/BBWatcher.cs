using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Picks up "Sync to Unity" from Blender: applies it straight away when it is clean (setting), otherwise opens
    /// the window with the preview. Also notices when a sync is undone (Ctrl+Z) and tells Blender its file is out of date.</summary>
    [InitializeOnLoad]
    static class BBWatcher
    {
        static double next;
        static DateTime lastSeen;
        static string scenePath, key;

        static BBWatcher()
        {
            EditorApplication.update += Tick;
            Undo.undoRedoEvent += OnUndoRedo;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1.0;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path)) return;
            if (scene.path != scenePath) { scenePath = scene.path; key = BBPaths.Key(scene); }
            if (!BBPlanner.HasResult(key, out var t) || t == lastSeen) return;
            lastSeen = t;
            var plan = BBPlanner.Build(key);
            bool clean = plan.error == null && !plan.HasConflicts && !plan.HasWarnings;
            if (clean && BBSettings.instance.autoApply && !scene.isDirty && PrefabStageUtility.GetCurrentPrefabStage() == null)
                if (BBApply.Apply(plan, false)) { BBWindow.Applied(plan.Summary()); return; }
            BBWindow.ShowPlan(plan);
        }

        /// <summary>Undoing a sync puts the scene back, but the snapshot and Blender's ids already moved on: the session is
        /// marked out of date (Blender refuses to sync, the window offers Start fresh). Redo clears the mark.</summary>
        static void OnUndoRedo(in UndoRedoInfo undo)
        {
            if (string.IsNullOrEmpty(undo.undoName) || !undo.undoName.StartsWith(BBApply.UndoPrefix)) return;
            string k = undo.undoName.Substring(BBApply.UndoPrefix.Length);
            var s = BBUtil.Read<BBSession>(BBPaths.SessionJson(k));
            if (s == null) return;
            if (undo.isRedo) BBApply.WriteStatus(k, s.sessionId, BBStatus.Ok, "Sync redone in Unity");
            else BBApply.WriteStatus(k, s.sessionId, BBStatus.Undone, "The last sync was undone in Unity. Open the scene from Unity again (Start fresh).");
            BBWindow.RepaintAll();
        }
    }
}
