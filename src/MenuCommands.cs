using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>
    /// Main-menu automation: characters, worlds, starting a local world,
    /// joining a server, and logging back out. Drives FejdStartup the way its
    /// own buttons do, minus the dialogs.
    /// </summary>
    internal static partial class Commands
    {
        private static void RegisterMenu()
        {
            Add("characters", "characters", "List character profiles in the active save directory.", Characters, 5f);
            Add("create_character", "create_character name=<name>",
                "Create a new character (local save, default starting items). Must be at the main menu.", CreateCharacter, 15f);
            Add("delete_character", "delete_character name=<name> [force=false]",
                "Delete a character. Refused outside an isolated save dir unless force=true.", DeleteCharacter, 10f);
            Add("worlds", "worlds", "List worlds in the active save directory.", Worlds, 5f);
            Add("create_world", "create_world name=<name, 5+ chars> [seed=<seed>]",
                "Create a new local world (random seed if omitted). Must be at the main menu.", CreateWorld, 15f);
            Add("delete_world", "delete_world name=<name> [force=false]",
                "Delete a world. Refused outside an isolated save dir unless force=true.", DeleteWorld, 10f);
            Add("start_world", "start_world world=<name> [character=<name>] [open=false] [password=] [public=false] [crossplay=false] [wait=true] [timeout=240]",
                "Host and enter a local world with a character. wait=true blocks until the player is ready.", StartWorld, 240f);
            Add("join_server", "join_server address=<host:port> [character=<name>] [password=] [wait=true] [timeout=240]",
                "Connect to a (local) dedicated server. wait=true blocks until the player is ready.", JoinServer, 240f);
            Add("logout", "logout [save=true] [wait=true] [timeout=120]", "Leave the world and return to the main menu.", Logout, 120f);
        }

        // ------------------------------------------------------------------ listing

        private static IEnumerator Characters(Ctx c)
        {
            c.Result = SaveSystem.GetAllPlayerProfiles().Select(p => new
            {
                name = p.GetName(),
                filename = p.GetFilename(),
                source = p.m_fileSource.ToString(),
            }).ToList();
            yield break;
        }

        private static IEnumerator Worlds(Ctx c)
        {
            c.Result = SaveSystem.GetWorldList().Where(w => w.m_name != null).Select(w => new
            {
                name = w.m_name,
                seed = w.m_seedName,
                source = w.m_fileSource.ToString(),
                error = w.m_dataError.ToString(),
            }).ToList();
            yield break;
        }

        // ------------------------------------------------------------------ characters

        private static IEnumerator CreateCharacter(Ctx c)
        {
            var fs = NeedMenu();
            string name = c.Need("name").Trim();
            if (name.Length < 3 || name.Length > 15)
                throw Ctx.Fail("character name must be 3-15 characters");
            if (PlayerProfile.HaveProfile(name.ToLower()))
                throw Ctx.Fail($"character '{name}' already exists");

            fs.OnCharacterNew();
            yield return null;
            fs.m_csNewCharacterName.text = name;
            fs.OnNewCharacterDone(forceLocal: true);
            yield return null;
            if (fs.m_newCharacterError != null && fs.m_newCharacterError.activeInHierarchy)
            {
                fs.OnNewCharacterCancel();
                throw Ctx.Fail($"game rejected character name '{name}'");
            }
            ShowMainMenu(fs);
            c.Result = new { created = name, filename = name.ToLower() };
        }

        private static IEnumerator DeleteCharacter(Ctx c)
        {
            var fs = NeedMenu();
            GuardDestructive(c);
            var profile = FindProfile(c.Need("name"));
            PlayerProfile.RemoveProfile(profile.GetFilename());
            SaveSystem.InvalidateCache(SaveDataType.Character);
            Traverse.Create(fs).Field("m_profiles").SetValue(SaveSystem.GetAllPlayerProfiles());
            Traverse.Create(fs).Field("m_profileIndex").SetValue(0);
            Traverse.Create(fs).Method("UpdateCharacterList").GetValue();
            c.Result = new { deleted = profile.GetName() };
            yield break;
        }

        // ------------------------------------------------------------------ worlds

        private static IEnumerator CreateWorld(Ctx c)
        {
            var fs = NeedMenu();
            string name = c.Need("name").Trim();
            if (name.Length < 5 || name.Length > 20)
                throw Ctx.Fail("world name must be 5-20 characters");
            if (World.HaveWorld(name))
                throw Ctx.Fail($"world '{name}' already exists");

            fs.OnWorldNew();
            yield return null;
            fs.m_newWorldName.text = name;
            string seed = c.Str("seed");
            if (!string.IsNullOrEmpty(seed)) fs.m_newWorldSeed.text = seed;
            seed = fs.m_newWorldSeed.text;
            fs.OnNewWorldDone(forceLocal: true);
            yield return null;
            ShowMainMenu(fs);
            if (!World.HaveWorld(name)) throw Ctx.Fail("world creation did not produce a save");
            c.Result = new { created = name, seed };
        }

        private static IEnumerator DeleteWorld(Ctx c)
        {
            var fs = NeedMenu();
            GuardDestructive(c);
            var world = FindWorld(c.Need("name"));
            World.RemoveWorld(world.m_name, world.m_fileSource);
            SaveSystem.InvalidateCache(SaveDataType.World);
            Traverse.Create(fs).Field("m_world").SetValue(null);
            Traverse.Create(fs).Field("m_worlds").SetValue(SaveSystem.GetWorldList());
            c.Result = new { deleted = world.m_name };
            yield break;
        }

        // ------------------------------------------------------------------ entering a world

        private static IEnumerator StartWorld(Ctx c)
        {
            var fs = NeedMenu();
            var profile = SelectProfile(fs, c.Str("character"));
            var world = FindWorld(c.Need("world"));
            if (world.m_dataError != World.SaveDataError.None)
                throw Ctx.Fail($"world '{world.m_name}' has a data error: {world.m_dataError}");

            bool open = c.Bool("open", false);
            bool isPublic = c.Bool("public", false);
            string password = c.Str("password", "");
            if (open && isPublic && password.Length < 5)
                throw Ctx.Fail("public servers need a password of 5+ characters");

            DismissAll();
            var t = Traverse.Create(fs);
            t.Field("m_world").SetValue(world);
            ZNet.m_onlineBackend = c.Bool("crossplay", false) ? OnlineBackendType.PlayFab : OnlineBackendType.Steamworks;
            ZSteamMatchmaking.instance?.StopServerListing();
            t.Field("m_startingWorld").SetValue(true);
            ZNet.SetServer(server: true, open, isPublic, world.m_name, password, world);
            ZNet.ResetServerHost();
            Plugin.Log.LogInfo($"start_world: '{world.m_name}' as '{profile.GetName()}' (open={open})");
            t.Method("TransitionToMainScene").GetValue();

            if (!c.Bool("wait", true))
            {
                c.Result = new { world = world.m_name, character = profile.GetName(), waiting = false };
                yield break;
            }
            var waiter = WaitIngame(c, c.Float("timeout", 240f));
            while (waiter.MoveNext()) yield return waiter.Current;
            c.Result = new { world = world.m_name, character = profile.GetName(), state = GameState() };
        }

        private static IEnumerator JoinServer(Ctx c)
        {
            var fs = NeedMenu();
            string address = c.Need("address");
            if (!address.Contains(":")) address += ":2456";
            var profile = SelectProfile(fs, c.Str("character"));

            // ZNet answers the server's password prompt from this property.
            string password = c.Str("password");
            AccessTools.Property(typeof(FejdStartup), "ServerPassword").SetValue(null, string.IsNullOrEmpty(password) ? null : password);

            DismissAll();
            ZNet.m_onlineBackend = OnlineBackendType.Steamworks;
            Traverse.Create(fs).Field("m_joinServer").SetValue(new ServerJoinData(new ServerJoinDataDedicated(address)));
            Plugin.Log.LogInfo($"join_server: {address} as '{profile.GetName()}'");
            fs.JoinServer();

            if (!c.Bool("wait", true))
            {
                c.Result = new { address, character = profile.GetName(), waiting = false };
                yield break;
            }
            var waiter = WaitIngame(c, c.Float("timeout", 240f));
            while (waiter.MoveNext()) yield return waiter.Current;
            c.Result = new { address, character = profile.GetName(), state = GameState() };
        }

        private static IEnumerator Logout(Ctx c)
        {
            if (Game.instance == null || FejdStartup.instance != null)
                throw Ctx.Fail($"not in a world (state={GameState()})");
            InputOverride.Clear();
            DismissAll();
            Game.instance.Logout(save: c.Bool("save", true));
            if (!c.Bool("wait", true))
            {
                c.Result = new { logging_out = true };
                yield break;
            }
            float deadline = Time.realtimeSinceStartup + c.Float("timeout", 120f);
            while (GameState() != "menu")
            {
                if (Time.realtimeSinceStartup > deadline) throw Ctx.Fail("timed out waiting for the main menu");
                yield return null;
            }
            c.Result = new { state = "menu" };
        }

        /// Wait until the player is spawned and ready, failing fast on a connection error.
        private static IEnumerator WaitIngame(Ctx c, float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            float sawMenuAgainAt = -1f;
            string reason = null;
            // Give the scene transition a moment to start before reading the state.
            yield return new WaitForSecondsRealtime(1f);
            while (!PlayerReady(out reason))
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw Ctx.Fail($"timed out waiting to enter the world ({reason}, state={GameState()})");
                if (GameState() == "menu")
                {
                    string err = ConnectError();
                    if (err != null) throw Ctx.Fail($"connection failed: {err}");
                    if (sawMenuAgainAt < 0) sawMenuAgainAt = Time.realtimeSinceStartup;
                    else if (Time.realtimeSinceStartup - sawMenuAgainAt > 20f)
                        throw Ctx.Fail("still at the main menu 20s after starting; check 'log'");
                }
                else sawMenuAgainAt = -1f;
                yield return null;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void GuardDestructive(Ctx c)
        {
            if (!Plugin.Isolated && !c.Bool("force", false))
                throw Ctx.Fail("refusing to delete from the real save directory; launch isolated (AUTOHEIM_SAVEDIR) or pass force=true");
        }

        private static PlayerProfile FindProfile(string name)
        {
            var profiles = SaveSystem.GetAllPlayerProfiles();
            var p = profiles.FirstOrDefault(x => string.Equals(x.GetName(), name, StringComparison.OrdinalIgnoreCase))
                 ?? profiles.FirstOrDefault(x => string.Equals(x.GetFilename(), name, StringComparison.OrdinalIgnoreCase));
            if (p == null)
                throw Ctx.Fail($"no character '{name}'. Known: {string.Join(", ", profiles.Select(x => x.GetName()))}");
            return p;
        }

        private static World FindWorld(string name)
        {
            var worlds = SaveSystem.GetWorldList();
            var w = worlds.FirstOrDefault(x => string.Equals(x.m_name, name, StringComparison.OrdinalIgnoreCase));
            if (w == null)
                throw Ctx.Fail($"no world '{name}'. Known: {string.Join(", ", worlds.Select(x => x.m_name))}");
            return w;
        }

        /// Pick the named character (or the only/first one) and make it the active game profile.
        private static PlayerProfile SelectProfile(FejdStartup fs, string name)
        {
            var profiles = SaveSystem.GetAllPlayerProfiles();
            if (profiles.Count == 0) throw Ctx.Fail("no characters; use create_character first");
            var profile = string.IsNullOrEmpty(name) ? profiles[0] : FindProfile(name);
            var t = Traverse.Create(fs);
            t.Field("m_profiles").SetValue(profiles);
            t.Field("m_profileIndex").SetValue(profiles.FindIndex(p => p.GetFilename() == profile.GetFilename()));
            Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
            return profile;
        }

        private static void ShowMainMenu(FejdStartup fs)
        {
            Traverse.Create(fs).Method("HideAll").GetValue();
            fs.m_mainMenu.SetActive(true);
        }

        private static string MenuPanel(FejdStartup fs)
        {
            if (fs.m_loading != null && fs.m_loading.activeInHierarchy) return "loading";
            if (fs.m_connectionFailedPanel != null && fs.m_connectionFailedPanel.activeInHierarchy) return "connection_failed";
            if (fs.m_newCharacterPanel.activeInHierarchy) return "new_character";
            if (fs.m_characterSelectScreen.activeInHierarchy) return "character_select";
            if (fs.m_createWorldPanel.activeInHierarchy) return "create_world";
            if (fs.m_startGamePanel.activeInHierarchy) return "start_game";
            if (fs.m_mainMenu.activeInHierarchy) return "main_menu";
            return "other";
        }
    }
}
