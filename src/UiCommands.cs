using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AutoHeim
{
    /// <summary>
    /// Keys, buttons and the UI: synthetic key and button presses the game reads through ZInput
    /// (so they work unfocused, like the movement overrides), clicking any visible UI button by its
    /// label, typing into the open text box, and listing what can be clicked or typed into.
    /// Mods that read keys through ZInput (GetKeyDown, GetButtonDown ...) see these presses too.
    /// </summary>
    internal static partial class Commands
    {
        private static void RegisterUi()
        {
            Add("key", "key name=<KeyCode: Space|Escape|Return|E|M|Z|Alpha1|F5|...> [hold=0]",
                "Press a keyboard key as the game (and mods) read it through ZInput: down for one frame, held for hold= seconds, then up. Works unfocused.",
                Key, 30f);
            Add("button", "button name=<Use|Map|Inventory|Jump|Attack|SecondaryAttack|Block|Hide|Crouch|...> [hold=0]",
                "Press one of the game's input buttons (an action, whatever key it is bound to). Same timing as 'key'.",
                ButtonCmd, 30f);
            Add("click", "click label=<text> [index=0]",
                "Click a visible, interactable UI button whose label is that text (exact, else contained; case-insensitive). Lists the buttons when none matches.",
                Click, 10f);
            Add("type", "type text=<text> [enter=true]",
                "Type into the open text box (the game's text prompt, or the focused input field) and submit it with Enter.",
                TypeCmd, 10f);
            Add("ui", "ui", "Visible UI: the clickable buttons' labels, input fields, and the text box prompt if one is open.", Ui, 10f);
        }

        // ------------------------------------------------------------------ keys and buttons

        private static IEnumerator Key(Ctx c)
        {
            string name = c.Need("name");
            if (!Enum.TryParse(name, true, out KeyCode code) || code == KeyCode.None)
                throw Ctx.Fail($"unknown key '{name}' (Unity KeyCode names: Space, Escape, Return, E, Alpha1, F5, LeftShift ...)");
            yield return Synthetic.Press(Synthetic.Keys, code, c.Float("hold", 0f));
            c.Result = new { key = code.ToString(), held = c.Float("hold", 0f) };
        }

        private static IEnumerator ButtonCmd(Ctx c)
        {
            string name = c.Need("name");
            yield return Synthetic.Press(Synthetic.Buttons, name, c.Float("hold", 0f));
            c.Result = new { button = name, held = c.Float("hold", 0f) };
        }

        /// <summary>Presses the game reads: per key/button, the frame it went down and the time it comes up.</summary>
        internal static class Synthetic
        {
            internal sealed class State { internal int DownFrame; internal float UpAt; internal int UpFrame = -1; }

            internal static readonly Dictionary<KeyCode, State> Keys = new Dictionary<KeyCode, State>();
            internal static readonly Dictionary<string, State> Buttons = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

            /// Down from the next frame (so every reader this frame and the next sees one clean edge), held, then up.
            internal static IEnumerator Press<T>(Dictionary<T, State> table, T what, float hold)
            {
                var s = new State { DownFrame = Time.frameCount + 1, UpAt = float.MaxValue };
                table[what] = s;
                while (Time.frameCount <= s.DownFrame) yield return null;
                float until = Time.time + Mathf.Max(0f, hold);
                while (Time.time < until) yield return null;
                s.UpFrame = Time.frameCount + 1;
                while (Time.frameCount <= s.UpFrame) yield return null;
                if (table.TryGetValue(what, out var now) && now == s) table.Remove(what);
            }

            internal static bool Down<T>(Dictionary<T, State> table, T what) => table.TryGetValue(what, out var s) && Time.frameCount == s.DownFrame;
            internal static bool Held<T>(Dictionary<T, State> table, T what) => table.TryGetValue(what, out var s) && Time.frameCount >= s.DownFrame && (s.UpFrame < 0 || Time.frameCount < s.UpFrame);
            internal static bool Up<T>(Dictionary<T, State> table, T what) => table.TryGetValue(what, out var s) && Time.frameCount == s.UpFrame;
        }

        [HarmonyPatch]
        private static class Patch_SyntheticInput
        {
            [HarmonyPostfix, HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyDown))]
            private static void KeyDown(KeyCode key, ref bool __result) => __result |= Synthetic.Down(Synthetic.Keys, key);

            [HarmonyPostfix, HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKey))]
            private static void KeyHeld(KeyCode key, ref bool __result) => __result |= Synthetic.Held(Synthetic.Keys, key);

            [HarmonyPostfix, HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyUp))]
            private static void KeyUp(KeyCode key, ref bool __result) => __result |= Synthetic.Up(Synthetic.Keys, key);

            [HarmonyPostfix, HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
            private static void ButtonDown(string name, ref bool __result) => __result |= name != null && Synthetic.Down(Synthetic.Buttons, name);

            [HarmonyPostfix, HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton))]
            private static void ButtonHeld(string name, ref bool __result) => __result |= name != null && Synthetic.Held(Synthetic.Buttons, name);

            [HarmonyPostfix, HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonUp))]
            private static void ButtonUp(string name, ref bool __result) => __result |= name != null && Synthetic.Up(Synthetic.Buttons, name);
        }

        // ------------------------------------------------------------------ UI

        private static string Label(Component c)
        {
            var t = c.GetComponentInChildren<Text>(true)?.text;
            if (string.IsNullOrWhiteSpace(t)) t = c.GetComponentInChildren<TMP_Text>(true)?.text;
            return (t ?? "").Trim();
        }

        private static List<Button> VisibleButtons()
            => Resources.FindObjectsOfTypeAll<Button>()
                .Where(b => b != null && b.gameObject.activeInHierarchy && b.IsInteractable() && b.gameObject.scene.IsValid())
                .ToList();

        private static IEnumerator Click(Ctx c)
        {
            string label = c.Need("label").Trim();
            int index = c.Int("index", 0);
            var all = VisibleButtons().Select(b => (b, l: Label(b))).ToList();
            var exact = all.Where(x => string.Equals(x.l, label, StringComparison.OrdinalIgnoreCase)).ToList();
            var hits = exact.Count > 0 ? exact : all.Where(x => x.l.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (index < 0 || index >= hits.Count)
                throw Ctx.Fail($"no button '{label}'{(hits.Count > 0 ? $" #{index} (of {hits.Count})" : "")}. Visible: {string.Join(", ", all.Select(x => $"'{x.l}'").Distinct())}");
            var hit = hits[index];
            hit.b.onClick.Invoke();
            yield return null;
            c.Result = new { clicked = hit.l, matches = hits.Count };
        }

        private static IEnumerator TypeCmd(Ctx c)
        {
            string text = c.Need("text");
            bool enter = c.Bool("enter", true);
            // the game's text prompt (signs, portal tags, and mods' questions): as its own Enter does
            if (TextInput.IsVisible() && TextInput.instance != null)
            {
                var input = TextInput.instance;
                input.m_inputField.text = text;
                if (enter) input.OnEnter();
                yield return null;
                c.Result = new { typed = text, into = "text_input", entered = enter };
                yield break;
            }
            // else the focused field (or the only visible one)
            var focused = EventSystem.current?.currentSelectedGameObject;
            var tmp = focused?.GetComponent<TMP_InputField>() ?? Fields<TMP_InputField>().SingleOrDefaultIfMany();
            if (tmp != null)
            {
                tmp.text = text;
                if (enter) { tmp.onSubmit?.Invoke(text); tmp.onEndEdit?.Invoke(text); }
                yield return null;
                c.Result = new { typed = text, into = tmp.name, entered = enter };
                yield break;
            }
            var ui = focused?.GetComponent<InputField>() ?? Fields<InputField>().SingleOrDefaultIfMany();
            if (ui != null)
            {
                ui.text = text;
                if (enter) { ui.onSubmit?.Invoke(text); ui.onEndEdit?.Invoke(text); }
                yield return null;
                c.Result = new { typed = text, into = ui.name, entered = enter };
                yield break;
            }
            throw Ctx.Fail("no text box open and no (single) input field visible; see 'ui'");
        }

        private static IEnumerable<T> Fields<T>() where T : Component
            => Resources.FindObjectsOfTypeAll<T>().Where(f => f != null && f.gameObject.activeInHierarchy && f.gameObject.scene.IsValid());

        private static IEnumerator Ui(Ctx c)
        {
            var buttons = VisibleButtons().Select(Label).Where(l => l.Length > 0).ToList();
            var fields = Fields<TMP_InputField>().Select(f => (object)new { name = f.name, text = f.text })
                .Concat(Fields<InputField>().Select(f => (object)new { name = f.name, text = f.text })).ToList();
            string prompt = TextInput.IsVisible() && TextInput.instance != null ? TextInput.instance.m_topic?.text : null;
            yield return null;
            c.Result = new { buttons, input_fields = fields, text_input = prompt };
        }
    }

    internal static class UiExtensions
    {
        /// The only element, or null when there are none or several.
        internal static T SingleOrDefaultIfMany<T>(this IEnumerable<T> items) where T : class
        {
            T one = null;
            foreach (var x in items) { if (one != null) return null; one = x; }
            return one;
        }
    }
}
