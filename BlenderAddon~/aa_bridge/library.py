"""The asset library Unity feeds: BlenderSessions/_library/AA_UnityLibrary.blend.

Each Unity asset (prefab, or a mesh + materials combination) is one collection holding its mesh objects; scene files
link these collections and place instances, so every asset exists once. Prefabs are marked as assets with a catalog
per Unity folder, so they can be dragged in from the Asset Browser ("AA Unity" library). Blender-made assets link the
collection from their source .blend instead. Runs in a background Blender (launch.build_library); only what changed
is rebuilt, the rest is a cache.
"""
import hashlib
import json
import os
import struct
import uuid

import bpy
import numpy as np

from .common import mat, progress, read_json, srgb_to_linear, write_json

NS = uuid.UUID('6b1f0c6e-4f2a-4f7e-9a51-3c2b9e1f7a10')
INDEX = 'library_index.json'


def catalog_id(path):
    return str(uuid.uuid5(NS, path))


def load_aamesh(me, path):
    b = open(path, 'rb').read()
    if b[:4] != b'AAM1':
        raise ValueError(f'{path}: not an AAM1 mesh')
    n, flags, ns = struct.unpack_from('<iii', b, 4)
    off = 16
    counts = np.frombuffer(b, '<i4', ns, off).astype(np.int64)
    off += 4 * ns
    pos = np.frombuffer(b, '<f4', n * 3, off)
    off += 12 * n
    nrm = uv = None
    if flags & 1:
        nrm = np.frombuffer(b, '<f4', n * 3, off)
        off += 12 * n
    if flags & 2:
        uv = np.frombuffer(b, '<f4', n * 2, off)
        off += 8 * n
    counts -= counts % 3
    total = int(counts.sum())
    idx = np.frombuffer(b, '<i4', total, off)

    me.clear_geometry()
    me.vertices.add(n)
    me.vertices.foreach_set('co', pos)
    nt = total // 3
    if nt:
        me.loops.add(total)
        me.loops.foreach_set('vertex_index', idx)
        me.polygons.add(nt)
        me.polygons.foreach_set('loop_start', np.arange(0, total, 3, dtype=np.int32))
        try:
            me.polygons.foreach_set('loop_total', np.full(nt, 3, dtype=np.int32))
        except (AttributeError, TypeError, RuntimeError):
            pass  # derived from loop_start in newer Blender
        me.polygons.foreach_set('material_index', np.repeat(np.arange(ns, dtype=np.int32), counts // 3))
        if uv is not None:
            layer = me.uv_layers.new(name='UVMap')
            layer.data.foreach_set('uv', uv.reshape(-1, 2)[idx].ravel())
    me.update()
    if nrm is not None and nt:
        me.polygons.foreach_set('use_smooth', np.ones(nt, dtype=bool))
        try:
            me.normals_split_custom_set_from_vertices(nrm.reshape(-1, 3))
        except Exception:
            pass
    me.validate(clean_customdata=False)
    while len(me.materials) < ns:
        me.materials.append(None)


def _image(path, non_color):
    if not path or not os.path.exists(path):
        return None
    img = bpy.data.images.load(path, check_existing=True)
    if non_color:
        try:
            img.colorspace_settings.name = 'Non-Color'
        except TypeError:
            pass
    return img


def build_material(md, m=None):
    sig = hashlib.md5(json.dumps(md, sort_keys=True).encode()).hexdigest()
    if m is None:
        m = bpy.data.materials.new(md['name'] or 'Material')
        m['aa_key'] = md['key']
    if m.get('aa_sig') == sig:
        return m
    m['aa_sig'] = sig
    m.use_fake_user = True
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    out.location = (500, 0)
    bsdf = nt.nodes.new('ShaderNodeBsdfPrincipled')
    bsdf.location = (150, 0)
    nt.links.new(bsdf.outputs[0], out.inputs[0])

    col = list(md.get('color') or [1, 1, 1, 1]) + [1, 1, 1, 1]
    lin = [srgb_to_linear(c) for c in col[:3]] + [col[3]]
    m.diffuse_color = lin
    bsdf.inputs['Base Color'].default_value = lin
    bsdf.inputs['Metallic'].default_value = md.get('metallic', 0.0)
    bsdf.inputs['Roughness'].default_value = 1.0 - md.get('smoothness', 0.5)
    til = md.get('tiling') or [1, 1, 0, 0]
    uvsrc = []

    def uv_vector():
        if not uvsrc:
            if til[0] == 1 and til[1] == 1 and not til[2] and not til[3]:
                uvsrc.append(None)
            else:
                tc = nt.nodes.new('ShaderNodeTexCoord')
                tc.location = (-1000, 0)
                mp = nt.nodes.new('ShaderNodeMapping')
                mp.location = (-800, 0)
                mp.inputs['Scale'].default_value = (til[0], til[1], 1)
                mp.inputs['Location'].default_value = (til[2], til[3], 0)
                nt.links.new(tc.outputs['UV'], mp.inputs['Vector'])
                uvsrc.append(mp.outputs['Vector'])
        return uvsrc[0]

    def tex(path, non_color, y):
        img = _image(path, non_color)
        if not img:
            return None
        n = nt.nodes.new('ShaderNodeTexImage')
        n.image = img
        n.location = (-550, y)
        v = uv_vector()
        if v is not None:
            nt.links.new(v, n.inputs['Vector'])
        return n

    t = tex(md.get('baseMap'), False, 300)
    if t:
        if any(abs(c - 1.0) > 0.001 for c in lin[:3]):
            mix = nt.nodes.new('ShaderNodeMix')
            mix.data_type = 'RGBA'
            mix.blend_type = 'MULTIPLY'
            mix.location = (-200, 300)
            mix.inputs[0].default_value = 1.0
            mix.inputs[6].default_value = lin
            nt.links.new(t.outputs['Color'], mix.inputs[7])
            nt.links.new(mix.outputs[2], bsdf.inputs['Base Color'])
        else:
            nt.links.new(t.outputs['Color'], bsdf.inputs['Base Color'])
    alpha = md.get('alpha', 'opaque')
    if alpha != 'opaque':
        if t and alpha == 'clip':
            gt = nt.nodes.new('ShaderNodeMath')
            gt.operation = 'GREATER_THAN'
            gt.location = (-200, 100)
            gt.inputs[1].default_value = md.get('cutoff', 0.5)
            nt.links.new(t.outputs['Alpha'], gt.inputs[0])
            nt.links.new(gt.outputs[0], bsdf.inputs['Alpha'])
        elif t:
            nt.links.new(t.outputs['Alpha'], bsdf.inputs['Alpha'])
        else:
            bsdf.inputs['Alpha'].default_value = lin[3]
        for attr, val in (('surface_render_method', 'DITHERED' if alpha == 'clip' else 'BLENDED'), ('blend_method', 'CLIP' if alpha == 'clip' else 'BLEND')):
            try:
                setattr(m, attr, val)
                break
            except (AttributeError, TypeError):
                pass
    nt_tex = tex(md.get('normalMap'), True, -150)
    if nt_tex:
        nm = nt.nodes.new('ShaderNodeNormalMap')
        nm.location = (-200, -150)
        nm.inputs['Strength'].default_value = md.get('normalScale', 1.0)
        nt.links.new(nt_tex.outputs['Color'], nm.inputs['Color'])
        nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    e = md.get('emission') or [0, 0, 0]
    if max(e) > 0.0001:
        bsdf.inputs['Emission Color'].default_value = [srgb_to_linear(c) for c in e[:3]] + [1]
        bsdf.inputs['Emission Strength'].default_value = 1.0
        et = tex(md.get('emissionMap'), False, -450)
        if et:
            nt.links.new(et.outputs['Color'], bsdf.inputs['Emission Color'])
    return m


def build(lib_json):
    L = read_json(lib_json)
    blend = L['blend']
    os.makedirs(L['dir'], exist_ok=True)
    if os.path.exists(blend):
        bpy.ops.wm.open_mainfile(filepath=blend, load_ui=False)
    else:
        bpy.ops.wm.read_homefile(use_empty=True)

    meshes = {me['aa_key']: me for me in bpy.data.meshes if me.get('aa_key') and not me.library}
    mats = {m['aa_key']: m for m in bpy.data.materials if m.get('aa_key') and not m.library}
    colls = {c['aa_key']: c for c in bpy.data.collections if c.get('aa_key') and not c.library}
    files = {r['key']: r['file'] for r in L['meshes']}
    assets = L['assets']
    needed = sorted({p['mesh'] for a in assets for p in a['parts']})
    steps = len(L['materials']) + len(needed) + len(assets)
    step = 0

    for md in L['materials']:
        mats[md['key']] = build_material(md, mats.get(md['key']))
        step += 1
    progress(step, steps, 'materials')

    built = 0
    for k in needed:
        step += 1
        f = files.get(k)
        me = meshes.get(k)
        if not f or not os.path.exists(f) or (me is not None and me.get('aa_file') == f):
            continue
        if me is None:
            me = bpy.data.meshes.new(k[:60])
            me['aa_key'] = k
            meshes[k] = me
        try:
            load_aamesh(me, f)
            me['aa_file'] = f
            built += 1
        except Exception as e:
            print(f'AA_WARN mesh {k}: {e}', flush=True)
        if built % 20 == 0:
            progress(step, steps, f'meshes ({built} converted)')

    cats = {}
    rebuilt = 0
    for a in assets:
        step += 1
        sig = hashlib.md5((a['hash'] + json.dumps(a['parts'], sort_keys=True) + a.get('source', '')).encode()).hexdigest()
        c = colls.get(a['key'])
        if c is None:
            c = bpy.data.collections.new(a['name'])
            c['aa_key'] = a['key']
            colls[a['key']] = c
        c.use_fake_user = True
        if c.get('aa_sig') != sig:
            for o in list(c.objects):
                bpy.data.objects.remove(o)
            for ch in list(c.children):
                c.children.unlink(ch)
            c['aa_sig'] = sig
            c['aa_kind'] = a['kind']
            if a['kind'] == 'blender':
                c['aa_bb_key'] = a['bbKey']
                c['aa_source'] = a['source']
                try:
                    with bpy.data.libraries.load(a['source'], link=True) as (src, dst):
                        dst.collections = [n for n in src.collections if n == a['sourceCollection']]
                    if dst.collections and dst.collections[0]:
                        c.children.link(dst.collections[0])
                except Exception as e:
                    print(f"AA_WARN source {a['source']}: {e}", flush=True)
            else:
                many = len(a['parts']) > 1
                for i, p in enumerate(a['parts']):
                    me = meshes.get(p['mesh'])
                    if me is None:
                        continue
                    o = bpy.data.objects.new(f"{a['name']}_{i}" if many else a['name'], me)
                    for s, mk in enumerate(p['mats']):
                        if s < len(o.material_slots):
                            o.material_slots[s].link = 'OBJECT'
                            o.material_slots[s].material = mats.get(mk)
                    o.matrix_world = mat(p['m'])
                    o['aa_part'] = p.get('path', '')  # where this part sits inside the Unity prefab
                    c.objects.link(o)
            rebuilt += 1
        if c.name != a['name'] and not bpy.data.collections.get(a['name']):
            c.name = a['name']
        if a.get('browsable'):
            if c.asset_data is None:
                c.asset_mark()
            cid = catalog_id(a['catalog'])
            cats[a['catalog']] = cid
            c.asset_data.catalog_id = cid
            c.asset_data.description = a.get('prefab', '')
        elif c.asset_data is not None:
            c.asset_clear()
        if step % 50 == 0:
            progress(step, steps, f'assets ({rebuilt} rebuilt)')

    # Asset Browser catalogs (Unity folders), merged with whatever is already there
    cat_file = os.path.join(L['dir'], 'blender_assets.cats.txt')
    known = {}
    if os.path.exists(cat_file):
        for line in open(cat_file, encoding='utf-8'):
            parts = line.strip().split(':')
            if len(parts) >= 2 and not line.startswith(('#', 'VERSION')):
                known[parts[1]] = parts[0]
    for path in list(cats):
        segs = path.split('/')
        for i in range(1, len(segs)):
            parent = '/'.join(segs[:i])
            cats.setdefault(parent, catalog_id(parent))
    known.update(cats)
    with open(cat_file, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# This is an Asset Catalog Definition file for Blender.\n# Written by the Unity Blender Bridge.\n\nVERSION 1\n\n')
        for path in sorted(known):
            f.write(f"{known[path]}:{path}:{path.replace('/', '-')}\n")

    bpy.ops.wm.save_as_mainfile(filepath=blend, compress=False, relative_remap=False)
    write_json(os.path.join(L['dir'], INDEX), {k: c.name for k, c in colls.items()})
    progress(steps, steps, 'saved')
    print(f'AA_LIBRARY_DONE assets={len(assets)} rebuilt={rebuilt} meshes={built}', flush=True)
