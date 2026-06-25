# TAS Input Files (`.tas`)

IGTAS records and plays back runs as plain-text `.tas` files. They are designed to be read, diffed, and hand-edited in any text editor, and to be **composed** out of smaller reusable pieces.

## Where files live

The game's TAS folder (`BepInEx/config/TAS`) is symlinked to this project's [`inputs/`](../inputs) directory, so everything below lives under `inputs/`:

| Path | Purpose |
| --- | --- |
| `inputs/main.tas` | **The only file playback reads.** Your entrypoint. |
| `inputs/recordings/` | Auto-generated recordings (`tas_<timestamp>.tas`) and editor saves (`edit_<timestamp>.tas`). Git-ignored. |
| `inputs/segments/`, `inputs/test/`, … | Whatever folders you like for reusable pieces, pulled in via `@read_file`. |

> Playback (F8) **always** loads `inputs/main.tas` (never "the newest file"). To play anything else, reference it from `main.tas` (see [`@read_file`](#read_file)).

## Line format

One entry per line:

```
<frames>,<Key>,<Key>,...
```

- `<frames>` — how many consecutive frames to hold the listed keys (a positive integer).
- The keys after it are held **together** for those frames.
- A line with **just a number** is that many idle (no-input) frames.
- Blank lines and lines starting with `#` are ignored (use `#` for comments).

Entries are **run-length encoded**: one line covers many identical frames, so holding right for half a second is `30,Right`, not 30 separate lines. Recordings are saved in this collapsed form automatically.

### Example

```
30              # wait 30 frames
30,Right        # hold Right for 30 frames
20,Right,Jump   # hold Right + Jump for 20 frames
1,Restart       # tap Restart for 1 frame
```

## Key names

Semantic names match the in-game **Controls** screen:

| Name | Key |
| --- | --- |
| `Up` | W |
| `Left` | A |
| `Down` | S |
| `Right` | D |
| `Jump` | Space |
| `Dash` | Left Shift |
| `SwapHud` | Tab |
| `Menu` | Escape |
| `Restart` | R |

Names are **case-insensitive** (`jump` == `Jump`), but the spelling above is the only one accepted — there are no aliases, so the names stay consistent and greppable. **These are the only supported controls**: any other name is skipped with a warning in the BepInEx console, and recording captures only these keys too. (If arbitrary keys are ever needed, they'll be added deliberately rather than accepted implicitly.)

## Commands (`@`)

Any line beginning with `@` is a command rather than an input entry:

```
@<name>[=<argument>]
```

Unknown commands are ignored with a warning.

### `@read_file`

Splices another `.tas` file in at that exact point, as if its lines were pasted in:

```
@read_file=segments/dash_combo
```

- The path is **relative to the `inputs/` folder** and the `.tas` extension is optional (`segments/dash_combo` → `inputs/segments/dash_combo.tas`).
- Includes can nest: an included file may itself `@read_file` others.
- The same file may be included multiple times in sequence; **recursive** cycles (a file that ends up including itself) are detected and skipped with a warning.

**Sandboxing:** `@read_file` can only reach files **inside** `inputs/`. Absolute paths and `..` traversal are rejected, and the resolved path is re-checked to ensure it stays under the inputs root. There is no way to read files elsewhere on disk.

This lets you keep one trick per file and assemble runs by reference, only ever pointing the game at `main.tas`.

### `@frame_rate`

Sets the **presentation speed** of playback:

```
@frame_rate=50
```

- The value must be an integer **≥ 1**, or **-1** for *uncapped* (render as fast as the machine allows); anything else (including `0`) is ignored with a warning.
- **Each directive applies when playback reaches its point in the file**, so a run can change speed section by section (a fast approach, then a slow precision section). A directive placed *before any input line* sets the **starting** rate (if several lead, the last wins); every later directive is a timed change keyed to the frame it sits at — across `@read_file` includes too, since the frame count is global.
- Applied while playback is active and restored when it stops. Each change point is marked in the playback frame sidebar (`» Nfps` on the frame it takes effect), so a run's speed changes are visible in the frame list.

`@frame_rate` is a **presentation-only** knob: `50` = real-time, `200` = 4× fast-forward, `10` = slow-motion. It does **not** affect the simulation — physics is pinned to a fixed 50 Hz internally via `captureDeltaTime` lockstep, so a run is byte-identical regardless of the rate it's shown at (and changing it mid-run is determinism-safe — only the present rate moves). This is what makes runs reproducible: framerate-dependent jump-height jitter comes from the *physics* tracking the render rate, which lockstep prevents. Recording runs under the same lockstep, so a recording means the same thing at any `@frame_rate`. (Full reasoning: [determinism.md](determinism.md).)

### `@rng_seed`

Seeds `UnityEngine.Random` at playback start, so the economy/RNG-driven part of a run is reproducible:

```
@rng_seed=12345
```

- The value must be an integer; anything else is ignored with a warning.
- Global directive — **last one wins** across includes.
- Applied in `BeginPlayback` via `Random.InitState`, **gated to playback** (a normal session's RNG is untouched).

The game never seeds `Random` itself, so its economy RNG (clone draws, reward/box `InvokeRepeating` phase offsets) differs every launch. `@rng_seed` fixes the stream from F8 onward — `InitState` fully overwrites prior state, so the run is identical **regardless of how much idle time or how many draws happened before playback**. Movement is RNG-independent (its `Random` calls are all audio), so the seed only affects the economy. **Draw-order limit:** seeding at F8 covers everything that draws *after* F8 (e.g. a course entered during playback); scene objects that already ran `Start()` *before* F8 keep their already-drawn values — for a route that enters its zones after F8 (the normal case) this is fully covered. The harness has an earlier, broader seed (`IGTAS_RNG_SEED`, applied before scene-load) and takes precedence when set. (Full reasoning: [determinism.md](determinism.md).)

## Recording, playback & editing

| Key | Action |
| --- | --- |
| F1 | Frame-step: during a live run, freeze and advance exactly one frame per press (F7 to resume/stop) |
| F6 | Start recording → writes `inputs/recordings/tas_<timestamp>.tas` on stop |
| F7 | Stop recording / playback |
| F8 | Play back `inputs/main.tas` |
| F9 | Open the in-game frame editor |
| F10 / F11 | Add / remove frame |
| ← / → | Select frame |

### Editing a composed file

If you open the editor on a buffer that was built from `@read_file` (or any command), that buffer is a **flattened** copy — the include boundaries are gone. To avoid silently overwriting your composition, the editor saves those edits to a new `inputs/recordings/edit_<timestamp>.tas` and leaves `main.tas` (and its includes) untouched. Plain, command-free files still save back in place as before.

## Implementation notes

A few smaller details worth knowing if you touch the code ([`Inputs.cs`](../Inputs.cs)):

- **One in-memory model.** Files (de)serialize to the same `List<FrameInputSnapshot>` the recorder and editor use, so all disk I/O is isolated in `Inputs.cs`. `Plugin.SaveRecording` / `LoadRecording` are thin wrappers over `Inputs.Save` / `Inputs.Load`.
- **Only `isDown` matters for playback.** Each frame snapshot also carries `wentDown` / `wentUp`, but playback only reads the held state. On load `wentDown` is reconstructed by diffing against the previous frame and `wentUp` is left false, so a round-trip is *behaviourally* identical without being byte-identical to an old binary capture.
- **Run-length encoding is by held-set.** Consecutive frames whose set of held keys is identical collapse into one line; key columns are written in the canonical Controls order for stable, diff-friendly output.

### Tests

[`tests/`](../tests) holds a standalone harness that compiles the real `Inputs.cs` against tiny Unity/BepInEx stubs and asserts the parsing, include expansion, sandboxing, recursion guard, `@frame_rate`, and save/load round-trip. It needs no game install — run it from the repo root:

```
dotnet run --project tests
```
