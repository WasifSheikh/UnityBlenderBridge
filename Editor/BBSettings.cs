using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Shared project settings (ProjectSettings/BlenderBridge.asset, committed): which root objects of each
    /// scene Blender may edit, and which folders feed the Blender asset library. The Blender executable is per
    /// machine (EditorPrefs).</summary>
    [FilePath("ProjectSettings/BlenderBridge.asset", FilePathAttribute.Location.ProjectFolder)]
    public class BBSettings : ScriptableSingleton<BBSettings>
    {
        [Serializable] public class SceneRoots { public string sceneGuid; public List<string> rootIds = new List<string>(); }

        public List<SceneRoots> scenes = new List<SceneRoots>();
        public List<string> libraryFolders = new List<string> { "Assets" };
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

        const string ExeKey = "AA.BlenderBridge.BlenderExe";

        public static string BlenderExe
        {
            get
            {
                string p = EditorPrefs.GetString(ExeKey, "");
                return File.Exists(p) ? p : Detect();
            }
            set => EditorPrefs.SetString(ExeKey, value ?? "");
        }

        static string Detect()
        {
            foreach (var root in new[] { @"C:\Program Files\Blender Foundation", @"C:\Program Files (x86)\Blender Foundation" })
            {
                if (!Directory.Exists(root)) continue;
                var exe = Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                                   .Select(d => Path.Combine(d, "blender.exe")).FirstOrDefault(File.Exists);
                if (exe != null) return exe;
            }
            foreach (var p in new[] { "/Applications/Blender.app/Contents/MacOS/Blender", "/usr/bin/blender" })
                if (File.Exists(p)) return p;
            return "";
        }
    }
}
