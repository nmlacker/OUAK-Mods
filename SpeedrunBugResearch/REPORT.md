# Speedrun bugs in Once Upon a KATAMARI: dash and stamina (2026-11-07)

Read from the game's own code (current `GameAssembly.dll`, disassembled with `ModMenuSource/tools/disasm.ps1`) and measured in game with a test-only hot mod (`tests/`). There is no mod here: nothing in this folder is installed.

## Stamina in one paragraph

The game counts stamina **used** (`MainGameCore._oujiDashElapsedTimer`), up to **3.0** (`MaxOujiDashTime`). Every dash start, short or long, costs **0.5** (`AddTimePerDashActive`). A long dash lasts **1.5 s** (`_oujiDashTimer`) and costs **1 per second** while it runs. When you're not dashing, used goes down by **0.1 per second**. Going over 3.0 exhausts you: you can't dash for **8 s**, then used is reset to 0 (full stamina).

Inputs (tank controls): an L2 tap starts the wind-up and fires a **short dash** 0.5 s later (0.75x dash speed, no dash timer). Wiggling the sticks (L up + R down, then L down + R up) counts steps: step 3 starts the wind-up, step 7 fires a **long dash**, and every 4 more steps extend it. Five quick L2 taps also fire a long dash.

## 1. The bug: a long dash that lasts until your stamina comes back

**Cause (from the code).** `ExecuteOujiDash` sets the dash timer to 1.5 and then adds the 0.5 cost **without checking the limit**. The input code only checks "used <= 3.0" before the dash. So a long dash started with used between **2.5 and 3.0** pushes used over 3.0. From the next frame, the stamina function (`ExecuteOujiDashTimerSingle`) takes its "exhausted" branch first. That branch only counts the 8 s exhaustion and **never counts the dash timer down**. The dash timer stays at 1.5, so the movement code (`CalculateInputMovement`) keeps pushing you at dash speed. The dash ends when the exhaustion ends: used = 0 (full stamina), dash timer = -1.

In normal dashing this can't happen: while a dash runs, going over 3.0 ends it at once.

**The window:** used in (2.5, 3.0] at the moment the long dash fires.

| used when the long dash fires | what you get |
| --- | --- |
| 0 to 1.0 | a normal 1.5 s dash |
| 1.0 to 2.5 | a short-lived dash: it stops after 2.5 - used seconds, when you run out |
| **2.5 to 3.0** | **the bug: the dash keeps going for up to 8 s, then full stamina** |

**Why the user's recipe works:** 2 short dashes (0.5 + 0.5) + 1 full-length long dash (0.5 + 1.5 s) = 3.0, minus what comes back between them (0.1/s, about 0.2 to 0.3) = about **2.7**. That is inside the window, so the second long dash is the bug dash. Any mix that lands in the window works. Extending a long dash ("an extra longer") uses more time for the same 0.5 start cost, which is another way to get there.

**Notes for runners**
- Works with stick long dashes and with L2 long dashes (5 taps): both measured.
- A short dash (one L2 tap) in the window never starts it: it just exhausts you (measured).
- Afterwards you have full stamina right away (code), so you can set it up again.
- Things that still end it (code; walls measured): any wall bounce (`HitWallRefrect` -> `WallStop`), starting to climb a wall (`HitWall` -> `QuitOujiDash`), the braking branch of `CalculateInputMovement`.
- During it you can't start, extend or wind up another dash (`CanOujiDash` is false). Quick turns (L3/R3) still work.

## Setups (from full stamina: the stage start, or the moment an exhaustion or a bug dash ends)

Inputs: short dash = 1 L2 tap (it goes off 0.5 s later; wait for it before tapping again). Long dash = 7 stick-wiggle steps or 5 quick L2 taps (each within 0.5 s of the last). Extend = 4 more steps or taps while dashing.

| # | Setup | Why it lands in the window | Slack | Checked in game |
| --- | --- | --- | --- | --- |
| 1 | Short, Short, Long (full 1.5 s), Long | 0.5 + 0.5 + 2.0 = 3.0, minus rest | up to 5 s of non-long-dash time in total | yes: fired at 2.829 -> bug |
| 2 | Long (full), Short, Short, Long | same numbers, other order | same | by symmetry |
| 3 | 6 Shorts, then 5 quick L2 taps (L2 only; works on Easy controls; walls can't spoil it) | 6 x 0.5 = 3.0, minus rest | finish within about 5 s of the first short dash going off | yes, twice: fired at 2.634 and 2.633 -> bug |
| 4 | Long, extend once so the extend lands 0.6 to 1.0 s after the dash started (4 L2 taps about 0.2 s apart), let it run out, Long straight away | 0.5 + (extend time + 1.5) of dashing = about 2.8 at its end | extend timing window about 0.4 s | yes: extend at 0.73 s, fired at 2.691 -> bug |
| 5 | Chain: right after a bug dash ends (full stamina), do setup 4 again | about 3.3 s per setup, 2.3 s of it dashing | as setup 4 | no (inferred from the code) |

"Instant" from full stamina isn't possible: 2.5 has to be used first, and dashing (0.5 per start + 1.0 per second) is the fastest way, so about 3 s is the minimum. Setup 4 is close to that.

What spoils a setup: a setup long dash ended early by a wall bounce, a wall climb or hard braking (stick more than about 154 degrees from where the ball is going); extending the middle long dash in setups 1 and 2 (normal exhaustion); tapping L2 again before a short dash goes off (that becomes a long-dash wind-up); starting the last long dash's taps before the previous dash has ended (they extend it instead). In the setup 1 and 4 checks a test switch kept a wall from ending the setup long dash (1 wall hit each), standing in for open ground.

## 2. Other findings in the same code (useful, not bugs)

1. **Extending is cheaper than re-dashing.** `ExtendOujiDash` only resets the timer to 1.5; it doesn't charge the 0.5. Keep wiggling instead of letting a dash end and starting a new one. Measured: an extend 0.15 s into a dash took used 1.267 -> 1.413, only the 0.146 s of dashing.
2. **Exhausting yourself refills faster than resting.** Exhaustion gives back all 3.0 after 8 s. Resting gives 0.1/s (30 s from empty). Above about 0.8 used, burning the rest and waiting 8 s is quicker.
3. **A quick turn (L3/R3) pauses the dash and costs nothing.** While `_nowAction` is OujiTurn / OujiDashWaitTurn (0.4 s per turn), `CoreMove` runs only the turn and skips the stamina function. **Measured twice:** during the turn the dash timer (1.144 and 1.251) and stamina (0.856 and 0.749) didn't change at all. The ball kept moving at about dash speed: 0.35 units in 0.42 s, about 0.84/s against 0.86 dash speed. The dash then carried on (timer counting down again, still dashing 0.5 s later). So every quick turn during a long dash is about 0.4 s of dash-speed travel that uses no dash time and no stamina. In one sample the ball kept going the same world direction for at least 0.13 s after the turn. Not measured: a quick turn during a bug dash (a wall interfered both times).
4. **Don't fire a long dash at 1.0 to 2.5 used.** It ends after 2.5 - used seconds (measured: from 2.25 it ran 0.25 s and stopped at 3.006). One more short dash first moves you into the bug window instead.
5. **Easy controls (one stick, control type 1) have no stick dash at all.** `CheckDash` never calls the stick check for that type: only L2 dashes. (`CheckDashInputKantan` exists but nothing calls it.)
6. Not usable: D-pad Right also dashes, but only with the debug switch `GlobalManager._debugOujiDash`.

## Measured vs inferred

Measured in game (3 launches, Stage_Big1 at 5 cm and the Egypt desert at 1 m, inputs written into the game's own pad buffers):
- Constants: MaxOujiDashTime 3.000, AddTimePerDashActive 0.500, dash speed 0.864 (5 cm) / 34.32 (1 m), short dash 0.75x (0.648 / 25.74), control type 0.
- A normal long dash: 1.49 s, used 0 -> 1.999 (desert); the dash timer counts down (0.752 left at 0.75 s).
- One L2 tap: wind-up, a short dash 0.5 s later, used +0.5, the dash timer stays at -1.
- **The bug:** 8 long dashes started at used 2.75 in launches 2 and 3: 5 stick dashes with the sticks held forward, 2 with the sticks let go, 1 from L2 taps. (Launch 1 had 3 more, which a wall ended on the next frame.) All went to used 3.25 = exhausted on the same frame. On every frame after that the dash timer stayed at exactly 1.500 while the exhaustion timer counted up (up to 1.77 s / 248 frames). Speed stayed at dash speed (0.82 to 0.86 at 5 cm, 34.2 to 34.3 at 1 m). The prince shows the exhausted "Phew" bubble while dashing (frame capture `edge0_1.0s.png`).
- Every bug dash was ended by a wall (`WallStop`) after 0.06 to 1.77 s, because both stages have walls within reach at dash speed. With a test switch keeping walls from ending it, one bug dash ran 2.62 s (90 wall bounces) with the dash timer at exactly 1.500 the whole time, until `QuitOujiDash` (the braking or climbing exit) ended it.
- Setups 1, 3 and 4 checked from full stamina (table above): all fired inside the window and became bug dashes.
- Control: a long dash started at used 2.25 ended by running out after 0.25 s (used 3.006).
- A short dash at used 2.70 -> 3.20: exhausted, no dash.
- Extend: no 0.5 charge.
- Every save file had the same SHA-256 before and after each launch (all save writes were blocked during the tests).

Inferred from the code, not measured end to end:
- The bug dash lasting the full 8 s and ending with full stamina. No test stage had 8 s of open ground at dash speed. The 8 s end and the refill are the exhausted branch of `ExecuteOujiDashTimerSingle` (exhaust > 8 -> used = 0, exhaust = 0, timer = -1).
- The user's exact recipe landing in the window: the arithmetic uses the measured costs, but walls cut the first long dash short in both stages, so the natural run-through didn't reach the window in game.
- Easy controls having no stick dash (finding 5), the braking and climbing exits, and a quick turn during a bug dash.

Also read, nothing exploitable found: the stage clock (`MainGameManager.CountTimer`: the Timer freebie stops it, as intended), the freebie speed and climb multipliers (`GetItemMultiplier`: items 1/16 speed, 2/17, 3/18 climb), max speed and dash speed (`get_NowMaxSpeed`, `get_NowDashSpeed`), wall bounces (`HitWallRefrect`), climbing speed (`CalculateClimbMovement`).

## Files

- `tests/DashBugTest/`: the test hot mod (test only). `tests/run_dashtest.ps1` takes the shared game lock, installs it for one launch, removes it after and checks the save hashes. Flag text: scenario names (`stick l2tap edge edgeneutral control edgel2 edgeweak recipe recipeext s1 s3 s4 qturn qturnbug`), `stage <eStage>`, `slot N`. Five launches in all, with every save file unchanged (same SHA-256) after each.
- `tests/results/<time>/`: per-frame CSV (`dashtest.csv`), the log lines (`test.txt`), frame captures, save hashes.
- Code addresses (current build): CheckDash 18098A280, CheckDashInputItsumono 18098A070, ExecuteOujiDash 1805B6FF0, ExecuteOujiDashTimerSingle 1805B6DB0, ExtendOujiDash 1805B75E0, QuitOujiDash 1805C1350, CanOujiDash 1805A8680, StartAction 1805C62A0, ExecuteOujiTurn 1805B7340, CoreMove 1805B54A0, WallStop 1805CE010, HitWallRefrect 1805BB930. The older ISIL dump in the 0ea1aad6 scratchpad comes from an earlier build, so its addresses are shifted.
