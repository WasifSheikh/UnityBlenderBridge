"""Entry points Unity calls (through ../bb_entry.py, see BBLauncher.cs). Also installs a tiny add-on so the Unity
panel is there when a session .blend is opened by hand."""
import os

import bpy

STUB = 'aa_unity_bridge'
TOOLS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def build_library(path):
    from . import library
    return library.build(path)


def open_session(path):
    from . import session, ui
    ui.register_once()
    _install_stub()
    session.build(path)


def resume(path):
    from . import session, ui
    ui.register_once()
    _install_stub()
    session.resume(path)


# The stub registers the Unity panel only for files that are bridge sessions, and imports the bridge from the folder
# recorded in that file (aa_tools), so each project's session uses its own version of the package. TOOLS (the last
# project that launched Blender) is the fallback when that folder is gone (e.g. the package was updated).
STUB_TEXT = '''bl_info = {{"name": "Unity Blender Bridge", "author": "Auleek", "version": {release_version}, "blender": (4, 2, 0),
           "location": "3D View > Sidebar > Unity", "description": "Panel for scenes opened from Unity (Blender Bridge)",
           "category": "Import-Export"}}
import os
import sys

import bpy
from bpy.app.handlers import persistent

FALLBACK = r"{tools}"


def _tools():
    scene = getattr(bpy.context, "scene", None)
    if scene is None or not scene.get("aa_session_json"):
        return ""
    for t in (scene.get("aa_tools", ""), FALLBACK):
        if t and os.path.isfile(os.path.join(t, "aa_bridge", "__init__.py")):
            return t
    return ""


@persistent
def _on_load(*_):
    t = _tools()
    if not t:
        return
    sys.dont_write_bytecode = True
    if t not in sys.path:
        sys.path.insert(0, t)
    try:
        from aa_bridge import ui
        ui.register_once()
    except Exception as e:
        print("Unity Blender Bridge:", e)


def register():
    if _on_load not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(_on_load)


def unregister():
    if _on_load in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.remove(_on_load)
    try:
        from aa_bridge import ui
        ui.unregister()
    except Exception:
        pass
'''


def _install_stub():
    """Writes and enables the stub. Enabling is remembered when Blender saves its preferences (by default on quit);
    preferences are never saved from here."""
    try:
        import addon_utils
        folder = bpy.utils.user_resource('SCRIPTS', path='addons', create=True)
        path = os.path.join(folder, STUB + '.py')
        from .version import current
        text = STUB_TEXT.format(tools=TOOLS, release_version=tuple(int(n) for n in current().split('.')))
        current = open(path, encoding='utf-8').read() if os.path.exists(path) else None
        if current != text:
            with open(path, 'w', encoding='utf-8', newline='\n') as f:
                f.write(text)
            addon_utils.modules_refresh()
            if addon_utils.check(STUB)[0]:
                addon_utils.disable(STUB)   # reload the new text
        if not addon_utils.check(STUB)[0]:
            addon_utils.enable(STUB, default_set=True, persistent=True)
    except Exception as e:
        print('AA_WARN add-on install:', e)
