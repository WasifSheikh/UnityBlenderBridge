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
    }
}
