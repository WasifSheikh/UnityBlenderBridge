using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Auleek.BlenderBridge.Editor
{
    // =====================================================================================================
    // Blender Bridge: the Unity scene is the only source of truth. "Open in Blender" writes a snapshot of the
    // scene plus a cached asset library and builds a throwaway .blend from them; "Sync to Unity" in Blender
    // writes what changed, and Unity applies it. Nothing in BlenderSessions/ needs to be kept or committed.
    //
    //   BlenderSessions/_cache/          meshes and textures Unity converted for Blender (content-addressed)
    //   BlenderSessions/_library/        UnityLibrary.blend + library.json + asset catalogs (rebuilt as needed)
    //   BlenderSessions/<Scene>_<guid8>/ session.json (snapshot), <key>.blend, out/ (Blender's result), applied.json
    //
    // Assets modelled in Blender go to the Asset root setting ({scene} = scene name):
    //   <root>/Meshes/SM_<key>.fbx, Materials/, Textures/<key>/, Prefabs/PF_<key>.prefab, Source~/<key>.blend
    // =====================================================================================================

    public static class BBVersion
    {
        [Serializable] class Release { public string version = ""; }
        public static string Current
        {
            get
            {
                var package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BBVersion).Assembly);
                string path=package == null ? "" : Path.Combine(package.resolvedPath,"package.json");
                return File.Exists(path) ? JsonUtility.FromJson<Release>(File.ReadAllText(path)).version : package?.version ?? "unknown";
            }
        }
    }

    public static class BBPaths
    {
        static string project, tools;

        public static string Project => project ??= Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
        public static string Root => Project + "/BlenderSessions";
        public static string Backups => Project + "/SyncBackups/BlenderBridge";
        public static string MeshCache => Root + "/_cache/meshes";
        public static string TexCache => Root + "/_cache/textures";
        public static string LibraryDir => Root + "/_library";
        public static string LibraryBlend => LibraryDir + "/UnityLibrary.blend";
        public static string LibraryJson => LibraryDir + "/library.json";
        public static string LibraryLog => LibraryDir + "/library_build.log";
        public static string LibraryBuilt => LibraryDir + "/built.hash";
        public static string LibraryName => "Unity: " + Path.GetFileName(Project);

        /// <summary>Folder that holds the Blender side (bb_entry.py + aa_bridge/): BlenderAddon~ inside this package,
        /// wherever the package is installed (git URL, embedded in Packages/, or scripts copied into Assets).</summary>
        public static string Tools => tools ??= FindTools();
        public static string Entry => Tools + "/bb_entry.py";

        static string FindTools()
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BBPaths).Assembly);
            if (pkg != null && !string.IsNullOrEmpty(pkg.resolvedPath)) return pkg.resolvedPath.Replace('\\', '/') + "/BlenderAddon~";
            // not a package: BlenderAddon~ sits next to the Editor folder that holds this script
            string guid = AssetDatabase.FindAssets("BBCore t:MonoScript").FirstOrDefault();
            string script = guid == null ? null : AssetDatabase.GUIDToAssetPath(guid);
            string editorDir = string.IsNullOrEmpty(script) ? null : Path.GetDirectoryName(script);
            string pkgDir = string.IsNullOrEmpty(editorDir) ? null : Path.GetDirectoryName(editorDir);
            return string.IsNullOrEmpty(pkgDir) ? Project + "/BlenderAddon~" : Full(pkgDir.Replace('\\', '/') + "/BlenderAddon~");
        }

        // ---- one session per scene, keyed by name + GUID so two scenes called "Main" don't share files
        public static string Key(Scene scene) => Key(scene.name, AssetDatabase.AssetPathToGUID(scene.path));
        public static string Key(string sceneName, string sceneGuid) =>
            BBUtil.Slug(sceneName) + (string.IsNullOrEmpty(sceneGuid) ? "" : "_" + sceneGuid.Substring(0, Math.Min(8, sceneGuid.Length)));

        public static string SessionDir(string key) => Root + "/" + key;
        public static string SessionJson(string key) => SessionDir(key) + "/session.json";
        public static string Blend(string key) => SessionDir(key) + "/" + key + ".blend";
        public static string OutDir(string key) => SessionDir(key) + "/out";
        public static string ResultJson(string key) => OutDir(key) + "/result.json";
        public static string ImportProgress(string key) => OutDir(key) + "/import_progress.json";
        public static string AppliedJson(string key) => SessionDir(key) + "/applied.json";
        public static string StatusJson(string key) => SessionDir(key) + "/status.json";

        public static string AssetRoot(string sceneName)
        {
            string r = BBSettings.instance.assetRoot;
            if (string.IsNullOrWhiteSpace(r)) r = BBSettings.DefaultAssetRoot;
            return r.Trim().Replace('\\', '/').TrimEnd('/').Replace("{scene}", sceneName);
        }

        public static string Full(string assetPath) => Project + "/" + assetPath;

        /// <summary>Adds BlenderSessions/ and SyncBackups/ to the .gitignore of the repository that holds the project
        /// (which can be above the Unity project folder).</summary>
        public static void EnsureGitIgnore()
        {
            try
            {
                string repo = Project;
                while (repo != null && !Directory.Exists(repo + "/.git") && !File.Exists(repo + "/.git")) repo = Path.GetDirectoryName(repo)?.Replace('\\', '/');
                if (repo == null) return;
                string prefix = Project.Length > repo.Length ? Project.Substring(repo.Length + 1) + "/" : "";
                string gi = repo + "/.gitignore";
                string text = File.Exists(gi) ? File.ReadAllText(gi) : "";
                var lines = new HashSet<string>(text.Split('\n').Select(l => l.Trim()));
                var add = new[] { $"/{prefix}BlenderSessions/", $"/{prefix}SyncBackups/" }.Where(l => !lines.Contains(l) && !lines.Contains(l.TrimStart('/'))).ToList();
                if (add.Count == 0) return;
                File.AppendAllText(gi, (text.Length == 0 || text.EndsWith("\n") ? "" : "\n") +
                                       "# Blender Bridge working files (throwaway, rebuilt from the Unity scene)\n" + string.Join("\n", add) + "\n");
            }
            catch (Exception e) { Debug.LogWarning("[Blender Bridge] could not update .gitignore: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- axes
    /// <summary>Unity (left-handed, +Y up) to Blender (right-handed, +Z up): a point (x, y, z) maps to (-x, -z, y).
    /// It is the same mapping Blender's FBX exporter uses with forward -Z, up Y and baked space transform, so meshes
    /// exported from Blender land in Unity with identity rotation. It is a reflection, so triangle winding flips.
    /// Conjugation keeps products, so ToUnity(A * B) == ToUnity(A) * ToUnity(B) and local matrices convert too.</summary>
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
            if (f == null || f.Length != 16) return Matrix4x4.identity;
            var m = new Matrix4x4();
            for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) m[r, c] = f[c * 4 + r];
            return m;
        }

        /// <summary>Matrices equal within tolerance (position mm, rotation 0.05 deg, scale 1e-4 relative).</summary>
        public static bool Near(Matrix4x4 a, Matrix4x4 b)
        {
            Decompose(a, out var pa, out var ra, out var sa);
            Decompose(b, out var pb, out var rb, out var sb);
            return (pa - pb).magnitude < 0.001f && Quaternion.Angle(ra, rb) < 0.05f && (sa - sb).magnitude < 0.0001f * Mathf.Max(1f, sa.magnitude);
        }

        public static bool Near(float[] a, float[] b) => a == null || b == null ? a == b : Near(Unflat(a), Unflat(b));

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

        /// <summary>Sets t's local transform from a matrix relative to its parent.</summary>
        public static void SetLocal(Transform t, Matrix4x4 local)
        {
            Decompose(local, out var p, out var r, out var s);
            t.localPosition = p;
            t.localRotation = r;
            t.localScale = s;
        }

        /// <summary>Puts t at the given Unity world matrix, whatever its parent is.</summary>
        public static void SetWorld(Transform t, Matrix4x4 world) => SetLocal(t, t.parent ? t.parent.worldToLocalMatrix * world : world);

        /// <summary>World matrix of a transform (or identity for none) in Blender space.</summary>
        public static Matrix4x4 BlenderWorld(Transform t) => t ? ToBlender(t.localToWorldMatrix) : Matrix4x4.identity;
    }

    internal static class BBUtil
    {
        public static string Json<T>(T o) => JsonUtility.ToJson(o, false);

        public static T Read<T>(string path) where T : class
        {
            try { return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : null; }
            catch (Exception e) { Debug.LogWarning($"[Blender Bridge] could not read {path}: {e.Message}"); return null; }
        }

        /// <summary>Writes through a temp file and swaps it in, so readers never see a half-written file.</summary>
        public static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (!File.Exists(path)) { File.Move(tmp, path); return; }
            try { File.Replace(tmp, path, null); }
            catch (Exception) { File.Delete(path); File.Move(tmp, path); }   // e.g. file systems without Replace
        }

        public static string Goid(UnityEngine.Object o) => GlobalObjectId.GetGlobalObjectIdSlow(o).ToString();

        /// <summary>ids for many objects in one batch (much faster than one GetGlobalObjectIdSlow per object).</summary>
        public static string[] Goids(IList<GameObject> objs)
        {
            var arr = objs as GameObject[] ?? objs.ToArray();
            var ids = new GlobalObjectId[arr.Length];
            GlobalObjectId.GetGlobalObjectIdsSlow(arr, ids);
            var s = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++) s[i] = ids[i].ToString();
            return s;
        }

        public static GameObject[] AllObjects(Scene scene) =>
            scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToArray();

        /// <summary>id -> GameObject for every GameObject of the scene, in one batch.</summary>
        public static Dictionary<string, GameObject> SceneIndex(Scene scene)
        {
            var all = AllObjects(scene);
            var ids = Goids(all);
            var d = new Dictionary<string, GameObject>(all.Length);
            for (int i = 0; i < all.Length; i++) d[ids[i]] = all[i];
            return d;
        }

        const string BuiltinDefault = "0000000000000000e000000000000000", BuiltinExtra = "0000000000000000f000000000000000";

        /// <summary>"guid_localId" (as the exporter writes asset keys) back to the asset, built-in resources included.</summary>
        public static T FindByKey<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key)) return null;
            int u = key.LastIndexOf('_');
            if (u < 0 || !long.TryParse(key.Substring(u + 1), out long lid)) return null;
            string guid = key.Substring(0, u);
            string path = guid == BuiltinDefault ? "Library/unity default resources"
                        : guid == BuiltinExtra ? "Resources/unity_builtin_extra"
                        : AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o is T t && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out _, out long l) && l == lid) return t;
            return null;
        }

        public static string Slug(string s)
        {
            var chars = (s ?? "asset").Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
            string r = new string(chars).Trim('_');
            while (r.Contains("__")) r = r.Replace("__", "_");
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

        public static string Short(string hash, int n = 12) => string.IsNullOrEmpty(hash) ? "" : hash.Substring(0, Math.Min(n, hash.Length));
    }
}
