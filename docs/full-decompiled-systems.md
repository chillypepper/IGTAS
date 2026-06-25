# IGTAP systems — full decompiled reference

Comprehensive catalogue of **every** system in the build, grounded in the decompiled source and the extracted scene data — including content that exists in code but is **not reachable in the demo** (slated for the full release). For day-to-day routing use the trimmed companion, **[systems.md](systems.md)**, which covers only what a player can actually touch in the demo; this file is the reference when you need the whole picture.

Entries note the owning class and the save file/field where relevant; see [determinism.md](determinism.md) for which of these are RNG/timing sensitive.

> IGTAP is an **incremental game that is also a platformer**: you run platformer *courses*, and your runs seed *clones* that replay your path to generate currency, which buys *upgrades* that make everything faster — woven across *zones/areas* and *prestige* layers. The optimal route interleaves movement and economy.

## Demo-presence legend

Each system is tagged by what the **scene extraction** (`tools/assetparse`, see the appendix) found physically placed in the demo's scenes (`level0`=MainMenu, `level1`=Overworld):

- **`[DEMO]`** — reachable by normal play in the demo (a button exists / a component is instanced and active).
- **`[CODE-ONLY]`** — the code path exists, but nothing in the demo scenes drives it, so it stays at its default (inert) in normal demo play.
- **`[FULL]`** — full-release content: no button **and** no in-scene component in the demo.

---

## Geography

- **Course** `[DEMO]` — a platformer level/track (`courseScript`, `course1..5data.txt`). The demo has **5 courses** (all present, with tiers 0 / 2 / 5.2 / 10 / 16.5 for courses 1–5). Each owns its upgrade boxes, local upgrades, clones, a `reward`, and a `tier`/`rewardTier`. Running a course records your path; clones then replay it.
- **Zone** `[DEMO]` — `Movement.currentZone` (int, default 1; saved in `playerdata.txt`). Observed values 1 and 2. In the demo, zone 2 is reached by walking into a **`zoneChanger`** trigger volume (at world ≈(16677, 7933)) that toggles the `area1`/`area2` `ZoneDisableLoadList` roots — **not** via the `area2DoorOpen` upgrade (that door/`PlatformMover` is `[FULL]`).
- **Area** — higher-level region. **Area 1** has discrete **states** (`globalStats.area1states`: `normal=0`, `tripBreaker=1`, `Overgrown=2`; saved as `_area1State`) that change its layout/mechanics. **Area 2** is the zone-2 facility section.
- **placesToStart** *(debug lever)* `[DEMO]` — `Movement.placesToStart` enum (`NORMAL, Course2, Course3, Course4, Course5, Area2, Area2Atom, area2c3, infMoney`), consumed once from the static `globalStats.placesToStartOverride` at `Movement` start. `infMoney` unlocks all abilities + sets currencies high; the `CourseN`/`Area2` values start the player at that location. Driven in-scene by **8 `JumpToStateButton`s** in a debug panel (`Debug/pauseMenu`) — each **deletes `Savedata/`** then warps. A built-in fast-travel/test lever complementary to save fixtures.

## Currencies

Five, in `globalStats.Currencies` → `globalStats.currencyLookup` (a static `Dictionary<Currencies,double>`), persisted in `playerdata.txt` `_currencies` by `_Key`. Display symbols from `CashDisplay`:

| # | Enum | Symbol | Name | Source | Demo |
|---|------|--------|------|--------|------|
| 0 | `Cash` | **w** | Watts | Course rewards (clones completing loops); the primary platforming currency | `[DEMO]` — the **only** currency generated in the demo |
| 1 | `GreenPower` | **gp** | Green Power | Green clones; multiplied by Clone Dust | `[CODE-ONLY]` — `greenCloneClance` defaults 0 and no box raises it ⇒ no green clones ⇒ no gp |
| 2 | `AtomicPower` | **np** | Nuclear Power | Atoms (`globalStats.FixedUpdate` ticks `NPrewardPerTick` up to `NPrewardCap`) | `[CODE-ONLY]` — no `AtomColliderSystem` in-scene ⇒ `effectiveAtomCount` stays 0 ⇒ np stays 0 |
| 3 | `regularNumber` | — | (atom-count metric) | Not a spendable currency in the usual sense — used by `NPperSecDisplay`/`CashDisplay` to show atom progress (white-atom-equivalents toward the next tier). **Least understood; verify before relying on it.** | `[CODE-ONLY]` |
| 4 | `CloneDust` | **cd** | Clone Dust | Clones (when enabled per-course); raises a GP multiplier `CloneDustGpMult = CD^(0.25 + 0.01·increaseCloneDustPower)` | `[CODE-ONLY]` — `enableCloneDustGeneration` local upgrade has no box in the demo |

## Clones

- **Clone** `[DEMO]` — a replay of your recorded best path on a course (`clonesScript`). Clones run the course loop and, on completion, pay out currency. The core idle/income engine.
- **Clone path / sprites / scales / end velocity** — the recorded run a clone replays (`clonePath`, `cloneSprites`, `cloneScales`, `cloneEndVelocity`, `pathLength`, `bestPathTime`; saved per course). A faster/shorter best run ⇒ faster clone loops ⇒ more income.
- **Clone count** `[DEMO]` — how many clones run in parallel (`cloneCount`; the `cloneCount` local upgrade buys more). Loop interval `BaseCloneInterval = pathLength / cloneCount`.
- **Fastness / Bigness tiers** `[DEMO]` — per-clone random tiers rolled on spawn (`Random.Range(0,101)` vs `fastCloneChance`/`bigCloneChance`, capped by `maxCloneFastness`/`maxCloneBigness`). A bigger/faster clone pays a higher multiple: payout `≈ course.reward · (tier·2 + 1)`. **RNG-driven** — see determinism.
- **Green clone** `[CODE-ONLY]` — a rarer clone (`greenCloneClance` %) that pays **GreenPower** (and Clone Dust when enabled) instead of just Watts. Chance defaults to 0 and is unraisable in the demo.

## Atoms / Nuclear Power — `[FULL]`

No `AtomColliderSystem` instance and no `spawnNewAtom`/`atomLevelChance`/`ExemptCourseFromAtomPrestige` boxes exist in the demo scenes; the whole atom layer is full-release content. The `Area2Atom` debug warp targets a location not built in the demo.

- **Atom** — produced by spending to "spawn a new atom" (`spawnNewAtom` global upgrade; `AtomColliderSystem`, `atomColliderData.txt`). Atoms feed **AtomicPower (np)**: `globalStats.FixedUpdate` adds `NPrewardPerTick` each physics frame up to a cap.
- **Atom tier** — atoms have tiers; higher tiers are worth disproportionately more (`effectiveAtomCount += pow(atomTierEffectiveness + 10·Tier, Tier)·0.001`). Atoms **craft** up tiers when ≥`3+tier` of a tier collect (`HandleTierLogic`, advanced by `Time.deltaTime` — deterministic under lockstep; the visual ring uses `Time.time`).
- **grossAtomCount / effectiveAtomCount** — the two atom totals driving `NPrewardPerTick = effectiveAtomCount · (1 + grossAtomCount/20) · (income upgrades)`.
- **waitingAtoms** — atoms bought but not yet materialised (`callAddNewAtom` every 0.4s drips them in, each granting +1 np).

## Upgrades

- **Global upgrade** — `globalStats.globalUpgradeSet` (24 entries; key = ordinal), stored in `globalUpgradeDict`, saved in `playerdata.txt`. Apply game-wide. **Demo presence varies sharply per key** — only 6 of the 24 have a buyable box in the demo:
  - `[DEMO]` boxes: `cashPerLoop`, `fastCloneChance`, `bigCloneChance`, `maxCloneFastness`, `maxCloneBigness`, `unlockPrestige`.
  - `[DEMO]` via side effect: `cloneMult` (no box of its own — the `unlockPrestige` purchase increments it).
  - `[FULL]` (no box): `spawnNewAtom`, `atomLevelChance`, `greenCloneClance`, `TreeGrowth`, `openGate`, `increasedWatts`, `increasedGreenPower`, `increasedNuclearPower`, `area2DoorOpen`, `ZipMoversUnlocked`, `ExemptCourseFromAtomPrestige`, `TripleThreatIncrease`, `unlockJiggleDrops`, `increaseCloneDustPower`, `compBoostUnlocked`, `compBoostTime`, `compBoostStrength`.

  Full key list + effect dicts in `globalStats.cs`.
- **Local upgrade** — `localUpgrades.localUpgradeSet` (per course, `localUpgrades`, saved in `courseNdata.txt`): `cloneCount`, `cashPerLoop`, `fastCloneChance`, `bigCloneChance`, `cloneMult`, `prestige`, `GreenCloneRewardBase`, `activateNextBreakerLights`, `enableCloneDustGeneration`, plus `GLOBAL`/`Movement` category markers. Affect only their course. Demo boxes: `cloneCount`, `cashPerLoop`, `cloneMult`, `fastCloneChance`, `bigCloneChance`, `prestige` `[DEMO]`; `GreenCloneRewardBase`, `activateNextBreakerLights`, `enableCloneDustGeneration` `[FULL]`/`[CODE-ONLY]`.
- **Upgrade box** — the physical buyable in a course (`upgradeBox`). Has `baseUpgradeCost`, current `upgradeCost` (scales `·10^tier` and grows per purchase), `TimesUsed`, a `Cap`, `buyMax`, and a target (`globalUpgrade` or `upgrade`/`movementUpgrade`). `customBoxUpdate` (an `InvokeRepeating` with a randomised phase offset) auto-buys when affordable if configured. Demo has **46 boxes** total (3 in MainMenu, 43 in Overworld) — full inventory in the appendix.

## Prestige & resets

- **Prestige** `[DEMO]` — reset-for-multiplier mechanics (`prestigeMult` on `globalStats`; `onPrestige` on `courseScript`/`clonesScript`/`upgradeBox`/`localUpgrades`). The `prestige` local upgrade "boosts all previous courses" (boxes in courses 4 & 5); `unlockPrestige` global upgrade (one box, course 3, 70,000 w) enables the layer and grants +1 `cloneMult`. (`prestigeEnabler` the component is `[FULL]` — not in-scene — but the prestige *boxes* are present.)
- **Atom prestige** `[FULL]` — `spawnNewAtom` resets Watts + course upgrades to mint an atom; `ExemptCourseFromAtomPrestige` exempts a course from that reset (`exemptFromAtomPrestige` flag per course).
- **Breaker prestige** — a prestige variant tied to the trip-breaker layer (`isBreakerPrestige` in `onPrestige`).
- **Tier / rewardTier** — per-course exponents: `reward = ceil(baseReward · 10^rewardTier · mults)`. Higher tiers come from prestige and scale both cost and reward by powers of 10.

## Sub-systems & one-offs

- **Trip breaker** `[DEMO]` — `tripBreakerScript` (`breakerdata.txt`): emergency-lighting puzzle state in Area 1 (`activeLights`, `fixedAreas`, `trippedBreakerActive`); ties into the `area1states.tripBreaker` state and the `activateNextBreakerLights` local upgrade. One instance per scene.
- **Tree** `[FULL]` — `TreeController` (`treeData.txt`): an upgrade tree planted by the `TreeGrowth` global upgrade; `TreeSegment`s unlock more upgrade boxes. No `TreeController`/`TreeSegmentActivationComponent` in the demo scenes.
- **Block swap** `[DEMO]` — `blockSwapUnlocked` + `colouredBlockSwapper` blue/red block toggle (`isBlueActive`); a traversal mechanic. One `colouredBlockSwapper` in-scene; unlocked via the `swapBlocksOnce` (one-shot) → `unlockBlockSwap` box chain in courses 4–5.
- **Jiggle drops** `[FULL]` — jump/dash refresh orbs (`unlockJiggleDrops`); restore air jumps/dashes mid-flight. No `JiggleDropScript` in the demo scenes.
- **Zip movers** `[FULL]` — `ZipMoversUnlocked`; moving-platform traversal. No `PlatformMover` instances in the demo scenes at all.
- **Comp boost** `[FULL]` — completing a course temporarily boosts it (`compBoostUnlocked`, `compBoostTime`, `compBoostStrength`; `compBoostTimeLeft`/`compBoostBonus` per course). No boxes.

## Movement abilities (unlockables)

All on `Movement`, saved in `playerdata.txt`. **All movement-unlock boxes are present in the demo** (`upgradeBox.movementUpgrades` = `{dash, wallJump, doubleJump, swapBlocksOnce, unlockBlockSwap, endDemo}`):

- **Dash** `[DEMO]` (`dashUnlocked`, `maxAirDashes`) — box in course 2 (30,000 w). `omniDashUnlocked` is a further variant (`[CODE-ONLY]` — field exists, no box).
- **Double / air jump** `[DEMO]` (`doubleJumpUnlocked` ⇄ saved `_airJumpUnlocked`, `maxAirJumps`, `airJumpsLeft`) — box in course 3 (3,500,000 w).
- **Wall jump** `[DEMO]` (`wallJumpUnlocked`) — boxes in course 1 (700 w; one in each scene).
- **Block swap** `[DEMO]` — `swapBlocksOnce` (course 4, 50,000,000 w, one-shot, reveals hidden boxes) and `unlockBlockSwap` (course 5, 3×10¹⁸ w, `visible=0` until revealed).
- **Light** (`lightActive`, `lightRadius`) — a personal light radius (relevant in dark/breaker areas).
- **Checkpoints** `[DEMO]` — `isRespawningAtCheckpoints` (PlayerPrefs-backed) controls whether death respawns at a checkpoint or course start. This is a **death-respawn** system, *not* a savestate; it is gameplay-affecting and must be controlled for a reproducible run (see determinism source #10). Many `checkpointScript` instances in-scene (20 in MainMenu, 54 in Overworld).
- **End-of-demo** `[DEMO]` — `movementUpgrades.endDemo` box (zone 2) shows the `EndOfDemoScreen` and sets the `snfDemoCompleted` PlayerPref; the demo boundary.

## Determinism-relevant cross-references

- **RNG draws** (global `UnityEngine.Random`, unseeded by the game): clone fastness/bigness rolls (`clonesScript`), course `UpdateReward` `InvokeRepeating` phase offset (`Random.Range(0,5)`), upgrade-box `customBoxUpdate` phase offset. Seedable via `IGTAS_RNG_SEED` — see [determinism.md](determinism.md).
- **Scaled-time vs wall-clock:** course/box/atom timers use `Invoke`/`Time.deltaTime` (scaled → deterministic under lockstep); only the dash `TimeStop` and cosmetics use wall-clock `unscaledDeltaTime`/`Time.time`.
- **Income accrues during idle:** `globalStats.FixedUpdate` (np) and clone payouts tick every frame, so economy start-time is **not** invariant even though player physics is.

---

## Appendix — scene extraction (source of the demo-presence tags)

Generated by **[../tools/assetparse/extract_scene.py](../tools/assetparse/extract_scene.py)** (regenerable; see its README). The demo is a release build (Unity 6000.3.17f1) with MonoBehaviour type trees stripped — regenerated from `Assembly-CSharp.dll` via `TypeTreeGeneratorAPI`, so the box targets/costs/positions below are read directly from the serialized scenes, not inferred.

### Key system components — instance counts per scene

| Component | MainMenu (level0) | Overworld (level1) |
|---|---|---|
| `checkpointScript` | 20 | 54 |
| `SpringScript` | 2 | 19 |
| `spikeScript` | 6 | 7 |
| `startGate` / `endGate` | 1 / 1 | 5 / 16 |
| `colouredBlockSwapper` | 1 | 1 |
| `tripBreakerScript` | 1 | 1 |
| `zoneChanger` | 0 | 1 |
| `ZoneDisableLoadList` | 1 | 2 |
| `AtomColliderSystem` | 0 | **0** |
| `PlatformMover` (zip movers / area-2 door) | 0 | **0** |
| `TreeController` / `TreeSegmentActivationComponent` | 0 / 0 | **0 / 0** |
| `JiggleDropScript` | 0 | **0** |
| `prestigeEnabler` | 0 | **0** |

### Upgrade-box target census (both scenes, 46 boxes)

```
GLOBAL: cashPerLoop ×8 · maxCloneFastness ×2 · maxCloneBigness ×2 · bigCloneChance ×2 ·
        fastCloneChance ×1 · unlockPrestige ×1
LOCAL:  cashPerLoop ×6 · cloneCount ×6 · cloneMult ×4 · fastCloneChance ×3 ·
        bigCloneChance ×2 · prestige ×2
MOVE:   wallJump ×2 · dash ×1 · doubleJump ×1 · swapBlocksOnce ×1 · unlockBlockSwap ×1 · endDemo ×1
```

Full structured dump (per-box position, currency, cost, caps, visibility flags) lives in `artifacts/decompiled/scene_dump.json` (gitignored, regenerate with the extractor). The demo-routing view of these boxes — ability gates, off-path bonus pickups — is in **[systems.md](systems.md)**.
