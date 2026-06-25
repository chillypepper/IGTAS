using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IGTAS
{
    // Debug-only free-play tools. Deliberately fork game behaviour, so callers
    // not guaranteed by determinism setup. See docs/README.md "Controls".
    public partial class Plugin
    {
        // NO-CLIP (F3) — fly with WASD, ignoring collision and gravity.
        private const float NoclipSpeed = 1000f;      // units/sec at full WASD
        private const float NoclipBoostMult = 4f;     // Shift-key multiplier

        private bool noclipEnabled;
        private bool savedBodySimulated;

        // Camera snaps to player in no-clip to avoid player being off screen.
        private MonoBehaviour cameraMoverComp;
        private bool savedCameraMoverEnabled;

        // F3: toggle no-clip.
        private void ToggleNoclip()
        {
            if (noclipEnabled) DisableNoclip();
            else EnableNoclip();
        }

        // Take over body physics and the camera.
        private void EnableNoclip()
        {
            if (noclipEnabled) return;
            if (movementComp == null || bodyField == null)
            {
                Logger.LogWarning("No-clip: movement component not ready.");
                return;
            }

            var body = bodyField.GetValue(movementComp) as Rigidbody2D;
            if (body == null) { Logger.LogWarning("No-clip: no Rigidbody2D."); return; }

            savedBodySimulated = body.simulated;
            body.velocity = Vector2.zero;
            body.simulated = false; // game physics no longer owns the body

            // Resolve (once) + disable cameraMover so we can drive the camera at flight speed.
            if (cameraMoverComp == null)
            {
                var ct = GetTypeByName("cameraMover");
                if (ct != null) cameraMoverComp = FindObjectOfType(ct) as MonoBehaviour;
            }

            if (cameraMoverComp != null)
            {
                savedCameraMoverEnabled = cameraMoverComp.enabled;
                cameraMoverComp.enabled = false;
            }
            else Logger.LogWarning("No-clip: cameraMover not found; camera won't be centred.");

            noclipEnabled = true;
            CenterCameraOnPlayer();
            Logger.LogInfo("No-clip enabled.");
        }

        // Restore body physics + the camera.
        private void DisableNoclip()
        {
            if (!noclipEnabled) return;
            noclipEnabled = false;

            if (movementComp != null && bodyField != null)
            {
                var body = bodyField.GetValue(movementComp) as Rigidbody2D;
                if (body != null)
                {
                    body.simulated = savedBodySimulated;
                    body.velocity = Vector2.zero;
                }
            }

            if (cameraMoverComp != null)
                cameraMoverComp.enabled = savedCameraMoverEnabled;

            Logger.LogInfo("No-clip disabled.");
        }

        // Snap the camera onto the player, keeping its z plane.
        private void CenterCameraOnPlayer()
        {
            if (cameraMoverComp == null || movementComp == null) return;
            Vector3 p = movementComp.transform.position;
            Vector3 cam = cameraMoverComp.transform.position;
            cameraMoverComp.transform.position = new Vector3(p.x, p.y, cam.z);
        }

        // Handle no-clip movement on FixedUpdate tick.
        private void NoclipFixedTick()
        {
            if (movementComp == null) { DisableNoclip(); return; }

            var kb = Keyboard.current;
            if (kb == null) return;

            Vector2 dir = Vector2.zero;
            if (kb.dKey.isPressed) dir.x += 1f;
            if (kb.aKey.isPressed) dir.x -= 1f;
            if (kb.wKey.isPressed) dir.y += 1f;
            if (kb.sKey.isPressed) dir.y -= 1f;

            if (dir.sqrMagnitude > 1f) dir.Normalize();

            float speed = NoclipSpeed;
            if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)
                speed *= NoclipBoostMult;

            var t = movementComp.transform;
            t.position += (Vector3)(dir * (speed * Time.fixedDeltaTime));

            CenterCameraOnPlayer();
        }

        // ABILITY CYCLE (F2) — step through movement-unlock combinations.
        // Bit i ⇒ AbilityNames[i]; ApplyAbilityCombo writes the flags in this same bit order.
        private static readonly string[] AbilityNames =
            { "Wall Jump", "Dash", "Double Jump", "Block Swap" };
        private static readonly int FullAbilityMask = (1 << AbilityNames.Length) - 1; // all-on
        private static readonly int[] AbilityCycleOrder = BuildAbilityCycleOrder();
        private int abilityCycleStep = -1; // -1 = "N/A", the untouched state before first press

        // Reflection handles for the unlock fields + the air-ability max counts.
        private FieldInfo wallJumpUnlockedField;
        private FieldInfo doubleJumpUnlockedField;
        private FieldInfo dashUnlockedField;
        private FieldInfo blockSwapUnlockedField;
        private FieldInfo maxAirJumpsField;
        private FieldInfo maxAirDashesField;
        private bool abilityFieldsResolved;

        // Visit order: the two most-used combos (none, all) first, then 1..all-1 in binary-count order.
        private static int[] BuildAbilityCycleOrder()
        {
            var order = new int[FullAbilityMask + 1];
            order[0] = 0;               // none
            order[1] = FullAbilityMask; // all
            for (int m = 1; m < FullAbilityMask; m++) order[m + 1] = m;
            return order;
        }

        // F2: resolve reflection fields on first use, then advance to the next ability combo.
        private void CycleAbilities()
        {
            if (movementComp == null) { Logger.LogWarning("Cycle abilities: movement not ready."); return; }

            if (!abilityFieldsResolved)
            {
                var t = movementComp.GetType();
                const BindingFlags F = BindingFlags.Public | BindingFlags.Instance;
                wallJumpUnlockedField   = t.GetField("wallJumpUnlocked", F);
                doubleJumpUnlockedField = t.GetField("doubleJumpUnlocked", F);
                dashUnlockedField       = t.GetField("dashUnlocked", F);
                blockSwapUnlockedField  = t.GetField("blockSwapUnlocked", F);
                maxAirJumpsField        = t.GetField("maxAirJumps", F);
                maxAirDashesField       = t.GetField("maxAirDashes", F);
                abilityFieldsResolved = true;
            }

            abilityCycleStep = (abilityCycleStep + 1) % AbilityCycleOrder.Length;
            ApplyAbilityCombo(AbilityCycleOrder[abilityCycleStep]);
        }

        // Set the four unlock flags (+ air max counts) from an ability mask.
        private void ApplyAbilityCombo(int mask)
        {
            bool wall = (mask & 1) != 0;
            bool dash = (mask & 2) != 0;
            bool dbl  = (mask & 4) != 0;
            bool swap = (mask & 8) != 0;

            wallJumpUnlockedField?.SetValue(movementComp, wall);
            dashUnlockedField?.SetValue(movementComp, dash);
            maxAirDashesField?.SetValue(movementComp, dash ? 1 : 0);
            doubleJumpUnlockedField?.SetValue(movementComp, dbl);
            maxAirJumpsField?.SetValue(movementComp, dbl ? 1 : 0);
            blockSwapUnlockedField?.SetValue(movementComp, swap);
        }

        // HUD label for the current cycle step (N/A before first press; else none / all / "A + B").
        private string CurrentAbilityLabel()
        {
            if (abilityCycleStep < 0) return "N/A";
            int mask = AbilityCycleOrder[abilityCycleStep];
            if (mask == 0) return "none";
            if (mask == FullAbilityMask) return "all";
            var parts = new List<string>();
            for (int i = 0; i < AbilityNames.Length; i++)
                if ((mask & (1 << i)) != 0) parts.Add(AbilityNames[i]);
            return string.Join(" + ", parts);
        }
    }
}
