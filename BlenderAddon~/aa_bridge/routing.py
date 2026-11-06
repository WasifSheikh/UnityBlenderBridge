"""User-facing import intent, separate from scene names and immutable bridge keys.

Paths are previews only. Unity validates current project profiles and owns the final paths.
Optional protocol fields keep older version-2 sessions usable.
"""
import json
import re


def normalize_name(value, set_code=''):
    value = re.sub(r'\.\d{3}$', '', str(value or '').strip())
    value = re.sub(r'^(?:(?:SM|SK|PF|M|T)_)+', '', value, flags=re.IGNORECASE)
    words = re.findall(r'[A-Za-z0-9]+', value)
    stem = '_'.join(w.zfill(2) if w.isdigit() else w[0].upper() + w[1:] for w in words)
    code = '_'.join(re.findall(r'[A-Za-z0-9]+', str(set_code or ''))).upper()
    if code and stem and stem.casefold() != code.casefold() and not stem.casefold().startswith(code.casefold() + '_'):
        stem = code + '_' + stem
    return stem


def profiles(session):
    data = session or {}
    return [p for p in (data.get('profiles') or data.get('destinationProfiles') or []) if p.get('id')]


def asset_key(o):
    c = getattr(o, 'instance_collection', None)
    return o.get('aa_bb_key') or (c.get('aa_bb_key') if c is not None else '') or ''


def signature(destination, asset_name):
    return json.dumps([destination or '', asset_name or ''], separators=(',', ':'))


def intent(o, session, fallback_name=None):
    c = getattr(o, 'instance_collection', None)
    existing = asset_key(o)
    inherited = c if c is not None and c.get('aa_bb_key') else {}
    destination = str(o.get('aa_destination', inherited.get('aa_destination', '' if existing else (session or {}).get('defaultDestination', ''))))
    profile = next((p for p in profiles(session) if p['id'] == destination), {})
    raw_name = o.get('aa_asset_name', inherited.get('aa_asset_name', '')) or fallback_name or existing or o.name
    return destination, normalize_name(raw_name, profile.get('setCode', ''))


def intent_signature(o, session, fallback_name=None):
    return signature(*intent(o, session, fallback_name))


def dirty(o, session, fallback_name=None):
    current = intent_signature(o, session, fallback_name)
    # Older sources/sessions have no baseline. Only an explicit user choice makes them dirty.
    baseline = o.get('aa_intent_signature')
    return current != baseline if baseline is not None else ('aa_destination' in o or 'aa_asset_name' in o)


def seed(o, destination, asset_name):
    o['aa_destination'] = destination or ''
    o['aa_asset_name'] = asset_name or ''
    o['aa_intent_signature'] = signature(destination, asset_name)


def pending(o, session, fallback_name=None):
    destination, asset_name = intent(o, session, fallback_name)
    o['aa_destination'] = destination
    o['aa_asset_name'] = asset_name
    o['aa_pending_intent'] = signature(destination, asset_name)
    return {'destination': destination, 'assetName': asset_name}


def acknowledge(o, message, session):
    before = o.get('aa_pending_intent')
    if before is None:
        if ('destination' in message or 'assetName' in message) and not dirty(o, session):
            seed(o, message.get('destination', ''), message.get('assetName', ''))
        return
    if 'destination' in message or 'assetName' in message:
        destination = message.get('destination', '')
        asset_name = message.get('assetName', '')
        accepted = signature(destination, asset_name)
        # Keep edits made while Unity was applying the previous request.
        if intent_signature(o, session) == before:
            o['aa_destination'] = destination
            o['aa_asset_name'] = asset_name
    else:
        accepted = before
    o['aa_intent_signature'] = accepted
    del o['aa_pending_intent']


def preview(o, session, override=None):
    destination, name = override if override is not None else intent(o, session)
    profile = next((p for p in profiles(session) if p['id'] == destination), None)
    if profile is None:
        return ['Choose an import destination in Unity' if profiles(session) else 'Unity project asset root (legacy session)',
                'SM_' + name + '.fbx', 'PF_' + name + '.prefab']
    root = profile.get('assetRoot', '').rstrip('/').replace('{scene}', (session or {}).get('scene', ''))
    source = profile.get('sourceRoot', '').rstrip('/').replace('{scene}', (session or {}).get('scene', ''))
    return [root + '/Meshes/SM_' + name + '.fbx', root + '/Prefabs/PF_' + name + '.prefab', source + '/' + name + '.blend']
