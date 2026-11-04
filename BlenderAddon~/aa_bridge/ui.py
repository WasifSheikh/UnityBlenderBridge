"""3D View > Sidebar > Unity."""
import bpy

from . import sync


class AABRIDGE_OT_sync(bpy.types.Operator):
    bl_idname = 'aa_bridge.sync'
    bl_label = 'Sync to Unity'
    bl_description = 'Send everything changed in this file to the Unity scene (moves, deletes, copies, new and edited models)'

    @classmethod
    def poll(cls, context):
        return bool(context.scene.get('aa_session_json'))

    def execute(self, context):
        try:
            msg = sync.sync_to_unity()
        except Exception as e:
            import traceback
            traceback.print_exc()
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

    @staticmethod
    def has_prefab(objs):
        return any(o.instance_collection.get('aa_kind') == 'prefab' for o in objs)

    @staticmethod
    def targets(context):
        return [o for o in context.selected_objects if sync.is_instance(o) and 'aa_id' in o and not o.get('aa_locked')]

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and bool(cls.targets(context))

    def invoke(self, context, event):
        if self.has_prefab(self.targets(context)):
            return context.window_manager.invoke_props_dialog(self, width=420, title='Make editable')
        return self.execute(context)

    def draw(self, context):
        lay = self.layout
        lay.label(text='Some of these are prefab instances. In Unity, the new geometry should become:')
        lay.prop(self, 'mode', expand=True)
        if self.mode == 'source':
            lay.label(text='Every copy of the prefab, in every scene, will change.', icon='ERROR')
        else:
            lay.label(text='Other copies stay as they are.', icon='INFO')

    def execute(self, context):
        done = []
        for o in self.targets(context):
            try:
                done.append(sync.make_editable(o, self.mode))
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


class AABRIDGE_PT_panel(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Unity'
    bl_label = 'Unity Bridge'

    def draw(self, context):
        lay = self.layout
        sc = context.scene
        if not sc.get('aa_session_json'):
            lay.label(text='Not a Unity session.', icon='INFO')
            lay.label(text='Open it from Unity:')
            lay.label(text='Blender Bridge > Open in Blender')
            return
        lay.label(text=sc.name, icon='SCENE_DATA')
        row = lay.row()
        row.scale_y = 1.8
        row.operator('aa_bridge.sync', icon='EXPORT')
        status = sc.get('aa_status', '')
        if status:
            box = lay.box()
            for line in _wrap(status, 34):
                box.label(text=line)
        lay.operator('aa_bridge.make_editable', icon='EDITMODE_HLT')
        o = context.active_object
        if o is not None:
            col = lay.column(align=True)
            if o.get('aa_locked'):
                col.label(text='Locked context object', icon='LOCKED')
            elif sync.is_edit_root(o):
                col.label(text='Editable: geometry goes to Unity', icon='MESH_DATA')
                if o.get('aa_edit_mode') == 'source':
                    col.label(text='Changes the prefab (all copies)', icon='ERROR')
                elif o.get('aa_edit_mode') == 'variant':
                    col.label(text='Only this one (prefab variant)')
            elif 'aa_id' in o and sync.is_instance(o):
                col.label(text='Unity object (Make editable to model)', icon='LINKED')
            elif 'aa_id' in o:
                col.label(text='Unity object', icon='LINKED')
            elif sync.is_instance(o):
                col.label(text='New instance (on sync)', icon='ADD')
            elif o.type in sync.GEO:
                col.label(text='New model (on sync)', icon='MESH_DATA')
                col.label(text='Collider: custom prop "collider"')
        box = lay.box()
        box.label(text='Asset Browser > "AA Unity": drag', icon='ASSET_MANAGER')
        box.label(text='project prefabs in. New objects with')
        box.label(text='geometry become prefabs on sync.')


def _wrap(text, n):
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


CLASSES = (AABRIDGE_OT_sync, AABRIDGE_OT_make_editable, AABRIDGE_PT_panel)


def _tick():
    try:
        if bpy.context.scene and bpy.context.scene.get('aa_session_json'):
            if sync.stamp_applied():
                for w in bpy.context.window_manager.windows:
                    for a in w.screen.areas:
                        a.tag_redraw()
    except Exception as e:
        print('AA_WARN applied check:', e)
    return 2.0


def register_once():
    for c in CLASSES:
        if not c.is_registered:
            bpy.utils.register_class(c)
    if not bpy.app.timers.is_registered(_tick):
        bpy.app.timers.register(_tick, first_interval=2.0, persistent=True)


def unregister():
    if bpy.app.timers.is_registered(_tick):
        bpy.app.timers.unregister(_tick)
    for c in reversed(CLASSES):
        if c.is_registered:
            bpy.utils.unregister_class(c)
