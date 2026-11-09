# Blender Bridge

Edit Unity scenes with Blender's tools. **The Unity scene is the only thing that matters**: the Blender file is
throwaway and can be rebuilt from Unity at any time. Nothing in `BlenderSessions/` is committed.

## Install

Unity 6 (URP), Blender 4.2 or newer (tested with 5.2), Windows. In the project's `Packages/manifest.json`:

```json
"com.auleek.blenderbridge": "https://github.com/WasifSheikh/UnityBlenderBridge.git#v0.2.0"
```

Change the tag to update. Blender is found automatically (Program Files, Steam); otherwise set it under
**Settings > This PC** in the window.

### Working on the bridge itself

Clone this repo into the game's `Packages/` folder (e.g. `Packages/com.auleek.blenderbridge`) and git-ignore that
folder in the game repo. Unity uses an embedded package instead of the one in `manifest.json`, so you edit the bridge
live while everyone else stays on the tagged version. Commit and tag here, then bump the tag in the game's manifest.

Test the Blender side without Unity: `blender -b --factory-startup --python Tests~/test_blender_side.py`.

## Use it

1. Unity: **Tools > Blender Bridge**, then **Open in Blender**.
   - The status pill at the top says where the scene stands: *Not opened*, *Ready*, *Blender open*, *Changes
     waiting*, *Conflicts*, *Out of sync*.
   - **Editable objects** tab: tick which root objects Blender may edit. Everything else comes along as locked
     context. Saved for the team in `ProjectSettings/BlenderBridge.asset`.
   - With a Blender file already there, the button continues it; the ▾ menu can start fresh instead (and opens the
     session folder, backups and the library build log).
2. Blender: work as usual. The **Unity** tab in the sidebar (N) shows what the selected object is to the bridge.
   - Move, rotate, scale, delete, rename, Shift+D anything in *Unity (editable)*. Shift+D on a group copies the group;
     its children are copied only if you selected them too, as in Blender.
   - To model a Unity object (Edit Mode, extrude, loop cuts...): select it, **Make editable**. Unity objects come in
     as linked instances; this turns them into local geometry. By default the mesh is cleaned up for modelling
     (vertices Unity splits at seams welded, hard edges marked sharp, UV breaks marked as seams, triangles joined
     into quads); untick it to keep the exact Unity mesh. Unity materials stay the real ones. On sync:
     - a plain mesh object keeps its components and collider and gets the new mesh;
     - for a **prefab instance** you choose:
       - **Only this one (new variant)**: a Prefab Variant of the original, used by this instance. It inherits every
         component of the original prefab and still follows later changes to it.
       - **The prefab itself (all copies)**: the geometry goes into the original prefab, so every copy in every scene
         changes. The prefab is backed up first. Model files and package prefabs can't be edited this way; they get a
         variant instead.
     Parts are matched to the prefab's children by their path; new objects become new parts (static, with a collider
     when the prefab's parts have one); deleted parts are switched off. A LOD group keeps only the new LOD0.
   - Blender-made assets reopen from their saved source (modifiers intact) when you Make editable them. If someone
     changed one in Unity since (other meshes or materials), Blender gets the Unity version instead.
   - Drag project prefabs in from the Asset Browser, library **Unity: &lt;project&gt;** (one catalog per folder).
   - Model new things. Every new top-level object with geometry becomes its own asset; its origin is the pivot.
     Collider (**Mesh / Box / None** in the panel, custom property `collider`). Changing it later replaces the old
     collider.
3. Blender: **Sync to Unity**. Unity applies it within a few seconds (or shows the changes for review if something
   conflicts, or if auto-apply is off). One Ctrl+Z in Unity undoes the scene part; a backup of the scene is kept in
   `SyncBackups/BlenderBridge/`. If you do undo a sync, the session is marked *Out of sync* (Blender refuses to send
   more) until you start fresh.

## Assemble sections from multiple artists

Copy the sections into the same bridge session, then sync once. Keep each reusable prop as a separate
object, a mesh hierarchy under its own root, or a Blender collection instance. Matching exported geometry,
materials, part layout and collider settings share one prefab in that sync. Each copy keeps its own scene
transform. A plain Empty above a mesh hierarchy makes that hierarchy one asset. Use Blender Collections
for section organization, or mark an Empty as a **Section group** (panel button, custom property `map_group`) when it
only organises a section: its child assets keep their world transforms in Unity.

For Blender collection instances, each source collection is one reusable prefab: more placements later reuse it, and
editing the collection re-exports that prefab, so every placement follows. Pack external textures before handing
off the source files. Keep the same geometry and pivot for copies that should share a prefab.

## What Unity makes from a Blender model (key K)

Choose a **Destination** profile and an **Asset name** in Blender's Unity panel or Unity's sync review. Profiles
define an art leaf folder, an `ArtSource/` source folder, and an optional set code. Project and scene defaults
apply to new assets. An unresolved destination opens review instead of importing into a tool-specific folder.

The art leaf receives `Meshes/SM_<Name>.fbx` and `Prefabs/PF_<Name>.prefab` (static, with colliders).
Choose the material policy in Blender or Unity's import review:

- **Use Unity materials** keeps materials assigned in Unity and imports geometry only. New material slots use a
  Unity default until you assign one. It creates no material or texture files.
- **Sync Blender materials, without textures** imports material colors and scalar values while keeping existing
  Unity texture assignments. It does not copy, resolve, or clear texture maps.
- **Sync Blender materials and textures** also writes Blender-owned URP Lit materials to
  `Materials/M_<Name>_<Material>.mat` and image maps to `Textures/T_<Name>_<Material>_BC|N|MS|E.png`.

Project defaults apply only to new assets; existing assets keep their registered policy until explicitly changed.
This project's new-asset default is **Use Unity materials**. Existing Unity materials referenced by Blender remain
reused under either policy. Editable `.blend` sources go below project-root `ArtSource/`.
Prefixes are added once. Hierarchy instance names are independent of the shared asset name.

`ProjectSettings/BlenderBridgeRegistry.asset` stores immutable keys and GUIDs for models, prefabs, owned
materials and textures, plus exact source paths. Commit it with the assets. Existing legacy keys and `BBKey_K`
labels are retained. Adoption records the old files in place, including their old source paths; it does not move
or rename them. Changing an existing asset's destination/name records a proposal for a later migration only.
Re-sync resolves registered GUIDs and updates the same assets even after a Unity move/rename. Missing registered
assets and occupied new destinations stop import rather than creating replacements or overwriting unrelated files.
Internal FBX part names remain stable to preserve mesh fileIDs. A Blender UV2 is retained; otherwise Unity creates it.

Open a fresh bridge session after updating the package to receive the current profiles and controls; preserve
any unsynced work first. Old version-2 sessions remain readable, and Unity uses current project profiles when importing.

## What Blender gets

By default (**Settings > What Blender gets > Mesh objects only**) a scene comes into Blender as its meshes:

- mesh prefabs (props, buildings, vehicles, road pieces), as one instance each; lights or particles inside a prop
  prefab are part of it and stay Unity's;
- plain mesh objects (MeshFilter + MeshRenderer, e.g. roads and ground), unless **Also plain mesh objects** is off;
- the groups those sit in, so the hierarchy survives.

Everything else stays in Unity and no sync ever changes it: lights, Volumes, Adaptive Probe Volumes, reflection and
light probes, cameras, timelines, particle/VFX prefabs, characters (anything with a skinned mesh), UI, gameplay
helpers and empty objects. Objects with a component from the skip list (by class name; editable in Settings) are left
out even when they have a mesh, and so are meshes whose materials all use an effect shader (name containing Fog, Haze,
Volumetric or Particles/, also editable), such as fog and haze volumes. If you delete a group in Blender that also holds such hidden objects in Unity, they
are kept and move up one level; Shift+D of a group copies them along. **Everything** restores the old behaviour.
The setting applies the next time a scene is opened with **Start fresh**. **Preview for this scene** (next to the
setting, or **Tools > Blender Bridge: Preview What Blender Gets**) shows the counts without starting Blender.

## Good to know

- Pivots: the Blender object origin is the Unity pivot. Set Origin before syncing to choose it. Rotation and scale stay
  on the Unity transform unless you apply them in Blender. Moving the pivot of a shared prefab ("all copies") shifts the
  geometry of the other copies; Unity holds that sync for review with a warning.
- Blender shows Unity materials approximately (custom shaders become base colour/texture, normal, metal/smooth).
  Unity keeps the real materials. Only Blender-owned materials with **Sync Blender materials** enabled are updated
  from Blender; **Use Unity materials** preserves Unity assignments during geometry re-syncs.
- If the same object changed in Unity and in Blender since the last sync, Unity's version is kept unless you tick it
  (or press *Take Blender's for all*). Transforms are compared relative to the parent, so moving a group in one place
  doesn't make its children conflict.
- If a sync fails half way (e.g. a texture can't be imported), nothing in the scene changes; the assets that were
  already built are reused when you Apply again, not built twice.
- Objects added under a prefab instance in the scene are editable in Blender too.
- New lights, cameras and empties made in Blender are not sent to Unity.
- Geometry Nodes instances should be realized (Object > Apply > Visual Geometry to Mesh) before syncing.
- Blender-made sources in `ArtSource/` are committed with packed external textures; consider Git LFS for `*.blend`.
  Older projects can keep their explicitly registered source paths until they choose to migrate them.

## Settings

`ProjectSettings/BlenderBridge.asset` (commit it): editable roots per scene, library folders (what appears in
Blender's Asset Browser) and paths to leave out, auto-apply, destination profiles and scene/project defaults.
The old asset root remains for legacy compatibility. New assets require a configured destination profile.
The Blender executable is stored per PC. The registry is stored separately in `ProjectSettings/BlenderBridgeRegistry.asset`.

To put the window in your own menu, call `Auleek.BlenderBridge.Editor.BBWindow.Open()` from a `[MenuItem]` of yours
(the assembly is auto-referenced), or `EditorApplication.ExecuteMenuItem("Tools/Blender Bridge")`.

## Files

- Unity side: `Editor/` (assembly `Auleek.BlenderBridge.Editor`):
  `BBModels` (exchange format), `BBCore` (paths, axes, utilities), `BBSettings`, `BBExport` + `BBMeshWriter` +
  `BBMaterialReader` (Unity -> Blender), `BBPlan` (three-way diff), `BBApply` + `BBSceneOps` (applying),
  `BBAssets` + `BBPrefabBuilder` + `BBMaterialWriter` (Blender-made assets), `BBLauncher`, `BBWindow` + `BBStyles`,
  `BBWatcher`.
- Blender side: `BlenderAddon~/` (Unity ignores `~` folders). Unity starts Blender with `bb_entry.py`, which runs
  `aa_bridge`; it also installs a small `aa_unity_bridge` add-on so the Unity tab is there when a session file is
  opened by hand (it only appears in bridge session files).
- Working files, in the Unity project: `BlenderSessions/` (git-ignored automatically): `_cache` (converted
  meshes/textures; unused meshes are cleaned up), `_library` (`UnityLibrary.blend`), `<Scene>_<guid>/` (snapshot,
  .blend, exchange files). Scene backups before each sync: `SyncBackups/BlenderBridge/` (git-ignored automatically).
