# The .llcm format, version 1

## What is an .llcm file?

One cousin in one style, ready to build at run time: her skeleton, her skinned meshes already in Unity's units, axes and
winding, her textures as PNG files, and the tables that say which face pieces or cards show for each of OUAK's face
groups. `cousins.json` lists the files. `Tools/export_llcm.py` writes them from the `.blend` files and
`Tools/check_llcm.py` reads them back and checks them.

## What is in cousins.json?

A JSON object whose `cousins` array has one entry per cousin. The mod reads these keys of an entry:

| Key | What it holds |
|---|---|
| `id` | the cousin's id string (`burda`), the same as the INFO chunk's |
| `name` | the display name (`Burda`) |
| `styles` | style key to .llcm path, relative to this `Cousins/` folder: `{"new": "Burda/burda_new.llcm", "old": "Burda/burda_old.llcm"}` |
| `style_names` | style key to the words the tab shows after the name: `new style (OUAK)`, `old style (Katamari Forever)` |
| `icons` | style key to her icon on the game's select screen, a 1024x1024 PNG made by `Tools/render_icons.py`, relative to this `Cousins/` folder: `{"new": "Burda/burda_icon_new.png", "old": "Burda/burda_icon_old.png"}` |
| `notes` | plain text about the cousin, for people |
| `fit` | style key to how the player's presents and the stage HUD fit her (optional; a style without one gets no offsets, no list and scale 1). Inside: `joint_offsets` (game joint name to `[x, y, z]` cm in her joint's own axes), `present_offsets` (present id to `[x, y, z]` cm added to its joint's), `cannot_wear` (present ids she never shows), `hud_scale` (her marker's scale in the stage HUD copy, 0.5 to 1.5), `hud_offset` (`[x, y, z]` cm there), `notes` (where the numbers come from). Each offset is 3 numbers under 200 cm; anything else stops cousins.json from loading. |
| `forever` | style key to what that style needs to play Katamari Forever's motions from the player's own copy (optional; a style without one plays this game's motions). Inside: `scale` (her cm per Forever unit, 21.1 for Burda's old style), `axes` (3 signs: her axis i is `axes[i]` times Forever's axis i, `[-1, 1, -1]`), `joints` (her joint name to the Forever Prince's node, 0 to 255, that drives it; her other joints keep their rest pose under their parents), `states` (an OUAK Animator state name to `[pack, slot]`, pack 1 or 2 and slot 0 to 157, the Forever clip played while the game's Animator is in that state; other states play this game's motion), `notes`. Indices and numbers only: the curves, clip lengths, speeds, loop and face bytes are read from the player's copy at run time. Anything out of range stops cousins.json from loading. |

`build` (the source .blend per style, the face map and rig spec, paths relative to the LongLostCousins folder) is read
only by the two tools; the mod ignores it, as it ignores `about`, `version` and any key it does not know.

## What units and axes does it use?

Everything is in the local space of the game's `model` node (the child of the Animator object that holds `JNT_root`):
centimetres, +X the character's right, +Y up, +Z forward (the way she faces), Unity's left-handed frame. The mod puts
its marker root under `model` at position 0, rotation identity, scale 1, so "model space" below is also the marker's
space.

- Positions are model-space points. They are the rest pose; the bones move them.
- Normals are unit vectors in the same axes.
- Triangles are already in Unity's winding: for a triangle (a, b, c), `Vector3.Cross(b - a, c - a)` points the way of
  its vertex normals, out of the surface. Use them as they are.
- UVs are as Blender has them (V up from the bottom of the image). They are right when the texture is uploaded with
  its rows from the bottom: `OUAKModAPI.Png.Decode(bytes).RgbaBottomUp()` into `LoadRawTextureData`. A loader that
  uploads rows from the top would need V = 1 - V.
- Every joint rests at identity rotation, so a joint's bind pose is a pure translation (see MESH).

## How is the file laid out?

All numbers are little-endian. Nothing is aligned inside a chunk: read fields one after another (a `BinaryReader`
does this). `u8 u16 u32` are unsigned, `i8` is signed, `f32` is an IEEE float.

The file starts with a 16-byte header:

| Offset | Type | Field |
|---|---|---|
| 0 | char[4] | `LLCM` (bytes 4C 4C 43 4D) |
| 4 | u16 | version, 1. Reject any other. |
| 6 | u16 | flags: bit0 = the face is cards (old style), bit1 = the face is slot pieces (new style). Exactly one is set. |
| 8 | u32 | number of chunks |
| 12 | u32 | file length in bytes |

Then the chunks, each:

| Type | Field |
|---|---|
| char[4] | tag |
| u32 | payload length in bytes, without the padding |
| payload | |
| 0 to 3 zero bytes | padding to a multiple of 4 |

The next chunk starts at `payloadStart + ((length + 3) & ~3)`. Version 1 files hold these chunks in this order:
`STRS`, `INFO`, `SKEL`, `FOLW`, one `TEXR` per texture, `MATL`, one `MESH` per mesh, `FACE`. A reader should skip a
tag it does not know. Texture, material and mesh indices are their order of appearance, counting from 0. A string
field holds an index into the STRS table; 0xFFFF means none. "None" for a u8 index is 0xFF.

## What does each chunk hold?

### STRS: the string table

| Type | Field |
|---|---|
| u16 | string count N |
| N records | below |

Each string record:

| Type | Field |
|---|---|
| u16 | byte length L |
| L bytes | UTF-8, no terminator |

### INFO: which cousin and style

| Type | Field |
|---|---|
| u16 string | cousin id, as in cousins.json (`burda`) |
| u16 string | display name (`Burda`) |
| u16 string | style key (`new` or `old`) |
| u16 string | the marker root's name (`llc_Burda_new`) |
| u16 string | the source .blend's file name, no folders |

### SKEL: her joints

| Type | Field |
|---|---|
| u8 | joint count J (23) |
| J records | below |

Each joint record, 44 bytes:

| Type | Field |
|---|---|
| u16 string | name (`JNT_root`, `JNT_waist`, ...) |
| i8 | parent joint index, -1 for the top joint. Always less than the joint's own index. |
| u8 | follow bits: bit0 copy the game joint's localRotation, bit1 add the FOLW offset to the game joint's localPosition, bit2 copy its localScale |
| f32[3] | rest localPosition, relative to the parent joint (the top joint: relative to the marker root) |
| f32[4] | rest localRotation x, y, z, w (always 0, 0, 0, 1 in version 1) |
| f32[3] | rest model position (the sum of the localPositions up the chain) |

Name the mod's bone objects `llc_` + name. The game joint to follow is the one with the same name under the game's
`model/JNT_root`. All 23 joints have bits 0 and 2; only JNT_waist has bit1.

### FOLW: the waist rule

| Type | Field |
|---|---|
| u8 | entry count E (1) |
| E records | below |

Each entry, 14 bytes:

| Type | Field |
|---|---|
| u8 | joint index (JNT_waist) |
| u8 | mode: 1 = her localPosition = the game joint's localPosition + offset |
| f32[3] | offset, cm |

Burda: new style (0, 0, 0) and old style (0, 7.415, 0), each within float rounding (the new style's stored offset is
about (0, -3e-7, 2e-6)). Joints without bit1 keep their rest localPosition.

### TEXR: one texture

| Type | Field |
|---|---|
| u16 string | name (the PNG's file name) |
| u16 | width |
| u16 | height |
| u8 | encoding: 0 = the bytes are a PNG file |
| u8 | flags: bit0 the PNG has an alpha channel, bit1 sRGB colour |
| u8 | filter: 0 point, 1 bilinear |
| u8 | wrap: 0 repeat, 1 clamp |
| u32 | byte count |
| bytes | the PNG file, exactly as on disk |

The PNGs are 8-bit RGB or RGBA, not interlaced; `OUAKModAPI.Png.Decode` reads them. Width and height match the
decoded image. Every texture in version 1 is bilinear and clamped.

### MATL: materials

| Type | Field |
|---|---|
| u8 | material count K |
| K records | below |

Each material record, 4 bytes:

| Type | Field |
|---|---|
| u16 string | the Blender material's name |
| u8 | texture index |
| u8 | 1 if the Blender material blends with the texture's alpha, else 0 |

The mod does not build materials from this. It clones the game material named by each mesh's template and sets this
material's texture on `_BaseMap` and `_MainTex`.

### MESH: one skinned mesh

| Type | Field |
|---|---|
| u16 string | name, the Blender object's (`body_m`, `eye_01_m`, `face_1_neutral`) |
| u8 | role, below |
| u8 | flags: bit0 hidden in the .blend, bit1 casts shadows. Other bits are 0. |
| u8 | face slot: the index into the face component's `faceObject` array (0..22), 0xFF for none |
| u16 string | that slot's name (`eye_01`), none when no slot |
| u8 | card number 1..11 (old style), 0 for none |
| u8 | material index |
| u16 string | template: the game renderer object whose shared material to clone (`body_m`, `eye_01_m`, `tear_01_m`, `cheak_01_m`) |
| u8 | root bone, a joint index (JNT_waist) |
| u8 | bone count B |
| u8[B] | the bones array: joint index per entry, ascending |
| f32[3] | bounds min, model space, at rest |
| f32[3] | bounds max |
| u16 | vertex count n |
| u16 | triangle count t |
| f32[3] x n | positions |
| f32[3] x n | normals |
| f32[2] x n | UVs |
| u8[4] x n | bone indices per vertex, into this mesh's bones array (not joint indices) |
| u16[4] x n | bone weights per vertex: they sum to exactly 65535, largest first; an unused entry has weight 0 and index 0 |
| u16[3] x t | triangles, vertex indices, Unity winding |

The payload is 41 + B + 44n + 6t bytes.

Roles:

| Role | What it is | Where the mod puts it | When it shows |
|---|---|---|---|
| 0 | body part | the marker root | always |
| 1 | face plate (face_01..03) | under `faceObject[slot]` | with its slot |
| 2 | eye or mouth | under `faceObject[slot]` | with its slot |
| 3 | overlay (angry, cheek, shock, tear) | under `faceObject[slot]` | with its slot |
| 4 | extra face piece (eye_sing_m) | the marker root | only by a FACE show list |
| 5 | face card (old style) | the marker root | one card at a time, by FACE |

Building the Unity mesh: `vertices`, `normals`, `uv` and `triangles` straight from the arrays (the triangle array is
the 3t indices in order). `boneWeights[i]` = `boneIndex0..3` from the u8 indices and `weight0..3` = u16 / 65535f. The
renderer's `bones[k]` is the mod's bone for joint `bones[k]` of this mesh. `bindposes[k]` = the translation by minus
that joint's rest model position p: identity with m03 = -p.x, m13 = -p.y, m23 = -p.z (`Matrix4x4.Translate(-p)`).
`rootBone` is the root bone's object. The slot name lets the mod check that `faceObject[slot].name` is that slot
before it parents a piece there.

### FACE: which pieces or cards show

| Type | Field |
|---|---|
| u8 | slot count S (23) |
| u16 string x S | the face component's slot names, in `faceObject` order |
| u8 | the blink eye's slot index (2, eye_03) |
| f32 | blink: seconds open (3.0) |
| f32 | blink: seconds closed (0.15) |
| u8 | blink card (old style 3, new style 0) |
| u8 | the card the blink card replaces while the game blinks (old style 1, new style 0) |
| u8 | the card to show when the model has no face component (old style 1, new style 0) |
| u8 | group count G (28, OUAK's face groups) |
| G group records | below, group 0 first |
| u8 | expression count X (8 old, 0 new) |
| u8 x X | the card per Katamari Forever face expression 0..X-1 |
| u8 | row count W (77 old, 0 new) |
| W row records | u8 motion row, then u8 card; ascending by row |

Each group record, 12 + A + H bytes:

| Type | Field |
|---|---|
| u8[4] | the slots the game turns on for face plate, eye, mouth, overlay (0xFF for none) |
| u8[4] | new style: the mesh indices of her face plate, eye, mouth and overlay in this group (0xFF for none); old style: all 0xFF |
| u8 | old style: the card for this group; new style: 0 |
| u8 | 1 if she blinks in this group |
| u8 | show count A |
| u8 x A | mesh indices: extra renderers shown only while this group is current |
| u8 | hide count H |
| u8 x H | mesh indices: her slot pieces forced off while this group is current |

The current group is the face component's `prevFaceIndex`. Outside 0..G-1 (for example -1 after SetNoFace) no show
or hide list applies and no card shows.

New style: the game shows and hides the slot objects, and her pieces follow them. The show and hide lists cover the
groups where her face differs from the game's. Burda has one, group 26 (the whistle, where she sings): show
`mouth_05_m` (a second renderer of that mesh under the marker root) and `eye_sing_m`, hide `eye_01_m`, `eye_03_m`
(the game's blink would lay it over her singing eyes) and `mouth_10_m`. Where a slot is null or the face component is
missing, a slot piece shows only if it is one of group 0's pieces.

Old style: show the group's card. While the game blinks (`playBlink` and `blinkState == 1`) and that card is the
blink-over card, show the blink card instead. The rows and expressions are for playing Forever's motions later; slice
one does not use them.

## How do I check a reader against these files?

| File | Bytes | SHA-256 | Chunks | Strings | Meshes | Vertices | Triangles | Textures |
|---|---|---|---|---|---|---|---|---|
| Burda/burda_new.llcm | 274,560 | b486e15dd0b99b59bac0994ddf2df5083f46a8d3412f9a43ad2e2a3bdc468180 | 40 | 87 | 32 | 4,195 | 5,941 | 2 |
| Burda/burda_old.llcm | 75,840 | 7dc82c843574559ae94bfa430a19e0caab8c4c52c072606814705c9f97bd26f4 | 35 | 93 | 17 | 974 | 587 | 12 |

A few values to compare (floats within float rounding): in burda_new.llcm, mesh 0 is `antena_m` (bones: joints 19
and 22), mesh 4 is `head_m` (1,194 vertices, 1,571 triangles), mesh 8 is `eye_01_m` (slot 0, the only eye not hidden)
and mesh 31 is `eye_sing_m`; group 26 shows meshes 20 and 31 and hides 8, 10 and 25. Joint 1 is JNT_waist at
localPosition (0, 26.345, 0). In burda_old.llcm, mesh 6 is `face_1_neutral` (card 1) and JNT_waist sits at
(0, 33.760, 0).

## How are the files made?

```
blender -b --factory-startup --python-exit-code 1 --python Tools/export_llcm.py -- [cousin [style]]
python -P Tools/check_llcm.py --blender <blender.exe> --slots <expressions_OUJI01_D.json> --work <scratch folder>
```

The exporter never saves a .blend and writes the same bytes each run. The checker re-derives every value from the
.blend (through its own Blender dump), the rig spec, `face_map.json` and the game's face slot list, and its
`--selftest` shows it fails on a flipped triangle and on a joint moved 0.02 cm.
