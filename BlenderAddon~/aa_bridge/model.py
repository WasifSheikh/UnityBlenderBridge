"""What an object in a session file is to the bridge.

    Unity object      has aa_id (from Unity, or given by Unity after a sync)
    Unity instance    a Unity object shown as an instance of a library collection (prefab or mesh)
    edit root         a Unity object being modelled here: Make editable, or a Blender-made asset opened from its source
    new model         geometry without aa_id, the top of its hierarchy: becomes a new asset (prefab) in Unity
    placement         an instance of an ordinary Blender collection: the collection becomes a reusable prefab
    section group     an Empty with custom property map_group: only organises, its children are separate assets
"""
from .common import GEO


def is_instance(o):
    """An instance of a library collection (a Unity prefab or mesh)."""
    c = o.instance_collection if o.type == 'EMPTY' and o.instance_type == 'COLLECTION' else None
    return c is not None and bool(c.get('aa_key'))


def collection_placement(o):
    return o.type == 'EMPTY' and o.instance_type == 'COLLECTION' and o.instance_collection is not None


def has_geo(o):
    return o.type in GEO or any(d.type in GEO for d in o.children_recursive)


def is_edit_root(o):
    return 'aa_id' in o and bool(o.get('aa_bb_key') or o.get('aa_editable')) and not collection_placement(o)


def is_section_group(o):
    return o.type == 'EMPTY' and bool(o.get('map_group'))


def plain_new(o):
    return ('aa_id' not in o and not collection_placement(o) and not o.get('aa_locked')
            and o.type in GEO | {'EMPTY'} and not is_section_group(o))


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


def is_new_model(o):
    return plain_new(o) and (o.type in GEO or (o.instance_type == 'NONE' and has_geo(o))) and new_root(o)


def asset_parts(root):
    """root and its children, minus other Unity objects parented under it (they stay their own nodes)."""
    out, stack = [], [root]
    while stack:
        o = stack.pop()
        out.append(o)
        stack.extend(c for c in o.children if 'aa_id' not in c and not collection_placement(c))
    return out


def describe(o):
    """(kind, label) for the panel."""
    if o.get('aa_locked'):
        return 'locked', 'Locked context object (outside the editable roots)'
    if is_edit_root(o):
        mode = o.get('aa_edit_mode')
        return 'edit', {'source': 'Being modelled: changes the prefab itself (all copies)',
                        'variant': 'Being modelled: becomes a variant for this copy only'}.get(mode, 'Being modelled: geometry goes to Unity on sync')
    if 'aa_id' in o and is_instance(o):
        return 'unity', 'Unity object (Make editable to model it)'
    if 'aa_id' in o:
        return 'unity', 'Unity object'
    if is_instance(o):
        return 'add', 'New instance from the library (added on sync)'
    if collection_placement(o):
        return 'collection', 'Collection instance: the collection becomes a reusable prefab on sync'
    if is_section_group(o):
        return 'group', 'Section group: its children become separate assets'
    if o.type in GEO or (o.type == 'EMPTY' and has_geo(o)):
        return 'new', 'New model: becomes a prefab on sync' if is_new_model(o) else 'Part of a new model'
    return 'other', 'Not sent to Unity (only geometry, Unity objects and library instances are)'
