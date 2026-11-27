"""Circuit, new style (Once Upon a Katamari look).  Builds everything from an empty scene:
textures, materials, meshes, the rig from the shared rig spec, weights, renders, the .blend and the FBX.

    blender -b --factory-startup --python-exit-code 1 --python build_circuit_new.py

Units are Blender metres, Z up, they face -Y, +X is their left.  Every path is resolved from this file.
All geometry and every pixel is made here; nothing is read from any game file.
"""
import bpy, sys, os, json, math, glob
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
import bl_common as C

TEX_DIR = os.path.join(HERE, 'textures')
REN_DIR = os.path.join(HERE, 'renders')
RIG_SPEC = os.path.join(os.path.dirname(os.path.dirname(HERE)), 'Burda', 'burda_rig_spec.json')
BLEND = os.path.join(HERE, 'Circuit_new.blend')
FBX = os.path.join(HERE, 'Circuit_new.fbx')
BODY_TEX = 'circuit_new_body'
FACE_TEX = 'circuit_new_face'

# ---------------------------------------------------------------- design sheet colours
PURPLE, SHADE, LIGHT = '#473998', '#33296E', '#5A4CB4'
PINK, PALE = '#FF83C5', '#FED6E8'
RED = '#FF1107'
FACE, FACE_SH = '#FAF4E5', '#E6DCC6'
RIM = '#FBFA3B'
TRACE = '#64F284'
SLATE = '#524B77'
EYES, NOSE = '#000000', '#FFA43D'
GLOOM, FLUSH = ('#5E77A0', 0.80, 0.070), ('#E2564A', 0.62, 0.050)      # plate tints: colour, strength, fade (metres)
EYE_W, ANGER, BLUSH, SHOCK, TEAR = '#FFFFFF', '#7A82EE', '#FF8AA8', '#1C2550', '#8FD0D6'

# ---------------------------------------------------------------- sizes (metres), from the design sheet
N = 24                       # segments round every round section
NH = 16                      # segments round the horns and their balls
ZB = 0.843                   # brow height: the hood's widest ring
HOOD_W, HOOD_D = 0.184, 0.152            # hood half width and half depth at the brow
DOME_H, DOME_P = 0.220, 2.8              # dome: top at 1.063, a low rounded arch
SEC_P = 2.4                  # the hood's section is a superellipse with this exponent
# window (the yellow rim's outer outline, front view)
XC, ZT, ZTIP, DIP, SIDE_N = 0.117, 0.840, 0.660, 0.012, 1.8
RIM_IN = 0.0120              # rim thickness (sheet: 0.011 to 0.017; the mitred corners come out thicker)
PLATE_OFF = 0.0030           # the plate stands this far in front of the hood surface
RIM_OFF = 0.0080             # the rim's top
EYE_OFF = 0.0036             # eyes and mouths, in front of the plate
OVER_OFF = 0.0014            # overlays, in front of the plate
EYE_X, EYE_Z = 0.044, 0.774
# horns
HORN_TIP = (0.334, 1.209)    # centre of the horn's end under the ball (x, z): the ball (sheet: x 0.348) sits a little outward of it,
                             # as in the reference, where the outer edge runs up to the ball's centre line
HORN_RT = 0.015              # its radius there
HORN_IN = (0.107, 1.044)     # the inner edge meets the dome here; the outer edge runs straight from the brow corner (HOOD_W, ZB)
BALL = (0.348, 1.234, 0.037)
LEAN = math.tan(math.radians(10.0)); LEAN_Z = ZB
PINK_1, PINK_2 = 0.071, 0.143            # borders along the horn, from its tip: pale/strong (z 1.15), strong/purple (z 1.09)
# robe
ZR_TOP, ZR_BOT = 0.720, 0.155            # heights at the top and bottom rows of the robe blocks in the atlas
Z_PINK = 0.160
TH = [math.radians(15.0 * j) for j in range(N)]           # ring angles; 0 = front, 90 = their left
# the green trace, front view (x, z): a zigzag, read by eye from the reference between the sheet's limits
TRACE_PTS = [(-0.124, 0.469), (-0.086, 0.366), (-0.062, 0.557), (-0.026, 0.288), (-0.005, 0.474), (0.021, 0.396),
             (0.041, 0.596), (0.060, 0.386), (0.095, 0.474), (0.103, 0.425), (0.124, 0.464)]
TRACE_SX = 0.93              # pulled in a little so its ends stay on the robe's front
TRACE_HW = 0.0055


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


def pchip(pts):
    """Monotone cubic through (x, y) points; clamps outside."""
    xs = np.array([p[0] for p in pts], float); ys = np.array([p[1] for p in pts], float)
    h = np.diff(xs); d = np.diff(ys) / h
    m = np.zeros(len(xs)); m[0], m[-1] = d[0], d[-1]
    for i in range(1, len(xs) - 1):
        if d[i - 1] * d[i] > 0:
            w1, w2 = 2 * h[i] + h[i - 1], h[i] + 2 * h[i - 1]
            m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i])

    def f(x):
        x = np.clip(np.asarray(x, float), xs[0], xs[-1])
        i = np.clip(np.searchsorted(xs, x) - 1, 0, len(xs) - 2)
        t = (x - xs[i]) / h[i]
        return ((2 * t ** 3 - 3 * t ** 2 + 1) * ys[i] + (t ** 3 - 2 * t ** 2 + t) * h[i] * m[i]
                + (-2 * t ** 3 + 3 * t ** 2) * ys[i + 1] + (t ** 3 - t ** 2) * h[i] * m[i + 1])
    return f


# ---------------------------------------------------------------- the hood as a function
# Below the brow: half width (sheet), half depth to the front and to the back (by eye).  The stem under the chin
# reaches a little inside the robe's top, hidden: the chin sits right on the robe.
_HW = pchip([(0.610, 0.040), (0.622, 0.066), (0.640, 0.070), (0.660, 0.080), (0.672, 0.086), (0.694, 0.099), (0.744, 0.1295), (0.790, 0.160), (ZB, HOOD_W)])
_HF = pchip([(0.610, 0.036), (0.622, 0.062), (0.640, 0.083), (0.660, 0.094), (0.694, 0.115), (0.744, 0.137), (0.790, 0.149), (ZB, HOOD_D)])
_HB = pchip([(0.610, 0.032), (0.622, 0.055), (0.640, 0.073), (0.660, 0.084), (0.694, 0.104), (0.744, 0.131), (0.790, 0.147), (ZB, HOOD_D)])


def _dome(z):
    t = np.clip((np.asarray(z, float) - ZB) / DOME_H, 0.0, 1.0)
    return (1.0 - t ** DOME_P) ** (1.0 / DOME_P)


def hood_w(z): return _HW(z) * _dome(z)
def hood_f(z): return _HF(z) * _dome(z)
def hood_b(z): return _HB(z) * _dome(z)


# ---- above the brow the hood and the horns share their front and back faces ----
def horn_lines(z):
    """Front view, for the horn on their left: x of its outer edge (one straight line from the brow corner to under the
    ball) and of its inner edge (one straight line from where it meets the dome to under the ball, carried on below)."""
    zt = HORN_TIP[1]
    xo = HOOD_W + (HORN_TIP[0] + HORN_RT - HOOD_W) * (z - ZB) / (zt - ZB)
    xi = HORN_IN[0] + (HORN_TIP[0] - HORN_RT - HORN_IN[0]) * (z - HORN_IN[1]) / (zt - HORN_IN[1])
    return xo, xi


def face_depth(z):
    """Half depth of the shared faces: the hood's own at the brow, narrowing evenly to round under the ball."""
    return HOOD_D + (HORN_RT - HOOD_D) * (z - ZB) / (HORN_TIP[1] - ZB)


def shared_face(x, z):
    """Distance of the shared front (and back) face from the leaning middle plane at front-view (x, z), z above the
    brow.  It is level across the middle and rounds off to the horn's outer edge.  At the brow it is the hood's own
    section there, so the hood runs on into it without a step."""
    xo, xi = horn_lines(z)
    xr = max((xo + xi) / 2.0, 0.0)                 # the rounding starts on the horn's middle line
    r = min(max((abs(x) - xr) / (xo - xr), 0.0), 1.0)
    return face_depth(z) * (1.0 - r ** SEC_P) ** (1.0 / SEC_P)


def smin(a, b, k=8.0):
    if a <= 1e-9 or b <= 1e-9:
        return 0.0
    return (a ** -k + b ** -k) ** (-1.0 / k)


HOOD_UNDER = 0.0025          # above the brow the hood lies this far under the shared face where the horn covers it, and
HOOD_OVER = 0.0015           # this far over it across the middle, where the horn's inner end hides: the two surfaces cross
                             # at a grazing angle with the same slope, so no step or crease shows, and they never fight


def hood_top(x, z):
    """Half depth of the hood above the brow: the shared face, closed toward the arch's ends and rounded at its roof.
    With the horns hidden this is a closed top: a shallow arch from the front, a tapering wedge from the side."""
    t = min(max((z - ZB) / DOME_H, 0.0), 1.0)
    w = float(hood_w(z))
    if w <= 1e-9:
        return 0.0
    r = min(abs(x) / w, 1.0)
    g = float(sstep(0.80, 0.92, t))                # only near the roof do the arch's ends close more gently, under the horns'
    pc = SEC_P - 1.0 * g                            # inner sides; lower down the closing stays clear of the face you see
    cap = (1.6 - 0.6 * g) * face_depth(z) * (1.0 - r ** pc) ** (1.0 / pc)
    k = float(sstep(horn_x(-0.40, z), horn_x(0.10, z), abs(x)))   # the crossing lies where the horn is at its full depth
    off = (HOOD_OVER - (HOOD_OVER + HOOD_UNDER) * k) * float(sstep(0.0, 0.10, t))
    return (1.0 - t ** 10) ** 0.1 * smin(max(shared_face(x, z) + off, 0.0), cap)


def lean_y(z): return LEAN * max(z - LEAN_Z, 0.0)   # above the brow everything leans back by 10 degrees (a shear: the front view keeps its lines)


def hood_y(x, z):
    """y of the hood's front surface at front-view (x, z)."""
    r = np.clip(np.abs(x) / hood_w(z), 0.0, 1.0)
    return -hood_f(z) * (1.0 - r ** SEC_P) ** (1.0 / SEC_P)


def plate_y(x, z): return hood_y(x, z) - PLATE_OFF


def plate_grad(x, z, e=1e-4):
    return (float(plate_y(x + e, z) - plate_y(x - e, z)) / (2 * e), float(plate_y(x, z + e) - plate_y(x, z - e)) / (2 * e))


# ---------------------------------------------------------------- the window outline
def window_outline():
    """The rim's outer outline as a closed polygon of (x, z): 15 points down their left half (top middle, corner at
    index 5, tip at 14), then the mirror."""
    half = [(XC * k / 5.0, ZT - DIP * (1.0 - (k / 5.0) ** 2)) for k in range(6)]
    # the first side point sits well below the corner: nearer, the mitred corner of the inner rings would squeeze the
    # first side quad to a twisted sliver
    half += [(XC * (1.0 - t ** SIDE_N), ZT - t * (ZT - ZTIP)) for t in (0.19, 0.29, 0.39, 0.49, 0.59, 0.69, 0.79, 0.89, 1.0)]
    return half + [(-x, z) for x, z in half[-2:0:-1]]


def offset_poly(P, d):
    """The polygon moved inward by d, mitred at its corners."""
    n = len(P)
    area = sum(P[i][0] * P[(i + 1) % n][1] - P[(i + 1) % n][0] * P[i][1] for i in range(n))
    sg = 1.0 if area > 0 else -1.0
    out = []
    for i in range(n):
        a, b, c = P[i - 1], P[i], P[(i + 1) % n]
        ns = []
        for p, q in ((a, b), (b, c)):
            ex, ez = q[0] - p[0], q[1] - p[1]; L = math.hypot(ex, ez)
            ns.append((-ez / L * sg, ex / L * sg))
        mx, mz = ns[0][0] + ns[1][0], ns[0][1] + ns[1][1]; L = math.hypot(mx, mz); mx, mz = mx / L, mz / L
        k = d / max(mx * ns[0][0] + mz * ns[0][1], 0.3)
        out.append((b[0] + mx * k, b[1] + mz * k))
    return out


OUTLINE = window_outline()
PLATE_POLY = offset_poly(OUTLINE, 0.0065)       # the plate reaches under the rim
INNER_POLY = offset_poly(OUTLINE, RIM_IN + 0.0025)   # where the rim's inner wall meets the plate: the cream you see


def poly_half(poly):
    """(top polyline x -> z, side polyline z -> x) of an outline's +x half."""
    top = poly[0:6]; side = poly[5:15]
    return ([p[0] for p in top], [p[1] for p in top]), ([p[1] for p in side][::-1], [p[0] for p in side][::-1])


def in_poly(poly, x, z, margin=0.0):
    (tx, tz), (sz, sx) = poly_half(poly)
    ax = np.abs(x)
    return (z >= sz[0] + margin) & (z <= np.interp(ax, tx, tz) - margin) & (ax <= np.interp(z, sz, sx) - margin) & (ax <= tx[-1] - margin)


# ---------------------------------------------------------------- the robe's profile
# (z, half width) down the outside, from the collar lip to the top of the pink hem: the sheet's widths
# The robe's top is closed and rises inside the hood's chin, so there is no neck or collar to see: from the pinch under
# the hood (0.198 wide at z 0.694, narrowest about z 0.67 in the reference) it slopes straight out and down through 0.229
# at z 0.644 to the shoulders and sleeves.
ROBE = [(0.672, 0.084), (0.658, 0.100), (0.644, 0.1145), (0.626, 0.1225), (0.604, 0.1230), (0.575, 0.1195), (0.540, 0.1170), (0.495, 0.1165),
        (0.440, 0.124), (0.380, 0.136), (0.335, 0.145), (0.297, 0.1525), (0.250, 0.160), (0.205, 0.166), (Z_PINK, 0.1715)]
NECK = [(0.712, 0.045), (0.694, 0.068)]                                       # the robe's rounded top, hidden inside the hood
LIP = [(0.130, 0.1775), (0.105, 0.1790), (0.090, 0.1755), (0.082, 0.1680)]    # the turned-out hem lip, pink
UNDER = [(0.090, 0.150)]                                                      # the pink underside, closing at (0, 0.130)
Z_UNDER = 0.130


def robe_r(z):
    return np.interp(z, [p[0] for p in ROBE][::-1], [p[1] for p in ROBE][::-1])


def robe_ky(z):
    """Depth / width of the robe's section (sheet)."""
    return 0.75

# ---------------------------------------------------------------- atlas layout (pixels, top-left origin)
BODY_BLOCKS = {
    'robe_front': (3, 3, 250, 116),
    'hood':       (3, 128, 90, 80),
    'horn':       (101, 128, 60, 84),
    'robe_back':  (169, 128, 84, 80),
    'face':       (3, 220, 30, 30),
    'face_gloom': (39, 220, 30, 30),
    'face_flush': (75, 220, 30, 30),
    'rim':        (111, 220, 10, 30),
    'ball':       (127, 220, 22, 22),
    'pink':       (155, 220, 12, 30),
    'slate':      (173, 220, 12, 30),
    'sleeve':     (191, 220, 12, 30),
    'nose':       (209, 220, 12, 30),
}
FACE_PX = 512
FACE_PPM = 2800.0            # face atlas pixels per metre
# face pieces: the size of the quad each cell maps to (metres)
FACE_SIZES = {
    'eye_01_m': (0.030, 0.022), 'eye_02_m': (0.030, 0.022), 'eye_03_m': (0.030, 0.022), 'eye_04_m': (0.030, 0.022),
    'eye_05_m': (0.030, 0.022), 'eye_hum_m': (0.030, 0.022),
    'mouth_01_m': (0.040, 0.041), 'mouth_02_m': (0.034, 0.034), 'mouth_03_m': (0.036, 0.034), 'mouth_04_m': (0.054, 0.030),
    'mouth_05_m': (0.040, 0.044), 'mouth_06_m': (0.024, 0.024), 'mouth_07_m': (0.036, 0.042), 'mouth_08_m': (0.056, 0.020),
    'mouth_09_m': (0.046, 0.024), 'mouth_10_m': (0.040, 0.026), 'mouth_11_m': (0.050, 0.040),
    'angry_01_m': (0.028, 0.028), 'cheak_01_m': (0.030, 0.020), 'shock_01_m': (0.034, 0.029), 'tear_01_m': (0.012, 0.036),
}


def pack_cells(sizes, size, ppm, gap=8):
    """Shelf-pack the cells, tallest first.  name -> ((x, y, w, h) in pixels, (w, h) in metres)."""
    out = {}; x = y = gap // 2; shelf = 0
    for n in sorted(sizes, key=lambda k: (-sizes[k][1], k)):
        w, h = int(round(sizes[n][0] * ppm)), int(round(sizes[n][1] * ppm))
        if x + w + gap // 2 > size:
            x = gap // 2; y += shelf + gap; shelf = 0
        assert y + h + gap // 2 <= size, 'face atlas is full at ' + n
        out[n] = ((x, y, w, h), (w / ppm, h / ppm))
        x += w + gap; shelf = max(shelf, h)
    return out


FACE_CELLS = pack_cells(FACE_SIZES, FACE_PX, FACE_PPM)


def check_layout(rects, size, gut=3):
    """Every rect lies inside the atlas and no gutter covers another rect."""
    grow = lambda r: (r[0] - gut, r[1] - gut, r[0] + r[2] + gut, r[1] + r[3] + gut)
    hit = lambda a, b: a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]
    for n, r in rects.items():
        assert r[0] >= 0 and r[1] >= 0 and r[0] + r[2] <= size and r[1] + r[3] <= size, n
        for m, s in rects.items():
            if m != n:
                assert not hit(grow(r), (s[0], s[1], s[0] + s[2], s[1] + s[3])), (n, m)


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


def robe_base(cs, z):
    """The robe's purple: darker toward the sides, into the collar fold and toward the hem, a soft light on the chest."""
    c = mix3(hx(SHADE), hx(PURPLE), sstep(0.0, 0.65, cs))
    c = mix3(c, hx(LIGHT), 0.22 * np.exp(-((z - 0.565) / 0.05) ** 2) * sstep(0.3, 1.0, cs))
    c = mix3(c, hx(SHADE), 0.30 * sstep(0.645, 0.700, z))       # a soft shade under the chin
    return mix3(c, hx(SHADE), 0.45 * sstep(0.36, Z_PINK, z))


def f_robe_front(U, V):
    th = (U - 0.5) * math.pi
    z = ZR_TOP - V * (ZR_TOP - ZR_BOT)
    c = robe_base(np.cos(th), z)
    x = robe_r(z) * np.sin(th)                     # front-view x, so the trace shows undistorted from the front
    d, w = dist_polyline(x, z, [(px * TRACE_SX, pz, TRACE_HW) for px, pz in TRACE_PTS])
    return mix3(c, hx(TRACE), sstep(0.0008, -0.0008, d - w))


def f_robe_back(U, V):
    th = (U - 0.5) * math.pi
    return robe_base(np.cos(th), ZR_TOP - V * (ZR_TOP - ZR_BOT))


HOOD_V0, HOOD_V1 = 1.070, 0.590                    # heights at the top and bottom rows of the hood block


def f_hood(U, V):
    th = 2.0 * math.pi * U
    z = HOOD_V0 - V * (HOOD_V0 - HOOD_V1)
    c = mix3(hx(PURPLE), hx(SHADE), 0.40 * sstep(0.720, 0.630, z))                 # a little darker under the chin
    c = mix3(c, hx(SHADE), 0.22 * (1.0 - sstep(-1.0, 0.2, np.cos(th))) * sstep(0.840, 0.760, z))   # and round the back, below the horns
    dth = (th + math.pi) % (2.0 * math.pi) - math.pi                               # a soft sheen on the brow, clear of the horn roots
    sheen = np.exp(-((z - 0.930) / 0.045) ** 2) * np.exp(-((dth + 0.10) / 0.22) ** 2)      # kept to the middle of the brow
    return mix3(c, hx(LIGHT), 0.45 * sheen)


def tri_wave(t):
    t = t % 1.0
    return 1.0 - 2.0 * np.abs(2.0 * t - 1.0)       # -1..1


HORN_LEN = [0.0]                                    # the horn's length from tip to base, set once its shape is known


def f_horn(U, V):
    d = V * HORN_LEN[0]                             # metres from the tip
    b1 = PINK_1 + 0.010 * tri_wave(6.0 * U)         # pale / strong pink, a small zigzag
    b2 = PINK_2 + 0.020 * tri_wave(6.0 * U + 0.5)   # strong pink / purple: three teeth on each side you see
    pur = mix3(hx(PURPLE), hx(LIGHT), 0.55 * np.clip(np.cos(2.0 * math.pi * (U - 0.70)), 0.0, 1.0) ** 6 * sstep(0.62, 0.42, V) * sstep(0.18, 0.34, V))
    c = np.where((d < b2)[..., None], hx(PINK), pur)
    return np.where((d < b1)[..., None], hx(PALE), c)


def f_face(U, V):
    c = mix3(hx(FACE), hx(FACE_SH), 0.45 * (1.0 - sstep(0.0, 0.16, V)))             # the rim's soft shadow at the top
    return mix3(c, hx(FACE_SH), 0.50 * sstep(0.70, 1.0, V))                         # and a shade toward the point


FACE_U = 0.115                                      # the plate's block spans x +-FACE_U and z ZTIP..ZT + 0.002


def face_uv(block, x, z): return uvb(block, (x + FACE_U) / (2 * FACE_U), (ZT + 0.002 - z) / (ZT + 0.002 - ZTIP))


def f_face_tint(tint):
    col, k, fade = tint

    def f(U, V):
        x = (2.0 * U - 1.0) * FACE_U
        z = ZT + 0.002 - V * (ZT + 0.002 - ZTIP)
        top = ZT - DIP * (1.0 - np.clip(np.abs(x) / XC, 0, 1) ** 2) - 0.014         # the cream's top edge
        t = np.clip((top - z) / fade, 0.0, 1.0)
        return mix3(f_face(U, V), hx(col), k * (1.0 - t) ** 1.4)
    return f


def f_rim(U, V): return mix3(mix3(hx('#FFFE8A'), hx(RIM), sstep(0.0, 0.4, V)), hx('#D9C81E'), sstep(0.6, 1.0, V))
def f_ball(U, V): return mix3(mix3(hx('#FF6A5C'), hx(RED), sstep(0.0, 0.4, V)), hx('#C40A04'), sstep(0.6, 1.0, V))
def f_pink(U, V): return mix3(mix3(hx('#FFA9D7'), hx(PINK), sstep(0.0, 0.35, V)), hx('#D9589E'), sstep(0.55, 1.0, V))
def f_slate(U, V): return mix3(mix3(hx('#6A6392'), hx(SLATE), sstep(0.0, 0.4, V)), hx('#39335A'), sstep(0.6, 1.0, V))
def f_sleeve(U, V): return mix3(mix3(hx(LIGHT), hx(PURPLE), sstep(0.0, 0.40, V)), hx(SHADE), sstep(0.50, 1.0, V))
def f_nose(U, V): return mix3(mix3(hx('#FFC478'), hx(NOSE), sstep(0.0, 0.45, V)), hx('#D97F1E'), sstep(0.65, 1.0, V))


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
    cv[:] = hx(PURPLE) * 0.8
    P = lambda n, f, **k: paint(cv, BODY_BLOCKS[n], f, **k)
    P('robe_front', f_robe_front); P('robe_back', f_robe_back)
    P('hood', f_hood, wrap_u=True); P('horn', f_horn, wrap_u=True)
    P('face', f_face); P('face_gloom', f_face_tint(GLOOM)); P('face_flush', f_face_tint(FLUSH))
    P('rim', f_rim); P('ball', f_ball); P('pink', f_pink); P('slate', f_slate); P('sleeve', f_sleeve); P('nose', f_nose)
    rgb = downsample(cv)
    return save_png(BODY_TEX, np.dstack([rgb, np.ones(rgb.shape[:2])]), alpha=False)


def m_ellipse(X, Y, a, b, cx=0.0, cy=0.0): return ((X - cx) / a) ** 2 + ((Y - cy) / b) ** 2 <= 1.0


def m_rbox(X, Y, hw, hh, r):
    qx = np.abs(X) - (hw - r); qy = np.abs(Y) - (hh - r)
    return np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - r <= 0


def line(X, Y, pts, hw):
    """A stroke of half width hw along the points, with round ends."""
    d, w = dist_polyline(X, Y, [(x, y, hw) for x, y in pts])
    return d <= w


def lens(X, Y, a, b):
    """An almond with pointed ends: half length a, half height b."""
    return (np.abs(X) <= a) & (np.abs(Y) <= b * (1.0 - (X / a) ** 2))


def trapezoid(X, Y, top, bot, hh):
    """Half widths top and bot at the top and bottom edges, half height hh."""
    t = np.clip((hh - Y) / (2 * hh), 0.0, 1.0)
    return (np.abs(Y) <= hh) & (np.abs(X) <= top + (bot - top) * t)


def dome(X, Y, a, yb, h, r):
    """Open mouth with a round top: half width a, bottom at yb, height h, bottom corners rounded by r."""
    t = np.clip(np.abs(X) / a, 0.0, 1.0)
    top = yb + h * (1.0 - t ** 2.4) ** (1.0 / 2.4)
    return (np.abs(X) <= a) & (Y >= yb) & (Y <= top) & m_rbox(X, Y - (yb + h / 2), a, h / 2, r)


LW = 0.0042                  # half width of the line mouths: about as thick as the trace


def face_shapes():
    """mesh name -> (alpha function over metres from the cell centre, colour or colour function).  Eyes, the blush and
    the tears are drawn once; the mesh mirrors the UVs for the other one.  The two one-sided marks sit on their left."""
    def eye_01(X, Y): return lens(X, Y, 0.0112, 0.0070)                          # the black almonds

    def eye_02(X, Y):                                                            # half-closed: a thin almond under a flat lid line
        return (lens(X, Y, 0.0112, 0.0070) & (Y <= 0.0008)) | line(X, Y, [(-0.0118, 0.0010), (0.0118, 0.0010)], 0.0013)

    def eye_03(X, Y): return line(X, Y, [(-0.0095, 0.0), (0.0095, 0.0)], 0.0019)  # closed: short straight lines
    def eye_04(X, Y): return m_ellipse(X, Y, 0.0078, 0.0078)                     # round dots
    def eye_05(X, Y): return lens(X, Y, 0.0122, 0.0080)                          # white almonds with a thin black outline

    def eye_05_col(X, Y):
        return np.where(lens(X, Y, 0.0122 - 0.0026, 0.0080 - 0.0015)[..., None], hx(EYE_W), hx(EYES))

    def eye_hum(X, Y):                                                           # closed happy arcs, curving up
        return line(X, Y, [(0.0100 * t, -0.0032 + 0.0062 * (1 - t * t)) for t in np.linspace(-1, 1, 17)], 0.0019)

    def mouth_01(X, Y): return trapezoid(X, Y, 0.0090, 0.0160, 0.0175)           # the red trapezoid, wide edge down
    def mouth_02(X, Y): return m_rbox(X, Y, 0.0135, 0.0135, 0.0055)              # open, a rounded square
    def mouth_03(X, Y): return dome(X, Y, 0.0145, -0.0135, 0.0270, 0.0030)       # open, arched

    def mouth_04(X, Y):                                                          # a wide open grin: flat top, round bottom
        yt = 0.0115
        return (Y <= yt) & m_ellipse(X, Y, 0.0240, 0.0230, 0.0, yt) & m_rbox(X, Y - (yt - 0.0125), 0.0240, 0.0125, 0.0030)

    def mouth_05(X, Y): return m_ellipse(X, Y, 0.0165, 0.0195)                   # a big open oval
    def mouth_06(X, Y): return m_ellipse(X, Y, 0.0090, 0.0090)                   # small and round (humming)

    def mouth_07(X, Y):                                                          # open, a drop of drool from one corner
        yb = -0.0010
        d, w = dist_polyline(X, Y, [(0.0070, yb + 0.0012, 0.0032), (0.0072, yb - 0.0060, 0.0019), (0.0074, yb - 0.0140, 0.0034)])
        return dome(X, Y, 0.0140, yb, 0.0190, 0.0028) | (d <= w)

    def mouth_08(X, Y):                                                          # a wavy closed line
        return line(X, Y, [(x, 0.0026 * math.cos(2.0 * math.pi * x / 0.0170)) for x in np.linspace(-0.0215, 0.0215, 41)], LW)

    def mouth_09(X, Y):                                                          # an arched closed line
        return line(X, Y, [(0.0170 * t, -0.0050 + 0.0085 * (1 - t * t)) for t in np.linspace(-1, 1, 17)], LW)

    def mouth_10(X, Y):                                                          # a pucker with a crease beside it
        xr = 0.0050
        ring = m_ellipse(X, Y, 0.0090, 0.0085, xr, 0.0) & ~m_ellipse(X, Y, 0.0034, 0.0027, xr, 0.0012)
        rc = 0.0090 + 0.0045 + 0.0021
        return ring | line(X, Y, [(xr - rc * math.cos(math.radians(a)), rc * math.sin(math.radians(a))) for a in np.linspace(-34.0, 34.0, 9)], 0.0021)

    def mouth_11(X, Y):                                                          # a wavy open mouth with drool
        yc = 0.0075
        m = m_rbox(X, Y - yc, 0.0190, 0.0050, 0.0050)
        for xc in (-0.0162, -0.0054, 0.0054, 0.0162):
            m |= m_ellipse(X, Y, 0.0060, 0.0073, xc, yc)
        d, w = dist_polyline(X, Y, [(0.0080, yc - 0.0050, 0.0028), (0.0082, yc - 0.0125, 0.0018), (0.0084, yc - 0.0200, 0.0032)])
        return m | (d <= w)

    def angry_01(X, Y):                   # anger mark: four arcs bowed toward the middle, a cross of gaps between them
        m = np.zeros(X.shape, dtype=bool)
        for k in range(4):
            t = math.radians(45.0 + 90.0 * k)
            cx, cy = 0.0133 * math.cos(t), 0.0133 * math.sin(t)
            m |= line(X, Y, [(cx - 0.0051 * math.cos(t + math.radians(a)), cy - 0.0051 * math.sin(t + math.radians(a)))
                             for a in np.linspace(-60.0, 60.0, 13)], 0.0023)
        return m

    def cheak_01(X, Y):                   # blush: a soft rose oval
        return 0.90 * np.clip(1.0 - (X / 0.0140) ** 2 - (Y / 0.0092) ** 2, 0.0, 1.0) ** 1.2

    def shock_01(X, Y):                   # gloom: three short lines hanging from the top
        m = np.zeros(X.shape, dtype=bool)
        for xa, ln in ((-0.0115, 0.0150), (0.0, 0.0215), (0.0115, 0.0175)):
            m |= line(X, Y, [(xa, 0.0118), (xa, 0.0118 - ln)], 0.0021)
        return m

    def tear_01(X, Y):                    # a wavy tear, a little wider as it runs down
        top, bot = 0.0160, -0.0145
        d, w = dist_polyline(X, Y, [(0.0014 * math.sin(2.0 * math.pi * (top - y) / 0.0200), y, 0.0016 + 0.0012 * (top - y) / (top - bot))
                                    for y in np.linspace(top, bot, 41)])
        return d <= w

    return {'eye_01_m': (eye_01, EYES), 'eye_02_m': (eye_02, EYES), 'eye_03_m': (eye_03, EYES), 'eye_04_m': (eye_04, EYES),
            'eye_05_m': (eye_05, eye_05_col), 'eye_hum_m': (eye_hum, EYES),
            'mouth_01_m': (mouth_01, RED), 'mouth_02_m': (mouth_02, RED), 'mouth_03_m': (mouth_03, RED),
            'mouth_04_m': (mouth_04, RED), 'mouth_05_m': (mouth_05, RED), 'mouth_06_m': (mouth_06, RED),
            'mouth_07_m': (mouth_07, RED), 'mouth_08_m': (mouth_08, RED), 'mouth_09_m': (mouth_09, RED),
            'mouth_10_m': (mouth_10, RED), 'mouth_11_m': (mouth_11, RED),
            'angry_01_m': (angry_01, ANGER), 'cheak_01_m': (cheak_01, BLUSH), 'shock_01_m': (shock_01, SHOCK),
            'tear_01_m': (tear_01, TEAR)}


def cell_alpha(fn, wm, hm, U, V):
    inside = (U > 0.0) & (U < 1.0) & (V > 0.0) & (V < 1.0)
    return np.clip(np.asarray(fn((U - 0.5) * wm, (0.5 - V) * hm), dtype=np.float64), 0.0, 1.0) * inside


# front-view centre of each eye and mouth quad
EYE_PIECES = [('eye_01_m', EYE_Z), ('eye_02_m', EYE_Z), ('eye_03_m', EYE_Z), ('eye_04_m', EYE_Z), ('eye_05_m', EYE_Z), ('eye_hum_m', EYE_Z)]
MOUTH_PIECES = [('mouth_01_m', 0.7225), ('mouth_02_m', 0.7235), ('mouth_03_m', 0.7235), ('mouth_04_m', 0.7265),
                ('mouth_05_m', 0.7200), ('mouth_06_m', 0.7240), ('mouth_07_m', 0.7210), ('mouth_08_m', 0.7240),
                ('mouth_09_m', 0.7240), ('mouth_10_m', 0.7240), ('mouth_11_m', 0.7215)]
# overlays: front-view centre of the sheet (for a mirrored pair, the one on their left) and whether it is a pair
OVERLAYS = {'cheak_01_m': (0.0500, 0.7510, True), 'tear_01_m': (0.0440, 0.7445, True),
            'angry_01_m': (0.0700, 0.8000, False), 'shock_01_m': (0.0700, 0.8005, False)}
SHOWN = ('head_m', 'nose_m', 'antena_m', 'body_m', 'hand_L_m', 'hand_R_m', 'leg_L_m', 'leg_R_m', 'face_01_m', 'eye_01_m', 'mouth_01_m')


def check_face_layout(shapes):
    """Everything painted on a face piece shows on the cream inside the rim, in a front view; every overlay sheet lies
    over the plate; no overlay sheet lies over an eye or mouth quad."""
    for name, (fn, col) in shapes.items():
        wm, hm = FACE_CELLS[name][1]
        if name in OVERLAYS:
            cx, cz, pair = OVERLAYS[name]
        else:
            cx = EYE_X if name.startswith('eye') else 0.0
            cz = dict(EYE_PIECES + MOUTH_PIECES)[name]
        U, V = np.meshgrid(np.linspace(0.0, 1.0, 161), np.linspace(0.0, 1.0, 161))
        on = cell_alpha(fn, wm, hm, U, V) > 0.02
        assert on.any(), name
        x = cx + (U - 0.5) * wm; z = cz + (0.5 - V) * hm
        assert in_poly(INNER_POLY, x, z, 0.001)[on].all(), name + ' is drawn outside the cream'
        assert in_poly(PLATE_POLY, x, z, 0.0005).all(), name + ' quad leaves the plate'
        if name in OVERLAYS:
            for other, oz in EYE_PIECES + MOUTH_PIECES:
                ow, oh = FACE_CELLS[other][1]
                ox = EYE_X if other.startswith('eye') else 0.0
                assert abs(cx - ox) >= (wm + ow) / 2 - 1e-9 or abs(cz - oz) >= (hm + oh) / 2 - 1e-9, (name, other)


def paint_face():
    shapes = face_shapes()
    assert set(shapes) == set(FACE_CELLS), set(shapes) ^ set(FACE_CELLS)
    check_face_layout(shapes)
    cv = np.zeros((FACE_PX * SS, FACE_PX * SS, 4))
    cv[..., :3] = 0.5
    for name, (fn, col) in shapes.items():
        rect, (wm, hm) = FACE_CELLS[name]

        def cell(U, V, fn=fn, col=col, wm=wm, hm=hm):
            rgb = col((U - 0.5) * wm, (0.5 - V) * hm) if callable(col) else np.broadcast_to(hx(col), U.shape + (3,))
            return np.dstack([rgb, cell_alpha(fn, wm, hm, U, V)])
        paint(cv, rect, cell, gut=3)
    return save_png(FACE_TEX, downsample(cv), alpha=True)


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
    """Quads between successive rings.  ring_pts[i][j] are points, uv_rows[i][j] their UVs (j runs 0..n)."""
    n = len(ring_pts[0]) - 1
    for i in range(len(ring_pts) - 1):
        for j in range(n):
            m.face([ring_pts[i][j], ring_pts[i][j + 1], ring_pts[i + 1][j + 1], ring_pts[i + 1][j]],
                   [uv_rows[i][j], uv_rows[i][j + 1], uv_rows[i + 1][j + 1], uv_rows[i + 1][j]], out, grp, w)


# ---------------------------------------------------------------- hood, with the window's rim
def spow(v, e): return math.copysign(abs(v) ** e, v)


def hood_pt(th, z):
    """Point of the hood ring at height z; th = 0 is the front, 90 degrees their left."""
    s, c = math.sin(th), math.cos(th)
    x = float(hood_w(z)) * spow(s, 2.0 / SEC_P)
    if z > ZB + 1e-9:                               # above the brow: the faces it shares with the horns
        return (x, lean_y(z) - math.copysign(hood_top(x, z), c) if abs(c) > 1e-9 else lean_y(z), z)
    d = float(hood_f(z)) if c > 0 else float(hood_b(z))
    return (x, -d * spow(c, 2.0 / SEC_P), z)


HOOD_Z = [0.620, 0.640, 0.660, 0.694, 0.720, 0.744, 0.768, 0.790, ZB] + \
         [ZB + DOME_H * t for t in (0.22, 0.44, 0.62, 0.77, 0.88, 0.95, 0.99)]
HOOD_TOP = ZB + DOME_H


def build_head():
    m = MB('head_m')
    th = TH + [TH[0] + 2 * math.pi]
    cen = (0.0, 0.0, 0.83)
    out = lambda c: vsub(c, cen)
    hv = lambda z: (HOOD_V0 - z) / (HOOD_V0 - HOOD_V1)
    rings = [[hood_pt(th[j % N], z) for j in range(N + 1)] for z in HOOD_Z]
    uvs = [[uvb('hood', j / N, hv(z)) for j in range(N + 1)] for z in HOOD_Z]
    tube(m, rings, uvs, out, 'hood')
    for j in range(N):                              # the dome's crown and the closed, rounded chin end inside the robe's top
        m.face([rings[-1][j], rings[-1][j + 1], (0.0, lean_y(HOOD_TOP), HOOD_TOP)],
               [uvs[-1][j], uvs[-1][j + 1], uvb('hood', (j + 0.5) / N, hv(HOOD_TOP))], out, 'hood')
        m.face([rings[0][j], rings[0][j + 1], (0.0, 0.0, 0.610)],
               [uvs[0][j], uvs[0][j + 1], uvb('hood', (j + 0.5) / N, hv(0.610))], (0, 0, -1), 'hood')
    n_hood = len(m.faces)
    # The yellow rim: a raised band round the window.  Its outer skirt sinks into the hood, its inner wall drops to
    # behind the plate; the hood's own surface carries on behind the face.
    def ring(d, dy):
        return [(x, float(hood_y(x, z)) + dy, z) for x, z in offset_poly(OUTLINE, d)]
    A, B, Cc, D = ring(0.0, 0.0045), ring(0.0020, -RIM_OFF), ring(RIM_IN, -RIM_OFF), ring(RIM_IN + 0.0025, -0.0012)
    K = len(A)
    inward = lambda c: (-c[0], -0.004, 0.770 - c[2])
    for k in range(K):
        l = (k + 1) % K
        sec = 't' if (k < 5 or k >= K - 5) else ('l' if k < 14 else 'r')   # sharp across the window's two corners and its tip:
        # a mitred corner squeezes the inner wall's quad to a sliver, and a normal smoothed round the corner would turn against it
        m.face([A[k], A[l], B[l], B[k]], [uvb('rim', 0.5, 0.75)] * 2 + [uvb('rim', 0.5, 0.35)] * 2, (0, -1, 0), 'rim_out_' + sec)
        m.face([B[k], B[l], Cc[l], Cc[k]], [uvb('rim', 0.5, 0.30)] * 2 + [uvb('rim', 0.5, 0.20)] * 2, (0, -1, 0), 'rim_top_' + sec)
        m.face([Cc[k], Cc[l], D[l], D[k]], [uvb('rim', 0.5, 0.45)] * 2 + [uvb('rim', 0.5, 0.95)] * 2, inward, 'rim_in_' + sec)
    return m, n_hood


# ---------------------------------------------------------------- face plate, eyes, mouths, overlays, nose
def plate_rows(cols=4):
    """Rows of (x, z) filling the plate's outline: the top edge, a row at each side point, the tip."""
    (tx, tz), _ = poly_half(PLATE_POLY)
    xc, zc = PLATE_POLY[5]; ztip = PLATE_POLY[14][1]
    us = [k / cols for k in range(-cols, cols + 1)]
    ztop = lambda u: float(np.interp(abs(u) * xc, tx, tz))
    rows = [[(u * xc, ztop(u)) for u in us]]
    for i in range(1, 9):
        hxi, zi = PLATE_POLY[5 + i]
        v = (zi - ztip) / (zc - ztip)
        rows.append([(u * hxi, zi - (zc - ztop(u)) * v * v) for u in us])
    rows.append([(0.0, ztip)] * len(us))
    return rows


def build_plate(name, block):
    m = MB(name)
    rows = plate_rows()
    P = lambda p: (p[0], float(plate_y(p[0], p[1])), p[1])
    for i in range(len(rows) - 1):
        for k in range(len(rows[0]) - 1):
            q = [rows[i][k], rows[i][k + 1], rows[i + 1][k + 1], rows[i + 1][k]]
            m.face([P(p) for p in q], [face_uv(block, p[0], p[1]) for p in q], (0, -1, 0), 'plate')
    return m


def flat_quad(m, name, cx, cz, nx, nz, mirror, grp):
    """A flat piece on the plane that touches the plate's curve at (cx, cz), EYE_OFF in front of it."""
    wm, hm = FACE_CELLS[name][1]
    gx, gz = plate_grad(cx, cz)
    y0 = float(plate_y(cx, cz)) - EYE_OFF
    pt = lambda s, t: (cx + (s - 0.5) * wm, y0 + gx * (s - 0.5) * wm + gz * (t - 0.5) * hm, cz + (t - 0.5) * hm)
    uv = lambda s, t: uvf(name, 1.0 - s if mirror else s, 1.0 - t)
    for i in range(nz):
        for k in range(nx):
            s0, s1, t0, t1 = k / nx, (k + 1) / nx, i / nz, (i + 1) / nz
            m.face([pt(s0, t0), pt(s1, t0), pt(s1, t1), pt(s0, t1)], [uv(s0, t0), uv(s1, t0), uv(s1, t1), uv(s0, t1)], (0, -1, 0), grp)


def build_eye(name, zf):
    m = MB(name)
    flat_quad(m, name, EYE_X, zf, 1, 1, False, 'eye_l')
    flat_quad(m, name, -EYE_X, zf, 1, 1, True, 'eye_r')
    return m


def build_mouth(name, zf):
    m = MB(name)
    flat_quad(m, name, 0.0, zf, 2, 2, False, 'mouth')
    return m


def build_overlay(name):
    """An open sheet that follows the plate, OVER_OFF in front of it.  U and V follow the front view."""
    m = MB(name)
    wm, hm = FACE_CELLS[name][1]
    cx, cz, pair = OVERLAYS[name]
    nx, nz = max(2, int(math.ceil(wm / 0.010))), max(2, int(math.ceil(hm / 0.010)))
    for sd in ((1.0, -1.0) if pair else (1.0,)):
        def pt(s, t):
            x, z = sd * cx + (s - 0.5) * wm, cz + (t - 0.5) * hm
            return (x, float(plate_y(x, z)) - OVER_OFF, z)
        uv = lambda s, t: uvf(name, s if sd > 0 else 1.0 - s, 1.0 - t)
        for i in range(nz):
            for k in range(nx):
                s0, s1, t0, t1 = k / nx, (k + 1) / nx, i / nz, (i + 1) / nz
                m.face([pt(s0, t0), pt(s1, t0), pt(s1, t1), pt(s0, t1)], [uv(s0, t0), uv(s1, t0), uv(s1, t1), uv(s0, t1)], (0, -1, 0), 'overlay')
    return m


def build_nose():
    """A small pyramid, point up: from the front the sheet's triangle (0.018 wide, z 0.742 to 0.772), its ridge
    standing 9 mm off the plate."""
    m = MB('nose_m')
    P = lambda x, z, off: (x, float(plate_y(x, z)) - off, z)
    b1, b2, top = P(-0.009, 0.742, 0.0008), P(0.009, 0.742, 0.0008), P(0.0, 0.772, 0.0008)
    peak = P(0.0, 0.7475, 0.0090)
    cen = tuple(sum(p[i] for p in (b1, b2, top, peak)) / 4.0 for i in range(3))
    out = lambda c: vsub(c, cen)
    m.face([b1, top, peak], [uvb('nose', 0.2, 0.55), uvb('nose', 0.5, 0.05), uvb('nose', 0.5, 0.45)], out, 'n_r')
    m.face([top, b2, peak], [uvb('nose', 0.5, 0.05), uvb('nose', 0.8, 0.40), uvb('nose', 0.5, 0.30)], out, 'n_l')
    m.face([b1, b2, peak], [uvb('nose', 0.2, 0.95), uvb('nose', 0.8, 0.95), uvb('nose', 0.5, 0.70)], out, 'n_under')
    m.face([b1, b2, top], [uvb('nose', 0.5, 0.5)] * 3, out, 'n_back')
    return m


# ---------------------------------------------------------------- horns (the antenna piece)
HORN_UR = -0.68              # below the dome, the horn's inner rounding starts this far across its section (-1 inner end, +1 outer edge)


def horn_inner(z):
    """Where the horn's section ends on its inner side, and where across it (u, -1 to 1) its inner rounding starts.
    From the dome up the end is the inner edge itself and the section is a plain oval.  Below that the end is hidden
    well inside the hood and the rounding is kept to the hidden part."""
    xo, xi = horn_lines(z)
    if z >= HORN_IN[1]:
        return xi, 0.0
    return max(xi, float(hood_w(z)) - 0.110), HORN_UR * (1.0 - float(sstep(1.015, HORN_IN[1], z)))


def horn_x(u, z):
    xo, _ = horn_lines(z); xin, _ = horn_inner(z)
    return (xo + xin) / 2.0 + (xo - xin) / 2.0 * u


def horn_half_depth(u, z):
    _, ur = horn_inner(z)
    r = min(max((ur - u) / (ur + 1.0), 0.0), 1.0)
    return shared_face(horn_x(u, z), z) * (1.0 - r ** SEC_P) ** (1.0 / SEC_P)


def hood_brow_depth(x, back):
    """|y| of the hood mesh's brow ring (its 24-sided outline) at x."""
    pts = [hood_pt(th, ZB) for th in TH]
    side = sorted((q[0], abs(q[1])) for q in pts if (q[1] >= -1e-9 if back else q[1] <= 1e-9))
    return float(np.interp(abs(x), [q[0] for q in side], [q[1] for q in side]))


HORN_Z = [ZB, 0.885, 0.930, 0.975, 1.020, 1.060, 1.100, 1.140, 1.175, HORN_TIP[1]]


def build_antenna():
    """Both horns with their pink tips and red balls.  Each is the hood's upper corner drawn out: at the brow its
    section is the hood's own, and above it the hood's front and back faces are the horn's (shared_face)."""
    m = MB('antena_m')
    zt = HORN_TIP[1]
    c0 = sum(horn_lines(ZB)) / 2.0
    ax = (c0 - HORN_TIP[0], ZB - zt); L = math.hypot(*ax); ax = (ax[0] / L, ax[1] / L)      # down the horn, front view
    ang = [2 * math.pi * k / NH for k in range(NH + 1)]

    # Where the horn's 16 vertices sit across its section.  The outer nine are placed so that at the brow they are the
    # hood ring's own vertices there: the horn's root edge is the hood's edge, exactly.
    x0 = [hood_pt(math.radians(d), ZB)[0] for d in (90.0, 75.0, 60.0, 45.0, 30.0)]
    us = [(x - horn_x(0.0, ZB)) / (horn_x(1.0, ZB) - horn_x(0.0, ZB)) for x in x0] + [HORN_UR, -0.82, -0.95, -1.0]
    assert abs(us[0] - 1.0) < 1e-9 and all(a > b for a, b in zip(us[:-1], us[1:])), us

    def base(z, a):
        k = int(round(a / (2 * math.pi / NH))) % NH
        u = us[min(k, NH - k)]; back = 0 < k < NH // 2
        x = horn_x(u, z)
        y = horn_half_depth(u, z) if z > ZB else hood_brow_depth(x, back)
        if k in (0, NH // 2):
            y = 0.0
        return (x, (y if back else -y) + lean_y(z), z)
    along = lambda p: max((p[0] - HORN_TIP[0]) * ax[0] + (p[2] - zt) * ax[1], 0.0)             # metres from the tip, along the horn
    HORN_LEN[0] = max(along(base(ZB, a)) for a in ang)
    print('HORN axis (%.3f, %.3f), length for the bands %.3f' % (ax[0], ax[1], HORN_LEN[0]))
    for sd in (1.0, -1.0):
        pt = lambda z, a: (sd * base(z, a)[0],) + base(z, a)[1:]
        uv = lambda z, k: uvb('horn', k / NH, along(base(z, ang[k])) / HORN_LEN[0])
        ctr = lambda z: (sd * horn_x(0.0, max(z, ZB)), lean_y(z), z)
        zs = HORN_Z
        rings = [[pt(z, a) for a in ang] for z in zs]
        uvs = [[uv(z, k) for k in range(NH + 1)] for z in zs]
        for i in range(len(zs) - 1):
            mid = ctr((zs[i] + zs[i + 1]) / 2 - 0.02)
            for k in range(NH):
                m.face([rings[i][k], rings[i][k + 1], rings[i + 1][k + 1], rings[i + 1][k]],
                       [uvs[i][k], uvs[i][k + 1], uvs[i + 1][k + 1], uvs[i + 1][k]], lambda c, mid=mid: vsub(c, mid), 'horn_%d' % sd)
        for k in range(NH):                         # closed at both ends: inside the hood, and under the ball
            m.face([rings[0][k], rings[0][k + 1], ctr(zs[0])], [uvs[0][k], uvs[0][k + 1], uvb('horn', (k + 0.5) / NH, 1.0)], (0, 0, -1), 'horn_base_%d' % sd)
            m.face([rings[-1][k], rings[-1][k + 1], ctr(zt)], [uvs[-1][k], uvs[-1][k + 1], uvb('horn', (k + 0.5) / NH, 0.0)], (0, 0, 1), 'horn_base_t%d' % sd)
        bx, bz, br = BALL
        sphere(m, (sd * bx, lean_y(bz), bz), br, lambda d: uvb('ball', 0.5 + 0.4 * d[0], 0.5 - 0.46 * d[2]), 'ball_%d' % sd, nseg=NH, nlat=8)
    return m


# ---------------------------------------------------------------- one shading for the hood's top and the horns
def depth_normal(dfun, x, z, back, lean):
    """Outward unit normal of the surface y = (lean) -/+ dfun(|x|, z) at (x, z), by differences."""
    e = 1e-4; ax = abs(x); sg = -1.0 if x < 0 else 1.0
    dx = (dfun(ax + e, z) - dfun(max(ax - e, 0.0), z)) / (ax + e - max(ax - e, 0.0))
    dz = (dfun(ax, z + e) - dfun(ax, z - e)) / (2 * e)
    ln = LEAN if lean else 0.0
    n = Vector((-dx * sg, 1.0, -ln - dz)) if back else Vector((-dx * sg, -1.0, ln - dz))
    return n.normalized()


def hood_low_depth(back):
    def f(ax, z):
        w = float(hood_w(z)); d = float(hood_b(z) if back else hood_f(z))
        return d * (1.0 - min(ax / w, 1.0) ** SEC_P) ** (1.0 / SEC_P)
    return f


def horn_depth(ax, z):
    xo, _ = horn_lines(z); xin, _ = horn_inner(z)
    return horn_half_depth((ax - (xo + xin) / 2.0) / ((xo - xin) / 2.0), z)


def brow_normal(x, back):
    """At the brow ring the hood's and the horn's vertices lie together and take one normal: the mean of the hood's
    band below and the shared face just above."""
    return (depth_normal(hood_low_depth(back), x, ZB - 0.002, back, False) + depth_normal(shared_face, x, ZB + 0.002, back, True)).normalized()


def set_shared_normals(ob, m, kind):
    """Custom corner normals above the brow, so head_m and antena_m shade as one surface: each corner takes the normal
    of the surface function its vertex lies on (the shared face, where both meshes follow it).  The hood's closed roof
    and arch ends, which the shared face does not cover, and every other corner keep their own normals."""
    me = ob.data
    own = [Vector(c.vector) for c in me.corner_normals]
    out = [(0.0, 0.0, 0.0)] * len(me.loops)                   # a zero vector keeps the corner's own normal
    n_set = 0
    for poly, (ids, _, grp) in zip(me.polygons, m.faces):
        if not (grp == 'hood' if kind == 'hood' else (grp.startswith('horn_') and not grp.startswith('horn_base'))):
            continue
        for li, vi in zip(poly.loop_indices, ids):
            x, y, z = m.v[vi]
            if z < ZB - 1e-6 or abs(x) < 1e-9 and kind == 'hood' and z > ZB + DOME_H - 1e-6:
                continue
            back = y > lean_y(z)
            if z <= ZB + 1e-6:
                n = brow_normal(x, back)
            elif kind == 'horn':
                n = depth_normal(horn_depth, abs(x), z, back, True)
                if z > HORN_IN[1] and abs(x) < horn_x(0.0, z):         # just above the dome the horn's inner side turns away
                    k = 1.0 - float(sstep(HORN_IN[1], 1.125, z))       # from the face; ease its shading in over the next rings
                    n = (n * (1.0 - k) + depth_normal(shared_face, abs(x), z, back, True) * k).normalized()
                n.x *= (-1.0 if x < 0 else 1.0)
            else:
                t = min(max((z - ZB) / DOME_H, 0.0), 1.0)
                off = (HOOD_OVER - (HOOD_OVER + HOOD_UNDER) * float(sstep(horn_x(-0.40, z), horn_x(0.10, z), abs(x)))) * float(sstep(0.0, 0.10, t))
                w = 1.0 - float(sstep(0.0005, 0.0040, abs(hood_top(x, z) - (shared_face(x, z) + off))))   # 1 on the shared face
                w = max(w, 1.0 - float(sstep(0.86, 0.99, t)))          # the arch's ends under the horns shade as the face too
                n = (own[li] * (1.0 - w) + depth_normal(shared_face, x, z, back, True) * w).normalized()
            out[li] = tuple(n); n_set += 1
    me.normals_split_custom_set(out)
    me.update()
    bad = 0
    me.calc_loop_triangles()
    for t in me.loop_triangles:                                # every triangle still agrees with its corner normals
        v = [me.vertices[i].co for i in t.vertices]
        c = (v[1] - v[0]).cross(v[2] - v[0])
        if c.length < 1e-12 or c.dot(sum((me.corner_normals[l].vector for l in t.loops), Vector())) <= 0:
            bad += 1; print('NORMALS bad', ob.name, [tuple(round(q, 4) for q in w) for w in v])
    print('NORMALS %s: %d corners set, %d triangles against their corner normals' % (ob.name, n_set, bad))
    assert bad == 0, ob.name


# ---------------------------------------------------------------- robe
def bp(th, r, z, ky):
    return (r * math.sin(th), -ky * r * math.cos(th), z)


def robe_uv(j, end, z):
    """UV of body angle index j (15-degree steps; end: the far side of a column) at height z.  The front half has its
    own block with the trace; the back half is plain."""
    d = 15.0 * j
    v = (ZR_TOP - z) / (ZR_TOP - ZR_BOT)
    if d < 90.0 or (end and d == 90.0): return uvb('robe_front', 0.5 + d / 180.0, v)
    if d < 270.0 or (end and d == 270.0): return uvb('robe_back', (d - 90.0) / 180.0, v)
    return uvb('robe_front', (d - 270.0) / 180.0, v)


def build_body():
    m = MB('body_m')
    th = TH + [TH[0] + 2 * math.pi]
    prof = [(z, r, 'robe') for z, r in NECK + ROBE] + [(z, r, 'lip') for z, r in LIP] + [(z, r, 'under') for z, r in UNDER]
    ky = lambda z, grp: robe_ky(z)
    kys = [ky(z, g) for z, r, g in prof]
    pink_v = {0.160: 0.10, 0.130: 0.25, 0.105: 0.40, 0.090: 0.60, 0.082: 0.75}

    def hint(z0, r0, z1, r1):
        nr, nz = -(z1 - z0), (r1 - r0)             # the profile's outward normal (radial, up)
        def f(c):
            L = math.hypot(c[0], c[1]) or 1.0
            return (nr * c[0] / L, nr * c[1] / L, nz)
        return f
    for i in range(len(prof) - 1):
        (z0, r0, g0), (z1, r1, g1) = prof[i], prof[i + 1]
        grp = g1
        for j in range(N):
            pts = [bp(th[j], r0, z0, kys[i]), bp(th[j + 1], r0, z0, kys[i]), bp(th[j + 1], r1, z1, kys[i + 1]), bp(th[j], r1, z1, kys[i + 1])]
            if grp == 'robe':
                uv = [robe_uv(j, False, z0), robe_uv(j + 1, True, z0), robe_uv(j + 1, True, z1), robe_uv(j, False, z1)]
            elif grp == 'lip':
                uv = [uvb('pink', 0.5, pink_v[round(z0, 3)])] * 2 + [uvb('pink', 0.5, pink_v[round(z1, 3)])] * 2
            else:
                uv = [uvb('pink', 0.5, 0.80)] * 2 + [uvb('pink', 0.5, 0.92)] * 2
            m.face(pts, uv, hint(z0, r0, z1, r1), grp)
    zt, rt, _ = prof[0]; zu, ru, _ = prof[-1]
    for j in range(N):                              # closed: the top of the neck inside the hood, and the pink underside
        m.face([bp(th[j], rt, zt, kys[0]), bp(th[j + 1], rt, zt, kys[0]), (0.0, 0.0, zt + 0.004)],
               [robe_uv(j, False, zt), robe_uv(j + 1, True, zt), robe_uv(j, False, zt)], (0, 0, 1), 'robe')
        m.face([bp(th[j], ru, zu, kys[-1]), bp(th[j + 1], ru, zu, kys[-1]), (0.0, 0.0, Z_UNDER)],
               [uvb('pink', 0.5, 0.92)] * 2 + [uvb('pink', 0.5, 0.98)], (0, 0, -1), 'under')
    return m


# ---------------------------------------------------------------- sleeves and legs
def build_arm(sd, spec):
    m = MB('hand_L_m' if sd > 0 else 'hand_R_m')
    za = spec['JNT_arm_L']['head'][2]
    xe = spec['JNT_elbow_L']['head'][0]; xh = spec['JNT_hand_L']['head'][0]
    r_ball = 0.030; xc = xh + 0.022
    q_join = 135.0
    xj = xc + r_ball * math.cos(math.radians(q_join)); rj = r_ball * math.sin(math.radians(q_join))
    xw = xh - 0.022
    xr = [0.085, xe - 0.030, xe, xe + 0.030, xw, xj - 0.0085, xj]
    rs = [0.0240 + (0.0205 - 0.0240) * (x - xr[0]) / (xw - xr[0]) for x in xr[:5]] + [0.0212, rj]
    ang = [2 * math.pi * j / N for j in range(N + 1)]
    pt = lambda x, r, a: (sd * x, -r * math.sin(a), za + r * math.cos(a))        # a = 0 is the top of the sleeve
    sv = lambda a: 0.42 - 0.30 * math.cos(a)                                     # lighter on top, shade beneath
    rings = [[pt(x, r, a) for a in ang] for x, r in zip(xr, rs)]
    uvs = [[uvb('sleeve', 0.5, sv(a)) for a in ang] for x in xr]
    tube(m, rings, uvs, lambda c: (0.0, c[1], c[2] - za), 'arm')
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], (sd * xr[0], 0.0, za)], [uvb('sleeve', 0.5, 0.5)] * 3, (-sd, 0, 0), 'root')
    cen = (sd * xc, 0.0, za)                        # the mitten: a round end; its ring at q_join is the tube's last ring
    qs = [0.0, 27.0, 54.0, 81.0, 108.0, q_join]
    hpt = lambda q, a: (sd * (xc + r_ball * math.cos(math.radians(q))), -r_ball * math.sin(math.radians(q)) * math.sin(a),
                        za + r_ball * math.sin(math.radians(q)) * math.cos(a))
    hs = lambda q: math.sin(math.radians(q)) + (1.0 - math.sin(math.radians(q))) * float(sstep(108.0, q_join, q))
    huv = lambda q, a: uvb('sleeve', 0.5, 0.42 - 0.30 * hs(q) * math.cos(a))
    for i in range(len(qs) - 1):
        for j in range(N):
            a, b = ang[j], ang[j + 1]
            m.face([hpt(qs[i], a), hpt(qs[i], b), hpt(qs[i + 1], b), hpt(qs[i + 1], a)],
                   [huv(qs[i], a), huv(qs[i], b), huv(qs[i + 1], b), huv(qs[i + 1], a)], lambda c: vsub(c, cen), 'arm')
    return m


def build_leg(sd, spec):
    m = MB('leg_L_m' if sd > 0 else 'leg_R_m')
    xl = spec['JNT_leg_L']['head'][0]; zk = spec['JNT_knee_L']['head'][2]
    zr = [0.275, zk + 0.030, zk, zk - 0.030, 0.085, 0.020]
    rr = lambda z: 0.0100 + 0.0015 * float(sstep(0.10, 0.20, z))                 # 0.020 across below the hem (sheet)
    ang = [2 * math.pi * j / N for j in range(N + 1)]
    pt = lambda z, a: (sd * (xl + rr(z) * math.sin(a)), -rr(z) * math.cos(a), z)
    sv = lambda z: 0.35 + 0.45 * float(sstep(0.075, 0.12, z))                    # shaded under the robe
    rings = [[pt(z, a) for a in ang] for z in zr]
    uvs = [[uvb('slate', 0.5, sv(z)) for a in ang] for z in zr]
    tube(m, rings, uvs, lambda c: (c[0] - sd * xl, c[1], 0.0), 'leg')
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], (sd * xl, 0.0, zr[0])], [uvb('slate', 0.5, 0.8)] * 3, (0, 0, 1), 'top')
        m.face([rings[-1][j], rings[-1][j + 1], (sd * xl, 0.0, zr[-1])], [uvb('slate', 0.5, 0.8)] * 3, (0, 0, -1), 'end')
    # the shoe: pointed, its sole on the ground, pointing forward and out at 45 degrees
    c45 = math.sqrt(0.5)
    d = (sd * c45, -c45, 0.0); a2 = (sd * c45, c45, 0.0)
    t0, t1 = -0.022, 0.062

    def sec(t):
        if t < 0.0:
            k = math.sqrt(max(1.0 - (t / t0) ** 2, 0.0)); return 0.0170 * k, 0.032 * (0.45 + 0.55 * k)
        k = 1.0 - (t / t1) ** 1.6
        return 0.0170 * k, 0.009 + 0.023 * (1.0 - t / t1) ** 1.3
    ts = [-0.0185, -0.0070, 0.0100, 0.0300, 0.0500]
    w_sh = {'JNT_ankle_' + ('L' if sd > 0 else 'R'): 1.0}

    def spt(t, a):
        w, h = sec(t)
        return (sd * xl + d[0] * t + a2[0] * w * math.cos(a), d[1] * t + a2[1] * w * math.cos(a), h / 2 * (1.0 + math.sin(a)))
    suv = lambda t, a: uvb('slate', 0.5, 0.45 - 0.38 * math.sin(a))
    rings = [[spt(t, a) for a in ang] for t in ts]
    uvs = [[suv(t, a) for a in ang] for t in ts]
    axis_pt = lambda c: (sd * xl + d[0] * ((c[0] - sd * xl) * d[0] + c[1] * d[1]), d[1] * ((c[0] - sd * xl) * d[0] + c[1] * d[1]), 0.010)
    tube(m, rings, uvs, lambda c: vsub(c, axis_pt(c)), 'shoe', w=w_sh)
    heel = (sd * xl + d[0] * t0, d[1] * t0, 0.032 * 0.45 / 2); toe = (sd * xl + d[0] * t1, d[1] * t1, 0.0045)
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], heel], [uvs[0][j], uvs[0][j + 1], uvb('slate', 0.5, 0.45)], (-d[0], -d[1], 0), 'shoe', w=w_sh)
        m.face([rings[-1][j], rings[-1][j + 1], toe], [uvs[-1][j], uvs[-1][j + 1], uvb('slate', 0.5, 0.45)], (d[0], d[1], 0), 'shoe', w=w_sh)
    return m


# ---------------------------------------------------------------- weights
def w_body(p):
    x, z = p[0], p[2]
    a = float(sstep(0.330, 0.430, z))       # waist -> spine_01, round the spine_01 joint (0.380)
    b = float(sstep(0.442, 0.532, z))       # spine_01 -> spine_02, round the spine_02 joint (0.487)
    c = float(sstep(0.650, 0.700, z))       # spine_02 -> neck, only for the robe's top inside the hood; the shoulders stay on spine_02
    # The long robe follows the thighs toward the hem, as a skirt does: each side mostly on its own thigh, the
    # middle shared.  It ends below 0.31, before spine_01 begins, so no vertex has more than three joints.
    t = SKIRT_W * float(sstep(0.310, 0.110, z))
    l = float(sstep(-0.055, 0.055, x))
    return {'JNT_waist': (1 - a) * (1 - t), 'JNT_leg_L': t * l, 'JNT_leg_R': t * (1 - l),
            'JNT_spine_01': a * (1 - b), 'JNT_spine_02': b * (1 - c), 'JNT_neck': c}


SKIRT_W = 0.50


def w_arm(sd, spec):
    s = 'L' if sd > 0 else 'R'
    xe = spec['JNT_elbow_L']['head'][0]; xh = spec['JNT_hand_L']['head'][0]

    def f(p):
        x = abs(p[0])
        e = float(sstep(xe - 0.040, xe + 0.040, x)); h = float(sstep(xh - 0.024, xh - 0.002, x))
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
        w = {b: x for b, x in (m.wover.get(i) or wfun(p)).items() if x > 1e-3}
        tot = sum(w.values())
        for b, x in w.items():
            if b not in groups:
                groups[b] = ob.vertex_groups.new(name=b)
            groups[b].add([i], x / tot, 'REPLACE')
    ob.parent = arm
    ob.matrix_parent_inverse = RIG_TURN.inverted()   # the mesh stays where it was built, under the turned rig
    mod = ob.modifiers.new('Armature', 'ARMATURE'); mod.object = arm
    return ob


# OUAK's cousin joints rest with identity local rotations, world-aligned.  The FBX exporter writes the rig object as a
# Null under its Z-up to Y-up conversion, Rx(-90), and each bone as its rest matrix in rig space.  So the rig object is
# turned +90 degrees about X, which cancels that conversion, and every bone lies along the rig's +Y with roll 0, which
# makes its rest matrix identity.  In the viewport every bone points straight up.
RIG_TURN = Matrix.Rotation(math.pi / 2, 4, 'X')        # rig space -> world


def build_rig(spec):
    arm = bpy.data.armatures.new('Circuit_rig')
    ob = bpy.data.objects.new('model', arm)              # OUAK clips bind paths 'model/JNT_root/...'
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
        length = (Vector(tail(n)) - Vector(j['head'])).length
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
    """The factory start-up has a File Browser that remembers the home documents folder of whoever runs the build,
    and a .blend saves it.  Overwrite the whole stored text, then leave the file's own folder."""
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == 'FILE_BROWSER' and space.params is not None:
                    space.params.directory = b'/' * 512
                    space.params.directory = b'//'


# ---------------------------------------------------------------- self checks and renders
def check_hood_behind_plate(head, n_hood):
    """The hood's own surface lies behind the plate everywhere (no hole, and nothing of it pokes through the face)."""
    tree = BVHTree.FromPolygons([Vector(v) for v in head.v], [f[0] for f in head.faces[:n_hood]])
    worst = 1.0
    rows = plate_rows(cols=12)
    for row in rows:
        for x, z in row:
            y = float(plate_y(x, z))
            loc, nrm, idx, dist = tree.ray_cast(Vector((x * 0.999, y - 0.5, z)), Vector((0, 1, 0)))
            assert loc is not None, (x, z)
            worst = min(worst, loc.y - y)
    print('PLATE hood surface is at least %.2f mm behind the plate' % (worst * 1e3))
    assert worst > 0.0008


def check_face_depths(obs):
    """The same measurement the spec check makes: signed distance to face_01_m."""
    po = obs['face_01_m']; PW = po.matrix_world
    tree = BVHTree.FromPolygons([PW @ v.co for v in po.data.vertices], [tuple(p.vertices) for p in po.data.polygons])

    def sd(w):
        loc, n, i, d = tree.find_nearest(w)
        n = (PW.to_3x3() @ po.data.polygons[i].normal).normalized()
        return d if (w - loc).dot(n) >= 0 else -d
    flat = [n for n, _ in EYE_PIECES + MOUTH_PIECES]
    e_min = min(sd(obs[n].matrix_world @ v.co) for n in flat for v in obs[n].data.vertices)
    print('DEPTH eyes and mouths at least %.2f mm in front of the plate' % (e_min * 1e3))
    assert e_min >= 0.0022
    for n in OVERLAYS:
        o = obs[n]; W = o.matrix_world
        ds = [sd(W @ v.co) for v in o.data.vertices] + [sd(W @ p.center) for p in o.data.polygons]
        ny = max(float((W.to_3x3() @ p.normal).normalized().y) for p in o.data.polygons)
        print('DEPTH %-10s %.2f to %.2f mm off the plate, worst normal y %.2f' % (n, min(ds) * 1e3, max(ds) * 1e3, ny))
        assert 0.0007 <= min(ds) and max(ds) <= e_min - 0.0007 and ny < -0.6, n


def set_pose(rig, pose):
    """Turn joints about fixed axes of the body (X = their left, -Y = their front, Z = up), in degrees."""
    for pb in rig.pose.bones:
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
        if pb.name in pose:
            ax, deg = pose[pb.name]
            rest = (rig.matrix_world @ pb.bone.matrix_local).to_3x3().normalized()
            pb.rotation_quaternion = (rest.inverted() @ Matrix.Rotation(math.radians(deg), 3, ax) @ rest).to_quaternion()
    bpy.context.view_layer.update()


STRIDE = {'JNT_leg_L': ('X', -40.0), 'JNT_leg_R': ('X', 40.0), 'JNT_knee_L': ('X', 30.0), 'JNT_knee_R': ('X', 30.0)}


def legs_through_robe(obs, tag):
    """Counts leg vertices that lie outside the posed robe with robe below them: a leg showing above the hem."""
    dg = bpy.context.evaluated_depsgraph_get()

    def posed(ob):
        ev = ob.evaluated_get(dg); me = ev.to_mesh()
        vs = [ev.matrix_world @ v.co for v in me.vertices]; ps = [tuple(p.vertices) for p in me.polygons]
        ev.to_mesh_clear()
        return vs, ps
    vs, ps = posed(obs['body_m'])
    tree = BVHTree.FromPolygons(vs, ps)

    def hits(o, d):
        n = 0; o = Vector(o)
        while n < 20:
            loc, nrm, idx, dist = tree.ray_cast(o, d)
            if loc is None:
                break
            n += 1; o = loc + d * 1e-5
        return n
    bad = 0; tot = 0
    for name in ('leg_L_m', 'leg_R_m'):
        for v in posed(obs[name])[0]:
            tot += 1
            if hits(v, Vector((0, 0, 1))) % 2 == 0 and hits(v, Vector((0, 0, -1))) > 0:
                bad += 1
    print('LEGS %s: %d of %d leg vertices show above the hem' % (tag, bad, tot))
    return bad


def clear_render_rig():
    for ob in [o for o in bpy.context.scene.objects if o.type in ('CAMERA', 'LIGHT')]:
        data = ob.data
        bpy.data.objects.remove(ob)
        (bpy.data.cameras if isinstance(data, bpy.types.Camera) else bpy.data.lights).remove(data)
    for w in list(bpy.data.worlds):
        bpy.data.worlds.remove(w)
    bpy.context.scene.camera = None


def render(prefix, views):
    clear_render_rig()
    C.render_views(os.path.join(REN_DIR, prefix), views=views)


# ---------------------------------------------------------------- main
def main():
    for p in glob.glob(os.path.join(TEX_DIR, 'circuit_new_*.png')) + glob.glob(os.path.join(REN_DIR, 'Circuit_new_*.png')) + \
            [BLEND, BLEND + '1', FBX]:
        if os.path.exists(p):
            os.remove(p)
    os.makedirs(TEX_DIR, exist_ok=True); os.makedirs(REN_DIR, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0

    check_layout(BODY_BLOCKS, 256)
    check_layout({n: c[0] for n, c in FACE_CELLS.items()}, FACE_PX)
    antenna = build_antenna()                       # first: the horn block's painting needs the horn's length
    body_png = paint_body()
    face_png = paint_face()
    mat_body = C.img_material('Circuit_new_body', body_png)
    mat_face = C.img_material('Circuit_new_face', face_png, alpha=True)
    for mt in (mat_body, mat_face):
        mt.use_backface_culling = True              # as a game draws it: a flipped face would vanish

    with open(RIG_SPEC) as fh:
        spec = json.load(fh)['new_style']['joints']
    rig = build_rig(spec)

    head = {'JNT_head': 1.0}
    w_head = lambda p: head
    obs = []
    hood, n_hood = build_head()
    check_hood_behind_plate(hood, n_hood)
    obs.append(make_object(hood, mat_body, w_head, rig))
    set_shared_normals(obs[-1], hood, 'hood')
    obs.append(make_object(antenna, mat_body, w_head, rig))
    set_shared_normals(obs[-1], antenna, 'horn')
    for name, block in (('face_01_m', 'face'), ('face_02_m', 'face_gloom'), ('face_03_m', 'face_flush')):
        obs.append(make_object(build_plate(name, block), mat_body, w_head, rig))
    obs.append(make_object(build_nose(), mat_body, w_head, rig))
    for name, zf in EYE_PIECES:
        obs.append(make_object(build_eye(name, zf), mat_face, w_head, rig))
    for name, zf in MOUTH_PIECES:
        obs.append(make_object(build_mouth(name, zf), mat_face, w_head, rig))
    for name in OVERLAYS:
        obs.append(make_object(build_overlay(name), mat_face, w_head, rig))
    obs.append(make_object(build_body(), mat_body, w_body, rig))
    for sd in (1.0, -1.0):
        obs.append(make_object(build_arm(sd, spec), mat_body, w_arm(sd, spec), rig))
    for sd in (1.0, -1.0):
        obs.append(make_object(build_leg(sd, spec), mat_body, w_leg(sd, spec), rig))
    by = {ob.name: ob for ob in obs}
    assert len(by) == len(obs) and set(SHOWN) <= set(by)
    for ob in obs:
        if ob.name not in SHOWN:                    # every other face piece waits hidden
            ob.hide_render = True
            ob.hide_viewport = True                 # the FBX writes Visibility from this flag, not from hide_render
    bpy.context.view_layer.update()
    check_face_depths(by)

    total = 0
    for ob in obs:
        t = sum(len(p.vertices) - 2 for p in ob.data.polygons); total += t
        print('PART %-12s tris %5d verts %5d' % (ob.name, t, len(ob.data.vertices)))
    print('PART total tris', total)
    assert 4500 <= total <= 6000

    render('Circuit_new', ('front', 'side', 'back'))
    by['antena_m'].hide_render = True               # as when a present hides the antenna: the dome stays whole
    render('Circuit_new_nohorns', ('front', 'back'))
    by['antena_m'].hide_render = False
    assert legs_through_robe(by, 'rest') == 0
    set_pose(rig, STRIDE)
    bad = legs_through_robe(by, 'stride')
    render('Circuit_new_stride', ('side', 'three'))
    set_pose(rig, {})
    assert bad == 0, 'a leg shows through the robe in the stride'

    clear_render_rig()
    sc = bpy.context.scene
    sc.render.filepath = '_' * 1023
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
