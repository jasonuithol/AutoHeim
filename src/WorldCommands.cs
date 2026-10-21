using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>Observing the world: nearby things, environment, notable locations; time and saving.</summary>
    internal static partial class Commands
    {
        private static void RegisterWorld()
        {
            Add("nearby", "nearby [radius=30] [kinds=characters,items,interactables,pieces] [name=<text>] [limit=40]",
                "Things around the player, nearest first.", Nearby, 10f);
            Add("env", "env", "Day number, time of day, weather, wind, biome.", Env, 5f);
            Add("locations", "locations [name=<text>] [limit=10]",
                "Generated world locations (StartTemple, Vendor_BlackForest, Eikthyrnir, Crypt...) nearest first. Host only.",
                Locations, 10f);
            Add("ground", "ground x=<x> z=<z>", "Terrain height at a point (only for loaded areas).", Ground, 5f);
            Add("set_time", "set_time fraction=<0..1>", "Set time of day (0=midnight, 0.25=dawn, 0.5=noon). Host only.", SetTime, 5f);
            Add("save", "save", "Save the character (and the world when hosting).", Save, 30f);
        }

        private static IEnumerator Nearby(Ctx c)
        {
            var p = NeedPlayer();
            var kinds = new HashSet<string>((c.Str("kinds") ?? "characters,items,interactables,pieces").Split(',').Select(k => k.Trim()));
            c.Result = BuildNearby(p, kinds, c.Float("radius", 30f), c.Int("limit", 40), c.Str("name"));
            yield break;
        }

        // Kept out of the coroutine body: large iterator methods trip this game's Mono JIT.
        private static Dictionary<string, object> BuildNearby(Player p, HashSet<string> kinds, float radius, int limit, string name)
        {
            var at = p.transform.position;
            var result = new Dictionary<string, object>();
            if (kinds.Contains("characters"))
            {
                result["characters"] = Character.GetAllCharacters()
                    .Where(ch => ch != null && ch != p && Vector3.Distance(at, ch.transform.position) <= radius)
                    .Where(ch => name == null || Matches(ch, name))
                    .OrderBy(ch => Vector3.Distance(at, ch.transform.position)).Take(limit)
                    .Select(ch => DescribeCharacter(p, ch)).ToList();
            }
            if (kinds.Contains("items"))
            {
                result["items"] = UnityEngine.Object.FindObjectsOfType<ItemDrop>()
                    .Where(d => d != null && Vector3.Distance(at, d.transform.position) <= radius)
                    .Where(d => name == null || Matches(d, name))
                    .OrderBy(d => Vector3.Distance(at, d.transform.position)).Take(limit)
                    .Select(d => DescribeDrop(at, d)).ToList();
            }
            if (kinds.Contains("interactables"))
            {
                result["interactables"] = FindInteractables(at, Mathf.Min(radius, 50f))
                    .Where(t => !(t.mb is ItemDrop) && !(t.mb is Character))
                    .Where(t => name == null || Matches(t.mb, name))
                    .OrderBy(t => t.dist).Take(limit)
                    .Select(t => DescribeInteractable(t.mb, t.dist)).ToList();
            }
            if (kinds.Contains("pieces"))
            {
                var pieces = new List<Piece>();
                Piece.GetAllPiecesInRadius(at, radius, pieces);
                result["pieces"] = pieces
                    .Where(pc => name == null || Matches(pc, name))
                    .OrderBy(pc => Vector3.Distance(at, pc.transform.position)).Take(limit)
                    .Select(pc => DescribePiece(p, pc)).ToList();
            }
            return result;
        }

        private static object DescribeDrop(Vector3 at, ItemDrop d) => new
        {
            prefab = Utils.GetPrefabName(d.gameObject),
            name = Loc(d.m_itemData.m_shared.m_name),
            stack = d.m_itemData.m_stack,
            quality = d.m_itemData.m_quality,
            position = Json.Vec(d.transform.position),
            distance = Json.R(Vector3.Distance(at, d.transform.position)),
            yaw = Yaw(d.transform),
        };

        private static object DescribeInteractable(MonoBehaviour mb, float dist) => new
        {
            prefab = Utils.GetPrefabName(mb.transform.root.gameObject),
            name = Describe(mb.gameObject),
            type = mb.GetType().Name,
            position = Json.Vec(mb.transform.position),
            distance = Json.R(dist),
            yaw = Yaw(mb.transform),
        };

        private static IEnumerator Env(Ctx c)
        {
            var env = EnvMan.instance ?? throw Ctx.Fail("not in a world");
            var p = Player.m_localPlayer;
            float frac = env.GetDayFraction();
            c.Result = new
            {
                day = env.GetDay(),
                time_fraction = Json.R(frac),
                clock = $"{(int)(frac * 24):00}:{(int)(frac * 1440 % 60):00}",
                is_day = EnvMan.IsDaylight(),
                is_night = EnvMan.IsNight(),
                weather = env.GetCurrentEnvironment()?.m_name,
                wind_dir = Json.Vec(env.GetWindDir()),
                wind_intensity = Json.R(env.GetWindIntensity()),
                biome = p != null ? BiomeOf(p) : null,
                world = ZNet.instance?.GetWorldName(),
            };
            yield break;
        }

        private static IEnumerator Locations(Ctx c)
        {
            var p = NeedPlayer();
            var zs = ZoneSystem.instance;
            string name = c.Str("name");
            int limit = c.Int("limit", 10);
            var at = p.transform.position;
            var list = zs.GetLocationList()
                .Where(l => l.m_location != null)
                .Where(l => name == null || l.m_location.m_prefabName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(l => Vector3.Distance(at, l.m_position)).Take(limit)
                .Select(l => new
                {
                    name = l.m_location.m_prefabName,
                    position = Json.Vec(l.m_position),
                    distance = Json.R(Vector3.Distance(at, l.m_position)),
                    placed = l.m_placed,
                }).ToList();
            if (list.Count == 0 && !ZNet.instance.IsServer())
                throw Ctx.Fail("location list is only available when hosting the world");
            c.Result = list;
            yield break;
        }

        private static IEnumerator Ground(Ctx c)
        {
            var zs = ZoneSystem.instance ?? throw Ctx.Fail("not in a world");
            var pt = new Vector3(c.NeedFloat("x"), 0f, c.NeedFloat("z"));
            bool ok = zs.GetGroundHeight(pt, out float h);
            c.Result = new { loaded = ok, height = Json.ROrNull(ok, h) };
            yield break;
        }

        private static IEnumerator SetTime(Ctx c)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) throw Ctx.Fail("only the host can set the time");
            float frac = Mathf.Repeat(c.NeedFloat("fraction"), 1f);
            var env = EnvMan.instance;
            env.m_debugTimeOfDay = false; // a previous 'tod' console command pins the clock
            double dayLen = env.m_dayLengthSec;
            double now = ZNet.instance.GetTimeSeconds();
            double dayStart = Math.Floor(now / dayLen) * dayLen;
            double target = dayStart + frac * dayLen;
            if (target < now) target += dayLen; // never run the clock backwards
            ZNet.instance.SetNetTime(target);
            yield return null;
            c.Result = new { day = env.GetDay(), time_fraction = Json.R(env.GetDayFraction()) };
        }

        private static IEnumerator Save(Ctx c)
        {
            NeedPlayer();
            Game.instance.SavePlayerProfile(setLogoutPoint: false);
            bool world = ZNet.instance != null && ZNet.instance.IsServer();
            if (world) ZNet.instance.Save(sync: true);
            yield return null;
            c.Result = new { character = true, world };
        }
    }
}
