"""Exports each cousin's .blend files to the mod's run-time format, .llcm (see Cousins/FORMAT.md).

Run it with Blender 5.2, headless, from any folder:

    blender -b --factory-startup --python-exit-code 1 --python export_llcm.py -- [cousin [style]]

With no arguments it exports every style of every cousin in Cousins/cousins.json. Each .blend is opened and read,
never saved: its SHA-256 is taken before and after, and a changed hash fails the run. check_llcm.py reads the
output back with none of this code and checks it against the .blend, the rig spec and the face map.

What it reads: the armature (23 JNT_* bones, rest at identity), every mesh object (rest geometry, corner normals,
the UV layer, vertex-group weights, hide flags, its one material and that material's image file), the cousin's
face_map.json and, for the waist offset, the OUAK Prince's waist height below.
"""
import bpy
import sys
import os
import re
import json
import struct
import hashlib
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(HERE)
COUSINS = os.path.join(MOD, 'Cousins')
LLC = os.path.dirname(MOD)

FORMAT_VERSION = 1
# The OUAK Prince's JNT_waist rest localPosition in his prefab (OUJI01_D), Unity cm. OUAK's clips write the waist
# there, so a cousin whose waist rests higher gets the difference as its FOLW offset.
PRINCE_WAIST = np.array([0.0, 26.345, 0.0])

NONE8, NONE16 = 0xFF, 0xFFFF
ROLE_BODY, ROLE_PLATE, ROLE_FACE, ROLE_OVERLAY, ROLE_EXTRA, ROLE_CARD = range(6)
MESH_HIDDEN, MESH_SHADOWS = 1, 2
FOLLOW_ROT, FOLLOW_POS, FOLLOW_SCALE = 1, 2, 4
FILE_CARDS, FILE_SLOTS = 1, 2
GROUP_ROLES = ('face', 'eye', 'mouth', 'overlay')   # face_map's columns, in FACE's order
FILTER_BILINEAR, WRAP_CLAMP = 1, 1                  # design: every texture bilinear and clamped
TEX_ALPHA, TEX_SRGB = 1, 2


class ExportError(Exception):
    pass


def fail(msg):
    raise ExportError(msg)


def sha256(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def read_json(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def read_face_map(path, load=None):
    """A cousin's face_map.json with its cousin-named keys (<id>_old_cards, <id>_new_pieces) also under the plain names
    old_cards and new_pieces, and each list its 'shared' block points to (forever_expressions, old_style_rows) read from
    that other face map, its cards matched by card number: card k here stands where the other cousin's card k stands."""
    if load is None:
        load = read_json
    fm = load(path)
    for plain in ('old_cards', 'new_pieces'):
        keys = [k for k in fm if k.endswith('_' + plain)]
        if len(keys) != 1:
            fail('%s: expected one *_%s key, found %s' % (path, plain, keys))
        fm[plain] = fm[keys[0]]
    for key, src in (fm.get('shared') or {}).items():
        if key == 'note':
            continue
        if key in fm:
            fail('%s: %s is both its own and shared' % (path, key))
        other = read_face_map(os.path.join(LLC, src), load)
        number = {name: k for k, name in other['old_cards'].items()}
        out = []
        for e in other[key]:
            k = number.get(e['card'])
            if k is None or k not in fm['old_cards']:
                fail('%s: shared %s names card %s, which has no number here' % (path, key, e['card']))
            out.append(dict(e, card=fm['old_cards'][k]))
        fm[key] = out
    return fm


class Strings:
    """The STRS table: each string once, index by first use."""

    def __init__(self):
        self.items, self.index = [], {}

    def __call__(self, s):
        if s is None:
            return NONE16
        if s not in self.index:
            if len(self.items) >= NONE16:
                fail('too many strings')
            self.index[s] = len(self.items)
            self.items.append(s)
        return self.index[s]

    def payload(self):
        out = bytearray(struct.pack('<H', len(self.items)))
        for s in self.items:
            b = s.encode('utf-8')
            out += struct.pack('<H', len(b)) + b
        return bytes(out)


def png_header(data, name):
    if data[:8] != b'\x89PNG\r\n\x1a\n' or data[12:16] != b'IHDR':
        fail('%s is not a PNG file' % name)
    w, h, depth, colour, _comp, _filt, interlace = struct.unpack('>IIBBBBB', data[16:29])
    if depth != 8 or colour not in (0, 2, 3, 4, 6):
        fail('%s: PNG depth %d colour type %d is not one the mod reads' % (name, depth, colour))
    return w, h, colour


def quantize(weights):
    """Weights (already normalized) -> u16 values summing to exactly 65535, by largest remainder."""
    raw = [w * 65535.0 for w in weights]
    q = [int(r) for r in raw]
    order = sorted(range(len(raw)), key=lambda i: (-(raw[i] - q[i]), i))
    for i in order[:65535 - sum(q)]:
        q[i] += 1
    return q


def read_joints(rig, spec_joints):
    bones = list(rig.data.bones)
    order = []

    def visit(b):
        order.append(b)
        for c in bones:
            if c.parent is not None and c.parent.name == b.name:
                visit(c)

    for b in bones:
        if b.parent is None:
            visit(b)
    if len(order) != len(bones):
        fail('bone tree walk lost bones')
    if sorted(b.name for b in order) != sorted(spec_joints):
        fail('bone names differ from the rig spec')
    index = {b.name: i for i, b in enumerate(order)}
    joints = []
    for b in order:
        R = np.array(b.matrix_local.to_3x3(), float)
        dev = float(np.abs(R - np.eye(3)).max())
        if dev > 1e-5:
            fail('%s does not rest at identity (largest element off by %g)' % (b.name, dev))
        parent = b.parent.name if b.parent else None
        if parent != spec_joints[b.name]['parent']:
            fail('%s: parent %s, rig spec says %s' % (b.name, parent, spec_joints[b.name]['parent']))
        h = np.array(b.head_local, float)                        # armature space: X her left, Y up, Z forward (m)
        model = 100.0 * np.array([-h[0], h[1], h[2]])             # Unity cm under the game's `model` node
        joints.append(dict(name=b.name, parent=index[parent] if parent else -1, model=model))
    for j in joints:
        j['local'] = j['model'] - (joints[j['parent']]['model'] if j['parent'] >= 0 else 0.0)
    return joints, index


def classify(name, slot_names, extra_names, card_no):
    """-> (role, slot index, card number)"""
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
    """The game renderer whose shared material the mod clones for this piece (design.md, Meshes, materials)."""
    if role in (ROLE_BODY, ROLE_PLATE, ROLE_CARD):
        return 'body_m'
    if role in (ROLE_FACE, ROLE_OVERLAY):
        return slot_names[slot] + '_m'
    return 'mouth_01_m' if name.startswith('mouth') else 'eye_01_m'


def image_of(mat):
    nodes = [n for n in mat.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image] if mat.node_tree else []
    if len(nodes) != 1:
        fail('material %s has %d image textures, the format takes 1' % (mat.name, len(nodes)))
    node = nodes[0]
    alpha_linked = any(l.from_node == node and l.from_socket.name == 'Alpha' and l.to_socket.name == 'Alpha'
                       for l in mat.node_tree.links)
    img = node.image
    path = os.path.normpath(bpy.path.abspath(img.filepath, library=img.library))
    return img, path, alpha_linked


def read_mesh(o, rig, A_inv, jindex):
    me = o.data
    if me.shape_keys:
        fail('%s has shape keys' % o.name)
    mods = list(o.modifiers)
    if (len(mods) != 1 or mods[0].type != 'ARMATURE' or mods[0].object != rig or not mods[0].use_vertex_groups
            or mods[0].use_bone_envelopes):
        fail('%s: expected one Armature modifier on the rig, vertex groups only' % o.name)
    if len(o.material_slots) != 1 or o.material_slots[0].material is None:
        fail('%s: expected exactly one material' % o.name)
    if me.uv_layers.active is None:
        fail('%s has no UV layer' % o.name)

    me.calc_loop_triangles()
    nv, nl, nt = len(me.vertices), len(me.loops), len(me.loop_triangles)
    co = np.zeros(nv * 3, np.float32)
    me.vertices.foreach_get('co', co)
    cn = np.zeros(nl * 3, np.float32)
    me.corner_normals.foreach_get('vector', cn)
    uv = np.zeros(nl * 2, np.float32)
    me.uv_layers.active.data.foreach_get('uv', uv)
    tv = np.zeros(nt * 3, np.int32)
    me.loop_triangles.foreach_get('vertices', tv)
    tl = np.zeros(nt * 3, np.int32)
    me.loop_triangles.foreach_get('loops', tl)
    tv, tl = tv.reshape(-1, 3), tl.reshape(-1, 3)

    # Mesh coordinates -> armature space -> Unity cm under `model`: (x, y, z) -> 100 * (-x, y, z).
    M = np.array(A_inv @ o.matrix_world, float)
    L = M[:3, :3]
    if np.linalg.det(L) <= 0:
        fail('%s: its transform mirrors' % o.name)
    mirror = np.array([-1.0, 1.0, 1.0])
    P = ((co.reshape(-1, 3).astype(float) @ L.T + M[:3, 3]) * mirror * 100.0).astype(np.float32) + np.float32(0)
    N = cn.reshape(-1, 3).astype(float) @ np.linalg.inv(L)
    N /= np.linalg.norm(N, axis=1, keepdims=True)
    N = (N * mirror).astype(np.float32) + np.float32(0)
    UV = uv.reshape(-1, 2) + np.float32(0)

    # Weights per Blender vertex: bones only, up to 4, largest first, quantized to sum 65535.
    gname = {g.index: g.name for g in o.vertex_groups}
    vw = []
    for v in me.vertices:
        ws = []
        for g in v.groups:
            if g.weight <= 0:
                continue
            n = gname[g.group]
            if n not in jindex:
                fail('%s: vertex %d has weight on group %s, which is not a bone' % (o.name, v.index, n))
            ws.append((jindex[n], g.weight))
        if not ws:
            vw.append(None)
            continue
        if len(ws) > 4:
            fail('%s: vertex %d has %d influences, the format takes 4' % (o.name, v.index, len(ws)))
        s = sum(w for _, w in ws)
        if abs(s - 1.0) > 1e-4:
            fail('%s: vertex %d weights sum to %.6f' % (o.name, v.index, s))
        q = quantize([w / s for _, w in ws])
        pairs = sorted(((qq, j) for qq, (j, _) in zip(q, ws) if qq > 0), key=lambda p: (-p[0], p[1]))
        vw.append(pairs)

    # Split corners into Unity vertices: one per (vertex, normal, UV). Triangles keep Blender's order;
    # each (c0, c1, c2) is written (c0, c2, c1), Unity's winding.
    key_index, vsrc, vcorner, tris = {}, [], [], []
    for t in range(nt):
        out = []
        for k in (0, 2, 1):
            c, v = int(tl[t, k]), int(tv[t, k])
            key = (v, N[c].tobytes(), UV[c].tobytes())
            i = key_index.get(key)
            if i is None:
                i = key_index[key] = len(vsrc)
                vsrc.append(v)
                vcorner.append(c)
            out.append(i)
        tris.append(out)
    if len(vsrc) > 65535:
        fail('%s has %d vertices, the format takes 65535' % (o.name, len(vsrc)))
    for v in set(vsrc):
        if vw[v] is None:
            fail('%s: vertex %d has no weights' % (o.name, v))

    palette = sorted({j for v in set(vsrc) for _, j in vw[v]})
    pal_of = {j: i for i, j in enumerate(palette)}
    bidx = np.zeros((len(vsrc), 4), np.uint8)
    bw = np.zeros((len(vsrc), 4), np.uint16)
    for i, v in enumerate(vsrc):
        for k, (q, j) in enumerate(vw[v]):
            bidx[i, k] = pal_of[j]
            bw[i, k] = q

    pos = P[vsrc]
    return dict(name=o.name, hidden=bool(o.hide_viewport or o.hide_render), pos=pos, nrm=N[vcorner], uv=UV[vcorner],
                bidx=bidx, bw=bw, tris=np.array(tris, np.uint16), palette=palette,
                material=o.material_slots[0].material,
                bmin=pos.min(0), bmax=pos.max(0), blender_verts=nv, blender_tris=nt)


def face_payload(S, fm, slot_names, mesh_index, has_slots, has_cards):
    card_no = {name: int(k) for k, name in fm['old_cards'].items()}
    blink = fm['blink']['ouak']
    out = bytearray(struct.pack('<B', len(slot_names)))
    for s in slot_names:
        out += struct.pack('<H', S(s))
    blink_slot = slot_names.index(blink['blink_eye'])
    blink_card = over_card = default_card = 0
    groups = sorted(fm['ouak_groups'], key=lambda g: g['group'])
    if [g['group'] for g in groups] != list(range(len(groups))):
        fail('face_map groups are not 0..n-1')
    if has_cards:
        named = [n for n in re.findall(r'face_\d+_[a-z_]+', fm['blink']['old_style']) if n in card_no]
        if len(named) < 2:
            fail('face_map blink.old_style names fewer than two cards')
        blink_card, over_card = card_no[named[0]], card_no[named[1]]
        default_card = card_no[groups[0]['old_style_card']]
    out += struct.pack('<BffBBB', blink_slot, float(blink['open_seconds']), float(blink['closed_seconds']),
                       blink_card, over_card, default_card)
    out += struct.pack('<B', len(groups))
    blink_piece = blink['blink_eye'] + '_m'
    for g in groups:
        game = [slot_names.index(g['ouak'][r]) if g['ouak'][r] else NONE8 for r in GROUP_ROLES]
        pieces, show, hide = [NONE8] * 4, [], []
        if has_slots:
            for k, r in enumerate(GROUP_ROLES):
                mine = g['new_style'][r]
                theirs = g['ouak'][r] + '_m' if g['ouak'][r] else None
                if mine:
                    if mine not in mesh_index:
                        fail('face_map group %d names %s, which the model lacks' % (g['group'], mine))
                    pieces[k] = mesh_index[mine]
                if mine != theirs:
                    # Her piece differs from the one the game's slot shows: show hers as an extra renderer and
                    # force the slot's piece off. If the eye differs, the game's blink would lay its blink eye
                    # over hers, so that piece goes off too.
                    if mine:
                        show.append(mesh_index[mine])
                    if theirs:
                        hide.append(mesh_index[theirs])
                    if r == 'eye':
                        hide.append(mesh_index[blink_piece])
        show, hide = sorted(set(show)), sorted(set(hide))
        card = card_no[g['old_style_card']] if has_cards else 0
        out += struct.pack('<4B4BBB', *game, *pieces, card, 1 if g['blinks'] else 0)
        out += struct.pack('<B%dB' % len(show), len(show), *show)
        out += struct.pack('<B%dB' % len(hide), len(hide), *hide)
    exprs, rows = [], []
    if has_cards:
        ex = sorted(fm['forever_expressions'], key=lambda e: e['expression'])
        if [e['expression'] for e in ex] != list(range(len(ex))):
            fail('face_map expressions are not 0..n-1')
        exprs = [card_no[e['card']] for e in ex]
        rows = sorted((r['row'], card_no[r['card']]) for r in fm['old_style_rows'])
        if len({r for r, _ in rows}) != len(rows) or rows[-1][0] > 255:
            fail('face_map rows repeat or pass 255')
    out += struct.pack('<B%dB' % len(exprs), len(exprs), *exprs)
    out += struct.pack('<B', len(rows))
    for r, c in rows:
        out += struct.pack('<BB', r, c)
    return bytes(out)


def export(entry, style):
    build = entry['build']
    blend = os.path.normpath(os.path.join(LLC, build['blend'][style]))
    fm = read_face_map(os.path.join(LLC, build['face_map']))
    spec = read_json(os.path.join(LLC, build['rig_spec']))[build['rig_spec_block'][style]]['joints']
    out_path = os.path.normpath(os.path.join(COUSINS, entry['styles'][style]))
    hash_before = sha256(blend)
    bpy.ops.wm.open_mainfile(filepath=blend, load_ui=False)

    rigs = [o for o in bpy.data.objects if o.type == 'ARMATURE']
    if len(rigs) != 1:
        fail('%s has %d armatures, expected 1' % (blend, len(rigs)))
    rig = rigs[0]
    A_inv = rig.matrix_world.inverted()
    joints, jindex = read_joints(rig, spec)

    slot_names = [s['slot'] for s in fm['ouak_slots']]
    extra_names = [n for n in fm['new_pieces'] if not (n.endswith('_m') and n[:-2] in slot_names)]
    card_no = {name: int(k) for k, name in fm['old_cards'].items()}

    meshes = []
    for o in bpy.data.objects:
        if o.type != 'MESH':
            continue
        m = read_mesh(o, rig, A_inv, jindex)
        m['role'], m['slot'], m['card'] = classify(o.name, slot_names, extra_names, card_no)
        meshes.append(m)
    group_of = {ROLE_BODY: 0, ROLE_PLATE: 1, ROLE_FACE: 1, ROLE_OVERLAY: 1, ROLE_EXTRA: 2, ROLE_CARD: 3}
    meshes.sort(key=lambda m: (group_of[m['role']], m['slot'] if m['slot'] != NONE8 else m['card'], m['name']))
    if len(meshes) > 254:
        fail('more than 254 meshes')

    has_slots = any(m['slot'] != NONE8 for m in meshes)
    has_cards = any(m['card'] for m in meshes)
    if has_slots == has_cards:
        fail('a model must have either face slot pieces or face cards')
    if has_slots and sorted(m['slot'] for m in meshes if m['slot'] != NONE8) != list(range(len(slot_names))):
        fail('the face slot pieces do not cover each slot exactly once')
    if has_cards and sorted(m['card'] for m in meshes if m['card']) != sorted(card_no.values()):
        fail('the face cards do not cover each card exactly once')
    mesh_index = {m['name']: i for i, m in enumerate(meshes)}

    S = Strings()
    chunks = []
    marker = 'llc_%s_%s' % (entry['name'], style)
    chunks.append((b'INFO', struct.pack('<5H', S(entry['id']), S(entry['name']), S(style), S(marker),
                                        S(os.path.basename(blend)))))

    waist = jindex['JNT_waist']
    skel = bytearray(struct.pack('<B', len(joints)))
    for i, j in enumerate(joints):
        follow = FOLLOW_ROT | FOLLOW_SCALE | (FOLLOW_POS if i == waist else 0)
        skel += struct.pack('<HbB3f4f3f', S(j['name']), j['parent'], follow, *j['local'].astype(np.float32),
                            0.0, 0.0, 0.0, 1.0, *j['model'].astype(np.float32))
    chunks.append((b'SKEL', bytes(skel)))
    offset = joints[waist]['local'] - PRINCE_WAIST
    chunks.append((b'FOLW', struct.pack('<BBB3f', 1, waist, 1, *offset.astype(np.float32))))

    textures, tex_index, materials, mat_index = [], {}, [], {}
    for m in meshes:
        mat = m['material']
        if mat.name not in mat_index:
            img, path, alpha_linked = image_of(mat)
            if path not in tex_index:
                with open(path, 'rb') as f:
                    data = f.read()
                w, h, colour = png_header(data, os.path.basename(path))
                if tuple(img.size) not in ((0, 0), (w, h)):
                    fail('%s: Blender says %s, the file says %dx%d' % (path, tuple(img.size), w, h))
                flags = (TEX_ALPHA if colour in (4, 6) else 0) | (TEX_SRGB if img.colorspace_settings.name == 'sRGB' else 0)
                tex_index[path] = len(textures)
                textures.append((os.path.basename(path), w, h, flags, data))
            mat_index[mat.name] = len(materials)
            materials.append((mat.name, tex_index[path], 1 if alpha_linked else 0))
        m['mat'] = mat_index[mat.name]
    for name, w, h, flags, data in textures:
        chunks.append((b'TEXR', struct.pack('<3H4BI', S(name), w, h, 0, flags, FILTER_BILINEAR, WRAP_CLAMP, len(data))
                       + data))
    matl = bytearray(struct.pack('<B', len(materials)))
    for name, tex, alpha in materials:
        matl += struct.pack('<HBB', S(name), tex, alpha)
    chunks.append((b'MATL', bytes(matl)))

    for m in meshes:
        flags = (MESH_HIDDEN if m['hidden'] else 0) | (MESH_SHADOWS if m['role'] in (ROLE_BODY, ROLE_PLATE) else 0)
        slot_name = slot_names[m['slot']] if m['slot'] != NONE8 else None
        template = template_for(m['role'], m['name'], slot_names, m['slot'])
        nb = len(m['palette'])
        head = struct.pack('<HBBBHBBHBB%dB6fHH' % nb, S(m['name']), m['role'], flags, m['slot'], S(slot_name),
                           m['card'], m['mat'], S(template), waist, nb, *m['palette'],
                           *m['bmin'].astype(np.float32), *m['bmax'].astype(np.float32), len(m['pos']), len(m['tris']))
        body = b''.join(a.astype(a.dtype.newbyteorder('<')).tobytes() for a in (
            m['pos'], m['nrm'], m['uv'], m['bidx'], m['bw'], m['tris']))
        chunks.append((b'MESH', head + body))

    chunks.append((b'FACE', face_payload(S, fm, slot_names, mesh_index, has_slots, has_cards)))
    chunks.insert(0, (b'STRS', S.payload()))

    body = bytearray()
    for tag, payload in chunks:
        body += tag + struct.pack('<I', len(payload)) + payload + b'\0' * (-len(payload) % 4)
    flags = FILE_CARDS if has_cards else FILE_SLOTS
    data = b'LLCM' + struct.pack('<HHII', FORMAT_VERSION, flags, len(chunks), 16 + len(body)) + bytes(body)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, 'wb') as f:
        f.write(data)

    hash_after = sha256(blend)
    if hash_after != hash_before:
        fail('%s changed while it was open (%s -> %s)' % (blend, hash_before, hash_after))
    print('EXPORT %s %s: %s  meshes %d (hidden %d)  triangles %d  vertices %d  joints %d  textures %d  '
          'materials %d  bytes %d  sha256 %s' % (
              entry['id'], style, os.path.relpath(out_path, LLC).replace('\\', '/'), len(meshes),
              sum(m['hidden'] for m in meshes), sum(len(m['tris']) for m in meshes),
              sum(len(m['pos']) for m in meshes), len(joints), len(textures), len(materials), len(data),
              hashlib.sha256(data).hexdigest()))
    print('BLEND %s unchanged: sha256 %s' % (os.path.basename(blend), hash_after))


def main():
    args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    cj = read_json(os.path.join(COUSINS, 'cousins.json'))
    jobs = []
    for entry in cj['cousins']:
        if args and entry['id'] != args[0]:
            continue
        for style in entry['styles']:
            if len(args) > 1 and style != args[1]:
                continue
            jobs.append((entry, style))
    if not jobs:
        fail('nothing to export for %s' % args)
    for entry, style in jobs:
        export(entry, style)
    print('EXPORT DONE %d file(s)' % len(jobs))


try:
    main()
except ExportError as e:
    print('EXPORT FAIL', e)
    sys.stdout.flush()
    os._exit(1)
bpy.ops.wm.quit_blender()
