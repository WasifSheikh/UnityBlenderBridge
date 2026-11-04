using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Starts Blender: a background run that brings the asset library up to date (Unity waits, with a
    /// progress bar), then the interactive session. The Blender side lives in this package's BlenderAddon~ folder (aa_bridge);
    /// nothing needs installing by hand.</summary>
    public static class BBLauncher
    {
        static string Py(string fn, string arg) =>
            $"import sys; sys.dont_write_bytecode = True; sys.path.insert(0, r'{BBPaths.Tools}'); import aa_bridge.launch as L; L.{fn}(r'{arg}')";

        public static bool Ready(out string error)
        {
            error = null;
            if (!File.Exists(BBSettings.BlenderExe)) error = "Blender not found. Set blender.exe in the Blender Bridge window.";
            else if (!File.Exists(BBPaths.Tools + "/aa_bridge/__init__.py")) error = "The Blender add-on (BlenderAddon~/aa_bridge) is missing from the Blender Bridge package: " + BBPaths.Tools;
            return error == null;
        }

        public static bool BuildLibrary(out string error)
        {
            if (!Ready(out error)) return false;
            var psi = new ProcessStartInfo(BBSettings.BlenderExe,
                $"-b --factory-startup --python-exit-code 1 --python-expr \"{Py("build_library", BBPaths.LibraryJson)}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                WorkingDirectory = BBPaths.LibraryDir,
            };
            var log = new StringBuilder();
            string msg = "Starting Blender";
            float frac = 0f;
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (log)
                    {
                        log.AppendLine(e.Data);
                        if (!e.Data.StartsWith("AA_PROGRESS ")) return;
                        var parts = e.Data.Split(new[] { ' ' }, 4);
                        if (parts.Length == 4 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0)
                        { frac = a / b; msg = parts[3]; }
                    }
                };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(100))
                {
                    string m; float f;
                    lock (log) { m = msg; f = frac; }
                    if (EditorUtility.DisplayCancelableProgressBar("Blender Bridge: updating the asset library", m, f))
                    {
                        try { p.Kill(); } catch { }
                        error = "cancelled";
                        break;
                    }
                }
                p.WaitForExit();
                EditorUtility.ClearProgressBar();
                if (error == null && p.ExitCode != 0) error = $"the library build failed (exit {p.ExitCode})";
            }
            string text;
            lock (log) text = log.ToString();
            File.WriteAllText(BBPaths.LibraryDir + "/library_build.log", text);
            if (error == null && !text.Contains("AA_LIBRARY_DONE")) error = "the library build did not finish";
            if (error != null)
                Debug.LogError($"[Blender Bridge] {error}. Log: BlenderSessions/_library/library_build.log\n" +
                               string.Join("\n", text.Split('\n').Reverse().Take(25).Reverse()));
            return error == null;
        }

        /// <summary>resume: open the existing .blend; otherwise build a fresh one from session.json.</summary>
        public static bool OpenSession(string scene, bool resume, out string error)
        {
            if (!Ready(out error)) return false;
            string json = BBPaths.SessionJson(scene);
            string args = resume
                ? $"\"{BBPaths.Blend(scene)}\" --python-expr \"{Py("resume", json)}\""
                : $"--python-expr \"{Py("open_session", json)}\"";
            Process.Start(new ProcessStartInfo(BBSettings.BlenderExe, args) { UseShellExecute = false, WorkingDirectory = BBPaths.SessionDir(scene) });
            return true;
        }
    }
}
