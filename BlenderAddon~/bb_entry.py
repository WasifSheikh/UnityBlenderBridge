"""What Unity runs: blender [file.blend] --python bb_entry.py -- <command> <json path>

Commands (aa_bridge/launch.py): build_library, open_session, resume. Arguments come after "--", so paths with spaces,
quotes or apostrophes never end up inside Python source.
"""
import os
import sys

sys.dont_write_bytecode = True  # the package folder may be read-only (Library/PackageCache)
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    if len(argv) < 2:
        raise SystemExit('usage: blender --python bb_entry.py -- <build_library|open_session|resume> <json>')
    from aa_bridge import launch
    fn = getattr(launch, argv[0], None)
    if fn is None or argv[0].startswith('_'):
        raise SystemExit(f'unknown command {argv[0]}')
    fn(argv[1])


main()
