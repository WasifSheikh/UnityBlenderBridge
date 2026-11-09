using System;
using System.Collections.Generic;

namespace Auleek.BlenderBridge.Editor
{
    // =====================================================================================================
    // Files exchanged with Blender (JsonUtility on this side, json on the Python side). Field names are the
    // protocol: rename one here and in BlenderAddon~/aa_bridge together, and bump BBFormat.Version.
    //
    //   Unity -> Blender   session.json (scene snapshot), library.json (asset library), status.json
    //   Blender -> Unity   out/result.json (+ out/assets/<key>/ exported models)
    //   Unity -> Blender   applied.json (ids of what Unity created, after a sync was applied)
    // =====================================================================================================

    public static class BBFormat
    {
        /// <summary>2: AAM2 meshes (UV2, colours), session keys with scene GUID, id remaps, status.json.</summary>
        public const int Version = 2;
    }

    // ---------------------------------------------------------------- Unity -> Blender
    [Serializable] public class BBDestinationProfile
    {
        public string id = "", name = "", assetRoot = "", sourceRoot = "", setCode = "";
    }
    [Serializable] public class BBTextureAlias { public string from, to; }

    [Serializable] public class BBLight { public string type = ""; public float[] color; public float intensity, range, spot; }

    [Serializable] public class BBNode
    {
        public string id;          // GlobalObjectId of the GameObject
        public string name;
        public string parent = ""; // parent node id ("" = top level)
        public string asset = "";  // library asset key ("" = no visual)
        public string kind;        // instance | group | light | marker
        public bool editable;
        public bool active = true;
        public float[] m;          // world matrix in Blender space, column-major
        public BBLight light;
    }

    [Serializable] public class BBSession
    {
        public int version = BBFormat.Version;
        public string sessionId, key, scene, scenePath, sceneGuid, created, library, libraryJson, libraryName, blend, project,
                      assetRoot, outDir, appliedPath, statusPath, tools;
        public List<BBNode> nodes = new List<BBNode>();
        public List<BBDestinationProfile> profiles = new List<BBDestinationProfile>();
        public string defaultDestination = "";
        public string defaultMaterialMode = "blender";
        public bool defaultSyncTextures = true;
        public List<BBTextureAlias> textureAliases = new List<BBTextureAlias>();
    }

    [Serializable] public class BBPart { public string mesh; public string[] mats; public float[] m; public string path = ""; }

    [Serializable] public class BBAsset
    {
        public string key, name, kind, catalog = "", hash, prefab = "", source = "", sourceCollection = "", bbKey = "";
        public string destination = "", assetName = "";
        public string materialMode = "";
        public bool syncTextures = true;
        public bool browsable, hasLods;
        public List<BBPart> parts = new List<BBPart>();
    }

    [Serializable] public class BBMaterial
    {
        public string key, name, shader, baseMap = "", normalMap = "", emissionMap = "", alpha = "opaque";
        public float[] color, emission, tiling;
        public float metallic, smoothness = 0.5f, normalScale = 1, cutoff = 0.5f;
    }

    [Serializable] public class BBMeshRef { public string key, file; }

    [Serializable] public class BBLibrary
    {
        public int version = BBFormat.Version;
        public string blend, dir, libraryName;
        public List<BBMeshRef> meshes = new List<BBMeshRef>();
        public List<BBMaterial> materials = new List<BBMaterial>();
        public List<BBAsset> assets = new List<BBAsset>();
        public List<string> keep = new List<string>();   // asset keys other scenes' Blender files still link
        // Unity-only bookkeeping (Blender ignores these): library prefabs left out by the content rules, so the next
        // export can skip them without loading them again.
        public string contentFilter = "";
        public List<BBSkipped> skipped = new List<BBSkipped>();
    }

    [Serializable] public class BBSkipped { public string key, hash; }

    /// <summary>Unity's state of the session, read by Blender (status line, and "undone" blocks syncing).</summary>
    [Serializable] public class BBStatus
    {
        public string sessionId, state = BBStatus.Ok, message = "", time;
        public const string Ok = "ok", Undone = "undone";
    }

    // ---------------------------------------------------------------- Blender -> Unity ("Sync to Unity")
    [Serializable] public class BBOutNode { public string id = "", name = "", parent = "", tmp = "", of = "", asset = ""; public float[] m; }

    [Serializable] public class BBOutMaterial
    {
        public string id = ""; // stable owned material identity; older results use the slot name as an alias
        public string name, unity = "", baseMap = "", normalMap = "", maskMap = "", emissionMap = "", alpha = "opaque";
        public float[] color, emission;
        public float metallic, smoothness = 0.5f, normalScale = 1, cutoff = 0.5f;
    }

    [Serializable] public class BBOutPart { public string @object = "", path = ""; public bool known; public float[] m; public int depth; }

    [Serializable] public class BBOutAsset
    {
        public string key, name, fbx, source, sourceCollection, collider = "mesh", hash, dir;
        public string destination = "", assetName = "";
        public string materialMode = "";
        public bool syncTextures = true;
        public bool metadataOnly;
        public string node = "";   // the Unity object whose geometry this is (Make editable), if any
        public string mode = "";   // prefab instances: "variant" (this instance only) or "source" (the prefab, all copies)
        public string prefab = ""; // "prefab:<guid>" of the original prefab
        public bool pivotMoved, isNew, hasUv2;
        public List<BBOutPart> parts = new List<BBOutPart>();
        public List<string> removed = new List<string>();  // prefab part paths deleted in Blender
        public List<BBOutMaterial> materials = new List<BBOutMaterial>();
    }

    [Serializable] public class BBResult
    {
        public int version;
        public string sessionId, written, blend;
        public List<BBOutNode> nodes = new List<BBOutNode>();   // every editable node Blender still has
        public List<BBOutNode> dups = new List<BBOutNode>();    // Shift+D copies: of = source id
        public List<BBOutNode> added = new List<BBOutNode>();   // new instances of library assets / new Blender assets
        public List<BBOutAsset> assets = new List<BBOutAsset>(); // Blender-made assets to (re)build
    }

    // ---------------------------------------------------------------- Unity -> Blender, after applying
    [Serializable] public class BBIdMap { public string tmp, id, asset = "", hash = "", destination = "", assetName = "", materialMode = ""; public bool syncTextures = true; }
    [Serializable] public class BBRemap { public string from, to; }

    [Serializable] public class BBApplied
    {
        public string sessionId, applied, summary;
        public List<BBIdMap> ids = new List<BBIdMap>();
        public List<BBRemap> remap = new List<BBRemap>();   // Unity ids that changed (e.g. an instance switched to a variant)
    }

    // ---------------------------------------------------------------- Unity-only bookkeeping
    /// <summary>Assets already imported for the pending result.json: a failed Apply resumes instead of importing twice.</summary>
    [Serializable] public class BBImportProgress
    {
        public string sessionId, written;
        public List<BBImportDone> done = new List<BBImportDone>();
    }

    [Serializable] public class BBImportDone { public string key, finalKey, prefab, model, hash, routing, prefabGuid, modelGuid; }

    /// <summary>Stored in a Blender-made prefab's importer userData: what Blender last wrote into it.</summary>
    [Serializable] public class BBAssetMeta { public string collider = "mesh", parts = ""; }

    [Serializable] public class BBJournalEntry { public string time, scene, summary, backup; }
}
