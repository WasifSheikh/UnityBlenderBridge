# Blender Bridge — User Guide

For Alien Armageddon / ProjectAlienShooter. Verified against the local bridge implementation on 6 November 2026.

## 1. Everyday workflow

1. Save your Unity scene.
2. Open **Alien Armageddon → Environment → Blender Bridge**.
3. Choose **Continue in Blender** to resume your existing Blender work, or **Start fresh from Unity** to rebuild from Unity's current saved scene.
4. In Blender, press **N** and open the **Unity** sidebar.
5. Move, rotate, scale, duplicate, or create objects. For geometry changes to an existing asset, click **Make editable**.
6. For new assets, choose **Import destination and name**, then **Material sync settings**.
7. Click **Sync to Unity**. Clean changes apply automatically; otherwise resolve the choices in Unity's review and click **Apply**.
8. Wait for Unity's acknowledgement before continuing another sync.

Only mesh objects come into Blender (mesh prefabs such as props and buildings, plus plain meshes such as roads). Lights, volumes, probes, cameras, FX, characters and empty helpers stay in Unity and are never changed by a sync. Change this under **Settings → What Blender gets**, then use **Start fresh**.

Unity remains the authority for the level layout. Keep unsynced Blender work in its current session until you have either applied it to Unity or saved a separate copy.

## 2. Where the Blender files are saved

All paths below are relative to the **Unity project root**, the folder containing `Assets/` and `ProjectSettings/`.

| Location | What it contains | How it is used |
|---|---|---|
| `BlenderSessions/<Scene>_<guid8>/<Scene>_<guid8>.blend` | Your full bridge editing session: layout, linked assets, and local edits | Continue reopens it; Start fresh rebuilds it |
| `ArtSource/<configured folder>/<AssetName>.blend` | The editable source of one imported asset or grouped asset | Updated when that model is exported and imported again |
| `BlenderSessions/_library/UnityLibrary.blend` | Shared Blender asset library built from Unity assets and registered sources | Updated/rebuilt as needed |
| `BlenderSessions/_cache/` | Converted meshes and texture previews | Generated working data |
| `BlenderSessions/<Scene>_<guid8>/out/assets/<key>/` | Staged FBX, materials/texture data, and a compact source `.blend` for an exported asset | Reused for that key on subsequent exports |
| `SyncBackups/BlenderBridge/` | Backups made before Unity applies syncs | Scene/session recovery; see section 9 |

For M1 Outbreak, the current full-session file is:

```text
BlenderSessions/M1_Outbreak_e71f55e3/M1_Outbreak_e71f55e3.blend
```

The migrated Building A editable source is:

```text
ArtSource/Downtown/DT_Building_A.blend
```

New sources follow their selected profile. For example, new Downtown buildings use `ArtSource/Downtown/Buildings/`. Existing assets keep their registered source paths.

**The full session and the per-asset source are different files.** The session contains the level editing context. A per-asset source contains that asset's editable objects/data, including supported modifiers; it is not a copy of the entire level.

## 3. What “Start fresh from Unity” does

**It creates new session contents at the same session filename. It does not create a new timestamped Blender file on every click.**

For the same saved Unity scene name and GUID, the session folder and `.blend` path stay the same. Start fresh:

- Exports a new Unity scene snapshot and gives it a new session ID.
- Updates the shared asset library as needed.
- Builds the Blender scene again from that snapshot.
- Saves it over the existing session `.blend`.
- Clears pending result/acknowledgement/import-progress files for the replaced session.

It does **not** import new Unity materials/textures merely because you opened a fresh session. It also does not replace the registered `ArtSource/` files just by rebuilding the level session.

### Before starting fresh

Apply Blender changes you want to keep, or save a separate copy outside the generated session path. **Ctrl+S alone does not protect the session from Start fresh:** that saved session file is the file being replaced.

The bridge warns about a pending unapplied sync and a Blender window it knows is already open. It cannot guarantee a warning for every unsynced edit in every Blender window.

A Blender window from the previous session becomes stale after Start fresh; its later syncs are refused because its session ID no longer matches.

### When do additional session folders appear?

Renaming the Unity scene changes the name portion of the session key. A copied/new scene with a different Unity GUID also gets a different key. These can leave separate session folders; repeated Start fresh on an unchanged scene does not.

You may also see `.blend1` files. Blender's current save-version setting is 1, and backups are present in this project. These are Blender save backups, not a bridge-managed history of every Start fresh. Do not rely on them as the only copy of valuable unsynced work.

## 4. Continue, save, and sync

| Action | Result |
|---|---|
| **Continue in Blender** | Opens the existing saved session; does not rebuild it from Unity's latest scene state |
| **Ctrl+S in Blender** | Saves the session and prepares an update for Unity, including while Unity is closed |
| **Sync to Unity** | Saves the working session, exports changed assets/scene edits, and sends the result to Unity |
| **Start fresh from Unity** | Rebuilds the session from the saved Unity scene and replaces its session file |

When Unity imports a changed model, its compact editable source is copied to the asset's registered `ArtSource/` path, replacing the previous source file there. Ordinary transform-only syncs do not export a new per-asset source.

A source `.blend` may contain packed images even when texture importing is disabled. Packing keeps the Blender source editable; it does not mean those images were imported into Unity's `Assets/.../Textures/` folder.

## 5. Edit existing objects and choose destinations

For an ordinary Unity prefab, **Make editable** offers:

- **Only this one** — create a variant for this instance.
- **The prefab itself** — edit the shared prefab, affecting its copies.

A Blender-made asset reopens its saved source. Geometry edits update that shared asset, so its placed copies follow.

For a new asset, select its root and click **Import destination and name**. Choose a purpose such as **Buildings / Downtown**, **Location / M1 Downtown**, or a props category.

Enter a name without `SM_` or `PF_`; check the path preview. For example, Downtown buildings with `Building_N` produce:

```text
Assets/Art/Environment/Buildings/Downtown/Meshes/SM_DT_Building_N.fbx
Assets/Art/Environment/Buildings/Downtown/Prefabs/PF_DT_Building_N.prefab
```

Changing an existing asset's destination/name records preferences for a later explicit migration. It does not automatically move or rename its registered files. Renaming a hierarchy instance is also separate from renaming its shared asset.

## 6. Material and texture options

Open **Material sync settings** in Blender, or use Unity's import review.

| Setting | What happens |
|---|---|
| **Use Unity materials** | No bridge-created material or texture files. Existing Unity assignments/settings are retained; new slots use a Unity default material until assigned |
| **Sync Blender materials**, **Sync textures** off | Updates owned Blender material colors and surface settings without copying textures; existing texture assignments are retained |
| **Sync Blender materials**, **Sync textures** on | Updates owned Blender materials and imports their supported texture maps |

**New assets default to Use Unity materials, with textures skipped.** Existing migrated assets keep their earlier Blender-material/texture policy until you change it.

For placeholder Blender textures, leave **Use Unity materials** selected. Assign the final materials on the Unity prefab so its instances share them. Later geometry syncs preserve those assignments. A newly added material slot may still need a Unity assignment.

Materials originally supplied by Unity are referenced and reused; the bridge does not convert them into newly owned Blender materials. Conversely, when Blender owns a material and full material syncing is enabled, later syncs can overwrite its Unity-side settings. Switch that asset to **Use Unity materials** when you want Unity to own those settings.

## 7. Do repeated syncs create duplicate materials and textures?

**Normal repeated syncs of the same registered asset reuse its existing files and GUIDs. They should not create another material and texture set on every sync.**

The bridge tracks:

- The asset's immutable bridge key and its model/prefab GUIDs.
- Owned material identities and their Unity material GUIDs.
- Texture identities by material identity plus map type, such as base color or normal, and their texture GUIDs.

For an existing map, the importer copies updated pixels over the registered texture file and reimports it. It loads/configures the existing material rather than calling CreateAsset again. Legacy material-slot and texture-name mappings support the migrated assets.

Unchanged geometry/material data normally skips asset export. A material-policy change can deliberately trigger an export even when geometry is unchanged. Temporary export files or changing cache files under `BlenderSessions/` are not additional imported art assets.

### When new files are expected

- Importing a genuinely new asset, a separate variant, or a new owned Blender material.
- Adding a map type that has not previously been registered for that material.
- Treating an object/material as new after losing or replacing its bridge identity.

This is identity-based reuse, **not global deduplication of identical pixels or similarly named materials across unrelated assets**. Two different assets/materials can intentionally own separate copies of the same-looking texture.

Keep the bridge's custom properties. Do not delete/recreate materials or strip IDs simply to rename them. For existing owned resources, a missing registered GUID stops sync rather than silently recreating the missing file; a conflicting new destination is rejected.

### Does switching textures off remove old files?

No. Skipping textures prevents their import/update; it does not delete texture files already imported. Removed materials/maps are not automatically pruned either. Review their Unity references before deleting anything.

### What was checked?

The implemented re-sync checks verified stable GUIDs after material/texture moves, repeated unchanged syncs retaining asset count, partial-import retries reusing resources, and Unity-owned assignments surviving geometry changes without new visual files. These cover normal reuse; they are not a promise that arbitrary removal/recreation of Blender data retains its old identity.

## 8. Recommended workflow for placeholder art

1. Open or continue the bridge session.
2. Model the object and choose its purpose/name.
3. Keep **Use Unity materials** selected.
4. Sync and wait for Unity to apply it.
5. Assign final Unity materials on the prefab.
6. Continue modeling and syncing the same asset with its bridge properties intact.

If Blender should author the final look, explicitly switch to **Sync Blender materials** and enable **Sync textures** only when the images are ready.

## 9. Backups, recovery, and files to keep

Before applying a sync, the bridge creates a dated backup of the saved Unity scene and session JSON under:

```text
SyncBackups/BlenderBridge/<Scene>_<guid8>/<timestamp>/
```

**This is not a complete backup of every imported asset or every Blender session file.** Unity scene Undo does not undo imported asset files. Undoing a bridge sync marks the Blender session out of date; rebuild it from Unity before sending more changes.

Keep the Unity assets with their `.meta` files, `ArtSource/`, `ProjectSettings/BlenderBridge.asset`, and `ProjectSettings/BlenderBridgeRegistry.asset` in version control. The registry is what lets later syncs find moved/renamed assets reliably.

Generated sessions and caches can be rebuilt after their wanted work is safely represented in Unity and the registered sources. Do not remove `BlenderSessions/` while it contains your only copy of unsynced edits. The earlier M1 organization migration has its own separate, fuller backup.

## Implementation references

These details were checked against the current repository code:

- [Session paths](Editor/BBCore.cs), [Start fresh / Continue](Editor/BBWindow.cs), and [Blender session saving](BlenderAddon~/aa_bridge/session.py).
- [Per-asset source export](BlenderAddon~/aa_bridge/export.py), [source importing](Editor/BBAssets.cs), and [change detection / acknowledgement](BlenderAddon~/aa_bridge/sync.py).
- [Material/texture importing](Editor/BBMaterialWriter.cs), [material identities](BlenderAddon~/aa_bridge/materials.py), and [GUID registry](Editor/BBRegistry.cs).
- [Scene/session backups and Undo limits](Editor/BBApply.cs).

## 10. Work with Blender only (0.3.0 and later)

Open the bridge session once, then close Unity if desired. Work in the full session .blend and save normally. Saving finishes exporting the update before returning; you can then close Blender. Open Unity and load the matching scene: the bridge detects the saved update and applies clean changes, or opens its review. Blender does not need to stay open.

Repeated saves while Unity is closed publish the latest snapshot. Updates use separate immutable working folders below the session's out/saved/ folder, so imported payloads are not overwritten mid-import. These are generated snapshots, not new Unity material/texture assets. They are not currently automatically pruned.

If Unity has already applied another update since the saved snapshot's baseline, the bridge retains the snapshot and reports it as stale instead of risking duplicate objects. Reopen the existing Blender session, allow its acknowledgement to load, and sync again. Do not use Start fresh to recover unsent work.

Saving an ordinary .blend or a per-asset ArtSource file is not a full bridge-session update. Save the session at its registered filename. If export fails, Blender still saves your work and shows Not sent on save; fix that error before expecting Unity to import it.

The release number appears in both interfaces and comes from package.json. It is separate from the sync format version. Every completed update, including documentation-only changes, increments at least the patch number.
