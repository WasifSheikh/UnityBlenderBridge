"""One asset -> what Unity imports: an FBX (geometry in Unity axes), material values + textures, and a source .blend
(the editable original for later sessions). Plus the hashes that decide whether an asset changed at all."""
import hashlib
import json
import os
import shutil
from contextlib import contextmanager

import bpy
import numpy as np
from mathutils import Matrix, Vector

from . import materials as M
from .common import GEO, SOURCE_COLLECTION, flat
from .model import asset_parts, collection_placement


# ---------------------------------------------------------------- change detection
def fbx_name(o):
    """The name an object is exported under. Fixed the first time it is exported, so a renamed object (or a ".001"
    a re-opened source gets) does not change Unity's mesh ids, which are derived from these names."""
    return o.get('aa_fbx_name') or o.name


def asset_hash(root):
    """Changes when anything Unity would get changes: geometry, UVs, layout of the parts, materials."""
    h = hashlib.md5()
    dg = bpy.context.evaluated_depsgraph_get()
    inv = root.matrix_world.inverted_safe()
    for o in sorted(asset_parts(root), key=fbx_name):
        h.update(fbx_name(o).encode())
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
                for layer in me.uv_layers:
                    uv = np.empty(len(me.loops) * 2, dtype=np.float32)
                    layer.data.foreach_get('uv', uv)
                    h.update(uv.round(5).tobytes())
            except Exception:
                pass
            finally:
                ev.to_mesh_clear()
            for s in o.material_slots:
                m = s.material
                if m is None:
                    continue
                h.update(m.name.encode())
                nt = M.node_tree(m)
                for n in (nt.nodes if nt is not None else []):
                    h.update(n.bl_idname.encode())
                    if getattr(n, 'image', None):
                        h.update(n.image.name.encode())
                    for i in n.inputs:
                        if not i.is_linked and hasattr(i, 'default_value'):
                            try:
                                h.update(np.array(i.default_value, dtype=np.float32).tobytes())
                            except Exception:
                                pass
    h.update(str(root.get('collider', 'mesh')).encode())
    return h.hexdigest()[:16]


def _depth(o, within):
    d, p = 0, o.parent
    while p is not None:
        if p in within:
            d += 1
        p = p.parent
    return d


def prefab_signature(root, fingerprint):
    """Fingerprint of exported mesh data and local layout, independent of placement and object names: two models
    with the same signature are the same prefab."""
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
            h.update(json.dumps([fingerprint.material(s.material) for s in ev.material_slots], sort_keys=True).encode())
            signatures.append((_depth(o, parts), h.hexdigest()))
        finally:
            ev.to_mesh_clear()
    if not signatures:
        return None
    return hashlib.sha256(json.dumps([str(root.get('collider', 'mesh')), sorted(signatures)]).encode()).hexdigest()[:24]


def geo_centers(root):
    """Centre of the asset's geometry in world space and in the root's own space (pivot-relative)."""
    pts = [o.matrix_world @ Vector(c) for o in asset_parts(root) if o.type in GEO for c in o.bound_box]
    if not pts:
        return [0, 0, 0], [0, 0, 0]
    lo = Vector([min(p[i] for p in pts) for i in range(3)])
    hi = Vector([max(p[i] for p in pts) for i in range(3)])
    w = (lo + hi) / 2
    return list(w), list(root.matrix_world.inverted_safe() @ w)


def stamp_base(root):
    """Remember where geometry and origin are now, to tell a moved pivot from a moved object later."""
    root['aa_base_world'], root['aa_base_local'] = geo_centers(root)
    root['aa_base_origin'] = list(root.matrix_world.translation)


def pivot_moved(root):
    """True when the origin moved differently from the geometry since the last sync: a moved pivot, as opposed to
    moving the whole object (both move together) or editing geometry (origin stays)."""
    if 'aa_base_world' not in root or 'aa_base_origin' not in root:
        return False
    w, _ = geo_centers(root)
    od = root.matrix_world.translation - Vector(root['aa_base_origin'])
    gd = Vector(w) - Vector(root['aa_base_world'])
    return od.length > 0.01 and (gd - od).length > 0.01


# ---------------------------------------------------------------- staging
@contextmanager
def collection_model(placement):
    """Stage a Blender collection as a model in prefab-local space without changing the placement. The staged copies
    are exported under their source objects' names, so Unity's mesh ids stay the same from sync to sync."""
    scene_collection = bpy.context.scene.collection
    root = bpy.data.objects.new(placement.instance_collection.name, None)
    created = [root]
    try:
        scene_collection.objects.link(root)
        root['collider'] = str(placement.get('collider', 'mesh'))
        root['aa_fbx_name'] = placement.instance_collection.name

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
                    obj['aa_fbx_name'] = source.get('aa_fbx_name') or source.name
                    if source.get('aa_part') is not None:
                        obj['aa_part'] = source['aa_part']
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


@contextmanager
def _exportable(parts):
    """Every part visible, selectable, in the view layer and named as it is exported; all restored afterwards."""
    vl = bpy.context.view_layer
    scene_collection = bpy.context.scene.collection
    state = [(o, o.hide_get(), o.hide_select, o.hide_viewport, o.select_get()) for o in vl.objects]
    active = vl.objects.active
    linked, renamed = [], []
    try:
        for o in parts:
            if o.name not in vl.objects:   # e.g. in an excluded collection
                scene_collection.objects.link(o)
                linked.append(o)
        vl.update()
        for o in parts:
            want = fbx_name(o)
            o['aa_fbx_name'] = want
            if o.name != want:
                other = bpy.data.objects.get((want, None))
                if other is not None and other is not o:
                    renamed.append((other, other.name))
                    other.name = want + '__aa_tmp'
                renamed.append((o, o.name))
                o.name = want
        for o in vl.objects:
            o.select_set(False)
        for o in parts:
            o.hide_viewport = False
            o.hide_select = False
            o.hide_set(False)
            o.select_set(True)
        yield
    finally:
        for o, name in reversed(renamed):
            o.name = name
        for o in linked:
            scene_collection.objects.unlink(o)
        for o, hid, hs, hv, sel in state:
            try:
                o.hide_viewport = hv
                o.hide_select = hs
                o.hide_set(hid)
                o.select_set(sel)
            except Exception:
                pass
        vl.objects.active = active


# ---------------------------------------------------------------- export of one model
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
    material_mode = str(root.get('aa_material_mode', 'blender'))
    sync_textures = bool(root.get('aa_sync_textures', True))
    images = M.Images(os.path.join(d, 'Textures'), project) if material_mode != 'unity' and sync_textures else None
    bpy.context.view_layer.update()
    to_root = root.matrix_world.inverted_safe()
    rel = {o: flat(to_root @ o.matrix_world) for o in parts}  # before the root is moved to the origin for export
    materials = [M.reference(m) if material_mode == 'unity' else M.describe(m, images, sync_textures) for m in mats]
    h = asset_hash(root)
    geo = [o for o in parts if o.type in GEO]
    has_uv2 = any(o.type == 'MESH' and len(o.data.uv_layers) > 1 for o in geo)

    saved, parent = root.matrix_world.copy(), root.parent
    coll = None
    with _exportable(parts):
        try:
            vl = bpy.context.view_layer
            vl.objects.active = root
            root.parent = None                     # the source .blend must not drag the Unity parent along
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
                nt = M.node_tree(m)
                for img in {n.image for n in (nt.nodes if nt is not None else []) if getattr(n, 'image', None)}:
                    project_path = M.project_asset(img, project)
                    if project_path:
                        img['aa_project_path'] = project_path  # Unity texture: kept as a file, found again by project path on any PC
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
            root.parent = parent
            root.matrix_world = saved
    root['aa_pending_hash'] = h
    known = {o.get('aa_part') for o in geo if 'aa_part' in o}
    original = json.loads(root.get('aa_orig_parts', '[]'))
    return {'key': key, 'name': root.name, 'fbx': key + '.fbx', 'source': key + '.blend', 'sourceCollection': SOURCE_COLLECTION,
            'mode': str(root.get('aa_edit_mode', '')), 'prefab': str(root.get('aa_prefab', '')), 'pivotMoved': pivot_moved(root),
            'parts': [{'object': fbx_name(o), 'path': str(o.get('aa_part', '')), 'known': 'aa_part' in o, 'm': rel[o],
                       'depth': _depth(o, parts)} for o in geo],
            'removed': [p for p in original if p not in known],
            'collider': str(root.get('collider', 'mesh')), 'hash': h, 'dir': d.replace('\\', '/'), 'isNew': is_new, 'node': node,
            'hasUv2': has_uv2, 'materials': materials, 'materialMode': material_mode, 'syncTextures': sync_textures}
