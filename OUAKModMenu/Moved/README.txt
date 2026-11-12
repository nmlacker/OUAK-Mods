KatamariBall / multiplayer code moved out of the Mod Menu on 2026-11-06 (kept, not compiled: see OUAKModMenu.csproj).

The user: "In this mod, don't allow changing of the Katamari Ball or Multiplayer aspects via the API. This is a mod menu
purely for speedrunning, TASing, and messing around with the Singleplayer part of the game."

Handed to the OUAK Multiplayer session to copy into OUAKMultiplayer (the mods must not reference each other):
- CustomMatch.cs, MenuMatch.cs: the Match tab (custom rules for offline KatamariBall, incl. the King Box gap fix).
- TrainingTest.cs (trainingtest.flag: tokens kingchance, kcall, rules:off|some|none), KingChanceProbe.cs, TargetProbe.cs:
  the KatamariBall test harness and probes.
- KingChances.cs, TargetPoints.cs: King Chance effects / Target points drafts (now the API's patches + the Multiplayer mod).
The slot loader the single-player tests use moved to ../TestSlot.cs.

- FreebieEffects.cs (2026-11-06, later): Shoes/Crown/Mushroom in story + the Random redraw, now the API's GamePatches.StoryFreebies / RandomFreebie (the menu enables them). Kept here, not built.
