# Burda (부르다): design sheet

Burda is cousin 26 in the cancelled Katamari Damacy Online (KDO), a playable singer. This sheet fixes what she looks like, so the old-style model and the new-style model show the same character. Both versions follow it. Each version then follows its own style rules (see `../STYLE_STUDY.md`).

The only reference is one cropped in-game screenshot on Katamaripedia (`Katamari_Damacy_Online/Cousins`, file `Singer_Cousin.jpg`). It shows her from the front, from the hips up, with one arm raised. Everything below the hips and everything behind her is designed by eye.

## What the reference shows

The head is the Prince's sideways cylinder. Each end is a microphone head: a dark, rounded grille ball criss-crossed by black lines in a diamond pattern, about as wide as the cylinder or slightly wider. Between each grille and the middle of the head sits a silver collar.

The middle of the head is a silver sleeve with a square window cut in the front for the face. The silver is thick below the face (a lip) and thin at the sides. Above the face window, the top of the middle section is black hair.

Inside the window, the face is dark brown. Black hair comes down over the top of the face with a curl at the top left and a lock down the left side (her right side). The eyes are short black slanted lids with a confident, half-closed look. The nose is a small orange triangle. The lips are a short red bar.

The antenna is the classic yellow cone with a red ball, on top of the middle section.

Her neck and shoulders are bare, dark brown. She wears a pink bead necklace.

Her dress is a strapless magenta dress with a sweetheart neckline edged in light pink. It is covered in white polka dots, with a few white four-point sparkles among them.

Her arms are thin, dark brown and bendy, ending in small round hands.

## Sizes measured on the reference

Read off the screenshot in pixels, so they are rough: the head is turned slightly and the crop is small. W is the head's full width, H its height.

| Feature | Size |
|---|---|
| Head height | 0.44 W (the Forever median is also 0.44) |
| Silver sleeve | the middle 0.5 W |
| Each grille end | 0.25 W, the same diameter as the sleeve, rounded like a barrel end |
| Face window | 0.35 W wide, from the top of the sleeve down to 0.69 H |
| Silver lip under the window | 0.31 H |
| Silver band on each side of the window | about 0.075 W |
| Hair over the face | a fringe about 0.2 H deep at the centre, a curl in the top corner on her right, a lock down her right side to about 0.5 H |
| Eyes | short slanted lids at about 0.28 H below the top, 0.17 W apart |
| Nose | a triangle 0.04 W wide and 0.13 H tall, centred, just below the eyes |
| Lips | a bar 0.065 W wide and 0.045 H tall, at about 0.5 H |
| Antenna cone | base 0.075 W, height 0.27 H |
| Antenna ball | diameter 0.11 W |
| Dress top | 0.32 W wide at the neckline, flaring below |
| Arm thickness | about 0.05 W |

The curl and the lock are on the left of the picture, which is her right side (−X).

## Designed by eye (not in the reference)

The legs are dark brown, the same as the arms. The shoes are magenta like the dress, rounded. The dress flares slightly and ends at about the knee. The back of the head repeats the silver sleeve all round (no window), with the hair colour on top as at the front. The back of the dress has the same dots.

## Colours

Sampled from the reference (median of a 3×3 patch), then rounded. Silver comes out grey in the small, dark crop; it is lifted to read as silver.

| Use | Hex | Note |
|---|---|---|
| Skin | `#633E1A` | face, neck, shoulders, arms, legs |
| Skin shade | `#3F250E` | |
| Hair | `#110B0A` | near black |
| Hair sheen | `#3D1F07` | the curl's inner edge |
| Lips | `#DF151F` | |
| Nose | `#E89127` | |
| Eyes | `#110B0A` | |
| Silver light | `#E6E4E1` | lifted by eye |
| Silver mid | `#A09C99` | sampled |
| Silver shade | `#877D74` | sampled |
| Grille fill | `#362C2B` | |
| Grille lines | `#110B0A` | diamond crosshatch, about 6 diamonds across a ball's visible face |
| Antenna cone | `#FEF404` | |
| Antenna ball | `#F3131B` | |
| Dress | `#7C0F68` | |
| Dress light | `#B71B89` | |
| Neckline trim, beads | `#EAA7C7` | |
| Bead shade | `#BF7787` | |
| Dots, sparkles | `#F5D5E5` | |
| Shoes | `#7C0F68` | by eye |

## Faces (expressions)

Both versions carry her own four, in this order:

1. neutral: the reference face.
2. singing: mouth open as a red oval, eyes closed as curved lids.
3. blink: both eyes closed as lines.
4. surprised: round eyes, small round open mouth.

The hair curl and lock are part of every face.

### What does the new style add?

A piece for each of OUAK's face slots that her four don't fill. Every mouth is in her lip red, and the line mouths are red strokes about as wide as her eye lines. The anger mark and the gloom lines sit on the skin between her left eye and the hair line, the only part of the window's top the fringe leaves bare. The plate tints start at the hair line for the same reason.

| Piece | Look | Colour |
|---|---|---|
| `eye_02_m` | half-closed: a thin lens under a flat lid | eyes |
| `eye_05_m` | white almonds | `#F8F6F2` |
| `face_02_m` | a blue-grey shade fading down from the hair line | `#5E77A0` |
| `face_03_m` | a red flush fading down from the hair line | `#D2342A` |
| `mouth_02_m` | open, a rounded square | lips |
| `mouth_03_m` | open, arched | lips |
| `mouth_04_m` | a wide open grin | lips |
| `mouth_07_m` | open, with a drop of drool | lips |
| `mouth_08_m` | a wavy closed line | lips |
| `mouth_09_m` | an arched closed line | lips |
| `mouth_10_m` | a pucker with a crease beside it | lips |
| `mouth_11_m` | a wavy open mouth with drool | lips |
| `angry_01_m` | a four-armed anger mark | `#7A82EE` |
| `cheak_01_m` | soft blush on both cheeks | `#FF8AA8` |
| `shock_01_m` | three short gloom lines | `#1C2550` |
| `tear_01_m` | a wavy tear under each eye | `#8FD0D6` |

### What does the old style add?

A card for each of Katamari Forever's face expressions her four don't cover. As on Forever's cards, the eyes stay her neutral ones on every card but the angry one.

| Card | Forever expression | Look |
|---|---|---|
| 5, open | 1 | a tall open mouth as wide as her bar |
| 6, smile | 2 | a red trapezoid, wide edge up |
| 7, angry | 3 | steep glaring lids, a red triangle mouth |
| 8, blush and open mouth | 4 | blush, the open mouth |
| 9, pale | 5 | a blue-grey band under the fringe, a pale ashen lower face, a small open mouth |
| 10, blush | 6 | blush, her bar |
| 11, blush and smile | 7 | blush, the smile |

The blush is `#E8607E`, the pale card's band `#5C7682` and its ashen skin `#8A7868`.
