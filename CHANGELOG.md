# Changelog

## 0.2.0

Blender files made with 0.1.0 can't be continued: open each scene once with **Start fresh**.

### Fixed
- Sync to Unity crashed ("string indices must be integers") for any model with a textured material.
- Blender-made assets were never reopened from their source .blend with the default asset root (found by label now).
- Shift+D on a group in Blender doubled its children in Unity (or copied children Blender didn't know about).
- A prefab instance switched to its own variant stopped syncing (its Unity id changed); ids are remapped now.
- Applying again after a failed asset import built duplicate assets (`K_2`); finished assets are reused.
- Placing a Blender collection again in a later sync made a new duplicate prefab; editing the collection was never
  sent. A collection is one prefab now, re-exported when it changes.
- Make editable on a multi-part plain object left the old collider active.
- Changing the `collider` property didn't remove the old collider.
- Undoing a sync in Unity silently left Blender out of step; the session is marked out of date now.
- Default-Material and other built-in materials weren't found when placing library meshes.
- Paths with an apostrophe broke the Blender launch (arguments are passed after `--` now).
- Two scenes with the same name shared one session folder (sessions are keyed by name + GUID).
- `.gitignore` is found in the repository root even above the Unity project, and `SyncBackups/` is ignored too.
- Objects in an excluded collection made the sync fail.
- Renaming an object (or a `.001` on reopening) changed Unity's mesh ids and broke references (stable export names).
- Transparent URP materials were incomplete until opened in the inspector.
- The add-on no longer saves your Blender preferences, and only shows its tab in bridge session files.

### Changed
- Transforms are compared relative to the parent: moving a group no longer lists (or conflicts on) every child.
- New/added objects are created first, then parented, then placed, so copies under copies land correctly.
- Make editable cleans the Unity mesh up for modelling (weld, sharp edges, seams, quads); can be turned off.
- Meshes carry UV2 and vertex colours to Blender (AAM2); a UV2 from Blender is kept as the lightmap UV.
- LOD groups keep only the new LOD0 after a Make editable on a LOD prefab (with a warning in the preview).
- Unity-side edits to a Blender-made prefab are noticed: Blender gets the Unity version instead of a stale source.
- Library: unchanged prefabs are reused from the previous export without loading them; the background library
  build is skipped when nothing changed; deleted prefabs and unused cached meshes are cleaned up.
- Generic catalogs (relative to each library folder) and a "leave out" list replace the hard-coded `Environment`
  and `/_` rules. The asset library is named after the project.
- New window: status pill, tabs, changes grouped by type with filters and bulk conflict choices, searchable object
  list, settings with Blender version, cache clearing.
- New Blender panel: status, selection info, Make editable options, collider buttons, section group toggle, context
  visibility, help.
- Code split into focused files on both sides; headless Blender test in `Tests~/`.

## 0.1.0

First version, split out of Alien Armageddon.
