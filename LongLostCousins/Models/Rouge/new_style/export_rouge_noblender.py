"""Writes Rouge's new-style .llcm and select-screen icon without Blender, straight from build_rouge_new.py's meshes.

    python3 export_rouge_noblender.py

It follows Mod/Tools/export_llcm.py field for field (Cousins/FORMAT.md), with the .blend's part played by
build_rouge_new.build_parts(): the same vertices, UVs, weights, hidden flags, rig spec, materials and textures the Blender
build puts in the .blend.  Two things differ from a Blender export, both inside what the format allows:
- Normals are smooth across faces of the same group and split between groups, as the build's sharp edges make them in
  Blender; Blender's own corner normals may differ in the last digits.
- Quads are split into triangles as a fan from their first corner; Blender may pick the other diagonal.
The textures are the build's pixels saved by Pillow (8-bit, not interlaced).  The icon is drawn with
preview_rouge_new.py's rasteriser at render_icons.py's camera scale (509.5 px per metre, ground on row 973), from the
front three-quarters, since the front three-quarters reads the puzzle piece best.

Writes Mod/Cousins/Rouge/rouge_new.llcm, rouge_icon_new.png, and the textures into textures/.  Needs numpy and Pillow.
"""
import os, sys, io, re, json, struct, hashlib
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True
import build_rouge_new as B
import preview_rouge_new as PV

LLC = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
COUSINS = os.path.join(LLC, 'Mod', 'Cousins')
OUT = os.path.join(COUSINS, 'Rouge', 'rouge_new.llcm')
ICON = os.path.join(COUSINS, 'Rouge', 'rouge_icon_new.png')
ENTRY = dict(id='rouge', name='Rouge')
STYLE = 'new'

# ---- as in export_llcm.py
FORMAT_VERSION = 1
PRINCE_WAIST = np.array([0.0, 26.345, 0.0])
NONE8, NONE16 = 0xFF, 0xFFFF
ROLE_BODY, ROLE_PLATE, ROLE_FACE, ROLE_OVERLAY, ROLE_EXTRA, ROLE_CARD = range(6)
MESH_HIDDEN, MESH_SHADOWS = 1, 2
FOLLOW_ROT, FOLLOW_POS, FOLLOW_SCALE = 1, 2, 4
FILE_CARDS, FILE_SLOTS = 1, 2
GROUP_ROLES = ('face', 'eye', 'mouth', 'overlay')
FILTER_BILINEAR, WRAP_CLAMP = 1, 1
TEX_ALPHA, TEX_SRGB = 1, 2


def fail(msg):
    raise SystemExit('EXPORT FAIL ' + msg)


def fm_section(fm, suffix):
    keys = [k for k in fm if k.endswith(suffix)]
    if len(keys) > 1:
        fail('face_map has more than one *%s table' % suffix)
    return fm[keys[0]] if keys else {}


class Strings:
    def __init__(self):
        self.items, self.index = [], {}

    def __call__(self, s):
        if s is None:
            return NONE16
        if s not in self.index:
            self.index[s] = len(self.items)
            self.items.append(s)
        return self.index[s]

    def payload(self):
        out = bytearray(struct.pack('<H', len(self.items)))
        for s in self.items:
            b = s.encode('utf-8')
            out += struct.pack('<H', len(b)) + b
        return bytes(out)


def quantize(weights):
    raw = [w * 65535.0 for w in weights]
    q = [int(r) for r in raw]
    order = sorted(range(len(raw)), key=lambda i: (-(raw[i] - q[i]), i))
    for i in order[:65535 - sum(q)]:
        q[i] += 1
    return q


def classify(name, slot_names, extra_names, card_no):
    if name.endswith('_m') and name[:-2] in slot_names:
        slot = slot_names.index(name[:-2])
        if name.startswith('face_'):
            return ROLE_PLATE, slot, 0
        if name.startswith('eye_') or name.startswith('mouth_'):
            return ROLE_FACE, slot, 0
        return ROLE_OVERLAY, slot, 0
    if name in extra_names:
        return ROLE_EXTRA, NONE8, 0
    if name in card_no:
        return ROLE_CARD, NONE8, card_no[name]
    return ROLE_BODY, NONE8, 0


def template_for(role, name, slot_names, slot):
    if role in (ROLE_BODY, ROLE_PLATE, ROLE_CARD):
        return 'body_m'
    if role in (ROLE_FACE, ROLE_OVERLAY):
        return slot_names[slot] + '_m'
    return 'mouth_01_m' if name.startswith('mouth') else 'eye_01_m'


def to_unity(p):
    """Blender world (m, Z up, faces -Y, +X their left) -> Unity cm under `model` (+X right, Y up, Z forward)."""
    p = np.asarray(p, float)
    return 100.0 * np.stack([-p[..., 0], p[..., 2], -p[..., 1]], axis=-1)


def read_joints(spec):
    names = list(spec)
    order = []

    def visit(n):
        order.append(n)
        for c in names:
            if spec[c]['parent'] == n:
                visit(c)
    for n in names:
        if spec[n]['parent'] is None:
            visit(n)
    index = {n: i for i, n in enumerate(order)}
    joints = []
    for n in order:
        par = spec[n]['parent']
        joints.append(dict(name=n, parent=index[par] if par else -1, model=to_unity(spec[n]['head'])))
    for j in joints:
        j['local'] = j['model'] - (joints[j['parent']]['model'] if j['parent'] >= 0 else 0.0)
    return joints, index


def read_mesh(m, wf, jindex):
    P0, UVc, T, grps = PV.triangulate(m)
    Nc = PV.smooth_normals(P0, T, grps)                       # Blender world axes, per corner
    P = to_unity(P0).astype(np.float32) + np.float32(0)
    N = to_unity(Nc) / 100.0
    N = (N / np.linalg.norm(N, axis=-1, keepdims=True)).astype(np.float32) + np.float32(0)
    UVc = UVc.astype(np.float32) + np.float32(0)
    vw = []
    for i, p in enumerate(m.v):
        w = {b: x for b, x in (m.wover.get(i) or wf(p)).items() if x > 1e-4}
        if not w:
            fail('%s: vertex %d has no weights' % (m.name, i))
        if len(w) > 4:
            fail('%s: vertex %d has %d influences' % (m.name, i, len(w)))
        s = sum(w.values())
        items = [(jindex[b], x / s) for b, x in w.items()]
        q = quantize([x for _, x in items])
        vw.append(sorted(((qq, j) for qq, (j, _) in zip(q, items) if qq > 0), key=lambda t: (-t[0], t[1])))
    key_index, vsrc, vdata, tris = {}, [], [], []
    for t in range(len(T)):
        out = []
        for k in (0, 2, 1):                                   # the triangle reversed: the mirror flips Unity's winding back
            v = int(T[t, k])
            key = (v, N[t, k].tobytes(), UVc[t, k].tobytes())
            i = key_index.get(key)
            if i is None:
                i = key_index[key] = len(vsrc)
                vsrc.append(v); vdata.append((N[t, k], UVc[t, k]))
            out.append(i)
        tris.append(out)
    if len(vsrc) > 65535:
        fail('%s: too many vertices' % m.name)
    palette = sorted({j for v in set(vsrc) for _, j in vw[v]})
    pal_of = {j: i for i, j in enumerate(palette)}
    bidx = np.zeros((len(vsrc), 4), np.uint8); bw = np.zeros((len(vsrc), 4), np.uint16)
    for i, v in enumerate(vsrc):
        for k, (q, j) in enumerate(vw[v]):
            bidx[i, k] = pal_of[j]; bw[i, k] = q
    pos = P[vsrc]
    nrm = np.array([d[0] for d in vdata], np.float32); uv = np.array([d[1] for d in vdata], np.float32)
    tri = np.array(tris, np.uint16)
    # the check FORMAT.md states: Cross(b - a, c - a) points the way of the vertex normals
    a, b, c = pos[tri[:, 0]].astype(float), pos[tri[:, 1]].astype(float), pos[tri[:, 2]].astype(float)
    cr = np.cross(b - a, c - a)
    nmean = nrm[tri].astype(float).sum(1)
    good = (np.einsum('ij,ij->i', cr, nmean) > 0) | (np.linalg.norm(cr, axis=1) < 1e-9)
    if good.mean() < 0.98:
        fail('%s: %d of %d triangles wound against their normals' % (m.name, (~good).sum(), len(tri)))
    return dict(name=m.name, hidden=m.name not in B.SHOWN, pos=pos, nrm=nrm, uv=uv, bidx=bidx, bw=bw, tris=tri,
                palette=palette, bmin=pos.min(0), bmax=pos.max(0))


def face_payload(S, fm, slot_names, mesh_index):
    blink = fm['blink']['ouak']
    out = bytearray(struct.pack('<B', len(slot_names)))
    for s in slot_names:
        out += struct.pack('<H', S(s))
    blink_slot = slot_names.index(blink['blink_eye'])
    out += struct.pack('<BffBBB', blink_slot, float(blink['open_seconds']), float(blink['closed_seconds']), 0, 0, 0)
    groups = sorted(fm['ouak_groups'], key=lambda g: g['group'])
    out += struct.pack('<B', len(groups))
    blink_piece = blink['blink_eye'] + '_m'
    for g in groups:
        game = [slot_names.index(g['ouak'][r]) if g['ouak'][r] else NONE8 for r in GROUP_ROLES]
        pieces, show, hide = [NONE8] * 4, [], []
        for k, r in enumerate(GROUP_ROLES):
            mine = g['new_style'][r]
            theirs = g['ouak'][r] + '_m' if g['ouak'][r] else None
            if mine:
                pieces[k] = mesh_index[mine]
            if mine != theirs:
                if mine:
                    show.append(mesh_index[mine])
                if theirs:
                    hide.append(mesh_index[theirs])
                if r == 'eye':
                    hide.append(mesh_index[blink_piece])
        show, hide = sorted(set(show)), sorted(set(hide))
        out += struct.pack('<4B4BBB', *game, *pieces, 0, 1 if g['blinks'] else 0)
        out += struct.pack('<B%dB' % len(show), len(show), *show)
        out += struct.pack('<B%dB' % len(hide), len(hide), *hide)
    out += struct.pack('<B', 0) + struct.pack('<B', 0)
    return bytes(out)


def png_bytes(arr, alpha):
    img = Image.fromarray((np.clip(arr if alpha else arr[..., :3], 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA' if alpha else 'RGB')
    bio = io.BytesIO(); img.save(bio, 'PNG', optimize=False)
    return bio.getvalue()


def main():
    fm = json.load(open(os.path.join(HERE, '..', 'face_map.json'), encoding='utf-8'))
    spec = B.load_spec()
    joints, jindex = read_joints(spec)
    os.makedirs(B.TEX_DIR, exist_ok=True); os.makedirs(os.path.dirname(OUT), exist_ok=True)
    B.check_layout(B.BODY_BLOCKS, B.BODY_PX)
    B.check_layout({n: c[0] for n, c in B.FACE_CELLS.items()}, B.FACE_PX)
    body = B.paint_body_array(); face, _ = B.paint_face_array()
    tex = {'body': (B.BODY_TEX + '.png', png_bytes(body, False), 'Rouge_new_body', 0),
           'face': (B.FACE_TEX + '.png', png_bytes(face, True), 'Rouge_new_face', 1)}
    for name, data, _, _ in tex.values():
        open(os.path.join(B.TEX_DIR, name), 'wb').write(data)

    slot_names = [s['slot'] for s in fm['ouak_slots']]
    extra_names = [n for n in fm_section(fm, '_new_pieces') if not (n.endswith('_m') and n[:-2] in slot_names)]
    parts = B.build_parts(spec)
    meshes = []
    for m, t, wf in parts:
        d = read_mesh(m, wf, jindex)
        d['role'], d['slot'], d['card'] = classify(m.name, slot_names, extra_names, {})
        d['tex'] = t
        meshes.append(d)
    group_of = {ROLE_BODY: 0, ROLE_PLATE: 1, ROLE_FACE: 1, ROLE_OVERLAY: 1, ROLE_EXTRA: 2, ROLE_CARD: 3}
    meshes.sort(key=lambda m: (group_of[m['role']], m['slot'] if m['slot'] != NONE8 else m['card'], m['name']))
    if sorted(m['slot'] for m in meshes if m['slot'] != NONE8) != list(range(len(slot_names))):
        fail('the face slot pieces do not cover each slot exactly once')
    mesh_index = {m['name']: i for i, m in enumerate(meshes)}

    S = Strings()
    chunks = []
    marker = 'llc_%s_%s' % (ENTRY['name'], STYLE)
    chunks.append((b'INFO', struct.pack('<5H', S(ENTRY['id']), S(ENTRY['name']), S(STYLE), S(marker), S('Rouge_new.blend'))))
    waist = jindex['JNT_waist']
    skel = bytearray(struct.pack('<B', len(joints)))
    for i, j in enumerate(joints):
        follow = FOLLOW_ROT | FOLLOW_SCALE | (FOLLOW_POS if i == waist else 0)
        skel += struct.pack('<HbB3f4f3f', S(j['name']), j['parent'], follow, *j['local'].astype(np.float32),
                            0.0, 0.0, 0.0, 1.0, *j['model'].astype(np.float32))
    chunks.append((b'SKEL', bytes(skel)))
    offset = joints[waist]['local'] - PRINCE_WAIST
    chunks.append((b'FOLW', struct.pack('<BBB3f', 1, waist, 1, *offset.astype(np.float32))))

    order = sorted(tex.values(), key=lambda t: t[3])
    for name, data, _, _ in order:
        img = Image.open(io.BytesIO(data))
        flags = (TEX_ALPHA if img.mode == 'RGBA' else 0) | TEX_SRGB
        chunks.append((b'TEXR', struct.pack('<3H4BI', S(name), img.width, img.height, 0, flags, FILTER_BILINEAR, WRAP_CLAMP, len(data)) + data))
    matl = bytearray(struct.pack('<B', len(order)))
    for name, data, mat, idx in order:
        matl += struct.pack('<HBB', S(mat), idx, 1 if idx == 1 else 0)
    chunks.append((b'MATL', bytes(matl)))

    for m in meshes:
        flags = (MESH_HIDDEN if m['hidden'] else 0) | (MESH_SHADOWS if m['role'] in (ROLE_BODY, ROLE_PLATE) else 0)
        slot_name = slot_names[m['slot']] if m['slot'] != NONE8 else None
        template = template_for(m['role'], m['name'], slot_names, m['slot'])
        nb = len(m['palette'])
        head = struct.pack('<HBBBHBBHBB%dB6fHH' % nb, S(m['name']), m['role'], flags, m['slot'], S(slot_name),
                           m['card'], tex[m['tex']][3], S(template), waist, nb, *m['palette'],
                           *m['bmin'].astype(np.float32), *m['bmax'].astype(np.float32), len(m['pos']), len(m['tris']))
        body_b = b''.join(a.astype(a.dtype.newbyteorder('<')).tobytes() for a in (m['pos'], m['nrm'], m['uv'], m['bidx'], m['bw'], m['tris']))
        chunks.append((b'MESH', head + body_b))
    chunks.append((b'FACE', face_payload(S, fm, slot_names, mesh_index)))
    chunks.insert(0, (b'STRS', S.payload()))
    body_all = bytearray()
    for tag, payload in chunks:
        body_all += tag + struct.pack('<I', len(payload)) + payload + b'\0' * (-len(payload) % 4)
    data = b'LLCM' + struct.pack('<HHII', FORMAT_VERSION, FILE_SLOTS, len(chunks), 16 + len(body_all)) + bytes(body_all)
    open(OUT, 'wb').write(data)
    print('EXPORT rouge new: meshes %d (hidden %d) triangles %d vertices %d joints %d bytes %d sha256 %s' % (
        len(meshes), sum(m['hidden'] for m in meshes), sum(len(m['tris']) for m in meshes),
        sum(len(m['pos']) for m in meshes), len(joints), len(data), hashlib.sha256(data).hexdigest()))
    print('WAIST offset (cm)', offset)
    make_icon(parts, spec, body, face)


def make_icon(parts, spec, body, face):
    """1024 x 1024, transparent, 509.5 px per metre, the ground on row 973: render_icons.py's camera scale.  Arms 70
    degrees below level as in the Prince's icon pose."""
    pose = {'JNT_arm_L': ((0, 1, 0), 70), 'JNT_arm_R': ((0, 1, 0), -70), 'JNT_elbow_L': ((0, 0, 1), 10), 'JNT_elbow_R': ((0, 0, 1), -10),
            'JNT_leg_L': ((0, 1, 0), 3), 'JNT_leg_R': ((0, 1, 0), -3)}
    mats = PV.skin_mats(spec, pose)
    texs = {'body': body, 'face': face}
    items = []
    for m, t, wf in parts:
        if m.name not in B.SHOWN:
            continue
        P, UV, T, g = PV.triangulate(m)
        Nn = PV.smooth_normals(P, T, g)
        P, Nn = PV.pose_points(P, Nn, T, PV.weights(m, wf), mats)
        items.append((P, Nn, UV, T, texs[t], t == 'face'))
    img = PV.render(items, (0.62, -0.78, 0.12), res=(1024, 1024), transparent=True, px_per_m=509.5, ground_row=973)
    img.save(ICON)
    print('ICON', os.path.relpath(ICON, LLC))


if __name__ == '__main__':
    main()
