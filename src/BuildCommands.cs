using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>
    /// Building like a real player: equip the build tool, select the piece, walk into reach,
    /// aim the camera's crosshair ray at a point, set rotation in the game's scroll steps,
    /// then press place / remove / repair and let the game's own code decide everything
    /// (ghost position, snapping, placement rules, materials, creator, stamina, skill).
    /// </summary>
    internal static partial class Commands
    {
        private static void RegisterBuild()
        {
            Add("build_pieces", "build_pieces [name=<text>] [all=false]",
                "Pieces your build tools (Hammer, Hoe, Cultivator...) can place: unlocked?, buildable right now?, materials, station.",
                BuildPieces, 10f);
            Add("build", "build piece=<prefab or name> x=<x> z=<z> [y=<surface y>] [rotation=<deg>] [snap=true] [snap_point=-1] [approach=true] [timeout=30]",
                "Place a piece the way a player does: aim the crosshair at the surface point (x, y, z), rotate in 22.5° scroll steps, " +
                "snap to nearby pieces unless snap=false (Alt), optional snap_point (Tab). Uses materials and records you as creator.",
                Build, 30f);
            Add("destroy", "destroy [name=<text>] [x=<x> z=<z>] [radius=3] [approach=true] [timeout=30]",
                "Deconstruct the nearest matching piece with the hammer (middle-click): aims at it, removes it, resources drop.",
                Destroy, 30f);
            Add("repair", "repair [name=<text>] [x=<x> z=<z>] [radius=3] [approach=true] [timeout=30]",
                "Repair the nearest matching damaged piece with the hammer's repair tool (needs its crafting station in range).",
                Repair, 30f);
        }

        // ------------------------------------------------------------------ player internals

        private static Traverse<T> PF<T>(Player p, string field) => Traverse.Create(p).Field<T>(field);

        private sealed class AimResult
        {
            internal bool Ok;
            internal RaycastHit Hit;
            internal string Error;
        }

        // ------------------------------------------------------------------ build_pieces

        private static IEnumerator BuildPieces(Ctx c)
        {
            var p = NeedPlayer();
            string name = c.Str("name");
            bool all = c.Bool("all", false);
            var list = new List<object>();
            foreach (var tool in BuildTools(p))
            {
                var table = tool.m_shared.m_buildPieces;
                foreach (var go in table.m_pieces)
                {
                    var piece = go != null ? go.GetComponent<Piece>() : null;
                    if (piece == null) continue;
                    if (name != null && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0
                        && Loc(piece.m_name).IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool known = p.IsRecipeKnown(piece.m_name);
                    if (!known && !all) continue;
                    list.Add(DescribeBuildable(p, tool, piece, known));
                }
            }
            c.Result = list;
            yield break;
        }

        private static object DescribeBuildable(Player p, ItemDrop.ItemData tool, Piece piece, bool known) => new
        {
            prefab = piece.gameObject.name,
            name = Loc(piece.m_name),
            tool = ItemName(tool),
            category = piece.m_category.ToString(),
            unlocked = known,
            can_build_now = p.HaveRequirements(piece, Player.RequirementMode.CanBuild),
            station = piece.m_craftingStation != null ? Loc(piece.m_craftingStation.m_name) : null,
            resources = piece.m_resources.Select(r => new
            {
                item = r.m_resItem != null ? r.m_resItem.gameObject.name : null,
                amount = r.m_amount,
                have = r.m_resItem != null ? p.GetInventory().CountItems(r.m_resItem.m_itemData.m_shared.m_name) : 0,
            }).ToList(),
            special = piece.m_repairPiece ? "repair" : piece.m_removePiece ? "remove" : null,
        };

        private static IEnumerable<ItemDrop.ItemData> BuildTools(Player p) =>
            p.GetInventory().GetAllItems().Where(i => i.m_shared.m_buildPieces != null);

        // ------------------------------------------------------------------ build

        private static IEnumerator Build(Ctx c)
        {
            var p = NeedPlayer();
            string query = c.Need("piece");
            float timeout = c.Float("timeout", 30f);
            float deadline = Time.realtimeSinceStartup + timeout;

            // 1. Find the piece in one of the player's build tools.
            ItemDrop.ItemData tool = null;
            Piece piece = null;
            foreach (var t in BuildTools(p))
            {
                piece = FindPieceInTable(t.m_shared.m_buildPieces, query);
                if (piece != null) { tool = t; break; }
            }
            if (piece == null)
            {
                if (!BuildTools(p).Any()) throw Ctx.Fail("no build tool in inventory (need a Hammer; 'give item=Hammer' or craft one)");
                throw Ctx.Fail($"no buildable piece matching '{query}' in your tools; see build_pieces all=true");
            }
            if (!p.IsRecipeKnown(piece.m_name) && !p.NoCostCheat())
                throw Ctx.Fail($"{piece.gameObject.name} is not unlocked yet (players unlock pieces by holding their materials" +
                               (piece.m_craftingStation != null ? $" and having seen a {Loc(piece.m_craftingStation.m_name)})" : ")"));

            Vector3 aim = new Vector3(c.NeedFloat("x"), 0f, c.NeedFloat("z"));
            aim.y = c.Has("y") ? c.Float("y", 0f) : GroundOrFail(aim);
            string missing = MissingRequirements(p, piece, aim);
            if (missing != null) throw Ctx.Fail(missing);

            // 2. Equip the tool and select the piece, as a player would from the build menu.
            var step = EquipTool(p, tool);
            while (step.MoveNext()) yield return step.Current;
            p.SetSelectedPiece(piece);
            yield return null;
            if (p.GetSelectedPiece() != piece) throw Ctx.Fail("could not select the piece in the build menu");

            // 3. Rotation in scroll-wheel steps; snapping on (default) or off (Alt held); manual snap point (Tab).
            float degrees = PF<float>(p, "m_placeRotationDegrees").Value;
            int steps = Mathf.RoundToInt(c.Float("rotation", 0f) / degrees);
            PF<int>(p, "m_placeRotation").Value = steps;
            BuildControl.ForceAltPlace = !c.Bool("snap", true);
            PF<int>(p, "m_manualSnapPoint").Value = c.Int("snap_point", -1);

            try
            {
                // 4. Walk within reach and aim the crosshair at the point.
                if (c.Bool("approach", true))
                {
                    var walk = Approach(p, aim, deadline);
                    while (walk.MoveNext()) yield return walk.Current;
                }
                var result = new AimResult();
                int mask = PF<int>(p, "m_placeRayMask").Value;
                var aimer = AimAt(p, new[] { aim }, mask, h => Vector3.Distance(h.point, aim) < 0.1f, result, deadline);
                while (aimer.MoveNext()) yield return aimer.Current;
                if (!result.Ok) throw Ctx.Fail(result.Error);

                // 5. The game has now positioned (and possibly snapped) the ghost; check its verdict.
                yield return null;
                var ghost = PF<GameObject>(p, "m_placementGhost").Value;
                var status = PF<Player.PlacementStatus>(p, "m_placementStatus").Value;
                if (ghost == null || !ghost.activeInHierarchy) throw Ctx.Fail($"no placement ghost (status {status})");
                Vector3 ghostPos = ghost.transform.position;
                Quaternion ghostRot = ghost.transform.rotation;
                if (status != Player.PlacementStatus.Valid)
                    throw Ctx.Fail($"placement not allowed here: {status}{Diagnose(p, piece, ghost, result.Hit, mask, status)} (ghost at {Fmt(ghostPos)})");

                // 6. Press place, then confirm the piece exists.
                var lastUse = PF<float>(p, "m_lastToolUseTime");
                float before = lastUse.Value;
                var press = Press(p, "m_placePressedTime", "m_placeDelay", () => lastUse.Value != before, deadline);
                while (press.MoveNext()) yield return press.Current;
                if (lastUse.Value == before) throw Ctx.Fail("the game did not place the piece (check stamina, materials, or 'log')");

                yield return null;
                var placed = FindPlaced(p, piece.gameObject.name, ghostPos);
                c.Result = new
                {
                    placed = placed != null,
                    aim_point = Json.Vec(aim),
                    ghost_position = Json.Vec(ghostPos),
                    ghost_yaw = Json.R(ghostRot.eulerAngles.y),
                    rotation_steps = steps,
                    snap = !BuildControl.ForceAltPlace,
                    piece = placed != null ? DescribePiece(p, placed) : null,
                    stamina = Json.R(p.GetStamina()),
                };
            }
            finally
            {
                BuildControl.ForceAltPlace = false;
                PF<int>(p, "m_manualSnapPoint").Value = -1;
                InputOverride.HoldUntil = 0f;
            }
        }

        /// Explain a refusal using the same tests the game applies (the game itself only says "Invalid").
        private static string Diagnose(Player p, Piece piece, GameObject ghost, RaycastHit hit, int mask, Player.PlacementStatus status)
        {
            var why = new List<string>();
            if (status == Player.PlacementStatus.Invalid)
            {
                if (piece.m_notOnTiltingSurface && hit.normal.y < 0.8f)
                    why.Add($"surface too steep (slope {Vector3.Angle(hit.normal, Vector3.up):F0}°)");
                if (piece.m_groundOnly && hit.collider.GetComponent<Heightmap>() == null) why.Add("must be placed on the ground");
                if (piece.m_noClipping)
                {
                    var overlaps = new HashSet<string>();
                    var mine = ghost.GetComponentsInChildren<Collider>();
                    foreach (var other in Physics.OverlapSphere(ghost.transform.position, 10f, mask))
                    {
                        if (other.transform.root == ghost.transform.root) continue;
                        foreach (var k in mine)
                        {
                            if (Physics.ComputePenetration(k, k.transform.position, k.transform.rotation, other, other.transform.position, other.transform.rotation, out _, out float depth) && depth > 0.2f)
                                overlaps.Add(other.GetComponent<Heightmap>() != null ? "the terrain (ground too uneven)" : Describe(other.gameObject));
                        }
                    }
                    if (overlaps.Count > 0) why.Add($"overlaps {string.Join(", ", overlaps.Take(5))}");
                }
                var wear = hit.collider.GetComponentInParent<WearNTear>();
                if (wear != null && !wear.m_supports) why.Add($"{Describe(wear.gameObject)} can't support pieces");
            }
            return why.Count == 0 ? "" : " - " + string.Join("; ", why);
        }

        private static Piece FindPieceInTable(PieceTable table, string query)
        {
            Piece Get(GameObject go) => go != null ? go.GetComponent<Piece>() : null;
            var exact = table.m_pieces.FirstOrDefault(go => go != null && string.Equals(go.name, query, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return Get(exact);
            var byName = table.m_pieces.FirstOrDefault(go => Get(go) != null && string.Equals(Loc(Get(go).m_name), query, StringComparison.OrdinalIgnoreCase));
            return Get(byName);
        }

        private static string MissingRequirements(Player p, Piece piece, Vector3 at)
        {
            if (p.NoCostCheat() || p.HaveRequirements(piece, Player.RequirementMode.CanBuild))
            {
                if (piece.m_craftingStation == null || CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, at) != null)
                    return null;
            }
            var parts = new List<string>();
            foreach (var r in piece.m_resources)
            {
                if (r.m_resItem == null) continue;
                int have = p.GetInventory().CountItems(r.m_resItem.m_itemData.m_shared.m_name);
                if (have < r.m_amount) parts.Add($"{r.m_resItem.gameObject.name} {have}/{r.m_amount}");
            }
            if (piece.m_craftingStation != null && CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, at) == null)
                parts.Add($"needs a {Loc(piece.m_craftingStation.m_name)} in range");
            return parts.Count == 0 ? null : $"missing requirements: {string.Join(", ", parts)}";
        }

        private static Piece FindPlaced(Player p, string prefab, Vector3 at)
        {
            var near = new List<Piece>();
            Piece.GetAllPiecesInRadius(at, 1f, near);
            return near.Where(pc => Utils.GetPrefabName(pc.gameObject) == prefab && pc.GetCreator() == p.GetPlayerID())
                .OrderBy(pc => Vector3.Distance(pc.transform.position, at)).FirstOrDefault();
        }

        // ------------------------------------------------------------------ destroy / repair

        private static IEnumerator Destroy(Ctx c)
        {
            var p = NeedPlayer();
            float deadline = Time.realtimeSinceStartup + c.Float("timeout", 30f);
            var target = FindTargetPiece(c, p);
            if (!target.m_canBeRemoved) throw Ctx.Fail($"{Utils.GetPrefabName(target.gameObject)} cannot be deconstructed");
            var tool = BuildTools(p).FirstOrDefault(t => t.m_shared.m_buildPieces.m_canRemovePieces)
                       ?? throw Ctx.Fail("no tool that can remove pieces (need a Hammer)");
            string label = Utils.GetPrefabName(target.gameObject);
            Vector3 pos = target.transform.position;
            var resources = target.m_resources.Where(r => r.m_resItem != null && r.m_recover)
                .Select(r => $"{r.m_resItem.gameObject.name} x{r.m_amount}").ToList();

            var step = EquipTool(p, tool);
            while (step.MoveNext()) yield return step.Current;
            try
            {
                var aimStep = AimAtPiece(c, p, target, deadline);
                while (aimStep.MoveNext()) yield return aimStep.Current;
                var press = Press(p, "m_removePressedTime", "m_removeDelay", () => target == null, deadline);
                while (press.MoveNext()) yield return press.Current;
                float until = Time.realtimeSinceStartup + 1.5f;
                while (target != null && Time.realtimeSinceStartup < until) yield return null;
                if (target != null) throw Ctx.Fail($"the game did not remove {label} (ward/permissions, stamina, or 'log')");
            }
            finally { InputOverride.HoldUntil = 0f; }
            c.Result = new { destroyed = label, position = Json.Vec(pos), resources_dropped = resources, stamina = Json.R(p.GetStamina()) };
        }

        private static IEnumerator Repair(Ctx c)
        {
            var p = NeedPlayer();
            float deadline = Time.realtimeSinceStartup + c.Float("timeout", 30f);
            var target = FindTargetPiece(c, p, damagedOnly: !c.Has("x"));
            var wear = target.GetComponent<WearNTear>() ?? throw Ctx.Fail("that piece has no health to repair");
            ItemDrop.ItemData tool = null;
            Piece repairPiece = null;
            foreach (var t in BuildTools(p))
            {
                repairPiece = t.m_shared.m_buildPieces.m_pieces.Select(go => go != null ? go.GetComponent<Piece>() : null)
                    .FirstOrDefault(pc => pc != null && pc.m_repairPiece);
                if (repairPiece != null) { tool = t; break; }
            }
            if (repairPiece == null) throw Ctx.Fail("no tool with a repair mode (need a Hammer)");
            if (target.m_craftingStation != null && CraftingStation.HaveBuildStationInRange(target.m_craftingStation.m_name, target.transform.position) == null)
                throw Ctx.Fail($"repairing needs a {Loc(target.m_craftingStation.m_name)} in range");

            string label = Utils.GetPrefabName(target.gameObject);
            float before = wear.GetHealthPercentage();
            var step = EquipTool(p, tool);
            while (step.MoveNext()) yield return step.Current;
            p.SetSelectedPiece(repairPiece);
            yield return null;
            try
            {
                var aimStep = AimAtPiece(c, p, target, deadline);
                while (aimStep.MoveNext()) yield return aimStep.Current;
                var press = Press(p, "m_placePressedTime", "m_placeDelay", () => wear.GetHealthPercentage() > before, deadline);
                while (press.MoveNext()) yield return press.Current;
                float until = Time.realtimeSinceStartup + 1f;
                while (wear.GetHealthPercentage() <= before && Time.realtimeSinceStartup < until) yield return null;
            }
            finally { InputOverride.HoldUntil = 0f; }
            float after = wear.GetHealthPercentage();
            string seen = null;
            if (after <= before)
            {
                var hover = p.GetHoveringPiece();
                var station = target.m_craftingStation;
                seen = $"selected={p.GetSelectedPiece()?.gameObject.name ?? "none"}, hovering={(hover != null ? Utils.GetPrefabName(hover.gameObject) : "none")}" +
                       $"{(hover != null && hover != target ? " (a different piece)" : "")}, " +
                       $"station_in_range={(station == null || CraftingStation.HaveBuildStationInRange(station.m_name, p.transform.position) != null)}, " +
                       $"stamina={p.GetStamina():F0}";
            }
            c.Result = new
            {
                piece = label,
                repaired = after > before,
                health_pct_before = Json.R(before * 100f),
                health_pct_after = Json.R(after * 100f),
                note = after > before ? null : (before >= 0.999f ? "did not need repair" : "the game refused the repair"),
                game_state = seen,
            };
        }

        private static Piece FindTargetPiece(Ctx c, Player p, bool damagedOnly = false)
        {
            Vector3 at = c.Has("x") && c.Has("z") ? new Vector3(c.Float("x", 0f), c.Float("y", p.transform.position.y), c.Float("z", 0f)) : p.transform.position;
            float radius = c.Float("radius", c.Has("x") ? 1.5f : 3f);
            string name = c.Str("name");
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(at, Mathf.Max(radius, 0.5f) + 2f, pieces);
            var target = pieces
                .Where(pc => InputOverride.HorizDist(pc.transform.position, at) <= radius)
                .Where(pc => name == null || Matches(pc, name))
                .Where(pc => !damagedOnly || (pc.GetComponent<WearNTear>() != null && pc.GetComponent<WearNTear>().GetHealthPercentage() < 0.999f))
                .OrderBy(pc => Vector3.Distance(pc.transform.position, at))
                .FirstOrDefault();
            if (target == null)
                throw Ctx.Fail($"no {(damagedOnly ? "damaged " : "")}piece{(name != null ? $" matching '{name}'" : "")} within {radius}m");
            return target;
        }

        // ------------------------------------------------------------------ shared steps

        private static IEnumerator EquipTool(Player p, ItemDrop.ItemData tool)
        {
            if (!p.IsItemEquiped(tool)) p.EquipItem(tool);
            float until = Time.realtimeSinceStartup + 2f;
            while (!p.InPlaceMode())
            {
                if (Time.realtimeSinceStartup > until)
                    throw Ctx.Fail($"equipping {ItemName(tool)} did not enter build mode" +
                                   (p.IsSwimming() ? ": can't use tools while swimming" :
                                    p.InAttack() || p.InDodge() ? ": busy attacking/dodging" : ""));
                yield return null;
            }
        }

        /// Walk until the point is comfortably inside the game's reach (m_maxPlaceDistance from the eye).
        /// Like a player, pick a spot next to the target that has free footing, a clear walk from here
        /// and a clear view of the target, without standing where the piece will go.
        private static IEnumerator Approach(Player p, Vector3 point, float deadline)
        {
            float reach = PF<float>(p, "m_maxPlaceDistance").Value;
            if (Vector3.Distance(p.GetEyePoint(), point) <= reach - 1f && HasLineOfSight(p.GetEyePoint(), point)) yield break;

            Vector3 stand = ChooseStandPoint(p, point, reach);
            InputOverride.BeginWaypoint(stand, run: false);
            try
            {
                while (InputOverride.HorizDist(p.transform.position, stand) > 0.5f)
                {
                    if (Time.realtimeSinceStartup > deadline)
                        throw Ctx.Fail($"could not walk within reach of the target (stuck at {Fmt(p.transform.position)}, wanted {Fmt(stand)})");
                    yield return null;
                }
            }
            finally
            {
                InputOverride.Waypoint = null;
            }
            yield return new WaitForSecondsRealtime(0.2f); // let the body settle before aiming
            if (Vector3.Distance(p.GetEyePoint(), point) > reach)
                throw Ctx.Fail($"target is out of reach ({Vector3.Distance(p.GetEyePoint(), point):F1}m > {reach}m) and could not be approached");
        }

        private static readonly int SolidMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "vehicle", "Default_small");

        private static Vector3 ChooseStandPoint(Player p, Vector3 point, float reach)
        {
            Vector3 from = p.transform.position;
            Vector3 best = Vector3.zero;
            float bestScore = float.MaxValue;
            foreach (float dist in new[] { 2.5f, 3.2f, 1.8f })
            {
                for (int a = 0; a < 360; a += 30)
                {
                    Vector3 dir = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
                    Vector3 stand = point + dir * dist;
                    if (!ZoneSystem.instance.GetSolidHeight(stand, out float y, 1000)) continue;
                    stand.y = y;
                    Vector3 eye = stand + Vector3.up * 1.6f;
                    if (Vector3.Distance(eye, point) > reach - 0.5f) continue;
                    if (Mathf.Abs(y - point.y) > 2.5f) continue;
                    // Room to stand (capsule clear of obstacles) and a view of the target.
                    if (Physics.CheckCapsule(stand + Vector3.up * 0.6f, stand + Vector3.up * 1.6f, 0.35f, SolidMask)) continue;
                    if (!HasLineOfSight(eye, point)) continue;
                    // Prefer close spots reachable in a straight walk.
                    float score = Vector3.Distance(from, stand);
                    if (Physics.Linecast(from + Vector3.up * 0.8f, stand + Vector3.up * 0.8f, SolidMask)) score += 50f;
                    if (score < bestScore) { bestScore = score; best = stand; }
                }
                if (bestScore < 50f) break;
            }
            if (bestScore == float.MaxValue)
                throw Ctx.Fail("no free spot to stand within reach of the target with a clear view of it");
            return best;
        }

        private static bool HasLineOfSight(Vector3 eye, Vector3 point)
        {
            Vector3 d = point - eye;
            float len = d.magnitude;
            return len < 0.2f || !Physics.Raycast(eye, d / len, len - 0.2f, SolidMask);
        }

        /// Turn the view until the camera's crosshair ray (what the game uses for building) hits
        /// something accepted by <paramref name="accept"/>. The third-person camera orbits as the view
        /// turns, so this re-aims each frame until it converges.
        private static IEnumerator AimAt(Player p, Vector3[] points, int mask, Func<RaycastHit, bool> accept, AimResult result, float deadline)
        {
            string lastMiss = "nothing";
            foreach (var point in points)
            {
                for (int i = 0; i < 40; i++)
                {
                    if (Time.realtimeSinceStartup > deadline) break;
                    var cam = GameCamera.instance.transform;
                    Vector3 d = point - cam.position;
                    float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
                    InputOverride.HoldUntil = Time.time + 2f; // stand still, ignore the real mouse
                    InputOverride.SetLook(p, yaw, pitch);
                    yield return null;
                    cam = GameCamera.instance.transform;
                    if (Physics.Raycast(cam.position, cam.forward, out var hit, 50f, mask))
                    {
                        if (accept(hit) && i > 1)
                        {
                            result.Ok = true;
                            result.Hit = hit;
                            yield break;
                        }
                        lastMiss = $"{Describe(hit.collider.gameObject)} at {Fmt(hit.point)}";
                    }
                    else lastMiss = "nothing (sky)";
                }
            }
            result.Error = $"could not aim at the target; the crosshair hits {lastMiss}";
        }

        private static IEnumerator AimAtPiece(Ctx c, Player p, Piece target, float deadline)
        {
            // Candidate points on the piece, nearest-to-us first: visible surface, then centres.
            var cols = target.GetComponentsInChildren<Collider>().Where(k => k.enabled && !k.isTrigger).ToList();
            if (cols.Count == 0) throw Ctx.Fail("piece has no collider to aim at");
            Vector3 eye = p.GetEyePoint();
            var points = new List<Vector3>();
            foreach (var k in cols)
            {
                var center = k.bounds.center;
                points.Add(Vector3.Lerp(k.ClosestPoint(eye), center, 0.2f));
                points.Add(center);
            }
            if (c.Bool("approach", true))
            {
                var walk = Approach(p, points.OrderBy(pt => Vector3.Distance(pt, eye)).First(), deadline);
                while (walk.MoveNext()) yield return walk.Current;
                eye = p.GetEyePoint();
            }
            float reach = PF<float>(p, "m_maxPlaceDistance").Value;
            var ordered = points.OrderBy(pt => Vector3.Distance(pt, eye)).Take(6).ToArray();
            var result = new AimResult();
            int mask = PF<int>(p, "m_removeRayMask").Value;
            var aimer = AimAt(p, ordered, mask,
                h => h.collider.GetComponentInParent<Piece>() == target && Vector3.Distance(h.point, p.GetEyePoint()) < reach,
                result, deadline);
            while (aimer.MoveNext()) yield return aimer.Current;
            if (!result.Ok) throw Ctx.Fail(result.Error);
        }

        /// Simulate a click of the given action: the game reads "pressed within the last 0.2s" from this field.
        private static readonly System.Reflection.MethodInfo PlayerTakeInput = AccessTools.Method(typeof(Player), "TakeInput");

        private static IEnumerator Press(Player p, string pressedField, string delayField, Func<bool> done, float deadline)
        {
            // The game only reads tool clicks while it accepts player input (no menus/chat open,
            // not in a spawn/sleep animation); a real click would be ignored too, so wait for it.
            while (!(bool)PlayerTakeInput.Invoke(p, null))
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw Ctx.Fail(p.InCutscene() ? "the game is ignoring input: player is in a cutscene/animation (just spawned or sleeping)"
                                                  : "the game is ignoring input: a menu, chat, console or map is open (try 'popups dismiss=true')");
                yield return null;
            }
            var lastUse = PF<float>(p, "m_lastToolUseTime");
            float delay = PF<float>(p, delayField).Value;
            while (Time.time - lastUse.Value <= delay)
            {
                if (Time.realtimeSinceStartup > deadline) throw Ctx.Fail("tool cooldown did not expire");
                yield return null;
            }
            InputOverride.HoldUntil = Time.time + 2f;
            PF<float>(p, pressedField).Value = Time.time;
            float until = Time.realtimeSinceStartup + 1f;
            while (!done() && Time.realtimeSinceStartup < until) yield return null;
        }

        private static float GroundOrFail(Vector3 at)
        {
            if (!ZoneSystem.instance.GetSolidHeight(at, out float h, 1000))
                throw Ctx.Fail("that area isn't loaded; pass y= or move closer");
            return h;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
    }

    /// <summary>Holds the "AltPlace" button (disables snapping) while a scripted build wants it.</summary>
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton))]
    internal static class BuildControl
    {
        internal static bool ForceAltPlace;

        private static bool Prefix(string name, ref bool __result)
        {
            if (!ForceAltPlace || name != "AltPlace") return true;
            __result = true;
            return false;
        }
    }
}
