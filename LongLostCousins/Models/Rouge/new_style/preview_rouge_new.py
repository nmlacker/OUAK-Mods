"""A Blender-free preview of Rouge's new style: builds the meshes and paints the textures with build_rouge_new.py's own
code, then draws them with a small numpy rasteriser (orthographic, z-buffered, smooth normals, texture lookups,
linear-blend skinning for a test pose).  It checks the parts as well: triangle counts, weights, the atlas layout.

    python3 preview_rouge_new.py [out_folder]

It writes preview PNGs (and copies of the two textures) into out_folder, by default ./preview.  These are a check
before a Blender build, not the build's renders: the Blender build (build_rouge_new.py) makes the real ones.
Needs numpy and Pillow.
"""
import os, sys, math
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True
import build_rouge_new as B


# ---------------------------------------------------------------- mesh data
def triangulate(m):
    """-> positions (V,3), per-corner uvs (T,3,2), triangle vertex ids (T,3), groups per triangle"""
    tris, uvs, grps = [], [], []
    for ids, uu, g in m.faces:
        for k in range(1, len(ids) - 1):
            tris.append((ids[0], ids[k], ids[k + 1])); uvs.append((uu[0], uu[k], uu[k + 1])); grps.append(g)
    return np.array(m.v), np.array(uvs), np.array(tris), grps


def smooth_normals(P, T, grps):
    """Per-corner normals, smoothed across faces of the same group (sharp between groups, as in the build)."""
    fn = np.cross(P[T[:, 1]] - P[T[:, 0]], P[T[:, 2]] - P[T[:, 0]])
    acc = {}
    for t, g in enumerate(grps):
        for c in range(3):
            k = (T[t, c], g)
            acc[k] = acc.get(k, 0) + fn[t]
    N = np.zeros(T.shape + (3,))
    for t, g in enumerate(grps):
        for c in range(3):
            v = acc[(T[t, c], g)]
            N[t, c] = v / max(np.linalg.norm(v), 1e-12)
    return N


def weights(m, wf):
    out = []
    for i, p in enumerate(m.v):
        w = {b: x for b, x in (m.wover.get(i) or wf(p)).items() if x > 1e-4}
        tot = sum(w.values())
        out.append({b: x / tot for b, x in w.items()})
    return out


# ---------------------------------------------------------------- posing
def rot(axis, deg):
    a = math.radians(deg); c, s = math.cos(a), math.sin(a)
    x, y, z = np.array(axis, float) / np.linalg.norm(axis)
    return np.array([[c + x * x * (1 - c), x * y * (1 - c) - z * s, x * z * (1 - c) + y * s],
                     [y * x * (1 - c) + z * s, c + y * y * (1 - c), y * z * (1 - c) - x * s],
                     [z * x * (1 - c) - y * s, z * y * (1 - c) + x * s, c + z * z * (1 - c)]])


def skin_mats(spec, pose):
    """joint -> (R, t) with posed = R @ rest + t"""
    W = {}

    def world(n):
        if n in W:
            return W[n]
        h = np.array(spec[n]['head']); par = spec[n]['parent']
        Rl = rot(*pose[n]) if n in pose else np.eye(3)
        if par is None:
            R, o = Rl, h
        else:
            Rp, op = world(par)
            R = Rp @ Rl; o = Rp @ (h - np.array(spec[par]['head'])) + op
        W[n] = (R, o)
        return W[n]
    out = {}
    for n in spec:
        R, o = world(n)
        out[n] = (R, o - R @ np.array(spec[n]['head']))
    return out


def pose_points(P, N, T, wts, mats):
    Pp = np.zeros_like(P); Rv = np.zeros((len(P), 3, 3))
    for i, w in enumerate(wts):
        for b, x in w.items():
            R, t = mats[b]
            Pp[i] += x * (R @ P[i] + t); Rv[i] += x * R
    Np = np.einsum('tcij,tcj->tci', Rv[T], N)
    Np /= np.maximum(np.linalg.norm(Np, axis=-1, keepdims=True), 1e-12)
    return Pp, Np


# ---------------------------------------------------------------- raster
def sample(tex, uv):
    h, w = tex.shape[:2]
    x = np.clip(uv[..., 0] * w - 0.5, 0, w - 1); y = np.clip((1.0 - uv[..., 1]) * h - 0.5, 0, h - 1)
    x0 = np.floor(x).astype(int); y0 = np.floor(y).astype(int)
    x1 = np.minimum(x0 + 1, w - 1); y1 = np.minimum(y0 + 1, h - 1)
    fx = (x - x0)[..., None]; fy = (y - y0)[..., None]
    return (tex[y0, x0] * (1 - fx) * (1 - fy) + tex[y0, x1] * fx * (1 - fy) + tex[y1, x0] * (1 - fx) * fy + tex[y1, x1] * fx * fy)


def render(items, view_dir, res=(720, 900), bg=(0.86, 0.84, 0.92), up=(0, 0, 1), ss=2, transparent=False, px_per_m=None, ground_row=None):
    """items: list of (P (V,3), N (T,3,3), UV (T,3,2), T (T,3), texture HxWx4, alpha bool)"""
    d = np.array(view_dir, float); d /= np.linalg.norm(d)            # from the object toward the camera
    upv = np.array(up, float); right = np.cross(upv, d); right /= np.linalg.norm(right); upv = np.cross(d, right)
    allp = np.concatenate([it[0][it[3]].reshape(-1, 3) for it in items])
    mn, mx = allp.min(0), allp.max(0); ctr = (mn + mx) / 2
    size = max(mx - mn) * 1.15
    W, H = res[0] * ss, res[1] * ss
    scale = min(W, H) / size
    oy = H / 2
    if px_per_m:                                                     # a fixed camera: scale, and the ground on a row
        scale = px_per_m * ss
        ctr = np.zeros(3)                                            # the cousin's axis at the ground
        oy = (ground_row if ground_row is not None else res[1] / 2) * ss
    img = np.ones((H, W, 3)) * np.array(bg); zb = np.full((H, W), -1e9)
    cov = np.zeros((H, W))
    sun = rot((0, 0, 1), 30) @ (rot((1, 0, 0), -40) @ (-d)); sun = sun / np.linalg.norm(sun)
    sun = 0.6 * d + 0.5 * upv + 0.35 * right; sun /= np.linalg.norm(sun)
    fill = 0.4 * d - 0.2 * upv - 0.6 * right; fill /= np.linalg.norm(fill)
    order = sorted(range(len(items)), key=lambda i: items[i][5])      # opaque first
    for idx in order:
        P, Nc, UV, T, tex, alpha = items[idx]
        q = P - ctr
        sx = q @ right * scale + W / 2; sy = oy - q @ upv * scale; sz = q @ d
        for t in range(len(T)):
            a, b, c = T[t]
            xs = np.array([sx[a], sx[b], sx[c]]); ys = np.array([sy[a], sy[b], sy[c]]); zs = np.array([sz[a], sz[b], sz[c]])
            area = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
            if area >= 0:                                            # back-face culled (screen y is down)
                continue
            x0, x1 = int(max(np.floor(xs.min()), 0)), int(min(np.ceil(xs.max()), W - 1))
            y0, y1 = int(max(np.floor(ys.min()), 0)), int(min(np.ceil(ys.max()), H - 1))
            if x0 > x1 or y0 > y1:
                continue
            X, Y = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
            w0 = ((xs[1] - X) * (ys[2] - Y) - (xs[2] - X) * (ys[1] - Y)) / area
            w1 = ((xs[2] - X) * (ys[0] - Y) - (xs[0] - X) * (ys[2] - Y)) / area
            w2 = 1 - w0 - w1
            inside = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
            if not inside.any():
                continue
            z = w0 * zs[0] + w1 * zs[1] + w2 * zs[2]
            sub = zb[y0:y1 + 1, x0:x1 + 1]
            vis = inside & (z > sub)
            if not vis.any():
                continue
            uv = w0[..., None] * UV[t, 0] + w1[..., None] * UV[t, 1] + w2[..., None] * UV[t, 2]
            n = w0[..., None] * Nc[t, 0] + w1[..., None] * Nc[t, 1] + w2[..., None] * Nc[t, 2]
            n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-12)
            col = sample(tex, uv)
            lit = 0.62 + 0.38 * np.clip(n @ sun, 0, 1) + 0.12 * np.clip(n @ fill, 0, 1) - 0.06
            rgb = np.clip(col[..., :3] * lit[..., None], 0, 1)
            if alpha:
                a_ = col[..., 3]
                vis = vis & (a_ > 0.02)
                cur = img[y0:y1 + 1, x0:x1 + 1]
                blend = cur * (1 - a_[..., None]) + rgb * a_[..., None]
                cur[vis] = blend[vis]
                sub[vis & (a_ > 0.5)] = z[vis & (a_ > 0.5)]
            else:
                img[y0:y1 + 1, x0:x1 + 1][vis] = rgb[vis]
                sub[vis] = z[vis]
                cov[y0:y1 + 1, x0:x1 + 1][vis] = 1.0
    img = img.reshape(res[1], ss, res[0], ss, 3).mean(axis=(1, 3))
    if not transparent:
        return Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8))
    a = cov.reshape(res[1], ss, res[0], ss).mean(axis=(1, 3))
    bgc = np.array(bg)
    rgb = np.where(a[..., None] > 0, (img - bgc * (1 - a[..., None])) / np.maximum(a[..., None], 1e-6), 0.0)
    for _ in range(6):                                               # spread the edge colour outward, as render_icons does
        m = a > 0
        acc = np.zeros_like(rgb); n = np.zeros(a.shape)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            mm = np.roll(m, (dy, dx), (0, 1)); acc += np.roll(rgb, (dy, dx), (0, 1)) * mm[..., None]; n += mm
        grow = (~m) & (n > 0)
        rgb[grow] = acc[grow] / n[grow][:, None]; a = np.where(grow, 1e-6, a)
    out = np.dstack([np.clip(rgb, 0, 1), np.where(a > 1e-5, a, 0)])
    return Image.fromarray((out * 255 + 0.5).astype(np.uint8), 'RGBA')


# ---------------------------------------------------------------- checks
def check(parts, spec):
    total = 0
    for m, tex, wf in parts:
        P, UV, T, g = triangulate(m)
        total += len(T)
        wts = weights(m, wf)
        assert all(abs(sum(w.values()) - 1.0) < 1e-9 for w in wts), m.name
        bones = sorted({b for w in wts for b in w})
        assert set(bones) <= set(spec), (m.name, bones)
        nmax = max(len(w) for w in wts)
        uvok = (UV >= 0).all() and (UV <= 1).all()
        print('PART %-12s tris %5d verts %5d  influences<=%d  bones %s%s' % (m.name, len(T), len(P), nmax, ','.join(b[4:] for b in bones), '' if uvok else '  UV OUT OF RANGE'))
        assert uvok, m.name
    print('PART total tris', total)
    return total


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'preview')
    os.makedirs(out, exist_ok=True)
    B.check_layout(B.BODY_BLOCKS, B.BODY_PX)
    B.check_layout({n: c[0] for n, c in B.FACE_CELLS.items()}, B.FACE_PX)
    body = B.paint_body_array()
    face, _ = B.paint_face_array()
    Image.fromarray((np.clip(body[..., :3], 0, 1) * 255 + 0.5).astype(np.uint8)).save(os.path.join(out, 'rouge_new_body.png'))
    Image.fromarray((np.clip(face, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(out, 'rouge_new_face.png'))
    texs = {'body': body, 'face': face}
    spec = B.load_spec()
    parts = B.build_parts(spec)
    total = check(parts, spec)
    assert 4500 <= total <= 6000, total

    def items(pose, face_set=None):
        mats = skin_mats(spec, pose)
        res = []
        shown = set(B.SHOWN) if face_set is None else (set(B.SHOWN) - {'face_01_m', 'eye_01_m', 'mouth_01_m'}) | set(face_set)
        for m, tex, wf in parts:
            if m.name not in shown:
                continue
            P, UV, T, g = triangulate(m)
            Nn = smooth_normals(P, T, g)
            if pose:
                P, Nn = pose_points(P, Nn, T, weights(m, wf), mats)
            res.append((P, Nn, UV, T, texs[tex], tex == 'face'))
        return res
    rest = items({})
    views = {'front': (0, -1, 0), 'side': (1, 0, 0), 'back': (0, 1, 0), 'three': (0.7, -0.7, 0.15), 'side_l': (-1, 0, 0)}
    for name, d in views.items():
        render(rest, d).save(os.path.join(out, 'Rouge_new_preview_%s.png' % name))
        print('wrote', name)
    pose = {'JNT_arm_L': ((0, 1, 0), 55), 'JNT_arm_R': ((0, 1, 0), -40), 'JNT_elbow_R': ((0, 0, 1), -50),
            'JNT_leg_L': ((1, 0, 0), -25), 'JNT_leg_R': ((1, 0, 0), 20), 'JNT_knee_R': ((1, 0, 0), -30),
            'JNT_head': ((1, 0, 0), 8), 'JNT_antenna': ((1, 0, 0), -15)}
    render(items(pose), (0.75, -0.62, 0.2)).save(os.path.join(out, 'Rouge_new_preview_pose_three.png'))
    # a close-up sheet of a few face groups, front view of the head only
    faces = [('normal', ['face_01_m', 'eye_01_m', 'mouth_01_m']), ('whistle', ['face_01_m', 'eye_01_m', 'mouth_10_m']),
             ('surprised', ['face_01_m', 'eye_04_m', 'mouth_05_m']), ('angry', ['face_03_m', 'eye_02_m', 'mouth_04_m', 'angry_01_m']),
             ('gloom', ['face_02_m', 'eye_02_m', 'mouth_09_m', 'shock_01_m']), ('crying', ['face_01_m', 'eye_03_m', 'mouth_11_m', 'tear_01_m']),
             ('blush', ['face_01_m', 'eye_05_m', 'mouth_06_m', 'cheak_01_m']), ('happy', ['face_01_m', 'eye_03_m', 'mouth_03_m'])]
    tiles = []
    for label, fs in faces:
        its = items({}, fs)
        # crop to the muzzle end: render only the head-side items with a tight frame
        head_items = []
        for (P, Nn, UV, T, tx, al) in its:
            keep = (P[T][:, :, 1].max(axis=1) < B.Y_FRONT + 0.02) & (P[T][:, :, 2].min(axis=1) > getattr(B, 'FACE_TILE_ZMIN', -1e9))
            if keep.any():
                head_items.append((P, Nn[keep], UV[keep], T[keep], tx, al))
        tiles.append(render(head_items, (0, -1, 0), res=(300, 340)))
    sheet = Image.new('RGB', (300 * 4, 340 * 2), (220, 216, 234))
    for i, t in enumerate(tiles):
        sheet.paste(t, ((i % 4) * 300, (i // 4) * 340))
    sheet.save(os.path.join(out, 'Rouge_new_preview_faces.png'))
    print('PREVIEW done')


if __name__ == '__main__':
    main()
