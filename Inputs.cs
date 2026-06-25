using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine.InputSystem;

namespace IGTAS
{
    /// <summary>
    /// The sole on-disk (de)serializer for TAS recordings (run-length-encoded text).
    /// Format grammar, @commands, key names, and sandboxing: docs/tas_inputs.md.
    ///
    /// Only the controls in <see cref="KeyToName"/> are supported (on disk and when
    /// recording). The in-memory model (List&lt;FrameInputSnapshot&gt;) is shared with the
    /// recorder/editor, so they keep working untouched. Playback consumes only the held
    /// (isDown) state, so on load wentDown is reconstructed by diffing against the previous
    /// frame and wentUp is left false — a round-trip is behaviourally, not byte-, identical.
    /// </summary>
    internal static class Inputs
    {
        // The only supported controls, named per the in-game Controls screen.
        // These names are the canonical, greppable spelling used everywhere.
        private static readonly Dictionary<Key, string> KeyToName = new()
        {
            { Key.W,         "Up"      },
            { Key.A,         "Left"    },
            { Key.S,         "Down"    },
            { Key.D,         "Right"   },
            { Key.Space,     "Jump"    },
            { Key.LeftShift, "Dash"    },
            { Key.Tab,       "SwapHud" },
            { Key.Escape,    "Menu"    },
            { Key.R,         "Restart" },
        };

        // Canonical column order so saved lines read consistently
        // (matches the in-game Controls list).
        private static readonly List<Key> Order = new()
        {
            Key.W, Key.A, Key.S, Key.D,
            Key.Space, Key.LeftShift, Key.Tab, Key.Escape, Key.R,
        };

        private static readonly Dictionary<string, Key> NameToKey = BuildNameLookup();

        private static Dictionary<string, Key> BuildNameLookup()
        {
            var map = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in KeyToName) map[kv.Value] = kv.Key;
            return map;
        }

        /// <summary>The keys that may be recorded, edited, and written to file.</summary>
        public static IEnumerable<Key> SupportedKeys => KeyToName.Keys;

        /// <summary>Result of loading a TAS file, including any expanded commands.</summary>
        public sealed class LoadResult
        {
            public readonly List<FrameInputSnapshot> Frames = new();

            // True when the buffer was expanded from @commands. Such a buffer is a
            // flattened composition and must not be written back over its source.
            public bool HadCommands;

            // Starting frames-per-second: the @frame_rate directive(s) that appear BEFORE any
            // input line (flattened position 0), last one wins. Applied once at playback start.
            // Mid-file directives go to FrameRateChanges instead. Null if no leading directive.
            public int? FrameRate;

            // Mid-run @frame_rate changes, each keyed to the flattened frame index it takes effect
            // at (the running Frames.Count when the directive was parsed, > 0). Ordered by position
            // — across @read_file includes too, since the count is global. Playback applies each as
            // replayIndex crosses it (presentation-only; physics stays 1/50 under lockstep).
            public readonly List<(int frame, int fps)> FrameRateChanges = new();

            // RNG seed requested via @rng_seed, or null if unset. Applied via
            // Random.InitState at playback start so the economy/RNG stream is
            // reproducible from F8 onward (history-independent — see BeginPlayback).
            public int? RngSeed;
        }

        /// <summary>Write frames to <paramref name="path"/> as run-length-encoded text.</summary>
        public static void Save(string path, List<FrameInputSnapshot> frames)
        {
            using var writer = new StreamWriter(path, append: false);

            int i = 0;
            while (i < frames.Count)
            {
                string keys = HeldNames(frames[i]);

                // Collapse consecutive frames that hold exactly the same keys.
                int run = 1;
                while (i + run < frames.Count && HeldNames(frames[i + run]) == keys)
                    run++;

                writer.WriteLine(keys.Length > 0 ? $"{run},{keys}" : run.ToString());
                i += run;
            }

            Plugin.Logger.LogInfo($"Saved {frames.Count} frames to {Path.GetFileName(path)}.");
        }

        /// <summary>
        /// Parse a text file (expanding any @read_file includes) into per-frame
        /// snapshots plus any global directives.
        /// </summary>
        public static LoadResult Load(string path)
        {
            var result = new LoadResult();
            if (!File.Exists(path))
            {
                Plugin.Logger.LogWarning($"TAS file not found: {path}");
                return result;
            }

            // @read_file paths resolve relative to the directory of the entry
            // file (the inputs folder) and may never escape it.
            var state = new LoadState
            {
                Root = Path.GetDirectoryName(Path.GetFullPath(path)),
                Result = result,
            };
            LoadInto(path, state);
            return result;
        }

        // Mutable state threaded through a (possibly nested) load.
        private sealed class LoadState
        {
            public string Root;
            public LoadResult Result;
            public readonly HashSet<Key> PreviouslyHeld = new();
            public readonly HashSet<string> IncludeChain = new(); // cycle guard
        }

        private static void LoadInto(string filePath, LoadState st)
        {
            string full = Path.GetFullPath(filePath);
            if (!File.Exists(full))
            {
                Plugin.Logger.LogWarning($"read_file: '{filePath}' not found.");
                return;
            }
            if (!st.IncludeChain.Add(full))
            {
                Plugin.Logger.LogWarning($"read_file: skipping recursive include of {Path.GetFileName(full)}.");
                return;
            }

            string source = Path.GetFileName(full);
            int lineNumber = 0;

            foreach (var raw in File.ReadAllLines(full))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                if (line.StartsWith("@"))
                {
                    st.Result.HadCommands = true;
                    HandleCommand(line.Substring(1).Trim(), st, source, lineNumber);
                    continue;
                }

                ParseEntry(line, st, source, lineNumber);
            }

            st.IncludeChain.Remove(full);
        }

        // Dispatch an '@command' line. Supported commands:
        //   @read_file=<relative/path>   — splice another .tas file in at this point.
        //   @frame_rate=<int >= 1>       — set the playback frames-per-second.
        //   @rng_seed=<int>              — seed UnityEngine.Random at playback start.
        private static void HandleCommand(string command, LoadState st, string source, int lineNumber)
        {
            string name = command;
            string arg = "";
            int eq = command.IndexOf('=');
            if (eq >= 0)
            {
                name = command.Substring(0, eq).Trim();
                arg = command.Substring(eq + 1).Trim();
            }

            switch (name.ToLowerInvariant())
            {
                case "read_file":
                    ReadFile(arg, st, source, lineNumber);
                    break;
                case "frame_rate":
                    SetFrameRate(arg, st, source, lineNumber);
                    break;
                case "rng_seed":
                    SetRngSeed(arg, st, source, lineNumber);
                    break;
                default:
                    Plugin.Logger.LogWarning($"{source}:{lineNumber}: unknown command '@{name}', ignoring.");
                    break;
            }
        }

        private static void ReadFile(string arg, LoadState st, string source, int lineNumber)
        {
            if (arg.Length == 0)
            {
                Plugin.Logger.LogWarning($"{source}:{lineNumber}: @read_file needs a path.");
                return;
            }

            string rel = arg.Replace('\\', '/');
            if (!rel.EndsWith(".tas", StringComparison.OrdinalIgnoreCase)) rel += ".tas";

            // Containment: reject absolute paths and any '..' traversal up front.
            if (Path.IsPathRooted(rel) || rel.Split('/').Any(seg => seg == ".."))
            {
                Plugin.Logger.LogWarning($"{source}:{lineNumber}: @read_file '{arg}' must stay inside the inputs folder.");
                return;
            }

            string root = Path.GetFullPath(st.Root);
            string candidate = Path.GetFullPath(Path.Combine(root, rel));

            // Defence in depth: the resolved path must live under the inputs root.
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Plugin.Logger.LogWarning($"{source}:{lineNumber}: @read_file '{arg}' resolves outside the inputs folder.");
                return;
            }

            LoadInto(candidate, st);
        }

        private static void SetFrameRate(string arg, LoadState st, string source, int lineNumber)
        {
            // Valid: >= 1 (a present rate), or -1 (uncapped — render as fast as possible, the
            // Application.targetFrameRate sentinel). 0 and other negatives are meaningless as a present rate.
            if (!int.TryParse(arg, out int fps) || fps == 0 || fps < -1)
            {
                Plugin.Logger.LogWarning($"{source}:{lineNumber}: @frame_rate needs an integer >= 1, or -1 for uncapped (got '{arg}').");
                return;
            }

            // Key the directive to its position in the flattened stream. A leading directive (before
            // any input line) sets the starting rate (last-at-0 wins); a mid-file one is a timed
            // change applied when playback reaches that frame. See FrameRate / FrameRateChanges.
            int at = st.Result.Frames.Count;
            if (at == 0)
                st.Result.FrameRate = fps;
            else
                st.Result.FrameRateChanges.Add((at, fps));
        }

        private static void SetRngSeed(string arg, LoadState st, string source, int lineNumber)
        {
            if (!int.TryParse(arg, out int seed))
            {
                Plugin.Logger.LogWarning($"{source}:{lineNumber}: @rng_seed needs an integer (got '{arg}').");
                return;
            }

            st.Result.RngSeed = seed; // last one wins
        }

        private static void ParseEntry(string line, LoadState st, string source, int lineNumber)
        {
            var parts = line.Split(',');
            if (!int.TryParse(parts[0].Trim(), out int count) || count < 1)
            {
                Plugin.Logger.LogWarning($"{source}:{lineNumber}: invalid frame count '{parts[0]}', skipping line.");
                return;
            }

            var held = new HashSet<Key>();
            for (int p = 1; p < parts.Length; p++)
            {
                string name = parts[p].Trim();
                if (name.Length == 0) continue;
                if (TryResolveKey(name, out Key key))
                    held.Add(key);
                else
                    Plugin.Logger.LogWarning($"{source}:{lineNumber}: unknown control '{name}', ignoring.");
            }

            for (int f = 0; f < count; f++)
            {
                var frame = new FrameInputSnapshot();
                foreach (var key in held)
                {
                    // wentDown fires only on the first frame a key becomes held.
                    bool justPressed = f == 0 && !st.PreviouslyHeld.Contains(key);
                    frame.keyStates[key] = new KeySnapshot
                    {
                        isDown = true,
                        wentDown = justPressed,
                        wentUp = false,
                    };
                }
                st.Result.Frames.Add(frame);
            }

            st.PreviouslyHeld.Clear();
            foreach (var key in held) st.PreviouslyHeld.Add(key);
        }

        // Ordered, comma-joined names of every key held this frame ("" if idle).
        private static string HeldNames(FrameInputSnapshot frame)
        {
            var held = frame.keyStates
                .Where(kv => kv.Value.isDown && KeyToName.ContainsKey(kv.Key))
                .Select(kv => kv.Key)
                .OrderBy(SortIndex)
                .ThenBy(k => k.ToString());

            return string.Join(",", held.Select(k => KeyToName[k]));
        }

        private static int SortIndex(Key key)
        {
            int i = Order.IndexOf(key);
            return i >= 0 ? i : Order.Count;
        }

        // Only the named controls resolve; everything else is rejected.
        private static bool TryResolveKey(string name, out Key key) =>
            NameToKey.TryGetValue(name, out key);
    }
}
