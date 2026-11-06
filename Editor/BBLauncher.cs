using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Auleek.BlenderBridge.Editor
{
    /// <summary>Starts Blender: a background run that brings the asset library up to date (Unity waits, with a progress
    /// bar; skipped when library.json is unchanged since the last good build), then the interactive session. Blender
    /// runs BlenderAddon~/bb_entry.py with the command and file after "--", so no path is ever pasted into Python code.</summary>
    public static class BBLauncher
    {
        static readonly Dictionary<string, Process> open = new Dictionary<string, Process>();

        static string Q(string s) => "\"" + s.Replace("\"", "") + "\"";   // Windows paths can't contain quotes
        static string Run(string command, string json) => $"--python {Q(BBPaths.Entry)} -- {command} {Q(json)}";

        public static bool Ready(out string error)
        {
            error = null;
            if (!File.Exists(BBSettings.BlenderExe)) error = "Blender not found. Set blender.exe in Settings.";
            else if (!File.Exists(BBPaths.Entry)) error = "The Blender side (BlenderAddon~/bb_entry.py) is missing from the Blender Bridge package: " + BBPaths.Tools;
            return error == null;
        }

        /// <summary>Blender started by this editor that still has this scene's file open.</summary>
        public static bool IsOpen(string key)
        {
            if (!open.TryGetValue(key, out var p)) return false;
            try { if (!p.HasExited) return true; } catch { }
            open.Remove(key);
            return false;
        }

        public static bool BuildLibrary(out string error)
        {
            if (!Ready(out error)) return false;
            string hash = Hash128.Compute(File.ReadAllText(BBPaths.LibraryJson) + "|" + BBSettings.BlenderExe).ToString();
            if (File.Exists(BBPaths.LibraryBlend) && File.Exists(BBPaths.LibraryBuilt) && File.ReadAllText(BBPaths.LibraryBuilt).Trim() == hash) return true;

            var psi = new ProcessStartInfo(BBSettings.BlenderExe, "-b --factory-startup --python-exit-code 1 " + Run("build_library", BBPaths.LibraryJson))
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
            File.WriteAllText(BBPaths.LibraryLog, text);
            if (error == null && !text.Contains("AA_LIBRARY_DONE")) error = "the library build did not finish";
            if (error != null)
            {
                Debug.LogError($"[Blender Bridge] {error}. Log: BlenderSessions/_library/library_build.log\n" +
                               string.Join("\n", text.Split('\n').Reverse().Take(25).Reverse()));
                if (File.Exists(BBPaths.LibraryBuilt)) File.Delete(BBPaths.LibraryBuilt);
                return false;
            }
            File.WriteAllText(BBPaths.LibraryBuilt, hash);
            return true;
        }

        /// <summary>resume: open the existing .blend; otherwise build a fresh one from session.json.</summary>
        public static bool OpenSession(string key, bool resume, out string error)
        {
            if (!Ready(out error)) return false;
            string json = BBPaths.SessionJson(key);
            string args = resume ? Q(BBPaths.Blend(key)) + " " + Run("resume", json) : Run("open_session", json);
            var p = Process.Start(new ProcessStartInfo(BBSettings.BlenderExe, args) { UseShellExecute = false, WorkingDirectory = BBPaths.SessionDir(key) });
            if (p != null) open[key] = p;
            return true;
        }

        /// <summary>"Blender 5.2.1" from the executable, for the settings panel (cached per path).</summary>
        public static string VersionLabel(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return "not found";
            if (versions.TryGetValue(exe, out var v)) return v;
            try { v = FileVersionInfo.GetVersionInfo(exe).ProductVersion; } catch { }
            if (string.IsNullOrEmpty(v)) v = BBSettings.VersionOf(exe).ToString();
            return versions[exe] = "Blender " + v;
        }
        static readonly Dictionary<string, string> versions = new Dictionary<string, string>();
    }
}
