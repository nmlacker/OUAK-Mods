# Long Lost Cousins

Long Lost Cousins lets you play Once Upon a KATAMARI as a cousin the game never shipped. Version 0.1.0 has one of
them: Burda, the singer whose head is a sideways microphone, a cousin from the cancelled Katamari Damacy Online. She
comes in two looks.

## What does it do?

It dresses your own cousin as Burda. The game's body and face on your cousin are switched off, and Burda's body and
face, built from the mod's own files, take their place. She wears your cousin's presents. In the new style she moves
with the Prince's motions; in the old style with Katamari Forever's, read from your own copy of that game (see below).
Her face changes when your cousin's would, blinks included.

Nothing new goes into your save and nothing is sent over the network. When you pick Burda, the save keeps your own
cousin. Under her the game builds the Prince, whoever your own cousin is, so she always stands on the same body.

## How do I pick Burda?

On the game's own select screen, The Prince & Cousins (on the Prince's Ship, in the Plaza and on OujiStar). She is on
page 4, in the first free slot after the last cousin, and she is found from the start. Pick her and confirm, and you
are Burda. Your save keeps the cousin you were. Pick any cousin there, your own one included, and she is off again.

You can also pick her in the Long Lost Cousins tab of the mod window. There are three choices:

- Off, the game's own cousin. This is the default.
- Burda, new style (OUAK): made like this game's cousins, with a face piece for each of the game's 23 face slots and
  her own singing eyes when your cousin whistles.
- Burda, old style (Katamari Forever): the look of the older games, with 11 drawn face cards.

The tab also picks her style. Picked on the select screen, she comes in the style last picked in the tab (the new
style if none was), and her slot shows that style's picture the next time the screen opens.

Both ways do the same: the mod notes which cousin your save holds when you pick her, and she is worn while the save
still holds that cousin. She comes off by herself when the saved cousin changes: another cousin picked on the select
screen, a swap with a cousin in the Plaza, the Cousin Creator, or another save slot loaded. Your own cousin is back
then, and you can pick her again.

The change shows the next time the game builds your cousin. On the Prince's Ship the game does that right after you
confirm on the select screen; elsewhere it is the next stage or the next visit to the Plaza or the ship. Your choice is
kept in `UserData/LongLostCousins/settings.json`, never in the game's save.

## Where is my cousin dressed?

- In story stages played alone, and in the copy of your cousin the stage screen shows in its corner. There she is
  drawn a little smaller in the new style (0.966), a rough value meant to make her as wide as the Prince would be.
- In the Plaza and on the Prince's Ship, and in the copy of your cousin their screens show.
- On both stage results screens, in the ending, on the star map (OujiStar), in Cosmo, in the tutorial Plaza, in the mini
  theme and in the present collection popup. In each of them the mod has the game build the Prince for you and dresses
  him. None of these has been seen working in the game yet: the in-game test reaches only the ship and the stage, and
  it can't get past the "Are you finished?" question to the results.

One of the two results screens (its code calls it AA) shows a copy of the stage's model, which is already the Prince
under her. That screen also picks its camera by the saved cousin (cousins 73 and 74 get their own), and the mod has it
pick the Prince's camera.

Wherever the game builds a cousin other than the Prince for you, she is left off rather than put on the wrong body.

## Where is it not dressed?

On the select screen itself (it shows a flat picture per cousin, and hers is the mod's own), in online battles, in OUAK
Multiplayer and in the Cousin Creator. In OUAK Multiplayer the other players see the game's own cousin, and so do the
ghosts and portraits there. The one stage where the game always plays the Prince stays the Prince.

## Does she wear my presents?

Yes, the hats, faces and other solid presents your cousin wears. Each one hangs on her own joint of the same name (a hat
on her head), so it moves with her and shows wherever she is shown, on the stage screen's copy and the Plaza's too.
A present you put on while she is worn goes straight onto her. None of this has been seen working in the game yet; the
in-game test checks it on the ship and in the stage.

In the new style she is built on the Prince's joints, so a present sits where it would sit on the Prince. In the old
style her head is taller and reaches further forward, so presents on the head are moved 7.015 cm up and 3.547 cm
forward. That value was worked out from her model (the top and front of her head against the Prince's) and has not
been checked in the game yet.

The body suits are not shown on her. They are bent around the Prince's own body and would not fit hers, so they stay
hidden. When a hat hides the Prince's antenna, hers is hidden too (new style; the old style has no separate antenna).

The numbers are in `Cousins/cousins.json`, under "fit", for each style: an offset per joint, an offset for a present
that needs its own, a list of presents she never wears, and her scale in the stage screen's corner. They are her own
numbers, not taken from the game.

## How does the old style move?

With Katamari Forever's own motions, read from your own copy of Katamari Forever. That game came out only on the
PlayStation 3, so the mod reads its disc folder (a copy of your disc, as an emulator uses it). Set the folder once in
the Long Lost Cousins tab: paste it into the box under "Katamari Forever's motions" and press Use this folder. It can be
the disc folder itself (the one holding `PS3_GAME`), `PS3_GAME`, or `PS3_GAME/USRDIR`.

At each start the mod reads four files from that copy: the motion table, the Prince's model and his two motion packs,
about 0.6 MB of Forever's archive. It checks each one against the copy the mod was made for, then fits the Prince's
motions to Burda's body in memory, away from the game's main thread, in under a second. Nothing of Forever is stored or
sent anywhere. The mod itself carries only which Forever motion plays for each of this game's motions, which Forever
joint moves each of hers, the scale between the two games, and the four checksums.

While you play, she takes the Forever motion that matches what your cousin is doing (46 of this game's motions have
one), at Forever's own pace. Her face card follows that motion's expression, unless the game shows a face of its own
at that moment (an override face, no face, the whistle); then the game's face wins. Where Forever has no matching motion
(the cars, some emotes, the select screen and a few more), she plays this game's motion, with her waist 7.415 cm
higher than the Prince's so her legs reach the ground.

### What happens without a copy?

She plays this game's motions everywhere, as described above, and the tab says why in one line: no folder set, no
Katamari Forever found in it, or files that are not the copy the mod knows. Only the North American disc has been
tried. Other regions may differ from it, and then they count as the wrong files.

### Where is the folder kept?

In `UserData/LongLostCousins/settings.json`, under `forever_folder`. The mod never writes the folder in the log or shows
it in the tab. The log only says found, not found or wrong files. Forget the folder clears it, and Read it again reads
the copy again without a restart.

## Does it do anything while it is off?

It adds her slot to the select screen. Seven hooks are always in place, because that screen is where she is picked:
the screen's slot setup, its confirm, the two places in the Cousin Creator's selection that store a cousin straight
into the save, and one check on every save the game writes, which makes sure the mod's id never reaches the file. They
act only on those calls and do no work of their own each frame.

Four more hooks exist only while the settings name her. One of them sits on the game's "which cousin is saved"
question, which about 62 places in the game ask, so it is removed as soon as the settings stop naming her. It answers
differently only inside short stretches the mod marks: the select screen's update, where it puts the current-cousin
marker on her slot, and the game's loading of your own model, where it says "the Prince". The other three mark the
select screen's stretches.

Thirteen hooks build the Prince under her and keep the save safe while they do. They exist while the settings name
her, or while a model dressed as her is still around: the two loaders of your cousin on the ship and in the Plaza, the
stage's and the ending's loading, the star map's two loading steps, Cosmo's, the mini theme's, the present collection
popup's, the AA results screen's camera choice, the save's conversion to bytes, and the Plaza swap (two hooks: the
model that walks off as a cousin is undressed first, and she comes off). The hooks that dress your cousin, and the
work each frame, exist only while she is worn.

## Why does the select screen never store her in the save?

Because the save keeps your own cousin. The game's save only knows its own cousins, and about 62 places in the game
read the saved cousin. Her slot carries the mod's own id, 100. When you confirm her, the mod turns that slot into the
cousin your save already holds (for a Cousin Creator cousin, the same designed cousin too) just before the game stores
it, and puts 100 back right after. So the game stores what it had, and the save stays as it was.

Her slot is never filled on the Cousin Creator's selection screen, whose own code stores a slot's id straight into the
save. If 100 ever reached the save anyway, the mod puts back the cousin it noted when you picked her before the game
writes the file (the log then shows an error). While she is worn, the screen shows the current-cousin marker on her
slot and lets you pick your own cousin again, which turns her off.

## What does it need?

MelonLoader 0.7.2 and the OUAK Modding API (`OUAKModAPI`). For the old style's own motions, your copy of Katamari
Forever's disc folder (optional).

## How is it tested in the game?

With a flag file. Put a file called `cousintest.flag` in `UserData/LongLostCousins/` and start the game. The mod reads
the words in it and deletes it:

- `style new` or `style old`: the look to test (new when not given).
- `slot N`: the save slot to load (the last one used when not given).
- `stage`: also play As Large As Possible 1.
- `results`: also finish that stage and check the results (this includes `stage`).
- `select`: start with Burda not picked, and pick her on the game's select screen on the ship (see below).
- `probe`: also run the probes for the next version and log what they find (see below). Use it with `stage`.
- `cousin N`: the cousin the save holds for the run (6 when not given).
- `creator`: the save holds a Cousin Creator cousin for the run, with the custom index the slot already has.
- `forever`: use the Katamari Forever folder already in `settings.json` and check the old style's Forever motions (see
  below). Without it the test treats the folder as unset, so the old style plays this game's motions as before.

The test turns Burda on for that game session only and never writes `settings.json`. It makes a backup of the saves,
blocks every save write for the whole session, and loads the slot the way the title's continue does. Right after the
slot is read, it sets the saved cousin and the game's current-cousin id to the test's cousin, in memory only, so that
building the Prince under her is a real change. Before the last line it puts both back and checks that the saved
cousin, its custom index, the current-cousin id and the worn presents are as the slot had them.

On the Prince's Ship it checks that the settings noted the saved cousin and that the walking cousin and its screen copy
are dressed: none of the game's renderers on, hers on and drawn, her waist where it belongs, and her joints turned the
way the game's are, measured in world space. It checks that the model under her is the Prince (`OujiPrefab_001`, present
id 0), that the save still holds the test's cousin, and that every dressed model is one of the player's. Then it shows
face groups 0, 9, 10, 12, 15, 17 and 26 and checks which of her face pieces (or which card) show.

Without `select`, it then turns her off in the tab and checks that the save and the current-cousin id still hold the
test's cousin. It has the ship rebuild your cousin (a `[direct]` step) and checks that the new model is the test's
cousin itself, undressed. It turns her on again, has the ship rebuild once more, and checks that she is back on the
Prince.

In the stage it checks the player and the stage screen's copy, that both stand on the Prince (`OUJI01_D`, present id
0), that the save still holds the test's cousin, that her renderers are in the player's fade list, and that fading the
player to half fades her too. With `results` it then tries to reach the results screen and check the model there. That
part doesn't reach the results yet (see above), so it ends with one FAIL line.

On the ship's walking cousin, its screen copy, the stage's player and the stage screen's copy, it also checks the
presents the slot wears. It waits for the worn face and head presents to arrive, then checks that each solid one hangs
on her attach node (and that the game's entry for it names that node), that it is on with all its renderers, on her
body's layer, and near her joint in world space (within its offset, its own place in the prefab and 1 cm). On the stage
player it checks that the fade list holds each present exactly when the game had it there. A body suit must stay
hidden and off her nodes. On the walking cousin it switches the game's antenna off and on again (`[direct]`) and
checks that hers follows. In the stage screen's corner it checks that her marker carries the style's scale, that she
and her presents sit inside the screen camera's view with a margin, and it logs her width against the hidden Prince's.
Pictures: `present_ship_*`, `present_antenna_*`, `present_stage_*`, `hud_*` and `hud_rt_*` (the picture the corner's
second camera draws, which no panel shows).

With `select`, nobody is picked at the start. On the ship the test opens The Prince & Cousins through the game's own
calls: once the ship would take up an open menu, the pause menu, then its Mates entry. It logs every child of her slot
and of Young King's slot on the same page, then checks her slot on page 4: the id 100, her icon drawn (shown, her
sprite and texture, at a found slot's size, opaque), the found look, her name and no New mark. It takes a picture
there and confirms her the way the menu and the dialog's yes do, inside the menu's scope: the "already selected"
compare, then the confirm. It checks that the save and the game's current cousin still hold the test's cousin, that
the settings say Burda and noted that cousin, that the dressing hooks and the marker hooks are in, that the scope is
closed again and that the save check never fired. Then it waits for the ship to rebuild your cousin and runs the ship
checks above on it. Last it opens the screen again, checks that the current-cousin marker sits on her slot and not on
the test cousin's, and that the scope reads the saved cousin as her. It picks the test cousin's own slot and checks
that she is off, that the save is unchanged, that the marker hooks are gone and that the rebuilt cousin is the test's
cousin itself. When that cousin has no found slot on the screen (a Cousin Creator cousin, or one the save has not
found), it picks the Prince instead and says so; the save then holds the Prince and is not compared.

When the ship does not take a step by itself (taking up the open menu, opening the screen, starting the cousin change
after a confirm, or resetting the screen before it opens again), the test logs the state and makes that step with the
game's own call, on a line marked `[direct]`. With `stage` too, she is turned on again in memory before the stage.

A `select` run ends with four FAIL lines today, two for each time it opens the menu. All four are the waits for the
ship to take up the open menu by itself. The test runs on the ship while the title screen is still up ("Press", Options,
Quit Game, seen in every picture), and the ship's menu handler's update flag stays off the whole time (logged as
`update handler False`). That the title is what keeps the flag off is a guess, not traced. The
`[direct]` step after each of them opens it, and every check on her slot, the confirm and the rebuild runs after that.
Both styles gave pass=48 fail=4 on 2026-11-20, before the saved cousin was kept; the run with the checks above has not
been made yet.

### What does the forever word check?

On the ship (before her off and on) and in the stage, with `style old`: that the copy in `settings.json` was found and
every clip the map names was baked. Then it watches her for up to 8 seconds, and on at least two Forever states (the
game's own, else states the test plays on the game's Animator) it checks that her 23 joints stand where the bake puts
them at the time the test works out from the Animator, in world space within 0.05 degrees and 0.05 cm. That margin is
float rounding over her chain of at most 8 joints; a frame off, or a joint left on the game's pose, misses it by far
more. It checks that her pose is not the game's (over 1 degree apart) and that Forever's time runs at the clip's pace
over half a second, within 5 %. On state 0066, which has no Forever motion, it checks that she copies the game's joints
again, with her waist 7.415 cm above the game's and her other joints back at their rest offsets. Last, on a Forever
state whose expression card differs from its face group's card, it checks that the expression's card shows while the
game shows the row's own face group, and that the game's group wins when another is set. The stage screen's copy gets
the joint check too. Pictures: `forever_ship_old`, `forever_stage_old`, `forever_ouak_*`, `forever_face_*`.

With `style new` it checks that she never plays a Forever clip. In the ordinary checks, a model on a Forever state is
measured against the bake instead of the game's joints, and the face check expects the expression's card on the row's
own group.

### What does the probe word do?

It answers questions the next version needs answered in the game, and changes nothing the mod does in normal play.
Every answer is a `[CousinProbe]` line in the log. The probe hooks are made only in a probe run.

On the ship it lists the presents of the walking cousin, logs every state change of the game's Animator under her,
asks the walking cousin for a few motions, and has the ship rebuild the walking cousin twice: once with the saved
cousin reading as the Prince inside the model loader, once as the game has it. In the stage, the game's current-cousin
id is set to the Prince for the load, so the stage builds the Prince under her while the save keeps your cousin. The
probe then logs which model each part built, the presents and when they arrive, the HUD copy's camera and her size in
it, and every hat object in the scene. It takes pictures of the HUD with the hats switched off, then loads the stage
once more with the worn hat set to none for one more picture. It also loads the Prince's motion controller from the
game's catalog and compares it with the one on the model.

Each change is made in memory only and undone right after, and again at the end. The test blocks every save write as
always, and the probe logs the saved cousin and its custom index at the start and at the end and checks they match.
The probe's start is after the test set the saved cousin, so it compares with the test's cousin.

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
