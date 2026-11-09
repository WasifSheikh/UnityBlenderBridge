using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>State of one Apply: what was built, created and deleted, and the scene objects by id.</summary>
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

        public string Final(string key) => assetKeys.TryGetValue(key, out var k) ? k : key;
        public Transform Resolve(string id) => !string.IsNullOrEmpty(id) && byId.TryGetValue(id, out var go) && go ? go.transform : null;
    }

    /// <summary>The scene operations a sync is made of. All of them record Undo, so one Ctrl+Z reverts the sync.</summary>
    internal static class BBSceneOps
    {
        // ================================================================ transforms
        /// <summary>Puts go where Blender has it: relative to its parent when Unity's parent is Blender's (so a parent kept
        /// at Unity's position in a conflict carries its children along), else at Blender's world position.</summary>
        public static void Place(BBCtx ctx, GameObject go, string parentRef, string nodeRef)
        {
            if (!go) return;
            Undo.RecordObject(go.transform, "Move");
            var p = ctx.Resolve(parentRef);
            bool sameParent = go.transform.parent == p && (p != null || string.IsNullOrEmpty(parentRef));
            if (sameParent && ctx.plan.locals.TryGetValue(nodeRef, out var local)) BBConvert.SetLocal(go.transform, local);
            else if (ctx.plan.worlds.TryGetValue(nodeRef, out var world)) BBConvert.SetWorld(go.transform, world);
        }

        public static void Reparent(BBCtx ctx, GameObject go, string parentRef, string nodeRef)
        {
            if (!go) return;
            var p = ctx.Resolve(parentRef);
            if (p == null && !string.IsNullOrEmpty(parentRef)) ctx.log.Add($"{go.name}: its new parent is not in the scene; kept at the top level");
            if (go.transform.parent != p) Undo.SetTransformParent(go.transform, p, "Re-parent");
            if (!p && go.scene != ctx.scene) SceneManager.MoveGameObjectToScene(go, ctx.scene);
            Place(ctx, go, parentRef, nodeRef);
        }

        /// <summary>New objects (copies and added instances) once all of them exist, so one can be the parent of another.</summary>
        public static void PlaceCreated(BBCtx ctx)
        {
            foreach (var c in ctx.created.OrderBy(c => Depth(ctx, c.node)))
            {
                if (!c.go) continue;
                var p = ctx.Resolve(c.node.parent);
                if (c.go.transform.parent != p) Undo.SetTransformParent(c.go.transform, p, "Place");
                Place(ctx, c.go, c.node.parent, "tmp:" + c.node.tmp);
            }
        }

        static int Depth(BBCtx ctx, BBOutNode n)
        {
            int d = 0;
            for (var t = ctx.Resolve(n.parent); t && d < 64; t = t.parent) d++;
            return d;
        }

        // ================================================================ deleting
        /// <summary>Objects under a plain object that were not sent to Blender (content rules: lights, volumes, probes...).
        /// The Blender user never saw them, so deleting their parent there must not delete them. A prefab instance is
        /// different: what was added under it belongs to it and goes with it.</summary>
        public static List<Transform> HiddenChildren(BBPlan plan, GameObject go)
        {
            var list = new List<Transform>();
            if (go && !PrefabUtility.IsPartOfPrefabInstance(go)) CollectHidden(plan, go.transform, list);
            return list;
        }

        // through children Blender does show (they are deleted with it, possibly after it), down to the hidden ones
        static void CollectHidden(BBPlan plan, Transform t, List<Transform> list)
        {
            foreach (Transform c in t)
            {
                if (!plan.nodeObjects.Contains(c.gameObject)) list.Add(c);
                else if (!PrefabUtility.IsPartOfPrefabInstance(c.gameObject)) CollectHidden(plan, c, list);
            }
        }

        /// <summary>Moves the hidden children of an object Blender deleted up to its parent, keeping their world placement
        /// (as Blender does with the children of a deleted object).</summary>
        public static void KeepHiddenChildren(BBCtx ctx, GameObject go)
        {
            var hidden = HiddenChildren(ctx.plan, go);
            if (hidden.Count == 0) return;
            var parent = go.transform.parent;
            foreach (var c in hidden) Undo.SetTransformParent(c, parent, "Keep hidden child");
            ctx.log.Add($"{go.name} was deleted in Blender; {hidden.Count} object(s) under it that Blender doesn't show were kept: " +
                        string.Join(", ", hidden.Take(5).Select(c => c.name)) + (hidden.Count > 5 ? ", ..." : ""));
        }

        // ================================================================ new objects
        /// <summary>A Shift+D copy: Unity duplicates the original (prefab links, overrides, components kept). Children of the
        /// original that Blender knows as their own objects are removed from the copy: Blender sends them as their own copies
        /// if they were duplicated too, and not at all if they weren't.</summary>
        public static void Duplicate(BBCtx ctx, BBOutNode n, GameObject src, string asset, string kind)
        {
            var copy = DuplicateOf(src);
            if (!copy) { ctx.log.Add($"Copy of {src.name}: could not duplicate it"); return; }
            StripNodeChildren(ctx, src.transform, copy.transform);
            Register(ctx, n, copy, asset, kind);
        }

        static GameObject DuplicateOf(GameObject src)
        {
            var sel = Selection.objects;
            Selection.objects = new Object[] { src };
            Unsupported.DuplicateGameObjectsUsingPasteboard();
            var copy = Selection.activeGameObject;
            Selection.objects = sel;
            if (copy && copy != src) return copy;
            copy = PrefabUtility.IsOutermostPrefabInstanceRoot(src)
                ? (GameObject)PrefabUtility.InstantiatePrefab(PrefabUtility.GetCorrespondingObjectFromSource(src), src.transform.parent)
                : Object.Instantiate(src, src.transform.parent);
            Undo.RegisterCreatedObjectUndo(copy, "Copy");
            return copy;
        }

        static void StripNodeChildren(BBCtx ctx, Transform orig, Transform copy)
        {
            if (orig.childCount != copy.childCount) return;
            for (int i = orig.childCount - 1; i >= 0; i--)
            {
                var oc = orig.GetChild(i);
                var cc = copy.GetChild(i);
                if (ctx.plan.nodeObjects.Contains(oc.gameObject)) Undo.DestroyObjectImmediate(cc.gameObject);
                else StripNodeChildren(ctx, oc, cc);
            }
        }

        public static void Create(BBCtx ctx, BBOutNode n)
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
                string key = ctx.Final(n.asset.Substring(8));
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
            Register(ctx, n, go, asset, "instance");
        }

        static void Register(BBCtx ctx, BBOutNode n, GameObject go, string asset, string kind)
        {
            Undo.RecordObject(go, "Place");
            go.name = BBUtil.CleanName(n.name);
            ctx.byId["tmp:" + n.tmp] = go;
            ctx.created.Add((n, go, asset, kind));
        }

        // ================================================================ assets
        public static void ImportAsset(BBCtx ctx, BBOutAsset a)
        {
            var r = BBAssets.Import(a, ctx.plan.session, ctx.log, ctx.plan.backupDir);
            Record(ctx, a.key, r.key, r.prefab, r.model);
        }

        public static void Record(BBCtx ctx, string key, string finalKey, string prefab, string model)
        {
            ctx.assetKeys[key] = finalKey;
            if (prefab != null) { ctx.assetPrefabs[finalKey] = prefab; ctx.assetModels[finalKey] = model; }
        }

        /// <summary>A Unity object modelled in Blender (Make editable): it keeps its identity, components and materials and
        /// takes the new geometry. Plain mesh objects get the new mesh (and their MeshCollider follows); prefab instances
        /// switch to the new prefab, keeping their overrides.</summary>
        public static void Retarget(BBCtx ctx, BBOutAsset a)
        {
            string key = ctx.Final(a.key);
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
                return;   // its GlobalObjectId may change: Apply sends Blender the new one (applied.json remap)
            }
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(model).GetComponentsInChildren<MeshRenderer>(true);
            go.TryGetComponent<MeshFilter>(out var mf);
            var oldMesh = mf ? mf.sharedMesh : null;
            if (src.Length == 1 && mf)
            {
                var newMesh = src[0].GetComponent<MeshFilter>().sharedMesh;
                Undo.RecordObject(mf, "Blender geometry");
                mf.sharedMesh = newMesh;
                if (go.TryGetComponent<MeshRenderer>(out var mr))
                {
                    Undo.RecordObject(mr, "Blender geometry");
                    if (BBRegistry.Get(key)?.materialMode == "unity")
                    {
                        var previous = mr.sharedMaterials;
                        var materials = new Material[newMesh.subMeshCount];
                        for (int i = 0; i < materials.Length; i++) materials[i] = i < previous.Length ? previous[i] : BBMaterialWriter.UnityDefaultMaterial();
                        mr.sharedMaterials = materials;
                    }
                    else mr.sharedMaterials = src[0].sharedMaterials;
                }
                foreach (var mc in go.GetComponents<MeshCollider>())
                    if (!mc.sharedMesh || mc.sharedMesh == oldMesh) { Undo.RecordObject(mc, "Blender geometry"); mc.sharedMesh = newMesh; }
                return;
            }
            // several parts now: the new prefab goes under the object; the old visual and its collider are switched off
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, go.transform);
            Undo.RegisterCreatedObjectUndo(inst, "Blender geometry");
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;
            if (go.TryGetComponent<MeshRenderer>(out var old)) { Undo.RecordObject(old, "Blender geometry"); old.enabled = false; }
            foreach (var mc in go.GetComponents<MeshCollider>())
                if (mc.enabled && (!mc.sharedMesh || mc.sharedMesh == oldMesh)) { Undo.RecordObject(mc, "Blender geometry"); mc.enabled = false; }
        }
    }
}
