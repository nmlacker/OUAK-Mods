# Pew: design sheet

Pew is not a Katamari Damacy Online cousin. They are the fan-made "cousin with a gun" from 2017: a GIF that made the
rounds on Tumblr and was shown to Keita Takahashi on X (`x.com/KeitaTakahacy/status/868871121956986881`, 28 May 2017).
He answered that he had mixed feelings and would not design such a cousin even as a joke, so this one is the fans'
and this mod's, not his. The GIF gives no name; "Pew" and "they" are this file's choices.

The character is the Prince's head drawn as a grey toy pistol: the slide is the head, the face sits on the muzzle end,
the grip is the body, and thin grey arms and legs come out of the grip. This sheet fixes what they look like. Only a
new-style (Once Upon a KATAMARI) model exists; it follows `../STYLE_STUDY.md`'s new-style rules.

## Which pictures is it built from?

The GIF, a turntable of a flat-shaded 3D model, read from four frames at 448 × 299 pixels: a side view with the face
to the left, the same side three-quarters from the front, the other side, and a near-front three-quarter view of the
face. Sizes below were read from the pure side view; colours are the most common values of each area. The back, the
underside, the grip panel and every face but the neutral one are designed by eye.

## What does the reference show?

- The slide is a long grey box with rounded top edges, about 2.6 times as long as it is tall, with its ends cut
  square and softly rounded. A darker panel line on each side makes a U: two lines come down from the top and are
  joined by a line about 60 % of the way down the side.
- The face is on the slide's front end, low on it: pale peach, with solid black eyes that glare (the lids slope down
  toward the nose), a small red triangle nose with its point up, and red pouting lips. The model drops the lips for a
  dark frown (the user's call), so the usual face is a scowl. In the GIF the plate is an
  oval; this model makes it a **rounded square**, as the Prince's own face plate is (the user's call).
- The antenna in the GIF is a thin yellow stalk with a small red ball, standing on the slide's top near the front end,
  about where a front sight would be. The model uses the Prince's antenna there instead (the user's call): his yellow
  cone and red ball, at his sizes and colours, as Burda's build makes it.
- The grip hangs under the slide's rear, its back flush with the slide's back. It is about 0.42 of the slide's length
  deep and about as tall as the slide.
- The trigger is a small grey piece under the slide just in front of the grip, bent back toward it.
- The arms are thin grey rods out of the grip's sides, below the slide, ending in small ball hands.
- The legs are short thin grey rods under the grip, with small feet.
- The undersides of the slide and the grip are near black: a black line along every bottom edge in the GIF.

## Sizes

Read at 0.004 m per pixel, which makes Pew 0.90 m tall with the antenna (the Prince is 1.055). Rounded.

| Feature | Picture (pixels, side view) | Model (m) |
|---|---|---|
| Slide | 217 long × 84 tall | 0.88 long (y −0.695 to 0.185) × 0.34 tall (z 0.445 to 0.785), 0.27 wide |
| Slide top corner radius / bottom | soft / small | 0.058 / 0.022, ends rounded over 0.018 |
| Panel line U | legs at 0.22 and 0.73 of the length from the back, joined at 0.6 of the height down | legs at y −0.009 and −0.457, joined at z 0.581 |
| Grip | 91 deep × 81 tall | 0.37 deep (y ±0.185) × 0.33 visible (z 0.115 to the slide), 0.23 wide |
| Legs below the grip | 28 | 0.115, legs 0.020 thick, 0.09 apart |
| Arms | about 35 long, leaving the grip 40 below the slide | 0.14 long at z 0.285, 0.019 thick, ball hands 0.038 across |
| Antenna | 28 tall, 0.12 of the length from the front | the Prince's: cone 0.060 across at its foot, ball 0.080 across, its centre 0.126 above the slide top (y −0.59, z 0.911) |
| Face plate (rounded square) | — | 0.176 × 0.176, centre z 0.595, domed 6 mm |
| Eyes | 0.39 of the face's width apart | centres x ±0.032, z 0.618 |
| Nose | about 0.02 × 0.03 | z 0.572 to 0.600 |
| Mouth (frown) | the reference's lips, about 0.04 wide | a frown 0.03 wide, centre z 0.534 |

## Designed by eye (not in the reference)

- The grip panel: a rounded rectangle of panel line on each side of the grip.
- The trigger's exact shape: a blade of rounded-box section, 3.8 cm wide and 3 cm thick where it leaves the slide's
  underside 8 cm in front of the grip, hanging 15 cm and curling back toward the grip at the tip. It is part of `head_m`, so it moves with the slide.
- The back of the slide and the grip: plain grey, no lines.
- Rest pose: arms straight out along the arm line, as every OUAK model rests.
- The joint positions (see below) and every face except the neutral one.

## Joints

The Prince's joint positions cannot carry this shape: his arm line (0.588) would sit inside the slide and his hips
(0.263) far above the GIF's tiny legs. So `pew_rig_spec.json` keeps OUAK's 23 joint names and parents, each at rest
with identity rotation, at Pew's own positions read from the picture. `STYLE_STUDY.md` notes that OUAK takes a cousin
with its own joint positions under the same names (Kinoko's arm line is higher). The waist rests at 0.14 against the
Prince's 0.263, which the exporter turns into a −12.345 cm waist offset (FOLW). `JNT_head` sits over the middle of the
grip at the slide's middle height, so the slide nods and turns about a point above the body, not about its own middle.
Nobody has loaded this rig in the game.

## Colours

| Use | Hex | Note |
|---|---|---|
| Grey, sides | `#C3C5C2` | sampled |
| Grey, top light | `#DCDDDA` | by eye |
| Grey, shade | `#9C9E9B` | by eye |
| Panel lines | `#7E807D` | sampled from the arms' grey; a light lip `#E4E5E2` beside each line |
| Undersides | `#2C2D2B` | the black bottom edges |
| Arms, legs, feet, trigger | `#7E807D` | sampled, shade `#5E605D` |
| Face | `#F8E3A8` | sampled `#F5E2A0`, lifted a little; shade `#EED291` |
| Eyes | `#100C0A` | |
| Nose | `#E2271B` | sampled `#D22618` to `#EE2E2D` |
| Mouths other than the frown | `#D82418` | |
| Antenna cone | `#FEF404` | the Prince's antenna yellow, as on Burda (the GIF's stalk is `#E2CF35`) |
| Antenna ball | `#F3131B` | the Prince's antenna red, as on Burda |

## Faces (expressions)

The new style has a piece for each of OUAK's 23 face slots, named as the slot, plus one extra, `eye_aim_m`. Pew always
glares: every open eye is cut by a lid that slopes down toward the nose, so even surprised or blushing faces stay angry.

| Piece | Look | Colour |
|---|---|---|
| `eye_01_m` | the reference's glare: solid black ovals cut low by lids sloping down toward the nose | eyes |
| `eye_02_m` | half-closed: the same, the lids lower and flatter | eyes |
| `eye_03_m` | closed: short lines sloping down toward the nose (the blink) | eyes |
| `eye_04_m` | round black dots, cut by the same sloping lid | eyes |
| `eye_05_m` | white almonds with a thin black outline, cut by the sloping lid | `#FFFFFF` |
| `eye_aim_m` | aiming: their right eye squeezed shut, the left one glaring (extra) | eyes |
| `face_01_m` | the peach rounded-square plate | face |
| `face_02_m` | the plate with a blue-grey shade fading down from the top edge | `#5E77A0` |
| `face_03_m` | the plate with a red flush fading down from the top edge | `#E2564A` |
| `nose_m` | a small red cone, point up and forward | nose |
| `mouth_01_m` | their usual mouth: a frown, a dark downturned line (no lips) | eyes |
| `mouth_02_m` … `mouth_11_m` | as Burda's set: open square, arched, wide grin, big oval, small round, drool, wavy line, arched line, pucker, wavy with drool | lips |
| `angry_01_m` | a four-armed anger mark, upper corner on their left | `#7A82EE` |
| `cheak_01_m` | soft blush on both cheeks | `#FF8AA8` |
| `shock_01_m` | three gloom lines, upper corner on their left | `#1C2550` |
| `tear_01_m` | a wavy tear under each eye | `#8FD0D6` |

Where the Prince whistles (face group 26), Pew takes aim instead: `eye_aim_m` with the game's pucker, no blink.

## What changes for the mod?

The model is built by `new_style/build_pew_new.py`. Without Blender, `new_style/export_pew_noblender.py` writes
`Mod/Cousins/Pew/pew_new.llcm` and its icon straight from that script's meshes (field for field as
`Mod/Tools/export_llcm.py` would), and `new_style/preview_pew_new.py` renders the previews and checks the meshes. Run
in Blender 5.2, `build_pew_new.py` also makes the .blend that `export_llcm.py` reads. The checked-in `Pew_new.blend` and
`fix_pew_new.py` predate the changes below: rebuild the .blend from `pew_new.llcm` with `Mod/Tools/import_llcm.py`, and
do not run `fix_pew_new.py` on it (this model already has its own black ring). The catalog entry, as first written
(the `fit` numbers are first guesses until a probe run measures them):

```json
{
 "id": "pew",
 "name": "Pew",
 "screen": { "id": 101, "page": 4, "slot": 15 },
 "styles": { "new": "Pew/pew_new.llcm" },
 "icons": { "new": "Pew/pew_icon_new.png" },
 "style_names": { "new": "new style (OUAK)" },
 "notes": "The fan-made 'cousin with a gun' (2017): the Prince's head as a grey pistol, the face on the muzzle end. OUAK's 23 joint names at Pew's own positions, resting at identity; the waist rests 12.345 cm lower than the Prince's. A piece for each of OUAK's 23 face slots plus their aiming eyes.",
 "fit": {
  "new": {
   "notes": "First guesses from the model, not measured in the game. Presents on the head joint sit over the grip; the slide reaches 0.70 m forward of it.",
   "joint_offsets": {},
   "present_offsets": {},
   "cannot_wear": [],
   "hud_scale": 1.0,
   "hud_offset": [0, 0, 0]
  }
 },
 "build": {
  "blend": { "new": "Models/Pew/new_style/Pew_new.blend" },
  "face_map": "Models/Pew/face_map.json",
  "rig_spec": "Models/Pew/pew_rig_spec.json",
  "rig_spec_block": { "new": "new_style" }
 }
}
```

## Metallic grey and the black ring (later changes, on request)

Pew is painted metallic grey: his flat sides reflect a dark ground, a sharp bright horizon band and a softer sky, and
his rounded edges catch a sharp glint on their upper corners (`metal_flat`, `metal_round`, `METAL_GREY` in
`new_style/build_pew_new.py`); his arms, legs and trigger are a darker polished grey. A thick black square ring, 0.020
wide, hugs his face plate on the muzzle end (`f_cap_front`, on its own `cap_front` block, so the rear end stays plain).

## The Prince's face (later change, on request)

Pew wears the Prince's face: a peach plate with rosy cheeks, grey almond eyes tilted up at their outer ends, an orange
triangle nose (a three-sided pyramid, `nose_m`) and a small red box of a mouth. The plate is a little smaller (0.150
square, was 0.176) and so is the black ring round it (0.016 wide, was 0.020).

## Sunken face and darker panel lines (later change, on request)

His face sits an inch and a half (0.0381) back into the muzzle end: the end cap has a hole the shape of the plate, and a black
wall runs from its edge back to the plate (`RECESS`, in the plate's own mesh, so the gloom and flush plates sink too).
The plate's dome is 0.004 now. The panel lines on his body are near black (`#16181A`).

