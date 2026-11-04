"""Unity Blender Bridge (Blender side). Unity is the source of truth; this package only builds throwaway
Blender files from what Unity sends and sends edits back.

    launch      entry points Unity calls (build_library in a background Blender, open_session / resume interactively)
    library     BlenderSessions/_library/AA_UnityLibrary.blend: every Unity asset as a collection, Asset Browser catalogs
    session     the scene file: Unity's objects as collection instances, editable or locked
    sync        "Sync to Unity": changes, duplicates, new instances and new/edited models (FBX + materials + source .blend)
    ui          3D View > Sidebar > Unity
"""
