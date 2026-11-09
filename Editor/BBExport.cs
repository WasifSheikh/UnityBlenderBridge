using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UMesh = UnityEngine.Mesh;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>
    /// Unity -> Blender. Writes the scene snapshot (session.json) and the asset library description (library.json),
    /// converting meshes and the textures Blender cannot read into the shared cache. Prefab instances are atomic
    /// nodes that instance the prefab's library asset; plain mesh objects instance a mesh asset; everything else in an
    /// editable root becomes an empty, so the hierarchy survives. Objects outside the editable roots are sent as
    /// locked context (visuals only, flat).
    ///
    /// Scales with big libraries: an asset whose dependency hash is the same as in the previous library.json is
    /// reused without loading the prefab, and meshes are converted only when their cache file is missing.
    ///
    /// Content rules (Settings > What Blender gets): by default only mesh prefabs and plain mesh objects are sent.
    /// Lights, volumes, probes, cameras, FX and helper empties stay in Unity and are never touched by a sync; an empty
    /// group is only sent when something under it is, so the hierarchy of the props survives.
    /// </summary>
    public class BBExport
    {
        public BBSession session;
        public BBLibrary lib;
        public int meshesWritten, assetsReused, objectsWalked, librarySkipped;
        public readonly List<string> warnings = new List<string>();

        readonly Dictionary<string, BBAsset> assets = new Dictionary<string, BBAsset>();
        readonly Dictionary<string, BBMaterial> mats = new Dictionary<string, BBMaterial>();
        readonly Dictionary<string, BBMeshRef> meshRefs = new Dictionary<string, BBMeshRef>();
        readonly Dictionary<UMesh, string> meshKeys = new Dictionary<UMesh, string>();
        readonly Dictionary<Material, string> matKeys = new Dictionary<Material, string>();
        readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<GameObject, string> ids;
        BBMaterialReader reader;
        Dictionary<string, BBAsset> prevAssets = new Dictionary<string, BBAsset>();
        Dictionary<string, BBMeshRef> prevMeshes = new Dictionary<string, BBMeshRef>();
        Dictionary<string, BBMaterial> prevMats = new Dictionary<string, BBMaterial>();
        Dictionary<string, string> prevSkipped = new Dictionary<string, string>();
        string prevContentFilter = "";
        readonly List<BBSkipped> skipped = new List<BBSkipped>();
        readonly Dictionary<UMesh, string> modelKeyOf = new Dictionary<UMesh, string>();
        BBContentFilter filter;

        public int TexturesBaked => reader?.baked ?? 0;

        public static BBExport Run(Scene scene, ICollection<string> editableRootIds)
        {
            var x = new BBExport();
            try { x.Export(scene, new HashSet<string>(editableRootIds)); }
            finally { EditorUtility.ClearProgressBar(); }
            return x;
        }

        void Export(Scene scene, HashSet<string> editable)
        {
            string key = BBPaths.Key(scene);
            session = new BBSession
            {
                sessionId = Guid.NewGuid().ToString("N"), key = key, scene = scene.name, scenePath = scene.path, sceneGuid = AssetDatabase.AssetPathToGUID(scene.path),
                created = DateTime.Now.ToString("s"), library = BBPaths.LibraryBlend, libraryJson = BBPaths.LibraryJson, libraryName = BBPaths.LibraryName,
                blend = BBPaths.Blend(key), project = BBPaths.Project, assetRoot = BBPaths.AssetRoot(scene.name), outDir = BBPaths.OutDir(key),
                appliedPath = BBPaths.AppliedJson(key), statusPath = BBPaths.StatusJson(key), tools = BBPaths.Tools,
                profiles = BBSettings.instance.destinationProfiles.Select(p => new BBDestinationProfile
                    { id = p.id, name = p.name, assetRoot = p.assetRoot, sourceRoot = p.sourceRoot, setCode = p.setCode }).ToList(),
                defaultDestination = BBSettings.instance.For(AssetDatabase.AssetPathToGUID(scene.path), false)?.defaultDestination ?? "",
                defaultMaterialMode = string.IsNullOrEmpty(BBSettings.instance.defaultMaterialMode) ? "blender" : BBSettings.instance.defaultMaterialMode,
                defaultSyncTextures = BBSettings.instance.defaultMaterialMode != "unity" &&
                    (string.IsNullOrEmpty(BBSettings.instance.defaultMaterialMode) || BBSettings.instance.defaultSyncTextures),
                textureAliases = BBRegistry.TextureAliases(),
            };
            if (string.IsNullOrEmpty(session.defaultDestination)) session.defaultDestination = BBSettings.instance.defaultDestination;
            lib = new BBLibrary { blend = BBPaths.LibraryBlend, dir = BBPaths.LibraryDir, libraryName = BBPaths.LibraryName };
            reader = new BBMaterialReader(warnings);
            filter = new BBContentFilter(BBSettings.instance);
            Directory.CreateDirectory(BBPaths.MeshCache);
            Directory.CreateDirectory(BBPaths.TexCache);
            Directory.CreateDirectory(BBPaths.LibraryDir);
            LoadPrevious();

            var all = BBUtil.AllObjects(scene);
            var gids = BBUtil.Goids(all);
            ids = new Dictionary<GameObject, string>(all.Length);
            for (int i = 0; i < all.Length; i++) ids[all[i]] = gids[i];

            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Blender Bridge", $"Reading {roots[i].name}", 0.5f * i / Mathf.Max(1, roots.Length));
                Walk(roots[i], "", editable.Contains(ids[roots[i]]), new HashSet<GameObject>());
            }

            // the asset library: every mesh prefab in the library folders, plus every Blender-made asset in the project
            var s = BBSettings.instance;
            var blenderMade = new HashSet<string>(AssetDatabase.FindAssets("l:" + BBAssets.Label + " t:Prefab").Select(AssetDatabase.GUIDToAssetPath));
            var paths = s.libraryFolders.Select(f => f.Trim().TrimEnd('/')).Where(AssetDatabase.IsValidFolder).Distinct()
                         .SelectMany(f => AssetDatabase.FindAssets("t:Prefab", new[] { f })).Select(AssetDatabase.GUIDToAssetPath)
                         .Where(p => !s.Excluded(p))
                         .Concat(blenderMade)
                         .Distinct().ToList();
            for (int i = 0; i < paths.Count; i++)
            {
                if (i % 25 == 0) EditorUtility.DisplayProgressBar("Blender Bridge", "Asset library: " + Path.GetFileNameWithoutExtension(paths[i]), 0.5f + 0.5f * i / paths.Count);
                if (!blenderMade.Contains(paths[i]) && !LibraryWanted(paths[i])) continue;
                PrefabAsset(paths[i]);
            }
            lib.contentFilter = filter.Signature;
            lib.skipped = skipped;
            lib.assets = assets.Values.ToList();
            lib.materials = mats.Values.ToList();
            lib.meshes = meshRefs.Values.ToList();
            lib.keep = KeptByOtherSessions(key);
        }

        public void Save()
        {
            BBPaths.EnsureGitIgnore();
            BBUtil.Write(BBPaths.LibraryJson, BBUtil.Json(lib));
            BBUtil.Write(BBPaths.SessionJson(session.key), BBUtil.Json(session));
            BBUtil.Write(BBPaths.StatusJson(session.key), BBUtil.Json(new BBStatus { sessionId = session.sessionId, time = DateTime.Now.ToString("s") }));
            PruneMeshCache();
        }

        public string Summary => $"{session.nodes.Count(n => n.editable)} editable + {session.nodes.Count(n => !n.editable)} context objects" +
                                 (filter != null && filter.MeshesOnly ? $" ({Math.Max(0, objectsWalked - session.nodes.Count)} non-mesh objects left in Unity)" : "") +
                                 $", {assets.Count} library assets ({assetsReused} unchanged" + (librarySkipped > 0 ? $", {librarySkipped} non-mesh prefabs left out" : "") +
                                 $"), {meshesWritten} meshes converted, {TexturesBaked} textures baked";

        // ================================================================ reuse of the previous export
        void LoadPrevious()
        {
            var prev = BBUtil.Read<BBLibrary>(BBPaths.LibraryJson);
            if (prev == null || prev.version != BBFormat.Version) return;
            prevAssets = prev.assets.GroupBy(a => a.key).ToDictionary(g => g.Key, g => g.First());
            prevMeshes = prev.meshes.GroupBy(m => m.key).ToDictionary(g => g.Key, g => g.First());
            prevMats = prev.materials.GroupBy(m => m.key).ToDictionary(g => g.Key, g => g.First());
            prevContentFilter = prev.contentFilter ?? "";
            if (prev.skipped != null) prevSkipped = prev.skipped.Where(x => x?.key != null).GroupBy(x => x.key).ToDictionary(g => g.Key, g => g.First().hash);
        }

        /// <summary>Whether a library prefab is mesh content. Decided without loading the prefab when it is unchanged
        /// since the last export under the same content rules.</summary>
        bool LibraryWanted(string path)
        {
            if (!filter.MeshesOnly) return true;
            string key = "prefab:" + AssetDatabase.AssetPathToGUID(path);
            if (assets.ContainsKey(key)) return true;   // placed in the scene and already judged there
            string hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            if (prevContentFilter == filter.Signature)
            {
                if (prevSkipped.TryGetValue(key, out var h) && h == hash) { skipped.Add(new BBSkipped { key = key, hash = hash }); librarySkipped++; return false; }
                if (prevAssets.TryGetValue(key, out var a) && a.hash == hash) return true;
            }
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root && filter.IsMeshPrefab(root)) return true;
            skipped.Add(new BBSkipped { key = key, hash = hash });
            librarySkipped++;
            return false;
        }

        bool TryReuse(string key, string hash, bool browsable, string catalog)
        {
            if (!prevAssets.TryGetValue(key, out var a) || a.hash != hash) return false;
            var meshes = a.parts.Select(p => p.mesh).Distinct().ToList();
            if (meshes.Any(m => !prevMeshes.TryGetValue(m, out var r) || !File.Exists(r.file))) return false;
            var matKeysUsed = a.parts.SelectMany(p => p.mats ?? Array.Empty<string>()).Where(k => k != "").Distinct().ToList();
            if (matKeysUsed.Any(m => !prevMats.ContainsKey(m))) return false;
            foreach (var m in meshes) meshRefs[m] = prevMeshes[m];
            foreach (var m in matKeysUsed) if (!mats.ContainsKey(m)) mats[m] = prevMats[m];
            a.name = UniqueName(a.name);
            a.browsable = browsable;
            a.catalog = catalog;
            assets[key] = a;
            assetsReused++;
            return true;
        }

        static List<string> KeptByOtherSessions(string current)
        {
            var keep = new HashSet<string>();
            if (!Directory.Exists(BBPaths.Root)) return keep.ToList();
            foreach (var dir in Directory.GetDirectories(BBPaths.Root))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("_") || name == current) continue;
                var s = BBUtil.Read<BBSession>(dir + "/session.json");
                if (s?.nodes != null) foreach (var n in s.nodes) if (!string.IsNullOrEmpty(n.asset)) keep.Add(n.asset);
            }
            return keep.ToList();
        }

        void PruneMeshCache()
        {
            try
            {
                var used = new HashSet<string>(lib.meshes.Select(m => Path.GetFullPath(m.file)), StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(BBPaths.MeshCache, "*.aamesh"))
                    if (!used.Contains(Path.GetFullPath(f)) && (DateTime.Now - File.GetLastWriteTime(f)).TotalDays > 1) File.Delete(f);
            }
            catch (Exception e) { warnings.Add("mesh cache cleanup: " + e.Message); }
        }

        // ================================================================ scene
        /// <summary>Adds go (and what is under it) to the snapshot. Returns true when anything of its subtree was added,
        /// so a group with nothing to show is dropped again (content rules: meshes only).</summary>
        bool Walk(GameObject go, string parentId, bool editable, HashSet<GameObject> hiddenLods)
        {
            if ((go.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSave)) != 0) return false;
            objectsWalked++;
            bool meshesOnly = filter.MeshesOnly;
            string id = ids[go];
            var n = new BBNode
            {
                id = id, name = go.name, parent = editable ? parentId : "", editable = editable, active = go.activeInHierarchy,
                m = BBConvert.Flat(BBConvert.ToBlender(go.transform.localToWorldMatrix)),
            };
            int at = session.nodes.Count;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                bool wanted = !meshesOnly || filter.IsMeshPrefab(go);
                string key = wanted && !string.IsNullOrEmpty(path) ? PrefabAsset(path) : null;
                if (key != null)
                {
                    n.kind = "instance";
                    n.asset = key;
                    session.nodes.Add(n);
                    // prefab instances are atomic (their insides stay Unity's), but objects added under them in the
                    // scene are the scene's own and stay editable
                    WalkAdded(go, editable ? id : "", editable, hiddenLods);
                    return true;
                }
                if (meshesOnly)
                {
                    // not a mesh prefab (FX, light rig, character...): its insides stay Unity's. Only objects added under
                    // it in the scene can be content; then it comes along as a plain group so they keep their parent.
                    n.kind = "group";
                    if (editable) session.nodes.Add(n);
                    bool anyAdded = WalkAdded(go, editable ? id : "", editable, hiddenLods);
                    if (!anyAdded && editable) session.nodes.RemoveAt(at);
                    return anyAdded;
                }
            }
            if (go.TryGetComponent<LODGroup>(out var lg)) BBAssets.HideLods(lg, hiddenLods);
            if (!hiddenLods.Contains(go) && BBAssets.MeshOf(go, out var mesh, out var materials) && (!meshesOnly || filter.IsLooseMesh(go)))
            {
                n.kind = "instance";
                n.asset = MeshAsset(mesh, materials, go.name);
            }
            else if (!meshesOnly && go.TryGetComponent<Light>(out var l))
            {
                n.kind = "light";
                n.light = new BBLight { type = l.type.ToString(), color = new[] { l.color.r, l.color.g, l.color.b }, intensity = l.intensity, range = l.range, spot = l.spotAngle };
            }
            else n.kind = !meshesOnly && go.GetComponent<Renderer>() ? "marker" : "group";
            bool content = n.kind == "instance";
            if (editable || content) session.nodes.Add(n);
            bool any = false;
            foreach (Transform c in go.transform) any |= Walk(c.gameObject, editable ? id : "", editable, hiddenLods);
            if (meshesOnly && !content && !any)
            {
                if (editable) session.nodes.RemoveAt(at);   // nothing below was added, so it is still the last node
                return false;
            }
            return true;
        }

        bool WalkAdded(GameObject instanceRoot, string parentId, bool editable, HashSet<GameObject> hiddenLods)
        {
            bool any = false;
            foreach (var c in PrefabUtility.GetAddedGameObjects(instanceRoot))
                if (c.instanceGameObject && c.instanceGameObject.transform.parent == instanceRoot.transform)
                    any |= Walk(c.instanceGameObject, parentId, editable, hiddenLods);
            return any;
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

        /// <summary>Asset Browser catalog: the folder below the library folder it was found in (trailing Prefabs/Meshes/
        /// Models dropped), prefixed with that library folder's name.</summary>
        static string Catalog(string path)
        {
            string folder = BBSettings.instance.LibraryFolderOf(path);
            string dir = Path.GetDirectoryName(path).Replace('\\', '/');
            string rel = folder != null && dir.Length > folder.Length ? dir.Substring(folder.Length + 1) : "";
            var parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            while (parts.Count > 0 && (parts[parts.Count - 1] == "Prefabs" || parts[parts.Count - 1] == "Meshes" || parts[parts.Count - 1] == "Models")) parts.RemoveAt(parts.Count - 1);
            parts.Insert(0, folder != null ? Path.GetFileName(folder) : "Other");
            return string.Join("/", parts);
        }

        string PrefabAsset(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            string key = "prefab:" + guid;
            if (assets.ContainsKey(key)) return key;
            var s = BBSettings.instance;
            bool browsable = s.LibraryFolderOf(path) != null && !s.Excluded(path);
            string catalog = Catalog(path);

            // Blender-made (or Blender-edited) prefab whose editable source is still current
            string bbk = BBAssets.KeyFromLabels(new GUID(guid));
            string src = bbk != null ? BBAssets.SourceBlendForKey(bbk) : "";
            bool hasSrc = !string.IsNullOrEmpty(src) && File.Exists(src);
            string hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            if (bbk != null) hash += "|" + Hash128.Compute(AssetImporter.GetAtPath(path)?.userData ?? "") + (hasSrc ? "|" + File.GetLastWriteTimeUtc(src).Ticks : "");
            if (TryReuse(key, hash, browsable, hasSrc ? "Blender made" : catalog))
            {
                var cached = assets[key];
                cached.prefab = path;
                if (hasSrc && cached.kind == "blender") cached.source = src;
                Routing(cached, bbk);
                return key;
            }

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!root) return null;
            var a = new BBAsset { key = key, name = UniqueName(root.name), kind = "prefab", catalog = catalog, prefab = path, browsable = browsable, hash = hash };
            Routing(a, bbk);
            assets[key] = a;

            if (hasSrc)
            {
                var meta = BBAssets.ReadMeta(path);
                if (meta == null || string.IsNullOrEmpty(meta.parts) || meta.parts == BBAssets.PartsSignature(root))
                {
                    a.kind = "blender";
                    a.source = src;
                    a.sourceCollection = BBAssets.SourceCollection;
                    a.bbKey = bbk;
                    a.catalog = "Blender made";
                    return key;
                }
                warnings.Add($"{root.name}: its geometry was changed in Unity since Blender last wrote it, so Blender gets the Unity version (the source .blend is out of date)");
            }

            var parts = BBAssets.VisibleParts(root);
            a.hasLods = root.GetComponentsInChildren<LODGroup>(true).Any(g => g.lodCount > 1);
            foreach (var p in parts)
                a.parts.Add(new BBPart { mesh = MeshKey(p.mesh), mats = p.materials.Select(MatKey).ToArray(), m = BBConvert.Flat(p.blenderMatrix), path = p.path });
            return key;
        }

        static void Routing(BBAsset a, string bbKey)
        {
            var record = string.IsNullOrEmpty(bbKey) ? null : BBRegistry.Get(bbKey);
            a.destination = record?.destination ?? "";
            a.assetName = record?.assetName ?? (string.IsNullOrEmpty(bbKey) ? "" : BBRegistry.NormalizeName(bbKey));
            a.materialMode = record == null ? "" : string.IsNullOrEmpty(record.materialMode) ? "blender" : record.materialMode;
            a.syncTextures = a.materialMode != "unity" && (record == null || string.IsNullOrEmpty(record.materialMode) || record.syncTextures);
        }

        string MeshAsset(UMesh mesh, Material[] materials, string name)
        {
            // a mesh modelled in Blender earlier: offer it from its source, so it can be edited again with modifiers intact
            if (!modelKeyOf.TryGetValue(mesh, out string bbk)) modelKeyOf[mesh] = bbk = BBAssets.KeyOfModelMesh(mesh);
            if (bbk != null)
            {
                string pf = BBAssets.FindPrefab(bbk);
                if (pf != null && File.Exists(BBAssets.SourceBlendForKey(bbk))) { var k = PrefabAsset(pf); if (k != null) return k; }
            }
            string mk = MeshKey(mesh);
            var mks = materials.Select(MatKey).ToArray();
            string key = "mesh:" + BBUtil.Short(Hash128.Compute(mk + "|" + string.Join(",", mks)).ToString(), 16);
            if (!assets.ContainsKey(key))
                assets[key] = new BBAsset
                {
                    key = key, name = UniqueName(name), kind = "mesh", hash = mk, browsable = false,
                    parts = { new BBPart { mesh = mk, mats = mks, m = BBConvert.Flat(Matrix4x4.identity) } },
                };
            return key;
        }

        // ================================================================ meshes and materials
        string MeshKey(UMesh mesh)
        {
            if (!mesh) return "";
            if (meshKeys.TryGetValue(mesh, out var k)) return k;
            k = BBAssets.MeshId(mesh, out string hash);
            string file = $"{BBPaths.MeshCache}/{k}__{BBUtil.Short(hash)}.aamesh";
            if (!File.Exists(file))
            {
                try { BBMeshWriter.Write(mesh, file); meshesWritten++; }
                catch (Exception e) { warnings.Add($"mesh {mesh.name}: {e.Message}"); }
            }
            meshRefs[k] = new BBMeshRef { key = k, file = file };
            meshKeys[mesh] = k;
            return k;
        }

        string MatKey(Material m)
        {
            if (!m) return "";
            if (matKeys.TryGetValue(m, out var k)) return k;
            k = BBMaterialReader.KeyOf(m);
            matKeys[m] = k;
            if (!mats.ContainsKey(k)) mats[k] = reader.Describe(m, k);
            return k;
        }
    }

    /// <summary>The content rules: what counts as a mesh object worth editing in Blender (Settings > What Blender gets).</summary>
    internal class BBContentFilter
    {
        public readonly bool MeshesOnly, looseMeshes;
        public readonly string Signature;
        readonly HashSet<string> skip;

        public BBContentFilter(BBSettings s)
        {
            MeshesOnly = s.MeshesOnly;
            looseMeshes = s.includeLooseMeshes;
            Signature = s.ContentSignature;
            skip = new HashSet<string>((s.skipComponents ?? new List<string>()).Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.Ordinal);
        }

        /// <summary>go carries a component from the skip list (matched by class name).</summary>
        public bool Skipped(GameObject go)
        {
            if (skip.Count == 0) return false;
            foreach (var c in go.GetComponents<Component>())
                if (c && skip.Contains(c.GetType().Name)) return true;
            return false;
        }

        /// <summary>A prefab (scene instance root or prefab asset root) that is a static mesh: at least one visible
        /// MeshFilter + MeshRenderer, no skinned (character) meshes, and no skip-listed component on its root. Lights,
        /// particles and the like inside a prop are fine: the prefab is sent whole and its insides stay Unity's.</summary>
        public bool IsMeshPrefab(GameObject root)
        {
            if (Skipped(root)) return false;
            bool mesh = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || !ActiveUnder(r.transform, root.transform)) continue;
                if (r is SkinnedMeshRenderer smr) { if (smr.sharedMesh) return false; }
                else if (!mesh && r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh) mesh = true;
            }
            return mesh;
        }

        /// <summary>A plain (non-prefab) object with a static mesh, e.g. a road or ground mesh.</summary>
        public bool IsLooseMesh(GameObject go) =>
            looseMeshes && go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh && go.TryGetComponent<MeshRenderer>(out var mr) && mr.enabled && !Skipped(go);

        static bool ActiveUnder(Transform t, Transform root)
        {
            for (; t; t = t.parent)
            {
                if (t == root) return true;
                if (!t.gameObject.activeSelf) return false;
            }
            return true;
        }
    }
}
