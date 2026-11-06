using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Auleek.BlenderBridge.Editor
{
    public enum BBKind { Asset, Duplicate, Add, Reparent, Move, Rename, Delete, Conflict, Warning }

    /// <summary>When an item runs. Objects are created first, then parented, then placed (parents before children),
    /// so a copy placed under another new copy, or under a parent that also moved, lands where Blender had it.</summary>
    internal enum BBPhase { Assets = 0, Create = 1, Geometry = 2, Parent = 3, Transform = 4, Rename = 5, Delete = 6, Info = 9 }

    public class BBItem
    {
        public BBKind kind;
        /// <summary>For conflicts: what the change would be (Move, Reparent, Delete...).</summary>
        public BBKind change;
        public string id, field, text;
        public bool apply = true, choice;
        public GameObject target;
        internal BBPhase phase;
        internal int depth;
        internal Action<BBCtx> run;
        public string Key => $"{kind}|{id}|{field}";
    }

    public class BBPlan
    {
        public string key, sceneName, error, backupDir;
        public BBSession session;
        public BBResult result;
        public BBLibrary lib;
        public readonly List<BBItem> items = new List<BBItem>();
        internal Dictionary<string, GameObject> live;
        /// <summary>Unity-space matrices of what Blender sent, by node ref (id, or "tmp:..." for new objects).</summary>
        internal readonly Dictionary<string, Matrix4x4> locals = new Dictionary<string, Matrix4x4>(), worlds = new Dictionary<string, Matrix4x4>();
        internal readonly HashSet<GameObject> nodeObjects = new HashSet<GameObject>();

        public int Count(BBKind k) => items.Count(i => i.kind == k);
        public bool HasConflicts => items.Any(i => i.kind == BBKind.Conflict);
        public bool HasWarnings => items.Any(i => i.kind == BBKind.Warning);
        public bool IsEmpty => items.All(i => i.kind == BBKind.Warning);
        public string Fingerprint => string.Join("\n", items.Select(i => i.Key));

        public string Summary()
        {
            var p = new List<string>();
            void P(BBKind k, string w) { int n = items.Count(i => i.kind == k); if (n > 0) p.Add($"{n} {w}"); }
            P(BBKind.Asset, "assets built");
            P(BBKind.Add, "added");
            P(BBKind.Duplicate, "duplicated");
            P(BBKind.Move, "moved");
            P(BBKind.Reparent, "re-parented");
            P(BBKind.Rename, "renamed");
            P(BBKind.Delete, "deleted");
            int c = Count(BBKind.Conflict);
            if (c > 0) p.Add($"{c} conflicts ({items.Count(i => i.kind == BBKind.Conflict && !i.apply)} kept Unity's)");
            return p.Count == 0 ? "no changes" : string.Join(", ", p);
        }
    }

    /// <summary>
    /// Blender -> Unity, three-way per node and field: base = the snapshot Unity sent (or the state after the last
    /// sync), Blender = result.json, Unity = the scene now. Changed only in Blender: applied. Changed in both: a
    /// conflict that keeps Unity's unless ticked. Transforms are compared relative to the parent, so moving a group
    /// moves its children without listing (or conflicting on) each one.
    /// </summary>
    public static class BBPlanner
    {
        public static bool HasResult(string key, out DateTime time)
        {
            string p = BBPaths.ResultJson(key);
            bool e = File.Exists(p);
            time = e ? File.GetLastWriteTimeUtc(p) : default;
            return e;
        }

        public static BBPlan Build(string key)
        {
            var plan = new BBPlan { key = key };
            plan.session = BBUtil.Read<BBSession>(BBPaths.SessionJson(key));
            plan.result = BBUtil.Read<BBResult>(BBPaths.ResultJson(key));
            plan.lib = BBUtil.Read<BBLibrary>(BBPaths.LibraryJson);
            if (plan.session == null) { plan.error = "No Blender session for this scene. Use Open in Blender first."; return plan; }
            plan.sceneName = plan.session.scene;
            if (plan.result == null) { plan.error = "Nothing from Blender yet. In Blender: Unity tab > Sync to Unity."; return plan; }
            if (plan.result.sessionId != plan.session.sessionId)
            { plan.error = "These changes come from an older Blender file than the current session. Sync again from the current one."; return plan; }
            if (plan.result.version != BBFormat.Version)
            { plan.error = "These changes come from a Blender file made by another version of the bridge. Open the scene in Blender again (Start fresh)."; return plan; }
            if (BBUtil.Read<BBStatus>(BBPaths.StatusJson(key))?.state == BBStatus.Undone)
            { plan.error = "The last Blender sync was undone in Unity, so the Blender file no longer matches the scene. Open in Blender > Start fresh."; return plan; }
            var scene = SceneManager.GetActiveScene();
            if (scene.path != plan.session.scenePath) { plan.error = $"Open {plan.session.scenePath} to apply its Blender changes."; return plan; }

            var live = plan.live = BBUtil.SceneIndex(scene);
            var idOf = new Dictionary<GameObject, string>(live.Count);
            foreach (var kv in live) idOf[kv.Value] = kv.Key;
            var snap = plan.session.nodes.Where(n => n.editable).GroupBy(n => n.id).ToDictionary(g => g.Key, g => g.First());
            foreach (var s in snap.Keys) if (live.TryGetValue(s, out var g) && g) plan.nodeObjects.Add(g);

            // ---- what Blender has, by ref: world matrices and parents, for local transforms and depths
            var bWorld = new Dictionary<string, Matrix4x4>();
            var bParent = new Dictionary<string, string>();
            void Add(string r, BBOutNode n) { bWorld[r] = BBConvert.Unflat(n.m); bParent[r] = n.parent ?? ""; }
            foreach (var n in plan.result.nodes) if (!string.IsNullOrEmpty(n.id) && !bWorld.ContainsKey(n.id)) Add(n.id, n);
            foreach (var n in plan.result.dups.Concat(plan.result.added)) Add("tmp:" + n.tmp, n);
            Matrix4x4 BLocal(string r) => (bParent[r] != "" && bWorld.TryGetValue(bParent[r], out var pw) ? pw.inverse : Matrix4x4.identity) * bWorld[r];
            foreach (var r in bWorld.Keys) { plan.locals[r] = BBConvert.ToUnity(BLocal(r)); plan.worlds[r] = BBConvert.ToUnity(bWorld[r]); }
            int Depth(string r) { int d = 0; for (string p = r; bParent.TryGetValue(p, out var pp) && pp != "" && d < 64; p = pp) d++; return d; }

            Matrix4x4 SLocal(BBNode s) => (!string.IsNullOrEmpty(s.parent) && snap.TryGetValue(s.parent, out var ps) ? BBConvert.Unflat(ps.m).inverse : Matrix4x4.identity) * BBConvert.Unflat(s.m);
            Matrix4x4 ULocal(GameObject go) => BBConvert.BlenderWorld(go.transform.parent).inverse * BBConvert.BlenderWorld(go.transform);
            string Name(string id) => string.IsNullOrEmpty(id) ? "(scene root)" : snap.TryGetValue(id, out var s) ? s.name : id.StartsWith("tmp:") ? "a new object" : "(scene root)";

            AddAssets(plan, live, snap);

            // ---- existing nodes
            var seen = new HashSet<string>();
            foreach (var r in plan.result.nodes)
            {
                if (string.IsNullOrEmpty(r.id) || !snap.TryGetValue(r.id, out var s) || !seen.Add(r.id)) continue;
                bool bParent2 = (r.parent ?? "") != (s.parent ?? "");
                var sl = SLocal(s);
                bool bMoved = !BBConvert.Near(BLocal(r.id), sl);
                string bName = BBUtil.CleanName(r.name);
                bool bRenamed = !string.IsNullOrEmpty(bName) && bName != s.name;
                if (!bMoved && !bParent2 && !bRenamed) continue;
                if (!live.TryGetValue(r.id, out var go) || !go)
                {
                    plan.items.Add(new BBItem { kind = BBKind.Warning, phase = BBPhase.Info, id = r.id, text = $"{s.name}: deleted in Unity during the session; Blender's changes to it are dropped" });
                    continue;
                }
                string uParent = go.transform.parent && idOf.TryGetValue(go.transform.parent.gameObject, out var up) ? up : "";
                bool uMoved = !BBConvert.Near(ULocal(go), sl), uParentChanged = uParent != (s.parent ?? "");
                int depth = Depth(r.id);
                string rid = r.id, pref = r.parent ?? "";
                var target = go;
                if (bParent2)
                    Field(plan, BBKind.Reparent, BBPhase.Parent, target, rid, "parent", uParentChanged || uMoved, depth,
                          $"{s.name}: moved under {Name(pref)}", ctx => BBSceneOps.Reparent(ctx, target, pref, rid));
                else if (bMoved)
                    Field(plan, BBKind.Move, BBPhase.Transform, target, rid, "transform", uMoved || uParentChanged, depth,
                          $"{s.name}: moved / rotated / scaled", ctx => BBSceneOps.Place(ctx, target, pref, rid));
                if (bRenamed)
                    Field(plan, BBKind.Rename, BBPhase.Rename, target, rid, "name", go.name != s.name, depth,
                          $"{s.name}: renamed to {bName}", ctx => { Undo.RecordObject(target, "Rename"); target.name = bName; });
            }

            // ---- deleted in Blender
            foreach (var s in snap.Values)
            {
                if (seen.Contains(s.id) || !live.TryGetValue(s.id, out var go) || !go) continue;
                var target = go;
                string id = s.id;
                string uParent = go.transform.parent && idOf.TryGetValue(go.transform.parent.gameObject, out var up) ? up : "";
                bool uChanged = !BBConvert.Near(ULocal(go), SLocal(s)) || uParent != (s.parent ?? "");
                plan.items.Add(new BBItem
                {
                    kind = uChanged ? BBKind.Conflict : BBKind.Delete, change = BBKind.Delete, id = s.id, field = "delete", target = go, choice = true, apply = !uChanged,
                    phase = BBPhase.Delete, text = uChanged ? $"{s.name}: deleted in Blender, but moved in Unity since. Kept unless ticked." : $"{s.name}: deleted in Blender",
                    run = ctx => { if (target) { ctx.deleted.Add(id); Undo.DestroyObjectImmediate(target); } },
                });
            }

            // ---- Shift+D copies and new instances
            foreach (var d in plan.result.dups)
            {
                live.TryGetValue(d.of ?? "", out var src);
                if (!src) { plan.items.Add(new BBItem { kind = BBKind.Warning, phase = BBPhase.Info, id = d.tmp, text = $"Copy of {Name(d.of)}: the original is gone from Unity; skipped" }); continue; }
                var node = d;
                var source = src;
                snap.TryGetValue(d.of, out var sn);
                string srcAsset = sn?.asset ?? "", srcKind = sn?.kind ?? "group";
                plan.items.Add(new BBItem
                {
                    kind = BBKind.Duplicate, phase = BBPhase.Create, depth = Depth("tmp:" + d.tmp), id = "tmp:" + d.tmp, target = src, text = $"Copy of {src.name}",
                    run = ctx => BBSceneOps.Duplicate(ctx, node, source, srcAsset, srcKind),
                });
            }
            foreach (var a in plan.result.added)
            {
                var node = a;
                string what = a.asset.StartsWith("blender:") ? "new Blender asset " + a.asset.Substring(8)
                            : plan.lib?.assets.FirstOrDefault(x => x.key == a.asset)?.name ?? a.asset;
                plan.items.Add(new BBItem
                {
                    kind = BBKind.Add, phase = BBPhase.Create, depth = Depth("tmp:" + a.tmp), id = "tmp:" + a.tmp, text = $"Added {BBUtil.CleanName(a.name)} ({what})",
                    run = ctx => BBSceneOps.Create(ctx, node),
                });
            }

            plan.items.Sort((x, y) => x.phase != y.phase ? x.phase.CompareTo(y.phase) : x.depth.CompareTo(y.depth));
            return plan;
        }

        static void AddAssets(BBPlan plan, Dictionary<string, GameObject> live, Dictionary<string, BBNode> snap)
        {
            foreach (var a in plan.result.assets)
            {
                var asset = a;
                string originalPath = !string.IsNullOrEmpty(a.prefab) && a.prefab.StartsWith("prefab:") ? AssetDatabase.GUIDToAssetPath(a.prefab.Substring(7)) : "";
                string original = Path.GetFileNameWithoutExtension(originalPath);
                string text;
                if (a.mode == "source")
                {
                    int here = live.Values.Count(g => g && PrefabUtility.IsOutermostPrefabInstanceRoot(g) && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g) == originalPath);
                    text = $"{original} (the prefab itself) gets the Blender geometry: every copy changes, {here} in this scene plus any in other scenes";
                    if (a.pivotMoved)
                        plan.items.Add(new BBItem { kind = BBKind.Warning, phase = BBPhase.Info, id = a.key, field = "pivot",
                            text = $"The pivot of {original} moved in Blender. Other copies keep their positions, so their geometry shifts by the same offset." });
                }
                else if (a.mode == "variant")
                    text = a.isNew ? $"New prefab variant PF_{a.key} of {original} with the Blender geometry" : $"Prefab variant PF_{a.key} updated";
                else
                    text = a.isNew ? $"New Blender asset {a.name}: model, {a.materials.Count} material(s), prefab" : $"Blender asset {a.name} updated (model, materials)";
                if (a.isNew && (a.mode == "source" || a.mode == "variant"))
                {
                    var pf = AssetDatabase.LoadAssetAtPath<GameObject>(originalPath);
                    if (pf && pf.GetComponentsInChildren<LODGroup>(true).Any(g => g.lodCount > 1))
                        plan.items.Add(new BBItem { kind = BBKind.Warning, phase = BBPhase.Info, id = a.key, field = "lod",
                            text = $"{original} has LOD levels. After this, it shows the Blender geometry at every distance (its old LOD1+ meshes are switched off)." });
                }
                plan.items.Add(new BBItem { kind = BBKind.Asset, phase = BBPhase.Assets, id = a.key, text = text, run = ctx => BBSceneOps.ImportAsset(ctx, asset) });
                if (a.isNew && !string.IsNullOrEmpty(a.node))
                {
                    live.TryGetValue(a.node, out var target);
                    string nm = snap.TryGetValue(a.node, out var sn0) ? sn0.name : a.name;
                    bool prefab = target && PrefabUtility.IsOutermostPrefabInstanceRoot(target);
                    plan.items.Add(new BBItem
                    {
                        kind = BBKind.Move, phase = BBPhase.Geometry, id = a.node, field = "geometry", target = target,
                        text = a.mode == "source" ? $"{nm}: shows the edited prefab"
                             : prefab ? $"{nm}: switches to its own prefab variant PF_{a.key} (its overrides and the prefab's components are kept)"
                             : $"{nm}: modelled in Blender; now uses the new mesh SM_{a.key} (materials and components kept)",
                        run = ctx => BBSceneOps.Retarget(ctx, asset),
                    });
                }
            }
        }

        static void Field(BBPlan plan, BBKind kind, BBPhase phase, GameObject go, string id, string field, bool unityChanged, int depth, string text, Action<BBCtx> run)
        {
            plan.items.Add(unityChanged
                ? new BBItem { kind = BBKind.Conflict, change = kind, id = id, field = field, target = go, choice = true, apply = false, phase = phase, depth = depth, run = run,
                               text = text + " in Blender, but also changed in Unity since. Unity's kept unless ticked." }
                : new BBItem { kind = kind, change = kind, id = id, field = field, target = go, phase = phase, depth = depth, run = run, text = text });
        }
    }
}
