using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace IGTAS
{
    // Collider / hitbox visual overlay — a diagnostic, render-only view for inspecting
    // collision and tuning TAS routes. OFF by default, toggled with F4.
    //
    // NORTH STAR: the game should be fully playable from the hitbox view alone. Anything
    // that behaves differently on contact must look different — distinct colour for a
    // distinct interaction, and a per-instance label where the magnitude varies (e.g. a
    // spring's launch strength). Hold new colliders to that bar. (Rationale + the full
    // category/data catalogue: docs/hitbox_overlay.md.)
    //
    // For each tracked Collider2D we parent a child GameObject carrying a LineRenderer
    // and draw the outline in world space (technique borrowed from Rostmoment/HitboxViewer,
    // not vendored — see docs). The value we add over a generic viewer is SEMANTIC: we
    // know each collider's owning gameplay script from artifacts/decompiled/, so spikes, gates,
    // springs and ground are coloured by what they DO, not just their Unity type.
    //
    // Touch-point posture: strictly read-only (reads colliders, draws lines on new child
    // objects; never mutates game state, RNG or Rigidbody2D). Runs in Update at render
    // rate — never FixedUpdate — so it cannot perturb the 50 Hz lockstep, and it is
    // skipped while the determinism harness is active.
    public partial class Plugin
    {
        // ===== CATEGORY COLOURS =====
        private static readonly Color ColPlayer = new Color(0.20f, 1.00f, 1.00f);   // cyan  — the player body
        private static readonly Color ColGround = new Color(0.30f, 0.85f, 0.30f);   // green — solid terrain (tagged "Ground")
        private static readonly Color ColMovingPlatform = new Color(0.65f, 1.00f, 0.20f); // lime — moving platforms
        private static readonly Color ColHazard = new Color(1.00f, 0.15f, 0.10f);   // red   — spikes / kill volumes
        private static readonly Color ColCheckpoint = new Color(0.25f, 0.55f, 1.00f); // blue  — checkpoints
        private static readonly Color ColGate = new Color(0.20f, 0.95f, 0.75f);     // teal  — course gate (start/end told apart by hatch — see below)
        private static readonly Color ColZone = new Color(1.00f, 0.20f, 0.85f);      // magenta — zone transitions
        // One colour for all interaction boxes — the per-box target LABEL distinguishes them (the
        // scripts that look like they'd want their own colour mostly don't draw; see docs/hitbox_overlay.md).
        private static readonly Color ColButton = new Color(1.00f, 0.55f, 0.10f);    // orange — interaction boxes (labelled with what they sell)
        private static readonly Color ColClone = new Color(0.60f, 0.40f, 0.85f);     // purple — clones
        private static readonly Color ColSpring = new Color(1.00f, 0.45f, 0.75f);    // pink  — spring / launch pads
        private static readonly Color ColLongFall = new Color(0.90f, 0.35f, 0.20f);  // burnt orange — long-fall base (active/reset told apart by hatch)
        private static readonly Color ColCameraVolume = new Color(0.45f, 0.45f, 0.50f); // grey — camera / non-gameplay triggers
        private static readonly Color ColTriggerDefault = new Color(0.85f, 0.85f, 0.20f); // yellow — unclassified trigger
        private static readonly Color ColSolidDefault = new Color(0.85f, 0.85f, 0.85f);   // white — solid, NOT on Ground layer (passable!)

        // ===== HATCH (sub-category) COLOURS =====
        // A diagonal hatch-fill inside the box marks a variant of one base interaction (shared base
        // border = "same kind of thing", hatch = which one). See docs/hitbox_overlay.md.
        private static readonly Color ColHatchGateStart = new Color(0.30f, 1.00f, 0.40f); // green  — start gate
        private static readonly Color ColHatchGateEnd = new Color(1.00f, 0.82f, 0.10f);   // gold   — end gate
        private static readonly Color ColHatchGroundMetal = new Color(0.70f, 0.70f, 0.75f);// grey   — solid/metal ground
        private static readonly Color ColHatchGroundMoss = new Color(0.40f, 0.80f, 0.30f); // green  — mossy ground
        private static readonly Color ColHatchPassable = new Color(0.05f, 0.05f, 0.05f);   // black  — looks-solid-but-passable
        private static readonly Color ColHatchLongFallActive = new Color(1.00f, 0.55f, 0.15f); // bright orange — active (raises fall speed)
        private static readonly Color ColHatchLongFallReset = new Color(0.35f, 0.25f, 0.22f);  // dark brown   — inert reset
        // Upgrade-box warning hatches: red = radically changes the game (trips breaker / ends demo),
        // blue = grants a movement ability. Most boxes carry no hatch (the label suffices).
        private static readonly Color ColHatchUpgradeBreaker = new Color(0.95f, 0.10f, 0.10f); // red  — trips breaker / ends demo
        private static readonly Color ColHatchUpgradeAbility = new Color(0.20f, 0.55f, 1.00f); // blue — grants a movement ability

        // Owning-script name -> outline colour (names mirror artifacts/decompiled/*.cs; checked on
        // the collider's GameObject and its parent). A renamed class falls through to the
        // fallback colours, harmless for a diagnostic.
        private static readonly (string script, Color color)[] scriptCategories =
        {
            ("spikeScript",           ColHazard),
            ("checkpointScript",      ColCheckpoint),
            // startGate/endGate: handled in ClassifyHitbox (teal base + start/end hatch).
            ("zoneChanger",           ColZone),
            ("upgradeBox",            ColButton),
            ("localUpgrades",         ColButton),
            ("JumpToStateButton",     ColButton),   // debug state-warp button
            // These don't surface as hitboxes (0 demo instances / manager Singletons), kept for
            // completeness — they'd fall through to a fallback colour anyway.
            ("tripBreakerScript",     ColButton),
            ("prestigeEnabler",       ColButton),
            ("colouredBlockSwapper",  ColButton),
            ("PlatformMover",         ColMovingPlatform),
            ("SpringScript",          ColSpring),
            // longFallColliderController: handled in ClassifyHitbox (burnt-orange + active/inert hatch).
            ("JiggleDropScript",      ColLongFall),
            ("clonesScript",          ColClone),
            ("clonesLoD",             ColClone),
            // Cosmetic / non-gameplay volumes (camera, music, text, sprite visibility).
            ("camSizeTrigger",        ColCameraVolume),
            ("camZoneScript",         ColCameraVolume),
            ("TutorialTextTrigger",   ColCameraVolume),
            ("MusicController",       ColCameraVolume),
            ("GenericSpriteDisabler", ColCameraVolume),
        };

        // The on-screen legend rows: base colour + optional hatch sub-colour.
        private static readonly (string label, Color color, Color? hatch)[] hitboxLegend =
        {
            ("Player",         ColPlayer,        null),
            ("Ground: metal",  ColGround,        ColHatchGroundMetal),
            ("Ground: moss",   ColGround,        ColHatchGroundMoss),
            ("Solid, passable",ColSolidDefault,  ColHatchPassable),
            ("Moving plat.",   ColMovingPlatform,null),
            ("Spring",         ColSpring,        null),
            ("Long fall: on",  ColLongFall,      ColHatchLongFallActive),
            ("Long fall: rst", ColLongFall,      ColHatchLongFallReset),
            ("Hazard",         ColHazard,        null),
            ("Checkpoint",     ColCheckpoint,    null),
            ("Gate: start",    ColGate,          ColHatchGateStart),
            ("Gate: end",      ColGate,          ColHatchGateEnd),
            ("Zone change",    ColZone,          null),
            ("Upgrade box",    ColButton,        null),
            ("Upg: ability",   ColButton,        ColHatchUpgradeAbility),
            ("Upg: breaker",   ColButton,        ColHatchUpgradeBreaker),
            ("Clone",          ColClone,         null),
            ("Camera/trigger", ColCameraVolume,  null),
            ("Unclass. trig.", ColTriggerDefault,null),
        };

        private const int HitboxCircleSegments = 32;

        // Constants, not Config.Bind: a bound value persists to disk and overrides the
        // code default forever, so a shipped change never reaches existing installs.
        // (Full rationale in docs/hitbox_overlay.md + CLAUDE.md.)
        private const float HitboxLineWidthPx = 2.0f; // on-screen outline thickness, pixels
        private const int HitboxUpdateRate = 1;       // frames between refreshes (1 = every frame)

        // ===== STATE =====
        private bool hitboxOverlayEnabled;
        private GameObject hitboxRoot;
        private Material hitboxMaterial;
        // One collider can need MORE than one outline: a CompositeCollider2D (tilemap
        // ground) is a set of disjoint merged paths, each its own LineRenderer (a single
        // renderer would draw spurious connectors between paths). Every other shape is a
        // one-element list. Keyed by collider so stale-cleanup stays a single sweep.
        private readonly Dictionary<Collider2D, List<LineRenderer>> hitboxLines = new();
        // Diagonal hatch fill (a secondary colour channel: a base outline + a tinted
        // diagonal-stripe fill, so related interactions share a border colour but a
        // variant is still visible — e.g. start/end gates, ground solid/moss/passable,
        // long-fall active/reset). One textured quad per box (a repeating stripe texture,
        // tinted per-instance) — replaces the old per-diagonal LineRenderers: fewer draws,
        // no sub-pixel shimmer (the texture is filtered), far less code. Box colliders only
        // (the hatched categories are all boxes); others get the base outline alone.
        private readonly Dictionary<Collider2D, MeshRenderer> hitboxHatch = new();
        private Material hitboxHatchMaterial;   // shares the stripe texture; tint per-instance via MPB
        private Texture2D hitboxHatchTexture;   // generated once: a repeating 45° stripe
        // Spring launch-direction arrows. A spring throws the player along its own
        // transform.up (validated cardinal-for-cardinal against the live diagnostic), and
        // the demo's springs face all four cardinals (one even faces down) — so the launch
        // direction is NOT inferable from the pad's shape and needs to be DRAWN. One textured
        // arrow quad per spring (a generated chevron texture, like the hatch stripe), oriented
        // to the live transform.up and tinted spring-pink. Keyed by collider; same lifecycle
        // as the hatch quads.
        private readonly Dictionary<Collider2D, MeshRenderer> hitboxArrow = new();
        private Material hitboxArrowMaterial;   // shares the arrow texture; tint via MPB
        private Texture2D hitboxArrowTexture;   // generated once: a chevron pointing +V
        private readonly HashSet<Collider2D> hitboxSeen = new();
        private readonly List<Collider2D> hitboxStale = new();
        private int hitboxUpdateCounter;
        private GUIStyle hitboxLegendStyle;

        // Per-instance data labels drawn next to colliders (spring strength, long-fall
        // multiplier, upgrade-box target). Collected in world space during the refresh
        // sweep, projected to screen and drawn in OnGUI. `worldSize` is the collider's
        // world-space bounds extent (x,y); the renderer projects it to screen and scales
        // the font to fill the box where it fits, falling back to a readable minimum.
        // (worldPos, worldSize, text, colour).
        private readonly List<(Vector3 world, Vector2 worldSize, string text, Color color)> hitboxLabels = new();
        // One label per gameplay-script INSTANCE per sweep — a box can own >1 collider, and
        // the label resolves on self-or-parent, so without this guard the same value would
        // stack on top of itself. Cleared each refresh alongside hitboxLabels.
        private readonly HashSet<UnityEngine.Object> hitboxLabelledInstances = new();
        private GUIStyle hitboxLabelStyle;
        // Reflected SpringScript [SerializeField] floats (strength, upForce), resolved
        // lazily off the first spring instance seen (the type lives in Assembly-CSharp,
        // so no direct reference). `springFieldsResolved` flips true after the one lookup,
        // so a genuinely-absent field isn't re-searched every sweep.
        private FieldInfo springStrengthField;
        private FieldInfo springUpForceField;
        private FieldInfo springMoveLockField;
        private bool springFieldsResolved;
        // Reflected longFallColliderController fields (resolved once off the first
        // instance seen): longFallMult (the fall-speed multiplier) + NewCutsceneMode
        // (the enum it writes — longfall = active speed modifier, none = inert reset).
        private FieldInfo longFallMultField;
        private FieldInfo longFallModeField;
        private bool longFallLookupDone;

        private void ToggleHitboxOverlay()
        {
            hitboxOverlayEnabled = !hitboxOverlayEnabled;
            if (!hitboxOverlayEnabled) ClearHitboxOverlay();
            else hitboxUpdateCounter = 0; // refresh immediately on next Update
            Logger.LogInfo($"Hitbox overlay {(hitboxOverlayEnabled ? "enabled" : "disabled")}.");
        }

        // Called from Update() at render rate. Throttled by the HitboxUpdateRate
        // constant: FindObjectsOfType over a clone-heavy scene is not free.
        private void UpdateHitboxOverlay()
        {
            if (hitboxUpdateCounter > 0) { hitboxUpdateCounter--; return; }
            hitboxUpdateCounter = Mathf.Max(0, HitboxUpdateRate - 1);

            EnsureHitboxRoot();

            // Width is configured in *screen pixels*, converted to world units at the
            // current camera zoom. The game's camera is orthographic and lerps its
            // orthographicSize continuously (cameraMover.LateUpdate), and all gameplay
            // colliders sit at Z=0 in a ~1000-unit-tall view — so a fixed world-space
            // width reads as sub-pixel and shimmers as the zoom/round-snapped position
            // drift it across the pixel-coverage threshold. Recomputing px->world each
            // refresh keeps the outline a stable on-screen thickness at any zoom.
            // (At UpdateRate > 1 the width tracks zoom only as often as we refresh.)
            float width = WorldWidthForPixels(HitboxLineWidthPx);
            hitboxSeen.Clear();
            hitboxLabels.Clear();
            hitboxLabelledInstances.Clear();

            foreach (Collider2D col in UnityEngine.Object.FindObjectsOfType<Collider2D>())
            {
                if (!IsSupportedCollider(col)) continue;

                var (color, hatch, show) = ClassifyHitbox(col);
                if (!show) continue;

                // Inset each outline inward by half the line width so the border's OUTER
                // edge lands exactly on the true collider edge (LineRenderer centres width
                // on the path). Most shapes yield ONE path; a CompositeCollider2D (tilemap
                // ground) yields several disjoint merged paths — one LineRenderer each.
                List<Vector3[]> paths = ComputeOutlines(col, width * 0.5f);
                if (paths == null || paths.Count == 0) continue;

                List<LineRenderer> lines = GetOrCreateLines(col, paths.Count);
                if (lines == null) continue;

                for (int i = 0; i < lines.Count; i++)
                {
                    LineRenderer lr = lines[i];
                    Vector3[] pts = paths[i];
                    lr.enabled = pts != null && pts.Length >= 2;
                    if (!lr.enabled) continue;
                    lr.startWidth = lr.endWidth = width;
                    lr.startColor = lr.endColor = color;
                    lr.positionCount = pts.Length;
                    lr.SetPositions(pts);
                }

                // Diagonal hatch fill (only box colliders — the hatched categories are all
                // boxes; others get the base outline only). Inset by half the border width,
                // same as the outline, so the stripe fill sits within the true edge.
                UpdateHatch(col, hatch, width * 0.5f);

                // Spring launch-direction arrow (springs only; cleared for everything else).
                UpdateSpringArrow(col);

                CollectHitboxLabel(col);

                hitboxSeen.Add(col);
            }

            // Drop lines whose collider was destroyed or is no longer drawn.
            hitboxStale.Clear();
            foreach (var kv in hitboxLines)
                if (kv.Key == null || !hitboxSeen.Contains(kv.Key))
                    hitboxStale.Add(kv.Key);

            foreach (var key in hitboxStale)
            {
                if (hitboxLines.TryGetValue(key, out List<LineRenderer> lines))
                    foreach (LineRenderer lr in lines)
                        if (lr != null) Destroy(lr.gameObject);
                hitboxLines.Remove(key);
                DestroyHatch(key);
                DestroyArrow(key);
            }
        }

        private void ClearHitboxOverlay()
        {
            hitboxLines.Clear();
            hitboxHatch.Clear();
            hitboxArrow.Clear();
            hitboxSeen.Clear();
            hitboxStale.Clear();
            hitboxLabels.Clear();
            if (hitboxRoot != null)
            {
                Destroy(hitboxRoot);
                hitboxRoot = null;
            }
            // Material/texture are not parented to the root, so destroy them explicitly.
            if (hitboxHatchMaterial != null) { Destroy(hitboxHatchMaterial); hitboxHatchMaterial = null; }
            if (hitboxHatchTexture != null) { Destroy(hitboxHatchTexture); hitboxHatchTexture = null; }
            if (hitboxArrowMaterial != null) { Destroy(hitboxArrowMaterial); hitboxArrowMaterial = null; }
            if (hitboxArrowTexture != null) { Destroy(hitboxArrowTexture); hitboxArrowTexture = null; }
            if (hitboxMaterial != null) { Destroy(hitboxMaterial); hitboxMaterial = null; }
        }

        private void EnsureHitboxRoot()
        {
            if (hitboxRoot == null)
            {
                hitboxRoot = new GameObject("IGTAS_HitboxOverlay");
                hitboxLines.Clear(); // any cached lines died with the old root
                hitboxHatch.Clear();
                hitboxArrow.Clear();
            }
            EnsureHitboxMaterial();
        }

        // A single shared unlit material; per-line colour comes from the LineRenderer
        // vertex colours (startColor/endColor), which Sprites/Default respects.
        private void EnsureHitboxMaterial()
        {
            if (hitboxMaterial != null) return;

            Shader shader = Shader.Find("Sprites/Default")
                            ?? Shader.Find("Unlit/Color")
                            ?? Shader.Find("Hidden/Internal-Colored");

            hitboxMaterial = new Material(shader) { color = Color.white };

            // Hatch material: same unlit shader, but carries the repeating stripe texture.
            // Per-instance tint is applied via MaterialPropertyBlock (no per-box material
            // copies); the stripe pattern is constant, only the colour varies — which is
            // exactly the existing hatch model (one shape, the sub-colour distinguishes).
            hitboxHatchTexture = MakeStripeTexture();
            hitboxHatchMaterial = new Material(shader)
            {
                color = Color.white,
                mainTexture = hitboxHatchTexture,
            };

            // Arrow material: same model as the hatch — one shared shader carrying a
            // generated chevron texture, tinted per spring via a MaterialPropertyBlock.
            hitboxArrowTexture = MakeArrowTexture();
            hitboxArrowMaterial = new Material(shader)
            {
                color = Color.white,
                mainTexture = hitboxArrowTexture,
            };
        }

        // Generate a small repeating diagonal-stripe texture once. White stripes on
        // transparent, so the fill reads as sparse diagonals (like the old line hatch)
        // rather than a solid wash; the per-instance tint colours the stripes. wrapMode
        // Repeat + bilinear so it tiles seamlessly and is filtered (no 1px shimmer).
        private static Texture2D MakeStripeTexture()
        {
            const int N = 16;          // texel grid
            const int stripeWidth = 3; // texels of stripe per N-period (≈ old 4-diagonal density)
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    // 45° stripes: constant on (x + y) mod N. Stripe where the residue is
                    // within stripeWidth; transparent otherwise.
                    bool on = ((x + y) % N) < stripeWidth;
                    px[y * N + x] = on ? new Color32(255, 255, 255, 255)
                                       : new Color32(255, 255, 255, 0);
                }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // Get the collider's list of outline LineRenderers, grown/shrunk to exactly `count`
        // (composite path counts can change as the tilemap streams in/out). Reuses existing
        // renderers; creates or destroys only the delta.
        private List<LineRenderer> GetOrCreateLines(Collider2D col, int count)
        {
            if (!hitboxLines.TryGetValue(col, out List<LineRenderer> lines) || lines == null)
            {
                lines = new List<LineRenderer>(count);
                hitboxLines[col] = lines;
            }
            // Drop any that died with a scene/root teardown.
            for (int i = lines.Count - 1; i >= 0; i--)
                if (lines[i] == null) lines.RemoveAt(i);

            while (lines.Count < count)
                lines.Add(NewOverlayLine("IGTAS_Hitbox", short.MaxValue));
            while (lines.Count > count)
            {
                LineRenderer lr = lines[lines.Count - 1];
                lines.RemoveAt(lines.Count - 1);
                if (lr != null) Destroy(lr.gameObject);
            }
            return lines;
        }

        // Shared LineRenderer setup for both outlines and hatch fills. `order` lets the
        // hatch draw just under the border so the border stays crisp on top.
        private LineRenderer NewOverlayLine(string name, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(hitboxRoot.transform, false);

            var lr = go.AddComponent<LineRenderer>();
            lr.material = hitboxMaterial;
            lr.useWorldSpace = true;
            lr.loop = false;            // box/polygon outlines are closed by repeating the first point
            lr.numCapVertices = 0;
            lr.numCornerVertices = 0;
            // TransformZ, not View: View billboards each segment to the camera, which on
            // this dead-on top-down 2D view collapses the width to sub-pixel and shimmers.
            lr.alignment = LineAlignment.TransformZ;
            lr.textureMode = LineTextureMode.Stretch;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sortingLayerName = "Default";
            lr.sortingOrder = order;
            return lr;
        }

        // ===== DIAGONAL HATCH FILL (tiled stripe quad) =====
        // World-space size of one stripe-texture tile. Smaller = denser hatch. Constant in
        // world units (not box-relative) so every box shows the same stripe density and a
        // large volume isn't a coarse smear — UVs are derived from world size / this.
        private const float HitboxHatchTileWorld = 24f;
        private static readonly MaterialPropertyBlock hitboxHatchMpb = new();
        private static readonly int HatchColorId = Shader.PropertyToID("_Color");

        // Fill the box interior with a tinted, repeating diagonal-stripe quad just under the
        // border. One MeshRenderer per box (vs. the old 4 LineRenderers + clip math), tinted
        // per-instance via MaterialPropertyBlock. Only BoxCollider2D is hatched (the hatched
        // categories are all boxes); a null hatch or non-box clears any existing fill. The
        // quad is inset to the same true edge as the outline so the stripes don't bleed out.
        private void UpdateHatch(Collider2D col, Color? hatch, float worldInset)
        {
            if (!hatch.HasValue || !(col is BoxCollider2D box))
            {
                DestroyHatch(col);
                return;
            }

            if (!hitboxHatch.TryGetValue(col, out MeshRenderer mr) || mr == null)
            {
                mr = NewHatchQuad();
                hitboxHatch[col] = mr;
            }

            Transform t = box.transform;
            Vector2 s = InsetHalfSize(t, box.size, worldInset);
            Vector2 o = box.offset;

            // Quad corners in the box's local space (CCW), transformed to world like the
            // outline. MeshRenderer needs object-space verts; we author the mesh in world
            // space and leave the quad GameObject at identity, so set verts to world points.
            Mesh mesh = mr.GetComponent<MeshFilter>().sharedMesh;
            mesh.vertices = new[]
            {
                t.TransformPoint(o + new Vector2(-s.x, -s.y)),
                t.TransformPoint(o + new Vector2(-s.x,  s.y)),
                t.TransformPoint(o + new Vector2( s.x,  s.y)),
                t.TransformPoint(o + new Vector2( s.x, -s.y)),
            };
            // UVs tile the stripe at constant WORLD density: span the box's world extent
            // divided by the tile size, so stripe pitch is identical on every box.
            float uW = (s.x * 2f * Mathf.Abs(t.lossyScale.x)) / HitboxHatchTileWorld;
            float uH = (s.y * 2f * Mathf.Abs(t.lossyScale.y)) / HitboxHatchTileWorld;
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, uH),
                new Vector2(uW, uH), new Vector2(uW, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            // Tint via mesh vertex colours — the channel Sprites/Default reads most
            // reliably (its _Color/_RendererColor handling varies); the stripe texture's
            // alpha then masks it to diagonals. MPB _Color is also set as a belt-and-braces
            // for the Unlit/Color fallback shader, which keys off _Color instead.
            Color c = hatch.Value;
            mesh.colors = new[] { c, c, c, c };
            mesh.RecalculateBounds();

            hitboxHatchMpb.SetColor(HatchColorId, c);
            mr.SetPropertyBlock(hitboxHatchMpb);
        }

        // A hatch quad: its own GameObject with a MeshFilter (unique Mesh, since verts are
        // rewritten per refresh) + MeshRenderer sharing the stripe material. sortingOrder
        // one under the border so the outline stays crisp on top.
        private MeshRenderer NewHatchQuad()
        {
            var go = new GameObject("IGTAS_HitboxHatch");
            go.transform.SetParent(hitboxRoot.transform, false);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = new Mesh { name = "IGTAS_HatchQuad" };

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = hitboxHatchMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sortingLayerName = "Default";
            mr.sortingOrder = short.MaxValue - 1;
            return mr;
        }

        private void DestroyHatch(Collider2D col)
        {
            if (col == null) return;
            if (hitboxHatch.TryGetValue(col, out MeshRenderer mr) && mr != null)
            {
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
                Destroy(mr.gameObject);
            }
            hitboxHatch.Remove(col);
        }

        // ===== SPRING LAUNCH-DIRECTION ARROW (oriented textured quad) =====
        // The arrow's world size as a fraction of the spring collider's smaller world
        // extent, clamped to a world min/max so a far-zoom spring still shows a readable
        // arrow and a huge one doesn't overflow the pad. Constant-ish across springs (they
        // are similar scale), like the hatch tile pitch.
        private const float HitboxArrowFill = 0.8f;     // of the pad's smaller extent
        private const float HitboxArrowMinWorld = 24f;  // floor (world units)
        private const float HitboxArrowMaxWorld = 220f; // ceiling
        private static readonly MaterialPropertyBlock hitboxArrowMpb = new();

        // Draw (or refresh) a spring's launch arrow, oriented to its live transform.up.
        // Springs only; any other collider clears an existing arrow. The arrow is a single
        // textured quad whose local +V axis is the launch direction, so it rotates with the
        // pad (the demo's springs face all four cardinals; one faces down). Tinted pink.
        private void UpdateSpringArrow(Collider2D col)
        {
            // Only the spring's OWN collider draws the arrow (the script sits on the same
            // GameObject as the pad collider — see the label code).
            var spring = col.GetComponent("SpringScript") as MonoBehaviour;
            if (spring == null)
            {
                DestroyArrow(col);
                return;
            }

            if (!hitboxArrow.TryGetValue(col, out MeshRenderer mr) || mr == null)
            {
                mr = NewArrowQuad();
                hitboxArrow[col] = mr;
            }

            Transform t = spring.transform;
            Vector3 up = t.up;                                   // launch direction (world)
            Vector3 right = new Vector3(up.y, -up.x, 0f);        // perpendicular in XY
            Vector3 center = col.bounds.center;

            // Half-size from the collider's smaller world extent, clamped. Square quad so the
            // chevron texture isn't stretched regardless of how oblong the pad is.
            Vector3 ext = col.bounds.extents;
            float half = Mathf.Clamp(Mathf.Min(ext.x, ext.y) * HitboxArrowFill,
                                     HitboxArrowMinWorld * 0.5f, HitboxArrowMaxWorld * 0.5f);

            // Quad corners (CCW): texture V runs along +up (the arrow points that way),
            // texture U along +right.
            Mesh mesh = mr.GetComponent<MeshFilter>().sharedMesh;
            mesh.vertices = new[]
            {
                center - right * half - up * half, // (u0,v0)
                center - right * half + up * half, // (u0,v1)
                center + right * half + up * half, // (u1,v1)
                center + right * half - up * half, // (u1,v0)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(1f, 1f), new Vector2(1f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            Color c = ColSpring;
            mesh.colors = new[] { c, c, c, c };
            mesh.RecalculateBounds();

            hitboxArrowMpb.SetColor(HatchColorId, c);
            mr.SetPropertyBlock(hitboxArrowMpb);
        }

        private MeshRenderer NewArrowQuad()
        {
            var go = new GameObject("IGTAS_HitboxSpringArrow");
            go.transform.SetParent(hitboxRoot.transform, false);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = new Mesh { name = "IGTAS_SpringArrow" };

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = hitboxArrowMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sortingLayerName = "Default";
            // Same top sort order as the outline (short.MaxValue is the ceiling) — both sit
            // above all game sprites; the arrow being opaque pink over its own pad outline
            // reads cleanly regardless of the tie-break order. Confirmed in-game.
            mr.sortingOrder = short.MaxValue;
            return mr;
        }

        private void DestroyArrow(Collider2D col)
        {
            if (col == null) return;
            if (hitboxArrow.TryGetValue(col, out MeshRenderer mr) && mr != null)
            {
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
                Destroy(mr.gameObject);
            }
            hitboxArrow.Remove(col);
        }

        // Generate an upward-pointing arrow texture once (white on transparent; the per-
        // spring tint colours it). Drawn in UV space with +V = up: a triangular head over
        // the top portion and a rectangular shaft below it, centred on U. Bilinear-filtered
        // + clamped so it stays crisp at any zoom and doesn't tile.
        private static Texture2D MakeArrowTexture()
        {
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[N * N];
            var clear = new Color32(255, 255, 255, 0);
            var solid = new Color32(255, 255, 255, 255);

            // Layout (v from 0 bottom to 1 top): shaft in [0.10,0.58], head in [0.58,0.92].
            // Head is a triangle narrowing to a point at the top; shaft is a centred bar.
            const float headBase = 0.58f, headTop = 0.92f;
            const float shaftBot = 0.10f, shaftHalf = 0.16f, headHalf = 0.34f;
            for (int y = 0; y < N; y++)
            {
                float v = (y + 0.5f) / N;
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N - 0.5f; // centred, [-0.5,0.5]
                    bool on = false;
                    if (v >= shaftBot && v < headBase)
                        on = Mathf.Abs(u) <= shaftHalf;
                    else if (v >= headBase && v <= headTop)
                    {
                        float k = (headTop - v) / (headTop - headBase); // 1 at base, 0 at tip
                        on = Mathf.Abs(u) <= headHalf * k;
                    }
                    px[y * N + x] = on ? solid : clear;
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // ===== CLASSIFICATION =====
        private static bool IsSupportedCollider(Collider2D col)
        {
            return col is BoxCollider2D
                || col is CircleCollider2D
                || col is PolygonCollider2D
                || col is EdgeCollider2D
                || col is CapsuleCollider2D
                || col is CompositeCollider2D   // tilemap ground (merged paths)
                || col is TilemapCollider2D;     // standalone tilemap (bounds fallback)
        }

        // Returns the base outline colour, an optional diagonal-hatch sub-colour (null =
        // no hatch), and whether to show the collider at all.
        private (Color color, Color? hatch, bool show) ClassifyHitbox(Collider2D col)
        {
            GameObject go = col.gameObject;

            // Player first — identified by tag, or by carrying the Movement component.
            if (go.CompareTag("Player") || HasComponentNamed(go, "Movement"))
                return (ColPlayer, null, true);

            // Gates: one teal base, start vs end told apart by the hatch (green/gold)
            // instead of two separate top-level colours.
            if (HasComponentNamed(go, "startGate") || HasComponentOnParent(go, "startGate"))
                return (ColGate, ColHatchGateStart, true);
            if (HasComponentNamed(go, "endGate") || HasComponentOnParent(go, "endGate"))
                return (ColGate, ColHatchGateEnd, true);

            // Long-fall volumes carry per-instance state: an ACTIVE one (cutsceneMode
            // longfall) raises fall terminal velocity by longFallMult; an INERT one
            // (cutsceneMode none, mult 0) only forces the player back to the 'none'
            // state on contact (cancels a spring/cutscene mid-air). Same burnt-orange
            // base, told apart by the hatch (read from the live instance).
            MonoBehaviour longFall = GetComponentNamedOnSelfOrParent(go, "longFallColliderController");
            if (longFall != null)
                return (ColLongFall,
                        LongFallIsActive(longFall) ? ColHatchLongFallActive : ColHatchLongFallReset,
                        true);

            // Upgrade boxes: orange base + the per-instance target label distinguishes the
            // 18 upgrades, but two CATEGORIES warrant a glance-level hatch warning. A box
            // that radically reshapes the game — trips the breaker (the lights-off event) or
            // ends the demo — gets a RED hatch; a box that grants a movement ability gets a
            // BLUE hatch. Read from the live instance (tripBreaker [SerializeField] bool +
            // the movement target), same reflection pattern as springs/long-fall.
            MonoBehaviour upgradeBox = GetComponentNamedOnSelfOrParent(go, "upgradeBox");
            if (upgradeBox != null)
                return (ColButton, UpgradeBoxHatch(upgradeBox), true);

            // Owning gameplay script (self or parent).
            Color? scripted = MatchScriptCategory(go);
            if (scripted.HasValue)
                return (scripted.Value, null, true);

            // Landable terrain = on the "Ground" LAYER (what Movement's raycasts test),
            // which is broader than the "Ground" TAG. The tag only sub-classifies after a
            // hit: tagged => solid (metal) ground; on-layer-but-untagged => mossy. Both are
            // genuine ground (same base green); the hatch (grey vs green) says which. The
            // two differ only cosmetically today — see docs.
            if (GroundLayer >= 0 && go.layer == GroundLayer)
                return go.CompareTag("Ground")
                    ? (ColGround, ColHatchGroundMetal, true)
                    : (ColGround, ColHatchGroundMoss, true);

            // Fallback: a solid collider NOT on the Ground layer looks solid but is
            // PASSABLE (you fall through). Kept as its OWN white base + a black hatch
            // rather than folded under the ground base — the whole point is that it must
            // NOT read as ground at a glance (it's a fake floor). The hatch reinforces it.
            if (!col.isTrigger)
                return (ColSolidDefault, ColHatchPassable, true);

            // Unclassified trigger volume.
            return (ColTriggerDefault, null, true);
        }

        private static bool HasComponentOnParent(GameObject go, string name)
        {
            Transform p = go.transform.parent;
            return p != null && p.GetComponent(name) != null;
        }

        // The game's solid layer, resolved once. Layer indices are fixed at build time
        // (unlike Camera.main, which can change across scenes), so caching is safe.
        private static readonly int GroundLayer = LayerMask.NameToLayer("Ground");

        private static Color? MatchScriptCategory(GameObject go)
        {
            Transform parent = go.transform.parent;
            foreach (var (script, color) in scriptCategories)
            {
                if (go.GetComponent(script) != null) return color;
                if (parent != null && parent.GetComponent(script) != null) return color;
            }
            return null;
        }

        private static bool HasComponentNamed(GameObject go, string name)
            => go.GetComponent(name) != null;

        // Like MatchScriptCategory's lookup, but returns the component instance (self or
        // parent) so its per-instance fields can be read. Mirrors the self-then-parent
        // order the category table uses.
        private static MonoBehaviour GetComponentNamedOnSelfOrParent(GameObject go, string name)
        {
            var c = go.GetComponent(name) as MonoBehaviour;
            if (c != null) return c;
            Transform parent = go.transform.parent;
            return parent != null ? parent.GetComponent(name) as MonoBehaviour : null;
        }

        // ===== PER-INSTANCE DATA LABELS =====
        // Queue any per-instance value worth showing next to a collider. Each adds a
        // world-space anchor + the collider's world-space bounds size (so the renderer
        // can size the text to the box). The north-star driver: where a colour alone
        // can't tell two instances apart — a spring's strength, a long-fall's multiplier,
        // *which* upgrade a box sells — the label supplies the missing fact. Generic by
        // design; add cases as the mechanic audit surfaces them (see docs).
        private void CollectHitboxLabel(Collider2D col)
        {
            Vector3 center = col.bounds.center;
            Vector2 size = col.bounds.size;

            MonoBehaviour spring = col.GetComponent("SpringScript") as MonoBehaviour;
            if (spring != null && FirstLabelFor(spring))
            {
                // All three [SerializeField] floats are route-relevant: strength + upForce
                // size the launch (hitSpring: Velocity.y = upForce*9 + up.y*strength*3, so
                // upForce*9 dominates), and movementLock is how long air-steering is locked
                // after launch (Invoke cancelSpringCutscene, movementLock). Stacked one per
                // line. (The 4th field, anim, is purely visual — skipped.)
                EnsureSpringFields(spring);
                float? strength = ReadSpringField(springStrengthField, spring);
                float? upForce = ReadSpringField(springUpForceField, spring);
                float? moveLock = ReadSpringField(springMoveLockField, spring);
                if (strength.HasValue || upForce.HasValue || moveLock.HasValue)
                {
                    string s = strength.HasValue ? strength.Value.ToString("0.###") : "?";
                    string u = upForce.HasValue ? upForce.Value.ToString("0.###") : "?";
                    string l = moveLock.HasValue ? moveLock.Value.ToString("0.###") : "?";
                    hitboxLabels.Add((center, size, "s" + s + "\nu" + u + "\nlock" + l, ColSpring));
                }
            }

            // Long-fall: active ones show their fall multiplier (varies 1.1 vs 2.2);
            // inert ones (mult 0) show "reset" — they only force cutsceneMode->none.
            MonoBehaviour longFall = GetComponentNamedOnSelfOrParent(col.gameObject, "longFallColliderController");
            if (longFall != null && FirstLabelFor(longFall))
            {
                // Label text carries the variant ("2.2x fall" vs "reset"); the border +
                // hatch carry the colour, so both labels use the readable base orange.
                if (LongFallIsActive(longFall))
                {
                    float? mult = ReadLongFallMult(longFall);
                    if (mult.HasValue)
                        hitboxLabels.Add((center, size, mult.Value.ToString("0.###") + "x fall", ColLongFall));
                }
                else
                {
                    hitboxLabels.Add((center, size, "reset", ColLongFall));
                }
            }

            // Upgrade boxes: all share one colour but sell 18 distinct things (the 6
            // movement-ability unlocks are route-critical). Label each with WHAT it sells
            // — the static scene target (cost numbers are runtime/save-dependent, left off).
            // Each box GameObject has TWO colliders: a tall solid body (95x176) and a thin
            // trigger strip (80x4) at the base — the activation pad. Anchor the label on
            // the BODY, not whichever collider the sweep reached first (the strip would
            // drop the text at the box's bottom edge — the "strange placement" seen).
            MonoBehaviour upgrade = GetComponentNamedOnSelfOrParent(col.gameObject, "upgradeBox");
            if (upgrade != null && FirstLabelFor(upgrade))
            {
                string target = ReadUpgradeTarget(upgrade);
                if (!string.IsNullOrEmpty(target))
                {
                    Bounds anchor = LargestColliderBounds(upgrade.gameObject, col);
                    hitboxLabels.Add((anchor.center, anchor.size, target, ColButton));
                }
            }
        }

        // Pick the largest-area Collider2D's bounds on a GameObject (fallback: the
        // collider that triggered the call). Used to anchor a box's label on its solid
        // body rather than its small activation-trigger strip.
        private static Bounds LargestColliderBounds(GameObject go, Collider2D fallback)
        {
            Collider2D best = fallback;
            float bestArea = -1f;
            foreach (Collider2D c in go.GetComponents<Collider2D>())
            {
                Vector3 s = c.bounds.size;
                float area = s.x * s.y;
                if (area > bestArea) { bestArea = area; best = c; }
            }
            return best != null ? best.bounds : fallback.bounds;
        }

        // True the first time a given script instance is labelled this sweep; false after.
        // A box can own more than one collider, so without this its label would stack.
        private bool FirstLabelFor(UnityEngine.Object instance)
            => hitboxLabelledInstances.Add(instance);

        // Resolve both SpringScript [SerializeField] float FieldInfos once, off the first
        // spring seen. Either may stay null if the game renames the field (harmless — the
        // label prints "?" for the missing value).
        private void EnsureSpringFields(MonoBehaviour spring)
        {
            if (springFieldsResolved) return;
            Type t = spring.GetType();
            springStrengthField = t.GetField("strength",     BindingFlags.NonPublic | BindingFlags.Instance);
            springUpForceField  = t.GetField("upForce",      BindingFlags.NonPublic | BindingFlags.Instance);
            springMoveLockField = t.GetField("movementLock", BindingFlags.NonPublic | BindingFlags.Instance);
            springFieldsResolved = true;
        }

        private static float? ReadSpringField(FieldInfo field, MonoBehaviour spring)
        {
            if (field == null) return null;
            try { return (float)field.GetValue(spring); }
            catch { return null; }
        }

        // longFallColliderController.NewCutsceneMode (enum, [SerializeField]) +
        // longFallMult (float). An instance is "active" iff it writes the longfall mode
        // (which actually changes fall physics); a 'none'-mode instance is inert (only a
        // state reset). Resolve both FieldInfos once off the first instance seen.
        private void EnsureLongFallFields(MonoBehaviour lf)
        {
            if (longFallLookupDone) return;
            Type t = lf.GetType();
            longFallMultField = t.GetField("longFallMult", BindingFlags.NonPublic | BindingFlags.Instance);
            longFallModeField = t.GetField("NewCutsceneMode", BindingFlags.NonPublic | BindingFlags.Instance);
            longFallLookupDone = true;
        }

        private bool LongFallIsActive(MonoBehaviour lf)
        {
            EnsureLongFallFields(lf);
            // Prefer the enum (the mode it writes is what determines behaviour); fall
            // back to mult>0 if the field shape ever changes. cutsceneModes.longfall == 3.
            if (longFallModeField != null)
            {
                try { return Convert.ToInt32(longFallModeField.GetValue(lf)) == 3; }
                catch { }
            }
            float? m = ReadLongFallMult(lf);
            return m.HasValue && m.Value > 0f;
        }

        private float? ReadLongFallMult(MonoBehaviour lf)
        {
            EnsureLongFallFields(lf);
            if (longFallMultField == null) return null;
            try { return (float)longFallMultField.GetValue(lf); }
            catch { return null; }
        }

        // upgradeBox sells one of 18 distinct things; the static target is reliable
        // (only the cost is runtime/save-dependent). The `upgrade` field discriminates:
        // 0 => GLOBAL (read globalUpgrade), 1 => Movement (read movementUpgrade), any
        // other value IS itself a localUpgradeSet value. All three fields are enum-typed in
        // the game (localUpgradeSet / globalUpgradeSet / movementUpgrades), so the member name
        // is read straight off the game's own enum (EnumNameOf) rather than mirrored in a hand-
        // kept table — the game is the single source of truth, immune to it reordering/inserting
        // members on an update. (The discriminator values GLOBAL=0/Movement=1 are themselves the
        // first two localUpgradeSet members.)
        private FieldInfo upgradeKindField, upgradeGlobalField, upgradeMoveField;
        private bool upgradeFieldsLookupDone;
        // tripBreaker is a private [SerializeField] bool on upgradeBox (true => buying this
        // box trips the lights-off breaker event). Resolved lazily off the first box seen.
        private FieldInfo upgradeTripBreakerField;
        private bool upgradeTripBreakerLookupDone;

        // The hatch warning for an upgrade box: RED for a box that radically reshapes the
        // game (trips the breaker, or the endDemo movement target), BLUE for a box that
        // grants any movement ability, else no hatch. (Movement-kind targets other than
        // endDemo are all ability grants: dash/wallJump/doubleJump/swapBlocksOnce/
        // unlockBlockSwap.) Reflection-read, so a renamed field just yields no hatch.
        private Color? UpgradeBoxHatch(MonoBehaviour box)
        {
            if (ReadTripBreaker(box))
                return ColHatchUpgradeBreaker;

            string target = ReadUpgradeTarget(box);
            if (target == "endDemo")
                return ColHatchUpgradeBreaker;
            if (IsMovementUpgrade(box))   // any other Movement-kind box = an ability grant
                return ColHatchUpgradeAbility;
            return null;
        }

        // True iff the box's `upgrade` kind == 1 (Movement). Cheap reuse of the already-
        // resolved kind field; avoids string-comparing every ability name.
        private bool IsMovementUpgrade(MonoBehaviour box)
        {
            if (!upgradeFieldsLookupDone) ReadUpgradeTarget(box); // resolves the field
            if (upgradeKindField == null) return false;
            try { return Convert.ToInt32(upgradeKindField.GetValue(box)) == 1; }
            catch { return false; }
        }

        private bool ReadTripBreaker(MonoBehaviour box)
        {
            if (!upgradeTripBreakerLookupDone)
            {
                upgradeTripBreakerField = box.GetType().GetField(
                    "tripBreaker", BindingFlags.NonPublic | BindingFlags.Instance);
                upgradeTripBreakerLookupDone = true;
            }
            if (upgradeTripBreakerField == null) return false;
            try { return (bool)upgradeTripBreakerField.GetValue(box); }
            catch { return false; }
        }

        private string ReadUpgradeTarget(MonoBehaviour box)
        {
            if (!upgradeFieldsLookupDone)
            {
                Type t = box.GetType();
                // Public fields on upgradeBox (not [SerializeField]-private).
                upgradeKindField   = t.GetField("upgrade",         BindingFlags.Public | BindingFlags.Instance);
                upgradeGlobalField = t.GetField("globalUpgrade",   BindingFlags.Public | BindingFlags.Instance);
                upgradeMoveField   = t.GetField("movementUpgrade", BindingFlags.Public | BindingFlags.Instance);
                upgradeFieldsLookupDone = true;
            }
            if (upgradeKindField == null) return null;
            try
            {
                int kind = Convert.ToInt32(upgradeKindField.GetValue(box));
                if (kind == 0 && upgradeGlobalField != null)
                    return EnumNameOf(upgradeGlobalField, box);
                if (kind == 1 && upgradeMoveField != null)
                    return EnumNameOf(upgradeMoveField, box);
                return EnumNameOf(upgradeKindField, box);
            }
            catch { return null; }
        }

        // The enum member name of an enum-typed field on box, read off the game's own enum so it
        // can't drift from the runtime. A value outside the enum yields "?N" rather than throwing.
        private static string EnumNameOf(FieldInfo enumField, MonoBehaviour box)
        {
            object v = enumField.GetValue(box);
            Type t = enumField.FieldType;
            if (t.IsEnum && Enum.IsDefined(t, v)) return Enum.GetName(t, v);
            return "?" + Convert.ToInt32(v);
        }

        // Pixels -> world units at the current zoom: an ortho camera shows 2*orthoSize
        // world units of height, so one pixel == 2*orthoSize / Screen.height. Pixel width
        // (not world width) keeps the outline a stable thickness as the camera zoom lerps;
        // see docs. Camera.main is re-resolved each call, NOT cached — it can be recreated
        // across scenes, and the lookup is trivial next to the per-refresh collider sweep.
        private static float WorldWidthForPixels(float pixels)
        {
            Camera cam = Camera.main;
            if (cam == null || !cam.orthographic || Screen.height <= 0)
                return pixels;
            return pixels * (cam.orthographicSize * 2f / Screen.height);
        }

        // ===== OUTLINE GEOMETRY =====
        // `worldInset` shrinks the drawn outline inward by that many WORLD units (passed
        // half the line width) so the border's OUTER edge sits on the true collider edge.
        // The LineRenderer centres its width on the path, so without the inset every shape
        // reads ~½-width larger than the real hitbox and the border bleeds OUTWARD past it.
        // Boxes/capsules inset their local half-size; the circle shrinks its (world) radius;
        // closed polygon paths offset each vertex inward along the angle bisector. Edge
        // colliders are an open polyline with no interior, so they can't be insetted and
        // draw on-edge (none exist on gameplay geometry today).
        // Returns one or more closed/open world-space outlines for the collider. All but
        // CompositeCollider2D yield a single path; the composite (tilemap ground) yields one
        // per merged region.
        private static List<Vector3[]> ComputeOutlines(Collider2D col, float worldInset)
        {
            switch (col)
            {
                case BoxCollider2D b:       return One(BoxOutline(b, worldInset));
                case CircleCollider2D c:    return One(CircleOutline(c, worldInset));
                case PolygonCollider2D p:   return One(InsetClosedPath(PathOutline(p.transform, p.points, p.offset, true), worldInset));
                case EdgeCollider2D e:      return One(PathOutline(e.transform, e.points, e.offset, false));
                case CapsuleCollider2D cap: return One(CapsuleOutline(cap, worldInset)); // box approximation
                case CompositeCollider2D comp: return CompositeOutlines(comp, worldInset);
                // A composited TilemapCollider2D contributes its geometry to a sibling
                // CompositeCollider2D (drawn above) — skip it to avoid double-draw. Only a
                // standalone (non-composited) tilemap falls back to its bounds box.
                case TilemapCollider2D tm: return tm.usedByComposite ? null : TilemapBoundsOutline(tm, worldInset);
                default:                    return null;
            }
        }

        private static List<Vector3[]> One(Vector3[] path)
            => path == null ? null : new List<Vector3[]> { path };

        // Offset a CLOSED world-space outline (last point == first) inward by `worldInset`
        // along each vertex's angle bisector, so its outer edge lands on the true collider
        // edge — the polygon analogue of the box inset. Winding is detected from the signed
        // area so the offset goes inward regardless of vertex order; a vertex whose bisector
        // collapses (near-180° spike) is left in place. Exact for convex spans (the bulk of
        // tilemap ground), visually clean at a 1px inset on concave corners.
        private static Vector3[] InsetClosedPath(Vector3[] closed, float worldInset)
        {
            if (closed == null || closed.Length < 4 || worldInset <= 0f) return closed;
            int n = closed.Length - 1; // last == first

            // Signed area (shoelace) → winding sign. CCW (>0) wants the inward normal on the
            // left of each edge; CW (<0) the right. We fold that into `side`.
            float area2 = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = closed[i], b = closed[i + 1];
                area2 += a.x * b.y - b.x * a.y;
            }
            float side = area2 >= 0f ? 1f : -1f;

            var outp = new Vector3[closed.Length];
            for (int i = 0; i < n; i++)
            {
                Vector3 prev = closed[(i - 1 + n) % n];
                Vector3 cur = closed[i];
                Vector3 next = closed[(i + 1) % n];

                Vector2 ePrev = new Vector2(cur.x - prev.x, cur.y - prev.y);
                Vector2 eNext = new Vector2(next.x - cur.x, next.y - cur.y);
                if (ePrev.sqrMagnitude < 1e-12f || eNext.sqrMagnitude < 1e-12f)
                {
                    outp[i] = cur;
                    continue;
                }
                ePrev.Normalize(); eNext.Normalize();
                // Inward normal of an edge (dir d) is side*(-d.y, d.x); the vertex bisector
                // is the sum of the two adjacent inward normals.
                Vector2 nPrev = side * new Vector2(-ePrev.y, ePrev.x);
                Vector2 nNext = side * new Vector2(-eNext.y, eNext.x);
                Vector2 bis = nPrev + nNext;
                float len = bis.magnitude;
                if (len < 1e-4f) { outp[i] = cur; continue; } // collinear spike — leave put
                bis /= len;
                // Miter length: inset / cos(half-angle); cos = dot(bisector, edge normal).
                float cosHalf = Mathf.Max(0.2f, Vector2.Dot(bis, nNext)); // clamp avoids blow-up
                float d = worldInset / cosHalf;
                outp[i] = new Vector3(cur.x + bis.x * d, cur.y + bis.y * d, cur.z);
            }
            outp[n] = outp[0]; // re-close
            return outp;
        }

        // Convert a world-space inset to the box's local half-size space (divide by the
        // per-axis world scale), clamped so a thick border on a tiny box collapses to the
        // centre rather than inverting. Returns the inset local half-extents.
        private static Vector2 InsetHalfSize(Transform t, Vector2 localSize, float worldInset)
        {
            Vector3 ls = t.lossyScale;
            float ix = Mathf.Abs(ls.x) > 1e-6f ? worldInset / Mathf.Abs(ls.x) : 0f;
            float iy = Mathf.Abs(ls.y) > 1e-6f ? worldInset / Mathf.Abs(ls.y) : 0f;
            float hx = Mathf.Max(0f, localSize.x * 0.5f - ix);
            float hy = Mathf.Max(0f, localSize.y * 0.5f - iy);
            return new Vector2(hx, hy);
        }

        private static Vector3[] BoxOutline(BoxCollider2D b, float worldInset)
        {
            Transform t = b.transform;
            Vector2 s = InsetHalfSize(t, b.size, worldInset);
            Vector2 o = b.offset;
            return new[]
            {
                t.TransformPoint(o + new Vector2(-s.x, -s.y)),
                t.TransformPoint(o + new Vector2(-s.x,  s.y)),
                t.TransformPoint(o + new Vector2( s.x,  s.y)),
                t.TransformPoint(o + new Vector2( s.x, -s.y)),
                t.TransformPoint(o + new Vector2(-s.x, -s.y)),
            };
        }

        private static Vector3[] CircleOutline(CircleCollider2D c, float worldInset)
        {
            Transform t = c.transform;
            Vector3 scale = t.lossyScale;
            // Radius is already in world units after the scale; shrink it by the inset so
            // the border's outer edge lands on the true circle, clamped to a hair > 0.
            float r = Mathf.Max(1e-3f, c.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)) - worldInset);
            Vector3 center = t.TransformPoint(c.offset);

            var pts = new Vector3[HitboxCircleSegments + 1];
            for (int i = 0; i <= HitboxCircleSegments; i++)
            {
                float a = (i / (float)HitboxCircleSegments) * Mathf.PI * 2f;
                pts[i] = center + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f);
            }
            return pts;
        }

        private static Vector3[] PathOutline(Transform t, Vector2[] localPoints, Vector2 offset, bool close)
        {
            if (localPoints == null || localPoints.Length < 2) return null;

            int n = localPoints.Length + (close ? 1 : 0);
            var pts = new Vector3[n];
            for (int i = 0; i < localPoints.Length; i++)
                pts[i] = t.TransformPoint(localPoints[i] + offset);
            if (close)
                pts[localPoints.Length] = pts[0];
            return pts;
        }

        // Tilemap ground is a CompositeCollider2D: many disjoint MERGED outlines (one per
        // contiguous solid region). Each GetPath returns a closed polygon in the composite
        // transform's local space; we transform to world, close it, and inset inward like
        // any polygon so the border's outer edge lands on the true edge. This is the
        // real-geometry path; the raw TilemapCollider2D (when not composited) falls back to
        // a coarse bounds box in TilemapBoundsOutline.
        private static List<Vector3[]> CompositeOutlines(CompositeCollider2D comp, float worldInset)
        {
            Transform t = comp.transform;
            int paths = comp.pathCount;
            if (paths <= 0) return null;

            var outlines = new List<Vector3[]>(paths);
            for (int p = 0; p < paths; p++)
            {
                int n = comp.GetPathPointCount(p);
                if (n < 2) continue;
                var local = new Vector2[n];
                comp.GetPath(p, local);

                var world = new Vector3[n + 1]; // +1 to close
                for (int i = 0; i < n; i++)
                    world[i] = t.TransformPoint(local[i]);
                world[n] = world[0];

                outlines.Add(InsetClosedPath(world, worldInset));
            }
            return outlines.Count > 0 ? outlines : null;
        }

        // A TilemapCollider2D NOT backed by a composite has no clean path API; outline its
        // overall world bounds as a single box. Coarse (it wraps the whole tilemap, not each
        // tile), but the demo's ground tilemaps are composite-backed so this is the rarely-
        // hit fallback — better a visible bound than nothing.
        private static List<Vector3[]> TilemapBoundsOutline(Collider2D col, float worldInset)
        {
            Bounds b = col.bounds;
            float hx = Mathf.Max(0f, b.extents.x - worldInset);
            float hy = Mathf.Max(0f, b.extents.y - worldInset);
            Vector3 c = b.center;
            return One(new[]
            {
                new Vector3(c.x - hx, c.y - hy, c.z),
                new Vector3(c.x - hx, c.y + hy, c.z),
                new Vector3(c.x + hx, c.y + hy, c.z),
                new Vector3(c.x + hx, c.y - hy, c.z),
                new Vector3(c.x - hx, c.y - hy, c.z),
            });
        }

        // Capsule rendered as its bounding box — good enough for a diagnostic, and
        // capsule colliders are not expected on platforming geometry in this game.
        private static Vector3[] CapsuleOutline(CapsuleCollider2D cap, float worldInset)
        {
            Transform t = cap.transform;
            Vector2 s = InsetHalfSize(t, cap.size, worldInset);
            Vector2 o = cap.offset;
            return new[]
            {
                t.TransformPoint(o + new Vector2(-s.x, -s.y)),
                t.TransformPoint(o + new Vector2(-s.x,  s.y)),
                t.TransformPoint(o + new Vector2( s.x,  s.y)),
                t.TransformPoint(o + new Vector2( s.x, -s.y)),
                t.TransformPoint(o + new Vector2(-s.x, -s.y)),
            };
        }

        // ===== LEGEND (drawn from OnGUI when the overlay is on) =====
        private void DrawHitboxLegend()
        {
            hitboxLegendStyle ??= new GUIStyle
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(4, 4, 1, 1),
            };

            const int rowH = 16;
            const int w = 150;
            int h = hitboxLegend.Length * rowH + 24;
            int x = Screen.width - w - 10;
            int y = 40;

            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(x - 4, y - 4, w + 8, h + 8), Texture2D.whiteTexture);
            GUI.color = Color.white;

            hitboxLegendStyle.normal.textColor = new Color(1f, 0.85f, 0.3f);
            GUI.Label(new Rect(x, y, w, rowH), "HITBOXES (F4)", hitboxLegendStyle);
            y += rowH + 6;

            foreach (var (label, color, hatch) in hitboxLegend)
            {
                var swatch = new Rect(x, y + 3, 12, 10);
                GUI.color = color;
                GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                // Hatch sub-colour shown as a diagonal slash across the swatch.
                if (hatch.HasValue)
                {
                    GUI.color = hatch.Value;
                    GUI.DrawTexture(new Rect(swatch.x + 3, swatch.y, 3, swatch.height), Texture2D.whiteTexture);
                }
                GUI.color = Color.white;

                hitboxLegendStyle.normal.textColor = color;
                GUI.Label(new Rect(x + 18, y, w - 18, rowH), label, hitboxLegendStyle);
                y += rowH;
            }
        }

        // Label font-size bounds (px). Labels scale with the collider's on-screen size so
        // a big box (a 400u long-fall volume, a tall spring) gets big readable text and a
        // small box stays legible without overflowing wildly. Clamped both ends.
        private const int HitboxLabelMinPx = 11;
        private const int HitboxLabelMaxPx = 28;
        // 8-direction black outline offsets (px) — drawn under the coloured text so it
        // reads on any background, not just the cheap single drop-shadow it replaced.
        private static readonly Vector2[] LabelOutlineOffsets =
        {
            new Vector2(-1,-1), new Vector2(0,-1), new Vector2(1,-1),
            new Vector2(-1, 0),                    new Vector2(1, 0),
            new Vector2(-1, 1), new Vector2(0, 1), new Vector2(1, 1),
        };

        // ===== PER-INSTANCE LABELS (drawn from OnGUI when the overlay is on) =====
        // Project each collected world-space label to screen and draw it, sized to the
        // collider's on-screen footprint with a black outline for legibility. Collected
        // in the Update-rate refresh sweep (CollectHitboxLabel); drawn here every OnGUI.
        private void DrawHitboxLabels()
        {
            if (hitboxLabels.Count == 0) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            hitboxLabelStyle ??= new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
            };

            // World->screen pixel scale for the size projection: an ortho camera maps
            // 2*orthoSize world units to Screen.height pixels. (Falls back to a fixed
            // mid size if the camera isn't orthographic.)
            float pxPerWorld = (cam.orthographic && cam.orthographicSize > 0f)
                ? Screen.height / (cam.orthographicSize * 2f) : 0f;

            foreach (var (world, worldSize, text, color) in hitboxLabels)
            {
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z < 0f) continue; // behind the camera

                // Font size from the box's on-screen height: fill ~45% of the box so the
                // text sits comfortably inside it, clamped to the readable px range. Tiny
                // far-zoom boxes still get the minimum; huge near boxes the maximum.
                int fontPx = HitboxLabelMinPx;
                if (pxPerWorld > 0f)
                {
                    float boxPxH = worldSize.y * pxPerWorld;
                    fontPx = Mathf.Clamp(Mathf.RoundToInt(boxPxH * 0.45f),
                                         HitboxLabelMinPx, HitboxLabelMaxPx);
                }
                hitboxLabelStyle.fontSize = fontPx;

                // WorldToScreenPoint is bottom-left origin; GUI is top-left -> flip Y.
                float gx = sp.x;
                float gy = Screen.height - sp.y;
                // Multi-line labels (e.g. the spring's s/u/lock stack): size width from the
                // LONGEST line (not total length, which would over-pad across the newlines)
                // and height from the line count so MiddleCenter doesn't clip the stack.
                int lineCount = 1, longest = 0, run = 0;
                foreach (char ch in text)
                {
                    if (ch == '\n') { lineCount++; if (run > longest) longest = run; run = 0; }
                    else run++;
                }
                if (run > longest) longest = run;
                float w = Mathf.Max(80f, longest * fontPx * 0.62f);
                float h = fontPx * (lineCount + 1f); // one font-height of vertical padding
                var rect = new Rect(gx - w * 0.5f, gy - h * 0.5f, w, h);

                // Black outline (8 directions) under the coloured fill.
                hitboxLabelStyle.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
                foreach (Vector2 off in LabelOutlineOffsets)
                    GUI.Label(new Rect(rect.x + off.x, rect.y + off.y, rect.width, rect.height),
                              text, hitboxLabelStyle);

                hitboxLabelStyle.normal.textColor = color;
                GUI.Label(rect, text, hitboxLabelStyle);
            }
        }
    }
}
