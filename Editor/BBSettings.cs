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
        [Serializable] public class SceneRoots { public string sceneGuid; public List<string> rootIds = new List<string>(); }

        public List<SceneRoots> scenes = new List<SceneRoots>();
        /// <summary>Prefabs under these folders appear in Blender's Asset Browser.</summary>
        public List<string> libraryFolders = new List<string> { "Assets" };
        /// <summary>Prefabs whose path contains any of these are left out of the Asset Browser (e.g. "/_Archive/").</summary>
        public List<string> libraryExcludes = new List<string>();
        public const string DefaultAssetRoot = "Assets/BlenderBridge/{scene}";
        /// <summary>Where assets modelled in Blender are written; {scene} is replaced by the scene name.</summary>
        public string assetRoot = DefaultAssetRoot;
        public bool autoApply = true;

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
