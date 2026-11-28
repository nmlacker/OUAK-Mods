import bpy, math, mathutils

def clean():
    bpy.ops.wm.read_factory_settings(use_empty=True)

def img_material(name, image_path=None, color=(0.8, 0.8, 0.8, 1), alpha=False, unlit=True):
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree; nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    bsdf = nt.nodes.new('ShaderNodeBsdfPrincipled')
    bsdf.inputs['Roughness'].default_value = 0.85
    if 'Specular IOR Level' in bsdf.inputs: bsdf.inputs['Specular IOR Level'].default_value = 0.1
    nt.links.new(bsdf.outputs[0], out.inputs[0])
    if image_path:
        tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = bpy.data.images.load(image_path, check_existing=True)
        tex.interpolation = 'Linear'
        nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
        if alpha:
            nt.links.new(tex.outputs['Alpha'], bsdf.inputs['Alpha'])
            try: m.surface_render_method = 'BLENDED'
            except Exception: pass
    else:
        bsdf.inputs['Base Color'].default_value = color
    return m

def render_views(out_prefix, target_height=None, center=None, res=(720, 900), views=('front', 'side', 'back'), bg=(0.86, 0.84, 0.92)):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE' if 'BLENDER_EEVEE' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'BLENDER_EEVEE_NEXT'
    sc.render.resolution_x, sc.render.resolution_y = res
    sc.render.film_transparent = False
    for a in dir(sc.render):                      # no file path, date or scene names in the PNG text chunks
        if a.startswith('use_stamp'): setattr(sc.render, a, False)
    sc.view_settings.view_transform = 'Standard'
    w = bpy.data.worlds.new('W'); sc.world = w; w.use_nodes = True
    w.node_tree.nodes['Background'].inputs[0].default_value = (*bg, 1); w.node_tree.nodes['Background'].inputs[1].default_value = 1.0
    # bounds
    mn = mathutils.Vector((1e9,) * 3); mx = mathutils.Vector((-1e9,) * 3)
    for o in sc.objects:
        if o.type != 'MESH' or o.hide_render: continue
        for c in o.bound_box:
            v = o.matrix_world @ mathutils.Vector(c); mn = mathutils.Vector(map(min, mn, v)); mx = mathutils.Vector(map(max, mx, v))
    ctr = (mn + mx) / 2 if center is None else mathutils.Vector(center)
    size = max(mx.x - mn.x, mx.z - mn.z, mx.y - mn.y) * 1.15
    cam_d = bpy.data.cameras.new('C'); cam_d.type = 'ORTHO'; cam_d.ortho_scale = size
    cam = bpy.data.objects.new('C', cam_d); sc.collection.objects.link(cam); sc.camera = cam
    sun_d = bpy.data.lights.new('S', 'SUN'); sun_d.energy = 3.0
    sun = bpy.data.objects.new('S', sun_d); sc.collection.objects.link(sun)
    fill_d = bpy.data.lights.new('F', 'SUN'); fill_d.energy = 1.2
    fill = bpy.data.objects.new('F', fill_d); sc.collection.objects.link(fill)
    dirs = {'front': (0, -1), 'side': (1, 0), 'back': (0, 1), 'side_l': (-1, 0), 'three': (0.7, -0.7)}
    paths = []
    for v in views:
        dx, dy = dirs[v]; dist = size * 3
        cam.location = (ctr.x + dx * dist, ctr.y + dy * dist, ctr.z)
        cam.rotation_euler = (math.radians(90), 0, math.atan2(dx, -dy))
        sun.rotation_euler = (math.radians(50), 0, math.atan2(dx, -dy) + math.radians(30))
        fill.rotation_euler = (math.radians(70), 0, math.atan2(dx, -dy) + math.radians(-120))
        p = f'{out_prefix}_{v}.png'; sc.render.filepath = p
        bpy.ops.render.render(write_still=True); paths.append(p)
    # The output path is a fixed 1024-byte buffer: setting '//' alone leaves the old absolute path behind the
    # terminator, and a saved .blend keeps those bytes. Overwrite the whole buffer first.
    sc.render.filepath = '_' * 1023
    sc.render.filepath = '//'
    return paths
