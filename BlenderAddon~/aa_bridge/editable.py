"""Make editable: a Unity object (a linked instance of a library collection) becomes local geometry you can model.

A Blender-made asset comes back from its source .blend (modifiers intact). Any other Unity mesh is copied from the
library, optionally cleaned up for modelling: Unity stores triangles split at every UV seam and hard edge, which is
awkward to edit (loop cuts stop at seams), so the copy is welded, hard edges become sharp edges, UV islands become
seams, and triangles are joined into quads where that keeps UVs, colours and sharp edges intact.
"""
import json
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Matrix

from . import export as X
from . import materials as M
from .common import GEO, SOURCE_COLLECTION
from .session import load as load_session


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


def _clear_custom_normals(me):
    if not getattr(me, 'has_custom_normals', False):
        return
    a = me.attributes.get('custom_normal')   # Blender 5: custom normals are an attribute
    if a is not None:
        try:
            me.attributes.remove(a)
        except Exception:
            pass
    if getattr(me, 'has_custom_normals', False):
        try:
            me.normals_split_custom_set([(0.0, 0.0, 0.0)] * len(me.loops))   # zero = automatic normal
        except Exception:
            pass


def clean_for_modelling(me, weld=1e-5, hard_angle=1.0):
    """Weld Unity's split vertices; hard edges -> sharp edges, UV breaks -> seams; triangles -> quads."""
    if not len(me.polygons):
        return
    cn = np.empty(len(me.loops) * 3, dtype=np.float32)
    me.corner_normals.foreach_get('vector', cn)
    cn = cn.reshape(-1, 3)
    starts = np.empty(len(me.polygons), dtype=np.int32)
    me.polygons.foreach_get('loop_start', starts)

    bm = bmesh.new()
    bm.from_mesh(me)
    src = bm.loops.layers.int.new('aa_loop')
    for fi, f in enumerate(bm.faces):   # from_mesh keeps polygon order and each polygon's loop order
        base = int(starts[fi])
        for k, loop in enumerate(f.loops):
            loop[src] = base + k
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=weld)

    cos_t = math.cos(math.radians(hard_angle))
    uv = bm.loops.layers.uv.active
    for e in bm.edges:
        lf = e.link_faces
        if len(lf) != 2:
            continue
        sharp = seam = False
        for v in e.verts:
            a = next((x for x in lf[0].loops if x.vert == v), None)
            b = next((x for x in lf[1].loops if x.vert == v), None)
            if a is None or b is None:
                continue
            if float(np.dot(cn[a[src]], cn[b[src]])) < cos_t:
                sharp = True
            if uv is not None and (a[uv].uv - b[uv].uv).length > 1e-5:
                seam = True
        if sharp:
            e.smooth = False
        if seam:
            e.seam = True
    for f in bm.faces:
        f.smooth = True
    try:
        bmesh.ops.join_triangles(bm, faces=list(bm.faces), cmp_seam=True, cmp_sharp=True, cmp_uvs=True, cmp_vcols=True,
                                 cmp_materials=True, angle_face_threshold=math.radians(40), angle_shape_threshold=math.radians(40))
    except TypeError:   # older/newer argument sets
        bmesh.ops.join_triangles(bm, faces=list(bm.faces), angle_face_threshold=math.radians(40), angle_shape_threshold=math.radians(40))
    bm.loops.layers.int.remove(src)
    bm.to_mesh(me)
    bm.free()
    _clear_custom_normals(me)
    me.update()


def make_editable(o, mode='variant', cleanup=True):
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
                me.materials[i] = M.local_copy(slot.material, cache)
        if cleanup and p.type == 'MESH':
            clean_for_modelling(me)
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
        extra.update(aa_prefab=c['aa_key'], aa_edit_mode=mode, aa_orig_parts=json.dumps(sorted({p.get('aa_part', '') for p in parts})))
    _swap_in(o, root, extra)
    bpy.context.view_layer.update()
    X.stamp_base(root)
    root['aa_asset_hash'] = X.asset_hash(root)  # unchanged geometry is not sent
    return root


def relink_project_images():
    """Source .blend files store Unity textures by absolute path, which differs between PCs. Images tagged with their
    'Assets/...' path are pointed at this machine's project, following Unity's explicit migration aliases."""
    session = load_session() or {}
    project = session.get('project', '')
    if not project:
        return
    aliases = {}
    for alias in session.get('textureAliases', []):
        old, new = M.project_asset_path(alias.get('from')), M.project_asset_path(alias.get('to'))
        if old and new:
            aliases[old] = new if old not in aliases or aliases[old] == new else ''
    for img in bpy.data.images:
        rel = M.project_asset_path(img.get('aa_project_path'))
        if not rel or img.packed_file is not None:
            continue
        original, visited = rel, set()
        while rel in aliases and rel not in visited and aliases[rel]:
            visited.add(rel)
            rel = aliases[rel]
        if rel in visited:  # Do not guess through an invalid/cyclic alias chain.
            rel = original
        p = os.path.normpath(os.path.join(project, rel))
        if os.path.exists(p):
            img['aa_project_path'] = rel
            if os.path.normcase(os.path.abspath(bpy.path.abspath(img.filepath))) != os.path.normcase(p):
                img.filepath = p
                try:
                    img.reload()
                except Exception:
                    pass


def edit_asset(o):
    """Swap an instance of a Blender-made asset for its editable source objects (same Unity id)."""
    from . import routing, policy
    c = o.instance_collection
    key, src = c.get('aa_bb_key'), c.get('aa_source')
    destination, asset_name = routing.intent(o, {})
    baseline = o.get('aa_intent_signature', routing.signature(c.get('aa_destination', ''),
                            c.get('aa_asset_name', '') or routing.normalize_name(key)))
    material_mode, sync_textures = policy.values(o, {})
    material_baseline = o.get('aa_material_signature', policy.signature(c.get('aa_material_mode', 'blender'), c.get('aa_sync_textures', True)))
    if not src or not os.path.exists(src):
        raise RuntimeError(f'Source file for {key} not found: {src}')
    with bpy.data.libraries.load(src, link=False) as (s, d):
        d.collections = [n for n in s.collections if n == SOURCE_COLLECTION]
    if not d.collections or d.collections[0] is None:
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
    for k in ('aa_tmp', 'aa_pending_hash', 'aa_pending_intent', 'aa_pending_material_signature'):
        if k in root:
            del root[k]
    root['aa_name'] = root.name
    root['aa_bb_key'] = key
    # Registry/library preferences take precedence over stale custom properties in an older .blend source.
    routing.seed(root, destination, asset_name)
    root['aa_intent_signature'] = baseline
    policy.seed(root, material_mode, sync_textures)
    root['aa_material_signature'] = material_baseline
    bpy.context.view_layer.update()
    root['aa_asset_hash'] = X.asset_hash(root)
    X.stamp_base(root)
    for x in bpy.context.view_layer.objects:
        x.select_set(False)
    root.select_set(True)
    bpy.context.view_layer.objects.active = root
    return root
