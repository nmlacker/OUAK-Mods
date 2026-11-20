# Burda: notes on the two models

Burda (부르다) is cousin 26 of the cancelled Katamari Damacy Online, a singer whose head is a sideways microphone. This folder holds two original models of her. `old_style/` is built the way Katamari Forever (2009) builds its cousins, `new_style/` the way Once Upon a Katamari (2025) does. Each version is made from nothing by its own Blender script.

## What is in this folder?

| Path | What it is |
|---|---|
| `Burda_design_sheet.md` | What she looks like: shapes, sizes read off the reference, colours, her faces. Both versions follow it. |
| `burda_rig_spec.json` | The 23 joints of each version: name, parent, position, and where each position came from. For the old style, also which Katamari Forever joint drives each joint when it plays Forever's motions. |
| `face_map.json` | Which of her face pieces (new style) or cards (old style) show for each face OUAK asks for, and for each of Forever's eight face expressions. |
| `../forever_clip_map.json` | Which Katamari Forever clip the old style plays for each row of OUAK's motion table, with both clips' lengths, the speeds, and why each clip was picked. |
| `old_style/build_burda_old.py` | Builds the old-style model, textures, renders, `.blend` and FBX. |
| `old_style/Burda_old.blend`, `Burda_old.fbx` | The old-style model, rigged and skinned. |
| `old_style/textures/` | `burda_old_body.png` (128×128) and eleven face cards `burda_old_face_1.png` to `_11.png` (64×64). |
| `old_style/renders/` | Front, side and back views, plus two posed views. |
| `new_style/build_burda_new.py` | Builds the new-style model, textures, renders, `.blend` and FBX. |
| `new_style/Burda_new.blend`, `Burda_new.fbx` | The new-style model, rigged and skinned. |
| `new_style/textures/` | `burda_new_body.png` (256×256) and `burda_new_face.png` (512×512, with alpha). |
| `new_style/renders/` | Front, side and back views, plus two posed views. |
| `*/bl_common.py` | A small shared helper for materials and renders. |

`../STYLE_STUDY.md` explains how the two games build a cousin, with the numbers both versions follow.

## What were the models made from?

Her only reference is one cropped in-game screenshot on Katamaripedia (page `Katamari_Damacy_Online/Cousins`, file `Singer_Cousin.jpg`). It shows her from the front, from the hips up. Her legs, shoes, back and sides are designed by eye.

How each style looks comes from the style study. It was measured on cousin models extracted from a local copy of each game: all 58 characters of Katamari Forever, and the Prince and two cousins of Once Upon a Katamari. Those extracted files stay on the machine that made them and are not part of this folder.

The build scripts read nothing from any game. Each reads only `burda_rig_spec.json` and the textures it has just painted. Every vertex and every pixel is made by the script.

## How many polygons does each version have?

All counts are triangles.

| Part | Old style | New style |
|---|---|---|
| Head (sleeve, collars, grille ends, hair, antenna; old style also the nose) | 233 | 1,571 |
| Antenna (separate in the new style) | in the head | 432 |
| Face plates, 3 of them (new style) | (cards, below) | 24 each |
| Nose | in the head | 28 |
| Body (dress, trim, chest, shoulders, beads) | 154 | 1,252 |
| Each arm (old style: a tube closed at the shoulder) | 38 | 624 |
| Each leg | 40 | 600 |
| Face cards, 11 of them (old style) | 4 each | |
| Eyes, 6 sets (new style) | | 4 each |
| Mouths, 11 (new style) | | 8 each |
| Face overlays, 4 (new style: blush, tears, anger mark, gloom lines) | | 26 in all |
| Total | 587 | 5,941 |

Forever's 58 characters have a median of 430 triangles (range 275 to 848). The OUAK Prince's base parts total 5,148, cousin 06 about 5,500.

## What did each version copy from its game's style?

The old style follows Katamari Forever:

- A faceted, low-poly body with hard edges above 75° and smooth shading elsewhere.
- One 128×128 body texture painted in flat colour cells.
- The face as a card with an opaque 64×64 painted texture, one card per expression. Each card lies on the front of the octagonal head and bends back over the face above it, the way Forever's cards do. There are eleven: her four, and seven more so that each of Forever's eight face expressions has its own (the first is her neutral card). Only the first card shows; the other ten are hidden, in the FBX too.
- Proportions and joint positions from the Forever Prince, scaled to the OUAK Prince's height.
- Forever's own motions (see "Can the old style move like a Forever cousin?" below).

The new style follows Once Upon a Katamari:

- Smooth, rounded parts, split the way the OUAK Prince is split: `head_m`, `antena_m`, `face_01_m`, `nose_m`, `body_m`, `hand_L_m`, `hand_R_m`, `leg_L_m`, `leg_R_m`.
- A piece for each of OUAK's 23 face slots, named as OUAK names its own: five eyes (`eye_01_m` to `eye_05_m`), three face plates (`face_01_m` to `face_03_m`), eleven mouths (`mouth_01_m` to `mouth_11_m`) and four overlays (`angry_01_m`, `cheak_01_m` in OUAK's spelling, `shock_01_m`, `tear_01_m`). Her singing eyes, `eye_sing_m`, are an extra. Eyes, mouths and overlays are small sheets over the plate, drawn on one 512×512 atlas with alpha. The three plates differ only in their tint on the body texture, and the hair lies over them on `head_m`. Only `face_01_m`, `eye_01_m` and `mouth_01_m` show; the others are hidden, in the FBX too.
- A 3D nose.
- One 256×256 body texture.
- Proportions and joint positions from the OUAK Prince.

## What was measured, and what was designed by eye?

Measured:

- The head's layout from the reference: sleeve, grille ends, window, lip, side bands, eye spacing, nose, lips, antenna, dress top. The design sheet lists each size.
- Every colour, sampled from the reference. The silver was lifted by eye because the crop is dark.
- The old style's joints: the Forever Prince's, scaled by 0.211. The new style's joints: the OUAK Prince's.
- The old style's body proportions from the Forever Prince, the new style's from the OUAK Prince.

Designed by eye:

- Everything the reference does not show: legs, shoes, the back and sides of the head and dress, the hem height.
- Joints Forever has no match for, in the old style: both inner shoulder links, spine_01, spine_02, neck, mouth and nose. They sit at the fractions OUAK uses.
- The three extra faces (singing, blink, surprised).
- The grille's diamond count (12 round the head, 6 across the visible half), the number of beads, the curl and lock shapes, the dress dots and sparkles.

The build scripts note the main departures from the design sheet next to the values:

- Old style: the total height is 1.060, not 1.053, and the antenna ball is 0.10 W, not 0.11 W. The window runs to 0.71 H and the lip is 0.29 H (sheet: 0.69 H and 0.31 H). The Forever head's slight forward pitch is not copied, so the lower part of the card stays upright.
- New style: the nose follows the reference's height-to-width ratio (about 1.6) rather than the sheet's 0.13 H, which came out too short on this head. The grille diamonds are 1.25 times wider than tall, as measured on the reference. The 10 necklace beads have 8 sides; rounder beads do not fit the triangle budget.

## Was the work reviewed?

Yes. Three reviewers per version looked at likeness to the reference, fidelity to the game's style, and technical soundness. A fourth tried to refute each finding by measuring it again. Of 37 findings, 23 were confirmed and worth fixing, and the verifiers added 3 the reviewers had missed. All 23 were fixed, along with 2 of the 3 extras. Among them: a too-wide hair lock and silver bands that did not shade with the lip (old style), flat grille ends, too many dress dots, a hard crease at the wrist and beads buried in the head (new style), and hidden expressions that came back visible from the FBX (both).

The faces added on 2026-11-19 had one more review per version, of every piece at full size and at a quarter of it. It asked for gloom lines in navy rather than her hair's black, set lower under the hair, a stronger gloom tint, a bigger anger mark, and a pucker moved to one side with a crease (new style), and a wider open mouth and a bigger triangle mouth on the angry card (old style). Those were fixed. The spec check then found the gloom lines' top row tilted up past its front-facing limit, so they moved 4 mm lower. Two small points were left: the tears start about 1.5 cm under the half-closed eyes (new style), and on her right cheek the hair lock's flick touches the edge of the blush (old style).

One finding waited for the user: in side and three-quarter views the old style's face window stood out from the head like a box, where Forever's cards bend back round the head. The user chose the fold. The window zone is now on the same octagon as the rest of the head (8 fewer triangles), and each card folds over the front and upper front faces. Its rows sit 1.4 to 3.5 mm in front of the head. The texture runs evenly down the whole card, so every painted feature keeps its place in the front view. In a front close-up, 0.3% of pixels change by more than 8/255: the card's edges, the nose outline and a faint shading shift where the upper half now tilts back.

## Will the rig work in Once Upon a Katamari?

Probably, but nobody has loaded it in Unity or the game yet. Both versions use OUAK's 23 joint names and parents, and their joint positions match the spec. Every joint rests at identity, as the OUAK Prince's do, so OUAK's clips turn her joints from the same starting frame as his. A check computes each joint's rest rotation from the FBX's own transform fields, the way Unity is expected to read them, and fails on any rotation. Unity itself was not run.

The rig object is named `model`, as in the Prince's prefab. OUAK's clips find a joint by its path below the object that holds the Animator, such as `model/JNT_root/JNT_waist`. Of the 21 paths the Prince's clips use, 20 exist in both FBX files. The last one, used by the two spin clips, matches no joint of the Prince's either.

The FBX is in metres, with every node at scale 1. OUAK's numbers are centimetres: the Prince's waist rests 26.345 above his root, and 99 of his 104 clips set it there. So she needs a scale of 100 on import. In Unity that should be the model's Scale Factor; it has not been tried.

`model` is not the file's only top node: the new FBX has 33 top nodes and the old one 18, because the meshes sit beside the rig rather than under it (corrected 2026-11-20; an earlier version of this note said otherwise). If the FBX is ever imported into Unity, the Animator has to sit on the object above `model` so the clip paths start at `model/JNT_root`. The mod itself doesn't import the FBX: it builds her at run time from data exported from the .blend (`../../Mod/Cousins/FORMAT.md`).

The new style's 23 joint positions are copied from the OUAK Prince, so OUAK's animations should fit her. The old style's come from the Forever Prince, scaled to the same height. Besides the joints, the new style takes a few overall sizes from the OUAK Prince (head width, sleeve radius, hem width, body depth), and the old style its proportions from the Forever Prince. The meshes and textures themselves share nothing with any game model.

The old style's skinning follows the brief, not Forever's data. Most Forever characters use 2 to 4 influences per vertex, and seven of them go up to 5 or 6. The old style uses at most 1 on the head, 2 on arms and legs and 3 on the body.

`face_map.json` maps her faces onto OUAK's. OUAK shows a face by switching 23 objects (5 eyes, 3 face plates, 11 mouths, 4 overlays), and its code picks one of 28 face groups for each motion row. The new style has a piece for every slot, named as the slot, so each group shows on her as OUAK combines it, with one change: her singing face stands in for the Prince's whistle. She blinks as the Prince does, with `eye_03_m` for 0.15 s every 3 s. On the old style, a row that keeps OUAK's clip shows the card that fits its face group's mood (gloom groups the pale card, the anger groups the angry card, and so on). These are my choices; the game has not shown them.

## Can the old style move like a Forever cousin?

Yes. The user asked for the old style to use the original Katamari Forever animations when it is selected. Burda's old-style joints sit at the Forever Prince's joints, scaled by 0.211, so his clips fit her without changes. All 160 of them (pack p1: 110 clips, pack p2: 50; 4,238 frames at 30 per second) have been retargeted onto her rig. Played back next to the Prince's rig, every driven joint lands within 0.006 mm of his joint, scaled. A second, separate check rebuilt his joints straight from the raw motion data instead of his Blender export: over all 160 clips her joints land within 0.007 mm and turn the same way to within 0.0002°. Those few thousandths of a millimetre are the rounding of the joint positions in the rig spec.

`burda_rig_spec.json` (block `old_style`, key `forever_motions`) lists which Forever joint drives each of her joints. Sixteen are driven. The seven Forever has no match for (spine_01, spine_02, neck, both inner shoulders, mouth, nose) stay at rest under their parents, because the Prince's head and arms hang straight off his waist. Each driven joint copies the Forever joint's turn in world space and its movement times 0.211, so the two rigs' different bone directions do not matter.

The clips themselves are Forever's data, so they are not in this folder, and the `.blend` and FBX here carry no animation. The animated copy exists only on the machine that made it. My default for a mod is to read the clips from the player's own copy of Katamari Forever at run time and apply the map above.

### Which Forever clip plays when?

`../forever_clip_map.json` answers it for each of the 109 rows of OUAK's motion table: the Forever clip the old style plays, why, both clips' lengths, and the state speed that keeps Forever's pace. Forever's system file names its motions, and those names were matched against OUAK's row names and against how the clips move. Of the 81 rows OUAK's code asks for, 46 get a Forever clip: 26 where the names and the pose comparison agree, 5 where the names fit two clips and the pose picks one, 13 on the names alone, 1 on the pose alone and 1 by my choice. 31 rows keep OUAK's clip (the car, emotes Forever lacks, the select screen, pulling), and 4 have no clip in OUAK either.

OUAK's clips set the waist at the Prince's height, so on those 31 rows the old style needs her waist raised by about 7.4 in OUAK's numbers. Six picks need a look in the game. On the turns on the spot and the side brakes (rows 16, 17, 20 and 21) the two sides tie on pose, so the side rests on the comparison's mirror test, and on the turns it goes against the names. On rows 65 and 101 the Forever clip does not loop where OUAK's state does.

### How does Forever pick a face card?

Per motion. Each motion record in Forever's system file carries a face expression from 0 to 7, and each character's part map turns that into one of its eight cards. Nothing in a clip switches cards. Her old style has a card for each of the eight. Expression 0 shows her neutral card; 1 to 7 have their own: open mouth, smile, angry, blush with an open mouth, pale, blush, and blush with a smile. `face_map.json` lists them.

### Should a cousin use its own clip set?

Burda uses the Prince's set, idle included, because her joints are his. 43 of the 56 other Forever characters play his clips byte for byte in every slot but slot 1, the idle, which each has in its own version.

## What still needs the in-game check?

- The import: the scale of 100 and Preserve Hierarchy (see above), then her joints and meshes next to the Prince's in one scene.
- Her limbs under OUAK's clips. The rest frames match on paper.
- Which clip and face each motion row shows on the old style, starting with the six rows the clip map flags.
- Materials after Unity imports the FBX. Both versions use plain textured materials, not OUAK's shaders.
- How the alpha face pieces sort against the face plate and against each other, such as the tears under the eyes.
- The old style's cards: OUAK has no face cards, so a mod has to show and hide them by `face_map.json`.
- Reading Forever's clips from the player's own copy of Katamari Forever at run time.
- The select-screen icon. OUAK's are 1024×1024 DXT5 sprites drawn at a fifth of that size. Hers is not drawn yet.

The in-game probe is on hold until the API/Patch work says it can run.

## What was found but left as it is?

- Old style: the grille's crosshatch breaks in a ring where the end cap's flat mapping meets the barrel's mapping, seen from the side. Each of the three remedies tried made another view worse.
- Old style: the grille ends are slightly flatter than the reference's, by about as much as the reference's two ends differ from each other.
- New style: the arms reach about 5 mm into the collars at rest. The OUAK Prince's arms reach about 1 cm into his head the same way.
- New style: the reference's chest is darker than her face because the head shades it. Painting that shadow into the texture would double up with the game's lighting.

## How were the models checked?

Each version was rebuilt from its script with its outputs deleted first, then checked:

- The rig: 23 joints with the spec's names, parents and positions, in the `.blend` and again after re-importing the FBX.
- The meshes: names, weights per part, the triangle budget, every closed part facing outward, and renders with backface culling on. The new style's eye and mouth pieces must be flat and face the front. Its overlays follow the head's curve: each must be an open sheet with every face turned to the front, lying between the plate and the eyes at least 0.5 mm from each. A throwaway model proves the depth rule fails on an overlay sunk to the plate. Each old-style card must be an open sheet, every face of it turned to the front, lying 0.5 to 5 mm outside the head. A throwaway model proves this card check fails on a card turned backwards, one lifted 10 mm off the head and one pushed into it.
- The textures: names, sizes and alpha.
- No game data: no mesh shares more than 2% of its vertices with any of 1,019 meshes from both games (mirrored copies included), and no texture comes within 35/255 of any of 537 game textures.
- No private data: a byte search of every file, in plain text and UTF-16, for user names, home folders, temporary folders and absolute install paths.
- Hidden pieces: re-importing each final FBX brings the hidden pieces back hidden, 10 of the old style's 17 meshes and 21 of the new style's 32, the same ones as in the `.blend`.
- Every face rendered on the final head of each version (the 28 OUAK face groups, the white eyes and the pucker on the new style; all 11 cards on the old), with no clash between the face pieces and the hair or window. The new style's build also checks that each overlay keeps clear of the hair, the lock, the window side and the eyes.
- The necklace: on the final new-style mesh, the two end beads touch the head by 0.4 mm and the other eight are clear.
- The old style's Forever motions: every clip played back on her rig and the Prince's, and again against the raw motion data (see above), and one frame of every clip rendered. The animated FBX's 160 takes match the raw data to 0.003 mm. Blender connects none of her bones when it re-imports that file, since they all rest pointing up. The delivered `.blend` and FBX hold no actions or takes.
- The joint frames: every `JNT_*` joint of each FBX rests at identity, computed from the file's transform fields as Unity is expected to read them. Self-test files with a rolled joint, a tilted rig, Z-up axes and a pre-rotation each fail this check.
- OUAK's clip paths: 20 of the 21 paths the Prince's clips use exist in each FBX. The 21st matches no joint of the Prince's either.
- The FBX units: every node is at scale 1, and against the earlier export every vertex, joint and weight is the same in metres.
- The face map: all 28 of OUAK's face groups rendered on both versions from `face_map.json`. The render fails if the map names a piece or card the model lacks.
- The test pose: it lowers the arms and bends the elbows, knees and head about her body's own axes, so it looks the same whichever way the bones point.

Nobody has opened the models in the Blender UI, Unity or the game yet. The new style was posed only in the check's one test pose. The old style was also rendered in one frame of each Forever clip.

## How do I rebuild a version?

Run its script with Blender 5.2 from any folder:

```
blender -b --factory-startup --python old_style/build_burda_old.py
blender -b --factory-startup --python new_style/build_burda_new.py
```

Each script writes into its own folder and overwrites the `.blend`, the FBX, the textures and the front, side and back renders. The two posed renders (`*_pose_front.png`, `*_pose_three.png`) come from the check that bends the rig, not from the build script.

The old style with Forever's motions is made by a separate local tool (`../../_reference_local/tools/bl_burda_kf_motions.py`). It reads the old-style `.blend` and the Forever Prince's export and writes its result under `_reference_local/` only. The build scripts never read it.
