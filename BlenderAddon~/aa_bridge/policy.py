"""Material ownership/texture transfer is independent of asset routing and scene placement."""
import json


def values(o, session):
    c = getattr(o, 'instance_collection', None)
    inherited = c if c is not None and c.get('aa_bb_key') else {}
    existing = bool(o.get('aa_bb_key') or inherited)
    mode = str(o.get('aa_material_mode', inherited.get('aa_material_mode', 'blender' if existing else (session or {}).get('defaultMaterialMode', 'blender'))))
    textures = bool(o.get('aa_sync_textures', inherited.get('aa_sync_textures', True if existing else (session or {}).get('defaultSyncTextures', True))))
    return ('unity' if mode == 'unity' else 'blender'), textures


def signature(mode, textures):
    return json.dumps([mode, bool(textures)], separators=(',', ':'))


def dirty(o, session):
    current = signature(*values(o, session))
    baseline = o.get('aa_material_signature')
    return current != baseline if baseline is not None else ('aa_material_mode' in o or 'aa_sync_textures' in o)


def seed(o, mode, textures):
    o['aa_material_mode'], o['aa_sync_textures'] = mode, bool(textures)
    o['aa_material_signature'] = signature(mode, textures)


def pending(o, session):
    mode, textures = values(o, session)
    o['aa_material_mode'], o['aa_sync_textures'] = mode, textures
    o['aa_pending_material_signature'] = signature(mode, textures)
    return {'materialMode': mode, 'syncTextures': textures}


def acknowledge(o, message, session):
    before = o.get('aa_pending_material_signature')
    # Old Unity responses omit both fields: acknowledge exactly what was sent.
    explicit = 'materialMode' in message and bool(message['materialMode'])
    accepted = signature(message['materialMode'], message.get('syncTextures', True)) if explicit else before
    if before is not None:
        if explicit and signature(*values(o, session)) == before:
            o['aa_material_mode'], o['aa_sync_textures'] = message['materialMode'], bool(message.get('syncTextures', True))
        o['aa_material_signature'] = accepted
        del o['aa_pending_material_signature']
    elif explicit and not dirty(o, session):
        seed(o, message['materialMode'], message.get('syncTextures', True))
