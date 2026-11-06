using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public static Dictionary<string, Material> Build(BBOutAsset a, string key, string root, string src, List<string> log)
        {
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
            foreach (var m in own)
                foreach (var (file, suffix) in new[] { (m.baseMap, "BC"), (m.normalMap, "N"), (m.maskMap, "MS"), (m.emissionMap, "E") })
                {
                    if (string.IsNullOrEmpty(file) || tex.ContainsKey(file)) continue;
                    var t = Texture(a, key, root, src, m, file, suffix, log);
                    if (t) tex[file] = t;
                }

            var lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            foreach (var m in own)
            {
                string path = $"{root}/Materials/M_{key}_{BBUtil.Slug(m.name)}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!mat) { mat = new Material(lit); AssetDatabase.CreateAsset(mat, path); }
                Configure(mat, m, tex, lit);
                EditorUtility.SetDirty(mat);
                mats[m.name] = mat;
            }
            AssetDatabase.SaveAssets();
            return mats;
        }

        static Texture2D Texture(BBOutAsset a, string key, string root, string src, BBOutMaterial m, string file, string suffix, List<string> log)
        {
            if (file.StartsWith("asset:"))
            {
                string ap = file.Substring(6);
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(ap);
                if (!t) { log.Add($"{a.name}: texture {ap} not found in the project"); return null; }
                if (suffix == "N" && AssetImporter.GetAtPath(ap) is TextureImporter eti && eti.textureType != TextureImporterType.NormalMap)
                    log.Add($"{a.name}: {ap} is used as a normal map but is not imported as one");
                return t;
            }
            string from = src + "/" + file;
            if (!File.Exists(from)) { log.Add($"{a.name}: texture {file} is missing from Blender's export"); return null; }
            BBAssets.EnsureFolder($"{root}/Textures/{key}");
            string dst = $"{root}/Textures/{key}/T_{key}_{BBUtil.Slug(m.name)}_{suffix}{Path.GetExtension(file).ToLowerInvariant()}";
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
            return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
        }

        /// <summary>Blender's Principled values to URP Lit. Blender colours are linear; material colours are stored gamma.
        /// Sets everything URP's own material editor would, so transparent materials are right without opening them.</summary>
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
