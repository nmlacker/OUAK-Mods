# OUAK Multiplayer: online leaderboards (design)

Status: design and an offline stand-in only; the user's first decisions (2026-11-12) are in section 4. Nothing is sent anywhere, the game's screens are unchanged, and recording is **off** until the open questions below are answered.

The user's brief (2026-11-11, relayed by the API session):
- "implement online leaderboards inspired by Beautiful Katamari, including persistent online scores, player rankings, and global leaderboards."
- "I want scoring done via cookies, like how Beautiful Katamari was."
- Four leaderboards: **Everyone's Katamari** ("take every players' Katamari size, and add it, with a leaderboard"), **Cookies** ("how many cookies you have in the Multiplayer Mod"), **Cousins** ("Which Cousin is popular") and **Presents** ("which Presents are popular").
- The boards will live in the game's Cosmos screen, reworked like Beautiful Katamari's space menu with four destinations (mock-ups by the API session; no UI change before the user approves one).
- "Don't make assumptions about design decisions I'll be providing later."

## 1. What the game already has (read-only findings, 2026-11-12)

**An unfinished cookie system.** Once Upon a KATAMARI has cookies for online play, apparently never finished:
- `GlobalManager.multiCookieNum` (uint): a cookie count kept while the game runs (no save field for it was found);
- `NetworkUserInfo.cookieNum`: every player's cookie count, sent with their look in the game's own online versus;
- `NetworkCookieInfo` { `userID`, `cookieNum` } with `GetByteData` / `SetByteData`: an online packet for cookie counts, held in `MultiplayController.cookieInfo`; `MainGameCore.isSendCookieInfo` flags sending it;
- `MultiplayResultObjectManager.ChangeMyCookieToStar` / `ChangeMyCookieToStarCor`: a Katamari Ball results animation that turns "my cookie" into a star (moves, scales and spins it, with sounds).

**The game has no cookie formula** (checked 2026-11-12 by disassembling all 14,174 game methods): no game code reads or writes `multiCookieNum` (field 0x23C) and none touches `isSendCookieInfo` (0xF6). `MultiplayController.Init` creates the `NetworkCookieInfo` object and nothing uses it after that. Nothing was found that sets `NetworkUserInfo.cookieNum` (it travels as 0), and the cookie-to-star animation has no callers. Only the containers exist: a counter, a number in every player's online info, a packet, an animation. The amounts have to come from us.

**The Cosmos screen** (where the game shows scores): `CosmoSceneSetup`, `CosmoPageTable` (pages of `CosmoStarInfo`: a `StarIDEnum` per star, satellites, "down" stars), `CosmoStarScoreController` (a star's title, stage name and best record: `SaveRecodeEnum m_BestScoreRecode`, `ResultSaveData`), `CosmoShStarManager` / `CosmoShStarScoreController` (shooting stars), `CosmoStarEventManager`, `CosmoScrollAnimationController`, `CosmoSceneTransition`, `CosmoSoundManager`. Stages map to stars through `SelectHirobaManager.StageToStarID` / `StarIDToStage`. Saved records: `SaveInfoGameData._ResultSaveData` (`ResultSaveData[]`), `SaveInfoResult.BestSaveInfo._BestScore`. Nothing was changed.

**Cousins**: the game's character id (`GlobalSaveData._selectPlayerId`, sent as `NetworkUserInfo.characterID`); 999 is a custom cousin, with its slot in `customPlayerIndex`. In the hubs, the cousin you walk as is `ModelLoader.PlayerNow`.

**Presents**: integer present ids. Worn: `GlobalSaveData._wearingPresent` (`int[]`, sent as `NetworkUserInfo.wearingPresent`). Given by a stage: `Game.ResultData.PresentID`. Found: `SaveInfoMonoData._Present` (`bool[]`). Names and pictures: `PresentDataTable`, `PresentData`, `PresentSprite`, `PresentItemName`.

## 2. Beautiful Katamari, for reference

From fan wikis, guides and 2007 previews (not official, so unconfirmed):
- the King rated each level and gave **cookies**; ratings depended on time, size and the objects matching the King's request. One walkthrough says a level scores up to 120 points (100 unlocks its endless mode);
- the **winner of an online match** received cookies;
- cookies accumulated over all play (achievements at 100 and 1,000 cookies);
- **Everyone's Katamari** was a worldwide katamari: the total of everything every Xbox Live player rolled up, with your own contribution.

Sources: [Katamari wiki](https://katamari.fandom.com/wiki/Beautiful_Katamari), [Beautiful Katamari walkthrough](https://laserqcopper.netlify.app/beautiful-katamari-walkthrough), [GamesRadar preview](https://gamesradar.com/katamari-360-goes-crazy-online-new-details), [achievements list](https://www.gamerevolution.com/guides/38977-beautiful-katamari-achievements-list).

## 3. What each game records

One **run record** per finished run (`Leaderboards/LeaderboardModels.cs`, `RunRecord`), built from the run's own numbers. It holds the raw facts every option below needs; no open rule is applied when recording.

| Field | What | From |
|---|---|---|
| `EventId` | a new GUID per run; the same record twice counts once | made at recording |
| `Schema`, `ModVersion`, `PlayedUtc` | format version, mod version, when | |
| `Player` | `Kind` "steam" (SteamID64) or "install" (this install's random id), `Id`, `Name` (the session name) | `NetSession.MyIdentity`, never an email |
| `Mode` | `Solo` (story stage, no session), `Session` (played together), `KatamariBallOffline` (not recorded yet) | |
| `SessionRunId`, `PlayersInRun` | session runs: host + run + map, the same for everyone in that run | `SyncRun` |
| `HostVerified`, `Verdict` | session runs: the host's checks (late start, size, time) found nothing | `RunResult.Verdict` |
| `Place` | session runs: the place under the session's ranking system | `Ranking` |
| `Stage`, `StageName` | the game's `eStage` and internal name | |
| `Size`, `Objects`, `ReachedWin` | final size (tenths of a mm), objects rolled up, goal met | `RunResult` |
| `Rating`, `ScoreKind`, `Score` | the game's stage rating (0 = top badge) and the stage's own score | `CheckClearRate`, `StageScore` |
| `Igt`, `Rta`, `WinIgt`, `WinRta` | the run's timers | `RunClocks` |
| `Cousin`, `CustomCousin` | the cousin played | your look |
| `Presents` | presents worn | your look |
| `PresentGiven` | the present the stage gave | not filled yet (`Game.ResultData.PresentID`) |

When: session runs when the session's results are final (place and host checks known); solo story stages when the stage ends (API `GameEvents.StageEnded`). Where: `UserData/OUAKMultiplayer/Leaderboards/runs.jsonl` (one JSON record per line). Setting: `[OUAKModMenu_Online] LeaderboardRecording` (off).

## 4. The boards, and the rules

**Decided by the user (2026-11-12):**
- Everyone's Katamari adds up the **final size of every run** (`KatamariRule.FinalSizeOfEveryRun`).
- A cousin's popularity is **every run played as it** (`CousinRule.RunsPlayedAs`).
- A present's popularity is **every run it was worn in** (`PresentRule.WornInRuns`).
- **Which runs count** (the user: "Only Cookies should be tracked in our multiplayer sessions; Cousins, Presents, and Everybody's Katamari should be trackable in Singleplayer only for now"): Everyone's Katamari, Cousins and Presents count **single-player stages only** (`Mode == Solo`); Cookies count **session runs only** (`BoardRules.KatamariRuns / CousinRuns / PresentRuns / CookieRuns`).
- Cookies are earned only in **the mod's Multiplayer sessions, against at least one other real player**: not Katamari Ball, not single player (`LeaderboardRules.EarnsCookies`: `Mode == Session` and `OtherHumans >= 1`; the loopback test bot doesn't count). How many is still open (question 1).

**Cookies, decided (2026-11-12)** (`Leaderboards/CookieRules.cs`, `BeautifulKatamariCookies`): 20 for taking part; 1st +30, 2nd +20, 3rd +10, 4th to 8th +0; a special bonus of +10 for 1st to 3rd; a random bonus of 0-50 (from the run's id, so one run always gets the same bonus). A shared place gets that place's cookies for everyone in it; a player who left the race gets nothing. So 1st 60, 2nd 50, 3rd 40, 4th-8th 20, each + 0-50. Checked by a standalone test (all places, DQ, bot-only, solo, ties, the random range, the local boards, reloading).

The user's Beautiful Katamari table it comes from:

| Reward | Cookies |
|---|---|
| Participation | 20 |
| 1st place | +30 |
| 2nd place | +20 |
| 3rd place | +10 |
| 4th place | +0 |
| Random bonus | 0-50 |
| Special bonus | +10 each |


`Leaderboards/ILeaderboardService.cs` holds the rules as options; every rule starts **Undecided**, and a board whose rule is undecided stays empty. The local stand-in counts every option, so a decision is a setting, not new code.

| Board | Value per entry | Options so far |
|---|---|---|
| Everyone's Katamari | each player's share, and the total of everyone | final size of every run / best size per stage / objects rolled up |
| Cookies | each player's cookies | open (question 1) |
| Cousins | how popular each cousin is | latest cousin per player / every run played as it |
| Presents | how popular each present is | worn per player / worn in runs / given by stages |

Ties share a place (1, 1, 3). Custom cousins are each player's own, so they count together as one entry ("Custom cousins").

## 5. Questions for the user

1. (answered: cookies, section 4.)
2. (answered: the other three boards count single-player stages only, for now.)
3. **Custom cousins** on the Cousin board: counted together as one entry ("Custom cousins"), or left out?
4. (answered: Everyone's Katamari, Cousins, Presents.)
5. **Periods**: all time only, or weekly / monthly boards too? Per-stage boards?
6. **Who counts, and is it opt-in?** Steam players only, or also copies without Steam (weaker identity)? Uploading on by default, or asked first? Names shown, or anonymous for those who prefer?
7. **Which runs count?** Every run, or only session runs the host verified?

## 6. Code (this mod)

- `Leaderboards/LeaderboardModels.cs`: the records and pages above.
- `Leaderboards/ILeaderboardService.cs`: the service interface; the open rules (`KatamariRule`, `CousinRule`, `PresentRule`, `ICookieRule`), all undecided.
- `Leaderboards/LocalLeaderboardService.cs`: the offline stand-in (records in `runs.jsonl`, boards counted from them by the given rules, never online).
- `Leaderboards/LeaderboardRecorder.cs`: builds and keeps the records (off by default).
- Online test: with the recorder on for that run only (a throwaway folder), the record must match the run (size, cousin, place, players), undecided rules must give empty boards, every option must count correctly, and the same record twice must count once.
