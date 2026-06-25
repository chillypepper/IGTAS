using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace IGTAS
{
    // Observe layer — the per-frame whole-game state CSV + two FNV hashes (movement-only anchor +
    // full-state). Lifted out of the headless harness so live-F8 verification survives without it:
    // a human drives the run (F8) with IGTAS_LIVE_STATE_LOG set, and the CSV/hash make frame-exact
    // drift checkable. OpenLiveStateLog / CloseLiveStateLog (Plugin.cs) drive these.
    public partial class Plugin
    {
        private StreamWriter harnessStateLog;
        // Two FNV-1a accumulators. harnessHash covers ONLY player position+velocity, so
        // it stays comparable to the pre-economy movement baseline (a regression anchor).
        // harnessFullHash covers the entire logged state — physics decomposition, ground/
        // wall flags, the RNG internal state, and the economy — so an economy- or RNG-only
        // divergence shows up here even when movement is bit-identical.
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;
        private ulong harnessHash = FnvOffset;
        private ulong harnessFullHash = FnvOffset;
        private int harnessFrames;
        // Lazily-resolved reflection handles for whole-game state (economy + RNG).
        private bool harnessStateRefsTried;
        private MonoBehaviour globalStatsInstance;       // Singleton<globalStats>, for instance fields
        private FieldInfo currencyLookupField;           // static Dictionary<Currencies,double>
        private FieldInfo npRewardPerTickField;          // instance: passive income rate
        private Array currencyEnumValues;                // stable iteration order over Currencies
        private FieldInfo rngS0Field, rngS1Field, rngS2Field, rngS3Field; // UnityEngine.Random.State internals
        private FieldInfo normalColliderField;           // Movement.normalCollider (the player's BoxCollider2D)
        private FieldInfo[] observeBoolFields;            // Movement private bools → observation columns (order = ObserveBoolFieldNames)
        private FieldInfo springCooldownField;            // SpringScript.cooldown (private bool); resolved once for the springcooldown column
        private UnityEngine.Object[] springInstances;     // cached active SpringScript instances (the OR set)
        private bool springObsResolved;                   // true once the field + a non-empty instance set are cached
        private FieldInfo currentZoneField;               // Movement.currentZone (public int) for the currentzone column
        private FieldInfo zoneCoolingField;               // zoneChanger.coolingDown (private bool) for the zonecooldown column
        private UnityEngine.Object[] zoneInstances;       // cached active zoneChanger instances (the OR set)
        private bool zoneObsResolved;                     // true once the field + a non-empty instance set are cached
        private static readonly string[] ObserveBoolFieldNames = { "recentlyJumped", "dashBuffer", "jumpBuffer", "jumpCutBuffer", "isDead" };
        private PropertyInfo excludeLayersProp;          // Collider2D.excludeLayers (LayerMask); reflected so a
                                                         // reference assembly without the API can't break the build
        private enum LogKind { Float, Int, Double }

        // One row per logged column — the SINGLE source of truth for the per-frame state log. The
        // header, the CSV data row, and BOTH hashes are all derived from this table (see
        // HarnessEmitState), so adding/moving/removing a column is a one-line edit here that can't
        // silently desync them. Kind drives both the row formatting and which hash the value feeds.
        // InMoveHash flags the four columns in
        // the movement-only regression-anchor hash; InFullHash flags everything in the full-state
        // hash (frame is in neither — it's a deterministic row index, not state). Column order here
        // is load-bearing: it fixes the hash byte order, so a reorder changes the hash. The currency
        // columns (cash..clonedust) sit in Currencies-enum order; npreward is the passive income rate.
        private readonly struct StateColumn
        {
            public readonly string Name;
            public readonly LogKind Kind;
            public readonly bool InMoveHash;
            public readonly bool InFullHash;
            public StateColumn(string name, LogKind kind, bool inMoveHash, bool inFullHash)
            { Name = name; Kind = kind; InMoveHash = inMoveHash; InFullHash = inFullHash; }
        }

        private static readonly StateColumn[] StateColumns =
        {
            new("frame",         LogKind.Int,    false, false),
            new("posx",          LogKind.Float,  true,  true),
            new("posy",          LogKind.Float,  true,  true),
            new("velx",          LogKind.Float,  true,  true),
            new("vely",          LogKind.Float,  true,  true),
            new("momx",          LogKind.Float,  false, true),
            new("momy",          LogKind.Float,  false, true),
            new("mpvx",          LogKind.Float,  false, true),
            new("mpvy",          LogKind.Float,  false, true),
            new("onground",      LogKind.Int,    false, true),
            new("rng0",          LogKind.Int,    false, true),
            new("rng1",          LogKind.Int,    false, true),
            new("rng2",          LogKind.Int,    false, true),
            new("rng3",          LogKind.Int,    false, true),
            new("cash",          LogKind.Double, false, true),
            new("greenpower",    LogKind.Double, false, true),
            new("atomicpower",   LogKind.Double, false, true),
            new("regularnumber", LogKind.Double, false, true),
            new("clonedust",     LogKind.Double, false, true),
            new("npreward",      LogKind.Double, false, true),
            // Pure observation (in NEITHER hash, like `frame`): the player collider's raw excludeLayers
            // mask. Mostly 0; the wall jump sets the Ground bit for the reEnableNormalCollider clip
            // window, so this column makes that (and any future layer-exclusion check) directly visible
            // in the CSV without re-baselining any hash. Promote to InFullHash later if we ever want the
            // hash itself to police collision-layer state (that WOULD require a re-baseline).
            new("excludelayers", LogKind.Int,    false, false),
            // Pure observation (NEITHER hash): Movement private bools surfaced for behaviour-takeover
            // window measurement (the wall-clock-Invoke flags the sweep watches). recentlyjumped =
            // endRecentJump window; the *buffer flags = input-buffer expiry windows. Order matches
            // ObserveBoolFieldNames. Promote to InFullHash only with a deliberate re-baseline.
            new("recentlyjumped", LogKind.Int,   false, false),
            new("dashbuffer",     LogKind.Int,   false, false),
            new("jumpbuffer",     LogKind.Int,   false, false),
            new("jumpcutbuffer",  LogKind.Int,   false, false),
            // isdead = Movement.isDead, surfaced as a CSV column so the death sweep is diffable.
            new("isdead",         LogKind.Int,   false, false),
            // FOREIGN-OBJECT observation (NEITHER hash): OR of every active SpringScript.cooldown — the
            // window the SpringScript.endCooldown takeover sweep watches (the cooldown bool lives on the
            // spring, not Movement, so it needs its own resolver — ObserveSpringCooldown). Measurement
            // keystone; promote to InFullHash only with a deliberate re-baseline.
            new("springcooldown", LogKind.Int,   false, false),
            // ZONE-CHANGE observation (NEITHER hash). currentzone = Movement.currentZone (public int) — makes a
            // zoneChanger swap directly visible. zonecooldown = OR of zoneChanger.coolingDown (foreign bool) — the
            // window the zoneChanger.endCooldown takeover sweep watches. Promote to InFullHash only with a re-bake.
            new("currentzone",    LogKind.Int,   false, false),
            new("zonecooldown",   LogKind.Int,   false, false),
        };

        // CSV header line, shared by every writer (harness boot, manifest segments, reset segments,
        // live F8). Derived from StateColumns so it can never disagree with the data row.
        private static readonly string StateLogHeader =
            string.Join(",", Array.ConvertAll(StateColumns, c => c.Name));
        private void HarnessResolveStateRefs()
        {
            if (harnessStateRefsTried) return;
            harnessStateRefsTried = true;

            // UnityEngine.Random.State is a public struct with private serialized ints s0..s3.
            Type stateType = typeof(UnityEngine.Random.State);
            const BindingFlags inst = BindingFlags.NonPublic | BindingFlags.Instance;
            rngS0Field = stateType.GetField("s0", inst);
            rngS1Field = stateType.GetField("s1", inst);
            rngS2Field = stateType.GetField("s2", inst);
            rngS3Field = stateType.GetField("s3", inst);

            // globalStats : Singleton<globalStats> — currencyLookup is a static dict; the
            // income rate (NPrewardPerTick) is an instance field.
            Type gs = GetTypeByName("globalStats");
            if (gs != null)
            {
                currencyLookupField = gs.GetField("currencyLookup", BindingFlags.Public | BindingFlags.Static);
                npRewardPerTickField = gs.GetField("NPrewardPerTick", BindingFlags.Public | BindingFlags.Instance);
                globalStatsInstance = FindObjectOfType(gs) as MonoBehaviour;
                Type currencies = gs.GetNestedType("Currencies", BindingFlags.Public | BindingFlags.NonPublic);
                if (currencies != null) currencyEnumValues = Enum.GetValues(currencies);
            }
            if (gs == null || currencyLookupField == null)
                Logger.LogWarning("HARNESS: globalStats/currencyLookup not resolved; economy columns will read 0.");

            // Player collider's excludeLayers, for the `excludelayers` observation column. Resolve the
            // property off the field TYPE (BoxCollider2D : Collider2D) so no live instance is needed.
            normalColliderField = movementComp?.GetType().GetField("normalCollider",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (normalColliderField != null)
                excludeLayersProp = normalColliderField.FieldType.GetProperty("excludeLayers");
            if (normalColliderField == null || excludeLayersProp == null)
                Logger.LogWarning("HARNESS: Movement.normalCollider.excludeLayers not resolved; the excludelayers column will read 0.");

            // Movement private bools for the takeover-window observation columns (recentlyjumped + buffers).
            observeBoolFields = new FieldInfo[ObserveBoolFieldNames.Length];
            for (int i = 0; i < ObserveBoolFieldNames.Length; i++)
            {
                observeBoolFields[i] = movementComp?.GetType().GetField(ObserveBoolFieldNames[i],
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (movementComp != null && observeBoolFields[i] == null)
                    Logger.LogWarning($"HARNESS: Movement.{ObserveBoolFieldNames[i]} not resolved; its observation column reads 0.");
            }
        }
        // Called once per replayed physics frame, right after the input is queued. Logs
        // the full player-physics decomposition (body.linearVelocity = Velocity + momentum
        // + movingPlatformVelocity), ground/wall flags, the RNG internal state, and the
        // economy ledger. harnessHash tracks pos+vel only (movement regression anchor);
        // harnessFullHash folds in everything.
        private void HarnessLogState(int frame)
        {
            if (movementComp == null) return;
            HarnessResolveStateRefs();

            // Read the authoritative physics body position, not transform.position
            // (which can lag the Box2D body depending on transform-sync timing).
            // Velocity comes from the game's own field (its compile type matches the
            // 2021.3 reference assembly; Rigidbody2D.linearVelocity does not).
            var body = bodyField != null ? bodyField.GetValue(movementComp) as Rigidbody2D : null;
            Vector2 pos = body != null ? body.position : (Vector2)movementComp.transform.position;
            Vector2 vel = velocityField != null ? (Vector2)velocityField.GetValue(movementComp) : Vector2.zero;

            Vector2 mom = momentumField != null ? (Vector2)momentumField.GetValue(movementComp) : Vector2.zero;
            Vector2 mpv = movingPlatformVelocityField != null ? (Vector2)movingPlatformVelocityField.GetValue(movementComp) : Vector2.zero;
            bool onGround = onGroundField != null && (bool)onGroundField.GetValue(movementComp);

            // RNG internal state (4 ints). Boxing the struct lets reflection read the privates.
            int r0 = 0, r1 = 0, r2 = 0, r3 = 0;
            if (rngS0Field != null)
            {
                object boxed = UnityEngine.Random.state;
                r0 = (int)rngS0Field.GetValue(boxed); r1 = (int)rngS1Field.GetValue(boxed);
                r2 = (int)rngS2Field.GetValue(boxed); r3 = (int)rngS3Field.GetValue(boxed);
            }

            // Economy ledger: the five currencies (static dict) + passive income rate.
            double cash = 0, green = 0, atomic = 0, regular = 0, cloneDust = 0, npReward = 0;
            if (currencyLookupField != null && currencyEnumValues != null
                && currencyLookupField.GetValue(null) is IDictionary ledger)
            {
                double[] vals = new double[currencyEnumValues.Length];
                for (int i = 0; i < currencyEnumValues.Length; i++)
                {
                    object key = currencyEnumValues.GetValue(i);
                    if (ledger.Contains(key)) vals[i] = (double)ledger[key];
                }
                // Order matches the Currencies enum: Cash, GreenPower, AtomicPower, regularNumber, CloneDust.
                if (vals.Length > 0) cash = vals[0];
                if (vals.Length > 1) green = vals[1];
                if (vals.Length > 2) atomic = vals[2];
                if (vals.Length > 3) regular = vals[3];
                if (vals.Length > 4) cloneDust = vals[4];
            }
            if (npRewardPerTickField != null && globalStatsInstance != null)
                npReward = (double)npRewardPerTickField.GetValue(globalStatsInstance);

            // Collider excludeLayers mask (observation column). Reflected value boxed as LayerMask.
            int excludeLayers = 0;
            if (normalColliderField != null && excludeLayersProp != null)
            {
                object col = normalColliderField.GetValue(movementComp);
                if (col != null && excludeLayersProp.GetValue(col) is LayerMask mask) excludeLayers = mask.value;
            }

            // Movement observation bools (recentlyjumped + the three buffers) as 1/0.
            int ObserveBool(int i)
            {
                try { return (observeBoolFields?[i]?.GetValue(movementComp) is bool b && b) ? 1 : 0; }
                catch { return 0; }
            }

            harnessFrames = frame + 1;

            // Build the value vector in StateColumns order, then let HarnessEmitState derive the CSV
            // row AND both hashes from it. Values stay boxed at their real type — a float must format
            // as a float ({0:R}), not be widened to double (which would lengthen its "R" text and
            // desync the CSV), and HashFull* dispatches on the column Kind which must match the box.
            HarnessEmitState(new object[]
            {
                frame,
                pos.x, pos.y, vel.x, vel.y,
                mom.x, mom.y, mpv.x, mpv.y,
                onGround ? 1 : 0,
                r0, r1, r2, r3,
                cash, green, atomic, regular, cloneDust, npReward,
                excludeLayers,
                ObserveBool(0), ObserveBool(1), ObserveBool(2), ObserveBool(3), ObserveBool(4),
                ObserveSpringCooldown(),
                ObserveCurrentZone(), ObserveZoneCooldown(),
            });

            // Periodic flush so a run that FREEZES (e.g. an action sets timeScale=0 and the
            // FixedUpdate-driven harness stops ticking) and is then -KILL'd still leaves its
            // CSV trace on disk. Cheap (every 100 frames) and harmless to a normal run that
            // flushes fully at finish anyway. Without this, a pause-action probe loses all state.
            if ((frame % 100) == 0) harnessStateLog?.Flush();
        }
        // Foreign-object observation: OR of every active SpringScript.cooldown, for the springcooldown
        // column (the SpringScript.endCooldown takeover's measurement window). The field is the same on
        // every instance, so resolve it once; the instance set is found once and cached (the spring probes
        // load springs at boot and never zone-swap). LIMITATION: a zone-swap-activated spring would NOT
        // appear in the cache — refresh the instance list here when the zoneChanger work lands. A controlled
        // probe hits one spring, so the OR is unambiguous; go per-instance only if a multi-spring case needs it.
        private int ObserveSpringCooldown()
        {
            try
            {
                if (!springObsResolved)
                {
                    Type st = GetTypeByName("SpringScript");
                    if (st == null) return 0; // game type not loaded yet (or renamed — the takeover seam logs that loud)
                    if (springCooldownField == null)
                        springCooldownField = st.GetField("cooldown", BindingFlags.NonPublic | BindingFlags.Instance);
                    springInstances = UnityEngine.Object.FindObjectsOfType(st);
                    if (springCooldownField != null && springInstances != null && springInstances.Length > 0)
                        springObsResolved = true; // cache once springs exist; until then retry each emit (cheap)
                }
                if (springCooldownField == null || springInstances == null) return 0;
                for (int i = 0; i < springInstances.Length; i++)
                    if (springInstances[i] != null && springCooldownField.GetValue(springInstances[i]) is bool b && b)
                        return 1;
                return 0;
            }
            catch { return 0; }
        }

        // Movement.currentZone (public int) — makes a zoneChanger swap directly visible in the CSV.
        private int ObserveCurrentZone()
        {
            try
            {
                if (currentZoneField == null && movementComp != null)
                    currentZoneField = movementComp.GetType().GetField("currentZone", BindingFlags.Public | BindingFlags.Instance);
                return currentZoneField?.GetValue(movementComp) is int z ? z : 0;
            }
            catch { return 0; }
        }

        // Foreign-object observation: OR of every active zoneChanger.coolingDown (the endCooldown takeover's
        // measurement window). Same find-once-cache shape as ObserveSpringCooldown — and same LIMITATION: a
        // zoneChanger in a not-yet-activated area wouldn't be cached (refresh here when that case arises).
        private int ObserveZoneCooldown()
        {
            try
            {
                if (!zoneObsResolved)
                {
                    Type zt = GetTypeByName("zoneChanger");
                    if (zt == null) return 0;
                    if (zoneCoolingField == null)
                        zoneCoolingField = zt.GetField("coolingDown", BindingFlags.NonPublic | BindingFlags.Instance);
                    zoneInstances = UnityEngine.Object.FindObjectsOfType(zt);
                    if (zoneCoolingField != null && zoneInstances != null && zoneInstances.Length > 0)
                        zoneObsResolved = true;
                }
                if (zoneCoolingField == null || zoneInstances == null) return 0;
                for (int i = 0; i < zoneInstances.Length; i++)
                    if (zoneInstances[i] != null && zoneCoolingField.GetValue(zoneInstances[i]) is bool b && b)
                        return 1;
                return 0;
            }
            catch { return 0; }
        }
        // Derive the CSV row and both running hashes from one value vector, walking StateColumns so
        // schema and output share a single order. The two hashes use independent accumulators
        // (harnessHash vs harnessFullHash), so feeding both per-column keeps each accumulator's byte
        // order identical to a separate move-then-full pass. Hashing runs every frame regardless of
        // whether a CSV log is open (the hash is the determinism anchor); the row write is null-
        // guarded. A length mismatch is a developer error (the schema and the literal vector
        // disagree) — fail loudly rather than emit a corrupt row.
        private void HarnessEmitState(object[] row)
        {
            if (row.Length != StateColumns.Length)
            {
                Logger.LogWarning($"HARNESS state row has {row.Length} values but the schema has " +
                                  $"{StateColumns.Length} columns — StateColumns/row drift, skipping row.");
                return;
            }

            var sb = new StringBuilder(192);
            for (int i = 0; i < StateColumns.Length; i++)
            {
                StateColumn c = StateColumns[i];
                if (i > 0) sb.Append(',');
                switch (c.Kind)
                {
                    case LogKind.Float:
                        float f = (float)row[i];
                        sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                        if (c.InMoveHash) HashFloat(f);
                        if (c.InFullHash) HashFullFloat(f);
                        break;
                    case LogKind.Int:
                        int n = (int)row[i];
                        sb.Append(n.ToString(CultureInfo.InvariantCulture));
                        if (c.InFullHash) HashFullInt(n);
                        break;
                    case LogKind.Double:
                        double d = (double)row[i];
                        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                        if (c.InFullHash) HashFullDouble(d);
                        break;
                }
            }
            harnessStateLog?.WriteLine(sb.ToString());
        }
        // Open (truncate) the per-frame state CSV at path into harnessStateLog and write the shared
        // StateLogHeader. The single home for the open policy — truncate, no auto-flush, header — so
        // the four writer sites (harness boot, reload segment, manifest segment, live F8) can't drift.
        // Returns whether the log is open; on failure (or empty path) leaves harnessStateLog null and
        // logs under label. Callers own disposing any prior writer before calling.
        private bool OpenStateLog(string path, string label)
        {
            harnessStateLog = null;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                harnessStateLog = new StreamWriter(path, false) { AutoFlush = false };
                harnessStateLog.WriteLine(StateLogHeader);
                return true;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{label} could not open state log {path}: {e.Message}");
                harnessStateLog = null;
                return false;
            }
        }
        // FNV-1a fold of one byte into an accumulator.
        private static ulong FnvByte(ulong h, byte b) => (h ^ b) * FnvPrime;

        // Movement-only accumulator (regression anchor): keep the exact byte order the
        // pre-economy baseline used, so its hash stays comparable run-to-run/version.
        private void HashFloat(float f)
        {
            uint bits = unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
            for (int i = 0; i < 4; i++)
                harnessHash = FnvByte(harnessHash, (byte)(bits >> (i * 8)));
        }

        // Full-state accumulator helpers.
        private void HashFullFloat(float f) => HashFullBytes(BitConverter.GetBytes(f));
        private void HashFullInt(int v) => HashFullBytes(BitConverter.GetBytes(v));
        private void HashFullDouble(double d) => HashFullBytes(BitConverter.GetBytes(d));
        private void HashFullBytes(byte[] bytes)
        {
            foreach (byte b in bytes) harnessFullHash = FnvByte(harnessFullHash, b);
        }
    }
}
