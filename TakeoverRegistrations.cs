using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IGTAS
{
    // ============================================================================================
    // Behaviour-takeover REGISTRATIONS — the live per-case WIP catalogue.
    //
    // The stable framework lives in docs/behaviour-takeovers.md (read it first; this file does NOT
    // re-explain it): the detect/block/drive/complete mechanism, the count model (window + the four
    // offset origins — POLL +0 / physics-callback +1 effect-read / FixedUpdate-method +1 arm-eat /
    // Update-method +1), the methodology (real trigger + phase sweep, target the first-possible),
    // the failure-mode lessons, the scope/exempt set, and version-fragility.
    //
    // THIS FILE is the live source of truth for PER-CASE status while cases are still being built +
    // measured — each case's trigger/block/count/status/residuals in its own VALIDATION box at its
    // Register…() method. The count is derived INLINE at each call site (the canonical home for the
    // arithmetic; the doc carries the why) and is NEVER shared — it's a property of how THAT timer is
    // checked and when its effect is read. Built cases first; the not-yet-built backlog is the
    // "// ===== NOT BUILT" section near the bottom. Graduate this catalogue to a table in the doc once
    // it's complete + the final cross-case re-validation pass is done.
    //
    // ⚠ STILL PROVISIONAL: every ✓ is measured-correct against the cases explored so far (lockstep
    // entry-phase only — the non-lockstep human range is unmeasured), not yet final. A later case can
    // retroactively reframe an earlier ✓ (the count model gained a new offset-origin on roughly every
    // other early case) — that's the rule-set maturing, not a regression. Don't re-run the whole suite
    // per change (the sweeps pin each in isolation); DO a final sweep-everything pass once complete.
    //
    // Per-frame observation columns (in NEITHER hash) live in StateLog.cs (ObserveBoolFieldNames +
    // StateColumns; add one + a foreign resolver per new behaviour): excludelayers, recentlyjumped,
    // dash/jump/jumpcutbuffer, isdead, springcooldown, zonecooldown, currentzone.
    //
    // ───────────────────────────────────────────────────────────────────────────────────────────
    // STILL OWED  (the backlog of what's NOT yet built/measured)
    // ───────────────────────────────────────────────────────────────────────────────────────────
    //   • JUMPBUFFER DASH branch — the 0.2s expiry (cutsceneMode==dash at press, fp 10) IS handled by the
    //     jumpBuffer takeover's delay branch but UNSWEPT: a super-jump consumes the buffer, so observing the
    //     expiry needs an UNCONSUMED dash-jump (air dash + jump with no air-jump left so the buffer expires).
    //   • DASH + jumpCut buffer expiry · SPRING endRecentJump trigger — NOT built (see the NOT BUILT section
    //     + the endRecentJump VALIDATION box; the spring trigger is now unblocked by spring-cooldown).
    //   • HELD-SPRING mid-lock — spring-cooldown's re-trigger is validated as a BOUNCE (gaps ≫ lock); the
    //     continuous-overlap mid-lock re-fire (<16f, re-arming the spring-LOCK while active) needs a wall/
    //     ceiling-constrained or lateral held spring.
    //   • FRAMEWORK GAP — per-instance Takeovers. Each Takeover is a SINGLETON; N concurrent same-kind (two
    //     springs cooling within 7f; multiple zoneChangers) can't be tracked — current cases skip-if-busy + log
    //     and leave the 2nd native. Build a per-instance pool only when a route needs concurrent foreign timers.
    //   • FINAL cross-case re-validation sweep — once the catalogue is complete (the provisional note above).
    //   • Misc: "captured" LogInfo spams long runs (route via EventTracker .tas_events if noisy); each sweep
    //     re-runs the WHOLE route so keep probes SHORT (a short spike-death fixture would speed the death sweep);
    //     the settle env var is a load-bearing harness workaround — docs/determinism.md + docs/future-work.md.
    //
    // ───────────────────────────────────────────────────────────────────────────────────────────
    // PER-CASE STATUS  (index — each case's own VALIDATION box holds the detail)  ✓ measured · ⚠ partial · ☐ not built
    // ───────────────────────────────────────────────────────────────────────────────────────────
    //   ✓ dash-freeze         — Frozen-drive coroutine (floor+1; `<=`/post-inc, unlike the Invoke cases).
    //   ✓ death-respawn       — poll, ceil (30 @0.6s), +0; pins fp=30 ∈ {30,31}.
    //   ✓ spring-lock         — physics-callback seam (hitSpring), ceil+1=16; pins lock=15 ∈ {15,16}. +1 = effect-read.
    //   ✓ wall-jump collider  — FixedUpdate-method seam, ceil+1=2; pins window=1 ∈ {1,2}. +1 = arm-eat.
    //   ✓ endRecentJump       — FixedUpdate-method seam, ceil=2; pins window=1 ∈ {1,2}. NORMAL-jump + SUPER-JUMP
    //                           (CheckForSuperJump seam) both built+measured. ⚠ spring trigger NOT built (now unblocked).
    //   ✓ jumpBuffer          — Update-method seam, floor+1=7; pins window=6 ∈ {6,7}. ⚠ NON-dash only; dash branch (fp 10) unswept.
    //   ✓ spring-cooldown     — FOREIGN physics-callback seam (SpringScript.OnTriggerStay2D), floor+1=8; pins window=7 ∈ {7,8}.
    //                           Re-trigger cascade validated (bounce); ⚠ continuous-overlap mid-lock re-fire unmeasured.
    //   ✓ zone-cooldown       — FOREIGN poll (zoneChanger.coolingDown), ceil=25; pins window=25 ∈ {25,26}. Recross
    //                           VALIDATED both ways (block-within/allow-after, ×2). Swap RNG = 1-time Start draw on
    //                           first activation, not per-swap. ⚠ async-Task tripwire (re-check `await` on update).
    //   ☐ dash/jumpCut buffers · jumpBuffer DASH branch (0.2, fp10 — code handles it, UNSWEPT) · endRecentJump
    //     SPRING trigger — NOT built/unswept (counts are GUESSES; MEASURE).
    //   SHARED residual on every ✓: the sweeps are LOCKSTEP entry-phase only; a bare non-lockstep human range is unmeasured.
    //
    // ───────────────────────────────────────────────────────────────────────────────────────────
    // TEST ASSETS  (CURRENT — fixtures/specs/ + inputs/; the trusted-vs-contaminated split matters)
    // ───────────────────────────────────────────────────────────────────────────────────────────
    //   fixtures: spring-onspot (player FALLS onto a real horizontal spring — TRUSTED; the hit lands in
    //     playback at default settle now the settle-stick is fixed), zone-boundary (spawn high above the
    //     'zone 2 activator' @16677,7933 so the free-fall crosses it IN playback, ~f32), walljump-unlocked
    //     (real wall jump off the spawn-LEFT wall at x≈-455.5), dash-unlocked, fresh.
    //   inputs/: spring_real_probe.tas (REAL spring, single launch) · spring_held_probe.tas (400f idle — the
    //     up-launch arcs back, re-triggering 3× for the cascade) · zone_probe.tas (200f idle — fall through
    //     the boundary) · zone_recross_{blocked,allowed}.tas [.actions] (deathRespawn-driven re-entry within /
    //     after the cooldown window — the recross gate test, see zone-cooldown box) · walljump_probe.tas (walk
    //     into wall, jump, wall-slide, jump) · spring_probe.tas [.actions] (action-`call` PROXY — CONTAMINATED
    //     by +1; kept ONLY to demonstrate the proxy lie).
    //   Detection note: a real spring's velx during the lock is up.x ≈ 1.00000012 (transform.up float),
    //     NOT exactly 1 — match the lock with velx>0.5, not ==1 (that bug hid the lock once).
    //
    // Lifecycle wiring (called from Plugin.cs): TakeoverInit / TakeoverReset / TakeoverNormalTick /
    //   TakeoverAdvanceFrozen / TakeoverStopAll / TakeoverShutdown — methods at the bottom of the file.
    // ============================================================================================
    public partial class Plugin
    {
        // The dash-freeze delay is a [SerializeField] read live (dashTimeStopTime). The death delay
        // is a call-site literal in Movement.onDeath()'s Invoke("deathRespawn", 0.6f) — not a
        // reflectable field, so it is a documented constant. Both are version-fragility tripwires.
        private const float DeathRespawnSeconds = 0.6f;
        private const string DeathRespawnMethodName = "deathRespawn";

        private Takeover freezeTakeover;   // dash TimeStop (Frozen drive, coroutine seam)
        private Takeover deathTakeover;    // onDeath respawn (Normal drive, polled isDead edge)
        private Takeover springTakeover;   // non-cardinal spring lock (Normal drive, hitSpring method seam)
        private Takeover colliderTakeover; // wall-jump Ground-exclusion (Normal drive, getPlayerControlledInput seam)
        private Takeover endRecentJumpTakeover; // recentlyJumped clear (Normal drive, getPlayerControlledInput seam)
        private Takeover jumpBufferTakeover; // jumpBuffer expiry (Normal drive, Update seam; aborts if consumed)
        private Takeover springCooldownTakeover; // SpringScript re-trigger gate (Normal drive, FOREIGN OnTriggerStay2D seam)
        private Takeover zoneCooldownTakeover;   // zoneChanger re-cross gate (Normal drive, FOREIGN coolingDown poll)
        private MethodInfo takeoverDeathRespawnMethod;  // resolved Movement.deathRespawn() (private)
        private MethodInfo takeoverEndCooldownMethod;   // resolved SpringScript.endCooldown() (private; foreign — lazy from instance)
        private MethodInfo takeoverZoneEndCooldownMethod; // resolved zoneChanger.endCooldown() (private; foreign — lazy from instance)
        private FieldInfo takeoverZoneCoolingField;     // resolved zoneChanger.coolingDown (private bool; poll predicate)
        private UnityEngine.Object[] takeoverZoneInstances; // cached zoneChanger instances (re-found per run in TakeoverReset)
        private MethodInfo takeoverCancelSpringMethod;  // resolved Movement.cancelSpringCutscene() (private)
        private MethodInfo takeoverReEnableColliderMethod; // resolved Movement.reEnableNormalCollider() (private)
        private MethodInfo takeoverEndRecentJumpMethod; // resolved Movement.endRecentJump() (private)
        private MethodInfo takeoverCancelJumpBufferMethod; // resolved Movement.cancelJumpBuffer() (private)
        private FieldInfo takeoverJumpBufferField;      // resolved Movement.jumpBuffer (private bool; consume-abort check)
        private FieldInfo takeoverCutsceneModeField;    // resolved Movement.cutsceneMode (public enum)
        private bool takeoverHookInstalled;

        // ===== Registration (Awake) =====
        private void TakeoverInit()
        {
            RegisterFreezeTakeover();
            RegisterDeathTakeover();
            RegisterSpringTakeover();
            RegisterSpringCooldownTakeover();
            RegisterZoneCooldownTakeover();
            RegisterColliderTakeover();
            RegisterEndRecentJumpTakeover();
            RegisterJumpBufferTakeover();
            TakeoverInstallCoroutineHook();
            takeoverHookInstalled = takeoverHarmony != null;
            if (!takeoverHookInstalled)
                Logger.LogWarning("TAKEOVER: coroutine hook NOT installed — dash freezes will be non-deterministic this session.");
            // Patch any registered method seams (none today; armed once a deferred handler registers
            // one). After the coroutine hook so takeoverHarmony exists, after the Register…() calls so
            // the seam list is populated.
            TakeoverInstallMethodSeams();
        }

        // ===== DASH FREEZE — Frozen drive, coroutine seam + return-false block =====
        // Movement.TimeStop(dashTimeStopTime) StartCoroutine's IE_TimeStop:
        //   Time.timeScale = 0f; float i = 0; while (i <= dur) { i += unscaledDeltaTime; yield; }
        //   if (!menuOpen) Time.timeScale = baseGameSpeed;
        // unscaledDeltaTime has no setter and is inert under timeScale==0, so we capture the
        // coroutine at the StartCoroutine seam, step it ONCE (applying timeScale=0), then hold for
        // our own frame count and replicate the exit. The coroutine is never MoveNext'd again — a
        // further step would read the live wall clock and let it self-exit on its own terms.
        private void RegisterFreezeTakeover()
        {
            freezeTakeover = new Takeover(
                name: "dash-freeze",
                drive: TakeoverDrive.Frozen,
                onArm: ctx => { try { (ctx.Data as IEnumerator)?.MoveNext(); } catch (Exception e) { Logger.LogWarning($"FREEZE: MoveNext threw: {e.Message}"); FreezeResume(ctx); } },
                onFrame: (ctx, isFinal) =>
                {
                    // Advance one route frame per frozen frame (record captures it / replay injects
                    // it) — the buffer-arming window live play also runs (Movement.Update each frame).
                    if (isReplaying) PlayFrame();
                    else if (isRecording) CaptureFrame();
                    if (isFinal) FreezeResume(ctx); // final canonical frame: replicate the exit
                },
                interrupt: null,
                teardown: (ctx, _) => FreezeResume(ctx)); // run-stop mid-freeze: un-freeze (guarded)
            takeovers.Add(freezeTakeover);

            // The seam: match Movement's compiler-generated IE_TimeStop state machine by the stable
            // "g__IE_TimeStop" name fragment (a recompile bumps the DisplayClass index, not this).
            coroutineSeams.Add(new CoroutineSeam(
                matches: (mb, it) => mb.GetType().Name == "Movement"
                                     && it.GetType().Name.IndexOf("g__IE_TimeStop", StringComparison.Ordinal) >= 0,
                onCapture: (mb, it) =>
                {
                    // Re-entrancy: a second TimeStop while already driving one shouldn't happen
                    // (timeScale=0 freezes FixedUpdate, so no new dash fires mid-freeze) — but if it
                    // ever does, resume the first rather than orphan its iterator.
                    if (freezeTakeover.Active)
                    {
                        Logger.LogWarning("TAKEOVER freeze: second TimeStop while driving one — resuming the first.");
                        FreezeResume(freezeTakeover.Ctx);
                        freezeTakeover.CancelSilently();
                    }
                    float dur = TakeoverReadDuration(mb, "dashTimeStopTime", 0.05f);
                    // INLINE COUNT — coroutine `while (i <= dur) { i += dt; yield; }`, i post-
                    // incremented AFTER the `<=` check, so it runs while (k-1)*dt <= dur → the count
                    // is floor(dur*PhysicsHz) + 1. At 0.05*50 = 2.5 → floor(2.5)+1 = 3. Min 1.
                    // (NOT ceil: this `<=`/post-increment differs from the Invoke `>=` cases; the two
                    // coincide here only because 2.5 is non-integer — diverge if dur*Hz is integral.)
                    int frames = Mathf.Max(1, Mathf.FloorToInt(dur * PhysicsHz) + 1);
                    Logger.LogInfo($"TAKEOVER freeze: capturing IE_TimeStop (dur={dur}, frames={frames}, iter={it.GetType().Name}).");
                    freezeTakeover.Arm(frames, new TakeoverContext(mb, it)); // onArm MoveNext's -> timeScale=0 this frame
                }));
        }

        // Restore timeScale exactly as the coroutine's own exit does (guarded by !menuOpen so
        // cancelling while a menu holds the pause leaves it paused), reading the game's own source
        // values — a faithful replication of the exit, not a hardcoded 1f. Clears the takeover ctx.
        private void FreezeResume(TakeoverContext ctx)
        {
            try { if (!FreezeMenuOpen(ctx?.Instance)) Time.timeScale = FreezeBaseGameSpeed(); }
            catch (Exception e) { Logger.LogWarning($"FREEZE: resume failed, forcing timeScale=1: {e.Message}"); Time.timeScale = 1f; }
            if (ctx != null) ctx.Data = null;
        }

        private bool FreezeMenuOpen(MonoBehaviour movement)
        {
            try
            {
                MonoBehaviour mv = movement ?? movementComp;
                if (mv == null) return false;
                var pmField = AccessTools.Field(mv.GetType(), "pauseMenu");
                var pm = pmField?.GetValue(mv);
                var moField = pm != null ? AccessTools.Field(pm.GetType(), "menuOpen") : null;
                return moField?.GetValue(pm) is bool b && b;
            }
            catch { return false; }
        }

        private float FreezeBaseGameSpeed()
        {
            try
            {
                Type gs = GetTypeByName("globalStats");
                var f = gs != null ? AccessTools.Field(gs, "baseGameSpeed") : null;
                if (f?.GetValue(null) is float v) return v;
            }
            catch { }
            return 1f;
        }

        // ===== DEATH RESPAWN — Normal drive, polled isDead edge + CancelInvoke block =====
        // ┌─ VALIDATION STATUS ───────────────────────────────────────────────────────────────────┐
        // │ ✓ COUNT MEASURED-CORRECT. bare ceil (30 @0.6s) with a +0 sample-point, confirmed against   │
        // │   native by the real-death phase-sweep (up-to-clone route, real spike death +              │
        // │   phase sweep): native respawn ∈ {30,31} frames (first-possible 30; 31 ONLY at phase        │
        // │   EXACTLY 0), and this count PINS respawn at the native first-possible f1860 (gap 30) across │
        // │   every phase — frame-identical to native's non-straddled phases, never below the floor.    │
        // │   The +0 (NO spring-style +1) is correct BECAUSE death's POLL detector arms one frame after │
        // │   onDeath sets isDead: that arm-lag is the sample-point offset the spring's SYNCHRONOUS      │
        // │   postfix supplied with an explicit +1. Detector arm-timing sets the per-case term, not the │
        // │   timer. Residual: the sweep is LOCKSTEP entry-phase only; a non-lockstep human range is     │
        // │   unmeasured (same caveat as spring).                                                       │
        // └────────────────────────────────────────────────────────────────────────────────────────┘
        // Movement.onDeath() → Invoke("deathRespawn", 0.6f). Time.time has no setter and its
        // absolute phase differs live-vs-headless, so a 0.6s = 30.0-frame delay straddles a frame
        // boundary and fires ±1 apart. We cancel the game's Invoke on the isDead rising edge and
        // re-fire deathRespawn ourselves on our canonical frame. Death does NOT freeze time
        // (deathFreeze is only a cutsceneMode), so this drives in Normal regime.
        private void RegisterDeathTakeover()
        {
            deathTakeover = new Takeover(
                name: "death-respawn",
                drive: TakeoverDrive.Normal,
                onFrame: (ctx, isFinal) =>
                {
                    if (!isFinal || takeoverDeathRespawnMethod == null) return;
                    // Fire the game's own deathRespawn() on our canonical frame — same code path the
                    // Invoke would have run, just frame-scheduled.
                    try { takeoverDeathRespawnMethod.Invoke(ctx.Instance, null); }
                    catch (Exception e) { Logger.LogWarning($"TAKEOVER death: deathRespawn() threw: {e.Message}"); }
                },
                onArm: null,
                // If isDead goes false before our countdown completes (the game beat us — a cancel
                // ever failed and its wall-clock Invoke fired), abandon the countdown.
                interrupt: ctx => TakeoverIsDead(ctx.Instance) ? TakeoverInterrupt.None : TakeoverInterrupt.Abort,
                // Run-stop mid-death: hand the pending respawn back to the game's Invoke so the
                // player isn't stranded dead.
                teardown: (ctx, frames) => TakeoverHandBackInvoke(ctx.Instance, DeathRespawnMethodName, frames));
            takeovers.Add(deathTakeover);

            polledDetectors.Add(new PolledDetector(
                predicate: () => TakeoverIsDead(movementComp),
                onRisingEdge: () =>
                {
                    if (movementComp == null) return;
                    // Rising edge: onDeath() just scheduled Invoke("deathRespawn", 0.6f). Cancel it
                    // and take over on our frame counter.
                    TakeoverCancelInvoke(movementComp, DeathRespawnMethodName);
                    // INLINE COUNT — Invoke fires on the first frame where elapsed >= delay, i.e.
                    // N*dt >= delay → N = ceil(delay*PhysicsHz). Float 0.6*50 = 30.0000019, so
                    // subtract ε (1e-3 frames, far above the ~1e-6 float error, far below the
                    // 0.5-frame delay granularity) before the ceil so the boundary lands on 30, not
                    // 31. Sample-point term is +0 (NO spring-style +1) — MEASURED via the real-death
                    // phase sweep, not assumed: native respawn ∈ {30,31}, first-possible 30, and this
                    // count pins f1860 across all phases. The +0 holds because the POLL detector arms
                    // one frame after onDeath (its arm-lag IS the offset the spring's postfix adds as
                    // +1). See the VALIDATION STATUS box above. PER-CASE: do NOT copy +0 to a seam case.
                    int frames = Mathf.Max(1, Mathf.CeilToInt(DeathRespawnSeconds * PhysicsHz - 1e-3f));
                    deathTakeover.Arm(frames, new TakeoverContext(movementComp));
                }));
        }

        private bool TakeoverIsDead(MonoBehaviour movement)
        {
            try { return movement != null && isDeadField != null && (bool)isDeadField.GetValue(movement); }
            catch { return false; }
        }

        // ===== SPRING LOCK — Normal drive, hitSpring method seam + CancelInvoke block =====
        // ┌─ VALIDATION STATUS ───────────────────────────────────────────────────────────────────┐
        // │ Mechanism: VALIDATED — fires on a real hitSpring, captures movementLock, blocks + drives,│
        // │   byte-deterministic ×N, inert for non-spring runs (smoke 7/7).                          │
        // │ Count: MEASURED, lockstep-correct. Real spring (spring-onspot fixture, player falls onto  │
        // │   a level-1 horizontal spring) + phase sweep: native lock ∈ {15,16} (first-               │
        // │   possible 15); this count (ceil + 1 sample-point) pins the takeover at 15 across ALL      │
        // │   phases — in range, at the first-possible frame. The earlier clean-calc (no +1) gave 14,  │
        // │   BELOW the native floor = provably wrong; caught only by this real-trigger sweep, not by  │
        // │   reasoning or the contaminated action probe. RESIDUAL GAP: the sweep is LOCKSTEP (entry-  │
        // │   phase); a bare NON-lockstep human run's render/physics interleaving could in principle   │
        // │   widen the range — unmeasured (would need a non-lockstep probe or one live capture).      │
        // └────────────────────────────────────────────────────────────────────────────────────────┘
        // A non-cardinal spring runs Movement.hitSpring(upForce, strength, movementLock, up):
        //   cutsceneMode = spring;
        //   if (up == up || up == down) cutsceneMode = none;            // cardinal → instant, NO timer
        //   else Invoke("cancelSpringCutscene", movementLock);          // angled → wall-clock end
        // While cutsceneMode==spring the game locks Velocity.x every FixedUpdate (frame-based — we REUSE
        // that, untouched); the only wall-clock piece is WHEN cancelSpringCutscene flips it back to none.
        // movementLock is a SpringScript [SerializeField] passed as a hitSpring PARAMETER — not on
        // Movement, not in collision data — so we capture it at a hitSpring POSTFIX (the only place the
        // value is in hand), CancelInvoke the game's timer, and re-fire on our frame counter.
        private void RegisterSpringTakeover()
        {
            springTakeover = new Takeover(
                name: "spring-lock",
                drive: TakeoverDrive.Normal,
                onFrame: (ctx, isFinal) =>
                {
                    if (!isFinal || takeoverCancelSpringMethod == null) return;
                    // Fire the game's own cancelSpringCutscene() on our canonical frame (cutsceneMode→none).
                    try { takeoverCancelSpringMethod.Invoke(ctx.Instance, null); }
                    catch (Exception e) { Logger.LogWarning($"TAKEOVER spring: cancelSpringCutscene() threw: {e.Message}"); }
                },
                onArm: null,
                // The game ends the spring early itself in two ways, both of which leave cutsceneMode no
                // longer 'spring': death (onDeath CancelInvokes cancelSpringCutscene + sets deathFreeze)
                // and a buffered dash (cutsceneMode→none). Either supersedes us → abort (no completion).
                interrupt: ctx => TakeoverSpringActive(ctx.Instance) ? TakeoverInterrupt.None : TakeoverInterrupt.Abort,
                // Run-stop mid-lock: hand the pending cancel back so the player isn't stuck in the spring.
                teardown: (ctx, frames) => TakeoverHandBackInvoke(ctx.Instance, "cancelSpringCutscene", frames));
            takeovers.Add(springTakeover);

            methodSeams.Add(new MethodSeam(
                typeName: "Movement", methodName: "hitSpring",
                argTypes: new[] { typeof(float), typeof(float), typeof(float), typeof(Vector2) },
                when: SeamWhen.Postfix,
                onFire: (mb, args) =>
                {
                    // Cardinal springs (up == up/down) set cutsceneMode=none synchronously and schedule
                    // NOTHING — nothing to take over. Only angled springs Invoke cancelSpringCutscene.
                    var up = (Vector2)args[3];
                    if (up == Vector2.up || up == Vector2.down) return true;
                    float movementLock = (float)args[2];
                    TakeoverCancelInvoke(mb, "cancelSpringCutscene");
                    // INLINE COUNT — MEASURED against a real spring (respawn-on-spring fixture +
                    // phase sweep). Two terms:
                    //   ceil(movementLock*Hz − ε)  = frames until the native Invoke FIRES (15 @0.3); ε
                    //     handles a float delay a hair over an integer boundary.
                    //   + 1                        = the SAMPLE-POINT term. Unity processes the Invoke in
                    //     the delayed-call phase AFTER that frame's FixedUpdate, so cancelSpringCutscene's
                    //     cutsceneMode=none is read by the velocity switch one FixedUpdate LATER. Our
                    //     takeover fires at frame-TOP (before the switch), so without +1 it releases a
                    //     frame early — below the native floor (a lock length a human never gets).
                    // Real-spring sweep: native lock ∈ {15,16} (first-possible 15); this count pins it at
                    // 15 across all phases. NOT a buffer — it replicates the game's Update-after-physics
                    // structure. The +1 is PER-CASE (death/collider differ — derive each from its own read
                    // point; do NOT share this).
                    int frames = Mathf.Max(1, Mathf.CeilToInt(movementLock * PhysicsHz - 1e-3f) + 1);
                    springTakeover.Arm(frames, new TakeoverContext(mb, movementLock));
                    Logger.LogInfo($"TAKEOVER spring: captured hitSpring (movementLock={movementLock}, frames={frames}).");
                    return true; // postfix: return ignored
                }));
        }

        // cutsceneMode is a public enum field; compare by name so a reordered enum doesn't silently
        // shift an int. Lazily resolved (the Movement instance is stable within a run).
        private bool TakeoverSpringActive(MonoBehaviour movement)
        {
            try
            {
                if (movement == null) return false;
                if (takeoverCutsceneModeField == null)
                    takeoverCutsceneModeField = movement.GetType().GetField("cutsceneMode", BindingFlags.Public | BindingFlags.Instance);
                var v = takeoverCutsceneModeField?.GetValue(movement);
                return v != null && v.ToString() == "spring";
            }
            catch { return false; }
        }

        // ===== SPRING COOLDOWN (re-trigger gate) — Normal drive, FOREIGN SpringScript.OnTriggerStay2D seam =====
        // ┌─ VALIDATION STATUS ───────────────────────────────────────────────────────────────────┐
        // │ ✓ COUNT MEASURED-CORRECT (single-launch). Native springcooldown window ∈ {7,8} (spring-     │
        // │   onspot + phase sweep on the new springcooldown column; measured at settle 10         │
        // │   AND 50, identical): gap 8 ≤offset0.010, 7 ≥0.012 — so 0.15·50=7.5 DOES straddle {7,8}       │
        // │   despite being non-integer (the "non-int ⇒ robust" guess was WRONG; the sweep caught it).   │
        // │   fp=7. count=floor(7.5)+1=8 PINS the window at 7 across ALL phases (in range, first-poss-   │
        // │   ible). The +1 is the OBSERVATION-POINT term (count=7 measured window=6 < fp): our frame-   │
        // │   top fire reads a frame earlier than native's delayed-call Invoke. Byte-identical ×2;       │
        // │   INERT off the spring path (jump_probe fullhash unchanged).                                  │
        // │   ✓ SETTLE-STICK FIXED (was: a trigger firing during settle armed a takeover that never      │
        // │   advanced + had CancelInvoke'd the native timer → playback-start TakeoverReset dropped it → │
        // │   game state STUCK). Fix: TakeoverRunActive gates on isReplaying/isRecording, NOT harnessActive│
        // │   (true through settle) — settle now resolves on the game's own lockstep-deterministic timers,│
        // │   takeovers engage from playback-start. Removed the settle=10 fixture workaround. This was a  │
        // │   SHARED gap (every takeover incl. spring-LOCK); see BehaviourTakeovers.cs TakeoverRunActive.  │
        // │   RE-TRIGGER CASCADE validated (spring_held_probe, 400f idle: the up-launch arcs back onto   │
        // │   the spring 3×): both takeovers re-arm each cycle, every cooldown window pins at 7, byte-    │
        // │   identical ×2, no stuck/warn — resolves the header's "stuck on" (each cycle now completes).  │
        // │   ⚠ RESIDUALS: (1) those are BOUNCES (re-fires ~118f apart ≫ lock 16f), NOT the continuous-   │
        // │   overlap MID-LOCK re-fire (re-fire <16f, re-arming the spring-LOCK while still active) — the │
        // │   vertical-launch geometry can't hold the player in-trigger across cooldown; needs a wall/    │
        // │   ceiling-constrained or lateral held spring. Re-arm logic is reasoned-equivalent (the spring-│
        // │   LOCK singleton re-Arms on every hitSpring), not yet measured. (2) CONCURRENT springs within │
        // │   native (loud log); per-instance deferred. (3) settle→playback STRADDLE: a timer scheduled   │
        // │   in settle that fires in playback runs native (±1) — narrow, see the gate comment.           │
        // └────────────────────────────────────────────────────────────────────────────────────────┘
        // FIRST FOREIGN-OBJECT takeover (the cooldown bool + endCooldown live on SpringScript, not Movement).
        // SpringScript.OnTriggerStay2D: if (Player && !cooldown) { hitSpring(...); cooldown=true;
        //   Invoke("endCooldown",0.15f); }   endCooldown(): cooldown=false. The !cooldown gate makes a spring
        // fire ONCE PER ~7.5f cooldown CYCLE (NOT every overlapping frame). cooldown (7.5f) < spring-lock
        // movementLock (15f), so a HELD spring re-fires hitSpring MID-LOCK each cycle → re-arms the spring-lock
        // takeover; owning this re-trigger endpoint is what makes a held spring deterministic. Unconditional on
        // any hit (not gated by the up-direction cardinal check that only affects cancelSpringCutscene), so this
        // covers ALL springs, unlike the angled-only spring-lock. Detected by a FOREIGN postfix seam on
        // OnTriggerStay2D (gives the instance via __instance; a poll would need per-instance edge plumbing over a
        // zone-swap-dynamic instance set), guarded by IsInvoking("endCooldown") so it fires ONLY on a real hit.
        private void RegisterSpringCooldownTakeover()
        {
            springCooldownTakeover = new Takeover(
                name: "spring-cooldown",
                drive: TakeoverDrive.Normal,
                onFrame: (ctx, isFinal) =>
                {
                    if (!isFinal) return;
                    // Resolve SpringScript.endCooldown lazily off the instance (foreign type — can't resolve
                    // from movementComp at TakeoverReset like the Movement cases).
                    try
                    {
                        if (takeoverEndCooldownMethod == null && ctx.Instance != null)
                            takeoverEndCooldownMethod = ctx.Instance.GetType().GetMethod(
                                "endCooldown", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (takeoverEndCooldownMethod == null)
                            Logger.LogWarning("TAKEOVER spring-cooldown: could not resolve SpringScript.endCooldown() — re-verify against this build.");
                        else takeoverEndCooldownMethod.Invoke(ctx.Instance, null); // cooldown=false on our canonical frame
                    }
                    catch (Exception e) { Logger.LogWarning($"TAKEOVER spring-cooldown: endCooldown() threw: {e.Message}"); }
                },
                onArm: null,
                interrupt: null, // endCooldown is the ONLY clearer of cooldown and is idempotent; nothing supersedes it
                teardown: (ctx, frames) => TakeoverHandBackInvoke(ctx.Instance, "endCooldown", frames));
            takeovers.Add(springCooldownTakeover);

            methodSeams.Add(new MethodSeam(
                typeName: "SpringScript", methodName: "OnTriggerStay2D",
                argTypes: new[] { typeof(Collider2D) },
                when: SeamWhen.Postfix,
                onFire: (mb, args) =>
                {
                    // A spring scheduled endCooldown THIS call iff its !cooldown gate let a hit through.
                    // IsInvoking is false during our countdown (we cancelled it) and when the gate blocked a
                    // re-overlap, so this fires ONLY on a real hit frame. (Re-arms next cooldown cycle naturally.)
                    if (!mb.IsInvoking("endCooldown")) return true;
                    // MULTI-INSTANCE: the Takeover is a singleton. If it is already driving a DIFFERENT spring,
                    // do NOT clobber it (that would strand the first spring cooling forever — its Invoke is
                    // already cancelled). Leave THIS spring on its native Invoke (±1) instead; the common one-
                    // spring-at-a-time case still takes over fully. Concurrent springs within 7f need per-
                    // instance Takeovers — a real framework gap, deferred until a route needs it.
                    if (springCooldownTakeover.Active && !ReferenceEquals(springCooldownTakeover.Ctx?.Instance, mb))
                    {
                        Logger.LogWarning("TAKEOVER spring-cooldown: a 2nd spring fired mid-countdown — leaving it on its native Invoke (±1). Per-instance takeovers not built.");
                        return true;
                    }
                    TakeoverCancelInvoke(mb, "endCooldown");
                    // INLINE COUNT — MEASURED (spring-onspot + phase sweep on springcooldown;
                    // settle-independent since the settle-stick fix). Native window ∈ {7,8}, fp=floor(0.15·50)=7.
                    // count = fp + 1 = 8 PINS the window at 7 across all phases. The +1 is the OBSERVATION-POINT
                    // term: our endCooldown fires at frame-top (TakeoverNormalTick, BEFORE the state log) but
                    // native's Invoke fires in the delayed-call phase AFTER it, so the same logical fire reads one
                    // frame earlier in our observable — without +1 the window pins at 6 (< fp, MEASURED). NOTE the
                    // FORM: floor(·)+1, NOT the spring-lock's ceil(·−ε)+1 — those diverge for a non-integer delay
                    // (7.5: ceil+1=9 ≠ floor+1=8); the spring-lock's integer 15.0 hid the difference. PER-CASE.
                    int frames = Mathf.Max(1, Mathf.FloorToInt(0.15f * PhysicsHz + 1e-3f) + 1);
                    springCooldownTakeover.Arm(frames, new TakeoverContext(mb));
                    Logger.LogInfo($"TAKEOVER spring-cooldown: captured OnTriggerStay2D hit (frames={frames}).");
                    return true; // postfix: return ignored
                }));
        }

        // ===== ZONE COOLDOWN (re-cross gate) — Normal drive, FOREIGN zoneChanger.coolingDown POLL =====
        // ┌─ VALIDATION STATUS ───────────────────────────────────────────────────────────────────┐
        // │ ✓ COUNT MEASURED-CORRECT (single crossing). Native zonecooldown window ∈ {25,26} (zone-      │
        // │   boundary fixture: player falls through the 'zone 2 activator' trigger in PLAYBACK + IGTAS_  │
        // │   PHASE_OFFSET sweep on the new zonecooldown column): 26 ONLY at offset 0, 25 every other     │
        // │   phase — the exact-integer 0.5·50=25.0 straddle (death-shape {30,31}). fp=25. count=ceil(25− │
        // │   ε)=25 (POLL, +0 like death: the poll arms a frame after coolingDown is set, absorbing the   │
        // │   observation-point offset) PINS the window at 25 across all phases. Byte-identical ×2; INERT │
        // │   off the zone path (jump_probe fullhash unchanged).                                          │
        // │ ✓ SWAP RNG FAN-OUT — source IDENTIFIED, ONE-TIME not per-swap. The draw is courseScript.Start()│
        // │   firing when an area FIRST activates (its InvokeRepeating("UpdateReward", Random.Range(0,5),5)│
        // │   start-phase draw). Start runs ONCE per component lifetime, so it fires on the FIRST swap to a │
        // │   never-active area (zone2 @f32: rng0 shifts) but NOT on re-activation (the 2→1 swap-back @f139 │
        // │   draws NOTHING — rng0 identical across it; measured). So the recross-desync risk is NARROWER   │
        // │   than first feared: the only RNG-bearing event is the first activation, tied to the already-   │
        // │   pinned first swap; a recross toggles area-active state but adds no draw.                      │
        // │ ✓ RECROSS VALIDATED both ways (deathRespawn-driven re-entry, byte-identical ×2):                │
        // │   • BLOCKED within window — zone_recross_blocked.tas: cross @f32, then a within-window re-entry │
        // │     (respawnPoint→16677,8100 + deathRespawn @f41 → fall back through the trigger ~f45 < clear   │
        // │     f57) does NOT swap (currentzone STAYS 2). The gate's `if(coolingDown)return` holds.         │
        // │   • ALLOWED after window — zone_recross_allowed.tas: deathRespawn @f70 → re-cross @f139 (> f57) │
        // │     swaps 2→1 + re-arms cooldown. Confirms the trigger stays ACTIVE post-swap (recross is real).│
        // │ ⚠ RESIDUALS: (1) the recross re-entry is deathRespawn-DRIVEN (a controlled position reset, valid │
        // │   for GATE-logic+determinism — not a count, already pinned; no natural recross exists, the      │
        // │   boundary free-falls into void). (2) ASYNC TRIPWIRE: ChangeScene is async-but-synchronous      │
        // │   TODAY; an added await → unownable Task clock, NO regime. Re-check for `await` on every update.│
        // └────────────────────────────────────────────────────────────────────────────────────────┘
        // SECOND FOREIGN-OBJECT takeover. zoneChanger.OnTriggerEnter2D (async void → ChangeScene, synchronous
        // TODAY — no await — but a version tripwire to an unownable Task clock): if (coolingDown) return;
        // coolingDown=true; Invoke("endCooldown",0.5f); <swap area1<->area2 SetActive + Movement.currentZone>.
        // endCooldown(): coolingDown=false. The cooldown only BITES on a RECROSS within 0.5s (OnTriggerEnter,
        // not Stay). DETECTED BY A POLL on coolingDown (NOT a seam): 1 instance so no per-instance need; fp=25
        // is large so the poll's 1-frame arm-lag is absorbed (death-style); AND a poll sidesteps Harmony-
        // patching the async OnTriggerEnter2D entirely (the async state machine is a fragile patch target).
        private void RegisterZoneCooldownTakeover()
        {
            zoneCooldownTakeover = new Takeover(
                name: "zone-cooldown",
                drive: TakeoverDrive.Normal,
                onFrame: (ctx, isFinal) =>
                {
                    if (!isFinal) return;
                    try
                    {
                        if (takeoverZoneEndCooldownMethod == null && ctx.Instance != null)
                            takeoverZoneEndCooldownMethod = ctx.Instance.GetType().GetMethod(
                                "endCooldown", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (takeoverZoneEndCooldownMethod == null)
                            Logger.LogWarning("TAKEOVER zone-cooldown: could not resolve zoneChanger.endCooldown() — re-verify against this build.");
                        else takeoverZoneEndCooldownMethod.Invoke(ctx.Instance, null); // coolingDown=false on our canonical frame
                    }
                    catch (Exception e) { Logger.LogWarning($"TAKEOVER zone-cooldown: endCooldown() threw: {e.Message}"); }
                },
                onArm: null,
                interrupt: null, // endCooldown is the ONLY clearer of coolingDown and is idempotent
                teardown: (ctx, frames) => TakeoverHandBackInvoke(ctx.Instance, "endCooldown", frames));
            takeovers.Add(zoneCooldownTakeover);

            polledDetectors.Add(new PolledDetector(
                predicate: () => TakeoverZoneCoolingInstance() != null,
                onRisingEdge: () =>
                {
                    MonoBehaviour zc = TakeoverZoneCoolingInstance();
                    if (zc == null) return;
                    // Rising edge: a cross just set coolingDown + Invoke("endCooldown",0.5f). Cancel it, drive on
                    // our counter. (If two zoneChangers ever cool at once the OR poll catches only the first edge —
                    // a per-instance concern deferred; the demo has ONE zoneChanger.)
                    TakeoverCancelInvoke(zc, "endCooldown");
                    // INLINE COUNT — ⚠ HYPOTHESIS (fp=25 measured; pin pending). Exact-integer 0.5·50=25.0 →
                    // ceil(25−ε)=25. POLL detector (+0 like death: the poll arms one frame after coolingDown is
                    // set, absorbing the observation-point offset the spring-cooldown SEAM needed +1 for). So
                    // window=count=25=fp expected. RE-SWEEP takeovers-ON before trusting. PER-CASE.
                    int frames = Mathf.Max(1, Mathf.CeilToInt(0.5f * PhysicsHz - 1e-3f));
                    zoneCooldownTakeover.Arm(frames, new TakeoverContext(zc));
                    Logger.LogInfo($"TAKEOVER zone-cooldown: captured coolingDown edge (frames={frames}).");
                }));
        }

        // The (single, in the demo) zoneChanger currently cooling down, or null. Caches the instance list +
        // the coolingDown field; the list is re-found per run (TakeoverReset nulls it — instances change on
        // scene reload). LIMITATION: a zoneChanger in a not-yet-activated area wouldn't be in the cache.
        private MonoBehaviour TakeoverZoneCoolingInstance()
        {
            try
            {
                if (takeoverZoneInstances == null)
                {
                    Type zt = GetTypeByName("zoneChanger");
                    if (zt == null) return null;
                    if (takeoverZoneCoolingField == null)
                        takeoverZoneCoolingField = zt.GetField("coolingDown", BindingFlags.NonPublic | BindingFlags.Instance);
                    takeoverZoneInstances = UnityEngine.Object.FindObjectsOfType(zt);
                }
                if (takeoverZoneCoolingField == null) return null;
                for (int i = 0; i < takeoverZoneInstances.Length; i++)
                    if (takeoverZoneInstances[i] is MonoBehaviour mb && mb != null
                        && takeoverZoneCoolingField.GetValue(mb) is bool b && b)
                        return mb;
                return null;
            }
            catch { return null; }
        }

        // ===== WALL-JUMP COLLIDER — Normal drive, getPlayerControlledInput seam + CancelInvoke block =====
        // ┌─ VALIDATION STATUS ───────────────────────────────────────────────────────────────────┐
        // │ ✓ MECHANISM + COUNT MEASURED-CORRECT. Real wall jump (walljump-unlocked fixture, off the    │
        // │   spawn-left wall) + IGTAS_PHASE_OFFSET sweep on the excludelayers Ground bit (layer 7):     │
        // │   native exclusion window ∈ {1,2} frames (first-possible 1; 2 ONLY at phase exactly 0), and  │
        // │   count ceil(0.02·50 − ε)+1 = 2 PINS the window at 1 across all phases (frame-identical to   │
        // │   native's non-straddled phases). Byte-deterministic ×2; INERT for non-wall-jump runs (the  │
        // │   up-to-clone fullhash is unchanged with this always-on seam installed). The +1 is an ARM-   │
        // │   TIMING term (the FixedUpdate-seam same-frame eat below), NOT the spring's effect-read +1 — │
        // │   same value, different cause; derived independently, not copied. Residual: sweep is LOCKSTEP│
        // │   entry-phase only (same caveat as spring/death); a non-lockstep human range is unmeasured.  │
        // └────────────────────────────────────────────────────────────────────────────────────────┘
        // getPlayerControlledInput()'s wall-jump branch (Movement.cs:1436-1438):
        //   normalCollider.excludeLayers += Ground;     // SYNCHRONOUS — reused untouched
        //   Invoke("reEnableNormalCollider", 0.02f);     // wall-clock endpoint — OURS
        // reEnableNormalCollider() removes Ground (Movement.cs:1565). The Ground-exclusion lets the wall
        // jump clip the wall corner for a frame; the window is COLLISION-CRITICAL. 0.02s = exactly 1
        // frame, so it sits ON the boundary (maximally straddle-prone). Detected by a POSTFIX seam on
        // getPlayerControlledInput — a poll can't be used (the 1-frame Invoke would fire before a polled
        // edge saw it) — guarded by IsInvoking("reEnableNormalCollider"), the sole Invoke site, so it
        // fires ONLY on a wall-jump frame.
        private void RegisterColliderTakeover()
        {
            colliderTakeover = new Takeover(
                name: "walljump-collider",
                drive: TakeoverDrive.Normal,
                onFrame: (ctx, isFinal) =>
                {
                    if (!isFinal || takeoverReEnableColliderMethod == null) return;
                    // Fire the game's own reEnableNormalCollider() (excludeLayers -= Ground) on our
                    // canonical frame — same code path the Invoke would have run.
                    try { takeoverReEnableColliderMethod.Invoke(ctx.Instance, null); }
                    catch (Exception e) { Logger.LogWarning($"TAKEOVER collider: reEnableNormalCollider() threw: {e.Message}"); }
                },
                onArm: null,
                interrupt: null, // 1-frame window; nothing but reEnableNormalCollider (cancelled) removes Ground
                // Run-stop mid-window: hand the pending re-enable back so the collider isn't left clipping.
                teardown: (ctx, frames) => TakeoverHandBackInvoke(ctx.Instance, "reEnableNormalCollider", frames));
            takeovers.Add(colliderTakeover);

            methodSeams.Add(new MethodSeam(
                typeName: "Movement", methodName: "getPlayerControlledInput",
                argTypes: Type.EmptyTypes, // no-arg
                when: SeamWhen.Postfix,
                onFire: (mb, args) =>
                {
                    // A wall jump in THIS call is the only thing that scheduled reEnableNormalCollider
                    // (sole Invoke site). No pending Invoke ⇒ this getPlayerControlledInput didn't wall-jump.
                    if (!mb.IsInvoking("reEnableNormalCollider")) return true;
                    TakeoverCancelInvoke(mb, "reEnableNormalCollider");
                    // INLINE COUNT — MEASURED (real wall jump + phase sweep, see the VALIDATION box).
                    // 0.02*50 = 1.0 → ceil(1 − ε) = 1, +1 = 2. The +1 is an ARM-TIMING term (NOT the
                    // spring's effect-read term, despite the same value): getPlayerControlledInput runs
                    // inside Movement.FixedUpdate, BEFORE our TakeoverNormalTick advance, so a seam arming
                    // here is decremented the SAME frame — without +1 it fires on the wall-jump frame and
                    // the window collapses to 0. The sweep confirms +1 pins the window at the native
                    // first-possible (1). PER-CASE — this +1 ≠ the spring's +1; do not unify them.
                    int frames = Mathf.Max(1, Mathf.CeilToInt(0.02f * PhysicsHz - 1e-3f) + 1);
                    colliderTakeover.Arm(frames, new TakeoverContext(mb));
                    Logger.LogInfo($"TAKEOVER collider: captured wall-jump reEnableNormalCollider (frames={frames}).");
                    return true; // postfix: return ignored
                }));
        }

        // ===== endRecentJump — Normal drive, getPlayerControlledInput seam + CancelInvoke block =====
        // ┌─ VALIDATION STATUS ───────────────────────────────────────────────────────────────────┐
        // │ ✓ NORMAL-JUMP trigger MEASURED-CORRECT (ground jump_probe + IGTAS_PHASE_OFFSET sweep on   │
        // │   the recentlyjumped column): native window ∈ {1,2} (first-possible 1; 2 spans phases     │
        // │   ≤~0.011, 1 above — NOT phase-robust, the old "phase-robust at 50Hz" note was WRONG),    │
        // │   count ceil(0.03·50 − ε)=2 PINS the window at 1 across all phases.                         │
        // │ ✓ SUPER-JUMP (dash-jump) trigger MEASURED-CORRECT — its OWN seam on CheckForSuperJump        │
        // │   (Movement.cs:1189; called FixedUpdate @1048 AFTER getPlayerControlledInput, so the normal- │
        // │   jump seam misses it). dash_jumpbuffer.tas + dash-unlocked + phase sweep: native window ∈    │
        // │   {1,2}, fp=1; same FixedUpdate-seam count ceil(0.03·50−ε)=2 PINS it at 1 across all phases.  │
        // │   Byte-identical ×2; normal-jump window unchanged (1); smoke 7/7 (fullhash unchanged → inert  │
        // │   on non-super-jump runs). NB: takeovers-ON-without-this-seam sat at a stable gap=2 — the LATE │
        // │   phase, an uncontrolled coincidence of the freeze drive; pinning fp=1 is human-first-possible.│
        // │ ⚠ SPRING trigger (hitSpring:2077) STILL NOT BUILT — its old blocker (re-arms every overlapping │
        // │   OnTriggerStay2D frame) is now LIFTED by the spring-cooldown takeover, but the hitSpring-     │
        // │   postfix seam needs building + sweeping (measured native fp_window=1; physics-seam ⇒ likely 1).│
        // └────────────────────────────────────────────────────────────────────────────────────────┘
        // Jump / super-jump / spring set recentlyJumped=true and Invoke("endRecentJump",0.03) — it
        // suppresses ground re-detection (~1.5f) so a jump's first airborne frames aren't read as a
        // landing (Movement.cs:815 FixedUpdate + :2132 collision callback). endRecentJump() just sets
        // recentlyJumped=false (idempotent → no interrupt needed; a redundant fire is harmless, unlike
        // the collider's mask-subtract). 0.03·50=1.5 STRADDLES {1,2}. Detected by a getPlayerControlledInput
        // POSTFIX seam (a poll arms a frame late → min window 2, can't reach first-possible 1), guarded
        // by IsInvoking("endRecentJump"). Rides the collider's already-patched getPlayerControlledInput.
        private void RegisterEndRecentJumpTakeover()
        {
            endRecentJumpTakeover = new Takeover(
                name: "endRecentJump",
                drive: TakeoverDrive.Normal,
                onFrame: (ctx, isFinal) =>
                {
                    if (!isFinal || takeoverEndRecentJumpMethod == null) return;
                    try { takeoverEndRecentJumpMethod.Invoke(ctx.Instance, null); }
                    catch (Exception e) { Logger.LogWarning($"TAKEOVER endRecentJump: endRecentJump() threw: {e.Message}"); }
                },
                onArm: null,
                interrupt: null, // recentlyJumped=false is idempotent; an early game-side clear needs no abort
                teardown: (ctx, frames) => TakeoverHandBackInvoke(ctx.Instance, "endRecentJump", frames));
            takeovers.Add(endRecentJumpTakeover);

            methodSeams.Add(new MethodSeam(
                typeName: "Movement", methodName: "getPlayerControlledInput",
                argTypes: Type.EmptyTypes, // no-arg; rides the collider's patch via the install dedup
                when: SeamWhen.Postfix,
                onFire: (mb, args) =>
                {
                    if (!mb.IsInvoking("endRecentJump")) return true;
                    TakeoverCancelInvoke(mb, "endRecentJump"); // NB: cancels ALL stacked endRecentJump (fine for one jump)
                    // INLINE COUNT — MEASURED (ground jump + phase sweep, see VALIDATION box). 0.03*50 = 1.5
                    // → ceil(1.5 − ε) = 2. NO explicit +1 (unlike the collider) even though this is the SAME
                    // FixedUpdate-seam term-B: the first-possible WINDOW (1) is BELOW ceil(delay·Hz)=2, so the
                    // term-B same-frame eat (window = count − 1) lands count=2 on window=1 with no extra. The
                    // count targets the MEASURED first-possible window, not a delay formula — re-derive per Hz.
                    int frames = Mathf.Max(1, Mathf.CeilToInt(0.03f * PhysicsHz - 1e-3f));
                    endRecentJumpTakeover.Arm(frames, new TakeoverContext(mb));
                    Logger.LogInfo($"TAKEOVER endRecentJump(jump): captured (frames={frames}).");
                    return true;
                }));

            // SUPER-JUMP (dash-jump) endRecentJump trigger — CheckForSuperJump (Movement.cs:1189) also does
            // Invoke("endRecentJump",0.03). It is called in FixedUpdate (Movement.cs:1048) AFTER
            // getPlayerControlledInput, so the seam above misses it — it needs its OWN seam on
            // CheckForSuperJump. Mutually exclusive with the normal-jump trigger in a frame (jumpBuffer is
            // consumed by exactly one), and the IsInvoking guard + our CancelInvoke keep the two seams from
            // double-arming the shared singleton: a normal jump cancels its own schedule before
            // CheckForSuperJump runs, so this seam sees no pending Invoke; a super-jump leaves
            // getPlayerControlledInput's seam seeing nothing (the schedule happens later, here).
            methodSeams.Add(new MethodSeam(
                typeName: "Movement", methodName: "CheckForSuperJump",
                argTypes: Type.EmptyTypes,
                when: SeamWhen.Postfix,
                onFire: (mb, args) =>
                {
                    if (!mb.IsInvoking("endRecentJump")) return true;
                    TakeoverCancelInvoke(mb, "endRecentJump");
                    // INLINE COUNT — MEASURED (dash_jumpbuffer + dash-unlocked + phase sweep, takeovers off):
                    // native super-jump endRecentJump window ∈ {1,2}, fp=1. SAME derivation as the normal-jump
                    // trigger (re-derived, not copied): CheckForSuperJump runs in FixedUpdate BEFORE our advance,
                    // so it is a FixedUpdate-method seam (term-B arm-eat, window = count−1); 0.03·50=1.5 →
                    // ceil(1.5−ε)=2, and the same-frame eat lands count=2 on window=1. (Takeovers-ON without this
                    // seam sat at a stable gap=2 — the LATE phase, an uncontrolled coincidence of the freeze
                    // drive's phase; pinning at fp=1 is the human-first-possible, per the methodology.)
                    int frames = Mathf.Max(1, Mathf.CeilToInt(0.03f * PhysicsHz - 1e-3f));
                    endRecentJumpTakeover.Arm(frames, new TakeoverContext(mb));
                    Logger.LogInfo($"TAKEOVER endRecentJump(super-jump): captured (frames={frames}).");
                    return true;
                }));

            // ⚠ SPRING endRecentJump trigger STILL NOT BUILT. The spring trigger (hitSpring:2077) fires
            // hitSpring via OnTriggerStay2D EVERY overlapping frame, gated natively by SpringScript.cooldown
            // (0.15s). The spring-cooldown takeover now owns that re-trigger gate, so the prior blocker
            // ("re-arms every overlapping frame, recentlyJumped stuck on") is lifted — but a hitSpring-postfix
            // endRecentJump seam still needs building + sweeping (measured native spring fp_window = 1,
            // physics-callback seam ⇒ count likely 1, like spring-lock — MEASURE, do not assume).
        }

        // ===== jumpBuffer expiry — Normal drive, Movement.Update seam + CancelInvoke block =====
        // ┌─ VALIDATION STATUS ───────────────────────────────────────────────────────────────────┐
        // │ ✓ NON-DASH jumpBuffer MEASURED-CORRECT (jumpbuffer_probe: ground jump then an unconsumed   │
        // │   mid-air press + IGTAS_PHASE_OFFSET sweep): the EXPIRY window ∈ {6,7} (fp 6; 7 only at     │
        // │   phase 0), count floor(0.12·50)=6 PINS it at 6. The CONSUMED press (ground jump, a 1-frame │
        // │   jumpbuffer blip cleared by the jump executing) is left ALONE — the takeover aborts when   │
        // │   jumpBuffer goes false. ⚠ The DASH branch (press during cutsceneMode==dash → 0.2s delay)   │
        // │   is NOT yet swept — its count (floor(0.2·50)=10) is unmeasured. dash/jumpCut buffers TODO.  │
        // └────────────────────────────────────────────────────────────────────────────────────────┘
        // Movement.Update (on jumpAction press): jumpBuffer=true; CancelInvoke; Invoke("cancelJumpBuffer",
        // 0.2 if cutsceneMode==dash else 0.12). The buffer is READ/consumed in FixedUpdate; the wall-clock
        // Invoke only fires if it's NEVER consumed (the leniency edge — a press with no jump available).
        // Detected by a Movement.Update POSTFIX seam (the schedule is in Update; under lockstep Update runs
        // AFTER our FixedUpdate advance, so this is a term-B-0 seam: window=count) guarded by
        // IsInvoking("cancelJumpBuffer"). Re-arms on every re-press (the game does CancelInvoke;Invoke).
        // ABORTS when jumpBuffer goes false (the game consumed it + cancelled its own Invoke).
        private void RegisterJumpBufferTakeover()
        {
            jumpBufferTakeover = new Takeover(
                name: "jumpBuffer",
                drive: TakeoverDrive.Normal,
                onFrame: (ctx, isFinal) =>
                {
                    if (!isFinal || takeoverCancelJumpBufferMethod == null) return;
                    try { takeoverCancelJumpBufferMethod.Invoke(ctx.Instance, null); }
                    catch (Exception e) { Logger.LogWarning($"TAKEOVER jumpBuffer: cancelJumpBuffer() threw: {e.Message}"); }
                },
                onArm: null,
                // The game consumes the buffer (a jump executed) → jumpBuffer=false + it CancelInvokes its
                // own timer. We're driving the EXPIRY, which now must NOT fire → abort (no completion).
                interrupt: ctx => TakeoverReadBool(ctx.Instance, takeoverJumpBufferField) ? TakeoverInterrupt.None : TakeoverInterrupt.Abort,
                teardown: (ctx, frames) => TakeoverHandBackInvoke(ctx.Instance, "cancelJumpBuffer", frames));
            takeovers.Add(jumpBufferTakeover);

            methodSeams.Add(new MethodSeam(
                typeName: "Movement", methodName: "Update",
                argTypes: Type.EmptyTypes,
                when: SeamWhen.Postfix,
                onFire: (mb, args) =>
                {
                    if (!mb.IsInvoking("cancelJumpBuffer")) return true;
                    TakeoverCancelInvoke(mb, "cancelJumpBuffer"); // re-arm on every re-press rides this same path
                    // INLINE COUNT — MEASURED non-dash (see VALIDATION box). fp_window = floor(0.12·50) = 6,
                    // count = fp + 1 = 7. The +1 is the UPDATE-seam offset (a THIRD distinct origin, measured
                    // not assumed: the press is in Update(N) but jumpBuffer is first LOGGED/read in
                    // FixedUpdate(N+1), while the takeover arms at N and advances from N+1 — so without +1 it
                    // fires a frame early, window=count−1=5 < fp). Delay branches on cutsceneMode==dash at
                    // press (0.2 vs 0.12) as the game does; the DASH branch (fp 10) is NOT yet swept.
                    float delay = TakeoverCutsceneModeIs(mb, "dash") ? 0.2f : 0.12f;
                    int frames = Mathf.Max(1, Mathf.FloorToInt(delay * PhysicsHz + 1e-3f) + 1);
                    jumpBufferTakeover.Arm(frames, new TakeoverContext(mb));
                    return true;
                }));
        }

        // Read a resolved private bool field defensively (interrupt consume-checks).
        private bool TakeoverReadBool(MonoBehaviour mb, FieldInfo field)
        {
            try { return mb != null && field?.GetValue(mb) is bool b && b; }
            catch { return false; }
        }

        // True if Movement.cutsceneMode == the named enum value (compared by name, reusing the lazily
        // resolved field). Used by the jumpBuffer delay branch.
        private bool TakeoverCutsceneModeIs(MonoBehaviour movement, string name)
        {
            try
            {
                if (movement == null) return false;
                if (takeoverCutsceneModeField == null)
                    takeoverCutsceneModeField = movement.GetType().GetField("cutsceneMode", BindingFlags.Public | BindingFlags.Instance);
                return takeoverCutsceneModeField?.GetValue(movement)?.ToString() == name;
            }
            catch { return false; }
        }

        // ===== Lifecycle wiring (called from Plugin.cs) =====

        // Run activation: reset detector edges + in-flight state, resolve reflection fresh (the
        // Movement instance can change between runs). Loud on a resolve miss (version-fragility).
        private void TakeoverReset()
        {
            TakeoverDetectReset();
            for (int i = 0; i < takeovers.Count; i++) takeovers[i].CancelSilently();

            // Foreign zoneChanger instances change on scene reload — re-find them next poll.
            takeoverZoneInstances = null;

            takeoverDeathRespawnMethod = movementComp?.GetType().GetMethod(
                DeathRespawnMethodName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (movementComp != null && takeoverDeathRespawnMethod == null)
                Logger.LogWarning($"TAKEOVER: could not resolve Movement.{DeathRespawnMethodName}() — death respawn falls back to the game's wall-clock Invoke (non-deterministic ±1). Re-verify against this build.");

            takeoverCancelSpringMethod = movementComp?.GetType().GetMethod(
                "cancelSpringCutscene", BindingFlags.NonPublic | BindingFlags.Instance);
            if (movementComp != null && takeoverCancelSpringMethod == null)
                Logger.LogWarning("TAKEOVER: could not resolve Movement.cancelSpringCutscene() — spring lock falls back to the game's wall-clock Invoke (non-deterministic ±1). Re-verify against this build.");

            takeoverReEnableColliderMethod = movementComp?.GetType().GetMethod(
                "reEnableNormalCollider", BindingFlags.NonPublic | BindingFlags.Instance);
            if (movementComp != null && takeoverReEnableColliderMethod == null)
                Logger.LogWarning("TAKEOVER: could not resolve Movement.reEnableNormalCollider() — wall-jump collider falls back to the game's wall-clock Invoke (non-deterministic ±1). Re-verify against this build.");

            takeoverEndRecentJumpMethod = movementComp?.GetType().GetMethod(
                "endRecentJump", BindingFlags.NonPublic | BindingFlags.Instance);
            if (movementComp != null && takeoverEndRecentJumpMethod == null)
                Logger.LogWarning("TAKEOVER: could not resolve Movement.endRecentJump() — recentlyJumped clear falls back to the game's wall-clock Invoke (non-deterministic ±1). Re-verify against this build.");

            takeoverCancelJumpBufferMethod = movementComp?.GetType().GetMethod(
                "cancelJumpBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
            takeoverJumpBufferField = movementComp?.GetType().GetField(
                "jumpBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
            if (movementComp != null && (takeoverCancelJumpBufferMethod == null || takeoverJumpBufferField == null))
                Logger.LogWarning("TAKEOVER: could not resolve Movement.cancelJumpBuffer()/jumpBuffer — jumpBuffer expiry falls back to the game's wall-clock Invoke (non-deterministic ±1). Re-verify against this build.");

            if (!takeoverHookInstalled)
                Logger.LogWarning("TAKEOVER HOOK NOT INSTALLED — dash freezes will be NON-DETERMINISTIC this run (wall-clock). Re-verify the StartCoroutine patch against this build.");
        }

        // Top of each played/recorded physics frame, before the state log. Advance existing Normal
        // takeovers FIRST, then detect new edges — so a takeover armed this frame is NOT decremented
        // this frame (matching the game's Invoke: scheduled now, first elapses next frame).
        private void TakeoverNormalTick()
        {
            TakeoverTick(TakeoverDrive.Normal);
            TakeoverPollDetect();
        }

        // Update while timeScale==0. A Frozen takeover (the dash freeze) drives the route forward
        // itself (its OnFrame calls PlayFrame/CaptureFrame); if none is active this is "someone
        // else's freeze" (e.g. a menu pause) and nothing advances, so an unmodelled pause never
        // drains the route.
        private void TakeoverAdvanceFrozen()
        {
            if (TakeoverAnyActive(TakeoverDrive.Frozen)) { TakeoverTick(TakeoverDrive.Frozen); return; }
        }

        private bool TakeoverAnyActive(TakeoverDrive regime)
        {
            for (int i = 0; i < takeovers.Count; i++)
                if (takeovers[i].Active && takeovers[i].Drive == regime) return true;
            return false;
        }

        // Every stop/crash path: hand in-flight takeovers back to the game, then a residual safety
        // restore — if time is still stopped and no menu holds it, un-freeze (covers a freeze the
        // game itself was running because the run started mid-freeze, with no captured coroutine).
        private void TakeoverStopAll()
        {
            TakeoverTeardownAll();
            if (Time.timeScale == 0f && !FreezeMenuOpen(movementComp))
                Time.timeScale = FreezeBaseGameSpeed();
        }

        private void TakeoverShutdown()
        {
            // If shut down mid-freeze, don't leave time stopped.
            if (freezeTakeover != null && freezeTakeover.Active) FreezeResume(freezeTakeover.Ctx);
            try { takeoverHarmony?.UnpatchSelf(); } catch { }
            takeoverHarmony = null;
        }

    }
}
