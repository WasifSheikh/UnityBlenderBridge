"""Real Blender save/close handoff regression. Uses temporary fixtures, never the live Unity project."""
import os,sys,tempfile,json
from pathlib import Path
path=Path(__file__).with_name('test_blender_side.py')
scope={'__file__':str(path),'__name__':'offline_fixture'}
exec(compile(path.read_text().rsplit('\nmain()',1)[0],str(path),'exec'),scope)
root=tempfile.mkdtemp(prefix='bb_offline_')
scope['run'](root)
import bpy
from aa_bridge import session,auto_save,version
from aa_bridge.common import write_json
S=session.load()
write_json(S['statusPath'],dict(sessionId=S['sessionId'],state='ok'))
os.environ.pop('AA_BRIDGE_DISABLE_AUTO_SAVE',None)
auto_save.register()
bpy.ops.wm.save_as_mainfile(filepath=S['blend'])
bpy.ops.wm.save_mainfile()
pointer=Path(S['outDir'])/'saved-latest.json'
assert pointer.exists(),'Save did not publish an update'
first=json.loads(pointer.read_text()); original=Path(first['result']).read_bytes()
bpy.context.scene['offline_test_marker']='second save'
bpy.ops.wm.save_mainfile()
second=json.loads(pointer.read_text())
assert first['result']!=second['result'],'Payload must be immutable per save'
assert Path(first['result']).read_bytes()==original,'Later save modified earlier payload'
assert first['baseHash']==second['baseHash'],'Offline saves must share unchanged Unity baseline'
assert json.loads(Path(second['result']).read_text())['sessionId']==S['sessionId']
assert version.current()=='0.3.0'
auto_save.unregister()
bpy.ops.wm.open_mainfile(filepath=S['blend'])
assert bpy.context.scene.get('aa_session')==S['sessionId'],'Saved bridge IDs did not survive reopen'
print('AA_OFFLINE_SAVE_DONE: save twice, immutable payload, baseline, version and reopen passed',flush=True)
