"""Burda, old style (the way Katamari Forever built its cousins).

Builds everything from an empty scene: textures, materials, meshes, the rig from
../burda_rig_spec.json (block old_style), weights, renders, Burda_old.blend and Burda_old.fbx.

    blender -b --factory-startup --python build_burda_old.py

Units: Blender metres, Z up, Burda faces -Y, +X is her left.
Every path is resolved from this file. Geometry and textures are made here from numbers only.
"""
import bpy, os, sys, json, math
import numpy as np
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

HERE = os.path.dirname(os.path.abspath(__file__))
TEX_DIR = os.path.join(HERE, 'textures')
REN_DIR = os.path.join(HERE, 'renders')
SPEC_PATH = os.path.normpath(os.path.join(HERE, '..', 'burda_rig_spec.json'))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
sys.dont_write_bytecode = True                 # no __pycache__ next to the deliverables
import bl_common as C

T8 = math.tan(math.radians(22.5))          # octagon: half side / half width

# ----------------------------------------------------------------------------- palette (design sheet)
PAL = {
    'skin': '633E1A', 'skin_shade': '3F250E', 'hair': '110B0A', 'hair_sheen': '3D1F07',
    'silver_light': 'E6E4E1', 'silver_mid': 'A09C99', 'silver_shade': '877D74', 'grille_fill': '362C2B',
    'antenna_cone': 'FEF404', 'antenna_ball': 'F3131B', 'dress': '7C0F68', 'dress_light': 'B71B89',
    'trim': 'EAA7C7', 'bead_shade': 'BF7787', 'lips': 'DF151F', 'nose': 'E89127',
    'dots': 'F5D5E5', 'grille_line': '110B0A', 'eyes': '110B0A',
    # face cards only: the blush mixed into her cheeks, and the pale card's cool band and ashen lower face
    'blush': 'E8607E', 'pale_band': '5C7682', 'pale_skin': '8A7868',
}
# the column of flat colour cells on the left of the body texture, top to bottom
CELLS = ['skin', 'skin_shade', 'hair', 'hair_sheen', 'silver_light', 'silver_mid', 'silver_shade', 'grille_fill',
         'antenna_cone', 'antenna_ball', 'dress', 'dress_light', 'trim', 'bead_shade', 'lips', 'nose']


def rgb(name):
    h = PAL[name]
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64) / 255.0


# ----------------------------------------------------------------------------- sizes
# Head: Forever Prince scaled to 1.055 (brief table); layout along X from the design sheet.
HEAD_W = 0.663
HEAD_ZMIN, HEAD_ZMAX = 0.629, 0.922
HEAD_H = HEAD_ZMAX - HEAD_ZMIN                     # 0.293 = 0.44 W
HEAD_CZ = (HEAD_ZMIN + HEAD_ZMAX) / 2
HEAD_RZ = (HEAD_H / 2) / math.cos(math.radians(22.5))
HEAD_FRONT = 0.154                                 # front face plane; the card plane (rig spec) is y -0.156
HEAD_RY = HEAD_FRONT / math.cos(math.radians(22.5))
PHI = [22.5 + 45 * k for k in range(8)]            # ring corner angles, from front (-Y) towards up
# faces between corners k and k+1: 0 upper front, 1 top, 2 upper back, 3 back, 4 lower back, 5 bottom,
# 6 lower front (the lip), 7 front.  The face card lies on the front face and folds back over the upper
# front face, the way Forever's face cards bend round their octagonal heads.
X_WIN = 0.35 * HEAD_W / 2                          # face window 0.35 W wide
X_SLEEVE = 0.25 * HEAD_W                           # sleeve is the middle 0.5 W; the rest is grille
X_END = HEAD_W / 2
GRILLE_RINGS = [(0.0, 1.0), (0.40, 1.0), (0.70, 0.86), (0.90, 0.62), (1.0, 0.36)]   # (fraction of length, radius factor)
CARD_W = 2 * X_WIN
CARD_ZTOP = HEAD_ZMAX - 0.012                      # the card runs from just under the top of the head ...
CARD_ZBOT = HEAD_CZ - HEAD_RZ * math.sin(math.radians(22.5))    # ... down to 0.71 H (sheet: 0.69 H); the lip is the face below
CARD_ZFOLD = HEAD_CZ + HEAD_RZ * math.sin(math.radians(22.5))   # the corner between the front and upper front faces
CARD_GAP = (0.0020, 0.0035, 0.0015)                # how far in front of the head (along -Y) the card's top, fold and
                                                   # bottom rows stand; the fold row stands furthest, clear of the corner
ANT_Y = -0.0247                                    # the antenna stands on the antenna joint (rig spec)
ANT_CONE_R, ANT_CONE_H = 0.075 * HEAD_W / 2, 0.27 * HEAD_H     # cone base 0.075 W, height 0.27 H
ANT_BALL_R = 0.0335                                # ball 0.10 W (sheet: 0.11 W), kept a little small to stay about 1.05 tall
ANT_BALL_Z = HEAD_ZMAX + 0.071 + ANT_BALL_R        # the cone tip ends just inside the ball; top of the ball at 1.060

# face features, front view (design sheet fractions of W and H, measured from the top of the head)
EYE_X = 0.17 * HEAD_W / 2
EYE_Z = HEAD_ZMAX - 0.28 * HEAD_H
NOSE_HALF_W = 0.04 * HEAD_W / 2
NOSE_ZTOP = HEAD_ZMAX - 0.305 * HEAD_H
NOSE_ZBOT = NOSE_ZTOP - 0.13 * HEAD_H
NOSE_OUT = 0.030                                   # how far the 3D nose sticks out (Forever: about 0.033)
LIPS_Z = HEAD_ZMAX - 0.5 * HEAD_H
LIPS_HALF_W = 0.065 * HEAD_W / 2
LIPS_HALF_H = 0.045 * HEAD_H / 2
LIPS_TOP = LIPS_Z + LIPS_HALF_H                    # the open mouths, smile and angry mouth hang from the bar's top edge

# Body
B_RATIO = 0.82                                     # depth / width (Forever Prince body 0.264 / 0.323)
Z_HEM = 0.205                                      # dress ends about the knee (knee joint z 0.181)
BODY_PROFILE = [(Z_HEM, 0.172), (0.335, 0.140), (0.455, 0.114), (0.586, 0.106)]   # (z, half width); top = 0.32 W
NECK_PEAK, NECK_SIDE, NECK_BACK, NECK_DIP = 0.586, 0.570, 0.573, 0.556            # sweetheart neckline heights
TRIM_H = 0.013
SHOULDER = (0.615, 0.088, 0.066)                   # z, half width, half depth
NECK_TOP = (0.675, 0.040, 0.040)                   # ends inside the head

# Limbs
ARM_ZC = 0.6105                                    # centre line a little under the arm joints so the arm clears the head
ARM_RINGS = [(0.070, 0.0160), (0.222, 0.0145), (0.390, 0.0125)]     # (x, half thickness); elbow joint x 0.222
HAND_X, HAND_R = 0.398, 0.030
LEG_X = 0.05233
LEG_RINGS = [(0.3228, 0.0165), (0.181, 0.0145), (0.030, 0.0130)]    # (z, half thickness); knee joint z 0.181
SHOE_Y, SHOE_RX, SHOE_RY, SHOE_H = -0.008, 0.033, 0.031, 0.058     # about as deep as wide, like a Forever foot

# ----------------------------------------------------------------------------- body texture layout (128 x 128, y down)
TS = 128
CELL_W, CELL_H = 16, 8
DRESS = (16, 0, 128, 80)        # x0, y0, x1, y1: magenta with dots and sparkles
GRILLE = (16, 80, 96, 128)      # dark with the diamond crosshatch
LIPSW = (96, 80, 128, 128)      # two-tone silver swatch: the lip under the face window, the collars' inner edge
GRILLE_P = 16                   # crosshatch period in texels
GRILLE_M_PER_P = 0.0775         # one period on the model: 12 diamonds round the head, 6 across the half seen from the front
DRESS_U0, DRESS_USPAN = 17.0, 110.0     # the dress pattern repeats every 110 texels (half way round the body)
DRESS_VTOP, DRESS_VBOT = 1.0, 79.0
FS = 64                         # face texture size


def px(x, y):
    """texel position (y down) -> UV"""
    return (x / TS, 1.0 - y / TS)


# octagon face widths for a unit ring: side, diagonal, front
_WS, _WD, _WF = 2 * B_RATIO * T8, (1 - T8) * math.hypot(1, B_RATIO), 2 * T8
_HALF = _WS + 2 * _WD + _WF
DRESS_CUM = [0.0, _WS / _HALF, (_WS + _WD) / _HALF, (_WS + _WD + _WF) / _HALF, 1.0]
DRESS_UC = (DRESS_CUM[2] + DRESS_CUM[3]) / 2       # u of the middle of the front face


def dress_uv(u, z):
    v = (z - Z_HEM) / (NECK_PEAK - Z_HEM)
    return px(DRESS_U0 + DRESS_USPAN * u, DRESS_VBOT - (DRESS_VBOT - DRESS_VTOP) * v)


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


def cat_rom(pts, n=10):
    """smooth open curve through the points"""
    p = [np.array(q, dtype=float) for q in pts]
    e = [2 * p[0] - p[1]] + p + [2 * p[-1] - p[-2]]
    out = []
    for i in range(len(p) - 1):
        p0, p1, p2, p3 = e[i], e[i + 1], e[i + 2], e[i + 3]
        for j in range(n):
            t = j / n
            out.append(0.5 * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (3 * p1 - p0 - 3 * p2 + p3) * t ** 3))
    out.append(p[-1])
    return [(float(q[0]), float(q[1])) for q in out]


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
    cv = Canvas(TS, TS, 8, rgb('grille_fill'))
    x, y = cv.x, cv.y
    # flat colour cells
    for i, name in enumerate(CELLS):
        cv.a[(x < CELL_W) & (y >= i * CELL_H) & (y < (i + 1) * CELL_H)] = rgb(name)
    # dress: magenta, two light stripes, white dots and four-point sparkles
    x0, y0, x1, y1 = DRESS
    area = (x >= x0) & (x < x1) & (y >= y0) & (y < y1)
    cv.a[area] = rgb('dress')
    z_row = Z_HEM + (DRESS_VBOT - y) / (DRESS_VBOT - DRESS_VTOP) * (NECK_PEAK - Z_HEM)      # model height of each row
    a_row = np.interp(z_row, [p[0] for p in BODY_PROFILE], [p[1] for p in BODY_PROFILE])
    hpm = DRESS_USPAN / (_HALF * a_row)             # texels per metre across, changes with the dress width
    vpm = (DRESS_VBOT - DRESS_VTOP) / (NECK_PEAK - Z_HEM)                                   # texels per metre up
    for ua, ub in ((DRESS_UC - 0.3400, DRESS_UC - 0.2800), (DRESS_UC + 0.2800, DRESS_UC + 0.3400)):
        for m in (-1, 0, 1):
            sx0, sx1 = DRESS_U0 + DRESS_USPAN * (ua + m), DRESS_U0 + DRESS_USPAN * (ub + m)
            cv.a[area & (x >= sx0) & (x < sx1)] = rgb('dress_light')
    rows_z = [0.545, 0.487, 0.429, 0.371, 0.313, 0.255]
    # 'D' dot, 'S' sparkle; rows 1, 3, 5 from the top sit near the edges of the front face, rows 2, 4, 6 on its middle
    kinds = ['DDSD', 'DDDS', 'DSDD', 'DDSD', 'DDDS', 'DSDD']
    for r, (zc, row) in enumerate(zip(rows_z, kinds)):
        cy = DRESS_VBOT - (zc - Z_HEM) * vpm
        for k, kind in enumerate(row):
            u = DRESS_UC + (k - 2) * 0.25 + (-0.125 if r % 2 == 0 else 0.0)
            for m in (-1, 0, 1):
                cx = DRESS_U0 + DRESS_USPAN * (u + m)
                if cx < x0 - 12 or cx > x1 + 12:
                    continue
                dx = np.abs(x - cx) / hpm
                dy = np.abs(y - cy) / vpm
                if kind == 'D':
                    mask = dx * dx + dy * dy <= 0.0155 ** 2
                else:
                    mask = (dx / 0.0225) ** 0.55 + (dy / 0.0225) ** 0.55 <= 1.0
                cv.a[area & mask] = rgb('dots')
    # grille: diamond crosshatch, black lines on dark fill
    x0, y0, x1, y1 = GRILLE
    area = (x >= x0) & (x < x1) & (y >= y0) & (y < y1)
    gx, gy = x - x0, y - y0
    d1 = np.abs(((gx + gy + GRILLE_P / 2) % GRILLE_P) - GRILLE_P / 2)
    d2 = np.abs(((gx - gy + GRILLE_P / 2) % GRILLE_P) - GRILLE_P / 2)
    cv.a[area & ((d1 < 1.9) | (d2 < 1.9))] = rgb('grille_line')
    # two-tone silver swatch: a light strip over silver mid
    x0, y0, x1, y1 = LIPSW
    area = (x >= x0) & (x < x1) & (y >= y0) & (y < y1)
    cv.a[area] = rgb('silver_mid')
    cv.a[area & (y < y0 + 12)] = rgb('silver_light')
    return cv.result()


# ----------------------------------------------------------------------------- face textures
def fpt(xf, hf):
    """window fraction across (0 = her right edge), head-height fraction below the head top -> (x, z) metres"""
    return ((xf - 0.5) * CARD_W, HEAD_ZMAX - hf * HEAD_H)


# One row per face card, in card order: object name, then eyes, mouth, blush and tint. The card number (1 up)
# names the texture and the material. Cards 1 and 5 to 11 are Katamari Forever's eight face expressions (1 is its
# neutral face); 2 to 4 are her own extras.
FACES = [
    ('face_1_neutral', dict(eyes='lids', mouth='bar', blush=False, tint=None)),
    ('face_2_singing', dict(eyes='arcs', mouth='oval', blush=False, tint=None)),
    ('face_3_blink', dict(eyes='lines', mouth='bar', blush=False, tint=None)),
    ('face_4_surprised', dict(eyes='dots', mouth='round', blush=False, tint=None)),
    ('face_5_open', dict(eyes='lids', mouth='open', blush=False, tint=None)),
    ('face_6_smile', dict(eyes='lids', mouth='smile', blush=False, tint=None)),
    ('face_7_angry', dict(eyes='glare', mouth='peak', blush=False, tint=None)),
    ('face_8_blush_open', dict(eyes='lids', mouth='open', blush=True, tint=None)),
    ('face_9_pale', dict(eyes='lids', mouth='open_small', blush=False, tint='pale')),
    ('face_10_blush', dict(eyes='lids', mouth='bar', blush=True, tint=None)),
    ('face_11_blush_smile', dict(eyes='lids', mouth='smile', blush=True, tint=None)),
]
# blush: a soft flat-topped spot on each cheek, below and outside the eyes, outside the nose
BLUSH_X, BLUSH_Z, BLUSH_RX, BLUSH_RZ, BLUSH_MIX = 0.069, 0.807, 0.021, 0.017, 0.50
# pale card: the cool band holds from the card top (under the fringe) down to PALE_Z0, then fades into the ashen
# lower face by PALE_Z1
PALE_Z0, PALE_Z1 = 0.834, 0.806


def rbox_mask(xm, z, cz, hw, hh, r):
    """box on the middle line, centred at height cz, with rounded corners of radius r"""
    ax, az = np.abs(xm), np.abs(z - cz)
    qx, qz = np.maximum(ax - (hw - r), 0), np.maximum(az - (hh - r), 0)
    return qx * qx + qz * qz <= r * r


def mouth_mask(kind, xm, z):
    if kind == 'bar':           # red bar
        return rbox_mask(xm, z, LIPS_Z, LIPS_HALF_W, LIPS_HALF_H, 0.003)
    if kind == 'oval':          # open red oval
        return (xm / 0.0170) ** 2 + ((z - (LIPS_Z - 0.008)) / 0.0205) ** 2 <= 1.0
    if kind == 'round':         # small round open mouth
        return xm ** 2 + (z - (LIPS_Z - 0.003)) ** 2 <= 0.0105 ** 2
    if kind == 'open':          # the bar dropped open: about as wide, taller than wide, hanging from the bar's top edge
        return rbox_mask(xm, z, LIPS_TOP - 0.0235, 0.0200, 0.0235, 0.0036)
    if kind == 'open_small':    # a smaller open box, nearly square
        return rbox_mask(xm, z, LIPS_TOP - 0.0160, 0.0150, 0.0160, 0.0030)
    if kind == 'smile':         # trapezoid, the wide edge up as wide as the bar
        return poly_mask(xm, z, [(-LIPS_HALF_W, LIPS_TOP), (LIPS_HALF_W, LIPS_TOP), (0.0110, LIPS_TOP - 0.0180),
                                 (-0.0110, LIPS_TOP - 0.0180)])
    if kind == 'peak':          # open triangle, point up, about as wide as the bar and clear of the nose above it
        return poly_mask(xm, z, [(0.0, LIPS_TOP + 0.0010), (0.0200, LIPS_TOP - 0.0270), (-0.0200, LIPS_TOP - 0.0270)])
    raise ValueError('unknown mouth %r' % kind)


def eye_mask(kind, xm, z, s):
    """one eye; s is -1 for her right eye (left in the image), 1 for her left"""
    cx = s * EYE_X
    if kind == 'arcs':          # closed, curved lids
        pts = [(cx + q * 0.0135, EYE_Z + 0.0045 - 0.0090 * (1 - q * q)) for q in np.linspace(-1, 1, 13)]
        return stroke_mask(xm, z, pts, 0.0064)
    if kind == 'dots':          # round eyes
        return (xm - cx) ** 2 + (z - EYE_Z) ** 2 <= 0.0105 ** 2
    if kind not in ('lids', 'lines', 'glare'):
        raise ValueError('unknown eyes %r' % kind)
    # the slanted ones: degrees of slant, the outer end higher; the glare sits a little lower and a little
    # further in, where the fringe is higher, to stay clear of it
    th = math.radians({'lids': 20.0, 'lines': 17.0, 'glare': 38.0}[kind])
    if kind == 'glare':
        cx, cz = s * (EYE_X - 0.0020), EYE_Z - 0.0035
    else:
        cz = EYE_Z
    axx, axz = math.cos(th), s * math.sin(th)          # along the lid
    u = (xm - cx) * axx + (z - cz) * axz
    v = -(xm - cx) * axz + (z - cz) * axx              # across the lid, up is positive
    if kind == 'lids':          # half-closed slanted lids: a lens, both edges sloping down towards the nose
        return np.abs(v) <= 0.0060 * (1 - (u / 0.0145) ** 2)
    if kind == 'lines':         # closed, straight lines
        pts = [(cx - 0.0135 * axx, cz - 0.0135 * axz), (cx + 0.0135 * axx, cz + 0.0135 * axz)]
        return stroke_mask(xm, z, pts, 0.0064)
    # the glare: half a lens, a hard straight top edge and a deep curved underside, steeper than the lids
    return (v <= 0.0035) & (v >= 0.0035 - 0.0135 * (1 - (u / 0.0160) ** 2))


def paint_face(card):
    """card: 1 up, a row of FACES. The card is opaque and carries the whole face."""
    if not 1 <= card <= len(FACES):
        raise ValueError('unknown face card %r' % card)
    spec = FACES[card - 1][1]
    cv = Canvas(FS, FS, 8, rgb('skin'))
    xm = (cv.x / FS - 0.5) * CARD_W                 # metres; the left of the image is her right side
    t = cv.y / FS
    z = CARD_ZTOP + (CARD_ZBOT - CARD_ZTOP) * t     # features are drawn in front-view metres
    black, eye = rgb('hair'), rgb('eyes')
    # her skin: plain, or pale (a cool band under the fringe fading into an ashen lower face)
    shade = rgb('skin_shade')
    if spec['tint'] == 'pale':
        w = np.clip((z - PALE_Z1) / (PALE_Z0 - PALE_Z1), 0.0, 1.0)
        w = (w * w * (3 - 2 * w))[..., None]
        cv.a[:] = rgb('pale_skin') * (1 - w) + rgb('pale_band') * w
        shade = rgb('pale_skin') * (rgb('skin_shade') / rgb('skin'))     # the shadow line darkens the ashen skin alike
    elif spec['tint'] is not None:
        raise ValueError('unknown tint %r' % spec['tint'])
    if not isinstance(spec['blush'], bool):
        raise ValueError('unknown blush %r' % spec['blush'])
    if spec['blush']:           # rose pink mixed into each cheek
        for s in (-1, 1):
            r2 = ((xm - s * BLUSH_X) / BLUSH_RX) ** 2 + ((z - BLUSH_Z) / BLUSH_RZ) ** 2
            w = (BLUSH_MIX * np.exp(-r2 * r2))[..., None]
            cv.a[:] = cv.a * (1 - w) + rgb('blush') * w
    skin = cv.a.copy()          # the skin layer, for the curl below
    # shadow line at the bottom of the window (the face sits inside the sleeve)
    cv.a[z < CARD_ZBOT + 0.006] = shade
    # nose and lips first so the hair can overlap nothing
    nose = poly_mask(xm, z, [(0.0, NOSE_ZTOP), (-NOSE_HALF_W, NOSE_ZBOT), (NOSE_HALF_W, NOSE_ZBOT)])
    cv.a[nose] = rgb('nose')
    cv.a[mouth_mask(spec['mouth'], xm, z)] = rgb('lips')
    for s in (-1, 1):
        cv.a[eye_mask(spec['eyes'], xm, z, s)] = eye
    # hair: fringe over the top, deepest near the middle, thinning to nothing at her left corner. On her right it
    # stays low (0.25 H) so the curl has hair round it.
    edge = cat_rom([fpt(-0.06, 0.25), fpt(0.0, 0.25), fpt(0.10, 0.235), fpt(0.20, 0.215), fpt(0.30, 0.208), fpt(0.50, 0.215),
                    fpt(0.65, 0.200), fpt(0.78, 0.162), fpt(0.88, 0.100), fpt(0.95, 0.040), fpt(1.0, -0.012), fpt(1.06, -0.05)])
    fringe = poly_mask(xm, z, [fpt(-0.06, -0.2)] + edge + [fpt(1.06, -0.2)])
    # lock down her right side: a thin strip along the window edge, ending in a small flick towards the face at
    # about half the head height
    upper = cat_rom([fpt(-0.06, 0.24), fpt(0.04, 0.25), fpt(0.04, 0.32), fpt(0.045, 0.39), fpt(0.10, 0.425),
                     fpt(0.16, 0.44), fpt(0.185, 0.43)])
    lower = cat_rom([fpt(0.185, 0.43), fpt(0.14, 0.47), fpt(0.08, 0.505), fpt(0.03, 0.53), fpt(-0.06, 0.535)])
    lock = poly_mask(xm, z, upper + lower[1:])
    cv.a[fringe | lock] = black
    # the curl in her right top corner: an open C of skin showing through the hair
    curl = cat_rom([fpt(0.065, 0.255), fpt(0.055, 0.17), fpt(0.075, 0.11), fpt(0.12, 0.088), fpt(0.165, 0.105),
                    fpt(0.172, 0.135), fpt(0.148, 0.152)])
    curl = stroke_mask(xm, z, curl, 0.0060)
    cv.a[curl] = skin[curl]
    return cv.result()


# ----------------------------------------------------------------------------- mesh builder
class Part:
    """Collects triangles. Vertices at the same place are shared. Each triangle is wound so that its
    normal points away from the current inside point, so no face comes out inside-out."""

    def __init__(self, name):
        self.name = name
        self.verts, self.weights, self.index = [], [], {}
        self.tris, self.uvs = [], []
        self.inside = Vector((0, 0, 0))
        self.wfun = None

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
        """closed band between two rings with the same number of points"""
        n = len(ra)
        for i in range(n):
            j = (i + 1) % n
            self.quad([ra[i], ra[j], rb[j], rb[i]], uv, flip=(i % 2 == 1))

    def solid(self, top, loops, bottom, uv):
        """pole, loops, pole: a low-segment ball or blob"""
        n = len(loops[0])
        for i in range(n):
            j = (i + 1) % n
            self.tri([top, loops[0][i], loops[0][j]], uv)
            self.tri([bottom, loops[-1][j], loops[-1][i]], uv)
        for la, lb in zip(loops[:-1], loops[1:]):
            self.band(la, lb, uv)


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
    keep = part.inside
    part.inside = c
    for i in range(5):
        j = (i + 1) % 5
        part.tri([c + ax * r, a[i], a[j]], uv)
        part.tri([a[i], b[i], a[j]], uv)
        part.tri([b[i], b[j], a[j]], uv)
        part.tri([c - ax * r, b[j], b[i]], uv)
    part.inside = keep


def ball(part, centre, radii, frame, nseg, lats, uv, phase=0.0):
    """low-segment sphere: frame = (u1, u2, axis), radii along them, lats = loop latitudes in degrees"""
    u1, u2, ax = (Vector(f) for f in frame)
    c = Vector(centre)
    loops = []
    for lat in lats:
        cl, sl = math.cos(math.radians(lat)), math.sin(math.radians(lat))
        loops.append([c + u1 * (radii[0] * cl * math.cos(phase + 2 * math.pi * i / nseg)) +
                      u2 * (radii[1] * cl * math.sin(phase + 2 * math.pi * i / nseg)) + ax * (radii[2] * sl) for i in range(nseg)])
    keep = part.inside
    part.inside = c
    part.solid(c + ax * radii[2], loops, c - ax * radii[2], uv)
    part.inside = keep


# ----------------------------------------------------------------------------- head
def hp(x, k, r=1.0):
    """point on the head's octagonal section at corner k"""
    phi = math.radians(PHI[k % 8])
    return Vector((x, -r * HEAD_RY * math.cos(phi), HEAD_CZ + r * HEAD_RZ * math.sin(phi)))


def card_rows():
    """the face card's three rows (y, z, v): top on the upper front face, fold at the corner, bottom on the front
    face. v rises with z at one rate over the whole card, so the front view keeps the painted features in place."""
    k0, k1 = hp(0, 0), hp(0, 1)                    # the corner at the fold and the next corner up
    y_top = k0.y + (CARD_ZTOP - k0.z) / (k1.z - k0.z) * (k1.y - k0.y)   # the upper front face at the card's top
    v = (CARD_ZFOLD - CARD_ZBOT) / (CARD_ZTOP - CARD_ZBOT)
    return [(y_top - CARD_GAP[0], CARD_ZTOP, 1.0), (-(HEAD_FRONT + CARD_GAP[1]), CARD_ZFOLD, v),
            (-(HEAD_FRONT + CARD_GAP[2]), CARD_ZBOT, 0.0)]


def card_y(z):
    """y of the card at height z"""
    rows = card_rows()
    for (ya, za, _), (yb, zb, _) in zip(rows[:-1], rows[1:]):
        if zb <= z <= za:
            return ya + (z - za) / (zb - za) * (yb - ya)
    raise ValueError('z %.4f is off the card' % z)


def build_head():
    p = Part('head')
    p.wfun = lambda co: {'JNT_head': 1.0}
    p.inside = Vector((0, 0, HEAD_CZ))
    # the middle: face window zone, on the same octagon as the collars, so their edges meet corner to corner.
    # The card lies over the front and upper front faces; hair on top, silver behind and below.
    def win_ring(x):
        return [hp(x, k) for k in range(8)]
    left, right = win_ring(-X_WIN), win_ring(X_WIN)
    # upper front, top, upper back, back, lower back, bottom, lip, front (behind the card). The back carries on
    # the collars' colours, so the back of the head reads as one silver sleeve under the hair.
    win = ['hair', 'hair', 'hair', 'silver_light', 'silver_mid', 'silver_mid', None, 'skin_shade']
    for i in range(8):
        j = (i + 1) % 8
        cos = [left[i], right[i], right[j], left[j]]
        if win[i] is None:      # the lip: a thin bright strip under the window, silver mid below (two-tone swatch)
            x0, y0, x1, y1 = LIPSW
            uv = [px(x0 + 8, y1 - 2), px(x1 - 8, y1 - 2), px(x1 - 8, y0 + 6), px(x0 + 8, y0 + 6)]
        else:
            uv = win[i]
        p.quad(cos, uv)
    length = X_END - X_SLEEVE
    xs = [X_SLEEVE + f * length for f, _ in GRILLE_RINGS]
    rs = [r for _, r in GRILLE_RINGS]
    # distance along the surface from the collar, for the crosshatch
    run = [0.0]
    for j in range(1, len(xs)):
        run.append(run[-1] + math.hypot(xs[j] - xs[j - 1], (rs[j] - rs[j - 1]) * HEAD_RZ))
    gx0, gy0, gx1, gy1 = GRILLE
    vscale = min(GRILLE_P / GRILLE_M_PER_P, (gy1 - gy0 - 2.0) / run[-2])     # the last band is mapped flat
    for sx in (1, -1):
        # collar: a light silver band between the window zone and the grille, with a thin darker rim next to
        # the window (the same two-tone swatch as the lip, turned sideways). Light over the top and the back,
        # darker underneath, and on the lower front face it takes the lip's mapping, so band and lip darken
        # together below the window.
        x0, y0, x1, y1 = LIPSW
        for k in range(8):
            cos = [hp(sx * X_WIN, k), hp(sx * X_SLEEVE, k), hp(sx * X_SLEEVE, k + 1), hp(sx * X_WIN, k + 1)]
            if k in (4, 5):
                uv = 'silver_mid'
            elif k in (2, 3):
                uv = 'silver_light'
            elif k == 6:
                uv = [px(x0 + 8, y1 - 2), px(x1 - 8, y1 - 2), px(x1 - 8, y0 + 6), px(x0 + 8, y0 + 6)]
            else:
                uv = [px(x0 + 10, y0 + 12.8), px(x0 + 10, y0 + 2), px(x1 - 10, y0 + 2), px(x1 - 10, y0 + 12.8)]
            p.quad(cos, uv)
        # grille ball: the crosshatch wraps round, 1.5 diamonds per face (so neighbouring faces use alternate
        # halves of a 3-diamond strip); the rounded end is mapped flat from the side so the lattice stays even
        # there, centred on one diamond
        s = GRILLE_P / GRILLE_M_PER_P

        def flat(c):
            return px(gx0 + 48 + c.y * s, gy0 + 24 - (c.z - HEAD_CZ) * s)
        last = len(xs) - 2
        for j in range(len(xs) - 1):
            for k in range(8):
                cos = [hp(sx * xs[j], k, rs[j]), hp(sx * xs[j], k + 1, rs[j]), hp(sx * xs[j + 1], k + 1, rs[j + 1]), hp(sx * xs[j + 1], k, rs[j + 1])]
                if j == last:
                    uv = [flat(c) for c in cos]
                else:
                    ua = gx0 + GRILLE_P / 2 + 1.5 * GRILLE_P * (k % 2)
                    ub = ua + 1.5 * GRILLE_P
                    va, vb = gy0 + 1.0 + run[j] * vscale, gy0 + 1.0 + run[j + 1] * vscale
                    uv = [px(ua, va), px(ub, va), px(ub, vb), px(ua, vb)]
                p.quad(cos, uv, flip=(k % 2 == 1))
        cap = [hp(sx * xs[-1], k, rs[-1]) for k in range(8)]
        p.fan(cap, [flat(c) for c in cap])
    # antenna: yellow cone and red ball on top of the middle
    base = Vector((0, ANT_Y, HEAD_ZMAX - 0.006))
    apex = Vector((0, ANT_Y, HEAD_ZMAX + ANT_CONE_H))
    ring = [base + Vector((ANT_CONE_R * math.cos(2 * math.pi * i / 6), ANT_CONE_R * math.sin(2 * math.pi * i / 6), 0)) for i in range(6)]
    p.inside = base + (apex - base) * 0.3
    for i in range(6):
        p.tri([ring[i], ring[(i + 1) % 6], apex], 'antenna_cone')
    ball(p, (0, ANT_Y, ANT_BALL_Z), (ANT_BALL_R,) * 3, ((1, 0, 0), (0, 1, 0), (0, 0, 1)), 6, (45, 0, -45), 'antenna_ball')
    # nose: a small orange pyramid standing on the card, as on the Forever heads
    def on_card(x, z):      # a point just in front of the card
        return Vector((x, card_y(z) - 0.0005, z))
    top, bl, br = on_card(0, NOSE_ZTOP), on_card(-NOSE_HALF_W, NOSE_ZBOT), on_card(NOSE_HALF_W, NOSE_ZBOT)
    tip = on_card(0, NOSE_ZBOT + 0.30 * (NOSE_ZTOP - NOSE_ZBOT)) + Vector((0, -NOSE_OUT, 0))
    p.inside = (top + bl + br + tip) / 4
    for tri in ((top, bl, tip), (top, tip, br), (bl, br, tip)):
        p.tri(tri, 'nose')
    return p


def build_face(name):
    """face card: a strip of 6 vertices (4 triangles) just in front of the window zone, folded back over the
    upper front face like Forever's cards"""
    p = Part(name)
    p.wfun = lambda co: {'JNT_head': 1.0}
    p.inside = Vector((0, 0, HEAD_CZ))
    rows = card_rows()
    e = 0.5 / FS                                   # half a texel in from the border
    for (ya, za, va), (yb, zb, vb) in zip(rows[:-1], rows[1:]):
        va, vb = e + va * (1 - 2 * e), e + vb * (1 - 2 * e)
        cos = [(-X_WIN, ya, za), (X_WIN, ya, za), (X_WIN, yb, zb), (-X_WIN, yb, zb)]
        p.quad(cos, [(e, va), (1 - e, va), (1 - e, vb), (e, vb)])
    return p


# ----------------------------------------------------------------------------- body
def oct_ring(z, a, b):
    """8 points: 0-1 her right side, 2-3 front, 4-5 her left side, 6-7 back"""
    xy = [(-a, b * T8), (-a, -b * T8), (-a * T8, -b), (a * T8, -b), (a, -b * T8), (a, b * T8), (a * T8, b), (-a * T8, b)]
    zs = z if isinstance(z, (list, tuple)) else [z] * 8
    return [Vector((x, y, zz)) for (x, y), zz in zip(xy, zs)]


def body_weights(co):
    z = co.z
    if z <= 0.36:
        return {'JNT_waist': 1.0}
    if z <= 0.50:
        return {'JNT_waist': 0.25, 'JNT_spine_01': 0.60, 'JNT_spine_02': 0.15}
    if z <= 0.592:
        return {'JNT_spine_01': 0.25, 'JNT_spine_02': 0.75}
    if z <= 0.64:
        return {'JNT_spine_02': 0.60, 'JNT_neck': 0.40}
    return {'JNT_neck': 1.0}


def build_body():
    p = Part('body')
    p.wfun = body_weights
    p.inside = Vector((0, 0, 0.45))
    rings = [oct_ring(z, a, a * B_RATIO) for z, a in BODY_PROFILE[:-1]]
    a_top = BODY_PROFILE[-1][1]
    neck_z = [NECK_SIDE, NECK_SIDE, NECK_PEAK, NECK_PEAK, NECK_SIDE, NECK_SIDE, NECK_BACK, NECK_BACK]
    neck = oct_ring(neck_z, a_top, a_top * B_RATIO)
    trim = oct_ring([z - TRIM_H for z in neck_z], a_top, a_top * B_RATIO)
    neck_c = Vector((0, -a_top * B_RATIO, NECK_DIP))           # the dip of the sweetheart neckline
    trim_c = Vector((0, -a_top * B_RATIO, NECK_DIP - TRIM_H))
    sh = oct_ring(SHOULDER[0], SHOULDER[1], SHOULDER[2])
    nk = oct_ring(NECK_TOP[0], NECK_TOP[1], NECK_TOP[2])

    def duv(ring, j, end):
        """dress UV of ring point j as the start (end=0) or the end (end=1) of face j"""
        q = ring[(j + end) % 8]
        return dress_uv(DRESS_CUM[j % 4 + end], q.z)

    # hem cap, then the dress up to the trim
    p.fan(rings[0], 'dress')
    stack = rings + [trim]
    for ra, rb in zip(stack[:-1], stack[1:]):
        for j in range(8):
            if rb is trim and j == 2:       # front face reaches up to the dip vertex: five corners
                uc = dress_uv(DRESS_UC, trim_c.z)
                p.tri([ra[2], ra[3], trim_c], [duv(ra, 2, 0), duv(ra, 2, 1), uc])
                p.tri([ra[2], trim_c, rb[2]], [duv(ra, 2, 0), uc, duv(rb, 2, 0)])
                p.tri([ra[3], rb[3], trim_c], [duv(ra, 2, 1), duv(rb, 2, 1), uc])
                continue
            p.quad([ra[j], ra[(j + 1) % 8], rb[(j + 1) % 8], rb[j]], [duv(ra, j, 0), duv(ra, j, 1), duv(rb, j, 1), duv(rb, j, 0)],
                   flip=(j in (1, 5, 6)))
    # light pink trim along the neckline
    for j in range(8):
        if j == 2:
            p.quad([trim[2], trim_c, neck_c, neck[2]], 'trim')
            p.quad([trim_c, trim[3], neck[3], neck_c], 'trim', flip=True)
        else:
            p.quad([trim[j], trim[(j + 1) % 8], neck[(j + 1) % 8], neck[j]], 'trim', flip=(j in (1, 5, 6)))
    # bare chest, shoulders and neck
    for j in range(8):
        if j == 2:
            p.tri([neck[2], neck_c, sh[2]], 'skin')
            p.tri([neck_c, sh[3], sh[2]], 'skin')
            p.tri([neck_c, neck[3], sh[3]], 'skin')
        else:
            p.quad([neck[j], neck[(j + 1) % 8], sh[(j + 1) % 8], sh[j]], 'skin', flip=(j in (1, 5, 6)))
    p.band(sh, nk, 'skin')
    # necklace: beads resting on the chest and the back, each the outer half of an 8-face ball. Each bead takes
    # the skin weights of the chest point under it, so it moves with the surface when the neck or spine bends,
    # and its base sits a little into the chest.
    tree = BVHTree.FromPolygons([tuple(v) for v in p.verts], [tuple(t) for t in p.tris])
    beads = [(-0.042 + 0.014 * i, -1.0) for i in range(7)] + [(-0.030 + 0.015 * i, 1.0) for i in range(5)]
    r = 0.0080
    for bx, side in beads:
        bz = 0.594 + 0.018 * (bx / 0.042) ** 2 if side < 0 else 0.606 + 0.006 * (bx / 0.030) ** 2
        hit, nrm, fi, _ = tree.ray_cast(Vector((bx, side, bz)), Vector((0, -side, 0)))
        if nrm.y * side < 0:
            nrm = -nrm
        # barycentric position of the hit in its triangle, then the blend of the three corners' weights
        ia, ib, ic = p.tris[fi]
        A, B, Cv = p.verts[ia], p.verts[ib], p.verts[ic]
        v0, v1, v2 = B - A, Cv - A, hit - A
        d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
        den = d00 * d11 - d01 * d01
        lb = (d11 * d20 - d01 * d21) / den
        lc = (d00 * d21 - d01 * d20) / den
        wsum = {}
        for vi, lam in ((ia, 1 - lb - lc), (ib, lb), (ic, lc)):
            for bone, wv in p.weights[vi].items():
                wsum[bone] = wsum.get(bone, 0.0) + lam * wv
        wsum = {b: wv for b, wv in wsum.items() if wv > 1e-4}
        tot = sum(wsum.values())
        p.wfun = lambda co, w={b: wv / tot for b, wv in wsum.items()}: w
        t1 = Vector((0, 0, 1)).cross(nrm).normalized()
        t2 = nrm.cross(t1).normalized()                 # up along the surface
        base = hit - nrm * 0.003
        apex = hit + nrm * r
        e, n, w, s = base + t1 * r, base + t2 * r, base - t1 * r, base - t2 * r
        p.inside = base - nrm * 0.004
        p.tri([apex, e, n], 'trim')
        p.tri([apex, n, w], 'trim')
        p.tri([apex, w, s], 'bead_shade')
        p.tri([apex, s, e], 'bead_shade')
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
        p.band(ra, rb, 'skin')
    # close the tube at the shoulder. The cap's centre has the same x as the usual inside point, so wind it
    # against a point further out along the arm instead: the cap then faces the body.
    keep = p.inside
    p.inside = Vector((side * ARM_RINGS[1][0], 0.0, ARM_ZC))
    p.quad(rings[0], 'skin')
    p.inside = keep
    p.wfun = lambda co: {hand: 1.0}
    icosa(p, (side * HAND_X, 0.0, ARM_ZC), HAND_R, ((0, 1, 0), (0, 0, 1), (side, 0, 0)), 'skin')
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
        p.band(ra, rb, 'skin')
    # rounded magenta shoe with a nearly flat sole and a pointed toe
    p.wfun = lambda co: {ankle: 1.0}

    def loop(z, f):
        return [Vector((x0 + SHOE_RX * f * math.cos(math.radians(-90 + 60 * i)), SHOE_Y + SHOE_RY * f * math.sin(math.radians(-90 + 60 * i)), z)) for i in range(6)]
    p.inside = Vector((x0, SHOE_Y, 0.02))
    p.solid(Vector((x0, SHOE_Y + 0.008, SHOE_H)), [loop(0.69 * SHOE_H, 0.78), loop(0.15 * SHOE_H, 0.94)], Vector((x0, SHOE_Y, 0.0)), 'dress')
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


def make_object(part, mat, rig, sharp_deg=75.0):
    me = bpy.data.meshes.new(part.name)
    me.from_pydata([tuple(v) for v in part.verts], [], part.tris)
    me.update()
    uv = me.uv_layers.new(name='UVMap')
    for poly, uvs in zip(me.polygons, part.uvs):
        poly.use_smooth = True
        for li, co in zip(poly.loop_indices, uvs):
            uv.data[li].uv = co
    # edges sharper than sharp_deg are made hard: the octagon edges and the grille end caps (about 22 degrees)
    # stay soft; box edges, the dress hem and the shoe soles stay hard
    me.set_sharp_from_angle(angle=math.radians(sharp_deg))
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
    data = bpy.data.armatures.new('Burda_rig')
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


def scrub_private_paths():
    """Blender's default workspaces carry a file browser that remembers the home folder of whoever runs the
    script; a saved .blend would hold that path. Overwrite the whole text buffer, then blank it, so the file
    names no user (setting it to nothing alone would leave the rest of the old path behind)."""
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
    """look for the name and home folder of the user running the build inside the files just written"""
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
    body_png = os.path.join(TEX_DIR, 'burda_old_body.png')
    save_png('burda_old_body', paint_body(), body_png)
    face_png = []
    for i in range(1, len(FACES) + 1):
        face_png.append(os.path.join(TEX_DIR, 'burda_old_face_%d.png' % i))
        save_png('burda_old_face_%d' % i, paint_face(i), face_png[-1])

    # 2 materials
    body_mat = make_material('burda_old_body', body_png)
    face_mats = [make_material('burda_old_face_%d' % (i + 1), q) for i, q in enumerate(face_png)]
    img = body_mat.node_tree.nodes['Image Texture'].image
    sx, sy = int(CELL_W / 2), TS - 1 - int(CELL_H / 2)          # a texel inside the skin cell (rows are stored bottom-up)
    px4 = img.pixels[(sy * TS + sx) * 4:(sy * TS + sx) * 4 + 3]
    print('PIXEL skin cell #%02X%02X%02X (design sheet #%s)' % (*[int(round(c * 255)) for c in px4], PAL['skin']))

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
        print('TRIS %-18s %4d   verts %4d' % (ob.name, n, len(ob.data.vertices)))
    print('TRIS total %d' % total)
    dims = [max(max(v.co[i] for v in ob.data.vertices) for ob in objs) - min(min(v.co[i] for v in ob.data.vertices) for ob in objs) for i in range(3)]
    print('SIZE %.3f wide, %.3f deep, %.3f tall' % tuple(dims))

    # 5 renders: rest pose, neutral face
    C.render_views(os.path.join(REN_DIR, 'Burda_old'), views=('front', 'side', 'back'))

    # 6 clean up and save
    sc = bpy.context.scene
    for ob in [o for o in sc.objects if o.type in {'CAMERA', 'LIGHT'}]:
        data = ob.data
        bpy.data.objects.remove(ob)
        (bpy.data.cameras if isinstance(data, bpy.types.Camera) else bpy.data.lights).remove(data)
    if sc.world:
        bpy.data.worlds.remove(sc.world)
    sc.render.filepath = '//'
    blend = os.path.join(HERE, 'Burda_old.blend')
    fbx = os.path.join(HERE, 'Burda_old.fbx')
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
    print('LEAK check (user name or home folder inside the written files): %s' % (', '.join(leaks) if leaks else 'none'))
    print('DONE')


main()
