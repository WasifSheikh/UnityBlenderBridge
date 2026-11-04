"""Entry points Unity calls (BBLauncher.cs). Also installs a tiny add-on so the Unity panel is there when a session
.blend is opened by hand."""
import os

import bpy

STUB = 'aa_unity_bridge'


def build_library(path):
    from . import library
    library.build(path)


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


def _install_stub():
    try:
        import addon_utils
        tools = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        folder = bpy.utils.user_resource('SCRIPTS', path='addons', create=True)
        path = os.path.join(folder, STUB + '.py')
        text = (
            'bl_info = {"name": "Unity Blender Bridge", "author": "Auleek", "version": (1, 0, 0), "blender": (4, 2, 0),\n'
            '           "location": "3D View > Sidebar > Unity", "description": "Unity-driven scene editing (Blender Bridge)",\n'
            '           "category": "Import-Export"}\n'
            'import sys\n'
            'sys.dont_write_bytecode = True\n'
            f'TOOLS = r"{tools}"\n\n\n'
            'def register():\n'
            '    if TOOLS not in sys.path:\n'
            '        sys.path.insert(0, TOOLS)\n'
            '    from aa_bridge import ui\n'
            '    ui.register_once()\n\n\n'
            'def unregister():\n'
            '    from aa_bridge import ui\n'
            '    ui.unregister()\n')
        if not os.path.exists(path) or open(path, encoding='utf-8').read() != text:
            with open(path, 'w', encoding='utf-8', newline='\n') as f:
                f.write(text)
            addon_utils.modules_refresh()
        if not addon_utils.check(STUB)[0]:
            addon_utils.enable(STUB, default_set=True, persistent=True)
            bpy.ops.wm.save_userpref()
    except Exception as e:
        print('AA_WARN add-on install:', e)
