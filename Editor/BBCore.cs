using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Auleek.BlenderBridge.Editor
{
    // =====================================================================================================
    // Blender Bridge: the Unity scene is the only source of truth. "Open in Blender" writes a snapshot of the
    // scene plus a cached asset library and builds a throwaway .blend from them; "Sync to Unity" in Blender
    // writes what changed, and Unity applies it. Nothing in BlenderSessions/ needs to be kept or committed.
    //
    //   BlenderSessions/_cache/          meshes and textures Unity converted for Blender (keyed by asset hash)
    //   BlenderSessions/_library/        AA_UnityLibrary.blend + library.json + asset catalogs (rebuilt as needed)
    //   BlenderSessions/<Scene>/         session.json (snapshot), <Scene>.blend, out/ (Blender's result), applied.json
    //
    // Assets modelled in Blender become  <Asset root>/{Meshes,Materials,Textures,Prefabs} (setting, {scene} = scene name)
    // and keep their editable source in   .../Blender/Source~/<key>.blend  (Unity ignores "~" folders; git keeps it).
    // =====================================================================================================

    // ---------------------------------------------------------------- files exchanged with Blender (JsonUtility)
    [Serializable] public class BBLight { public string type = ""; public float[] color; public float intensity, range, spot; }

    [Serializable] public class BBNode
    {
        public string id;          // GlobalObjectId of the GameObject
        public string name;
        public string parent = ""; // parent node id ("" = top level)
        public string asset = "";  // library asset key ("" = no visual)
        public string kind;        // instance | group | light | marker
        public bool editable;
        public bool active = true;
        public float[] m;          // world matrix in Blender space, column-major
        public BBLight light;
    }

    [Serializable] public class BBSession
    {
        public int version = 1;
        public string sessionId, scene, scenePath, sceneGuid, created, library, libraryJson, blend, project, assetRoot, outDir, appliedPath;
        public List<BBNode> nodes = new List<BBNode>();
    }

    [Serializable] public class BBPart { public string mesh; public string[] mats; public float[] m; public string path = ""; }

    [Serializable] public class BBAsset
    {
        public string key, name, kind, catalog = "", hash, prefab = "", source = "", sourceCollection = "", bbKey = "";
        public bool browsable;
        public List<BBPart> parts = new List<BBPart>();
    }

    [Serializable] public class BBMaterial
    {
        public string key, name, shader, baseMap = "", normalMap = "", emissionMap = "", alpha = "opaque";
        public float[] color, emission, tiling;
        public float metallic, smoothness = 0.5f, normalScale = 1, cutoff = 0.5f;
    }

    [Serializable] public class BBMeshRef { public string key, file; }

    [Serializable] public class BBLibrary
    {
        public int version = 1;
        public string blend, dir;
        public List<BBMeshRef> meshes = new List<BBMeshRef>();
        public List<BBMaterial> materials = new List<BBMaterial>();
        public List<BBAsset> assets = new List<BBAsset>();
    }

    // ---- written by Blender ("Sync to Unity")
    [Serializable] public class BBOutNode { public string id = "", name = "", parent = "", tmp = "", of = "", asset = ""; public float[] m; }

    [Serializable] public class BBOutMaterial
    {
        public string name, unity = "", baseMap = "", normalMap = "", maskMap = "", emissionMap = "", alpha = "opaque";
        public float[] color, emission;
        public float metallic, smoothness = 0.5f, normalScale = 1, cutoff = 0.5f;
    }

    [Serializable] public class BBOutAsset
    {
        public string key, name, fbx, source, sourceCollection, collider = "mesh", hash, dir;
        public string node = "";   // the Unity object whose geometry this is (Make editable), if any
        public string mode = "";   // prefab instances: "variant" (this instance only) or "source" (the prefab, all copies)
        public string prefab = ""; // "prefab:<guid>" of the original prefab
        public bool pivotMoved;
        public List<BBOutPart> parts = new List<BBOutPart>();
        public List<string> removed = new List<string>();  // prefab part paths deleted in Blender
        public bool isNew;
        public List<BBOutMaterial> materials = new List<BBOutMaterial>();
    }

    [Serializable] public class BBOutPart { public string @object = "", path = ""; public bool known; public float[] m; public int depth; }

    [Serializable] public class BBResult
    {
        public string sessionId, written, blend;
        public List<BBOutNode> nodes = new List<BBOutNode>();   // every editable node Blender still has
        public List<BBOutNode> dups = new List<BBOutNode>();    // Shift+D copies: of = source id
        public List<BBOutNode> added = new List<BBOutNode>();   // new instances of library assets / new Blender assets
        public List<BBOutAsset> assets = new List<BBOutAsset>(); // Blender-made assets to (re)build
    }

    [Serializable] public class BBIdMap { public string tmp, id, asset = "", hash = ""; }
    [Serializable] public class BBApplied { public string sessionId, applied, summary; public List<BBIdMap> ids = new List<BBIdMap>(); }

    // ---------------------------------------------------------------- paths
    public static class BBPaths
    {
        public static string Project => Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
        public static string Root => Project + "/BlenderSessions";
        public static string MeshCache => Root + "/_cache/meshes";
        public static string TexCache => Root + "/_cache/textures";
        public static string LibraryDir => Root + "/_library";
        public static string LibraryBlend => LibraryDir + "/AA_UnityLibrary.blend";
        public static string LibraryJson => LibraryDir + "/library.json";
        /// <summary>Folder that holds the Blender add-on (aa_bridge): BlenderAddon~ inside this package, wherever the
        /// package is installed (git URL, embedded in Packages/, or local path).</summary>
        public static string Tools
        {
            get
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BBPaths).Assembly);
                if (pkg != null && !string.IsNullOrEmpty(pkg.resolvedPath)) return pkg.resolvedPath.Replace('\\', '/') + "/BlenderAddon~";
                // Not installed as a package (scripts copied into Assets): look next to the scripts.
                var guid = AssetDatabase.FindAssets("BBCore t:MonoScript").FirstOrDefault();
                string dir = guid == null ? "" : Path.GetDirectoryName(AssetDatabase.GUIDToAssetPath(guid));
                return Full(Path.GetDirectoryName(dir ?? "")?.Replace('\\', '/') + "/BlenderAddon~");
            }
        }
        public static string SessionDir(string scene) => Root + "/" + scene;
        public static string SessionJson(string scene) => SessionDir(scene) + "/session.json";
        public static string Blend(string scene) => SessionDir(scene) + "/" + scene + ".blend";
        public static string OutDir(string scene) => SessionDir(scene) + "/out";
        public static string ResultJson(string scene) => OutDir(scene) + "/result.json";
        public static string AppliedJson(string scene) => SessionDir(scene) + "/applied.json";
        public static string AssetRoot(string scene)
        {
            string r = BBSettings.instance.assetRoot;
            if (string.IsNullOrWhiteSpace(r)) r = BBSettings.DefaultAssetRoot;
            return r.Trim().TrimEnd('/').Replace("{scene}", scene);
        }
        public static string Full(string assetPath) => Project + "/" + assetPath;

        public static void EnsureGitIgnore()
        {
            string gi = Project + "/.gitignore";
            if (!File.Exists(gi)) return;
            string t = File.ReadAllText(gi);
            if (t.Contains("/BlenderSessions/")) return;
            File.AppendAllText(gi, (t.EndsWith("\n") ? "" : "\n") + "# Blender Bridge working files (throwaway, rebuilt from the Unity scene)\n/BlenderSessions/\n");
        }
    }

    // ---------------------------------------------------------------- axes
    /// <summary>Unity (left-handed, +Y up) to Blender (right-handed, +Z up): a point (x, y, z) maps to (-x, -z, y).
    /// It is the same mapping Blender's FBX exporter uses with forward -Z, up Y and baked space transform, so meshes
    /// exported from Blender land in Unity with identity rotation. It is a reflection, so triangle winding flips.</summary>
    public static class BBConvert
    {
        static readonly Matrix4x4 M = new Matrix4x4(new Vector4(-1, 0, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, -1, 0, 0), new Vector4(0, 0, 0, 1));
        static readonly Matrix4x4 Mi = M.transpose;

        public static Matrix4x4 ToBlender(Matrix4x4 unity) => M * unity * Mi;
        public static Matrix4x4 ToUnity(Matrix4x4 blender) => Mi * blender * M;

        public static float[] Flat(Matrix4x4 m)
        {
            var f = new float[16];
            for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) f[c * 4 + r] = m[r, c];
            return f;
        }

        public static Matrix4x4 Unflat(float[] f)
        {
            var m = new Matrix4x4();
            if (f == null || f.Length != 16) return Matrix4x4.identity;
            for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) m[r, c] = f[c * 4 + r];
            return m;
        }

        /// <summary>World matrices equal within tolerance (position mm, rotation 0.05 deg, scale 1e-4).</summary>
        public static bool Near(float[] a, float[] b)
        {
            if (a == null || b == null) return a == b;
            Decompose(Unflat(a), out var pa, out var ra, out var sa);
            Decompose(Unflat(b), out var pb, out var rb, out var sb);
            return (pa - pb).magnitude < 0.001f && Quaternion.Angle(ra, rb) < 0.05f && (sa - sb).magnitude < 0.0001f * Mathf.Max(1f, sa.magnitude);
        }

        public static void Decompose(Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            pos = m.GetColumn(3);
            Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
            scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0) scale.x = -scale.x;  // mirrored
            var r = Matrix4x4.identity;
            r.SetColumn(0, scale.x != 0 ? (Vector4)(x / scale.x) : Vector4.zero);
            r.SetColumn(1, scale.y != 0 ? (Vector4)(y / scale.y) : Vector4.zero);
            r.SetColumn(2, scale.z != 0 ? (Vector4)(z / scale.z) : Vector4.zero);
            rot = r.rotation;
        }

        /// <summary>Puts t at the given Unity world matrix, whatever its parent is.</summary>
        public static void SetWorld(Transform t, Matrix4x4 world)
        {
            var local = t.parent ? t.parent.worldToLocalMatrix * world : world;
            Decompose(local, out var p, out var r, out var s);
            t.localPosition = p;
            t.localRotation = r;
            t.localScale = s;
        }
    }

    internal static class BBUtil
    {
        public static string Json<T>(T o) => JsonUtility.ToJson(o, false);

        public static T Read<T>(string path) where T : class
        {
            try { return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : null; }
            catch (Exception e) { Debug.LogWarning($"[Blender Bridge] could not read {path}: {e.Message}"); return null; }
        }

        public static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static string Goid(UnityEngine.Object o) => GlobalObjectId.GetGlobalObjectIdSlow(o).ToString();

        /// <summary>id -> GameObject for every GameObject of the scene, in one batch.</summary>
        public static Dictionary<string, GameObject> SceneIndex(UnityEngine.SceneManagement.Scene scene)
        {
            var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToArray();
            var ids = new GlobalObjectId[all.Length];
            GlobalObjectId.GetGlobalObjectIdsSlow(all, ids);
            var d = new Dictionary<string, GameObject>(all.Length);
            for (int i = 0; i < all.Length; i++) d[ids[i].ToString()] = all[i];
            return d;
        }

        /// <summary>"guid_localId" (as the exporter writes asset keys) back to the asset.</summary>
        public static T FindByKey<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key)) return null;
            int u = key.LastIndexOf('_');
            if (u < 0 || !long.TryParse(key.Substring(u + 1), out long lid)) return null;
            string guid = key.Substring(0, u);
            string path = guid == "0000000000000000e000000000000000" ? "Library/unity default resources" : AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o is T t && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out _, out long l) && l == lid) return t;
            return null;
        }

        public static string Slug(string s)
        {
            var chars = (s ?? "asset").Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
            string r = new string(chars).Trim('_');
            return string.IsNullOrEmpty(r) ? "asset" : r;
        }

        /// <summary>Blender's ".001" duplicate suffix removed.</summary>
        public static string CleanName(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            int dot = n.LastIndexOf('.');
            if (dot > 0 && n.Length - dot == 4 && n.Substring(dot + 1).All(char.IsDigit)) return n.Substring(0, dot);
            return n;
        }
    }
}
