# Behaviour takeovers — replacing wall-clock game logic with our frame counter

> **The last-resort determinism tool, made first-class.** Lockstep ([determinism.md](determinism.md)) and RNG seeding own the clocks we *can* set. This doc owns the ones we can't: `Time.time` and `unscaledDeltaTime` have **no setter** (only `fixedDeltaTime` / `maximumDeltaTime` / `timeScale` / `captureDeltaTime` are settable). For game progression that fires off those clocks (Unity `Invoke`/`InvokeRepeating`, the dash-freeze coroutine) we don't chase the clock's phase — we **block the scheduled action and re-drive it off our own gameplay-frame counter**. Code: [BehaviourTakeovers.cs](../BehaviourTakeovers.cs) (framework) + [TakeoverRegistrations.cs](../TakeoverRegistrations.cs) (the per-case registrations + their VALIDATION boxes — the live WIP catalogue; its `NOT BUILT` section holds the backlog).

## The principle

"We own the clocks" is shorthand; the achievable invariant is weaker: **no clock we can't control is left driving progression.** Where a clock is unownable, sever the dependency at the point it drives progression and move that behaviour onto a clock we own — the gameplay-frame counter (`FixedUpdate` in normal time, `Update` while frozen — the [dual-clock model](#frozen-time-under-lockstep--the-dual-clock-frame-model)).

This **reaches into game behaviour** — the furthest thing from "let the game run" — so every takeover is a touch point ([CLAUDE.md](../CLAUDE.md)) and a **last resort**: use it only after confirming the clock genuinely can't be owned (lockstep/seed don't reach it) *and* the behaviour genuinely drives progression (→ "When not to take over"). Chasing an unownable clock's phase (e.g. nudging `Time.time` via a `captureDeltaTime` bridge) is a hack that fights the engine and is rejected.

## Frozen time under lockstep — the dual-clock frame model

This is the drive regime the freeze takeover runs on (the `Frozen` drive in the capability matrix below). Lockstep's premise is **we own every clock the game reads, so a frame is a fixed slice of *simulated* time at any present rate** — and a `timeScale=0` freeze is the one regime where that has a hole: `Time.deltaTime` is 0 and `unscaledDeltaTime` free-runs at the host render rate with `captureDeltaTime` inert, so anything pacing off it is wall-clock-dependent (non-deterministic, different headless vs live). `unscaledDeltaTime` has **no setter**, so we can't own this clock at all — the resolution is the takeover pattern itself: sever the dependency, re-drive off our frame counter.

**The dual-clock frame model:** a TAS "frame" maps to one tick of whichever loop is the live authority — `FixedUpdate` in normal play (Update slaved 1:1 by lockstep), **`Update` while frozen** (FixedUpdate not ticking ⇒ Update becomes the authority). The frame counter stays monotonic across the switch; one continuous frame list; no `.tas` format change. A freeze is just frames whose physics is frozen, advanced from `Update` for an **imposed** count, never one *measured* off the render rate. ([Plugin.cs](../Plugin.cs) `Update`: `if (Time.timeScale == 0f) TakeoverAdvanceFrozen()`.)

**Why the count is load-bearing, not cosmetic:** the dash freeze sets `timeScale=0` but **not** `GamePaused`/`menuOpen`, so `Movement.Update` ([Movement.cs:625](../artifacts/decompiled/Movement.cs#L625)) runs in full each frozen frame — arming input buffers from live input, running afterimages + raycasts. Real TAS-relevant gameplay happens during the freeze (input arms while physics is frozen), so each canonical freeze-frame must run `Update` and consume a route frame; dropping the freeze to 0 frames silently deletes that window. The freeze's own capture-and-drive mechanism, count derivation, and version-fragility are the dash-freeze case in [TakeoverRegistrations.cs](../TakeoverRegistrations.cs) (`RegisterFreezeTakeover`).

## The mechanism

Every takeover is **detect → block → drive N frames → complete → teardown**, inert in normal play (it acts only during a TAS run, so live-record / live-playback / harness are identical by construction).

- **Detect** — `Polled(predicate)` (rising edge of a state the trigger sets: `isDead`, `recentlyJumped`) *or* `Seam(Harmony hook)` (when we need the trigger's *dynamic data* — the freeze iterator at the `StartCoroutine` seam, the spring's `movementLock` at a `hitSpring` postfix).
- **Block** — `CancelInvoke(name)` for a named `Invoke`, or a `StartCoroutine` Harmony prefix returning `false` for a coroutine (opaque once launched — no cancel-by-name). Any detector pairs with any blocker.
- **Drive N frames** on our counter — N is the **canonical frame count** (→ "Frame-count calculation", the high-stakes step).
- **Complete** on the final frame — call the game's original method if it re-invokes cleanly (death respawn, via reflection), else replicate its effect (the freeze's `if (!menuOpen) timeScale = baseGameSpeed`, reading live values not a hardcoded `1f`).
- **Teardown** on a mid-takeover stop — hand state back to the game (re-`Invoke` the remaining time / restore `timeScale`) so normal play resumes. Distinct from an **Abort**, where the game's *own* logic superseded us (death cancels a spring; a buffer was consumed early) → drop the takeover with no completion and no hand-back.

The framework is a small wrapper + per-behaviour callbacks (`ComputeFrames` / `OnArm` / `OnFrame(isFinal)` / `Interrupt{Abort,ReArm}` / `Teardown`), modelled as **explicit state, not a C# iterator** — an iterator would re-introduce the launched-coroutine opacity the block primitive exists to defeat, and interrupt / re-arm / hand-back all need to grip an in-flight takeover. Dynamic data flows via `TakeoverContext`. Full callback contracts: the [BehaviourTakeovers.cs](../BehaviourTakeovers.cs) header.

**Lifecycle edge cases carry as much weight as the happy path** — and several are reasoned-safe but not yet frame-proven, so flag them rather than assume them. Both run-boundary directions matter: a run **stopped mid-takeover** leans on `Teardown` as the fallback to un-strand game state (un-freeze a held `timeScale=0`, honouring the `!menuOpen` guard so an open menu keeps its *own* pause — a bare `timeScale=1` would steal it); a run **started mid-behaviour** — armed while the game is already mid-freeze or mid-respawn — is held by the FixedUpdate start gate so it can't promote until the game's pre-existing timer resolves. Other `timeScale=0` owners (menu, death, cutscene) are left untouched. Enumerate these per behaviour: like the count rules, the exception set is **open**, and the fallback path deserves the same scrutiny as the main path.

### Capability matrix

| Behaviour | Detect | Block | Drive | OnArm | OnFrame per-frame | Dynamic data | Interrupt | Teardown |
|---|---|---|---|---|---|---|---|---|
| Freeze | Seam (prefix) | return-false | Frozen | `MoveNext` (timeScale=0) | route-advance | the iterator | 2nd TimeStop → resume first | restore timeScale (guarded) |
| Death | Polled (`isDead`↑) | CancelInvoke | Normal | — | — | — | `!isDead` → abort | hand back `Invoke` |
| Collider | Polled (wall-jump) | CancelInvoke | Normal | — | — | — | — | hand back |
| Spring | Seam (`hitSpring` postfix) | CancelInvoke | Normal | — | — | `movementLock` | death / dash-cancel → abort | hand back |
| Buffers | Seam (press edge) | CancelInvoke | Normal | — | — | which buffer + delay | consumed → abort; re-press → re-arm | hand back |
| `endRecentJump` | Polled (`recentlyJumped`↑) | CancelInvoke | Normal | — | — | — | already-false → abort | hand back |

Every column has ≥2 owners — the surface is the real design space, not single-case scaffolding. A new timer plugs in as detect + block + callbacks **without changing the wrapper**; the wrapper only grows for a genuinely new *drive regime*, *block kind*, or *detect kind*.

## Frame-count calculation — the high-stakes step

This is where false confidence about determinism is most dangerous. **There is no universal formula** — the count is a property of *how the game checks the timer*, and a wrong derivation is a silent ±1. Three things decide it:

- the **comparison operator** (`<` / `<=` / `>` / `>=`) and whether the counter **increments before or after** the check — e.g. a coroutine's `i <= dur` post-increment runs one frame longer than an `Invoke`'s `>= delay` at the *same* duration;
- **float robustness** — small-decimal delays aren't exact, so a count meant to be a whole number of frames can land a hair high and a naive `CeilToInt` over-rounds; subtract an ε (above the float error, below the frame granularity) at any exact boundary;
- the **sample point** — whether the effect is read before or after the firing frame's own physics (below).

**Each count is derived per case, inline at its call site in code, and never shared** ([TakeoverRegistrations.cs](../TakeoverRegistrations.cs)). A shared `ceilFrames()` helper would hide exactly the per-case differences that decide correctness (operator, sample point, rounding direction, interactions) and invite porting a formula without re-deriving it — independent per-case confidence beats DRY here. The doc carries the *reasoning*; the code owns the arithmetic. Always **verify empirically** (double-run headless hash-identity + one live-vs-headless parity), stressing every count against the standing failure modes — **tick-rate** (recompute at a different `PhysicsHz`; exact-multiple and phase-robust delays swap behaviour), **boundary rounding**, and **conflicts** (another timer or state change that cancels, reschedules, or short-circuits this one mid-count).

The three levers and these failure modes are a **starting checklist, not a closed set.** Derive each behaviour from its own checked code in depth — and expect the rule-set itself to grow as new cases surface things the current list doesn't model. Treat any "this looks like the same calculation as that one" as a prompt to re-derive, not to reuse.

The count itself is **not** a cross-machine risk (integer-valued, IEEE-deterministic for identical inputs); the *physics float accumulation* during those frames is the open one (the Box2D floor). All counts to date are one-machine-validated, and the death case's live-vs-headless equivalence is reasoned, not yet frame-proven.

### The count model — window + per-seam offset

The residue that isn't arithmetic is **where the effect is read relative to our fire** (a loop-order fact, not `ceil`-vs-`>`). Don't compute the count from the delay; **target the measured first-possible window and add the detector's fire-to-observe offset:** `count = fp_window + offset`.

- **`fp_window`** = the low edge of the native straddle = `floor(delay·Hz)` — the value a human reproduces (the native `±1` above it is pure float-rounding slop at the boundary). **Measure it** with the phase sweep; `floor` is the model, not a licence to skip the sweep.
- **`offset`** = frames between our fire and the observed window-end, with **four measured origins** — do *not* predict it, they coincide in value across cases and will fool you:
  - **POLL → +0** — the poll arms one frame *after* the trigger sets state; that late-arm absorbs the effect-read offset (death, zone-cooldown).
  - **physics-callback seam (`OnTrigger*`) → +1** — arms synchronously at the trigger (no arm-eat), but the observable is read one `FixedUpdate` later than our frame-top fire (the effect-read term) (spring-lock, spring-cooldown).
  - **FixedUpdate-method seam → +1** — *different cause:* arms *during* `Movement.FixedUpdate`, before our advance, so the arming frame is eaten (`window = count−1`) (wall-jump collider; normal + super-jump `endRecentJump`).
  - **Update-method seam → +1** — *different again:* the press is in `Update(N)` but the flag is first read in `FixedUpdate(N+1)` (`jumpBuffer`).

The offsets derive against the per-frame execution order: `Movement.FixedUpdate` → our takeover advance + detect → our state log → `Physics2D.Simulate` (fires `OnTrigger`/`OnCollision`) → `Update` → delayed-call phase (`Invoke` + coroutine resumes) → `LateUpdate`. The set of origins is **open** — the last several cases (incl. the super-jump) added none, but re-measure every new case; never extrapolate. Death respawn is the simplest read of all this: gated-a-frame-later (movement resumes next `FixedUpdate` off cleared `isDead`), so sample-point-insensitive (+0); the wall-jump collider is the opposite (physics reads `excludeLayers` on the firing frame → +1).

### Failure modes — each was a wrong value the tooling caught after reasoning failed

- **The phase sweep is the load-bearing tool, not reasoning.** Drive the *real* event from a fixture and sweep the phase offset (a deferred Drive-layer tool) — it exposes the native straddle headlessly, the only way to find the human first-possible. The spring's missing `+1` (a bare `ceil` gave a 14-frame lock, *below* the native floor of 15 — a value no human gets) was caught only this way.
- **Don't trust a proxy trigger** (an action-`call`): it fires at a different intra-frame point and shifts the count ~1 frame.
- **Don't match native-headless:** headless is the *late* phase of the straddle (fires +1 vs live); matching it bakes in the non-human value. (Worked example: the super-jump `endRecentJump` sat at a stable-but-late gap=2 with takeovers on until pinned to fp=1.)
- **Byte-identical across two harness runs proves determinism, NOT correctness** — a count can be deterministically wrong.
- **"Takeover present but no effect" can be plumbing before it's a count error** — confirm the seam's capture actually fires (a postfix dispatcher that returned after the first match once hid a second seam sharing a method).

## Catalogue — live in code

The per-case catalogue — each takeover's trigger, unownable clock, block, count derivation, status, residuals, and validation — is **WIP in the per-case VALIDATION boxes of [TakeoverRegistrations.cs](../TakeoverRegistrations.cs)** (each at its `Register…()` method), the live source of truth while cases are still being built and measured. It is deliberately *not* duplicated here: a table would drift against the code (the staleness this doc just shed). **Graduate it to a table in this doc once the catalogue is complete and the final cross-case re-validation pass is done** — and if a late case reopens the framework, freeze the doc again until it settles. The framework, [Scope](#scope--whats-in-whats-out), and method above are stable and current.

The exact counts live inline at each call site (the canonical home for the arithmetic — this doc carries the *why*, per [doc-principles](doc-principles.md)). The standing shared caveat on every measured count: it is validated against the **lockstep entry-phase** only; the non-lockstep human range is unmeasured.

## Scope — what's in, what's out

**The rule:** take over a wall-clock-paced mechanic **iff** it drives progression **and** its firing frame can differ across contexts (live/headless) or tick-rates. `unscaledDeltaTime` timers are the worst — non-deterministic *within* a context too. Scope is **not** Movement-only: any such actor qualifies (a spawner, a moving platform). **Phase-robustness is not an exemption** — "fine at 50 Hz on this machine" is tick-rate-fragile, not control, and skipping a case skips the investigation that might surface an unpredicted interaction (`endRecentJump` was the worked example: phase-robust-looking, still uncontrolled; the super-jump variant sat at a stable-but-late gap=2 riding the freeze drive's phase until pinned).

**Out of scope — two reasons, both "out of scope," never "seems safe now":**

- **The clock is ownable** — lockstep / RNG seeding already pins it (most `deltaTime`-accumulated timers: coyote frames, the clone interval, `compBoostTimeLeft`).
- **It doesn't drive progression** — cosmetic `Time.time` readers never touch position / velocity / economy / RNG: `CamShake`, light flicker, the menu camera, and `ObjectBobber`'s `Sin(Time.time)` bob (present in numbers, but every target is colliderless UI/text — not ride-able).

**Coverage — the demo scope is fully mapped.** A sweep of every scheduling / unownable-clock primitive (`Invoke` / `InvokeRepeating` / coroutine `WaitForSeconds*` / direct `Time.time` / `unscaledDeltaTime`) across all decompiled sources, cross-checked against a per-scene component census ([tools/assetparse/list_components.py](../tools/assetparse/list_components.py)), finds **no in-scope demo mechanic outside this doc**. Remaining work is *building/measuring* the known cases, not *finding* new ones. Re-run both on every game update.

**Exempt — checked, NOT takeovers (recorded so they aren't re-chased):**

- `courseScript.UpdateReward` (`InvokeRepeating`) — every reward-affecting purchase calls it synchronously, so the 5 s repeat only re-runs an idempotent display calc; its single Start-time `Random.Range` draw *is* the zone-swap RNG fan-out (deterministic, in the fullhash — see the zone-cooldown case).
- `tripBreaker` `StartMusic` / `activateBreakerLights` — area state is set synchronously; the timers are only music + lights.
- Cosmetic timers: `IE_Land` / `IE_Jump` squash (`Time.deltaTime`, owned under lockstep), `upgradeBox` sprite/text `InvokeRepeating`s, `RubbleDisplay`, camera / light / `DOTween` tweens, the playtime `timer`. (Watch `DOTween` if it ever drives gameplay.)
- **Render/culling callbacks are clean.** `OnBecameVisible/Invisible` exists on `upgradeBox` (gates a sprite swap only — the purchase is collider + currency), `InworldAbilityTracker` / `CullAnimatorComponent` / `RubbleDisplay` (cosmetic). No demo component drives gameplay off culling — which *would* be a headless-vs-live hazard (no rendering headless), so re-sweep this on update.
- **Saving** — the harness disables it (autosave / `manualSave`).

**Red herrings:** `endDash` & `respawn` appear only as `CancelInvoke` (defensive cancels, no scheduling `Invoke`) — not timers, don't chase them. `EngineCrashWorkaround.changeScene` is boot-only.

**Full-release-only (out of demo scope), per [full-decompiled-systems.md](full-decompiled-systems.md):** jiggle drops (`JiggleDropScript`) and zip movers (`PlatformMover`) are absent from every demo scene; comp boost is `[FULL]` (and clock-owned anyway). NB `JiggleDropScript` is the *other* `TimeStop` caller, with its own `timeStopDuration` (not `dashTimeStopTime`) — if it ever enters scope, the freeze count must read the **iterator's captured `duration`**, not the dash field.

**Wrapper regimes & uncovered ones:** the wrapper handles a one-shot `Invoke` (most cases) and a captured coroutine (the freeze). A recurring `InvokeRepeating` re-fire and a multi-phase coroutine + wall-clock wait would each need a *new drive regime* — **neither has a demo case** (the `InvokeRepeating`s are all exempt above; `PlatformMover` is `[FULL]`).

## Version fragility

Every takeover short-circuits the game's own scheduling, so an update can silently break it — a changed duration, a renamed method, a new `timeScale=0` writer, a changed check operator. The runtime tripwires (live-duration read, loud log on a resolve/cancel failure) **don't** catch a changed frame-count *semantic*, so on every update re-verify the catalogue against the new build: each trigger still fires, each duration unchanged or its count re-derived, each blocked method still named the same.
