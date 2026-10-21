using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutoHeim
{
    internal delegate IEnumerator CommandFn(Ctx c);

    internal sealed class CommandDef
    {
        internal string Name;
        internal string Usage;
        internal string Help;
        internal CommandFn Fn;
        /// Default seconds the HTTP thread waits; "timeout" arg overrides.
        internal float Wait;
    }

    /// <summary>Command registry plus the general-purpose commands (help, status, log, console, quit...).</summary>
    internal static partial class Commands
    {
        private static readonly Dictionary<string, CommandDef> Registry = new Dictionary<string, CommandDef>();

        static Commands()
        {
            RegisterGeneral();
            RegisterMenu();
            RegisterPlayer();
            RegisterControl();
            RegisterWorld();
            RegisterMedia();
            RegisterObserve();
            RegisterBuild();
            RegisterUi();
        }

        internal static void Add(string name, string usage, string help, CommandFn fn, float wait = 15f)
            => Registry[name] = new CommandDef { Name = name, Usage = usage, Help = help, Fn = fn, Wait = wait };

        internal static CommandDef Get(string name) => Registry.TryGetValue(name, out var d) ? d : null;

        internal static float WaitBudget(string name, JObject args)
        {
            var def = Get(name);
            float t = def?.Wait ?? 15f;
            var to = args?["timeout"];
            if (to != null && to.Type != JTokenType.Null && float.TryParse(to.ToString(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                t = f;
            if (args?["seconds"] != null && float.TryParse(args["seconds"].ToString(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s))
                t = Mathf.Max(t, s);
            return t + 10f;
        }

        // ------------------------------------------------------------------ state helpers

        internal static string GameState()
        {
            if (FejdStartup.instance != null)
                return SceneManager.GetActiveScene().name == "start" ? "menu" : "loading";
            var p = Player.m_localPlayer;
            if (p == null)
            {
                if (ZNet.instance == null) return "loading";
                var cs = ZNet.GetConnectionStatus();
                return cs == ZNet.ConnectionStatus.Connecting ? "connecting" : "loading";
            }
            if (p.IsDead()) return "dead";
            return "ingame";
        }

        /// True when the local player exists and can act: spawned, out of the
        /// intro, not mid-teleport, and the ground around it has loaded.
        internal static bool PlayerReady(out string why)
        {
            var p = Player.m_localPlayer;
            why = null;
            if (p == null) { why = "no local player"; return false; }
            if (p.IsDead()) { why = "player is dead"; return false; }
            if (p.InIntro()) { why = "player in intro"; return false; }
            if (p.IsTeleporting()) { why = "player teleporting"; return false; }
            // e.g. the wake-up animation after spawning; the game ignores all input until it ends.
            if (p.InCutscene()) { why = "player in a cutscene animation (input blocked)"; return false; }
            if (ZNetScene.instance != null && !ZNetScene.instance.IsAreaReady(p.transform.position))
            { why = "area not loaded"; return false; }
            return true;
        }

        internal static Player NeedPlayer()
        {
            var p = Player.m_localPlayer;
            if (p == null) throw Ctx.Fail($"not in game (state={GameState()})");
            return p;
        }

        internal static FejdStartup NeedMenu()
        {
            var fs = FejdStartup.instance;
            if (fs == null || GameState() != "menu") throw Ctx.Fail($"not at main menu (state={GameState()}); use 'logout' first");
            return fs;
        }

        // ------------------------------------------------------------------ general commands

        private static void RegisterGeneral()
        {
            Add("help", "help [name=<cmd>]", "List commands, or show one command's usage.", Help, 5f);
            Add("status", "status", "Game state (menu/loading/connecting/ingame/dead), window, network and player summary.", Status, 5f);
            Add("wait", "wait state=<menu|ingame> [timeout=120]",
                "Block until the game reaches a state. 'ingame' also waits for the player to be ready (out of intro, area loaded).",
                Wait, 120f);
            Add("log", "log [after=<seq>] [max=200] [contains=<text>]",
                "Recent BepInEx/Unity log lines. Pass the returned 'seq' as 'after' next time to get only new lines.", LogCmd, 5f);
            Add("console", "console text=\"<command line>\"",
                "Run an in-game console command (e.g. 'devcommands', 'spawn Boar 2', 'tod 0.5'). Returns new console output.",
                ConsoleCmd, 10f);
            Add("chat", "chat text=<message> [shout=false]", "Send a chat message as the player.", ChatCmd, 5f);
            Add("popups", "popups [dismiss=false]", "List visible popups/dialogs; dismiss=true closes them.", Popups, 5f);
            Add("quit", "quit", "Quit the game process cleanly (saves if in game).", Quit, 10f);
        }

        private static IEnumerator Help(Ctx c)
        {
            string name = c.Str("name");
            if (name != null)
            {
                var d = Get(name) ?? throw Ctx.Fail($"unknown command '{name}'");
                c.Result = new { name = d.Name, usage = d.Usage, help = d.Help };
            }
            else
            {
                c.Result = Registry.Values.OrderBy(d => d.Name)
                    .Select(d => new { name = d.Name, usage = d.Usage, help = d.Help }).ToList();
            }
            yield break;
        }

        private static IEnumerator Status(Ctx c)
        {
            var p = Player.m_localPlayer;
            PlayerReady(out string notReady);
            object net = null;
            if (ZNet.instance != null)
            {
                net = new
                {
                    connection = ZNet.GetConnectionStatus().ToString(),
                    is_server = ZNet.instance.IsServer(),
                    world = ZNet.instance.GetWorldName(),
                    players = ZNet.instance.GetPlayerList().Select(pi => pi.m_name).ToList(),
                };
            }
            c.Result = new
            {
                state = GameState(),
                ready = notReady == null,
                not_ready_reason = notReady,
                scene = SceneManager.GetActiveScene().name,
                menu = FejdStartup.instance != null ? MenuPanel(FejdStartup.instance) : null,
                popup = UnifiedPopup.IsVisible(),
                isolated_saves = Plugin.Isolated,
                save_dir = Utils.GetSaveDataPath(FileHelpers.FileSource.Local),
                window = new { width = Screen.width, height = Screen.height, focused = Application.isFocused, fps = Json.R(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f)) },
                net,
                player = p == null ? null : new
                {
                    name = p.GetPlayerName(),
                    position = Json.Vec(p.transform.position),
                    health = Json.R(p.GetHealth()),
                    dead = p.IsDead(),
                },
                control = InputOverride.Describe(),
                version = Plugin.PluginVersion,
            };
            yield break;
        }

        private static IEnumerator Wait(Ctx c)
        {
            string target = c.Need("state");
            float deadline = Time.realtimeSinceStartup + c.Float("timeout", 120f);
            string reason = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                string state = GameState();
                if (target == "ingame")
                {
                    if (PlayerReady(out reason)) break;
                    // A failed connection drops us back to the menu: report it rather than waiting forever.
                    if (state == "menu" && ConnectError() != null)
                        throw Ctx.Fail($"connection failed: {ConnectError()}");
                }
                else if (state == target) { reason = null; break; }
                else reason = $"state is {state}";
                yield return null;
            }
            if (Time.realtimeSinceStartup >= deadline)
                throw Ctx.Fail($"timed out waiting for '{target}' ({reason ?? GameState()})");
            c.Result = new { state = GameState() };
        }

        private static IEnumerator LogCmd(Ctx c)
        {
            var (last, lines) = LogTail.Since((long)c.Float("after", 0), c.Int("max", 200), c.Str("contains"));
            c.Result = new { seq = last, lines };
            yield break;
        }

        private static IEnumerator ConsoleCmd(Ctx c)
        {
            string text = c.Need("text");
            var console = Console.instance ?? throw Ctx.Fail("console not available yet");
            var buffer = AccessField<List<string>>(console, "m_chatBuffer");
            int before = buffer?.Count ?? 0;
            console.TryRunCommand(text, silentFail: false, skipAllowedCheck: c.Bool("force", false));
            yield return null;
            var output = buffer == null ? new List<string>() : buffer.Skip(Math.Min(before, buffer.Count)).ToList();
            c.Result = new { ran = text, output };
        }

        private static IEnumerator ChatCmd(Ctx c)
        {
            NeedPlayer();
            string text = c.Need("text");
            var chat = Chat.instance ?? throw Ctx.Fail("chat not available");
            chat.SendText(c.Bool("shout", false) ? Talker.Type.Shout : Talker.Type.Normal, text);
            c.Result = new { sent = text };
            yield break;
        }

        private static IEnumerator Popups(Ctx c)
        {
            var open = new List<string>();
            if (UnifiedPopup.IsVisible()) open.Add("unified_popup");
            var fs = FejdStartup.instance;
            if (fs != null)
            {
                foreach (var f in new[] { "m_connectionFailedPanel", "m_removeCharacterDialog", "m_removeWorldDialog",
                             "m_cloudStorageWarningNextSave", "m_worldVersionPanel", "m_playerVersionPanel", "m_newGameVersionPanel", "m_changeLog" })
                {
                    var go = AccessField<GameObject>(fs, f);
                    if (go != null && go.activeInHierarchy) open.Add(f.Substring(2));
                }
            }
            if (ZNet.instance != null && ZNet.instance.InPasswordDialog()) open.Add("password_dialog");
            if (Menu.IsVisible()) open.Add("game_menu");
            if (InventoryGui.IsVisible()) open.Add("inventory");
            if (Minimap.IsOpen()) open.Add("map");
            if (TextInput.IsVisible()) open.Add("text_input");

            if (c.Bool("dismiss", false)) DismissAll();
            c.Result = new { open, connect_error = ConnectError(), dismissed = c.Bool("dismiss", false) };
            yield break;
        }

        internal static void DismissAll()
        {
            for (int i = 0; i < 10 && UnifiedPopup.IsVisible(); i++) UnifiedPopup.Pop();
            var fs = FejdStartup.instance;
            if (fs != null)
            {
                foreach (var f in new[] { "m_connectionFailedPanel", "m_removeCharacterDialog", "m_removeWorldDialog",
                             "m_cloudStorageWarningNextSave", "m_changeLog" })
                    AccessField<GameObject>(fs, f)?.SetActive(false);
            }
            if (Menu.IsVisible()) Menu.instance.Hide();
            if (InventoryGui.IsVisible()) InventoryGui.instance.Hide();
            if (Minimap.IsOpen()) Minimap.instance.SetMapMode(Minimap.MapMode.Small);
        }

        internal static string ConnectError()
        {
            var fs = FejdStartup.instance;
            if (fs == null) return null;
            var panel = AccessField<GameObject>(fs, "m_connectionFailedPanel");
            if (panel == null || !panel.activeInHierarchy) return null;
            var text = AccessField<TMPro.TMP_Text>(fs, "m_connectionFailedError");
            return text != null ? text.text : "unknown";
        }

        private static IEnumerator Quit(Ctx c)
        {
            c.Result = new { quitting = true };
            if (Game.instance != null && Player.m_localPlayer != null)
            {
                Game.instance.SavePlayerProfile(setLogoutPoint: true);
                if (ZNet.instance != null && ZNet.instance.IsServer()) ZNet.instance.Save(sync: true);
            }
            // Let the HTTP reply go out before the process dies.
            Runner.Instance.StartCoroutine(QuitSoon());
            yield break;
        }

        private static IEnumerator QuitSoon()
        {
            yield return new WaitForSecondsRealtime(0.5f);
            Application.Quit();
        }

        internal static T AccessField<T>(object obj, string field) where T : class
        {
            var fi = HarmonyLib.AccessTools.Field(obj.GetType(), field);
            return fi?.GetValue(obj) as T;
        }
    }
}
