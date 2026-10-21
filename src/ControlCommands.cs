using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>Moving, looking, fighting and interacting through synthetic input.</summary>
    internal static partial class Commands
    {
        private static void RegisterControl()
        {
            Add("move", "move [forward=1] [right=0] [seconds=1] [run=false]",
                "Hold movement keys relative to the look direction for a duration (forward/right in -1..1).", Move, 15f);
            Add("move_to", "move_to x=<x> z=<z> [run=true] [tolerance=1.5] [timeout=60] [jump_when_stuck=true]",
                "Walk/run to a world position (straight line with simple stuck avoidance). Blocks until arrival or timeout.",
                MoveTo, 60f);
            Add("stop", "stop", "Release all synthetic input and cancel move_to.", Stop, 5f);
            Add("look", "look [yaw=<deg>] [pitch=<deg>] | look x=<x> y=<y> z=<z> | look turn=<deg>",
                "Set view direction. yaw 0=+Z(north) 90=+X(east); pitch positive looks down. Or face a world point.", Look, 5f);
            Add("jump", "jump", "Jump once.", Jump, 5f);
            Add("dodge", "dodge [forward=1] [right=0]", "Dodge roll in a direction relative to the view.", Dodge, 5f);
            Add("crouch", "crouch [on=<bool>]", "Toggle (or set) crouch/sneak.", Crouch, 5f);
            Add("attack", "attack [secondary=false] [count=1] [interval=0.9] [hold=0]",
                "Primary or secondary attack with the current weapon. hold=seconds keeps the button held (bows, charged attacks).",
                Attack, 30f);
            Add("fight", "fight [name=<text>] [radius=20] [timeout=30] [reach=2.2] [secondary=false]",
                "Chase and attack the nearest hostile (or nearest character matching name) every frame until it dies, it escapes, or timeout.",
                Fight, 30f);
            Add("block", "block [seconds=1]", "Hold block for a duration.", Block, 30f);
            Add("emote", "emote name=<wave|sit|challenge|cheer|nonono|thumbsup|point|...>", "Play an emote.", EmoteCmd, 5f);
            Add("teleport", "teleport x=<x> z=<z> [y=<ground>] [timeout=60]",
                "Teleport to a world position and wait until the area has loaded. y defaults to ground height.", Teleport, 60f);
            Add("interact", "interact [name=<text>] [radius=3] [alt=false] [hold=false]",
                "Interact (E) with the hovered object, or the nearest interactable whose name/prefab contains 'name'.", Interact, 10f);
            Add("pickup", "pickup [name=<text>] [radius=5] [all=true]", "Pick up item drops lying nearby.", Pickup, 15f);
        }

        private static IEnumerator Move(Ctx c)
        {
            var p = NeedPlayer();
            float seconds = Mathf.Clamp(c.Float("seconds", 1f), 0f, 120f);
            var start = p.transform.position;
            var dir = new Vector3(Mathf.Clamp(c.Float("right", 0f), -1f, 1f), 0f, Mathf.Clamp(c.Float("forward", 1f), -1f, 1f));
            InputOverride.Waypoint = null;
            InputOverride.MoveLocal = dir;
            InputOverride.Run = c.Bool("run", false);
            InputOverride.MoveUntil = Time.time + seconds;
            while (Time.time < InputOverride.MoveUntil) yield return null;
            InputOverride.Run = false;
            var end = p.transform.position;
            c.Result = new { from = Json.Vec(start), to = Json.Vec(end), distance = Json.R(Vector3.Distance(start, end)) };
        }

        private static IEnumerator MoveTo(Ctx c)
        {
            var p = NeedPlayer();
            var target = new Vector3(c.NeedFloat("x"), p.transform.position.y, c.NeedFloat("z"));
            float tolerance = c.Float("tolerance", 1.5f);
            float timeout = c.Float("timeout", 60f);
            InputOverride.AutoJumpWhenStuck = c.Bool("jump_when_stuck", true);
            InputOverride.BeginWaypoint(target, c.Bool("run", true));
            var start = p.transform.position;
            float deadline = Time.realtimeSinceStartup + timeout;
            string outcome = "arrived";
            try
            {
                while (InputOverride.HorizDist(p.transform.position, target) > tolerance)
                {
                    if (p.IsDead()) { outcome = "died"; break; }
                    if (Time.realtimeSinceStartup > deadline) { outcome = "timeout"; break; }
                    if (InputOverride.Waypoint == null) { outcome = "stopped"; break; }
                    yield return null;
                }
            }
            finally
            {
                InputOverride.Waypoint = null;
                InputOverride.Run = false;
            }
            var pos = p.transform.position;
            c.Result = new
            {
                outcome,
                arrived = outcome == "arrived",
                position = Json.Vec(pos),
                remaining = Json.R(InputOverride.HorizDist(pos, target)),
                travelled = Json.R(InputOverride.HorizDist(pos, start)),
                stuck_events = InputOverride.StuckEvents,
            };
        }

        private static IEnumerator Stop(Ctx c)
        {
            InputOverride.Clear();
            c.Result = new { stopped = true };
            yield break;
        }

        private static IEnumerator Look(Ctx c)
        {
            var p = NeedPlayer();
            if (c.Has("x") && c.Has("z"))
            {
                var target = new Vector3(c.Float("x", 0f), c.Float("y", p.GetEyePoint().y), c.Float("z", 0f));
                InputOverride.FaceTowards(p, target, includePitch: c.Has("y"));
            }
            else if (c.Has("turn"))
                InputOverride.SetLook(p, InputOverride.GetYaw(p) + c.Float("turn", 0f), null);
            else
            {
                float? pitch = null;
                if (c.Has("pitch")) pitch = c.Float("pitch", 0f);
                InputOverride.SetLook(p, c.Float("yaw", InputOverride.GetYaw(p)), pitch);
            }
            // The body turns toward the look direction over a few frames.
            yield return new WaitForSecondsRealtime(0.1f);
            c.Result = new { yaw = Json.R(InputOverride.GetYaw(p)), pitch = Json.R(InputOverride.GetPitch(p)), look_dir = Json.Vec(p.GetLookDir()) };
        }

        private static IEnumerator Jump(Ctx c)
        {
            var p = NeedPlayer();
            float y0 = p.transform.position.y;
            InputOverride.JumpPulse = true;
            float peak = y0;
            float until = Time.time + 1f;
            while (Time.time < until)
            {
                peak = Mathf.Max(peak, p.transform.position.y);
                yield return null;
            }
            c.Result = new { jumped = peak - y0 > 0.2f, height = Json.R(peak - y0) };
        }

        private static IEnumerator Dodge(Ctx c)
        {
            NeedPlayer();
            InputOverride.MoveLocal = new Vector3(c.Float("right", 0f), 0f, c.Float("forward", 1f));
            InputOverride.MoveUntil = Time.time + 0.1f;
            InputOverride.DodgePulse = true;
            yield return new WaitForSecondsRealtime(0.8f);
            c.Result = new { dodged = true };
        }

        private static IEnumerator Crouch(Ctx c)
        {
            var p = NeedPlayer();
            // IsCrouching() reads the animator and lags; the toggle flag is the real state.
            var toggled = Traverse.Create(p).Field<bool>("m_crouchToggled");
            bool want = c.Bool("on", !toggled.Value);
            if (want != toggled.Value) InputOverride.CrouchPulse = true;
            yield return new WaitForSecondsRealtime(0.3f);
            c.Result = new { crouching = toggled.Value };
        }

        private static IEnumerator Attack(Ctx c)
        {
            var p = NeedPlayer();
            bool secondary = c.Bool("secondary", false);
            int count = Mathf.Clamp(c.Int("count", 1), 1, 50);
            float interval = Mathf.Max(0.1f, c.Float("interval", 0.9f));
            float hold = c.Float("hold", 0f);
            float stamina0 = p.GetStamina();
            int started = 0;
            for (int i = 0; i < count; i++)
            {
                if (c.Cancelled || p.IsDead()) break;
                if (secondary)
                {
                    InputOverride.SecondaryPulse = true;
                    if (hold > 0) InputOverride.SecondaryHoldUntil = Time.time + hold;
                }
                else
                {
                    InputOverride.AttackPulse = true;
                    if (hold > 0) InputOverride.AttackHoldUntil = Time.time + hold;
                }
                yield return new WaitForSecondsRealtime(0.15f);
                if (p.InAttack()) started++;
                float wait = Mathf.Max(interval - 0.15f, hold);
                if (wait > 0) yield return new WaitForSecondsRealtime(wait);
            }
            c.Result = new { attacks = count, started, weapon = ItemName(p.GetCurrentWeapon()), stamina_used = Json.R(stamina0 - p.GetStamina()) };
        }

        private static IEnumerator Fight(Ctx c)
        {
            var p = NeedPlayer();
            string name = c.Str("name");
            float radius = c.Float("radius", 20f);
            float reach = c.Float("reach", 2.2f);
            bool secondary = c.Bool("secondary", false);
            float deadline = Time.realtimeSinceStartup + c.Float("timeout", 30f);

            Character target = Character.GetAllCharacters()
                .Where(ch => ch != null && ch != p && !ch.IsDead())
                .Where(ch => name != null ? Matches(ch, name) : BaseAI.IsEnemy(p, ch))
                .Where(ch => Vector3.Distance(p.transform.position, ch.transform.position) <= radius)
                .OrderBy(ch => Vector3.Distance(p.transform.position, ch.transform.position))
                .FirstOrDefault();
            if (target == null)
                throw Ctx.Fail(name != null ? $"no living character matching '{name}' within {radius}m" : $"no hostile within {radius}m");

            string label = Utils.GetPrefabName(target.gameObject);
            float startHp = target.GetHealth();
            int swings = 0;
            bool wasAttacking = false;
            string outcome = "timeout";
            try
            {
                while (true)
                {
                    if (target == null || target.IsDead()) { outcome = "killed"; break; }
                    if (p.IsDead()) { outcome = "died"; break; }
                    if (Time.realtimeSinceStartup > deadline) break;
                    float dist = InputOverride.HorizDist(p.transform.position, target.transform.position);
                    if (dist > radius * 2f) { outcome = "escaped"; break; }

                    if (dist > reach)
                    {
                        if (InputOverride.Waypoint == null) InputOverride.BeginWaypoint(target.transform.position, run: true);
                        else InputOverride.Waypoint = target.transform.position;
                    }
                    else
                    {
                        InputOverride.Waypoint = null;
                        InputOverride.FaceTowards(p, target.GetCenterPoint(), includePitch: true);
                        if (!p.InAttack())
                        {
                            if (secondary) InputOverride.SecondaryPulse = true;
                            else InputOverride.AttackPulse = true;
                        }
                    }
                    bool attacking = p.InAttack();
                    if (attacking && !wasAttacking) swings++;
                    wasAttacking = attacking;
                    yield return null;
                }
            }
            finally
            {
                InputOverride.Waypoint = null;
                InputOverride.Run = false;
            }
            float hp = target != null ? target.GetHealth() : 0f;
            c.Result = new
            {
                outcome,
                target = label,
                swings,
                target_health = Json.R(hp),
                damage_dealt = Json.R(startHp - hp),
                health = Json.R(p.GetHealth()),
                stamina = Json.R(p.GetStamina()),
            };
        }

        private static IEnumerator Block(Ctx c)
        {
            var p = NeedPlayer();
            float seconds = Mathf.Clamp(c.Float("seconds", 1f), 0.1f, 60f);
            InputOverride.BlockPulse = true;
            InputOverride.BlockHoldUntil = Time.time + seconds;
            bool blocking = false;
            // Blocking only registers once any attack/dodge animation has finished.
            while (Time.time < InputOverride.BlockHoldUntil)
            {
                blocking |= p.IsBlocking();
                yield return null;
            }
            c.Result = new { blocked_for = seconds, was_blocking = blocking };
        }

        private static IEnumerator EmoteCmd(Ctx c)
        {
            var p = NeedPlayer();
            string name = c.Need("name").ToLowerInvariant();
            bool ok = p.StartEmote(name, oneshot: name != "sit");
            if (!ok) throw Ctx.Fail($"could not start emote '{name}'");
            yield return null;
            c.Result = new { emote = name };
        }

        private static IEnumerator Teleport(Ctx c)
        {
            var p = NeedPlayer();
            float x = c.NeedFloat("x"), z = c.NeedFloat("z");
            float timeout = c.Float("timeout", 60f);
            float deadline = Time.realtimeSinceStartup + timeout;
            var from = p.transform.position;
            InputOverride.Clear();

            Vector3 target = new Vector3(x, c.Has("y") ? c.Float("y", 0f) : from.y, z);
            bool distant = InputOverride.HorizDist(from, target) > 64f;
            bool explicitY = c.Has("y");
            if (!explicitY && ZoneSystem.instance.GetGroundHeight(target, out float gh)) target.y = gh + 0.5f;
            else if (!explicitY && distant) target.y = 200f; // the game snaps distant teleports to ground once loaded

            // TeleportTo refuses for ~2s after spawning/teleporting; retry until accepted.
            while (!p.TeleportTo(target, p.transform.rotation, distant))
            {
                if (Time.realtimeSinceStartup > deadline) throw Ctx.Fail("teleport was not accepted (cooldown or already teleporting)");
                yield return new WaitForSecondsRealtime(0.25f);
            }
            yield return null;
            while (p.IsTeleporting() || !ZNetScene.instance.IsAreaReady(p.transform.position))
            {
                if (Time.realtimeSinceStartup > deadline) throw Ctx.Fail("timed out waiting for teleport to finish");
                yield return null;
            }
            // The game parks the player at the requested height and only checks that a floor
            // exists below; without an explicit y, put them on it instead of dropping from altitude.
            if (!explicitY) SnapToGround(p);
            c.Result = new { from = Json.Vec(from), position = Json.Vec(p.transform.position) };
        }

        private static IEnumerator Interact(Ctx c)
        {
            var p = NeedPlayer();
            string name = c.Str("name");
            float radius = c.Float("radius", 3f);
            GameObject target = null;
            if (name == null)
            {
                target = p.GetHoverObject();
                if (target == null) throw Ctx.Fail("not hovering anything; pass name=<text> to pick the nearest interactable");
            }
            else
            {
                var best = FindInteractables(p.transform.position, radius)
                    .Where(t => Matches(t.mb, name))
                    .OrderBy(t => t.dist).FirstOrDefault();
                if (best.mb == null) throw Ctx.Fail($"no interactable matching '{name}' within {radius}m");
                target = best.mb.gameObject;
                InputOverride.FaceTowards(p, target.transform.position, includePitch: true);
            }
            string label = Describe(target);
            AccessTools.Method(typeof(Player), "Interact").Invoke(p, new object[] { target, c.Bool("hold", false), c.Bool("alt", false) });
            yield return new WaitForSecondsRealtime(0.5f);
            // Harvesting (berries, stones, mushrooms) spawns a drop that is only auto-collected when
            // standing right next to it; collect it so the result matches what a player would get.
            var picked = new List<string>();
            if (target.GetComponentInParent<Pickable>() != null && target != null)
            {
                foreach (var d in Object.FindObjectsOfType<ItemDrop>())
                {
                    if (d == null || Vector3.Distance(d.transform.position, target.transform.position) > 3f) continue;
                    string drop = $"{Utils.GetPrefabName(d.gameObject)} x{d.m_itemData.m_stack}";
                    if (p.Pickup(d.gameObject, autoequip: false, autoPickupDelay: false)) picked.Add(drop);
                }
            }
            c.Result = new { interacted = label, position = Json.Vec(target.transform.position), attached = p.IsAttached(), picked_up = picked };
        }

        private static IEnumerator Pickup(Ctx c)
        {
            var p = NeedPlayer();
            string name = c.Str("name");
            float radius = c.Float("radius", 5f);
            bool all = c.Bool("all", true);
            var picked = new List<string>();
            var drops = Object.FindObjectsOfType<ItemDrop>()
                .Where(d => d != null && Vector3.Distance(d.transform.position, p.transform.position) <= radius)
                .Where(d => name == null || Matches(d, name))
                .OrderBy(d => Vector3.Distance(d.transform.position, p.transform.position)).ToList();
            foreach (var d in drops)
            {
                string label = $"{d.m_itemData.m_dropPrefab?.name ?? Utils.GetPrefabName(d.gameObject)} x{d.m_itemData.m_stack}";
                if (p.Pickup(d.gameObject, autoequip: false, autoPickupDelay: false)) picked.Add(label);
                if (!all && picked.Count > 0) break;
                yield return null;
            }
            c.Result = new { picked, candidates = drops.Count };
        }

        // ------------------------------------------------------------------ helpers

        internal static void SnapToGround(Player p)
        {
            var pos = p.transform.position;
            float y = ZoneSystem.instance.GetSolidHeight(pos);
            if (Mathf.Abs(pos.y - y) < 0.3f) return;
            p.transform.position = new Vector3(pos.x, y + 0.2f, pos.z);
            var t = Traverse.Create(p);
            var body = t.Field<Rigidbody>("m_body").Value;
            if (body != null) body.linearVelocity = Vector3.zero;
            t.Field<float>("m_maxAirAltitude").Value = y + 0.2f; // no fall damage from the old height
        }

        internal static IEnumerable<(MonoBehaviour mb, float dist)> FindInteractables(Vector3 at, float radius)
        {
            var seen = new HashSet<GameObject>();
            foreach (var col in Physics.OverlapSphere(at, radius, ~0, QueryTriggerInteraction.Collide))
            {
                var inter = col.GetComponentInParent<Interactable>() as MonoBehaviour;
                if (inter == null || !seen.Add(inter.gameObject)) continue;
                yield return (inter, Vector3.Distance(at, inter.transform.position));
            }
        }

        internal static bool Matches(Component mb, string query)
        {
            string prefab = Utils.GetPrefabName(mb.transform.root.gameObject);
            if (prefab.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var h = mb.GetComponentInParent<Hoverable>();
            string hover = h != null ? Loc(h.GetHoverName()) : null;
            return hover != null && hover.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string Describe(GameObject go)
        {
            var h = go.GetComponentInParent<Hoverable>();
            string prefab = Utils.GetPrefabName(go.transform.root.gameObject);
            string hover = h != null ? Loc(h.GetHoverName()) : null;
            return string.IsNullOrEmpty(hover) ? prefab : $"{hover} ({prefab})";
        }
    }
}
