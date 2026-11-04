using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Auleek.BlenderBridge.Editor
{
    public enum BBKind { Asset, Duplicate, Add, Reparent, Move, Rename, Delete, Conflict, Warning }

    public class BBItem
    {
        public BBKind kind;
        public string id, field, text;
        public bool apply = true, choice;
        public GameObject target;
        internal int order, depth;
        internal Action<BBCtx> run;
        public string Key => $"{kind}|{id}|{field}";
    }

    internal class BBCtx
    {
        public Scene scene;
        public BBPlan plan;
        public Dictionary<string, GameObject> byId;
        public readonly Dictionary<string, string> assetKeys = new Dictionary<string, string>();     // Blender's key -> final key
        public readonly Dictionary<string, string> assetPrefabs = new Dictionary<string, string>();  // final key -> prefab path
        public readonly Dictionary<string, string> assetModels = new Dictionary<string, string>();   // final key -> model path
        public readonly List<(BBOutNode node, GameObject go, string asset, string kind)> created = new List<(BBOutNode, GameObject, string, string)>();
        public readonly HashSet<string> deleted = new HashSet<string>();
        public readonly List<string> log = new List<string>();
    }

    public class BBPlan
    {
        public string scene, error, backupDir;
        public BBSession session;
        public BBResult result;
        public BBLibrary lib;
        public readonly List<BBItem> items = new List<BBItem>();

        public int Count(BBKind k) => items.Count(i => i.kind == k);
        public bool HasConflicts => items.Any(i => i.kind == BBKind.Conflict);
        public string Fingerprint => string.Join("\n", items.Select(i => i.Key));

        public string Summary()
        {
            var p = new List<string>();
            void P(BBKind k, string w) { int n = items.Count(i => i.kind == k && (i.apply || !i.choice)); if (n > 0) p.Add($"{n} {w}"); }
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
    /// Blender -> Unity. Three-way per node and field: base = the snapshot Unity sent (or the state after the last sync),
    /// Blender = result.json, Unity = the scene now. Changed only in Blender: applied. Changed in both: a conflict that
    /// keeps Unity's unless ticked. <see cref="Apply"/> backs the scene up, applies everything as one Undo step, saves,
    /// rewrites the snapshot and tells Blender the ids of what it created (applied.json).
    /// </summary>
    public static class BBApply
    {
        public static bool HasResult(string scene, out DateTime time)
        {
            string p = BBPaths.ResultJson(scene);
            time = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : default;
            return File.Exists(p);
        }

        // ================================================================ plan
        public static BBPlan BuildPlan(string sceneName)
        {
            var plan = new BBPlan { scene = sceneName };
            plan.session = BBUtil.Read<BBSession>(BBPaths.SessionJson(sceneName));
            plan.result = BBUtil.Read<BBResult>(BBPaths.ResultJson(sceneName));
            plan.lib = BBUtil.Read<BBLibrary>(BBPaths.LibraryJson);
            if (plan.session == null) { plan.error = "No Blender session for this scene. Use Open in Blender first."; return plan; }
            if (plan.result == null) { plan.error = "Nothing from Blender yet. In Blender: Unity tab > Sync to Unity."; return plan; }
            if (plan.result.sessionId != plan.session.sessionId)
            { plan.error = "These changes come from an older Blender file than the current session. Sync again from the current one."; return plan; }
            var scene = SceneManager.GetActiveScene();
            if (scene.path != plan.session.scenePath) { plan.error = $"Open {plan.session.scenePath} to apply its Blender changes."; return plan; }

            var live = BBUtil.SceneIndex(scene);
            var idOf = live.ToDictionary(kv => kv.Value, kv => kv.Key);
            var snap = plan.session.nodes.Where(n => n.editable).GroupBy(n => n.id).ToDictionary(g => g.Key, g => g.First());
            var rParents = plan.result.nodes.Where(n => !string.IsNullOrEmpty(n.id)).GroupBy(n => n.id).ToDictionary(g => g.Key, g => g.First().parent ?? "");
            int Depth(string id) { int d = 0; for (string p = id; rParents.TryGetValue(p, out var pp) && pp != "" && d < 64; p = pp) d++; return d; }
            string Name(string id) => snap.TryGetValue(id, out var s) ? s.name : id.StartsWith("tmp:") ? "a new object" : "(scene root)";

            // ---- assets modelled in Blender
            foreach (var a in plan.result.assets)
            {
                var asset = a;
                string originalPath = !string.IsNullOrEmpty(a.prefab) && a.prefab.StartsWith("prefab:") ? AssetDatabase.GUIDToAssetPath(a.prefab.Substring(7)) : "";
                string text;
                if (a.mode == "source")
                {
                    int here = live.Values.Count(g => g && PrefabUtility.IsOutermostPrefabInstanceRoot(g) && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g) == originalPath);
                    text = $"{Path.GetFileNameWithoutExtension(originalPath)} (the prefab itself) gets the Blender geometry: every copy changes, {here} in this scene plus any in other scenes";
                    if (a.pivotMoved)
                        plan.items.Add(new BBItem { kind = BBKind.Warning, id = a.key, field = "pivot",
                            text = $"The pivot of {Path.GetFileNameWithoutExtension(originalPath)} moved in Blender. Other copies keep their positions, so their geometry shifts by the same offset." });
                }
                else if (a.mode == "variant")
                    text = a.isNew ? $"New prefab variant PF_{a.key} of {Path.GetFileNameWithoutExtension(originalPath)} with the Blender geometry" : $"Prefab variant PF_{a.key} updated";
                else
                    text = a.isNew ? $"New Blender asset {a.name}: model, {a.materials.Count} material(s), prefab" : $"Blender asset {a.name} updated (model, materials)";
                plan.items.Add(new BBItem { kind = BBKind.Asset, id = a.key, order = 0, text = text, run = ctx => ImportAsset(ctx, asset) });
                if (a.isNew && !string.IsNullOrEmpty(a.node))
                {
                    live.TryGetValue(a.node, out var target);
                    string nm = snap.TryGetValue(a.node, out var sn0) ? sn0.name : a.name;
                    bool prefab = target && PrefabUtility.IsOutermostPrefabInstanceRoot(target);
                    plan.items.Add(new BBItem
                    {
                        kind = BBKind.Move, id = a.node, field = "geometry", target = target, order = 2,
                        text = a.mode == "source" ? $"{nm}: shows the edited prefab"
                             : prefab ? $"{nm}: switches to its own prefab variant PF_{a.key} (its overrides and the prefab's components are kept)"
                             : $"{nm}: modelled in Blender; now uses the new mesh SM_{a.key} (materials and components kept)",
                        run = ctx => Retarget(ctx, asset),
                    });
                }
            }

            // ---- existing nodes
            var seen = new HashSet<string>();
            foreach (var r in plan.result.nodes)
            {
                if (string.IsNullOrEmpty(r.id) || !snap.TryGetValue(r.id, out var s)) continue;
                seen.Add(r.id);
                bool bMoved = !BBConvert.Near(r.m, s.m), bParent = (r.parent ?? "") != (s.parent ?? "");
                string bName = BBUtil.CleanName(r.name);
                bool bRenamed = !string.IsNullOrEmpty(bName) && bName != s.name;
                if (!bMoved && !bParent && !bRenamed) continue;
                if (!live.TryGetValue(r.id, out var go) || !go)
                {
                    plan.items.Add(new BBItem { kind = BBKind.Warning, id = r.id, text = $"{s.name}: deleted in Unity during the session; Blender's changes to it are dropped" });
                    continue;
                }
                var uNow = BBConvert.Flat(BBConvert.ToBlender(go.transform.localToWorldMatrix));
                string uParent = go.transform.parent && idOf.TryGetValue(go.transform.parent.gameObject, out var up) ? up : "";
                int depth = Depth(r.id);
                var node = r;
                var target = go;
                if (bParent)
                    Field(plan, BBKind.Reparent, target, r.id, "parent", uParent != (s.parent ?? ""), depth,
                          $"{s.name}: moved under {Name(r.parent ?? "")}", ctx => Reparent(ctx, target, node.parent));
                if (bMoved)
                    Field(plan, BBKind.Move, target, r.id, "transform", !BBConvert.Near(uNow, s.m), depth,
                          $"{s.name}: moved / rotated / scaled", ctx => SetWorld(target, node.m));
                if (bRenamed)
                    Field(plan, BBKind.Rename, target, r.id, "name", go.name != s.name, depth,
                          $"{s.name}: renamed to {bName}", ctx => { Undo.RecordObject(target, "Rename"); target.name = bName; });
            }

            // ---- deleted in Blender
            foreach (var s in snap.Values)
            {
                if (seen.Contains(s.id) || !live.TryGetValue(s.id, out var go) || !go) continue;
                var target = go;
                string id = s.id;
                bool uChanged = !BBConvert.Near(BBConvert.Flat(BBConvert.ToBlender(go.transform.localToWorldMatrix)), s.m);
                plan.items.Add(new BBItem
                {
                    kind = uChanged ? BBKind.Conflict : BBKind.Delete, id = s.id, field = "delete", target = go, choice = true, apply = !uChanged, order = 6,
                    text = uChanged ? $"{s.name}: deleted in Blender, but moved in Unity since. Kept unless ticked." : $"{s.name}: deleted in Blender",
                    run = ctx => { if (target) { ctx.deleted.Add(id); Undo.DestroyObjectImmediate(target); } },
                });
            }

            // ---- Shift+D copies and new instances
            foreach (var d in plan.result.dups)
            {
                live.TryGetValue(d.of ?? "", out var src);
                if (!src) { plan.items.Add(new BBItem { kind = BBKind.Warning, id = d.tmp, text = $"Copy of {Name(d.of)}: the original is gone from Unity; skipped" }); continue; }
                var node = d;
                var source = src;
                string srcAsset = snap.TryGetValue(d.of, out var sn) ? sn.asset : "", srcKind = sn != null ? sn.kind : "group";
                plan.items.Add(new BBItem
                {
                    kind = BBKind.Duplicate, id = "tmp:" + d.tmp, target = src, order = 1, text = $"Copy of {src.name}",
                    run = ctx => Place(ctx, node, DuplicateOf(source), srcAsset, srcKind),
                });
            }
            foreach (var a in plan.result.added)
            {
                var node = a;
                string what = a.asset.StartsWith("blender:") ? "new Blender asset " + a.asset.Substring(8)
                            : plan.lib?.assets.FirstOrDefault(x => x.key == a.asset)?.name ?? a.asset;
                plan.items.Add(new BBItem
                {
                    kind = BBKind.Add, id = "tmp:" + a.tmp, order = 2, text = $"Added {BBUtil.CleanName(a.name)} ({what})",
                    run = ctx => Create(ctx, node),
                });
            }

            plan.items.Sort((x, y) => x.order != y.order ? x.order.CompareTo(y.order) : x.depth.CompareTo(y.depth));
            return plan;
        }

        static void Field(BBPlan plan, BBKind kind, GameObject go, string id, string field, bool unityChanged, int depth, string text, Action<BBCtx> run)
        {
            int order = kind == BBKind.Reparent ? 3 : kind == BBKind.Move ? 4 : 5;
            plan.items.Add(unityChanged
                ? new BBItem { kind = BBKind.Conflict, id = id, field = field, target = go, choice = true, apply = false, order = order, depth = depth, run = run,
                               text = text + " in Blender, but also changed in Unity since. Unity's kept unless ticked." }
                : new BBItem { kind = kind, id = id, field = field, target = go, order = order, depth = depth, run = run, text = text });
        }

        // ================================================================ apply
        public static bool Apply(BBPlan preview, bool interactive)
        {
            var scene = SceneManager.GetActiveScene();
            if (preview.session == null || scene.path != preview.session.scenePath) { preview.error = "the scene the changes are for is not open"; return false; }
            if (scene.isDirty)
            {
                if (!interactive) { preview.error = "the scene has unsaved changes"; return false; }
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo() || scene.isDirty) { preview.error = "save the scene first, so the backup holds everything"; return false; }
            }
            var plan = BuildPlan(preview.scene);
            if (plan.error != null) { preview.error = plan.error; return false; }
            if (plan.Fingerprint != preview.Fingerprint) { preview.error = "the scene or Blender's changes moved on since the preview; preview again"; return false; }
            var choices = preview.items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First().apply);
            foreach (var i in plan.items) if (i.choice && choices.TryGetValue(i.Key, out var a)) i.apply = a;

            plan.backupDir = Backup(plan);
            var ctx = new BBCtx { scene = scene, plan = plan, byId = BBUtil.SceneIndex(scene) };
            try
            {
                // assets first (asset files are not part of the scene Undo)
                var assetItems = plan.items.Where(i => i.kind == BBKind.Asset).ToList();
                for (int i = 0; i < assetItems.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Blender Bridge", assetItems[i].text, 0.5f * i / Mathf.Max(1, assetItems.Count));
                    try { assetItems[i].run(ctx); }
                    catch (Exception e)
                    {
                        // nothing in the scene has changed yet: stop here, the sync stays pending
                        Debug.LogException(e);
                        preview.error = $"{assetItems[i].text} failed: {e.Message}. The scene was not changed.";
                        return false;
                    }
                }
                Undo.IncrementCurrentGroup();
                int group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Blender Bridge: sync " + plan.scene);
                EditorUtility.DisplayProgressBar("Blender Bridge", "Applying scene changes", 0.6f);
                foreach (var i in plan.items)
                    if (i.kind != BBKind.Asset && i.apply && i.run != null)
                    {
                        try { i.run(ctx); }
                        catch (Exception e) { ctx.log.Add($"{i.text}: {e.Message}"); Debug.LogException(e); }
                    }
                Undo.CollapseUndoOperations(group);
            }
            finally { EditorUtility.ClearProgressBar(); }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);   // new objects get their file ids, so their ids are final now
            var applied = FinishSnapshot(ctx);
            EditorSceneManager.SaveScene(scene);
            string summary = plan.Summary();
            applied.summary = summary;
            BBUtil.Write(BBPaths.AppliedJson(plan.scene), BBUtil.Json(applied));
            string rj = BBPaths.ResultJson(plan.scene), done = rj.Replace("result.json", "result.applied.json");
            if (File.Exists(done)) File.Delete(done);
            File.Move(rj, done);
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(plan.backupDir), "journal.jsonl"),
                $"{{\"time\":\"{DateTime.Now:s}\",\"scene\":\"{plan.session.scenePath}\",\"summary\":\"{summary}\",\"backup\":\"{plan.backupDir.Replace('\\', '/')}\"}}\n");
            foreach (var l in ctx.log) Debug.LogWarning("[Blender Bridge] " + l);
            Debug.Log($"[Blender Bridge] {plan.scene}: {summary}. Backup: {plan.backupDir}");
            preview.error = null;
            preview.backupDir = plan.backupDir;
            return true;
        }

        /// <summary>The snapshot becomes what Blender now has, so the next sync from the same .blend only carries newer
        /// edits (a conflict where Unity's value was kept is not offered again). Returns the ids Blender must learn.</summary>
        static BBApplied FinishSnapshot(BBCtx ctx)
        {
            var plan = ctx.plan;
            var applied = new BBApplied { sessionId = plan.session.sessionId, applied = DateTime.Now.ToString("s") };
            var tmpIds = new Dictionary<string, string>();
            foreach (var c in ctx.created)
                if (c.go) tmpIds["tmp:" + c.node.tmp] = BBUtil.Goid(c.go);
            string Map(string p) => p != null && tmpIds.TryGetValue(p, out var id) ? id : p ?? "";

            var rById = plan.result.nodes.Where(n => !string.IsNullOrEmpty(n.id)).GroupBy(n => n.id).ToDictionary(g => g.Key, g => g.First());
            var keep = new List<BBNode>();
            foreach (var s in plan.session.nodes)
            {
                if (!s.editable) { keep.Add(s); continue; }
                if (rById.TryGetValue(s.id, out var r))
                {
                    s.m = r.m;
                    s.parent = Map(r.parent);
                    if (!string.IsNullOrEmpty(r.name)) s.name = BBUtil.CleanName(r.name);
                    keep.Add(s);
                }
                else if (!ctx.deleted.Contains(s.id)) { s.editable = false; keep.Add(s); }  // kept in Unity on purpose: no longer in Blender
            }
            foreach (var c in ctx.created)
            {
                if (!c.go) continue;
                string id = tmpIds["tmp:" + c.node.tmp];
                keep.Add(new BBNode { id = id, name = c.go.name, parent = Map(c.node.parent), asset = c.asset, kind = c.kind, editable = true, active = c.go.activeInHierarchy, m = c.node.m });
                string bbKey = c.node.asset.StartsWith("blender:") ? Final(ctx, c.node.asset.Substring(8)) : "";  // new model placed
                var oa = bbKey != "" ? plan.result.assets.FirstOrDefault(a => a.key == c.node.asset.Substring(8)) : null;
                applied.ids.Add(new BBIdMap { tmp = c.node.tmp, id = id, asset = bbKey, hash = oa?.hash ?? "" });
            }
            foreach (var a in plan.result.assets.Where(a => !a.isNew))
                applied.ids.Add(new BBIdMap { tmp = "", id = "", asset = Final(ctx, a.key), hash = a.hash });
            foreach (var a in plan.result.assets.Where(a => a.isNew && !string.IsNullOrEmpty(a.node)))
                applied.ids.Add(new BBIdMap { tmp = "", id = a.node, asset = Final(ctx, a.key), hash = a.hash });
            plan.session.nodes = keep;
            BBUtil.Write(BBPaths.SessionJson(plan.scene), BBUtil.Json(plan.session));
            return applied;
        }

        static string Final(BBCtx ctx, string key) => ctx.assetKeys.TryGetValue(key, out var k) ? k : key;

        // ================================================================ scene operations
        static Transform Resolve(BBCtx ctx, string id) =>
            !string.IsNullOrEmpty(id) && ctx.byId.TryGetValue(id, out var go) && go ? go.transform : null;

        static void SetWorld(GameObject go, float[] m)
        {
            if (!go) return;
            Undo.RecordObject(go.transform, "Move");
            BBConvert.SetWorld(go.transform, BBConvert.ToUnity(BBConvert.Unflat(m)));
        }

        static void Reparent(BBCtx ctx, GameObject go, string parentId)
        {
            if (!go) return;
            var p = Resolve(ctx, parentId);
            if (go.transform.parent != p) Undo.SetTransformParent(go.transform, p, "Re-parent");
            if (!p && go.scene != ctx.scene) SceneManager.MoveGameObjectToScene(go, ctx.scene);
        }

        static GameObject DuplicateOf(GameObject src)
        {
            var sel = Selection.objects;
            Selection.objects = new Object[] { src };
            Unsupported.DuplicateGameObjectsUsingPasteboard();   // keeps prefab links, overrides and components
            var copy = Selection.activeGameObject;
            Selection.objects = sel;
            if (copy && copy != src) return copy;
            copy = PrefabUtility.IsOutermostPrefabInstanceRoot(src)
                ? (GameObject)PrefabUtility.InstantiatePrefab(PrefabUtility.GetCorrespondingObjectFromSource(src), src.transform.parent)
                : Object.Instantiate(src, src.transform.parent);
            Undo.RegisterCreatedObjectUndo(copy, "Copy");
            return copy;
        }

        static void Place(BBCtx ctx, BBOutNode n, GameObject go, string asset, string kind)
        {
            if (!go) return;
            var p = Resolve(ctx, n.parent);
            if (go.transform.parent != p) Undo.SetTransformParent(go.transform, p, "Place");
            Undo.RecordObject(go.transform, "Place");
            Undo.RecordObject(go, "Place");
            go.name = BBUtil.CleanName(n.name);
            BBConvert.SetWorld(go.transform, BBConvert.ToUnity(BBConvert.Unflat(n.m)));
            ctx.byId["tmp:" + n.tmp] = go;
            ctx.created.Add((n, go, asset, kind));
        }

        static void Create(BBCtx ctx, BBOutNode n)
        {
            GameObject go = null;
            string asset = n.asset;
            if (n.asset.StartsWith("prefab:"))
            {
                var pf = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(n.asset.Substring(7)));
                if (pf) go = (GameObject)PrefabUtility.InstantiatePrefab(pf, ctx.scene);
            }
            else if (n.asset.StartsWith("blender:"))
            {
                string key = Final(ctx, n.asset.Substring(8));
                string path = ctx.assetPrefabs.TryGetValue(key, out var p) ? p : BBAssets.FindPrefab(key);
                var pf = path != null ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
                if (pf) { go = (GameObject)PrefabUtility.InstantiatePrefab(pf, ctx.scene); asset = "prefab:" + AssetDatabase.AssetPathToGUID(path); }
            }
            else if (n.asset.StartsWith("mesh:"))
            {
                var part = ctx.plan.lib?.assets.FirstOrDefault(a => a.key == n.asset)?.parts.FirstOrDefault();
                var mesh = part != null ? BBUtil.FindByKey<Mesh>(part.mesh) : null;
                if (mesh)
                {
                    go = new GameObject(n.name, typeof(MeshFilter), typeof(MeshRenderer));
                    SceneManager.MoveGameObjectToScene(go, ctx.scene);
                    go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    go.GetComponent<MeshRenderer>().sharedMaterials = part.mats.Select(BBUtil.FindByKey<Material>).ToArray();
                }
            }
            if (!go) { ctx.log.Add($"{n.name}: could not create it from {n.asset}"); return; }
            Undo.RegisterCreatedObjectUndo(go, "Add from Blender");
            Place(ctx, n, go, asset, "instance");
        }

        static void ImportAsset(BBCtx ctx, BBOutAsset a)
        {
            string prefab = BBAssets.Import(a, ctx.plan.session, out string finalKey, out string model, ctx.log, ctx.plan.backupDir);
            ctx.assetKeys[a.key] = finalKey;
            if (prefab != null) { ctx.assetPrefabs[finalKey] = prefab; ctx.assetModels[finalKey] = model; }
        }

        /// <summary>A Unity object modelled in Blender (Make editable): it keeps its identity, components and materials and
        /// takes the new geometry. Plain mesh objects get the new mesh (and their MeshCollider follows); prefab instances
        /// switch to the new prefab, keeping their overrides.</summary>
        static void Retarget(BBCtx ctx, BBOutAsset a)
        {
            string key = Final(ctx, a.key);
            if (!ctx.byId.TryGetValue(a.node, out var go) || !go) { ctx.log.Add($"{a.name}: the Unity object is gone"); return; }
            if (!ctx.assetPrefabs.TryGetValue(key, out var prefabPath) || !ctx.assetModels.TryGetValue(key, out var model))
            { ctx.log.Add($"{a.name}: the new asset was not built"); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == prefabPath) return;  // the prefab itself was edited
                PrefabUtility.ReplacePrefabAssetOfPrefabInstance(go, prefab, new PrefabReplacingSettings
                {
                    logInfo = false, changeRootNameToAssetName = false, objectMatchMode = ObjectMatchMode.ByHierarchy,
                    prefabOverridesOptions = PrefabOverridesOptions.KeepAllPossibleOverrides,
                }, InteractionMode.UserAction);
                return;
            }
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(model).GetComponentsInChildren<MeshRenderer>(true);
            if (src.Length == 1 && go.TryGetComponent<MeshFilter>(out var mf))
            {
                var oldMesh = mf.sharedMesh;
                var newMesh = src[0].GetComponent<MeshFilter>().sharedMesh;
                Undo.RecordObject(mf, "Blender geometry");
                mf.sharedMesh = newMesh;
                if (go.TryGetComponent<MeshRenderer>(out var mr))
                {
                    Undo.RecordObject(mr, "Blender geometry");
                    mr.sharedMaterials = src[0].sharedMaterials;
                }
                foreach (var mc in go.GetComponents<MeshCollider>())
                    if (!mc.sharedMesh || mc.sharedMesh == oldMesh) { Undo.RecordObject(mc, "Blender geometry"); mc.sharedMesh = newMesh; }
                return;
            }
            // several parts now: the new prefab goes under the object and the old visual is switched off
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, go.transform);
            Undo.RegisterCreatedObjectUndo(inst, "Blender geometry");
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;
            if (go.TryGetComponent<MeshRenderer>(out var old)) { Undo.RecordObject(old, "Blender geometry"); old.enabled = false; }
        }

        // ================================================================ backup
        static string Backup(BBPlan plan)
        {
            string dir = Path.Combine(BBPaths.Project, "SyncBackups", "BlenderBridge", plan.scene, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            string src = BBPaths.Full(plan.session.scenePath);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), true);
            File.Copy(BBPaths.SessionJson(plan.scene), Path.Combine(dir, "session.json"), true);
            File.WriteAllText(Path.Combine(dir, "README.txt"),
                $"Backup taken before applying Blender changes to {plan.session.scenePath} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n" +
                $"To roll back: close the scene in Unity, copy {Path.GetFileName(src)} back over {plan.session.scenePath}, then reopen it.\n" +
                "Ctrl+Z right after the sync also undoes the scene changes (not the new asset files).\n");
            return dir;
        }
    }
}
