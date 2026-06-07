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
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        private MonoBehaviour movementComp;
        private FieldInfo momentumField;
        private FieldInfo velocityField;
        private FieldInfo bodyField;
        private FieldInfo onGroundField;
        private FieldInfo OnWallField;
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
        private ConfigEntry<KeyboardShortcut> keybindToggleSlowdown;
        private ConfigEntry<KeyboardShortcut> keybindStartRecord;
        private ConfigEntry<KeyboardShortcut> keybindStopRecord;
        private ConfigEntry<KeyboardShortcut> keybindPlayback;
        private ConfigEntry<KeyboardShortcut> keybindToggleEditor;
        private ConfigEntry<KeyboardShortcut> keybindInsertFrame;
        private ConfigEntry<KeyboardShortcut> keybindRemoveFrame;
        private ConfigEntry<KeyboardShortcut> keybindEditorPrev;
        private ConfigEntry<KeyboardShortcut> keybindEditorNext;

        // Built dynamically from config — keys that should never be recorded as gameplay input
        private HashSet<Key> ignoredKeys = new();

        // ===== TAS CORE =====
        private List<FrameInputSnapshot> recordedFrames = new();
        private int replayIndex = 0;
        private bool isRecording = false;
        private bool isReplaying = false;
        private bool isSlowdownEnabled = false;
        private string tasFolder;
        private string savedInputFile;

        private HashSet<Key> previouslyDown = new();
        private Keyboard virtualKeyboard;

        // ===== EDITOR =====
        private bool isEditing = false;
        private int editFrame = 0;

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
            { Key.R,  "⟳ Reset" },
        };

        private void Awake()
        {
            Logger = base.Logger;

            const string section = "Keybinds";
            keybindToggleSlowdown = Config.Bind(section, "ToggleSlowdown", new KeyboardShortcut(KeyCode.F5), "Toggle slowdown mode during record/playback.");
            keybindStartRecord = Config.Bind(section, "StartRecording", new KeyboardShortcut(KeyCode.F6), "Start a new TAS recording.");
            keybindStopRecord = Config.Bind(section, "StopRecording", new KeyboardShortcut(KeyCode.F7), "Stop recording or playback.");
            keybindPlayback = Config.Bind(section, "StartPlayback", new KeyboardShortcut(KeyCode.F8), "Load and play back the most recent TAS file.");
            keybindToggleEditor = Config.Bind(section, "ToggleEditor", new KeyboardShortcut(KeyCode.F9), "Open or close the frame editor.");
            keybindInsertFrame = Config.Bind(section, "InsertFrame", new KeyboardShortcut(KeyCode.F10), "Insert a blank frame after the current editor frame.");
            keybindRemoveFrame = Config.Bind(section, "RemoveFrame", new KeyboardShortcut(KeyCode.F11), "Remove the current frame in the editor.");
            keybindEditorPrev = Config.Bind(section, "EditorStepBack", new KeyboardShortcut(KeyCode.LeftArrow), "Step back one frame in the editor.");
            keybindEditorNext = Config.Bind(section, "EditorStepForward", new KeyboardShortcut(KeyCode.RightArrow), "Step forward one frame in the editor.");

            keybindDefs = new[]
            {
                ("Toggle Slowdown",  keybindToggleSlowdown),
                ("Start Recording",  keybindStartRecord),
                ("Stop Recording",   keybindStopRecord),
                ("Playback",         keybindPlayback),
                ("Toggle Editor",    keybindToggleEditor),
                ("Insert Frame",     keybindInsertFrame),
                ("Remove Frame",     keybindRemoveFrame),
                ("Editor \u25c4 Prev",   keybindEditorPrev),
                ("Editor \u25ba Next",   keybindEditorNext),
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

            InputSystem.onDeviceChange += OnDeviceChange;
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
        }

        private void Update()
        {
            deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            HandleTASControls(keyboard);
            if (isEditing) HandleEditorControls(keyboard);
            if (movementComp == null) {
                TryFindMovement();
                Logger.LogInfo("TryFindMovement");
            }
            ;
        }

        private void FixedUpdate()
        {
            if (isRecording) CaptureFrame();
            if (isReplaying) PlayFrame();
        }

        // ==============================
        // RECORDING
        // ==============================
        private void CaptureFrame()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            var snapshot = new FrameInputSnapshot();

            foreach (Key key in Enum.GetValues(typeof(Key)))
            {
                if (key == Key.None) continue;
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
        private void PlayFrame()
        {
            if (virtualKeyboard == null) { StopPlayback(); return; }

            if (replayIndex >= recordedFrames.Count)
            {
                StopPlayback();
                return;
            }

            var snapshot = recordedFrames[replayIndex];
            var keyboardState = new KeyboardState();

            foreach (var kv in snapshot.keyStates)
                if (kv.Value.isDown)
                    keyboardState.Set(kv.Key, true);

            InputSystem.QueueStateEvent(virtualKeyboard, keyboardState, InputState.currentTime);
            replayIndex++;
        }

        private void StopPlayback()
        {
            isReplaying = false;
            replayIndex = 0;

            if (virtualKeyboard != null)
                InputSystem.QueueStateEvent(virtualKeyboard, new KeyboardState(), InputState.currentTime);

            ResetActionsToDefault();
            Logger.LogInfo("Playback complete.");

            Time.timeScale = 1f;
        }

        // ==============================
        // EDITOR
        // ==============================
        private void EnterEditor()
        {
            if (recordedFrames.Count == 0) { Logger.LogWarning("No frames to edit."); return; }

            isEditing = true;
            isReplaying = false;
            isRecording = false;
            editFrame = Mathf.Clamp(editFrame, 0, recordedFrames.Count - 1);

            Time.timeScale = 0f;
            Logger.LogInfo($"Editor opened at frame {editFrame}.");
        }

        private void ExitEditor()
        {
            isEditing = false;
            Time.timeScale = 1f;

            if (savedInputFile != null)
            {
                SaveRecording(savedInputFile);
                Logger.LogInfo("Editor closed, changes saved.");
            }
        }

        private void HandleEditorControls(Keyboard keyboard)
        {
            if (keybindEditorPrev.Value.IsDown())
                editFrame = Mathf.Max(0, editFrame - 1);

            if (keybindEditorNext.Value.IsDown())
                editFrame = Mathf.Min(recordedFrames.Count - 1, editFrame + 1);

            foreach (Key key in Enum.GetValues(typeof(Key)))
            {
                if (key == Key.None) continue;
                if (ignoredKeys.Contains(key)) continue;

                var ctrl = keyboard[key];
                if (ctrl == null || !ctrl.wasPressedThisFrame) continue;

                var frame = recordedFrames[editFrame];

                if (frame.keyStates.TryGetValue(key, out var existing))
                {
                    if (existing.isDown)
                    {
                        frame.keyStates.Remove(key);
                        Logger.LogInfo($"Frame {editFrame}: removed {key}");
                    }
                    else
                    {
                        existing.isDown = true;
                        Logger.LogInfo($"Frame {editFrame}: set {key} down");
                    }
                }
                else
                {
                    frame.keyStates[key] = new KeySnapshot { isDown = true, wentDown = true, wentUp = false };
                    Logger.LogInfo($"Frame {editFrame}: added {key}");
                }
            }
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
        private void SaveRecording(string path)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);

            bw.Write(recordedFrames.Count);
            foreach (var frame in recordedFrames)
            {
                bw.Write(frame.keyStates.Count);
                foreach (var kv in frame.keyStates)
                {
                    bw.Write((int)kv.Key);
                    bw.Write(kv.Value.isDown);
                    bw.Write(kv.Value.wentDown);
                    bw.Write(kv.Value.wentUp);
                }
            }

            Logger.LogInfo($"Saved {recordedFrames.Count} frames.");
        }

        private void LoadRecording(string path)
        {
            if (!File.Exists(path)) return;
            recordedFrames.Clear();

            using var fs = new FileStream(path, FileMode.Open);
            using var br = new BinaryReader(fs);

            int frameCount = br.ReadInt32();
            for (int i = 0; i < frameCount; i++)
            {
                var frame = new FrameInputSnapshot();
                int keyCount = br.ReadInt32();
                for (int j = 0; j < keyCount; j++)
                {
                    var key = (Key)br.ReadInt32();
                    frame.keyStates[key] = new KeySnapshot
                    {
                        isDown = br.ReadBoolean(),
                        wentDown = br.ReadBoolean(),
                        wentUp = br.ReadBoolean()
                    };
                }
                recordedFrames.Add(frame);
            }

            Logger.LogInfo($"Loaded {recordedFrames.Count} frames.");
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

            if (IsBindingPressed(keybindToggleSlowdown))
            {
                isSlowdownEnabled = !isSlowdownEnabled;
                Logger.LogInfo($"Slowdown {(isSlowdownEnabled ? "enabled" : "disabled")}.");
            }

            if (IsBindingPressed(keybindStartRecord))
            {
                if (isEditing) ExitEditor();

                if (isSlowdownEnabled)
                    Time.timeScale = 0.1f;

                isRecording = true;
                isReplaying = false;
                recordedFrames.Clear();
                previouslyDown.Clear();
                savedInputFile = Path.Combine(tasFolder, $"tas_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
                Logger.LogInfo("Recording started.");
            }

            if (IsBindingPressed(keybindStopRecord))
            {
                if (isRecording)
                {
                    isRecording = false;
                    SaveRecording(savedInputFile);
                    Logger.LogInfo("Recording stopped.");
                }
                if (isReplaying) StopPlayback();

                Time.timeScale = 1f;
            }

            if (IsBindingPressed(keybindPlayback))
            {
                if (isEditing) ExitEditor();

                string[] files = Directory.GetFiles(tasFolder, "tas_*.bin");
                if (files.Length == 0) { Logger.LogWarning("No TAS files found."); return; }

                if (isSlowdownEnabled)
                    Time.timeScale = 0.1f;

                Array.Sort(files);
                savedInputFile = files[files.Length - 1];
                LoadRecording(savedInputFile);
                RebindActionsToVirtualKeyboard();

                isRecording = false;
                isReplaying = true;
                replayIndex = 0;

                Logger.LogInfo($"Playback started from {savedInputFile}");
            }

            if (IsBindingPressed(keybindToggleEditor))
            {
                if (isEditing) ExitEditor();
                else EnterEditor();
            }

            if (IsBindingPressed(keybindInsertFrame) && isEditing)
            {
                int insertAt = Mathf.Clamp(editFrame + 1, 0, recordedFrames.Count);
                recordedFrames.Insert(insertAt, new FrameInputSnapshot());
                editFrame = insertAt;
                Logger.LogInfo($"Inserted blank frame at {insertAt}. Total: {recordedFrames.Count}");
            }

            if (IsBindingPressed(keybindRemoveFrame) && isEditing)
            {
                if (recordedFrames.Count > 1)
                {
                    recordedFrames.RemoveAt(editFrame);
                    editFrame = Mathf.Clamp(editFrame, 0, recordedFrames.Count - 1);
                    Logger.LogInfo($"Removed frame. Now at {editFrame}. Total: {recordedFrames.Count}");
                }
                else
                {
                    Logger.LogWarning("Can't remove last remaining frame.");
                }
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
            velocityField = t.GetField("Velocity", BindingFlags.Public | BindingFlags.Instance);
            bodyField = t.GetField("body", BindingFlags.NonPublic | BindingFlags.Instance);
            onGroundField = t.GetField("onGround", BindingFlags.NonPublic | BindingFlags.Instance);
            OnWallField = t.GetField("OnWall", BindingFlags.NonPublic | BindingFlags.Instance);
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

        private void OnGUI()
        {
            GUI.depth = -1000;
            EnsureRebindStyles();

            float fps = 1.0f / deltaTime;
            GUI.Label(new Rect(0, 0, Screen.width, 25), $"FPS: {fps:F1}", fpsStyle);
            GUI.Label(new Rect(0, 25, Screen.width, 50), "MODDED", topCenterStyle);

            if (movementComp != null && bodyField != null)
            {
                Rigidbody2D body = (Rigidbody2D)bodyField.GetValue(movementComp);
                bool onGround = onGroundField != null && (bool)onGroundField.GetValue(movementComp);
                Vector2 Velocity = (Vector2)velocityField.GetValue(movementComp);
                Vector2 momentum = (Vector2)momentumField.GetValue(movementComp);
                Single dashCooldown = (Single)dashCooldownField.GetValue(movementComp);
                Single dashFramesRemaining = (Single)dashFramesRemainingField.GetValue(movementComp);
                float dashTimeRemaining = Mathf.Max(0f, dashCooldown);

                Single cooldownAfterDash = (Single)0.3;
                // calculates currently dashing time also as cooldown
                //if (dashTimeRemaining > 50)
                //{
                //    //dashTimeRemaining = dashTimeRemaining - (100 - dashTime) + cooldownAfterDash;
                //}

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
                    $"Editing:   {isEditing}\n" +
                    $"Slowdown Enabled:   {isSlowdownEnabled}\n" +
                    $"Replay: {replayIndex}/{recordedFrames.Count}";

                GUI.Label(new Rect(10, 10, 400, 240), debugText, debugStyle);
            }

            // ---- Keybind panel (visible when settings screen is open) ----
            if (IsSettingsScreenOpen())
                DrawRebindPanel();

            // ---- Frame editor sidebar ----
            if ((isEditing || isReplaying) && recordedFrames.Count > 0)
            {
                int currentFrame = isEditing ? editFrame : replayIndex - 1;
                int visibleCount = 10;
                int rowHeight = 28;
                int sidebarWidth = 220;
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

                if (isEditing)
                {
                    GUI.color = new Color(1, 1, 0, 0.9f);
                    GUI.Label(
                        new Rect(startX, startY + totalHeight + 8, sidebarWidth, 80),
                        "\u25c4 \u25ba  step frame\nAny key  toggle\nF10  insert frame\nF11  remove frame\nF9  save & exit",
                        sidebarStyle
                    );
                    GUI.color = Color.white;
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