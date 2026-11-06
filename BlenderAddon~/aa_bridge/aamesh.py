"""Meshes Unity converts for Blender (Editor/BBMeshWriter.cs), already in Blender space with winding flipped.

AAM1: "AAM1", int verts, int flags (1 normals, 2 uv0), int submeshes, int count per submesh, f3 positions,
      [f3 normals], [f2 uv0], int indices
AAM2: like AAM1, flags also 4 uv1, 8 colours; after uv0 come [f2 uv1] and [u8x4 colours]
"""
import struct

import numpy as np

F_NORMALS, F_UV0, F_UV1, F_COLORS = 1, 2, 4, 8


def read(path):
    b = open(path, 'rb').read()
    magic = b[:4]
    if magic not in (b'AAM1', b'AAM2'):
        raise ValueError(f'{path}: not an AAM mesh')
    v2 = magic == b'AAM2'
    n, flags, ns = struct.unpack_from('<iii', b, 4)
    off = 16
    counts = np.frombuffer(b, '<i4', ns, off).astype(np.int64)
    off += 4 * ns
    d = {'n': n, 'counts': counts}

    def take(dtype, count, size):
        nonlocal off
        a = np.frombuffer(b, dtype, count, off)
        off += size
        return a

    d['pos'] = take('<f4', n * 3, 12 * n)
    d['nrm'] = take('<f4', n * 3, 12 * n) if flags & F_NORMALS else None
    d['uv0'] = take('<f4', n * 2, 8 * n) if flags & F_UV0 else None
    d['uv1'] = take('<f4', n * 2, 8 * n) if v2 and flags & F_UV1 else None
    d['col'] = take('u1', n * 4, 4 * n) if v2 and flags & F_COLORS else None
    counts -= counts % 3
    d['idx'] = np.frombuffer(b, '<i4', int(counts.sum()), off)
    return d


def load(me, path):
    """Fills mesh me from an .aamesh file: triangles, one material slot per submesh, UVs (UVMap, UV2), vertex colours
    (Col) and Unity's normals as custom normals."""
    d = read(path)
    n, counts, idx = d['n'], d['counts'], d['idx']
    total = int(counts.sum())
    nt = total // 3
    me.clear_geometry()
    me.vertices.add(n)
    me.vertices.foreach_set('co', d['pos'])
    if nt:
        me.loops.add(total)
        me.loops.foreach_set('vertex_index', idx)
        me.polygons.add(nt)
        me.polygons.foreach_set('loop_start', np.arange(0, total, 3, dtype=np.int32))
        try:
            me.polygons.foreach_set('loop_total', np.full(nt, 3, dtype=np.int32))
        except (AttributeError, TypeError, RuntimeError):
            pass  # derived from loop_start in newer Blender
        me.polygons.foreach_set('material_index', np.repeat(np.arange(len(counts), dtype=np.int32), counts // 3))
        for name, uv in (('UVMap', d['uv0']), ('UV2', d['uv1'])):
            if uv is not None:
                layer = me.uv_layers.new(name=name)
                layer.data.foreach_set('uv', uv.reshape(-1, 2)[idx].ravel())
        if d['col'] is not None:
            attr = me.color_attributes.new('Col', 'BYTE_COLOR', 'CORNER')
            rgba = (d['col'].reshape(-1, 4)[idx].astype(np.float32) / 255.0).ravel()
            try:
                attr.data.foreach_set('color_srgb', rgba)  # bytes as they were in Unity
            except (AttributeError, TypeError):
                attr.data.foreach_set('color', rgba)
    me.update()
    if d['nrm'] is not None and nt:
        me.polygons.foreach_set('use_smooth', np.ones(nt, dtype=bool))
        try:
            me.normals_split_custom_set_from_vertices(d['nrm'].reshape(-1, 3))
        except Exception:
            pass
    me.validate(clean_customdata=False)
    while len(me.materials) < len(counts):
        me.materials.append(None)
