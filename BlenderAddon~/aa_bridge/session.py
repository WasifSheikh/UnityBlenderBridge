"""The scene file: built fresh from Unity's snapshot (session.json) whenever Unity asks, so it never has to be kept.

Every Unity object becomes an object carrying its Unity id (custom property aa_id): prefab instances and mesh objects
are collection instances of the library asset, other objects are empties (hierarchy kept), lights are lights.
Objects outside the editable roots go to "Unity context (locked)": visible, not selectable, never sent back.
"""
import math
import os

import bpy

from .common import CONTEXT, EDIT, mat, read_json, srgb_to_linear
from .library import INDEX


def session_path():
    return bpy.context.scene.get('aa_session_json', '')


def load():
    p = session_path()
    return read_json(p) if p and os.path.exists(p) else None


def ensure_asset_library(lib_dir, name):
    """Registers BlenderSessions/_library as an asset library (link, not append). Preferences are not saved here:
    Unity registers it again on every launch, and saving would also store unrelated unsaved preference changes."""
    prefs = bpy.context.preferences
    d = os.path.normcase(os.path.normpath(lib_dir))
    libs = prefs.filepaths.asset_libraries
    lib = next((x for x in libs if os.path.normcase(os.path.normpath(bpy.path.abspath(x.path))) == d), None)
    if lib is None:
        try:
            lib = libs.new(name=name, directory=lib_dir)
        except Exception:
            bpy.ops.preferences.asset_library_add(directory=lib_dir)
            lib = libs[-1]
    if lib.name != name:
        lib.name = name
    try:
        lib.import_method = 'LINK'
    except Exception:
        pass
    return lib.name


def _light(n):
    ld = n['light']
    kind = {'Directional': 'SUN', 'Spot': 'SPOT', 'Point': 'POINT', 'Rectangle': 'AREA', 'Disc': 'AREA'}.get(ld.get('type'), 'POINT')
    data = bpy.data.lights.new(n['name'], kind)
    c = ld.get('color') or [1, 1, 1]
    data.color = [srgb_to_linear(x) for x in c[:3]]
    i = ld.get('intensity', 1.0)
    data.energy = i * 3.0 if kind == 'SUN' else i * 80.0  # rough viewport match; Unity keeps the real light
    if kind == 'SPOT':
        data.spot_size = math.radians(max(1.0, ld.get('spot', 30.0)))
    return data


def _library_name(S):
    return S.get('libraryName') or 'Unity'


def build(path):
    S = read_json(path)
    index_path = os.path.join(os.path.dirname(S['library']), INDEX)
    index = read_json(index_path) if os.path.exists(index_path) else {}

    bpy.ops.wm.read_homefile(use_empty=True)
    scene = bpy.context.scene
    scene.name = S['scene']
    scene['aa_session'] = S['sessionId']
    scene['aa_session_json'] = path
    scene['aa_tools'] = S.get('tools') or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    scene['aa_status'] = 'Built from Unity ' + S.get('created', '').replace('T', ' ')
    scene.unit_settings.system = 'METRIC'

    keys = {n['asset'] for n in S['nodes'] if n.get('asset')}
    want = {index[k] for k in keys if k in index}
    linked = {}
    if want and os.path.exists(S['library']):
        with bpy.data.libraries.load(S['library'], link=True) as (src, dst):
            dst.collections = [c for c in src.collections if c in want]
        by_name = {c.name: c for c in dst.collections if c}
        linked = {k: by_name.get(index[k]) for k in keys if k in index}

    edit = bpy.data.collections.new(EDIT)
    ctx = bpy.data.collections.new(CONTEXT)
    scene.collection.children.link(edit)
    scene.collection.children.link(ctx)
    ctx.hide_select = True
    ctx.color_tag = 'COLOR_07'
    edit.color_tag = 'COLOR_05'

    objs, hidden, missing = {}, [], 0
    for n in S['nodes']:
        kind = n.get('kind', 'group')
        if kind == 'light' and n.get('light'):
            o = bpy.data.objects.new(n['name'], _light(n))
        else:
            o = bpy.data.objects.new(n['name'], None)
            if kind == 'instance':
                c = linked.get(n.get('asset'))
                if c is not None:
                    o.instance_type = 'COLLECTION'
                    o.instance_collection = c
                    o.empty_display_size = 0.1
                else:
                    missing += 1
                    o.empty_display_type = 'CUBE'
                    o.empty_display_size = 0.5
            elif kind == 'marker':
                o.empty_display_type = 'SPHERE'
                o.empty_display_size = 0.3
            else:
                o.empty_display_type = 'PLAIN_AXES'
                o.empty_display_size = 0.5
        o['aa_id'] = n['id']
        if n.get('editable'):
            edit.objects.link(o)
        else:
            ctx.objects.link(o)
            o['aa_locked'] = 1
            o.hide_select = True
        p = n.get('parent')
        if p and p in objs:
            o.parent = objs[p]
        o.matrix_world = mat(n['m'])
        o['aa_name'] = o.name
        objs[n['id']] = o
        if not n.get('active', True):
            hidden.append(o)
    for o in hidden:
        o.hide_set(True)

    scene['aa_library'] = ensure_asset_library(os.path.dirname(S['library']), _library_name(S))
    bpy.ops.wm.save_as_mainfile(filepath=S['blend'], relative_remap=False)
    n_edit = sum(1 for n in S['nodes'] if n.get('editable'))
    msg = f"{n_edit} editable, {len(S['nodes']) - n_edit} context objects"
    if missing:
        msg += f', {missing} without a library asset'
    scene['aa_status'] = 'Built from Unity: ' + msg
    print('AA_SESSION_BUILT ' + msg, flush=True)
    if not bpy.app.background:
        bpy.app.timers.register(setup_view, first_interval=0.5)


def resume(path):
    S = read_json(path) if os.path.exists(path) else None
    scene = bpy.context.scene
    if S and scene.get('aa_session') != S['sessionId']:
        scene['aa_status'] = 'This file is not the current Unity session; open the scene from Unity again.'
    if S:
        scene['aa_tools'] = S.get('tools') or scene.get('aa_tools', '')
        scene['aa_library'] = ensure_asset_library(os.path.dirname(S['library']), _library_name(S))
    if not bpy.app.background:
        bpy.app.timers.register(setup_view, first_interval=0.5)


def setup_view():
    try:
        for win in bpy.context.window_manager.windows:
            for area in win.screen.areas:
                if area.type != 'VIEW_3D':
                    continue
                sp = area.spaces.active
                sp.clip_start = 0.05
                sp.clip_end = 5000
                sp.show_region_ui = True
                sp.shading.type = 'SOLID'
                sp.shading.color_type = 'TEXTURE'
                region = next((r for r in area.regions if r.type == 'WINDOW'), None)
                if region is not None:
                    with bpy.context.temp_override(window=win, area=area, region=region):
                        coll = bpy.data.collections.get(EDIT)
                        if coll is not None and coll.objects:
                            bpy.ops.object.select_all(action='DESELECT')
                            for o in coll.objects:
                                if o.visible_get():
                                    o.select_set(True)
                            bpy.ops.view3d.view_selected()
                            bpy.ops.object.select_all(action='DESELECT')
    except Exception as e:
        print('AA_WARN view setup:', e)
    return None
