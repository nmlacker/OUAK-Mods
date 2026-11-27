"""Give Pew's face a black border, like Pis' yellow one, and lift the face off the gun's muzzle.

Run it on the Pew_new.blend that import_llcm.py rebuilt from pew_new.llcm:
  blender -b --factory-startup --python-exit-code 1 --python fix_pew_new.py -- <Pew_new.blend>
It saves that .blend (uncompressed, image paths kept relative) and paints the border texels into the
.blend's own textures/pew_new_body.png. It refuses a .blend it has already fixed (the plates no longer
have 161 vertices), so run it once per fresh import.

What it changes:
- Face plates face_01/02/03_m: the old side wall goes; in its place a band BAND wide runs out from the
  cream face's edge in the face plane, then a short rim back towards the head. The cream face, its UVs
  and its normals stay as they were. Every band and rim corner samples one texel pair painted black in
  that plate's own tile, so the band is one flat black with a hard edge where it meets the cream.
- Texture: in each plate tile, the two texel rows and columns just outside the texels the cream face
  samples become black. The first texel outside the cream's UV edge is left alone, because bilinear
  filtering reads it at the edge. Nothing else in the texture changes.
- The plates and every face piece (eyes, mouths, nose, overlays) move SHIFT towards the front (-Y)
  together, so the plate's rearmost point clears head_m's surface by HEAD_CLEAR. The pieces keep their
  gaps to the plate. Joints, other meshes, names and hide flags stay as they are.
"""
import bpy, bmesh, os, sys, struct, zlib
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

BAND = 0.010          # visible band width in the face plane (Pis' yellow band is about 8.4-10.5 mm)
SHIFT = 0.003         # whole face forward, metres
HEAD_CLEAR = 0.0017   # plate's rear edge in front of head_m's surface (at least 1.5 mm asked)
BLACK = (20, 20, 20)  # sRGB bytes
PLATES = ('face_01_m', 'face_02_m', 'face_03_m')
PIECES = ('eye_', 'mouth_', 'nose_', 'angry_', 'cheak_', 'shock_', 'tear_')
N_PLATE = 161         # vertices of an unfixed plate: 1 + 4 front rings of 32 (cream) + the side ring of 32
RING = 32
RING_A = range(97, 129)   # cream edge
RING_SIDE = range(129, 161)
# band rings: (outward offset from the cream edge, depth behind the cream edge): the flat band, then the rim's
# front corner; one more ring at the same offset sits at the back, HEAD_CLEAR in front of the head
BAND_RINGS = ((0.9 * BAND, 0.0009), (BAND, 0.0015))


def fail(msg):
    raise SystemExit('fix_pew_new: ' + msg)


blend = sys.argv[sys.argv.index('--') + 1]
bpy.ops.wm.open_mainfile(filepath=blend, load_ui=False)
O = bpy.data.objects
for n in PLATES + ('head_m',):
    if n not in O:
        fail('no %s in %s' % (n, blend))
for n in PLATES:
    if len(O[n].data.vertices) != N_PLATE:
        fail('%s has %d vertices, not %d: this .blend is already fixed or is not the imported Pew'
             % (n, len(O[n].data.vertices), N_PLATE))
pieces = [o for o in O if o.type == 'MESH' and (o.name in PLATES or o.name.startswith(PIECES))]
for o in pieces:
    for r in range(4):
        for c in range(4):
            if abs(o.matrix_world[r][c] - (1.0 if r == c else 0.0)) > 1e-6:
                fail('%s is not at identity in world space' % o.name)

# 1. Move the face forward together.
for o in pieces:
    me = o.data
    for v in me.vertices:
        v.co.y -= SHIFT
    if me.shape_keys:
        for kb in me.shape_keys.key_blocks:
            for d in kb.data:
                d.co.y -= SHIFT
    me.update()

# The head's front surface under the widened plate, for the rim's depth.
bm = bmesh.new(); bm.from_mesh(O['head_m'].data); head = BVHTree.FromBMesh(bm); bm.free()


def head_y(x, z):
    hit = head.ray_cast(Vector((x, -5.0, z)), Vector((0, 1, 0)))[0]
    if hit is None:
        fail('head_m is not behind the plate at x %.4f z %.4f' % (x, z))
    return hit.y


def outline_offset(P, d):
    """Offset a closed 2D outline (list of (x, z)) outward by d, keeping every edge parallel (mitred)."""
    n = len(P)
    cx = sum(p[0] for p in P) / n; cz = sum(p[1] for p in P) / n
    N = []
    for k in range(n):
        ex = P[(k + 1) % n][0] - P[k][0]; ez = P[(k + 1) % n][1] - P[k][1]
        l = (ex * ex + ez * ez) ** 0.5
        N.append((ez / l, -ex / l))
    out = sum(N[k][0] * ((P[k][0] + P[(k + 1) % n][0]) / 2 - cx) + N[k][1] * ((P[k][1] + P[(k + 1) % n][1]) / 2 - cz)
              for k in range(n))
    if out < 0:
        N = [(-a, -b) for a, b in N]
    Q = []
    for k in range(n):
        a = N[k - 1]; b = N[k]
        s = 1.0 + a[0] * b[0] + a[1] * b[1]
        Q.append((P[k][0] + d * (a[0] + b[0]) / s, P[k][1] + d * (a[1] + b[1]) / s))
    return Q, N


# 2. Texture: find the body image (shared by the three plates).
mats = {O[n].data.materials[0] for n in PLATES}
if len(mats) != 1:
    fail('the plates use %d materials, expected 1' % len(mats))
mat = mats.pop()
imgs = [nd.image for nd in mat.node_tree.nodes if nd.type == 'TEX_IMAGE' and nd.image]
if len(imgs) != 1:
    fail('%s has %d image textures' % (mat.name, len(imgs)))
img = imgs[0]
W, H = img.size
CH = img.channels
px = list(img.pixels)
painted = set()


def paint(x, y):
    if not (0 <= x < W and 0 <= y < H):
        fail('border texel %d,%d is outside the %dx%d texture' % (x, y, W, H))
    i = ((H - 1 - y) * W + x) * CH
    for c in range(3):
        px[i + c] = BLACK[c] / 255.0
    if CH == 4:
        px[i + 3] = 1.0
    painted.add((x, y))


# 3. Rebuild each plate's edge as a black band.
for name in PLATES:
    o = O[name]; me = o.data
    uvd = me.uv_layers.active.data
    side = set(RING_SIDE)
    # cream UV box (pixels, y down) from the corners of faces that do not touch the side wall
    cu = [(uvd[li].uv.x * W, (1.0 - uvd[li].uv.y) * H)
          for p in me.polygons if not side.intersection(p.vertices) for li in p.loop_indices]
    ulo = round(min(u for u, v in cu)); uhi = round(max(u for u, v in cu))
    vlo = round(min(v for u, v in cu)); vhi = round(max(v for u, v in cu))
    if max(abs(min(u for u, v in cu) - ulo), abs(max(u for u, v in cu) - uhi),
           abs(min(v for u, v in cu) - vlo), abs(max(v for u, v in cu) - vhi)) > 0.25:
        fail('%s: the cream UVs do not end on texel edges' % name)
    # bilinear reads texels ulo-1..uhi and vlo-1..vhi for the cream; the two texels beyond those go black
    for y in range(vlo - 3, vhi + 3):
        for x in (ulo - 3, ulo - 2, uhi + 1, uhi + 2):
            paint(x, y)
    for x in range(ulo - 3, uhi + 3):
        for y in (vlo - 3, vlo - 2, vhi + 1, vhi + 2):
            paint(x, y)
    # one point on the line between the two black left-hand columns: all four bilinear taps are black
    black_uv = ((ulo - 2) / W, 1.0 - ((vlo + vhi) / 2) / H)

    bm = bmesh.new(); bm.from_mesh(me)
    bm.verts.ensure_lookup_table()
    A = [bm.verts[i] for i in RING_A]
    S = [bm.verts[i] for i in RING_SIDE]
    ya = [v.co.y for v in A]
    if max(ya) - min(ya) > 1e-5 or any(v.co.y <= max(ya) for v in S):
        fail('%s: rings are not where the imported plate has them' % name)
    yA = ya[0]
    bmesh.ops.delete(bm, geom=S, context='VERTS')
    dl = bm.verts.layers.deform.verify()
    uvl = bm.loops.layers.uv.active
    P = [(v.co.x, v.co.z) for v in A]
    rings = [A]
    Q = None
    for off, depth in BAND_RINGS:
        Q, N = outline_offset(P, off)
        rings.append([bm.verts.new((x, yA + depth, z)) for x, z in Q])
    y_back = min(head_y(x, z) for x, z in Q) - HEAD_CLEAR
    if y_back - (yA + BAND_RINGS[-1][1]) < 0.0003:
        fail('%s: no room for the rim between the band and the head (SHIFT too small)' % name)
    rings.append([bm.verts.new((x, y_back, z)) for x, z in Q])
    for r in rings[1:]:
        for k, v in enumerate(r):
            for g, w in A[k][dl].items():
                v[dl][g] = w
    new_faces = []
    for i in range(len(rings) - 1):
        r0, r1 = rings[i], rings[i + 1]
        last = i == len(rings) - 2
        for k in range(RING):
            k1 = (k + 1) % RING
            f = bm.faces.new((r0[k], r0[k1], r1[k1], r1[k]))
            f.normal_update()
            want = Vector((N[k][0], 0.0, N[k][1])) if last else Vector((0.0, -1.0, 0.0))
            if f.normal.dot(want) < 0:
                f.normal_flip()
            f.smooth = True
            f.material_index = 0
            for lp in f.loops:
                lp[uvl].uv = black_uv
            new_faces.append(f)
    # hard edges: the cream edge (the cream keeps its own normals) and the rim's outer corner
    for r in (rings[0], rings[-2]):
        for k in range(RING):
            e = bm.edges.get((r[k], r[(k + 1) % RING]))
            e.smooth = False
    bm.to_mesh(me); bm.free()
    # corner normals: Blender's own from the smooth faces and hard edges, stored as custom normals
    # the way import_llcm.py stores them
    me.normals_split_custom_set([(0.0, 0.0, 0.0)] * len(me.loops))
    me.normals_split_custom_set([tuple(c.vector) for c in me.corner_normals])
    me.update()
    print('PLATE %s: %d vertices, cream uv %d..%d x %d..%d px, black at %.1f,%.1f px, rim back y %.4f'
          % (name, len(me.vertices), ulo, uhi, vlo, vhi, black_uv[0] * W, (1 - black_uv[1]) * H, y_back))

def write_png(path, rgba, w, h):
    """8-bit PNG with only IHDR, IDAT and IEND (Blender's own save adds colour chunks and grows the file).
    RGB when every alpha is 255, as the imported texture is. Rows get the PNG filter that sums smallest."""
    a = (np.clip(np.asarray(rgba, dtype=np.float64), 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)
    a = a.reshape(h, w, 4)[::-1]
    if (a[:, :, 3] == 255).all():
        a = a[:, :, :3]
    bpp = a.shape[2]
    x = a.reshape(h, w * bpp).astype(np.int16)
    left = np.zeros_like(x); left[:, bpp:] = x[:, :-bpp]
    up = np.zeros_like(x); up[1:] = x[:-1]
    ul = np.zeros_like(x); ul[1:, bpp:] = x[:-1, :-bpp]
    p = left + up - ul
    pa, pb, pc = np.abs(p - left), np.abs(p - up), np.abs(p - ul)
    paeth = np.where((pa <= pb) & (pa <= pc), left, np.where(pb <= pc, up, ul))
    cands = [x, x - left, x - up, x - (left + up) // 2, x - paeth]
    cands = [(c % 256).astype(np.uint8) for c in cands]
    cost = np.stack([np.abs(c.astype(np.int8).astype(np.int32)).sum(axis=1) for c in cands])
    best = cost.argmin(axis=0)
    raw = b''.join(bytes([int(best[r])]) + cands[best[r]][r].tobytes() for r in range(h))

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    data = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6 if bpp == 4 else 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))
    with open(path, 'wb') as f:
        f.write(data)
    return len(data)


size = write_png(bpy.path.abspath(img.filepath), px, W, H)
img.reload()
print('TEXTURE %s: %d texels painted black, %d bytes' % (img.filepath, len(painted), size))
# Image paths: relative with forward slashes. An image's path is a fixed 1024-byte field written whole, so the
# absolute path the import first gave it would stay behind the relative one's end; overwrite that tail first.
for im in bpy.data.images:
    p = im.filepath_raw
    if p.startswith('//'):
        im.filepath_raw = '/' * 1023
        im.filepath_raw = p.replace('\\', '/')
# The factory start-up's File Browser remembers the home documents folder of whoever runs this, and a .blend saves
# it (open_mainfile with load_ui=False keeps that start-up UI). Overwrite the whole stored text, then leave '//'.
for screen in bpy.data.screens:
    for area in screen.areas:
        for space in area.spaces:
            if space.type == 'FILE_BROWSER' and space.params is not None:
                space.params.directory = b'/' * 512
                space.params.directory = b'//'
had_backup = os.path.exists(blend + '1')
bpy.ops.wm.save_mainfile(filepath=blend, compress=False, relative_remap=False)
with open(blend, 'rb') as fh:
    if os.path.expanduser('~').encode() in fh.read():
        fail('%s still holds the home folder path' % blend)
if not had_backup and os.path.exists(blend + '1'):
    os.remove(blend + '1')   # Blender's own .blend1 of the unfixed import; the import is the backup
print('SAVED', blend)
