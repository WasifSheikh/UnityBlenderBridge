"""Unity Blender Bridge (Blender side). Unity is the source of truth; this package only builds throwaway Blender files
from what Unity sends and sends edits back.

    launch       entry points Unity calls through ../bb_entry.py (build_library in a background Blender,
                 open_session / resume interactively); installs a tiny add-on for session files opened by hand
    library      BlenderSessions/_library/UnityLibrary.blend: every Unity asset as a collection, Asset Browser catalogs
    aamesh       reader for the meshes Unity converts (AAM1/AAM2)
    session      the scene file: Unity's objects as collection instances, editable or locked
    model        what an object is to the bridge (Unity object, new model, edited asset, collection placement...)
    materials    Blender materials -> values + textures Unity rebuilds as URP Lit
    export       one asset -> FBX + material values + source .blend, plus hashes/signatures to skip unchanged ones
    editable     Make editable: a linked Unity instance -> local geometry ready to model
    sync         "Sync to Unity" (result.json) and what Unity answers (applied.json, status.json)
    ui           3D View > Sidebar > Unity
"""
