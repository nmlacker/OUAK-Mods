"""Reads .llcm files back and checks every value in them (format: Cousins/FORMAT.md). System Python 3, numpy, PIL.

    python -P check_llcm.py --blender <blender.exe> --slots <expressions json> --work <dir> [file.llcm ...]
    python -P check_llcm.py --blender <blender.exe> --slots <expressions json> --work <dir> --selftest
    add --dump <dir> to also write each file's meshes (pos__<name>, Blender metres) and PNGs for no_game_data.py

--slots is the OUAK Prince's face component as dumped from the game (expressions_OUJI01_D.json: the 23 faceObject
names in order, the slot offsets and the 28 face groups). It stays in the local reference folder, outside Mod/.
--blender is Blender 5.2: the checker writes its own small dump script into --work and runs it headless on each
.blend. That script reads the file raw (Blender world metres, corner normals, UVs, weights by bone name, hide flags,
image files) and never saves; the .blend's SHA-256 is compared before and after. With no files given, it checks
every style listed in Cousins/cousins.json.

Nothing here comes from export_llcm.py. The parser follows FORMAT.md; the axis change is done from Blender world,
where the exporter works from armature space; the face tables, materials and shadows are derived again from
face_map.json, the slot dump and the design. Every check runs and prints PASS or FAIL; the exit code is 1 if any
failed. --selftest writes broken copies (one triangle flipped, one joint moved 0.02 cm, both) into --work and
passes only if the checker catches each of them and passes the real files.
"""
import sys
import os
import io
import re
import json
import zlib
import struct
import shutil
import hashlib
import argparse
import tempfile
import subprocess
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
COUSINS = os.path.join(os.path.dirname(HERE), 'Cousins')
LLC = os.path.dirname(os.path.dirname(HERE))

CHUNK_ORDER = ['STRS', 'INFO', 'SKEL', 'FOLW', 'TEXR+', 'MATL', 'MESH+', 'FACE']
ROLES = ('face', 'eye', 'mouth', 'overlay')
TOL_CM = 1e-4          # positions and joints, cm
TOL_N = 1e-5           # unit normals
TOL_UV = 1e-6
# The design's waist offsets (FOLW), cm.
DESIGN_FOLLOW = {'new': (0.0, 0.0, 0.0), 'old': (0.0, 7.415, 0.0)}
# The design's group-26 override for the new style: she sings where the Prince whistles.
DESIGN_GROUP26 = {'show': {'eye_sing_m', 'mouth_05_m'}, 'hide': {'eye_01_m', 'eye_03_m', 'mouth_10_m'}}
# Counts and volumes the research measured on the .blend files (burda_data_and_units.md), as an outside cross-check.
RESEARCH = {
    ('burda', 'new'): dict(meshes=32, tris=5941, verts=4195, hidden=21, volumes={'head_m': 59313, 'body_m': 24387}),
    ('burda', 'old'): dict(meshes=17, tris=587, verts=974, hidden=10, volumes={'head': 45162, 'body': 18462}),
}

RAW_DUMP = r'''
import bpy, sys, json
out = sys.argv[sys.argv.index('--') + 1]
def rows(M):
    return [list(r) for r in M]
res = {'rigs': [], 'meshes': []}
for o in bpy.data.objects:
    if o.type == 'ARMATURE':
        res['rigs'].append(dict(name=o.name, matrix_world=rows(o.matrix_world), bones=[dict(
            name=b.name, parent=b.parent.name if b.parent else None, head_local=list(b.head_local),
            matrix_local=rows(b.matrix_local)) for b in o.data.bones]))
for o in bpy.data.objects:
    if o.type != 'MESH':
        continue
    me = o.data
    me.calc_loop_triangles()
    names = {g.index: g.name for g in o.vertex_groups}
    mat = o.material_slots[0].material if len(o.material_slots) else None
    images, alpha = [], False
    if mat is not None and mat.node_tree is not None:
        for n in mat.node_tree.nodes:
            if n.type == 'TEX_IMAGE' and n.image:
                images.append(bpy.path.abspath(n.image.filepath, library=n.image.library))
        alpha = any(l.from_node.type == 'TEX_IMAGE' and l.from_socket.name == 'Alpha' and l.to_socket.name == 'Alpha'
                    for l in mat.node_tree.links)
    res['meshes'].append(dict(
        name=o.name, matrix_world=rows(o.matrix_world), hide_viewport=o.hide_viewport, hide_render=o.hide_render,
        material_slots=len(o.material_slots), material=mat.name if mat else None, images=images, alpha_linked=alpha,
        co=[list(v.co) for v in me.vertices],
        tri_verts=[list(t.vertices) for t in me.loop_triangles],
        tri_loops=[list(t.loops) for t in me.loop_triangles],
        corner_normals=[list(c.vector) for c in me.corner_normals],
        uv=[list(d.uv) for d in me.uv_layers.active.data] if me.uv_layers.active else [],
        weights=[[[names[g.group], g.weight] for g in v.groups] for v in me.vertices]))
with open(out, 'w') as f:
    json.dump(res, f)
bpy.ops.wm.quit_blender()
'''


# ---------------------------------------------------------------- reading the file

class FormatError(Exception):
    pass


class Reader:
    def __init__(self, data, base):
        self.d, self.p, self.base = data, 0, base

    def take(self, fmt):
        fmt = '<' + fmt
        n = struct.calcsize(fmt)
        if self.p + n > len(self.d):
            raise FormatError('read past the end of a chunk')
        v = struct.unpack_from(fmt, self.d, self.p)
        self.p += n
        return v

    def one(self, fmt):
        return self.take(fmt)[0]

    def array(self, dtype, count):
        dt = np.dtype(dtype)
        n = dt.itemsize * count
        if self.p + n > len(self.d):
            raise FormatError('array runs past the end of a chunk')
        a = np.frombuffer(self.d, dt, count, self.p).copy()
        self.p += n
        return a

    def at(self):
        return self.base + self.p


def parse(data):
    """Strict reader. Returns a dict; raises FormatError on anything FORMAT.md does not allow."""
    f = {'size': len(data)}
    if len(data) < 16 or data[:4] != b'LLCM':
        raise FormatError('no LLCM header')
    f['version'], f['flags'], f['chunk_count'], f['file_length'] = struct.unpack_from('<HHII', data, 4)
    chunks, p = [], 16
    while p < len(data):
        if p + 8 > len(data):
            raise FormatError('a chunk header runs past the end of the file')
        tag = data[p:p + 4].decode('ascii', 'replace')
        n = struct.unpack_from('<I', data, p + 4)[0]
        start, end = p + 8, p + 8 + n
        pad = -n % 4
        if end + pad > len(data):
            raise FormatError('chunk %s runs past the end of the file' % tag)
        chunks.append(dict(tag=tag, start=start, length=n, pad_zero=data[end:end + pad] == b'\0' * pad))
        p = end + pad
    f['chunks'] = chunks
    f['trailing'] = len(data) - p
    st = {}

    def payload(c):
        return Reader(data[c['start']:c['start'] + c['length']], c['start'])

    def done(r, c):
        if r.p != c['length']:
            raise FormatError('chunk %s has %d bytes left over' % (c['tag'], c['length'] - r.p))

    f['textures'], f['meshes'] = [], []
    for c in chunks:
        r = payload(c)
        t = c['tag']
        if t == 'STRS':
            n = r.one('H')
            strs = []
            for _ in range(n):
                k = r.one('H')
                strs.append(r.take('%ds' % k)[0].decode('utf-8'))
            f['strings'] = strs
        elif t == 'INFO':
            f['info'] = dict(zip(('id', 'name', 'style', 'marker', 'source'), r.take('5H')))
        elif t == 'SKEL':
            n = r.one('B')
            joints = []
            for _ in range(n):
                at = r.at()
                name, parent, follow = r.take('HbB')
                local = r.take('3f')
                rot = r.take('4f')
                model = r.take('3f')
                joints.append(dict(name=name, parent=parent, follow=follow, local=np.array(local), rot=rot,
                                   model=np.array(model), at_local=at + 4, at_model=at + 32))
            f['joints'] = joints
        elif t == 'FOLW':
            n = r.one('B')
            f['follow'] = [dict(zip(('joint', 'mode'), r.take('BB')), offset=np.array(r.take('3f'))) for _ in range(n)]
        elif t == 'TEXR':
            name, w, h, enc, flags, filt, wrap, n = r.take('3H4BI')
            f['textures'].append(dict(name=name, w=w, h=h, encoding=enc, flags=flags, filter=filt, wrap=wrap,
                                      bytes=r.take('%ds' % n)[0]))
        elif t == 'MATL':
            n = r.one('B')
            f['materials'] = [dict(zip(('name', 'texture', 'alpha'), r.take('HBB'))) for _ in range(n)]
        elif t == 'MESH':
            m = dict(zip(('name', 'role', 'flags', 'slot', 'slot_name', 'card', 'material', 'template', 'root', 'nb'),
                         r.take('HBBBHBBHBB')))
            m['palette'] = list(r.take('%dB' % m['nb']))
            m['bmin'], m['bmax'] = np.array(r.take('3f')), np.array(r.take('3f'))
            nv, nt = r.take('HH')
            m['pos'] = r.array('<f4', nv * 3).reshape(nv, 3)
            m['nrm'] = r.array('<f4', nv * 3).reshape(nv, 3)
            m['uv'] = r.array('<f4', nv * 2).reshape(nv, 2)
            m['bidx'] = r.array('u1', nv * 4).reshape(nv, 4)
            m['bw'] = r.array('<u2', nv * 4).reshape(nv, 4)
            m['at_tris'] = r.at()
            m['tris'] = r.array('<u2', nt * 3).reshape(nt, 3).astype(np.int64)
            f['meshes'].append(m)
        elif t == 'FACE':
            fc = {}
            n = r.one('B')
            fc['slots'] = list(r.take('%dH' % n))
            fc['blink_slot'], fc['open_s'], fc['closed_s'], fc['blink_card'], fc['over_card'], fc['default_card'] = \
                r.take('BffBBB')
            groups = []
            for _ in range(r.one('B')):
                g = dict(game=list(r.take('4B')), pieces=list(r.take('4B')))
                g['card'], g['blinks'] = r.take('BB')
                g['show'] = list(r.take('%dB' % r.one('B')))
                g['hide'] = list(r.take('%dB' % r.one('B')))
                groups.append(g)
            fc['groups'] = groups
            fc['expressions'] = list(r.take('%dB' % r.one('B')))
            fc['rows'] = [r.take('BB') for _ in range(r.one('B'))]
            f['face'] = fc
        else:
            continue     # unknown chunks are skipped, as FORMAT.md tells a reader to
        done(r, c)
    return f


def order_ok(tags):
    """The chunk order FORMAT.md fixes; '+' means one or more in a row."""
    i = 0
    for want in CHUNK_ORDER:
        tag = want.rstrip('+')
        if i >= len(tags) or tags[i] != tag:
            return False
        i += 1
        while want.endswith('+') and i < len(tags) and tags[i] == tag:
            i += 1
    return i == len(tags)


# ---------------------------------------------------------------- PNG, read here without PIL

def png_decode(data):
    """8-bit, non-interlaced PNG -> (w, h, RGBA uint8 array rows from the top, chunk types)."""
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise FormatError('not a PNG')
    p, idat, types, plte, trns = 8, b'', [], None, None
    while p < len(data):
        n, t = struct.unpack_from('>I4s', data, p)
        body = data[p + 8:p + 8 + n]
        if zlib.crc32(t + body) & 0xffffffff != struct.unpack_from('>I', data, p + 8 + n)[0]:
            raise FormatError('PNG chunk %s has a bad CRC' % t)
        t = t.decode('ascii')
        types.append(t)
        if t == 'IHDR':
            w, h, depth, colour, _c, _f, interlace = struct.unpack('>IIBBBBB', body)
        elif t == 'PLTE':
            plte = body
        elif t == 'tRNS':
            trns = body
        elif t == 'IDAT':
            idat += body
        p += 12 + n
        if t == 'IEND':
            break
    if depth != 8 or interlace != 0:
        raise FormatError('this checker reads 8-bit non-interlaced PNGs only')
    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colour]
    raw = bytearray(zlib.decompress(idat))
    stride = w * ch
    if len(raw) != (stride + 1) * h:
        raise FormatError('PNG data has the wrong length')
    out = bytearray(stride * h)
    prev = bytearray(stride)
    for y in range(h):
        ft = raw[y * (stride + 1)]
        line = raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)]
        for x in range(stride):
            a = line[x - ch] if x >= ch else 0
            b = prev[x]
            c = prev[x - ch] if x >= ch else 0
            if ft == 1:
                line[x] = (line[x] + a) & 255
            elif ft == 2:
                line[x] = (line[x] + b) & 255
            elif ft == 3:
                line[x] = (line[x] + ((a + b) >> 1)) & 255
            elif ft == 4:
                pp = a + b - c
                pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
            elif ft != 0:
                raise FormatError('PNG filter %d' % ft)
        out[y * stride:(y + 1) * stride] = line
        prev = line
    px = np.frombuffer(bytes(out), np.uint8).reshape(h, w, ch)
    if colour == 3:
        pal = np.frombuffer(plte, np.uint8).reshape(-1, 3)
        alpha = np.full(len(pal), 255, np.uint8)
        if trns:
            alpha[:len(trns)] = np.frombuffer(trns, np.uint8)
        rgba = np.concatenate([pal[px[..., 0]], alpha[px[..., 0]][..., None]], -1)
    elif colour == 0:
        rgba = np.concatenate([np.repeat(px, 3, -1), np.full((h, w, 1), 255, np.uint8)], -1)
    elif colour == 4:
        rgba = np.concatenate([np.repeat(px[..., :1], 3, -1), px[..., 1:]], -1)
    elif colour == 2:
        rgba = np.concatenate([px, np.full((h, w, 1), 255, np.uint8)], -1)
    else:
        rgba = px
    return w, h, rgba, types, colour


# ---------------------------------------------------------------- Blender, raw

def sha256(path):
    with open(path, 'rb') as fh:
        return hashlib.sha256(fh.read()).hexdigest()


_raw_cache = {}


def blender_raw(blend, blender, work):
    if blend in _raw_cache:
        return _raw_cache[blend]
    script = os.path.join(work, 'llcm_rawdump.py')
    with open(script, 'w') as fh:
        fh.write(RAW_DUMP)
    out = os.path.join(work, 'raw_%s.json' % os.path.splitext(os.path.basename(blend))[0])
    before = sha256(blend)
    proc = subprocess.run([blender, '-b', '--factory-startup', blend, '--python-exit-code', '1', '--python', script,
                           '--', out], capture_output=True, text=True, errors='replace')
    after = sha256(blend)
    if proc.returncode != 0 or not os.path.exists(out):
        raise FormatError('Blender dump failed (exit %d): %s' % (proc.returncode, proc.stdout[-400:]))
    with open(out) as fh:
        raw = json.load(fh)
    raw['hash_before'], raw['hash_after'] = before, after
    _raw_cache[blend] = raw
    return raw


def w2u(p):
    """Blender world metres -> Unity cm under the game's `model` node: (x, y, z) -> (-100x, 100z, -100y)."""
    p = np.asarray(p, float)
    return np.stack([-100.0 * p[..., 0], 100.0 * p[..., 2], -100.0 * p[..., 1]], -1)


def w2u_dir(n):
    n = np.asarray(n, float)
    return np.stack([-n[..., 0], n[..., 2], -n[..., 1]], -1)


# ---------------------------------------------------------------- the checks

class Results:
    def __init__(self, quiet=False):
        self.n_pass = self.n_fail = 0
        self.failed = []
        self.quiet = quiet

    def __call__(self, name, ok, detail=''):
        ok = bool(ok)
        if ok:
            self.n_pass += 1
        else:
            self.n_fail += 1
            self.failed.append(name)
        if not self.quiet or not ok:
            print('  %s %s%s' % ('PASS' if ok else 'FAIL', name, (': ' + detail) if detail else ''))


def shells(pos, tris):
    """Weld vertices by exact position, split into connected shells.
    -> list of (closed, consistent, signed volume, triangle count). A shell is consistent when no edge is used twice
    in the same direction (its triangles agree on which side is out), closed when every directed edge is used once
    and its reverse once."""
    keys = {}
    wid = np.array([keys.setdefault(p.tobytes(), len(keys)) for p in pos])
    T = wid[tris]
    parent = list(range(len(keys)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    for a, b, c in T:
        ra, rb, rc = find(a), find(b), find(c)
        parent[rb] = ra
        parent[find(rc)] = ra
    groups = {}
    for i, t in enumerate(T):
        groups.setdefault(find(t[0]), []).append(i)
    out = []
    P = pos.astype(float)
    for idx in groups.values():
        edges = {}
        for a, b, c in T[idx]:
            for e in ((a, b), (b, c), (c, a)):
                edges[e] = edges.get(e, 0) + 1
        consistent = all(n == 1 for n in edges.values())
        closed = consistent and all(edges.get((e[1], e[0])) == 1 for e in edges)
        A, B, C = P[tris[idx, 0]], P[tris[idx, 1]], P[tris[idx, 2]]
        vol = float(np.einsum('ij,ij->i', A, np.cross(B, C)).sum() / 6.0)
        out.append((closed, consistent, vol, len(idx)))
    return out


def check_file(path, args, R, cj):
    with open(path, 'rb') as fh:
        data = fh.read()
    print('CHECK %s (%d bytes, sha256 %s)' % (os.path.basename(path), len(data), hashlib.sha256(data).hexdigest()))
    try:
        f = parse(data)
    except (FormatError, struct.error, UnicodeDecodeError, KeyError) as e:
        R('file parses', False, str(e))
        return None
    R('file parses', True)
    S = f.get('strings', [])

    def s(i):
        return None if i == 0xFFFF else S[i] if i < len(S) else '<bad string %d>' % i

    # ---- structure
    tags = [c['tag'] for c in f['chunks']]
    R('header: version 1', f['version'] == 1, 'version %d' % f['version'])
    R('header: chunk count and file length', f['chunk_count'] == len(tags) and f['file_length'] == f['size'],
      'header says %d chunks / %d bytes, file has %d / %d' % (f['chunk_count'], f['file_length'], len(tags), f['size']))
    R('chunks in FORMAT.md order', order_ok(tags), ' '.join(tags[:6]) + ' ...')
    R('chunk padding is zero, nothing after the last chunk', all(c['pad_zero'] for c in f['chunks']) and f['trailing'] == 0)
    used = [f['info'][k] for k in f['info']] + [j['name'] for j in f['joints']] + [t['name'] for t in f['textures']] \
        + [m['name'] for m in f['materials']] + [m[k] for m in f['meshes'] for k in ('name', 'slot_name', 'template')] \
        + f['face']['slots']
    R('string indices in range', all(i == 0xFFFF or i < len(S) for i in used), '%d strings' % len(S))
    R('strings are unique', len(set(S)) == len(S))

    # ---- INFO and the cousin entry
    info = {k: s(v) for k, v in f['info'].items()}
    entry = next((e for e in cj['cousins'] if e['id'] == info['id']), None)
    style = info['style']
    R('INFO names a cousin and style in cousins.json', entry is not None and style in entry.get('styles', {}),
      '%s / %s' % (info['id'], style))
    if entry is None or style not in entry['styles']:
        return f
    build = entry['build']
    blend = os.path.normpath(os.path.join(LLC, build['blend'][style]))
    R('INFO name, marker and source', info['name'] == entry['name'] and info['marker'] == 'llc_%s_%s' % (
        entry['name'], style) and info['source'] == os.path.basename(blend),
      '%s, %s, %s' % (info['name'], info['marker'], info['source']))
    with open(os.path.join(LLC, build['face_map']), encoding='utf-8') as fh:
        fm = json.load(fh)
    with open(os.path.join(LLC, build['rig_spec']), encoding='utf-8') as fh:
        spec_all = json.load(fh)
    spec = spec_all[build['rig_spec_block'][style]]['joints']
    cards_style = bool(f['flags'] & 1)
    R('header flags: exactly one of cards (old) and slots (new)', f['flags'] in (1, 2), 'flags %d' % f['flags'])

    raw = None
    try:
        raw = blender_raw(blend, args.blender, args.work) if args.blender else None
    except (FormatError, OSError) as e:
        R('Blender dump of %s' % os.path.basename(blend), False, str(e))
    if raw is None and not args.blender:
        R('Blender dump of %s' % os.path.basename(blend), False, '--blender not given')
    if raw is not None:
        R('the .blend is unchanged by the dump', raw['hash_before'] == raw['hash_after'], raw['hash_after'])

    # ---- skeleton
    J = f['joints']
    jn = [s(j['name']) for j in J]
    R('23 joints with the rig spec names', len(J) == 23 and sorted(jn) == sorted(spec), '%d joints' % len(J))
    R('parents before children, as in the rig spec', all(
        -1 <= j['parent'] < i and (s(J[j['parent']]['name']) if j['parent'] >= 0 else None) == spec[jn[i]]['parent']
        for i, j in enumerate(J)))
    want = {n: w2u(spec[n]['head']) for n in spec}
    d_spec = max(float(np.abs(J[i]['model'] - want[n]).max()) for i, n in enumerate(jn) if n in want)
    R('joint model positions equal the converted rig spec (%g cm)' % TOL_CM, d_spec <= TOL_CM, 'largest difference %.2e cm' % d_spec)
    acc = []
    for j in J:
        acc.append(j['local'].astype(float) + (acc[j['parent']] if j['parent'] >= 0 else 0.0))
    d_acc = max(float(np.abs(a - j['model']).max()) for a, j in zip(acc, J))
    R('local positions add up to the model positions', d_acc <= TOL_CM, 'largest difference %.2e cm' % d_acc)
    d_acc_spec = max(float(np.abs(a - want[n]).max()) for a, n in zip(acc, jn) if n in want)
    R('local positions add up to the converted rig spec', d_acc_spec <= TOL_CM, 'largest difference %.2e cm' % d_acc_spec)
    R('rest rotations are identity (0, 0, 0, 1)', all(j['rot'] == (0.0, 0.0, 0.0, 1.0) for j in J))
    waist = jn.index('JNT_waist') if 'JNT_waist' in jn else -1
    R('follow bits: rotation and scale on all, position on JNT_waist only',
      all(j['follow'] == (7 if i == waist else 5) for i, j in enumerate(J)))
    if raw is not None:
        rigs = raw['rigs']
        ok_rig = len(rigs) == 1
        if ok_rig:
            rx90 = np.array([[1, 0, 0, 0], [0, 0, -1, 0], [0, 1, 0, 0], [0, 0, 0, 1]], float)
            MW = np.array(rigs[0]['matrix_world'])
            ok_rig = float(np.abs(MW - rx90).max()) < 1e-6
        R('Blender: one rig, turned +90 about X (so model space is Blender world converted)', ok_rig)
        if ok_rig:
            bones = {b['name']: b for b in rigs[0]['bones']}
            d_bl = max(float(np.abs(J[i]['model'] - w2u((MW @ np.r_[bones[n]['head_local'], 1.0])[:3])).max())
                       for i, n in enumerate(jn))
            R('joint model positions equal the Blender bones converted', d_bl <= TOL_CM, 'largest difference %.2e cm' % d_bl)
            d_rot = max(float(np.abs(np.array(b['matrix_local'])[:3, :3] - np.eye(3)).max()) for b in bones.values())
            R('Blender bones rest at identity', d_rot < 1e-5, 'largest element off by %.1e' % d_rot)

    # ---- FOLW
    prince_waist = w2u(spec_all['new_style']['joints']['JNT_waist']['head'])
    fl = f['follow']
    ok = len(fl) == 1 and fl[0]['joint'] == waist and fl[0]['mode'] == 1
    d_off = float(np.abs(fl[0]['offset'] - (acc[waist] - prince_waist)).max()) if ok else 1e9
    d_des = float(np.abs(fl[0]['offset'] - np.array(DESIGN_FOLLOW[style])).max()) if ok else 1e9
    R('FOLW: one entry, JNT_waist, mode 1', ok)
    R('FOLW offset = her waist - the Prince\'s waist, and equals the design', d_off <= TOL_CM and d_des <= TOL_CM,
      'offset %s, differences %.1e / %.1e cm' % (np.round(fl[0]['offset'], 4).tolist() if fl else None, d_off, d_des))

    # ---- textures and materials
    rmeshes = {m['name']: m for m in raw['meshes']} if raw is not None else {}
    for k, t in enumerate(f['textures']):
        name = s(t['name'])
        try:
            w, h, rgba, types, colour = png_decode(t['bytes'])
            pil = np.array(Image.open(io.BytesIO(t['bytes'])).convert('RGBA'))
            ok = (w, h) == (t['w'], t['h']) == (pil.shape[1], pil.shape[0]) and np.array_equal(rgba, pil)
            R('texture %s decodes to %dx%d (own decoder = PIL, pixel for pixel)' % (name, t['w'], t['h']), ok,
              'decoded %dx%d' % (w, h))
            R('texture %s: PNG, no text chunks, flags/filter/wrap' % name, t['encoding'] == 0
              and not {'tEXt', 'zTXt', 'iTXt'} & set(types) and t['flags'] == ((1 if colour in (4, 6) else 0) | 2)
              and t['filter'] == 1 and t['wrap'] == 1, 'chunks %s' % sorted(set(types)))
        except (FormatError, OSError, KeyError, ValueError, zlib.error) as e:
            R('texture %s decodes' % name, False, str(e))
        srcs = {os.path.normpath(p) for m in rmeshes.values() for p in m['images']
                if os.path.basename(p) == name}
        same = len(srcs) == 1 and open(next(iter(srcs)), 'rb').read() == t['bytes']
        R('texture %s is the .blend\'s image file, byte for byte' % name, same, '%d source(s)' % len(srcs))
    mats = f['materials']
    R('materials name a texture in range', all(m['texture'] < len(f['textures']) for m in mats), '%d materials' % len(mats))
    for m in mats:
        users = [r for r in rmeshes.values() if r['material'] == s(m['name'])]
        tex = s(f['textures'][m['texture']]['name']) if m['texture'] < len(f['textures']) else None
        R('material %s: its texture and alpha flag match Blender' % s(m['name']), users and all(
            [os.path.basename(p) for p in u['images']] == [tex] and u['alpha_linked'] == bool(m['alpha'])
            for u in users))

    # ---- meshes
    slot_doc = None
    if args.slots:
        with open(args.slots, encoding='utf-8') as fh:
            slot_doc = json.load(fh)
    else:
        R('slot dump given (--slots)', False, 'needed for the slot and face checks')
    M = f['meshes']
    mn = [s(m['name']) for m in M]
    R('mesh names are unique and are the .blend\'s meshes', len(set(mn)) == len(mn) and (
        raw is None or sorted(mn) == sorted(rmeshes)), '%d meshes' % len(M))
    slot_names = slot_doc['faceObject'] if slot_doc else [s(i) for i in f['face']['slots']]
    offs = slot_doc['offsets'] if slot_doc else {'eye': 0, 'face': 5, 'mouth': 8, 'parts': 19}
    card_no = {name: int(k) for k, name in fm['burda_old_cards'].items()}
    extras = {n for n in fm['burda_new_pieces'] if not (n.endswith('_m') and n[:-2] in slot_names)}
    group0 = sorted(fm['ouak_groups'], key=lambda g: g['group'])[0]
    visible_start = ({group0['old_style_card']} if cards_style else
                     {group0['new_style'][r] for r in ROLES if group0['new_style'][r]})
    totals = dict(tris=0, verts=0, hidden=0)
    volumes, closed_parts = {}, []
    for mi, m in enumerate(M):
        name = mn[mi]
        tag = 'mesh %s' % name
        n, T = len(m['pos']), m['tris']
        totals['tris'] += len(T)
        totals['verts'] += n
        totals['hidden'] += m['flags'] & 1
        # what the design says this piece is
        if name.endswith('_m') and name[:-2] in slot_names:
            sl = slot_names.index(name[:-2])
            role = 1 if offs['face'] <= sl < offs['mouth'] else 3 if sl >= offs['parts'] else 2
            want_meta = (role, sl, name[:-2], 0, name if role != 1 else 'body_m')
        elif name in extras:
            want_meta = (4, 0xFF, None, 0, 'mouth_01_m' if name.startswith('mouth') else 'eye_01_m')
        elif name in card_no:
            want_meta = (5, 0xFF, None, card_no[name], 'body_m')
        else:
            want_meta = (0, 0xFF, None, 0, 'body_m')
        got_meta = (m['role'], m['slot'], s(m['slot_name']), m['card'], s(m['template']))
        R(tag + ': role, slot, card, template', got_meta == want_meta, 'got %s, want %s' % (got_meta, want_meta))
        R(tag + ': casts shadows only if a body part or face plate', bool(m['flags'] & 2) == (want_meta[0] in (0, 1)))
        R(tag + ': starts hidden unless body or the first face', bool(m['flags'] & 1) == (
            want_meta[0] != 0 and name not in visible_start))
        R(tag + ': material in range, root bone JNT_waist', m['material'] < len(mats) and m['root'] == waist)
        pal = m['palette']
        R(tag + ': palette ascending, joints in range', pal == sorted(set(pal)) and all(p < len(J) for p in pal)
          and 0 < len(pal) == m['nb'])
        bw, bi = m['bw'].astype(np.int64), m['bidx'].astype(np.int64)
        srt = np.all(bw[:, :-1] >= bw[:, 1:])
        R(tag + ': weights sum to 65535, largest first, unused slots index 0', np.all(bw.sum(1) == 65535) and srt
          and np.all(bi[bw == 0] == 0) and np.all(bw[:, 0] > 0))
        R(tag + ': palette indices in range, every palette entry used', np.all(bi < len(pal)) and
          set(bi[bw > 0].tolist()) == set(range(len(pal))))
        R(tag + ': triangle indices in range, every vertex used', T.size and T.max() < n and
          len(np.unique(T)) == n)
        R(tag + ': bounds are the positions\' min and max', np.array_equal(m['bmin'], m['pos'].min(0)) and
          np.array_equal(m['bmax'], m['pos'].max(0)))
        R(tag + ': normals are unit length', float(np.abs(np.linalg.norm(m['nrm'], axis=1) - 1).max()) < TOL_N)
        P = m['pos'].astype(float)
        A, B, C = P[T[:, 0]], P[T[:, 1]], P[T[:, 2]]
        cr = np.cross(B - A, C - A)
        nsum = m['nrm'][T[:, 0]].astype(float) + m['nrm'][T[:, 1]] + m['nrm'][T[:, 2]]
        dots = np.einsum('ij,ij->i', cr, nsum)
        bad = int((dots <= 0).sum()) + int((np.linalg.norm(cr, axis=1) == 0).sum())
        R(tag + ': winding: every triangle\'s cross product agrees with its normals', bad == 0,
          '%d of %d triangles disagree' % (bad, len(T)))
        R(tag + ': flags use bits 0 and 1 only', m['flags'] & ~3 == 0)
        sh = shells(m['pos'], T)
        R(tag + ': no edge is used twice the same way (triangles agree on the outside)', all(k for _, k, _, _ in sh))
        shut = [(v, nt) for c, _, v, nt in sh if c]
        if shut:
            R(tag + ': every closed part has positive signed volume', all(v > 0 for v, _ in shut),
              '%d of %d shells closed, volumes %s cm3' % (len(shut), len(sh), ', '.join('%.1f' % v for v, _ in shut[:4])
                                                          + (' ...' if len(shut) > 4 else '')))
        closed_parts.append((name, len(shut), len(sh)))
        volumes[name] = (sum(v for _, _, v, _ in sh), max(sh, key=lambda x: x[3])[0])
        # against the raw .blend
        rm = rmeshes.get(name)
        if raw is None:
            continue
        if rm is None:
            R(tag + ': found in the .blend', False)
            continue
        R(tag + ': hidden flag equals the .blend', bool(m['flags'] & 1) == (rm['hide_viewport'] or rm['hide_render']))
        R(tag + ': material is the .blend\'s', rm['material_slots'] == 1 and m['material'] < len(mats)
          and s(mats[m['material']]['name']) == rm['material'])
        V, Lp = np.array(rm['tri_verts']), np.array(rm['tri_loops'])
        if len(V) != len(T):
            R(tag + ': triangle count equals the .blend', False, '%d vs %d' % (len(T), len(V)))
            continue
        MW = np.array(rm['matrix_world'])
        co = np.array(rm['co'], float)
        world = co @ MW[:3, :3].T + MW[:3, 3]
        cn = np.array(rm['corner_normals'], float) @ np.linalg.inv(MW[:3, :3])
        cn /= np.linalg.norm(cn, axis=1, keepdims=True)
        uvb = np.array(rm['uv'], float)
        # Unity triangle (a, b, c) holds Blender corners (0, 2, 1)
        lv = np.stack([T[:, 0], T[:, 1], T[:, 2]], 1).ravel()
        bv = V[:, [0, 2, 1]].ravel()
        bl = Lp[:, [0, 2, 1]].ravel()
        dp = float(np.abs(m['pos'][lv] - w2u(world[bv])).max())
        dn = float(np.abs(m['nrm'][lv] - w2u_dir(cn[bl])).max())
        du = float(np.abs(m['uv'][lv] - uvb[bl]).max())
        R(tag + ': every corner\'s position equals the .blend converted', dp <= TOL_CM, '%.1e cm' % dp)
        R(tag + ': every corner\'s normal equals the .blend converted', dn <= TOL_N, '%.1e' % dn)
        R(tag + ': every corner\'s UV equals the .blend', du <= TOL_UV, '%.1e' % du)
        src, mine = {}, {}
        ok_map = True
        for li, bvi, bli in zip(lv.tolist(), bv.tolist(), bl.tolist()):
            if src.setdefault(li, bvi) != bvi:
                ok_map = False
            mine.setdefault((bvi, m['nrm'][li].tobytes(), m['uv'][li].tobytes()), set()).add(li)
        R(tag + ': vertices split once per (vertex, normal, UV), no more', ok_map and all(
            len(v) == 1 for v in mine.values()) and len(mine) == n, '%d vertices, %d keys' % (n, len(mine)))
        dw, ok_w = 0.0, True
        for li, bvi in src.items():
            ws = {g: w for g, w in rm['weights'][bvi] if w > 0}
            tot = sum(ws.values())
            got = {s(J[pal[bi[li, k]]]['name']): bw[li, k] / 65535.0 for k in range(4) if bw[li, k] > 0}
            if set(got) != set(ws):
                ok_w = False
                continue
            dw = max(dw, max(abs(got[g] - ws[g] / tot) for g in ws))
        R(tag + ': every vertex\'s weights equal the .blend\'s, by bone name', ok_w and dw <= 1.0 / 65535 + 1e-9,
          'largest difference %.2e' % dw)

    # ---- the slot table, cards and hidden set
    fc = f['face']
    fslots = [s(i) for i in fc['slots']]
    if slot_doc:
        R('FACE slot table equals the face component\'s 23 faceObject names, in order',
          fslots == slot_doc['faceObject'] and len(fslots) == 23, ', '.join(fslots[:3]) + ' ...')
    slot_meshes = sorted(m['slot'] for m in M if m['slot'] != 0xFF)
    card_meshes = sorted(m['card'] for m in M if m['card'])
    if cards_style:
        R('old style: no slot pieces, each card 1..11 once', not slot_meshes and card_meshes == sorted(card_no.values()))
    else:
        R('new style: each of the 23 slots has exactly one piece, no cards', slot_meshes == list(range(23))
          and not card_meshes)
    if raw is not None:
        R('hidden set equals the .blend\'s', {n for n, m in zip(mn, M) if m['flags'] & 1} ==
          {n for n, r in rmeshes.items() if r['hide_viewport'] or r['hide_render']})

    # ---- FACE against face_map.json
    groups = sorted(fm['ouak_groups'], key=lambda g: g['group'])
    idx = {n: i for i, n in enumerate(mn)}
    blink = fm['blink']['ouak']
    R('FACE blink: slot, timers', fc['blink_slot'] == slot_names.index(blink['blink_eye']) and
      abs(fc['open_s'] - blink['open_seconds']) < 1e-6 and abs(fc['closed_s'] - blink['closed_seconds']) < 1e-6,
      'slot %d, %.2f s / %.2f s' % (fc['blink_slot'], fc['open_s'], fc['closed_s']))
    ok_groups = len(fc['groups']) == len(groups) == 28
    for g, fg in zip(groups, fc['groups']):
        game = [slot_names.index(g['ouak'][r]) if g['ouak'][r] else 0xFF for r in ROLES]
        if cards_style:
            want = dict(game=game, pieces=[0xFF] * 4, card=card_no[g['old_style_card']], blinks=int(g['blinks']),
                        show=[], hide=[])
        else:
            theirs = {g['ouak'][r] + '_m' for r in ROLES if g['ouak'][r]}
            hers = {g['new_style'][r] for r in ROLES if g['new_style'][r]}
            hide = theirs - hers
            if g['new_style']['eye'] != (g['ouak']['eye'] + '_m' if g['ouak']['eye'] else None):
                hide.add(blink['blink_eye'] + '_m')
            want = dict(game=game, pieces=[idx.get(g['new_style'][r], -1) if g['new_style'][r] else 0xFF for r in ROLES],
                        card=0, blinks=int(g['blinks']), show=sorted(idx[x] for x in hers - theirs),
                        hide=sorted(idx[x] for x in hide))
        got = {k: fg[k] for k in want}
        if got != want:
            ok_groups = False
            print('    group %d: got %s, want %s' % (g['group'], got, want))
    R('FACE groups equal face_map.json (slots, pieces, cards, blinks, overrides)', ok_groups, '%d groups' % len(fc['groups']))
    if not cards_style and len(fc['groups']) > 26:
        g26 = fc['groups'][26]
        R('FACE group 26 is the design\'s singing override', {mn[i] for i in g26['show']} == DESIGN_GROUP26['show']
          and {mn[i] for i in g26['hide']} == DESIGN_GROUP26['hide'],
          'show %s, hide %s' % (sorted(mn[i] for i in g26['show']), sorted(mn[i] for i in g26['hide'])))
        R('FACE: only group 26 has an override', all(not (g['show'] or g['hide']) for k, g in enumerate(fc['groups'])
                                                    if k != 26))
    if cards_style:
        names = re.findall(r'face_\d+_[a-z_]+', fm['blink']['old_style'])
        R('FACE blink cards and default card', (fc['blink_card'], fc['over_card'], fc['default_card']) == (
            card_no.get(names[0]), card_no.get(names[1]), card_no[group0['old_style_card']]),
          'blink %d over %d, default %d' % (fc['blink_card'], fc['over_card'], fc['default_card']))
        ex = [card_no[e['card']] for e in sorted(fm['forever_expressions'], key=lambda e: e['expression'])]
        R('FACE Forever expression cards equal face_map.json', fc['expressions'] == ex, str(fc['expressions']))
        rows = sorted((r['row'], card_no[r['card']]) for r in fm['old_style_rows'])
        R('FACE row cards equal face_map.json', [tuple(r) for r in fc['rows']] == rows, '%d rows' % len(fc['rows']))
    else:
        R('FACE: no cards, expressions or rows in the new style', (fc['blink_card'], fc['over_card'],
                                                                     fc['default_card']) == (0, 0, 0)
          and not fc['expressions'] and not fc['rows'])
    if slot_doc:
        sg = sorted(slot_doc['groups'], key=lambda g: g['index'])
        same = len(sg) == len(groups) and all(
            [a.get('face'), a.get('eye'), a.get('mouth'), a.get('parts')] == [b['ouak'][r] for r in ROLES]
            for a, b in zip(sg, groups))
        R('face_map.json\'s OUAK columns equal the game\'s face groups', same)
        hidden_blink = all(not (a['isBlink'] and not b['blinks']) or (
            cards_style or idx[blink['blink_eye'] + '_m'] in fg['hide']) for a, b, fg in zip(sg, groups, fc['groups']))
        R('where the game blinks and she does not, her blink eye is forced off', hidden_blink)
        R('she blinks only where the game blinks', all(a['isBlink'] or not b['blinks'] for a, b in zip(sg, groups)))
        if cards_style:
            R('old style: groups showing the blink-over card blink as face_map says', all(
                not (a['isBlink'] and fg['card'] == fc['over_card']) or b['blinks']
                for a, b, fg in zip(sg, groups, fc['groups'])))

    # ---- outside cross-check against the research numbers
    rs = RESEARCH.get((info['id'], style))
    if rs:
        got = dict(meshes=len(M), tris=totals['tris'], verts=totals['verts'], hidden=totals['hidden'])
        R('counts equal the research (meshes, triangles, vertices, hidden)', all(got[k] == rs[k] for k in got),
          str(got))
        R('head and body: whole-mesh signed volumes equal the research within 1 cm3', all(
            k in volumes and abs(volumes[k][0] - v) <= 1.0 for k, v in rs['volumes'].items()),
          ', '.join('%s %.1f' % (k, volumes[k][0] if k in volumes else float('nan')) for k in rs['volumes']))
        head = [k for k in rs['volumes'] if k.startswith('head')][0]
        R("the head's largest shell is closed", head in volumes and volumes[head][1])
    nclosed = sum(c for _, c, _ in closed_parts)
    R('closed parts exist to test the volume check on', nclosed > 0, '%d closed shells in %d meshes' % (
        nclosed, sum(1 for _, c, _ in closed_parts if c)))
    if not R.quiet:
        print('  INFO closed shells / shells per mesh: ' + ', '.join('%s %d/%d' % p for p in closed_parts))
    if args.dump:
        d = os.path.join(args.dump, os.path.splitext(os.path.basename(path))[0])
        os.makedirs(os.path.join(d, 'textures'), exist_ok=True)
        np.savez(os.path.join(d, 'meshes.npz'), **{'pos__' + n: (m['pos'][:, [0, 2, 1]] * [-0.01, -0.01, 0.01])
                                                   .astype(np.float32) for n, m in zip(mn, M)})
        for t in f['textures']:
            with open(os.path.join(d, 'textures', s(t['name'])), 'wb') as fh:
                fh.write(t['bytes'])
    return f


def run(paths, args, quiet=False):
    with open(os.path.join(COUSINS, 'cousins.json'), encoding='utf-8') as fh:
        cj = json.load(fh)
    total_fail, total_pass, failed = 0, 0, []
    for p in paths:
        R = Results(quiet)
        try:
            check_file(p, args, R, cj)
        except Exception as e:          # a crash is a failure, never a skip
            R('checker ran to the end', False, '%s: %s' % (type(e).__name__, e))
        print('RESULT %s pass=%d fail=%d' % (os.path.basename(p), R.n_pass, R.n_fail))
        total_fail += R.n_fail
        total_pass += R.n_pass
        failed += R.failed
    return total_pass, total_fail, failed


def default_paths():
    with open(os.path.join(COUSINS, 'cousins.json'), encoding='utf-8') as fh:
        cj = json.load(fh)
    return [os.path.join(COUSINS, p) for e in cj['cousins'] for p in e['styles'].values()]


def selftest(args):
    """Broken copies must fail, by the checks that should catch them."""
    print('SELFTEST originals')
    _, f0, _ = run(default_paths(), args, quiet=True)
    src = next(p for p in default_paths() if p.endswith('_new.llcm'))
    data = bytearray(open(src, 'rb').read())
    f = parse(bytes(data))
    S = f['strings']
    head = next(m for m in f['meshes'] if S[m['name']] in ('head_m', 'head'))
    hand = next(j for j in f['joints'] if S[j['name']] == 'JNT_hand_L')

    def flip(b):       # swap the last two indices of the head's first triangle
        o = head['at_tris']
        b[o + 2:o + 4], b[o + 4:o + 6] = b[o + 4:o + 6], b[o + 2:o + 4]

    def move(b):       # move JNT_hand_L 0.02 cm along X, local and model position alike
        for o in (hand['at_local'], hand['at_model']):
            x = struct.unpack_from('<f', b, o)[0]
            struct.pack_into('<f', b, o, x + 0.02)
    cases = [('flipped_triangle', [flip], ['winding', 'the same way', 'largest shell is closed',
                                           'position equals the .blend']),
             ('wrong_joint', [move], ['converted rig spec', 'Blender bones converted']),
             ('both', [flip, move], ['winding', 'converted rig spec'])]
    ok = f0 == 0
    print('SELFTEST originals: %s' % ('pass' if f0 == 0 else '%d failures' % f0))
    for name, muts, expect in cases:
        b = bytearray(data)
        for mfn in muts:
            mfn(b)
        p = os.path.join(args.work, 'mutant_%s.llcm' % name)
        with open(p, 'wb') as fh:
            fh.write(b)
        print('SELFTEST %s' % name)
        _, nf, failed = run([p], args, quiet=True)
        caught = all(any(e in fl for fl in failed) for e in expect)
        print('SELFTEST %s: %d check(s) failed; expected failures %s caught: %s' % (
            name, nf, expect, 'yes' if caught else 'NO'))
        ok = ok and nf > 0 and caught
    print('SELFTEST %s' % ('PASS' if ok else 'FAIL'))
    return ok


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('files', nargs='*')
    ap.add_argument('--blender', help='Blender 5.2 executable')
    ap.add_argument('--slots', help='the face component dump (expressions_OUJI01_D.json)')
    ap.add_argument('--work', help='scratch folder for the raw dumps and the self-test copies')
    ap.add_argument('--dump', help='also write meshes.npz and PNGs per file here, for no_game_data.py')
    ap.add_argument('--selftest', action='store_true')
    args = ap.parse_args()
    made = None
    if not args.work:
        args.work = made = tempfile.mkdtemp(prefix='llcm_check_')
    os.makedirs(args.work, exist_ok=True)
    try:
        if args.selftest:
            return 0 if selftest(args) else 1
        paths = args.files or default_paths()
        tp, tf, _ = run(paths, args)
        print('ALL pass=%d fail=%d' % (tp, tf))
        return 1 if tf else 0
    finally:
        if made:
            shutil.rmtree(made, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
