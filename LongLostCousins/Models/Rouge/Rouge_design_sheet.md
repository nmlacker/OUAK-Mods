# Rouge (루즈): design sheet

Rouge is a cousin of the cancelled Katamari Damacy Online (KDO); the wiki gives her pronouns as she/her. Her name
comes from her lipstick-shaped head. Only a new-style (Once Upon a KATAMARI) model exists; it follows
`../STYLE_STUDY.md`'s new-style rules.

## Which pictures is it built from?

Katamaripedia's page on Rouge: the KDO art of her looking at a dress (`File:Rougedress.jpg`, 550 × 413 px, and its
crop `File:Ruz.jpg`), where she stands with her back to us and her reflection gives a front view, and an in-game shot
(`File:Rouge.jpg`). Sizes were read from the reflection at 0.00182 m per pixel, the soles at 0; colours were sampled
from the art (the gold is by eye: the art's gold is small and shaded).

## What does the art show?

- Her head is a lipstick: a hot-pink bullet with a slanted top, a gold ring round its foot, over a brown case. The
  slant's low side is straight at the back; its high corner is over her face, and the antenna stands on that corner.
  The lipstick is painted metallic pink and the ring metallic gold: sharp bright and dark vertical bands, as polished
  metal reflecting a bright sky and a dark ground.
- Her face is in a window in the case, framed in gold: peach skin, rosy cheeks, and her hair laid out in the window as
  the photo `Ruz_1.jpg` (her face in the mirror) shows it: a thin fringe across the top, a little lower in the middle,
  with a light streak across it, running down the window's side on the photo's right (her left) in a short lock that
  ends about level with her eyes. Her eyes, nose and mouth sit in the window where the photo has them (eyes at about a
  quarter of the way down). On request she wears the Prince's face: grey almond eyes tilted up at their outer ends, an
  orange triangle nose and a small red mouth.
- The brown case is her hair: a head block round her face window, a narrower neck under it, one plain brown. Over the
  gold ring the lipstick has a slightly darker pink band.
- Her dress, as the photo's back view (`File:Ruz.jpg`), the same at the front and the back: a brown upper body; hot pink
  rising up the viewer's left side to a little over half the way to the neck (the brown chest above it), with a darker
  pink curved stripe in it, and curving in to a point left of the middle; and two curved
  stripes, medium and light pink, arcs round a point below the hem on the viewer's right. Her sleeves are really short,
  brown with gold cuffs, and her arms are skin. She is skinnier than the art.
- She wears hot-pink tights. The antenna is the classic yellow cone and red ball, on the lipstick's top.

## Sizes

| Feature | Model (m) |
|---|---|
| Lipstick | radius 0.128 (skinnier than the art's 0.136), from the ring (0.668) to a top slanted from 0.885 (back) to 0.950 (front), its rim rounded over 0.018 |
| Gold ring | z 0.630 to 0.668, radius 0.132 |
| Case (hair) | radius 0.122 from z 0.470 to 0.630, rounding in to a neck of radius 0.094 (z 0.434 to 0.446) that flares into the dress |
| Face window | a square window 0.180 wide, from z 0.515 (just below her mouth) up into the gold ring, its corners square, its rim a gold frame 0.010 wide |
| Dress | from the neck flaring to the hem at 0.180 (radius 0.162; the art's is 0.190); gold hem 0.022 tall |
| Arms | at z 0.44: a brown sleeve to 0.010 past the case, a gold cuff 0.014 wide, then skin 0.030 thick and peach hands |
| Legs | hot-pink tights 0.025 thick, pink shoes |
| Antenna | the Prince's: cone 0.060 across at its foot, ball 0.080 across, centre 0.126 above the top at (0, -0.066), on the high corner over her face (z 1.061) |

## Designed by eye

The light panels' exact curve, the fringe's exact line, the shoes, the triangle nose (the art shows none) and every face.

## Taller than the art

On request she is a little taller than the art: the meshes and joints are built at the art's sizes, then stretched in
height (`stretch_z` in the build script): the tights 0.035 longer, the dress 0.020 longer, everything above raised by
0.055. The sizes in the table above are before that stretch.

## Joints

OUAK's 23 joint names and parents at Rouge's own positions (`rouge_rig_spec.json`), every joint resting at identity:
her arms leave the case below the face window (0.44; the Prince's are at 0.588), her waist rests at 0.25 (0.291 after the
height stretch: a waist offset of +2.79 cm), and `JNT_head` sits at the window's bottom edge (0.47). The whole body is one lathe-turned mesh
(`body_m`): weighted from the waist up through spine_01 and spine_02 to the head, so the window, the ring and the
lipstick move with the head and the dress with the waist.

## Colours

| Use | Hex |
|---|---|
| Lipstick / light / shade | `#F84878` (sampled) / `#F87898` / `#C8305E` |
| Gold / light / shade | `#F0C848` / `#FBE590` / `#B88A28` |
| Case and hair (plain brown) | `#7A4626` |
| Dress: hot pink / its dark stripe / medium stripe / light stripe | `#E8346E` / `#B81E54` / `#F268A6` / `#F68EC0` |
| Band over the gold ring | `#D8366A` |
| Tights / shoes | `#F0407A` / `#C8285E` |
| Skin / shade / cheeks | `#FFE2C8` / `#F2C8A8` / `#F8A0A0` |
| Hair in the window / its streak | `#8E6038` / `#E2B88C` |
| Eyes / mouth / nose (the Prince's) | `#6E7078` / `#E02818` with `#8E1010` / `#F06A28` |
| Antenna cone / ball | `#FEF404` / `#F3131B` (the Prince's) |

## What changes for the mod?

`cousins.json` lists Rouge (id `rouge`). The `.llcm` and icon are made by `new_style/export_rouge_noblender.py`
straight from `new_style/build_rouge_new.py`'s meshes, without Blender (`new_style/preview_rouge_new.py` renders the
previews and checks the meshes). She is on page 4 of the select screen in slot 18 (id 104), and in the mod window's
Long Lost Cousins tab. No `Rouge_new.blend` is checked in yet: `build_rouge_new.py` makes it when run in Blender, or
`Mod/Tools/import_llcm.py` can rebuild one from `rouge_new.llcm`.
