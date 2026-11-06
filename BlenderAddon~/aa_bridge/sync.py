"""Sync to Unity: writes <session>/out/result.json for Unity to apply (Editor/BBPlan.cs).

    nodes   every editable Unity object still here: transform (world), parent, name
    dups    Shift+D / Alt+D copies of a Unity object (same aa_id): Unity duplicates the original
    added   instances dragged in from the library, new models, and placements of Blender collections
    assets  new or edited models: FBX + textures + material values + a source .blend each

Anything with geometry that is not from Unity is a new model. Its top object is the asset (its children come with
it); its origin becomes the prefab's pivot. Custom property "collider" on it: mesh (default), box or none. A Blender
collection placed as an instance becomes one prefab for all its placements, and is re-exported when the collection
changes. After Unity applies, applied.json tells this file the Unity ids of what it created (stamp_applied, polled),
and status.json says whether Unity still matches this file.
"""
import datetime
import os
import re
import shutil
import uuid

import bpy

from . import export as X
from . import materials as M
from . import session as sess
from .common import FORMAT_VERSION, flat, read_json, scene_json, set_scene_json, slug, write_json
from .model import collection_placement, has_geo, is_edit_root, is_instance, is_new_model

COLLECTION_KEYS = 'aa_collection_keys'        # collection ident -> {"key": Unity asset key, "sig": signature last sent}
COLLECTION_PENDING = 'aa_collection_pending'  # asset key -> {"ident", "sig"} sent, waiting for Unity to apply


def collection_ident(o):
    c = o.instance_collection
    lib = c.library.filepath if c.library else ''
    return f"{lib}|{c.name}|{o.get('collider', 'mesh')}"


def read_status(S=None):
    S = S or sess.load()
    p = (S or {}).get('statusPath', '')
    try:
        return read_json(p) if p and os.path.exists(p) else {}
    except (OSError, ValueError):
        return {}


def _pick_original(lst):
    """Of several objects sharing a Unity id (Shift+D), the original: the one still named as built; else one that is
    not an auto-suffixed copy of that name (the original may have been renamed); else the first by name."""
    if len(lst) == 1:
        return lst[0]
    exact = [o for o in lst if o.name == o.get('aa_name')]
    if exact:
        return exact[0]
    base = lst[0].get('aa_name', '')
    renamed = [o for o in lst if not re.fullmatch(re.escape(base) + r'\.\d{3}', o.name)]
    return sorted(renamed or lst, key=lambda x: x.name)[0]


def sync_to_unity():
    S = sess.load()
    scene = bpy.context.scene
    if S is None:
        raise RuntimeError('This file is not a Unity Blender Bridge session (open it from Unity).')
    if scene.get('aa_session') != S['sessionId']:
        raise RuntimeError('Unity has started a newer session for this scene. Open the scene from Unity again.')
    st = read_status(S)
    if st.get('state') == 'undone' and st.get('sessionId') == S['sessionId']:
        raise RuntimeError('The last sync was undone in Unity, so this file no longer matches the scene. In Unity: Open in Blender > Start fresh.')
    stamp_applied()
    if bpy.context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()  # every matrix_world current before anything is measured
    out = S['outDir']
    if os.path.exists(os.path.join(out, 'result.json')):
        raise RuntimeError('Apply or discard the pending sync in Unity before sending another.')
    os.makedirs(out, exist_ok=True)
    shutil.rmtree(os.path.join(out, 'assets'), ignore_errors=True)

    objs = [o for o in scene.objects if not o.get('aa_locked')]
    seen = set()
    for o in objs:   # tmp ids must be unique (a copy of an unsynced object copies its tmp id)
        for k in ('aa_pending_hash', 'aa_pending_sig'):
            if k in o:
                del o[k]
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
    original = {i: _pick_original(lst) for i, lst in groups.items()}

    ref, dups, insts, roots, placements, edits = {}, [], [], [], [], []
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
        if collection_placement(o) and not is_instance(o):
            placements.append(o)   # with or without a Unity id: the collection may have changed
        if 'aa_id' in o:
            continue
        if is_instance(o):
            ref[o] = 'tmp:' + tmp(o)
            insts.append(o)
        elif is_new_model(o):
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

    project = S.get('project', '')
    fingerprint = M.Fingerprint(project)
    ckeys = scene_json(scene, COLLECTION_KEYS)
    used = {o['aa_bb_key'].casefold() for o in scene.objects if o.get('aa_bb_key')} | {v['key'].casefold() for v in ckeys.values()}
    assets, by_signature, new_placements, pending = [], {}, [], {}

    def reserve(name):
        base = slug(name)
        key, n = base, 2
        while key.casefold() in used:
            key, n = f'{base}_{n}', n + 1
        used.add(key.casefold())
        return key

    def new_model(root, name, signature):
        key = by_signature.get(signature) if signature is not None else None
        if key is None:
            key = reserve(name)
            assets.append(X.export_asset(root, key, out, True, project))
            if signature is not None:
                by_signature[signature] = key
        else:
            root['aa_pending_hash'] = X.asset_hash(root)
        return key

    # ---- new models
    for o in sorted(roots, key=lambda obj: obj.name):
        new_placements.append((o, new_model(o, o.name, X.prefab_signature(o, fingerprint))))

    # ---- Blender collections: one prefab per collection; re-exported when the collection changed
    by_ident = {}
    for o in placements:
        by_ident.setdefault(collection_ident(o), []).append(o)
    for ident, plist in sorted(by_ident.items()):
        known = ckeys.get(ident)
        fresh = [o for o in plist if 'aa_id' not in o]
        if known is None and not fresh:
            continue
        with X.collection_model(plist[0]) as root:
            if not has_geo(root):
                continue
            sig = X.prefab_signature(root, fingerprint)
            if known is not None:
                key = known['key']
                if sig != known.get('sig'):
                    assets.append(X.export_asset(root, key, out, False, project))
                    pending[key] = {'ident': ident, 'sig': sig}
            else:
                key = new_model(root, plist[0].instance_collection.name, sig)
        for o in fresh:
            ref[o] = 'tmp:' + tmp(o)
            new_placements.append((o, key))
            if known is None:
                o['aa_coll_ident'] = ident
                o['aa_pending_sig'] = sig

    # ---- Unity objects being modelled
    edited_keys = {}
    for o in edits:
        if X.asset_hash(o) == o.get('aa_asset_hash'):
            continue
        key = o.get('aa_bb_key')
        if not key:  # first edit of a Unity object's geometry: it becomes a new mesh asset
            key = reserve(o.name)
        if key in edited_keys:
            if X.prefab_signature(o, fingerprint) != X.prefab_signature(edited_keys[key], fingerprint):
                raise RuntimeError(f'Several placements of {key} have different mesh edits. Edit one placement per asset before syncing.')
            o['aa_pending_hash'] = X.asset_hash(o)
            continue
        edited_keys[key] = o
        assets.append(X.export_asset(o, key, out, not o.get('aa_bb_key'), project, o['aa_id']))

    nodes = [entry(o, id=i, name=o.name if o.name != o.get('aa_name') else '') for i, o in original.items()]
    dup_out = [entry(o, tmp=o['aa_tmp'], of=i) for o, i in dups]
    added = [entry(o, tmp=o['aa_tmp'], asset=o.instance_collection['aa_key']) for o in insts]
    added.extend(entry(o, tmp=o['aa_tmp'], asset='blender:' + key) for o, key in new_placements)
    set_scene_json(scene, COLLECTION_PENDING, pending)

    if bpy.data.filepath:
        bpy.ops.wm.save_mainfile()
    write_json(os.path.join(out, 'result.json'), {
        'version': FORMAT_VERSION, 'sessionId': S['sessionId'], 'written': datetime.datetime.now().isoformat(timespec='seconds'),
        'blend': bpy.data.filepath, 'nodes': nodes, 'dups': dup_out, 'added': added, 'assets': assets})
    bits = [f'{len(nodes)} objects', f'{len(dup_out)} copies', f'{len(insts)} from the library',
            f'{len(new_placements)} new placements', f'{len(assets)} models exported']
    msg = 'Sent to Unity: ' + ', '.join(bits) + '. Waiting for Unity to apply...'
    scene['aa_status'] = msg
    scene['aa_waiting'] = 1
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
    ckeys = scene_json(scene, COLLECTION_KEYS)
    pending = scene_json(scene, COLLECTION_PENDING)

    # ids Unity changed (e.g. an instance switched to its own prefab variant)
    remap = {r['from']: r['to'] for r in A.get('remap', []) if r.get('from') and r.get('to')}
    if remap:
        for o in scene.objects:
            if o.get('aa_id') in remap:
                o['aa_id'] = remap[o['aa_id']]

    def settle(o, m):
        o['aa_asset_hash'] = o.get('aa_pending_hash') or m.get('hash', '')
        if 'aa_pending_hash' in o:
            del o['aa_pending_hash']
        X.stamp_base(o)

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
                if 'aa_coll_ident' in o:   # first placement of a Blender collection: remember its asset
                    ckeys[o['aa_coll_ident']] = {'key': m['asset'], 'sig': o.get('aa_pending_sig', '')}
                    for k in ('aa_coll_ident', 'aa_pending_sig'):
                        if k in o:
                            del o[k]
            n += 1
        elif m.get('id') and m.get('asset'):
            for o in scene.objects:
                if o.get('aa_id') == m['id'] and is_edit_root(o):
                    o['aa_bb_key'] = m['asset']
                    settle(o, m)
                    if 'aa_editable' in o:
                        del o['aa_editable']
        elif m.get('asset'):
            for o in scene.objects:
                if o.get('aa_bb_key') == m['asset'] and is_edit_root(o) and 'aa_pending_hash' in o:
                    settle(o, m)
            done = pending.pop(m['asset'], None)
            if done and done.get('ident') in ckeys:
                ckeys[done['ident']]['sig'] = done.get('sig', '')
    set_scene_json(scene, COLLECTION_KEYS, ckeys)
    set_scene_json(scene, COLLECTION_PENDING, pending)
    scene['aa_status'] = f"Unity applied {A.get('applied', '').replace('T', ' ')}: {A.get('summary', '')}"
    scene['aa_waiting'] = 0
    try:
        if bpy.data.filepath:
            bpy.ops.wm.save_mainfile()
    except Exception:
        pass
    return n or 1


def poll_status():
    """status.json changed: Unity applied, discarded or undid something. Returns True when the panel should redraw."""
    scene = bpy.context.scene
    S = sess.load()
    p = (S or {}).get('statusPath', '')
    if not p or not os.path.exists(p):
        return False
    mt = os.path.getmtime(p)
    if scene.get('aa_status_mtime') == mt:
        return False
    scene['aa_status_mtime'] = mt
    st = read_status(S)
    if st.get('sessionId') != S['sessionId']:
        return False
    scene['aa_unity_state'] = st.get('state', 'ok')
    if st.get('state') == 'undone':
        scene['aa_status'] = st.get('message', 'The last sync was undone in Unity.')
    return True


def discarded():
    """Unity discarded the last sync (result.json gone without an applied.json for it): stop waiting.
    Runs after stamp_applied, which already cleared the flag if Unity applied it."""
    scene = bpy.context.scene
    S = sess.load()
    if not scene.get('aa_waiting') or S is None:
        return False
    if os.path.exists(os.path.join(S.get('outDir', ''), 'result.json')):
        return False
    scene['aa_waiting'] = 0
    scene['aa_status'] = 'Unity discarded the last sync. Your changes are still here; sync again to send them.'
    return True
