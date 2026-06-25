using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IGTAS
{
    // ============================================================================================
    // Behaviour-takeover FRAMEWORK — replaces a piece of the game's wall-clock-scheduled logic
    // (an Invoke / coroutine pacing off Time.time or unscaledDeltaTime, clocks we can't own) with
    // our gameplay-frame counter. Concept, the per-behaviour frame-count discipline, the lifecycle
    // edge cases: docs/behaviour-takeovers.md — the canonical home, read it first. The per-case
    // registrations + their VALIDATION boxes (the live WIP catalogue) are in TakeoverRegistrations.cs;
    // its `NOT BUILT` section holds the not-yet-built backlog.
    //
    // Orientation for the code below: Detect (Polled rising-edge | Coroutine Harmony Seam) × Block
    // (CancelInvoke | StartCoroutine-return-false) are orthogonal primitives. A takeover is an
    // explicit state machine, NOT a C# iterator (we must grip an in-flight one to interrupt / re-arm
    // / hand it back — an iterator re-introduces the launched-coroutine opacity the block exists to
    // defeat), driven by per-behaviour callbacks: ComputeFrames (inline, never shared) / OnArm /
    // OnFrame(isFinal) / Interrupt{Abort,ReArm} / Teardown. Always installed, inert in normal play
    // (gated on TakeoverRunActive).
    // ============================================================================================
    public partial class Plugin
    {
        // The clock that advances a takeover's countdown (dual-clock model): Normal = the played/
        // recorded FixedUpdate tick (timeScale=1); Frozen = Update, while FixedUpdate is halted.
        private enum TakeoverDrive { Normal, Frozen }

        // Per-frame interrupt decision. Abort = the game superseded us (drop, no completion, no
        // hand-back); ReArm = restart the countdown (e.g. a buffer re-press).
        private readonly struct TakeoverInterrupt
        {
            public enum K { None, Abort, ReArm }
            public readonly K Kind;
            public readonly int Frames;
            private TakeoverInterrupt(K kind, int frames) { Kind = kind; Frames = frames; }
            public static readonly TakeoverInterrupt None = new(K.None, 0);
            public static readonly TakeoverInterrupt Abort = new(K.Abort, 0);
            public static TakeoverInterrupt ReArm(int frames) => new(K.ReArm, frames);
        }

        // The game actor + its seam-captured payload (the freeze iterator, a spring's movementLock),
        // re-read every OnFrame so a handler can re-sample live state.
        private sealed class TakeoverContext
        {
            public readonly MonoBehaviour Instance;
            public object Data;
            public TakeoverContext(MonoBehaviour instance, object data = null)
            {
                Instance = instance; Data = data;
            }
        }

        // An explicit owned-clock state machine: the callbacks + a countdown. The count is supplied
        // per-Arm, computed inline by the caller (never here — see the header for why no shared helper).
        private sealed class Takeover
        {
            public readonly string Name;
            public readonly TakeoverDrive Drive;
            private readonly Action<TakeoverContext> onArm;
            private readonly Action<TakeoverContext, bool> onFrame;
            private readonly Func<TakeoverContext, TakeoverInterrupt> interrupt;
            private readonly Action<TakeoverContext, int> teardown;

            public int FramesRemaining { get; private set; }
            public bool Active => FramesRemaining > 0;
            public TakeoverContext Ctx { get; private set; }

            public Takeover(string name, TakeoverDrive drive,
                Action<TakeoverContext, bool> onFrame,
                Action<TakeoverContext> onArm = null,
                Func<TakeoverContext, TakeoverInterrupt> interrupt = null,
                Action<TakeoverContext, int> teardown = null)
            {
                Name = name; Drive = drive;
                this.onFrame = onFrame; this.onArm = onArm;
                this.interrupt = interrupt; this.teardown = teardown;
            }

            // onArm fires synchronously here — the freeze's timeScale=0 must land on the arming frame.
            public void Arm(int frames, TakeoverContext ctx)
            {
                FramesRemaining = Mathf.Max(1, frames);
                Ctx = ctx;
                onArm?.Invoke(ctx);
            }

            // Returns true on the completing frame. Interrupt is checked before the frame is consumed.
            public bool Advance()
            {
                if (FramesRemaining <= 0) return false;
                if (interrupt != null)
                {
                    var d = interrupt(Ctx);
                    if (d.Kind == TakeoverInterrupt.K.Abort) { FramesRemaining = 0; return false; }
                    if (d.Kind == TakeoverInterrupt.K.ReArm) FramesRemaining = Mathf.Max(1, d.Frames);
                }
                FramesRemaining--;
                bool isFinal = FramesRemaining == 0;
                onFrame?.Invoke(Ctx, isFinal);
                return isFinal;
            }

            // Run-stop: hand in-flight state back to the game (vs CancelSilently, which just drops it).
            public void Teardown()
            {
                if (FramesRemaining <= 0) return;
                teardown?.Invoke(Ctx, FramesRemaining);
                FramesRemaining = 0;
            }

            // Drop the countdown without completing or handing back.
            public void CancelSilently() => FramesRemaining = 0;
        }

        private readonly List<Takeover> takeovers = new();

        // Advance every active takeover on the given clock by one frame.
        private void TakeoverTick(TakeoverDrive regime)
        {
            for (int i = 0; i < takeovers.Count; i++)
            {
                var t = takeovers[i];
                if (t.Active && t.Drive == regime) t.Advance();
            }
        }

        // Hand every in-flight takeover back to the game (run-stop).
        private void TakeoverTeardownAll()
        {
            for (int i = 0; i < takeovers.Count; i++)
                if (takeovers[i].Active) takeovers[i].Teardown();
        }

        // ===== Detection primitive 1: POLLED rising edge (no Harmony) =====
        // Watch a state the trigger sets (isDead, recentlyJumped, a wall-jump branch) at Normal
        // frame-top; on a rising edge run onRisingEdge (which blocks via CancelInvoke, computes the
        // count INLINE, and Arms its takeover). Each behaviour owns its own predicate + edge action.
        private sealed class PolledDetector
        {
            public readonly Func<bool> Predicate;
            public readonly Action OnRisingEdge;
            public bool Prev;
            public PolledDetector(Func<bool> predicate, Action onRisingEdge)
            {
                Predicate = predicate; OnRisingEdge = onRisingEdge;
            }
        }

        private readonly List<PolledDetector> polledDetectors = new();

        // Called at Normal frame-top AFTER advancing existing takeovers, so an edge armed this frame
        // isn't decremented this frame.
        private void TakeoverPollDetect()
        {
            if (!TakeoverRunActive) return; // not a TAS run: no detection
            for (int i = 0; i < polledDetectors.Count; i++)
            {
                var d = polledDetectors[i];
                bool now;
                try { now = d.Predicate(); } catch { continue; }
                if (now && !d.Prev) { try { d.OnRisingEdge(); } catch (Exception e) { Logger.LogWarning($"TAKEOVER detect edge threw: {e.Message}"); } }
                d.Prev = now;
            }
        }

        // Reset at run activation so a stale "was already true" doesn't swallow the first real edge.
        private void TakeoverDetectReset()
        {
            for (int i = 0; i < polledDetectors.Count; i++) polledDetectors[i].Prev = false;
        }

        // ===== Block primitive A: CancelInvoke (named Invoke timer) =====
        // IsInvoking still true after the cancel = a renamed method (version-fragility tripwire); the
        // game's wall-clock ±1 would still fire that time.
        private void TakeoverCancelInvoke(MonoBehaviour instance, string method)
        {
            instance.CancelInvoke(method);
            if (instance.IsInvoking(method))
                Logger.LogWarning($"TAKEOVER: CancelInvoke({method}) did not clear the pending Invoke — " +
                                  $"the game's wall-clock ±1 will fire this time. Re-verify the method name against this build.");
        }

        // Teardown for named-Invoke cases: re-schedule the game's own Invoke for the frames not yet counted down.
        private void TakeoverHandBackInvoke(MonoBehaviour instance, string method, int framesRemaining)
        {
            if (instance == null || framesRemaining <= 0 || instance.IsInvoking(method)) return;
            try { instance.Invoke(method, framesRemaining * (1f / PhysicsHz)); } catch { }
        }

        // ===== Block primitive B: coroutine (Harmony prefix on StartCoroutine) =====
        // A launched coroutine is opaque (no cancel-by-name) — the only grip is the StartCoroutine call.
        // On a match the prefix captures the iterator + arms a takeover, then returns false so Unity
        // never schedules it on the wall-clock loop.
        private sealed class CoroutineSeam
        {
            private readonly Func<MonoBehaviour, IEnumerator, bool> matches;
            private readonly Action<MonoBehaviour, IEnumerator> onCapture;
            public CoroutineSeam(Func<MonoBehaviour, IEnumerator, bool> matches,
                                 Action<MonoBehaviour, IEnumerator> onCapture)
            {
                this.matches = matches; this.onCapture = onCapture;
            }
            public bool Matches(MonoBehaviour mb, IEnumerator it) => matches(mb, it);
            public void Capture(MonoBehaviour mb, IEnumerator it) => onCapture(mb, it);
        }

        private readonly List<CoroutineSeam> coroutineSeams = new();
        private Harmony takeoverHarmony;

        // Always installed, inert in normal play (the prefix only acts on a matched coroutine while a run is active).
        private void TakeoverInstallCoroutineHook()
        {
            try
            {
                takeoverHarmony = new Harmony("igtas.behaviour.takeovers");
                var target = AccessTools.Method(typeof(MonoBehaviour), "StartCoroutine", new[] { typeof(IEnumerator) });
                if (target == null) { Logger.LogWarning("TAKEOVER: could not resolve MonoBehaviour.StartCoroutine."); return; }
                var prefix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(TakeoverStartCoroutinePrefix),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                takeoverHarmony.Patch(target, prefix: prefix);
            }
            catch (Exception e) { Logger.LogWarning($"TAKEOVER: coroutine hook install failed: {e.Message}"); }
        }

        // Returns false (block) for a matched coroutine while a run is active; true (pass-through) otherwise.
        private static bool TakeoverStartCoroutinePrefix(MonoBehaviour __instance, IEnumerator routine, ref Coroutine __result)
        {
            try
            {
                var self = PluginInstance;
                if (self == null || routine == null || __instance == null || !self.TakeoverRunActive) return true;
                for (int i = 0; i < self.coroutineSeams.Count; i++)
                {
                    var seam = self.coroutineSeams[i];
                    if (seam.Matches(__instance, routine))
                    {
                        seam.Capture(__instance, routine);
                        __result = null; // we are the scheduler now; no native handle
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                try { Logger.LogWarning($"TAKEOVER prefix error, passing through: {e.Message}"); } catch { }
                return true; // never break a coroutine on our account
            }
        }

        // ===== Detection primitive 3: METHOD seam (Harmony prefix/postfix on a named game method) =====
        // The poll and coroutine primitives don't cover a behaviour whose TRIGGER is a plain method
        // call we must catch at its EXACT firing moment (a 1-frame Invoke a lagged poll would miss),
        // or whose duration is a method PARAMETER no field exposes (a spring's movementLock). A method
        // seam Harmony-patches a named game method; on each call the shared dispatcher hands the live
        // instance + boxed args to the seam's OnFire, which blocks (CancelInvoke for the Invoke that
        // method scheduled, or — for a Prefix — return-false to kill the method itself) and Arms its
        // takeover. The orthogonal sibling of CoroutineSeam: same takeoverHarmony, same TakeoverRunActive
        // gate, inert in normal play (OnFire only acts during a run). One shared Prefix + one shared
        // Postfix dispatcher serve ALL seams, matched by __originalMethod — no per-method patch class.
        private enum SeamWhen { Prefix, Postfix }

        private sealed class MethodSeam
        {
            public readonly string TypeName;     // resolved by name at install (game asm loaded at Awake)
            public readonly string MethodName;
            public readonly Type[] ArgTypes;     // null = no-arg / first overload; set to disambiguate
            public readonly SeamWhen When;
            // OnFire(instance, boxedArgs): for a Prefix, returning false BLOCKS the original (method
            // killing); the return is IGNORED for a Postfix (the original has already run).
            public readonly Func<MonoBehaviour, object[], bool> OnFire;
            public MethodInfo Resolved;          // filled at install; null ⇒ seam inert (logged loud)
            public MethodSeam(string typeName, string methodName, Type[] argTypes, SeamWhen when,
                              Func<MonoBehaviour, object[], bool> onFire)
            {
                TypeName = typeName; MethodName = methodName; ArgTypes = argTypes; When = when; OnFire = onFire;
            }
        }

        private readonly List<MethodSeam> methodSeams = new();

        // Patch every registered method seam onto takeoverHarmony. Called from TakeoverInit AFTER the
        // coroutine hook (so takeoverHarmony exists) and AFTER all Register…() calls (so the list is
        // populated). A type/method that won't resolve is a version-fragility tripwire: logged loud,
        // its seam left inert rather than crashing the plugin.
        private void TakeoverInstallMethodSeams()
        {
            if (takeoverHarmony == null || methodSeams.Count == 0) return;
            var prefix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(TakeoverMethodPrefix),
                BindingFlags.NonPublic | BindingFlags.Static));
            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(TakeoverMethodPostfix),
                BindingFlags.NonPublic | BindingFlags.Static));

            // Multiple seams can target ONE method (e.g. getPlayerControlledInput drives BOTH the wall-jump
            // collider and endRecentJump; Update drives all three input buffers). The shared dispatcher
            // already runs every matching seam per call, so each (method, hook-kind) must be Patched only
            // ONCE — double-patching would double-dispatch (every onFire firing twice). Resolve all seams,
            // patch each unique (method, When) once; later seams on the same method just ride the dispatcher.
            var patched = new HashSet<string>();
            foreach (MethodSeam seam in methodSeams)
            {
                try
                {
                    Type t = GetTypeByName(seam.TypeName);
                    if (t == null)
                    {
                        Logger.LogWarning($"TAKEOVER: method-seam type '{seam.TypeName}' not found — {seam.MethodName} seam INERT. Re-verify against this build.");
                        continue;
                    }
                    seam.Resolved = seam.ArgTypes != null
                        ? AccessTools.Method(t, seam.MethodName, seam.ArgTypes)
                        : AccessTools.Method(t, seam.MethodName);
                    if (seam.Resolved == null)
                    {
                        Logger.LogWarning($"TAKEOVER: method-seam {seam.TypeName}.{seam.MethodName} not found — seam INERT. Re-verify against this build.");
                        continue;
                    }
                    string key = $"{seam.Resolved.Module.Name}:{seam.Resolved.MetadataToken}:{seam.When}";
                    if (patched.Add(key))
                    {
                        takeoverHarmony.Patch(seam.Resolved,
                            prefix: seam.When == SeamWhen.Prefix ? prefix : null,
                            postfix: seam.When == SeamWhen.Postfix ? postfix : null);
                        Logger.LogInfo($"TAKEOVER: method seam armed on {seam.TypeName}.{seam.MethodName} ({seam.When}).");
                    }
                    else
                    {
                        Logger.LogInfo($"TAKEOVER: method seam {seam.TypeName}.{seam.MethodName} ({seam.When}) rides an already-patched method (shared dispatcher).");
                    }
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"TAKEOVER: method-seam patch failed for {seam.TypeName}.{seam.MethodName} ({e.Message}); seam INERT.");
                }
            }
        }

        // Shared Prefix dispatcher: run EVERY prefix seam on the method that just fired (multiple seams
        // can share one method — getPlayerControlledInput drives collider + endRecentJump). Returning
        // false BLOCKS the original; if ANY seam blocks, the original is blocked. Pass-through on any
        // miss/throw so a seam can never break unrelated game code.
        private static bool TakeoverMethodPrefix(MonoBehaviour __instance, object[] __args, MethodBase __originalMethod)
        {
            bool runOriginal = true;
            try
            {
                var self = PluginInstance;
                if (self == null || __instance == null || !self.TakeoverRunActive) return true;
                for (int i = 0; i < self.methodSeams.Count; i++)
                {
                    var s = self.methodSeams[i];
                    if (s.When == SeamWhen.Prefix && s.Resolved != null && s.Resolved.Equals(__originalMethod))
                        if (!s.OnFire(__instance, __args)) runOriginal = false; // any block wins; still run the rest
                }
            }
            catch (Exception e) { try { Logger.LogWarning($"TAKEOVER method prefix error, passing through: {e.Message}"); } catch { } }
            return runOriginal;
        }

        // Shared Postfix dispatcher: the trigger method has run; run EVERY postfix seam on it (NOT just
        // the first — multiple seams share one method) so each reads its result/args and Arms. Return is
        // void — a postfix cannot block.
        private static void TakeoverMethodPostfix(MonoBehaviour __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                var self = PluginInstance;
                if (self == null || __instance == null || !self.TakeoverRunActive) return;
                for (int i = 0; i < self.methodSeams.Count; i++)
                {
                    var s = self.methodSeams[i];
                    if (s.When == SeamWhen.Postfix && s.Resolved != null && s.Resolved.Equals(__originalMethod))
                        s.OnFire(__instance, __args); // run ALL matching seams, no early return
                }
            }
            catch (Exception e) { try { Logger.LogWarning($"TAKEOVER method postfix error: {e.Message}"); } catch { } }
        }

        // ===== Shared gate + version-fragility tripwire =====

        // Gate on the ACTUAL RUN, identically for live and harness: playback = isReplaying, live record =
        // isRecording. This is the "no functional difference between harness and live" invariant in code — the
        // SAME signal turns takeovers on in both, so harness playback and live F8 playback behave identically.
        // NOT harnessActive: it is true from harness boot through SETTLE (the pre-playback phase where the harness
        // runs physics under lockstep to reach a reproducible start). Including it made the harness run takeovers
        // BEFORE the run began — something live NEVER does (live pre-F8 = normal play = takeovers inert, native
        // timers). That was a harness-vs-live DIVERGENCE, and it manifested as a stick: a takeover armed during
        // settle never advances (TakeoverNormalTick is playback-only) AND has already CancelInvoke'd the native
        // timer, so the playback-start TakeoverReset drops it → the timer fires NEVER and state STICKS (a settle
        // spring hit froze cutsceneMode=spring + cooldown=true all run). Gating on isReplaying makes the harness
        // pre-playback phase match live's pre-F8 normal play: takeovers inert, settle resolves on the game's own
        // (lockstep-deterministic) wall-clock timers, the run takes over from playback-start. Rate-independent
        // (verified: identical fullhash present=-1 vs 50 — lockstep pins physics to 1/50 regardless of present fps).
        // Consequence: an event triggered pre-playback whose timer STRADDLES into the run fires native (±1) — a
        // fundamental edge of "playback starting while a native timer is pending", present in BOTH harness (settle)
        // and live (a timer scheduled just before F8); narrow, and matches "let a pre-existing timer resolve".
        private bool TakeoverRunActive => isReplaying || isRecording;

        // Read a live [SerializeField] duration (scene values override decompile defaults). Loud on a
        // miss — a missing/renamed field means the behaviour shape changed (version-fragility tripwire).
        private float TakeoverReadDuration(MonoBehaviour instance, string field, float fallback)
        {
            try
            {
                var f = AccessTools.Field(instance.GetType(), field);
                if (f?.GetValue(instance) is float d && d > 0f) return d;
                Logger.LogWarning($"TAKEOVER: '{field}' missing/non-positive on {instance.GetType().Name} — " +
                                  $"behaviour shape may have CHANGED; falling back to {fallback}. Re-verify against this build.");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"TAKEOVER: reading '{field}' failed ({e.Message}); falling back to {fallback}. Re-verify.");
            }
            return fallback;
        }
    }
}
