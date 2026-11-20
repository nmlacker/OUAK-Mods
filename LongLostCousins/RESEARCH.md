# Long Lost Cousins Mod — Research (draft)

Status 2026-11-12: **research only. No mod code and no assets.** The user makes the models and assets. UI work waits for the user's screenshots and diagrams.

- Part 1 (Katamari Damacy Online cousins): done, from the wiki. The pages are listed under Sources.
- Part 2 (how OUAK handles cousins): traced statically on 2026-11-12 (code read only, game not launched). Each claim is marked **[traced]** (read in the disassembly of the current GameAssembly.dll), **[catalog]** (read from the Addressables catalog) or **[inferred]** (not proven yet).
- Part 3 (plan): a draft, below.

---

## 1. Katamari Damacy Online (KDO) cousins

Source: https://katamaripedia.miraheze.org/wiki/Katamari_Damacy_Online/Cousins (last edited 7 Oct 2025). It was read through a fetch agent, so the descriptions here are paraphrased. The page's own banner says it is a mess.

### "Forbidden Cousins": the term is not on the page
The Cousins page, the main KDO page and the KDO stages page never use the words "Forbidden Cousins" or any similar group name. **Open question for the user:** where does the name come from? Is it your own name for the KDO-exclusive cousins, a term from a community or video, or does it mean only the NPC and "Unknown" ones? The mod's name, "Long Lost Cousins", follows from the same question.

### Counts don't agree
- The Cousins page says 34 new cousins, 32 of them with names. Its Korean name list has 38 entries.
- The main KDO page says 39.
- The page's table has 43 rows. Some rows have no name, and one row covers 3 chest cousins.

### Table (page order)
Type is what the page says: Playable, NPC, Unknown or Promo. ✱ marks a row with hedged or uncertain info.

| # | Name (Korean) | Look | Type |
|---|---|---|---|
| 1 | Mint (민트) | Looks like the Princess (hair, clothes). The most popular KDO cousin. Has flavour text. | Playable |
| 2 | Melo (멜로) | Yellow with a chocolate-dip pattern. Hates sweets. Has flavour text. | Playable |
| 3 | Elise (엘리제) | Like Odeko, black/red pattern, white lace around the head | Playable |
| 4 | Wink (윙크) | Ginger hair, freckles, overalls | Playable |
| 5 | Air (에어) | Macho's body with Jungle's pattern gimmick, bent antenna | NPC |
| 6 | Candi (캔디) | Shaped like a candy | Playable |
| 7 | Cheda (체다) | Wedge of cheese | NPC |
| 8 | Cold (blue) / Horuru (호루루, cyan) / Pandora (판도라, red); also "Lexi (렉시)" | Treasure-chest bodies | NPC |
| 9 | Jang (장) | Head like a dresser drawer | Playable |
| 10 | Dongdongi (둥둥이) | Drum head | Playable |
| 11 | Andro (앤드로) | Body shows Earth's layers, face in the core ✱ | NPC |
| 12 | Yoring (요링) | Onion body. Has a feminine alternate design. | Unknown ✱ |
| 13 | Lace / Reis (레이스) | Blue dress, laced head, likes dolls | Playable |
| 14 | Hart (하트) | Hearts all over, bad at makeup | Playable |
| 15 | Ice Ball cousin | Ice-ball body. Maybe **Snou (스노우)** or **Crio (크리오)**. Like Sherman. | Unknown ✱ |
| 16 | Fishbowl / terrarium cousin | Transparent jar body. 3 seen by the water slides. | NPC (no name) |
| 17 | Katamari cousin | Katamari-shaped body. A shop item, "Heart Core", looked like it. | NPC (no name) |
| 18 | Prisia (프리지아) | Freesia bud | Playable |
| 19 | Sensi (센시) | Pins sticking out of the head | NPC |
| 20 | Momo (모모) | Pink peach | Playable |
| 21 | Pis (피스) | Puzzle-piece body | NPC |
| 22 | Record cousin | Vinyl-record body | Unknown (no name) |
| 23 | Robot cousin | All robot, holds a keyboard | NPC (no name) |
| 24 | Cube (큐브) | Rubik's-cube head | Playable |
| 25 | Wire / Waier (와이어) | Thin metal lines, 4 faces | Playable |
| 26 | **Burda (부르다)** | Singer, sides of the head look like a microphone (the example in the user's mock-up) | Playable |
| 27 | Welldon (웰던) | T-bone steak | NPC |
| 28 | Gimbapi (김밥이) | Gimbap roll | Playable |
| 29 | Tiger (티거) | Tiger stripes (2010 = Year of the Tiger?) ✱ | Playable |
| 30 | Maneljjangyachi (마늘짱아치) | Pickled garlic | Playable |
| 31 | Rouge / Ruz (루즈) | Pink lipstick head | Playable |
| 32 | Cheri (체리) | Child, round red cherry head, scalloped jumper | Playable |
| 33 | Calendar cousin | Calendar head | NPC (no name) |
| 34 | Circuit (서킷) | Two antennae, wave graph on the body | NPC |
| 35 | Soso (소소) | See-saw head | Playable |
| 36 | Etude (에뛰드) | Etude House make-up brand event cousin, pink dress | Playable (brand tie-in) |
| 37 | 8-Bit (8비트) | Computer | Playable |
| 38 | Red cousin | All red with yellow features. Only on one promo poster. | Promo (no name) |
| 39 | Black cousin | Black clothes, hair over one eye, owns the rocket launcher, Nutsuo-like body | NPC (no name) |
| 40 | V-Cousin | Red with a yellow V on the chest, superhero, by the carwash | NPC (no name) |
| 41 | Parking cousin | Flat face, looks like a road, runs the parking area | NPC (no name) |
| 42 | Vividi (비비디) | Stereo head | Playable |
| 43 | Rainbow cousin | Rainbow pattern, like Marcy | Unknown (no name) |

Names that appear only in the Korean name list and have no table row: **Bacbaci (박박이)**, **Pandoring (판도링)**, **Tempero (템페로)**, **Snou (스노우)** and **Crio (크리오)**. Snou and Crio may be the Ice Ball cousin. Nothing is known about how any of these look.

Extra notes from the page:
- Elise, Yoring, 8-Bit and Welldon are said to stand for AfreecaTV "Broadcast Jockeys" from an event.
- The page does not explain how cousins were unlocked. The main KDO page says cousins were bought in shops with in-game coins.
- KDO also had a "Cousin Hideout" stage with many cousins you can't play as.
- The page has nothing on cousin customization.

### What's known vs lost
- **Known:** names, a rough look for most, low-res screenshots and promo art (file names are on the wiki page).
- **Unknown or lost:**
  - The models themselves. The game is lost media.
  - Turnarounds, size and stats.
  - Flavour text for everyone except Mint and Melo.
  - Real names for the 9 no-name cousins.
  - Any look at all for Bacbaci, Pandoring and Tempero.
  - Which name goes with the Ice Ball cousin.
  - Whether Lexi is a 4th chest cousin.

### Sources (from the wiki; not opened yet)
- 4Gamer announcement: https://www.4gamer.net/games/037/G003718/20070129200543/
- Gamtoon interview with the team leader: http://www.gamtoon.com/new/gs/interview/view.gam?num=209
- Windysoft English page (archived 2010): https://web.archive.org/web/20100211173718/http://www.windysoft.net:80/eng/games/katamari.htm
- Korean press: m.betanews.net/article/476791 and /485031, n.news.naver.com/mnews/article/311/0000013087, jungle.co.kr/magazine/15821
- Stage art: https://www.artstation.com/artwork/49mPGl and https://m.blog.naver.com/volky412/220263020709
- Linked wiki pages not read yet: "Cold, Horuru, and Pandora", "Etude", and each cousin's own article. These may hold more references.

---

## 2. How OUAK handles cousins

In the game's code a cousin is an **Itoko** (いとこ), and cousins you have found are **Mates**. Types are in `App.KatamariSin`. Addresses are VAs in the current `GameAssembly.dll` (image base 0x180000000) and come from `tools/methodmap.tsv`.

### 2.1 Roster: the `ITOKONAME` enum [traced]
`ItokoSpawnData.ITOKONAME`:
- `NONE = -1`
- `OUJI = 0` (the Prince)
- 75 cousins from `LALALA = 1` to `KOZUMI = 75`. The list includes `KINOKO = 28`, `NICKEL = 21`, `PRONCESS = 55` (the Princess, spelled that way in the code) and `KING = 58`.
- `CUSTOMCHARA = 999` (the player's own custom cousin)

That makes 76 named ids in all. A cousin is an int id everywhere: in saves, the select screen, models and network data.

Other per-cousin data:
- `ItokoDataTable` (ScriptableObject, Addressables key `ItokoDataTable`): rows of `{_id, _itokoName, _prefabName, _stageID, _spawnName}`, i.e. which stage each cousin hides in.
- `ItokoSpawnData`: a MonoBehaviour placed in stages, with `_itoko`, `_spawnPosition` and `_stageID`.
- `MissionItem._itokoID` (int[]) lists the cousins for each stage on the stage list.
- `GlobalManager.GetStageOujiItoko(stage)` reads `AllStageData[stage]+0x48`.

### 2.2 Unlocking [traced]
- The saved "found" flags are `GlobalSaveData.OujiItokoGet`, a `bool[]` at +0x240, indexed by cousin id.
  - `GlobalManager.CheckOujiItokoGet(id)` (0x1808256A0) reads it through `GlobalManager.glbSave` (+0x158) → `GlobalSaveData.CheckOujiItokoGet` (0x18065F590). An id out of range returns false.
  - `GlobalSaveData.SetOujiItokoGet(id, bool)` (0x180662EB0) sets it.
- In a stage, rolling up the cousin sets `MainGameManager.SetItokoRolled`. At stage end, `MainGameManager.StoreGlobalSave` checks `CheckOujiItokoGet` and calls `SetOujiItokoGet`. In short: you find a cousin by rolling it up in its stage.
- "New!" badges use a separate array, `GlobalSaveData.ItokoNotifications` (+0xB8), read by `NewGetNotificatioManager`.
- The King (id 58) is a special case. On the select screen, if the Katamari Damacy or We Love Katamari save-data flags exist (`GetExistKatamariDamacyFlag` / `GetExistWeLoveKatamariFlag`), the game sets `OujiItokoGet[58]` to true.
- Debug: `GlobalManager._debugItokoEnable` (+0x1DF) shows every cousin as found.
- [inferred] The length of `OujiItokoGet` was not read. Assume new ids (≥ 76) don't fit in it, so the mod should keep its own unlock save and never write the game's save.

### 2.3 Choosing a cousin [traced]
- The cousin currently in use is `GlobalManager._nowCharacterID` (+0x1D4), via `Get/SetNowOujiItokoChara`. Those 7-byte accessors are inlined, so they have no call sites to hook.
- `GlobalSaveData.get_GetSelectPlayerId` holds the saved choice.
- Custom cousins: `GlobalSaveData._data` (`CustomCharaData[]` +0xF8) and `_customPlayerIndex`.

### 2.4 Models [catalog + traced]
- `Game.GetItokoPrefabAddressFromID(id)` (0x180540820):
  - id < 0 → a fixed string
  - id 999 → the custom-cousin address
  - otherwise → a string built from `(id + 1)`
- The catalog has `OUJI01_D.prefab` … `OUJI78_D.prefab`, each with an `OUJInn_D_Custom.prefab` twin, so the address is almost certainly `OUJI{id+1:00}_D`. [inferred: the string literal itself wasn't decoded]
- **OUJI77 and OUJI78 exist but no `ITOKONAME` points at them** (the highest id, 75, is OUJI76). They could be unused or DLC models. Worth a look.
- Callers that load models:
  - `ModelLoader` (`OujiModelLoader`, `ModelOnlyLoader`, `SelectHirobaModelLoad`, `SpaceColonyOujiModelLoader`)
  - `OujiModelCreate.ModelLoad` (the select screen)
  - `MultiplayModelLoader.PrefabLoad` (matches and online)
  - `MiniThemeManager`, `EndingGameManager`, `GlobalLoadAddressables.PerformLoadResultMono` (results)
  - `MainGameManager.InitMultiPlay`

  A new cousin has to resolve in **all** of them. The cleanest way is one hook on `GetItokoPrefabAddressFromID` that maps new ids to the mod's own asset.
- Animation clips are shared (`0001_OUJI.anim` …). Some cousins have override clips (`0008_OUJI_28.anim` for Kinoko, `_31`, `_57`, `_76` and others). So all cousins share one rig and one set of animation states, and cousins with unusual bodies get extra clips. [catalog; that they share one skeleton is inferred from the clip names]
- The rig, read from the Prince's prefab (`OUJI01_D`) in the bundles [read from the files]:
  - 23 joints named `JNT_*` under a node `model`, all at scale 1, every joint at rest at identity (within 0.06°). The numbers are centimetres: the waist rests at 26.345.
  - The Animator sits on `OUJI_D`, the parent of `model`, with no Avatar, so it is a Generic rig. A clip finds each joint by the CRC32 of its path below the Animator, such as `model/JNT_root/JNT_waist`.
  - The clips write joint turns, and the waist's position in 99 of 104 clips. One path, used by the two spin clips, matches no node of the Prince.
  - There are 78 controllers `acOujiNNNN`, none of them an override controller. The Prince's has 113 states and 12 transitions, and two bools, `isArrive` and `isPataPata`.
  - The code plays a motion by its row in `OujiAnimationSetting_0000` (109 rows): `Animator.Play(state)` with no crossfade, speed = `AnimeSpeed` × the row's speed, then `SetFace` with the row's face group.
- The `CustomModelParameter` and `ModelDataOUJInn_D_Custom` classes recolour models for the custom-cousin editor. They aren't needed for plain cousins.

### 2.5 "The Prince & Cousins" screen = character select [traced]
- `CharacterSelectionManager`
  - → `SelectionMenuController` (`_pageIndex`, `ChangePage`, `PageCountUpdate _scrollSelectionMenu` = the page dots)
  - → `IconWindowController[]`, one per page
  - → `ShortcutIconData[] _shortcutIconDatas`, one per flower slot
- `ShortcutIconData` fields:
  - `_backImage`, `_iconImage`, `_blankImage` (grey flower), `_hatenaImage` (the "?")
  - `_text` (TMP)
  - `_oujiData` (`OujiData`: `_name`, `_isReleased`)
  - **`_ITOKONAME`** (+0x50)
  - `_customId`
- `ShortcutIconData.Initialize` (0x18079D970) sets each slot's state:

  | Slot state | When | What shows |
  |---|---|---|
  | **Empty** | `_ITOKONAME == NONE (-1)`, or the cousin is in the DLC-pack-4 group (`Game.AAItokoGroup`) and the DLC isn't owned | grey flower only, text set to a fixed string, presumably "-------" [string inferred] |
  | **Locked** | not found yet (not Prince, not debug) | grey flower plus "?", name from `OujiData.SetName(id)`. When not found it uses a fixed string, presumably "???" [inferred] |
  | **Found** | `CheckOujiItokoGet(id)`, or id 0, or debug | back image plus icon, name from the text table: `GlobalManager+0x90` → `GetText(id)` |
  | Custom (999) | — | icon sprite loaded from Addressables by the save's sprite index |

- **Important:** for normal cousins, `Initialize` never sets `_iconImage.sprite`. The icon must already be set on each slot in the screen's prefab, and the code only switches the image on or off. [traced: no sprite write in that branch; that the prefab holds the sprite is inferred] The user's grey "-------" slots are real `ShortcutIconData` objects whose `_ITOKONAME` is NONE.
- So, as the user's mock-up suggests, a new cousin could go into an empty slot like this:
  1. Set the slot's `_ITOKONAME` to the new id.
  2. Set `_iconImage.sprite` to the mod's icon.
  3. Make the found check, the name and the model resolve for that id: hooks on `CheckOujiItokoGet`, `OujiData.SetName`/`GetTextId` and `GetItokoPrefabAddressFromID`.
- The pink name in the mock-up would be a colour change on `_text`. Nothing in the game does that today.
- Page 4 in the user's screenshot has only **8 truly empty slots**: row 2, slot 7 and all of row 3. Row 2, slots 1–6 show "???", which means real cousins not found yet (not empty slots). KDO has 43+ cousins, so most would need **new pages**. That means cloning an `IconWindowController` page, adding a page dot to `PageCountUpdate._pageCursorRect`, and raising the page count in `SelectionMenuController`. That's the riskiest UI part, and it waits for the user's diagrams.
- Other places that show cousins and would also need the new ids, or need to skip them safely:
  - `SelectHirobaTalkItokoIcon` (stage talk)
  - `MissionItemUIChange` (mates on the stage list)
  - `SubjectListDataSet.SetItokoImages`
  - `WorldStageUI.SetUp`
  - `ResultItokohatokoBehavior` (results)
  - `CharacterEffect.OujiEffectOffset` (per-cousin effect offsets)
  - multiplayer `NetworkUserInfo` (the cousin id is sent over the network)

- The icons are 1024×1024 DXT5 textures with one mip level, one full-size sprite each at 100 pixels per unit, centre pivot, not in an atlas. They are baked into `CharacterSelection.unity` and drawn at 1024 with a scale of 0.2. [read from the files]

### 2.6 Not found / not traced yet
- The text table for cousin names (`GlobalManager+0x90`), and whether its keys are ids or string ids (`OujiData.GetTextId`).
- The `AAItokoGroup` contents (which cousins belong to DLC pack 4).

Found since: the Unity version is 6000.0.30f1, read from the bundle headers. The rig is in 2.4 and the icon size in 2.5.

---

## 3. Draft plan (to discuss)

1. **Data:** a mod-side list of Long Lost Cousins. Each entry has a new id (≥ 100, well clear of 0–75, 999 and the unused OUJI77/78), a name, an optional description, an icon, a model asset and an unlock rule. Unlocks go in the mod's own save under `UserData/`. The game's `OujiItokoGet` is never written, so removing the mod leaves the save clean.
2. **Model:** hook `Game.GetItokoPrefabAddressFromID` (or the loaders) so a new id loads the user's model from an AssetBundle (the API already has async `Bundles`). Check every caller in 2.4. Decide what other players see in multiplayer: the vanilla Prince, or the cousin if they have the mod.
3. **Select screen:** fill the empty slots on page 4 first, then add pages. Locked cousins show "???" with the "?" flower; found ones show the icon and the name in the normal text style; unused slots stay "-------". For now every Long Lost Cousin starts unlocked. Wait for the user's diagrams.
4. **Unlock rules:** decided. Cousins are found in custom stages (later), by rolling them up the way the game does with its own cousins (a mod-placed `ItokoSpawnData`-like object). The unlock goes to the mod's own save, not `OujiItokoGet`.
5. **Multiplayer:** decided. Others see the cousin. The cousin id travels in `NetworkUserInfo`, so a client without the mod would get an unknown id. Fall back to the Prince for those players.
6. **Before any code:** the icon size, the rig and the Unity version are now read from the game's files. One read-only in-game probe is still worth running before code, to see a model imported with the settings in `Models/Burda/NOTES.md` stand next to the Prince. It uses the shared `game.lock` and a 35 s gap. **Only when the API/Patch chat gives the go-ahead.**

### Decisions (user, 2026-11-12)
- "Forbidden Cousins" is the community nickname for the KDO-exclusive cousins.
- **All of them count**: every KDO cousin, whether Playable, NPC, Unknown or promo-only.
- **Unlocks:** custom stages, made later. Each cousin is found in a mod-made stage.
- **New pages** after page 4 are OK.
- **Multiplayer:** other players should see the cousin (that needs the mod on both sides).
- **In-game probe:** don't run it until the API/Patch chat says so.

- **The pink name in the mock-up was only a dev note.** Names use the game's normal text style.
- **Until custom stages exist, every Long Lost Cousin is unlocked from the start.**

### Decisions (user, 2026-11-18)
- Each cousin model comes in two styles: old (Katamari Forever) and new (OUAK). The first is Burda, in `Models/Burda/`.
- The user's words: "Use the original Katamari Forever animations as well when selecting the old style." For Burda, the Forever Prince's 160 clips are retargeted onto the old-style rig (joint map in `Models/Burda/burda_rig_spec.json`, `old_style.forever_motions`).
- Old-style face cards fold back round the octagonal head like Forever's.

### My defaults (2026-11-19, not the user's decisions)
The user asked me to settle the open points rather than hand them back. These are my choices, and the user can overrule any of them.
- Which Forever clip plays for each OUAK motion row: `Models/forever_clip_map.json`. 46 of the 81 rows the code asks for get a Forever clip.
- OUAK's own clips play on the other rows (the car, emotes Forever lacks, the select screen). On the old style the waist is raised by about 7.4, in OUAK's numbers, on the clips that set it.
- Each state's speed is set so a Forever clip keeps Forever's pace, undoing OUAK's per-row speed.
- Face cards follow the motion, as in Forever: each Forever motion record carries a face expression. `Models/Burda/face_map.json` maps Forever's 8 expressions and OUAK's 28 face groups onto her faces. Her singing face takes the Prince's whistle.
- Her full face set (drawn 2026-11-19). The new style has a piece for each of OUAK's 23 face slots, named as OUAK names its own objects (`eye_01_m` … `tear_01_m`, with OUAK's spelling `cheak_01_m`), plus her singing eyes `eye_sing_m`. Three names changed meaning: the earlier `eye_02_m` (singing eyes), `mouth_02_m` (open oval) and `mouth_03_m` (small round) are now `eye_sing_m`, `mouth_05_m` and `mouth_06_m`, and the old names carry OUAK's pieces. The old style has 11 cards, one for each of Forever's 8 expressions among them. Where the old style keeps OUAK's clip, the card follows the face group's mood.
- The clips are Forever's data and do not ship. The mod reads them from the player's own copy of Katamari Forever at run time, the way REROLL reads KDR.
- The FBX is in metres with every node at scale 1. It imports into Unity with a scale of 100 and Preserve Hierarchy on (untested).

### Slice one: Burda in the game (2026-11-20)
The mod's first code is in `Mod/` (see its README). It dresses your own cousin's model as Burda, in either style: the
game keeps its cousin id, the mod switches the game's renderers off and adds hers, built at run time from data exported
from the .blend files (`Mod/Cousins/FORMAT.md`). No new ids, no save writes, nothing sent online.

What did the in-game test show? On the Prince's Ship, in As Large As Possible 1 and on the stage screen's copy, both
styles are dressed, with none of the game's renderers on. Her waist is within 0.0001 cm of where it belongs. Her joints
turn as the game's do, in world space. The new style's faces follow the game's face groups (0, 9, 10, 12, 15, 17, 26)
and the old style's cards follow the face map. Fading the player to half fades her too.

What is still unproven? The results and the ending. After the test answers "Are you finished?", the game stays in the
stage. The mod hooks `ResultMonoSetter.SetPlayCharacter` for the results, but the test hasn't reached it yet.

Things learned on the way:
- On the ship, `SingletonMonoBehaviour<CharacterManager>.instance` stays unset. The walking character comes from the
  ship's `PlayCharacterAdominist` instead (as OUAK Multiplayer does).
- Cousins' rigs aren't all the Prince's. Cousin 06's left shoulder rests at x -5.836 against the Prince's -1.063 (read
  from both prefabs), 4.775 cm apart, which is what the game showed. She keeps her own joint positions and copies only
  rotations (and the waist), so that is expected.
- The data's FACE chunk carries OUAK's 23 face-slot names and its 28-group slot table. They are names and small
  indices needed to work with the game, not game art, so they stay in the data (my call).

### Left undecided (by the user's choice, 2026-11-12)
- No-name cousins (Robot, Fishbowl, Katamari, Record, Calendar, Red, Black, V-Cousin, Parking, Rainbow).
- The five names with no known look (Bacbaci, Pandoring, Tempero, Snou, Crio).

### Assets the user will provide (formats confirmed after the in-game probe)
- **Model:** one per cousin, rigged to the OUAK cousin skeleton and working with its Animator states, delivered as a Unity AssetBundle built with the game's Unity version. FBX + textures also work if the mod side builds the bundle. Optional override clips for unusual bodies, like the game's `_28`/`_76` ones.
- **Icon:** one per cousin in the select-screen style. Signolo and Kinoko are the reference. 1024×1024, the size of the game's own icons; plan on a transparent PNG.
- **Text:** name, and optionally a description, per cousin.
