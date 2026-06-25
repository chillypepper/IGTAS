# IGTAP systems — demo reference

The day-to-day reference for routing the **demo**: only the systems a player can actually reach by normal mechanics. Everything here is confirmed present in the demo scenes by the scene extraction (`tools/assetparse`). For the exhaustive picture — including the atom/tree/green-economy layers that exist in code but are **not in the demo** — see **[full-decompiled-systems.md](full-decompiled-systems.md)**.

Entries note the owning class and the save file/field where useful; see [determinism.md](determinism.md) for RNG/timing sensitivity.

> IGTAP is an incremental game that is also a platformer. In the **demo** the economy is deliberately small: a **single currency (Watts)** driven by **clones** that replay your best course runs, spent on a handful of multipliers and the **5 movement abilities**. No atoms, no upgrade tree, no green/clone-dust economy, no moving platforms — those are full-release. The route is *mostly platforming* with a thin Watts-clone economy layered on top.

---

## The whole demo economy in one paragraph

Clones replay your recorded best path on each course and pay **Watts (w)** on each loop (`clonesScript` → `courseScript.reward`). You spend Watts on **local** upgrades (per course: more clones, higher base reward, reward multiplier, fastness/bigness chance) and a few **global** upgrades (game-wide reward/fastness/bigness, the clone-tier caps, and the prestige unlock). Faster/shorter best runs ⇒ faster clone loops ⇒ more Watts. That's it — the other four currencies (`gp`, `np`, `regularNumber`, `cd`) exist in the code but are never generated in demo play.

## Geography

- **5 courses** (`courseScript`, `course1..5data.txt`), with cost/reward tier exponents (cost & reward scale by `10^tier`):

  | Course | tier | world pos (approx) | reached via |
  |---|---|---|---|
  | 1 | 0 | (2000, −148) | start |
  | 2 | 2 | (4553, 2036) | course 1→2 |
  | 3 | 5.2 | (9265, 7115) | course 2→3 |
  | 4 | 10 | (19960, 10700) | course 3→4 |
  | 5 | 16.5 | (−3041, −150) | "course 1→5" branch (far-left, high-tier detour) |

- **Zones 1 & 2** (`Movement.currentZone`). Zone 2 (the lower facility section) is reached by **walking into a `zoneChanger` trigger** at ≈(16677, 7933) — it toggles the `area1`/`area2` scene roots. (In the demo this is *not* gated behind an upgrade.)
- **Gameplay scene** = `Overworld` (`level1`); `MainMenu` (`level0`) is an intro with a course-1 preview. The harness enters gameplay via the real Start Demo button (`changeScene` → `difficultyLevel=0`; Hard-mode `changeSceneHard` → `difficultyLevel=1`, which switches `Saveloader` to the `Savedatahard` dir) — the startup ordering, the fast-boot equivalent, and why the raw `LoadScene` forks startup are in [determinism.md](determinism.md).
- **Connectivity is spatial.** All 5 courses live in the one Overworld scene at different world positions; you **walk/platform between them** — there are no inter-course scene transitions (the only `SceneTransitionObject` just loads `MainMenu`). So world layout *is* the route graph (see the map below).
- **Course start/end** — each course has a `startGate` (entering it begins recording your path for the clone and sets your respawn `resetPoint`) and one or more `endGate`s (`isEndOfCourse` marks the finish; others are intermediate "stop recording" splits).

## Course layout & route order

World positions from the scene dump (regenerate the table with [../tools/assetparse/route_map.py](../tools/assetparse/route_map.py); "Δ from start" = distance from that course's start gate, a rough detour cost). Courses are grouped by the scene hierarchy, so each box is attributed to the course that owns it.

| Order | Course | tier | start gate (x,y) | ability unlocked here | free off-path globals (Δ from start) |
|---|---|---|---|---|---|
| 1 | 1 | 0 | (2493, −218) | wall jump | — |
| 2 | 2 | 2 | (5064, 1958) | dash | cashPerLoop (1.1k u) |
| 3 | 3 | 5.2 | (9750, 7049) | double jump · **unlock prestige** | maxCloneFastness (1.5k) · cashPerLoop (1.7k) · bigCloneChance (3.5k) |
| 4 | 4 | 10 | (20441, 10630) | swap blocks (once) | fastCloneChance (5.6k) · cashPerLoop (11k) · cashPerLoop (21k) |
| 5 | 5 | 16.5 | (−3518, −220) | unlock block swap | cashPerLoop (5.8k) |

Spatial route, in words: course **1** (centre, the spawn area) → **2** (up and left) → **3** (centre, where the prestige unlock and double-jump live) → **4** (far top-right); the **zone-1↔2 changer** sits between courses 3 and 4 at ≈(16677, 7933), and the **end-of-demo** box is in the zone-2 pocket at ≈(17106, 3533). **Course 5** is a separate far-left, high-tier (16.5) branch off the course-1 area at ≈(−3041, −150).

Two placement quirks worth knowing: course 4's hierarchy owns a free `cashPerLoop` box ~21k units away (over near the left edge), and the **swap-blocks-once box and the hidden `unlockBlockSwap` box are co-located** at ≈(17768, 10788) — buying the one-shot swap (course 4) reveals the permanent block-swap unlock (owned by course 5's hierarchy) in place.

### Course rewards (`courseScript`) — the static seed + the formula

Extracted per-instance with `mechanic_values.py courseScript`. The placed `baseReward` is a **small integer seed**; the actual Watt payout is computed at runtime and is dominated by `10^rewardTier`, *not* by `baseReward`:

| Course | tier | `baseReward` | `RewardPrestigeMod` | `BreakerLayerTierMod` | `BreakerLayerRewardTierMod` |
|---|---|---|---|---|---|
| 1 | 0.0 | 1 | 1.0 | 0.0 | −0.85 |
| 2 | 2.0 | 4 | 0.5 | 1.0 | −0.7 |
| 3 | 5.2 | 7 | 1.0 | 1.2 | 0.5 |
| 4 | 10.0 | 10 | 4.0 | 0.0 | 0.0 |
| 5 | 16.5 | 7 | 1.0 | 0.0 | 0.0 |

Payout ([courseScript.cs](../artifacts/decompiled/courseScript.cs)) is `ceil(baseReward · upgrade-multiplier-stack · 10^rewardTier)`, where `rewardTier` seeds from the course's `tier` and is rewritten on prestige (offset by the per-course `*Mod` fields above; persisted to `courseNdata.txt`). **Routing consequence:** the `10^rewardTier` exponent sets course value overwhelmingly by **tier** (a no-prestige run pays ~`10^0` at course 1 up to ~`10^16.5` at course 5 per loop); the upgrade stack is a secondary lever. The serialized `reward`/`gpReward`/`CdReward` are `0.0` at rest (runtime-computed); `gpReward`/`CdReward` never fire in the Watts-only demo.

## Movement abilities — the route spine

All on `Movement` (saved in `playerdata.txt`), each unlocked by a one-time `upgradeBox`. The cost wall gates progression:

| Ability | Cost (w) | Course | Field | Notes |
|---|---|---|---|---|
| Wall jump | 700 | 1 | `wallJumpUnlocked` | |
| Dash | 30,000 | 2 | `dashUnlocked`, `maxAirDashes++` | |
| Double/air jump | 3,500,000 | 3 | `doubleJumpUnlocked`, `maxAirJumps++` | |
| Swap blocks (once) | 50,000,000 | 4 | `swapBlocks()` | one-shot; **reveals hidden boxes**, incl. ↓ |
| Unlock block swap | 3×10¹⁸ | 5 | `blockSwapUnlocked` | `visible=0` until the swap-once box reveals it |
| End demo | free | zone 2 | — | shows `EndOfDemoScreen`, sets `snfDemoCompleted` PlayerPref |

Also live: **checkpoints** (`isRespawningAtCheckpoints`, PlayerPrefs) — death respawns at a checkpoint vs course start; a death-respawn system, *not* a savestate, and gameplay-affecting, so control it for reproducible runs (determinism source #10). **Block swap** (`colouredBlockSwapper`) blue/red toggle is the one traversal gimmick present.

## Clones (the income engine)

- **`cloneCount`** local upgrade adds parallel clones; loop interval `BaseCloneInterval = pathLength / cloneCount`. Shorter/faster best path ⇒ more loops/s.
- **Fastness / Bigness** — each clone rolls a tier on spawn (`Random.Range(0,101)` vs `fastCloneChance`/`bigCloneChance`, capped by `maxCloneFastness`/`maxCloneBigness`); payout `≈ reward · (tier·2 + 1)`. **RNG-driven** — see [determinism.md](determinism.md).

## Upgrades you can actually buy

**Local (per-course boxes):** `cloneCount`, `cashPerLoop` (base reward), `cloneMult`, `fastCloneChance`, `bigCloneChance`, `prestige` (boost all previous courses; boxes in courses 4 & 5).

**Global (game-wide boxes):** `cashPerLoop`, `fastCloneChance`, `bigCloneChance`, `maxCloneFastness`, `maxCloneBigness`, and **`unlockPrestige`** (course 3, 70,000 w — enables the prestige layer and grants +1 `cloneMult`). The clone-tier-cap globals (`maxCloneFastness`/`maxCloneBigness`) raise the *ceiling* on clone payout multipliers and are disproportionately strong.

### Off-the-beaten-path bonus boxes

Global upgrades placed **away from each course's main upgrade row** — several **free** — worth a routing detour. (x,y world position; "free" = baseCost 0.)

| Pos (x,y) | Upgrade | Cost | Course area |
|---|---|---|---|
| (5239, 838) | cashPerLoop | **free** | 2 (below main row) |
| (6350, 6245) | bigCloneChance | **free** | 3 (below) |
| (8622, 5780) | cashPerLoop | **free** | 3 (below) |
| (11200, 7521) | **maxCloneFastness** | **free** | 3 (far right) |
| (−324, 14487) | cashPerLoop | **free** | 4 (far up-left) |
| (10032, 14487) | cashPerLoop | **free** | 4 (high) |
| (15017, 11959) | fastCloneChance | **free** | 4 (above row) |
| (−9545, 2690) | cashPerLoop | **free** | 5 (far left) |
| (20731, 15314) | maxCloneBigness | 300 | 4 (high) |
| (−5047, 4209) | bigCloneChance | 1,000 | 5 |

The free `maxCloneFastness` / `fastCloneChance` / `bigCloneChance` pickups are the high-value grabs (permanent global tier/chance boosts for ~nothing but travel time).

## Upgrade-box mechanics (`upgradeBox`)

A box is bought by standing in its trigger when you can afford it (`OnTriggerStay2D`, 0.2 s cooldown). Cost scales per purchase and by course tier (`·10^tier`); `Cap` limits purchases; `buyMax` buys as many as affordable. `customBoxUpdate` auto-buys when configured (on an `InvokeRepeating` with a **randomised phase offset** — an RNG source, see determinism). Targets are one of: a global upgrade, a local upgrade, or a movement unlock.

## Debug warp room

8 `JumpToStateButton`s in a debug panel (`Debug/pauseMenu`) warp to `Course2…Course5 / Area2 / Area2Atom / area2c3 / infMoney` — each **wipes `Savedata/`** first, then loads. Same lever as `Movement.placesToStart`; handy for harness fixtures or zone-entering `.tas` files without grinding there. (`infMoney` unlocks all abilities and sets currencies high.)

## Determinism quick-refs

- **RNG (unseeded by the game):** clone fastness/bigness rolls, course `UpdateReward` phase offset, upgrade-box `customBoxUpdate` phase offset. Seed with `IGTAS_RNG_SEED`.
- **Income accrues while idle:** clone payouts tick every frame, so economy start-time is not invariant even though player physics is.
- Full investigation: [determinism.md](determinism.md).
