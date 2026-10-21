using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>
    /// Detailed descriptions of things in the world (characters, building pieces,
    /// containers) plus world-wide object search over the network state, which
    /// also covers areas that are not currently loaded.
    /// </summary>
    internal static partial class Commands
    {
        private static void RegisterObserve()
        {
            Add("container", "container [name=<text>] [radius=5] | container x=<x> z=<z>",
                "List the contents of the nearest chest/cart/ship container (optionally matching name, or nearest to a point).",
                ContainerCmd, 10f);
            Add("container_put", "container_put item=<prefab or name> [count=all] [name=<container>] [radius=5]",
                "Move items from your inventory into the nearest container.", ContainerPut, 10f);
            Add("container_take", "container_take item=<prefab or name> [count=all] [name=<container>] [radius=5]",
                "Move items from the nearest container into your inventory.", ContainerTake, 10f);
            Add("world_objects", "world_objects name=<prefab text> [radius=<m>] [x=<x> z=<z>] [limit=50]",
                "Search every saved object in the world (loaded or not) by prefab name: position, facing, health, creator. " +
                "Covers the whole world when hosting; on a dedicated server only the area synced to this client.",
                WorldObjects, 30f);
        }

        // ------------------------------------------------------------------ characters

        internal static float Yaw(Transform t) => Json.R(t.eulerAngles.y);

        internal static float YawOf(Vector3 dir) => Json.R(Mathf.Repeat(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 360f));

        internal static object DescribeCharacter(Player me, Character ch)
        {
            var at = me.transform.position;
            var ai = ch.GetComponent<BaseAI>();
            var tame = ch.GetComponent<Tameable>();
            return new
            {
                prefab = Utils.GetPrefabName(ch.gameObject),
                name = Loc(ch.m_name),
                player = ch.IsPlayer(),
                player_name = ch is Player pl ? pl.GetPlayerName() : null,
                position = Json.Vec(ch.transform.position),
                distance = Json.R(Vector3.Distance(at, ch.transform.position)),
                yaw = Yaw(ch.transform),
                look_yaw = YawOf(ch.GetLookDir()),
                speed = Json.R(ch.GetVelocity().magnitude),
                health = Json.R(ch.GetHealth()),
                max_health = Json.R(ch.GetMaxHealth()),
                health_pct = Json.R(ch.GetHealthPercentage() * 100f),
                level = ch.GetLevel(),
                stars = Mathf.Max(0, ch.GetLevel() - 1),
                boss = ch.IsBoss(),
                faction = ch.m_faction.ToString(),
                hostile = BaseAI.IsEnemy(me, ch),
                dead = ch.IsDead(),
                ai = ai == null ? null : DescribeAI(me, ai),
                tamed = ch.IsTamed(),
                tame = tame == null ? null : DescribeTame(tame),
            };
        }

        private static object DescribeAI(Player me, BaseAI ai)
        {
            var target = ai.GetTargetCreature();
            return new
            {
                alerted = ai.IsAlerted(),
                has_target = ai.HaveTarget(),
                target = target == null ? null : (target is Player tp ? $"player:{tp.GetPlayerName()}" : Utils.GetPrefabName(target.gameObject)),
                targeting_me = target != null && target == me,
                can_see_me = ai.CanSeeTarget(me),
                fleeing = FleeTracker.IsFleeing(ai),
                sleeping = ai.IsSleeping(),
                aggravated = ai.IsAggravatable() && ai.IsAggravated(),
                charging = ai.IsCharging(),
            };
        }

        private static object DescribeTame(Tameable t)
        {
            string name = null;
            try { name = t.GetText(); } catch { }
            bool hungry = false;
            try { hungry = t.IsHungry(); } catch { }
            return new { name = string.IsNullOrEmpty(name) ? null : name, hungry };
        }

        // ------------------------------------------------------------------ building pieces

        private static readonly System.Reflection.MethodInfo WearGetSupport = AccessTools.Method(typeof(WearNTear), "GetSupport");
        private static readonly System.Reflection.MethodInfo WearGetMaxSupport = AccessTools.Method(typeof(WearNTear), "GetMaxSupport");

        internal static object DescribePiece(Player me, Piece pc)
        {
            var wear = pc.GetComponent<WearNTear>();
            var zdo = pc.GetComponent<ZNetView>()?.GetZDO();
            long creator = pc.GetCreator();
            return new
            {
                prefab = Utils.GetPrefabName(pc.gameObject),
                name = Loc(pc.m_name),
                position = Json.Vec(pc.transform.position),
                distance = Json.R(Vector3.Distance(me.transform.position, pc.transform.position)),
                yaw = Yaw(pc.transform),
                rotation = Json.Vec(pc.transform.eulerAngles),
                health = wear == null || zdo == null ? null : (object)Json.R(zdo.GetFloat(ZDOVars.s_health, wear.m_health)),
                max_health = wear == null ? null : (object)Json.R(wear.m_health),
                health_pct = wear == null ? null : (object)Json.R(wear.GetHealthPercentage() * 100f),
                support = wear == null ? null : (object)Json.R((float)WearGetSupport.Invoke(wear, null)),
                max_support = wear == null ? null : (object)Json.R((float)WearGetMaxSupport.Invoke(wear, null)),
                material = wear == null ? null : wear.m_materialType.ToString(),
                creator_id = creator == 0 ? null : (object)creator,
                creator = CreatorName(me, creator),
            };
        }

        internal static string CreatorName(Player me, long id)
        {
            if (id == 0) return null;
            if (me != null && me.GetPlayerID() == id) return $"you ({me.GetPlayerName()})";
            var p = Player.GetPlayer(id);
            return p != null ? p.GetPlayerName() : "unknown (offline player)";
        }

        // ------------------------------------------------------------------ containers

        private static Container FindContainer(Ctx c, Player p)
        {
            Vector3 at = c.Has("x") && c.Has("z")
                ? new Vector3(c.Float("x", 0f), p.transform.position.y, c.Float("z", 0f))
                : p.transform.position;
            float radius = c.Float("radius", c.Has("x") ? 3f : 5f);
            string name = c.Str("name");
            var box = UnityEngine.Object.FindObjectsOfType<Container>()
                .Where(k => k != null && InputOverride.HorizDist(at, k.transform.position) <= radius)
                .Where(k => name == null || Matches(k, name) || k.m_name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(k => Vector3.Distance(at, k.transform.position))
                .FirstOrDefault();
            if (box == null) throw Ctx.Fail($"no container{(name != null ? $" matching '{name}'" : "")} within {radius}m");
            return box;
        }

        private static IEnumerator ContainerCmd(Ctx c)
        {
            var p = NeedPlayer();
            var box = FindContainer(c, p);
            var inv = box.GetInventory() ?? throw Ctx.Fail("container has no inventory loaded");
            c.Result = new
            {
                prefab = Utils.GetPrefabName(box.transform.root.gameObject),
                name = Loc(box.m_name),
                position = Json.Vec(box.transform.position),
                yaw = Yaw(box.transform),
                distance = Json.R(Vector3.Distance(p.transform.position, box.transform.position)),
                in_use = box.IsInUse(),
                width = inv.GetWidth(),
                height = inv.GetHeight(),
                slots_used = inv.GetAllItems().Count,
                weight = Json.R(inv.GetTotalWeight()),
                items = inv.GetAllItems().OrderBy(i => i.m_gridPos.y).ThenBy(i => i.m_gridPos.x).Select(DescribeItem).ToList(),
            };
            yield break;
        }

        private static IEnumerator ContainerPut(Ctx c)
        {
            var p = NeedPlayer();
            var box = FindContainer(c, p);
            var item = FindItem(p, c.Need("item"));
            if (p.IsItemEquiped(item)) p.UnequipItem(item);
            yield return Transfer(c, box, p.GetInventory(), box.GetInventory(), item, "put");
        }

        private static IEnumerator ContainerTake(Ctx c)
        {
            var p = NeedPlayer();
            var box = FindContainer(c, p);
            var inv = box.GetInventory() ?? throw Ctx.Fail("container has no inventory loaded");
            string q = c.Need("item");
            var item = inv.GetAllItems().FirstOrDefault(i => (i.m_dropPrefab != null && string.Equals(i.m_dropPrefab.name, q, StringComparison.OrdinalIgnoreCase))
                                                             || string.Equals(Loc(i.m_shared.m_name), q, StringComparison.OrdinalIgnoreCase))
                    ?? inv.GetAllItems().FirstOrDefault(i => ItemName(i).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? throw Ctx.Fail($"container has no '{q}'. Contents: {string.Join(", ", inv.GetAllItems().Select(ItemName))}");
            yield return Transfer(c, box, inv, p.GetInventory(), item, "took");
        }

        private static IEnumerator Transfer(Ctx c, Container box, Inventory from, Inventory to, ItemDrop.ItemData item, string verb)
        {
            // In multiplayer only the owner of a container may change it (opening it does the same).
            var nview = box.GetComponent<ZNetView>();
            if (nview != null && !nview.IsOwner()) nview.ClaimOwnership();
            int amount = Mathf.Clamp(c.Int("count", item.m_stack), 1, item.m_stack);
            string name = ItemName(item);
            int before = to.CountItems(item.m_shared.m_name);
            if (!to.CanAddItem(item, amount)) throw Ctx.Fail("destination inventory is full");
            if (amount == item.m_stack)
                to.MoveItemToThis(from, item);
            else
            {
                // Split the stack: add a copy with the requested amount, then remove that many.
                var part = item.Clone();
                part.m_stack = amount;
                if (to.AddItem(part)) from.RemoveItem(item, amount);
            }
            yield return null;
            c.Result = new { action = verb, item = name, count = to.CountItems(item.m_shared.m_name) - before };
        }

        // ------------------------------------------------------------------ world-wide search

        private static readonly Dictionary<int, string> PrefabNames = new Dictionary<int, string>();

        private static string PrefabName(int hash)
        {
            if (PrefabNames.TryGetValue(hash, out var n)) return n;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            n = go != null ? go.name : null;
            PrefabNames[hash] = n;
            return n;
        }

        private static IEnumerator WorldObjects(Ctx c)
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null) throw Ctx.Fail("not in a world");
            string name = c.Need("name");
            int limit = c.Int("limit", 50);
            var me = Player.m_localPlayer;
            Vector3 center = c.Has("x") && c.Has("z") ? new Vector3(c.Float("x", 0f), 0f, c.Float("z", 0f))
                : me != null ? me.transform.position : Vector3.zero;
            float radius = c.Float("radius", float.MaxValue);

            var all = Traverse.Create(ZDOMan.instance).Field<Dictionary<ZDOID, ZDO>>("m_objectsByID").Value;
            var hits = new List<(ZDO zdo, string prefab, float dist)>();
            int scanned = 0;
            foreach (var zdo in all.Values)
            {
                scanned++;
                string prefab = PrefabName(zdo.GetPrefab());
                if (prefab == null || prefab.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                float dist = InputOverride.HorizDist(center, zdo.GetPosition());
                if (dist > radius) continue;
                hits.Add((zdo, prefab, dist));
            }

            c.Result = new
            {
                scope = ZNet.instance.IsServer() ? "whole world" : "area synced to this client",
                scanned,
                matches = hits.Count,
                by_prefab = hits.GroupBy(h => h.prefab).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
                objects = hits.OrderBy(h => h.dist).Take(limit).Select(h => DescribeZdo(me, h.zdo, h.prefab, h.dist)).ToList(),
            };
            yield break;
        }

        private static object DescribeZdo(Player me, ZDO zdo, string prefab, float dist)
        {
            float health = zdo.GetFloat(ZDOVars.s_health, -1f);
            long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
            string tamedName = zdo.GetString(ZDOVars.s_tamedName, "");
            int level = zdo.GetInt(ZDOVars.s_level, 0);
            return new
            {
                prefab,
                position = Json.Vec(zdo.GetPosition()),
                distance = Json.R(dist),
                yaw = Json.R(zdo.GetRotation().eulerAngles.y),
                health = health >= 0f ? (object)Json.R(health) : null,
                level = level > 0 ? (object)level : null,
                tamed_name = string.IsNullOrEmpty(tamedName) ? null : tamedName,
                creator = CreatorName(me, creator),
                loaded = ZNetScene.instance.HaveInstance(zdo),
            };
        }
    }

    /// <summary>The AI has no public "fleeing" flag; remember when each AI last ran its flee routine.</summary>
    [HarmonyPatch(typeof(BaseAI), "Flee")]
    internal static class FleeTracker
    {
        private static readonly Dictionary<int, float> LastFlee = new Dictionary<int, float>();

        private static void Postfix(BaseAI __instance)
        {
            LastFlee[__instance.GetInstanceID()] = Time.time;
            if (LastFlee.Count > 4096) LastFlee.Clear();
        }

        internal static bool IsFleeing(BaseAI ai) =>
            LastFlee.TryGetValue(ai.GetInstanceID(), out float t) && Time.time - t < 1f;
    }
}
