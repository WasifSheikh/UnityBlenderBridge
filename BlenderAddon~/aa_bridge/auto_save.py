"""Export before saving; publish only after Blender successfully saves its IDs and pending state."""
import hashlib
import os
import uuid
import bpy
from bpy.app.handlers import persistent
from . import session, sync
from .common import write_json

_busy = False
_pending = None

@persistent
def before_save(*_):
    global _busy, _pending
    if _busy or getattr(sync, '_saving', False) or os.environ.get('AA_BRIDGE_DISABLE_AUTO_SAVE') == '1':
        return
    scene = bpy.context.scene
    if not scene.get('aa_session_json') or not scene.get('aa_auto_sync_on_save', True):
        return
    data = session.load()
    if not data or not bpy.data.filepath or os.path.normcase(os.path.abspath(bpy.data.filepath)) != os.path.normcase(os.path.abspath(data['blend'])):
        return
    _busy = True
    _pending = None
    try:
        # A snapshot never modifies payloads Unity may already be importing.
        folder = os.path.join(data['outDir'], 'saved', uuid.uuid4().hex)
        with open(scene['aa_session_json'], 'rb') as f:
            base = hashlib.sha256(f.read()).hexdigest()
        sync.sync_to_unity(save_session=False, out_dir=folder)
        _pending = (os.path.join(data['outDir'], 'saved-latest.json'),
                    dict(sessionId=data['sessionId'], baseHash=base, blend=data['blend'], result=os.path.join(folder, 'result.json')))
    except Exception as error:
        scene['aa_status'] = 'Not sent on save: ' + str(error)
        print('AA_SAVE_EXPORT_ERROR', error)
    finally:
        _busy = False

@persistent
def after_save(*_):
    global _pending
    if _pending is not None:
        path, message = _pending
        if os.path.normcase(os.path.abspath(bpy.data.filepath)) != os.path.normcase(os.path.abspath(message['blend'])):
            _pending = None
            return
        write_json(path, message)
        bpy.context.scene['aa_status'] = 'Saved update ready for Unity. You can close Blender.'
        _pending = None

def register():
    for handlers, fn in ((bpy.app.handlers.save_pre, before_save), (bpy.app.handlers.save_post, after_save)):
        if fn not in handlers:
            handlers.append(fn)

def unregister():
    for handlers, fn in ((bpy.app.handlers.save_pre, before_save), (bpy.app.handlers.save_post, after_save)):
        if fn in handlers:
            handlers.remove(fn)
