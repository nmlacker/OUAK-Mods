"""Burda, new style (Once Upon a Katamari look).  Builds everything from an empty scene:
textures, materials, meshes, the rig from ../burda_rig_spec.json, weights, renders, the .blend and the FBX.

    blender -b --factory-startup --python-exit-code 1 --python build_burda_new.py

Units are Blender metres, Z up, she faces -Y, +X is her left.  Every path is resolved from this file.
All geometry and every pixel is made here; nothing is read from any game file.
"""
import bpy, sys, os, json, math, glob
import numpy as np
from mathutils import Matrix, Vector

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
import bl_common as C

TEX_DIR = os.path.join(HERE, 'textures')
REN_DIR = os.path.join(HERE, 'renders')
RIG_SPEC = os.path.join(os.path.dirname(HERE), 'burda_rig_spec.json')
BLEND = os.path.join(HERE, 'Burda_new.blend')
FBX = os.path.join(HERE, 'Burda_new.fbx')
BODY_TEX = 'burda_new_body'
FACE_TEX = 'burda_new_face'

# ---------------------------------------------------------------- design sheet colours
SKIN, SKIN_SH = '#633E1A', '#3F250E'
HAIR, SHEEN = '#110B0A', '#3D1F07'
LIPS, NOSE, EYES = '#DF151F', '#E89127', '#110B0A'
SIL_L, SIL_M, SIL_S = '#E6E4E1', '#A09C99', '#877D74'
GR_FILL, GR_LINE = '#362C2B', '#110B0A'
ANT_Y, ANT_R = '#FEF404', '#F3131B'
DRESS, DRESS_L = '#7C0F68', '#B71B89'
TRIM, BEAD_SH, DOT = '#EAA7C7', '#BF7787', '#F5D5E5'
# expression colours, picked by rendering them on her plate
GLOOM, FLUSH = ('#5E77A0', 0.80), ('#D2342A', 0.62)          # plate tints under the hair line: colour, strength
GLOOM_FADE, FLUSH_FADE = 0.065, 0.044                        # how far down each tint fades out (metres)
EYE_W, BLUSH, TEAR = '#F8F6F2', '#FF8AA8', '#8FD0D6'
ANGER, SHOCK = '#7A82EE', '#1C2550'                          # gloom lines in navy, so they part from her black hair

# ---------------------------------------------------------------- sizes (metres)
N = 24                      # segments round every round section
W_HEAD = 0.724              # head width, OUAK Prince (x +-0.362)
ZC = 0.7485                 # head axis height (JNT_head)
RS = 0.1435                 # silver sleeve radius (Prince head, z 0.602..0.889)
RC = 0.1505                 # collar radius
RG, RG0 = 0.1495, 0.1410    # grille: widest radius, radius where it leaves the collar
RH = 0.1485                 # top of the hair pad on the sleeve
RB = 0.1325                 # window floor (backing) radius
RP = 0.1345                 # face plate radius
RO = RP + 0.0030            # hair overlay on the plate
RE = RP + 0.0045            # eyes and mouths
RV = RP + 0.0015            # blush, tears and marks: in front of the plate, behind the hair and the eyes
SAG = 0.0009                # an overlay row's chord sags at most this far inside RV
XP = 0.127                  # window inner half width (face 0.35 W)
XE = XP + 0.0015            # face plate half width
PL_LO, PL_HI = -19.6, 83.5  # face plate: degrees round the head, from its bottom edge to under the hair pad
XW = 0.136                  # window outer half width (top of the chamfer)
XS = 0.150                  # sleeve end / collar start
XC1 = 0.184                 # collar end / grille start
XGE = 0.235                 # grille: barrel ends, rounded cap starts (a near-hemisphere cap, as in the reference)
XPOLE = W_HEAD / 2          # 0.362
PH = [math.radians(-22.5 + 15.0 * j) for j in range(N)]   # head ring angles; 0 = front, 90 = top
PHB = math.radians(-19.0)   # window inner bottom edge
J_WIN0, J_WIN1 = 0, 7       # window spans PH[0]..PH[7]   (-22.5 .. 82.5): from the top of the sleeve
J_CAP1 = 11                 # hair pad spans PH[7]..PH[11] (82.5 .. 142.5)

BY0 = -0.006                # body axis y (Prince body y -0.143..0.131)
KY = 0.78                   # body depth / width
Z_HEM = 0.172               # dress hem, just above the knee joint
Z_DT = 0.550                # dress top: the neckline humps reach this height
ZBT = 0.552                 # z at the top row of the dress block in the atlas
TH = [math.radians(15.0 * j) for j in range(N)]           # body ring angles; 0 = front, 90 = her left

DOT_R = 0.0150              # polka dot radius
SPK_R = 0.0200              # sparkle tip length

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


def Rd(z):
    """Dress radius (x half width) at height z: 0.32 W at the neckline, flaring to the Prince's 0.176 at the hem."""
    if z >= Z_DT:
        return 0.1155 - 0.16 * (z - Z_DT)
    t = (Z_DT - z) / (Z_DT - Z_HEM)
    return 0.1155 + 0.0605 * (1.0 - (1.0 - t) ** 2.3)        # flares fast under the neckline, then nearly straight


# skin profile of the upper body (z, radius), top first
SKIN_PROFILE = [(0.660, 0.045), (0.610, 0.058), (0.604, 0.073), (0.597, 0.088), (0.588, 0.098),
                (0.575, 0.1045), (0.560, 0.1089), (0.500, Rd(0.500) - 0.005)]


def r_skin(z):
    zs = [p[0] for p in SKIN_PROFILE][::-1]; rs = [p[1] for p in SKIN_PROFILE][::-1]
    return float(np.interp(z, zs, rs))


def ky_at(z):
    return KY + (0.92 - KY) * float(sstep(0.592, 0.615, z))


def neck_top(th_deg):
    """Height of the dress top edge round the body: sweetheart at the front, level at the back."""
    a = abs((th_deg + 180.0) % 360.0 - 180.0)
    if a <= 27.0:
        return 0.512 + 0.038 * math.sin(math.pi / 2 * a / 27.0) ** 0.85
    if a <= 90.0:
        return 0.516 + 0.034 * (1.0 + math.cos(math.pi * (a - 27.0) / 63.0)) / 2.0
    return 0.516 + 0.004 * (1.0 - math.cos(math.pi * (a - 90.0) / 90.0)) / 2.0


# grille profile: a short barrel that bulges out of the collar, then an elliptical cap to the pole
def _grille_table():
    x1 = np.linspace(XC1, XGE, 200)
    r1 = RG - (RG - RG0) * (1.0 - (x1 - XC1) / (XGE - XC1)) ** 2.2
    a = np.linspace(0.0, math.pi / 2, 400)[1:]
    x2 = XGE + (XPOLE - XGE) * np.sin(a); r2 = RG * np.cos(a)
    xs = np.concatenate([x1, x2]); rs = np.concatenate([r1, r2])
    ds = np.hypot(np.diff(xs), np.diff(rs))
    s = np.concatenate([[0.0], np.cumsum(ds)])
    rm = np.maximum((rs[1:] + rs[:-1]) / 2, 0.02)
    phi = np.concatenate([[0.0], np.cumsum(ds / rm)])        # angle a 45-degree line turns through
    return xs, rs, s, phi


G_X, G_R, G_S, G_PHI = _grille_table()
G_LEN = float(G_S[-1])
G_AROUND = 12                                             # diamonds round the ball: 6 across the face you see
G_COLS = 2 * N // G_AROUND                                # head columns covered by the two-period grille block
G_RINGS = [(XC1 + (XGE - XC1) * t, None) for t in (0.0, 0.30, 1.0)] + \
          [(XGE + (XPOLE - XGE) * math.sin(math.radians(a)), RG * math.cos(math.radians(a))) for a in (15, 30, 45, 60, 75)]
G_RINGS = [(x, float(np.interp(x, G_X, G_R)) if r is None else r) for x, r in G_RINGS]

# ---------------------------------------------------------------- atlas layout (pixels, top-left origin)
BODY_BLOCKS = {
    'dress':    (0, 0, 256, 120),
    'grille':   (4, 130, 48, 112),
    'sleeve':   (62, 130, 20, 64),
    'collar':   (90, 130, 20, 64),
    'chamfer':  (118, 130, 8, 64),
    'hair':     (134, 130, 24, 32),
    'trim':     (134, 172, 24, 10),
    'curl':     (166, 130, 40, 60),
    'face':     (214, 130, 36, 48),
    'face_gloom': (196, 218, 24, 35),
    'face_flush': (226, 218, 24, 35),
    'skin':     (62, 204, 12, 44),
    'antY':     (82, 204, 12, 32),
    'antR':     (102, 204, 20, 24),
    'nose':     (130, 204, 12, 24),
    'bead':     (150, 204, 12, 16),
    'shoe':     (170, 204, 20, 24),
    'under':    (198, 204, 8, 8),
    'hairflat': (214, 204, 8, 8),
    'backing':  (230, 204, 8, 8),
}
# face atlas cells, by the mesh that shows them: (x, y, w, h) in pixels and the size of the quad they map to, in metres
FACE_PX = 512
FACE_CELLS_OLD = {                             # the first seven cells, 4 px apart, kept where they were
    'eye_01_m':   ((4, 4, 96, 64), (0.0405, 0.0270)),
    'eye_sing_m': ((104, 4, 96, 64), (0.0405, 0.0270)),
    'eye_03_m':   ((4, 72, 96, 64), (0.0405, 0.0270)),
    'eye_04_m':   ((104, 72, 72, 80), (0.0315, 0.0350)),
    'mouth_06_m': ((184, 72, 56, 56), (0.0280, 0.0280)),
    'mouth_01_m': ((4, 160, 128, 48), (0.0560, 0.0210)),
    'mouth_05_m': ((140, 160, 72, 88), (0.0405, 0.0495)),
}
FACE_CELLS_NEW = {                             # about 2300 px per metre, as the first seven
    'eye_02_m':   ((248, 4, 96, 64), (0.0405, 0.0270)),
    'eye_05_m':   ((350, 4, 96, 64), (0.0405, 0.0270)),
    'mouth_04_m': ((248, 74, 136, 68), (0.0591, 0.0296)),
    'mouth_10_m': ((390, 74, 80, 64), (0.0348, 0.0278)),
    'mouth_02_m': ((248, 148, 80, 64), (0.0348, 0.0278)),
    'mouth_03_m': ((334, 148, 80, 64), (0.0348, 0.0278)),
    'mouth_09_m': ((420, 148, 88, 44), (0.0383, 0.0191)),
    'mouth_08_m': ((248, 218, 112, 44), (0.0487, 0.0191)),
    'mouth_11_m': ((366, 218, 112, 80), (0.0487, 0.0348)),
    'cheak_01_m': ((248, 268, 100, 68), (0.0435, 0.0296)),
    'shock_01_m': ((366, 304, 72, 95), (0.0313, 0.0413)),
    'tear_01_m':  ((4, 258, 40, 184), (0.0174, 0.0800)),
    'mouth_07_m': ((50, 258, 72, 92), (0.0313, 0.0400)),
    'angry_01_m': ((128, 258, 72, 72), (0.0313, 0.0313)),
}
FACE_CELLS = {**FACE_CELLS_OLD, **FACE_CELLS_NEW}


def check_layout(rects, size, newer, gut=3):
    """Every rect lies inside the atlas and no gutter covers another rect.  The rects named in newer also keep their
    gutter inside the atlas, clear of every other gutter."""
    grow = lambda r: (r[0] - gut, r[1] - gut, r[0] + r[2] + gut, r[1] + r[3] + gut)
    hit = lambda a, b: a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]
    for n, r in rects.items():
        g = grow(r)
        box = g if n in newer else (r[0], r[1], r[0] + r[2], r[1] + r[3])
        assert box[0] >= 0 and box[1] >= 0 and box[2] <= size and box[3] <= size, n
        for m, s in rects.items():
            if m == n:
                continue
            assert not hit(g, (s[0], s[1], s[0] + s[2], s[1] + s[3])), (n, m)
            if n in newer:
                assert not hit(g, grow(s)), (n, m)


CURL_X0, CURL_X1 = -0.1285, -0.075            # the fringe columns that carry the curl cell
CURL_P0, CURL_P1 = 34.5, 83.5                 # degrees round the head covered by the curl cell
CURL_LEG = (-0.119, 0.822, 0.855)             # curl: x of its leg, bottom (under the hair line) and top, front-view z
CURL_HOOK = (-0.106, 0.855, 0.012, -40.0)     # then a hook clockwise round (x, z) at this radius, ending at this angle


def uvb(name, u, v):
    x, y, w, h = BODY_BLOCKS[name]
    return ((x + u * w) / 256.0, 1.0 - (y + v * h) / 256.0)


def uvf(name, u, v):
    x, y, w, h = FACE_CELLS[name][0]
    return ((x + u * w) / FACE_PX, 1.0 - (y + v * h) / FACE_PX)


# ---------------------------------------------------------------- texture painting
SS = 4   # supersampling


def paint(canvas, rect, fn, wrap_u=False, gut=3):
    """Fill rect plus a gutter with fn(U, V) -> rgb.  U, V run 0..1 over the rect, clamped (or wrapped) in the gutter."""
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


def dress_features():
    """Dots and four-point sparkles as (theta degrees, z, kind) on the 180-degree dress block."""
    # as sparse as the reference: rows of 3 (at -60/0/60) and 4 (at +-22.5/+-67.5) alternate, so no column stacks
    rows = [(0.478, 3, 0.50), (0.405, 4, 0.50), (0.332, 3, 0.50), (0.259, 4, 0.50), (0.195, 3, 0.50)]
    spark = {(0, 1), (1, 3), (2, 0), (3, 2), (4, 2)}
    jit = [(2.0, 0.003), (-3.0, -0.004), (1.0, 0.005), (-2.0, 0.002), (3.0, -0.003), (-1.0, 0.004)]
    out = []
    for i, (z, n, ph) in enumerate(rows):
        for k in range(n):
            dt, dz = jit[(k + 2 * i) % len(jit)]
            th = -90.0 + 180.0 * (k + ph) / n + dt
            out.append((th, z + dz, 'spark' if (i, k) in spark else 'dot', 1.0))
    # two small ones on the front humps of the neckline (above the dress top at the back, so unseen there)
    out += [(-27.0, 0.5215, 'spark', 0.62), (27.0, 0.5215, 'dot', 0.75)]
    return out


FEATURES = dress_features()


def f_dress(U, V):
    th = -90.0 + 180.0 * U
    z = ZBT - V * (ZBT - Z_HEM)
    cs = np.cos(np.radians(th))
    c = mix3(hx('#560A49'), hx(DRESS), sstep(0.0, 0.6, cs))                      # darker toward the sides
    c = mix3(c, hx(DRESS_L), 0.90 * np.exp(-((th + 46.0) / 7.5) ** 2))            # satin streak on her right
    c = mix3(c, hx('#EE8FD0'), 0.60 * np.exp(-((th + 47.5) / 2.4) ** 2))          # its bright core
    c = mix3(c, hx(DRESS_L), 0.60 * np.exp(-((th - 50.0) / 13.0) ** 2))           # broad soft band on her left
    c = c * (1.0 - 0.20 * sstep(0.27, Z_HEM, z))[..., None]                       # darker toward the hem
    c = mix3(c, hx(DRESS_L), 0.10 * sstep(0.46, 0.55, z))
    m = np.zeros(U.shape, dtype=bool)
    for t0, z0, kind, sc in FEATURES:
        dth = (th - t0 + 90.0) % 180.0 - 90.0
        rx = Rd(z0)
        g = math.hypot(rx * math.cos(math.radians(t0)), KY * rx * math.sin(math.radians(t0)))
        du = np.radians(dth) * g                       # metres round the body
        dv = (z - z0) * 1.02                           # metres up the slope
        if kind == 'dot':
            m |= du * du + dv * dv <= (DOT_R * sc) ** 2
        else:
            m |= np.sqrt(np.abs(du) / (SPK_R * sc)) + np.sqrt(np.abs(dv) / (SPK_R * sc)) <= 1.0
    return mix3(c, hx(DOT), m.astype(np.float64))


def f_grille(U, V):
    s = V * G_LEN
    r = np.interp(s, G_S, G_R); phi = np.interp(s, G_S, G_PHI)
    b = phi / (2.0 * math.pi / G_AROUND) / 1.25        # periods travelled along the profile; cells 1.25x wider than tall
    a = 2.0 * U                                        # the block is two periods wide
    f1 = (a + b) % 1.0; f2 = (a - b) % 1.0
    d1 = np.minimum(f1, 1.0 - f1); d2 = np.minimum(f2, 1.0 - f2)
    q = np.minimum(d1, d2)
    wl = 0.100
    fill = mix3(hx(GR_FILL) * 0.72, hx(GR_FILL), sstep(wl, 0.42, q))                          # pillowed cells
    c = np.where((q < wl)[..., None], hx(GR_LINE), fill)
    c = c * (1.0 - 0.20 * sstep(G_LEN * 0.35, G_LEN, s))[..., None]                            # darker toward the end
    return mix3(c, hx(GR_FILL) * 0.72, sstep(0.040, 0.015, r))                                 # the mesh closes up at the end, only inside the pole fan


def f_sleeve(U, V):
    h = 1.0 - V
    c = mix3(hx(SIL_S), hx(SIL_M), sstep(0.0, 0.16, h))
    c = mix3(c, hx('#C4C1BE'), sstep(0.12, 0.36, h))
    c = mix3(c, hx(SIL_L), sstep(0.50, 0.85, h))
    c = mix3(c, hx(SIL_L), 0.22 * np.exp(-((h - 0.21) / 0.045) ** 2))             # soft shine on the lip
    return c * (1.0 - 0.22 * sstep(0.50, 1.0, U))[..., None]


def f_collar(U, V):
    h = 1.0 - V
    c = mix3(hx(SIL_M), hx(SIL_L), sstep(0.0, 0.5, h))
    c = mix3(c, hx(SIL_S), 0.35 * (1.0 - sstep(0.0, 0.2, h)))
    c = c * (0.80 + 0.20 * np.sin(math.pi * np.clip((U - 0.22) / 0.56, 0.0, 1.0)))[..., None]   # crowned: round, not a flat bar
    rim = 1.0 - sstep(0.0, 0.22, U) * (1.0 - sstep(0.78, 1.0, U))
    return mix3(c, hx(SIL_S), 0.75 * rim)


def f_chamfer(U, V):
    h = 1.0 - V
    side = mix3(hx(SIL_M), hx(SIL_L), sstep(0.2, 0.7, h))
    return np.where((U < 0.5)[..., None], hx('#F4F3F1'), side)


def f_hair(U, V):
    return mix3(hx(HAIR), hx('#2B1A12'), 0.8 * np.exp(-((V - 0.33) / 0.16) ** 2))


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


def f_curl(U, V):
    """The curl, drawn in front-view coordinates so it shows undistorted from the front: a leg rising from the hair line
    near the window edge, then a hook over the top that comes down on the right."""
    x = CURL_X0 + U * (CURL_X1 - CURL_X0)
    z = ZC + RO * np.sin(np.radians(CURL_P1 - V * (CURL_P1 - CURL_P0)))
    xl, z0, z1 = CURL_LEG; cx, cz, rr, a_end = CURL_HOOK
    pts = [(xl, zz) for zz in np.linspace(z0, z1, 8)]
    pts += [(cx + rr * math.cos(math.radians(a)), cz + rr * math.sin(math.radians(a))) for a in np.linspace(180.0, a_end, 40)[1:]]
    seg = [math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(pts[:-1], pts[1:])]
    cum = np.concatenate([[0.0], np.cumsum(seg)]) / sum(seg)
    pw = [(px, pz, 0.0042 * min(1.0, 0.6 + f * 5.0) * min(1.0, 0.15 + (1.0 - f) * 4.0)) for (px, pz), f in zip(pts, cum)]
    d, w = dist_polyline(x, z, pw)
    core = mix3(hx(SHEEN), hx(SKIN), 0.85)
    line = mix3(core, hx(SHEEN), sstep(0.35, 1.0, d / np.maximum(w, 1e-6)))
    return np.where((d <= w)[..., None], line, hx(HAIR))


def f_face(U, V):
    c = mix3(hx(SKIN), hx(SKIN_SH), 0.30 * (1.0 - sstep(0.25, 0.55, V)))          # soft shadow under the hair
    c = mix3(c, hx(SKIN_SH), 0.55 * sstep(0.86, 1.0, V))                          # darker toward the lip
    c = mix3(c, hx(SKIN_SH), 0.25 * (1.0 - sstep(0.0, 0.08, np.minimum(U, 1.0 - U))))
    return np.clip(c * (1.0 + 0.06 * np.exp(-((V - 0.62) / 0.2) ** 2))[..., None], 0, 1)


def f_face_tint(tint, fade):
    """The plate with a tint that starts right under the hair line and fades out downward over fade metres, at once
    (no flat band under the hair) and smoothly to nothing.  Each texel's front-view x and z come from the plate's
    mapping: U linear in x, V linear in the angle."""
    col, k = tint
    xs = np.linspace(-XE, XE, 257)
    zs = np.array([catmull(FRINGE, x) for x in xs])

    def f(U, V):
        x = (2.0 * U - 1.0) * XE
        z = ZC + RP * np.sin(np.radians(PL_HI - V * (PL_HI - PL_LO)))
        t = np.clip((np.interp(x, xs, zs) - z) / fade, 0.0, 1.0)
        return mix3(f_face(U, V), hx(col), k * (1.0 - t) ** 1.4)
    return f


def f_skin(U, V):
    c = mix3(np.clip(hx(SKIN) * 1.12, 0, 1), hx(SKIN), sstep(0.0, 0.35, V))
    return mix3(c, hx(SKIN_SH), sstep(0.45, 1.0, V))


def f_antY(U, V): return mix3(hx(ANT_Y), hx('#F0B800'), sstep(0.55, 1.0, V))
def f_antR(U, V): return mix3(mix3(hx('#FF5A4E'), hx(ANT_R), sstep(0.0, 0.4, V)), hx('#B90D14'), sstep(0.6, 1.0, V))
def f_nose(U, V): return mix3(mix3(hx('#F7B552'), hx(NOSE), sstep(0.0, 0.5, V)), hx('#C56E12'), sstep(0.75, 1.0, V))
def f_bead(U, V): return mix3(mix3(hx('#F8CFE2'), hx(TRIM), sstep(0.0, 0.35, V)), hx(BEAD_SH), sstep(0.5, 1.0, V))
def f_trim(U, V): return mix3(mix3(hx('#F4BFD9'), hx(TRIM), sstep(0.0, 0.5, V)), hx(BEAD_SH), 0.35 * sstep(0.6, 1.0, V))
def f_shoe(U, V): return mix3(mix3(hx(DRESS_L), hx(DRESS), sstep(0.05, 0.5, V)), hx('#560A49'), sstep(0.6, 1.0, V))
def flat(col): return lambda U, V: np.broadcast_to(hx(col), U.shape + (3,))


def downsample(canvas):
    n = canvas.shape[0] // SS
    return canvas.reshape(n, SS, n, SS, canvas.shape[2]).mean(axis=(1, 3))


def save_png(name, arr, alpha):
    """arr: H x W x 4, rows top-down, sRGB values 0..1.  Saved as an 8-bit PNG in textures/."""
    h, w = arr.shape[:2]
    img = bpy.data.images.new(name + '_paint', w, h, alpha=alpha)
    img.pixels.foreach_set(np.ascontiguousarray(arr[::-1], dtype=np.float32).ravel())
    path = os.path.join(TEX_DIR, name + '.png')
    img.filepath_raw = path; img.file_format = 'PNG'; img.save()
    bpy.data.images.remove(img)
    return path


def paint_body():
    cv = np.zeros((256 * SS, 256 * SS, 3))
    cv[:] = hx(DRESS) * 0.6
    P = lambda n, f, **k: paint(cv, BODY_BLOCKS[n], f, **k)
    P('dress', f_dress, wrap_u=True)
    P('grille', f_grille, wrap_u=True)
    P('sleeve', f_sleeve); P('collar', f_collar); P('chamfer', f_chamfer)
    P('hair', f_hair); P('trim', f_trim); P('curl', f_curl); P('face', f_face); P('skin', f_skin)
    P('face_gloom', f_face_tint(GLOOM, GLOOM_FADE)); P('face_flush', f_face_tint(FLUSH, FLUSH_FADE))
    P('antY', f_antY); P('antR', f_antR); P('nose', f_nose); P('bead', f_bead); P('shoe', f_shoe)
    P('under', flat('#3E0835')); P('hairflat', flat(HAIR)); P('backing', flat(SKIN_SH))
    rgb = downsample(cv)
    return save_png(BODY_TEX, np.dstack([rgb, np.ones(rgb.shape[:2])]), alpha=False)


def m_ellipse(X, Y, a, b, cx=0.0, cy=0.0): return ((X - cx) / a) ** 2 + ((Y - cy) / b) ** 2 <= 1.0


def frame(X, Y, deg):
    """Coordinates in a frame turned by deg (counter-clockwise)."""
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return X * c + Y * s, -X * s + Y * c


def m_rbox(X, Y, hw, hh, r):
    qx = np.abs(X) - (hw - r); qy = np.abs(Y) - (hh - r)
    return np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - r <= 0


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
    """mesh name -> (alpha function over metres from the cell centre, colour).  An alpha function may return a mask
    or a float alpha in 0..1.  Eyes, the blush and the tears are drawn as her right one (left in a front picture);
    the mesh mirrors the UVs for the other one.  The two one-sided marks sit on her left."""
    def eye_01(X, Y):                     # half-lidded: a slanted almond, flatter on top
        x, y = frame(X, Y, -15.0)
        a = 0.0170
        u = np.clip(1.0 - (x / a) ** 2, 0.0, None)
        return (np.abs(x) <= a) & (y <= 0.0038 * u ** 0.9 + 0.0007) & (y >= -0.0095 * u ** 0.75)

    def eye_02(X, Y):                     # half-closed: her almond's lower lid under a flat, lowered top lid
        x, y = frame(X, Y, -11.0)
        a = 0.0170
        u = np.clip(1.0 - (x / a) ** 2, 0.0, None)
        return (np.abs(x) <= a) & (y <= -0.0004) & (y >= -0.0095 * u ** 0.75)

    def eye_sing(X, Y):                   # closed, curved lid
        x, y = frame(X, Y, -8.0)
        pts = [(0.0160 * t, 0.0032 - 0.0068 * (1 - t * t), 0.0030) for t in np.linspace(-1, 1, 17)]
        d, w = dist_polyline(x, y, pts)
        return d <= w

    def eye_03(X, Y):                     # closed, a line
        x, y = frame(X, Y, -10.0)
        d, w = dist_polyline(x, y, [(-0.0160, 0.0, 0.0027), (0.0160, 0.0, 0.0027)])
        return d <= w

    def eye_04(X, Y): return m_ellipse(X, Y, 0.0125, 0.0145)

    def eye_05(X, Y):                     # white: a slightly fuller almond
        x, y = frame(X, Y, -12.0)
        a = 0.0172
        u = np.clip(1.0 - (x / a) ** 2, 0.0, None)
        return (np.abs(x) <= a) & (y <= 0.0050 * u ** 0.8 + 0.0008) & (y >= -0.0098 * u ** 0.75)

    def mouth_01(X, Y): return m_rbox(X, Y, 0.0235, 0.0065, 0.0048)
    def mouth_02(X, Y): return m_rbox(X, Y, 0.0135, 0.0110, 0.0060)              # open, a rounded square
    def mouth_03(X, Y):                                                          # open, arched over a gently curved bottom
        return dome(X, Y, 0.0150, -0.0105, 0.0215, 0.0030) & (Y >= -0.0105 + 0.0020 * (1.0 - (X / 0.0150) ** 2))

    def mouth_04(X, Y):                                                          # wide open grin: flat top, round bottom
        yt = 0.0085
        return (Y <= yt) & m_ellipse(X, Y, 0.0265, 0.0220, 0.0, yt) & m_rbox(X, Y - (yt - 0.0125), 0.0265, 0.0125, 0.0035)

    def mouth_05(X, Y): return m_ellipse(X, Y, 0.0155, 0.0195)
    def mouth_06(X, Y): return m_ellipse(X, Y, 0.0105, 0.0105)

    def mouth_07(X, Y):                                                          # open, a drop of drool from one corner
        yb = 0.0020
        d, w = dist_polyline(X, Y, [(0.0058, yb + 0.0012, 0.0032), (0.0060, yb - 0.0038, 0.0019), (0.0062, yb - 0.0092, 0.0032)])
        return dome(X, Y, 0.0125, yb, 0.0165, 0.0028) | (d <= w)

    def mouth_08(X, Y):                                                          # wavy closed line
        return line(X, Y, [(x, 0.0019 * math.cos(2.0 * math.pi * x / 0.0160)) for x in np.linspace(-0.0200, 0.0200, 41)], 0.0030)

    def mouth_09(X, Y):                                                          # closed, an upside-down U
        return line(X, Y, [(0.0155 * t, -0.0035 + 0.0075 * (1 - t * t)) for t in np.linspace(-1, 1, 17)], 0.0030)

    def mouth_10(X, Y):                                                          # puckered to one side: a ring of lip, fuller below,
        xr = 0.0045                                                              # and a crease on the cheek it is pushed away from
        ring = m_ellipse(X, Y, 0.0090, 0.0085, xr, 0.0) & ~m_ellipse(X, Y, 0.0036, 0.0028, xr, 0.0016)
        rc = 0.0090 + 0.0047 + 0.0021
        return ring | line(X, Y, [(xr - rc * math.cos(math.radians(a)), rc * math.sin(math.radians(a))) for a in np.linspace(-34.0, 34.0, 9)], 0.0021)

    def mouth_11(X, Y):                                                          # wavy open mouth with drool
        yc = 0.0060
        m = m_rbox(X, Y - yc, 0.0190, 0.0050, 0.0050)                           # a band with four round lobes on it
        for xc in (-0.0162, -0.0054, 0.0054, 0.0162):
            m |= m_ellipse(X, Y, 0.0060, 0.0071, xc, yc)
        d, w = dist_polyline(X, Y, [(0.0070, yc - 0.0050, 0.0028), (0.0072, yc - 0.0098, 0.0018), (0.0074, yc - 0.0146, 0.0029)])
        return m | (d <= w)

    def angry_01(X, Y):                   # anger mark: four arcs bowed toward the middle, a cross of gaps between them,
        m = np.zeros(X.shape, dtype=bool)  # about 5 mm wide so they still show in a small picture
        for k in range(4):
            t = math.radians(45.0 + 90.0 * k)
            cx, cy = 0.0151 * math.cos(t), 0.0151 * math.sin(t)
            m |= line(X, Y, [(cx - 0.0058 * math.cos(t + math.radians(a)), cy - 0.0058 * math.sin(t + math.radians(a)))
                             for a in np.linspace(-60.0, 60.0, 13)], 0.0026)
        return m

    def cheak_01(X, Y):                   # blush: a soft rose oval
        return 0.55 * np.clip(1.0 - (X / 0.0185) ** 2 - (Y / 0.0125) ** 2, 0.0, 1.0) ** 2

    def shock_01(X, Y):                   # gloom lines: three strokes hanging under the hair line, with bare skin between
        cx, cz, _ = OVERLAYS['shock_01_m']
        m = np.zeros(X.shape, dtype=bool)
        for xa, ln in ((0.092, 0.020), (0.104, 0.028), (0.116, 0.024)):
            zt = float(hair_edge(xa)) - 0.0130      # low enough that the quad's top row still faces the front
            m |= line(X, Y, [(xa - cx, zt - cz), (xa - cx, zt - ln - cz)], 0.0022)
        return m

    def tear_01(X, Y):                    # a tear streak, wavy, a little wider as it runs down
        hm = FACE_CELLS['tear_01_m'][1][1]
        top, bot = hm / 2 - 0.0026, -hm / 2 + 0.0045
        ys = np.linspace(top, bot, 41)
        d, w = dist_polyline(X, Y, [(0.0016 * math.sin(2.0 * math.pi * (top - y) / 0.0270), y, 0.0021 + 0.0011 * (top - y) / (top - bot))
                                    for y in ys])
        return d <= w

    return {'eye_01_m': (eye_01, EYES), 'eye_02_m': (eye_02, EYES), 'eye_03_m': (eye_03, EYES), 'eye_04_m': (eye_04, EYES),
            'eye_05_m': (eye_05, EYE_W), 'eye_sing_m': (eye_sing, EYES),
            'mouth_01_m': (mouth_01, LIPS), 'mouth_02_m': (mouth_02, LIPS), 'mouth_03_m': (mouth_03, LIPS),
            'mouth_04_m': (mouth_04, LIPS), 'mouth_05_m': (mouth_05, LIPS), 'mouth_06_m': (mouth_06, LIPS),
            'mouth_07_m': (mouth_07, LIPS), 'mouth_08_m': (mouth_08, LIPS), 'mouth_09_m': (mouth_09, LIPS),
            'mouth_10_m': (mouth_10, LIPS), 'mouth_11_m': (mouth_11, LIPS),
            'angry_01_m': (angry_01, ANGER), 'cheak_01_m': (cheak_01, BLUSH), 'shock_01_m': (shock_01, SHOCK),
            'tear_01_m': (tear_01, TEAR)}


def cell_alpha(fn, wm, hm, U, V):
    inside = (U > 0.0) & (U < 1.0) & (V > 0.0) & (V < 1.0)
    return np.clip(np.asarray(fn((U - 0.5) * wm, (0.5 - V) * hm), dtype=np.float64), 0.0, 1.0) * inside


def paint_face():
    shapes = face_shapes()
    assert set(shapes) == set(FACE_CELLS), set(shapes) ^ set(FACE_CELLS)
    check_marks(shapes)
    cv = np.zeros((FACE_PX * SS, FACE_PX * SS, 4))
    cv[..., :3] = 0.5
    for name, (fn, col) in shapes.items():
        rect, (wm, hm) = FACE_CELLS[name]

        def cell(U, V, fn=fn, col=col, wm=wm, hm=hm):
            return np.dstack([np.broadcast_to(hx(col), U.shape + (3,)), cell_alpha(fn, wm, hm, U, V)])
        paint(cv, rect, cell, gut=3)
    return save_png(FACE_TEX, downsample(cv), alpha=True)


def check_marks(shapes):
    """Every overlay lies on bare skin, in a front view.  Every texel it paints (on both sheets of a pair) is under the
    hair line, clear of the lock and inside the plate; the one-sided marks are also outside every eye quad."""
    lz = [catmull(FRINGE, -0.123) if z is None else z for z, x in LOCK][::-1]
    lx = [x for z, x in LOCK][::-1]
    pl_lo = ZC + RP * math.sin(math.radians(PL_LO))
    for name in OVERLAYS:
        fn = shapes[name][0]
        wm, hm = FACE_CELLS[name][1]
        cx, cz, pair = OVERLAYS[name]
        U, V = np.meshgrid(np.linspace(0.0, 1.0, 241), np.linspace(0.0, 1.0, 241))
        on = cell_alpha(fn, wm, hm, U, V) > 0.0
        assert on.any(), name
        z = cz + (0.5 - V) * hm
        for sd in ((1.0, -1.0) if pair else (1.0,)):
            x = sd * (cx + (U - 0.5) * wm)                 # the mirrored sheet of a pair flips the drawing
            hair = float((hair_edge(x) - z)[on].min())
            lock = (x - np.interp(z, lz, lx))[on & (z >= lz[0])]
            lock = float(lock.min()) if lock.size else 1.0
            side = float((XP - np.abs(x))[on].min()); low = float((z - pl_lo)[on].min())
            print('MARK %-10s x %.4f..%.4f z %.4f..%.4f, clear of the hair line by %.4f, the lock %.4f, the window side %.4f, '
                  'the plate bottom %.4f' % (name, x[on].min(), x[on].max(), z[on].min(), z[on].max(), hair, lock, side, low))
            assert min(hair, lock, side, low) > 0.0015, name
            if not pair:
                for eye, ez in EYE_PIECES:
                    ew, eh = FACE_CELLS[eye][1]
                    assert not (on & (np.abs(np.abs(x) - EYE_X) < ew / 2) & (np.abs(z - ez) < eh / 2)).any(), (name, eye)


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
    for i in range(len(ring_pts) - 1):
        for j in range(N):
            m.face([ring_pts[i][j], ring_pts[i][j + 1], ring_pts[i + 1][j + 1], ring_pts[i + 1][j]],
                   [uv_rows[i][j], uv_rows[i][j + 1], uv_rows[i + 1][j + 1], uv_rows[i + 1][j]], out, grp, w)


# ---------------------------------------------------------------- head
def hp(x, r, phi):
    return (x, -r * math.cos(phi), ZC + r * math.sin(phi))


def head_radial(c): return (0.0, c[1], c[2] - ZC)
def ring_h(phi): return 1.0 - (math.sin(phi) + 1.0) / 2.0     # v in the silver blocks: 0 at the top of the ring


def build_head():
    m = MB('head_m')
    ph = PH + [PH[0] + 2 * math.pi]
    for sd in (1.0, -1.0):
        # silver band beside the window, full ring
        for j in range(N):
            a, b = ph[j], ph[j + 1]
            m.face([hp(sd * XW, RS, a), hp(sd * XS, RS, a), hp(sd * XS, RS, b), hp(sd * XW, RS, b)],
                   [uvb('sleeve', 0, ring_h(a)), uvb('sleeve', 1, ring_h(a)), uvb('sleeve', 1, ring_h(b)), uvb('sleeve', 0, ring_h(b))],
                   head_radial, 'sleeve')
        # collar: wall, bevel, top, bevel, wall
        prof = [(XS, RS, 0.0), (XS + 0.0005, RC - 0.0022, 0.10), (XS + 0.0032, RC, 0.22),
                (XC1 - 0.0032, RC, 0.78), (XC1 - 0.0005, RC - 0.0022, 0.90), (XC1, RG0, 1.0)]
        grp = ['collar_in', 'collar', 'collar', 'collar', 'collar_out']
        outs = [(-sd, 0.0, 0.0), head_radial, head_radial, head_radial, (sd, 0.0, 0.0)]
        for k in range(5):
            (x0, r0, u0), (x1, r1, u1) = prof[k], prof[k + 1]
            for j in range(N):
                a, b = ph[j], ph[j + 1]
                m.face([hp(sd * x0, r0, a), hp(sd * x1, r1, a), hp(sd * x1, r1, b), hp(sd * x0, r0, b)],
                       [uvb('collar', u0, ring_h(a)), uvb('collar', u1, ring_h(a)), uvb('collar', u1, ring_h(b)), uvb('collar', u0, ring_h(b))],
                       outs[k], grp[k])
        # grille ball
        ref = (sd * 0.25, 0.0, ZC)
        gout = lambda c, ref=ref: vsub(c, ref)
        vv = [float(np.interp(x, G_X, G_S)) / G_LEN for x, r in G_RINGS]
        for k in range(len(G_RINGS) - 1):
            (x0, r0), (x1, r1) = G_RINGS[k], G_RINGS[k + 1]
            for j in range(N):
                a, b = ph[j], ph[j + 1]
                u0 = (j % G_COLS) / float(G_COLS); u1 = u0 + 1.0 / G_COLS
                m.face([hp(sd * x0, r0, a), hp(sd * x1, r1, a), hp(sd * x1, r1, b), hp(sd * x0, r0, b)],
                       [uvb('grille', u0, vv[k]), uvb('grille', u0, vv[k + 1]), uvb('grille', u1, vv[k + 1]), uvb('grille', u1, vv[k])],
                       gout, 'grille')
        xl, rl = G_RINGS[-1]
        for j in range(N):
            a, b = ph[j], ph[j + 1]
            u0 = (j % G_COLS) / float(G_COLS); u1 = u0 + 1.0 / G_COLS
            m.face([hp(sd * xl, rl, a), hp(sd * xl, rl, b), (sd * XPOLE, 0.0, ZC)],
                   [uvb('grille', u0, vv[-1]), uvb('grille', u1, vv[-1]), uvb('grille', (u0 + u1) / 2, 1.0)], gout, 'grille')
        # window side chamfer
        inner = [PHB] + PH[J_WIN0 + 1:J_WIN1 + 1]
        for j in range(J_WIN0, J_WIN1):
            a, b = PH[j], PH[j + 1]; ia, ib = inner[j], inner[j + 1]
            m.face([hp(sd * XW, RS, a), hp(sd * XW, RS, b), hp(sd * XP, RB, ib), hp(sd * XP, RB, ia)],
                   [uvb('chamfer', 0.75, ring_h(a)), uvb('chamfer', 0.75, ring_h(b)), uvb('chamfer', 0.75, ring_h(ib)), uvb('chamfer', 0.75, ring_h(ia))],
                   head_radial, 'chamfer_side_%d' % sd)
    # sleeve between the window edges: all round except the window (top, back, underside and the lip)
    for j in range(J_WIN1, N):
        a, b = ph[j], ph[j + 1]
        m.face([hp(-XW, RS, a), hp(XW, RS, a), hp(XW, RS, b), hp(-XW, RS, b)],
               [uvb('sleeve', 0, ring_h(a)), uvb('sleeve', 0, ring_h(a)), uvb('sleeve', 0, ring_h(b)), uvb('sleeve', 0, ring_h(b))],
               head_radial, 'sleeve')
    # window bottom chamfer (the bright top of the lip)
    m.face([hp(-XW, RS, PH[0]), hp(XW, RS, PH[0]), hp(XP, RB, PHB), hp(-XP, RB, PHB)],
           [uvb('chamfer', 0.25, 0.3), uvb('chamfer', 0.25, 0.3), uvb('chamfer', 0.25, 0.7), uvb('chamfer', 0.25, 0.7)],
           head_radial, 'chamfer_bottom')
    # window floor behind the face plate
    inner = [PHB] + PH[J_WIN0 + 1:J_WIN1 + 1]
    for j in range(J_WIN0, J_WIN1):
        m.face([hp(-XP, RB, inner[j]), hp(XP, RB, inner[j]), hp(XP, RB, inner[j + 1]), hp(-XP, RB, inner[j + 1])],
               [uvb('backing', 0.5, 0.5)] * 4, head_radial, 'backing')
    # wall over the forehead, between the window floor and the sleeve top (hair coloured)
    a = PH[J_WIN1]
    fwd = (0.0, -math.sin(a), -math.cos(a))
    m.face([hp(-XP, RB, a), hp(XP, RB, a), hp(XW, RS, a), hp(-XW, RS, a)], [uvb('hairflat', 0.5, 0.5)] * 4, fwd, 'brow')
    # hair top: a soft pad lying on the sleeve from the window top (82.5) to the back (142.5), edges sunk into it
    a0, a1 = math.degrees(PH[J_WIN1]), math.degrees(PH[J_CAP1])
    cols = [(-1.0, 0.65), (-0.88, 0.9), (-0.5, 1.0), (0.0, 1.0), (0.5, 1.0), (0.88, 0.9), (1.0, 0.65)]   # (x / XW, height)
    rows = [(a0 - 0.5, 0.62), (90.0, 1.0), (110.0, 0.97), (130.0, 0.85), (a1, 0.60)]   # (degrees, height); row 0 clear of the brow plane
    low = lambda u, i: hp(u * XW, RS - 0.002, math.radians(rows[i][0]))
    pad = lambda k, i: hp(cols[k][0] * XW, RS - 0.002 + (RH - RS + 0.002) * rows[i][1] * cols[k][1], math.radians(rows[i][0]))
    puv = lambda k, i: uvb('hair', (cols[k][0] + 1.0) / 2.0, (rows[i][0] - rows[0][0]) / (a1 - rows[0][0]))
    for i in range(len(rows) - 1):
        for k in range(len(cols) - 1):
            m.face([pad(k, i), pad(k + 1, i), pad(k + 1, i + 1), pad(k, i + 1)], [puv(k, i), puv(k + 1, i), puv(k + 1, i + 1), puv(k, i + 1)],
                   head_radial, 'pad')
        for k, sd in ((0, -1.0), (len(cols) - 1, 1.0)):   # low side walls, so the pad ends cleanly beside the silver bands
            m.face([pad(k, i), pad(k, i + 1), low(sd, i + 1), low(sd, i)], [uvb('hairflat', 0.5, 0.5)] * 4, (sd, 0.0, 0.0), 'pad_side_%d' % sd)
    last = len(rows) - 1
    for k in range(len(cols) - 1):                        # its front face, flush with the wall over the forehead, and its back
        m.face([pad(k, 0), pad(k + 1, 0), low(cols[k + 1][0], 0), low(cols[k][0], 0)], [uvb('hairflat', 0.5, 0.5)] * 4, fwd, 'pad_front')
        m.face([pad(k, last), pad(k + 1, last), low(cols[k + 1][0], last), low(cols[k][0], last)], [uvb('hairflat', 0.5, 0.5)] * 4,
               (0.0, math.sin(PH[J_CAP1]), math.cos(PH[J_CAP1])), 'pad_back')
    build_hair_on_plate(m)
    return m


# ---------------------------------------------------------------- face plate, hair on it, eyes, mouths, nose
def fp(x, zf, r):
    """Point on the head cylinder of radius r that shows at (x, zf) in a front view."""
    return (x, -math.sqrt(max(r * r - (zf - ZC) ** 2, 0.0)), zf)


def catmull(pts, x):
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    i = int(np.clip(np.searchsorted(xs, x) - 1, 0, len(xs) - 2))
    p0 = ys[max(i - 1, 0)]; p1 = ys[i]; p2 = ys[i + 1]; p3 = ys[min(i + 2, len(ys) - 1)]
    t = (x - xs[i]) / (xs[i + 1] - xs[i])
    return 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3)


FRINGE = [(-0.1285, 0.8270), (-0.117, 0.8275), (-0.105, 0.8290), (-0.090, 0.8285), (-0.076, 0.8272), (-0.050, 0.8240),
          (-0.021, 0.8236), (0.008, 0.8272), (0.0375, 0.8350), (0.0667, 0.8478), (0.090, 0.8625), (0.107, 0.8760),
          (0.1285, 0.8820)]            # on her left the hair line rises to the window edge just under its top
# the fringe mesh's columns; its lower edge runs straight between them, a little off the curve
FRINGE_XS = [-0.1285, -0.117, -0.100, -0.0875, -0.075, -0.055, -0.035, -0.015, 0.005, 0.025, 0.045, 0.063, 0.080, 0.095,
             0.107, 0.1285]
Z_HAIR_TOP = ZC + RO * math.sin(math.radians(PL_HI))       # the fringe's top edge, under the hair pad
_HX = np.linspace(-XE, XE, 1025)
_HZ = np.minimum([min(catmull(FRINGE, x), Z_HAIR_TOP) for x in _HX],
                 np.interp(_HX, FRINGE_XS, [min(catmull(FRINGE, x), Z_HAIR_TOP) for x in FRINGE_XS]))


def hair_edge(x):
    """Front-view z of the hair line at x: the lower of the curve and the fringe mesh's real edge."""
    return np.interp(x, _HX, _HZ)


# lock down her right edge: (front-view z, x of its inner edge), top first
LOCK = [(None, -0.123), (0.800, -0.123), (0.7705, -0.123), (0.7690, -0.110), (0.7680, -0.092), (0.7645, -0.095),
        (0.760, -0.100), (0.755, -0.107), (0.749, -0.115), (0.7425, -0.1285)]


def build_plate(name, block):
    """The brown plate under the window, 12 rows round the head.  The head is a cylinder along x and U runs
    linearly across x, so one column draws the same picture as several."""
    m = MB(name)
    rows = 12
    for i in range(rows):
        a = math.radians(PL_LO + (PL_HI - PL_LO) * i / rows); b = math.radians(PL_LO + (PL_HI - PL_LO) * (i + 1) / rows)
        va, vb = 1.0 - i / rows, 1.0 - (i + 1) / rows
        m.face([hp(-XE, RP, a), hp(XE, RP, a), hp(XE, RP, b), hp(-XE, RP, b)],
               [uvb(block, 0, va), uvb(block, 1, va), uvb(block, 1, vb), uvb(block, 0, vb)], head_radial, 'plate')
    return m


def build_hair_on_plate(m):
    """The fringe and the lock, on the head so they stay whichever plate shows."""
    p_hi = PL_HI
    # fringe: from under the hair pad down to the hair line
    z_top = Z_HAIR_TOP
    xs = FRINGE_XS
    nrow = 3

    def fr_pt(x, t):
        lo = math.asin((min(catmull(FRINGE, x), z_top) - ZC) / RO); hi = math.radians(p_hi)
        return hp(x, RO, lo + (hi - lo) * t)

    def fr_uv(x, t):
        if x <= CURL_X1 + 1e-9:
            lo = math.degrees(math.asin((min(catmull(FRINGE, x), z_top) - ZC) / RO))
            p = lo + (p_hi - lo) * t
            return uvb('curl', (x - CURL_X0) / (CURL_X1 - CURL_X0), (CURL_P1 - p) / (CURL_P1 - CURL_P0))
        return uvb('hairflat', 0.5, 0.5)
    for k in range(len(xs) - 1):
        x0, x1 = xs[k], xs[k + 1]
        curl = x1 <= CURL_X1 + 1e-9
        for i in range(nrow):
            t0, t1 = i / nrow, (i + 1) / nrow
            uv = [fr_uv(x0, t0), fr_uv(x1, t0), fr_uv(x1, t1), fr_uv(x0, t1)] if curl else [uvb('hairflat', 0.5, 0.5)] * 4
            m.face([fr_pt(x0, t0), fr_pt(x1, t0), fr_pt(x1, t1), fr_pt(x0, t1)], uv, head_radial, 'hair')
    # lock
    lock = [(catmull(FRINGE, -0.123) if z is None else z, x) for z, x in LOCK]
    zl0 = catmull(FRINGE, -0.1285)
    for i in range(len(lock) - 1):
        (z0, x0), (z1, x1) = lock[i], lock[i + 1]
        zl = zl0 if i == 0 else z0
        m.face([fp(-0.1285, zl, RO), fp(x0, z0, RO), fp(x1, z1, RO), fp(-0.1285, z1, RO)],
               [uvb('hairflat', 0.5, 0.5)] * 4, head_radial, 'hair')
    return m


EYE_X = 0.0637          # eyes 0.17 W apart
EYE_Z = 0.812           # 0.28 H below the head top
MOUTH_Z = 0.7485        # 0.5 H
# front-view centre height of each eye and mouth quad; the open mouths hang from her bar mouth's top line
EYE_PIECES = [('eye_01_m', EYE_Z), ('eye_02_m', EYE_Z), ('eye_03_m', EYE_Z), ('eye_04_m', EYE_Z - 0.005), ('eye_05_m', EYE_Z),
              ('eye_sing_m', EYE_Z)]
MOUTH_PIECES = [('mouth_01_m', MOUTH_Z), ('mouth_02_m', MOUTH_Z - 0.005), ('mouth_03_m', MOUTH_Z - 0.005),
                ('mouth_04_m', MOUTH_Z - 0.0025), ('mouth_05_m', MOUTH_Z - 0.013), ('mouth_06_m', MOUTH_Z - 0.006),
                ('mouth_07_m', MOUTH_Z - 0.0125), ('mouth_08_m', MOUTH_Z - 0.002), ('mouth_09_m', MOUTH_Z - 0.002),
                ('mouth_10_m', MOUTH_Z - 0.004), ('mouth_11_m', MOUTH_Z - 0.0072)]
# overlays: front-view centre of the quad (for a mirrored pair, her right one) and whether it is a mirrored pair
OVERLAYS = {'cheak_01_m': (-0.0700, 0.7745, True), 'tear_01_m': (-0.0620, 0.7490, True),
            'angry_01_m': (0.1045, 0.8465, False), 'shock_01_m': (0.1040, 0.8484, False)}
SHOWN = ('head_m', 'nose_m', 'antena_m', 'body_m', 'hand_L_m', 'hand_R_m', 'leg_L_m', 'leg_R_m', 'face_01_m', 'eye_01_m', 'mouth_01_m')


def build_eye(name, zf):
    m = MB(name)
    wm, hm = FACE_CELLS[name][1]
    for sd in (-1.0, 1.0):
        cx = sd * EYE_X
        pts = [fp(cx - wm / 2, zf - hm / 2, RE), fp(cx + wm / 2, zf - hm / 2, RE), fp(cx + wm / 2, zf + hm / 2, RE), fp(cx - wm / 2, zf + hm / 2, RE)]
        us = [0, 1, 1, 0] if sd < 0 else [1, 0, 0, 1]       # her left eye mirrors the drawing
        m.face(pts, [uvf(name, u, v) for u, v in zip(us, [1, 1, 0, 0])], head_radial, 'eye')
    return m


def build_overlay(name):
    """A sheet lying on the head's curve at radius RV: full-width rows, evenly spaced in angle, as few as keep every
    chord's sag under SAG.  V follows the front-view height, so the drawing shows undistorted from the front."""
    m = MB(name)
    wm, hm = FACE_CELLS[name][1]
    cx, zc, pair = OVERLAYS[name]
    z0, z1 = zc - hm / 2, zc + hm / 2
    p0, p1 = math.asin((z0 - ZC) / RV), math.asin((z1 - ZC) / RV)
    n = int(math.ceil((p1 - p0) / (2.0 * math.acos(1.0 - SAG / RV))))
    ps = [p0 + (p1 - p0) * i / n for i in range(n + 1)]
    vs = [(z1 - (ZC + RV * math.sin(p))) / hm for p in ps]
    sag = RV * (1.0 - math.cos((p1 - p0) / n / 2.0))
    nrm = [0.0, 0.0]
    for sd in ((1.0, -1.0) if pair else (1.0,)):
        x0, x1 = sd * cx - wm / 2, sd * cx + wm / 2
        ua, ub = (0, 1) if sd > 0 else (1, 0)              # the mirrored one of a pair flips the drawing
        for i in range(n):
            a, b = ps[i], ps[i + 1]
            m.face([hp(x0, RV, a), hp(x1, RV, a), hp(x1, RV, b), hp(x0, RV, b)],
                   [uvf(name, ua, vs[i]), uvf(name, ub, vs[i]), uvf(name, ub, vs[i + 1]), uvf(name, ua, vs[i + 1])], head_radial, 'overlay')
            area = wm * 2.0 * RV * math.sin((b - a) / 2.0)
            nrm[0] += -math.cos((a + b) / 2.0) * area; nrm[1] += math.sin((a + b) / 2.0) * area
    ny = nrm[0] / math.hypot(nrm[0], nrm[1])
    for other, zf in EYE_PIECES + MOUTH_PIECES:          # no quad of it lies over an eye or mouth quad in a front view
        ow, oh = FACE_CELLS[other][1]
        for ox in ((-EYE_X, EYE_X) if other.startswith('eye') else (0.0,)):
            for sd in ((1.0, -1.0) if pair else (1.0,)):
                assert abs(sd * cx - ox) >= (wm + ow) / 2 or abs(zc - zf) >= (hm + oh) / 2, (name, other)
    print('OVERLAY %-10s rows %d, chord sag %.2f mm, nearest the plate %.2f mm, mean normal y %.3f'
          % (name, n, sag * 1000, (RV - sag - RP) * 1000, ny))
    assert sag < SAG and RV - sag > RP and ny < -0.5, name
    return m


def build_mouth(name, zf):
    """A flat 3 x 3 grid (9 vertices, 8 triangles) whose top and bottom edges lie on the eye/mouth radius."""
    m = MB(name)
    wm, hm = FACE_CELLS[name][1]
    y0 = fp(0.0, zf - hm / 2, RE)[1]; y1 = fp(0.0, zf + hm / 2, RE)[1]
    pt = lambda s, t: (-wm / 2 + wm * s, y0 + (y1 - y0) * t, zf - hm / 2 + hm * t)
    for i in range(2):
        for k in range(2):
            s0, s1, t0, t1 = k / 2, (k + 1) / 2, i / 2, (i + 1) / 2
            m.face([pt(s0, t0), pt(s1, t0), pt(s1, t1), pt(s0, t1)],
                   [uvf(name, s0, 1 - t0), uvf(name, s1, 1 - t0), uvf(name, s1, 1 - t1), uvf(name, s0, 1 - t1)], head_radial, 'mouth')
    return m, zf, hm


def build_nose():
    """A 14-sided closed cone standing out of the plate and pointing up and forward, so that from the front it
    shows the reference's narrow triangle, flat base down: 0.036 W wide and 0.057 W tall (1.6 times as tall as wide,
    as in the reference; the sheet's 0.13 H was read on the reference's taller head)."""
    m = MB('nose_m')
    n = 14; rad = 0.013; alpha = math.radians(65.0)
    z_low, z_tip = 0.762, 0.803                                # bottom and tip of the nose in a front view
    d = (0.0, -math.cos(alpha), math.sin(alpha))               # axis: up and forward
    e2 = (0.0, math.sin(alpha), math.cos(alpha))               # across the base, toward the back
    zb = z_low + rad * e2[2]
    bc = (0.0, fp(0.0, zb, RP)[1] - 0.002, zb)                  # base centre just in front of the plate
    length = (z_tip - zb) / d[2]
    apex = (0.0, bc[1] + length * d[1], bc[2] + length * d[2])
    cen = (0.0, bc[1] + 0.25 * length * d[1], bc[2] + 0.25 * length * d[2])
    rim = [(bc[0] + rad * math.cos(2 * math.pi * k / n), bc[1] + rad * math.sin(2 * math.pi * k / n) * e2[1],
            bc[2] + rad * math.sin(2 * math.pi * k / n) * e2[2]) for k in range(n + 1)]
    out = lambda c: vsub(c, cen)
    for k in range(n):
        m.face([rim[k], rim[k + 1], apex], [uvb('nose', 0.2, 0.62), uvb('nose', 0.8, 0.62), uvb('nose', 0.5, 0.03)], out, 'cone')
        m.face([rim[k + 1], rim[k], bc], [uvb('nose', 0.8, 0.95), uvb('nose', 0.2, 0.95), uvb('nose', 0.5, 0.95)], (-d[0], -d[1], -d[2]), 'base')
    return m


# ---------------------------------------------------------------- antenna
def build_antenna(spec):
    m = MB('antena_m')
    ax, ay, az = spec['JNT_antenna']['head']
    r_ball = 0.040
    z0, z2 = 0.884, az - r_ball + 0.006
    z_apex = az - r_ball + 0.021
    rr = lambda z: 0.030 * (z_apex - z) / (z_apex - z0)
    zs = [z0, (z0 + z2) / 2, z2]
    rings = [[(ax + rr(z) * math.sin(TH[j % N]), ay - rr(z) * math.cos(TH[j % N]), z) for j in range(N + 1)] for z in zs]
    uvs = [[uvb('antY', j / N, 1.0 - (z - z0) / (z2 - z0)) for j in range(N + 1)] for z in zs]
    tube(m, rings, uvs, lambda c: (c[0] - ax, c[1] - ay, 0.004), 'cone')
    sphere(m, (ax, ay, az), r_ball, lambda d: uvb('antR', 0.5 + 0.4 * d[0], 0.5 - 0.48 * d[2]), 'ball', nlat=8)
    return m, (z0, z2, az - r_ball)


# ---------------------------------------------------------------- body: shoulders, dress, necklace
def bp(th, r, z, ky=None):
    k = ky_at(z) if ky is None else ky
    return (r * math.sin(th), BY0 - k * r * math.cos(th), z)


def body_radial(c): return (c[0], c[1] - BY0, 0.0)


def dress_u(j):
    """U of the two sides of body column j in the 180-degree dress block (front and back share it)."""
    if j < 6: return 0.5 + j / 12.0, 0.5 + (j + 1) / 12.0          # front, her left half
    if j < 18: return (j - 6) / 12.0, (j - 5) / 12.0               # back
    return (j - 18) / 12.0, (j - 17) / 12.0                        # front, her right half


def dress_ua(d, end):
    """U of body angle d (degrees) in the dress block.  At the block's seams (90 and 270) a column's end takes 1.0."""
    if d < 90.0 or (end and d == 90.0): return 0.5 + d / 180.0
    if d < 270.0 or (end and d == 270.0): return (d - 90.0) / 180.0
    return (d - 270.0) / 180.0


def dress_v(z): return (ZBT - z) / (ZBT - Z_HEM)


def build_body():
    m = MB('body_m')
    th = TH + [TH[0] + 2 * math.pi]
    # skin: neck (inside the head), shoulder shelf, chest down to under the dress
    sv = lambda z: 0.30 + 0.60 * float(sstep(0.588, 0.612, z))
    out_skin = lambda c: (c[0], c[1] - BY0, 0.05)
    for i in range(len(SKIN_PROFILE) - 1):
        (z0, r0), (z1, r1) = SKIN_PROFILE[i], SKIN_PROFILE[i + 1]
        for j in range(N):
            m.face([bp(th[j], r0, z0), bp(th[j + 1], r0, z0), bp(th[j + 1], r1, z1), bp(th[j], r1, z1)],
                   [uvb('skin', 0.3, sv(z0)), uvb('skin', 0.7, sv(z0)), uvb('skin', 0.7, sv(z1)), uvb('skin', 0.3, sv(z1))], out_skin, 'skin')
    # (the neck's top ring at z 0.660 stays open: it is deep inside the head)
    # dress.  The rim, the trim and the first dress row carry four extra columns (+-7.5 and +-22.5 degrees), so the
    # sweetheart humps come out round; the 0.495 ring joins back to the 24 columns at 0.440.
    dc = sorted([15.0 * j for j in range(N)] + [7.5, 22.5, 337.5, 352.5]) + [360.0]
    tc = [math.radians(d) for d in dc]
    ztop = [neck_top(d) for d in dc]
    A = [bp(t, Rd(z) - 0.0065, z, KY) for t, z in zip(tc, ztop)]
    B = [bp(t, Rd(z), z - 0.0008, KY) for t, z in zip(tc, ztop)]
    Cc = [bp(t, Rd(z - 0.013), z - 0.013, KY) for t, z in zip(tc, ztop)]
    R1 = [bp(t, Rd(0.495), 0.495, KY) for t in tc]
    for k in range(len(dc) - 1):
        ua, ub = (k % 2) * 0.5 + 0.1, (k % 2) * 0.5 + 0.4
        m.face([A[k], A[k + 1], B[k + 1], B[k]], [uvb('trim', ua, 0.05), uvb('trim', ub, 0.05), uvb('trim', ub, 0.30), uvb('trim', ua, 0.30)], (0, 0, 1), 'rim')
        m.face([B[k], B[k + 1], Cc[k + 1], Cc[k]], [uvb('trim', ua, 0.30), uvb('trim', ub, 0.30), uvb('trim', ub, 0.95), uvb('trim', ua, 0.95)], body_radial, 'dress')
        u0, u1 = dress_ua(dc[k], False), dress_ua(dc[k + 1], True)
        m.face([Cc[k], Cc[k + 1], R1[k + 1], R1[k]],
               [uvb('dress', u0, dress_v(ztop[k] - 0.013)), uvb('dress', u1, dress_v(ztop[k + 1] - 0.013)),
                uvb('dress', u1, dress_v(0.495)), uvb('dress', u0, dress_v(0.495))], body_radial, 'dress')
    zr = [0.440, 0.380, 0.263, Z_HEM]
    rows = [[bp(th[j], Rd(z), z, KY) for j in range(N + 1)] for z in zr]
    v1, v2 = dress_v(0.495), dress_v(0.440)
    for j in range(N):                                  # 0.495 -> 0.440: a quad per column, 3 triangles where it was split
        k0, k1 = dc.index(15.0 * j), dc.index(15.0 * (j + 1))
        u0, u1 = dress_u(j); R2 = rows[0]
        if k1 - k0 == 1:
            m.face([R1[k0], R1[k1], R2[j + 1], R2[j]], [uvb('dress', u0, v1), uvb('dress', u1, v1), uvb('dress', u1, v2), uvb('dress', u0, v2)], body_radial, 'dress')
        else:
            km = k0 + 1; um = (u0 + u1) / 2
            m.face([R1[k0], R1[km], R2[j]], [uvb('dress', u0, v1), uvb('dress', um, v1), uvb('dress', u0, v2)], body_radial, 'dress')
            m.face([R1[km], R2[j + 1], R2[j]], [uvb('dress', um, v1), uvb('dress', u1, v2), uvb('dress', u0, v2)], body_radial, 'dress')
            m.face([R1[km], R1[k1], R2[j + 1]], [uvb('dress', um, v1), uvb('dress', u1, v1), uvb('dress', u1, v2)], body_radial, 'dress')
    for i in range(len(rows) - 1):
        for j in range(N):
            u0, u1 = dress_u(j)
            m.face([rows[i][j], rows[i][j + 1], rows[i + 1][j + 1], rows[i + 1][j]],
                   [uvb('dress', u0, dress_v(zr[i])), uvb('dress', u1, dress_v(zr[i])),
                    uvb('dress', u1, dress_v(zr[i + 1])), uvb('dress', u0, dress_v(zr[i + 1]))], body_radial, 'dress')
    hem = rows[-1]
    K = [bp(th[j], Rd(Z_HEM) - 0.007, Z_HEM - 0.0015, KY) for j in range(N + 1)]
    cen = (0.0, BY0, Z_HEM + 0.030)
    for j in range(N):
        m.face([hem[j], hem[j + 1], K[j + 1], K[j]], [uvb('under', 0.5, 0.5)] * 4, (0, 0, -1), 'under')
        m.face([K[j], K[j + 1], cen], [uvb('under', 0.5, 0.5)] * 3, (0, 0, -1), 'under')
    # necklace: small touching beads on a short front arc of a loop round the neck that droops onto the chest.
    # Past 40 degrees round the loop the beads would sink into the underside of the head, so the arc stops there.
    r_bead, b_gap, t_max = 0.0065, 0.013, 40.0
    tt = np.linspace(-180.0, 180.0, 1441)

    def loop_pt(t):
        d = 0.5 * (1.0 + math.cos(math.pi * t / 45.0)) if abs(t) < 45.0 else 0.0
        z = 0.612 - 0.024 * d
        return bp(math.radians(t), r_skin(z) + 0.0050, z + 0.002, ky_at(z))
    path = np.array([loop_pt(t) for t in tt])
    seg = np.linalg.norm(np.diff(path, axis=0), axis=1)
    cum = np.concatenate([[0.0], np.cumsum(seg)])
    s0 = float(np.interp(0.0, tt, cum))
    beads = []
    for k in range(40):
        for sg in (-1.0, 1.0):
            s = s0 + sg * (k + 0.5) * b_gap
            if abs(float(np.interp(s, cum, tt))) <= t_max:
                beads.append(tuple(float(np.interp(s, cum, path[:, i])) for i in range(3)))
    for i, c in enumerate(beads):
        sphere(m, c, r_bead, lambda d: uvb('bead', 0.5 + 0.3 * d[0], 0.5 - 0.45 * d[2]), 'bead%d' % i, nseg=8, nlat=4, w=w_body(c[2]))
    return m, len(beads)


# ---------------------------------------------------------------- arms and legs
def build_arm(sd, spec):
    m = MB('hand_L_m' if sd > 0 else 'hand_R_m')
    za = spec['JNT_arm_L']['head'][2]
    xe = spec['JNT_elbow_L']['head'][0]; xh = spec['JNT_hand_L']['head'][0]
    r_ball = 0.030; xc = xh + 0.032
    q_join = 135.0
    xj = xc + r_ball * math.cos(math.radians(q_join)); rj = r_ball * math.sin(math.radians(q_join))
    # the arm tapers from the shoulder to a slim wrist, then flares through one ring into the round hand (no crease)
    xw = xh - 0.018
    xr = [0.060, xe - 0.039, xe - 0.019, xe, xe + 0.019, xe + 0.039, xw, xj - 0.0085, xj]
    rs = [0.0185 + (0.0160 - 0.0185) * (x - xr[0]) / (xw - xr[0]) for x in xr[:7]] + [0.0180, rj]
    ang = [2 * math.pi * j / N for j in range(N + 1)]
    pt = lambda x, r, a: (sd * x, -r * math.sin(a), za + r * math.cos(a))        # a = 0 is the top of the arm
    sv = lambda a: 0.42 - 0.30 * math.cos(a)                                     # lighter on top, shade beneath
    rings = [[pt(x, r, a) for a in ang] for x, r in zip(xr, rs)]
    uvs = [[uvb('skin', 0.5, sv(a)) for a in ang] for x in xr]
    tube(m, rings, uvs, lambda c: (0.0, c[1], c[2] - za), 'arm')
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], (sd * xr[0], 0.0, za)], [uvb('skin', 0.5, 0.5)] * 3, (-sd, 0, 0), 'root')
    # round hand; its ring at q_join is the last ring of the tube
    cen = (sd * xc, 0.0, za)
    qs = [0.0, 27.0, 54.0, 81.0, 108.0, q_join]
    hpt = lambda q, a: (sd * (xc + r_ball * math.cos(math.radians(q))), -r_ball * math.sin(math.radians(q)) * math.sin(a),
                        za + r_ball * math.sin(math.radians(q)) * math.cos(a))
    hs = lambda q: math.sin(math.radians(q)) + (1.0 - math.sin(math.radians(q))) * float(sstep(108.0, q_join, q))
    huv = lambda q, a: uvb('skin', 0.5, 0.42 - 0.30 * hs(q) * math.cos(a))       # meets the arm's shading at the join
    for i in range(len(qs) - 1):
        for j in range(N):
            a, b = ang[j], ang[j + 1]
            m.face([hpt(qs[i], a), hpt(qs[i], b), hpt(qs[i + 1], b), hpt(qs[i + 1], a)],
                   [huv(qs[i], a), huv(qs[i], b), huv(qs[i + 1], b), huv(qs[i + 1], a)], lambda c: vsub(c, cen), 'arm')
    return m


def build_leg(sd, spec):
    m = MB('leg_L_m' if sd > 0 else 'leg_R_m')
    xl = spec['JNT_leg_L']['head'][0]; zk = spec['JNT_knee_L']['head'][2]
    zr = [0.305, zk + 0.039, zk + 0.0195, zk, zk - 0.0195, zk - 0.039, 0.078, 0.046]
    rr = lambda z: 0.019 if z <= zr[-1] + 1e-9 else 0.0150 + (0.0175 - 0.0150) * (z - zr[-1]) / (zr[0] - zr[-1])   # widens inside the shoe
    ang = [2 * math.pi * j / N for j in range(N + 1)]
    pt = lambda z, a: (sd * (xl + rr(z) * math.sin(a)), -rr(z) * math.cos(a), z)
    sv = lambda z: 0.40 + 0.50 * float(sstep(0.13, 0.19, z))                     # shaded under the skirt
    rings = [[pt(z, a) for a in ang] for z in zr]
    uvs = [[uvb('skin', 0.5, sv(z)) for a in ang] for z in zr]
    tube(m, rings, uvs, lambda c: (c[0] - sd * xl, c[1], 0.0), 'leg')
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], (sd * xl, 0.0, zr[0])], [uvb('skin', 0.5, 0.9)] * 3, (0, 0, 1), 'top')
    # shoe: a rounded magenta ellipsoid, a little longer toward the toe
    sphere(m, (sd * xl, -0.012, 0.0275), (0.031, 0.044, 0.0275), lambda d: uvb('shoe', 0.5 + 0.35 * d[0], 0.5 - 0.46 * d[2]),
           'shoe', nlat=6, w={'JNT_ankle_' + ('L' if sd > 0 else 'R'): 1.0})
    return m


# ---------------------------------------------------------------- weights
def w_body(z):
    a = float(sstep(0.330, 0.430, z))       # waist -> spine_01, round the spine_01 joint (0.380)
    b = float(sstep(0.442, 0.532, z))       # spine_01 -> spine_02, round the spine_02 joint (0.487)
    c = float(sstep(0.600, 0.640, z))       # spine_02 -> neck; the shoulders stay on spine_02 with the arms
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


# ---------------------------------------------------------------- Blender objects
def make_object(m, mat, wfun, arm):
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
    ob.matrix_parent_inverse = RIG_TURN.inverted()   # the mesh stays where it was built, under the turned rig
    mod = ob.modifiers.new('Armature', 'ARMATURE'); mod.object = arm
    return ob


# OUAK's cousin joints rest with identity local rotations, world-aligned (OUJI01_D).  The FBX exporter writes the rig
# object as a Null under its Z-up to Y-up conversion, Rx(-90), and each bone as its rest matrix in rig space.  So the
# rig object is turned +90 degrees about X, which cancels that conversion, and every bone lies along the rig's +Y with
# roll 0, which makes its rest matrix identity.  In the viewport every bone points straight up.
RIG_TURN = Matrix.Rotation(math.pi / 2, 4, 'X')        # rig space -> world


def build_rig(spec):
    arm = bpy.data.armatures.new('Burda_rig')
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
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == 'FILE_BROWSER' and space.params is not None:
                    space.params.directory = b'/' * 512
                    space.params.directory = b'//'


# ---------------------------------------------------------------- main
def main():
    for p in glob.glob(os.path.join(TEX_DIR, 'burda_new_*.png')) + glob.glob(os.path.join(REN_DIR, 'Burda_new_*.png')) + \
            [BLEND, BLEND + '1', FBX]:
        if os.path.exists(p):
            os.remove(p)
    os.makedirs(TEX_DIR, exist_ok=True); os.makedirs(REN_DIR, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0

    check_layout(BODY_BLOCKS, 256, ('face_gloom', 'face_flush'))
    check_layout({n: c[0] for n, c in FACE_CELLS.items()}, FACE_PX, FACE_CELLS_NEW)
    body_png = paint_body()
    face_png = paint_face()
    mat_body = C.img_material('Burda_new_body', body_png)
    mat_face = C.img_material('Burda_new_face', face_png, alpha=True)
    for mt in (mat_body, mat_face):
        mt.use_backface_culling = True              # as a game draws it: a flipped face would vanish
    chk = bpy.data.images.load(body_png, check_existing=True)
    px = np.array(chk.pixels[:]).reshape(256, 256, 4)[::-1]
    x, y, w, h = BODY_BLOCKS['hairflat']
    got = tuple(int(round(v * 255)) for v in px[y + h // 2, x + w // 2, :3])
    print('TEXCHECK hair cell', got, 'sheet', tuple(int(round(v * 255)) for v in hx(HAIR)))
    assert got == tuple(int(round(v * 255)) for v in hx(HAIR))

    with open(RIG_SPEC) as fh:
        spec = json.load(fh)['new_style']['joints']
    rig = build_rig(spec)

    head = {'JNT_head': 1.0}
    w_head = lambda p: head
    obs = []
    obs.append(make_object(build_head(), mat_body, w_head, rig))
    ant, (az0, az1, az_ball) = build_antenna(spec)

    def w_ant(p):
        t = 1.0 if p[2] >= az_ball - 1e-6 else float(np.clip((p[2] - az0) / (az1 - az0), 0.0, 1.0))
        return {'JNT_head': 1 - t, 'JNT_antenna': t}
    obs.append(make_object(ant, mat_body, w_ant, rig))
    for name, block in (('face_01_m', 'face'), ('face_02_m', 'face_gloom'), ('face_03_m', 'face_flush')):
        obs.append(make_object(build_plate(name, block), mat_body, w_head, rig))
    obs.append(make_object(build_nose(), mat_body, w_head, rig))
    for name, zf in EYE_PIECES:
        obs.append(make_object(build_eye(name, zf), mat_face, w_head, rig))
    for name, zf in MOUTH_PIECES:
        mm, zc, hm = build_mouth(name, zf)

        def w_mouth(p, zc=zc, hm=hm):                # upper lip half on the head, lower lip on the mouth joint
            t = float(np.clip((zc + hm / 2 - p[2]) / hm, 0.0, 1.0))
            return {'JNT_head': 0.5 - 0.4 * t, 'JNT_mouth': 0.5 + 0.4 * t}
        obs.append(make_object(mm, mat_face, w_mouth, rig))
    for name in OVERLAYS:
        obs.append(make_object(build_overlay(name), mat_face, w_head, rig))
    body, nbeads = build_body()
    obs.append(make_object(body, mat_body, lambda p: w_body(p[2]), rig))
    for sd in (1.0, -1.0):
        obs.append(make_object(build_arm(sd, spec), mat_body, w_arm(sd, spec), rig))
    for sd in (1.0, -1.0):
        obs.append(make_object(build_leg(sd, spec), mat_body, w_leg(sd, spec), rig))
    assert len({ob.name for ob in obs}) == len(obs) and set(SHOWN) <= {ob.name for ob in obs}
    for ob in obs:
        if ob.name not in SHOWN:                    # every other face piece waits hidden
            ob.hide_render = True
            ob.hide_viewport = True                 # the FBX writes Visibility from this flag, not from hide_render

    total = 0
    for ob in obs:
        t = sum(len(p.vertices) - 2 for p in ob.data.polygons); total += t
        print('PART %-12s tris %5d verts %5d' % (ob.name, t, len(ob.data.vertices)))
    print('PART total tris', total, 'beads', nbeads)

    C.render_views(os.path.join(REN_DIR, 'Burda_new'), views=('front', 'side', 'back'))

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


main()
