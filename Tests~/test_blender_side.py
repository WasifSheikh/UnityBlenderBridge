"""Headless test of the Blender side of the bridge, with fake Unity data (no Unity needed).

    blender -b --factory-startup --python Tests~/test_blender_side.py

Builds a library and a session from generated Unity files in a temp folder, edits the scene the way an artist would
(move, Shift+D a group, new models, a collection used twice, Make editable on a prefab and a mesh), syncs, answers as
Unity would (applied.json with new ids and an id remap), syncs again, reopens a Blender-made asset from its source,
and checks result.json at every step. Exit code 1 on failure. Nothing outside the temp folder is touched (the add-on
stub is not installed).
"""
import json
import math
import os
import shutil
import struct
import sys
import tempfile
import traceback

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.join(os.path.dirname(HERE), 'BlenderAddon~')
sys.dont_write_bytecode = True
sys.path.insert(0, TOOLS)

from aa_bridge import editable, export, library, model, session, sync, ui  # noqa: E402
from aa_bridge.common import read_json, write_json  # noqa: E402

CHECKS = []


def check(cond, what):
    CHECKS.append((bool(cond), what))
    print(('PASS ' if cond else 'FAIL ') + what, flush=True)


def ident(t=(0, 0, 0)):
    return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, t[0], t[1], t[2], 1]


def write_cube_aam2(path, size=1.0):
    """Unity-style cube: 24 vertices (split per face), per-face normals, uv0, uv1, colours, 2 submeshes."""
    s = size / 2
    faces = [((0, 0, 1), [(-s, -s, s), (s, -s, s), (s, s, s), (-s, s, s)]), ((0, 0, -1), [(-s, s, -s), (s, s, -s), (s, -s, -s), (-s, -s, -s)]),
             ((1, 0, 0), [(s, -s, -s), (s, s, -s), (s, s, s), (s, -s, s)]), ((-1, 0, 0), [(-s, -s, s), (-s, s, s), (-s, s, -s), (-s, -s, -s)]),
             ((0, 1, 0), [(-s, s, s), (s, s, s), (s, s, -s), (-s, s, -s)]), ((0, -1, 0), [(-s, -s, -s), (s, -s, -s), (s, -s, s), (-s, -s, s)])]
    pos, nrm, uv, cols, sub0, sub1 = [], [], [], [], [], []
    for fi, (n, quad) in enumerate(faces):
        b = len(pos)
        for k, p in enumerate(quad):
            pos.append(p)
            nrm.append(n)
            uv.append(((k in (1, 2)) * 1.0, (k in (2, 3)) * 1.0))
            cols.append((255, 128, 0, 255))
        tri = [b, b + 1, b + 2, b, b + 2, b + 3]
        (sub0 if fi < 4 else sub1).extend(tri)
    n = len(pos)
    with open(path, 'wb') as f:
        f.write(b'AAM2')
        f.write(struct.pack('<iii', n, 1 | 2 | 4 | 8, 2))
        f.write(struct.pack('<ii', len(sub0), len(sub1)))
        for arr in (pos, nrm):
            for v in arr:
                f.write(struct.pack('<fff', *v))
        for arr in (uv, uv):
            for v in arr:
                f.write(struct.pack('<ff', *v))
        for c in cols:
            f.write(struct.pack('<BBBB', *c))
        f.write(struct.pack(f'<{len(sub0) + len(sub1)}i', *(sub0 + sub1)))


def save_png(path, color):
    img = bpy.data.images.new('tmp_png', 4, 4)
    img.pixels.foreach_set(list(color) * 16)
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)


def setup(root):
    proj = os.path.join(root, 'Project')
    sess_dir = os.path.join(proj, 'BlenderSessions', 'Test_abcd1234')
    lib_dir = os.path.join(proj, 'BlenderSessions', '_library')
    cache = os.path.join(proj, 'BlenderSessions', '_cache', 'meshes')
    for d in (sess_dir, lib_dir, cache, os.path.join(proj, 'Assets', 'Textures')):
        os.makedirs(d, exist_ok=True)
    cube = os.path.join(cache, 'g1_1__h.aamesh')
    write_cube_aam2(cube)
    tex = os.path.join(proj, 'Assets', 'Textures', 'brick.png')
    save_png(tex, (0.8, 0.3, 0.2, 1))
    lib = {
        'version': 2, 'blend': os.path.join(lib_dir, 'UnityLibrary.blend'), 'dir': lib_dir, 'libraryName': 'Unity: Test',
        'meshes': [{'key': 'g1_1', 'file': cube}],
        'materials': [{'key': 'm1_1', 'name': 'M_Brick', 'shader': 'Universal Render Pipeline/Lit', 'baseMap': tex, 'normalMap': '', 'emissionMap': '',
                       'alpha': 'opaque', 'color': [1, 1, 1, 1], 'emission': [0, 0, 0], 'tiling': [2, 2, 0, 0], 'metallic': 0, 'smoothness': 0.3,
                       'normalScale': 1, 'cutoff': 0.5},
                      {'key': '0000000000000000f000000000000000_10303', 'name': 'Default-Material', 'shader': 'Standard', 'baseMap': '', 'normalMap': '',
                       'emissionMap': '', 'alpha': 'opaque', 'color': [1, 1, 1, 1], 'emission': [0, 0, 0], 'tiling': [1, 1, 0, 0], 'metallic': 0,
                       'smoothness': 0.5, 'normalScale': 1, 'cutoff': 0.5}],
        'assets': [
            {'key': 'prefab:p1', 'name': 'Crate', 'kind': 'prefab', 'catalog': 'Environment/Props', 'hash': 'h1', 'prefab': 'Assets/P/Crate.prefab',
             'source': '', 'sourceCollection': '', 'bbKey': '', 'browsable': True, 'hasLods': False,
             'parts': [{'mesh': 'g1_1', 'mats': ['m1_1', '0000000000000000f000000000000000_10303'], 'm': ident(), 'path': ''}]},
            {'key': 'prefab:p2', 'name': 'Barrel', 'kind': 'prefab', 'catalog': 'Environment/Props', 'hash': 'h2', 'prefab': 'Assets/P/Barrel.prefab',
             'source': '', 'sourceCollection': '', 'bbKey': '', 'browsable': True, 'hasLods': True,
             'parts': [{'mesh': 'g1_1', 'mats': ['m1_1', 'm1_1'], 'm': ident(), 'path': 'Body'},
                       {'mesh': 'g1_1', 'mats': ['m1_1', 'm1_1'], 'm': ident((0, 0, 1.2)), 'path': 'Lid'}]},
            {'key': 'mesh:abc', 'name': 'Rock', 'kind': 'mesh', 'catalog': '', 'hash': 'g1_1', 'prefab': '', 'source': '', 'sourceCollection': '',
             'bbKey': '', 'browsable': False, 'hasLods': False, 'parts': [{'mesh': 'g1_1', 'mats': ['m1_1', 'm1_1'], 'm': ident(), 'path': ''}]},
            {'key': 'prefab:old', 'name': 'Old', 'kind': 'prefab', 'catalog': 'Environment/Old', 'hash': 'h3', 'prefab': 'Assets/P/Old.prefab',
             'source': '', 'sourceCollection': '', 'bbKey': '', 'browsable': True, 'hasLods': False,
             'parts': [{'mesh': 'g1_1', 'mats': ['m1_1', 'm1_1'], 'm': ident(), 'path': ''}]},
        ],
        'keep': []}
    S = {'version': 2, 'sessionId': 'sess1', 'key': 'Test_abcd1234', 'scene': 'Test', 'scenePath': 'Assets/Scenes/Test.unity', 'sceneGuid': 'abcd1234',
         'created': '2026-11-04T12:00:00', 'library': lib['blend'], 'libraryJson': os.path.join(lib_dir, 'library.json'), 'libraryName': 'Unity: Test',
         'blend': os.path.join(sess_dir, 'Test_abcd1234.blend'), 'project': proj, 'assetRoot': 'Assets/BlenderBridge/Test',
         'outDir': os.path.join(sess_dir, 'out'), 'appliedPath': os.path.join(sess_dir, 'applied.json'),
         'statusPath': os.path.join(sess_dir, 'status.json'), 'tools': TOOLS,
         'nodes': [
             {'id': 'S', 'name': 'Street', 'parent': '', 'asset': '', 'kind': 'group', 'editable': True, 'active': True, 'm': ident()},
             {'id': 'C1', 'name': 'Crate', 'parent': 'S', 'asset': 'prefab:p1', 'kind': 'instance', 'editable': True, 'active': True, 'm': ident((2, 0, 0))},
             {'id': 'B1', 'name': 'Barrel', 'parent': 'S', 'asset': 'prefab:p2', 'kind': 'instance', 'editable': True, 'active': True, 'm': ident((4, 0, 0))},
             {'id': 'R1', 'name': 'Rock', 'parent': 'S', 'asset': 'mesh:abc', 'kind': 'instance', 'editable': True, 'active': True, 'm': ident((6, 0, 0))},
             {'id': 'G2', 'name': 'Sub', 'parent': 'S', 'asset': '', 'kind': 'group', 'editable': True, 'active': True, 'm': ident((0, 5, 0))},
             {'id': 'C2', 'name': 'Crate (1)', 'parent': 'G2', 'asset': 'prefab:p1', 'kind': 'instance', 'editable': True, 'active': True, 'm': ident((0, 6, 0))},
             {'id': 'X1', 'name': 'Building', 'parent': '', 'asset': 'prefab:p1', 'kind': 'instance', 'editable': False, 'active': True, 'm': ident((0, -10, 0))},
             {'id': 'L1', 'name': 'Lamp', 'parent': 'S', 'asset': '', 'kind': 'light', 'editable': True, 'active': True, 'm': ident((0, 0, 3)),
              'light': {'type': 'Point', 'color': [1, 1, 1], 'intensity': 2, 'range': 10, 'spot': 30}}]}
    write_json(S['libraryJson'], lib)
    write_json(os.path.join(sess_dir, 'session.json'), S)
    write_json(S['statusPath'], {'sessionId': 'sess1', 'state': 'ok', 'message': '', 'time': ''})
    return proj, S, lib, os.path.join(sess_dir, 'session.json')


def obj(name):
    return bpy.data.objects[name]


def shift_d(o):
    c = o.copy()
    for coll in o.users_collection:
        coll.objects.link(c)
    return c


def new_mesh(name, size=1.0, mat=None, coll=None):
    import bmesh
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=size)
    bm.to_mesh(me)
    bm.free()
    me.uv_layers.new(name='UVMap')
    o = bpy.data.objects.new(name, me)
    (coll or bpy.context.scene.collection).objects.link(o)
    if mat is not None:
        me.materials.append(mat)
    return o


def textured_material(name, image):
    m = bpy.data.materials.new(name)
    if bpy.app.version < (5, 0, 0):
        m.use_nodes = True
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    t = nt.nodes.new('ShaderNodeTexImage')
    t.image = image
    nt.links.new(t.outputs['Color'], bsdf.inputs['Base Color'])
    return m


def result(S):
    return read_json(os.path.join(S['outDir'], 'result.json'))


def unity_applies(S, R, remap=None, keys=None):
    """What Unity writes after applying R: ids for every new object, final keys for assets."""
    keys = keys or {}
    ids = [{'tmp': n['tmp'], 'id': 'U:' + n['tmp'], 'asset': keys.get(n.get('asset', '')[8:], n.get('asset', '')[8:]) if n.get('asset', '').startswith('blender:') else '',
            'hash': next((a['hash'] for a in R['assets'] if 'blender:' + a['key'] == n.get('asset')), '')} for n in R['dups'] + R['added']]
    for a in R['assets']:
        if not a['isNew']:
            ids.append({'tmp': '', 'id': '', 'asset': a['key'], 'hash': a['hash']})
        elif a['node']:
            ids.append({'tmp': '', 'id': (remap or {}).get(a['node'], a['node']), 'asset': keys.get(a['key'], a['key']), 'hash': a['hash']})
    write_json(S['appliedPath'], {'sessionId': S['sessionId'], 'applied': '2026-11-04T12:30:00', 'summary': 'test',
                                  'ids': ids, 'remap': [{'from': k, 'to': v} for k, v in (remap or {}).items()]})
    os.replace(os.path.join(S['outDir'], 'result.json'), os.path.join(S['outDir'], 'result.applied.json'))


def run(root):
    proj, S, lib, sjson = setup(root)

    # ---- library
    st = library.build(S['libraryJson'])
    check(st['assets'] == 4 and st['rebuilt'] == 4 and st['meshes'] == 1, f'library built {st}')
    st = library.build(S['libraryJson'])
    check(st['rebuilt'] == 0 and st['meshes'] == 0, f'library rebuild is incremental {st}')
    me = bpy.data.meshes.get('g1_1')
    check(me is not None and len(me.uv_layers) == 2 and len(me.color_attributes) == 1, 'AAM2 mesh has UVMap + UV2 + colours')
    lib['assets'] = [a for a in lib['assets'] if a['key'] != 'prefab:old']
    write_json(S['libraryJson'], lib)
    st = library.build(S['libraryJson'])
    check(st['pruned'] == 1 and not any(c.get('aa_key') == 'prefab:old' for c in bpy.data.collections), f'deleted prefab pruned from the library {st}')

    # ---- session
    session.build(sjson)
    sc = bpy.context.scene
    check(len([o for o in sc.objects if 'aa_id' in o]) == 8, 'session has all Unity objects')
    check(obj('Crate').instance_collection is not None and obj('Crate').instance_collection.library is not None, 'prefab instance links the library collection')
    check(obj('Building').get('aa_locked') == 1, 'context object locked')

    # ---- artist edits
    obj('Crate').location.x += 1.0
    obj('Street').location.z += 0.5                         # moving the group moves its children: no per-child moves
    g_copy = shift_d(obj('Sub'))
    c_copy = shift_d(obj('Crate (1)'))
    c_copy.parent = g_copy
    proj_img = bpy.data.images.load(os.path.join(proj, 'Assets', 'Textures', 'brick.png'))
    gen = bpy.data.images.new('Painted', 8, 8)
    gen.pixels.foreach_set([0.1, 0.9, 0.2, 1.0] * 64)
    gen.pack()
    nc = new_mesh('NewCrate', 1.0, textured_material('MatProj', proj_img))
    nc.data.materials.append(textured_material('MatPacked', gen))
    nc['collider'] = 'box'
    nc.location = (10, 0, 0)
    grp = bpy.data.objects.new('Section', None)
    grp['map_group'] = True
    sc.collection.objects.link(grp)
    a1 = new_mesh('PostA', 0.3)
    a1.parent = grp
    hidden_coll = bpy.data.collections.new('Hidden stuff')
    sc.collection.children.link(hidden_coll)
    new_mesh('HiddenModel', 0.5, coll=hidden_coll)
    bpy.context.view_layer.layer_collection.children['Hidden stuff'].exclude = True
    bench = bpy.data.collections.new('Bench')
    new_mesh('BenchSeat', 2.0, coll=bench)
    for i in range(2):
        p = bpy.data.objects.new(f'BenchPlace{i}', None)
        p.instance_type = 'COLLECTION'
        p.instance_collection = bench
        p.location = (0, 20 + 3 * i, 0)
        sc.collection.objects.link(p)
    barrel = editable.make_editable(obj('Barrel'), 'variant', True)
    body = next(c for c in barrel.children if c.get('aa_part') == 'Body')
    check(len(body.data.vertices) == 8 and len(body.data.polygons) == 6, f'cleanup welds and makes quads ({len(body.data.vertices)} verts, {len(body.data.polygons)} faces)')
    check(sum(1 for e in body.data.edges if e.use_edge_sharp) == 12, 'cube hard edges became sharp edges')
    check(not body.data.has_custom_normals, 'custom normals cleared after cleanup')
    body.data.vertices[0].co.z += 0.3
    rock = editable.make_editable(obj('Rock'), 'variant', False)
    check(len(rock.data.vertices) == 24, 'without cleanup the Unity mesh is kept exactly')
    rock.scale = (1, 1, 1)
    for v in rock.data.vertices:
        v.co *= 1.5
    lamp = obj('Lamp')
    obj('Crate (1)').name = 'Crate One'

    # ---- sync 1
    sync.sync_to_unity()
    R = result(S)
    nodes = {n['id']: n for n in R['nodes']}
    check(R['version'] == 2 and R['sessionId'] == 'sess1', 'result header')
    check(set(nodes) == {'S', 'C1', 'B1', 'R1', 'G2', 'C2', 'L1'}, f'nodes {sorted(nodes)}')
    check(nodes['C2']['name'] == 'Crate One' and nodes['C1']['name'] == '', 'only renamed objects carry a name')
    dups = {d['of']: d for d in R['dups']}
    check(set(dups) == {'G2', 'C2'} and dups['C2']['parent'] == 'tmp:' + dups['G2']['tmp'], 'Shift+D of a group: child copy parented to the group copy')
    added = R['added']
    blender_added = [a for a in added if a['asset'].startswith('blender:')]
    names = sorted(a['name'] for a in blender_added)
    check(names == ['BenchPlace0', 'BenchPlace1', 'HiddenModel', 'NewCrate', 'PostA'], f'new placements {names}')
    bench_keys = {a['asset'] for a in blender_added if a['name'].startswith('Bench')}
    check(len(bench_keys) == 1, 'both placements of a collection use one asset')
    check(next(a for a in blender_added if a['name'] == 'PostA')['parent'] == '', 'section group children are top-level assets')
    assets = {a['key']: a for a in R['assets']}
    print('assets:', sorted(assets), flush=True)
    newc = next(a for a in R['assets'] if a['name'] == 'NewCrate')
    check(newc['isNew'] and newc['collider'] == 'box', 'new model asset with its collider')
    mats = {m['name']: m for m in newc['materials']}
    check(mats['MatProj']['baseMap'] == 'asset:Assets/Textures/brick.png', 'project texture referenced, not copied')
    check(mats['MatPacked']['baseMap'].startswith('Textures/') and os.path.exists(os.path.join(newc['dir'], mats['MatPacked']['baseMap'])),
          'packed texture written as a PNG')
    for a in R['assets']:
        ok = os.path.exists(os.path.join(a['dir'], a['fbx'])) and os.path.exists(os.path.join(a['dir'], a['source']))
        ok &= all(isinstance(p['m'], list) and len(p['m']) == 16 for p in a['parts'])
        check(ok, f"asset {a['key']}: fbx + source written, part matrices sent")
    ba = next(a for a in R['assets'] if a['node'] == 'B1')
    check(ba['mode'] == 'variant' and ba['prefab'] == 'prefab:p2' and ba['isNew'], 'Make editable on a prefab: variant of prefab:p2')
    check(sorted(p['path'] for p in ba['parts']) == ['Body', 'Lid'] and all(p['known'] for p in ba['parts']), 'prefab parts matched by path')
    ra = next(a for a in R['assets'] if a['node'] == 'R1')
    check(ra['hasUv2'], 'UV2 from Unity reported (Unity keeps it instead of generating one)')
    check(not obj('HiddenModel').visible_get() and 'HiddenModel' not in bpy.context.view_layer.objects, 'excluded collection left as it was')
    check(obj('Sub.001').name == 'Sub.001' and obj('NewCrate').get('aa_fbx_name') == 'NewCrate', 'names restored after export, export names stored')

    # FBX contents: object names are the stored export names
    fbx = os.path.join(ba['dir'], ba['fbx'])
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=fbx)
    imported = [o for o in bpy.data.objects if o not in before]
    check(sorted(o.name.split('.')[0] for o in imported if o.type == 'MESH') == sorted(p['object'] for p in ba['parts']), 'FBX object names match parts[].object')
    for o in imported:
        bpy.data.objects.remove(o)

    # ---- Unity applies sync 1 (renames NewCrate to NewCrate_2, Barrel switches to a variant with a new id)
    keymap = {newc['key']: newc['key'] + '_2'}
    src_copy = os.path.join(root, 'src', 'NewCrate_2.blend')
    os.makedirs(os.path.dirname(src_copy), exist_ok=True)
    shutil.copy(os.path.join(newc['dir'], newc['source']), src_copy)
    unity_applies(S, R, remap={'B1': 'B1v'}, keys=keymap)
    sync.stamp_applied()
    check(barrel.get('aa_id') == 'B1v' and barrel.get('aa_bb_key') == ba['key'] and 'aa_editable' not in barrel, 'id remap and asset key stamped on the edited prefab')
    check(obj('NewCrate').get('aa_bb_key') == newc['key'] + '_2' and obj('NewCrate').get('aa_id'), 'new model got its Unity id and final key')
    ck = json.loads(sc.get(sync.COLLECTION_KEYS, '{}'))
    check(len(ck) == 1 and next(iter(ck.values()))['key'] == next(iter(bench_keys))[8:], f'collection asset remembered {ck}')
    first_sig = next(iter(ck.values()))['sig']

    # ---- sync 2: nothing changed except the collection, plus one more placement
    obj('NewCrate').name = 'NewCrate renamed'      # renaming does not re-export
    bench.objects[0].data.vertices[0].co.z += 0.5
    p3 = bpy.data.objects.new('BenchPlace2', None)
    p3.instance_type = 'COLLECTION'
    p3.instance_collection = bench
    sc.collection.objects.link(p3)
    sync.sync_to_unity()
    R2 = result(S)
    check([a['key'] for a in R2['assets']] == [next(iter(bench_keys))[8:]] and not R2['assets'][0]['isNew'], f"only the edited collection is re-exported {[a['key'] for a in R2['assets']]}")
    check(sorted(p['object'] for p in R2['assets'][0]['parts']) == ['BenchSeat'], 'collection parts exported under their source names')
    check([a['name'] for a in R2['added']] == ['BenchPlace2'] and R2['added'][0]['asset'] in bench_keys, 'new placement of a known collection reuses its asset')
    check({n['id'] for n in R2['nodes']} >= {'B1v', 'U:' + dups['G2']['tmp']}, 'remapped and created ids are nodes now')
    unity_applies(S, R2)
    sync.stamp_applied()
    ck = json.loads(sc.get(sync.COLLECTION_KEYS, '{}'))
    check(next(iter(ck.values()))['sig'] not in ('', first_sig) and json.loads(sc.get(sync.COLLECTION_PENDING, '{}')) == {},
          'collection signature updated once Unity applied the re-export')

    sync.sync_to_unity()
    R3 = result(S)
    check(R3['assets'] == [] and R3['added'] == [] and R3['dups'] == [], 'third sync: nothing new to send')
    os.remove(os.path.join(S['outDir'], 'result.json'))

    # ---- reopen a Blender-made asset from its source
    c = bpy.data.collections.new('BBLib')
    c['aa_key'] = 'prefab:pfnew'
    c['aa_kind'] = 'blender'
    c['aa_bb_key'] = 'NewCrate_2'
    c['aa_source'] = src_copy
    inst = bpy.data.objects.new('NewCrate (placed)', None)
    inst.instance_type = 'COLLECTION'
    inst.instance_collection = c
    inst['aa_id'] = 'U:placed'
    sc.collection.objects.link(inst)
    r = editable.make_editable(inst)
    check(r.get('aa_bb_key') == 'NewCrate_2' and r.get('aa_id') == 'U:placed' and r.type == 'MESH', 'Blender-made asset reopened from its source')
    check(export.fbx_name(r) == 'NewCrate', f'reopened asset keeps its export name ({export.fbx_name(r)})')
    check(len(r.data.materials) == 2, 'source keeps its materials')
    sync.sync_to_unity()
    check(result(S)['assets'] == [], 'reopened, unchanged asset is not re-exported')
    os.remove(os.path.join(S['outDir'], 'result.json'))

    # ---- undone in Unity: syncing is refused
    write_json(S['statusPath'], {'sessionId': 'sess1', 'state': 'undone', 'message': 'undone', 'time': ''})
    try:
        sync.sync_to_unity()
        check(False, 'sync refused after an undo in Unity')
    except RuntimeError:
        check(True, 'sync refused after an undo in Unity')
    check(sync.poll_status() and sc.get('aa_unity_state') == 'undone', 'panel learns the session is out of date')

    # ---- add-on pieces
    from aa_bridge import launch
    compile(launch.STUB_TEXT.format(tools=TOOLS), 'stub', 'exec')
    check(True, 'add-on stub compiles')
    ui.register_once()
    check(hasattr(bpy.types, 'AABRIDGE_PT_selection'), 'panels register')
    check(model.describe(obj('Building'))[0] == 'locked', 'describe: locked context')


def main():
    root = tempfile.mkdtemp(prefix='bb_test_')
    try:
        run(root)
    except Exception:
        traceback.print_exc()
        CHECKS.append((False, 'exception'))
    failed = [w for ok, w in CHECKS if not ok]
    print(f'AA_TEST_DONE {len(CHECKS) - len(failed)}/{len(CHECKS)} passed' + (': FAILED ' + '; '.join(failed) if failed else ''), flush=True)
    shutil.rmtree(root, ignore_errors=True)
    sys.exit(1 if failed else 0)


main()
