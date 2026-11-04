# Blender Bridge

Edit Unity scenes with Blender's tools. **The Unity scene is the only thing that matters**: the Blender file is
throwaway and can be rebuilt from Unity at any time. Nothing in `BlenderSessions/` is committed.

## Install

Unity 6, Blender 4.2 or newer (Windows). In the project's `Packages/manifest.json`:

```json
"com.auleek.blenderbridge": "https://github.com/<owner>/UnityBlenderBridge.git#v0.1.0"
```

Change the tag to update. The first time you open the window, set **Blender (this PC)** if it isn't found.

### Working on the bridge itself

Clone this repo into the game's `Packages/` folder (e.g. `Packages/com.auleek.blenderbridge`) and git-ignore that
folder in the game repo. Unity uses an embedded package instead of the one in `manifest.json`, so you edit the bridge
live while everyone else stays on the tagged version. Commit and tag here, then bump the tag in the game's manifest.

## Use it

1. Unity: **Tools > Blender Bridge** (or your project's own menu entry), then **Open in Blender**.
   - First time per scene, tick which root objects Blender may edit (*Editable in Blender*). Everything else comes along
     as locked context. The choice is saved for the team in `ProjectSettings/BlenderBridge.asset`.
   - If a Blender file for the scene already exists you can continue it or start fresh. Lost it? Just open again.
2. Blender: work as usual.
   - Move, rotate, scale, delete, rename, Shift+D anything in *Unity (editable)*.
   - To model a Unity object (Edit Mode, extrude, loop cuts...): select it, **Unity tab > Make editable**. Unity objects
     come in as linked instances; this turns them into local geometry. Unity materials stay the real ones, and later
     edits come back from the saved source (modifiers intact). On sync:
     - a plain mesh object keeps its components and collider and gets the new mesh;
     - for a **prefab instance** you choose:
       - **Only this one (new variant)**: a Prefab Variant of the original, used by this instance. It inherits every
         component of the original prefab and still follows later changes to it.
       - **The prefab itself (all copies)**: the geometry goes into the original prefab, so every copy in every scene
         changes. The prefab is backed up first. Model files and package prefabs can't be edited this way; they get a
         variant instead.
     Parts are matched to the prefab's children by their path; new objects become new parts, deleted parts are switched off.
   - Drag project prefabs in from the Asset Browser, library **AA Unity** (one catalog per Unity folder).
   - Model new things. Every new top-level object with geometry becomes its own asset; its origin is the pivot.
     Custom property `collider` on it: `mesh` (default), `box` or `none`.
3. Blender: **Unity tab > Sync to Unity**. Unity applies it within a few seconds (or shows a preview if something
   conflicts, or if auto-apply is off). One Ctrl+Z in Unity undoes the scene part; a backup of the scene is kept in
   `SyncBackups/BlenderBridge/`.

## Assemble sections from multiple artists

Copy the sections into the same bridge session, then sync once. Keep each reusable prop as a separate
object, a mesh hierarchy under its own root, or a Blender collection instance. Matching exported geometry,
materials, part layout and collider settings share one prefab in that sync. Each copy keeps its own scene
transform. A plain Empty above a mesh hierarchy makes that hierarchy one asset. Use Blender Collections
for section organization, or set the Blender custom property `map_group` to true on an Empty that is
only used to organize a section. Its child assets keep their world transforms in Unity.

For Blender collection instances, each source collection is a reusable prefab. Pack external textures before
handing off the source files. Keep the same geometry and pivot for copies that should share a prefab.

## What Unity makes from a Blender model (key K)

Under the **Asset root** setting (default `Assets/BlenderBridge/{scene}`, `{scene}` = scene name): `Meshes/SM_K.fbx`, `Materials/M_K_<material>.mat` (URP Lit),
`Textures/K/T_K_<material>_BC|N|MS|E.png` (textures already in the project are reused, not copied),
`Prefabs/PF_K.prefab` (static, with colliders, label `BlenderAsset`), and `Source~/K.blend`, the editable source the
next Blender session uses. Re-syncing an edited asset updates these in place, so every instance follows.

## Good to know

- Pivots: the Blender object origin is the Unity pivot. Set Origin before syncing to choose it. Rotation and scale stay
  on the Unity transform unless you apply them in Blender. Moving the pivot of a shared prefab ("all copies") shifts the
  geometry of the other copies; Unity holds that sync for review with a warning.
- Blender shows Unity materials approximately (custom shaders become base colour/texture, normal, metal/smooth).
  Unity keeps the real materials. Materials of Blender-made assets are owned by Blender: a re-sync overwrites them.
- If the same object changed in Unity and in Blender since the last sync, Unity's version is kept unless you tick it.
- New lights, cameras and empties made in Blender are not sent to Unity.
- Geometry Nodes instances should be realized (Object > Apply > Visual Geometry to Mesh) before syncing.

## Settings

`ProjectSettings/BlenderBridge.asset` (commit it): editable roots per scene, library folders (what appears in
Blender's Asset Browser), auto-apply, and the asset root. The Blender executable is stored per PC.

To put the window in your own menu, call `Auleek.BlenderBridge.Editor.BBWindow.Open()` or
`EditorApplication.ExecuteMenuItem("Tools/Blender Bridge")` from a `[MenuItem]` of yours.

## Files

- Unity side: `Editor/` (assembly `Auleek.BlenderBridge.Editor`).
- Blender side: `BlenderAddon~/aa_bridge/` (Unity ignores `~` folders). Unity starts Blender with it; it also
  installs a small `aa_unity_bridge` add-on so the Unity tab is there when a session file is opened by hand.
- Working files, in the Unity project: `BlenderSessions/` (git-ignored automatically): `_cache` (converted
  meshes/textures), `_library` (`AA_UnityLibrary.blend`), `<Scene>/` (snapshot, .blend, exchange files).
  Scene backups before each sync: `SyncBackups/BlenderBridge/` (ignore it in git too).
