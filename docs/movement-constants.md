# Movement physics constants — the master block

The `Movement` MonoBehaviour carries ~108 serialized fields; this is the TAS-relevant subset — the actual **placed `[SerializeField]` values from the scene**, extracted with [`tools/assetparse/mechanic_values.py Movement`](../tools/assetparse/mechanic_values.py) (scene `level1`). The single most reused reference for routing and for predicting movement frame-by-frame.

> **Trust the scene values, not the decompile defaults.** Unity overwrites a field's code default with the serialized scene value on deserialization, and several differ sharply — e.g. [Movement.cs](../artifacts/decompiled/Movement.cs) declares `runSpeed = 2000f` (L113) and `framesToReachTopSpeed = 2f` (L438), but the scene ships `45` and `4.5`. The numbers below are the scene values (what actually runs). This is the whole reason the scene-asset audit exists: the decompile gives logic, the scene gives the values.

> **The `*10f` factor.** Every horizontal-velocity expression in `Movement.FixedUpdate` multiplies `runSpeed` by `10f` (e.g. `Velocity.x = runSpeed * 10f`, L1522). So the effective top run speed is **450 u/s** (`45 * 10`), not 45. Likewise wall-jump sets `Velocity.x = runSpeed * wallJumpDirection * 10f`. Apply the ×10 to any `runSpeed`-derived figure.

## Locomotion

| Field | Value | Meaning |
|---|---|---|
| `runSpeed` | 45 | ×10 in use ⇒ **450 u/s** effective top speed |
| `framesToReachTopSpeed` | 4.5 | accel = `runSpeed*10/4.5` = **100 u/s per frame** to reach 450 |
| `jumpForce` | 780 | initial upward velocity on a grounded jump |
| `gravity` | 25 | per-frame downward accel (×`50*fixedDeltaTime` = ×1 at 50 Hz) |
| `maxDownwardsSpeed` | 100 | terminal fall speed clamp (×10 in use ⇒ **1000 u/s**) |
| `fastFallMod` | 1.35 | down-held fall multiplier |
| `jumpApexSpeedBonus` | 6 | horizontal bonus at apex |
| `jumpApexAntiGrav` | 5 | reduced gravity near apex |

## Coyote / leniency (frames)

| Field | Value | Meaning |
|---|---|---|
| `maxCoyoteFrames` | 6 | grounded-jump grace after leaving a ledge |
| `maxWallCoyoteFrames` | 6 | wall-jump grace after leaving a wall |
| `wallBounceLeniency` | 0.1 | seconds |
| `SquishGraceFrames` | 4 | frames before a squish kills |
| `squishDistance` | −20 | squish detection threshold |

(Input buffers `jumpBuffer` 0.12s / `jumpCutBuffer` 0.26s are second-based `Invoke` timers, not serialized here — see [determinism.md](determinism.md) for their framerate interaction.)

## Wall movement

| Field | Value | Meaning |
|---|---|---|
| `wallJumpForce` | 715 | upward velocity off a wall jump |
| `maxWallJumps` | 4 | wall jumps before grounding |
| `wallJumpMovementLockFrames` | 5 | frames of horizontal lock after a wall jump |
| `maxWallSlideSpeed` | 350 | wall-slide terminal speed |
| `wallSlideFriction` | 11 | slide decel |
| `finalWallClingTime` | 0.7 | seconds of cling at top |
| `wallBounceMagnitude` | (−65, 300) | wall-bounce velocity |
| `wallBounceMovementLock` | 1.0 | seconds locked after a bounce |

## Air jump (default-locked)

| Field | Value | Meaning |
|---|---|---|
| `airJumpForce` | 720 | velocity per air jump |
| `maxAirJumps` | 0 | **0 until `doubleJumpUnlocked` grants it** (course 3) |

## Dash (default-locked)

| Field | Value | Meaning |
|---|---|---|
| `dashSpeed` | 167 | dash velocity (note: not ×10) |
| `dashFrames` | 10 | dash duration in frames |
| `defaultDashCooldown` | 0.2 | seconds (10 frames at 50 Hz) |
| `dashTimeStopTime` | 0.05 | freeze duration → driven as **3 canonical frames** by the TAS freeze hook (see below) |
| `maxAirDashes` | 0 | **0 until `dashUnlocked`** (course 2) |
| `dashBoostMin` / `dashBoostMax` | 0 / 6 | dash-boost range |
| `dashBoostUpwards` | 420 | upward boost from a dash-jump |
| `dashJumpMagnitude` | 100 | dash-jump horizontal carry |
| `hyperJumpMagnitude` | 165 | hyper-jump carry |
| `hyperJumpHeightMod` | 0.57 | hyper-jump height scale |

### `dashTimeStopTime` — the determinism outlier

`TimeStop(0.05)` ([Movement.cs](../artifacts/decompiled/Movement.cs) L1965) sets `Time.timeScale = 0` and runs `while (i <= duration) { i += Time.unscaledDeltaTime; yield WaitForEndOfFrame; }`. The freeze paces off `unscaledDeltaTime`, which **free-runs under `timeScale = 0`** (`captureDeltaTime` is inert while frozen — it does *not* pin `unscaledDeltaTime`), so left to the game the freeze spans a present-rate-dependent, non-deterministic count (measured ~90 frames headless). The TAS path makes it deterministic by **owning the freeze's progression**: a Harmony hook captures the `IE_TimeStop` coroutine and drives it for exactly **`floor(dashTimeStopTime · 50) + 1 = 3` canonical frames** at any present rate (the coroutine's `<=`/post-increment semantics — *not* a `ceil`), then restores `timeScale` (to `globalStats.baseGameSpeed`, via the coroutine's own `!menuOpen` guard). Proven byte-identical and present-rate-independent; live-validated. This is the dash-freeze **behaviour-takeover**; mechanism, frame-count derivation + version-fragility: [behaviour-takeovers.md](behaviour-takeovers.md) (the dual-clock frame model is in [behaviour-takeovers.md](behaviour-takeovers.md#frozen-time-under-lockstep--the-dual-clock-frame-model); determinism framing in [determinism.md](determinism.md) mechanism (b)). (`baseGameSpeed == 1f` in normal play; a non-1 Hard-mode base speed would need checking — see [determinism.md](determinism.md).)

## Momentum

| Field | Value | Meaning |
|---|---|---|
| `momentumDecay` | 1.35 | standard momentum bleed |
| `MovingOppositeMomentumDecay` | 20 | decay when input opposes momentum |
| `flatMomentumDecay` | 300 | flat per-frame momentum loss |
| `maxPlatformMomentumInherited` | 1300 | cap on platform-carried velocity |
| `afterimageMomentumMagnitudeRequired` | 200 | momentum to spawn afterimages (cosmetic) |

## Collider (swaps shape on dash)

| State | size | offset |
|---|---|---|
| Default | (41, 82) | (4, −10) |
| Dash | (41, 54) | (4, −5) |

Dashing **shrinks the hitbox vertically** (82→54) — relevant to spike/ceiling clearance while dashing. The collider is swapped live in `Movement`, so the hitbox overlay sees whichever is active.

## Layers

| Field | Bits | Layer |
|---|---|---|
| `groundRaycastLayer` | 128 | the `Ground` layer (bit 7) — solidity source of truth |
| `blockingGroundRaycastLayer` | 64 | bit 6 — block-swap blocking geometry |

(Matches the overlay's layer-based ground classification — see [hitbox-overlay.md](hitbox-overlay.md).)

## Other notable serialized state

- `DEBUG_startingCourse = 0` — the in-scene debug-start lever (`placesToStart`); settable to warp a run straight to a course. An alternative to walking in from spawn for reaching a course quickly (see [systems.md](systems.md) debug warp room).
- `respawnPoint = (−60.6, −301.1)`, `isRespawningAtCheckpoints = 1` — default respawn.
- **Death → respawn timing.** `onDeath()` freezes the player (`cutsceneMode = deathFreeze`) and schedules `Invoke("deathRespawn", 0.6f)` — a **0.6 s (= 30 frames at 50 Hz) freeze** before the player is teleported to `respawnPoint`/`courseResetPoint`. Spikes (the common hazard, `spikeScript`) kill via `OnTriggerStay2D` after `graceFrames = 1` (i.e. the 2nd overlapping physics frame), or immediately if dashing into them (`killPlayerOnDash`). This `Invoke` once landed a death ±1 frame headless-vs-live (a `Time.time`-phase straddle); it is now **resolved** by the death-respawn behaviour-takeover, which severs the dependency on `Time.time` and re-drives the respawn off the frame counter at `ceil(0.6 · 50) = 30` frames — see [behaviour-takeovers.md](behaviour-takeovers.md) and [determinism.md](determinism.md) "respawn-timer +1".
- Ability-unlock flags (`wallJumpUnlocked`, `dashUnlocked`, `doubleJumpUnlocked`, `blockSwapUnlocked`, `omniDashUnlocked`) all ship **0** — granted by save / upgrade boxes. `omniDashUnlocked` has no demo box (code-only).

## Movement tech (untested routing opportunities)

Two behaviours in the current build that no `.tas` exercises yet — things to probe when route-building resumes. Both also feed the exploit hunt as movement-chain concepts (the broader clip/momentum surface) — testable hypotheses for the glitch hunt.

- **Step-up.** The grounding path casts a third downward ground-ray (`raycastHit2D5`) from a foot point offset **6u in the facing direction, y=−32**, **23u** down on the `Ground` layer; when it hits ground and neither blocking-layer ray does, the player grounds on it ([Movement.cs:802](../artifacts/decompiled/Movement.cs#L802), [:815](../artifacts/decompiled/Movement.cs#L815), gated `!recentlyJumped`). Effect: the player walks up a low ledge (rise ≤~23u, ~¼ of the 82u collider) that previously needed a jump — stair-like terrain may be climbable input-free. (Distinct from the long-standing `+10f` position nudge at [:813](../artifacts/decompiled/Movement.cs#L813), a separate ledge-clearing snap.)
- **Super-jump refills air dashes.** `CheckForSuperJump` fires when `jumpBuffer && onGround` and **not** `(canHyper && framesSinceLastDash < 5)` — i.e. that pairing is a *blocker* (early-return), not a requirement; the body then takes the **hyper** branch when `canHyper` (reduced-height `jumpForce·hyperJumpHeightMod` + `hyperJumpMagnitude`) else the **dash-jump** branch (full `jumpForce` + `dashJumpMagnitude`). Either way it sets `airDashesLeft = maxAirDashes` on launch — restoring the full air-dash budget the way a spring does. (Verified: a ground dash-then-jump fires the dash-jump branch — see the super-jump `endRecentJump` takeover.)

The post-launch suppression these interact with (`recentlyJumped`, a 30 ms ground-redetection window also covering springs) is a determinism consideration — see [determinism.md](determinism.md).

## Regenerating

These are placed scene values that can change between builds, so **re-check after any game update** — and note the reflection field names in `Plugin.cs`/the overlay mirror them (they break silently if the game renames a field). Regenerate with `mechanic_values.py Movement`; setup/invocation is in [tools/assetparse/README.md](../tools/assetparse/README.md).
