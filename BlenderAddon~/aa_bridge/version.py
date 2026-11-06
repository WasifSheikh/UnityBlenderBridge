"""The package manifest is the release version shared by both interfaces."""
import json
from pathlib import Path

def current():
    return json.loads((Path(__file__).resolve().parents[2] / 'package.json').read_text())['version']
