using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Auleek.BlenderBridge.Editor
{
    [Serializable] public class BBResourceRecord
    {
        public string id, guid, path;
        public List<string> aliases = new List<string>();
    }

    [Serializable] public class BBAssetRecord
    {
        public string key, modelGuid, prefabGuid, modelPath, prefabPath, source, root, destination = "", assetName, origin = "", hash = "";
        public bool legacy;
        public string materialMode = "blender";
        public bool syncTextures = true;
        public List<BBResourceRecord> materials = new List<BBResourceRecord>();
        public List<BBResourceRecord> textures = new List<BBResourceRecord>();
    }

    public class BBImportRoute
    {
        public string key, assetName, destination, root, source, model, prefab;
        public bool legacy;
        public string materialMode = "blender";
        public bool syncTextures = true;
        public BBAssetRecord record;
        public string Fingerprint => string.Join("|", key, assetName, destination, root, source, model, prefab, materialMode, syncTextures ? "1" : "0");
    }

    /// <summary>Project-owned identities. Asset moves are never a side effect of an import or a profile change.</summary>
    [FilePath("ProjectSettings/BlenderBridgeRegistry.asset", FilePathAttribute.Location.ProjectFolder)]
    public class BBRegistry : ScriptableSingleton<BBRegistry>
    {
        public int version = 1;
        public List<BBAssetRecord> assets = new List<BBAssetRecord>();
        public static BBAssetRecord Get(string key) => instance.assets.FirstOrDefault(a => a.key == key);
        public static void SaveNow() => instance.Save(true);
        public static void Remove(string key) { instance.assets.RemoveAll(a => a.key == key); SaveNow(); }

        public static string NormalizeName(string value)
        {
            string s = Regex.Replace(value ?? "", @"\.\d{3}$", "").Trim();
            s = Regex.Replace(s, @"^(?:(?:SM|SK|PF|M|T)_)+", "", RegexOptions.IgnoreCase);
            return string.Join("_", Regex.Split(s, "[^A-Za-z0-9]+").Where(w => w.Length > 0)
                .Select(w => w.All(char.IsDigit) ? w.PadLeft(2, '0') : char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        public static string AssetPath(string path)
        {
            string p = (path ?? "").Replace('\\', '/').TrimEnd('/');
            if (!p.StartsWith("Assets/", StringComparison.Ordinal) || p.Split('/').Any(x => x == "." || x == ".." || x.Length == 0 || x.EndsWith(".", StringComparison.Ordinal) || x.EndsWith(" ", StringComparison.Ordinal))
                || p.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new Exception("Destination must be a project folder below Assets/ without traversal or invalid characters: " + p);
            return p;
        }

        public static string SourcePath(string path)
        {
            string p = (path ?? "").Replace('\\', '/').TrimEnd('/');
            if (!p.StartsWith("ArtSource/", StringComparison.Ordinal) || p.Split('/').Any(x => x == "." || x == ".." || x.Length == 0 || x.EndsWith(".", StringComparison.Ordinal) || x.EndsWith(" ", StringComparison.Ordinal))
                || p.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new Exception("Editable sources must be below the project-root ArtSource/ folder: " + p);
            return p;
        }

        public static string PathForGuid(string guid, string description)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(p)) throw new Exception(description + " is missing (GUID " + guid + "). Restore it or repair the registry; sync will not recreate it.");
            return p;
        }

        static string Unique(IEnumerable<string> paths, string description)
        {
            var found = paths.Distinct().ToArray();
            if (found.Length > 1) throw new Exception("Ambiguous " + description + ": " + string.Join(", ", found));
            return found.FirstOrDefault();
        }

        public static string FindModel(string key)
        {
            var rec = Get(key);
            if (rec != null && !string.IsNullOrEmpty(rec.modelGuid)) return PathForGuid(rec.modelGuid, key + " model");
            var labelled = AssetDatabase.FindAssets("l:" + BBAssets.KeyLabel + key + " t:Model")
                .Where(g => AssetDatabase.GetLabels(new GUID(g)).Contains(BBAssets.KeyLabel + key)).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            if (labelled.Length > 0) return Unique(labelled, "model for " + key);
            return Unique(AssetDatabase.FindAssets("SM_" + key + " t:Model").Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetFileNameWithoutExtension(p) == "SM_" + key && p.Replace('\\', '/').Contains("/Meshes/")), "legacy model for " + key);
        }

        public static string FindPrefab(string key)
        {
            var rec = Get(key);
            if (rec != null && !string.IsNullOrEmpty(rec.prefabGuid)) return PathForGuid(rec.prefabGuid, key + " prefab");
            var labelled = AssetDatabase.FindAssets("l:" + BBAssets.KeyLabel + key + " t:Prefab")
                .Where(g => AssetDatabase.GetLabels(new GUID(g)).Contains(BBAssets.KeyLabel + key)).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            // Source-edit prefabs can also carry the key; prefer the bridge's canonical placement prefab.
            var canonical = labelled.Where(p => AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(p)).Contains(BBAssets.Label)).ToArray();
            return Unique(canonical.Length > 0 ? canonical : labelled, "prefab for " + key);
        }

        static BBAssetRecord Discover(string key)
        {
            string model = FindModel(key);
            if (model == null) return null;
            string root = Path.GetDirectoryName(Path.GetDirectoryName(model)).Replace('\\', '/');
            var rec = new BBAssetRecord { key = key, modelGuid = AssetDatabase.AssetPathToGUID(model),
                prefabGuid = AssetDatabase.AssetPathToGUID(FindPrefab(key) ?? ""), modelPath = model, prefabPath = FindPrefab(key), root = root,
                source = root + "/Source~/" + key + ".blend", assetName = NormalizeName(key), legacy = true };
            if (AssetImporter.GetAtPath(model) is ModelImporter mi)
                foreach (var kv in mi.GetExternalObjectMap())
                {
                    string p = AssetDatabase.GetAssetPath(kv.Value);
                    if (kv.Value is Material && p.StartsWith(root + "/Materials/", StringComparison.Ordinal)
                        && Path.GetFileName(p).StartsWith("M_" + key + "_", StringComparison.Ordinal))
                        Remember(rec.materials, kv.Key.name, p);
                }
            string textureRoot = root + "/Textures/" + key;
            if (AssetDatabase.IsValidFolder(textureRoot))
                foreach (var g in AssetDatabase.FindAssets("t:Texture", new[] { textureRoot }))
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    // Full legacy filename is a stable alias, independent of where the texture is later moved.
                    Remember(rec.textures, "legacy:" + Path.GetFileName(p), p);
                }
            return rec;
        }

        public static int AdoptAllLegacy()
        {
            int count = 0;
            foreach (var key in AssetDatabase.FindAssets("t:Model").Select(g => BBAssets.KeyFromLabels(new GUID(g))).Where(k => k != null).Distinct())
            {
                if (Get(key) != null) continue;
                var rec = Discover(key);
                if (rec != null) { instance.assets.Add(rec); count++; }
            }
            SaveNow(); return count;
        }

        public static BBImportRoute Resolve(BBOutAsset a, BBSession s)
        {
            if (a.metadataOnly && a.isNew) throw new Exception("A new asset needs a model import before its destination or name can be acknowledged.");
            if (string.IsNullOrWhiteSpace(a.key) || a.key != BBUtil.Slug(a.key)) throw new Exception("Invalid bridge identity: " + a.key);
            string key = a.key;
            var rec = Get(key) ?? Discover(key);
            if (a.isNew && rec != null && !(rec.origin == s.sessionId && !string.IsNullOrEmpty(s.sessionId)))
            {
                string b = key; int n = 2;
                do { key = b + "_" + n++; } while (Get(key) != null || FindModel(key) != null || FindPrefab(key) != null);
                rec = null;
            }
            if (!a.isNew && rec == null) throw new Exception("Existing Blender asset " + key + " could not be found. Sync stopped to avoid creating a duplicate.");
            bool explicitPolicy = !string.IsNullOrEmpty(a.materialMode);
            bool recordedPolicy = rec != null && !string.IsNullOrEmpty(rec.materialMode);
            bool configuredPolicy = !string.IsNullOrEmpty(BBSettings.instance.defaultMaterialMode);
            string materialMode = explicitPolicy ? a.materialMode : rec != null ? (recordedPolicy ? rec.materialMode : "blender")
                : configuredPolicy ? BBSettings.instance.defaultMaterialMode : "blender";
            bool syncTextures = explicitPolicy ? a.syncTextures : rec != null ? !recordedPolicy || rec.syncTextures
                : !configuredPolicy || BBSettings.instance.defaultSyncTextures;
            if (materialMode != "unity" && materialMode != "blender") throw new Exception("Choose Use Unity materials or Sync Blender materials for " + a.name + ".");
            if (materialMode == "unity") syncTextures = false;
            string destination = !string.IsNullOrEmpty(a.destination) ? a.destination : rec?.destination;
            if (string.IsNullOrEmpty(destination) && rec == null) destination = DefaultDestination(s);
            var profile = BBSettings.instance.destinationProfiles.FirstOrDefault(p => p.id == destination);
            if (!string.IsNullOrEmpty(destination) && profile == null) throw new Exception("Destination profile '" + destination + "' no longer exists. Choose a destination for " + a.name + ".");
            string name = !string.IsNullOrWhiteSpace(a.assetName) ? a.assetName : rec?.assetName;
            if (string.IsNullOrWhiteSpace(name)) name = NormalizeName(a.name ?? key);
            if (string.IsNullOrEmpty(name) || name != NormalizeName(name)) throw new Exception("Choose an asset name using PascalCase words joined with underscores: " + name);
            if (profile != null && !string.IsNullOrWhiteSpace(profile.setCode) && !name.StartsWith(profile.setCode + "_", StringComparison.Ordinal))
                name = profile.setCode + "_" + name;
            if (name != NormalizeName(name)) throw new Exception("The profile set code and asset name must form PascalCase words joined by underscores: " + name);
            if (rec != null)
            {
                string model = PathForGuid(rec.modelGuid, key + " model") ?? rec.modelPath;
                string prefab = PathForGuid(rec.prefabGuid, key + " prefab") ?? rec.prefabPath;
                if (string.IsNullOrEmpty(rec.modelGuid)) VerifyPendingPath(model, key);
                if (string.IsNullOrEmpty(rec.prefabGuid)) VerifyPendingPath(prefab, key);
                return new BBImportRoute { key = key, assetName = name, destination = destination ?? "", root = rec.root,
                    source = rec.source, model = model, prefab = prefab ?? rec.root + "/Prefabs/PF_" + key + ".prefab", legacy = rec.legacy, record = rec,
                    materialMode = materialMode, syncTextures = syncTextures };
            }
            if (profile == null) throw new Exception("Choose a destination for " + a.name + " in Blender or the Unity import review.");
            string root = AssetPath(profile.assetRoot), sourceRoot = SourcePath(profile.sourceRoot);
            string source = sourceRoot + "/" + name + ".blend";
            var route = new BBImportRoute { key = key, assetName = name, destination = profile.id, root = root, source = source,
                model = root + "/Meshes/SM_" + name + ".fbx", prefab = root + "/Prefabs/PF_" + name + ".prefab", materialMode = materialMode, syncTextures = syncTextures };
            string original = !string.IsNullOrEmpty(a.prefab) && a.prefab.StartsWith("prefab:") ? AssetDatabase.GUIDToAssetPath(a.prefab.Substring(7)) : "";
            bool sourceEdit = a.mode == "source" && original.StartsWith("Assets/") && original.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
            if (sourceEdit) route.prefab = original;
            AssertFree(route.model); if (!sourceEdit) AssertFree(route.prefab);
            if (File.Exists(BBPaths.Full(source))) throw new Exception("Source destination already exists: " + source);
            return route;
        }

        static string DefaultDestination(BBSession s)
        {
            var scene = BBSettings.instance.For(s.sceneGuid, false);
            return !string.IsNullOrEmpty(scene?.defaultDestination) ? scene.defaultDestination : BBSettings.instance.defaultDestination;
        }

        public static void AssertFree(string path)
        {
            AssetPath(path);
            if (File.Exists(BBPaths.Full(path)) || File.Exists(BBPaths.Full(path) + ".meta") || Directory.Exists(BBPaths.Full(path)) || !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets)))
                throw new Exception("Destination is occupied by another asset: " + path);
        }

        static void VerifyPendingPath(string path, string key)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("Incomplete registry identity for " + key);
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset && AssetDatabase.GetLabels(asset).Contains(BBAssets.KeyLabel + key)) return;
            AssertFree(path);
        }

        public static void Begin(BBImportRoute route, BBOutAsset a, BBSession s)
        {
            if (Get(route.key) == null)
            {
                var rec = route.record ?? new BBAssetRecord { key = route.key, root = route.root, source = route.source,
                    assetName = route.assetName, destination = route.destination, origin = s.sessionId, hash = a.hash,
                    modelPath = route.model, prefabPath = route.prefab, materialMode = route.materialMode, syncTextures = route.syncTextures };
                if (a.mode == "source" && !string.IsNullOrEmpty(a.prefab) && a.prefab.StartsWith("prefab:")
                    && AssetDatabase.GUIDToAssetPath(a.prefab.Substring(7)) == route.prefab)
                    rec.prefabGuid = a.prefab.Substring(7);
                instance.assets.Add(rec); route.record = rec;
            }
            else route.record = Get(route.key);
            // Persist partial imports after every artifact so retry never overwrites an unrelated file.
            SaveNow();
        }

        public static void Commit(BBImportRoute route, BBOutAsset a, BBSession s, string model, string prefab)
        {
            Begin(route, a, s);
            var r = route.record;
            r.modelGuid = AssetDatabase.AssetPathToGUID(model); r.prefabGuid = AssetDatabase.AssetPathToGUID(prefab);
            r.destination = route.destination; r.assetName = route.assetName; r.hash = a.hash;
            r.materialMode = route.materialMode; r.syncTextures = route.syncTextures;
            SaveNow();
        }

        public static void Remember(List<BBResourceRecord> resources, string id, string path)
        {
            var r = resources.FirstOrDefault(x => x.id == id);
            if (r == null) { r = new BBResourceRecord { id = id }; resources.Add(r); }
            if (r.aliases == null) r.aliases = new List<string>();
            if (!string.IsNullOrEmpty(r.path) && r.path != path && !r.aliases.Contains(r.path)) r.aliases.Add(r.path);
            r.guid = AssetDatabase.AssetPathToGUID(path); r.path = path;
        }

        public static string ResourcePath(BBImportRoute route, bool texture, string id, string suggested, string legacyAlias = null)
        {
            var list = texture ? route.record.textures : route.record.materials;
            var r = list.FirstOrDefault(x => x.id == id) ?? (legacyAlias == null ? null : list.FirstOrDefault(x => x.id == legacyAlias));
            if (r != null) return PathForGuid(r.guid, route.key + " resource " + id);
            AssertFree(suggested); return suggested;
        }

        public static string ResolveTextureAlias(string path)
        {
            var aliases = instance.assets.SelectMany(a => a.textures).Where(t => t.path == path || (t.aliases != null && t.aliases.Contains(path))).Select(t => t.guid).Distinct().ToArray();
            if (aliases.Length > 1) throw new Exception("Ambiguous legacy texture path " + path);
            return aliases.Length == 1 ? PathForGuid(aliases[0], path) : path;
        }

        public static List<BBTextureAlias> TextureAliases()
        {
            return instance.assets.SelectMany(a => a.textures).SelectMany(t =>
            {
                string current = AssetDatabase.GUIDToAssetPath(t.guid);
                return (t.aliases ?? new List<string>()).Concat(new[] { t.path }).Where(p => !string.IsNullOrEmpty(p) && p != current && !string.IsNullOrEmpty(current))
                    .Select(p => new BBTextureAlias { from = p, to = current });
            }).GroupBy(a => a.from).Select(g =>
            {
                if (g.Select(a => a.to).Distinct().Count() != 1) throw new Exception("Ambiguous texture alias " + g.Key);
                return g.First();
            }).ToList();
        }
    }
}
