using HarmonyLib;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>
    /// Synthetic controls for the local player. While anything here is active
    /// we replace PlayerController.FixedUpdate's keyboard/gamepad read with our
    /// own SetControls call, so the game behaves exactly as if keys were held.
    /// It works with the window unfocused and ignores real input meanwhile.
    ///
    /// Pulses (jump, attack, dodge, crouch toggle) are consumed after one
    /// physics tick, matching the "pressed this frame" edge the game expects.
    /// </summary>
    internal static class InputOverride
    {
        // Local-space move (x = strafe right, z = forward), relative to look direction.
        internal static Vector3 MoveLocal;
        internal static float MoveUntil;
        // World-space waypoint; overrides MoveLocal while set.
        internal static Vector3? Waypoint;
        internal static bool Run;
        internal static bool AutoJumpWhenStuck = true;

        internal static bool JumpPulse, AttackPulse, SecondaryPulse, DodgePulse, CrouchPulse, BlockPulse;
        internal static float AttackHoldUntil, SecondaryHoldUntil, BlockHoldUntil;

        // Stuck tracking for waypoint walking.
        internal static int StuckEvents;
        private static Vector3 _stuckRef;
        private static float _stuckCheckAt;
        private static float _steer;

        private static readonly AccessTools.FieldRef<PlayerController, Player> ControllerPlayer =
            AccessTools.FieldRefAccess<PlayerController, Player>("m_character");
        private static readonly AccessTools.FieldRef<Character, Quaternion> LookYaw =
            AccessTools.FieldRefAccess<Character, Quaternion>("m_lookYaw");
        private static readonly AccessTools.FieldRef<Player, float> LookPitch =
            AccessTools.FieldRefAccess<Player, float>("m_lookPitch");

        private static float Now => Time.time;

        internal static bool Active =>
            Waypoint.HasValue || Now < MoveUntil || JumpPulse || AttackPulse || SecondaryPulse || DodgePulse ||
            CrouchPulse || BlockPulse || Now < AttackHoldUntil || Now < SecondaryHoldUntil || Now < BlockHoldUntil;

        internal static void Clear()
        {
            MoveLocal = Vector3.zero;
            MoveUntil = 0f;
            Waypoint = null;
            Run = false;
            JumpPulse = AttackPulse = SecondaryPulse = DodgePulse = CrouchPulse = BlockPulse = false;
            AttackHoldUntil = SecondaryHoldUntil = BlockHoldUntil = 0f;
            _steer = 0f;
        }

        internal static void BeginWaypoint(Vector3 target, bool run)
        {
            Waypoint = target;
            Run = run;
            StuckEvents = 0;
            _steer = 0f;
            _stuckRef = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            _stuckCheckAt = Now + 1.5f;
        }

        internal static object Describe() => new
        {
            active = Active,
            waypoint = Waypoint.HasValue ? Json.Vec(Waypoint.Value) : null,
            moving = Now < MoveUntil,
            run = Run,
        };

        /// Set yaw (degrees, 0 = +Z/north, 90 = +X/east) and pitch (degrees, positive = down).
        internal static void SetLook(Player p, float yaw, float? pitch)
        {
            LookYaw(p) = Quaternion.Euler(0f, yaw, 0f);
            if (pitch.HasValue) LookPitch(p) = Mathf.Clamp(pitch.Value, -89f, 89f);
            // Re-derive eye rotation and look dir the same way SetMouseLook does.
            p.SetMouseLook(Vector2.zero);
        }

        internal static float GetYaw(Player p) => LookYaw(p).eulerAngles.y;
        internal static float GetPitch(Player p) => LookPitch(p);

        internal static void FaceTowards(Player p, Vector3 target, bool includePitch)
        {
            Vector3 eye = p.GetEyePoint();
            Vector3 d = target - eye;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float? pitch = null;
            if (includePitch)
            {
                float flat = new Vector2(d.x, d.z).magnitude;
                pitch = -Mathf.Atan2(d.y, flat) * Mathf.Rad2Deg;
            }
            SetLook(p, yaw, pitch);
        }

        internal static float HorizDist(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        /// Called in place of PlayerController.FixedUpdate when active.
        internal static void Apply(Player p)
        {
            Vector3 move = Now < MoveUntil ? MoveLocal : Vector3.zero;
            bool jump = JumpPulse;

            if (Waypoint.HasValue)
            {
                Vector3 pos = p.transform.position;
                Vector3 to = Waypoint.Value - pos;
                to.y = 0f;
                if (to.sqrMagnitude > 0.0001f)
                {
                    float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + _steer;
                    SetLook(p, yaw, null);
                    move = Vector3.forward;
                }

                if (Now >= _stuckCheckAt)
                {
                    float moved = HorizDist(pos, _stuckRef);
                    if (moved < 0.5f && !p.IsSwimming())
                    {
                        // Alternate steering left/right and hop over small obstacles.
                        StuckEvents++;
                        _steer = (StuckEvents % 3 == 1) ? 50f : (StuckEvents % 3 == 2) ? -50f : 0f;
                        if (AutoJumpWhenStuck) jump = true;
                    }
                    else _steer = 0f;
                    _stuckRef = pos;
                    _stuckCheckAt = Now + 1.5f;
                }
            }

            bool attack = AttackPulse;
            bool attackHold = attack || Now < AttackHoldUntil;
            bool secondary = SecondaryPulse;
            bool secondaryHold = secondary || Now < SecondaryHoldUntil;
            bool blockHold = BlockPulse || Now < BlockHoldUntil;

            p.SetControls(move, attack, attackHold, secondary, secondaryHold, BlockPulse, blockHold,
                jump, CrouchPulse, Run && move != Vector3.zero, autoRun: false, dodge: DodgePulse);

            JumpPulse = AttackPulse = SecondaryPulse = DodgePulse = CrouchPulse = BlockPulse = false;
        }

        [HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
        private static class Patch_FixedUpdate
        {
            private static bool Prefix(PlayerController __instance)
            {
                if (!Active) return true;
                var p = ControllerPlayer(__instance);
                if (p == null || p != Player.m_localPlayer) return true;
                Apply(p);
                return false;
            }
        }

        // Keep real mouse movement from fighting a scripted look while we are in control.
        [HarmonyPatch(typeof(PlayerController), "LateUpdate")]
        private static class Patch_LateUpdate
        {
            private static bool Prefix(PlayerController __instance)
            {
                if (!Active) return true;
                var p = ControllerPlayer(__instance);
                return p == null || p != Player.m_localPlayer;
            }
        }
    }
}
