# IGTAS docs

**IGTAS** is a **BepInEx 5.x plugin** that adds TAS (Tool-Assisted Speedrun) record/playback to **IGTAP — *An Incremental Game That's Also a Platformer*** (the Steam demo). It hooks the compiled Unity game from outside (virtual keyboard, reflection, `captureDeltaTime` lockstep) — it does **not** modify the game's source. Keyboard-only. [CLAUDE.md](../CLAUDE.md) is the session entry point + framework; this is the doc index + the project-level reference (code map, build/run, gotchas) that no topic doc owns.

## Doc index

| Doc | Covers |
|---|---|
| [determinism.md](determinism.md) | **Live investigation.** Measurement, settled facts, open theories, ordered experiments. The deepest + most active doc. |
| [behaviour-takeovers.md](behaviour-takeovers.md) | **The last-resort determinism tool.** Replacing wall-clock game logic (dash freeze, death respawn, …) with our frame counter when the clock (`Time.time`/`unscaledDeltaTime`) can't be owned: the block→count→drive→complete mechanism, the per-behaviour frame-count derivation, the catalogue, and the dual-clock frame model. |
| [systems.md](systems.md) | Demo-reachable systems (courses, zones, Watts/clone economy, upgrades, the 5 abilities). Day-to-day routing reference. |
| [full-decompiled-systems.md](full-decompiled-systems.md) | Exhaustive system catalogue incl. full-release-only layers (atoms, tree, green economy, zip movers) tagged by demo presence. |
| [movement-constants.md](movement-constants.md) | The `Movement` physics block (actual scene values) + build-specific movement tech (step-up, super-jump). |
| [tas-inputs.md](tas-inputs.md) | The `.tas` file format + `.tas` design decisions. |
| [hitbox-overlay.md](hitbox-overlay.md) | The F4 collider/hitbox overlay (behaviour-based classification + hatches, per-instance labels, spring launch arrows, tilemap/composite ground, the config-persistence trap). |
| [doc-principles.md](doc-principles.md) | **The density rubric** — the checklist this `docs/` set was trimmed against; read before any doc / code-comment review pass, or as a first-pass writing guide. |

See also [../README.md](../README.md) (install/usage).

## Code map

The plugin is one `partial class Plugin` (a `MonoBehaviour`) split across files; `Inputs.cs` is the only standalone, unit-testable piece.

- **[Plugin.cs](../Plugin.cs)** — config/keybinds, recording (`CaptureFrame` in `FixedUpdate`), playback (`PlayFrame` → `KeyboardState` events to a virtual keyboard), the editor, action rebinding, GUI/HUD, lockstep (`ApplyFrameRate`/`RestoreFrameRate`), the canonical start gate (`ServiceStartGate`/`LockstepStartReady`), TAS-interpolation toggle (`ApplyTasInterpolation`), and the live per-frame state CSV (`IGTAS_LIVE_STATE_LOG`).
- **[StateLog.cs](../StateLog.cs)** — `partial`; the **Observe** layer: the per-physics-frame whole-game state CSV + two FNV hashes (`hash` = pos+vel anchor, `fullhash` = whole state) + the per-frame observation columns the takeovers watch. Emitted for a live F8 run via `IGTAS_LIVE_STATE_LOG`. See [determinism.md](determinism.md).
- **[BehaviourTakeovers.cs](../BehaviourTakeovers.cs)** — `partial`; the shared **behaviour-takeover** framework (detect × block primitives, the owned-clock state machine, teardown), plus the always-installed Harmony prefix on `StartCoroutine`. Replaces game progression that hangs off a clock we can't own (`Time.time`, `unscaledDeltaTime`) with our frame counter. Inert in normal play; **version-fragile**. [behaviour-takeovers.md](behaviour-takeovers.md).
- **[TakeoverRegistrations.cs](../TakeoverRegistrations.cs)** — `partial`; the per-case takeover registrations wired onto the framework, each with its inline **VALIDATION box** (trigger / block / count derivation / status / residuals). Built: dash freeze, death respawn, spring-lock, spring-cooldown, zone-cooldown, wall-jump collider, `endRecentJump` (normal + super-jump), non-dash `jumpBuffer`. The `NOT BUILT` section holds the backlog (dash/jumpCut buffers, jumpBuffer dash branch, spring `endRecentJump`). [behaviour-takeovers.md](behaviour-takeovers.md).
- **[Inputs.cs](../Inputs.cs)** — `.tas` file I/O + the text⇆frames transform (`@read_file`/`@frame_rate`/`@rng_seed`). Depends only on the `Key` enum (unit-testable). `SaveRecording`/`LoadRecording` wrap `Inputs.Save`/`Load`. See [tas-inputs.md](tas-inputs.md).
- **[HitboxOverlay.cs](../HitboxOverlay.cs)** — `partial`; render-only F4 collider overlay, read-only, off by default. See [hitbox-overlay.md](hitbox-overlay.md).
- **[DebugMode.cs](../DebugMode.cs)** — `partial`; free-play debug tools (F3 no-clip, F2 ability cycling). Deliberately forks behaviour, so off by default and gated out of the determinism path (only active when no run is in progress). See "Controls" below.
- **In-memory model** — `List<FrameInputSnapshot>`, each a `Dictionary<Key, KeySnapshot{isDown,wentDown,wentUp}>`. The contract between recorder, editor, and file I/O; stable regardless of disk format.
- **`artifacts/`** — gitignored umbrella for non-source, non-committed trees (incl. the decompiled sources the docs link as `artifacts/decompiled/…`), excluded from the plugin compile.

> **Deferred — the headless Drive layer.** The env-gated headless harness (scene-load → settle → playback under lockstep, action scripts, manifest tests, the reset/savestate core) and the virtual gamepad/mouse devices are **not in this baseline** — they return later as a `Driver`/`Autoplay` layer. The determinism docs still describe them as the (deferred) measurement path; `StateLog` is the Observe half that stayed.

## Build, deploy, run

```bash
# build (override the managed-dir per machine)
dotnet build -c Release -p:GameManagedDir="<game>/IGTAP_Data/Managed"
# -> bin/Release/net472/IGTAS.dll ; deploy by copying to <game>/BepInEx/plugins/IGTAS.dll (no auto-copy step yet)
```

Machine-specific paths live in `.env` (gitignored: `steam_path`, `save_path`). The game's TAS folder (`<game>/BepInEx/config/TAS`) is a **symlink to this repo's `inputs/`**, so `main.tas` (the sole playback entrypoint) and hand-authored files live in-repo.

**Constraint:** this environment has no OS input-injection tool (xdotool/ydotool/wtype), so live F8 playback and menu nav can't be driven programmatically. The headless driver that exercised gameplay without OS input is deferred (above), so today verification is a hands-off live F8 run with `IGTAS_LIVE_STATE_LOG=1` — see [determinism.md](determinism.md).

### Env vars

The plugin reads a single runtime env var; it is inert/off unless set, so the shipped plugin is unaffected.

| Var | Owner | Purpose |
|---|---|---|
| `IGTAS_LIVE_STATE_LOG` | Plugin.cs | make a **live** F8 run emit the per-frame state CSV (`1` for the default `playback/<ts>.state.csv` path, or an explicit path), so two runs can be diffed frame-by-frame |

(The broader `IGTAS_*` lever inventory belonged to the deferred headless Drive layer; the live F8 RNG-seed companion is the `@rng_seed` `.tas` directive — see [tas-inputs.md](tas-inputs.md).)

## Controls

F1 frame-step (live run: freeze + advance one frame) · F2 cycle abilities · F3 no-clip · F4 hitboxes · F6 record · F7 stop · F8 play `main.tas` · F9 editor · F10/F11 add/remove frame · ←/→ select frame. Gameplay controls: Up, Left, Down, Right, Jump, Dash, SwapHud, Menu, Restart.

**Debug-only free-play tools** ([DebugMode.cs](../DebugMode.cs)) deliberately fork game behaviour, so they're gated out of the determinism path (only invoked when no TAS run is active; `FixedUpdate` force-disables no-clip if a run starts) and off by default:

- **F3 no-clip** — fly with WASD at 1000 u/s (Shift = 4×). Disables `Rigidbody2D.simulated` (so `Movement`'s velocity write is inert) and drives `transform.position`; takes over the camera (the game's lerp can't keep up at flight speed). Both restored on exit.
- **F2 cycle abilities** — cycles the four demo-usable unlocks (wallJump, dash, doubleJump, blockSwap), mirroring the game's grant code (air abilities need their `maxAir*` count too). `omniDashUnlocked` excluded (code-only, no demo box).

## Gotchas

- **The `TAS` symlink can silently revert to a plain dir.** If `BepInEx/config/TAS` isn't a symlink to the repo's `inputs/` at launch, the plugin recreates `TAS/recordings` as a real dir, *masking* it — playback then logs `TAS file not found: …/main.tas`. Fix: `rm -rf` the plain dir and re-create the symlink (`ln -s <repo>/inputs <game>/BepInEx/config/TAS`). A Steam update can also clobber the deployed DLL + revert this symlink.
- **BepInEx `Config.Bind` persists — code-default changes do NOT reach existing installs.** A bound value is written to `BepInEx/config/IGTAS.cfg` on first run and the file then wins over the code default forever; BepInEx can't tell a user-changed value from a stale default. When a config/default change "has no effect," suspect the persisted `.cfg` first. Rule: only `Config.Bind` what should genuinely persist (keybinds); tuning knobs with one right answer belong in code constants. Detail: [hitbox-overlay.md](hitbox-overlay.md).
- **Any real-device input during playback desyncs the run** — not just a controller. Pressing *any* physical key (e.g. toggling F4 hitboxes) mid-playback perturbs the InputSystem's arbitration and diverges the played inputs; an idle device is silent, so a hands-off run is fine. Input isolation is owed. The older controller-specific case (keyboard-only TAS) has a reset workaround in [../README.md](../README.md).
- **Reflection field names mirror the game's `Movement` members** and break silently if the game renames one — re-check after any game update. Same failure mode for the overlay's script-name matching.

## Scene-asset mechanic audit — findings digest

Per-instance `[SerializeField]` values for every gameplay mechanic are extracted (decompile = logic; scenes = runtime-authoritative values that override code defaults). **Extraction is complete**; the values live in the topic docs (the overlay consumes them), only **runtime** data (live reward/cost numbers) is unextracted (needs a live run). The headline findings, each owned by its topic doc:

- **Springs** are a *continuum*, not a small/big binary, and face **all four cardinals** (one launches *down*) → one colour + per-pad value labels + a live launch arrow. [hitbox-overlay.md](hitbox-overlay.md).
- **Ground** solidity is the `Ground` **layer**, not the tag (the tag only sub-picks solid/mossy, cosmetic). [hitbox-overlay.md](hitbox-overlay.md).
- **`spikeScript`** kills on the 2nd overlapping frame (or instantly mid-dash); mostly tilemap-painted. [hitbox-overlay.md](hitbox-overlay.md).
- **`upgradeBox`** — geometric cost, 18 distinct targets (all Watts but one); two colliders (solid body + trigger pad). Targets in [systems.md](systems.md).
- **`courseScript`** payout scales as `10^rewardTier`, so **tier dominates** `baseReward`. [systems.md](systems.md).
- **`Movement`** — the full physics block (scene values override decompile defaults; `runSpeed*10` = 450 u/s top speed). [movement-constants.md](movement-constants.md).
- **`longFallColliderController`** raises fall terminal velocity (some instances inert); **`PlatformMover`** has 0 demo instances (full-game only). [hitbox-overlay.md](hitbox-overlay.md), [full-decompiled-systems.md](full-decompiled-systems.md).
