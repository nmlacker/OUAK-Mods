"""Pew, new style (Once Upon a Katamari look).  Builds everything from an empty scene:
textures, materials, meshes, the rig from ../pew_rig_spec.json, weights, renders, the .blend and the FBX.

    blender -b --factory-startup --python-exit-code 1 --python build_pew_new.py

Units are Blender metres, Z up, Pew faces -Y, +X is their left.  Every path is resolved from this file.
All geometry and every pixel is made here; nothing is read from any game file.

Pew is the fan-made "cousin with a gun" from 2017: the Prince's head drawn as a grey pistol.  The slide is the head
(rigid on JNT_head) with the face on its muzzle end, the grip is the body, and thin grey arms and legs come out of the
grip.  See ../Pew_design_sheet.md.  The helpers (texture painting, mesh builder, rig, weights, export) follow
Burda's build_burda_new.py.

Everything above main() is plain Python and numpy, so preview_pew_new.py can build the meshes and paint the textures
without Blender.
"""
import sys, os, json, math, glob
import numpy as np

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

TEX_DIR = os.path.join(HERE, 'textures')
REN_DIR = os.path.join(HERE, 'renders')
RIG_SPEC = os.path.join(os.path.dirname(HERE), 'pew_rig_spec.json')
BLEND = os.path.join(HERE, 'Pew_new.blend')
FBX = os.path.join(HERE, 'Pew_new.fbx')
BODY_TEX = 'pew_new_body'
FACE_TEX = 'pew_new_face'

# ---------------------------------------------------------------- design sheet colours
GREY_L, GREY, GREY_S = '#DCDDDA', '#C3C5C2', '#9C9E9B'      # top light, sampled side grey, shade
METAL_GREY = [(0.0, '#26282B'), (0.25, '#4E5257'), (0.50, '#868B91'), (0.72, '#B8BDC2'), (0.88, '#E2E6EA'), (1.0, '#FFFFFF')]
RING_BLACK, RING_W = '#0E0E10', 0.016                        # the thick black square ring round his face, and its width
GROOVE, GROOVE_HI = '#16181A', '#E4E5E2'                    # panel lines (made darker on request) and the light lip beside them
UNDER = '#2C2D2B'                                           # undersides (the black edge in the reference)
LIMB, LIMB_S = '#7E807D', '#5E605D'                         # arms, legs, feet, trigger
FACE, FACE_SH = '#FBE3CC', '#F0CDB0'                        # the Prince's peach face
CHEEKS, PRINCE_EYE = '#F6A0A0', '#6E7078'                    # the Prince's blush and grey eyes
NOSE, LIPS, LIPS_D, EYES = '#F06A28', '#D82418', '#9E1610', '#100C0A'
ANT_Y, ANT_R = '#FEF404', '#F3131B'                    # the Prince's antenna colours, as on Burda
GLOOM, FLUSH = ('#5E77A0', 0.80), ('#E2564A', 0.60)          # plate tints from the top edge: colour, strength
GLOOM_FADE, FLUSH_FADE = 0.060, 0.045                        # how far down each tint fades out (metres)
EYE_W, BLUSH, TEAR = '#FFFFFF', '#FF8AA8', '#8FD0D6'
ANGER, SHOCK = '#7A82EE', '#1C2550'

# ---------------------------------------------------------------- sizes (metres)
N = 24                      # segments round every round section
# The reference picture is read at 0.004 m per pixel (its side view: slide 217 x 84 px, grip 91 x 81 px, legs 28 px,
# antenna 28 px), which makes Pew 0.90 tall.  Their joints are placed on that picture (../pew_rig_spec.json).
# slide (the head): a rounded box along Y.  Cross-section: XW half width, Z0..Z1, corner radii RT (top), RBC (bottom)
XW, Z0, Z1 = 0.135, 0.445, 0.785
RT, RBC = 0.058, 0.022
Y_FRONT, Y_BACK = -0.695, 0.185                     # muzzle end (face) and rear end, flush with the grip's back
EB = 0.018                                          # both ends rounded over this radius
LEN = Y_BACK - Y_FRONT
ZC = (Z0 + Z1) / 2                                  # the slide's middle height (JNT_head sits over the grip at 0.615)
# side panel lines: a U on each side (two legs over the top, joined low on the side), as in the reference
GR_YA, GR_YB, GR_Z = -0.009, -0.457, 0.581
GR_W = 0.0065
# grip (the body): a rounded box standing under the slide's rear, its back flush with the slide's
GX, GY0, GY1, GR = 0.115, -0.185, 0.185, 0.040      # half width, front, back, corner radius (seen from above)
GZ0, GZ1, GEB = 0.115, 0.470, 0.018                 # bottom, top (inside the slide), bottom edge radius
GP = (-0.125, 0.140, 0.165, 0.395, 0.026)           # grip panel line on each side: y0, y1, z0, z1, corner radius
# face plate: a rounded square on the muzzle end (a superellipse of power FP), slightly domed
FX, FZ, FA, FB = 0.0, 0.595, 0.075, 0.075           # centre and half sizes (made a little smaller on request)
FP = 5.0                                            # 2 would be an oval; 5 is a square with soft corners
RECESS = 0.0381                                     # the face sits an inch and a half back into the muzzle end (on request)
YP0 = Y_FRONT + RECESS                              # the plate's rim, sunk into the end cap
DOME = 0.004                                        # how far its middle stands forward of the rim
OFF_EYE, OFF_OVL = 0.0015, 0.0008                   # eyes and mouths, overlays: in front of the plate
# antenna
# antenna: the Prince's, at his sizes (as Burda's build makes it): a yellow cone 0.030 in radius at its foot, rising to a
# point inside a red ball 0.040 in radius whose centre stands 0.126 above the head top
ANT_CONE_R = 0.030
ANT_BALL = 0.040
# trigger: a curved blade hanging from the slide's underside just in front of the grip, its tip curling back toward
# the grip: (y, z) along its middle, from inside the slide down to the tip
TRIGGER = [(-0.266, 0.458), (-0.266, 0.420), (-0.263, 0.384), (-0.254, 0.353), (-0.240, 0.330), (-0.224, 0.315), (-0.208, 0.308)]
TRIG_R = (0.0190, 0.0150, 0.0095)                   # half width (x), half thickness at the top and at the tip

# ---------------------------------------------------------------- small maths
def hx(s):
    s = s.lstrip('#')
    return np.array([int(s[i:i + 2], 16) / 255.0 for i in (0, 2, 4)])


def sstep(e0, e1, x):
    t = np.clip((np.asarray(x, dtype=np.float64) - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def mix3(a, b, t):
    a = np.asarray(a, dtype=np.float64); b = np.asarray(b, dtype=np.float64)
    t = np.asarray(t, dtype=np.float64)[..., None]
    return a * (1.0 - t) + b * t


def vsub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def vdot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def rrect_profile(hw, lo, hi, r_lo, r_hi, segs):
    """A rounded rectangle in the (a, b) plane: |a| <= hw, lo <= b <= hi, bottom corners r_lo, top corners r_hi.
    segs: (bottom half, bottom corner, side, top corner, top half) segment counts.  Starts at the bottom middle and
    runs toward +a.  -> list of (a, b, na, nb) points, closed (the last equals the first)."""
    nb0, nc0, ns, nc1, nt0 = segs
    pts = []

    def add(a, b, na, nb):
        pts.append((a, b, na, nb))
    for k in range(nb0):
        add((hw - r_lo) * k / nb0, lo, 0.0, -1.0)
    for k in range(nc0):
        t = -math.pi / 2 + (math.pi / 2) * k / nc0
        add(hw - r_lo + r_lo * math.cos(t), lo + r_lo + r_lo * math.sin(t), math.cos(t), math.sin(t))
    for k in range(ns):
        add(hw, lo + r_lo + (hi - r_hi - lo - r_lo) * k / ns, 1.0, 0.0)
    for k in range(nc1):
        t = (math.pi / 2) * k / nc1
        add(hw - r_hi + r_hi * math.cos(t), hi - r_hi + r_hi * math.sin(t), math.cos(t), math.sin(t))
    for k in range(2 * nt0):
        add((hw - r_hi) * (1.0 - 2.0 * k / (2 * nt0)), hi, 0.0, 1.0)
    for k in range(nc1):
        t = math.pi / 2 + (math.pi / 2) * k / nc1
        add(-(hw - r_hi) + r_hi * math.cos(t), hi - r_hi + r_hi * math.sin(t), math.cos(t), math.sin(t))
    for k in range(ns):
        add(-hw, hi - r_hi - (hi - r_hi - lo - r_lo) * k / ns, -1.0, 0.0)
    for k in range(nc0):
        t = math.pi + (math.pi / 2) * k / nc0
        add(-(hw - r_lo) + r_lo * math.cos(t), lo + r_lo + r_lo * math.sin(t), math.cos(t), math.sin(t))
    for k in range(nb0):
        add(-(hw - r_lo) * (1.0 - k / nb0), lo, 0.0, -1.0)
    pts.append(pts[0])
    return pts


def arc_table(prof):
    ab = np.array([(p[0], p[1]) for p in prof])
    s = np.concatenate([[0.0], np.cumsum(np.hypot(*np.diff(ab, axis=0).T))])
    return s


SLIDE_PROF = rrect_profile(XW, Z0, Z1, RBC, RT, (2, 4, 4, 6, 3))           # (x, z, nx, nz)
SLIDE_S = arc_table(SLIDE_PROF); SLIDE_L = float(SLIDE_S[-1])
GRIP_PROF = rrect_profile(GX, GY0, GY1, GR, GR, (2, 5, 2, 5, 2))            # (x, y, nx, ny): starts at the front middle
GRIP_S = arc_table(GRIP_PROF); GRIP_L = float(GRIP_S[-1])
# a dense copy of the slide profile, for the texture: arc length -> point and normal
_dense = rrect_profile(XW, Z0, Z1, RBC, RT, (40, 60, 40, 120, 40))
_DS = arc_table(_dense); _DS = _DS / _DS[-1] * SLIDE_L
_DP = np.array(_dense)
_dg = rrect_profile(GX, GY0, GY1, GR, GR, (40, 80, 60, 80, 40))
_GS = arc_table(_dg); _GS = _GS / _GS[-1] * GRIP_L
_GP = np.array(_dg)

# ---------------------------------------------------------------- atlas layout (pixels, top-left origin)
BODY_PX = 512
BODY_BLOCKS = {
    'slide':      (4, 4, 384, 240),       # U round the slide's section from the bottom middle, V from the muzzle back
    'grip':       (4, 256, 288, 168),     # U round the grip from the front middle, V from its top down
    'cap':        (400, 4, 104, 112),     # the rear end cap, mapped flat in x and z
    'cap_front':  (320, 262, 72, 92),     # the muzzle end cap round his face, with the black square ring
    'face':       (400, 128, 48, 64),
    'face_gloom': (456, 128, 48, 64),
    'face_flush': (400, 204, 48, 64),
    'limb':       (456, 204, 16, 48),
    'antY':       (480, 204, 12, 40),
    'antR':       (400, 280, 24, 24),
    'nose':       (432, 280, 16, 24),
    'trigger':    (456, 280, 16, 32),
    'under':      (480, 280, 12, 12),
    'plate_rim':  (304, 256, 8, 8),
}
# face atlas cells, by the mesh that shows them: (x, y, w, h) in pixels and the size of the quad they map to, in metres
FACE_PX = 512
PPM = 2400.0                               # about 2400 px per metre
FACE_SIZES = [                             # mesh -> the size of the quad its cell maps to, in metres
    ('eye_01_m', 0.0300, 0.0220), ('eye_02_m', 0.0300, 0.0220), ('eye_03_m', 0.0300, 0.0220), ('eye_04_m', 0.0300, 0.0220),
    ('eye_05_m', 0.0300, 0.0220), ('eye_aim_m', 0.0300, 0.0220),
    ('mouth_01_m', 0.0440, 0.0300), ('mouth_02_m', 0.0300, 0.0240), ('mouth_03_m', 0.0300, 0.0240),
    ('mouth_04_m', 0.0420, 0.0240), ('mouth_05_m', 0.0320, 0.0360), ('mouth_06_m', 0.0220, 0.0220),
    ('mouth_07_m', 0.0280, 0.0340), ('mouth_08_m', 0.0400, 0.0160), ('mouth_09_m', 0.0340, 0.0170),
    ('mouth_10_m', 0.0300, 0.0240), ('mouth_11_m', 0.0400, 0.0300),
    ('angry_01_m', 0.0240, 0.0240), ('shock_01_m', 0.0240, 0.0300), ('cheak_01_m', 0.0280, 0.0180),
    ('tear_01_m', 0.0100, 0.0480)]


def pack_cells(sizes, size, gap=8):
    """Shelf-packs the cells left to right, top to bottom, gap pixels apart.  -> name: ((x, y, w, h), (wm, hm))"""
    out = {}; x = y = gap; row = 0
    for name, wm, hm in sizes:
        w, h = int(round(wm * PPM)), int(round(hm * PPM))
        if x + w + gap > size:
            x = gap; y += row + gap; row = 0
        out[name] = ((x, y, w, h), (wm, hm))
        x += w + gap; row = max(row, h)
    assert y + row + gap <= size
    return out


FACE_CELLS = pack_cells(FACE_SIZES, FACE_PX)


def check_layout(rects, size, gut=3):
    """Every rect and its gutter lies inside the atlas, and no gutter covers another rect's gutter."""
    grow = lambda r: (r[0] - gut, r[1] - gut, r[0] + r[2] + gut, r[1] + r[3] + gut)
    hit = lambda a, b: a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]
    for n, r in rects.items():
        g = grow(r)
        assert g[0] >= 0 and g[1] >= 0 and g[2] <= size and g[3] <= size, n
        for m, s in rects.items():
            if m != n:
                assert not hit(g, grow(s)), (n, m)


def uvb(name, u, v):
    x, y, w, h = BODY_BLOCKS[name]
    return ((x + u * w) / BODY_PX, 1.0 - (y + v * h) / BODY_PX)


def uvf(name, u, v):
    x, y, w, h = FACE_CELLS[name][0]
    return ((x + u * w) / FACE_PX, 1.0 - (y + v * h) / FACE_PX)


# ---------------------------------------------------------------- texture painting
SS = 4   # supersampling


def paint(canvas, rect, fn, wrap_u=False, gut=3):
    """Fill rect plus a gutter with fn(U, V) -> rgb (or rgba).  U, V run 0..1 over the rect, clamped (or wrapped) in
    the gutter."""
    x, y, w, h = rect
    size = canvas.shape[0] // SS
    x0, x1 = max(x - gut, 0), min(x + w + gut, size)
    y0, y1 = max(y - gut, 0), min(y + h + gut, size)
    px = (np.arange(x0 * SS, x1 * SS) + 0.5) / SS
    py = (np.arange(y0 * SS, y1 * SS) + 0.5) / SS
    U = (px - x) / w; V = np.clip((py - y) / h, 0.0, 1.0)
    U = U % 1.0 if wrap_u else np.clip(U, 0.0, 1.0)
    U, V = np.meshgrid(U, V)
    out = fn(U, V)
    canvas[y0 * SS:y1 * SS, x0 * SS:x1 * SS, :out.shape[2]] = out


def band(d, hw, soft):
    """1 inside |d| < hw, fading to 0 over soft."""
    return 1.0 - sstep(hw - soft / 2, hw + soft / 2, np.abs(d))


def groove(c, d_line, d_side):
    """An inset panel line: a dark groove where |d_line| is small, and a light lip on the side d_side > 0 points to.
    d_side is the signed distance across the line (the same as d_line where the line is straight)."""
    g = band(d_line, GR_W / 2, 0.0022)
    lip = band(d_side - GR_W * 0.95, GR_W * 0.45, 0.0020) * (1.0 - g)
    c = mix3(c, hx(GROOVE_HI), 0.55 * lip)
    return mix3(c, hx(GROOVE), 1.0 * g)


def ramp(v, stops):
    xs = [a for a, _ in stops]; cs = np.array([hx(c) for _, c in stops])
    v = np.clip(v, 0.0, 1.0)
    return np.stack([np.interp(v, xs, cs[:, k]) for k in range(3)], axis=-1)


def metal_flat(z):
    """A polished flat side, by height: dark ground below, a sharp bright horizon band, softer sky above."""
    t = (np.asarray(z) - Z0) / (Z1 - Z0)
    return np.interp(t, [-0.6, 0.0, 0.30, 0.50, 0.56, 0.62, 0.80, 1.0, 1.6], [0.20, 0.22, 0.32, 0.55, 0.95, 0.60, 0.52, 0.66, 0.70])


def metal_round(n):
    """A polished rounded edge, by how far its normal points up (n: -1 down .. 1 up): sharp glint on the upper corner."""
    return np.interp(n, [-1.0, -0.5, 0.0, 0.35, 0.55, 0.68, 0.85, 1.0], [0.12, 0.22, 0.45, 0.62, 1.00, 0.70, 0.62, 0.74])


def f_slide(U, V):
    s = U * SLIDE_L
    x = np.interp(s, _DS, _DP[:, 0]); z = np.interp(s, _DS, _DP[:, 1])
    nx = np.interp(s, _DS, _DP[:, 2]); nz = np.interp(s, _DS, _DP[:, 3])
    y = Y_FRONT + V * LEN
    flatside = np.abs(nx) > 0.97
    v = np.where(flatside, metal_flat(z), metal_round(nz))                 # metallic grey
    v = v + 0.06 * np.sin(2.0 * math.pi * (y - Y_FRONT) / LEN)            # a slow sheen along it
    c = ramp(v, METAL_GREY)
    c = c * (1.0 - 0.10 * sstep(0.80, 1.0, np.abs(2.0 * V - 1.0)))[..., None]   # darker bands at both ends
    # panel lines, in surface distances: the legs run across the top and down each side to the low line
    side = np.abs(nx) > 0.5
    top_or_upper = (z > GR_Z - GR_W) | (nz > 0.5)
    for ya, toward in ((GR_YA, 1.0), (GR_YB, -1.0)):
        leg = top_or_upper & (nz > -0.2)
        d = (y - ya) * toward
        c = np.where(leg[..., None], groove(c, d, -d), c)      # lip on the inside of the U
    low = side & (y < GR_YA + GR_W / 2) & (y > GR_YB - GR_W / 2)
    d = z - GR_Z
    c = np.where(low[..., None], groove(c, d, -d), c)          # lip under the line
    # the undersides go dark, as the black edge under the slide in the reference
    c = mix3(c, hx(UNDER), sstep(-0.55, -0.92, nz))
    return c


def f_cap(U, V):
    x = (2.0 * U - 1.0) * XW; z = Z1 - V * (Z1 - Z0)
    c = ramp(metal_flat(z) + 0.05 * x / XW, METAL_GREY)
    return mix3(c, hx(UNDER), sstep(Z0 + 0.010, Z0 + 0.001, z))


def f_cap_front(U, V):
    """The muzzle end round his face: metallic grey, with a thick black square ring hugging the face plate."""
    x = (2.0 * U - 1.0) * XW; z = Z1 - V * (Z1 - Z0)
    c = f_cap(U, V)
    ro = (np.abs((x - FX) / (FA + RING_W)) ** 9 + np.abs((z - FZ) / (FB + RING_W)) ** 9) ** (1.0 / 9)   # outer: near square
    ring = sstep(1.0 + 0.004, 1.0 - 0.004, ro)
    c = mix3(c, hx(RING_BLACK), ring)
    hi = np.exp(-((ro - 0.93) / 0.025) ** 2) * sstep(FZ - 0.02, FZ + FB, z)          # a faint gloss on the ring's top
    return mix3(c, hx('#3A3C40'), 0.6 * hi * ring)


def f_grip(U, V):
    s = U * GRIP_L
    x = np.interp(s, _GS, _GP[:, 0]); y = np.interp(s, _GS, _GP[:, 1])
    nx = np.interp(s, _GS, _GP[:, 2]); ny = np.interp(s, _GS, _GP[:, 3])
    z = GZ1 - V * (GZ1 - GZ0)
    flatside = np.abs(nx) > 0.97
    vz = np.interp(z, [GZ0, GZ0 + 0.08, 0.30, 0.34, 0.37, 0.43, GZ1], [0.20, 0.30, 0.52, 0.92, 0.58, 0.40, 0.30])
    v = np.where(flatside, vz, metal_round(-ny * 0.9) * 0.85 + 0.15 * vz)
    c = ramp(v, METAL_GREY)
    c = c * (1.0 - 0.28 * sstep(Z0 - 0.045, Z0 - 0.002, z))[..., None]    # in the shadow under the slide
    # the grip panel: a rounded rectangle of panel line on each side
    y0, y1, z0, z1, r = GP
    cy, cz, hy, hz = (y0 + y1) / 2, (z0 + z1) / 2, (y1 - y0) / 2, (z1 - z0) / 2
    qy = np.abs(y - cy) - (hy - r); qz = np.abs(z - cz) - (hz - r)
    sd = np.hypot(np.maximum(qy, 0), np.maximum(qz, 0)) + np.minimum(np.maximum(qy, qz), 0) - r   # < 0 inside
    side = np.abs(nx) > 0.6
    c = np.where(side[..., None], groove(c, sd, -sd), c)
    return mix3(c, hx(UNDER), sstep(GZ0 + 0.012, GZ0 + 0.002, z))


def sq_norm(X, Y):
    """The plate's own radius: 1 on its rim."""
    return (np.abs(X) ** FP + np.abs(Y) ** FP) ** (1.0 / FP)


def f_face(U, V):
    X = 2.0 * U - 1.0; Y = 1.0 - 2.0 * V                    # -1..1 over the plate's box
    r = sq_norm(X, Y)
    c = mix3(hx(FACE), hx(FACE_SH), 0.45 * sstep(0.82, 1.0, r))           # a little darker at the rim
    c = mix3(c, hx(FACE_SH), 0.15 * sstep(-0.4, -1.0, Y))                  # and toward the chin
    for sx in (-1.0, 1.0):                                                  # the Prince's rosy cheeks
        e = ((X - sx * 0.62) / 0.22) ** 2 + ((Y + 0.16) / 0.14) ** 2
        c = mix3(c, hx(CHEEKS), 0.50 * np.clip(1.0 - e, 0.0, 1.0) ** 1.5)
    return np.clip(c * (1.0 + 0.04 * np.exp(-((Y - 0.35) / 0.35) ** 2))[..., None], 0, 1)


def f_face_tint(tint, fade):
    """The plate with a tint that starts at its top edge and fades out downward over fade metres."""
    col, k = tint

    def f(U, V):
        X = 2.0 * U - 1.0; Y = 1.0 - 2.0 * V
        top = FB * np.clip(1.0 - np.abs(X) ** FP, 0.0, 1.0) ** (1.0 / FP)  # the rim's height above the centre
        t = np.clip((top - Y * FB) / fade, 0.0, 1.0)
        return mix3(f_face(U, V), hx(col), k * (1.0 - t) ** 1.4)
    return f


def f_limb(U, V): return ramp(np.interp(V, [0.0, 0.18, 0.30, 0.45, 0.75, 1.0], [0.62, 0.95, 0.55, 0.42, 0.25, 0.15]) * 0.85, METAL_GREY)
def f_antY(U, V): return mix3(hx(ANT_Y), hx('#F0B800'), sstep(0.55, 1.0, V))
def f_antR(U, V): return mix3(mix3(hx('#FF5A4E'), hx(ANT_R), sstep(0.0, 0.4, V)), hx('#B90D14'), sstep(0.6, 1.0, V))
def f_nose(U, V): return mix3(mix3(hx('#FF9A50'), hx(NOSE), sstep(0.1, 0.6, V)), hx('#B8400E'), sstep(0.7, 1.0, V))
def f_trigger(U, V): return ramp(np.interp(V, [0.0, 0.25, 0.40, 0.70, 1.0], [0.55, 0.90, 0.50, 0.30, 0.18]), METAL_GREY)
def flat(col): return lambda U, V: np.broadcast_to(hx(col), U.shape + (3,))


def downsample(canvas):
    n = canvas.shape[0] // SS
    return canvas.reshape(n, SS, n, SS, canvas.shape[2]).mean(axis=(1, 3))


def save_png(name, arr, alpha):
    """arr: H x W x 4, rows top-down, sRGB values 0..1.  Saved as an 8-bit PNG in textures/."""
    import bpy
    h, w = arr.shape[:2]
    img = bpy.data.images.new(name + '_paint', w, h, alpha=alpha)
    img.pixels.foreach_set(np.ascontiguousarray(arr[::-1], dtype=np.float32).ravel())
    path = os.path.join(TEX_DIR, name + '.png')
    img.filepath_raw = path; img.file_format = 'PNG'; img.save()
    bpy.data.images.remove(img)
    return path


def paint_body_array():
    cv = np.zeros((BODY_PX * SS, BODY_PX * SS, 3))
    cv[:] = hx(GREY_S)
    P = lambda n, f, **k: paint(cv, BODY_BLOCKS[n], f, **k)
    P('slide', f_slide, wrap_u=True)
    P('grip', f_grip, wrap_u=True)
    P('cap', f_cap); P('cap_front', f_cap_front); P('face', f_face)
    P('face_gloom', f_face_tint(GLOOM, GLOOM_FADE)); P('face_flush', f_face_tint(FLUSH, FLUSH_FADE))
    P('limb', f_limb); P('antY', f_antY); P('antR', f_antR); P('nose', f_nose); P('trigger', f_trigger)
    P('under', flat(UNDER)); P('plate_rim', flat(RING_BLACK))
    rgb = downsample(cv)
    return np.dstack([rgb, np.ones(rgb.shape[:2])])


def m_ellipse(X, Y, a, b, cx=0.0, cy=0.0): return ((X - cx) / a) ** 2 + ((Y - cy) / b) ** 2 <= 1.0


def frame(X, Y, deg):
    """Coordinates in a frame turned by deg (counter-clockwise)."""
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return X * c + Y * s, -X * s + Y * c


def m_rbox(X, Y, hw, hh, r):
    qx = np.abs(X) - (hw - r); qy = np.abs(Y) - (hh - r)
    return np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - r <= 0


def dist_polyline(X, Y, pts):
    """Distance to a polyline and the interpolated per-point value (half width)."""
    best = np.full(X.shape, 1e9); val = np.zeros(X.shape)
    for (ax, ay, aw), (bx, by, bw) in zip(pts[:-1], pts[1:]):
        dx, dy = bx - ax, by - ay
        L2 = dx * dx + dy * dy
        t = np.clip(((X - ax) * dx + (Y - ay) * dy) / max(L2, 1e-12), 0.0, 1.0)
        d = np.hypot(X - (ax + t * dx), Y - (ay + t * dy))
        w = aw + (bw - aw) * t
        upd = d - w < best - val
        best = np.where(upd, d, best); val = np.where(upd, w, val)
    return best, val


def line(X, Y, pts, hw):
    """A stroke of half width hw along the points, with round ends."""
    d, w = dist_polyline(X, Y, [(x, y, hw) for x, y in pts])
    return d <= w


def dome(X, Y, a, yb, h, r):
    """Open mouth with a round top: half width a, bottom at yb, height h, bottom corners rounded by r."""
    t = np.clip(np.abs(X) / a, 0.0, 1.0)
    top = yb + h * (1.0 - t ** 2.4) ** (1.0 / 2.4)
    return (np.abs(X) <= a) & (Y >= yb) & (Y <= top) & m_rbox(X, Y - (yb + h / 2), a, h / 2, r)


def face_shapes():
    """mesh name -> (alpha function over metres from the cell centre, colour).  An alpha function may return a mask,
    a float alpha in 0..1, or (alpha, rgb) for a cell of more than one colour.  Eyes, the blush and the tears are drawn
    as their right one (left in a front picture), whose inner end is on the +X side; the mesh mirrors the UVs for the
    other one.  The two one-sided marks sit on their left."""
    def angry_almond(X, Y, lid0, slope):
        """The reference's glare: a solid black oval, a little pointed at the outer end, cut by a straight lid that slopes
        down toward the nose (the inner end is on +X)."""
        x, y = frame(X, Y, 8.0)
        a, b = 0.0120, 0.0072
        sx = x + 0.0012                                       # fuller toward the nose, pointed toward the outside
        k = np.where(sx < 0, 1.0 + 0.55 * np.clip(-sx / a, 0, 1), 1.0)
        oval = (sx / a) ** 2 + (y * k / b) ** 2 <= 1.0
        return oval & (Y <= lid0 - slope * X)

    # Every eye glares: a lid line that slopes down toward the nose cuts each one, as on the half-lidded glare.
    def lid(X, Y, lid0, slope): return Y <= lid0 - slope * X

    def eye_01(X, Y):                     # the Prince's eyes: grey almonds, their outer ends tilted up
        a = math.radians(14.0)
        xr = X * math.cos(a) - Y * math.sin(a); yr = X * math.sin(a) + Y * math.cos(a)
        L = 0.0115
        half = 0.0036 * np.clip(1.0 - (xr / L) ** 2, 0.0, 1.0) ** 0.75
        m = (np.abs(xr) <= L) & (np.abs(yr) <= half)
        return m.astype(np.float64), np.broadcast_to(hx(PRINCE_EYE), X.shape + (3,))
    def eye_02(X, Y): return angry_almond(X, Y, -0.0018, 0.30)        # half-closed: the lid lower still

    def eye_03(X, Y):                     # closed: a short line, sloping down toward the nose
        x, y = frame(X, Y, -22.0)
        return line(x, y, [(-0.0110, 0.0), (0.0110, 0.0)], 0.0019)

    def eye_04(X, Y): return m_ellipse(X, Y, 0.0080, 0.0090) & lid(X, Y, 0.0030, 0.32)   # wide dots, still glaring

    def eye_05(X, Y):                     # white almonds with a thin black outline
        x, y = frame(X, Y, -18.0)
        a = 0.0120
        u = np.clip(1.0 - (x / a) ** 2, 0.0, None)
        inner = (np.abs(x) <= a) & (y <= 0.0042 * u ** 0.6 + 0.0005) & (y >= -0.0062 * u ** 0.7) & lid(X, Y, -0.0002, 0.30)
        a2 = a + 0.0014
        u2 = np.clip(1.0 - (x / a2) ** 2, 0.0, None)
        outer = (np.abs(x) <= a2) & (y <= 0.0042 * u2 ** 0.6 + 0.0019) & (y >= -0.0062 * u2 ** 0.7 - 0.0014) & lid(X, Y, 0.0012, 0.30)
        alpha = outer.astype(np.float64)
        rgb = np.where(inner[..., None], hx(EYE_W), hx(EYES))
        return alpha, rgb

    def eye_aim(X, Y):                    # aiming: their right eye squeezed shut, a line bent into a squint
        pts = [(0.0105 * t, 0.0016 * (1 - abs(t)) - 0.0010 * t) for t in np.linspace(-1, 1, 9)]
        return line(X, Y, pts, 0.0020)

    def lips(X, Y, w, h, gap):
        """The reference's pout: an upper lip of two soft humps with a small dip between them, a fuller lower lip
        pushed out a little to one side, and a thin dark line where they meet."""
        up = (m_ellipse(X, Y, w * 0.56, h * 0.26, -w * 0.40, h * 0.17) | m_ellipse(X, Y, w * 0.56, h * 0.26, w * 0.40, h * 0.17)) & (Y >= 0)
        lo = m_ellipse(X, Y, w * 0.82, h * 0.36, w * 0.06, -h * 0.13) & (Y <= h * 0.02)
        part = line(X, Y, [(-w * 0.85, h * 0.04), (-w * 0.3, 0.0), (w * 0.3, 0.0), (w * 0.85, h * 0.04)], gap)
        lips_ = up | lo
        alpha = lips_.astype(np.float64)
        rgb = np.where((part & lips_)[..., None], hx(LIPS_D), hx(LIPS))
        shine = m_ellipse(X, Y, w * 0.26, h * 0.07, w * 0.18, -h * 0.22) & lo
        rgb = mix3(rgb, hx('#F2685C'), 0.6 * shine)
        return alpha, rgb

    def mouth_01(X, Y):                   # the Prince's mouth: a small red box, darker along its top
        m = m_rbox(X, Y, 0.0075, 0.0050, 0.0010)
        top = Y >= 0.0050 - 0.0019
        return m.astype(np.float64), np.where(top[..., None], hx('#8E1010'), hx('#E02818'))
    def mouth_02(X, Y): return m_rbox(X, Y, 0.0120, 0.0100, 0.0055)
    def mouth_03(X, Y): return dome(X, Y, 0.0130, -0.0095, 0.0190, 0.0028) & (Y >= -0.0095 + 0.0018 * (1.0 - (X / 0.0130) ** 2))

    def mouth_04(X, Y):
        yt = 0.0075
        return (Y <= yt) & m_ellipse(X, Y, 0.0195, 0.0190, 0.0, yt) & m_rbox(X, Y - (yt - 0.0110), 0.0195, 0.0110, 0.0030)

    def mouth_05(X, Y): return m_ellipse(X, Y, 0.0135, 0.0165)
    def mouth_06(X, Y): return m_ellipse(X, Y, 0.0085, 0.0085)

    def mouth_07(X, Y):
        yb = 0.0030
        d, w = dist_polyline(X, Y, [(0.0050, yb + 0.0010, 0.0028), (0.0052, yb - 0.0040, 0.0017), (0.0054, yb - 0.0110, 0.0028)])
        return dome(X, Y, 0.0110, yb, 0.0130, 0.0025) | (d <= w)

    def mouth_08(X, Y):
        return line(X, Y, [(x, 0.0016 * math.cos(2.0 * math.pi * x / 0.0140)) for x in np.linspace(-0.0170, 0.0170, 35)], 0.0024)

    def mouth_09(X, Y):
        return line(X, Y, [(0.0135 * t, -0.0030 + 0.0062 * (1 - t * t)) for t in np.linspace(-1, 1, 17)], 0.0024)

    def mouth_10(X, Y):
        xr = 0.0035
        ring = m_ellipse(X, Y, 0.0078, 0.0074, xr, 0.0) & ~m_ellipse(X, Y, 0.0031, 0.0024, xr, 0.0014)
        rc = 0.0078 + 0.0040 + 0.0018
        return ring | line(X, Y, [(xr - rc * math.cos(math.radians(a)), rc * math.sin(math.radians(a))) for a in np.linspace(-34.0, 34.0, 9)], 0.0018)

    def mouth_11(X, Y):
        yc = 0.0055
        m = m_rbox(X, Y - yc, 0.0160, 0.0042, 0.0042)
        for xc in (-0.0136, -0.0045, 0.0045, 0.0136):
            m |= m_ellipse(X, Y, 0.0050, 0.0060, xc, yc)
        d, w = dist_polyline(X, Y, [(0.0060, yc - 0.0042, 0.0024), (0.0062, yc - 0.0084, 0.0016), (0.0064, yc - 0.0128, 0.0025)])
        return m | (d <= w)

    def angry_01(X, Y):                   # anger mark: four arcs bowed toward the middle
        m = np.zeros(X.shape, dtype=bool)
        for k in range(4):
            t = math.radians(45.0 + 90.0 * k)
            cx, cy = 0.0068 * math.cos(t), 0.0068 * math.sin(t)
            m |= line(X, Y, [(cx - 0.0040 * math.cos(t + math.radians(a)), cy - 0.0040 * math.sin(t + math.radians(a)))
                             for a in np.linspace(-60.0, 60.0, 13)], 0.0017)
        return m

    def cheak_01(X, Y): return 0.55 * np.clip(1.0 - (X / 0.0135) ** 2 - (Y / 0.0085) ** 2, 0.0, 1.0) ** 2

    def shock_01(X, Y):                   # gloom lines: three strokes hanging from the top of the cell
        m = np.zeros(X.shape, dtype=bool)
        for xa, ln in ((-0.0070, 0.016), (0.0, 0.022), (0.0070, 0.018)):
            m |= line(X, Y, [(xa, 0.0125), (xa, 0.0125 - ln)], 0.0016)
        return m

    def tear_01(X, Y):
        hm = FACE_CELLS['tear_01_m'][1][1]
        top, bot = hm / 2 - 0.0020, -hm / 2 + 0.0035
        ys = np.linspace(top, bot, 41)
        d, w = dist_polyline(X, Y, [(0.0012 * math.sin(2.0 * math.pi * (top - y) / 0.0200), y, 0.0016 + 0.0009 * (top - y) / (top - bot))
                                    for y in ys])
        return d <= w

    return {'eye_01_m': (eye_01, EYES), 'eye_02_m': (eye_02, EYES), 'eye_03_m': (eye_03, EYES), 'eye_04_m': (eye_04, EYES),
            'eye_05_m': (eye_05, EYES), 'eye_aim_m': (eye_aim, EYES),
            'mouth_01_m': (mouth_01, EYES), 'mouth_02_m': (mouth_02, LIPS), 'mouth_03_m': (mouth_03, LIPS),
            'mouth_04_m': (mouth_04, LIPS), 'mouth_05_m': (mouth_05, LIPS), 'mouth_06_m': (mouth_06, LIPS),
            'mouth_07_m': (mouth_07, LIPS), 'mouth_08_m': (mouth_08, LIPS), 'mouth_09_m': (mouth_09, LIPS),
            'mouth_10_m': (mouth_10, LIPS), 'mouth_11_m': (mouth_11, LIPS),
            'angry_01_m': (angry_01, ANGER), 'cheak_01_m': (cheak_01, BLUSH), 'shock_01_m': (shock_01, SHOCK),
            'tear_01_m': (tear_01, TEAR)}


def cell_rgba(fn, col, wm, hm, U, V):
    inside = (U > 0.0) & (U < 1.0) & (V > 0.0) & (V < 1.0)
    r = fn((U - 0.5) * wm, (0.5 - V) * hm)
    if isinstance(r, tuple):
        a, rgb = r
    else:
        a, rgb = r, np.broadcast_to(hx(col), U.shape + (3,))
    a = np.clip(np.asarray(a, dtype=np.float64), 0.0, 1.0) * inside
    return np.dstack([rgb, a])


def paint_face_array():
    shapes = face_shapes()
    assert set(shapes) == set(FACE_CELLS), set(shapes) ^ set(FACE_CELLS)
    cv = np.zeros((FACE_PX * SS, FACE_PX * SS, 4))
    cv[..., :3] = 0.5
    for name, (fn, col) in shapes.items():
        rect, (wm, hm) = FACE_CELLS[name]

        def cell(U, V, fn=fn, col=col, wm=wm, hm=hm):
            return cell_rgba(fn, col, wm, hm, U, V)
        paint(cv, rect, cell, gut=3)
    return downsample(cv), shapes


# ---------------------------------------------------------------- mesh builder
class MB:
    """Collects faces with per-corner UVs.  Vertices are welded by position.  Every face is given an
    outward hint and is wound to face that way.  Faces of different groups meet at a sharp edge."""

    def __init__(self, name):
        self.name = name; self.v = []; self.key = {}; self.faces = []; self.wover = {}

    def vert(self, p):
        k = (round(p[0] * 1e6), round(p[1] * 1e6), round(p[2] * 1e6))
        i = self.key.get(k)
        if i is None:
            i = len(self.v); self.v.append((float(p[0]), float(p[1]), float(p[2]))); self.key[k] = i
        return i

    def face(self, pts, uvs, out, grp, w=None):
        ids = []; uu = []
        for p, uv in zip(pts, uvs):
            i = self.vert(p)
            if ids and i == ids[-1]:
                continue
            ids.append(i); uu.append(uv)
        if len(ids) > 1 and ids[0] == ids[-1]:
            ids.pop(); uu.pop()
        if len(set(ids)) < 3:
            return
        n = [0.0, 0.0, 0.0]; c = [0.0, 0.0, 0.0]
        for a, b in zip(ids, ids[1:] + ids[:1]):
            pa, pb = self.v[a], self.v[b]
            n[0] += (pa[1] - pb[1]) * (pa[2] + pb[2]); n[1] += (pa[2] - pb[2]) * (pa[0] + pb[0]); n[2] += (pa[0] - pb[0]) * (pa[1] + pb[1])
            c[0] += pa[0]; c[1] += pa[1]; c[2] += pa[2]
        c = [x / len(ids) for x in c]
        o = out(c) if callable(out) else out
        if vdot(n, o) < 0:
            ids.reverse(); uu.reverse()
        self.faces.append((ids, uu, grp))
        if w is not None:
            for i in ids:
                self.wover[i] = w

    def tris(self):
        return sum(len(f[0]) - 2 for f in self.faces)


def sphere(m, centre, rad, uvfn, grp, nseg=N, nlat=8, w=None):
    """UV sphere (or ellipsoid: rad may be a 3-tuple), poles on Z.  uvfn(unit direction) -> uv."""
    rx, ry, rz = (rad, rad, rad) if not isinstance(rad, tuple) else rad

    def pt(q, a):
        s, c = math.sin(q), math.cos(q)
        d = (s * math.cos(a), s * math.sin(a), c)
        return (centre[0] + rx * d[0], centre[1] + ry * d[1], centre[2] + rz * d[2]), d
    qs = [math.pi * i / nlat for i in range(nlat + 1)]
    out = lambda c: vsub(c, centre)
    for i in range(len(qs) - 1):
        for j in range(nseg):
            a0, a1 = 2 * math.pi * j / nseg, 2 * math.pi * (j + 1) / nseg
            quad = [pt(qs[i], a0), pt(qs[i], a1), pt(qs[i + 1], a1), pt(qs[i + 1], a0)]
            m.face([p for p, d in quad], [uvfn(d) for p, d in quad], out, grp, w)


def tube(m, ring_pts, uv_rows, out, grp, w=None):
    """Quads between successive rings.  ring_pts[i][j] are points, uv_rows[i][j] their UVs (j runs 0..N)."""
    n = len(ring_pts[0]) - 1
    for i in range(len(ring_pts) - 1):
        for j in range(n):
            m.face([ring_pts[i][j], ring_pts[i][j + 1], ring_pts[i + 1][j + 1], ring_pts[i + 1][j]],
                   [uv_rows[i][j], uv_rows[i][j + 1], uv_rows[i + 1][j + 1], uv_rows[i + 1][j]], out, grp, w)


# ---------------------------------------------------------------- head: the slide, its end caps and the trigger
SLIDE_RINGS_Y = [Y_FRONT + EB + (LEN - 2 * EB) * t for t in np.linspace(0.0, 1.0, 8)]
END_ANGLES = [0.0, 30.0, 60.0, 90.0]                # the rounding at each end, 0 = where it leaves the long sides


def slide_out(c):
    yc = min(max(c[1], Y_FRONT + EB), Y_BACK - EB)
    return (c[0], c[1] - yc, c[2] - ZC)


def slide_ring(d, y):
    """The section inset by d, at y.  -> points (closed list) and their U."""
    pts = [(p[0] - p[2] * d, y, p[1] - p[3] * d) for p in SLIDE_PROF]
    us = [float(s) / SLIDE_L for s in SLIDE_S]
    return pts, us


def build_head():
    m = MB('head_m')
    rings = []                                       # (points, us, v)
    for a in END_ANGLES[::-1]:                       # front rounding: from the cap edge back to the long sides
        t = math.radians(a)
        d = EB * (1.0 - math.cos(t)); y = Y_FRONT + EB * (1.0 - math.sin(t))
        rings.append(slide_ring(d, y) + ((y - Y_FRONT) / LEN,))
    for y in SLIDE_RINGS_Y[1:-1]:
        rings.append(slide_ring(0.0, y) + ((y - Y_FRONT) / LEN,))
    for a in END_ANGLES:                             # back rounding
        t = math.radians(a)
        d = EB * (1.0 - math.cos(t)); y = Y_BACK - EB + EB * math.sin(t)
        rings.append(slide_ring(d, y) + ((y - Y_FRONT) / LEN,))
    for (pa, ua, va), (pb, ub, vb) in zip(rings[:-1], rings[1:]):
        for j in range(len(pa) - 1):
            m.face([pa[j], pa[j + 1], pb[j + 1], pb[j]],
                   [uvb('slide', ua[j], va), uvb('slide', ua[j + 1], va), uvb('slide', ub[j + 1], vb), uvb('slide', ub[j], vb)],
                   slide_out, 'slide')
    # end caps: the last inset section, filled by scaled copies toward its middle, flat
    cz = (Z0 + Z1) / 2
    fuv = lambda p: uvb('cap_front', (p[0] / XW + 1.0) / 2.0, (Z1 - p[2]) / (Z1 - Z0))
    edge = rings[0][0]
    hole_at = lambda c, s_: 0.995 / ((abs(c) / FA) ** FP + (abs(s_) / FB) ** FP) ** (1.0 / FP)
    ang_of = lambda p: math.atan2(p[2] - FZ, p[0] - FX)
    # the outline, its own points kept, with points added where it meets 64 even directions, so the hole is smooth
    outline = []
    for j in range(len(edge) - 1):
        pa, pb = edge[j], edge[j + 1]
        outline.append(pa)
        a0, a1 = ang_of(pa), ang_of(pb)
        da = (a1 - a0 + math.pi) % (2 * math.pi) - math.pi
        for k in range(64):
            t_ang = -math.pi + 2 * math.pi * (k + 0.5) / 64
            dt = (t_ang - a0 + math.pi) % (2 * math.pi) - math.pi
            if (da > 0 and 1e-6 < dt < da - 1e-6) or (da < 0 and da + 1e-6 < dt < -1e-6):
                c, s_ = math.cos(t_ang), math.sin(t_ang)        # where the ray from the face's middle crosses pa..pb
                ex, ez = pb[0] - pa[0], pb[2] - pa[2]
                den = c * ez - s_ * ex
                u = ((pa[0] - FX) * ez - (pa[2] - FZ) * ex) / den
                outline.append((FX + u * c, Y_FRONT, FZ + u * s_))
    outline.append(edge[-1])
    hole = []
    for p in outline:
        a = ang_of(p); c, s_ = math.cos(a), math.sin(a); k = hole_at(c, s_)
        hole.append((FX + k * c, Y_FRONT, FZ + k * s_))
    for j in range(len(outline) - 1):
        q = [outline[j], outline[j + 1], hole[j + 1], hole[j]]
        m.face(q, [fuv(p) for p in q], (0.0, -1.0, 0.0), 'slide')
    for edge, y, nrm, blk in ((rings[-1][0], Y_BACK, (0.0, 1.0, 0.0), 'cap'),):
        cuv = lambda p, blk=blk: uvb(blk, (p[0] / XW + 1.0) / 2.0, (Z1 - p[2]) / (Z1 - Z0))
        sc = [1.0, 0.72, 0.44, 0.18]
        loops = [[(p[0] * s, y, cz + (p[2] - cz) * s) for p in edge] for s in sc]
        for la, lb in zip(loops[:-1], loops[1:]):
            for j in range(len(la) - 1):
                m.face([la[j], la[j + 1], lb[j + 1], lb[j]], [cuv(la[j]), cuv(la[j + 1]), cuv(lb[j + 1]), cuv(lb[j])], nrm, 'slide')
        cen = (0.0, y, cz); last = loops[-1]
        for j in range(len(last) - 1):
            m.face([last[j], last[j + 1], cen], [cuv(last[j]), cuv(last[j + 1]), cuv(cen)], nrm, 'slide')
    build_trigger(m)
    return m


def build_trigger(m, w=None):
    """The trigger: a curved blade of rounded-box section, closed at its tip; its top end sinks into the slide.
    w: a weight override, or None for the weight function of the mesh it is built into."""
    path = TRIGGER
    n = 12
    hw, t0, t1 = TRIG_R
    rings, uvs = [], []
    for i, (y, z) in enumerate(path):
        if i == 0:
            ty, tz = path[1][0] - y, path[1][1] - z
        elif i == len(path) - 1:
            ty, tz = y - path[i - 1][0], z - path[i - 1][1]
        else:
            ty, tz = path[i + 1][0] - path[i - 1][0], path[i + 1][1] - path[i - 1][1]
        L = math.hypot(ty, tz); ty, tz = ty / L, tz / L
        ny, nz = -tz, ty                                 # in-plane normal
        ring = []; uv = []
        th = t0 + (t1 - t0) * i / (len(path) - 1)
        for k in range(n + 1):
            a = 2 * math.pi * k / n
            ca, sa = math.cos(a), math.sin(a)
            ex = math.copysign(abs(ca) ** 0.45, ca); ey = math.copysign(abs(sa) ** 0.45, sa)   # a rounded box section
            ring.append((hw * ex, y + th * ey * ny, z + th * ey * nz))
            uv.append(uvb('trigger', 0.5, 0.5 - 0.45 * math.sin(a) * (1 if nz >= 0 else -1)))
        rings.append(ring); uvs.append(uv)
    for i in range(len(rings) - 1):
        (ya, za), (yb, zb) = path[i], path[i + 1]
        cy, cz = (ya + yb) / 2, (za + zb) / 2
        for j in range(n):
            m.face([rings[i][j], rings[i][j + 1], rings[i + 1][j + 1], rings[i + 1][j]],
                   [uvs[i][j], uvs[i][j + 1], uvs[i + 1][j + 1], uvs[i + 1][j]],
                   lambda c, cy=cy, cz=cz: (c[0], c[1] - cy, c[2] - cz), 'trigger', w)
    (ya, za), (y, z) = path[-2], path[-1]
    L = math.hypot(y - ya, z - za); tip = (0.0, y + 0.006 * (y - ya) / L, z + 0.006 * (z - za) / L)
    for j in range(n):
        m.face([rings[-1][j], rings[-1][j + 1], tip], [uvs[-1][j], uvs[-1][j + 1], uvb('trigger', 0.5, 0.5)],
               lambda c, ya=ya, za=za: (c[0], c[1] - ya, c[2] - za), 'trigger', w)


# ---------------------------------------------------------------- face plate, eyes, mouths, overlays, nose
def plate_y(x, z):
    r2 = sq_norm((x - FX) / FA, (z - FZ) / FB) ** 2
    return YP0 - DOME * (1.0 - np.minimum(r2, 1.0))


def sq_dir(a):
    """Unit direction a, pushed out onto the plate's rounded square."""
    c, s_ = math.cos(a), math.sin(a)
    return math.copysign(abs(c) ** (2.0 / FP), c), math.copysign(abs(s_) ** (2.0 / FP), s_)


PLATE_RS = [0.0, 0.40, 0.72, 0.92, 1.0]


def build_plate(name, block):
    """The square plate: rings of 32 round a domed rounded square, a short wall at the rim back into the end cap.  U, V
    are linear in x and z, so a front view shows the block undistorted."""
    m = MB(name)
    ang = [2 * math.pi * (j + 0.5) / 32 for j in range(33)]
    def pt(r, a):
        dx, dz = sq_dir(a); x = FX + FA * r * dx; z = FZ + FB * r * dz
        return (x, float(plate_y(x, z)), z)
    uv = lambda p: uvb(block, (p[0] - FX) / (2 * FA) + 0.5, 0.5 - (p[2] - FZ) / (2 * FB))
    out = lambda c: (0.0, -1.0, 0.0)
    cen = pt(0.0, 0.0)
    for j in range(32):
        a, b = pt(PLATE_RS[1], ang[j]), pt(PLATE_RS[1], ang[j + 1])
        m.face([cen, a, b], [uv(cen), uv(a), uv(b)], out, 'plate')
    for r0, r1 in zip(PLATE_RS[1:-1], PLATE_RS[2:]):
        for j in range(32):
            q = [pt(r0, ang[j]), pt(r0, ang[j + 1]), pt(r1, ang[j + 1]), pt(r1, ang[j])]
            m.face(q, [uv(p) for p in q], out, 'plate')
    for j in range(32):                               # the black wall of the recess, from the sunken plate out to the end cap
        a, b = ang[j], ang[j + 1]
        (ax, az), (bx, bz) = sq_dir(a), sq_dir(b)
        q = [pt(1.0, a), pt(1.0, b), (FX + FA * bx, Y_FRONT - 0.0005, FZ + FB * bz), (FX + FA * ax, Y_FRONT - 0.0005, FZ + FB * az)]
        m.face(q, [uvb('plate_rim', 0.5, 0.5)] * 4, lambda c: (FX - c[0], 0.0, FZ - c[2]), 'rim')
    return m


EYE_X = 0.0320          # eye centres, from the reference's spacing
EYE_Z = 0.6140
MOUTH_Z = 0.5500
EYE_PIECES = [('eye_01_m', EYE_Z), ('eye_02_m', EYE_Z - 0.001), ('eye_03_m', EYE_Z - 0.001), ('eye_04_m', EYE_Z),
              ('eye_05_m', EYE_Z), ('eye_aim_m', EYE_Z)]
MOUTH_PIECES = [('mouth_01_m', MOUTH_Z), ('mouth_02_m', MOUTH_Z - 0.002), ('mouth_03_m', MOUTH_Z - 0.001),
                ('mouth_04_m', MOUTH_Z - 0.001), ('mouth_05_m', MOUTH_Z - 0.004), ('mouth_06_m', MOUTH_Z),
                ('mouth_07_m', MOUTH_Z - 0.004), ('mouth_08_m', MOUTH_Z + 0.001), ('mouth_09_m', MOUTH_Z + 0.001),
                ('mouth_10_m', MOUTH_Z), ('mouth_11_m', MOUTH_Z - 0.002)]
# overlays: front-view centre of the quad (for a mirrored pair, their right one) and whether it is a mirrored pair
OVERLAYS = {'cheak_01_m': (-0.0460, 0.5830, True), 'tear_01_m': (-0.0280, 0.5780, True),
            'angry_01_m': (0.0360, 0.6510, False), 'shock_01_m': (0.0300, 0.6470, False)}
# which cell each eye quad shows: (their right eye, their left eye); the left one is drawn mirrored
EYE_CELLS = {'eye_aim_m': ('eye_aim_m', 'eye_01_m')}
SHOWN = ('head_m', 'nose_m', 'antena_m', 'body_m', 'hand_L_m', 'hand_R_m', 'leg_L_m', 'leg_R_m', 'face_01_m', 'eye_01_m', 'mouth_01_m')


def front_y(cx, cz, wm, hm, off):
    """A flat piece over this front-view box: off in front of the nearest point of the plate under it."""
    xs = np.linspace(cx - wm / 2, cx + wm / 2, 21); zs = np.linspace(cz - hm / 2, cz + hm / 2, 21)
    X, Z = np.meshgrid(xs, zs)
    return float(plate_y(X, Z).min()) - off


def inside_plate(cx, cz, wm, hm, margin=0.0):
    for sx in (-1, 1):
        for sz in (-1, 1):
            x = cx + sx * wm / 2; z = cz + sz * hm / 2
            if sq_norm((x - FX) / (FA - margin), (z - FZ) / (FB - margin)) > 1.0:
                return False
    return True


def build_eye(name, zf):
    m = MB(name)
    cells = EYE_CELLS.get(name, (name, name))
    for sd, cell in ((-1.0, cells[0]), (1.0, cells[1])):
        wm, hm = FACE_CELLS[cell][1]
        cx = sd * EYE_X
        assert inside_plate(cx, zf, wm, hm), (name, sd)
        y = front_y(cx, zf, wm, hm, OFF_EYE)
        pts = [(cx - wm / 2, y, zf - hm / 2), (cx + wm / 2, y, zf - hm / 2), (cx + wm / 2, y, zf + hm / 2), (cx - wm / 2, y, zf + hm / 2)]
        us = [0, 1, 1, 0] if sd < 0 else [1, 0, 0, 1]       # their left eye mirrors the drawing
        m.face(pts, [uvf(cell, u, v) for u, v in zip(us, [1, 1, 0, 0])], (0.0, -1.0, 0.0), 'eye')
    return m


def build_mouth(name, zf):
    """A flat 3 x 3 grid (9 vertices, 8 triangles) facing the front."""
    m = MB(name)
    wm, hm = FACE_CELLS[name][1]
    assert inside_plate(0.0, zf, wm, hm), name
    y = front_y(0.0, zf, wm, hm, OFF_EYE)
    pt = lambda s, t: (-wm / 2 + wm * s, y, zf - hm / 2 + hm * t)
    for i in range(2):
        for k in range(2):
            s0, s1, t0, t1 = k / 2, (k + 1) / 2, i / 2, (i + 1) / 2
            m.face([pt(s0, t0), pt(s1, t0), pt(s1, t1), pt(s0, t1)],
                   [uvf(name, s0, 1 - t0), uvf(name, s1, 1 - t0), uvf(name, s1, 1 - t1), uvf(name, s0, 1 - t1)], (0.0, -1.0, 0.0), 'mouth')
    return m, zf, hm


def build_overlay(name):
    """A 4 x 4 sheet lying OFF_OVL in front of the domed plate, so it follows the dome."""
    m = MB(name)
    wm, hm = FACE_CELLS[name][1]
    cx, zc, pair = OVERLAYS[name]
    n = 4
    for sd in ((1.0, -1.0) if pair else (1.0,)):
        x0 = sd * cx - wm / 2
        assert inside_plate(sd * cx, zc, wm, hm), (name, sd)
        ua, ub = (0, 1) if sd > 0 else (1, 0)
        P = lambda i, k: (x0 + wm * k / n, float(plate_y(x0 + wm * k / n, zc - hm / 2 + hm * i / n)) - OFF_OVL, zc - hm / 2 + hm * i / n)
        for i in range(n):
            for k in range(n):
                u0 = ua + (ub - ua) * k / n; u1 = ua + (ub - ua) * (k + 1) / n
                v0 = 1.0 - i / n; v1 = 1.0 - (i + 1) / n
                m.face([P(i, k), P(i, k + 1), P(i + 1, k + 1), P(i + 1, k)],
                       [uvf(name, u0, v0), uvf(name, u1, v0), uvf(name, u1, v1), uvf(name, u0, v1)], (0.0, -1.0, 0.0), 'overlay')
    for other, zf in EYE_PIECES + MOUTH_PIECES:        # no quad of it lies over an eye or mouth quad in a front view
        cells = EYE_CELLS.get(other, (other, other))
        for ox, cell in (((-EYE_X, cells[0]), (EYE_X, cells[1])) if other.startswith('eye') else ((0.0, other),)):
            ow, oh = FACE_CELLS[cell][1]
            for sd in ((1.0, -1.0) if pair else (1.0,)):
                assert abs(sd * cx - ox) >= (wm + ow) / 2 or abs(zc - zf) >= (hm + oh) / 2, (name, other)
    return m


NOSE_GEO = dict(hw=0.0085, z_top=0.5970, z_bot=0.5750, depth=0.0120)   # the Prince's orange triangle


def build_nose():
    """A triangle nose: a small three-sided pyramid on the face plate, point up between the eyes, its tip forward."""
    m = MB('nose_m')
    g = NOSE_GEO
    y0 = lambda x, z: float(plate_y(x, z)) + 0.0005
    top = (0.0, y0(0.0, g['z_top']), g['z_top'])
    bl = (-g['hw'], y0(-g['hw'], g['z_bot']), g['z_bot']); br = (g['hw'], y0(g['hw'], g['z_bot']), g['z_bot'])
    zt = g['z_bot'] + 0.0035
    tip = (0.0, y0(0.0, zt) - g['depth'], zt)
    cen = (0.0, y0(0.0, zt) + 0.004, zt)
    out = lambda c: vsub(c, cen)
    m.face([bl, br, tip], [uvb('nose', 0.1, 0.95), uvb('nose', 0.9, 0.95), uvb('nose', 0.5, 0.75)], out, 'under')
    m.face([br, top, tip], [uvb('nose', 0.9, 0.6), uvb('nose', 0.5, 0.05), uvb('nose', 0.5, 0.5)], out, 'right')
    m.face([top, bl, tip], [uvb('nose', 0.5, 0.05), uvb('nose', 0.1, 0.6), uvb('nose', 0.5, 0.5)], out, 'left')
    return m


# ---------------------------------------------------------------- antenna: a thin yellow stalk and a red ball
def build_antenna(spec):
    """The Prince's antenna, built as Burda's is: a cone from the slide top to a point inside the ball, and the ball."""
    m = MB('antena_m')
    ax, ay, az = spec['JNT_antenna']['head']
    z0, z2 = Z1 - 0.001, az - ANT_BALL + 0.006
    z_apex = az - ANT_BALL + 0.021
    rr = lambda z: ANT_CONE_R * (z_apex - z) / (z_apex - z0)
    zs = [z0, (z0 + z2) / 2, z2]
    th = [2 * math.pi * j / N for j in range(N + 1)]
    rings = [[(ax + rr(z) * math.sin(t), ay - rr(z) * math.cos(t), z) for t in th] for z in zs]
    uvs = [[uvb('antY', j / N, 1.0 - (z - z0) / (z2 - z0)) for j in range(N + 1)] for z in zs]
    tube(m, rings, uvs, lambda c: (c[0] - ax, c[1] - ay, 0.004), 'cone')
    sphere(m, (ax, ay, az), ANT_BALL, lambda d: uvb('antR', 0.5 + 0.4 * d[0], 0.5 - 0.48 * d[2]), 'ball', nlat=8)
    return m, (z0, z2, az - ANT_BALL)


# ---------------------------------------------------------------- body: the grip
def build_body():
    m = MB('body_m')
    prof = GRIP_PROF
    us = [float(s) / GRIP_L for s in GRIP_S]
    vz = lambda z: (GZ1 - z) / (GZ1 - GZ0)
    gout = lambda c: (c[0], c[1] - (GY0 + GY1) / 2, 0.0 if c[2] > GZ0 + GEB else c[2] - (GZ0 + GEB))
    zr = [GZ1, 0.440, 0.410, 0.360, 0.300, 0.240, 0.180, GZ0 + GEB]
    rings = [([(p[0], p[1], z) for p in prof], z) for z in zr]
    for a in (30.0, 60.0, 90.0):                      # the bottom edge, rounded
        t = math.radians(a)
        d = GEB * (1.0 - math.cos(t)); z = GZ0 + GEB - GEB * math.sin(t)
        rings.append(([(p[0] - p[2] * d, p[1] - p[3] * d, z) for p in prof], z))
    for (pa, za), (pb, zb) in zip(rings[:-1], rings[1:]):
        for j in range(len(pa) - 1):
            ua, ub = us[j], us[j + 1]
            m.face([pa[j], pa[j + 1], pb[j + 1], pb[j]],
                   [uvb('grip', ua, vz(za)), uvb('grip', ub, vz(za)), uvb('grip', ub, vz(zb)), uvb('grip', ua, vz(zb))], gout, 'grip')
    # bottom: dark, flat, scaled copies of the last ring toward the middle
    cy = (GY0 + GY1) / 2
    edge = rings[-1][0]
    loops = [[(p[0] * s, cy + (p[1] - cy) * s, GZ0) for p in edge] for s in (1.0, 0.6, 0.25)]
    for la, lb in zip(loops[:-1], loops[1:]):
        for j in range(len(la) - 1):
            m.face([la[j], la[j + 1], lb[j + 1], lb[j]], [uvb('under', 0.5, 0.5)] * 4, (0, 0, -1), 'grip')
    for j in range(len(loops[-1]) - 1):
        m.face([loops[-1][j], loops[-1][j + 1], (0.0, cy, GZ0)], [uvb('under', 0.5, 0.5)] * 3, (0, 0, -1), 'grip')
    # top: inside the slide, closed so nothing is open
    top = rings[0][0]
    for j in range(len(top) - 1):
        m.face([top[j], top[j + 1], (0.0, cy, GZ1)], [uvb('under', 0.5, 0.5)] * 3, (0, 0, 1), 'grip_top')
    return m


# ---------------------------------------------------------------- arms and legs
def build_arm(sd, spec):
    m = MB('hand_L_m' if sd > 0 else 'hand_R_m')
    za = spec['JNT_arm_L']['head'][2]
    xe = spec['JNT_elbow_L']['head'][0]; xh = spec['JNT_hand_L']['head'][0]
    r_ball = 0.019; xc = xh + 0.016
    q_join = 140.0
    xj = xc + r_ball * math.cos(math.radians(q_join)); rj = r_ball * math.sin(math.radians(q_join))
    xr = [GX - 0.016, xe - 0.030, xe, xe + 0.030, xh - 0.010, xj]
    rs = [0.0100, 0.0096, 0.0094, 0.0092, 0.0088, rj]
    ang = [2 * math.pi * j / N for j in range(N + 1)]
    pt = lambda x, r, a: (sd * x, -r * math.sin(a), za + r * math.cos(a))        # a = 0 is the top of the arm
    sv = lambda a: 0.42 - 0.30 * math.cos(a)
    rings = [[pt(x, r, a) for a in ang] for x, r in zip(xr, rs)]
    uvs = [[uvb('limb', 0.5, sv(a)) for a in ang] for x in xr]
    tube(m, rings, uvs, lambda c: (0.0, c[1], c[2] - za), 'arm')
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], (sd * xr[0], 0.0, za)], [uvb('limb', 0.5, 0.5)] * 3, (-sd, 0, 0), 'root')
    cen = (sd * xc, 0.0, za)
    qs = [0.0, 35.0, 70.0, 105.0, q_join]
    hpt = lambda q, a: (sd * (xc + r_ball * math.cos(math.radians(q))), -r_ball * math.sin(math.radians(q)) * math.sin(a),
                        za + r_ball * math.sin(math.radians(q)) * math.cos(a))
    huv = lambda q, a: uvb('limb', 0.5, 0.42 - 0.30 * math.cos(a) * min(1.0, math.sin(math.radians(q)) + 0.3))
    for i in range(len(qs) - 1):
        for j in range(N):
            a, b = ang[j], ang[j + 1]
            m.face([hpt(qs[i], a), hpt(qs[i], b), hpt(qs[i + 1], b), hpt(qs[i + 1], a)],
                   [huv(qs[i], a), huv(qs[i], b), huv(qs[i + 1], b), huv(qs[i + 1], a)], lambda c: vsub(c, cen), 'arm')
    return m


def build_leg(sd, spec):
    m = MB('leg_L_m' if sd > 0 else 'leg_R_m')
    xl = spec['JNT_leg_L']['head'][0]; zk = spec['JNT_knee_L']['head'][2]
    zr = [GZ0 + 0.025, zk + 0.030, zk, zk - 0.030, 0.026]
    rr = lambda z: 0.0100
    ang = [2 * math.pi * j / N for j in range(N + 1)]
    pt = lambda z, a: (sd * (xl + rr(z) * math.sin(a)), -rr(z) * math.cos(a), z)
    sv = lambda z: 0.40 + 0.35 * float(sstep(0.14, 0.08, z))
    rings = [[pt(z, a) for a in ang] for z in zr]
    uvs = [[uvb('limb', 0.5, sv(z)) for a in ang] for z in zr]
    tube(m, rings, uvs, lambda c: (c[0] - sd * xl, c[1], 0.0), 'leg')
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], (sd * xl, 0.0, zr[0])], [uvb('limb', 0.5, 0.9)] * 3, (0, 0, 1), 'top')
    # foot: a small rounded grey shoe, longer toward the toe
    sphere(m, (sd * xl, -0.013, 0.0150), (0.017, 0.031, 0.0150), lambda d: uvb('limb', 0.5 + 0.3 * d[0], 0.5 - 0.46 * d[2]),
           'foot', nlat=6, w={'JNT_ankle_' + ('L' if sd > 0 else 'R'): 1.0})
    return m


# ---------------------------------------------------------------- weights
def w_body(z):
    a = float(sstep(0.170, 0.270, z))       # waist -> spine_01, round the spine_01 joint (0.220)
    b = float(sstep(0.250, 0.350, z))       # spine_01 -> spine_02, round the spine_02 joint (0.300)
    c = float(sstep(0.440, 0.470, z))       # spine_02 -> neck, inside the slide
    return {'JNT_waist': 1 - a, 'JNT_spine_01': a * (1 - b), 'JNT_spine_02': b * (1 - c), 'JNT_neck': c}


def w_arm(sd, spec):
    s = 'L' if sd > 0 else 'R'
    xe = spec['JNT_elbow_L']['head'][0]; xh = spec['JNT_hand_L']['head'][0]

    def f(p):
        x = abs(p[0])
        e = float(sstep(xe - 0.040, xe + 0.040, x)); h = float(sstep(xh - 0.020, xh + 0.004, x))
        return {'JNT_arm_' + s: 1 - e, 'JNT_elbow_' + s: e * (1 - h), 'JNT_hand_' + s: h}
    return f


def w_leg(sd, spec):
    s = 'L' if sd > 0 else 'R'
    zk = spec['JNT_knee_L']['head'][2]; za = spec['JNT_ankle_L']['head'][2]

    def f(p):
        z = p[2]
        k = float(sstep(zk + 0.040, zk - 0.040, z)); a = float(sstep(za + 0.020, za - 0.012, z))
        return {'JNT_leg_' + s: 1 - k, 'JNT_knee_' + s: k * (1 - a), 'JNT_ankle_' + s: a}
    return f


def build_parts(spec):
    """Every mesh with its texture ('body' or 'face') and weight function, in object order."""
    head = {'JNT_head': 1.0}
    w_head = lambda p: head
    parts = [(build_head(), 'body', w_head)]
    ant, (az0, az1, az_ball) = build_antenna(spec)

    def w_ant(p):
        t = 1.0 if p[2] >= az_ball - 1e-6 else float(np.clip((p[2] - az0) / (az1 - az0), 0.0, 1.0))
        return {'JNT_head': 1 - t, 'JNT_antenna': t}
    parts.append((ant, 'body', w_ant))
    for name, block in (('face_01_m', 'face'), ('face_02_m', 'face_gloom'), ('face_03_m', 'face_flush')):
        parts.append((build_plate(name, block), 'body', w_head))
    parts.append((build_nose(), 'body', w_head))
    for name, zf in EYE_PIECES:
        parts.append((build_eye(name, zf), 'face', w_head))
    for name, zf in MOUTH_PIECES:
        mm, zc, hm = build_mouth(name, zf)

        def w_mouth(p, zc=zc, hm=hm):                # upper lip half on the head, lower lip on the mouth joint
            t = float(np.clip((zc + hm / 2 - p[2]) / hm, 0.0, 1.0))
            return {'JNT_head': 0.5 - 0.4 * t, 'JNT_mouth': 0.5 + 0.4 * t}
        parts.append((mm, 'face', w_mouth))
    for name in OVERLAYS:
        parts.append((build_overlay(name), 'face', w_head))
    parts.append((build_body(), 'body', lambda p: w_body(p[2])))
    for sd in (1.0, -1.0):
        parts.append((build_arm(sd, spec), 'body', w_arm(sd, spec)))
    for sd in (1.0, -1.0):
        parts.append((build_leg(sd, spec), 'body', w_leg(sd, spec)))
    names = [m.name for m, _, _ in parts]
    assert len(set(names)) == len(names) and set(SHOWN) <= set(names)
    return parts


def load_spec():
    with open(RIG_SPEC) as fh:
        return json.load(fh)['new_style']['joints']


# ---------------------------------------------------------------- Blender objects
def make_object(m, mat, wfun, arm):
    import bpy
    me = bpy.data.meshes.new(m.name)
    me.from_pydata(m.v, [], [f[0] for f in m.faces])
    me.update(calc_edges=True)
    if me.validate(verbose=False):
        raise RuntimeError('mesh %s needed repair' % m.name)
    uvl = me.uv_layers.new(name='UVMap')
    for poly, f in zip(me.polygons, m.faces):
        assert list(poly.vertices) == f[0], m.name
        poly.use_smooth = True
        for li, uv in zip(poly.loop_indices, f[1]):
            uvl.data[li].uv = uv
    eg = {}
    for ids, _, grp in m.faces:
        for a, b in zip(ids, ids[1:] + ids[:1]):
            eg.setdefault((min(a, b), max(a, b)), set()).add(grp)
    for e in me.edges:
        if len(eg.get((min(e.vertices), max(e.vertices)), ())) > 1:
            e.use_edge_sharp = True
    me.materials.append(mat)
    me.update()
    ob = bpy.data.objects.new(m.name, me)
    bpy.context.scene.collection.objects.link(ob)
    groups = {}
    for i, p in enumerate(m.v):
        w = {b: x for b, x in (m.wover.get(i) or wfun(p)).items() if x > 1e-4}
        tot = sum(w.values())
        for b, x in w.items():
            if b not in groups:
                groups[b] = ob.vertex_groups.new(name=b)
            groups[b].add([i], x / tot, 'REPLACE')
    ob.parent = arm
    ob.matrix_parent_inverse = rig_turn().inverted()   # the mesh stays where it was built, under the turned rig
    mod = ob.modifiers.new('Armature', 'ARMATURE'); mod.object = arm
    return ob


# OUAK's cousin joints rest with identity local rotations, world-aligned (OUJI01_D).  The FBX exporter writes the rig
# object as a Null under its Z-up to Y-up conversion, Rx(-90), and each bone as its rest matrix in rig space.  So the
# rig object is turned +90 degrees about X, which cancels that conversion, and every bone lies along the rig's +Y with
# roll 0, which makes its rest matrix identity.  In the viewport every bone points straight up.
def rig_turn():
    from mathutils import Matrix
    return Matrix.Rotation(math.pi / 2, 4, 'X')        # rig space -> world


def build_rig(spec):
    import bpy
    from mathutils import Vector
    RIG_TURN = rig_turn()
    arm = bpy.data.armatures.new('Pew_rig')
    ob = bpy.data.objects.new('model', arm)                 # OUAK clips bind paths 'model/JNT_root/...'
    ob.rotation_euler = (math.pi / 2, 0.0, 0.0)          # RIG_TURN
    bpy.context.scene.collection.objects.link(ob)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode='EDIT')
    kids = {n: [c for c, j in spec.items() if j['parent'] == n] for n in spec}
    tails = {}

    def tail(n):
        if n in tails:
            return tails[n]
        h = spec[n]['head']
        if len(kids[n]) == 1:
            t = tuple(spec[kids[n][0]]['head'])
        else:                                        # no child or several: a stub along the parent, or up
            par = spec[n]['parent']; d = (0.0, 0.0, 1.0)
            if par is not None:
                ph_, pt_ = spec[par]['head'], tail(par)
                v = vsub(pt_, ph_); L = math.sqrt(vdot(v, v)); d = (v[0] / L, v[1] / L, v[2] / L)
            ln = 0.05 if kids[n] else 0.04
            t = (h[0] + d[0] * ln, h[1] + d[1] * ln, h[2] + d[2] * ln)
        tails[n] = t
        return t
    to_rig = RIG_TURN.inverted()
    for n, j in spec.items():
        eb = arm.edit_bones.new(n)
        length = (Vector(tail(n)) - Vector(j['head'])).length      # as long as before, but along the rig's +Y
        eb.head = to_rig @ Vector(j['head']); eb.tail = eb.head + Vector((0.0, max(length, 0.01), 0.0))
        eb.roll = 0.0
        assert eb.name == n
    for n, j in spec.items():
        if j['parent'] is not None:
            arm.edit_bones[n].parent = arm.edit_bones[j['parent']]
            arm.edit_bones[n].use_connect = False
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()
    for b in arm.bones:                 # every rest rotation identity, every joint at its spec position
        assert all(abs(b.matrix_local[r][c] - (r == c)) < 1e-6 for r in range(3) for c in range(3)), b.name
        assert ((ob.matrix_world @ b.head_local) - Vector(spec[b.name]['head'])).length < 1e-6, b.name
    return ob


def scrub_ui_paths():
    """The factory start-up has a File Browser (Shading workspace) that remembers the home documents folder of
    whoever runs the build, and a .blend saves it.  Overwrite the whole stored text, then leave the file's own folder."""
    import bpy
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == 'FILE_BROWSER' and space.params is not None:
                    space.params.directory = b'/' * 512
                    space.params.directory = b'//'


# ---------------------------------------------------------------- main
def main():
    import bpy
    import bl_common as C
    for p in glob.glob(os.path.join(TEX_DIR, 'pew_new_*.png')) + glob.glob(os.path.join(REN_DIR, 'Pew_new_*.png')) + \
            [BLEND, BLEND + '1', FBX]:
        if os.path.exists(p):
            os.remove(p)
    os.makedirs(TEX_DIR, exist_ok=True); os.makedirs(REN_DIR, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0

    check_layout(BODY_BLOCKS, BODY_PX)
    check_layout({n: c[0] for n, c in FACE_CELLS.items()}, FACE_PX)
    body_png = save_png(BODY_TEX, paint_body_array(), alpha=False)
    face_png = save_png(FACE_TEX, paint_face_array()[0], alpha=True)
    mats = {'body': C.img_material('Pew_new_body', body_png), 'face': C.img_material('Pew_new_face', face_png, alpha=True)}
    for mt in mats.values():
        mt.use_backface_culling = True              # as a game draws it: a flipped face would vanish
    chk = bpy.data.images.load(body_png, check_existing=True)
    px = np.array(chk.pixels[:]).reshape(BODY_PX, BODY_PX, 4)[::-1]
    x, y, w, h = BODY_BLOCKS['under']
    got = tuple(int(round(v * 255)) for v in px[y + h // 2, x + w // 2, :3])
    print('TEXCHECK under cell', got, 'sheet', tuple(int(round(v * 255)) for v in hx(UNDER)))
    assert got == tuple(int(round(v * 255)) for v in hx(UNDER))

    spec = load_spec()
    rig = build_rig(spec)
    obs = [make_object(m, mats[tex], wf, rig) for m, tex, wf in build_parts(spec)]
    for ob in obs:
        if ob.name not in SHOWN:                    # every other face piece waits hidden
            ob.hide_render = True
            ob.hide_viewport = True                 # the FBX writes Visibility from this flag, not from hide_render

    total = 0
    for ob in obs:
        t = sum(len(p.vertices) - 2 for p in ob.data.polygons); total += t
        print('PART %-12s tris %5d verts %5d' % (ob.name, t, len(ob.data.vertices)))
    print('PART total tris', total)

    C.render_views(os.path.join(REN_DIR, 'Pew_new'), views=('front', 'side', 'back', 'three'))

    sc = bpy.context.scene
    for ob in [o for o in sc.objects if o.type in ('CAMERA', 'LIGHT')]:
        data = ob.data
        bpy.data.objects.remove(ob)
        (bpy.data.cameras if isinstance(data, bpy.types.Camera) else bpy.data.lights).remove(data)
    sc.camera = None
    sc.render.filepath = '//'
    # The FBX goes out before the .blend is saved: its header stores bpy.data.filepath, still empty here.  Its exporter
    # also writes each texture's absolute path beside the relative one; hand it the relative path for both.
    import io_scene_fbx.export_fbx_bin as fbxbin
    _orig_vid_path = fbxbin._gen_vid_path

    def _relative_only(img, scene_data):
        fname_abs, fname_rel = _orig_vid_path(img, scene_data)
        return fname_rel, fname_rel
    fbxbin._gen_vid_path = _relative_only
    try:
        bpy.ops.export_scene.fbx(filepath=FBX, use_selection=False, object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
                                 bake_anim=False, path_mode='RELATIVE', axis_forward='-Z', axis_up='Y',
                                 apply_scale_options='FBX_SCALE_ALL')   # every node at scale 1; units in the file header
    finally:
        fbxbin._gen_vid_path = _orig_vid_path
    scrub_ui_paths()
    bpy.ops.wm.save_as_mainfile(filepath=BLEND, compress=False)
    bpy.ops.file.make_paths_relative()
    scrub_ui_paths()
    bpy.ops.wm.save_mainfile(compress=False)
    print('BUILD done')


if __name__ == '__main__':
    main()
