using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UMesh = UnityEngine.Mesh;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>
    /// Unity -> Blender. Writes the scene snapshot (session.json) and the asset library description (library.json),
    /// converting every mesh and the textures Blender cannot read into the shared cache. Prefab instances are atomic
    /// nodes that instance the prefab's library asset; plain mesh objects instance a mesh asset; everything else in an
    /// editable root becomes an empty, so the hierarchy survives. Objects outside the editable roots are sent as
    /// locked context (visuals only, flat).
    /// </summary>
    public class BBExport
    {
        public BBSession session;
        public BBLibrary lib;
        public int meshesWritten, texturesBaked;
        public readonly List<string> warnings = new List<string>();

        readonly Dictionary<string, BBAsset> assets = new Dictionary<string, BBAsset>();
        readonly Dictionary<string, BBMaterial> mats = new Dictionary<string, BBMaterial>();
        readonly Dictionary<UMesh, string> meshKeys = new Dictionary<UMesh, string>();
        readonly Dictionary<Material, string> matKeys = new Dictionary<Material, string>();
        readonly Dictionary<Texture, string> texPaths = new Dictionary<Texture, string>();
        readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<GameObject, string> ids;

        static readonly string[] BlenderReadable = { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".exr", ".bmp", ".hdr", ".psd", ".dds", ".webp" };

        public static BBExport Run(Scene scene, ICollection<string> editableRootIds)
        {
            var x = new BBExport();
            try { x.Export(scene, new HashSet<string>(editableRootIds)); }
            finally { EditorUtility.ClearProgressBar(); }
            return x;
        }

        void Export(Scene scene, HashSet<string> editable)
        {
            string sn = scene.name;
            session = new BBSession
            {
                sessionId = Guid.NewGuid().ToString("N"), scene = sn, scenePath = scene.path, sceneGuid = AssetDatabase.AssetPathToGUID(scene.path),
                created = DateTime.Now.ToString("s"), library = BBPaths.LibraryBlend, libraryJson = BBPaths.LibraryJson, blend = BBPaths.Blend(sn),
                project = BBPaths.Project, assetRoot = BBPaths.AssetRoot(sn), outDir = BBPaths.OutDir(sn), appliedPath = BBPaths.AppliedJson(sn),
            };
            lib = new BBLibrary { blend = BBPaths.LibraryBlend, dir = BBPaths.LibraryDir };
            Directory.CreateDirectory(BBPaths.MeshCache);
            Directory.CreateDirectory(BBPaths.TexCache);
            Directory.CreateDirectory(BBPaths.LibraryDir);

            var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToArray();
            var gids = new GlobalObjectId[all.Length];
            GlobalObjectId.GetGlobalObjectIdsSlow(all, gids);
            ids = new Dictionary<GameObject, string>(all.Length);
            for (int i = 0; i < all.Length; i++) ids[all[i]] = gids[i].ToString();

            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Blender Bridge", $"Reading {roots[i].name}", 0.6f * i / Mathf.Max(1, roots.Length));
                Walk(roots[i], "", editable.Contains(ids[roots[i]]), new HashSet<GameObject>());
            }

            // the asset library: every prefab in the library folders, plus every Blender-made asset in the project
            var paths = BBSettings.instance.libraryFolders.Where(AssetDatabase.IsValidFolder)
                .SelectMany(f => AssetDatabase.FindAssets("t:Prefab", new[] { f })).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !p.Contains("/_")).ToList();
            paths.AddRange(AssetDatabase.FindAssets("l:" + BBAssets.Label + " t:Prefab").Select(AssetDatabase.GUIDToAssetPath));
            paths = paths.Distinct().ToList();
            for (int i = 0; i < paths.Count; i++)
            {
                if (i % 20 == 0) EditorUtility.DisplayProgressBar("Blender Bridge", "Asset library: " + Path.GetFileNameWithoutExtension(paths[i]), 0.6f + 0.4f * i / paths.Count);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
                if (go) PrefabAsset(paths[i], go);
            }
            lib.assets = assets.Values.ToList();
            lib.materials = mats.Values.ToList();
        }

        public void Save()
        {
            BBPaths.EnsureGitIgnore();
            BBUtil.Write(BBPaths.LibraryJson, BBUtil.Json(lib));
            BBUtil.Write(BBPaths.SessionJson(session.scene), BBUtil.Json(session));
        }

        public string Summary => $"{session.nodes.Count(n => n.editable)} editable + {session.nodes.Count(n => !n.editable)} context objects, " +
                                 $"{assets.Count} library assets, {meshesWritten} meshes converted, {texturesBaked} textures baked";

        // ================================================================ scene
        void Walk(GameObject go, string parentId, bool editable, HashSet<GameObject> hiddenLods)
        {
            if ((go.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSave)) != 0) return;
            string id = ids[go];
            var n = new BBNode
            {
                id = id, name = go.name, parent = editable ? parentId : "", editable = editable, active = go.activeInHierarchy,
                m = BBConvert.Flat(BBConvert.ToBlender(go.transform.localToWorldMatrix)),
            };
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                var asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset)
                {
                    n.kind = "instance";
                    n.asset = PrefabAsset(path, asset);
                    session.nodes.Add(n);
                    return;  // prefab instances are atomic: their insides stay Unity's
                }
            }
            if (go.TryGetComponent<LODGroup>(out var lg)) HideLods(lg, hiddenLods);
            if (!hiddenLods.Contains(go) && MeshOf(go, out var mesh, out var materials))
            {
                n.kind = "instance";
                n.asset = MeshAsset(mesh, materials, go.name);
            }
            else if (go.TryGetComponent<Light>(out var l))
            {
                n.kind = "light";
                n.light = new BBLight { type = l.type.ToString(), color = new[] { l.color.r, l.color.g, l.color.b }, intensity = l.intensity, range = l.range, spot = l.spotAngle };
            }
            else n.kind = go.GetComponent<Renderer>() ? "marker" : "group";
            if (editable || n.kind == "instance") session.nodes.Add(n);
            foreach (Transform c in go.transform) Walk(c.gameObject, editable ? id : "", editable, hiddenLods);
        }

        static void HideLods(LODGroup lg, HashSet<GameObject> hidden)
        {
            var lods = lg.GetLODs();
            if (lods.Length < 2) return;
            var keep = new HashSet<Renderer>(lods[0].renderers.Where(r => r));
            for (int i = 1; i < lods.Length; i++)
                foreach (var r in lods[i].renderers)
                    if (r && !keep.Contains(r)) hidden.Add(r.gameObject);
        }

        static bool MeshOf(GameObject go, out UMesh mesh, out Material[] materials)
        {
            mesh = null; materials = null;
            if (go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh && go.TryGetComponent<MeshRenderer>(out var mr) && mr.enabled)
            { mesh = mf.sharedMesh; materials = mr.sharedMaterials; return true; }
            if (go.TryGetComponent<SkinnedMeshRenderer>(out var smr) && smr.sharedMesh && smr.enabled)
            { mesh = smr.sharedMesh; materials = smr.sharedMaterials; return true; }
            return false;
        }

        // ================================================================ library assets
        string UniqueName(string n)
        {
            n = string.IsNullOrEmpty(n) ? "Asset" : n;
            if (n.Length > 48) n = n.Substring(0, 48);
            string r = n;
            for (int i = 2; names.Contains(r); i++) r = $"{n} {i}";
            names.Add(r);
            return r;
        }

        static string Catalog(string path)
        {
            var parts = Path.GetDirectoryName(path).Replace('\\', '/').Split('/').ToList();
            int i = parts.IndexOf("Environment");
            var rest = i >= 0 ? parts.Skip(i + 1).ToList() : parts.Skip(1).ToList();
            while (rest.Count > 1 && (rest[rest.Count - 1] == "Prefabs" || rest[rest.Count - 1] == "Meshes" || rest[rest.Count - 1] == "Models")) rest.RemoveAt(rest.Count - 1);
            if (rest.Count == 0) rest.Add("Other");
            return string.Join("/", rest);
        }

        string PrefabAsset(string path, GameObject root)
        {
            string key = "prefab:" + AssetDatabase.AssetPathToGUID(path);
            if (assets.ContainsKey(key)) return key;
            var a = new BBAsset
            {
                key = key, name = UniqueName(root.name), kind = "prefab", catalog = Catalog(path), prefab = path, browsable = !path.Contains("/_"),
                hash = AssetDatabase.GetAssetDependencyHash(path).ToString(),
            };
            assets[key] = a;

            string bbk = BBAssets.KeyFromLabels(root) ?? (AssetDatabase.GetLabels(root).Contains(BBAssets.Label) ? BBAssets.KeyOf(path) : null);
            if (bbk != null)
            {
                string src = BBAssets.SourceBlendForKey(bbk);
                if (File.Exists(src))
                {
                    string slug = bbk;
                    a.kind = "blender";
                    a.source = src;
                    a.sourceCollection = "AA_Asset";  // sync.py SOURCE_COLLECTION
                    a.bbKey = slug;
                    a.catalog = "Blender made";
                    a.hash += "|" + File.GetLastWriteTimeUtc(src).Ticks;
                    return key;
                }
            }

            var hidden = new HashSet<GameObject>();
            foreach (var g in root.GetComponentsInChildren<LODGroup>(true)) HideLods(g, hidden);
            var inv = root.transform.worldToLocalMatrix;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (hidden.Contains(t.gameObject) || !ActiveUnder(t, root.transform)) continue;
                if (!MeshOf(t.gameObject, out var mesh, out var materials)) continue;
                a.parts.Add(new BBPart
                {
                    mesh = MeshKey(mesh), mats = materials.Select(MatKey).ToArray(),
                    m = BBConvert.Flat(BBConvert.ToBlender(inv * t.localToWorldMatrix)),
                    path = t == root.transform ? "" : AnimationUtility.CalculateTransformPath(t, root.transform),
                });
            }
            return key;
        }

        static bool ActiveUnder(Transform t, Transform root)
        {
            for (; t; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) return true;
            }
            return true;
        }

        string MeshAsset(UMesh mesh, Material[] materials, string name)
        {
            // a mesh modelled in Blender earlier: offer it from its source, so it can be edited again with modifiers intact
            string mp = AssetDatabase.GetAssetPath(mesh);
            if (mp.Contains("/Blender/Meshes/SM_"))
            {
                string k = Path.GetFileNameWithoutExtension(mp).Substring(3);
                string pf = BBAssets.FindPrefab(k);
                if (pf != null && File.Exists(BBAssets.SourceBlendForKey(k))) return PrefabAsset(pf, AssetDatabase.LoadAssetAtPath<GameObject>(pf));
            }
            string mk = MeshKey(mesh);
            var mks = materials.Select(MatKey).ToArray();
            string key = "mesh:" + Hash128.Compute(mk + "|" + string.Join(",", mks)).ToString().Substring(0, 16);
            if (!assets.ContainsKey(key))
                assets[key] = new BBAsset
                {
                    key = key, name = UniqueName(name), kind = "mesh", hash = "1", browsable = false,
                    parts = { new BBPart { mesh = mk, mats = mks, m = BBConvert.Flat(Matrix4x4.identity) } },
                };
            return key;
        }

        // ================================================================ meshes
        string MeshKey(UMesh mesh)
        {
            if (meshKeys.TryGetValue(mesh, out var k)) return k;
            string path = AssetDatabase.GetAssetPath(mesh), hash;
            if (!string.IsNullOrEmpty(path) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long lid))
            {
                k = guid + "_" + lid;
                hash = path.StartsWith("Assets") || path.StartsWith("Packages") ? AssetDatabase.GetAssetDependencyHash(path).ToString() : "builtin";
            }
            else
            {
                k = "scene_" + Hash128.Compute(mesh.name + "|" + mesh.vertexCount + "|" + mesh.bounds).ToString().Substring(0, 12);
                hash = session.sessionId;
            }
            string file = $"{BBPaths.MeshCache}/{k}__{hash.Substring(0, Math.Min(12, hash.Length))}.aamesh";
            if (!File.Exists(file))
            {
                try { WriteMesh(mesh, file); meshesWritten++; }
                catch (Exception e) { warnings.Add($"mesh {mesh.name}: {e.Message}"); }
            }
            lib.meshes.Add(new BBMeshRef { key = k, file = file });
            meshKeys[mesh] = k;
            return k;
        }

        /// <summary>AAM1: "AAM1", int verts, int flags (1 normals, 2 uv0), int submeshes, int indexCount per submesh,
        /// float3 positions, [float3 normals], [float2 uv0], int indices. All in Blender space, winding flipped.</summary>
        static void WriteMesh(UMesh mesh, string file)
        {
            using (var arr = UMesh.AcquireReadOnlyMeshData(mesh))
            {
                var d = arr[0];
                int n = d.vertexCount;
                bool hasN = d.HasVertexAttribute(VertexAttribute.Normal), hasUv = d.HasVertexAttribute(VertexAttribute.TexCoord0);
                var subs = new List<int[]>();
                for (int s = 0; s < d.subMeshCount; s++)
                {
                    var sm = d.GetSubMesh(s);
                    if (sm.topology != MeshTopology.Triangles) { subs.Add(new int[0]); continue; }
                    var ia = new NativeArray<int>(sm.indexCount, Allocator.Temp);
                    d.GetIndices(ia, s, true);
                    var a = ia.ToArray();
                    ia.Dispose();
                    for (int i = 0; i + 2 < a.Length; i += 3) { int t = a[i + 1]; a[i + 1] = a[i + 2]; a[i + 2] = t; }
                    subs.Add(a);
                }
                string tmp = file + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(Encoding.ASCII.GetBytes("AAM1"));
                    w.Write(n);
                    w.Write((hasN ? 1 : 0) | (hasUv ? 2 : 0));
                    w.Write(subs.Count);
                    foreach (var a in subs) w.Write(a.Length);
                    var v3 = new NativeArray<Vector3>(n, Allocator.Temp);
                    d.GetVertices(v3);
                    WriteV3(w, v3);
                    if (hasN) { d.GetNormals(v3); WriteV3(w, v3); }
                    v3.Dispose();
                    if (hasUv)
                    {
                        var uv = new NativeArray<Vector2>(n, Allocator.Temp);
                        d.GetUVs(0, uv);
                        var f = new float[n * 2];
                        for (int i = 0; i < n; i++) { f[i * 2] = uv[i].x; f[i * 2 + 1] = uv[i].y; }
                        uv.Dispose();
                        WriteRaw(w, f);
                    }
                    foreach (var a in subs) WriteRaw(w, a);
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
        }

        static void WriteV3(BinaryWriter w, NativeArray<Vector3> v)
        {
            var f = new float[v.Length * 3];
            for (int i = 0; i < v.Length; i++) { f[i * 3] = -v[i].x; f[i * 3 + 1] = -v[i].z; f[i * 3 + 2] = v[i].y; }
            WriteRaw(w, f);
        }

        static void WriteRaw(BinaryWriter w, Array a)
        {
            var b = new byte[Buffer.ByteLength(a)];
            Buffer.BlockCopy(a, 0, b, 0, b.Length);
            w.Write(b);
        }

        // ================================================================ materials
        string MatKey(Material m)
        {
            if (!m) return "";
            if (matKeys.TryGetValue(m, out var k)) return k;
            k = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string guid, out long lid) && !string.IsNullOrEmpty(guid)
                ? guid + "_" + lid
                : "inst_" + Hash128.Compute(m.name + "|" + (m.shader ? m.shader.name : "")).ToString().Substring(0, 12);
            matKeys[m] = k;
            if (!mats.ContainsKey(k)) mats[k] = Describe(m, k);
            return k;
        }

        /// <summary>A viewport approximation of any shader: base colour/texture, normal, metal/smooth, emission, alpha.</summary>
        BBMaterial Describe(Material m, string key)
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
            d.normalMap = TexPath(nt, true);
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
                if (m.HasTexture(p)) { var t = m.GetTexture(p); if (t is Texture2D) { prop = p; return t; } }
            prop = null;
            return null;
        }

        static Texture ScanTex(Material m, out string prop, params string[] tokens)
        {
            prop = null;
            var sh = m.shader;
            if (!sh) return null;
            foreach (var tok in tokens)
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    if (sh.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    string n = sh.GetPropertyName(i);
                    if (!n.ToLowerInvariant().Contains(tok)) continue;
                    var t = m.GetTexture(n);
                    if (t is Texture2D) { prop = n; return t; }
                }
            return null;
        }

        string TexPath(Texture t, bool normal)
        {
            if (!(t is Texture2D t2)) return "";
            if (texPaths.TryGetValue(t, out var p)) return p;
            p = "";
            string ap = AssetDatabase.GetAssetPath(t);
            string ext = Path.GetExtension(ap).ToLowerInvariant();
            if (BlenderReadable.Contains(ext))
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
            string hash = string.IsNullOrEmpty(ap) || ap.StartsWith("Library")
                ? Hash128.Compute(t.name + "|" + t.width + "x" + t.height).ToString()
                : AssetDatabase.GetAssetDependencyHash(ap).ToString();
            string file = $"{BBPaths.TexCache}/{BBUtil.Slug(t.name)}_{hash.Substring(0, 12)}.png";
            if (File.Exists(file)) return file;
            float s = Mathf.Min(1f, 2048f / Mathf.Max(t.width, t.height));
            int w = Mathf.Max(1, (int)(t.width * s)), h = Mathf.Max(1, (int)(t.height * s));
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(file, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                texturesBaked++;
                return file;
            }
            catch (Exception e) { warnings.Add($"texture {t.name}: {e.Message}"); return ""; }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
        }
    }
}
