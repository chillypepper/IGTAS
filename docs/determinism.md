# Determinism — working notes

> **The live investigation doc.** Settled facts graduate to the stable topic docs ([movement-constants.md](movement-constants.md), [README.md](README.md)); speculation, open theories, and half-run experiments stay here so those don't fill with "testing X."

## Guiding principle

**Lowest-touch-points TAS:** the only difference between this TAS and a human should be frame-perfect input timing — physics, timing, game logic stay the same code path a human plays. Every determinism decision prefers making the *existing* loop reproducible over special-casing playback. How close we are to "human + frame-perfect" is the question this doc tracks.

## How we measure

The headless harness makes determinism measurable without OS input injection:

```
IGTAS_AUTOPLAY=main.tas IGTAS_STATE_LOG=/tmp/run.csv IGTAS_RESULT_FILE=/tmp/run.result \
  timeout -s KILL 40 bash "<game>/run_bepinex.sh" "<game>/IGTAP.x86_64" \
      -batchmode -nographics -screen-fullscreen 0
```

It loads `Overworld`, enables lockstep, settles under it, then drives playback through the real virtual-keyboard path, logging the **full state per physics frame**:

```
frame, posx,posy, velx,vely,           # Rigidbody2D.position + game Velocity
       momx,momy, mpvx,mpvy, onground, # rest of body.linearVelocity = Velocity+momentum+movingPlatformVelocity
       rng0..rng3,                     # UnityEngine.Random.state (global PRNG)
       cash,greenpower,atomicpower,regularnumber,clonedust,npreward  # economy ledger
```

plus a summary line with **two FNV-1a hashes**: `hash` = pos+vel only (movement regression anchor), `fullhash` = the whole stream (catches an economy/RNG-only divergence even when movement is bit-identical). Both baseline values are **build-pinned** — they shift whenever the build's physics floats change *even while determinism holds*, so always compare against the pinned baseline, never eyeball the final position.

**Determinism check:** run the same `.tas` twice; identical `fullhash` ⇒ the whole state reproduced. Knobs: `IGTAS_AUTOPLAY_SCENE` / `SETTLE_FRAMES` / `STATE_LOG` / `RESULT_FILE` / `NO_QUIT` / `RNG_SEED` (full inventory: [README.md](README.md)).

**Instrument-and-measure precedent.** When an engine-behaviour claim needs proof and there's no input injection, add a temporary second `[BepInPlugin]` in the same DLL that drives/asserts the thing and logs a measured value, run headless, scrape, remove (an env-gated runtime probe is the same shape — it caught the `Config.Bind`-persistence bug). Measure; don't reason from the decompile alone.

## What's DETERMINED

- **Physics is a fixed 50 Hz** (`Time.fixedDeltaTime == 0.02`, probe-confirmed; no game code writes it) — the rate a TAS frame represents.
- **Physics in `FixedUpdate`, input edges in `Update`** (`InputSystem` is `ProcessEventsInDynamicUpdate`): jump/dash *presses* (`WasPressedThisFrame`) sample per render frame and buffer to the next fixed step; held axes (`ReadValue`) sample in `FixedUpdate`.
- **`Time.captureDeltaTime = 1/50` gives exact 1:1 Update→FixedUpdate lockstep**, decoupled from the wall clock; present rate then only sets presentation speed. No game code touches it, so it holds without re-assert. (probe-confirmed) The 1:1 is now **continuously guarded** by the lockstep tripwire (Tooling) — it asserts the per-frame FixedUpdate count every Update during a run, so a future drift or bad transition surfaces instead of silently desyncing.
- **The virtual keyboard genuinely reaches gameplay headlessly** — a played `main.tas` advances the player downrange while idle stays at spawn, jumps fire. The real input path, not a stub.
- **Lockstep from scene-load makes playback frame-perfect** — consecutive headless launches produce a byte-identical CSV + hash (position included). The lesson: lockstep must cover the **settle window too**, not just playback — a variable-length non-lockstep settle seeded the world slightly differently each run, which deterministic playback then amplified into position drift. (Always measure `Rigidbody2D.position`; `transform.position` lags/smooths the body.)
- **Start time is invariant for player physics** — settle 50/100/200/350 frames → identical hash + frame-0 state (the player rests at spawn well before 50 frames; the economy never feeds back into the body). **Not** invariant for an economy-scoring run — currency/clone draws keep evolving during idle.
- **The settle window (`IGTAS_SETTLE_FRAMES`, default 50) is currently LOAD-BEARING, and a workaround.** It exists because the harness load drops the player at the save's `_respawnPoint` *above* the floor, so it must fall and come to rest before playback begins, under lockstep, to reach a reproducible start. It is the harness analogue of a human already standing somewhere when they press F8 — NOT a game mechanic. Two consequences worth holding: **(a)** behaviour-takeovers are gated OFF during settle (`TakeoverRunActive` = `isReplaying || isRecording`, not `harnessActive`) so settle runs the game's own native timers exactly as live pre-F8 does — a takeover armed during settle would never advance and would strand state; **(b)** settle length decides which triggers land in settle (resolve native) vs playback (taken over), and a timer scheduled in settle that *straddles* into playback fires native (±1) — a narrow edge present in both harness and live. A fully-set-up harness load that places the body directly at rest would remove the need for settle entirely.
- **The player's `Movement` consumes ZERO gameplay RNG** — every `Random` call in it is audio (SFX/pitch). This is *why* movement reproduces bit-for-bit regardless of seed. (decompile-audited)
- **The game never seeds `UnityEngine.Random`** — system-seeded at startup, differs every launch. Harmless for movement, decisive for the economy. (decompile-audited)
- **Economy/RNG reproducibility is a seed away** — two unseeded runs diverge on the RNG/economy columns; two at the same seed are byte-identical across the full state (movement reproduces either way). See the economy section.
- **The economy start is save-file-dependent — and anchorable.** The game loads `Savedata/` (component-scattered `JsonUtility` files; `playerdata.txt` is the load gate). A reproducible economy run needs a *fixed save*, not just a seed (the physics spawn is save-independent, the economy isn't): `IGTAS_SAVE_FIXTURE=<absolute dir>` copies a committed fixture in before scene-load (the harness disables autosave so the run is non-destructive). ⚠️ It **must be absolute** — resolved against the *game's* cwd, a bare name silently falls through to stale `Savedata/`. Canonical base: the `fresh` fixture; the player is placed at the save's `_respawnPoint` (`fresh` = `(-60.6,-301.1)`).
- **Player-body interpolation is a render→logic leak; the TAS path turns it OFF** (the decisive fix for a jumping route). The body ships `interpolation = Interpolate` (a *render* feature writing a smoothed `transform.position`), but the game's movement logic reads `transform.position` for its ground/wall raycasts — so the interpolated transform **leaks render timing into gameplay**: it lags `body.position` by a render-frame *fraction* live but a **full physics step** headless (no render frames advance it). Symptom: every jump frame advanced the body's x ~7.6 px live but **0 px headless with byte-identical velocities** — accumulating into a missed platform + death ~frame 400. Fix: `body.interpolation = None` for any TAS run (record + playback, live + harness), so `transform = body` each physics step in both — one render-independent ruler (`ApplyTasInterpolation`/`RestoreTasInterpolation`, [Plugin.cs](../Plugin.cs)). Same family as the dash freeze (mechanism (b)); reversible if the game ever moves its raycasts into `FixedUpdate`. The "velocity matches, position doesn't, only on jump frames" fingerprint is the instrument-and-measure win.
- **The harness enters gameplay via the real Start Demo button** (`changeScene()` — `difficultyLevel=0` + the animator-driven `LoadScene`), not a raw `LoadScene`. The raw call **forked startup**: the player came up at the menu-camera framing `(2241,…)` with a stale `cash=2` instead of the new-game spawn `(-60.6,-301.1)` / `cash=0` (the `2241` is a long-standing red-herring — a camera framing position, not a spawn). Fast boot (default) now replicates `changeScene`'s effects directly, byte-identical.
- **Present rate is presentation-only** — byte-identical across 50 / 2000 / `999999` / `-1` (`captureDeltaTime` pins physics to 1/50 regardless). The harness applies one rate to the whole run (`IGTAS_PRESENT_FPS`, default `-1` uncapped); an explicit `@frame_rate` overrides for slow-mo. One caveat: the menu animator (below).
- **The canonical start gate** — record/play/harness all *arm* a run, then gate the first captured/played frame to a later `FixedUpdate` once "lockstep stable" holds (`ServiceStartGate`/`LockstepStartReady`), regardless of whether arming happened in `Update` (live) or `FixedUpdate` (harness). So the start phase is a system property, not an accident of keypress timing — a recording means the same thing live and headless.

## What's THEORISED (open)

> `main.tas` position drift is resolved (lockstep from scene-load). The candidates below are **not currently observed** — they're the next things to check **if a future `.tas` (moving platforms, dash freeze) reintroduces drift.**

1. **Unlogged contributors** — we log `Velocity` but not `momentum` / `movingPlatformVelocity`; drift could live there. Log them to confirm.
2. **Input-event timestamp jitter** — `PlayFrame` queues with wall-clock `InputState.currentTime`; held axes came out identical (low suspicion), and now **ruled out**: a deterministic monotonic timestamp leaves the harness hash byte-identical (safe to adopt) but does **not** fix the real-device-input desync (catalogue #12), so the timestamp is not the lever — the real input hazard is device/scheme *arbitration*.
3. **Box2D float nondeterminism** — solver iteration / contact ordering; same-machine stable, the likely floor we may not get under.
4. **Other scene actors** — `Overworld` may hold independently-moving rigidbodies near spawn. Auditing/freezing them is higher-touch — last resort.

### The constant +1 record→playback offset (observed; cause assumed, NOT verified)

**Observed, reproducibly:** recorded-then-played-back, every logged event lands exactly **one frame higher** in playback than in the recording (e.g. first death rec f1829 → play f1830), **constant** (+1, non-accumulating) and **closing by the end** (`run_end` lands on the same frame, the route reproduces identically). So it's **cosmetic for determinism** — byte-reproducible, shifted by one frame label. Live playback and harness agree (both +1 vs the recording); this is *separate* from the harness-only respawn +1 below.

**Assumed cause (unverified):** the capture-vs-replay input-application asymmetry — capture reads `keyboard.isPressed` in the same `FixedUpdate` the game consumes it, but replay's `QueueStateEvent` isn't applied until the next `Update` (under `ProcessEventsInDynamicUpdate`), so played frame N is consumed on step N+1. This queue→next-Update latency is inherent to using the real input path (a touch-point cost we've declined to fork). **Possibly compounded** by a logging sample-point difference (capture logs after frame N's input reflects; `PlayFrame` logs `body.position` at FixedUpdate-top, before the just-queued input takes effect) — so part or all may be a logging artifact, not a real offset. **Which dominates is unverified.**

**Why left for now (flagged as likely to bite):** if pure queue latency it's irreducible without a fork; if logging-point it's cosmetic and fixable by aligning sample points. Verify by aligning the capture/playback logging sample-point and seeing whether the +1 vanishes — **before** any frame-exact savestate/splice work, which needs capture and replay to mean the identical thing. (The [dual-clock](behaviour-takeovers.md#frozen-time-under-lockstep--the-dual-clock-frame-model) model sharpens *what "frame N" means* but isn't the fix here — this is a sample-point/queue asymmetry, not a frozen-time issue.)

### The respawn-timer +1 (RESOLVED via behaviour-takeover)

**Observed:** on a route that dies, the death-freeze begins on the **identical frame** live and harness, but the **respawn fires one physics frame later in the harness** — an *extra* +1 on top of the constant start +1, **harness-only** and **self-healing** (the run reconverges after the respawn; the route outcome is unchanged; the harness is byte-identical to itself run-to-run).

**Cause (verified — `IGTAS_DEATH_DIAG`):** death schedules `Invoke("deathRespawn", …)`, firing on the first frame where `Time.time >= death_t + delay`. The death window is `timeScale = 1` 50 Hz lockstep throughout (the "death freeze" is just `cutsceneMode == deathFreeze` zeroing velocity, with `FixedUpdate` ticking) — so `time` advances in clean exact steps in both environments. The ±1 is that the deadline lands a few microseconds from a frame edge, and live vs headless arrive at the death frame with a slightly different `death_t % frameTime` *phase* that straddles it (headless a hair under → misses → +1; live a hair over → catches). The phase differs because **live accumulates real wall-clock `Time.time` during menu navigation + the Start-Demo animation while the harness runs that pre-gameplay phase under lockstep** — so the two reach gameplay at a different absolute `Time.time`. This is absolute-`Time.time` sensitivity, **modulo the frame grid** — the *phase*. (It's why settle-frame-count changes don't shift it: whole-frame settle moves `death_t` by exact frame multiples, leaving the phase unchanged. Settle-invariance rules out *non-integer-frame* dependence, not absolute-`time` dependence.)

**Resolution: a behaviour-takeover, NOT a clock fix.** Two clock-side fixes were rejected — the dual-clock model (the death window has no frozen time) and pinning the `Time.time` phase (a hack fighting a read-only clock). The fix **severs the `Time.time` dependency**: block `Invoke("deathRespawn")` (`CancelInvoke` on the `isDead` edge), re-drive off our gameplay-frame counter at the derived count. Harness-proven (the post-respawn trajectory is byte-identical, shifted to the canonical frame). Every wall-clock `Invoke` in `Movement` shares this boundary-straddle mechanism — full catalogue: **[behaviour-takeovers.md](behaviour-takeovers.md)**.

**Still owed:** live cross-context validation (the canonical count matches live's *by derivation*, not yet frame-proven; batched with the `@rng_seed` live check). **Diagnostic:** `IGTAS_DEATH_DIAG` (latches on the `isDead` edge, logs the time-system each frame through the window; fires on both harness and a live `IGTAS_LIVE_STATE_LOG` run, so the two diff directly) — re-run after any `onDeath`/`deathRespawn` change.

## Full-game scope: economy, RNG, savestates

> Movement determinism is the *first zone's* worth. The full game is incremental — the economy is core from zone 1 (dash unlocks end of zone 2, so early-game is economy + movement, no dash). The economy must be deterministic too, and late-game iteration must not need replaying the whole hour. All in scope now.

### Nondeterminism source catalogue

| # | Source | Status / lever |
|---|--------|----------------|
| 1 | Input edge timing (framerate) | **Solved** — `captureDeltaTime` lockstep |
| 2 | Scene-load/settle seed | **Solved** — lockstep from scene-load; settle-invariant |
| 3 | Player physics integration | Deterministic **same-machine** (proven bit-identical) |
| 4 | **Global `UnityEngine.Random` (unseeded)** | Differs every launch. Movement-inert, **economy-critical**. **Solved** — `Random.InitState` wired both ways: harness `IGTAS_RNG_SEED` (scene-load) + the shipped `@rng_seed` directive (`BeginPlayback`). Live cross-validation still owed. |
| 5 | Box2D solver internals | Empirically stable same-machine/build; **cross-machine UNVERIFIED** — the likely floor |
| 6 | Wall-clock (`unscaledDeltaTime`) | dash `TimeStop` **solved** (behaviour-takeover, see (b)) + cosmetics; not economy. The render→physics leak |
| 6b | **Player-body interpolation (render→logic leak)** | **Solved** — `interpolation = None` during TAS (see "What's DETERMINED"). Confirmed instance of mechanism (b). |
| 7 | Object init order (RNG draw sequence) | Deterministic per build (assumed); verify with #4 |
| 8 | Invoke / coroutine timing | Scaled-time → deterministic under lockstep. The wall-clock `Invoke`/coroutine *boundary-straddle* class is owned by **behaviour-takeovers** (8 cases built + measured; live catalogue in `TakeoverRegistrations.cs` — [behaviour-takeovers.md](behaviour-takeovers.md)). |
| 9 | Save files (`Savedata/`, PlayerPrefs) | Must be **fixed/known** per run; see savestates |
| 10 | `UsingCheckpoints` respawn setting (PlayerPrefs) | Gameplay-affecting (changes where a death sends you); must be controlled |
| 11 | Cross-platform float/culture | Same-machine fine; cross-platform untested |
| 12 | Real-device input arbitration during playback | **Known desync**, fix owed: any physical key mid-playback (e.g. F4) diverges the played run via device/control-scheme arbitration (proven: 2nd-`Keyboard` injection changes the hash). The wall-clock timestamp is **ruled out** (deterministic timestamp doesn't fix it). Input-isolation fix owed. |
| 13 | **Animator + animation-event progression at fast-forward** | Don't fire reliably at high present rate. Boot **sidestepped** by fast-boot; menu/cutscene-driven runs **OPEN** — see "Menu animator under fast-forward" |
| 14 | **`async`/`await` game logic** (`zoneChanger`) | **Audited low-risk** — scene transitions are synchronous `LoadScene` (`SceneTransitionObject`/`pauseMenuScript`); zone changes are in-scene `SetActive` toggles; `zoneChanger.ChangeScene` is `async` but **awaitless** (runs synchronously to completion). The only `LoadSceneAsync` is crash-recovery (`EngineCrashWorkaround`). ⚠️ **Re-check on game update:** if a future build adds a real `await` to a gameplay path, the continuation resumes off the sync context at a wall-clock-dependent point → desync. (Unity async-op completion-frame timing is a known TAS hazard; it just doesn't bite *this* build.) |

### Economy RNG

The economy draws from the global unseeded `Random`: `clonesScript` (2× `Random.Range(0,101)`/interval — clone fastness/bigness + `nextCloneDue`; with zero upgrades the outcome collapses but the draws still fire, advancing the stream), and `courseScript`/`upgradeBox` `InvokeRepeating` phase offsets. **Seeding makes the full state reproducible** — measured on `main.tas`: 2× unseeded diverge on RNG/economy every launch; 2× same seed are byte-identical; a different seed is different-but-reproducible. (Movement `hash` is seed-independent — its `Random` is all audio.) **Wired both ways:** harness `IGTAS_RNG_SEED` (scene-load) and the live F8 `@rng_seed` directive (→ `Random.InitState` in `BeginPlayback`, gated to playback; the harness seed wins if both set) — [tas-inputs.md](tas-inputs.md). Caveat: `main.tas` exercises only the audio draws, not the clone/economy RNG (it never enters a zone) — the *stream* reproducibility is proven; *outcomes* need a zone-entering route (expected to reproduce, same seeded stream under lockstep). Owed: a live↔headless cross-validation. **Partial confirmation (measured):** a zone-boundary crossing IS a confirmed deterministic RNG consumer — the `zone-boundary` probe shows `Random.state` advancing exactly at the swap frame (a zone activation firing the activated objects' `Start` draws, e.g. `courseScript`'s `InvokeRepeating` phase), byte-identical ×2 and in the `fullhash`. This is *why* the `zoneChanger.endCooldown` behaviour-takeover matters: it pins the re-cross block/allow boundary, deterministically gating which area is active. The draw itself is a **one-time `courseScript.Start`** on an area's *first* activation (not per-swap — a swap-back to an already-active area draws nothing; measured via the recross probes), so the RNG-wobble risk is narrow but the area-state correctness is not. Recross is now validated both ways (block-within / allow-after, byte-identical ×2 — [behaviour-takeovers.md](behaviour-takeovers.md)). Still owed: the full clone-economy *outcome* (the probe is an idle fall, no clones generated).

**RNG capture/restore: available.** `Random.state` is public get/set (the 4 `rng0..rng3` ints) — `InitState(seed)` seeds from scratch, `Random.state = captured` restores arbitrary state (the RNG component of a savestate). The full savestate bundling it with player + economy is Tier-2 below.

#### Cosmetic↔gameplay RNG coupling — settled; tripwire-and-continue

There is **one** global `UnityEngine.Random` stream and the entire build's draws share it. Full decompile census (the drift source, mapped):

- **Gameplay draws: exactly two sites**, both in `clonesScript.Update` — `Random.Range(0,101)` for clone *fastness* (`i` → `nextCloneDue`, the clone rate) and *bigness* (`j`, which scales offscreen cash directly: `num6 = course.reward * (j*2+1)`). The offscreen branch is where currency actually accrues; both branches sit below the draws, so draw *count* is independent of the on/offscreen visual split. This is the only `UnityEngine.Random` consumer that touches position/velocity/economy. (`System.Random` appears only in `UniqueCodeGenerator`, an isolated UID-seeded instance; `Unity.Mathematics` hits are `math.*`, not RNG.)
- **Cosmetic draws: ~27 sites across 9 classes** — all `playSFX`/`Audio.PlayAudio` (per jump/dash/death/land/spring/box-hit), `FloatingNumberScript` (per reward), particle emits, and one-time light/anim seeds + `courseScript`/`upgradeBox` `InvokeRepeating` phase offsets (at object activation).

So two economy-critical draws are interleaved on the same seeded stream with twenty-seven cosmetic ones: the clone economy genuinely depends on the *count and order of cosmetic draws*.

**Severance (a separate gameplay RNG cursor) was considered and rejected** — it violates the north star. Cosmetic draws are deterministic functions of inputs + gameplay events (same jumps → same SFX draws), so for any boot state the real game produces exactly one interleaved sequence and the clone economy *is* a function of it. Separating the streams lands on clone rolls **no boot state of the unmodified game reaches** — it exits the game's behaviour space. This is stronger than the exceptions we *do* accept: seeding (`InitState`, which the game never calls) only *picks a starting point inside* that space; lockstep only picks *when* inputs land. Severance changes the relationship between draws — a behaviour fork the touch-point gate forbids. So we reproduce the game's full coupled stream rather than re-plumb it.

**The actual risk is narrow and forward-only:** a cosmetic draw becoming gated on something *context-dependent* (render culling, present rate, audio-device presence, wall-clock) so our headless tooling would execute a different draw count than live *on the same build*. Audited today, every draw class gates on a deterministic source — audio on gameplay events, `clonesScript.onScreen` on a *physics* trigger (`clonesLoD`, not render visibility despite the "LoD" name), seeds/phases at activation. No slop exists now.

**Load-bearing invariants holding this together (name them; don't rely on them silently):**
- Headless audio is **muted, not skipped** (`AudioListener.volume=0; pause=true`) — both control the audio DSP/output; neither halts the C# that does `clip = clips[Random.Range(...)]` / `pitch = 1f+Random.Range(...)` (plain expressions before a now-no-op `Play()`). Draws fire identically headless and live.
- **Player audio settings cannot perturb the stream** — the SFX control is a *volume slider* (`SettingsScript` → `PlayerPrefs "SFXvolume"`), applied to the source/mixer *after* the draw; every `playSFX`/`PlayAudio` call site is unguarded (no "disable SFX" toggle gating the *call*), and `Audio.PlayAudio` draws unconditionally once the id matches. Even a machine with no audio device still runs the draw (only `Play()` no-ops).
- All draws are in `Update`/`FixedUpdate`, which run 1:1 with physics under `captureDeltaTime` lockstep.

**Decision: tripwire-and-continue, no plumbing change.** `rng0..rng3` are in `fullhash` every frame, so any stream desync changes the hash and surfaces the instant two runs are diffed (e.g. live vs headless) — a *passive* detector, not a standing assertion. If a future build adds a context-dependent draw gate, that diff catches it; re-audit on any game update touching audio, particles, or zone-activation.

### Savestates & late-game iteration

Replaying an hour at ~767 fps headless is ~4 min/iteration — fine as an oracle, too slow for tight late-zone tweaking. Tiered:

- **Tier 0 — replay-prefix** (~767 fps) — always available, the oracle that validates Tiers 1–2.
- **Tier 1 — the game's own JSON saves** (low-touch). `courseNdata.txt` holds a full per-course economy snapshot; snapshot/restore anchors a zone *through the game's own load path*. The primary "start at zone N" lever — the harness reset / `BringToState` (part of the deferred Drive layer).
- **Tier 2 — frame-exact mid-zone** (higher-touch, unproven). The JSON doesn't capture the live rigidbody mid-course; needs `Movement` fields + `Rigidbody2D` (pos/vel) + `Random.state` capture/restore. Box2D internal contact state isn't exposed — the risk — but at a rest point it's likely reconstructible from pos+vel. Testable: capture → restore → continue → diff against the oracle.

**Alternative start-anchor — `placesToStart`** (debug lever): `Movement` reads `globalStats.placesToStartOverride` once at start and jumps to that course/area (`infMoney` also unlocks abilities + high currencies). Lower-touch than authoring a walk route; complementary to fixtures (fixtures pin the full economy, `placesToStart` just relocates). Not yet harness-wired — candidate if a zone-entering `.tas` proves fiddly. [systems.md](systems.md).

**Dependency order:** economy RNG (#4) lands *first* — a restored economy that re-randomises isn't reproducible, so savestates aren't trustworthy until the seed is proven. The checkpoint system is a death-respawn mechanic, *not* a savestate (#10).

## Current assessment

**A full economy route reproduces across recording / live / harness.** The up-to-clone route (`new_game_test_up_to_clone`) reproduces end-to-end in all three: identical start pose, four course completions, upgrades 1→4, both deaths, the clone purchase, identical ledger + `run_end` — reached without freezing actors or reaching into state (every fix is a lockstep/sync/entry-path lever).

Still not proven: economy *outcomes* with **real** clone RNG (the up-to-clone route's draws collapse to a fixed outcome at zero upgrades — a route that buys fastness/bigness exercises live `Random.Range`); **cross-machine** determinism (Box2D + float-culture, the likely floor); a live↔headless cross-check of the `@rng_seed` + behaviour-takeover work. `main.tas` doesn't exercise moving platforms (none in this build) or the dash freeze, so catalogue #2–#5 could reappear in a moving-platform route.

## Next experiments (ordered)

1. **Classify the constant +1 record→playback offset** — cosmetic (logging sample-point) vs real (queue latency) by aligning where capture vs playback sample. Benign now; flagged before frame-exact savestate/splice work. (The respawn +1 is resolved — only its live cross-check remains.)
2. **Prove economy outcomes with real RNG** — a route that buys clone-fastness/bigness so clones draw live `Random.Range`; byte-identical ×2 under a seed + matched to live.
3. **Live-validate the now-wired playback determinism** — one batched live F8 capture (seeded, real-time) diffed against the harness to confirm the `@rng_seed` *and* the death-respawn takeover reproduce live↔headless. [behaviour-takeovers.md](behaviour-takeovers.md).
4. **Savestate Tier 1** — confirm a mid-game fixture matches the replay-prefix oracle; capture richer states (`atomColliderData`, `treeData`).
5. **Savestate Tier 2 (if needed)** — `Movement` + `Rigidbody2D` + `Random.state` capture/restore; diff against the oracle.
6. Lower priority: audit `Overworld` for stray dynamic actors.

## Tooling

- **Event tracker** (deferred Drive-layer tool) — edge-triggered `.tas_events` rows on watched transitions (gates, purchases, springs, checkpoints, death/respawn, zone changes) + `run_start`/`run_end` pose markers, stamped with frame/pos/vel/ledger. Pure-read. Two uses: determinism (diff two timelines to find behavioural divergence, not float) and route comparison. (This is what exposed the harness starting at `(2241,…) cash 2` instead of the canonical pose.)
- **Live per-frame CSV** — `IGTAS_LIVE_STATE_LOG=1` makes a live F8 run emit the same CSV the harness logs, so live and headless diff frame-by-frame. (RNG columns differ — live is unseeded — diff on pos/vel.) This localised the interpolation drift.
- **`IGTAS_PHYS_DIAG`** — one-shot physics-config + body-vs-transform dump around the first jump; the re-confirmation tool after any update that might change the body's component settings (it *confirmed*, vs assumed, the interpolation leak).
- **Lockstep tripwire** (deferred Drive-layer tool) — the live guard on the 1:1 lockstep claim. Counts FixedUpdates per frame and asserts the schedule (1 normally, 0 while frozen) every Update during a TAS/harness run; logs a breach (with frame/time/phase-offset context) and a per-run held/breached summary. Catches float-timestep drift (if `captureDeltaTime`/`fixedDeltaTime` ever diverge) or an unmodelled transition — the mis-counted "first real frame" class — before it silently desyncs a long run. Pure-read, inert in normal play; always-on during runs (silent unless breached). `IGTAS_LOCKSTEP_TRIPWIRE=1` adds per-frame verbose logging (healthy frames + freeze transitions). **It only measures** — a breach is a bug to fix at source, never auto-corrected (cf. "sever, don't nudge the clock's phase").
- **`fresh` fixture** — a committed real new-game save (not an empty dir — an empty dir's defaults differ, e.g. `_respawnPoint` → `(2241,-292)` not `(-60.6,-301.1)`).

## Two non-determinism mechanisms (and what lockstep does to each)

"Without lockstep the game is non-deterministic" hides two *different* mechanisms:

**(a) Input-edge timing — a sampling-phase problem ("this physics frame or the next").** Presses are detected in `Update` (`WasPressedThisFrame`) and buffered to `FixedUpdate`; the buffer is cleared by second-based `Invoke` timers. When render is faster than physics, *which* physics step a press lands in depends on where the render frame falls vs the 50 Hz tick — change the framerate and the same press is consumed a frame earlier/later. Visible as tap-jump-height jitter (the jump-cut samples on whichever frame catches `Velocity.y` in its window). Coyote frames are NOT affected (they decrement in physics time). Rule: anything *counted in `FixedUpdate`* is framerate-independent; anything gated on *when an edge was sampled in `Update`* is not.

**(b) Dash `TimeStop` — a frozen-physics / live-render duration leak.** `timeScale=0` paces the freeze off `unscaledDeltaTime` (wall-clock, not halted by `timeScale=0`, and `captureDeltaTime` inert), so left to Unity the freeze spans a present-rate-dependent count. **`captureDeltaTime` does not reach this** — which is exactly why (b) needs its own ownership (the capture-and-drive behaviour-takeover, [behaviour-takeovers.md](behaviour-takeovers.md)) rather than riding lockstep like (a). The frozen frames are *real route frames* (input arms during them), so the count is load-bearing.

**Lockstep** kills (a) directly (the edge always falls on a fixed phase vs the tick) but not (b) (inert under `timeScale=0`). Catalogue rows #1 / #6 / #8 are these two mechanisms.

### Other wall-clock-window mechanics (same family as (b))

Time-based windows are deterministic under lockstep but cover a different frame count at a non-50 timeScale:
- **`recentlyJumped`** — `Invoke("endRecentJump", 0.03)` after jump/super-jump/spring suppresses ground re-detection ~1.5 frames (this build folds springs in: `hitSpring()` sets `recentlyJumped` like any jump). **Taken over** for the normal-jump + super-jump triggers (the spring trigger is still pending) — [behaviour-takeovers.md](behaviour-takeovers.md).
- **`Time.timeScale ← globalStats.baseGameSpeed`** at the dash-freeze exit — benign while `baseGameSpeed == 1` (normal play), but a non-1 Hard-mode speed would fight lockstep — confirm if Hard Mode (`difficultyLevel == 1`) enters scope. The only base-game time-system change in this build.

### Menu animator under fast-forward (OPEN HAZARD)

The Start Demo button loads the gameplay scene **via an animator** whose *animation event* calls `LoadScene`. Under a high/uncapped present rate this **doesn't fire reliably** — measured: the real-button boot hangs at `-1` (0/2 reached gameplay), works ≤2000, while a direct `LoadScene` is robust at `-1` (5/5). Mechanism unpinned (an animation event skipped when frames blast past, or a `WaitForEndOfFrame` racing the uncapped loop) but the dependency is clear: **progression riding an Animator + animation event is present-rate-fragile.** Fast boot (default) sidesteps it for *boot* (direct `LoadScene`, byte-identical). **Why flagged, not just worked around:** a whole-game TAS will need deterministic animator/animation-event runs (pause menu, scene/zone transitions, cutscenes), and we don't yet know whether the failure is only a *hang* or also a silent *off-by-N* (which would corrupt a proceeding run rather than stalling). Owed: (1) characterise — does the event fire *late* or *never*? instrument animation-event frame timing at several present rates; (2) the general fix — a safe capped rate during animator phases, or **take over** the animation-event transition (a frame-counted load — [behaviour-takeovers.md](behaviour-takeovers.md)). Add to the per-version re-verification.

### The original mod's abandoned `cooldownAfterDash`

The DEBUG `Dash Cooldown` HUD readout is **display-only** — it never feeds gameplay and is *not* tangled with dash determinism. The non-obvious part: the game holds two dash quantities in different units — `dashFramesRemaining` (active dash, a frame count) and `dashCooldown` (post-dash lockout, seconds — parked at `100f` *during* the dash as a sentinel, the real `0.2` set in `endDash`). The honest readout keeps the two separate; a single "cooldown" number conflating them is wrong.
