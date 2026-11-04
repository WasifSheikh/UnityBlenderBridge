"""Sync to Unity: writes <session>/out/result.json for Unity to apply.

    nodes   every editable Unity object still here: transform (world), parent, name
    dups    Shift+D / Alt+D copies of a Unity object (same aa_id): Unity duplicates the original
    added   collection instances dragged in from the "AA Unity" asset library, and new models
    assets  new or edited models: FBX + textures + material values + a source .blend each

Anything with geometry that is not from Unity is a new model. Its top object is the asset (its children come with
it); its origin becomes the prefab's pivot. Custom property "collider" on it: mesh (default), box or none.
After Unity applies, applied.json tells this file the Unity ids of what it created (stamp_applied, polled).
"""
import datetime
import hashlib
import json
import os
import re
import shutil
import uuid
from contextlib import contextmanager

import bpy
import numpy as np
from mathutils import Matrix

from .common import flat, read_json, slug, write_json
from . import session as sess

GEO = {'MESH', 'CURVE', 'SURFACE', 'META', 'FONT'}
TEX_EXT = {'.png', '.jpg', '.jpeg', '.tga', '.tif', '.tiff', '.exr', '.psd'}
SOURCE_COLLECTION = 'AA_Asset'


def is_instance(o):
    c = o.instance_collection if o.type == 'EMPTY' and o.instance_type == 'COLLECTION' else None
    return c is not None and bool(c.get('aa_key'))


def collection_placement(o):
    return o.type == 'EMPTY' and o.instance_type == 'COLLECTION' and o.instance_collection is not None


def has_geo(o):
    return o.type in GEO or any(d.type in GEO for d in o.children_recursive)


def is_edit_root(o):
    """A Unity object being modelled here: a Blender-made asset opened from its source, or any Unity object turned
    into local geometry with Make editable. Its geometry is sent to Unity when it changes."""
    return 'aa_id' in o and bool(o.get('aa_bb_key') or o.get('aa_editable')) and not collection_placement(o)


def plain_new(o):
    return ('aa_id' not in o and not collection_placement(o) and not o.get('aa_locked')
            and o.type in GEO | {'EMPTY'} and not (o.type == 'EMPTY' and o.get('map_group')))


def new_root(o):
    """o has no Unity id and holds geometry: True when it is the top of a new model."""
    a = o.parent
    while a is not None:
        if is_edit_root(a) or (plain_new(a) and has_geo(a)):
            return False
        if 'aa_id' in a or collection_placement(a):
            return True
        a = a.parent
    return True


def asset_parts(root):
    """root and its children, minus other Unity objects parented under it (they stay their own nodes)."""
    out, stack = [], [root]
    while stack:
        o = stack.pop()
        out.append(o)
        stack.extend(c for c in o.children if 'aa_id' not in c and not collection_placement(c))
    return out


def geo_centers(root):
    """Centre of the asset's geometry in world space and in the root's own space (pivot-relative)."""
    from mathutils import Vector
    pts = [o.matrix_world @ Vector(c) for o in asset_parts(root) if o.type in GEO for c in o.bound_box]
    if not pts:
        return [0, 0, 0], [0, 0, 0]
    lo = Vector([min(p[i] for p in pts) for i in range(3)])
    hi = Vector([max(p[i] for p in pts) for i in range(3)])
    w = (lo + hi) / 2
    return list(w), list(root.matrix_world.inverted_safe() @ w)


def pivot_moved(root):
    """True when the origin moved differently from the geometry since the last sync: a moved pivot, as opposed to
    moving the whole object (both move together) or editing geometry (origin stays)."""
    from mathutils import Vector
    if 'aa_base_world' not in root or 'aa_base_origin' not in root:
        return False
    w, _ = geo_centers(root)
    od = root.matrix_world.translation - Vector(root['aa_base_origin'])
    gd = Vector(w) - Vector(root['aa_base_world'])
    return od.length > 0.01 and (gd - od).length > 0.01


def asset_hash(root):
    h = hashlib.md5()
    dg = bpy.context.evaluated_depsgraph_get()
    inv = root.matrix_world.inverted_safe()
    for o in sorted(asset_parts(root), key=lambda x: x.name):
        h.update(o.name.encode())
        h.update(np.array(flat(inv @ o.matrix_world), dtype=np.float32).round(5).tobytes())
        if o.type in GEO:
            ev = o.evaluated_get(dg)
            try:
                me = ev.to_mesh()
                co = np.empty(len(me.vertices) * 3, dtype=np.float32)
                me.vertices.foreach_get('co', co)
                h.update(co.round(5).tobytes())
                lv = np.empty(len(me.loops), dtype=np.int32)
                me.loops.foreach_get('vertex_index', lv)
                h.update(lv.tobytes())
                if me.uv_layers.active:
                    uv = np.empty(len(me.loops) * 2, dtype=np.float32)
                    me.uv_layers.active.data.foreach_get('uv', uv)
                    h.update(uv.round(5).tobytes())
                ev.to_mesh_clear()
            except Exception:
                pass
            for s in o.material_slots:
                m = s.material
                if m is None:
                    continue
                h.update(m.name.encode())
                if m.use_nodes and m.node_tree:
                    for n in m.node_tree.nodes:
                        h.update(n.bl_idname.encode())
                        if getattr(n, 'image', None):
                            h.update(n.image.name.encode())
                        for i in n.inputs:
                            if not i.is_linked and hasattr(i, 'default_value'):
                                try:
                                    h.update(np.array(i.default_value, dtype=np.float32).tobytes())
                                except Exception:
                                    pass
    return h.hexdigest()[:16]


# ---------------------------------------------------------------- materials
def _image_from(socket):
    if socket is None or not socket.is_linked:
        return None
    stack, seen = [socket.links[0].from_node], set()
    while stack:
        n = stack.pop()
        if n in seen:
            continue
        seen.add(n)
        if n.type == 'TEX_IMAGE' and n.image is not None:
            return n.image
        for i in n.inputs:
            for link in i.links:
                stack.append(link.from_node)
    return None


def project_asset(img, project):
    """'Assets/...' when the image file is already part of the Unity project (then Unity reuses it), else ''."""
    if img is None or img.packed_file is not None or not img.filepath or not project:
        return ''
    p = os.path.abspath(bpy.path.abspath(img.filepath))
    a = os.path.abspath(os.path.join(project, 'Assets'))
    if os.path.normcase(p).startswith(os.path.normcase(a) + os.sep) and os.path.exists(p):
        return 'Assets/' + p[len(a) + 1:].replace(os.sep, '/')  # keep the real case: Unity paths are case-sensitive
    return ''


class _Images:
    def __init__(self, tex_dir, project=''):
        self.dir = tex_dir
        self.project = project
        self.done = {}
        self.names = set()

    def _name(self, base, ext):
        n = base + ext
        i = 2
        while n.lower() in self.names:
            n = f'{base}_{i}{ext}'
            i += 1
        self.names.add(n.lower())
        return n

    def save(self, img):
        if img is None:
            return ''
        if img.name in self.done:
            return self.done[img.name]
        existing = project_asset(img, self.project)
        if existing:
            self.done[img.name] = 'asset:' + existing  # already a Unity texture: reuse it, copy nothing
            return self.done[img.name]
        os.makedirs(self.dir, exist_ok=True)
        src = bpy.path.abspath(img.filepath) if img.filepath else ''
        ext = os.path.splitext(src)[1].lower()
        base = slug(os.path.splitext(img.name)[0])
        rel = ''
        if img.packed_file is None and src and os.path.exists(src) and ext in TEX_EXT:
            name = self._name(base, ext)
            shutil.copy2(src, os.path.join(self.dir, name))
            rel = 'Textures/' + name
        else:
            name = self._name(base, '.png')
            dst = os.path.join(self.dir, name)
            try:
                img.save(filepath=dst)
            except Exception:
                try:
                    c = img.copy()
                    c.filepath_raw = dst
                    c.file_format = 'PNG'
                    c.save()
                    bpy.data.images.remove(c)
                except Exception:
                    img.save_render(dst)
            rel = 'Textures/' + name
        self.done[img.name] = rel
        return rel

    def mask(self, metal_img, rough_img, metal, rough, name):
        """URP metallic map: R = metallic, A = smoothness (1 - roughness)."""
        imgs = [i for i in (metal_img, rough_img) if i is not None]
        w = max(i.size[0] for i in imgs)
        h = max(i.size[1] for i in imgs)

        def channel(img, default):
            if img is None:
                return np.full((h, w), default, dtype=np.float32)
            if tuple(img.size) != (w, h):
                img = img.copy()
                img.scale(w, h)
            px = np.empty(w * h * 4, dtype=np.float32)
            img.pixels.foreach_get(px)
            return px.reshape(h, w, 4)[:, :, 0]

        out = np.zeros((h, w, 4), dtype=np.float32)
        out[:, :, 0] = channel(metal_img, metal)
        out[:, :, 3] = 1.0 - channel(rough_img, rough)
        fname = self._name(slug(name) + '_MetallicSmoothness', '.png')
        im = bpy.data.images.new('aa_mask', w, h, alpha=True)
        try:
            im.colorspace_settings.name = 'Non-Color'
        except TypeError:
            pass
        im.pixels.foreach_set(out.ravel())
        im.filepath_raw = os.path.join(self.dir, fname)
        im.file_format = 'PNG'
        im.save()
        bpy.data.images.remove(im)
        return 'Textures/' + fname


def describe_material(m, images):
    r = {'name': m.name, 'unity': m.get('aa_key', ''), 'color': list(m.diffuse_color), 'metallic': 0.0, 'smoothness': 0.5, 'normalScale': 1.0,
         'alpha': 'opaque', 'cutoff': 0.5, 'emission': [0, 0, 0], 'baseMap': '', 'normalMap': '', 'maskMap': '', 'emissionMap': ''}
    if r['unity'] or not m.use_nodes or m.node_tree is None:
        return r  # a Unity material: Unity keeps using the real one
    nodes = m.node_tree.nodes
    bsdf = None
    out = next((n for n in nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output), None)
    if out is not None and out.inputs['Surface'].is_linked:
        stack = [out.inputs['Surface'].links[0].from_node]
        while stack and bsdf is None:
            n = stack.pop()
            if n.type == 'BSDF_PRINCIPLED':
                bsdf = n
            else:
                stack.extend(l.from_node for i in n.inputs for l in i.links)
    bsdf = bsdf or next((n for n in nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is None:
        return r
    I = bsdf.inputs
    base = _image_from(I['Base Color'])
    if base is not None:
        r['baseMap'] = images.save(base)
        r['color'] = [1, 1, 1, 1]
    else:
        r['color'] = list(I['Base Color'].default_value)
    a = I['Alpha']
    if a.is_linked or a.default_value < 0.999:
        method = getattr(m, 'surface_render_method', '') or getattr(m, 'blend_method', '')
        r['alpha'] = 'blend' if method in ('BLENDED', 'BLEND') else 'clip'
        if not a.is_linked:
            r['color'][3] = a.default_value
        else:
            gt = a.links[0].from_node
            if gt.type == 'MATH' and gt.operation == 'GREATER_THAN':
                r['cutoff'] = gt.inputs[1].default_value
    mi, ri = _image_from(I['Metallic']), _image_from(I['Roughness'])
    if mi is not None or ri is not None:
        r['maskMap'] = images.mask(mi, ri, I['Metallic'].default_value, I['Roughness'].default_value, m.name)
    r['metallic'] = float(I['Metallic'].default_value)
    r['smoothness'] = 1.0 - float(I['Roughness'].default_value)
    n = I['Normal']
    if n.is_linked and n.links[0].from_node.type == 'NORMAL_MAP':
        nm = n.links[0].from_node
        img = _image_from(nm.inputs['Color'])
        if img is not None:
            r['normalMap'] = images.save(img)
            r['normalScale'] = float(nm.inputs['Strength'].default_value)
    ec, es = I.get('Emission Color'), I.get('Emission Strength')
    strength = es.default_value if es is not None else 0.0
    if ec is not None and strength > 0:
        img = _image_from(ec)
        if img is not None:
            r['emissionMap'] = images.save(img)
            r['emission'] = [strength] * 3
        elif max(ec.default_value[:3]) > 0:
            r['emission'] = [c * strength for c in ec.default_value[:3]]
    return r


# ---------------------------------------------------------------- export of one model
def _depth(o, within):
    d, p = 0, o.parent
    while p is not None:
        if p in within:
            d += 1
        p = p.parent
    return d


class _MaterialFingerprint:
    """Use the same material interpretation as the exporter, without writing textures."""

    def __init__(self, project):
        self.project = project
        self.images = {}
        self.materials = {}

    def save(self, img):
        if img not in self.images:
            path = project_asset(img, self.project)
            if path:
                self.images[img] = 'asset:' + path
            else:
                pixels = np.empty(len(img.pixels), dtype=np.float32)
                img.pixels.foreach_get(pixels)
                self.images[img] = (tuple(img.size), img.colorspace_settings.name,
                                    img.alpha_mode, hashlib.sha256(pixels.tobytes()).hexdigest()
                                    if pixels.size else img.name)
        return self.images[img]

    def mask(self, metal_img, rough_img, metal, rough, name):
        return tuple(self.save(img) if img is not None else value
                     for img, value in ((metal_img, metal), (rough_img, rough)))

    def material(self, mat):
        if mat is None:
            return None
        if mat not in self.materials:
            description = describe_material(mat, self)
            del description['name']
            self.materials[mat] = description
        return self.materials[mat]


def prefab_signature(root, materials):
    """Fingerprint exported mesh data and local layout, independent of placement and object names."""
    dg = bpy.context.evaluated_depsgraph_get()
    inv = root.matrix_world.inverted_safe()
    parts = asset_parts(root)
    signatures = []
    for o in parts:
        if o.type not in GEO:
            continue
        ev = o.evaluated_get(dg)
        mesh = ev.to_mesh()
        if mesh is None:
            return None
        try:
            h = hashlib.sha256()

            def field(items, name, width, dtype):
                data = np.empty(len(items) * width, dtype=dtype)
                items.foreach_get(name, data)
                h.update(len(data).to_bytes(8, 'little'))
                h.update(data.tobytes())

            field(mesh.vertices, 'co', 3, np.float32)
            field(mesh.edges, 'vertices', 2, np.int32)
            field(mesh.loops, 'vertex_index', 1, np.int32)
            field(mesh.polygons, 'loop_total', 1, np.int32)
            field(mesh.polygons, 'material_index', 1, np.int32)
            field(mesh.polygons, 'use_smooth', 1, np.bool_)
            field(mesh.corner_normals, 'vector', 3, np.float32)
            for layer in mesh.uv_layers:
                field(layer.uv, 'vector', 2, np.float32)
            for layer in mesh.color_attributes:
                h.update(layer.domain.encode())
                field(layer.data, 'color', 4, np.float32)
            local = Matrix.Identity(4) if o == root else inv @ o.matrix_world
            matrix = np.array(flat(local), dtype=np.float32).round(5)
            matrix[matrix == 0] = 0
            h.update(matrix.tobytes())
            h.update(json.dumps([materials.material(s.material) for s in ev.material_slots],
                                sort_keys=True).encode())
            signatures.append((_depth(o, parts), h.hexdigest()))
        finally:
            ev.to_mesh_clear()
    return (str(root.get('collider', 'mesh')), tuple(sorted(signatures))) if signatures else None


@contextmanager
def collection_model(placement):
    """Stage an untagged Blender collection in prefab-local space without changing the placement."""
    scene_collection = bpy.context.scene.collection
    root = bpy.data.objects.new(placement.instance_collection.name, None)
    created = [root]
    try:
        scene_collection.objects.link(root)
        root['collider'] = str(placement.get('collider', 'mesh'))

        def stage(collection, matrix):
            origin = matrix @ Matrix.Translation(-collection.instance_offset)
            for source in collection.all_objects:
                local = origin @ source.matrix_world
                if collection_placement(source):
                    stage(source.instance_collection, local)
                elif source.type in GEO:
                    obj = source.copy()
                    created.append(obj)
                    for prop in list(obj.keys()):
                        if prop.startswith('aa_'):
                            del obj[prop]
                    obj.animation_data_clear()
                    obj.constraints.clear()
                    scene_collection.objects.link(obj)
                    obj.parent = root
                    obj.matrix_parent_inverse = Matrix.Identity(4)
                    obj.matrix_world = local

        stage(placement.instance_collection, Matrix.Identity(4))
        bpy.context.view_layer.update()
        yield root
    finally:
        for obj in reversed(created):
            bpy.data.objects.remove(obj, do_unlink=True)


def export_asset(root, key, out_dir, is_new, project='', node=''):
    """FBX + materials + source .blend of root and its children. For a prefab being modelled, also how the Blender
    objects map onto the prefab's parts (by path inside the prefab) and how Unity should apply it (mode)."""
    d = os.path.join(out_dir, 'assets', key)
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d, exist_ok=True)
    parts = asset_parts(root)
    mats = []
    for o in parts:
        for s in getattr(o, 'material_slots', []):
            if s.material is not None and s.material not in mats:
                mats.append(s.material)
    images = _Images(os.path.join(d, 'Textures'), project)
    bpy.context.view_layer.update()
    to_root = root.matrix_world.inverted_safe()
    rel = {o.name: flat(to_root @ o.matrix_world) for o in parts}  # before the root is moved to the origin for export
    materials = [describe_material(m, images) for m in mats]
    h = asset_hash(root)

    vl = bpy.context.view_layer
    state = [(o, o.hide_get(), o.hide_select, o.hide_viewport, o.select_get()) for o in vl.objects]
    active = vl.objects.active
    saved = root.matrix_world.copy()
    coll = None
    try:
        for o in parts:
            o.hide_viewport = False
            o.hide_select = False
            o.hide_set(False)
        for o in vl.objects:
            o.select_set(False)
        for o in parts:
            if o.name in vl.objects:
                o.select_set(True)
        vl.objects.active = root
        root.matrix_world = Matrix.Identity(4)  # the asset's origin becomes the prefab pivot
        vl.update()
        bpy.ops.export_scene.fbx(
            filepath=os.path.join(d, key + '.fbx'), use_selection=True, object_types={'MESH', 'EMPTY', 'OTHER'},
            # mesh data in Unity axes; child object transforms in the FBX are not reliable with this setting, so each
            # part's transform relative to the asset root is sent separately (parts[].m) and Unity sets it
            use_custom_props=False, axis_forward='-Z', axis_up='Y', bake_space_transform=True,
            apply_scale_options='FBX_SCALE_ALL', apply_unit_scale=True, global_scale=1.0, mesh_smooth_type='FACE',
            use_mesh_modifiers=True, bake_anim=False, add_leaf_bones=False, use_tspace=False, path_mode='AUTO')

        # editable source for later sessions: the objects, their data and packed textures
        for m in mats:
            for img in {n.image for n in (m.node_tree.nodes if m.use_nodes and m.node_tree else []) if getattr(n, 'image', None)}:
                rel = project_asset(img, project)
                if rel:
                    img['aa_project_path'] = rel  # Unity texture: kept as a file, found again by project path on any PC
                elif img.packed_file is None and img.source in {'FILE', 'GENERATED'}:
                    try:
                        img.pack()
                    except Exception:
                        pass
        coll = bpy.data.collections.new(SOURCE_COLLECTION)
        for o in parts:
            coll.objects.link(o)
        bpy.data.libraries.write(os.path.join(d, key + '.blend'), {coll}, path_remap='NONE', fake_user=True, compress=True)
    finally:
        if coll is not None:
            for o in list(coll.objects):
                coll.objects.unlink(o)
            bpy.data.collections.remove(coll)
        root.matrix_world = saved
        for o, hid, hs, hv, sel in state:
            try:
                o.hide_viewport = hv
                o.hide_select = hs
                o.hide_set(hid)
                o.select_set(sel)
            except Exception:
                pass
        vl.objects.active = active
    root['aa_pending_hash'] = h
    geo = [o for o in parts if o.type in GEO]
    known = {o.get('aa_part') for o in geo if 'aa_part' in o}
    original = json.loads(root.get('aa_orig_parts', '[]'))
    return {'key': key, 'name': root.name, 'fbx': key + '.fbx', 'source': key + '.blend', 'sourceCollection': SOURCE_COLLECTION,
            'mode': str(root.get('aa_edit_mode', '')), 'prefab': str(root.get('aa_prefab', '')), 'pivotMoved': pivot_moved(root),
            'parts': [{'object': o.name, 'path': str(o.get('aa_part', '')), 'known': 'aa_part' in o, 'm': rel[o.name],
                       'depth': _depth(o, parts)} for o in geo],
            'removed': [p for p in original if p not in known],
            'collider': str(root.get('collider', 'mesh')), 'hash': h, 'dir': d.replace('\\', '/'), 'isNew': is_new, 'node': node,
            'materials': materials}


# ---------------------------------------------------------------- sync
def sync_to_unity():
    S = sess.load()
    if S is None:
        raise RuntimeError('This file is not a Unity Blender Bridge session (open it from Unity).')
    if bpy.context.scene.get('aa_session') != S['sessionId']:
        raise RuntimeError('Unity has started a newer session for this scene. Open the scene from Unity again.')
    stamp_applied()
    if bpy.context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()  # every matrix_world current before anything is measured
    out = S['outDir']
    if os.path.exists(os.path.join(out, 'result.json')):
        raise RuntimeError('Apply or discard the pending sync in Unity before sending another.')
    os.makedirs(out, exist_ok=True)
    shutil.rmtree(os.path.join(out, 'assets'), ignore_errors=True)

    objs = [o for o in bpy.context.scene.objects if not o.get('aa_locked')]
    # tmp ids must be unique (a copy of an unsynced object copies its tmp id)
    seen = set()
    for o in objs:
        if 'aa_pending_hash' in o:
            del o['aa_pending_hash']
        t = o.get('aa_tmp')
        if t and t in seen:
            del o['aa_tmp']
        elif t:
            seen.add(t)

    def tmp(o):
        if 'aa_tmp' not in o:
            o['aa_tmp'] = uuid.uuid4().hex[:12]
        return o['aa_tmp']

    groups = {}
    for o in objs:
        if 'aa_id' in o:
            groups.setdefault(o['aa_id'], []).append(o)
    def pick_original(lst):
        """Of several objects sharing a Unity id (Shift+D), the original: the one still named as built; else one
        that is not an auto-suffixed copy of that name (the original may have been renamed); else the first."""
        if len(lst) == 1:
            return lst[0]
        exact = [o for o in lst if o.name == o.get('aa_name')]
        if exact:
            return exact[0]
        base = lst[0].get('aa_name', '')
        renamed = [o for o in lst if not re.fullmatch(re.escape(base) + r'\.\d{3}', o.name)]
        return sorted(renamed or lst, key=lambda x: x.name)[0]

    original = {i: pick_original(lst) for i, lst in groups.items()}

    ref = {}
    dups, insts, roots, collections, edits = [], [], [], [], []
    for i, o in original.items():
        ref[o] = i
        if is_edit_root(o):
            edits.append(o)
    for i, lst in groups.items():
        for o in lst:
            if o is not original[i]:
                ref[o] = 'tmp:' + tmp(o)
                dups.append((o, i))
    for o in objs:
        if 'aa_id' in o:
            continue
        if is_instance(o):
            ref[o] = 'tmp:' + tmp(o)
            insts.append(o)
        elif collection_placement(o):
            collections.append(o)
        elif plain_new(o) and (o.type in GEO or (o.instance_type == 'NONE' and has_geo(o))) and new_root(o):
            ref[o] = 'tmp:' + tmp(o)
            roots.append(o)

    def parent_of(o):
        p = o.parent
        while p is not None:
            if p.get('aa_locked'):
                return ''
            if p in ref:
                return ref[p]
            p = p.parent
        return ''

    def entry(o, **kw):
        e = {'name': o.name, 'parent': parent_of(o), 'm': flat(o.matrix_world)}
        e.update(kw)
        return e

    # names only travel when they were changed here (Unity names can themselves end in ".001")
    assets = []
    project = S.get('project', '')
    materials = _MaterialFingerprint(project)
    used = {o['aa_bb_key'].casefold() for o in edits if o.get('aa_bb_key')}
    by_signature = {}
    new_placements = []

    def reserve(name):
        base = slug(name)
        key, n = base, 2
        while key.casefold() in used:
            key, n = f'{base}_{n}', n + 1
        used.add(key.casefold())
        return key

    def new_model(root, name):
        signature = prefab_signature(root, materials)
        key = by_signature.get(signature) if signature is not None else None
        if key is None:
            key = reserve(name)
            assets.append(export_asset(root, key, out, True, project))
            if signature is not None:
                by_signature[signature] = key
        else:
            root['aa_pending_hash'] = asset_hash(root)
        return key

    for o in sorted(roots, key=lambda obj: obj.name):
        new_placements.append((o, new_model(o, o.name)))

    collection_keys = {}
    for o in sorted(collections, key=lambda obj: obj.name):
        source = o.instance_collection
        identity = (source, str(o.get('collider', 'mesh')))
        if identity not in collection_keys:
            with collection_model(o) as root:
                collection_keys[identity] = new_model(root, source.name) if has_geo(root) else None
        key = collection_keys[identity]
        if key is not None:
            ref[o] = 'tmp:' + tmp(o)
            new_placements.append((o, key))

    nodes = [entry(o, id=i, name=o.name if o.name != o.get('aa_name') else '') for i, o in original.items()]
    dup_out = [entry(o, tmp=o['aa_tmp'], of=i) for o, i in dups]
    added = [entry(o, tmp=o['aa_tmp'], asset=o.instance_collection['aa_key']) for o in insts]
    added.extend(entry(o, tmp=o['aa_tmp'], asset='blender:' + key) for o, key in new_placements)
    edited_keys = {}
    for o in edits:
        if asset_hash(o) == o.get('aa_asset_hash'):
            continue
        key = o.get('aa_bb_key')
        if not key:  # first edit of a Unity object's geometry: it becomes a new mesh asset
            key = reserve(o.name)
        if key in edited_keys:
            signature = prefab_signature(o, materials)
            if signature is None or signature != prefab_signature(edited_keys[key], materials):
                raise RuntimeError(f'Multiple placements of {key} have different mesh edits. Edit one placement per prefab before syncing.')
            o['aa_pending_hash'] = asset_hash(o)
            continue
        edited_keys[key] = o
        assets.append(export_asset(o, key, out, not o.get('aa_bb_key'), project, o['aa_id']))

    if bpy.data.filepath:
        bpy.ops.wm.save_mainfile()
    write_json(os.path.join(out, 'result.json'), {
        'sessionId': S['sessionId'], 'written': datetime.datetime.now().isoformat(timespec='seconds'), 'blend': bpy.data.filepath,
        'nodes': nodes, 'dups': dup_out, 'added': added, 'assets': assets})
    parts = [f'{len(nodes)} objects', f'{len(dup_out)} copies', f'{len(insts)} added from the library',
             f'{len(new_placements)} new placements', f'{len(assets)} models exported']
    msg = 'Sent to Unity: ' + ', '.join(parts) + '. Waiting for Unity to apply...'
    bpy.context.scene['aa_status'] = msg
    return msg


def stamp_applied():
    """Reads applied.json (written by Unity after it applied a sync) and gives the objects created there their ids."""
    scene = bpy.context.scene
    S = sess.load()
    if S is None or not os.path.exists(S.get('appliedPath', '')):
        return 0
    p = S['appliedPath']
    mt = os.path.getmtime(p)
    if scene.get('aa_applied_mtime') == mt:
        return 0
    A = read_json(p)
    scene['aa_applied_mtime'] = mt
    if A.get('sessionId') != S['sessionId'] or scene.get('aa_session') != S['sessionId']:
        return 0
    by_tmp = {o['aa_tmp']: o for o in scene.objects if 'aa_tmp' in o}
    n = 0
    for m in A.get('ids', []):
        if m.get('tmp'):
            o = by_tmp.get(m['tmp'])
            if o is None:
                continue
            o['aa_id'] = m['id']
            o['aa_name'] = o.name
            del o['aa_tmp']
            if m.get('asset'):
                o['aa_bb_key'] = m['asset']
                o['aa_asset_hash'] = o.get('aa_pending_hash') or m.get('hash', '')
                if 'aa_pending_hash' in o:
                    del o['aa_pending_hash']
            n += 1
        elif m.get('id') and m.get('asset'):
            for o in scene.objects:
                if o.get('aa_id') == m['id'] and is_edit_root(o):
                    o['aa_bb_key'] = m['asset']
                    o['aa_asset_hash'] = o.get('aa_pending_hash') or m.get('hash', '')
                    if 'aa_pending_hash' in o:
                        del o['aa_pending_hash']
                    o['aa_base_world'], o['aa_base_local'] = geo_centers(o)
                    o['aa_base_origin'] = list(o.matrix_world.translation)
                    if 'aa_editable' in o:
                        del o['aa_editable']
        elif m.get('asset'):
            for o in scene.objects:
                if o.get('aa_bb_key') == m['asset'] and is_edit_root(o):
                    if 'aa_pending_hash' in o:
                        o['aa_asset_hash'] = o['aa_pending_hash']
                        del o['aa_pending_hash']
                    o['aa_base_world'], o['aa_base_local'] = geo_centers(o)
                    o['aa_base_origin'] = list(o.matrix_world.translation)
    scene['aa_status'] = f"Unity applied {A.get('applied', '').replace('T', ' ')}: {A.get('summary', '')}"
    try:
        if bpy.data.filepath:
            bpy.ops.wm.save_mainfile()
    except Exception:
        pass
    return n


def _local_material(m, cache):
    """Local copy of a material linked from the library (keeps aa_key, so Unity keeps its own material)."""
    if m is None or m.library is None:
        return m
    if m.name not in cache:
        c = m.copy()
        c.name = m.name
        if c.use_nodes and c.node_tree:
            for n in c.node_tree.nodes:
                img = getattr(n, 'image', None)
                if img is not None and img.library is not None:
                    n.image = cache.setdefault('img:' + img.name, img.copy())
        cache[m.name] = c
    return cache[m.name]


def _swap_in(o, root, extra=None):
    """root takes o's place: same parent, world matrix, children, name and Unity id."""
    world, parent = o.matrix_world.copy(), o.parent
    kids = [(k, k.matrix_world.copy()) for k in o.children]
    ident = {k: o[k] for k in ('aa_id',) if k in o}
    name = o.name
    bpy.data.objects.remove(o)
    root.parent = parent
    root.matrix_world = world
    for k, mw in kids:
        k.parent = root
        k.matrix_world = mw
    root.name = name
    for k, v in ident.items():
        root[k] = v
    for k, v in (extra or {}).items():
        root[k] = v
    root['aa_name'] = root.name


def make_editable(o, mode='variant'):
    """Turn a Unity object (a linked instance) into local geometry you can model. Its materials stay the Unity ones.
    For a prefab instance, mode decides what the new geometry becomes in Unity:
        variant  a Prefab Variant of the original, used by this instance only (inherits all the prefab's components)
        source   the original prefab itself, so every copy in every scene changes"""
    c = o.instance_collection
    if c.get('aa_bb_key'):
        return edit_asset(o)
    target = o.users_collection[0] if o.users_collection else bpy.context.scene.collection
    parts = [x for x in c.all_objects if x.type in GEO]
    if not parts:
        raise RuntimeError(f'{o.name} has no geometry to edit')
    cache = {}
    made = []
    for p in parts:
        me = p.data.copy()
        for i, slot in enumerate(p.material_slots):
            if i < len(me.materials):
                me.materials[i] = _local_material(slot.material, cache)
        n = bpy.data.objects.new(p.name, me)
        n['aa_part'] = p.get('aa_part', '')
        target.objects.link(n)
        made.append((n, p.matrix_world.copy()))
    if len(made) == 1 and made[0][1] == Matrix.Identity(4):
        root = made[0][0]
    else:
        root = bpy.data.objects.new(o.name, None)
        target.objects.link(root)
        for n, local in made:
            n.parent = root
            n.matrix_parent_inverse = Matrix.Identity(4)
            n.matrix_basis = local
    extra = {'aa_editable': 1}
    if c.get('aa_kind') == 'prefab':
        extra.update(aa_prefab=c['aa_key'], aa_edit_mode=mode,
                     aa_orig_parts=json.dumps(sorted({p.get('aa_part', '') for p in parts})))
    _swap_in(o, root, extra)
    bpy.context.view_layer.update()
    root['aa_base_world'], root['aa_base_local'] = geo_centers(root)
    root['aa_base_origin'] = list(root.matrix_world.translation)
    root['aa_asset_hash'] = asset_hash(root)  # unchanged geometry is not sent
    return root


def relink_project_images():
    """Source .blend files store Unity textures by absolute path, which differs between PCs. Images tagged with their
    'Assets/...' path are pointed at this machine's project."""
    S = sess.load()
    project = (S or {}).get('project', '')
    if not project:
        return
    for img in bpy.data.images:
        rel = img.get('aa_project_path')
        if not rel or img.packed_file is not None:
            continue
        p = os.path.normpath(os.path.join(project, rel))
        if os.path.exists(p) and os.path.normcase(os.path.abspath(bpy.path.abspath(img.filepath))) != os.path.normcase(p):
            img.filepath = p
            try:
                img.reload()
            except Exception:
                pass


def edit_asset(o):
    """Swap an instance of a Blender-made asset for its editable source objects (same Unity id)."""
    c = o.instance_collection
    key, src = c.get('aa_bb_key'), c.get('aa_source')
    if not src or not os.path.exists(src):
        raise RuntimeError(f'Source file for {key} not found: {src}')
    with bpy.data.libraries.load(src, link=False) as (s, d):
        d.collections = [n for n in s.collections if n == SOURCE_COLLECTION]
    if not d.collections:
        raise RuntimeError(f'{src} has no {SOURCE_COLLECTION} collection')
    relink_project_images()
    new = d.collections[0]
    objects = list(new.objects)
    target = o.users_collection[0] if o.users_collection else bpy.context.scene.collection
    for x in objects:
        target.objects.link(x)
    bpy.data.collections.remove(new)
    tops = [x for x in objects if x.parent is None or x.parent not in objects]
    if len(tops) == 1:
        root = tops[0]
    else:
        root = bpy.data.objects.new(o.name, None)
        target.objects.link(root)
        for x in tops:
            x.parent = root
    world, parent, kids = o.matrix_world.copy(), o.parent, list(o.children)
    child_world = [(k, k.matrix_world.copy()) for k in kids]
    root.parent = parent
    root.matrix_world = world
    for k, mw in child_world:
        k.parent = root
        k.matrix_world = mw
    ident = {k: o[k] for k in ('aa_id',) if k in o}
    name = o.name
    bpy.data.objects.remove(o)
    root.name = name
    for k, v in ident.items():
        root[k] = v
    root['aa_name'] = root.name
    root['aa_bb_key'] = key
    bpy.context.view_layer.update()
    root['aa_asset_hash'] = asset_hash(root)
    root['aa_base_world'], root['aa_base_local'] = geo_centers(root)
    root['aa_base_origin'] = list(root.matrix_world.translation)
    for x in bpy.context.view_layer.objects:
        x.select_set(False)
    root.select_set(True)
    bpy.context.view_layer.objects.active = root
    return root
