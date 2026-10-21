using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace AutoHeim
{
    /// <summary>
    /// Patches that make an unattended client practical: no intro cinematics,
    /// no Valkyrie flight, no tutorial ravens, and no writes to the shared
    /// preferences file while running isolated.
    /// </summary>
    internal static class Patches
    {
        // Startup cinematic is gated on this pref; answer "skip" without persisting it.
        [HarmonyPatch(typeof(PlatformPrefs), nameof(PlatformPrefs.GetBool))]
        private static class Patch_SkipIntroPref
        {
            private static bool Prefix(string name, ref bool __result)
            {
                if (name != "SkipIntroCinematic" || !Plugin.CfgSkipIntros.Value) return true;
                __result = true;
                return false;
            }
        }

        // New characters normally arrive by Valkyrie; spawn them on the ground instead.
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Patch_NoValkyrie
        {
            private static void Prefix(ref bool spawnValkyrie)
            {
                if (Plugin.CfgSkipIntros.Value) spawnValkyrie = false;
            }
        }

        // The lore text / new-world cinematic. Skip it the same way the in-game skip key does.
        [HarmonyPatch(typeof(Game), "Update")]
        private static class Patch_SkipWorldIntro
        {
            private static void Postfix(Game __instance)
            {
                if (!Plugin.CfgSkipIntros.Value || !__instance.InIntro(includeQueued: true)) return;
                if (CinematicsManager.IsPlaying()) CinematicsManager.Stop();
                __instance.SkipIntro();
                Plugin.Log.LogInfo("Skipped world intro.");
            }
        }

        [HarmonyPatch(typeof(Raven), "Awake")]
        private static class Patch_NoTutorials
        {
            private static void Postfix()
            {
                if (Plugin.CfgDisableTutorials.Value) Raven.m_tutorialsEnabled = false;
            }
        }

        // An isolated automation client must not overwrite the human player's
        // settings (resolution, last character, volume...). Prefs still work in memory.
        [HarmonyPatch(typeof(PlatformPrefs), nameof(PlatformPrefs.Save))]
        private static class Patch_NoPrefsSave
        {
            private static bool Prefix() => !Plugin.Isolated;
        }

        // Apply the launcher's requested window size once the menu exists
        // (Valheim applies its saved resolution during startup and would undo an earlier change).
        [HarmonyPatch(typeof(FejdStartup), "Start")]
        private static class Patch_WindowSize
        {
            private static void Postfix(FejdStartup __instance)
            {
                string spec = System.Environment.GetEnvironmentVariable("AUTOHEIM_WINDOW");
                if (string.IsNullOrEmpty(spec)) return;
                var parts = spec.ToLowerInvariant().Split('x');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h)) return;
                __instance.StartCoroutine(Resize(w, h));
            }

            private static IEnumerator Resize(int w, int h)
            {
                yield return null;
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
                Plugin.Log.LogInfo($"Window set to {w}x{h} windowed.");
            }
        }
    }
}
