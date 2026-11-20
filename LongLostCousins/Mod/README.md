# Long Lost Cousins

Long Lost Cousins lets you play Once Upon a KATAMARI as a cousin the game never shipped. Version 0.1.0 has one of
them: Burda, the singer whose head is a sideways microphone, a cousin from the cancelled Katamari Damacy Online. She
comes in two looks.

## What does it do?

It dresses your own cousin as Burda. The game's body, face and presents on your cousin are switched off, and Burda's
body and face, built from the mod's own files, take their place. She moves with your cousin's motions, and her face
changes when your cousin's would, blinks included.

The game still thinks you are the cousin you picked in the game. Nothing goes into your save, no cousin is added to
the game, and nothing is sent over the network.

## How do I pick Burda?

Open the mod window and pick her in the Long Lost Cousins tab. There are three choices:

- Off, the game's own cousin. This is the default.
- Burda, new style (OUAK): made like this game's cousins, with a face piece for each of the game's 23 face slots and
  her own singing eyes when your cousin whistles.
- Burda, old style (Katamari Forever): the look of the older games, with 11 drawn face cards.

The change shows the next time the game loads your cousin: the next stage, the next visit to the Plaza or the
Prince's Ship, or a cousin change there. Your choice is kept in `UserData/LongLostCousins/settings.json`, never in the
game's save.

## Where is my cousin dressed?

- In story stages played alone, and in the copy of your cousin the stage screen shows.
- In the Plaza and on the Prince's Ship, and in the copy of your cousin their screens show.
- On the stage results, and in the ending. Neither has been seen working in the game yet: the in-game test can't
  get past the "Are you finished?" question to the results.

## Where is it not dressed?

The character select screen (it shows every cousin at once), online battles, OUAK Multiplayer, the mini theme, the
present collection popup, the Cousin Creator, the star map, Cosmo and the tutorial Plaza. In OUAK Multiplayer the
other players see the game's own cousin, and so do the ghosts and portraits there. The one stage where the game
always plays the Prince stays the Prince.

## Why does the old style move like this game's cousins?

She plays this game's motions for now. Her waist sits 7.415 cm higher than the Prince's, so her legs reach the
ground. Her own Katamari Forever motions may come later.

## Does it do anything while it is off?

Only its tab. While no cousin is picked, the mod hooks nothing in the game and does no work each frame.

## What does it need?

MelonLoader 0.7.2 and the OUAK Modding API (`OUAKModAPI`).

## How is it tested in the game?

With a flag file. Put a file called `cousintest.flag` in `UserData/LongLostCousins/` and start the game. The mod reads
the words in it and deletes it:

- `style new` or `style old`: the look to test (new when not given).
- `slot N`: the save slot to load (the last one used when not given).
- `stage`: also play As Large As Possible 1.
- `results`: also finish that stage and check the results (this includes `stage`).

The test turns Burda on for that game session only and never writes `settings.json`. It makes a backup of the saves,
blocks every save write for the whole session, and loads the slot the way the title's continue does. On the Prince's
Ship it checks that the walking cousin and its screen copy are dressed: none of the game's renderers on, hers on and
drawn, her waist where it belongs, and her joints turned the way the game's are, measured in world space. Then it shows
face groups 0, 9, 10, 12, 15, 17 and 26 and checks which of her face pieces (or which card) show. In the stage it checks
the player and the stage screen's copy, that her renderers are in the player's fade list, and that fading the player
to half fades her too. With `results` it then tries to reach the results screen and check the model there. That
part doesn't reach the results yet (see above), so it ends with one FAIL line.

Every check is logged as a `[CousinTest] PASS` or `[CousinTest] FAIL` line, the last line is
`[CousinTest] DONE pass=N fail=M`, pictures go to `UserData/LongLostCousins/TestShots`, and the game quits at the end.

## Where do her models come from?

They are the mod's own work. Each look is one `.llcm` file in `Cousins/Burda` (the format is in `Cousins/FORMAT.md`),
made from the Blender files by `Tools/export_llcm.py` and read back and checked by `Tools/check_llcm.py`. They hold no
game data. `Cousins/cousins.json` lists the cousins and their looks, so the next cousin is data, not code.

## How do I know the mod only calls what the game has?

Run `Tools/audit_calls.ps1` after a build. It lists every game and Unity member the built DLL uses and checks each
against the game's own build: a method without compiled code is printed as NO BODY and the script fails. It also
checks that every method the mod hooks has code of its own, shared with no other method.
