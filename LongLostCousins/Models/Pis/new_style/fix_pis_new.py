"""Fixes Pis's new-style model after Tools/import_llcm.py has rebuilt it from the shipped pis_new.llcm.

    blender -b --factory-startup --python-exit-code 1 <dir>/Pis_new.blend --python fix_pis_new.py

It edits the opened .blend and saves it in place as Pis_new.blend (uncompressed, image paths stay //textures/...).
It reads only the opened .blend and ../pis_rig_spec.json, and refuses a .blend it has already fixed.

What it changes, and why:

1. The head tab. In the shipped model the tab is part of body_m's single outline: one flat front cap runs from the
   torso up over the tab, triangulated across the boundary, and the vertices at the tab's base blend JNT_spine_02
   and JNT_head. The face plate is rigid on JNT_head and its back edge sat 1 mm behind that cap. When the head turns
   or nods, the blended cap triangles tilt and fold: they cut through the plate (the line across the face) and
   overlap each other in nearly one plane at the neck and the torso top (the flicker). The fix cuts the tab off:
   - the torso's outline gets a straight top edge where the tab was (its bevelled top band runs straight across,
     closing the torso), and loses every JNT_head weight;
   - the tab becomes its own closed shell, the same arch and bevel as before, weighted only to JNT_head, sitting
     1.5 mm above the torso's top (its bevel eased along the bottom edge), so nothing of it lies inside the torso;
   - a hidden plug (JNT_head only) reaches down into the torso: a ball about JNT_head cut flat underneath, its
     radius the torso's half depth minus 3.5 mm. It lies inside a cylinder of that radius about the nod axis and
     one about the turn axis, so no turn, nod or mix of them carries it out of the torso, and under nods its round
     top keeps filling the middle of the space the tab's edge uncovers;
   - TORSO_FILL (off) adds a hidden fill on the torso (JNT_spine_02), rising from inside the torso 1 cm into the
     tab. Its footprint is the part of the tab's flat bottom that every head turn up to 30 degrees keeps covering,
     kept over the torso's flat top. It closes the rest slit and narrows the opening under a nod's lifted edge a
     little at 10 degrees, but stands clear as a flat block at larger nods, leaves the tab from a 35 degree turn,
     and cannot reach the tab's end corners. It stays off.
   JNT_head moves 2 cm down to the plug's centre, 2 cm under the torso top, so a 30 degree forward nod swings the
   face's lower edge clear of the torso's front instead of into it.
2. The face layers. The plate moves 2.5 mm forward so its back sits 1.5 mm in front of the tab; the eyes and mouths
   go 2 mm in front of the plate (flat, as they were), the overlays 1.2 mm in front of it (following its dome), the
   nose with the plate, its hidden base kept 2 mm off the tab. JNT_mouth and JNT_nose move forward with the face.
   The antenna's base ring sinks 6 mm along its cone line into the tab, so its flat bottom no longer lies 1 mm
   under the tab's top.
3. The arms. JNT_arm sits just outside the body's top outer corners, JNT_shoulder just inside them, JNT_elbow half
   way to the unmoved JNT_hand. The rod is shortened to start at JNT_arm (its flat root becomes a short cone inside
   the rod's radius, so lowering the arm cannot swing it into the body); the ball stays where it was. Weights keep
   their rings, so arm, elbow and hand sit at the rod root, its middle and the ball, as before.
"""
import bpy
import bmesh
import json
import math
import os
from mathutils import Vector, Matrix

HERE = os.path.dirname(os.path.abspath(__file__))
SPEC = os.path.join(os.path.dirname(HERE), 'pis_rig_spec.json')

BEVEL = 0.012                                    # body bevel radius (m): rings at 0, 30, 60, 90 degrees
RING_ANGLES = (90, 60, 30, 0)                    # front cap ring first
HALF_DEPTH = 0.073                               # band half depth before the bevel
TORSO_TOP = 0.79                                 # torso lid (m), its highest point
TAB_BOTTOM = TORSO_TOP + 0.0015                  # tab shell bottom: wholly above the torso
TAB_BOTTOM_BEVEL = 0.25                          # bevel scale along the tab's bottom edge (1 on the arch and sides)
PLUG_R = 0.085 - 0.0035                          # plug: ball radius about JNT_head (torso half depth minus a margin)
PLUG_BOTTOM = -0.036                             # plug: flat bottom below JNT_head
PLUG_RINGS, PLUG_SEGMENTS = 8, 24                 # plug: latitude rings and segments round
TORSO_FILL = False                               # the torso's hidden fill under the tab (JNT_spine_02); see above
FILL_TURN = 30.0                                 # fill stays under the tab's flat bottom for head turns up to this
FILL_YC, FILL_XC = 0.083, 0.0925                 # tab bottom's half depth (85 mm) and flat half width (94.5 mm), less 2 mm
FILL_YCAP = HALF_DEPTH - 0.002                   # fill kept off the torso's top bevel (it starts at |y| 73 mm)
FILL_BOTTOM, FILL_TOP = 0.775, 0.80              # fill from inside the torso up into the tab
PLATE_SHIFT = -0.0025                            # face plate forward (m, -Y)
EYE_GAP, OVERLAY_GAP = 0.002, 0.0012             # in front of the plate's front surface
NOSE_BACK = -0.087                               # nose base kept in front of this y
ANTENNA_DROP = 0.006


class FixError(Exception):
    pass


def need(ok, msg):
    if not ok:
        raise FixError(msg)


def load_spec():
    with open(SPEC, encoding='utf-8') as f:
        return json.load(f)['new_style']['joints']


def corner_normals(me):
    return [Vector(c.vector) for c in me.corner_normals]


def set_normals(me, normals):
    me.normals_split_custom_set([tuple(n.normalized()) for n in normals])
    me.update()


def group_index(o, name):
    g = o.vertex_groups.get(name)
    if g is None:
        g = o.vertex_groups.new(name=name)
    return g.index


# ---------------------------------------------------------------- joints

def move_joints(rig, spec):
    """Every bone's head to the rig spec (Blender world -> armature space), tails keep their offset."""
    to_arm = rig.matrix_world.inverted()
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='EDIT')
    moved = []
    for eb in rig.data.edit_bones:
        want = to_arm @ Vector(spec[eb.name]['head'])
        if (eb.head - want).length > 1e-7:
            off = eb.tail - eb.head
            moved.append((eb.name, tuple(eb.head), tuple(want)))
            eb.head = want
            eb.tail = want + off
            eb.roll = 0.0
    bpy.ops.object.mode_set(mode='OBJECT')
    for b in rig.data.bones:
        dev = max(abs(x) for r in (b.matrix_local.to_3x3() - Matrix.Identity(3)) for x in r)
        need(dev < 1e-6, '%s no longer rests at identity' % b.name)
    return moved


# ---------------------------------------------------------------- body helpers

def ring_profile(front):
    """(y, inward offset, normal y, normal outward scale) per ring, front cap ring first (or back cap ring last)."""
    out = []
    angles = RING_ANGLES if front else tuple(reversed(RING_ANGLES))
    s = -1.0 if front else 1.0
    for a in angles:
        r = math.radians(a)
        out.append((s * (HALF_DEPTH + BEVEL * math.sin(r)), BEVEL * (1 - math.cos(r)), s * math.sin(r), math.cos(r)))
    return out


RINGS = ring_profile(True) + ring_profile(False)          # 8 rings, front cap .. back cap
RING_Y = [r[0] for r in RINGS]


def ring_point(k, s):
    """Ring k's (y, inward offset) on an outline point whose bevel is scaled by s (1 = the body's bevel)."""
    y, d, ny, _ = RINGS[k]
    sign = 1.0 if y > 0 else -1.0
    edge = HALF_DEPTH + BEVEL                     # the cap plane
    return sign * (edge - s * (edge - abs(y))), s * d


def ring_of(y):
    k = min(range(8), key=lambda i: abs(RING_Y[i] - y))
    return k if abs(RING_Y[k] - y) < 2e-5 else None


def planar_fit(samples):
    """UV = a*x + b*z + c by least squares over (x, z, u, v) samples -> function, worst residual."""
    import numpy as np
    A = np.array([[x, z, 1.0] for x, z, _, _ in samples])
    B = np.array([[u, v] for _, _, u, v in samples])
    sol = np.linalg.lstsq(A, B, rcond=None)[0]
    res = float(np.abs(A @ sol - B).max())
    return (lambda x, z: (float(sol[0, 0] * x + sol[1, 0] * z + sol[2, 0]),
                          float(sol[0, 1] * x + sol[1, 1] * z + sol[2, 1]))), res


def earclip(pts):
    """Triangles (index triples, CCW) of a simple CCW polygon; ears never contain or touch another vertex."""
    def a2(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])

    def d2(a, b):
        return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2

    idx = list(range(len(pts)))
    tris = []
    while len(idx) > 3:
        n, best = len(idx), None
        for k in range(n):
            i0, i1, i2 = idx[k - 1], idx[k], idx[(k + 1) % n]
            a, b, c = pts[i0], pts[i1], pts[i2]
            ar = a2(a, b, c)
            if ar <= 1e-12:
                continue
            if any(a2(a, b, pts[j]) >= -1e-13 and a2(b, c, pts[j]) >= -1e-13 and a2(c, a, pts[j]) >= -1e-13
                   for j in idx if j not in (i0, i1, i2)):
                continue
            q = ar / (d2(a, b) + d2(b, c) + d2(c, a))
            if best is None or q > best[0]:
                best = (q, k)
        need(best is not None, 'ear clipping found no ear')
        k = best[1]
        tris.append((idx[k - 1], idx[k], idx[(k + 1) % len(idx)]))
        idx.pop(k)
    need(a2(*[pts[i] for i in idx]) > 1e-12, 'ear clipping left a flat triangle')
    tris.append(tuple(idx))
    return tris


def boundary_loop(edges):
    """Boundary edges (BMEdge) forming one closed loop -> ordered verts."""
    adj = {}
    for e in edges:
        a, b = e.verts
        adj.setdefault(a, []).append(b)
        adj.setdefault(b, []).append(a)
    need(all(len(v) == 2 for v in adj.values()), 'cap hole boundary is not a simple loop')
    start = next(iter(adj))
    loop, prev, cur = [start], None, start
    while True:
        nxt = adj[cur][0] if adj[cur][0] is not prev else adj[cur][1]
        if nxt is start:
            break
        loop.append(nxt)
        prev, cur = cur, nxt
    need(len(loop) == len(adj), 'cap hole boundary has more than one loop')
    return loop


def new_face(bm, verts, want_normal, uvl, nl, uvs, nrms):
    """Face with the given verts, flipped if needed so its normal agrees with want_normal; per-corner UV/normal."""
    vs, uv_, nr_ = list(verts), list(uvs), list(nrms)
    n = (vs[1].co - vs[0].co).cross(vs[2].co - vs[0].co)
    if len(vs) == 4:
        n = (vs[2].co - vs[0].co).cross(vs[3].co - vs[1].co)
    if n.dot(want_normal) < 0:
        vs.reverse(); uv_.reverse(); nr_.reverse()
    f = bm.faces.new(vs)
    f.smooth = True
    for l, uv, nr in zip(f.loops, uv_, nr_):
        l[uvl].uv = uv
        l[nl] = nr
    return f


# ---------------------------------------------------------------- body

def fix_body(o, spec):
    me = o.data
    need(len(me.vertices) == 912 and len(me.polygons) == 1820, 'body_m is not the imported shape (already fixed?)')
    g_head, g_sp2 = group_index(o, 'JNT_head'), group_index(o, 'JNT_spine_02')
    cn = corner_normals(me)
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    uvl = bm.loops.layers.uv.active
    dl = bm.verts.layers.deform.active
    nl = bm.loops.layers.float_vector.new('fix_normal')
    for f in bm.faces:
        ls = me.polygons[f.index].loop_start
        for k, l in enumerate(f.loops):
            l[nl] = cn[ls + k]

    # cap UV maps (planar) and band v per ring, from the untouched mesh
    caps = {True: [], False: []}
    band_v = {}
    for f in bm.faces:
        cap = abs(f.normal.y) > 0.999 and all(ring_of(v.co.y) in (0, 7) for v in f.verts)
        for l in f.loops:
            if cap:
                caps[f.normal.y < 0].append((l.vert.co.x, l.vert.co.z, l[uvl].uv.x, l[uvl].uv.y))
            else:
                k = ring_of(l.vert.co.y)
                if k is not None:
                    band_v.setdefault(k, set()).add(round(l[uvl].uv.y, 6))
    need(all(len(band_v.get(k, ())) == 1 for k in range(8)), 'band v is not one value per ring: %s' % band_v)
    band_v = {k: next(iter(s)) for k, s in band_v.items()}
    cap_uv = {}
    for front, smp in caps.items():
        cap_uv[front], res = planar_fit(smp)
        need(res < 2e-4, 'cap UVs are not planar (residual %g)' % res)

    # the tab: outline vertices of every ring above the torso's straight top, between its two ends
    arch = [v for v in bm.verts if abs(v.co.x) < 0.1215 and v.co.z > 0.775]
    need(len(arch) == 8 * 31, 'expected 248 tab outline vertices, found %d' % len(arch))
    ends = {}
    for v in bm.verts:
        if 0.1215 <= abs(v.co.x) <= 0.1235 and v.co.z > 0.775:
            k = ring_of(v.co.y)
            need(k is not None and (k, v.co.x > 0) not in ends, 'unexpected vertex at the tab ends')
            ends[(k, v.co.x > 0)] = v
    need(len(ends) == 16, 'expected 16 tab end vertices')
    arch_set = set(arch)

    def band_corner(v):
        for l in v.link_loops:
            f = l.face
            if abs(f.normal.y) < 0.999 and not any(w in arch_set for w in f.verts):
                return Vector(l[uvl].uv), Vector(l[nl])
        raise FixError('no band corner at %s' % tuple(v.co))

    end_corner = {key: band_corner(v) for key, v in ends.items()}
    # the arch's outermost front ring (y = -0.073) from x = +0.108 over the top to -0.108, with its band u
    outer = sorted([v for v in arch if ring_of(v.co.y) == 3 and v.co.z > 0.8045], key=lambda v: -v.co.x)
    need(len(outer) == 21, 'expected 21 arch points, found %d' % len(outer))
    arch_pts = [(v.co.x, v.co.z, band_corner_any(v, uvl, nl)) for v in outer]

    bmesh.ops.delete(bm, geom=arch, context='VERTS')

    # torso: straight top band across where the tab was (closing the torso), then refill the cap holes
    for k in range(7):
        a0, a1, b0, b1 = ends[(k, True)], ends[(k + 1, True)], ends[(k, False)], ends[(k + 1, False)]
        uv = [end_corner[(k, True)][0], end_corner[(k, False)][0], end_corner[(k + 1, False)][0],
              end_corner[(k + 1, True)][0]]
        nr = [end_corner[(k, True)][1], end_corner[(k, False)][1], end_corner[(k + 1, False)][1],
              end_corner[(k + 1, True)][1]]
        mid_n = Vector((0, RINGS[k][2] + RINGS[k + 1][2], RINGS[k][3] + RINGS[k + 1][3]))
        new_face(bm, [a0, b0, b1, a1], mid_n, uvl, nl, uv, nr)
    for front in (True, False):
        ycap = RING_Y[0] if front else RING_Y[7]
        edges = [e for e in bm.edges if len(e.link_faces) == 1 and all(abs(v.co.y - ycap) < 2e-5 for v in e.verts)]
        loop = boundary_loop(edges)
        pts = [(v.co.x, v.co.z) for v in loop]
        area = sum(pts[i - 1][0] * pts[i][1] - pts[i][0] * pts[i - 1][1] for i in range(len(pts)))
        if area < 0:
            loop.reverse(); pts.reverse()
        want = Vector((0, -1 if front else 1, 0))
        for t in earclip(pts):
            vs = [loop[i] for i in t]
            new_face(bm, vs, want, uvl, nl, [cap_uv[front](v.co.x, v.co.z) for v in vs], [want] * 3)

    # torso weights: nothing on JNT_head any more
    for v in bm.verts:
        d = v[dl]
        if g_head in d:
            w = d[g_head]
            del d[g_head]
            d[g_sp2] = d.get(g_sp2, 0.0) + w

    # the tab shell (JNT_head only)
    def add_vert(co):
        v = bm.verts.new(co)
        v[dl][g_head] = 1.0
        return v

    outline, seam, scales = build_tab_outline(arch_pts)     # [(x, z, nx, nz, u)], CCW seen from the front
    n = len(outline)
    grid = [[None] * 8 for _ in range(n)]
    for i, (x, z, nx, nz, u) in enumerate(p[:5] for p in outline):
        for k in range(8):
            y, d = ring_point(k, scales[i])
            grid[i][k] = add_vert((x - d * nx, y, z - d * nz))
    for i in range(n):
        j = (i + 1) % n
        ui = outline[i][5] if i == seam else outline[i][4]    # the bottom middle vertex is the band's UV seam
        uj = outline[j][4]
        for k in range(7):
            vs = [grid[i][k], grid[j][k], grid[j][k + 1], grid[i][k + 1]]
            uvs = [(ui, band_v[k]), (uj, band_v[k]), (uj, band_v[k + 1]), (ui, band_v[k + 1])]
            nrs = []
            for (ii, kk) in ((i, k), (j, k), (j, k + 1), (i, k + 1)):
                x, z, nx, nz = outline[ii][:4]
                y, d, ny, s = RINGS[kk]
                nrs.append(Vector((s * nx, ny, s * nz)))
            mid = (nrs[0] + nrs[1] + nrs[2] + nrs[3])
            new_face(bm, vs, mid, uvl, nl, uvs, nrs)
    for front, k in ((True, 0), (False, 7)):
        want = Vector((0, -1 if front else 1, 0))
        loop = [grid[i][k] for i in range(n)]
        pts = [(v.co.x, v.co.z) for v in loop]
        if sum(pts[i - 1][0] * pts[i][1] - pts[i][0] * pts[i - 1][1] for i in range(n)) < 0:
            loop.reverse(); pts.reverse()
        for t in earclip(pts):
            vs = [loop[i] for i in t]
            new_face(bm, vs, want, uvl, nl, [cap_uv[front](v.co.x, v.co.z) for v in vs], [want] * 3)

    # the plug: a ball about JNT_head, cut flat underneath. Its radius is the torso's half depth less 3.5 mm, so it
    # lies inside both the nod cylinder (axis X) and the turn cylinder (axis Z) of that radius, and no head
    # rotation (turn, nod, tilt or any mix) can carry any of it out of the torso's front or back.
    zp = spec['JNT_head']['head'][2]
    r = PLUG_R
    need(r < HALF_DEPTH + BEVEL and -r < PLUG_BOTTOM < 0, 'plug size')
    centre = Vector((0, 0, zp))
    phi_max = math.acos(PLUG_BOTTOM / r)                  # polar angle of the cut, from the top
    pole = add_vert((0, 0, zp + r))
    rings = []
    for k in range(1, PLUG_RINGS + 1):
        phi = phi_max * k / PLUG_RINGS
        rings.append([add_vert((r * math.sin(phi) * math.cos(t), r * math.sin(phi) * math.sin(t), zp + r * math.cos(phi)))
                      for t in (2 * math.pi * m / PLUG_SEGMENTS for m in range(PLUG_SEGMENTS))])
    puv = lambda v: cap_uv[True](v.co.x, v.co.z)
    radial = lambda v: (v.co - centre).normalized()
    M = PLUG_SEGMENTS
    for m in range(M):
        vs = [pole, rings[0][m], rings[0][(m + 1) % M]]
        nrs = [radial(v) for v in vs]
        new_face(bm, vs, nrs[0] + nrs[1] + nrs[2], uvl, nl, [puv(v) for v in vs], nrs)
    for k in range(PLUG_RINGS - 1):
        for m in range(M):
            vs = [rings[k][m], rings[k + 1][m], rings[k + 1][(m + 1) % M], rings[k][(m + 1) % M]]
            nrs = [radial(v) for v in vs]
            new_face(bm, vs, nrs[0] + nrs[1] + nrs[2] + nrs[3], uvl, nl, [puv(v) for v in vs], nrs)
    down = Vector((0, 0, -1))
    base = rings[-1]
    for t in earclip([(v.co.x, v.co.y) for v in base]):
        vs = [base[i] for i in t]
        new_face(bm, vs, down, uvl, nl, [puv(v) for v in vs], [down] * 3)

    bm.normal_update()
    if TORSO_FILL:
        build_torso_fill(bm, dl, g_sp2, uvl, nl, puv)
    bm.to_mesh(me)
    bm.free()
    attr = me.attributes['fix_normal']
    nrm = [Vector(attr.data[i].vector) for i in range(len(me.loops))]
    me.attributes.remove(attr)
    set_normals(me, nrm)
    return dict(verts=len(me.vertices), faces=len(me.polygons))


def band_corner_any(v, uvl, nl):
    for l in v.link_loops:
        if abs(l.face.normal.y) < 0.999:
            return l[uvl].uv.x
    raise FixError('no band corner at %s' % tuple(v.co))


def fill_footprint():
    """The points every head turn up to FILL_TURN keeps under the tab's flat bottom (|x'| <= FILL_XC,
    |y'| <= FILL_YC): arcs of radius FILL_YC round the y axis and FILL_XC round the x axis, joined by tangent
    lines. CCW seen from above, starting on the +x axis."""
    t = math.radians(FILL_TURN)
    ax = math.asin(FILL_YC / FILL_XC) - t                 # where the x arc meets the tangent line
    need(ax > 0.05, 'fill footprint: turn range too wide for the tab')
    ay = math.pi / 2 - t                                  # where the y arc meets it
    pts = []
    for q in range(4):
        base = q * math.pi / 2
        r, a0, a1 = (FILL_XC, -ax, ax) if q % 2 == 0 else (FILL_YC, -t, t)
        c = math.pi / 2 * (q % 2 == 1) + (math.pi if q >= 2 else 0)
        steps = 4 if q % 2 == 0 else 6
        for k in range(steps + 1):
            a = c + a0 + (a1 - a0) * k / steps
            pts.append((r * math.cos(a), r * math.sin(a)))
    # clip to |y| <= FILL_YCAP, so the fill stays over the torso's flat top
    for sgn in (1, -1):
        out = []
        for i in range(len(pts)):
            p, q = pts[i - 1], pts[i]
            pin, qin = sgn * p[1] <= FILL_YCAP, sgn * q[1] <= FILL_YCAP
            if pin != qin:
                t = (sgn * FILL_YCAP - p[1]) / (q[1] - p[1])
                out.append((p[0] + t * (q[0] - p[0]), sgn * FILL_YCAP))
            if qin:
                out.append(q)
        pts = out
    return pts


def build_torso_fill(bm, dl, g_sp2, uvl, nl, puv):
    """A closed prism on fill_footprint() from inside the torso up into the tab, weighted to JNT_spine_02. Every
    turn up to FILL_TURN keeps it inside the tab above the torso; when the head nods, it shows under the lifted
    edge of the tab and narrows the opening there."""
    pts = fill_footprint()
    n = len(pts)
    rings = []
    for z in (FILL_BOTTOM, FILL_TOP):
        ring = []
        for x, y in pts:
            v = bm.verts.new((x, y, z))
            v[dl][g_sp2] = 1.0
            ring.append(v)
        rings.append(ring)
    # side normals: the two edge normals averaged where the outline bends gently, each edge's own at the clip's kinks
    edge_n = [Vector((pts[(i + 1) % n][1] - pts[i][1], -(pts[(i + 1) % n][0] - pts[i][0]), 0)).normalized()
              for i in range(n)]                                      # edge i runs from point i to point i + 1
    for i in range(n):
        j = (i + 1) % n
        ends = []
        for k, other in ((i, i - 1), (j, j)):                         # this edge's normal at point k, or the mean
            e, f = edge_n[i], edge_n[other % n]
            ends.append((e + f).normalized() if e.dot(f) > math.cos(math.radians(25)) else e)
        vs = [rings[0][i], rings[0][j], rings[1][j], rings[1][i]]
        nrs = [ends[0], ends[1], ends[1], ends[0]]
        new_face(bm, vs, edge_n[i], uvl, nl, [puv(v) for v in vs], nrs)
    for k, want in ((0, Vector((0, 0, -1))), (1, Vector((0, 0, 1)))):
        for t in earclip(pts):
            vs = [rings[k][i] for i in t]
            new_face(bm, vs, want, uvl, nl, [puv(v) for v in vs], [want] * 3)


def build_tab_outline(arch_pts):
    """The tab's outermost outline, CCW seen from the front: the shipped arch, rounded bottom corners from its ends,
    a flat bottom edge with a vertex in the middle (the band's UV seam). -> ([(x, z, nx, nz, u[, u_seam])], seam, bevel scales)"""
    xr, zr = arch_pts[0][0], arch_pts[0][1]               # right end (x > 0)
    xl, zl = arch_pts[-1][0], arch_pts[-1][1]
    need(abs(xr + xl) < 1e-4 and abs(zr - zl) < 1e-4, 'arch is not symmetric')
    pts = [(x, z) for x, z, _ in arch_pts]
    us = [u for _, _, u in arch_pts]
    du = (us[-1] - us[0]) / sum(math.hypot(pts[i + 1][0] - pts[i][0], pts[i + 1][1] - pts[i][1])
                                for i in range(len(pts) - 1))
    c = zl - TAB_BOTTOM                                   # the corners start where the arch ends; their radius
    need(BEVEL < c < 0.02, 'tab corner radius %.4f out of range' % c)   # stays above the bevel inset: no fold
    need(FILL_XC < xr - c - 0.0015, 'fill wider than the tab bottom')
    left = []
    for t in range(1, 4):
        a = math.pi + (math.pi / 2) * t / 4
        left.append((xl + c + c * math.cos(a), TAB_BOTTOM + c + c * math.sin(a)))
    left.append((xl + c, TAB_BOTTOM))
    right = [(xr - c, TAB_BOTTOM)]
    for t in range(1, 4):
        a = 1.5 * math.pi + (math.pi / 2) * t / 4
        right.append((xr - c + c * math.cos(a), TAB_BOTTOM + c + c * math.sin(a)))
    loop = pts + left + [(0.0, TAB_BOTTOM)] + right       # CCW, starts at the arch's right end
    n = len(loop)
    seam = len(pts) + len(left)                           # index of the bottom middle vertex
    # u: arch as shipped, then on along the left side to the seam, and back from the right end to the seam
    u = us + [None] * (n - len(pts))
    for i in range(len(pts), seam + 1):
        u[i] = u[i - 1] + du * math.hypot(loop[i][0] - loop[i - 1][0], loop[i][1] - loop[i - 1][1])
    u_seam_right = None
    back = us[0]
    for i in range(n - 1, seam - 1, -1):
        nxt = loop[(i + 1) % n]
        back -= du * math.hypot(loop[i][0] - nxt[0], loop[i][1] - nxt[1])
        if i == seam:
            u_seam_right = back
        else:
            u[i] = back
    out = []
    for i in range(n):
        p0, p1, p2 = loop[i - 1], loop[i], loop[(i + 1) % n]
        e0 = Vector((p1[1] - p0[1], -(p1[0] - p0[0]))).normalized()
        e1 = Vector((p2[1] - p1[1], -(p2[0] - p1[0]))).normalized()
        nn = (e0 + e1).normalized()
        out.append((p1[0], p1[1], nn.x, nn.y, u[i]))
    out[seam] = out[seam] + (u_seam_right,)
    # bevel scale: full on the arch and sides, TAB_BOTTOM_BEVEL along the bottom, eased round the corners
    scales = [1.0] * n
    b = TAB_BOTTOM_BEVEL
    for t, i in enumerate(range(len(pts), len(pts) + len(left))):
        scales[i] = 1 - (1 - b) * (t + 1) / len(left)
    scales[seam] = b
    for t, i in enumerate(range(seam + 1, n)):
        scales[i] = b + (1 - b) * t / (n - seam - 1)
    return out, seam, scales


# ---------------------------------------------------------------- face, nose, antenna

def face_tris(o):
    me = o.data
    me.calc_loop_triangles()
    P = [o.matrix_world @ v.co for v in me.vertices]
    return [(P[t.vertices[0]], P[t.vertices[1]], P[t.vertices[2]]) for t in me.loop_triangles
            if t.normal.y < -0.2]


def front_y(tris, x, z):
    """The front-most y of the plate's front-facing triangles at (x, z), or None outside it."""
    best = None
    for a, b, c in tris:
        d = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)
        if abs(d) < 1e-14:
            continue
        l1 = ((x - a.x) * (c.z - a.z) - (c.x - a.x) * (z - a.z)) / d
        l2 = ((b.x - a.x) * (z - a.z) - (x - a.x) * (b.z - a.z)) / d
        if l1 < -1e-9 or l2 < -1e-9 or l1 + l2 > 1 + 1e-9:
            continue
        y = a.y + l1 * (b.y - a.y) + l2 * (c.y - a.y)
        best = y if best is None else min(best, y)
    return best


def footprint_samples(o, n=8):
    me = o.data
    me.calc_loop_triangles()
    out = []
    for t in me.loop_triangles:
        a, b, c = (me.vertices[i].co for i in t.vertices)
        for i in range(n + 1):
            for j in range(n + 1 - i):
                p = a + (b - a) * (i / n) + (c - a) * (j / n)
                out.append((p.x, p.z))
    return out


def move_verts(o, fn):
    me = o.data
    cn = corner_normals(me)
    for v in me.vertices:
        v.co = fn(Vector(v.co))
    set_normals(me, cn)


def fix_face(objs):
    plates = [o for n, o in objs.items() if n.startswith('face_')]
    need(len(plates) == 3, 'expected three face plates')
    for o in plates:
        move_verts(o, lambda c: Vector((c.x, c.y + PLATE_SHIFT, c.z)))
    tris = face_tris(objs['face_01_m'])
    report = {}
    for name, o in objs.items():
        if name.startswith(('eye_', 'mouth_')):
            ys = [front_y(tris, x, z) for x, z in footprint_samples(o)]
            need(all(y is not None for y in ys), '%s reaches outside the plate' % name)
            y_new = min(ys) - EYE_GAP
            move_verts(o, lambda c, y=y_new: Vector((c.x, y, c.z)))
            report[name] = round(y_new, 5)
        elif name.startswith(('angry_', 'cheak_', 'shock_', 'tear_')):
            def onto(c):
                y = front_y(tris, c.x, c.z)
                need(y is not None, '%s reaches outside the plate' % name)
                return Vector((c.x, y - OVERLAY_GAP, c.z))
            move_verts(o, onto)
            report[name] = 'plate - %.1f mm' % (OVERLAY_GAP * 1000)
    move_verts(objs['nose_m'], lambda c: Vector((c.x, min(c.y + PLATE_SHIFT, NOSE_BACK), c.z)))
    return report


def fix_antenna(o):
    zs = sorted({round(v.co.z, 4) for v in o.data.vertices})
    z0, z1 = zs[0], zs[1]
    base = [v for v in o.data.vertices if abs(v.co.z - z0) < 1e-4]
    nxt = [v for v in o.data.vertices if abs(v.co.z - z1) < 1e-4]
    need(len(base) == 24 and len(nxt) == 24, 'antenna rings not as expected')
    z0 = sum(v.co.z for v in base) / 24
    z1 = sum(v.co.z for v in nxt) / 24
    r0 = sum(math.hypot(v.co.x, v.co.y) for v in base) / 24
    r1 = sum(math.hypot(v.co.x, v.co.y) for v in nxt) / 24
    zn = z0 - ANTENNA_DROP
    scale = (r0 + (r0 - r1) * ANTENNA_DROP / (z1 - z0)) / r0
    move_verts(o, lambda c: Vector((c.x * scale, c.y * scale, c.z - ANTENNA_DROP)) if abs(c.z - z0) < 1e-4 else c)
    return dict(base_z=round(zn, 4), base_r=round(r0 * scale, 4))


# ---------------------------------------------------------------- arms

def fix_hand(o, side, spec):
    s = 1.0 if side == 'L' else -1.0
    me = o.data
    need(len(me.vertices) == 218, '%s is not the imported shape' % o.name)
    xs = sorted({round(s * v.co.x, 4) for v in me.vertices})
    root, ball = xs[0], 0.42                              # rod root cap (0.225) and the rod's last ring
    need(abs(root - 0.225) < 1e-4 and 0.42 in xs, '%s rod not as expected' % o.name)
    pivot = s * spec['JNT_arm_' + side]['head'][0]
    radius = max(math.hypot(v.co.y, v.co.z - 0.76) for v in me.vertices if abs(s * v.co.x - root) < 1e-4)
    cn = corner_normals(me)
    tip = set()
    for v in me.vertices:
        x = s * v.co.x
        if x <= ball + 1e-6:
            xn = pivot + (x - root) * (ball - pivot) / (ball - root)
            if abs(x - root) < 1e-4 and math.hypot(v.co.y, v.co.z - 0.76) < 1e-6:
                xn = pivot - radius                       # the cap's centre: the root becomes a short cone
                tip.add(v.index)
            v.co.x = s * xn
    need(len(tip) == 1, '%s: expected one root cap centre' % o.name)
    # cone normals: tip straight back along the rod, rim 45 degrees out
    for p in me.polygons:
        if tip & set(p.vertices):
            for li in p.loop_indices:
                v = me.vertices[me.loops[li].vertex_index]
                if v.index in tip:
                    cn[li] = Vector((-s, 0, 0))
                else:
                    r = Vector((0, v.co.y, v.co.z - 0.76)).normalized()
                    cn[li] = (Vector((-s, 0, 0)) + r).normalized()
    set_normals(me, cn)
    return dict(pivot=pivot, root_tip=round(pivot - radius, 4), rod_radius=round(radius, 4))


# ---------------------------------------------------------------- main

def main():
    spec = load_spec()
    rig = bpy.data.objects.get('model')
    need(rig is not None and rig.type == 'ARMATURE', 'no rig object "model"')
    need(abs(rig.data.bones['JNT_arm_L'].head_local.x - 0.11263) < 1e-4, 'this .blend is already fixed')
    objs = {o.name: o for o in bpy.data.objects if o.type == 'MESH'}
    for o in objs.values():
        need(max(abs(a - b) for ra, rb in zip(o.matrix_world, Matrix.Identity(4)) for a, b in zip(ra, rb)) < 1e-6,
             '%s is not at identity' % o.name)
    hidden = {n: (o.hide_viewport, o.hide_render) for n, o in objs.items()}

    print('FIX body_m', fix_body(objs['body_m'], spec))
    print('FIX face', fix_face(objs))
    print('FIX antenna', fix_antenna(objs['antena_m']))
    for side in ('L', 'R'):
        print('FIX hand_%s_m' % side, fix_hand(objs['hand_%s_m' % side], side, spec))
    for name, a, b in move_joints(rig, spec):
        print('FIX joint %-14s %s -> %s (armature space)' % (name, tuple(round(x, 5) for x in a),
                                                            tuple(round(x, 5) for x in b)))
    need(hidden == {n: (o.hide_viewport, o.hide_render) for n, o in objs.items()}, 'hidden flags changed')

    # the factory start-up's File Browser remembers the home folder, and a saved .blend would keep that path
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == 'FILE_BROWSER' and space.params is not None:
                    space.params.directory = b'/' * 512
                    space.params.directory = b'//'
    out = os.path.join(os.path.dirname(bpy.data.filepath), 'Pis_new.blend')
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=out, compress=False, relative_remap=True)
    with open(out, 'rb') as f:
        need(os.path.expanduser('~').encode() not in f.read(), 'the saved .blend holds the home folder path')
    print('FIX saved', out)


main()
