using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace IGTAS
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public partial class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        private MonoBehaviour movementComp;
        private FieldInfo momentumField;
        private FieldInfo movingPlatformVelocityField;
        private FieldInfo velocityField;
        private FieldInfo bodyField;
        private FieldInfo onGroundField;
        private FieldInfo OnWallField;
        private FieldInfo isDeadField;        // Movement.isDead — death-takeover arming edge + CSV isdead column
        private FieldInfo dashCooldownField;
        private FieldInfo dashFramesField;
        private FieldInfo dashFramesRemainingField;
        private FieldInfo moveActionField;
        private FieldInfo jumpActionField;
        private FieldInfo dashActionField;
        private FieldInfo resetActionField;

        private MonoBehaviour pauseMenu;
        private FieldInfo settingsMenuOpenField;

        private GUIStyle topCenterStyle;
        private GUIStyle debugStyle;
        private GUIStyle fpsStyle;
        private GUIStyle sidebarStyle;
        private GUIStyle sidebarHighlightStyle;
        private GUIStyle sidebarDimStyle;

        // ===== REBIND UI STYLES =====
        private GUIStyle rebindTitleStyle;
        private GUIStyle rebindRowLabelStyle;
        private GUIStyle rebindButtonStyle;
        private GUIStyle rebindButtonActiveStyle;
        private GUIStyle rebindHintStyle;
        private bool rebindStylesInitialized = false;

        private float deltaTime;

        // ===== CONFIG KEYBINDS =====
        private ConfigEntry<KeyboardShortcut> keybindStartRecord;
        private ConfigEntry<KeyboardShortcut> keybindStopRecord;
        private ConfigEntry<KeyboardShortcut> keybindPlayback;
        private ConfigEntry<KeyboardShortcut> keybindToggleHitboxes;
        private ConfigEntry<KeyboardShortcut> keybindToggleNoclip;
        private ConfigEntry<KeyboardShortcut> keybindCycleAbilities;

        // Built dynamically from config — keys that should never be recorded as gameplay input
        private HashSet<Key> ignoredKeys = new();

        // ===== TAS CORE =====
        private List<FrameInputSnapshot> recordedFrames = new();
        private int replayIndex = 0;
        private bool isRecording = false;
        private bool isReplaying = false;
        // Canonical start boundary. Record (F6), live playback (F8) and the harness all *arm* a run —
        // set up lockstep, rebind, load — without immediately capturing/playing. The first frame is
        // gated (ServiceStartGate) until lockstep is confirmed stable, so it always lands on a clean,
        // fully-settled boundary regardless of which loop (Update or FixedUpdate) armed it — a property
        // of the system, not an accident of keypress timing, so a recording means the same thing on
        // live F8 and in the harness. Today readiness = one FixedUpdate elapsed under lockstep since
        // arming; a future pre-armed boot would satisfy it immediately (only LockstepStartReady
        // changes). See docs/determinism.md.
        private enum PendingStart { None, Record, Play }
        private PendingStart pendingStart = PendingStart.None;
        private int fixedUpdatesSinceArm;
        private string tasFolder;
        private string recordingsFolder;
        private string savedInputFile;
        // True when the loaded buffer was expanded from @commands (e.g. read_file).
        // Such a buffer is a flattened composition and must not overwrite its source.
        private bool loadedComposition;
        // Frames-per-second requested by @frame_rate in the loaded file, applied
        // during playback and restored on stop.
        private int? loadedFrameRate;
        // Mid-run @frame_rate changes (frame index -> fps) from the loaded file, applied as
        // playback crosses each frame. nextFrameRateIdx is the cursor into it (reset per run).
        // Presentation-only: changes Application.targetFrameRate, never captureDeltaTime.
        private List<(int frame, int fps)> loadedFrameRateChanges = new();
        private int nextFrameRateIdx;
        private bool frameRateOverridden;
        private int savedTargetFrameRate;
        private int savedVSyncCount;
        private float savedCaptureDeltaTime;
        // Present rate held during playback (the @frame_rate value, or PhysicsHz default).
        private int activePresentFps;

        // RNG seed requested by @rng_seed in the loaded file. Applied via Random.InitState
        // at playback start (BeginPlayback) so the economy/RNG stream reproduces from F8
        // onward, history-independently.
        private int? loadedRngSeed;

        private HashSet<Key> previouslyDown = new();
        private Keyboard virtualKeyboard;

        // ===== REBIND UI =====
        // Index of the keybind row currently awaiting a key press, or -1 if none.
        private int rebindingIndex = -1;

        // Ordered list of (label, ConfigEntry) pairs — drives both the UI and the ignored-key set.
        private (string label, ConfigEntry<KeyboardShortcut> entry)[] keybindDefs;

        private static readonly Dictionary<Key, string> keyLabels = new()
        {
            { Key.A,          "←  A"    },
            { Key.D,          "→  D"    },
            { Key.W,          "↑  W"    },
            { Key.S,          "↓  S"    },
            { Key.Space,      "⎵  Jump" },
            { Key.LeftShift,  "⇧  Dash" },
            { Key.Tab,        "⇄  SwapHud" },
            { Key.Escape,     "☰  Menu" },
            { Key.R,          "⟳  Restart" },
        };

        // Singleton self-reference for static Harmony patches (e.g. the freeze coroutine hook)
        // that need to reach instance state. The plugin is a BepInEx singleton.
        internal static Plugin PluginInstance;

        private void Awake()
        {
            Logger = base.Logger;
            PluginInstance = this;

            const string section = "Keybinds";
            keybindStartRecord = Config.Bind(section, "StartRecording", new KeyboardShortcut(KeyCode.F6), "Start a new TAS recording.");
            keybindStopRecord = Config.Bind(section, "StopRecording", new KeyboardShortcut(KeyCode.F7), "Stop recording or playback.");
            keybindPlayback = Config.Bind(section, "StartPlayback", new KeyboardShortcut(KeyCode.F8), "Load and play the main.tas file.");
            keybindToggleHitboxes = Config.Bind(section, "ToggleHitboxes", new KeyboardShortcut(KeyCode.F4), "Toggle the collider/hitbox overlay.");
            keybindToggleNoclip = Config.Bind(section, "ToggleNoclip", new KeyboardShortcut(KeyCode.F3), "Toggle debug no-clip flight (WASD to move, Shift to boost).");
            keybindCycleAbilities = Config.Bind(section, "CycleAbilities", new KeyboardShortcut(KeyCode.F2), "Cycle the player's movement-ability unlock combination.");

            keybindDefs = new[]
            {
                ("Start Recording",  keybindStartRecord),
                ("Stop Recording",   keybindStopRecord),
                ("Playback",         keybindPlayback),
                ("Toggle Hitboxes",  keybindToggleHitboxes),
                ("Toggle No-clip",   keybindToggleNoclip),
                ("Cycle Abilities",  keybindCycleAbilities),
            };

            RebuildIgnoredKeys();
            Config.SettingChanged += (_, _) => RebuildIgnoredKeys();

            topCenterStyle = new GUIStyle { fontSize = 40, normal = { textColor = Color.red }, alignment = TextAnchor.UpperCenter };
            debugStyle = new GUIStyle { fontSize = 16, normal = { textColor = Color.white }, alignment = TextAnchor.UpperLeft };
            fpsStyle = new GUIStyle { fontSize = 18, normal = { textColor = Color.yellow }, alignment = TextAnchor.UpperCenter };

            sidebarStyle = new GUIStyle
            {
                fontSize = 14,
                normal = { textColor = new Color(0.8f, 0.8f, 0.8f) },
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 2, 2)
            };
            sidebarHighlightStyle = new GUIStyle(sidebarStyle)
            {
                fontSize = 15,
                normal = { textColor = Color.yellow },
                fontStyle = FontStyle.Bold
            };
            sidebarDimStyle = new GUIStyle(sidebarStyle)
            {
                normal = { textColor = new Color(0.45f, 0.45f, 0.45f) }
            };

            tasFolder = Path.Combine(Paths.ConfigPath, "TAS");
            if (!Directory.Exists(tasFolder))
                Directory.CreateDirectory(tasFolder);

            // Recordings are written here; playback only ever reads main.tas.
            recordingsFolder = Path.Combine(tasFolder, "recordings");
            if (!Directory.Exists(recordingsFolder))
                Directory.CreateDirectory(recordingsFolder);

            InputSystem.onDeviceChange += OnDeviceChange;

            TakeoverInit();
        }

        // Lazily initialize styles that depend on GUISkin (must be called inside OnGUI).
        private void EnsureRebindStyles()
        {
            if (rebindStylesInitialized) return;
            rebindStylesInitialized = true;

            rebindTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.3f) },
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 4, 6, 4),
            };

            rebindRowLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) },
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 4, 0, 0),
            };

            rebindButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(4, 4, 2, 2),
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f), background = MakeTex(1, 1, new Color(0.18f, 0.18f, 0.22f)) },
                hover = { textColor = Color.white, background = MakeTex(1, 1, new Color(0.28f, 0.28f, 0.35f)) },
                active = { textColor = Color.white, background = MakeTex(1, 1, new Color(0.35f, 0.35f, 0.45f)) },
            };

            rebindButtonActiveStyle = new GUIStyle(rebindButtonStyle)
            {
                normal = { textColor = new Color(0.15f, 0.15f, 0.15f), background = MakeTex(1, 1, new Color(1f, 0.85f, 0.2f)) },
                hover = { textColor = new Color(0.15f, 0.15f, 0.15f), background = MakeTex(1, 1, new Color(1f, 0.85f, 0.2f)) },
                active = { textColor = new Color(0.15f, 0.15f, 0.15f), background = MakeTex(1, 1, new Color(1f, 0.85f, 0.2f)) },
            };

            rebindHintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                normal = { textColor = new Color(0.5f, 0.5f, 0.5f) },
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(4, 4, 2, 4),
                wordWrap = true,
            };
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var tex = new Texture2D(w, h);
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        private void RebuildIgnoredKeys()
        {
            ignoredKeys.Clear();

            if (keybindDefs == null) return;

            foreach (var (_, entry) in keybindDefs)
            {
                var shortcut = entry.Value;
                if (shortcut.MainKey != KeyCode.None)
                    ignoredKeys.Add(UnityKeyCodeToInputSystemKey(shortcut.MainKey));

                foreach (var mod in shortcut.Modifiers)
                    ignoredKeys.Add(UnityKeyCodeToInputSystemKey(mod));
            }

            ignoredKeys.Remove(Key.None);
        }

        private static Key UnityKeyCodeToInputSystemKey(KeyCode kc)
        {
            if (kc >= KeyCode.F1 && kc <= KeyCode.F15)
                return Key.F1 + (kc - KeyCode.F1);

            return kc switch
            {
                KeyCode.LeftArrow => Key.LeftArrow,
                KeyCode.RightArrow => Key.RightArrow,
                KeyCode.UpArrow => Key.UpArrow,
                KeyCode.DownArrow => Key.DownArrow,
                KeyCode.LeftShift => Key.LeftShift,
                KeyCode.RightShift => Key.RightShift,
                KeyCode.LeftControl => Key.LeftCtrl,
                KeyCode.RightControl => Key.RightCtrl,
                KeyCode.LeftAlt => Key.LeftAlt,
                KeyCode.RightAlt => Key.RightAlt,
                KeyCode.Space => Key.Space,
                KeyCode.Return => Key.Enter,
                KeyCode.Escape => Key.Escape,
                KeyCode.Tab => Key.Tab,
                KeyCode.Backspace => Key.Backspace,
                KeyCode.Delete => Key.Delete,
                KeyCode.Insert => Key.Insert,
                KeyCode.Home => Key.Home,
                KeyCode.End => Key.End,
                KeyCode.PageUp => Key.PageUp,
                KeyCode.PageDown => Key.PageDown,
                _ => Key.None
            };
        }

        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device.name == "VirtualKeyboard") return;
            if (virtualKeyboard != null) return;
            if (change != InputDeviceChange.Added) return;

            EnsureVirtualKeyboard();
        }

        // Idempotently create the virtual keyboard. Normally triggered reactively by
        // OnDeviceChange when a real device appears, but the harness calls it directly
        // because a headless run may have no input devices (so OnDeviceChange never fires).
        private void EnsureVirtualKeyboard()
        {
            if (virtualKeyboard != null) return;
            try
            {
                virtualKeyboard = InputSystem.AddDevice<Keyboard>("VirtualKeyboard");
                Logger.LogInfo($"Virtual keyboard added, id={virtualKeyboard.deviceId}");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to add virtual keyboard: {e}");
            }
        }

        private void OnDestroy()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;
            if (virtualKeyboard != null)
                InputSystem.RemoveDevice(virtualKeyboard);
            ClearHitboxOverlay();
            TakeoverShutdown();
        }

        private void Update()
        {
            deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;

            // Hold the present rate during playback. SettingsScript only writes
            // targetFrameRate at startup / on settings changes (not per frame), so a
            // one-shot would normally suffice, but re-asserting is cheap insurance.
            if (frameRateOverridden) Application.targetFrameRate = activePresentFps;

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            HandleTASControls(keyboard);

            // Frozen-time frame advance (the dual-clock model): while physics is FROZEN
            // (timeScale=0 -> no FixedUpdate), Update becomes the live authority and advances the
            // SAME frame list, so a freeze is an AUTHORED/DERIVED frame count, not one measured off
            // the free-running render rate. Drives the dash-freeze takeover (and the authored
            // pause). See docs/harness.md + TakeoverRegistrations.cs.
            if (Time.timeScale == 0f)
                TakeoverAdvanceFrozen();

            // Render-only overlay.
            if (hitboxOverlayEnabled) UpdateHitboxOverlay();
            if (movementComp == null) TryFindMovement();
        }

        private void FixedUpdate()
        {
            // No-clip forks game physics, so it must never overlap the determinism
            // path: if a TAS run begins while it's on, force it off first.
            if (noclipEnabled && (isRecording || isReplaying)) DisableNoclip();
            if (noclipEnabled) NoclipFixedTick();

            // Service the canonical start gate: a run armed on tick T is promoted on T+1, so
            // frame-0 always lands one full Update→FixedUpdate cycle into stable lockstep.
            ServiceStartGate();

            if (isRecording) CaptureFrame();
            if (isReplaying) PlayFrame();
        }

        // Promote a pending (armed) run to active once lockstep is confirmed stable. While a
        // start is pending, no frame is captured/played — this is the transition buffer. The
        // FixedUpdate that promotes the run also does not capture/play (it falls through with
        // isRecording/isReplaying still false this tick); the first real frame is the NEXT
        // FixedUpdate, one full Update→FixedUpdate cycle into stable lockstep.
        private void ServiceStartGate()
        {
            if (pendingStart == PendingStart.None) return;

            if (!LockstepStartReady())
            {
                fixedUpdatesSinceArm++;
                return;
            }

            // Activate under the unified TAS regime: render-independent transform (interpolation
            // off) so capture and replay share one deterministic ruler, alongside the lockstep
            // already applied at arm time. Done here, at activation, where the player body is
            // guaranteed resolved (after the arming buffer), for both record and playback.
            ApplyTasInterpolation();
            TakeoverReset();

            if (pendingStart == PendingStart.Record) isRecording = true;
            else if (pendingStart == PendingStart.Play) isReplaying = true;
            pendingStart = PendingStart.None;
        }

        // Readiness for the start gate: at least one FixedUpdate elapsed under lockstep since arming,
        // so the action rebind has been through a full Update/InputSystem cycle before the first input
        // is sampled. (The single point a future pre-armed boot would relax — see the pendingStart field.)
        private bool LockstepStartReady() => frameRateOverridden && fixedUpdatesSinceArm >= 1;

        // ==============================
        // RECORDING
        // ==============================
        private void CaptureFrame()
        {
            // Behaviour takeovers during recording too, so a recorded death respawns on the
            // canonical frame and record/playback agree (TakeoverRegistrations.cs).
            TakeoverNormalTick();

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            var snapshot = new FrameInputSnapshot();

            foreach (Key key in Inputs.SupportedKeys)
            {
                if (ignoredKeys.Contains(key)) continue;

                var ctrl = keyboard[key];
                if (ctrl == null) continue;

                bool isDown = ctrl.isPressed;
                bool wasDown = previouslyDown.Contains(key);

                if (isDown || wasDown)
                {
                    snapshot.keyStates[key] = new KeySnapshot
                    {
                        isDown = isDown,
                        wentDown = ctrl.wasPressedThisFrame,
                        wentUp = ctrl.wasReleasedThisFrame
                    };
                }

                if (isDown) previouslyDown.Add(key);
                else previouslyDown.Remove(key);
            }

            recordedFrames.Add(snapshot);
        }

        // ==============================
        // PLAYBACK
        // ==============================

        // Shared playback entry point used by the F8 hotkey and the harness.
        private void BeginPlayback(string file)
        {
            // Create the virtual keyboard for the run (rebound below; removed at plugin teardown).
            EnsureVirtualKeyboard();
            savedInputFile = file;

            LoadRecording(file);
            ApplyFrameRate();
            ApplyPlaybackRngSeed();
            RebindActionsToVirtualKeyboard();

            // Arm, don't activate: the start gate (ServiceStartGate) promotes this to
            // isReplaying once lockstep is confirmed stable, so the first PlayFrame lands on
            // the canonical start boundary regardless of whether F8 (Update) or the harness
            // (FixedUpdate) called us. replayIndex is reset now so frame-0 plays first.
            isRecording = false;
            isReplaying = false;
            pendingStart = PendingStart.Play;
            fixedUpdatesSinceArm = 0;
            replayIndex = 0;
            nextFrameRateIdx = 0;

            // Optional per-frame state CSV for a live F8 run, so two runs can be diffed
            // frame-by-frame to localise drift. Gated on IGTAS_LIVE_STATE_LOG; default
            // path is playback/<timestamp>.state.csv.
            OpenLiveStateLog();

            Logger.LogInfo($"Playback started from {file}");
        }

        // Apply the file's @rng_seed at playback start (see loadedRngSeed field).
        // ⚠️ Draw-order limit (not a bug): objects whose Start() ran BEFORE F8 keep their drawn phase
        // offsets; a route entering its zones after F8 (the normal case) is fully covered. docs/determinism.md.
        private void ApplyPlaybackRngSeed()
        {
            if (!loadedRngSeed.HasValue) return;   // no @rng_seed directive: leave RNG untouched
            UnityEngine.Random.InitState(loadedRngSeed.Value);
            Logger.LogInfo($"Playback seeded UnityEngine.Random with {loadedRngSeed.Value} (@rng_seed).");
        }

        private void PlayFrame()
        {
            if (virtualKeyboard == null) { StopPlayback(); return; }

            if (replayIndex >= recordedFrames.Count)
            {
                StopPlayback();
                return;
            }

            PlayFrameBody();
        }

        private void PlayFrameBody()
        {
            // Behaviour takeovers (death respawn, …) before the state log so a respawn fired this
            // frame shows in this frame's row. See TakeoverRegistrations.cs.
            TakeoverNormalTick();

            // Apply any @frame_rate change(s) that take effect at or before this frame (presentation
            // only — Update re-asserts activePresentFps every frame; physics stays 1/50). A run
            // authored with several @frame_rate directives changes speed at each point, not just
            // once for the whole file.
            ApplyFrameRateChangesUpTo(replayIndex);

            var snapshot = recordedFrames[replayIndex];
            var keyboardState = new KeyboardState();

            foreach (var kv in snapshot.keyStates)
                if (kv.Value.isDown)
                    keyboardState.Set(kv.Key, true);

            InputSystem.QueueStateEvent(virtualKeyboard, keyboardState, InputState.currentTime);
            if (liveStateLogging) HarnessLogState(replayIndex);
            replayIndex++;
        }

        private void StopPlayback()
        {
            isReplaying = false;
            pendingStart = PendingStart.None; // in case playback is stopped while still arming
            replayIndex = 0;

            if (virtualKeyboard != null)
                InputSystem.QueueStateEvent(virtualKeyboard, new KeyboardState(), InputState.currentTime);

            ResetActionsToDefault();
            RestoreFrameRate();
            RestoreTasInterpolation();
            CloseLiveStateLog();
            Logger.LogInfo("Playback complete.");

            // Hand every in-flight takeover back to the game: un-freeze a held dash freeze (else
            // the game hangs at timeScale=0 — we skipped Unity's scheduler; a menu-held pause is
            // left paused), re-Invoke a pending death respawn so the player isn't stranded dead.
            TakeoverStopAll();
        }

        // ===== LIVE PER-FRAME STATE LOG (diagnostic) =====
        // Emits the per-frame whole-game state CSV (the StateLog emitter) for a live F8 run, so two
        // runs can be diffed frame-by-frame to localise drift. Off unless IGTAS_LIVE_STATE_LOG is
        // set (to "1" for the default playback/<ts>.state.csv path, or to an explicit path).
        // Inert in normal play.
        private bool liveStateLogging;

        private void OpenLiveStateLog()
        {
            string v = System.Environment.GetEnvironmentVariable("IGTAS_LIVE_STATE_LOG");
            if (string.IsNullOrEmpty(v)) return;

            string path = (v == "1" || v == "true")
                ? Path.Combine(tasFolder, "playback", $"{DateTime.Now:yyyyMMdd_HHmmss}.state.csv")
                : v;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); }
            catch (Exception e) { Logger.LogWarning($"Could not open live state log {path}: {e.Message}"); return; }

            if (OpenStateLog(path, "Live state log"))
            {
                liveStateLogging = true;
                Logger.LogInfo($"Live state log writing to {path}");
            }
        }

        private void CloseLiveStateLog()
        {
            if (!liveStateLogging) return;
            liveStateLogging = false;
            Logger.LogInfo($"Live state log: frames={harnessFrames} hash=0x{harnessHash:X16} fullhash=0x{harnessFullHash:X16}");
            harnessStateLog?.Flush();
            harnessStateLog?.Dispose();
            harnessStateLog = null;
        }

        // The game's physics is a fixed 50 Hz (Time.fixedDeltaTime == 0.02, movement hard-normalised
        // to it). Lockstep pins every render frame to one physics step via captureDeltaTime; @frame_rate
        // is presentation-only. Full reasoning: docs/tas_inputs.md (@frame_rate) + docs/determinism.md.
        private const int PhysicsHz = 50;

        private void ApplyFrameRate()
        {
            // Idempotent on the saved restore-values: the harness enables lockstep
            // early (so the settle window is deterministic) and BeginPlayback calls
            // this again once the file's @frame_rate is known. Only capture the
            // originals on the first apply, or the second call would save the
            // already-locked values and RestoreFrameRate couldn't undo them.
            if (!frameRateOverridden)
            {
                savedVSyncCount = QualitySettings.vSyncCount;
                savedTargetFrameRate = Application.targetFrameRate;
                savedCaptureDeltaTime = Time.captureDeltaTime;
            }

            // vSync would clamp the present rate to the monitor and ignore
            // targetFrameRate, so the speed knob needs it off.
            QualitySettings.vSyncCount = 0;
            // Present rate: an explicit @frame_rate (loadedFrameRate) wins (so a slow-mo/debug rate
            // in a .tas is honoured); else real-time (PhysicsHz). Physics is 1/50 either way
            // (captureDeltaTime), so this only changes how fast the deterministic frames are shown.
            activePresentFps = loadedFrameRate ?? PhysicsHz;
            Application.targetFrameRate = activePresentFps;
            Time.captureDeltaTime = 1f / PhysicsHz;
            frameRateOverridden = true;

            Logger.LogInfo($"Lockstep playback: physics={PhysicsHz} Hz, present={activePresentFps} fps (captureDeltaTime={Time.captureDeltaTime:F4}).");
        }

        // Drain the @frame_rate change cursor up to (and including) the given frame. Each mid-run
        // directive only updates the present rate (activePresentFps + targetFrameRate); the Update
        // re-assert holds it. Idempotent past the end of the list; the cursor only moves forward.
        private void ApplyFrameRateChangesUpTo(int frameIndex)
        {
            while (nextFrameRateIdx < loadedFrameRateChanges.Count
                   && loadedFrameRateChanges[nextFrameRateIdx].frame <= frameIndex)
            {
                int fps = loadedFrameRateChanges[nextFrameRateIdx].fps;
                activePresentFps = fps;
                Application.targetFrameRate = fps;
                nextFrameRateIdx++;
                Logger.LogInfo($"@frame_rate -> {fps} fps at frame {frameIndex}.");
            }
        }

        private void RestoreFrameRate()
        {
            if (!frameRateOverridden) return;

            Time.captureDeltaTime = savedCaptureDeltaTime; // back to wall-clock timing
            Application.targetFrameRate = savedTargetFrameRate;
            QualitySettings.vSyncCount = savedVSyncCount;
            frameRateOverridden = false;
        }

        // The player Rigidbody2D ships with interpolation ON (a render feature), but the game's
        // movement raycasts read transform.position — so interpolation leaks render timing into
        // gameplay: the transform lags body.position by a render-frame fraction live but a full
        // physics step headless, diverging a live recording from a headless replay. We force
        // interpolation = None during any TAS run so transform = body each physics step in BOTH —
        // one render-independent ruler. Reversible if the game moves its raycasts to FixedUpdate;
        // restored on stop. Full reasoning: docs/determinism.md ("Player-body interpolation").
        private bool interpolationOverridden;
        private RigidbodyInterpolation2D savedInterpolation;

        private void ApplyTasInterpolation()
        {
            if (interpolationOverridden) return;
            if (movementComp == null) TryFindMovement();
            if (movementComp == null || bodyField == null) return;
            if (bodyField.GetValue(movementComp) is not Rigidbody2D body) return;

            savedInterpolation = body.interpolation;
            body.interpolation = RigidbodyInterpolation2D.None;
            interpolationOverridden = true;
            Logger.LogInfo($"TAS: body.interpolation {savedInterpolation} -> None (render-independent transform).");
        }

        private void RestoreTasInterpolation()
        {
            if (!interpolationOverridden) return;
            interpolationOverridden = false;
            if (movementComp != null && bodyField != null
                && bodyField.GetValue(movementComp) is Rigidbody2D body)
                body.interpolation = savedInterpolation;
        }

        // ==============================
        // ACTION REBINDING
        // ==============================
        private void RebindActionsToVirtualKeyboard()
        {
            if (virtualKeyboard == null) { Logger.LogWarning("Virtual keyboard not ready."); return; }
            if (movementComp == null || moveActionField == null) return;

            string vkPath = "/" + virtualKeyboard.name;
            Logger.LogInfo($"Rebinding to virtual keyboard: {vkPath}");

            RebindComposite(moveActionField, new Dictionary<string, string>
            {
                { "up",    $"{vkPath}/w" },
                { "down",  $"{vkPath}/s" },
                { "left",  $"{vkPath}/a" },
                { "right", $"{vkPath}/d" },
            });

            RebindSimple(jumpActionField, $"{vkPath}/space");
            RebindSimple(dashActionField, $"{vkPath}/leftShift");
            RebindSimple(resetActionField, $"{vkPath}/r");
        }

        private void ResetActionsToDefault()
        {
            if (movementComp == null || moveActionField == null) return;
            Logger.LogInfo("Resetting actions to default bindings.");
            ResetAction(moveActionField);
            ResetAction(jumpActionField);
            ResetAction(dashActionField);
            ResetAction(resetActionField);
        }

        private void ResetAction(FieldInfo field)
        {
            if (field == null) return;
            var action = field.GetValue(movementComp) as InputAction;
            if (action == null) return;
            bool wasEnabled = action.enabled;
            action.Disable();
            action.RemoveAllBindingOverrides();
            if (wasEnabled) action.Enable();
        }

        private void RebindComposite(FieldInfo field, Dictionary<string, string> partPaths)
        {
            if (field == null) return;
            var action = field.GetValue(movementComp) as InputAction;
            if (action == null) return;

            bool wasEnabled = action.enabled;
            action.Disable();

            for (int i = 0; i < action.bindings.Count; i++)
            {
                var binding = action.bindings[i];
                if (!binding.isPartOfComposite) continue;
                string partName = binding.name.ToLower();
                if (partPaths.TryGetValue(partName, out string newPath))
                    action.ApplyBindingOverride(i, new InputBinding { overridePath = newPath });
            }

            if (wasEnabled) action.Enable();
        }

        private void RebindSimple(FieldInfo field, string newPath)
        {
            if (field == null) return;
            var action = field.GetValue(movementComp) as InputAction;
            if (action == null) return;

            bool wasEnabled = action.enabled;
            action.Disable();

            for (int i = 0; i < action.bindings.Count; i++)
            {
                if (action.bindings[i].isComposite) continue;
                action.ApplyBindingOverride(i, new InputBinding { overridePath = newPath });
            }

            if (wasEnabled) action.Enable();
        }

        // ==============================
        // SAVE / LOAD
        // ==============================
        // File handling and the text format live in Inputs; the in-memory
        // model (recordedFrames) is independent of the disk format.
        private void SaveRecording(string path) => Inputs.Save(path, recordedFrames);

        private void LoadRecording(string path)
        {
            var result = Inputs.Load(path);
            recordedFrames.Clear();
            recordedFrames.AddRange(result.Frames);
            loadedComposition = result.HadCommands;
            loadedFrameRate = result.FrameRate;
            loadedFrameRateChanges = result.FrameRateChanges;
            loadedRngSeed = result.RngSeed;
        }

        // ==============================
        // INPUT CONTROLS
        // ==============================
        private bool IsBindingPressed(ConfigEntry<KeyboardShortcut> entry)
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            var shortcut = entry.Value;
            if (shortcut.MainKey == KeyCode.None) return false;

            Key mainKey = UnityKeyCodeToInputSystemKey(shortcut.MainKey);
            if (mainKey == Key.None) return false;

            // Check main key was pressed this frame
            var ctrl = kb[mainKey];
            if (ctrl == null || !ctrl.wasPressedThisFrame) return false;

            // Check all modifiers are currently held
            foreach (var mod in shortcut.Modifiers)
            {
                Key modKey = UnityKeyCodeToInputSystemKey(mod);
                if (modKey == Key.None) continue;
                if (!(kb[modKey]?.isPressed ?? false)) return false;
            }

            return true;
        }

        private void HandleTASControls(Keyboard keyboard)
        {
            // Block all TAS hotkeys while waiting for a rebind key press.
            if (rebindingIndex >= 0) return;

            if (IsBindingPressed(keybindToggleHitboxes))
                ToggleHitboxOverlay();

            // Debug-only tools that deliberately fork game behaviour — keep them out
            // of the determinism path entirely (no TAS record/playback, no harness).
            bool tasBusy = isRecording || isReplaying;
            if (!tasBusy)
            {
                if (IsBindingPressed(keybindToggleNoclip)) ToggleNoclip();
                if (IsBindingPressed(keybindCycleAbilities)) CycleAbilities();
            }

            if (IsBindingPressed(keybindStartRecord))
            {
                // Arm, don't activate: the start gate promotes this to isRecording on the
                // canonical start boundary (the same one playback uses), so frame-0 is captured
                // one full Update→FixedUpdate cycle into stable lockstep — matching how the
                // recording will later be played back.
                isRecording = false;
                isReplaying = false;
                pendingStart = PendingStart.Record;
                fixedUpdatesSinceArm = 0;
                loadedComposition = false;
                recordedFrames.Clear();
                previouslyDown.Clear();
                savedInputFile = Path.Combine(recordingsFolder, $"tas_{DateTime.Now:yyyyMMdd_HHmmss}.tas");

                // Capture under the SAME lockstep regime as playback. Without this,
                // CaptureFrame samples at a free-running, wall-clock-coupled render/
                // physics phase while BeginPlayback replays under captureDeltaTime
                // lockstep — so the recorded frames don't mean the same thing on
                // replay and a long route drifts off its path. Recording has no
                // @frame_rate file, so loadedFrameRate is null and ApplyFrameRate
                // presents at real-time (50 fps) — physics is 1/50 either way.
                loadedFrameRate = null;
                ApplyFrameRate();
                Logger.LogInfo("Recording started.");
            }

            if (IsBindingPressed(keybindStopRecord))
            {
                // Stop while still arming (gate hasn't promoted yet): unwind the arm so we
                // don't leave lockstep applied with no active run.
                if (pendingStart != PendingStart.None)
                {
                    pendingStart = PendingStart.None;
                    RestoreFrameRate();
                }
                if (isRecording)
                {
                    isRecording = false;
                    RestoreFrameRate();
                    RestoreTasInterpolation();
                    SaveRecording(savedInputFile);
                    Logger.LogInfo("Recording stopped.");
                }
                if (isReplaying) StopPlayback();

                // Hand in-flight takeovers back to the game (un-freeze a cancelled dash freeze,
                // re-Invoke a pending death respawn). StopPlayback already does this for the replay
                // case, but a stopped *recording* needs it too.
                TakeoverStopAll();
            }

            if (IsBindingPressed(keybindPlayback))
            {
                // main.tas is the single entrypoint; it may pull in other files
                // via @read_file. Recordings live in recordings/ and are only
                // played by referencing them from main.tas.
                string mainTas = Path.Combine(tasFolder, "main.tas");
                if (!File.Exists(mainTas)) { Logger.LogWarning("No main.tas found in TAS folder."); return; }

                BeginPlayback(mainTas);
            }
        }

        // ==============================
        // DEBUG / GUI
        // ==============================
        private void TryFindMovement()
        {
            GameObject player = GameObject.FindWithTag("Player");
            if (player == null) return;

            movementComp = player.GetComponent("Movement") as MonoBehaviour;
            if (movementComp == null) return;

            Type t = movementComp.GetType();
            momentumField = t.GetField("momentum", BindingFlags.Public | BindingFlags.Instance);
            movingPlatformVelocityField = t.GetField("movingPlatformVelocity", BindingFlags.Public | BindingFlags.Instance);
            velocityField = t.GetField("Velocity", BindingFlags.Public | BindingFlags.Instance);
            bodyField = t.GetField("body", BindingFlags.NonPublic | BindingFlags.Instance);
            onGroundField = t.GetField("onGround", BindingFlags.NonPublic | BindingFlags.Instance);
            OnWallField = t.GetField("OnWall", BindingFlags.NonPublic | BindingFlags.Instance);
            isDeadField = t.GetField("isDead", BindingFlags.NonPublic | BindingFlags.Instance);
            dashCooldownField = t.GetField("dashCooldown", BindingFlags.Public | BindingFlags.Instance);
            dashFramesRemainingField = t.GetField("dashFramesRemaining", BindingFlags.NonPublic | BindingFlags.Instance);
            dashFramesField = t.GetField("dashFrames", BindingFlags.NonPublic | BindingFlags.Instance);
            moveActionField = t.GetField("moveAction", BindingFlags.NonPublic | BindingFlags.Instance);
            jumpActionField = t.GetField("jumpAction", BindingFlags.NonPublic | BindingFlags.Instance);
            dashActionField = t.GetField("dashAction", BindingFlags.NonPublic | BindingFlags.Instance);
            resetActionField = t.GetField("resetAction", BindingFlags.NonPublic | BindingFlags.Instance);

            Logger.LogInfo("Movement component found.");
        }

        // Returns true if the game's settings screen is currently open,
        // by reading pauseMenuScript.settingsMenuOpen via reflection.
        private bool IsSettingsScreenOpen()
        {
            if (pauseMenu == null)
            {
                var obj = FindObjectOfType(GetTypeByName("pauseMenuScript")) as MonoBehaviour;
                if (obj == null) return false;

                pauseMenu = obj;
                settingsMenuOpenField = pauseMenu.GetType().GetField("settingsMenuOpen",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (settingsMenuOpenField == null) return false;

            return (bool)settingsMenuOpenField.GetValue(pauseMenu);
        }

        // Finds a Type anywhere in the current AppDomain by simple name.
        private static Type GetTypeByName(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var type in assembly.GetTypes())
                    if (type.Name == name) return type;
            return null;
        }

        private string FrameToString(int index)
        {
            if (index < 0 || index >= recordedFrames.Count) return "";

            var frame = recordedFrames[index];
            var parts = new List<string>();

            foreach (var kv in frame.keyStates)
                if (kv.Value.isDown)
                    parts.Add(keyLabels.TryGetValue(kv.Key, out string label) ? label : kv.Key.ToString());

            return parts.Count > 0 ? string.Join("  ", parts) : "\u2014";
        }

        // Pretty-print a KeyboardShortcut for the rebind button label.
        private static string ShortcutLabel(KeyboardShortcut ks)
        {
            if (ks.MainKey == KeyCode.None) return "\u2014";
            var parts = new List<string>();
            foreach (var mod in ks.Modifiers)
                parts.Add(mod.ToString());
            parts.Add(ks.MainKey.ToString());
            return string.Join("+", parts);
        }

        // The present rate (@frame_rate) that takes effect AT this frame, or null if none changes here:
        // the starting rate at frame 0, or a mid-run change keyed to this frame. Drives the sidebar
        // markers so a run's speed changes are visible in the frame list during playback.
        private int? FrameRateAtFrame(int frame)
        {
            if (frame == 0 && loadedFrameRate.HasValue) return loadedFrameRate;
            for (int i = 0; i < loadedFrameRateChanges.Count; i++)
                if (loadedFrameRateChanges[i].frame == frame) return loadedFrameRateChanges[i].fps;
            return null;
        }

        private void OnGUI()
        {
            GUI.depth = -1000;
            EnsureRebindStyles();

            float fps = 1.0f / deltaTime;
            GUI.Label(new Rect(0, 0, Screen.width, 25), $"FPS: {fps:F1}", fpsStyle);
            GUI.Label(new Rect(0, 25, Screen.width, 50), "MODDED", topCenterStyle);

            if (hitboxOverlayEnabled) { DrawHitboxLegend(); DrawHitboxLabels(); }

            if (movementComp != null && bodyField != null)
            {
                Rigidbody2D body = (Rigidbody2D)bodyField.GetValue(movementComp);
                bool onGround = onGroundField != null && (bool)onGroundField.GetValue(movementComp);
                Vector2 Velocity = (Vector2)velocityField.GetValue(movementComp);
                Vector2 momentum = (Vector2)momentumField.GetValue(movementComp);
                Single dashCooldown = (Single)dashCooldownField.GetValue(movementComp);
                Single dashFramesRemaining = (Single)dashFramesRemainingField.GetValue(movementComp);
                float dashTimeRemaining = Mathf.Max(0f, dashCooldown);

                string debugText =
                    $"DEBUG:\n" +
                    $"Momentum X: {momentum.x:F2}\n" +
                    $"Momentum Y: {momentum.y:F2}\n\n" +
                    $"Velocity: {Velocity}\n" +
                    $"Velocity: {body.velocity}\n" +
                    $"Position: {body.position}\n" +
                    $"On Ground: {onGround}\n" +
                    $"Dash Cooldown: {dashTimeRemaining}\n" +
                    $"dashFramesRemaining: {dashFramesRemaining}\n" +
                    $"Recording: {isRecording}\n" +
                    $"Replaying: {isReplaying}\n" +
                    $"Replay: {replayIndex}/{recordedFrames.Count}\n" +
                    $"No-clip (F3): {(noclipEnabled ? "ON" : "off")}\n" +
                    $"Abilities (F2): {CurrentAbilityLabel()}";

                GUI.Label(new Rect(10, 10, 400, 280), debugText, debugStyle);
            }

            // ---- Keybind panel (visible when settings screen is open) ----
            if (IsSettingsScreenOpen())
                DrawRebindPanel();

            // ---- Replay frame sidebar ----
            if (isReplaying && recordedFrames.Count > 0)
            {
                int currentFrame = replayIndex - 1;
                int visibleCount = 10;
                int rowHeight = 28;
                int sidebarWidth = 260;
                int totalHeight = visibleCount * rowHeight;
                int startY = (Screen.height - totalHeight) / 2;
                int startX = 10;

                GUI.color = new Color(0, 0, 0, 0.55f);
                GUI.DrawTexture(new Rect(startX - 4, startY - 4, sidebarWidth + 8, totalHeight + 8), Texture2D.whiteTexture);
                GUI.color = Color.white;

                int half = visibleCount / 2;
                int start = Mathf.Clamp(currentFrame - half, 0, Mathf.Max(0, recordedFrames.Count - visibleCount));

                for (int i = 0; i < visibleCount; i++)
                {
                    int frameIdx = start + i;
                    if (frameIdx >= recordedFrames.Count) break;

                    bool isCurrent = frameIdx == currentFrame;
                    string label = $"[{frameIdx:D4}]  {FrameToString(frameIdx)}";
                    // Mark a frame where @frame_rate changes (or the starting rate at frame 0).
                    int? rateHere = FrameRateAtFrame(frameIdx);
                    if (rateHere.HasValue) label += $"   » {rateHere.Value}fps";
                    var style = isCurrent ? sidebarHighlightStyle
                                       : (Mathf.Abs(frameIdx - currentFrame) > 3 ? sidebarDimStyle : sidebarStyle);

                    if (isCurrent)
                    {
                        GUI.color = new Color(1f, 1f, 0f, 0.15f);
                        GUI.DrawTexture(new Rect(startX - 4, startY + i * rowHeight, sidebarWidth + 8, rowHeight), Texture2D.whiteTexture);
                        GUI.color = Color.white;
                    }

                    GUI.Label(new Rect(startX, startY + i * rowHeight, sidebarWidth, rowHeight), label, style);
                }
            }
        }

        // ==============================
        // REBIND PANEL
        // ==============================
        private void DrawRebindPanel()
        {
            const int panelX = 10;
            const int panelWidth = 230;
            const int rowHeight = 26;
            const int btnWidth = 100;
            const int titleHeight = 28;
            const int hintHeight = 28;
            const int padding = 6;

            int rows = keybindDefs.Length;
            int panelHeight = titleHeight + rows * rowHeight + hintHeight + padding * 2;
            int panelY = (Screen.height - panelHeight) / 2;

            // Background
            GUI.color = new Color(0.08f, 0.08f, 0.10f, 0.92f);
            GUI.DrawTexture(new Rect(panelX, panelY, panelWidth, panelHeight), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Accent bar at top
            GUI.color = new Color(1f, 0.85f, 0.3f, 0.85f);
            GUI.DrawTexture(new Rect(panelX, panelY, panelWidth, 3), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Title
            GUI.Label(new Rect(panelX, panelY + 3, panelWidth, titleHeight), "  TAS  KEYBINDS", rebindTitleStyle);

            // Separator under title
            GUI.color = new Color(1f, 1f, 1f, 0.08f);
            GUI.DrawTexture(new Rect(panelX + 6, panelY + titleHeight, panelWidth - 12, 1), Texture2D.whiteTexture);
            GUI.color = Color.white;

            Event e = Event.current;

            // Capture the next key press when a row is awaiting rebind.
            if (rebindingIndex >= 0 && e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    rebindingIndex = -1;
                }
                else
                {
                    keybindDefs[rebindingIndex].entry.Value = new KeyboardShortcut(e.keyCode);
                    Logger.LogInfo($"Rebound '{keybindDefs[rebindingIndex].label}' to {e.keyCode}");
                    rebindingIndex = -1;
                    RebuildIgnoredKeys();
                }

                e.Use();
            }

            // Rows
            int contentY = panelY + titleHeight + padding;
            for (int i = 0; i < keybindDefs.Length; i++)
            {
                var (label, entry) = keybindDefs[i];
                bool isWaiting = rebindingIndex == i;
                int y = contentY + i * rowHeight;
                int labelWidth = panelWidth - btnWidth - padding * 3;

                // Row highlight when active
                if (isWaiting)
                {
                    GUI.color = new Color(1f, 0.85f, 0.2f, 0.08f);
                    GUI.DrawTexture(new Rect(panelX, y, panelWidth, rowHeight), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }

                // Action label
                GUI.Label(new Rect(panelX, y, labelWidth, rowHeight), label, rebindRowLabelStyle);

                // Binding button
                string btnText = isWaiting ? "press key\u2026" : ShortcutLabel(entry.Value);
                var btnStyle = isWaiting ? rebindButtonActiveStyle : rebindButtonStyle;
                var btnRect = new Rect(panelX + labelWidth + padding, y + 3, btnWidth, rowHeight - 6);

                if (GUI.Button(btnRect, btnText, btnStyle))
                    rebindingIndex = (rebindingIndex == i) ? -1 : i;
            }

            // Hint footer
            string hint = rebindingIndex >= 0
                ? "Press any key  \u2022  Esc to cancel"
                : "Click a binding to change it";
            GUI.Label(
                new Rect(panelX, panelY + panelHeight - hintHeight, panelWidth, hintHeight),
                hint,
                rebindHintStyle
            );
        }
    }

    public class KeySnapshot
    {
        public bool isDown;
        public bool wentDown;
        public bool wentUp;
    }

    public class FrameInputSnapshot
    {
        public Dictionary<Key, KeySnapshot> keyStates = new();
    }
}
