# Cousin style study: Katamari Forever and Once Upon a Katamari

This compares how the two games build a cousin, so a new cousin can be made in either style. It is written for the Long Lost Cousins models (first: Burda). It holds numbers only. The extracted game files it was measured from stay in `../_reference_local/` and never ship.

## What was measured?

For Katamari Forever (PS3, 2009), every character in `kdp_ps3_os.DAT`: 58 of them, entries 220 to 6712. Each has a rig, skinned meshes, textures and two motion packs, and 57 have face cards. All of it was exported to `.blend` and FBX under `_reference_local/KatamariForever/cousins/`.

The last one, entry 6712, was extracted after the other 57 were measured, so the figures below are over 57 unless they say otherwise. Adding it moves the triangle median from 427 to 430 and leaves the range as it is. It has a rig of its own (20 joints), and none of its 171 clips is byte-identical to the Prince's or to 6598's. They were not compared joint by joint, since its joints are numbered differently.

For Once Upon a Katamari (OUAK, 2025), three cousin prefabs from the Addressables bundles: the Prince (`OUJI01_D`), cousin 06 (`OUJI06_D`) and Kinoko (`OUJI29_D`). That is n = 3, so the OUAK numbers are examples, not a survey.

Lengths below are in Blender units: Z up, the cousin faces -Y, +X is the cousin's own left. Forever keeps its own units (the Prince is 5.0 tall with antenna). OUAK is in metres after the bundle's centimetres (the Prince is 1.055 tall).

## How are the bodies proportioned?

As fractions of total height, antenna included. The Forever column is a median. Joint and face rows use all 57 characters (56 for the face). Head rows use the 46 whose head is a separate rigid mesh (43 of them have an antenna), because the others merge head and body.

| Measure | Forever median | OUAK Prince | OUAK 06 | OUAK Kinoko |
|---|---|---|---|---|
| Head width | 0.63 | 0.69 | 0.69 | 0.79 |
| Head bottom | 0.60 | 0.57 | 0.57 | 0.64 |
| Head top, no antenna | 0.88 | 0.84 | 0.84 | 1.00 (no antenna) |
| Head height ÷ head width | 0.44 | 0.40 | 0.40 | 0.45 |
| Antenna base | 0.86 | 0.82 | 0.82 | none |
| Arm line (shoulder joint) | 0.58 | 0.56 | 0.56 | 0.64 |
| Hand joint reach (half span) | 0.33 | 0.35 (hand mesh 0.41) | 0.35 | 0.34 (hand mesh 0.40) |
| Waist joint | 0.32 | 0.25 | 0.25 | 0.27 |
| Head joint | 0.68 | 0.71 | 0.71 | 0.71 |
| Face width | 0.21 | 0.23 (face plate) | 0.23 | painted on the head |
| Face height | 0.18 | 0.17 (face plate) | 0.17 | painted on the head |

Forever heads vary a lot: a quarter of the separate heads are narrower than 0.40 of the height, and the widest is 1.30.

The Forever Prince itself: 4.99 tall, head 3.14 wide and 1.62 deep, head from 2.98 to 4.37, antenna from 4.29 to 4.99, body from 1.28 to 3.63, arms at 2.93, hands reaching 1.67, waist at 1.60, head joint at 3.37.

OUAK heads are wider and flatter than Forever's and sit a little lower, and the OUAK waist joint sits lower. The arm line is almost the same in both games.

## How are the meshes built?

Forever cousins are low poly: 427 triangles median, face cards included. Half of them fall between 403 and 475; the lowest is 275 and the highest 848. Without the face cards the median is 387. The Prince has 419: head with antenna 155, body 76, each arm 44, each leg 34, and 8 face cards of 4 triangles each. The head is an octagonal prism (8 sides) with inset octagonal end caps. Arms and legs are thin box tubes. Meshes are triangle strips, one mesh per body part. 46 of the 57 keep the head and the body as two rigid meshes; the rest merge them into one mesh or skin them.

OUAK cousins are smooth: the Prince's base parts total 5,148 triangles, cousin 06 about 5,500, Kinoko 3,110. Prince parts: `head_m` 1,638 (24 sides around), `body_m` 930 (24 sides), `hand_L_m` and `hand_R_m` 532 each (tube with a round hand), `leg_L_m` and `leg_R_m` 506 each, `antena_m` 380, `nose_m` 28 (a 3D cone), `face_01_m` 84. Cousin 06 reuses the Prince's head mesh and changes the body and the texture. Kinoko has its own head (360 triangles) and no separate face or antenna parts.

## How are they textured?

Forever gives each character one body texture (two on 2 characters) and one texture per face card. Most body textures are a strip of flat colour cells: 32×8 on 32 characters, 64×16 on 4, and one each of 32×16 and 64×32. The other 19 use a larger one, 64×64 on 6, 128×128 on 12 and 256×128 on 1. It holds a pattern (blue polka dots, white drops, coloured stars, hearts, cow spots, sparkles on black) with a few flat colour cells along one edge for the other parts. Face textures are 64×64 on 53 of the 56 characters with faces, and opaque: the card carries the whole face, skin colour included. One character (6028) uses alpha on its face textures. All textures are DXT3. Most are flat fills, and colour changes come from separate cells. A few are painted, such as the fur on 2272 and the leaf on 6256.

OUAK gives each cousin a body atlas: 1024² BC7 for the Prince, 256² BC7 for cousin 06, 1024² DXT1 for Kinoko. It has soft gradients, such as the darker bands at the head ends. Eyes, mouths and cheeks come from one shared 1024² DXT5 face atlas.

## How are faces done?

In Forever, a face is a card in front of the head. On 31 of the 56 characters with faces it is a curved strip of 6 vertices (4 triangles). Others use 2, 6 or 8 triangles, and a few odd heads use 12 to 28. Each of the 56 has 8 cards, one per expression, and shows one at a time. The mushroom cousin (3412) has no face. Two characters (4660, 6028) draw each expression with 3 cards. The cards are stored in the space of the joint that carries the head end: the head joint on 50 characters, the waist on the 6 whose head is merged into the body. The yellow face frame belongs to the head mesh; the card sits inside it.

The card is chosen per motion, not per frame. Forever's system file (`OUJI_SYS.BIN`) has a record for each of the 58 characters and each of the 158 motion slots. Each motion record carries an expression number from 0 to 7, and each character record has a part map that turns that number into one of the character's eight cards. Nothing inside a clip switches cards. The meaning of each number is read off the cards it picks: the angry and electric-shock motions get the angry card, the tired one the pale card, the glad ones the smile.

In OUAK, the face plate is a mesh of its own (`face_01_m` to `face_03_m`, 56 vertices) on the body atlas. Eyes (`eye_01_m` to `eye_05_m`, two quads each, one per eye) and mouths (`mouth_01_m` to `mouth_11_m`, 9 vertices each) are small meshes on the face atlas, swapped at run time. Cheeks, tears, an angry mark and a shock mark are extra overlays. The nose is a small 3D cone.

## How are they rigged?

Forever uses 16 joints: root, waist, head, head end, and shoulder, elbow, hand, thigh, knee and foot on each side. 47 of 57 characters use exactly this hierarchy. Six more have the same joints with the head end hanging from the waist. Three add one joint (1816, 3070, 3412), and the four-legged cousin (5230) adds seven. On 46 characters the head and the body are rigid, one joint each. Face cards are rigid. On the Prince, arms spread over 3 joints with at most 2 influences per vertex, and legs over 3 joints with at most 3. Across all 57, the most influences on one vertex is 1 on 1 character, 2 on 18, 3 on 15, 4 on 16, 5 on 5 and 6 on 2. Each character has two motion packs: 110 and 50 clips (111 and 60 on 6598 and 6712), 30 fps. A clip holds keys per joint: translation, and rotation in degrees applied x, then y, then z.

Most characters share the Prince's clips. 43 of the other 56 play his clips byte for byte in every slot but slot 1, the idle, which every character has in its own version. 1816 carries his clips renumbered for its own joints. The remaining 12 have clips of their own in 4 to 158 slots, and most of those sets come in pairs that hold the same data (3640 and 6370, 2614 and 3070, 5002 and 5914). A character with the Prince's clips also moves its waist by his amounts: the clips' translations are not scaled to the rig's height.

OUAK uses 23 joints, all named `JNT_*`: root, waist, leg, knee and ankle on each side, spine_01, spine_02, shoulder, arm, elbow and hand on each side, neck, head, mouth, nose and antenna. Kinoko has 22. From the Prince's skin data:

- `head_m`, the face plate, eyes, nose and overlays are rigid on `JNT_head`.
- `body_m` spreads over waist, spine_01, spine_02 and neck, with at most 3 influences.
- Hands spread over arm, elbow and hand, with at most 2 influences.
- Legs spread over leg, knee and ankle, with at most 2 influences.
- The antenna uses head and antenna, and the mouth uses head and mouth (2 influences each). On cousin 06 both are rigid.

Joint positions differ per cousin. Kinoko's arm line is higher than the Prince's. So the game accepts a cousin with its own joint positions under the same names.

## What does that give a new cousin?

The old style (Forever) means:

- 400 to 600 triangles, one mesh per part, flat or hard-edged shading, octagonal and box sections.
- One small body texture of flat colour cells: a 128×128 when the cousin has a pattern, otherwise a strip.
- One opaque 64×64 card per expression, a strip of 4 triangles that bends back round the octagonal head.
- A rigid head and rigid face cards.
- Proportions from the Forever medians.
- Forever's own motions. The old style's mapped joints sit at the Forever Prince's joints, scaled, so his 160 clips play on it with only their movement scaled (by 0.211 for Burda). Those clips are Forever's data and stay local.

The new style (OUAK) means:

- 4,500 to 6,000 triangles of smooth, rounded parts with 24-sided sections.
- A body atlas with soft gradients.
- A separate face plate, plus small eye and mouth meshes on a face texture.
- A 3D nose.
- Skin weights as in the Prince's data.
- Proportions from the OUAK Prince.

Both styles are rigged on the 23 OUAK joint names, parents and positions, and every joint rests at identity, as the OUAK Prince's do. OUAK's clips write each joint's turn, so they start from the same frame on her as on him. That is not yet proof that either works in OUAK: nobody has loaded them in Unity or the game. The new style uses the OUAK Prince's joint positions. The old style uses the Forever Prince's joints, scaled to the same height. Joints Forever lacks are placed by eye: spine_01, spine_02, neck, the inner shoulder link, mouth and nose. `Burda/burda_rig_spec.json` lists every joint and where its position came from, and for the old style which Forever joint drives each one.

## How sure is each fact?

These are proven by a test:

- Rebuilding every Forever rest pose from its stored translation, rotation and scale in x, y, z order matches the stored world matrices to 3e-7 on all 58 characters.
- The text "ディップ" on entry 2356's name plate reads unmirrored from outside under the chosen axis mapping. That settles left and right.
- Forever face cards come out upright with v = 0 at the top of the image.
- The x, y, z order for motion rotations is the one that keeps the feet on the ground. In the 8 clips where it differs most from x, z, y, the lowest foot stays within 0.3 units of its rest height. x, z, y lifts the feet 1.1 to 2.5 units and pushes a hand below the floor.

These are inferred, not proven:

- Hermite keys store (time, value, −in slope, out slope). The signs are a best fit: over 25,553 keys, the out slope matches the curve's direction 88% of the time, with a size ratio of 1.01. The game code that reads them was not read.
- Face cards attach to the joint that carries the head end. This rule puts every card of the 56 characters on the front of its head, centred. The game code that attaches them was not read.
- Keys of type 1 (time, value) are treated as linear.
- Motion record k of the system file belongs to section k of the motion packs. The names fit the clips, but the game code that pairs them was not read.
- The expression number of a motion record is its face. The numbers are read from the file; what each means is inferred from the cards it picks.
- Keys of type 3 have a third value that was 0 in all 32,510 seen, so their tangents are flat either way.

These were not decoded: the X3D stage format, the per-mesh render-state blocks, and the material shader ids.
