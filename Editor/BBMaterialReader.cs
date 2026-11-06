using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Unity materials as a viewport approximation Blender can draw (any shader: base colour/texture, normal,
    /// metal/smooth, emission, alpha). Textures Blender can't read, or generated ones, are baked to PNG once.</summary>
    internal class BBMaterialReader
    {
        static readonly HashSet<string> BlenderReadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".exr", ".bmp", ".hdr", ".psd", ".dds", ".webp" };

        readonly Dictionary<Texture, string> texPaths = new Dictionary<Texture, string>();
        readonly List<string> warnings;
        public int baked;

        public BBMaterialReader(List<string> warnings) { this.warnings = warnings; }

        public static string KeyOf(Material m) =>
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string guid, out long lid) && !string.IsNullOrEmpty(guid)
                ? guid + "_" + lid
                : "inst_" + BBUtil.Short(Hash128.Compute(m.name + "|" + (m.shader ? m.shader.name : "") + "|" + m.GetInstanceID()).ToString());

        public BBMaterial Describe(Material m, string key)
        {
            var d = new BBMaterial { key = key, name = m.name, shader = m.shader ? m.shader.name : "" };
            var col = FirstColor(m, "_BaseColor", "_Color", "_MainColor", "_TintColor", "_Tint", "_AlbedoColor") ?? Color.white;
            d.color = new[] { col.r, col.g, col.b, col.a };
            var bt = FirstTex(m, out string prop, "_BaseMap", "_MainTex", "_BaseColorMap", "_AlbedoMap", "_Albedo", "_DiffuseMap", "_Diffuse")
                     ?? ScanTex(m, out prop, "albedo", "basecolor", "base", "diffuse", "main", "color");
            d.baseMap = TexPath(bt, false);
            if (bt && prop != null)
            {
                var s = m.GetTextureScale(prop); var o = m.GetTextureOffset(prop);
                d.tiling = new[] { s.x, s.y, o.x, o.y };
            }
            else d.tiling = new[] { 1f, 1f, 0f, 0f };
            var nt = FirstTex(m, out _, "_BumpMap", "_NormalMap", "_Normal", "_NormalTex") ?? ScanTex(m, out _, "normal", "bump");
            d.normalMap = IsHeightNormal(nt) ? "" : TexPath(nt, true);
            d.normalScale = Float(m, 1f, "_BumpScale", "_NormalScale", "_NormalStrength");
            d.metallic = Float(m, 0f, "_Metallic");
            d.smoothness = Float(m, 0.5f, "_Smoothness", "_Glossiness");
            var ec = m.HasColor("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black;
            bool emissive = ec.maxColorComponent > 0.001f && (m.IsKeywordEnabled("_EMISSION") || d.shader != "Universal Render Pipeline/Lit");
            d.emission = emissive ? new[] { ec.r, ec.g, ec.b } : new[] { 0f, 0f, 0f };
            d.emissionMap = emissive ? TexPath(FirstTex(m, out _, "_EmissionMap", "_EmissiveColorMap", "_EmissionTex"), false) : "";
            if (Float(m, 0f, "_Surface") > 0.5f) d.alpha = "blend";
            else if (Float(m, 0f, "_AlphaClip") > 0.5f || m.IsKeywordEnabled("_ALPHATEST_ON")) { d.alpha = "clip"; d.cutoff = Float(m, 0.5f, "_Cutoff"); }
            return d;
        }

        /// <summary>A normal map Unity generates from a greyscale height file: the file itself is not a normal map.</summary>
        static bool IsHeightNormal(Texture t) =>
            t && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) is TextureImporter ti && ti.textureType == TextureImporterType.NormalMap && ti.convertToNormalmap;

        static Color? FirstColor(Material m, params string[] props)
        {
            foreach (var p in props) if (m.HasColor(p)) return m.GetColor(p);
            return null;
        }

        static float Float(Material m, float fallback, params string[] props)
        {
            foreach (var p in props) if (m.HasFloat(p)) return m.GetFloat(p);
            return fallback;
        }

        static Texture FirstTex(Material m, out string prop, params string[] props)
        {
            foreach (var p in props)
                if (m.HasTexture(p) && m.GetTexture(p) is Texture2D t) { prop = p; return t; }
            prop = null;
            return null;
        }

        static Texture ScanTex(Material m, out string prop, params string[] tokens)
        {
            prop = null;
            var sh = m.shader;
            if (!sh) return null;
            int count = sh.GetPropertyCount();
            foreach (var tok in tokens)
                for (int i = 0; i < count; i++)
                {
                    if (sh.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    string n = sh.GetPropertyName(i);
                    if (n.IndexOf(tok, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (m.GetTexture(n) is Texture2D t) { prop = n; return t; }
                }
            return null;
        }

        string TexPath(Texture t, bool normal)
        {
            if (!(t is Texture2D t2)) return "";
            if (texPaths.TryGetValue(t, out var p)) return p;
            p = "";
            string ap = AssetDatabase.GetAssetPath(t);
            if (BlenderReadable.Contains(Path.GetExtension(ap)))
            {
                if (ap.StartsWith("Assets/")) p = BBPaths.Full(ap);
                else if (ap.StartsWith("Packages/")) { var full = FileUtil.GetPhysicalPath(ap); if (File.Exists(full)) p = Path.GetFullPath(full).Replace('\\', '/'); }
            }
            if (p == "" && !normal) p = Bake(t2, ap);  // Blender can't read it (or it's generated): bake a PNG once
            texPaths[t] = p;
            return p;
        }

        string Bake(Texture2D t, string ap)
        {
            string hash = string.IsNullOrEmpty(ap) || ap.StartsWith("Library") || ap.StartsWith("Resources")
                ? Hash128.Compute(t.name + "|" + t.width + "x" + t.height + "|" + t.imageContentsHash).ToString()
                : AssetDatabase.GetAssetDependencyHash(ap).ToString();
            string file = $"{BBPaths.TexCache}/{BBUtil.Slug(t.name)}_{BBUtil.Short(hash)}.png";
            if (File.Exists(file)) return file;
            float s = Mathf.Min(1f, 2048f / Mathf.Max(t.width, t.height));
            int w = Mathf.Max(1, (int)(t.width * s)), h = Mathf.Max(1, (int)(t.height * s));
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(file, tex.EncodeToPNG());
                baked++;
                return file;
            }
            catch (Exception e) { warnings.Add($"texture {t.name}: {e.Message}"); return ""; }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (tex) UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
