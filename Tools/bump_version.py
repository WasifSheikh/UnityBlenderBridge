"""Run after each completed update: python Tools/bump_version.py [patch|minor|major]."""
from pathlib import Path
import json,sys
manifest=Path(__file__).resolve().parents[1]/'package.json'
data=json.loads(manifest.read_text());v=list(map(int,data['version'].split('.')))
kind=sys.argv[1] if len(sys.argv)>1 else 'patch'
i={'major':0,'minor':1,'patch':2}[kind];v[i]+=1
for n in range(i+1,3):v[n]=0
data['version']='.'.join(map(str,v));manifest.write_text(json.dumps(data,indent=2)+'\n')
print(data['version'])
