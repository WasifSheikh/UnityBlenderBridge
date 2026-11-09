using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Auleek.BlenderBridge.Editor
{
    public static class BBSavedUpdates
    {
        [Serializable] class Saved { public string sessionId, baseHash, result; }
        public static string Hash(string path)
        {
            using(var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }
        public static bool Promote(string key)
        {
            string pointer=BBPaths.OutDir(key)+"/saved-latest.json";
            if(!File.Exists(pointer) || File.Exists(BBPaths.ImportProgress(key))) return false;
            var saved=BBUtil.Read<Saved>(pointer);
            var session=BBUtil.Read<BBSession>(BBPaths.SessionJson(key));
            if(saved==null || session==null) return false;
            string payload=Path.GetFullPath(saved.result), root=Path.GetFullPath(BBPaths.OutDir(key)+"/saved")+Path.DirectorySeparatorChar;
            if(!payload.StartsWith(root,StringComparison.OrdinalIgnoreCase) || !File.Exists(payload)) return false;
            if(saved.sessionId!=session.sessionId || saved.baseHash!=Hash(BBPaths.SessionJson(key)))
            {
                File.Move(pointer,pointer+".stale-"+Guid.NewGuid().ToString("N"));
                Debug.LogWarning("[Blender Bridge] Saved Blender update uses an older Unity snapshot. Reopen the existing Blender session and sync again; the saved .blend has been preserved.");
                BBWindow.ShowPlan(new BBPlan { key=key, session=session, sceneName=session.scene,
                    error="Saved Blender update is based on an older Unity snapshot. Reopen the existing Blender session and sync again. Your .blend and exported snapshot have been preserved." });
                return false;
            }
            string result=BBPaths.ResultJson(key);
            if(File.Exists(result) && File.GetLastWriteTimeUtc(result)>=File.GetLastWriteTimeUtc(pointer)) { File.Delete(pointer); return false; }
            // Same-directory atomic publication; payload files remain immutable for import/retry.
            string tmp=result+".saved-tmp"; File.Copy(payload,tmp,true);
            if(File.Exists(result)) File.Replace(tmp,result,null); else File.Move(tmp,result);
            File.Delete(pointer); return true;
        }

        /// <summary>Every Blender save writes a complete update (result.json + exported models) into out/saved/&lt;id&gt;/.
        /// Once a sync is applied, those folders are spent; without this they pile up for the life of the session. Folders
        /// a pointer still names (the latest save, or a stale one kept for recovery) and very fresh ones (a save that is
        /// being written right now) are kept.</summary>
        public static void PruneSpent(string key)
        {
            try
            {
                string outDir = BBPaths.OutDir(key), root = Path.Combine(outDir, "saved");
                if (!Directory.Exists(root)) return;
                var keep = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ptr in Directory.GetFiles(outDir, "saved-latest.json*"))
                {
                    var s = BBUtil.Read<Saved>(ptr);
                    if (!string.IsNullOrEmpty(s?.result)) keep.Add(Path.GetFullPath(Path.GetDirectoryName(s.result)).TrimEnd(Path.DirectorySeparatorChar));
                }
                foreach (var d in Directory.GetDirectories(root))
                    if (!keep.Contains(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar)) && (DateTime.Now - Directory.GetLastWriteTime(d)).TotalMinutes > 2)
                        Directory.Delete(d, true);
            }
            catch (Exception e) { Debug.LogWarning("[Blender Bridge] could not clean up saved Blender updates: " + e.Message); }
        }
    }
}
