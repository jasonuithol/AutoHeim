using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>Reading player state and manipulating inventory/equipment.</summary>
    internal static partial class Commands
    {
        private static void RegisterPlayer()
        {
            Add("player", "player", "Full local player state: position, look, vitals, food, status effects, flags.", PlayerState, 5f);
            Add("inventory", "inventory", "Inventory contents with prefab names, stacks, equipped flags and durability.", Inventory, 5f);
            Add("equipment", "equipment", "Currently equipped item per slot.", Equipment, 5f);
            Add("skills", "skills", "Skill levels.", Skills, 5f);
            Add("equip", "equip item=<prefab or name>", "Equip an inventory item.", Equip, 10f);
            Add("unequip", "unequip item=<prefab or name> | slot=<right|left|chest|legs|helmet|shoulder|utility|trinket|ammo> | all=true",
                "Unequip an item, a slot, or everything.", Unequip, 10f);
            Add("use_item", "use_item item=<prefab or name>",
                "Use an item as if clicked in the inventory: eat food, drink a potion, toggle equip.", UseItem, 10f);
            Add("hotbar", "hotbar slot=<1-8>", "Use the item in a hotbar slot.", Hotbar, 10f);
            Add("drop", "drop item=<prefab or name> [count=all]", "Drop items on the ground in front of the player.", Drop, 10f);
            Add("give", "give item=<prefab> [count=1] [quality=1]",
                "Add items straight into the inventory (cheat; prefab names like Wood, AxeStone, CookedMeat).", Give, 10f);
            Add("remove_item", "remove_item item=<prefab or name> [count=all]", "Delete items from the inventory.", RemoveItem, 10f);
            Add("god", "god [on=true]", "Toggle god mode (no damage taken; stamina is still used, see heal).", God, 5f);
            Add("ghost", "ghost [on=true]", "Toggle ghost mode (monsters ignore you).", Ghost, 5f);
            Add("heal", "heal", "Restore health and stamina to full.", Heal, 5f);
        }

        // ------------------------------------------------------------------ reading

        private static IEnumerator PlayerState(Ctx c)
        {
            c.Result = DescribePlayer(NeedPlayer());
            yield break;
        }

        private static object DescribePlayer(Player p)
        {
            var pos = p.transform.position;
            return new
            {
                name = p.GetPlayerName(),
                id = p.GetPlayerID(),
                position = Json.Vec(pos),
                yaw = Json.R(InputOverride.GetYaw(p)),
                pitch = Json.R(InputOverride.GetPitch(p)),
                look_dir = Json.Vec(p.GetLookDir()),
                vitals = DescribeVitals(p),
                biome = BiomeOf(p),
                ground_height = GroundAt(pos),
                foods = DescribeFoods(p),
                status_effects = DescribeEffects(p),
                flags = DescribeFlags(p),
                hovering = Hover(p),
                current_weapon = ItemName(p.GetCurrentWeapon()),
            };
        }

        private static string BiomeOf(Player p)
        {
            try { return Heightmap.FindBiome(p.transform.position).ToString(); }
            catch { return "None"; }
        }

        private static object GroundAt(Vector3 pos)
        {
            if (ZoneSystem.instance == null) return null;
            return Json.R(ZoneSystem.instance.GetGroundHeight(pos));
        }

        private static object DescribeVitals(Player p) => new
        {
            health = Json.R(p.GetHealth()),
            max_health = Json.R(p.GetMaxHealth()),
            stamina = Json.R(p.GetStamina()),
            max_stamina = Json.R(p.GetMaxStamina()),
            eitr = Json.R(p.GetEitr()),
            max_eitr = Json.R(p.GetMaxEitr()),
            carry_weight = Json.R(p.GetInventory().GetTotalWeight()),
            max_carry_weight = Json.R(p.GetMaxCarryWeight()),
        };

        private static List<object> DescribeFoods(Player p)
        {
            var list = new List<object>();
            foreach (var f in p.GetFoods())
            {
                list.Add(new
                {
                    name = f.m_item != null && f.m_item.m_dropPrefab != null ? f.m_item.m_dropPrefab.name : f.m_name,
                    health = Json.R(f.m_health),
                    stamina = Json.R(f.m_stamina),
                    eitr = Json.R(f.m_eitr),
                    seconds_left = Json.R(f.m_time),
                });
            }
            return list;
        }

        private static List<object> DescribeEffects(Player p)
        {
            var list = new List<object>();
            foreach (var s in p.GetSEMan().GetStatusEffects())
            {
                object left = null;
                if (s.m_ttl > 0) left = Json.R(s.m_ttl - s.GetDuration());
                list.Add(new { name = s.name, display = Loc(s.m_name), seconds_left = left });
            }
            return list;
        }

        private static object DescribeFlags(Player p) => new
        {
            dead = p.IsDead(),
            on_ground = p.IsOnGround(),
            swimming = p.IsSwimming(),
            crouching = p.IsCrouching(),
            running = p.IsRunning(),
            blocking = p.IsBlocking(),
            attacking = p.InAttack(),
            encumbered = p.IsEncumbered(),
            sheltered = p.InShelter(),
            resting = p.IsSafeInHome(),
            attached = p.IsAttached(),
            teleporting = p.IsTeleporting(),
            in_intro = p.InIntro(),
            god = p.InGodMode(),
            ghost = p.InGhostMode(),
            debug_fly = p.InDebugFlyMode(),
        };

        private static object Hover(Player p)
        {
            var go = p.GetHoverObject();
            if (go == null) return null;
            var hoverable = go.GetComponentInParent<Hoverable>();
            return new
            {
                prefab = Utils.GetPrefabName(go.transform.root.gameObject),
                text = hoverable != null ? Loc(hoverable.GetHoverName()) : null,
                position = Json.Vec(go.transform.position),
            };
        }

        private static IEnumerator Inventory(Ctx c)
        {
            var p = NeedPlayer();
            var inv = p.GetInventory();
            c.Result = new
            {
                width = inv.GetWidth(),
                height = inv.GetHeight(),
                weight = Json.R(inv.GetTotalWeight()),
                max_weight = Json.R(p.GetMaxCarryWeight()),
                items = inv.GetAllItems().OrderBy(i => i.m_gridPos.y).ThenBy(i => i.m_gridPos.x).Select(DescribeItem).ToList(),
            };
            yield break;
        }

        private static IEnumerator Equipment(Ctx c)
        {
            var p = NeedPlayer();
            var result = new Dictionary<string, object>();
            foreach (var kv in SlotFields)
                result[kv.Key] = DescribeItemOrNull(Traverse.Create(p).Field(kv.Value).GetValue<ItemDrop.ItemData>());
            c.Result = result;
            yield break;
        }

        private static IEnumerator Skills(Ctx c)
        {
            var p = NeedPlayer();
            c.Result = p.GetSkills().GetSkillList()
                .OrderByDescending(s => s.m_level)
                .Select(s => new { skill = s.m_info.m_skill.ToString(), level = Json.R(s.m_level), progress = Json.R(s.GetLevelPercentage()) })
                .ToList();
            yield break;
        }

        // ------------------------------------------------------------------ manipulating

        private static IEnumerator Equip(Ctx c)
        {
            var p = NeedPlayer();
            var item = FindItem(p, c.Need("item"));
            if (!item.IsEquipable()) throw Ctx.Fail($"{ItemName(item)} is not equipable");
            if (!p.IsItemEquiped(item) && !p.EquipItem(item)) throw Ctx.Fail($"could not equip {ItemName(item)}");
            yield return null;
            c.Result = new { equipped = ItemName(item), ok = p.IsItemEquiped(item) };
        }

        private static IEnumerator Unequip(Ctx c)
        {
            var p = NeedPlayer();
            if (c.Bool("all", false))
            {
                p.UnequipAllItems();
                c.Result = new { unequipped = "all" };
                yield break;
            }
            ItemDrop.ItemData item;
            if (c.Has("slot"))
            {
                string slot = c.Str("slot").ToLowerInvariant();
                if (!SlotFields.TryGetValue(slot, out string field))
                    throw Ctx.Fail($"unknown slot '{slot}'. Slots: {string.Join(", ", SlotFields.Keys)}");
                item = Traverse.Create(p).Field(field).GetValue<ItemDrop.ItemData>();
                if (item == null) throw Ctx.Fail($"nothing equipped in {slot}");
            }
            else item = FindItem(p, c.Need("item"));
            p.UnequipItem(item);
            yield return null;
            c.Result = new { unequipped = ItemName(item), still_equipped = p.IsItemEquiped(item) };
        }

        private static IEnumerator UseItem(Ctx c)
        {
            var p = NeedPlayer();
            var item = FindItem(p, c.Need("item"));
            string name = ItemName(item);
            int before = p.GetInventory().CountItems(item.m_shared.m_name);
            p.UseItem(p.GetInventory(), item, fromInventoryGui: true);
            yield return new WaitForSecondsRealtime(0.2f);
            c.Result = new
            {
                used = name,
                count_before = before,
                count_after = p.GetInventory().CountItems(item.m_shared.m_name),
                equipped = p.IsItemEquiped(item),
                foods = p.GetFoods().Select(f => f.m_item?.m_dropPrefab?.name ?? f.m_name).ToList(),
            };
        }

        private static IEnumerator Hotbar(Ctx c)
        {
            var p = NeedPlayer();
            int slot = c.Int("slot", 0);
            if (slot < 1 || slot > 8) throw Ctx.Fail("slot must be 1-8");
            var item = p.GetInventory().GetItemAt(slot - 1, 0);
            p.UseHotbarItem(slot);
            yield return null;
            c.Result = new { slot, item = ItemName(item) };
        }

        private static IEnumerator Drop(Ctx c)
        {
            var p = NeedPlayer();
            var item = FindItem(p, c.Need("item"));
            int count = c.Int("count", item.m_stack);
            string name = ItemName(item);
            if (!p.DropItem(p.GetInventory(), item, Mathf.Clamp(count, 1, item.m_stack)))
                throw Ctx.Fail($"could not drop {name}");
            yield return null;
            c.Result = new { dropped = name, count };
        }

        private static IEnumerator Give(Ctx c)
        {
            var p = NeedPlayer();
            string prefab = c.Need("item");
            var go = ObjectDB.instance.GetItemPrefab(prefab);
            if (go == null)
            {
                var match = ObjectDB.instance.m_items.FirstOrDefault(i => string.Equals(i.name, prefab, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    var close = ObjectDB.instance.m_items.Where(i => i.name.IndexOf(prefab, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Take(15).Select(i => i.name);
                    throw Ctx.Fail($"no item prefab '{prefab}'. Similar: {string.Join(", ", close)}");
                }
                prefab = match.name;
            }
            int count = c.Int("count", 1);
            var added = p.GetInventory().AddItem(prefab, count, c.Int("quality", 1), 0, p.GetPlayerID(), p.GetPlayerName(), cheated: true);
            if (added == null) throw Ctx.Fail("inventory full or item could not be added");
            yield return null;
            c.Result = new { given = prefab, count, item = DescribeItem(added) };
        }

        private static IEnumerator RemoveItem(Ctx c)
        {
            var p = NeedPlayer();
            var item = FindItem(p, c.Need("item"));
            int count = c.Int("count", item.m_stack);
            string name = ItemName(item);
            if (p.IsItemEquiped(item)) p.UnequipItem(item);
            p.GetInventory().RemoveItem(item, Mathf.Clamp(count, 1, item.m_stack));
            yield return null;
            c.Result = new { removed = name, count };
        }

        private static IEnumerator God(Ctx c)
        {
            var p = NeedPlayer();
            p.SetGodMode(c.Bool("on", !p.InGodMode()));
            c.Result = new { god = p.InGodMode() };
            yield break;
        }

        private static IEnumerator Ghost(Ctx c)
        {
            var p = NeedPlayer();
            p.SetGhostMode(c.Bool("on", !p.InGhostMode()));
            c.Result = new { ghost = p.InGhostMode() };
            yield break;
        }

        private static IEnumerator Heal(Ctx c)
        {
            var p = NeedPlayer();
            p.Heal(p.GetMaxHealth() - p.GetHealth(), showText: false);
            p.AddStamina(p.GetMaxStamina());
            yield return null;
            c.Result = new { health = Json.R(p.GetHealth()), stamina = Json.R(p.GetStamina()) };
        }

        // ------------------------------------------------------------------ item helpers

        private static readonly Dictionary<string, string> SlotFields = new Dictionary<string, string>
        {
            ["right"] = "m_rightItem", ["left"] = "m_leftItem", ["chest"] = "m_chestItem", ["legs"] = "m_legItem",
            ["helmet"] = "m_helmetItem", ["shoulder"] = "m_shoulderItem", ["utility"] = "m_utilityItem",
            ["trinket"] = "m_trinketItem", ["ammo"] = "m_ammoItem",
        };

        internal static string Loc(string token) =>
            string.IsNullOrEmpty(token) ? token : (Localization.instance != null ? Localization.instance.Localize(token) : token);

        internal static string ItemName(ItemDrop.ItemData i)
        {
            if (i == null) return null;
            return i.m_dropPrefab != null ? i.m_dropPrefab.name : Loc(i.m_shared.m_name);
        }

        internal static object DescribeItemOrNull(ItemDrop.ItemData i) => i == null ? null : DescribeItem(i);

        internal static object DescribeItem(ItemDrop.ItemData i) => new
        {
            prefab = i.m_dropPrefab != null ? i.m_dropPrefab.name : null,
            name = Loc(i.m_shared.m_name),
            stack = i.m_stack,
            max_stack = i.m_shared.m_maxStackSize,
            quality = i.m_quality,
            type = i.m_shared.m_itemType.ToString(),
            equipped = i.m_equipped,
            grid = new { x = i.m_gridPos.x, y = i.m_gridPos.y },
            weight = Json.R(i.GetWeight()),
            durability = Json.ROrNull(i.m_shared.m_useDurability, i.m_durability),
            max_durability = Json.ROrNull(i.m_shared.m_useDurability, i.GetMaxDurability()),
        };

        /// Match by prefab name, then localized name, then token, then substring of any of those.
        internal static ItemDrop.ItemData FindItem(Player p, string query)
        {
            var items = p.GetInventory().GetAllItems();
            bool Eq(string a) => a != null && string.Equals(a, query, StringComparison.OrdinalIgnoreCase);
            bool Has(string a) => a != null && a.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            string Prefab(ItemDrop.ItemData i) => i.m_dropPrefab != null ? i.m_dropPrefab.name : null;

            var found = items.FirstOrDefault(i => Eq(Prefab(i)))
                ?? items.FirstOrDefault(i => Eq(Loc(i.m_shared.m_name)))
                ?? items.FirstOrDefault(i => Eq(i.m_shared.m_name))
                ?? items.FirstOrDefault(i => Has(Prefab(i)) || Has(Loc(i.m_shared.m_name)));
            if (found == null)
                throw Ctx.Fail($"no inventory item matching '{query}'. Have: {string.Join(", ", items.Select(ItemName))}");
            return found;
        }
    }
}
