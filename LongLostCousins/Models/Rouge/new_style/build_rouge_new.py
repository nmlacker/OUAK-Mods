"""Rouge, new style (Once Upon a Katamari look).  Builds everything from an empty scene:
textures, materials, meshes, the rig from ../rouge_rig_spec.json, weights, renders, the .blend and the FBX.

    blender -b --factory-startup --python-exit-code 1 --python build_rouge_new.py

Units are Blender metres, Z up, Rouge faces -Y, +X is her left.  Every path is resolved from this file.
All geometry and every pixel is made here; nothing is read from any game file.

Rouge (루즈) is a cousin of the cancelled Katamari Damacy Online, named for her lipstick-shaped head: a hot-pink
lipstick bullet with a slanted top and a gold ring, over a brown case with her face in a gold-framed window, and a
pink dress with a gold hem.  See ../Rouge_design_sheet.md.  Head, case and dress are one surface turned on a lathe,
weighted from the waist up to the head.  The helpers follow Pis's, Pew's and Burda's build scripts.

Everything above main() is plain Python and numpy, so preview_rouge_new.py and export_rouge_noblender.py can build
the meshes and paint the textures without Blender.
"""
import sys, os, json, math, glob
import numpy as np

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

TEX_DIR = os.path.join(HERE, 'textures')
REN_DIR = os.path.join(HERE, 'renders')
RIG_SPEC = os.path.join(os.path.dirname(HERE), 'rouge_rig_spec.json')
BLEND = os.path.join(HERE, 'Rouge_new.blend')
FBX = os.path.join(HERE, 'Rouge_new.fbx')
BODY_TEX = 'rouge_new_body'
FACE_TEX = 'rouge_new_face'
NAME = 'Rouge'

# ---------------------------------------------------------------- design sheet colours (from the KDO art)
CAP, CAP_L, CAP_S = '#F84878', '#F87898', '#C8305E'          # the lipstick: sampled, its light and shade
BAND_PINK, BAND_H = '#D8366A', 0.036                          # the slightly darker pink band right over the gold ring
GOLD, GOLD_L, GOLD_S = '#F0C848', '#FBE590', '#B88A28'        # ring, window frame, hem, cuffs
CASE, CASE_S = '#7A4626', '#5A3018'                          # the brown case and her long hair: one plain brown
DRESS, DRESS_S, DRESS_L = '#F86888', '#D84A72', '#F68EC0'    # dress (sampled), its shade, its light-pink stripe
DRESS_HOT, DRESS_MID, DRESS_DARK = '#E8346E', '#F268A6', '#B81E54'                 # the photo's hot-pink side and medium-pink stripe
TIGHTS, SHOE = '#F0407A', '#C8285E'
FACE, FACE_SH = '#FFE2C8', '#F2C8A8'
HAIR, HAIR_L = '#8E6038', '#E2B88C'          # her hair in the window (the photo) and its light streak
CHEEKS = '#F8A0A0'
NOSE, LIPS, LIPS_D, EYES = '#F06A28', '#D83048', '#8E1828', '#2A1A14'
ANT_Y, ANT_R = '#FEF404', '#F3131B'
GLOOM, FLUSH = ('#5E77A0', 0.80), ('#E2564A', 0.60)
GLOOM_FADE, FLUSH_FADE = 0.040, 0.030
EYE_W, BLUSH, TEAR = '#FFFFFF', '#FF8AA8', '#8FD0D6'
ANGER, SHOCK = '#7A82EE', '#1C2550'
PRINCE_EYE = '#6E7078'                       # the Prince's grey eyes

# ---------------------------------------------------------------- sizes (metres)
N = 28                                       # segments round the lathe body
# The KDO art (Rougedress.jpg, the mirror image: a front view) read at 0.00182 m per pixel, the soles at 0.
Z_HEM, Z_DRESS_TOP = 0.180, 0.400            # the dress: gold hem at the bottom, joins the case at the top
Z_WIN0, Z_WIN1 = 0.470, 0.630                # the face window on the case
Z_RING0, Z_RING1 = 0.630, 0.668              # the gold ring under the lipstick
Z_CAP0, Z_TOP_F, Z_TOP_B = 0.668, 0.885, 0.950   # the lipstick from the ring to its slanted top: its low and high side
TH_LOW = math.pi                              # the slant's low side: straight at the back; its high corner is over her face
R_CASE, R_RING, R_CAP, R_CAPTOP = 0.122, 0.132, 0.128, 0.120   # skinnier than the art, on request
R_HEM, R_DRESS_TOP = 0.162, 0.120
R_NECK = 0.094                               # the neck under her head block, as the art's back view
TOP_ROUND = 0.018                            # the slanted top's rim, rounded over this radius
HEM_H = 0.022
TONGUE_Z = 0.258                             # her hair's point on the dress ends here
Z_NECK_HAIR, TONGUE_W = 0.430, 0.108          # the hair leaves the neck this high, this wide (arc length)
ARC_C, ARC_ASPECT = (0.50, -0.65), 1.0         # the photo's stripes: arcs round this point (h, t)
ARC_R1, ARC_R2 = 1.30, 1.00                    # the medium-pink stripe's outer edge, the light-pink's
ARC_TIP = (-0.33, 0.37)                        # where the hot pink's curve meets the stripes: the brown's point
HOT_TOP = 0.60                                 # the hot pink reaches the dress's edge this high; the brown chest above
Y_FRONT = -R_CASE
TAB_TOP = 0.5 * (Z_TOP_F + Z_TOP_B)          # the top's middle
ANT_RHO = 0.55                               # the antenna stands toward the top's high side, this far out (share of R_CAPTOP)
# face plate: a rounded rectangle on the case's front (front-view x, z), its rim gold (the window frame)
PLATE_TOP, PLATE_BOT = 0.640, 0.515             # the window runs from under the gold ring down to just below her mouth
FX, FZ, FA, FB, FP = 0.0, 0.5 * (PLATE_TOP + PLATE_BOT), 0.090, 0.5 * (PLATE_TOP - PLATE_BOT), 14.0   # a square window
FRIM = 0.010
FACE_SCALE = 0.85
RP = R_CASE + 0.0015                         # the plate's radius: in front of the case
OFF_EYE, OFF_OVL = 0.0015, 0.0008
# antenna: the Prince's, at his sizes
ANT_CONE_R = 0.030
ANT_BALL = 0.040
# Taller: everything is built at the art's sizes, then stretched in height (stretch_z): the tights by LEG_EXTRA, the
# dress by DRESS_EXTRA, everything above the dress raised by both.  Shoes, face, case and lipstick keep their shapes.
LEG_EXTRA, DRESS_EXTRA = 0.035, 0.020
STRETCH_Z = [(-1.0, -1.0), (0.045, 0.045), (Z_HEM, Z_HEM + LEG_EXTRA), (Z_DRESS_TOP, Z_DRESS_TOP + LEG_EXTRA + DRESS_EXTRA),
             (3.0, 3.0 + LEG_EXTRA + DRESS_EXTRA)]


def stretch_z(z):
    return float(np.interp(z, [a for a, _ in STRETCH_Z], [b for _, b in STRETCH_Z]))


def unstretch_z(z):
    return float(np.interp(z, [b for _, b in STRETCH_Z], [a for a, _ in STRETCH_Z]))

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





# ---------------------------------------------------------------- the lathe profile (radius by height)
PROFILE = [                                   # (z, r) from the hem's underside up to where the slanted top begins
    (Z_HEM - 0.004, R_HEM - 0.012), (Z_HEM, R_HEM), (Z_HEM + HEM_H, R_HEM - 0.004), (Z_HEM + HEM_H + 0.001, R_HEM - 0.006),
    (0.260, 0.147), (0.330, 0.132), (0.380, 0.120), (Z_DRESS_TOP, 0.113), (0.418, 0.102), (0.434, R_NECK),
    (0.446, R_NECK), (0.457, R_NECK + 0.006), (0.465, R_CASE - 0.006), (Z_WIN0, R_CASE), (0.550, R_CASE),
    (Z_WIN1, R_CASE), (Z_RING0 + 0.001, R_RING), (Z_RING1 - 0.001, R_RING), (Z_RING1, R_CAP - 0.002),
    (0.700, R_CAP), (0.760, R_CAP - 0.002), (0.820, R_CAP - 0.004), (Z_TOP_F - TOP_ROUND - 0.006, R_CAPTOP + 0.004)]
PROF_Z = np.array([p[0] for p in PROFILE])
PROF_R = np.array([p[1] for p in PROFILE])


def top_z(th):
    """The slanted top's height over angle th (0 = front, toward -Y): lowest at the front, highest at the back."""
    return Z_TOP_F + (Z_TOP_B - Z_TOP_F) * 0.5 * (1.0 - np.cos(th - TH_LOW))


def lp(th, r, z):
    return (r * math.sin(th), -r * math.cos(th), z)


# ---------------------------------------------------------------- atlas layout (pixels, top-left origin)
BODY_PX = 512
BODY_BLOCKS = {
    'lathe':      (4, 4, 320, 420),       # round the body (U, from the front round her left) by height (V, top down)
    'top':        (332, 4, 96, 96),       # the slanted top, flat in x and y
    'face':       (332, 108, 96, 80),     # the face plate, flat in x and z
    'face_gloom': (332, 196, 96, 80),
    'face_flush': (332, 284, 96, 80),
    'arm':        (436, 4, 16, 64),
    'leg':        (460, 4, 16, 64),
    'shoe':       (484, 4, 20, 24),
    'hand':       (484, 36, 20, 24),
    'antY':       (436, 76, 12, 40),
    'antR':       (456, 76, 24, 24),
    'under':      (488, 76, 12, 12),
    'plate_rim':  (488, 96, 8, 8),
    'nose':       (436, 124, 16, 16),
}
FACE_PX = 512
PPM = 2600.0
FACE_SIZES = [(n, w * FACE_SCALE, h * FACE_SCALE) for n, w, h in [   # Pew's cells (metres), drawn FACE_SCALE smaller
    ('eye_01_m', 0.0300, 0.0220), ('eye_02_m', 0.0300, 0.0220), ('eye_03_m', 0.0300, 0.0220), ('eye_04_m', 0.0300, 0.0220),
    ('eye_05_m', 0.0300, 0.0220),
    ('mouth_01_m', 0.0300, 0.0220), ('mouth_02_m', 0.0300, 0.0240), ('mouth_03_m', 0.0300, 0.0240),
    ('mouth_04_m', 0.0420, 0.0240), ('mouth_05_m', 0.0320, 0.0360), ('mouth_06_m', 0.0220, 0.0220),
    ('mouth_07_m', 0.0280, 0.0340), ('mouth_08_m', 0.0400, 0.0160), ('mouth_09_m', 0.0340, 0.0170),
    ('mouth_10_m', 0.0300, 0.0240), ('mouth_11_m', 0.0400, 0.0300),
    ('angry_01_m', 0.0240, 0.0240), ('shock_01_m', 0.0240, 0.0300), ('cheak_01_m', 0.0280, 0.0180),
    ('tear_01_m', 0.0100, 0.0480)]]
LATHE_V0, LATHE_V1 = Z_TOP_B, PROF_Z[0]       # the lathe block's V runs from the top's highest point down


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
    return mix3(c, hx(GROOVE), 0.92 * g)




def lathe_r(z):
    return np.interp(z, PROF_Z, PROF_R)


METAL_TH = [-math.pi, -2.2, -1.5, -0.95, -0.72, -0.50, -0.20, 0.15, 0.50, 0.75, 0.95, 1.40, 2.20, math.pi]
METAL_VAL = [0.35, 0.22, 0.55, 0.85, 1.00, 0.60, 0.12, 0.30, 0.55, 0.92, 0.55, 0.28, 0.18, 0.35]
METAL_PINK = [(0.0, '#7E1440'), (0.30, '#C82A64'), (0.55, '#F44C86'), (0.82, '#FFA0C4'), (1.0, '#FFEAF4')]   # metallic pink
METAL_GOLD = [(0.0, '#5A3C0C'), (0.35, '#A8781A'), (0.60, '#E8B830'), (0.80, '#FFDC50'), (0.92, '#FFF4B0'), (1.0, '#FFFFFF')]


def metal_v(th):
    """How bright a metal surface is round the body: sharp bright and dark vertical bands, as a polished cylinder."""
    return np.interp((np.asarray(th) + math.pi) % (2 * math.pi) - math.pi, METAL_TH, METAL_VAL)


def ramp(v, stops):
    xs = [a for a, _ in stops]; cs = np.array([hx(c) for _, c in stops])
    return np.stack([np.interp(v, xs, cs[:, k]) for k in range(3)], axis=-1)


def f_lathe(U, V):
    th = (U - 0.5) * 2.0 * math.pi                 # 0 = front
    z = LATHE_V0 - V * (LATHE_V0 - LATHE_V1)
    side = np.abs(np.sin(th)); back = 0.5 * (1.0 - np.cos(th))
    # the lipstick
    wr = lambda a: (a + math.pi) % (2 * math.pi) - math.pi
    # metallic: the lipstick and the ring reflect a bright sky and a dark ground in sharp vertical bands round them
    v = metal_v(th)
    vz = np.clip(v + 0.10 * (z - 0.78) / 0.12, 0.0, 1.0)
    cap = ramp(vz, METAL_PINK)
    cap = mix3(cap, ramp(np.clip(vz - 0.18, 0.0, 1.0), METAL_PINK), sstep(Z_RING1 + BAND_H + 0.002, Z_RING1 + BAND_H - 0.002, z))   # the darker band
    gold = mix3(hx(GOLD_S), hx(GOLD), sstep(0.0, 0.5, 1.0 - back))
    ring_t = (z - Z_RING0) / (Z_RING1 - Z_RING0)
    prof = np.exp(-((ring_t - 0.42) / 0.20) ** 2) - 0.35 * (sstep(0.25, 0.0, ring_t) + sstep(0.80, 1.0, ring_t))
    ring = ramp(np.clip(0.55 * metal_v(th + 0.25) + 0.50 * prof + 0.05, 0.0, 1.0), METAL_GOLD)
    hem_t = (z - Z_HEM) / HEM_H
    hem = mix3(gold, hx(GOLD_L), 0.6 * np.exp(-((hem_t - 0.7) / 0.2) ** 2))
    # the case
    case = np.broadcast_to(hx(CASE), U.shape + (3,))                # plain brown, no shine
    # the dress, as the photo's back view (File:Ruz.jpg), the same at the front and the back.  In each view's own
    # measure (h across, -1 the viewer's left edge to +1 the right; t up, 0 the hem's top to 1 the neck): a brown upper
    # body; hot pink rising up the left side to near the neck and curving in to a point left of the middle; and two
    # curved stripes, medium and light pink, arcs round a point below the hem on the right.
    wr = lambda a: (a + math.pi) % (2 * math.pi) - math.pi
    tt = (z - (Z_HEM + HEM_H)) / (Z_NECK_HAIR - (Z_HEM + HEM_H))
    dress = np.broadcast_to(hx(CASE), U.shape + (3,)).copy()
    for th0 in (0.0, math.pi):                     # front: the viewer's right is her left (+th); back: her right
        dl = wr(th - th0) if th0 == 0.0 else wr(th - th0)
        h = np.sin(dl) if th0 == 0.0 else np.sin(dl)
        view = np.abs(dl) <= 0.5 * math.pi + 1e-9
        rr = np.hypot(h - ARC_C[0], (tt - ARC_C[1]) * ARC_ASPECT)
        hl = ARC_TIP[0] - (1.0 + ARC_TIP[0]) * np.clip((tt - ARC_TIP[1]) / (HOT_TOP - ARC_TIP[1]), 0.0, None) ** 0.8
        hot = ((tt > ARC_TIP[1]) & (h <= hl)) | ((tt <= ARC_TIP[1]) & (rr > ARC_R1) & (h < ARC_C[0]))
        mid = (rr <= ARC_R1) & ~hot
        light = rr <= ARC_R2
        dark = hot & (rr >= ARC_R1 + 0.10) & (rr <= ARC_R1 + 0.24)     # a darker curved stripe in the hot pink
        col = np.where(hot[..., None], hx(DRESS_HOT), hx(CASE))
        col = np.where(dark[..., None], hx(DRESS_DARK), col)
        col = np.where(mid[..., None], hx(DRESS_MID), col)
        col = np.where(light[..., None], hx(DRESS_L), col)
        dress = np.where(view[..., None], col, dress)
    c = np.where((z >= Z_RING1)[..., None], cap, ring)
    c = np.where((z < Z_RING0)[..., None], case, c)
    c = np.where((z < Z_NECK_HAIR)[..., None], dress, c)
    c = np.where((z < Z_HEM + HEM_H)[..., None], hem, c)
    return np.where((z < Z_HEM - 0.001)[..., None], hx(GOLD_S), c)


def f_top(U, V):
    """The slanted top, metallic: bright toward her front right, dark toward the back left, a sharp glint across it."""
    x = (2.0 * U - 1.0) * R_CAPTOP; y = (2.0 * V - 1.0) * R_CAPTOP
    t = np.clip(0.5 + 0.5 * (-0.6 * x - 0.8 * y) / R_CAPTOP, 0.0, 1.0)
    v = 0.25 + 0.55 * t + 0.35 * np.exp(-((t - 0.62) / 0.06) ** 2)
    return ramp(np.clip(v, 0.0, 1.0), METAL_PINK)


FP_TOP = 14.0                                # the window's upper corners are near square, so its frame runs into the ring


def sq_norm(X, Y):
    p = np.where(np.asarray(Y) > 0, FP_TOP, FP)
    return (np.abs(X) ** p + np.abs(Y) ** p) ** (1.0 / p)


def face_norm(x, z):
    return sq_norm((np.asarray(x) - FX) / FA, (np.asarray(z) - FZ) / FB)


def face_dir(a):
    c, s_ = math.cos(a), math.sin(a)
    return FX + FA * math.copysign(abs(c) ** (2.0 / FP), c), FZ + FB * math.copysign(abs(s_) ** (2.0 / FP), s_)


# Her hair in the window, as the photo (Ruz_1.jpg, her face in the mirror), measured in the window's own measure and
# drawn the same: u = x / FA across (-1 her right, +1 her left: the photo's right), d down from the gold ring over the
# window's height.  A thin fringe, a little lower in the middle, a light streak across it; on the photo's right it
# runs down the window's side in a short lock that ends about level with her eyes.
HAIR_TOP, HAIR_H = Z_RING0, Z_RING0 - (FZ - FB)
FRINGE_U = [-1.00, -0.70, -0.45, -0.20, 0.00, 0.20, 0.35, 0.48, 0.60, 0.72, 0.83, 0.92, 1.00]
FRINGE_D = [0.160, 0.155, 0.170, 0.190, 0.195, 0.180, 0.160, 0.150, 0.190, 0.250, 0.320, 0.355, 0.360]
LOCK_U0, LOCK_D0, LOCK_D1 = 9.0, 0.0, -1.0           # no separate lock: the fringe's own right end is the lock


def hair_line(x):
    """Front-view height of the fringe's lower edge."""
    u = np.asarray(x, float) / FA
    return HAIR_TOP - HAIR_H * np.interp(u, FRINGE_U, FRINGE_D)


SIDE_LOCK = LOCK_U0 * FA


def in_hair(x, z):
    x = np.asarray(x, float); z = np.asarray(z, float)
    u = x / FA; d = (HAIR_TOP - z) / HAIR_H
    lock_in = LOCK_U0 + 0.14 * np.clip((d - LOCK_D0) / 0.40, 0.0, None)
    end = LOCK_D1 - 0.08 * ((u - 1.0) / 0.3) ** 2                     # its end rounded
    return (z >= hair_line(x)) | ((u >= lock_in) & (d <= end))


def hair_shine(x, z):
    """The photo's light streak across the fringe, over the parting."""
    u = np.asarray(x, float) / FA; d = (HAIR_TOP - np.asarray(z, float)) / HAIR_H
    t = np.clip((u + 0.52) / 0.50, 0.0, 1.0)
    dc = 0.085 + 0.070 * t - 0.012 * np.sin(math.pi * t)
    w = 0.020 * np.sin(math.pi * t) ** 0.6 + 1e-4
    return np.clip(1.0 - np.abs(d - dc) / w, 0.0, 1.0) * ((u > -0.52) & (u < -0.02))


def f_face(U, V, tint=None):
    """The face in its gold window frame: peach skin with rosy cheeks, the brown fringe swept to her left and down both
    sides.  A tint (colour, strength, fade) shades the skin from the hair line down."""
    x = (2.0 * U - 1.0) * FA; z = FZ + (1.0 - 2.0 * V) * FB
    n = face_norm(x, z)
    d = (1.0 - n) * FB                         # about the distance in from the rim
    c = mix3(hx(FACE), hx(FACE_SH), 0.35 * sstep(FRIM + 0.010, FRIM, d))
    for sx in (-1.0, 1.0):                     # her cheeks, always rosy
        e = ((x - sx * 0.055) / 0.016) ** 2 + ((z - 0.557) / 0.010) ** 2
        c = mix3(c, hx(CHEEKS), 0.45 * np.clip(1.0 - e, 0.0, 1.0) ** 1.5)
    if tint is not None:
        col, k, fade = tint
        t = np.clip((hair_line(x) - z) / fade, 0.0, 1.0)
        c = mix3(c, hx(col), k * (1.0 - t) ** 1.4)
    hair = mix3(hx(HAIR), hx(HAIR_L), 0.85 * hair_shine(x, z))
    c = np.where(in_hair(x, z)[..., None], hair, c)
    rim = sstep(FRIM + 0.0007, FRIM - 0.0007, d)
    return mix3(c, mix3(hx(GOLD), hx(GOLD_L), 0.4 * sstep(FRIM * 0.4, FRIM, d)), rim)


def f_face_tint(tint, fade):
    return lambda U, V: f_face(U, V, (tint[0], tint[1], fade))


def f_arm(U, V):
    """A really short brown sleeve with a gold cuff, then skin, the colour of her hands."""
    sleeve = mix3(hx(CASE), hx(CASE_S), 0.6 * sstep(0.55, 1.0, V))
    cuff = mix3(mix3(hx(GOLD_L), hx(GOLD), sstep(0.0, 0.4, V)), hx(GOLD_S), sstep(0.55, 1.0, V))
    skin = mix3(mix3(hx('#FFF0E0'), hx(FACE), sstep(0.05, 0.5, V)), hx(FACE_SH), sstep(0.6, 1.0, V))
    return np.where((U < 0.33)[..., None], sleeve, np.where((U < 0.67)[..., None], cuff, skin))


def f_leg(U, V): return mix3(mix3(hx('#F878A8'), hx(TIGHTS), sstep(0.0, 0.4, V)), hx(SHOE), sstep(0.55, 1.0, V))
def f_shoe(U, V): return mix3(mix3(hx(TIGHTS), hx(SHOE), sstep(0.05, 0.5, V)), hx('#8E1A40'), sstep(0.6, 1.0, V))
def f_hand(U, V): return mix3(mix3(hx('#FFF0E0'), hx(FACE), sstep(0.05, 0.5, V)), hx(FACE_SH), sstep(0.6, 1.0, V))
def f_antY(U, V): return mix3(hx(ANT_Y), hx('#F0B800'), sstep(0.55, 1.0, V))
def f_antR(U, V): return mix3(mix3(hx('#FF5A4E'), hx(ANT_R), sstep(0.0, 0.4, V)), hx('#B90D14'), sstep(0.6, 1.0, V))
def f_nose(U, V): return mix3(mix3(hx('#FF9A50'), hx(NOSE), sstep(0.1, 0.6, V)), hx('#B8400E'), sstep(0.7, 1.0, V))
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
    cv[:] = hx(CASE)
    P = lambda n, f, **k: paint(cv, BODY_BLOCKS[n], f, **k)
    P('lathe', f_lathe, wrap_u=True); P('top', f_top)
    P('face', f_face); P('face_gloom', f_face_tint(GLOOM, GLOOM_FADE)); P('face_flush', f_face_tint(FLUSH, FLUSH_FADE))
    P('arm', f_arm); P('leg', f_leg); P('shoe', f_shoe); P('hand', f_hand); P('antY', f_antY); P('antR', f_antR)
    P('under', flat(SHOE)); P('plate_rim', flat(GOLD_S)); P('nose', f_nose)
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
        L = 0.0130
        half = 0.0042 * np.clip(1.0 - (xr / L) ** 2, 0.0, 1.0) ** 0.75
        m = (np.abs(xr) <= L) & (np.abs(yr) <= half)
        return m.astype(np.float64), np.broadcast_to(hx(PRINCE_EYE), X.shape + (3,))
    def eye_02(X, Y): return m_ellipse(X, Y, 0.0068, 0.0076) & (Y <= 0.0005)   # half-closed: the dots' lower half

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
        m = m_rbox(X, Y, 0.0085, 0.0058, 0.0012)
        top = Y >= 0.0058 - 0.0022
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
            'eye_05_m': (eye_05, EYES),
            'mouth_01_m': (mouth_01, LIPS), 'mouth_02_m': (mouth_02, LIPS), 'mouth_03_m': (mouth_03, LIPS),
            'mouth_04_m': (mouth_04, LIPS), 'mouth_05_m': (mouth_05, LIPS), 'mouth_06_m': (mouth_06, LIPS),
            'mouth_07_m': (mouth_07, LIPS), 'mouth_08_m': (mouth_08, LIPS), 'mouth_09_m': (mouth_09, LIPS),
            'mouth_10_m': (mouth_10, LIPS), 'mouth_11_m': (mouth_11, LIPS),
            'angry_01_m': (angry_01, ANGER), 'cheak_01_m': (cheak_01, BLUSH), 'shock_01_m': (shock_01, SHOCK),
            'tear_01_m': (tear_01, TEAR)}


def cell_rgba(fn, col, wm, hm, U, V):
    inside = (U > 0.0) & (U < 1.0) & (V > 0.0) & (V < 1.0)
    r = fn((U - 0.5) * wm / FACE_SCALE, (0.5 - V) * hm / FACE_SCALE)
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




# ---------------------------------------------------------------- the body: one surface turned on a lathe
def build_body():
    """Dress, case, ring and lipstick in one smooth surface, with the slanted top and a closed underside.  Weighted
    from the waist up to the head (w_body): the window, the ring and the lipstick move with the head."""
    m = MB('body_m')
    ths = [-math.pi + 2 * math.pi * j / N for j in range(N + 1)]          # the seam at the back
    U = lambda th: th / (2 * math.pi) + 0.5
    V = lambda z: (LATHE_V0 - z) / (LATHE_V0 - LATHE_V1)
    UVL = lambda th, z: uvb('lathe', U(th), V(z))
    out = lambda c: (c[0], c[1], 0.0)
    # the profile's rings, then rings that rise to the slanted top and round over its rim
    rows = [[(lp(t, r, z), UVL(t, z)) for t in ths] for z, r in PROFILE]
    k = 0.5 * (Z_TOP_B - Z_TOP_F) / R_CAPTOP
    dlx, dly = math.sin(TH_LOW), -math.cos(TH_LOW)          # toward the low side, in x and y
    plane = lambda x, y: TAB_TOP - k * (x * dlx + y * dly)
    for f in (0.5, 1.0):
        row = []
        for t in ths:
            z1 = float(top_z(t)) - TOP_ROUND
            z = PROF_Z[-1] + (z1 - PROF_Z[-1]) * f
            r = PROF_R[-1] + (R_CAPTOP - PROF_R[-1]) * f
            row.append((lp(t, r, z), UVL(t, z)))
        rows.append(row)
    edge = []
    for a in (30.0, 60.0, 90.0):
        ta = math.radians(a)
        row = []
        for t in ths:
            r = R_CAPTOP - TOP_ROUND * (1.0 - math.cos(ta))
            x, y, _ = lp(t, r, 0.0)
            z = plane(x, y) - TOP_ROUND * (1.0 - math.sin(ta))
            row.append(((x, y, z), UVL(t, z)))
        rows.append(row)
        edge = row
    for ra, rb in zip(rows[:-1], rows[1:]):
        for j in range(N):
            m.face([ra[j][0], ra[j + 1][0], rb[j + 1][0], rb[j][0]], [ra[j][1], ra[j + 1][1], rb[j + 1][1], rb[j][1]], out, 'body')
    # the slanted top: rings toward its middle
    nrm = (k * dlx, k * dly, 1.0)
    tuv = lambda p: uvb('top', p[0] / (2 * R_CAPTOP) + 0.5, p[1] / (2 * R_CAPTOP) + 0.5)
    rim = [p for p, _ in edge]
    loops = [[(p[0] * s, p[1] * s, plane(p[0] * s, p[1] * s)) for p in rim] for s in (1.0, 0.66, 0.33)]
    for la, lb in zip(loops[:-1], loops[1:]):
        for j in range(N):
            q = [la[j], la[j + 1], lb[j + 1], lb[j]]
            m.face(q, [tuv(p) for p in q], nrm, 'top')
    cen = (0.0, 0.0, TAB_TOP)
    for j in range(N):
        q = [loops[-1][j], loops[-1][j + 1], cen]
        m.face(q, [tuv(p) for p in q], nrm, 'top')
    # the underside of the skirt, closed (the legs come out of it)
    z0, r0 = PROFILE[0]
    for s0, s1 in ((1.0, 0.6), (0.6, 0.25)):
        for j in range(N):
            t0, t1 = ths[j], ths[j + 1]
            m.face([lp(t0, r0 * s0, z0), lp(t1, r0 * s0, z0), lp(t1, r0 * s1, z0), lp(t0, r0 * s1, z0)], [uvb('under', 0.5, 0.5)] * 4, (0, 0, -1), 'under')
    for j in range(N):
        m.face([lp(ths[j], r0 * 0.25, z0), lp(ths[j + 1], r0 * 0.25, z0), (0.0, 0.0, z0)], [uvb('under', 0.5, 0.5)] * 3, (0, 0, -1), 'under')
    return m


# ---------------------------------------------------------------- face plate on the case's front, eyes, mouths, overlays
def plate_y(x, z):
    """The plate lies on a cylinder of radius RP round the body's axis."""
    x = np.asarray(x, float)
    return -np.sqrt(np.maximum(RP * RP - x * x, 1e-9)) + 0.0 * np.asarray(z, float)


PLATE_G = 12                                 # the plate is a PLATE_G x PLATE_G grid, fine enough to follow the case's curve


def plate_pt(u, v):
    """A square grid point (u, v in -1..1) carried onto the rounded rectangle, ring by ring, and onto the plate."""
    rho = max(abs(u), abs(v))
    if rho < 1e-12:
        x, z = FX, FZ
    else:
        du, dv = u / rho, v / rho
        t = 1.0 / float(sq_norm(du, dv))
        x, z = FX + FA * du * t * rho, FZ + FB * dv * t * rho
    return (x, float(plate_y(x, z)), z)


def build_plate(name, block):
    """The rounded-rectangle face plate (the gold frame is its rim), curved round the case.  U, V are linear in x and z."""
    m = MB(name)
    g = [-1.0 + 2.0 * k / PLATE_G for k in range(PLATE_G + 1)]
    uv = lambda p: uvb(block, (p[0] - FX) / (2 * FA) + 0.5, 0.5 - (p[2] - FZ) / (2 * FB))
    out = lambda c: (c[0], c[1], 0.0)
    for a in range(PLATE_G):
        for b in range(PLATE_G):
            q = [plate_pt(g[a], g[b]), plate_pt(g[a + 1], g[b]), plate_pt(g[a + 1], g[b + 1]), plate_pt(g[a], g[b + 1])]
            m.face(q, [uv(p) for p in q], out, 'plate')
    ring = [(g[k], -1.0) for k in range(PLATE_G)] + [(1.0, g[k]) for k in range(PLATE_G)] + \
           [(g[PLATE_G - k], 1.0) for k in range(PLATE_G)] + [(-1.0, g[PLATE_G - k]) for k in range(PLATE_G)]
    for k in range(len(ring)):                 # the frame's edge, back into the case
        pa, pb = plate_pt(*ring[k]), plate_pt(*ring[(k + 1) % len(ring)])
        ia = (pa[0], float(-math.sqrt(max(R_CASE ** 2 - pa[0] ** 2, 1e-9))) + 0.003, pa[2])
        ib = (pb[0], float(-math.sqrt(max(R_CASE ** 2 - pb[0] ** 2, 1e-9))) + 0.003, pb[2])
        m.face([pa, pb, ib, ia], [uvb('plate_rim', 0.5, 0.5)] * 4, lambda c: (c[0] - FX, 0.0, c[2] - FZ), 'rim')
    return m


EYE_X = 0.0340
EYE_Z = 0.5840                # her eyes and mouth placed in the window as the photo's
MOUTH_Z = 0.5410
EYE_PIECES = [('eye_01_m', EYE_Z), ('eye_02_m', EYE_Z - 0.001), ('eye_03_m', EYE_Z - 0.001), ('eye_04_m', EYE_Z),
              ('eye_05_m', EYE_Z)]
MOUTH_PIECES = [('mouth_01_m', MOUTH_Z), ('mouth_02_m', MOUTH_Z), ('mouth_03_m', MOUTH_Z),
                ('mouth_04_m', MOUTH_Z), ('mouth_05_m', MOUTH_Z + 0.002), ('mouth_06_m', MOUTH_Z),
                ('mouth_07_m', MOUTH_Z + 0.001), ('mouth_08_m', MOUTH_Z), ('mouth_09_m', MOUTH_Z),
                ('mouth_10_m', MOUTH_Z), ('mouth_11_m', MOUTH_Z)]
OVERLAYS = {'cheak_01_m': (-0.0560, 0.5570, True), 'tear_01_m': (-0.0340, 0.5460, True),
            'angry_01_m': (0.0600, 0.5910, False), 'shock_01_m': (0.0600, 0.5860, False)}
EYE_CELLS = {}
FACE_TILE_ZMIN = Z_WIN0 - 0.01
SHOWN = ('nose_m', 'antena_m', 'body_m', 'hand_L_m', 'hand_R_m', 'leg_L_m', 'leg_R_m', 'face_01_m', 'eye_01_m', 'mouth_01_m')


def front_y(cx, cz, wm, hm, off):
    xs = np.linspace(cx - wm / 2, cx + wm / 2, 21); zs = np.linspace(cz - hm / 2, cz + hm / 2, 21)
    X, Z = np.meshgrid(xs, zs)
    return float(plate_y(X, Z).min()) - off


def inside_plate(cx, cz, wm, hm, margin=None):
    """Every corner on the skin: inside the frame and below the hair (overlays: inside the plate)."""
    for sx in (-1, 1):
        for sz in (-1, 1):
            x, z = cx + sx * wm / 2, cz + sz * hm / 2
            if margin is None:
                if float(face_norm(x, z)) > 1.0 - FRIM / FB or bool(in_hair(x, z)):
                    return False
            elif float(face_norm(x, z)) > 1.0 - margin:
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
        assert inside_plate(sd * cx, zc, wm, hm, 0.0), (name, sd)   # an overlay may reach over the rim
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


# ---------------------------------------------------------------- antenna: the Prince's, on top of the tab
def antenna_base():
    """Where the antenna stands: on the slanted top, toward its high side (the top of the lipstick's curve)."""
    th = TH_LOW + math.pi
    rho = ANT_RHO * R_CAPTOP
    x, y = rho * math.sin(th), -rho * math.cos(th)
    k = 0.5 * (Z_TOP_B - Z_TOP_F) / R_CAPTOP
    return x, y, TAB_TOP - k * (x * math.sin(TH_LOW) - y * math.cos(TH_LOW))


def build_antenna(spec):
    """The Prince's antenna, built as Burda's is: a cone from the tab's top to a point inside the ball, and the ball."""
    m = MB('antena_m')
    ax, ay, az = spec['JNT_antenna']['head']
    z0, z2 = antenna_base()[2] - 0.010, az - ANT_BALL + 0.006      # the cone's foot sunk a little into the slanted top
    z_apex = az - ANT_BALL + 0.021
    rr = lambda z: ANT_CONE_R * (z_apex - z) / (z_apex - z0)
    zs = [z0, (z0 + z2) / 2, z2]
    th = [2 * math.pi * j / N for j in range(N + 1)]
    rings = [[(ax + rr(z) * math.sin(t), ay - rr(z) * math.cos(t), z) for t in th] for z in zs]
    uvs = [[uvb('antY', j / N, 1.0 - (z - z0) / (z2 - z0)) for j in range(N + 1)] for z in zs]
    tube(m, rings, uvs, lambda c: (c[0] - ax, c[1] - ay, 0.004), 'cone')
    sphere(m, (ax, ay, az), ANT_BALL, lambda d: uvb('antR', 0.5 + 0.4 * d[0], 0.5 - 0.48 * d[2]), 'ball', nlat=8)
    return m, (z0, z2, az - ANT_BALL)


NOSE_GEO = dict(hw=0.0090, z_top=0.5770, z_bot=0.5540, depth=0.0130)   # the Prince's orange triangle


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


# ---------------------------------------------------------------- arms and legs
def build_arm(sd, spec):
    m = MB('hand_L_m' if sd > 0 else 'hand_R_m')
    za = spec['JNT_arm_L']['head'][2]
    xe = spec['JNT_elbow_L']['head'][0]; xh = spec['JNT_hand_L']['head'][0]
    r_ball = 0.022; xc = xh + 0.018
    q_join = 140.0
    xj = xc + r_ball * math.cos(math.radians(q_join)); rj = r_ball * math.sin(math.radians(q_join))
    xr = [R_CASE - 0.030, R_CASE + 0.010, R_CASE + 0.012, R_CASE + 0.026, R_CASE + 0.028, xe, xj]   # sleeve, cuff, arm
    rs = [0.0200, 0.0205, 0.0225, 0.0225, 0.0150, 0.0145, rj]
    ang = [2 * math.pi * j / N for j in range(N + 1)]
    pt = lambda x, r, a: (sd * x, -r * math.sin(a), za + r * math.cos(a))        # a = 0 is the top of the arm
    sv = lambda a: 0.42 - 0.30 * math.cos(a)
    rings = [[pt(x, r, a) for a in ang] for x, r in zip(xr, rs)]
    uvs = [[uvb('arm', (0.15, 0.15, 0.5, 0.5, 0.85, 0.85, 0.85)[i], sv(a)) for a in ang] for i, x in enumerate(xr)]   # sleeve, cuff, skin
    tube(m, rings, uvs, lambda c: (0.0, c[1], c[2] - za), 'arm')
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], (sd * xr[0], 0.0, za)], [uvb('arm', 0.25, 0.5)] * 3, (-sd, 0, 0), 'root')
    cen = (sd * xc, 0.0, za)
    qs = [0.0, 35.0, 70.0, 105.0, q_join]
    hpt = lambda q, a: (sd * (xc + r_ball * math.cos(math.radians(q))), -r_ball * math.sin(math.radians(q)) * math.sin(a),
                        za + r_ball * math.sin(math.radians(q)) * math.cos(a))
    huv = lambda q, a: uvb('hand', 0.5, 0.42 - 0.30 * math.cos(a) * min(1.0, math.sin(math.radians(q)) + 0.3))
    for i in range(len(qs) - 1):
        for j in range(N):
            a, b = ang[j], ang[j + 1]
            m.face([hpt(qs[i], a), hpt(qs[i], b), hpt(qs[i + 1], b), hpt(qs[i + 1], a)],
                   [huv(qs[i], a), huv(qs[i], b), huv(qs[i + 1], b), huv(qs[i + 1], a)], lambda c: vsub(c, cen), 'arm')
    return m


def build_leg(sd, spec):
    m = MB('leg_L_m' if sd > 0 else 'leg_R_m')
    xl = spec['JNT_leg_L']['head'][0]; zk = spec['JNT_knee_L']['head'][2]
    zr = [Z_HEM + 0.020, zk + 0.030, zk, zk - 0.030, 0.026]
    rr = lambda z: 0.0125
    ang = [2 * math.pi * j / N for j in range(N + 1)]
    pt = lambda z, a: (sd * (xl + rr(z) * math.sin(a)), -rr(z) * math.cos(a), z)
    sv = lambda z: 0.40 + 0.35 * float(sstep(0.18, 0.08, z))
    rings = [[pt(z, a) for a in ang] for z in zr]
    uvs = [[uvb('leg', 0.5, sv(z)) for a in ang] for z in zr]
    tube(m, rings, uvs, lambda c: (c[0] - sd * xl, c[1], 0.0), 'leg')
    for j in range(N):
        m.face([rings[0][j], rings[0][j + 1], (sd * xl, 0.0, zr[0])], [uvb('leg', 0.5, 0.9)] * 3, (0, 0, 1), 'top')
    # foot: a small rounded grey shoe, longer toward the toe
    sphere(m, (sd * xl, -0.016, 0.0200), (0.021, 0.036, 0.0200), lambda d: uvb('shoe', 0.5 + 0.3 * d[0], 0.5 - 0.46 * d[2]),
           'foot', nlat=6, w={'JNT_ankle_' + ('L' if sd > 0 else 'R'): 1.0})
    return m


# ---------------------------------------------------------------- weights
def w_body(z):
    a = float(sstep(0.270, 0.350, z))       # waist -> spine_01, round the spine_01 joint (0.320)
    b = float(sstep(0.350, 0.430, z))       # spine_01 -> spine_02, round the spine_02 joint (0.400)
    h = float(sstep(0.430, 0.470, z))       # spine_02 -> head under the window: window, ring and lipstick are the head
    return {'JNT_waist': 1 - a, 'JNT_spine_01': a * (1 - b), 'JNT_spine_02': b * (1 - h), 'JNT_head': h}


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
    """Every mesh with its texture ('body' or 'face') and weight function, in object order.  spec is load_spec()'s
    (stretched) joints: the meshes are built at the art's heights and stretched after (stretch_z)."""
    raw = {n: dict(j, head=[j['head'][0], j['head'][1], unstretch_z(j['head'][2])]) for n, j in spec.items()}
    parts = build_parts_raw(raw)
    out = []
    for m, tex, wf in parts:
        m.v = [(p[0], p[1], stretch_z(p[2])) for p in m.v]
        out.append((m, tex, lambda p, wf=wf: wf((p[0], p[1], unstretch_z(p[2])))))
    return out


def build_parts_raw(spec):
    head = {'JNT_head': 1.0}
    w_head = lambda p: head
    parts = []
    ant, (az0, az1, az_ball) = build_antenna(spec)

    def w_ant(p):
        t = 1.0 if p[2] >= az_ball - 1e-6 else float(np.clip((p[2] - az0) / (az1 - az0), 0.0, 1.0))
        return {'JNT_head': 1 - t, 'JNT_antenna': t}
    parts.append((ant, 'body', w_ant))
    for name, block in (('face_01_m', 'face'), ('face_02_m', 'face_gloom'), ('face_03_m', 'face_flush')):
        parts.append((build_plate(name, block), 'body', w_head))
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
    parts.append((build_nose(), 'body', w_head))
    parts.append((build_body(), 'body', lambda p: w_body(p[2])))
    for sd in (1.0, -1.0):
        parts.append((build_arm(sd, spec), 'body', w_arm(sd, spec)))
    for sd in (1.0, -1.0):
        parts.append((build_leg(sd, spec), 'body', w_leg(sd, spec)))
    names = [m.name for m, _, _ in parts]
    assert len(set(names)) == len(names) and set(SHOWN) <= set(names)
    return parts


def load_spec():
    """The rig spec's joints (at the art's heights), stretched in height as the meshes are (stretch_z)."""
    with open(RIG_SPEC) as fh:
        joints = json.load(fh)['new_style']['joints']
    return {n: dict(j, head=[j['head'][0], j['head'][1], stretch_z(j['head'][2])]) for n, j in joints.items()}


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
    arm = bpy.data.armatures.new(NAME + '_rig')
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
    for p in glob.glob(os.path.join(TEX_DIR, 'rouge_new_*.png')) + glob.glob(os.path.join(REN_DIR, 'Rouge_new_*.png')) + \
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
    mats = {'body': C.img_material('Rouge_new_body', body_png), 'face': C.img_material('Rouge_new_face', face_png, alpha=True)}
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

    C.render_views(os.path.join(REN_DIR, 'Rouge_new'), views=('front', 'side', 'back', 'three'))

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
