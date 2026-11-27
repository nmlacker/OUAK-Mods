"""Turns an .llcm file back into the sources export_llcm.py builds it from (format: Cousins/FORMAT.md).

Run it with Blender 5.2, headless, from any folder:

    blender -b --factory-startup --python-exit-code 1 --python import_llcm.py -- <file.llcm> <out_dir> [blend_name]

It writes into <out_dir>:
- <blend_name> (default: the INFO chunk's source .blend name): the rig `model` (turned 90 degrees about X, as in every
  cousin's .blend: armature space X her left, Y up, Z forward) and one mesh object per MESH chunk, at identity in world
  space, parented to the rig with an Armature modifier, its UVs, its corner normals as custom normals, its vertex
  groups and its one material with the texture.
- textures/<name>.png: each TEXR chunk's PNG, byte for byte.
- face_map.json and rig_spec.json: the face map and rig spec export_llcm.py and check_llcm.py read, rebuilt from the
  FACE and SKEL chunks.

Unity vertices that share a position and the same weights become one Blender vertex; their corners keep their own
normal and UV, so export_llcm.py splits them again. Use it for a cousin whose .blend is lost; the rebuilt .blend is a
starting point to edit, not a hand-made model.
"""
import bpy
import sys
import os
import json
import struct
import math
import numpy as np


class ImportError_(Exception):
    pass


def fail(msg):
    raise ImportError_(msg)


ROLE_BODY, ROLE_PLATE, ROLE_FACE, ROLE_OVERLAY, ROLE_EXTRA, ROLE_CARD = range(6)
GROUP_ROLES = ('face', 'eye', 'mouth', 'overlay')
NONE8, NONE16 = 0xFF, 0xFFFF


class R:
    def __init__(self, data):
        self.d, self.p = data, 0

    def take(self, fmt):
        v = struct.unpack_from('<' + fmt, self.d, self.p)
        self.p += struct.calcsize('<' + fmt)
        return v

    def one(self, fmt):
        return self.take(fmt)[0]

    def arr(self, dtype, count):
        a = np.frombuffer(self.d, dtype=np.dtype(dtype).newbyteorder('<'), count=count, offset=self.p)
        self.p += a.nbytes
        return a.copy()


def parse(path):
    data = open(path, 'rb').read()
    if data[:4] != b'LLCM':
        fail('not an .llcm file')
    version, flags, nchunks, length = struct.unpack_from('<HHII', data, 4)
    if version != 1 or length != len(data):
        fail('version %d, length %d of %d' % (version, length, len(data)))
    f = dict(flags=flags, textures=[], meshes=[])
    p = 16
    for _ in range(nchunks):
        tag = data[p:p + 4]
        n = struct.unpack_from('<I', data, p + 4)[0]
        r = R(data[p + 8:p + 8 + n])
        p += 8 + ((n + 3) & ~3)
        if tag == b'STRS':
            S = []
            for _ in range(r.one('H')):
                L = r.one('H')
                S.append(r.d[r.p:r.p + L].decode('utf-8'))
                r.p += L
            f['S'] = S
            s = lambda i: None if i == NONE16 else S[i]
        elif tag == b'INFO':
            f['id'], f['name'], f['style'], f['marker'], f['blend'] = [s(i) for i in r.take('5H')]
        elif tag == b'SKEL':
            J = []
            for _ in range(r.one('B')):
                name, parent, follow = r.take('HbB')
                local = r.take('3f')
                r.take('4f')
                model = r.take('3f')
                J.append(dict(name=s(name), parent=parent, follow=follow, local=np.array(local), model=np.array(model)))
            f['joints'] = J
        elif tag == b'FOLW':
            f['folw'] = [r.take('BB3f') for _ in range(r.one('B'))]
        elif tag == b'TEXR':
            name, w, h, enc, tflags, filt, wrap, nbytes = r.take('3H4BI')
            f['textures'].append(dict(name=s(name), w=w, h=h, flags=tflags, png=r.d[r.p:r.p + nbytes]))
        elif tag == b'MATL':
            f['materials'] = [dict(zip(('name', 'tex', 'alpha'), r.take('HBB'))) for _ in range(r.one('B'))]
            for m in f['materials']:
                m['name'] = s(m['name'])
        elif tag == b'MESH':
            name, role, mflags, slot, slot_name, card, mat, template, root, nb = r.take('HBBBHBBHBB')
            bones = list(r.take('%dB' % nb))
            r.take('6f')
            nv, nt = r.take('HH')
            m = dict(name=s(name), role=role, flags=mflags, slot=slot, slot_name=s(slot_name), card=card, mat=mat,
                     template=s(template), bones=bones)
            m['pos'] = r.arr('f4', nv * 3).reshape(-1, 3)
            m['nrm'] = r.arr('f4', nv * 3).reshape(-1, 3)
            m['uv'] = r.arr('f4', nv * 2).reshape(-1, 2)
            m['bidx'] = r.arr('u1', nv * 4).reshape(-1, 4)
            m['bw'] = r.arr('u2', nv * 4).reshape(-1, 4)
            m['tris'] = r.arr('u2', nt * 3).reshape(-1, 3)
            f['meshes'].append(m)
        elif tag == b'FACE':
            slots = [s(i) for i in r.take('%dH' % r.one('B'))]
            blink_slot, open_s, closed_s, blink_card, over_card, default_card = r.take('BffBBB')
            groups = []
            for gi in range(r.one('B')):
                game = r.take('4B')
                pieces = r.take('4B')
                card, blinks = r.take('BB')
                show = list(r.take('%dB' % r.one('B')))
                hide = list(r.take('%dB' % r.one('B')))
                groups.append(dict(group=gi, game=game, pieces=pieces, card=card, blinks=blinks, show=show, hide=hide))
            nx = r.one('B')
            exprs = list(r.take('%dB' % nx))
            rows = [r.take('BB') for _ in range(r.one('B'))]
            f['face'] = dict(slots=slots, blink_slot=blink_slot, open=open_s, closed=closed_s, blink_card=blink_card,
                             over_card=over_card, default_card=default_card, groups=groups, exprs=exprs, rows=rows)
    return f


def u2w(p):
    """Unity cm under `model` -> Blender world metres: (x, y, z) -> (-x, -z, y) / 100."""
    p = np.asarray(p, float)
    return np.stack([-p[..., 0], -p[..., 2], p[..., 1]], -1) / 100.0


def u2w_dir(n):
    n = np.asarray(n, float)
    return np.stack([-n[..., 0], -n[..., 2], n[..., 1]], -1)


def scrub_ui_paths():
    """The factory start-up has a File Browser that remembers the home documents folder of whoever runs this, and a
    .blend saves it (as the cousins' build scripts found). Overwrite the whole stored text, then leave the file's own
    folder."""
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == 'FILE_BROWSER' and space.params is not None:
                    space.params.directory = b'/' * 512
                    space.params.directory = b'//'


def build(f, out_dir, blend_name):
    if f['flags'] & 1:
        fail('old-style (card) files are not handled yet')
    bpy.ops.wm.read_factory_settings(use_empty=True)
    os.makedirs(os.path.join(out_dir, 'textures'), exist_ok=True)

    # the rig: armature space = Unity axes with X mirrored, in metres
    ad = bpy.data.armatures.new('model')
    rig = bpy.data.objects.new('model', ad)
    bpy.context.scene.collection.objects.link(rig)
    rig.rotation_euler = (math.pi / 2, 0.0, 0.0)
    bpy.context.view_layer.update()
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='EDIT')
    J = f['joints']
    for j in J:
        b = ad.edit_bones.new(j['name'])
        h = np.array([-j['model'][0], j['model'][1], j['model'][2]]) / 100.0
        b.head = h
        b.tail = h + np.array([0.0, 0.05, 0.0])
    for j in J:
        if j['parent'] >= 0:
            ad.edit_bones[j['name']].parent = ad.edit_bones[J[j['parent']]['name']]
    bpy.ops.object.mode_set(mode='OBJECT')
    jname = [j['name'] for j in J]

    # textures and materials
    images = []
    for t in f['textures']:
        path = os.path.join(out_dir, 'textures', t['name'])
        with open(path, 'wb') as fh:
            fh.write(t['png'])
        img = bpy.data.images.load(path)
        img.colorspace_settings.name = 'sRGB' if t['flags'] & 2 else 'Non-Color'
        img.filepath = '//textures/' + t['name']
        images.append(img)
    mats = []
    for m in f['materials']:
        mat = bpy.data.materials.new(m['name'])
        mat.use_nodes = True
        nt = mat.node_tree
        for n in list(nt.nodes):
            if n.type not in ('OUTPUT_MATERIAL', 'BSDF_PRINCIPLED'):
                nt.nodes.remove(n)
        bsdf = [n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'][0]
        tex = nt.nodes.new('ShaderNodeTexImage')
        tex.image = images[m['tex']]
        tex.interpolation = 'Linear'
        tex.extension = 'EXTEND'
        nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
        if m['alpha']:
            nt.links.new(tex.outputs['Alpha'], bsdf.inputs['Alpha'])
        mats.append(mat)

    merged_total = degenerate_total = 0
    for m in f['meshes']:
        P = u2w(m['pos'])
        N = u2w_dir(m['nrm'])
        # one Blender vertex per (position, weights)
        key_of, vmap, verts, vweights = {}, [], [], []
        for i in range(len(P)):
            ws = tuple((m['bones'][m['bidx'][i, k]], int(m['bw'][i, k])) for k in range(4) if m['bw'][i, k])
            key = (m['pos'][i].tobytes(), ws)
            v = key_of.get(key)
            if v is None:
                v = key_of[key] = len(verts)
                verts.append(P[i])
                vweights.append(ws)
            vmap.append(v)
        merged_total += len(P) - len(verts)
        faces, corners = [], []
        for a, b, c in m['tris']:
            tri = (int(a), int(c), int(b))      # Unity (a, b, c) is Blender (a, c, b)
            if len({vmap[x] for x in tri}) < 3:
                degenerate_total += 1
                continue
            faces.append([vmap[x] for x in tri])
            corners.extend(tri)
        me = bpy.data.meshes.new(m['name'])
        me.from_pydata([tuple(v) for v in verts], [], faces)
        me.update()
        if len(me.polygons) != len(faces):
            fail('%s: Blender kept %d of %d triangles' % (m['name'], len(me.polygons), len(faces)))
        uvl = me.uv_layers.new(name='UVMap')
        uv = np.array([m['uv'][c] for c in corners], np.float32)
        uvl.data.foreach_set('uv', uv.ravel())
        # Smooth faces, and a sharp edge wherever the file gives the two faces' corners at an end of it different
        # normals: corners in one smooth fan then share one custom normal, so they come back as one Unity vertex.
        me.shade_smooth()
        corner_n = {}
        for fi, face in enumerate(faces):
            for k in range(3):
                corner_n[(fi, face[k])] = m['nrm'][corners[3 * fi + k]]
        edge_faces = {}
        for fi, face in enumerate(faces):
            for k in range(3):
                e = tuple(sorted((face[k], face[(k + 1) % 3])))
                edge_faces.setdefault(e, []).append(fi)
        sharp = set()
        for e, fs in edge_faces.items():
            if len(fs) == 2 and any(np.abs(corner_n[(fs[0], v)] - corner_n[(fs[1], v)]).max() > 1e-6 for v in e):
                sharp.add(e)
        if sharp:
            attr = me.attributes.get('sharp_edge') or me.attributes.new('sharp_edge', 'BOOLEAN', 'EDGE')
            for ed in me.edges:
                if tuple(sorted(ed.vertices)) in sharp:
                    attr.data[ed.index].value = True
        me.normals_split_custom_set([tuple(N[c]) for c in corners])
        o = bpy.data.objects.new(m['name'], me)
        bpy.context.scene.collection.objects.link(o)
        o.data.materials.append(mats[m['mat']])
        o.parent = rig
        o.matrix_parent_inverse = rig.matrix_world.inverted()
        groups = {}
        for v, ws in enumerate(vweights):
            for j, w in ws:
                g = groups.get(j)
                if g is None:
                    g = groups[j] = o.vertex_groups.new(name=jname[j])
                g.add([v], w / 65535.0, 'REPLACE')
        mod = o.modifiers.new('Armature', 'ARMATURE')
        mod.object = rig
        mod.use_vertex_groups = True
        mod.use_bone_envelopes = False
        if m['flags'] & 1:
            o.hide_viewport = o.hide_render = True
    bpy.context.preferences.filepaths.save_version = 0
    out = os.path.join(out_dir, blend_name)
    scrub_ui_paths()
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=False, relative_remap=True)
    scrub_ui_paths()
    bpy.ops.wm.save_mainfile(compress=False)
    with open(out, 'rb') as fh:
        if os.path.expanduser('~').encode() in fh.read():
            fail('%s still holds the home folder path' % out)
    print('BLEND %s: meshes %d, vertices merged %d, degenerate triangles dropped %d'
          % (out, len(f['meshes']), merged_total, degenerate_total))


def face_map(f):
    F = f['face']
    slots = F['slots']
    names = [m['name'] for m in f['meshes']]
    sname = lambda i: None if i == NONE8 else slots[i]
    mname = lambda i: None if i == NONE8 else names[i]
    pieces = {m['name']: '' for m in f['meshes'] if m['role'] in (ROLE_PLATE, ROLE_FACE, ROLE_OVERLAY, ROLE_EXTRA)}
    groups = []
    for g in F['groups']:
        groups.append(dict(group=g['group'], ouak={r: sname(g['game'][k]) for k, r in enumerate(GROUP_ROLES)},
                           blinks=bool(g['blinks']), new_style={r: mname(g['pieces'][k]) for k, r in enumerate(GROUP_ROLES)},
                           file_show=[names[i] for i in g['show']], file_hide=[names[i] for i in g['hide']]))
    return {
        'about': "%s's face map, rebuilt by Tools/import_llcm.py from %s_%s.llcm. file_show and file_hide are the "
                 "file's own lists, kept to compare with a re-export." % (f['name'], f['id'], f['style']),
        'blink': {'ouak': {'blinkEyeType': F['blink_slot'], 'blink_eye': slots[F['blink_slot']],
                           'open_seconds': round(F['open'], 6), 'closed_seconds': round(F['closed'], 6)},
                  'new_style': '%s_m for %g s every %g s on groups that blink' % (slots[F['blink_slot']], F['closed'], F['open']),
                  'old_style': 'none: this cousin has no old style'},
        '%s_new_pieces' % f['id']: pieces,
        '%s_old_cards' % f['id']: {},
        'ouak_slots': [{'slot': s} for s in slots],
        'ouak_groups': groups,
    }


def rig_spec(f):
    J = f['joints']
    joints = {}
    for j in J:
        w = u2w(j['model'])
        joints[j['name']] = {'head': [round(float(x), 6) for x in w],
                             'parent': J[j['parent']]['name'] if j['parent'] >= 0 else None,
                             'source': 'rebuilt by Tools/import_llcm.py from the SKEL chunk'}
    return {'units': 'Blender world metres, Z up, -Y forward', f['style'] + '_style': {
        'note': "%s's joints as the .llcm file has them" % f['name'], 'joints': joints}}


def main():
    a = sys.argv[sys.argv.index('--') + 1:]
    f = parse(a[0])
    out_dir = os.path.abspath(a[1])
    os.makedirs(out_dir, exist_ok=True)
    blend_name = a[2] if len(a) > 2 else f['blend']
    build(f, out_dir, blend_name)
    with open(os.path.join(out_dir, 'face_map.json'), 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(face_map(f), fh, indent=1)
    with open(os.path.join(out_dir, 'rig_spec.json'), 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(rig_spec(f), fh, indent=1)
    print('IMPORT DONE %s %s: joints %d, meshes %d, textures %d' % (f['id'], f['style'], len(f['joints']),
                                                                    len(f['meshes']), len(f['textures'])))


try:
    main()
except ImportError_ as e:
    print('IMPORT FAILED: %s' % e)
    sys.exit(1)
