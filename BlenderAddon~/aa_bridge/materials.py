"""Blender materials of a new or edited model -> the values and textures Unity rebuilds as URP Lit
(Editor/BBMaterialWriter.cs). Materials that came from Unity (aa_key) are only referenced: Unity keeps the real one."""
import hashlib
import os
import uuid
import shutil

import bpy
import numpy as np

from .common import slug

TEX_EXT = {'.png', '.jpg', '.jpeg', '.tga', '.tif', '.tiff', '.exr', '.psd'}


def node_tree(m):
    return m.node_tree if m is not None and m.node_tree is not None and getattr(m, 'use_nodes', True) else None


def image_from(socket):
    """First image texture feeding a socket (through any nodes in between)."""
    if socket is None or not socket.is_linked:
        return None
    stack, seen = [socket.links[0].from_node], set()
    while stack:
        n = stack.pop()
        if n in seen:
            continue
        seen.add(n)
        if n.type == 'TEX_IMAGE' and n.image is not None:
            return n.image
        for i in n.inputs:
            for link in i.links:
                stack.append(link.from_node)
    return None


def project_asset_path(value):
    """Validate a project-relative Unity asset marker without permitting traversal or absolute paths."""
    parts = str(value or '').replace('\\', '/').split('/')
    if len(parts) < 2 or parts[0] != 'Assets' or any(p in ('', '.', '..') or ':' in p or '\0' in p for p in parts):
        return ''
    return '/'.join(parts)


def project_asset(img, project):
    """'Assets/...' when the image file is already part of the Unity project (then Unity reuses it), else ''."""
    if img is None or img.packed_file is not None or not project:
        return ''
    marker = project_asset_path(img.get('aa_project_path'))
    if marker:
        # A Unity move may have made the old file path unavailable. Keep its identity for Unity's alias
        # resolver rather than exporting cached/placeholder pixels as a new texture.
        return marker
    if not img.filepath:
        return ''
    p = os.path.abspath(bpy.path.abspath(img.filepath))
    a = os.path.abspath(os.path.join(project, 'Assets'))
    if os.path.normcase(p).startswith(os.path.normcase(a) + os.sep) and os.path.exists(p):
        return 'Assets/' + p[len(a) + 1:].replace(os.sep, '/')  # keep the real case: Unity paths are case-sensitive
    return ''


class Images:
    """Writes the textures of one export into <dir>; images that are project files are referenced, not copied."""

    def __init__(self, tex_dir, project=''):
        self.dir = tex_dir
        self.project = project
        self.done = {}
        self.names = set()

    def _name(self, base, ext):
        n = base + ext
        i = 2
        while n.lower() in self.names:
            n = f'{base}_{i}{ext}'
            i += 1
        self.names.add(n.lower())
        return n

    def save(self, img):
        if img is None:
            return ''
        if img.name in self.done:
            return self.done[img.name]
        existing = project_asset(img, self.project)
        if existing:
            self.done[img.name] = 'asset:' + existing  # already a Unity texture: reuse it, copy nothing
            return self.done[img.name]
        os.makedirs(self.dir, exist_ok=True)
        src = bpy.path.abspath(img.filepath) if img.filepath else ''
        ext = os.path.splitext(src)[1].lower()
        base = slug(os.path.splitext(img.name)[0])
        if img.packed_file is None and src and os.path.exists(src) and ext in TEX_EXT:
            name = self._name(base, ext)
            shutil.copy2(src, os.path.join(self.dir, name))
        else:
            name = self._name(base, '.png')
            dst = os.path.join(self.dir, name)
            c = img.copy()   # save a PNG copy: the original keeps its own format and path
            try:
                c.filepath_raw = dst
                c.file_format = 'PNG'
                c.save()
            except Exception:
                img.save_render(dst)
            finally:
                bpy.data.images.remove(c)
        rel = 'Textures/' + name
        self.done[img.name] = rel
        return rel

    def mask(self, metal_img, rough_img, metal, rough, name):
        """URP metallic map: R = metallic, A = smoothness (1 - roughness)."""
        imgs = [i for i in (metal_img, rough_img) if i is not None]
        w = max(i.size[0] for i in imgs)
        h = max(i.size[1] for i in imgs)

        def channel(img, default):
            if img is None:
                return np.full((h, w), default, dtype=np.float32)
            scaled = None
            if tuple(img.size) != (w, h):
                scaled = img = img.copy()
                img.scale(w, h)
            px = np.empty(w * h * 4, dtype=np.float32)
            img.pixels.foreach_get(px)
            if scaled is not None:
                bpy.data.images.remove(scaled)
            return px.reshape(h, w, 4)[:, :, 0]

        out = np.zeros((h, w, 4), dtype=np.float32)
        out[:, :, 0] = channel(metal_img, metal)
        out[:, :, 3] = 1.0 - channel(rough_img, rough)
        fname = self._name(slug(name) + '_MetallicSmoothness', '.png')
        im = bpy.data.images.new('aa_mask', w, h, alpha=True)
        try:
            im.colorspace_settings.name = 'Non-Color'
        except TypeError:
            pass
        os.makedirs(self.dir, exist_ok=True)
        im.pixels.foreach_set(out.ravel())
        im.filepath_raw = os.path.join(self.dir, fname)
        im.file_format = 'PNG'
        im.save()
        bpy.data.images.remove(im)
        return 'Textures/' + fname


class Fingerprint:
    """Same interpretation as Images, without writing files: used to tell whether two models are the same asset."""

    def __init__(self, project, material_mode='blender', sync_textures=True):
        self.project = project
        self.material_mode, self.sync_textures = material_mode, sync_textures
        self.images = {}
        self.materials = {}

    def save(self, img):
        if img not in self.images:
            path = project_asset(img, self.project)
            if path:
                self.images[img] = 'asset:' + path
            else:
                pixels = np.empty(len(img.pixels), dtype=np.float32)
                img.pixels.foreach_get(pixels)
                self.images[img] = (tuple(img.size), img.colorspace_settings.name, img.alpha_mode,
                                    hashlib.sha256(pixels.tobytes()).hexdigest() if pixels.size else img.name)
        return self.images[img]

    def mask(self, metal_img, rough_img, metal, rough, name):
        return tuple(self.save(img) if img is not None else value for img, value in ((metal_img, metal), (rough_img, rough)))

    def material(self, m):
        if m is None:
            return None
        if m not in self.materials:
            if self.material_mode == 'unity':
                self.materials[m] = {'name': m.name, 'unity': m.get('aa_key', '')}
                return self.materials[m]
            d = describe(m, self, self.sync_textures)
            del d['name']
            d.pop('id', None)  # Ownership identity does not change the visible material fingerprint.
            self.materials[m] = d
        return self.materials[m]


def reference(m):
    """Only slot/identity information; Unity owns this material's settings and textures."""
    r = {'name': m.name, 'unity': m.get('aa_key', '')}
    if not r['unity']:
        if m.get('aa_bb_material_id'):
            r['id'] = m['aa_bb_material_id']
        elif m.library is not None:
            identity = os.path.normcase(os.path.realpath(bpy.path.abspath(m.library.filepath))) + '|' + m.name
            r['id'] = uuid.uuid5(uuid.NAMESPACE_URL, 'blender-material:' + identity).hex
        else:
            m['aa_bb_material_id'] = uuid.uuid4().hex
            r['id'] = m['aa_bb_material_id']
    return r


def describe(m, images, sync_textures=True):
    r = {'name': m.name, 'unity': m.get('aa_key', ''), 'color': list(m.diffuse_color), 'metallic': 0.0, 'smoothness': 0.5, 'normalScale': 1.0,
         'alpha': 'opaque', 'cutoff': 0.5, 'emission': [0, 0, 0], 'baseMap': '', 'normalMap': '', 'maskMap': '', 'emissionMap': ''}
    nt = node_tree(m)
    r.update(reference(m))
    if r['unity'] or nt is None:
        return r  # a Unity material: Unity keeps using the real one
    nodes = nt.nodes
    bsdf = None
    out = next((n for n in nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output), None)
    if out is not None and out.inputs['Surface'].is_linked:
        stack = [out.inputs['Surface'].links[0].from_node]
        while stack and bsdf is None:
            n = stack.pop()
            if n.type == 'BSDF_PRINCIPLED':
                bsdf = n
            else:
                stack.extend(link.from_node for i in n.inputs for link in i.links)
    bsdf = bsdf or next((n for n in nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is None:
        return r
    inp = bsdf.inputs
    base = image_from(inp['Base Color']) if sync_textures else None
    if base is not None:
        r['baseMap'] = images.save(base)
        r['color'] = [1, 1, 1, 1]
    else:
        r['color'] = list(inp['Base Color'].default_value)
    a = inp['Alpha']
    if a.is_linked or a.default_value < 0.999:
        method = getattr(m, 'surface_render_method', '') or getattr(m, 'blend_method', '')
        r['alpha'] = 'blend' if method in ('BLENDED', 'BLEND') else 'clip'
        if not a.is_linked:
            r['color'][3] = a.default_value
        else:
            gt = a.links[0].from_node
            if gt.type == 'MATH' and gt.operation == 'GREATER_THAN':
                r['cutoff'] = gt.inputs[1].default_value
    mi, ri = (image_from(inp['Metallic']), image_from(inp['Roughness'])) if sync_textures else (None, None)
    if mi is not None or ri is not None:
        r['maskMap'] = images.mask(mi, ri, inp['Metallic'].default_value, inp['Roughness'].default_value, m.name)
    r['metallic'] = float(inp['Metallic'].default_value)
    r['smoothness'] = 1.0 - float(inp['Roughness'].default_value)
    n = inp['Normal']
    if n.is_linked and n.links[0].from_node.type == 'NORMAL_MAP':
        nm = n.links[0].from_node
        r['normalScale'] = float(nm.inputs['Strength'].default_value)
        img = image_from(nm.inputs['Color']) if sync_textures else None
        if img is not None:
            r['normalMap'] = images.save(img)
    ec, es = inp.get('Emission Color'), inp.get('Emission Strength')
    strength = es.default_value if es is not None else 0.0
    if ec is not None and strength > 0:
        img = image_from(ec) if sync_textures else None
        if img is not None:
            r['emissionMap'] = images.save(img)
            r['emission'] = [strength] * 3
        elif max(ec.default_value[:3]) > 0:
            r['emission'] = [c * strength for c in ec.default_value[:3]]
    return r


def local_copy(m, cache):
    """Local copy of a material linked from the library (keeps aa_key, so Unity keeps its own material)."""
    if m is None or m.library is None:
        return m
    if m.name not in cache:
        c = m.copy()
        c.name = m.name
        nt = node_tree(c)
        if nt is not None:
            for n in nt.nodes:
                img = getattr(n, 'image', None)
                if img is not None and img.library is not None:
                    n.image = cache.setdefault('img:' + img.name, img.copy())
        cache[m.name] = c
    return cache[m.name]
