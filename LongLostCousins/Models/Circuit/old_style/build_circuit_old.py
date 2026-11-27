"""Circuit, old style (the way Katamari Forever built its cousins).

Builds everything from an empty scene: textures, materials, meshes, the rig from
../../Burda/burda_rig_spec.json (block old_style), weights, renders, Circuit_old.blend and Circuit_old.fbx.

    blender -b --factory-startup --python build_circuit_old.py

Units: Blender metres, Z up, Circuit faces -Y, +X is their left.
Every path is resolved from this file. Geometry and textures are made here from numbers only.
"""
import bpy, os, sys, json, math
import numpy as np
from mathutils import Vector, Matrix

HERE = os.path.dirname(os.path.abspath(__file__))
TEX_DIR = os.path.join(HERE, 'textures')
REN_DIR = os.path.join(HERE, 'renders')
SPEC_PATH = os.path.normpath(os.path.join(HERE, '..', '..', 'Burda', 'burda_rig_spec.json'))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
sys.dont_write_bytecode = True                 # no __pycache__ next to the deliverables
import bl_common as C

T8 = math.tan(math.radians(22.5))          # octagon: half side / half width

# ----------------------------------------------------------------------------- palette (design sheet)
PAL = {
    'purple': '473998', 'purple_shade': '33296E', 'purple_light': '5A4CB4', 'pink': 'FF83C5', 'pink_pale': 'FED6E8',
    'red': 'FF1107', 'slate': '524B77', 'yellow': 'FBFA3B', 'green': '64F284', 'cream': 'FAF4E5',
    # face cards only
    'cream_shade': 'E6DCC6', 'eyes': '000000', 'nose': 'FFA43D', 'blush': 'F28AA0', 'pale_band': '5C7682',
    'pale_face': 'C9C3B4',
}
# the column of flat colour cells on the left of the body texture, top to bottom
CELLS = ['purple', 'purple_shade', 'purple_light', 'pink', 'pink_pale', 'red', 'slate', 'yellow', 'green', 'cream']


def rgb(name):
    h = PAL[name]
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64) / 255.0


# ----------------------------------------------------------------------------- the sheet's picture on the old rig
# The design sheet gives sizes in picture pixels (column, row). Old-style fit: 0.000471 m per pixel, the face
# centre (row 1086) on JNT_head at z 0.71139, which puts the soles (row 2599) at the ground.
M_PER_PX = 0.000471
HEAD_JZ = 0.71139


def PX(col):
    return (col - 828) * M_PER_PX


def PZ(row):
    return HEAD_JZ + (1086 - row) * M_PER_PX


# ----------------------------------------------------------------------------- hood (the head)
# Eight-sided rings, each with a wide flat front facet so the face card lies on it.
# z, a (half width), c (front facet half width), yf (front), ysf / ysb (side facet front and back ends),
# cb (back facet half width), yb (back).  Widths from the sheet; depths by eye (about 0.275 deep).
HOOD_RINGS = [
    (0.590, 0.070, 0.045, -0.080, -0.038, 0.036, 0.036, 0.070),     # hidden inside the robe's top, so a nod opens no gap
    (0.620, 0.080, 0.057, -0.09864, -0.048, 0.045, 0.045, 0.086),   # chin, sitting on the robe's top (picture: narrowest here)
    (0.659, 0.094, 0.0792, -0.11109, -0.067, 0.065, 0.058, 0.118),  # sheet: 400 px wide at row 1200; front on the card's plane
    (0.712, 0.126, 0.108, -0.128, -0.092, 0.090, 0.075, 0.145),     # the card's fold (sheet: 0.123 at z 0.706)
    (PZ(888), 0.175, 0.122, -0.125, -0.075, 0.090, 0.100, 0.150),   # brow, the widest (sheet: 742 px wide at row 900)
]
HOOD_TOP = (0.0, 0.010, PZ(454))                   # dome top between the horns (row 454)
# The horns are the hood's upper corners drawn out. Each one's root is the brow ring's whole side (front corner,
# side facet, back corner) and the two notch points where its inner edge meets the dome; from there six straight
# edges run to the small ring under the ball. Front view: outer edge brow corner -> tip, inner edge notch -> tip.
NOTCH_X, NOTCH_Z = PX(828 + 215), PZ(493)          # where the inner edge meets the dome (column 828 +- 215, row 493)
NOTCH_YF, NOTCH_YB = -0.082, 0.100                 # the dome's front and back at the notch, by eye
HORN_O0, HORN_O1 = (0.175, PZ(888)), (0.327, 1.142)       # outer edge: brow corner -> under the ball
HORN_I0, HORN_I1 = (NOTCH_X, NOTCH_Z), (0.306, 1.157)     # inner edge: notch -> under the ball
HORN_TIP_HALF_D = 0.015                            # under the ball the horn is round, about 0.03
HORN_SPLIT = 0.42                                  # below this the horn is plain purple; above it uses the tip area
HORN_T_PINK, HORN_T_PALE = 0.553, 0.775            # along the horn: purple/strong pink (row 400), strong/pale pink (row 280)
HORN_LEAN = math.tan(math.radians(10.0))           # the horns lean back 10 degrees from the brow up
BALL_X, BALL_Z, BALL_R = PX(1530), PZ(110), 75 * M_PER_PX

# face card: three rows (z, half width). It tapers to the chin with the hood; UVs stay front-view metres.
CARD_ROWS = [(0.803, 0.116), (0.712, 0.104), (0.624, 0.050)]
CARD_GAP = (0.0020, 0.0030, 0.0020)                # how far in front of the hood (along -Y) each row stands
FACE_X, FACE_Z0, FACE_Z1 = 0.116, 0.624, 0.803     # what the 64 x 64 card texture spans
FS = 64

# ----------------------------------------------------------------------------- robe
B_RATIO = 0.75                                     # depth / width (sheet)
# z, half width.  Regular octagons below the shoulders.
ROBE_PROFILE = [(0.585, 0.106), (PZ(1600), 0.111), (0.376, 0.128), (0.262, 0.148), (PZ(2270), 0.163), (0.080, 0.176)]
ROBE_TOP = (0.623, 0.083, 0.060, -0.101, -0.050, 0.047, 0.047, 0.089)  # as wide as the chin that sits on it; no collar
SHOULDER = (0.585, 0.114, 0.054, -0.096, -0.044, 0.042, 0.048, 0.090)     # the shoulders: straight out and down from the top
UNDER_APEX = (0.0, 0.0, 0.118)                     # the pink underside, a shallow cone inside the bell
Z_TRACE_TOP, Z_TRACE_BOT = ROBE_PROFILE[0][0], ROBE_PROFILE[3][0]
LEG_BLEND_TOP, LEG_BLEND_BOT = 0.400, ROBE_PROFILE[4][0]              # the robe follows the thighs below this

# ----------------------------------------------------------------------------- limbs
ARM_ZC = 0.6186                                    # the rig's arm line
ARM_RINGS = [(0.075, 0.0210), (0.222, 0.0210), (0.342, 0.0225)]      # (x, half thickness); elbow joint x 0.222
HAND_X, HAND_R = 0.362, 0.030
LEG_X = 0.05233
LEG_RINGS = [(0.3228, 0.0110), (0.181, 0.0100), (0.030, 0.0095)]     # (z, half thickness); knee joint z 0.181
SHOE_HEEL, SHOE_TOE, SHOE_HALF_W, SHOE_H = 0.020, 0.060, 0.021, 0.042

# ----------------------------------------------------------------------------- body texture layout (128 x 128, y down)
TS = 128
CELL_W, CELL_H = 16, 12
ROBE = (16, 0, 128, 80)         # x0, y0, x1, y1: purple with the green trace, seen from the front
ROBE_HALF_X = 0.160             # metres from the middle line to the area's edge
TIP = (16, 80, 128, 128)        # the horn above HORN_SPLIT, unrolled: purple, zigzag, strong pink, zigzag, pale pink
TIP_FACET = 16                  # texels per horn facet (one tooth each)
# trace: fractions of its box (columns 580-1078, rows 1396-2018), read from the picture
TRACE = [(0.00, 0.41), (0.15, 0.75), (0.26, 0.14), (0.41, 1.00), (0.49, 0.40), (0.58, 0.67), (0.67, 0.00), (0.74, 0.69),
         (0.89, 0.40), (0.91, 0.56), (1.00, 0.43)]
TRACE_W = 22 * M_PER_PX


def px(x, y):
    """texel position (y down) -> UV"""
    return (x / TS, 1.0 - y / TS)


def robe_uv(co):
    x0, y0, x1, y1 = ROBE
    u = x0 + 1 + (co[0] + ROBE_HALF_X) / (2 * ROBE_HALF_X) * (x1 - x0 - 2)
    v = y0 + 1 + (Z_TRACE_TOP - co[2]) / (Z_TRACE_TOP - Z_TRACE_BOT) * (y1 - y0 - 2)
    return px(u, v)


# ----------------------------------------------------------------------------- painting helpers
class Canvas:
    """Paint with supersampling, then average down. Coordinates are texels, y down."""

    def __init__(self, w, h, ss, base):
        self.w, self.h, self.ss = w, h, ss
        self.a = np.empty((h * ss, w * ss, 3))
        self.a[:] = base
        ys, xs = np.mgrid[0:h * ss, 0:w * ss]
        self.x = (xs + 0.5) / ss
        self.y = (ys + 0.5) / ss

    def result(self):
        a = self.a.reshape(self.h, self.ss, self.w, self.ss, 3).mean(axis=(1, 3))
        return np.round(a * 255.0) / 255.0


def poly_mask(gx, gy, pts):
    inside = np.zeros(gx.shape, dtype=bool)
    n = len(pts)
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % n]
        if y1 == y2:
            continue
        inside ^= ((y1 > gy) != (y2 > gy)) & (gx < (x2 - x1) * (gy - y1) / (y2 - y1) + x1)
    return inside


def stroke_mask(gx, gy, pts, width):
    m = np.zeros(gx.shape, dtype=bool)
    r2 = (width / 2) ** 2
    for (x1, y1), (x2, y2) in zip(pts[:-1], pts[1:]):
        dx, dy = x2 - x1, y2 - y1
        t = np.clip(((gx - x1) * dx + (gy - y1) * dy) / (dx * dx + dy * dy), 0, 1)
        m |= (gx - x1 - t * dx) ** 2 + (gy - y1 - t * dy) ** 2 <= r2
    return m


def save_png(name, arr, path):
    """arr: (h, w, 3) sRGB values 0..1, row 0 at the top"""
    h, w, _ = arr.shape
    img = bpy.data.images.new(name + '_paint', w, h, alpha=False)
    rgba = np.ones((h, w, 4), dtype=np.float32)
    rgba[..., :3] = arr[::-1]
    img.pixels.foreach_set(rgba.ravel())
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)


# ----------------------------------------------------------------------------- body texture
def paint_body():
    cv = Canvas(TS, TS, 8, rgb('purple'))
    x, y = cv.x, cv.y
    for i, name in enumerate(CELLS):
        cv.a[(x < CELL_W) & (y >= i * CELL_H) & (y < (i + 1) * CELL_H)] = rgb(name)
    # robe front: the green heartbeat trace on purple, drawn in front-view metres
    x0, y0, x1, y1 = ROBE
    area = (x >= x0) & (x < x1) & (y >= y0) & (y < y1)
    xm = (x - x0 - 1) / (x1 - x0 - 2) * 2 * ROBE_HALF_X - ROBE_HALF_X
    zm = Z_TRACE_TOP - (y - y0 - 1) / (y1 - y0 - 2) * (Z_TRACE_TOP - Z_TRACE_BOT)
    pts = [(PX(580) + fx * (PX(1078) - PX(580)), PZ(1396) + fy * (PZ(2018) - PZ(1396))) for fx, fy in TRACE]
    cv.a[area & stroke_mask(xm, zm, pts, TRACE_W)] = rgb('green')
    # horn tip: rows run along the horn (bottom of the area = HORN_SPLIT, top = under the ball), columns round it
    x0, y0, x1, y1 = TIP
    area = (x >= x0) & (x < x1) & (y >= y0) & (y < y1)
    f = (y1 - 2 - y) / (y1 - y0 - 4)                           # 0 at the split, 1 at the tip
    tooth = np.abs(((x - x0) / TIP_FACET) % 1.0 - 0.5) * 2      # 0 at a facet's middle, 1 at its edges
    fa = (HORN_T_PINK - HORN_SPLIT) / (1 - HORN_SPLIT)
    fb = (HORN_T_PALE - HORN_SPLIT) / (1 - HORN_SPLIT)
    amp = 0.085
    cv.a[area & (f > fa + amp * (1 - 2 * tooth))] = rgb('pink')         # teeth bite down into the purple
    cv.a[area & (f > fb + amp * (1 - 2 * tooth))] = rgb('pink_pale')
    return cv.result()


# ----------------------------------------------------------------------------- face textures
# One row per face card, in card order. Cards 1 and 5 to 11 are Katamari Forever's eight face expressions
# (1 is its neutral face); 2 to 4 are their own extras.
FACES = [
    ('face_1_neutral', dict(eyes='almond', mouth='trap', blush=False, tint=None)),
    ('face_2_humming', dict(eyes='arcs', mouth='round', blush=False, tint=None)),
    ('face_3_blink', dict(eyes='lines', mouth='trap', blush=False, tint=None)),
    ('face_4_surprised', dict(eyes='dots', mouth='trap_tall', blush=False, tint=None)),
    ('face_5_open', dict(eyes='almond', mouth='open', blush=False, tint=None)),
    ('face_6_smile', dict(eyes='almond', mouth='smile', blush=False, tint=None)),
    ('face_7_angry', dict(eyes='glare', mouth='peak', blush=False, tint=None)),
    ('face_8_blush_open', dict(eyes='almond', mouth='open', blush=True, tint=None)),
    ('face_9_pale', dict(eyes='almond', mouth='open_small', blush=False, tint='pale')),
    ('face_10_blush', dict(eyes='almond', mouth='trap', blush=True, tint=None)),
    ('face_11_blush_smile', dict(eyes='almond', mouth='smile', blush=True, tint=None)),
]
# the shield window with its yellow rim, right half: (half width, z) from the top corner down to the point
SHIELD = [(PX(1064), PZ(904)), (0.108, 0.764), (0.098, 0.722), (0.081, 0.690), (0.058, 0.664), (0.031, 0.642),
          (0.0, PZ(1268))]
SHIELD_DIP = 0.011                                 # the top edge dips this much in the middle
SHIELD_CZ = 0.725
CREAM_X, CREAM_TOP, CREAM_BOT = PX(1042), PZ(938), PZ(1244)
EYE_X, EYE_Z = PX(915.5), PZ(1038)
EYE_HALF_L, EYE_HALF_H = 22.5 * M_PER_PX, 14 * M_PER_PX      # sheet: each about 45 wide, rows 1024-1052
NOSE = [(0.0, PZ(1042)), (PX(810), PZ(1102)), (PX(846), PZ(1102))]
MOUTH_TOP, MOUTH_BOT = PZ(1116), PZ(1180)      # sheet: about rows 1110-1180; the top a texel lower, clear of the nose
MOUTH_HALF_NARROW, MOUTH_HALF_WIDE = 0.0094, PX(860)
BLUSH_X, BLUSH_Z, BLUSH_RX, BLUSH_RZ, BLUSH_MIX = 0.064, 0.706, 0.017, 0.012, 0.75
PALE_Z0, PALE_Z1 = 0.760, 0.728                    # the cool band holds down to Z0 and fades out by Z1


def shield_outline(scale_x=1.0, top=None, bot=None):
    """closed outline (x, z); the cream is the same shape pulled in by the rim"""
    def zz(z):
        if top is None:
            return z
        if z >= SHIELD_CZ:
            return SHIELD_CZ + (z - SHIELD_CZ) * (top - SHIELD_CZ) / (SHIELD[0][1] - SHIELD_CZ)
        return SHIELD_CZ + (z - SHIELD_CZ) * (SHIELD_CZ - bot) / (SHIELD_CZ - SHIELD[-1][1])
    right = [(w * scale_x, zz(z)) for w, z in SHIELD]
    left = [(-w, z) for w, z in right[-2::-1]]
    wt, zt = right[0]
    dip = SHIELD_DIP * (1.0 if top is None else 0.9)
    arc = [(-wt + 2 * wt * k / 12, zt - dip * (1 - (2 * k / 12 - 1) ** 2)) for k in range(1, 12)]
    return right + left + arc


def rbox_mask(xm, z, cz, hw, hh, r):
    ax, az = np.abs(xm), np.abs(z - cz)
    qx, qz = np.maximum(ax - (hw - r), 0), np.maximum(az - (hh - r), 0)
    return qx * qx + qz * qz <= r * r


def mouth_mask(kind, xm, z):
    n, w = MOUTH_HALF_NARROW, MOUTH_HALF_WIDE
    if kind == 'trap':          # red trapezoid, wide edge down (the reference's open mouth)
        return poly_mask(xm, z, [(-n, MOUTH_TOP), (n, MOUTH_TOP), (w, MOUTH_BOT), (-w, MOUTH_BOT)])
    if kind == 'trap_tall':     # the same, taller
        return poly_mask(xm, z, [(-n, MOUTH_TOP + 0.003), (n, MOUTH_TOP + 0.003), (w + 0.001, MOUTH_BOT - 0.012),
                                 (-w - 0.001, MOUTH_BOT - 0.012)])
    if kind == 'round':         # small round open mouth
        return xm ** 2 + (z - (MOUTH_TOP + MOUTH_BOT) / 2) ** 2 <= 0.0095 ** 2
    if kind == 'open':          # tall open mouth, as wide as the trapezoid
        return rbox_mask(xm, z, MOUTH_TOP + 0.002 - 0.0235, w, 0.0235, 0.005)
    if kind == 'open_small':
        return rbox_mask(xm, z, MOUTH_TOP - 0.0130, 0.0105, 0.0130, 0.004)
    if kind == 'smile':         # trapezoid, wide edge up
        return poly_mask(xm, z, [(-w, MOUTH_TOP), (w, MOUTH_TOP), (n, MOUTH_BOT), (-n, MOUTH_BOT)])
    if kind == 'peak':          # red triangle, point up
        return poly_mask(xm, z, [(0.0, MOUTH_TOP + 0.002), (w + 0.002, MOUTH_BOT), (-w - 0.002, MOUTH_BOT)])
    raise ValueError('unknown mouth %r' % kind)


def eye_mask(kind, xm, z, s):
    """one eye; s is -1 for their right eye (left in the image), 1 for their left"""
    cx = s * EYE_X
    if kind == 'almond':        # black almond with pointed ends
        return np.abs(z - EYE_Z) <= EYE_HALF_H * (1 - ((xm - cx) / EYE_HALF_L) ** 2)
    if kind == 'arcs':          # closed happy arcs, curving up
        pts = [(cx + q * 0.0115, EYE_Z - 0.0035 + 0.0075 * (1 - q * q)) for q in np.linspace(-1, 1, 13)]
        return stroke_mask(xm, z, pts, 0.0058)
    if kind == 'lines':         # closed, short straight lines
        return stroke_mask(xm, z, [(cx - 0.0105, EYE_Z), (cx + 0.0105, EYE_Z)], 0.0058)
    if kind == 'dots':          # round eyes
        return (xm - cx) ** 2 + (z - EYE_Z) ** 2 <= 0.0090 ** 2
    if kind == 'glare':         # steep glaring lids: half an almond under a hard edge that drops towards the nose
        th = math.radians(32.0)
        axx, axz = math.cos(th), s * math.sin(th)
        u = (xm - cx) * axx + (z - EYE_Z) * axz
        v = -(xm - cx) * axz + (z - EYE_Z) * axx
        return (v <= 0.0030) & (v >= 0.0030 - 0.0125 * (1 - (u / 0.0125) ** 2))
    raise ValueError('unknown eyes %r' % kind)


def paint_face(card):
    """card: 1 up, a row of FACES. The card is opaque and carries the whole window."""
    spec = FACES[card - 1][1]
    cv = Canvas(FS, FS, 8, rgb('purple'))
    xm = (cv.x / FS - 0.5) * 2 * FACE_X             # metres; the left of the image is their right side
    z = FACE_Z1 + (FACE_Z0 - FACE_Z1) * cv.y / FS
    cv.a[poly_mask(xm, z, shield_outline())] = rgb('yellow')
    cream = poly_mask(xm, z, shield_outline(CREAM_X / SHIELD[0][0], CREAM_TOP, CREAM_BOT))
    face = np.empty_like(cv.a)
    face[:] = rgb('cream')
    if spec['tint'] == 'pale':
        w = np.clip((z - PALE_Z1) / (PALE_Z0 - PALE_Z1), 0.0, 1.0)
        w = (w * w * (3 - 2 * w))[..., None]
        face[:] = rgb('pale_face') * (1 - w) + rgb('pale_band') * w
    elif spec['tint'] is not None:
        raise ValueError('unknown tint %r' % spec['tint'])
    if spec['blush']:
        for s in (-1, 1):
            r2 = ((xm - s * BLUSH_X) / BLUSH_RX) ** 2 + ((z - BLUSH_Z) / BLUSH_RZ) ** 2
            w = (BLUSH_MIX * np.exp(-r2 * r2))[..., None]
            face = face * (1 - w) + rgb('blush') * w
    cv.a[cream] = face[cream]
    cv.a[cream & poly_mask(xm, z, NOSE)] = rgb('nose')
    cv.a[cream & mouth_mask(spec['mouth'], xm, z)] = rgb('red')
    for s in (-1, 1):
        cv.a[cream & eye_mask(spec['eyes'], xm, z, s)] = rgb('eyes')
    return cv.result()


# ----------------------------------------------------------------------------- mesh builder
class Part:
    """Collects triangles. Vertices at the same place are shared. Each triangle is wound so that its
    normal points away from the current inside point, so no face comes out inside-out."""

    def __init__(self, name):
        self.name = name
        self.verts, self.weights, self.index = [], [], {}
        self.tris, self.uvs, self.smooth_flags = [], [], []
        self.inside = Vector((0, 0, 0))
        self.wfun = None
        self.smooth = False

    def v(self, co):
        key = (round(co[0], 6), round(co[1], 6), round(co[2], 6))
        i = self.index.get(key)
        if i is None:
            i = len(self.verts)
            self.index[key] = i
            self.verts.append(Vector(co))
            self.weights.append(dict(self.wfun(Vector(co))))
        return i

    def tri(self, cos, uv):
        a, b, c = (Vector(p) for p in cos)
        uvs = cell_uvs(uv, 3) if isinstance(uv, str) else list(uv)
        n = (b - a).cross(c - a)
        ctr = (a + b + c) / 3
        ref = self.inside(ctr) if callable(self.inside) else self.inside
        if n.dot(ctr - ref) < 0:
            b, c = c, b
            uvs = [uvs[0], uvs[2], uvs[1]]
        self.tris.append((self.v(a), self.v(b), self.v(c)))
        self.uvs.append(uvs)
        self.smooth_flags.append(self.smooth)

    def quad(self, cos, uv, flip=False):
        uvs = cell_uvs(uv, 4) if isinstance(uv, str) else list(uv)
        order = ((1, 2, 3), (1, 3, 0)) if flip else ((0, 1, 2), (0, 2, 3))
        for o in order:
            self.tri([cos[i] for i in o], [uvs[i] for i in o])

    def fan(self, cos, uv):
        uvs = cell_uvs(uv, len(cos)) if isinstance(uv, str) else list(uv)
        for i in range(1, len(cos) - 1):
            self.tri([cos[0], cos[i], cos[i + 1]], [uvs[0], uvs[i], uvs[i + 1]])

    def band(self, ra, rb, uv):
        """closed band between two rings with the same number of points; uv may be a cell name or a function of
        the face index giving a cell name or four UVs"""
        n = len(ra)
        for i in range(n):
            j = (i + 1) % n
            cos = [ra[i], ra[j], rb[j], rb[i]]
            u = uv(i, cos) if callable(uv) else uv
            self.quad(cos, u, flip=(i % 2 == 1))


def cell_uvs(name, n):
    """a small patch inside one flat colour cell"""
    cx, cy = CELL_W / 2, CELLS.index(name) * CELL_H + CELL_H / 2
    return [px(cx + 1.5 * math.cos(2 * math.pi * j / n + 0.7), cy + 1.5 * math.sin(2 * math.pi * j / n + 0.7)) for j in range(n)]


def icosa(part, centre, r, frame, uv):
    """20-face ball (icosahedron) with two corners on the frame's axis: the roundest ball for its triangle count"""
    u1, u2, ax = (Vector(f) for f in frame)
    c = Vector(centre)
    h, rr = r / math.sqrt(5), 2 * r / math.sqrt(5)
    a = [c + ax * h + (u1 * math.cos(math.radians(72 * i)) + u2 * math.sin(math.radians(72 * i))) * rr for i in range(5)]
    b = [c - ax * h + (u1 * math.cos(math.radians(72 * i + 36)) + u2 * math.sin(math.radians(72 * i + 36))) * rr for i in range(5)]
    keep, keep_s = part.inside, part.smooth
    part.inside, part.smooth = c, True
    for i in range(5):
        j = (i + 1) % 5
        part.tri([c + ax * r, a[i], a[j]], uv)
        part.tri([a[i], b[i], a[j]], uv)
        part.tri([b[i], b[j], a[j]], uv)
        part.tri([c - ax * r, b[j], b[i]], uv)
    part.inside, part.smooth = keep, keep_s


def ring8(z, a, c, yf, ysf, ysb, cb, yb):
    """8 points: 0-1 their right side, 2-3 front, 4-5 their left side, 6-7 back"""
    return [Vector(q) for q in ((-a, ysb, z), (-a, ysf, z), (-c, yf, z), (c, yf, z), (a, ysf, z), (a, ysb, z),
                                (cb, yb, z), (-cb, yb, z))]


def oct_ring(z, a):
    b = a * B_RATIO
    return ring8(z, a, a * T8, -b, -b * T8, b * T8, a * T8, b)


# ----------------------------------------------------------------------------- head
def hood_front_y(z):
    """y of the hood's front facet at height z (between the chin ring and the brow ring)"""
    for ra, rb in zip(HOOD_RINGS[1:-1], HOOD_RINGS[2:]):
        if ra[0] <= z <= rb[0]:
            return ra[3] + (z - ra[0]) / (rb[0] - ra[0]) * (rb[3] - ra[3])
    raise ValueError('z %.4f is off the hood front' % z)


def build_head():
    p = Part('head')
    p.wfun = lambda co: {'JNT_head': 1.0}
    rings = [ring8(*r) for r in HOOD_RINGS]
    p.inside = lambda c: Vector((0.0, 0.0, min(max(c.z, 0.62), 0.80)))
    p.fan(rings[0], 'purple')                       # closed underneath, inside the robe
    for ra, rb in zip(rings[:-1], rings[1:]):
        p.band(ra, rb, 'purple')
    brow = rings[-1]
    # the low dome in the notch between the horns: forehead, back, and four faces up to the top
    nf = {s: Vector((s * NOTCH_X, NOTCH_YF, NOTCH_Z)) for s in (1, -1)}
    nb = {s: Vector((s * NOTCH_X, NOTCH_YB, NOTCH_Z)) for s in (1, -1)}
    top = Vector(HOOD_TOP)
    p.inside = Vector((0.0, 0.01, 0.85))
    p.quad([brow[2], brow[3], nf[1], nf[-1]], 'purple')
    p.quad([brow[6], brow[7], nb[-1], nb[1]], 'purple')
    for a, b in ((nf[-1], nf[1]), (nf[1], nb[1]), (nb[1], nb[-1]), (nb[-1], nf[-1])):
        p.tri([a, b, top], 'purple')
    # horns
    c0 = ((HORN_O0[0] + HORN_I0[0]) / 2, (HORN_O0[1] + HORN_I0[1]) / 2)     # centre line, front view (x, z)
    c1 = ((HORN_O1[0] + HORN_I1[0]) / 2, (HORN_O1[1] + HORN_I1[1]) / 2)
    L = math.hypot(c1[0] - c0[0], c1[1] - c0[1])
    AX = ((c1[0] - c0[0]) / L, (c1[1] - c0[1]) / L)
    y0c = (brow[3].y + brow[6].y) / 2
    ty = (c1[1] - HORN_O0[1]) * HORN_LEAN            # the tip's lean back
    d = HORN_TIP_HALF_D
    x0, y0, x1, y1 = TIP
    for side in (1, -1):
        # six edges, in this order: outer front, outer back, back, inner back, inner front, front
        if side > 0:
            root = [brow[4], brow[5], brow[6], nb[1], nf[1], brow[3]]
        else:
            root = [brow[1], brow[0], brow[7], nb[-1], nf[-1], brow[2]]
        tip = [Vector((side * HORN_O1[0], ty - d * 0.5, HORN_O1[1])), Vector((side * HORN_O1[0], ty + d * 0.5, HORN_O1[1])),
               Vector((side * c1[0], ty + d, c1[1])),
               Vector((side * HORN_I1[0], ty + d * 0.5, HORN_I1[1])), Vector((side * HORN_I1[0], ty - d * 0.5, HORN_I1[1])),
               Vector((side * c1[0], ty - d, c1[1]))]

        def along(q):               # how far along the horn's centre line a point is, 0 at the root's middle, 1 at the tip
            return ((side * q.x - c0[0]) * AX[0] + (q.z - c0[1]) * AX[1]) / L

        def centre(q):              # the centre line's point beside q
            t = min(max(along(q), 0.08), 0.95)
            return Vector((side * (c0[0] + (c1[0] - c0[0]) * t), y0c + (ty - y0c) * t, c0[1] + (c1[1] - c0[1]) * t))
        # the split ring crosses the horn squarely, so the pink borders above it do too
        split = []
        for r, t in zip(root, tip):
            f = (HORN_SPLIT - along(r)) / (along(t) - along(r))
            split.append(r + (t - r) * f)
        p.inside = centre
        p.band(root, split, 'purple')

        def tip_uv(k, cos):
            ua, ub = x0 + 8 + k * TIP_FACET, x0 + 8 + (k + 1) * TIP_FACET
            return [px(ua, y1 - 2), px(ub, y1 - 2), px(ub, y0 + 2), px(ua, y0 + 2)]
        p.band(split, tip, tip_uv)
        p.fan(tip, 'pink_pale')                     # closed under the ball
        bz = BALL_Z
        icosa(p, (side * BALL_X, (bz - HORN_O0[1]) * HORN_LEAN, bz), BALL_R, ((1, 0, 0), (0, 1, 0), (0, 0, 1)), 'red')
    return p


def build_face(name):
    """face card: a strip of 6 vertices (4 triangles) just in front of the hood's front facet, bent at the fold
    and tapering to the chin with the hood. UVs are front-view metres, so the painting is not skewed."""
    p = Part(name)
    p.wfun = lambda co: {'JNT_head': 1.0}
    p.inside = Vector((0, 0.3, 0.71))
    p.smooth = True                                 # shaded across the fold, so the cream does not break there
    e = 0.5 / FS

    def uv(x, z):
        u = 0.5 + x / (2 * FACE_X)
        v = (z - FACE_Z0) / (FACE_Z1 - FACE_Z0)
        return (e + u * (1 - 2 * e), e + v * (1 - 2 * e))
    rows = [(z, hw, hood_front_y(z) - g) for (z, hw), g in zip(CARD_ROWS, CARD_GAP)]
    for (za, wa, ya), (zb, wb, yb) in zip(rows[:-1], rows[1:]):
        cos = [(-wa, ya, za), (wa, ya, za), (wb, yb, zb), (-wb, yb, zb)]
        p.quad(cos, [uv(-wa, za), uv(wa, za), uv(wb, zb), uv(-wb, zb)])
    return p


# ----------------------------------------------------------------------------- body
def body_weights(co):
    z = co.z
    if z > 0.60:
        return {'JNT_spine_02': 0.60, 'JNT_neck': 0.40}
    if z > 0.55:
        return {'JNT_spine_01': 0.25, 'JNT_spine_02': 0.75}
    if z > 0.42:
        return {'JNT_waist': 0.25, 'JNT_spine_01': 0.60, 'JNT_spine_02': 0.15}
    # the long robe: from the waist at the top to the near thigh at the hem; the corners beside the middle line
    # (front and back) also take a little of the other thigh, the side corners none
    f = min(max((LEG_BLEND_TOP - z) / (LEG_BLEND_TOP - LEG_BLEND_BOT), 0.0), 1.0)
    if abs(co.x) < 1e-5:
        w = {'JNT_leg_L': 0.25 * f, 'JNT_leg_R': 0.25 * f}
    else:
        own, other = ('JNT_leg_L', 'JNT_leg_R') if co.x > 0 else ('JNT_leg_R', 'JNT_leg_L')
        w = {own: OWN_SIDE * f} if abs(co.y) < abs(co.x) else {own: OWN_MID * f, other: OTHER_MID * f}
    w = {k: v for k, v in w.items() if v > 1e-4}
    w['JNT_waist'] = 1.0 - sum(w.values())
    return w


OWN_SIDE, OWN_MID, OTHER_MID = 0.80, 0.62, 0.13


def build_body():
    p = Part('body')
    p.wfun = body_weights
    p.inside = lambda c: Vector((0.0, 0.0, min(max(c.z, 0.20), 0.55)))
    robe_top = ring8(*ROBE_TOP)
    rings = [ring8(*SHOULDER)] + [oct_ring(z, a) for z, a in ROBE_PROFILE[1:]]
    # the top: closed across (the hood sits on it and reaches a little inside), then straight out and down to the
    # shoulders
    keep = p.inside
    p.inside = Vector((0, 0, 0.5))
    p.fan(robe_top, 'purple_shade')
    p.inside = keep
    p.band(robe_top, rings[0], 'purple')

    def robe_front(k, cos):         # the three facets seen from the front carry the trace, by front projection
        return [robe_uv(c) for c in cos] if k in (1, 2, 3) else 'purple'
    for i in range(len(rings) - 1):
        ra, rb = rings[i], rings[i + 1]
        if rb[0].z >= Z_TRACE_BOT - 1e-6:
            p.band(ra, rb, robe_front)
        elif i == len(rings) - 2:
            p.band(ra, rb, 'pink')                  # the turned-out hem lip
        else:
            p.band(ra, rb, 'purple')
    # pink underside: a shallow cone up into the bell
    apex = Vector(UNDER_APEX)
    p.inside = Vector((0, 0, 0.30))
    lip = rings[-1]
    for k in range(8):
        p.tri([lip[k], lip[(k + 1) % 8], apex], 'pink')
    return p


# ----------------------------------------------------------------------------- arms and legs
def build_arm(side):
    sfx = 'L' if side > 0 else 'R'
    arm, elbow, hand = 'JNT_arm_' + sfx, 'JNT_elbow_' + sfx, 'JNT_hand_' + sfx
    p = Part('arm_' + sfx)
    ring_w = [{arm: 1.0}, {arm: 0.5, elbow: 0.5}, {hand: 1.0}]
    rings = []
    for i, (x, h) in enumerate(ARM_RINGS):
        p.wfun = lambda co, w=ring_w[i]: w
        pts = [Vector((side * x, -h, ARM_ZC + h)), Vector((side * x, h, ARM_ZC + h)), Vector((side * x, h, ARM_ZC - h)), Vector((side * x, -h, ARM_ZC - h))]
        for q in pts:
            p.v(q)
        rings.append(pts)
    p.inside = lambda c: Vector((c.x, 0.0, ARM_ZC))
    for ra, rb in zip(rings[:-1], rings[1:]):
        p.band(ra, rb, 'purple')
    keep = p.inside
    p.inside = Vector((side * ARM_RINGS[1][0], 0.0, ARM_ZC))
    p.quad(rings[0], 'purple')                      # closed at the shoulder, inside the robe
    p.inside = keep
    p.wfun = lambda co: {hand: 1.0}
    icosa(p, (side * HAND_X, 0.0, ARM_ZC), HAND_R, ((0, 1, 0), (0, 0, 1), (side, 0, 0)), 'purple')    # mitten hand
    return p


def build_leg(side):
    sfx = 'L' if side > 0 else 'R'
    leg, knee, ankle = 'JNT_leg_' + sfx, 'JNT_knee_' + sfx, 'JNT_ankle_' + sfx
    p = Part('leg_' + sfx)
    x0 = side * LEG_X
    ring_w = [{leg: 1.0}, {leg: 0.5, knee: 0.5}, {ankle: 1.0}]
    rings = []
    for i, (z, h) in enumerate(LEG_RINGS):
        p.wfun = lambda co, w=ring_w[i]: w
        pts = [Vector((x0 - h, -h, z)), Vector((x0 + h, -h, z)), Vector((x0 + h, h, z)), Vector((x0 - h, h, z))]
        for q in pts:
            p.v(q)
        rings.append(pts)
    p.inside = lambda c: Vector((x0, 0.0, c.z))
    for ra, rb in zip(rings[:-1], rings[1:]):
        p.band(ra, rb, 'slate')
    p.inside = Vector((x0, 0.0, 0.2))
    p.quad(rings[0], 'slate')                       # closed at the hip, inside the robe
    # pointed slate shoe, pointing forward and out at 45 degrees
    p.wfun = lambda co: {ankle: 1.0}
    d = Vector((side * math.sqrt(0.5), -math.sqrt(0.5), 0.0))
    n = Vector((side * math.sqrt(0.5), math.sqrt(0.5), 0.0))
    o = Vector((x0, 0.0, 0.0))
    heel, toe = o - d * SHOE_HEEL, o + d * SHOE_TOE
    s1, s2 = o + n * SHOE_HALF_W, o - n * SHOE_HALF_W
    top = o + d * 0.002 + Vector((0, 0, SHOE_H))
    p.inside = o + Vector((0, 0, 0.012))
    for a, b in ((heel, s1), (s1, toe), (toe, s2), (s2, heel)):
        p.tri([a, b, top], 'slate')
    p.tri([heel, s1, toe], 'slate')
    p.tri([heel, toe, s2], 'slate')
    return p


# ----------------------------------------------------------------------------- objects
def make_material(name, path):
    m = bpy.data.materials.new(name)
    if m.node_tree is None:             # older Blender versions; newer ones always give a material its nodes
        m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    bsdf = nt.nodes.new('ShaderNodeBsdfPrincipled')
    bsdf.inputs['Roughness'].default_value = 0.85
    if 'Specular IOR Level' in bsdf.inputs:
        bsdf.inputs['Specular IOR Level'].default_value = 0.1
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(path, check_existing=True)
    tex.interpolation = 'Linear'
    nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    nt.links.new(bsdf.outputs[0], out.inputs[0])
    m.use_backface_culling = True       # as in a game: a face seen from behind is not drawn
    return m


def make_object(part, mat, rig):
    me = bpy.data.meshes.new(part.name)
    me.from_pydata([tuple(v) for v in part.verts], [], part.tris)
    me.update()
    uv = me.uv_layers.new(name='UVMap')
    for poly, uvs, smooth in zip(me.polygons, part.uvs, part.smooth_flags):
        poly.use_smooth = smooth        # flat facets everywhere; only the balls and the mitten hands are smooth
        for li, co in zip(poly.loop_indices, uvs):
            uv.data[li].uv = co
    me.materials.append(mat)
    me.validate()
    ob = bpy.data.objects.new(part.name, me)
    bpy.context.scene.collection.objects.link(ob)
    for i, w in enumerate(part.weights):
        for bone, weight in w.items():
            vg = ob.vertex_groups.get(bone) or ob.vertex_groups.new(name=bone)
            vg.add([i], weight, 'REPLACE')
    ob.parent = rig
    ob.matrix_parent_inverse = RIG_TURN.inverted()   # the mesh stays where it was built, under the turned rig
    mod = ob.modifiers.new('Armature', 'ARMATURE')
    mod.object = rig
    return ob


# OUAK's cousin joints rest with identity local rotations, world-aligned. The FBX exporter writes the rig object as a
# Null under its Z-up to Y-up conversion, Rx(-90), and each bone as its rest matrix in rig space. So the rig object is
# turned +90 degrees about X, which cancels that conversion, and every bone lies along the rig's +Y with roll 0, which
# makes its rest matrix identity. In the viewport every bone points straight up.
RIG_TURN = Matrix.Rotation(math.pi / 2, 4, 'X')        # rig space -> world


def build_rig():
    with open(SPEC_PATH, 'r', encoding='utf-8') as f:
        joints = json.load(f)['old_style']['joints']
    kids = {n: [c for c in joints if joints[c]['parent'] == n] for n in joints}
    data = bpy.data.armatures.new('Circuit_rig')
    rig = bpy.data.objects.new('model', data)              # OUAK clips bind paths 'model/JNT_root/...'
    rig.rotation_euler = (math.pi / 2, 0.0, 0.0)          # RIG_TURN
    bpy.context.scene.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='EDIT')
    to_rig = RIG_TURN.inverted()

    def add(name):
        j = joints[name]
        head = Vector(j['head'])
        if len(kids[name]) == 1:        # as long as the gap to the single child
            length = (Vector(joints[kids[name][0]]['head']) - head).length
        else:                           # no child or several: a short stub
            length = 0.04
        eb = data.edit_bones.new(name)
        eb.head = to_rig @ head
        eb.tail = eb.head + Vector((0, max(length, 0.01), 0))
        eb.roll = 0.0
        if j['parent']:
            eb.parent = data.edit_bones[j['parent']]
            eb.use_connect = False
        for c in kids[name]:
            add(c)
    for n in joints:
        if joints[n]['parent'] is None:
            add(n)
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()
    for b in data.bones:                # every rest rotation identity, every joint at its spec position
        assert all(abs(b.matrix_local[r][c] - (r == c)) < 1e-6 for r in range(3) for c in range(3)), b.name
        assert ((rig.matrix_world @ b.head_local) - Vector(joints[b.name]['head'])).length < 1e-6, b.name
    return rig


def set_pose(rig, turns):
    """turns: {joint: (axis, degrees)} about the cousin's own fixed axes (X = their left, Z = up); {} clears it"""
    for pb in rig.pose.bones:
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.location = (0, 0, 0)
        pb.scale = (1, 1, 1)
        if pb.name in turns:
            ax, deg = turns[pb.name]
            rest = (rig.matrix_world @ pb.bone.matrix_local).to_3x3().normalized()
            pb.rotation_quaternion = (rest.inverted() @ Matrix.Rotation(math.radians(deg), 3, ax) @ rest).to_quaternion()
    bpy.context.view_layer.update()


def scrub_private_paths():
    """Blender's default workspaces carry a file browser that remembers the home folder of whoever runs the
    script; a saved .blend would hold that path. Overwrite the whole text buffer, then blank it, so the file
    names nobody (setting it to nothing alone would leave the rest of the old path behind)."""
    n = 0
    for screen in bpy.data.screens:
        for area in screen.areas:
            for space in area.spaces:
                if space.type == 'FILE_BROWSER' and space.params is not None:
                    params = space.params
                    params.directory = b'/' * params.bl_rna.properties['directory'].length_max
                    params.directory = b''
                    n += 1
    return n


def leak_check(paths):
    """look for the name and home folder of whoever runs the build inside the files just written"""
    home = os.path.expanduser('~')
    words = {os.path.basename(home.rstrip('/' + os.sep)), home, home.replace(os.sep, '/')}
    hits = []
    for path in paths:
        with open(path, 'rb') as f:
            data = f.read()
        for w in words:
            if len(w) >= 3 and (w.encode('utf-8') in data or w.encode('utf-16-le') in data):
                hits.append(os.path.basename(path))
    return sorted(set(hits))


# ----------------------------------------------------------------------------- main
def main():
    C.clean()
    os.makedirs(TEX_DIR, exist_ok=True)
    os.makedirs(REN_DIR, exist_ok=True)
    bpy.context.preferences.filepaths.save_version = 0

    # 1 textures
    body_png = os.path.join(TEX_DIR, 'circuit_old_body.png')
    save_png('circuit_old_body', paint_body(), body_png)
    face_png = []
    for i in range(1, len(FACES) + 1):
        face_png.append(os.path.join(TEX_DIR, 'circuit_old_face_%d.png' % i))
        save_png('circuit_old_face_%d' % i, paint_face(i), face_png[-1])

    # 2 materials
    body_mat = make_material('circuit_old_body', body_png)
    face_mats = [make_material('circuit_old_face_%d' % (i + 1), q) for i, q in enumerate(face_png)]

    # 3 rig, 4 meshes and weights
    rig = build_rig()
    parts = [build_head(), build_body(), build_arm(1), build_arm(-1), build_leg(1), build_leg(-1)]
    objs = [make_object(q, body_mat, rig) for q in parts]
    for i, (n, _) in enumerate(FACES):
        ob = make_object(build_face(n), face_mats[i], rig)
        ob.hide_render = i > 0          # only the neutral face renders
        ob.hide_viewport = i > 0        # and only it shows on import: the FBX keeps this flag, not hide_render
        objs.append(ob)
    total = 0
    for ob in objs:
        n = len(ob.data.polygons)
        total += n
        print('TRIS %-20s %4d   verts %4d' % (ob.name, n, len(ob.data.vertices)))
    print('TRIS total %d' % total)
    dims = [(min(min(v.co[i] for v in ob.data.vertices) for ob in objs), max(max(v.co[i] for v in ob.data.vertices) for ob in objs)) for i in range(3)]
    print('SIZE x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f' % (dims[0] + dims[1] + dims[2]))

    # 5 renders: rest pose with the neutral face, then a stride (their left leg 40 degrees forward, their right
    # 40 back, knees bent 30) to show the robe following the thighs
    C.render_views(os.path.join(REN_DIR, 'Circuit_old'), views=('front', 'side', 'back'))
    set_pose(rig, {'JNT_leg_L': ('X', -40), 'JNT_leg_R': ('X', 40), 'JNT_knee_L': ('X', 30), 'JNT_knee_R': ('X', 30)})
    C.render_views(os.path.join(REN_DIR, 'Circuit_old_stride'), views=('side', 'three'))
    set_pose(rig, {})

    # 6 clean up and save
    sc = bpy.context.scene
    for ob in [o for o in sc.objects if o.type in {'CAMERA', 'LIGHT'}]:
        bpy.data.objects.remove(ob)
    for coll in (bpy.data.cameras, bpy.data.lights, bpy.data.worlds):
        for d in list(coll):
            coll.remove(d)
    sc.render.filepath = '//'
    blend = os.path.join(HERE, 'Circuit_old.blend')
    fbx = os.path.join(HERE, 'Circuit_old.fbx')
    # FBX first, while the scene has no file path: the FBX header stores the .blend's path. The exporter also
    # writes each texture's absolute path beside its relative one; hand it the relative path for both.
    assert bpy.data.filepath == '', 'export the FBX before the .blend is saved'
    import io_scene_fbx.export_fbx_bin as fbxbin
    _orig_vid_path = fbxbin._gen_vid_path

    def _relative_only(img, scene_data):
        fname_abs, fname_rel = _orig_vid_path(img, scene_data)
        return fname_rel, fname_rel
    fbxbin._gen_vid_path = _relative_only
    try:
        bpy.ops.export_scene.fbx(filepath=fbx, use_selection=False, object_types={'ARMATURE', 'MESH'},
                                 add_leaf_bones=False, bake_anim=False, path_mode='RELATIVE', axis_forward='-Z', axis_up='Y',
                                 apply_scale_options='FBX_SCALE_ALL')   # every node at scale 1; units in the file header
    finally:
        fbxbin._gen_vid_path = _orig_vid_path
    print('SCRUB file browser folders blanked: %d' % scrub_private_paths())
    bpy.ops.wm.save_as_mainfile(filepath=blend, compress=False)
    bpy.ops.file.make_paths_relative()
    scrub_private_paths()
    bpy.ops.wm.save_mainfile(compress=False)
    leaks = leak_check([blend, fbx] + [os.path.join(TEX_DIR, f) for f in sorted(os.listdir(TEX_DIR))])
    print('LEAK check (name or home folder of whoever ran the build, inside the written files): %s' % (', '.join(leaks) if leaks else 'none'))
    print('DONE')


main()
