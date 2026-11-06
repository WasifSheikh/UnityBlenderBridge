"""3D View > Sidebar (N) > Unity."""
import bpy

from . import model, sync
from .common import CONTEXT


def _session(context):
    return bool(context.scene.get('aa_session_json'))


# ---------------------------------------------------------------- operators
class AABRIDGE_OT_sync(bpy.types.Operator):
    bl_idname = 'aa_bridge.sync'
    bl_label = 'Sync to Unity'
    bl_description = 'Send everything changed in this file to the Unity scene (moves, deletes, copies, new and edited models)'

    @classmethod
    def poll(cls, context):
        return _session(context)

    def execute(self, context):
        try:
            msg = sync.sync_to_unity()
        except Exception as e:
            import traceback
            traceback.print_exc()
            context.scene['aa_status'] = 'Not sent: ' + str(e)
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        self.report({'INFO'}, msg)
        return {'FINISHED'}


class AABRIDGE_OT_make_editable(bpy.types.Operator):
    bl_idname = 'aa_bridge.make_editable'
    bl_label = 'Make editable'
    bl_description = ('Turn the selected Unity objects into real geometry you can model (Edit Mode, extrude, loop cuts...). '
                      'On sync, Unity swaps the new geometry into the same objects and keeps their materials and components')
    bl_options = {'REGISTER', 'UNDO'}

    mode: bpy.props.EnumProperty(
        name='Prefabs',
        items=[('variant', 'Only this one (new variant)',
                'Unity makes a Prefab Variant of the original with the new geometry and only this instance uses it. '
                'It keeps every component of the original prefab'),
               ('source', 'The prefab itself (all copies)',
                'The new geometry goes into the original prefab: every copy in every scene changes')],
        default='variant')
    cleanup: bpy.props.BoolProperty(
        name='Clean up for modelling', default=True,
        description='Weld the vertices Unity splits at UV seams and hard edges, turn hard edges into sharp edges and UV '
                    'breaks into seams, and join triangles into quads. Off: keep the exact Unity mesh (triangles, custom normals)')

    @staticmethod
    def targets(context):
        return [o for o in context.selected_objects if model.is_instance(o) and 'aa_id' in o and not o.get('aa_locked')]

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and bool(cls.targets(context))

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=440, title='Make editable')

    def draw(self, context):
        lay = self.layout
        objs = self.targets(context)
        prefabs = [o for o in objs if o.instance_collection.get('aa_kind') == 'prefab']
        blender = [o for o in objs if o.instance_collection.get('aa_bb_key')]
        if blender:
            lay.label(text=f'{len(blender)} Blender-made: opened from their source file (modifiers intact).', icon='FILE_BLEND')
        if prefabs:
            lay.label(text='Prefab instances: in Unity, the new geometry should become')
            lay.prop(self, 'mode', expand=True)
            if self.mode == 'source':
                lay.label(text='Every copy of the prefab, in every scene, will change.', icon='ERROR')
            else:
                lay.label(text='Other copies stay as they are.', icon='INFO')
            if any(o.instance_collection.get('aa_has_lods') for o in prefabs):
                lay.label(text='Has LODs: Unity will show the new geometry at every distance.', icon='INFO')
        if len(blender) < len(objs):
            lay.prop(self, 'cleanup')

    def execute(self, context):
        from . import editable
        done = []
        for o in self.targets(context):
            try:
                done.append(editable.make_editable(o, self.mode, self.cleanup))
            except Exception as e:
                self.report({'WARNING'}, f'{o.name}: {e}')
        for x in context.view_layer.objects:
            x.select_set(False)
        for r in done:
            r.select_set(True)
        if done:
            context.view_layer.objects.active = done[-1]
        self.report({'INFO'}, f'{len(done)} object(s) ready to model. Tab into Edit Mode.')
        return {'FINISHED'}


def _new_roots(context):
    return [o for o in context.selected_objects if model.is_new_model(o) or model.is_edit_root(o) or model.collection_placement(o)]


class AABRIDGE_OT_set_collider(bpy.types.Operator):
    bl_idname = 'aa_bridge.set_collider'
    bl_label = 'Collider'
    bl_description = 'Collider the Unity prefab gets (custom property "collider" on the selected new models)'
    bl_options = {'REGISTER', 'UNDO'}

    kind: bpy.props.EnumProperty(items=[('mesh', 'Mesh', 'Mesh collider from the geometry'), ('box', 'Box', 'One box around the model'),
                                        ('none', 'None', 'No collider')])

    @classmethod
    def poll(cls, context):
        return bool(_new_roots(context))

    def execute(self, context):
        for o in _new_roots(context):
            o['collider'] = self.kind
        return {'FINISHED'}


class AABRIDGE_OT_section_group(bpy.types.Operator):
    bl_idname = 'aa_bridge.section_group'
    bl_label = 'Section group'
    bl_description = 'An Empty that only organises: its children are sent as separate assets (custom property map_group)'
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        o = context.active_object
        return o is not None and o.type == 'EMPTY' and 'aa_id' not in o and not model.collection_placement(o)

    def execute(self, context):
        o = context.active_object
        o['map_group'] = not bool(o.get('map_group'))
        return {'FINISHED'}


class AABRIDGE_OT_toggle_context(bpy.types.Operator):
    bl_idname = 'aa_bridge.toggle_context'
    bl_label = 'Show locked context'
    bl_description = 'Show or hide the locked Unity objects outside the editable roots'

    @classmethod
    def poll(cls, context):
        return _session(context) and _context_layer(context) is not None

    def execute(self, context):
        lc = _context_layer(context)
        lc.hide_viewport = not lc.hide_viewport
        return {'FINISHED'}


def _context_layer(context):
    def find(lc):
        if lc.collection.name == CONTEXT:
            return lc
        for ch in lc.children:
            r = find(ch)
            if r is not None:
                return r
        return None
    return find(context.view_layer.layer_collection)


# ---------------------------------------------------------------- panels
def _wrap(text, n=36):
    words, line, out = text.split(), '', []
    for w in words:
        if len(line) + len(w) + 1 > n and line:
            out.append(line)
            line = w
        else:
            line = (line + ' ' + w).strip()
    if line:
        out.append(line)
    return out


def _lines(layout, text, icon='NONE'):
    col = layout.column(align=True)
    for i, line in enumerate(_wrap(text)):
        col.label(text=line, icon=icon if i == 0 else 'BLANK1' if icon != 'NONE' else 'NONE')


class _Panel:
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Unity'


class AABRIDGE_PT_panel(_Panel, bpy.types.Panel):
    bl_label = 'Unity Bridge'

    def draw(self, context):
        lay = self.layout
        sc = context.scene
        if not _session(context):
            _lines(lay, 'Not a Unity session. Open a scene from Unity: Tools > Blender Bridge > Open in Blender.', 'INFO')
            return
        row = lay.row()
        row.label(text=sc.name, icon='SCENE_DATA')
        lc = _context_layer(context)
        if lc is not None:
            row.operator('aa_bridge.toggle_context', text='', icon='HIDE_ON' if lc.hide_viewport else 'HIDE_OFF', emboss=False)
        undone = sc.get('aa_unity_state') == 'undone'
        row = lay.row()
        row.scale_y = 1.8
        row.enabled = not undone
        row.operator('aa_bridge.sync', icon='EXPORT')
        status = sc.get('aa_status', '')
        if status:
            box = lay.box()
            icon = 'ERROR' if undone or status.startswith('Not sent') else 'TIME' if sc.get('aa_waiting') else 'CHECKMARK' if status.startswith('Unity applied') else 'INFO'
            _lines(box, status, icon)


class AABRIDGE_PT_selection(_Panel, bpy.types.Panel):
    bl_label = 'Selection'
    bl_parent_id = 'AABRIDGE_PT_panel'

    @classmethod
    def poll(cls, context):
        return _session(context)

    def draw(self, context):
        lay = self.layout
        o = context.active_object
        if o is None:
            lay.label(text='Nothing selected')
        else:
            kind, label = model.describe(o)
            icon = {'locked': 'LOCKED', 'edit': 'EDITMODE_HLT', 'unity': 'LINKED', 'add': 'ADD', 'collection': 'OUTLINER_COLLECTION',
                    'group': 'GROUP', 'new': 'MESH_DATA'}.get(kind, 'QUESTION')
            _lines(lay.box(), f'{o.name}: {label}', icon)
        lay.operator('aa_bridge.make_editable', icon='EDITMODE_HLT')
        if _new_roots(context):
            col = lay.column(align=True)
            col.label(text='Collider in Unity')
            row = col.row(align=True)
            cur = str(o.get('collider', 'mesh')) if o is not None else 'mesh'
            for k, t in (('mesh', 'Mesh'), ('box', 'Box'), ('none', 'None')):
                row.operator('aa_bridge.set_collider', text=t, depress=cur == k).kind = k
        if o is not None and o.type == 'EMPTY' and 'aa_id' not in o and not model.collection_placement(o):
            lay.operator('aa_bridge.section_group', icon='GROUP', depress=bool(o.get('map_group')),
                         text='Section group (children separate)')


class AABRIDGE_PT_help(_Panel, bpy.types.Panel):
    bl_label = 'How it works'
    bl_parent_id = 'AABRIDGE_PT_panel'
    bl_options = {'DEFAULT_CLOSED'}

    @classmethod
    def poll(cls, context):
        return _session(context)

    def draw(self, context):
        lay = self.layout
        lib = context.scene.get('aa_library', 'Unity')
        for icon, text in (('ASSET_MANAGER', f'Asset Browser, library "{lib}": drag project prefabs in.'),
                           ('MESH_DATA', 'New objects with geometry become prefabs. Their origin is the pivot.'),
                           ('OUTLINER_COLLECTION', 'A collection placed as an instance becomes one prefab for all its copies.'),
                           ('DUPLICATE', 'Shift+D on Unity objects makes Unity copies (prefab links kept).'),
                           ('LOCKED', 'Grey objects are context outside the editable roots: not sent back.')):
            _lines(lay, text, icon)


CLASSES = (AABRIDGE_OT_sync, AABRIDGE_OT_make_editable, AABRIDGE_OT_set_collider, AABRIDGE_OT_section_group,
           AABRIDGE_OT_toggle_context, AABRIDGE_PT_panel, AABRIDGE_PT_selection, AABRIDGE_PT_help)


def _tick():
    try:
        sc = bpy.context.scene
        if sc is not None and sc.get('aa_session_json'):
            changed = bool(sync.stamp_applied()) | sync.poll_status() | sync.discarded()
            if changed:
                for w in bpy.context.window_manager.windows:
                    for a in w.screen.areas:
                        if a.type == 'VIEW_3D':
                            a.tag_redraw()
    except Exception as e:
        print('AA_WARN applied check:', e)
    return 2.0


def register_once():
    for c in CLASSES:
        if not getattr(c, 'is_registered', False):
            bpy.utils.register_class(c)
    if not bpy.app.background and not bpy.app.timers.is_registered(_tick):
        bpy.app.timers.register(_tick, first_interval=2.0, persistent=True)


def unregister():
    if bpy.app.timers.is_registered(_tick):
        bpy.app.timers.unregister(_tick)
    for c in reversed(CLASSES):
        if getattr(c, 'is_registered', False):
            bpy.utils.unregister_class(c)
