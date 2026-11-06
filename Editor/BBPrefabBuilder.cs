using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Puts Blender geometry into prefabs: the model prefab of a new asset (static, colliders), or an existing
    /// prefab / its variant, part by part.</summary>
    internal static class BBPrefabBuilder
    {
        const StaticEditorFlags EnvFlags = StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic |
                                           StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;

        /// <summary>Saving an instance of a prefab as a new prefab makes a Prefab Variant of it.</summary>
        public static void SaveVariant(string basePrefab, string path, string name)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(basePrefab));
            try { inst.name = name; PrefabUtility.SaveAsPrefabAsset(inst, path); }
            finally { Object.DestroyImmediate(inst); }
        }

        /// <summary>PF_key: a variant of the model, static, with the collider Blender asked for. previousCollider is what
        /// the last sync set up, so switching e.g. mesh -> box removes the mesh colliders it added.</summary>
        public static void BuildModelPrefab(string model, string prefabPath, string name, BBOutAsset a, string previousCollider, Dictionary<string, Material[]> preservedMaterials = null)
        {
            if (!System.IO.File.Exists(BBPaths.Full(prefabPath)))
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(model));
                try
                {
                    inst.name = name;
                    FixPartTransforms(inst, a);
                    Setup(inst, a.collider, "none");
                    RestoreMaterials(inst, preservedMaterials);
                    PrefabUtility.SaveAsPrefabAsset(inst, prefabPath);
                }
                finally { Object.DestroyImmediate(inst); }
                return;
            }
            var c = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                FixPartTransforms(c, a);
                Setup(c, a.collider, previousCollider);
                RestoreMaterials(c, preservedMaterials);
                PrefabUtility.SaveAsPrefabAsset(c, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(c); }
        }

        /// <summary>Each Blender object that came from a prefab part (its path inside the prefab) gives that part its new
        /// mesh, materials and position; new objects become new parts (static and with a collider when the prefab's
        /// parts have them); parts deleted in Blender are switched off; LOD groups keep only the edited LOD0. Everything
        /// else in the prefab (components, other children) is untouched.</summary>
        public static void ApplyParts(string prefabPath, string model, BBOutAsset a, List<string> log, bool keepUnityMaterials = false, Dictionary<string, Material[]> preservedMaterials = null)
        {
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var modelGo = AssetDatabase.LoadAssetAtPath<GameObject>(model);
                var srcs = modelGo.GetComponentsInChildren<MeshFilter>(true);
                var toModel = modelGo.transform.worldToLocalMatrix;
                bool collidersInPrefab = contents.GetComponentsInChildren<MeshCollider>(true).Length > 0;
                var flags = GameObjectUtility.GetStaticEditorFlags(contents);
                var changed = new HashSet<Transform>();
                foreach (var part in a.parts.OrderBy(p => p.depth))
                {
                    var src = srcs.FirstOrDefault(f => f.name == part.@object) ?? (srcs.Length == 1 && a.parts.Count == 1 ? srcs[0] : null);
                    if (!src) { log.Add($"{a.name}: {part.@object} not found in the exported model"); continue; }
                    var rel = PartMatrix(part, toModel * src.transform.localToWorldMatrix);  // relative to the prefab root
                    Transform target = !part.known ? null : part.path == "" ? contents.transform : contents.transform.Find(part.path);
                    bool added = false;
                    if (!target)
                    {
                        if (part.known) log.Add($"{a.name}: part {part.path} is no longer in the prefab; added it as a new part");
                        target = new GameObject(part.@object).transform;
                        target.SetParent(contents.transform, false);
                        BBConvert.SetWorld(target, contents.transform.localToWorldMatrix * rel);
                        GameObjectUtility.SetStaticEditorFlags(target.gameObject, flags);
                        added = true;
                    }
                    else if (target != contents.transform)
                    {
                        var cur = contents.transform.worldToLocalMatrix * target.localToWorldMatrix;
                        if (!BBConvert.Near(cur, rel)) BBConvert.SetWorld(target, contents.transform.localToWorldMatrix * rel);
                    }
                    if (!target.TryGetComponent<MeshFilter>(out var mf)) mf = target.gameObject.AddComponent<MeshFilter>();
                    if (!target.TryGetComponent<MeshRenderer>(out var mr)) mr = target.gameObject.AddComponent<MeshRenderer>();
                    var old = mf.sharedMesh;
                    mf.sharedMesh = src.sharedMesh;
                    mr.enabled = true;
                    if (!keepUnityMaterials || added) mr.sharedMaterials = src.GetComponent<MeshRenderer>().sharedMaterials;
                    foreach (var mc in target.GetComponents<MeshCollider>())
                        if (!mc.sharedMesh || mc.sharedMesh == old) mc.sharedMesh = src.sharedMesh;
                    if (added && collidersInPrefab) target.gameObject.AddComponent<MeshCollider>().sharedMesh = src.sharedMesh;
                    changed.Add(target);
                }
                foreach (var path in a.removed)
                {
                    var t = path == "" ? contents.transform : contents.transform.Find(path);
                    if (!t) continue;
                    if (t.TryGetComponent<MeshRenderer>(out var r)) r.enabled = false;
                    foreach (var mc in t.GetComponents<MeshCollider>()) mc.enabled = false;
                }
                CollapseLods(contents, changed, a, log);
                RestoreMaterials(contents, preservedMaterials);
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        public static Dictionary<string, Material[]> CaptureMaterials(string prefabPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            return !root ? null : root.GetComponentsInChildren<Renderer>(true)
                .GroupBy(r => AnimationUtility.CalculateTransformPath(r.transform, root.transform))
                .ToDictionary(g => g.Key, g => g.First().sharedMaterials);
        }

        static void RestoreMaterials(GameObject root, Dictionary<string, Material[]> materials)
        {
            if (materials == null) return;
            foreach (var kv in materials)
            {
                var target = kv.Key == "" ? root.transform : root.transform.Find(kv.Key);
                if (!target || !target.TryGetComponent<Renderer>(out var renderer)) continue;
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : target.GetComponent<MeshFilter>()?.sharedMesh;
                int count = mesh ? mesh.subMeshCount : kv.Value.Length;
                var current = renderer.sharedMaterials;
                var restored = new Material[count];
                for (int i = 0; i < count; i++) restored[i] = i < kv.Value.Length ? kv.Value[i]
                    : i < current.Length && current[i] ? current[i] : BBMaterialWriter.UnityDefaultMaterial();
                renderer.sharedMaterials = restored;
            }
        }

        /// <summary>A LOD group whose LOD0 was remodelled would still switch to the old LOD1+ meshes at distance: it keeps
        /// only LOD0 (same cull distance) and the lower LODs are switched off.</summary>
        static void CollapseLods(GameObject contents, HashSet<Transform> changed, BBOutAsset a, List<string> log)
        {
            foreach (var lg in contents.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lg.GetLODs();
                if (lods.Length < 2 || !lods[0].renderers.Any(r => r && changed.Contains(r.transform))) continue;
                var keep = new HashSet<Renderer>(lods[0].renderers.Where(r => r));
                for (int i = 1; i < lods.Length; i++)
                    foreach (var r in lods[i].renderers)
                        if (r && !keep.Contains(r)) r.enabled = false;
                lg.SetLODs(new[] { new LOD(lods[lods.Length - 1].screenRelativeTransitionHeight, lods[0].renderers) });
                log.Add($"{a.name}: {lg.name} had {lods.Length} LOD levels; it now uses the Blender LOD0 only (the old lower LODs are switched off)");
            }
        }

        /// <summary>A part's transform relative to the asset root, as Blender sent it (the FBX's own child transforms are
        /// not reliable), in Unity space.</summary>
        static Matrix4x4 PartMatrix(BBOutPart part, Matrix4x4 fallback) =>
            part.m != null && part.m.Length == 16 ? BBConvert.ToUnity(BBConvert.Unflat(part.m)) : fallback;

        /// <summary>Put each child mesh where Blender had it relative to the prefab pivot.</summary>
        static void FixPartTransforms(GameObject root, BBOutAsset a)
        {
            if (a.parts.Count == 1 && a.parts[0].depth == 0) return;
            var byName = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (!byName.ContainsKey(t.name)) byName[t.name] = t;
            foreach (var part in a.parts.OrderBy(p => p.depth))
                if (byName.TryGetValue(part.@object, out var t) && t != root.transform)
                    BBConvert.SetWorld(t, root.transform.localToWorldMatrix * PartMatrix(part, Matrix4x4.identity));
        }

        /// <summary>Static environment piece with the collider Blender asked for (custom property "collider" on the object:
        /// mesh (default), box or none). Colliders of the previous kind are removed when the kind changes.</summary>
        static void Setup(GameObject root, string collider, string previous)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, EnvFlags);
            collider = string.IsNullOrEmpty(collider) ? "mesh" : collider;
            if (previous != collider)
            {
                if (previous == "mesh")
                    foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true)) if (mc.GetComponent<MeshFilter>()) Object.DestroyImmediate(mc);
                if (previous == "box" && root.TryGetComponent<BoxCollider>(out var oldBox)) Object.DestroyImmediate(oldBox);
            }
            if (collider == "box")
            {
                var rs = root.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0) return;
                var b = new Bounds(root.transform.InverseTransformPoint(rs[0].bounds.center), Vector3.zero);
                foreach (var r in rs)
                {
                    var bb = r.bounds;
                    for (int i = 0; i < 8; i++)
                        b.Encapsulate(root.transform.InverseTransformPoint(new Vector3(
                            (i & 1) == 0 ? bb.min.x : bb.max.x, (i & 2) == 0 ? bb.min.y : bb.max.y, (i & 4) == 0 ? bb.min.z : bb.max.z)));
                }
                if (!root.TryGetComponent<BoxCollider>(out var bc)) bc = root.AddComponent<BoxCollider>();
                bc.center = b.center;
                bc.size = b.size;
            }
            else if (collider == "mesh")
            {
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (!mf.TryGetComponent<MeshCollider>(out var mc)) mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }
            }
        }
    }
}
