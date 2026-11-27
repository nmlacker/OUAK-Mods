# Circuit (서킷): design sheet

Circuit is cousin 34 in the cancelled Katamari Damacy Online (KDO), an NPC who stood by the Katamari factory. This sheet
fixes what they look like, so the old-style model and the new-style model show the same character. Both versions follow
it. Each version then follows its own style rules (`../STYLE_STUDY.md`). The wiki gives no pronouns, so this file and
everything made from it says "they".

## Which pictures is it built from?

The main reference is Katamaripedia's full-front art of Circuit (`File:Circuit_hd.png`, 1,645 × 2,640 pixels). It
shows them standing, facing front, with their hands on their hips. Sizes and colours below were read from its pixels.
A local screenshot of it is `_reference_local/KDO/circuit_wiki_screenshot.jpg`. Two tiny in-game screenshots on the same
page show them from behind (`circuit_back_small.png`): all purple, the pink hem showing at the bottom. The sides, the
back, the depth of every part and the rest pose are designed by eye.

## What does the reference show?

There is no wide Prince head. A purple hood covers the whole head, and its upper corners sweep up and out into two long
horns, like devil horns. The wiki calls them their two antennae. The hood's outer edge runs in one straight line from
each horn tip down to the hood's widest point at brow height, then curves in to the chin. Between the horns the hood
has a low rounded dome.

Each horn ends in a red ball. Under the ball the horn is pink where other cousins' antennae are yellow. There are two
pinks, with zigzag borders between them and the purple: a pale pink section right under the ball, then a stronger pink
band with three teeth on each side biting down into the purple.

The face shows through a shield-shaped window in the front of the hood. The window's top edge is a shallow arc that dips
in the middle, and its sides curve down to a point at the chin. A yellow rim runs round it. Inside, the face is pale
cream. The eyes are small black almonds with pointed ends. The nose is a small orange triangle, point up. The mouth is
a red trapezoid, wider at the bottom: an open mouth.

Below the hood is a long purple robe that widens into a bell. The sleeves are purple, and the hands are rounded purple
mitten ends of the sleeves (no skin shows anywhere but the face). In the picture the hands rest on the hips with the
elbows out; that is a pose, not the rest pose.

Across the middle of the robe's front runs a bright green line like a heartbeat trace: a level line broken by tall,
thin spikes up and down.

The robe's bottom edge turns out a little, and its underside is pink, so the hem shows as a pink band. Below it are thin
slate-coloured legs and pointed slate shoes that point out to the sides.

## How do the picture's sizes fit the rig?

Circuit uses the Prince's joints, from `../Burda/burda_rig_spec.json` (the `new_style` or `old_style` block), so OUAK's
motions and Katamari Forever's fit them as they fit Burda. Where the picture and a joint disagree, the joint wins.

With the shoe soles at the ground and the face centre on `JNT_head`, the picture lines up with the new-style rig by
itself: the chin lands on the neck joint and the shoulders on the arm line. That fit is 0.000496 m per picture pixel.
The old-style rig has a lower head and higher shoulders; fitted the same way (soles at 0, face centre on its `JNT_head`
at 0.711) it is 0.000471 m per pixel, and the sleeves then start at that rig's arm line.

Picture coordinates: x from the face's centre line (pixel column 828), y down from the top. Metres are for the new
style (z = (2599 − y) × 0.000496, x = (column − 828) × 0.000496); scale the old style by 0.95 and move it onto its joints.

| Feature | Picture (pixels) | New style (m) |
|---|---|---|
| Red balls | centres at columns 127 and 1530, row 110, 150 across | x ±0.348, z 1.234, 0.074 across |
| Horn tip under the ball | row 160 | z 1.209 |
| Pale pink / strong pink border (zigzag) | about row 280 | z 1.15 |
| Strong pink / purple border (three teeth a side) | about row 400 | z 1.09 |
| Hood dome top, between the horns | row 454 | z 1.063 |
| Horn's inner edge meets the dome | column 828 ± 215, row 493 | x ±0.107, z 1.044 |
| Hood at brow height (widest, the outer edge's corner) | 742 wide at row 900 | 0.368 wide at z 0.843 |
| Hood at row 1100 / 1200 | 522 / 400 wide | 0.259 / 0.198 wide |
| Chin, hood meets robe | 462 wide at row 1300 | 0.229 wide at z 0.644 |
| Face window with its rim | columns 592–1064, rows 904–1268 | x ±0.117, z 0.660–0.840 |
| Cream face inside the rim | columns 614–1042, rows 938–1244 | x ±0.106, z 0.672–0.823 |
| Rim thickness | 22 to 34 | 0.011 to 0.017 |
| Eyes | rows 1024–1052, outer ends at columns 718 and 938, each about 45 wide | centres x ±0.044, z 0.774; each 0.022 × 0.014 |
| Nose (triangle, point up) | columns 810–846, rows 1042–1102 | 0.018 wide, z 0.742–0.772 |
| Mouth (trapezoid, wide edge down) | about columns 795–860, rows 1110–1180 | about 0.032 wide, z 0.705–0.740 |
| Robe between the arms, chest | 470 wide at row 1600 | 0.233 wide at z 0.495 |
| Robe at row 2000 | 614 wide | 0.305 wide at z 0.297 |
| Bell at the top of the pink hem | 692 wide at row 2280 | 0.343 wide at z 0.158 |
| Hem's lowest point | row 2440 | z 0.079 |
| Pink hem band, seen from the front | rows 2270–2440 | z 0.079–0.164 |
| Green trace | columns 580–1078, rows 1396–2018, line about 22 thick | x ±0.124, z 0.288–0.596, 0.011 thick |
| Trace's level line | about row 1800 | z 0.40 |
| Legs below the hem | 40 wide, centres at columns 683 and 973 | 0.020 wide, x ±0.072 (rig legs at ±0.062) |
| Shoes | about 166 long, pointed | about 0.08 long |
| Total height with the balls | rows 43–2599 | 1.268 |

## Designed by eye (not in the reference)

- Depth: the hood is about 0.30 deep (the Prince's head depth), the robe about 0.75 of its width at each height.
- The horns are the hood's upper corners drawn out, not cones set on a round head (changed 2026-11-26 after the user
  found the first builds' horns and neck odd). From the front, each horn's outer edge is one straight line from under
  its ball down to the hood's widest point at brow height (x ±0.184, z 0.843), and its inner edge one straight line
  from under the ball down to where it meets the dome (x ±0.107, z 1.044). So at its root a horn is as wide as the
  hood's whole upper side, about 0.21 along that slope, and the hood above the brow is a V that widens upward with the
  low dome sitting in its notch. No crease, step or ring shows where horn meets hood: the surface runs on. In section
  the horn is a flattened oval, as deep as the hood at its root and narrowing evenly to round, about 0.03, under the
  ball. Seen from the side it leans back by about 10 degrees, so the tips sit a little behind the face. In the new
  style the horns are still their own mesh (`antena_m`), so they can hide: their root edge follows the hood's surface,
  and with them hidden the hood shows a closed rounded top, with no hole.
- The dome and the back of the hood are plain purple. The horns look the same from behind. The back of the robe is
  plain purple with no trace. The trace is on the front only, between the arm roots.
- The face window is cut into the hood front; the hood's purple wraps round it on all sides. The cream face is a plate
  set slightly back from the yellow rim.
- There is no neck and no collar to see (changed 2026-11-26). From the front the hood's lower sides curve in to the
  chin, and the chin sits right on the top of the robe, which is as wide as the chin there (0.229 at z 0.644) and slopes
  straight out and down to the shoulders and sleeves: sloping shoulders, no flat shelf, no square corner, no ring or
  lip. Seen from the side the hood's back curves down onto the robe's back the same way. The hood's lower part may
  reach a little inside the robe's top, hidden, so turning or nodding the head never opens a gap; nothing between them
  (no stem, ball or stub) may show from any side.
- The robe is closed at the bottom by its pink underside, a shallow pink cone or disc inside the bell, so nothing of
  the inside shows from below. The hem's turned-out lip is pink on its underside and outer face.
- Rest pose: arms straight out to the sides along the rig's arm line, as every OUAK and Forever model rests. The
  sleeves are tubes as thick as the picture's arms (about 0.04 to 0.05), ending in round purple mitten hands.
- The shoes point forward and out at about 45 degrees, so the front view shows them pointing to the sides as in the
  picture.

## Colours

Read from the picture's pixels (the most common exact value of each area). Shades are mine, by eye.

| Use | Hex | Note |
|---|---|---|
| Robe, hood, horns, sleeves, mitten hands | `#473998` | |
| Robe shade | `#33296E` | by eye, for the new style's soft shading and the old style's shade cell |
| Robe light | `#5A4CB4` | by eye |
| Strong pink: hem underside and lip, horn band | `#FF83C5` | |
| Pale pink: horn tip under the ball | `#FED6E8` | |
| Red: balls and mouth | `#FF1107` | |
| Face | `#FAF4E5` | |
| Face shade | `#E6DCC6` | by eye |
| Face window rim | `#FBFA3B` | |
| Green trace | `#64F284` | |
| Legs, shoes, the picture's arm outlines | `#524B77` | |
| Eyes | `#000000` | |
| Nose | `#FFA43D` | |

## Faces (expressions)

Both versions carry Circuit's own four, in this order:

1. neutral: the reference face (almond eyes, triangle nose, open trapezoid mouth).
2. humming: eyes closed as happy arcs (curving up), a small round open mouth. It stands where Burda has her singing
   face, so the same rows use it (OUAK's whistling group).
3. blink: both eyes closed as short straight lines, the neutral mouth.
4. surprised: round black eyes, the mouth a taller trapezoid.

Every face keeps the yellow rim and the cream plate.

### What does the new style add?

A piece for each of OUAK's 23 face slots, named as the slot, plus their humming eyes `eye_hum_m`. Every mouth is in the
mouth red; line mouths are red strokes about as thick as the trace. The overlays sit on the cream inside the rim.

| Piece | Look | Colour |
|---|---|---|
| `eye_01_m` | the black almonds (neutral) | eyes |
| `eye_02_m` | half-closed: thin almonds under a flat lid line | eyes |
| `eye_03_m` | closed: short straight lines (the blink) | eyes |
| `eye_04_m` | round black dots (surprised) | eyes |
| `eye_05_m` | white almonds with a thin black outline | `#FFFFFF` |
| `eye_hum_m` | closed happy arcs (humming), an extra | eyes |
| `face_01_m` | the cream plate | face |
| `face_02_m` | the plate with a blue-grey shade fading down from the top edge | `#5E77A0` |
| `face_03_m` | the plate with a red flush fading down from the top edge | `#E2564A` |
| `nose_m` | a small orange pyramid, point up, standing a little off the plate | nose |
| `mouth_01_m` | the red trapezoid, wide edge down (neutral) | red |
| `mouth_02_m` | open, a rounded square | red |
| `mouth_03_m` | open, arched | red |
| `mouth_04_m` | a wide open grin | red |
| `mouth_05_m` | a big open oval | red |
| `mouth_06_m` | a small round open mouth (humming) | red |
| `mouth_07_m` | open, with a drop of drool | red |
| `mouth_08_m` | a wavy closed line | red |
| `mouth_09_m` | an arched closed line | red |
| `mouth_10_m` | a pucker with a crease beside it | red |
| `mouth_11_m` | a wavy open mouth with drool | red |
| `angry_01_m` | a four-armed anger mark in the upper corner on their left | `#7A82EE` |
| `cheak_01_m` | soft blush on both cheeks | `#FF8AA8` |
| `shock_01_m` | three short gloom lines in the upper corner on their left | `#1C2550` |
| `tear_01_m` | a wavy tear under each eye | `#8FD0D6` |

Which pieces show together is OUAK's choice, per face group (`face_map.json`), not this sheet's.

### What does the old style add?

A card for each of Katamari Forever's face expressions their four don't cover, numbered as Burda's are. As on Forever's
cards, the eyes stay the neutral almonds on every card but the angry one. Every card shows the whole window: the yellow
shield rim, the cream face inside it, and hood purple in the corners outside the shield.

| Card | Forever expression | Look |
|---|---|---|
| 5, open | 1 | a tall open mouth, as wide as the trapezoid |
| 6, smile | 2 | a red trapezoid, wide edge up |
| 7, angry | 3 | steep glaring lids, a red triangle mouth |
| 8, blush and open mouth | 4 | blush, the open mouth |
| 9, pale | 5 | a blue-grey band under the rim's top edge, a paler grey-cream lower face, a small open mouth |
| 10, blush | 6 | blush, the neutral mouth |
| 11, blush and smile | 7 | blush, the smile |

The blush is `#F28AA0`, the pale card's band `#5C7682` and its ashen face `#C9C3B4`.

## What changes for the mod?

- The horns, with their pink tips and red balls, are the antenna piece (`antena_m` in the new style), so when a present
  hides the game's antenna they go and the dome remains. In the old style they are part of `head`, as Burda's antenna
  is.
- The hood's top is about 17 cm above where the Prince's head top is, so presents worn on the head need a raised
  offset. That and the HUD scale are first guesses until a probe run measures them in the game.
</content>
</invoke>
