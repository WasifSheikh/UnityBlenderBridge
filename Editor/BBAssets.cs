using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using UMesh = UnityEngine.Mesh;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>
    /// Assets modelled in Blender. For key K, under &lt;root&gt; = the Asset root setting:
    ///   Meshes/SM_K.fbx, Materials/M_K_&lt;material&gt;.mat (URP Lit), Textures/K/T_K_&lt;material&gt;_BC|N|MS|E.png,
    ///   Prefabs/PF_K.prefab (a variant of the model: static, colliders, label "BlenderAsset"),
    ///   Source~/K.blend (the editable source the next Blender session uses; Unity ignores "~" folders).
    /// The model and every prefab whose geometry comes from K carry the label "BBKey_K", which is how they are found
    /// again (wherever the asset root was when they were made). Re-syncing an edited asset updates the same files in
    /// place, so every placed instance follows.
    /// </summary>
    public static class BBAssets
    {
        public const string Label = "BlenderAsset";
        public const string KeyLabel = "BBKey_";            // on the model and any prefab whose geometry comes from Blender asset <key>
        public const string SourceCollection = "AA_Asset";  // aa_bridge/export.py SOURCE_COLLECTION

        // ================================================================ visible parts of a prefab
        public struct Part { public Transform t; public UMesh mesh; public Material[] materials; public Matrix4x4 blenderMatrix; public string path; }

        public static bool MeshOf(GameObject go, out UMesh mesh, out Material[] materials)
        {
            mesh = null; materials = null;
            if (go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh && go.TryGetComponent<MeshRenderer>(out var mr) && mr.enabled)
            { mesh = mf.sharedMesh; materials = mr.sharedMaterials; return true; }
            if (go.TryGetComponent<SkinnedMeshRenderer>(out var smr) && smr.sharedMesh && smr.enabled)
            { mesh = smr.sharedMesh; materials = smr.sharedMaterials; return true; }
            return false;
        }

        /// <summary>Renderers only in LOD1+ (Blender shows LOD0).</summary>
        public static void HideLods(LODGroup lg, HashSet<GameObject> hidden)
        {
            var lods = lg.GetLODs();
            if (lods.Length < 2) return;
            var keep = new HashSet<Renderer>(lods[0].renderers.Where(r => r));
            for (int i = 1; i < lods.Length; i++)
                foreach (var r in lods[i].renderers)
                    if (r && !keep.Contains(r)) hidden.Add(r.gameObject);
        }

        static bool ActiveUnder(Transform t, Transform root)
        {
            for (; t; t = t.parent)
            {
                if (t == root) return true;
                if (!t.gameObject.activeSelf) return false;
            }
            return true;
        }

        public static List<Part> VisibleParts(GameObject root)
        {
            var hidden = new HashSet<GameObject>();
            foreach (var g in root.GetComponentsInChildren<LODGroup>(true)) HideLods(g, hidden);
            var inv = root.transform.worldToLocalMatrix;
            var parts = new List<Part>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (hidden.Contains(t.gameObject) || !ActiveUnder(t, root.transform)) continue;
                if (!MeshOf(t.gameObject, out var mesh, out var materials)) continue;
                parts.Add(new Part
                {
                    t = t, mesh = mesh, materials = materials, blenderMatrix = BBConvert.ToBlender(inv * t.localToWorldMatrix),
                    path = t == root.transform ? "" : AnimationUtility.CalculateTransformPath(t, root.transform),
                });
            }
            return parts;
        }

        /// <summary>Stable key of a mesh: "guid_localId" for assets, "scene_&lt;content hash&gt;" for meshes that live in a scene.</summary>
        public static string MeshId(UMesh mesh, out string hash)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long lid))
            {
                hash = path.StartsWith("Assets") || path.StartsWith("Packages") ? AssetDatabase.GetAssetDependencyHash(path).ToString() : "builtin";
                return guid + "_" + lid;
            }
            hash = BBMeshWriter.ContentHash(mesh);
            return "scene_" + BBUtil.Short(hash, 16);
        }

        /// <summary>What a prefab looks like (meshes, materials, layout): recorded when Blender writes into a prefab, so a
        /// later change made in Unity is noticed and Blender gets Unity's version instead of an out-of-date source.</summary>
        public static string PartsSignature(GameObject root)
        {
            var h = new Hash128();
            foreach (var p in VisibleParts(root))
            {
                h.Append(p.path);
                h.Append(p.mesh ? MeshIdQuick(p.mesh) : "");
                foreach (var m in p.materials) h.Append(m && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string g, out long l) ? g + l : "-");
                for (int i = 0; i < 16; i++) h.Append(Mathf.Round(p.blenderMatrix[i] * 1000f));
            }
            return h.ToString();
        }

        static string MeshIdQuick(UMesh mesh) =>
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long lid) && !string.IsNullOrEmpty(guid) ? guid + "_" + lid : mesh.name;

        // ================================================================ finding Blender-made assets again
        public static string KeyFromLabels(GUID guid) => FromLabels(AssetDatabase.GetLabels(guid));
        public static string KeyFromLabels(Object o) => FromLabels(AssetDatabase.GetLabels(o));
        static string FromLabels(string[] labels) => labels?.Where(l => l.StartsWith(KeyLabel)).Select(l => l.Substring(KeyLabel.Length)).FirstOrDefault();

        static string ByName(string search, string fileName, string type) =>
            AssetDatabase.FindAssets(search + " t:" + type).Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == fileName);

        /// <summary>SM_&lt;key&gt; model: by label first; models made before labels by their folder layout (…/Meshes/SM_key).</summary>
        public static string FindModel(string key) =>
            ByName("l:" + KeyLabel + key, "SM_" + key, "Model")
            ?? AssetDatabase.FindAssets("SM_" + key + " t:Model").Select(AssetDatabase.GUIDToAssetPath)
                            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "SM_" + key && p.Replace('\\', '/').Contains("/Meshes/"));

        public static string FindPrefab(string key) =>
            ByName("l:" + Label, "PF_" + key, "Prefab")
            ?? AssetDatabase.FindAssets("l:" + KeyLabel + key + " t:Prefab").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();

        public static string SourceBlendForKey(string key)
        {
            string m = FindModel(key);
            return m == null ? "" : BBPaths.Full(Path.GetDirectoryName(Path.GetDirectoryName(m)).Replace('\\', '/') + "/Source~/" + key + ".blend");
        }

        /// <summary>The Blender asset key a mesh belongs to, when it is a mesh of a Blender-made model.</summary>
        public static string KeyOfModelMesh(UMesh mesh)
        {
            string p = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(p) || !Path.GetFileName(p).StartsWith("SM_") || !p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return null;
            string key = KeyFromLabels(AssetDatabase.LoadMainAssetAtPath(p)) ?? Path.GetFileNameWithoutExtension(p).Substring(3);
            return FindModel(key) == p ? key : null;
        }

        static bool KeyTaken(string key) => FindModel(key) != null || FindPrefab(key) != null;

        public static BBAssetMeta ReadMeta(string prefabPath)
        {
            var imp = AssetImporter.GetAtPath(prefabPath);
            if (imp == null || string.IsNullOrEmpty(imp.userData) || !imp.userData.TrimStart().StartsWith("{")) return null;
            try { return JsonUtility.FromJson<BBAssetMeta>(imp.userData); } catch { return null; }
        }

        static void WriteMeta(string prefabPath, BBAssetMeta meta)
        {
            var imp = AssetImporter.GetAtPath(prefabPath);
            if (imp == null) return;
            string json = JsonUtility.ToJson(meta);
            if (imp.userData == json) return;
            imp.userData = json;
            EditorUtility.SetDirty(imp);
            AssetDatabase.WriteImportSettingsIfDirty(prefabPath);
        }

        static void AddLabels(Object o, params string[] add)
        {
            if (!o) return;
            var cur = AssetDatabase.GetLabels(o);
            if (add.All(cur.Contains)) return;
            AssetDatabase.SetLabels(o, cur.Union(add).ToArray());
        }

        // ================================================================ import of one Blender asset
        public class Imported { public string key, prefab, model; }

        public static Imported Import(BBOutAsset a, BBSession s, List<string> log, string backupDir)
        {
            string key = BBUtil.Slug(a.key);
            string existing = FindModel(key);
            if (a.isNew && KeyTaken(key))
            {
                string b = key;
                for (int i = 2; KeyTaken(key = b + "_" + i); i++) { }
                existing = null;
            }
            string root = existing != null ? Path.GetDirectoryName(Path.GetDirectoryName(existing)).Replace('\\', '/') : s.assetRoot;
            foreach (var sub in new[] { "Meshes", "Materials", "Prefabs" }) EnsureFolder(root + "/" + sub);
            Directory.CreateDirectory(BBPaths.Full(root + "/Source~"));
            string src = a.dir.Replace('\\', '/');

            var mats = BBMaterialWriter.Build(a, key, root, src, log);

            // model
            string model = $"{root}/Meshes/SM_{key}.fbx";
            File.Copy(src + "/" + a.fbx, BBPaths.Full(model), true);
            AssetDatabase.ImportAsset(model, ImportAssetOptions.ForceUpdate);
            if (!(AssetImporter.GetAtPath(model) is ModelImporter mi)) throw new Exception($"{model} did not import as a model");
            mi.globalScale = 1f;
            mi.useFileScale = true;
            mi.bakeAxisConversion = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.importAnimation = false;
            mi.importBlendShapes = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.addCollider = false;
            mi.isReadable = false;
            mi.generateSecondaryUV = !a.hasUv2;   // keep a lightmap UV made in Blender
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var kv in mats) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            mi.SaveAndReimport();
            AddLabels(AssetDatabase.LoadMainAssetAtPath(model), KeyLabel + key);

            // editable source for the next Blender session
            if (!string.IsNullOrEmpty(a.source) && File.Exists(src + "/" + a.source))
                File.Copy(src + "/" + a.source, BBPaths.Full($"{root}/Source~/{key}.blend"), true);

            // a modelled prefab: into the prefab itself (all copies) or into a Prefab Variant of it (this instance)
            string mode = a.mode ?? "";
            string original = !string.IsNullOrEmpty(a.prefab) && a.prefab.StartsWith("prefab:") ? AssetDatabase.GUIDToAssetPath(a.prefab.Substring(7)) : null;
            if (string.IsNullOrEmpty(original)) original = null;
            if (mode == "source" && (original == null || !original.StartsWith("Assets/") || !original.EndsWith(".prefab")))
            {
                log.Add($"{a.name}: {original ?? "its prefab"} is a model file or read-only, so it can't be edited directly; made a variant for this instance instead");
                mode = "variant";
            }

            string prefabPath;
            if (mode == "source")
            {
                if (!string.IsNullOrEmpty(backupDir)) File.Copy(BBPaths.Full(original), Path.Combine(backupDir, Path.GetFileName(original)), true);
                BBPrefabBuilder.ApplyParts(original, model, a, log);
                prefabPath = original;
                AddLabels(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), KeyLabel + key);
            }
            else if (mode == "variant" && original != null)
            {
                prefabPath = $"{root}/Prefabs/PF_{key}.prefab";
                if (!File.Exists(BBPaths.Full(prefabPath))) BBPrefabBuilder.SaveVariant(original, prefabPath, "PF_" + key);
                BBPrefabBuilder.ApplyParts(prefabPath, model, a, log);
                AddLabels(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), Label, KeyLabel + key);
            }
            else
            {
                // a new model: a variant of the model, so a re-import updates every instance
                prefabPath = $"{root}/Prefabs/PF_{key}.prefab";
                var meta = ReadMeta(prefabPath);
                BBPrefabBuilder.BuildModelPrefab(model, prefabPath, "PF_" + key, a, meta?.collider ?? "mesh");
                AddLabels(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), Label, KeyLabel + key);
            }
            AssetDatabase.SaveAssets();
            WriteMeta(prefabPath, new BBAssetMeta { collider = a.collider, parts = PartsSignature(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)) });
            return new Imported { key = key, prefab = prefabPath, model = model };
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
