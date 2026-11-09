using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Shared project settings (ProjectSettings/BlenderBridge.asset, committed): which root objects of each
    /// scene Blender may edit, which folders feed the Blender asset library, where Blender-made assets go. The Blender
    /// executable is per machine (EditorPrefs).</summary>
    [FilePath("ProjectSettings/BlenderBridge.asset", FilePathAttribute.Location.ProjectFolder)]
    public class BBSettings : ScriptableSingleton<BBSettings>
    {
        [Serializable] public class SceneRoots { public string sceneGuid; public string defaultDestination = ""; public List<string> rootIds = new List<string>(); }

        public List<SceneRoots> scenes = new List<SceneRoots>();
        /// <summary>Prefabs under these folders appear in Blender's Asset Browser.</summary>
        public List<string> libraryFolders = new List<string> { "Assets" };
        /// <summary>Prefabs whose path contains any of these are left out of the Asset Browser (e.g. "/_Archive/").</summary>
        public List<string> libraryExcludes = new List<string>();
        public const string DefaultAssetRoot = "Assets/BlenderBridge/{scene}";
        /// <summary>Where assets modelled in Blender are written; {scene} is replaced by the scene name.</summary>
        public string assetRoot = DefaultAssetRoot;
        /// <summary>Project-specific art destinations. Empty keeps existing projects compatible; new routed assets
        /// require a selected profile once profiles have been configured.</summary>
        public List<BBDestinationProfile> destinationProfiles = new List<BBDestinationProfile>();
        public string defaultDestination = "";
        /// <summary>Defaults apply only to new bridge identities; existing assets retain their recorded policy.</summary>
        public string defaultMaterialMode = "blender";
        public bool defaultSyncTextures = true;
        public bool autoApply = true;

        // ---------------------------------------------------------------- what Blender gets
        public const string ContentMeshes = "meshes", ContentAll = "all";
        /// <summary>"meshes": only mesh prefabs (props, buildings, roads...) and, if <see cref="includeLooseMeshes"/>, plain
        /// mesh objects go to Blender; lights, volumes, probes, cameras, FX and empty helper objects stay in Unity, untouched.
        /// "all": the old behaviour (every object, lights as lights, the rest as empties).</summary>
        public string sceneContent = ContentMeshes;
        /// <summary>Plain (non-prefab) objects with a MeshFilter + MeshRenderer, e.g. road and ground meshes.</summary>
        public bool includeLooseMeshes = true;
        /// <summary>Objects (or prefab roots) carrying one of these components are left in Unity even when they have a mesh.
        /// Matched by class name, so render-pipeline types need no assembly reference.</summary>
        public List<string> skipComponents = new List<string>(DefaultSkipComponents);
        public static readonly string[] DefaultSkipComponents =
        {
            "Volume", "ProbeVolume", "ProbeAdjustmentVolume", "ProbeVolumePerSceneData", "ReflectionProbe", "LightProbeGroup",
            "LightProbeProxyVolume", "LocalVolumetricFog", "DecalProjector", "Camera", "PlayableDirector", "Canvas",
            "VisualEffect", "Terrain", "TextMeshPro", "TextMesh",
        };

        public bool MeshesOnly => sceneContent != ContentAll;

        /// <summary>Changes whenever the content rules change, so cached decisions about library prefabs are redone.</summary>
        public string ContentSignature => MeshesOnly
            ? "meshes|" + includeLooseMeshes + "|" + string.Join(",", skipComponents.Select(x => x.Trim()).Where(x => x.Length > 0).OrderBy(x => x, StringComparer.Ordinal))
            : "all";

        public void SaveNow() => Save(true);

        public SceneRoots For(string sceneGuid, bool create)
        {
            var s = scenes.FirstOrDefault(x => x.sceneGuid == sceneGuid);
            if (s == null && create) { s = new SceneRoots { sceneGuid = sceneGuid }; scenes.Add(s); }
            return s;
        }

        public bool Excluded(string assetPath) => libraryExcludes.Any(x => !string.IsNullOrWhiteSpace(x) && assetPath.IndexOf(x.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>The library folder an asset is under (null if none).</summary>
        public string LibraryFolderOf(string assetPath) =>
            libraryFolders.Select(f => f.Trim().TrimEnd('/')).Where(f => f.Length > 0 && (assetPath + "/").StartsWith(f + "/", StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(f => f.Length).FirstOrDefault();

        // ---------------------------------------------------------------- Blender executable (per PC)
        const string ExeKey = "AA.BlenderBridge.BlenderExe";
        static string detected;

        public static string BlenderExe
        {
            get
            {
                string p = EditorPrefs.GetString(ExeKey, "");
                return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : (detected ??= Detect());
            }
            set => EditorPrefs.SetString(ExeKey, value ?? "");
        }

        public static bool BlenderExeIsDetected => !File.Exists(EditorPrefs.GetString(ExeKey, ""));

        /// <summary>Newest Blender in the usual install places (Program Files by version, Steam, macOS, Linux).</summary>
        static string Detect()
        {
            var found = new List<string>();
            foreach (var root in new[] { @"C:\Program Files\Blender Foundation", @"C:\Program Files (x86)\Blender Foundation" })
                if (Directory.Exists(root))
                    found.AddRange(Directory.GetDirectories(root).Select(d => Path.Combine(d, "blender.exe")).Where(File.Exists));
            found = found.OrderByDescending(VersionOf).ToList();
            found.AddRange(new[] { @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe", "/Applications/Blender.app/Contents/MacOS/Blender",
                                   "/usr/bin/blender", "/usr/local/bin/blender", "/snap/bin/blender" }.Where(File.Exists));
            return found.FirstOrDefault() ?? "";
        }

        /// <summary>"…\Blender 5.2\blender.exe" -> 5.2 (folder names sort wrongly as text: 4.10 < 4.9).</summary>
        public static Version VersionOf(string exe)
        {
            string dir = Path.GetFileName(Path.GetDirectoryName(exe) ?? "");
            var digits = new string(dir.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return Version.TryParse(digits.Contains('.') ? digits : digits + ".0", out var v) ? v : new Version(0, 0);
        }
    }
}
