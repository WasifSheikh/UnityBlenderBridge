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
    /// <summary>
    /// Applies a plan: backs the scene up, builds the assets (resuming if an earlier attempt stopped half way), applies
    /// the scene changes as one Undo step, saves, rewrites the snapshot and tells Blender the ids of what it created and
    /// of any object whose id changed (applied.json).
    /// </summary>
    public static class BBApply
    {
        public const string UndoPrefix = "Blender Bridge: sync ";

        public static bool Apply(BBPlan preview, bool interactive)
        {
            var scene = SceneManager.GetActiveScene();
            if (preview.session == null || scene.path != preview.session.scenePath) { preview.error = "the scene the changes are for is not open"; return false; }
            if (scene.isDirty)
            {
                if (!interactive) { preview.error = "the scene has unsaved changes"; return false; }
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo() || scene.isDirty) { preview.error = "save the scene first, so the backup holds everything"; return false; }
            }
            var plan = BBPlanner.Build(preview.key);
            if (plan.error != null) { preview.error = plan.error; return false; }
            if (plan.Fingerprint != preview.Fingerprint) { preview.error = "the scene or Blender's changes moved on since the preview; preview again"; return false; }
            var choices = preview.items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First().apply);
            foreach (var i in plan.items) if (i.choice && choices.TryGetValue(i.Key, out var a)) i.apply = a;

            plan.backupDir = Backup(plan);
            var ctx = new BBCtx { scene = scene, plan = plan, byId = new Dictionary<string, GameObject>(plan.live) };
            try
            {
                if (!ImportAssets(ctx, preview)) return false;

                Undo.IncrementCurrentGroup();
                int group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(UndoPrefix + plan.key);
                EditorUtility.DisplayProgressBar("Blender Bridge", "Applying scene changes", 0.6f);
                bool placed = false;
                foreach (var i in plan.items)
                {
                    if (i.phase == BBPhase.Assets || i.phase == BBPhase.Info || !i.apply || i.run == null) continue;
                    if (!placed && i.phase > BBPhase.Create) { BBSceneOps.PlaceCreated(ctx); placed = true; }
                    try { i.run(ctx); }
                    catch (Exception e) { ctx.log.Add($"{i.text}: {e.Message}"); Debug.LogException(e); }
                }
                if (!placed) BBSceneOps.PlaceCreated(ctx);
                Undo.CollapseUndoOperations(group);
            }
            finally { EditorUtility.ClearProgressBar(); }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);   // new objects get their file ids, so their ids are final now
            var remap = Remap(ctx);
            var applied = FinishSnapshot(ctx, remap);
            string summary = plan.Summary();
            applied.summary = summary;
            BBUtil.Write(BBPaths.AppliedJson(plan.key), BBUtil.Json(applied));
            WriteStatus(plan.key, plan.session.sessionId, BBStatus.Ok, "Applied " + summary);
            string rj = BBPaths.ResultJson(plan.key), done = Path.Combine(Path.GetDirectoryName(rj), "result.applied.json");
            if (File.Exists(done)) File.Delete(done);
            File.Move(rj, done);
            string progress = BBPaths.ImportProgress(plan.key);
            if (File.Exists(progress)) File.Delete(progress);
            Journal(plan, summary);
            foreach (var l in ctx.log) Debug.LogWarning("[Blender Bridge] " + l);
            Debug.Log($"[Blender Bridge] {plan.sceneName}: {summary}. Backup: {plan.backupDir}");
            preview.error = null;
            preview.backupDir = plan.backupDir;
            return true;
        }

        /// <summary>Assets first (asset files are not part of the scene Undo). Each finished asset is recorded, so if one
        /// fails, the next Apply reuses the finished ones instead of importing them again under new names.</summary>
        static bool ImportAssets(BBCtx ctx, BBPlan preview)
        {
            var plan = ctx.plan;
            var items = plan.items.Where(i => i.kind == BBKind.Asset).ToList();
            if (items.Count == 0) return true;
            string path = BBPaths.ImportProgress(plan.key);
            var progress = BBUtil.Read<BBImportProgress>(path);
            if (progress == null || progress.sessionId != plan.result.sessionId || progress.written != plan.result.written)
                progress = new BBImportProgress { sessionId = plan.result.sessionId, written = plan.result.written };
            int reused = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var asset = plan.result.assets.First(x => x.key == items[i].id);
                var prev = progress.done.FirstOrDefault(d => d.key == asset.key && d.hash == asset.hash);
                if (prev != null && AssetDatabase.LoadAssetAtPath<GameObject>(prev.prefab))
                {
                    BBSceneOps.Record(ctx, prev.key, prev.finalKey, prev.prefab, prev.model);
                    reused++;
                    continue;
                }
                EditorUtility.DisplayProgressBar("Blender Bridge", items[i].text, 0.5f * i / items.Count);
                try
                {
                    items[i].run(ctx);
                    string finalKey = ctx.Final(asset.key);
                    ctx.assetPrefabs.TryGetValue(finalKey, out var pf);
                    ctx.assetModels.TryGetValue(finalKey, out var md);
                    progress.done.Add(new BBImportDone { key = asset.key, finalKey = finalKey, prefab = pf, model = md, hash = asset.hash });
                    BBUtil.Write(path, BBUtil.Json(progress));
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    int done = progress.done.Count;
                    preview.error = $"{items[i].text} failed: {e.Message}. The scene was not changed" +
                                    (done > 0 ? $"; {done} asset(s) before it were built and will be reused (not built twice) when you Apply again." : ".");
                    return false;
                }
            }
            if (reused > 0) ctx.log.Add($"{reused} asset(s) were already built by an earlier Apply and were reused");
            return true;
        }

        /// <summary>Ids that changed during the sync (e.g. a prefab instance switched to its own variant gets a new
        /// GlobalObjectId), old -> new.</summary>
        static Dictionary<string, string> Remap(BBCtx ctx)
        {
            var pairs = ctx.byId.Where(kv => !kv.Key.StartsWith("tmp:") && kv.Value).ToList();
            var now = BBUtil.Goids(pairs.Select(p => p.Value).ToArray());
            var remap = new Dictionary<string, string>();
            for (int i = 0; i < pairs.Count; i++) if (now[i] != pairs[i].Key) remap[pairs[i].Key] = now[i];
            return remap;
        }

        /// <summary>The snapshot becomes what Blender now has, so the next sync from the same .blend only carries newer
        /// edits (a conflict where Unity's value was kept is not offered again). Returns what Blender must learn.</summary>
        static BBApplied FinishSnapshot(BBCtx ctx, Dictionary<string, string> remap)
        {
            var plan = ctx.plan;
            var applied = new BBApplied { sessionId = plan.session.sessionId, applied = DateTime.Now.ToString("s") };
            var tmpIds = new Dictionary<string, string>();
            foreach (var c in ctx.created)
                if (c.go) tmpIds["tmp:" + c.node.tmp] = BBUtil.Goid(c.go);
            string Map(string p) => string.IsNullOrEmpty(p) ? "" : tmpIds.TryGetValue(p, out var id) ? id : remap.TryGetValue(p, out var r) ? r : p;

            var rById = plan.result.nodes.Where(n => !string.IsNullOrEmpty(n.id)).GroupBy(n => n.id).ToDictionary(g => g.Key, g => g.First());
            var keep = new List<BBNode>();
            foreach (var s in plan.session.nodes)
            {
                if (s.editable && rById.TryGetValue(s.id, out var r))
                {
                    s.m = r.m;
                    s.parent = Map(r.parent);
                    if (!string.IsNullOrEmpty(r.name)) s.name = BBUtil.CleanName(r.name);
                }
                else if (s.editable && ctx.deleted.Contains(s.id)) continue;
                else if (s.editable) s.editable = false;  // kept in Unity on purpose: no longer in Blender
                s.id = Map(s.id);
                if (!s.editable) s.parent = "";
                keep.Add(s);
            }
            foreach (var c in ctx.created)
            {
                if (!c.go) continue;
                string id = tmpIds["tmp:" + c.node.tmp];
                keep.Add(new BBNode { id = id, name = c.go.name, parent = Map(c.node.parent), asset = c.asset, kind = c.kind, editable = true, active = c.go.activeInHierarchy, m = c.node.m });
                string bbKey = c.node.asset.StartsWith("blender:") ? ctx.Final(c.node.asset.Substring(8)) : "";  // new model placed
                var oa = bbKey != "" ? plan.result.assets.FirstOrDefault(a => a.key == c.node.asset.Substring(8)) : null;
                applied.ids.Add(new BBIdMap { tmp = c.node.tmp, id = id, asset = bbKey, hash = oa?.hash ?? "" });
            }
            foreach (var a in plan.result.assets.Where(a => !a.isNew))
                applied.ids.Add(new BBIdMap { tmp = "", id = "", asset = ctx.Final(a.key), hash = a.hash });
            foreach (var a in plan.result.assets.Where(a => a.isNew && !string.IsNullOrEmpty(a.node)))
                applied.ids.Add(new BBIdMap { tmp = "", id = Map(a.node), asset = ctx.Final(a.key), hash = a.hash });
            applied.remap = remap.Select(kv => new BBRemap { from = kv.Key, to = kv.Value }).ToList();
            plan.session.nodes = keep;
            BBUtil.Write(BBPaths.SessionJson(plan.key), BBUtil.Json(plan.session));
            return applied;
        }

        public static void WriteStatus(string key, string sessionId, string state, string message) =>
            BBUtil.Write(BBPaths.StatusJson(key), BBUtil.Json(new BBStatus { sessionId = sessionId, state = state, message = message, time = DateTime.Now.ToString("s") }));

        // ================================================================ backup
        static string Backup(BBPlan plan)
        {
            string dir = Path.Combine(BBPaths.Backups, plan.key, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            string src = BBPaths.Full(plan.session.scenePath);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), true);
            File.Copy(BBPaths.SessionJson(plan.key), Path.Combine(dir, "session.json"), true);
            File.WriteAllText(Path.Combine(dir, "README.txt"),
                $"Backup taken before applying Blender changes to {plan.session.scenePath} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n" +
                $"To roll back: close the scene in Unity, copy {Path.GetFileName(src)} back over {plan.session.scenePath}, then reopen it.\n" +
                "Ctrl+Z right after the sync also undoes the scene changes (not the new asset files).\n");
            return dir;
        }

        static void Journal(BBPlan plan, string summary)
        {
            try
            {
                File.AppendAllText(Path.Combine(BBPaths.Backups, "journal.jsonl"), JsonUtility.ToJson(new BBJournalEntry
                    { time = DateTime.Now.ToString("s"), scene = plan.session.scenePath, summary = summary, backup = plan.backupDir.Replace('\\', '/') }) + "\n");
            }
            catch (Exception e) { Debug.LogWarning("[Blender Bridge] journal: " + e.Message); }
        }
    }
}
