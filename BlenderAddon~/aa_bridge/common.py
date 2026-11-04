import json
import os
import re

from mathutils import Matrix


def read_json(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def write_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(data, f)
    os.replace(tmp, path)


def mat(m):
    """Column-major 16 floats (as Unity writes them) -> Matrix."""
    return Matrix([[m[0], m[4], m[8], m[12]], [m[1], m[5], m[9], m[13]],
                   [m[2], m[6], m[10], m[14]], [m[3], m[7], m[11], m[15]]])


def flat(M):
    return [M[r][c] for c in range(4) for r in range(4)]


def progress(i, n, msg):
    print(f'AA_PROGRESS {i} {n} {msg}', flush=True)


def slug(s):
    s = re.sub(r'\.\d{3}$', '', s or 'asset')
    s = re.sub(r'[^A-Za-z0-9_-]+', '_', s).strip('_')
    return s or 'asset'


def srgb_to_linear(c):
    c = max(0.0, float(c))
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
