using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Materials of a Blender-made asset: Unity materials the model came with are kept as they are; the rest
    /// become URP Lit materials (owned by Blender: a re-sync overwrites their settings). Textures already in the project
    /// are reused; the rest are copied in as T_&lt;key&gt;_&lt;material&gt;_&lt;map&gt;.</summary>
    internal static class BBMaterialWriter
    {
        static string Stem(string name) => BBRegistry.NormalizeName(Regex.Replace(name ?? "", @"\.(\d{3})$", m => "_" + int.Parse(m.Groups[1].Value).ToString("00")));
        public static Dictionary<string, Material> Build(BBOutAsset a, BBImportRoute route, string src, List<string> log)
        {
            if (route.materialMode == "unity") return UnityMaterials(a, route, log);
            // materials that came from Unity (Make editable) keep the real Unity material
            var mats = new Dictionary<string, Material>();
            foreach (var m in a.materials.Where(m => !string.IsNullOrEmpty(m.unity)))
            {
                var um = BBUtil.FindByKey<Material>(m.unity);
                if (um) mats[m.name] = um;
                else log.Add($"{a.name}: Unity material for {m.name} not found; making a URP Lit one");
            }
            var own = a.materials.Where(m => !mats.ContainsKey(m.name)).ToList();
            if (own.Count == 0) return mats;

            var tex = new Dictionary<string, Texture2D>();
            foreach (var m in own.Where(_ => route.syncTextures))
                foreach (var (file, suffix) in new[] { (m.baseMap, "BC"), (m.normalMap, "N"), (m.maskMap, "MS"), (m.emissionMap, "E") })
                {
                    if (string.IsNullOrEmpty(file) || tex.ContainsKey(file)) continue;
                    var t = Texture(a, route, src, m, file, suffix, log);
                    if (t) tex[file] = t;
                }

            var lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            foreach (var m in own)
            {
                string id = string.IsNullOrEmpty(m.id) ? m.name : m.id;
                var old = route.record.materials.FirstOrDefault(r => r.id == id || r.id == m.name);
                string suggested = $"{route.root}/Materials/M_{route.assetName}_{Stem(m.name)}.mat";
                string path = old != null ? BBRegistry.PathForGuid(old.guid, "Material " + m.name)
                    : BBRegistry.ResourcePath(route, false, id, suggested);
                BBAssets.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!mat) { mat = new Material(lit); AssetDatabase.CreateAsset(mat, path); }
                BBRegistry.Remember(route.record.materials, id, path);
                BBRegistry.SaveNow();
                Configure(mat, m, tex, lit, route.syncTextures);
                EditorUtility.SetDirty(mat);
                mats[m.name] = mat;
            }
            AssetDatabase.SaveAssets();
            return mats;
        }

        public static Material UnityDefaultMaterial() => UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.defaultMaterial
            ?? AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");

        /// <summary>Unity owns these assignments. Never create or configure a material or texture in this mode.</summary>
        static Dictionary<string, Material> UnityMaterials(BBOutAsset a, BBImportRoute route, List<string> log)
        {
            var mats = new Dictionary<string, Material>();
            if (AssetImporter.GetAtPath(route.model) is ModelImporter importer)
                foreach (var kv in importer.GetExternalObjectMap())
                    if (kv.Value is Material material) mats[kv.Key.name] = material;

            foreach (var m in a.materials.Where(m => !string.IsNullOrEmpty(m.unity)))
            {
                var material = BBUtil.FindByKey<Material>(m.unity);
                if (material && !mats.ContainsKey(m.name)) mats[m.name] = material;
            }

            string prefab = route.prefab;
            if (!File.Exists(BBPaths.Full(prefab)) && !string.IsNullOrEmpty(a.prefab) && a.prefab.StartsWith("prefab:"))
                prefab = AssetDatabase.GUIDToAssetPath(a.prefab.Substring(7));
            var original = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(route.model);
            if (original && model)
            {
                var sourceRenderers = model.GetComponentsInChildren<MeshRenderer>(true);
                // Existing prefab overrides take precedence over the old model's material remaps.
                foreach (var part in a.parts.Where(p => p.known))
                {
                    var target = part.path == "" ? original.transform : original.transform.Find(part.path);
                    var targetRenderer = target ? target.GetComponent<MeshRenderer>() : null;
                    var sourceRenderer = sourceRenderers.FirstOrDefault(r => r.name == part.@object)
                        ?? (sourceRenderers.Length == 1 && a.parts.Count == 1 ? sourceRenderers[0] : null);
                    if (!targetRenderer || !sourceRenderer) continue;
                    var sourceMaterials = sourceRenderer.sharedMaterials;
                    var targetMaterials = targetRenderer.sharedMaterials;
                    var oldMap = ((ModelImporter)AssetImporter.GetAtPath(route.model)).GetExternalObjectMap();
                    for (int i = 0; i < sourceMaterials.Length && i < targetMaterials.Length; i++)
                        if (targetMaterials[i])
                            foreach (var slot in oldMap.Where(kv => kv.Value == sourceMaterials[i])) mats[slot.Key.name] = targetMaterials[i];
                }
            }
            var fallback = UnityDefaultMaterial();
            foreach (var m in a.materials)
                if (!mats.ContainsKey(m.name))
                {
                    mats[m.name] = fallback;
                    log.Add($"{a.name}: material slot {m.name} uses Unity's default material; assign a Unity material on the prefab when ready");
                }
            return mats;
        }

        static Texture2D Texture(BBOutAsset a, BBImportRoute route, string src, BBOutMaterial m, string file, string suffix, List<string> log)
        {
            if (file.StartsWith("asset:"))
            {
                string ap = BBRegistry.ResolveTextureAlias(file.Substring(6));
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(ap);
                if (!t) throw new System.Exception($"{a.name}: referenced Unity texture {ap} is missing; restore it or repair its registry mapping.");
                if (suffix == "N" && AssetImporter.GetAtPath(ap) is TextureImporter eti && eti.textureType != TextureImporterType.NormalMap)
                    log.Add($"{a.name}: {ap} is used as a normal map but is not imported as one");
                return t;
            }
            string from = src + "/" + file;
            if (!File.Exists(from)) { log.Add($"{a.name}: texture {file} is missing from Blender's export"); return null; }
            string id = (string.IsNullOrEmpty(m.id) ? m.name : m.id) + "|" + suffix;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string suggested = $"{route.root}/Textures/T_{route.assetName}_{Stem(m.name)}_{suffix}{ext}";
            string alias = $"legacy:T_{route.key}_{BBUtil.Slug(m.name)}_{suffix}{ext}";
            string dst = BBRegistry.ResourcePath(route, true, id, suggested, alias);
            BBAssets.EnsureFolder(Path.GetDirectoryName(dst).Replace('\\', '/'));
            File.Copy(from, BBPaths.Full(dst), true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            BBRegistry.Remember(route.record.textures, id, dst);
            BBRegistry.SaveNow();
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
            return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
        }

        /// <summary>Blender's Principled values to URP Lit. Blender colours are linear; material colours are stored gamma.
        /// Sets everything URP's own material editor would, so transparent materials are right without opening them.</summary>
        static void Configure(Material mat, BBOutMaterial m, Dictionary<string, Texture2D> tex, Shader lit, bool syncTextures)
        {
            if (lit && mat.shader != lit && syncTextures) mat.shader = lit;
            Texture2D T(string k) => !string.IsNullOrEmpty(k) && tex.TryGetValue(k, out var t) ? t : null;
            var c = m.color != null && m.color.Length >= 3 ? new Color(m.color[0], m.color[1], m.color[2], m.color.Length > 3 ? m.color[3] : 1f) : Color.white;
            var cg = c.gamma;
            cg.a = c.a;
            mat.SetColor("_BaseColor", cg);
            mat.SetColor("_Color", cg);
            var baseMap = T(m.baseMap);
            if (syncTextures)
            {
                mat.SetTexture("_BaseMap", baseMap);
                mat.SetTexture("_MainTex", baseMap);
            }

            var nrm = T(m.normalMap);
            if (syncTextures) mat.SetTexture("_BumpMap", nrm);
            mat.SetFloat("_BumpScale", m.normalScale);
            if (syncTextures) Keyword(mat, "_NORMALMAP", nrm);

            var mask = T(m.maskMap);
            if (syncTextures)
            {
                mat.SetTexture("_MetallicGlossMap", mask);
                Keyword(mat, "_METALLICSPECGLOSSMAP", mask);
            }
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
            mat.SetFloat("_Metallic", mask ? 1f : m.metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", mask ? 1f : m.smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", mask ? 1f : m.smoothness);

            var e = m.emission != null && m.emission.Length >= 3 ? new Color(m.emission[0], m.emission[1], m.emission[2]) : Color.black;
            Texture eMap = syncTextures ? T(m.emissionMap) : (mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null);
            bool emissive = e.maxColorComponent > 0.0001f || eMap;
            mat.SetColor("_EmissionColor", emissive ? (eMap && e.maxColorComponent <= 0.0001f ? Color.white : e.gamma) : Color.black);
            if (syncTextures) mat.SetTexture("_EmissionMap", eMap);
            Keyword(mat, "_EMISSION", emissive);
            mat.globalIlluminationFlags = emissive ? MaterialGlobalIlluminationFlags.BakedEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;

            bool clip = m.alpha == "clip", blend = m.alpha == "blend";
            mat.SetFloat("_AlphaClip", clip ? 1f : 0f);
            mat.SetFloat("_Cutoff", m.cutoff);
            Keyword(mat, "_ALPHATEST_ON", clip);
            mat.SetFloat("_Surface", blend ? 1f : 0f);
            mat.SetFloat("_Blend", 0f);   // alpha
            mat.SetFloat("_SrcBlend", blend ? (float)BlendMode.SrcAlpha : (float)BlendMode.One);
            mat.SetFloat("_DstBlend", blend ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
            mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", blend ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
            mat.SetFloat("_ZWrite", blend ? 0f : 1f);
            mat.SetFloat("_AlphaToMask", 0f);
            Keyword(mat, "_SURFACE_TYPE_TRANSPARENT", blend);
            Keyword(mat, "_ALPHAPREMULTIPLY_ON", false);
            Keyword(mat, "_ALPHAMODULATE_ON", false);
            mat.SetShaderPassEnabled("DepthOnly", !blend);
            mat.SetShaderPassEnabled("DepthNormals", !blend);
            mat.SetOverrideTag("RenderType", blend ? "Transparent" : clip ? "TransparentCutout" : "Opaque");
            mat.renderQueue = blend ? (int)RenderQueue.Transparent : clip ? (int)RenderQueue.AlphaTest : -1;
        }

        static void Keyword(Material m, string k, bool on)
        {
            if (on) m.EnableKeyword(k); else m.DisableKeyword(k);
        }
    }
}
