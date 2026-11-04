using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>
    /// Assets modelled in Blender, named the way the Project Validator expects. For key K, under
    /// &lt;root&gt; = the Asset root setting (default Assets/BlenderBridge/{scene}):
    ///   Meshes/SM_K.fbx, Materials/M_K_&lt;material&gt;.mat (URP Lit), Textures/K/T_K_&lt;material&gt;_BC|N|MS|E.png,
    ///   Prefabs/PF_K.prefab (a variant of the model: static, colliders, label "BlenderAsset"),
    ///   Source~/K.blend (the editable source the next Blender session uses; Unity ignores "~" folders).
    /// Textures that already live in the project are reused, not copied. Re-syncing an edited asset updates the same
    /// files in place, so every placed instance follows.
    /// </summary>
    public static class BBAssets
    {
        public const string Label = "BlenderAsset";
        public const string KeyLabel = "BBKey_";   // on any prefab whose geometry comes from Blender asset <key>

        public static string FindModel(string key) =>
            AssetDatabase.FindAssets("SM_" + key + " t:Model").Select(AssetDatabase.GUIDToAssetPath)
                         .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "SM_" + key && p.Contains("/Blender/Meshes/"));

        public static string KeyFromLabels(Object o) =>
            AssetDatabase.GetLabels(o).Where(l => l.StartsWith(KeyLabel)).Select(l => l.Substring(KeyLabel.Length)).FirstOrDefault();

        public static string SourceBlendForKey(string key)
        {
            string m = FindModel(key);
            return m == null ? "" : BBPaths.Full(Path.GetDirectoryName(Path.GetDirectoryName(m)).Replace('\\', '/') + "/Source~/" + key + ".blend");
        }

        static void AddLabels(Object o, params string[] add) => AssetDatabase.SetLabels(o, AssetDatabase.GetLabels(o).Union(add).ToArray());

        public static string KeyOf(string prefabPath)
        {
            string stem = Path.GetFileNameWithoutExtension(prefabPath);
            return stem.StartsWith("PF_") ? stem.Substring(3) : stem;
        }

        public static string SourceBlendFor(string prefabPath)
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(prefabPath)).Replace('\\', '/');
            return BBPaths.Full(root + "/Source~/" + KeyOf(prefabPath) + ".blend");
        }

        public static string FindPrefab(string key) =>
            AssetDatabase.FindAssets("l:" + Label + " t:Prefab").Select(AssetDatabase.GUIDToAssetPath)
                         .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "PF_" + key);

        static string Clean(string s)
        {
            s = BBUtil.Slug(s);
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s;
        }

        public static string Import(BBOutAsset a, BBSession s, out string key, List<string> log) => Import(a, s, out key, out _, log, null);

        public static string Import(BBOutAsset a, BBSession s, out string key, out string model, List<string> log, string backupDir)
        {
            model = null;
            key = Clean(a.key);
            string existing = FindModel(key);
            if (a.isNew && (existing != null || FindPrefab(key) != null))
            {
                string b = key;
                for (int i = 2; FindModel(key = b + "_" + i) != null || FindPrefab(key) != null; i++) { }
                existing = null;
            }
            string root = existing != null ? Path.GetDirectoryName(Path.GetDirectoryName(existing)).Replace('\\', '/') : s.assetRoot;
            foreach (var sub in new[] { "Meshes", "Materials", "Textures/" + key, "Prefabs" }) EnsureFolder(root + "/" + sub);
            Directory.CreateDirectory(BBPaths.Full(root + "/Source~"));
            string src = a.dir.Replace('\\', '/');

            // materials that came from Unity (Make editable) keep the real Unity material
            var mats = new Dictionary<string, Material>();
            foreach (var m in a.materials.Where(m => !string.IsNullOrEmpty(m.unity)))
            {
                var um = BBUtil.FindByKey<Material>(m.unity);
                if (um) mats[m.name] = um;
                else log.Add($"{a.name}: Unity material for {m.name} not found; making a URP Lit one");
            }

            // textures: reuse project textures, copy the rest under the project's naming (T_<key>_<material>_<map>)
            var tex = new Dictionary<string, Texture2D>();
            foreach (var m in a.materials.Where(m => !mats.ContainsKey(m.name)))
                foreach (var (file, suffix) in new[] { (m.baseMap, "BC"), (m.normalMap, "N"), (m.maskMap, "MS"), (m.emissionMap, "E") })
                {
                    if (string.IsNullOrEmpty(file) || tex.ContainsKey(file)) continue;
                    if (file.StartsWith("asset:"))
                    {
                        string ap = file.Substring(6);
                        var t = AssetDatabase.LoadAssetAtPath<Texture2D>(ap);
                        if (!t) { log.Add($"{a.name}: texture {ap} not found in the project"); continue; }
                        if (suffix == "N" && AssetImporter.GetAtPath(ap) is TextureImporter eti && eti.textureType != TextureImporterType.NormalMap)
                            log.Add($"{a.name}: {ap} is used as a normal map but is not imported as one");
                        tex[file] = t;
                        continue;
                    }
                    string from = src + "/" + file;
                    if (!File.Exists(from)) { log.Add($"{a.name}: texture {file} is missing from Blender's export"); continue; }
                    string dst = $"{root}/Textures/{key}/T_{key}_{Clean(m.name)}_{suffix}{Path.GetExtension(file).ToLowerInvariant()}";
                    File.Copy(from, BBPaths.Full(dst), true);
                    AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
                    if (AssetImporter.GetAtPath(dst) is TextureImporter ti)
                    {
                        bool n = suffix == "N", lin = suffix == "MS";
                        var type = n ? TextureImporterType.NormalMap : TextureImporterType.Default;
                        if (ti.textureType != type || ti.sRGBTexture == (n || lin))
                        {
                            ti.textureType = type;
                            ti.sRGBTexture = !(n || lin);
                            ti.SaveAndReimport();
                        }
                    }
                    tex[file] = AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
                }

            // new materials (URP Lit; Blender owns them, so a re-sync overwrites their settings)
            var lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            foreach (var m in a.materials.Where(m => !mats.ContainsKey(m.name)))
            {
                string path = $"{root}/Materials/M_{key}_{Clean(m.name)}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!mat) { mat = new Material(lit); AssetDatabase.CreateAsset(mat, path); }
                Configure(mat, m, tex, lit);
                EditorUtility.SetDirty(mat);
                mats[m.name] = mat;
            }
            AssetDatabase.SaveAssets();

            // model
            model = $"{root}/Meshes/SM_{key}.fbx";
            File.Copy(src + "/" + a.fbx, BBPaths.Full(model), true);
            AssetDatabase.ImportAsset(model, ImportAssetOptions.ForceUpdate);
            if (!(AssetImporter.GetAtPath(model) is ModelImporter mi)) { log.Add($"{a.name}: {model} did not import as a model"); return null; }
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
            mi.generateSecondaryUV = true;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            foreach (var kv in mats) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            mi.SaveAndReimport();

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
            if (mode == "source")
            {
                if (!string.IsNullOrEmpty(backupDir)) File.Copy(BBPaths.Full(original), Path.Combine(backupDir, Path.GetFileName(original)), true);
                ApplyParts(original, model, a, log);
                AddLabels(AssetDatabase.LoadAssetAtPath<GameObject>(original), KeyLabel + key);
                AssetDatabase.SaveAssets();
                return original;
            }
            string prefabPath = $"{root}/Prefabs/PF_{key}.prefab";
            if (mode == "variant" && original != null)
            {
                if (!File.Exists(BBPaths.Full(prefabPath)))
                {
                    var baseInst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(original));
                    try
                    {
                        baseInst.name = "PF_" + key;
                        PrefabUtility.SaveAsPrefabAsset(baseInst, prefabPath);  // saving an instance of the original = a Prefab Variant
                    }
                    finally { Object.DestroyImmediate(baseInst); }
                }
                ApplyParts(prefabPath, model, a, log);
                AddLabels(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), Label, KeyLabel + key);
                AssetDatabase.SaveAssets();
                return prefabPath;
            }

            // a new model: a variant of the model, so a re-import updates every instance
            if (!File.Exists(BBPaths.Full(prefabPath)))
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(model));
                try
                {
                    inst.name = "PF_" + key;
                    FixPartTransforms(inst, a);
                    Setup(inst, a.collider);
                    PrefabUtility.SaveAsPrefabAsset(inst, prefabPath);
                }
                finally { Object.DestroyImmediate(inst); }
            }
            else
            {
                var c = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    FixPartTransforms(c, a);
                    Setup(c, a.collider);
                    PrefabUtility.SaveAsPrefabAsset(c, prefabPath);
                }
                finally { PrefabUtility.UnloadPrefabContents(c); }
            }
            AddLabels(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), Label, KeyLabel + key);
            AssetDatabase.SaveAssets();
            return prefabPath;
        }

        /// <summary>Puts the Blender geometry into a prefab, part by part: each Blender object that came from a prefab part
        /// (its path inside the prefab) gives that part its new mesh, materials and position; new objects become new
        /// parts; parts deleted in Blender are switched off. Everything else in the prefab (components, other children)
        /// is untouched.</summary>
        static void ApplyParts(string prefabPath, string model, BBOutAsset a, List<string> log)
        {
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var modelGo = AssetDatabase.LoadAssetAtPath<GameObject>(model);
                var srcs = modelGo.GetComponentsInChildren<MeshFilter>(true);
                var toModel = modelGo.transform.worldToLocalMatrix;
                foreach (var part in a.parts.OrderBy(p => p.depth))
                {
                    var src = srcs.FirstOrDefault(f => f.name == part.@object) ?? (srcs.Length == 1 && a.parts.Count == 1 ? srcs[0] : null);
                    if (!src) { log.Add($"{a.name}: {part.@object} not found in the exported model"); continue; }
                    var rel = PartMatrix(part, toModel * src.transform.localToWorldMatrix);  // relative to the prefab root
                    Transform target = !part.known ? null : part.path == "" ? contents.transform : contents.transform.Find(part.path);
                    if (!target)
                    {
                        if (part.known) log.Add($"{a.name}: part {part.path} is no longer in the prefab; added it as a new part");
                        target = new GameObject(part.@object).transform;
                        target.SetParent(contents.transform, false);
                        BBConvert.SetWorld(target, contents.transform.localToWorldMatrix * rel);
                    }
                    else if (target != contents.transform)
                    {
                        var cur = contents.transform.worldToLocalMatrix * target.localToWorldMatrix;
                        if (!BBConvert.Near(BBConvert.Flat(cur), BBConvert.Flat(rel))) BBConvert.SetWorld(target, contents.transform.localToWorldMatrix * rel);
                    }
                    if (!target.TryGetComponent<MeshFilter>(out var mf)) mf = target.gameObject.AddComponent<MeshFilter>();
                    if (!target.TryGetComponent<MeshRenderer>(out var mr)) mr = target.gameObject.AddComponent<MeshRenderer>();
                    var old = mf.sharedMesh;
                    mf.sharedMesh = src.sharedMesh;
                    mr.enabled = true;
                    mr.sharedMaterials = src.GetComponent<MeshRenderer>().sharedMaterials;
                    foreach (var mc in target.GetComponents<MeshCollider>())
                        if (!mc.sharedMesh || mc.sharedMesh == old) mc.sharedMesh = src.sharedMesh;
                }
                foreach (var path in a.removed)
                {
                    var t = path == "" ? contents.transform : contents.transform.Find(path);
                    if (!t) continue;
                    if (t.TryGetComponent<MeshRenderer>(out var r)) r.enabled = false;
                    foreach (var mc in t.GetComponents<MeshCollider>()) mc.enabled = false;
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        /// <summary>A part's transform relative to the asset root, as Blender sent it (the FBX's own child transforms are
        /// not reliable), in Unity space.</summary>
        static Matrix4x4 PartMatrix(BBOutPart part, Matrix4x4 fallback) =>
            part.m != null && part.m.Length == 16 ? BBConvert.ToUnity(BBConvert.Unflat(part.m)) : fallback;

        /// <summary>Put each child mesh where Blender had it relative to the prefab pivot.</summary>
        static void FixPartTransforms(GameObject root, BBOutAsset a)
        {
            if (a.parts.Count == 1 && a.parts[0].depth == 0) return;
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var part in a.parts.OrderBy(p => p.depth))
            {
                var t = all.FirstOrDefault(x => x.name == part.@object);
                if (t && t != root.transform) BBConvert.SetWorld(t, root.transform.localToWorldMatrix * PartMatrix(part, Matrix4x4.identity));
            }
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        /// <summary>Static environment piece with the collider Blender asked for (custom property "collider" on the
        /// object: mesh (default), box or none).</summary>
        static void Setup(GameObject root, string collider)
        {
            const StaticEditorFlags flags = StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic |
                                            StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
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
            else if (collider != "none")
            {
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (!mf.TryGetComponent<MeshCollider>(out var mc)) mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }
            }
        }

        /// <summary>Blender's Principled values to URP Lit. Blender colours are linear; material colours are stored gamma.</summary>
        static void Configure(Material mat, BBOutMaterial m, Dictionary<string, Texture2D> tex, Shader lit)
        {
            if (lit && mat.shader != lit) mat.shader = lit;
            Texture2D T(string k) => !string.IsNullOrEmpty(k) && tex.TryGetValue(k, out var t) ? t : null;
            var c = m.color != null && m.color.Length >= 3 ? new Color(m.color[0], m.color[1], m.color[2], m.color.Length > 3 ? m.color[3] : 1f) : Color.white;
            var cg = c.gamma;
            cg.a = c.a;
            mat.SetColor("_BaseColor", cg);
            mat.SetColor("_Color", cg);
            var baseMap = T(m.baseMap);
            mat.SetTexture("_BaseMap", baseMap);
            mat.SetTexture("_MainTex", baseMap);

            var nrm = T(m.normalMap);
            mat.SetTexture("_BumpMap", nrm);
            mat.SetFloat("_BumpScale", m.normalScale);
            Keyword(mat, "_NORMALMAP", nrm);

            var mask = T(m.maskMap);
            mat.SetTexture("_MetallicGlossMap", mask);
            Keyword(mat, "_METALLICSPECGLOSSMAP", mask);
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
            mat.SetFloat("_Metallic", mask ? 1f : m.metallic);
            mat.SetFloat("_Smoothness", mask ? 1f : m.smoothness);

            var e = m.emission != null && m.emission.Length >= 3 ? new Color(m.emission[0], m.emission[1], m.emission[2]) : Color.black;
            var eMap = T(m.emissionMap);
            bool emissive = e.maxColorComponent > 0.0001f || eMap;
            mat.SetColor("_EmissionColor", emissive ? (eMap && e.maxColorComponent <= 0.0001f ? Color.white : e.gamma) : Color.black);
            mat.SetTexture("_EmissionMap", eMap);
            Keyword(mat, "_EMISSION", emissive);
            mat.globalIlluminationFlags = emissive ? MaterialGlobalIlluminationFlags.BakedEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;

            bool clip = m.alpha == "clip", blend = m.alpha == "blend";
            mat.SetFloat("_AlphaClip", clip ? 1f : 0f);
            mat.SetFloat("_Cutoff", m.cutoff);
            Keyword(mat, "_ALPHATEST_ON", clip);
            mat.SetFloat("_Surface", blend ? 1f : 0f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", blend ? (float)BlendMode.SrcAlpha : (float)BlendMode.One);
            mat.SetFloat("_DstBlend", blend ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
            mat.SetFloat("_ZWrite", blend ? 0f : 1f);
            Keyword(mat, "_SURFACE_TYPE_TRANSPARENT", blend);
            mat.SetOverrideTag("RenderType", blend ? "Transparent" : clip ? "TransparentCutout" : "Opaque");
            mat.renderQueue = blend ? (int)RenderQueue.Transparent : clip ? (int)RenderQueue.AlphaTest : -1;
        }

        static void Keyword(Material m, string k, bool on)
        {
            if (on) m.EnableKeyword(k); else m.DisableKeyword(k);
        }
    }
}
