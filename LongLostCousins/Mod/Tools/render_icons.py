"""Renders each cousin's select-screen icons from its .blend files, one per style.

Run it with Blender 5.2, headless, from any folder:

    blender -b --factory-startup --python-exit-code 1 --python render_icons.py -- [cousin [style]]

With no arguments it renders every style of every cousin in Cousins/cousins.json. The icon goes beside the style's
.llcm file as <id>_icon_<style>.png, for Burda Cousins/Burda/burda_icon_new.png and burda_icon_old.png.

Each .blend is opened and read, never saved: its SHA-256 is taken before and after, and a changed hash fails the run.
The pose, camera, lights and materials below exist only in memory.

How it matches OUAK's own cousin icons (CustomSkinIconNN, 1024x1024, drawn at a scale of 0.2):
- One fixed camera for every cousin, as the game does: front view, orthographic, 509.5 pixels per metre, centred
  on the cousin's axis, the ground on row 973 of 1024. The Prince (1.055 m with his antenna, head 0.728 m wide) comes
  out 536 pixels tall and 371 wide there, as in his icon.
- The Prince's icon pose: arms 70 degrees below level with the elbows a little open, each leg 3 degrees out, knees
  straight, the default face.
- Near-flat colour: mostly the texture as it is, plus a little soft light from the front and above. No outline, no
  highlights, no cast shadows. Transparent background.
- Checked by rendering the Prince's own model this way: his icon's outline is matched to 0.96 of its area (0.98 on
  the head) and its colours to about 2/255.
- The colour of the outermost pixels is spread a few pixels into the transparent area, so the edge doesn't darken
  when the game scales the icon down.
"""
import bpy
import sys
import os
import json
import math
import zlib
import struct
import hashlib
import tempfile
import numpy as np
from mathutils import Matrix

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(HERE)
COUSINS = os.path.join(MOD, 'Cousins')
LLC = os.path.dirname(MOD)

SIZE = 1024
PX_PER_M = 509.5           # measured on the Prince's icon: 536 px for 1.055 m, 371 px for his 0.728 m head
GROUND_ROW = 973           # row of the soles in the game's icons (0 = top)
EMIT = 0.85                # share of the texture colour shown flat; with SUN and FILL, matched on the Prince's icon
SUN = 0.25                 # strength of the soft key light from the front and above
FILL = 0.08                # strength of a weak light from below, so undersides don't drop to EMIT alone
FILTER_PX = 2.5            # pixel filter width: edges as soft as the game's (it has about twice Blender's default)
BLEED_PX = 8              # how far edge colour is spread into the transparent area
# Turns about the cousin's own body axes (X = her left, -Y = her front, Z = up), as in the posed check render:
# the right side mirrors the left, so the pose is the same whichever way the bones point.
POSE = {'arm': ('Y', 70.0), 'elbow': ('Y', -6.0), 'leg': ('Y', -3.0)}


class IconError(Exception):
    pass


def fail(msg):
    raise IconError(msg)


def sha256(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def read_json(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def pose_rig():
    arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
    if len(arms) != 1:
        fail('expected one armature, found %d' % len(arms))
    arm = arms[0]
    posed = []
    for pb in arm.pose.bones:
        nm = pb.name.lower()
        for k, (ax, deg) in POSE.items():
            if nm.endswith('_' + k + '_l') or nm.endswith('_' + k + '_r'):
                sgn = -1 if (nm.endswith('_r') and ax in ('Y', 'Z')) else 1
                rest = (arm.matrix_world @ pb.bone.matrix_local).to_3x3().normalized()
                turn = Matrix.Rotation(math.radians(deg) * sgn, 3, ax)
                pb.rotation_mode = 'QUATERNION'
                pb.rotation_quaternion = (rest.inverted() @ turn @ rest).to_quaternion()
                posed.append(pb.name)
                break
    if len(posed) != 2 * len(POSE):
        fail('posed %s, expected both sides of %s' % (posed, sorted(POSE)))
    bpy.context.view_layer.update()


def flat_material(m):
    """Swap the material's shading for near-flat colour, keeping its image and alpha."""
    nt = m.node_tree
    tex = [n for n in nt.nodes if n.type == 'TEX_IMAGE' and n.image]
    bsdf = [n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED']
    if len(tex) != 1 or len(bsdf) != 1:
        fail('material %s: expected one image and one Principled BSDF' % m.name)
    tex, bsdf = tex[0], bsdf[0]
    uses_alpha = bsdf.inputs['Alpha'].is_linked
    for n in [n for n in nt.nodes if n.name != tex.name]:
        nt.nodes.remove(n)
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    emit = nt.nodes.new('ShaderNodeEmission')
    emit.inputs['Strength'].default_value = EMIT
    diff = nt.nodes.new('ShaderNodeBsdfDiffuse')
    diff.inputs['Roughness'].default_value = 1.0
    add = nt.nodes.new('ShaderNodeAddShader')
    nt.links.new(tex.outputs['Color'], emit.inputs['Color'])
    nt.links.new(tex.outputs['Color'], diff.inputs['Color'])
    nt.links.new(emit.outputs[0], add.inputs[0])
    nt.links.new(diff.outputs[0], add.inputs[1])
    shader = add.outputs[0]
    if uses_alpha:
        clear = nt.nodes.new('ShaderNodeBsdfTransparent')
        mix = nt.nodes.new('ShaderNodeMixShader')
        nt.links.new(tex.outputs['Alpha'], mix.inputs['Fac'])
        nt.links.new(clear.outputs[0], mix.inputs[1])
        nt.links.new(shader, mix.inputs[2])
        shader = mix.outputs[0]
    nt.links.new(shader, out.inputs['Surface'])


def sun(name, strength, rot_x, rot_z):
    d = bpy.data.lights.new(name, 'SUN')
    d.energy = strength
    d.use_shadow = False
    d.angle = math.radians(30)
    o = bpy.data.objects.new(name, d)
    bpy.context.scene.collection.objects.link(o)
    o.rotation_euler = (math.radians(rot_x), 0.0, math.radians(rot_z))
    return o


def set_up_scene():
    sc = bpy.context.scene
    engines = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items]
    sc.render.engine = 'BLENDER_EEVEE' if 'BLENDER_EEVEE' in engines else 'BLENDER_EEVEE_NEXT'
    sc.render.resolution_x = sc.render.resolution_y = SIZE
    sc.render.resolution_percentage = 100
    sc.render.filter_size = FILTER_PX
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGBA'
    sc.render.image_settings.color_depth = '8'
    for a in dir(sc.render):                       # no file path, date or scene names in the PNG
        if a.startswith('use_stamp'):
            setattr(sc.render, a, False)
    sc.view_settings.view_transform = 'Standard'
    sc.view_settings.look = 'None'
    sc.view_settings.exposure = 0.0
    sc.view_settings.gamma = 1.0
    try:
        sc.eevee.taa_render_samples = 64
    except AttributeError:
        pass
    w = bpy.data.worlds.new('IconWorld')
    w.use_nodes = True
    bg = w.node_tree.nodes['Background']
    bg.inputs[0].default_value = (0, 0, 0, 1)
    bg.inputs[1].default_value = 0.0
    sc.world = w
    for o in list(sc.objects):                     # lights or cameras saved in the file would change the look
        if o.type in ('LIGHT', 'CAMERA'):
            bpy.data.objects.remove(o)
    for m in bpy.data.materials:
        if m.users and m.node_tree:
            flat_material(m)
    cd = bpy.data.cameras.new('IconCamera')
    cd.type = 'ORTHO'
    cd.ortho_scale = SIZE / PX_PER_M
    cd.clip_start, cd.clip_end = 0.1, 100.0
    cam = bpy.data.objects.new('IconCamera', cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    centre_z = (GROUND_ROW - SIZE / 2) / PX_PER_M     # the ground (z = 0) lands on GROUND_ROW
    cam.location = (0.0, -20.0, centre_z)
    cam.rotation_euler = (math.radians(90), 0.0, 0.0)     # looks along +Y at the cousin's front (-Y)
    sun('IconKey', SUN, 55, -20)                   # from the front, above and a little to her right
    sun('IconFill', FILL, 120, 15)                 # from the front and below


def bleed(rgba):
    """Spread the colour of the outermost visible pixels into the transparent ones next to them."""
    rgb = rgba[..., :3].astype(np.float32)
    known = rgba[..., 3] > 0
    rgb[~known] = 0
    for _ in range(BLEED_PX):
        acc = np.zeros_like(rgb)
        cnt = np.zeros(known.shape, np.float32)
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (-1, 1), (1, -1), (1, 1)):
            k = np.roll(np.roll(known, dy, 0), dx, 1)
            c = np.roll(np.roll(rgb, dy, 0), dx, 1)
            acc += c * k[..., None]
            cnt += k
        grow = (~known) & (cnt > 0)
        rgb[grow] = acc[grow] / cnt[grow][:, None]
        known = known | grow
    out = rgba.copy()
    clear = rgba[..., 3] == 0
    out[..., :3][clear] = np.round(rgb[clear]).astype(np.uint8)
    return out


def png_bytes(rgba):
    """An RGBA PNG with the smallest of the five row filters on each row, deflate level 9, no text chunks."""
    h, w, _ = rgba.shape
    a = rgba.reshape(h, w * 4).astype(np.int16)
    left = np.zeros_like(a); left[:, 4:] = a[:, :-4]
    up = np.zeros_like(a); up[1:] = a[:-1]
    ul = np.zeros_like(a); ul[1:, 4:] = a[:-1, :-4]
    p = left + up - ul
    pa, pb, pc = np.abs(p - left), np.abs(p - up), np.abs(p - ul)
    paeth = np.where((pa <= pb) & (pa <= pc), left, np.where(pb <= pc, up, ul))
    cands = [a, a - left, a - up, a - ((left + up) >> 1), a - paeth]
    cands = [(c & 0xFF).astype(np.uint8) for c in cands]
    score = np.stack([np.where(c < 128, c, 256 - c.astype(np.int16)).sum(1) for c in cands])
    pick = score.argmin(0)
    raw = bytearray()
    for y in range(h):
        raw.append(int(pick[y]))
        raw += cands[pick[y]][y].tobytes()

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


def render(entry, style):
    blend = os.path.normpath(os.path.join(LLC, entry['build']['blend'][style]))
    out_dir = os.path.dirname(os.path.join(COUSINS, entry['styles'][style]))
    out = os.path.join(out_dir, '%s_icon_%s.png' % (entry['id'], style))
    hash_before = sha256(blend)
    bpy.ops.wm.open_mainfile(filepath=blend, load_ui=False)
    pose_rig()
    set_up_scene()
    sc = bpy.context.scene
    tmp_dir = tempfile.mkdtemp(prefix='llc_icon_')
    tmp = os.path.join(tmp_dir, 'icon.png')
    sc.render.filepath = tmp
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(tmp)
    px = np.empty(SIZE * SIZE * 4, np.float32)
    img.pixels.foreach_get(px)                     # the stored 8-bit values / 255, bottom row first
    rgba = np.round(px.reshape(SIZE, SIZE, 4)[::-1] * 255).astype(np.uint8)
    bpy.data.images.remove(img)
    os.remove(tmp)
    os.rmdir(tmp_dir)
    sc.render.filepath = '_' * 1023                # nothing of the temporary path stays in memory either
    sc.render.filepath = '//'
    if not rgba[..., 3].any():
        fail('%s rendered empty' % os.path.basename(blend))
    ys, xs = np.nonzero(rgba[..., 3] > 8)
    data = png_bytes(bleed(rgba))
    with open(out, 'wb') as f:
        f.write(data)
    hash_after = sha256(blend)
    if hash_after != hash_before:
        fail('%s changed while it was open (%s -> %s)' % (blend, hash_before, hash_after))
    print('ICON %s %s: %d bytes, opaque box x %d..%d y %d..%d' % (entry['id'], style, len(data), xs.min(), xs.max(),
                                                                   ys.min(), ys.max()))
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
        fail('nothing to render for %s' % args)
    for entry, style in jobs:
        render(entry, style)
    print('ICONS DONE %d file(s)' % len(jobs))


try:
    main()
except IconError as e:
    print('ICONS FAIL', e)
    sys.stdout.flush()
    os._exit(1)
bpy.ops.wm.quit_blender()
