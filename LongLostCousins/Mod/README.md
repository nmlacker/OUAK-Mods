# Long Lost Cousins

Long Lost Cousins lets you play Once Upon a KATAMARI as a cousin the game never shipped. Version 0.1.0 has one of
them: Burda, the singer whose head is a sideways microphone, a cousin from the cancelled Katamari Damacy Online. She
comes in two looks.

## What does it do?

It dresses your own cousin as Burda. The game's body, face and presents on your cousin are switched off, and Burda's
body and face, built from the mod's own files, take their place. She moves with your cousin's motions, and her face
changes when your cousin's would, blinks included.

Nothing new goes into your save and nothing is sent over the network. When you pick Burda on the game's select screen,
the save keeps the Prince in her place, and the mod dresses him as her.

## How do I pick Burda?

On the game's own select screen, The Prince & Cousins (on the Prince's Ship, in the Plaza and on OujiStar). She is on
page 4, in the first free slot after the last cousin, and she is found from the start. Pick her and confirm, and you
are Burda. Pick any other cousin there and she is off again.

You can also pick her in the Long Lost Cousins tab of the mod window. There are three choices:

- Off, the game's own cousin. This is the default.
- Burda, new style (OUAK): made like this game's cousins, with a face piece for each of the game's 23 face slots and
  her own singing eyes when your cousin whistles.
- Burda, old style (Katamari Forever): the look of the older games, with 11 drawn face cards.

The tab also picks her style. Picked on the select screen, she comes in the style last picked in the tab (the new
style if none was), and her slot shows that style's picture the next time the screen opens.

Picked in the tab, she dresses whichever cousin you are now. Picked on the select screen, she needs the Prince in the
save: if something else changes the saved cousin, she is off. Her button in the tab turns her back on, picked in the
tab this time.

One known limit: when something other than the select screen changes the saved cousin, the settings still say Burda,
picked on the select screen. She is off while the save holds another cousin, and she comes back if the save holds the
Prince again.

The change shows the next time the game builds your cousin. On the Prince's Ship the game does that right after you
confirm on the select screen; elsewhere it is the next stage or the next visit to the Plaza or the ship. Your choice is
kept in `UserData/LongLostCousins/settings.json`, never in the game's save.

## Where is my cousin dressed?

- In story stages played alone, and in the copy of your cousin the stage screen shows.
- In the Plaza and on the Prince's Ship, and in the copy of your cousin their screens show.
- On the stage results, and in the ending. Neither has been seen working in the game yet: the in-game test can't
  get past the "Are you finished?" question to the results.

## Where is it not dressed?

The select screen itself (it shows a flat picture per cousin, and hers is the mod's own), online battles, OUAK
Multiplayer, the mini theme, the present collection popup, the Cousin Creator, the star map, Cosmo and the tutorial
Plaza. In OUAK Multiplayer the
other players see the game's own cousin, and so do the ghosts and portraits there. The one stage where the game
always plays the Prince stays the Prince.

## Why does the old style move like this game's cousins?

She plays this game's motions for now. Her waist sits 7.415 cm higher than the Prince's, so her legs reach the
ground. Her own Katamari Forever motions may come later.

## Does it do anything while it is off?

It adds her slot to the select screen. Seven hooks are always in place, because that screen is where she is picked:
the screen's slot setup, its confirm, the two places in the Cousin Creator's selection that store a cousin straight
into the save, and one check on every save the game writes, which makes sure the mod's id never reaches the file. They
act only on those calls and do no work of their own each frame.

Four more hooks exist only while the settings name her: the ones that put the current-cousin marker on her slot. One of
them sits on the game's "which cousin is saved" question, which about 65 places in the game ask, so it is removed as
soon as the settings stop naming her. The hooks that dress your cousin, and the work each frame, exist only while she
is worn.

## Why does the select screen never store her in the save?

The game's save only knows its own cousins, and about 70 places in the game read the saved cousin. Her slot carries
the mod's own id, 100. When you confirm her, the mod turns that id into the Prince's (0) just before the game stores
it and puts 100 back right after. Her slot is never filled on the Cousin Creator's selection screen, whose own code
stores a slot's id straight into the save, and if 100 ever reached the save anyway, the mod turns it into the Prince
before the game writes the file (the log then shows an error). While she is worn, the screen shows the
current-cousin marker on her slot and lets you pick the plain Prince again.

## What does it need?

MelonLoader 0.7.2 and the OUAK Modding API (`OUAKModAPI`).

## How is it tested in the game?

With a flag file. Put a file called `cousintest.flag` in `UserData/LongLostCousins/` and start the game. The mod reads
the words in it and deletes it:

- `style new` or `style old`: the look to test (new when not given).
- `slot N`: the save slot to load (the last one used when not given).
- `stage`: also play As Large As Possible 1.
- `results`: also finish that stage and check the results (this includes `stage`).
- `select`: start with Burda not picked, and pick her on the game's select screen on the ship (see below).

The test turns Burda on for that game session only and never writes `settings.json`. It makes a backup of the saves,
blocks every save write for the whole session, and loads the slot the way the title's continue does. On the Prince's
Ship it checks that the walking cousin and its screen copy are dressed: none of the game's renderers on, hers on and
drawn, her waist where it belongs, and her joints turned the way the game's are, measured in world space. Then it shows
face groups 0, 9, 10, 12, 15, 17 and 26 and checks which of her face pieces (or which card) show. In the stage it checks
the player and the stage screen's copy, that her renderers are in the player's fade list, and that fading the player
to half fades her too. With `results` it then tries to reach the results screen and check the model there. That
part doesn't reach the results yet (see above), so it ends with one FAIL line.

With `select`, nobody is picked at the start. On the ship the test opens The Prince & Cousins through the game's own
calls: once the ship would take up an open menu, the pause menu, then its Mates entry. It logs every child of her slot
and of Young King's slot on the same page, then checks her slot on page 4: the id 100, her icon drawn (shown, her
sprite and texture, at a found slot's size, opaque), the found look, her name and no New mark. It takes a picture
there and confirms her the way the menu and the dialog's yes do, inside the menu's scope: the "already selected"
compare, then the confirm. It checks that the save and the game's current cousin still hold the Prince, that the
settings say Burda, that the dressing hooks and the marker hooks are in, that the scope is closed again and that the
save check never fired. Then it waits for the ship to rebuild your cousin and runs the ship checks above on it. Last it
opens the screen again, checks that the current-cousin marker sits on her slot and that the scope reads the saved
Prince as her, picks the Prince and checks that she is off, that the marker hooks are gone and that the cousin the game
rebuilds is its own.

When the ship does not take a step by itself (taking up the open menu, opening the screen, starting the cousin change
after a confirm, or resetting the screen before it opens again), the test logs the state and makes that step with the
game's own call, on a line marked `[direct]`. With `stage` too, she is turned on again in memory before the stage.

A `select` run ends with four FAIL lines today, two for each time it opens the menu. All four are the waits for the
ship to take up the open menu by itself. The test runs on the ship while the title screen is still up ("Press", Options,
Quit Game), and the ship's menu handler waits until the player has control, so it never takes the menu there. The
`[direct]` step after each of them opens it, and every check on her slot, the confirm and the rebuild runs after that.
Both styles gave pass=48 fail=4 on 2026-11-20.

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
