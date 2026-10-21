using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>
    /// AutoHeim: lets an external agent (Claude Code) drive the Valheim client
    /// over a localhost HTTP/JSON API.
    ///
    /// The plugin is dormant unless activated, so it is safe to leave installed
    /// in a shared plugins folder. Activation sources (first wins):
    ///   env AUTOHEIM_PORT=&lt;port&gt;   (set by tools/autoheim launch)
    ///   config [General] Enabled = true
    ///
    /// Other env knobs (all optional, set by the launcher):
    ///   AUTOHEIM_SAVEDIR   isolate characters/worlds in this directory
    ///   AUTOHEIM_WINDOW    WIDTHxHEIGHT windowed resolution, e.g. 1280x720
    ///   AUTOHEIM_FPS       frame-rate cap (default 60)
    /// </summary>
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "tarbaby.autoheim";
        public const string PluginName = "AutoHeim";
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Log;
        internal static bool Active;
        internal static int Port;
        internal static bool Isolated;

        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<int> CfgPort;
        internal static ConfigEntry<bool> CfgSkipIntros;
        internal static ConfigEntry<bool> CfgDisableTutorials;
        internal static ConfigEntry<string> CfgOutputDir;

        private Harmony _harmony;
        private HttpApi _http;

        internal static string OutputDir
        {
            get
            {
                string dir = CfgOutputDir.Value;
                if (string.IsNullOrWhiteSpace(dir))
                    dir = Path.Combine(Paths.BepInExRootPath, "autoheim_output");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private void Awake()
        {
            Log = Logger;

            CfgEnabled = Config.Bind("General", "Enabled", false,
                "Start the automation API even when AUTOHEIM_PORT is not set.");
            CfgPort = Config.Bind("General", "Port", 7878,
                "Port for the localhost HTTP API (AUTOHEIM_PORT overrides).");
            CfgSkipIntros = Config.Bind("General", "SkipIntros", true,
                "Skip the startup cinematic and the Valkyrie arrival flight.");
            CfgDisableTutorials = Config.Bind("General", "DisableTutorials", true,
                "Suppress Hugin tutorial popups.");
            CfgOutputDir = Config.Bind("General", "OutputDir", "",
                "Where screenshots and audio recordings go. Empty = BepInEx/autoheim_output.");

            string envPort = Environment.GetEnvironmentVariable("AUTOHEIM_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int p))
            {
                Active = true;
                Port = p;
            }
            else if (CfgEnabled.Value)
            {
                Active = true;
                Port = CfgPort.Value;
            }

            if (!Active)
            {
                Log.LogInfo($"{PluginName} v{PluginVersion} dormant (set AUTOHEIM_PORT or [General] Enabled to activate).");
                return;
            }

            string saveDir = Environment.GetEnvironmentVariable("AUTOHEIM_SAVEDIR");
            if (!string.IsNullOrEmpty(saveDir))
            {
                Directory.CreateDirectory(saveDir);
                Utils.SetSaveDataPath(saveDir);
                Isolated = true;
                Log.LogInfo($"Save data isolated to {saveDir}");
            }

            // Note: Application/QualitySettings must not be touched here. The
            // chainloader runs before Unity finishes starting and doing so
            // double-faults the Mono runtime. Runner applies them on its first frame.

            LogTail.Install();

            _harmony = new Harmony(PluginId);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            var host = new GameObject("AutoHeim");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<Runner>();

            _http = new HttpApi(Port);
            try
            {
                _http.Start();
            }
            catch (Exception e)
            {
                Log.LogError($"Could not start HTTP API on 127.0.0.1:{Port}: {e.Message}");
                return;
            }

            Log.LogInfo($"{PluginName} v{PluginVersion} active. API: http://127.0.0.1:{Port}/  output: {OutputDir}");
        }

        internal static int EnvInt(string name, int fallback)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return int.TryParse(v, out int r) ? r : fallback;
        }

        private void OnDestroy()
        {
            _http?.Stop();
            _harmony?.UnpatchSelf();
        }
    }
}
