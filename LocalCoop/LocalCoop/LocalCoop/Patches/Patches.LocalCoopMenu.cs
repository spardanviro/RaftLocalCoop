using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SplitScreen
{
    internal enum SplitMode { SideBySide, DualMonitor }

    static class LocalCoopMenu
    {
        internal const int Index = 3;

        static bool IsZh()
        {
            var code = I2.Loc.LocalizationManager.CurrentLanguageCode;
            return !string.IsNullOrEmpty(code) && code.ToLowerInvariant().StartsWith("zh");
        }
        static string LabelLocalCoop() => IsZh() ? "本地双人" : "Local Co-op";
        static string LabelSplit(int v) => IsZh() ? (v == 1 ? "双显示器" : "左右分屏")
                                                  : (v == 1 ? "Dual Monitor" : "Side-by-Side");

        static readonly FieldInfo f_newDd  = AccessTools.Field(typeof(NewGameBox),  "authSettingDropdown");
        static readonly FieldInfo f_loadDd = AccessTools.Field(typeof(LoadGameBox), "authSettingDropdown");
        static readonly FieldInfo f_newFF  = AccessTools.Field(typeof(NewGameBox),  "toggle_FriendlyFire");
        static readonly FieldInfo f_loadFF = AccessTools.Field(typeof(LoadGameBox), "allowFriendlyFireToggle");

        internal static Dropdown NewDd(NewGameBox b)   => f_newDd  != null ? f_newDd.GetValue(b)  as Dropdown : null;
        internal static Dropdown LoadDd(LoadGameBox b) => f_loadDd != null ? f_loadDd.GetValue(b) as Dropdown : null;
        internal static Toggle   NewFF(NewGameBox b)   => f_newFF  != null ? f_newFF.GetValue(b)  as Toggle   : null;
        internal static Toggle   LoadFF(LoadGameBox b) => f_loadFF != null ? f_loadFF.GetValue(b) as Toggle   : null;

        sealed class SplitToggle { public GameObject Go; public Text Caption; public int Val; }
        static SplitToggle _newSm, _loadSm;

        internal static void AddOption(Dropdown dd)
        {
            if (dd == null) return;
            string label = LabelLocalCoop();
            if (dd.options.Count == Index)
            {
                dd.options.Add(new Dropdown.OptionData(label));
                dd.RefreshShownValue();
            }
            else if (dd.options.Count > Index && dd.options[Index].text != label)
            {
                dd.options[Index].text = label;
                dd.RefreshShownValue();
            }
        }

        static void ShowFriendlyFireIfLocalCoop(Dropdown dd, Toggle ff)
        {
            if (dd == null || ff == null || dd.value != Index) return;
            if (!ff.gameObject.activeSelf) ff.gameObject.SetActive(true);
        }

        static SplitToggle EnsureSplitToggle(Dropdown src, ref SplitToggle ctrl)
        {
            if (ctrl != null && ctrl.Go != null) return ctrl;
            ctrl = null;
            if (src == null || src.transform.parent == null) return null;
            var go = Object.Instantiate(src.gameObject, src.transform.parent);
            go.name = "SplitModeToggle";
            var srcDd = go.GetComponent<Dropdown>();
            Text caption = srcDd != null ? srcDd.captionText : null;
            if (caption == null) caption = go.GetComponentInChildren<Text>(true);
            foreach (var comp in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (comp != null && comp.GetType().Name.Contains("Localize")) Object.DestroyImmediate(comp);
            var _kill = new List<GameObject>();
            if (srcDd != null && srcDd.template != null) _kill.Add(srcDd.template.gameObject);
            foreach (var rtc in go.GetComponentsInChildren<RectTransform>(true))
                if (rtc != null && rtc.gameObject != go && (rtc.name.Contains("Template") || rtc.name.Contains("Dropdown List")))
                    _kill.Add(rtc.gameObject);
            if (srcDd != null) Object.DestroyImmediate(srcDd);
            foreach (var k in _kill) if (k != null) Object.DestroyImmediate(k);
            var le = go.GetComponent<LayoutElement>(); if (le == null) le = go.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            var rt = go.GetComponent<RectTransform>(); var srt = src.GetComponent<RectTransform>();
            if (rt != null && srt != null) rt.localPosition = srt.localPosition + new Vector3(srt.rect.width + 16f, 0f, 0f);
            var c = new SplitToggle { Go = go, Caption = caption, Val = 0 };
            var btn = go.GetComponent<Button>(); if (btn == null) btn = go.AddComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(() => { c.Val ^= 1; if (c.Caption != null) c.Caption.text = LabelSplit(c.Val); });
            if (caption != null) caption.text = LabelSplit(0);
            ctrl = c;
            return c;
        }

        static void TickSplitToggle(Dropdown authDd, ref SplitToggle ctrl)
        {
            if (authDd == null) return;
            bool localCoop = authDd.value == Index;
            var c = localCoop ? EnsureSplitToggle(authDd, ref ctrl) : ctrl;
            if (c == null) return;
            if (c.Go != null && c.Go.activeSelf != localCoop) c.Go.SetActive(localCoop);
            if (localCoop && c.Caption != null)
            {
                string lbl = LabelSplit(c.Val);
                if (c.Caption.text != lbl) c.Caption.text = lbl;
            }
        }

        static SplitMode Read(SplitToggle c) => (c != null && c.Val == 1) ? SplitMode.DualMonitor : SplitMode.SideBySide;

        internal static void TickNew(NewGameBox b)
        { var dd = NewDd(b); AddOption(dd); ShowFriendlyFireIfLocalCoop(dd, NewFF(b)); TickSplitToggle(dd, ref _newSm); }
        internal static void TickLoad(LoadGameBox b)
        { var dd = LoadDd(b); AddOption(dd); ShowFriendlyFireIfLocalCoop(dd, LoadFF(b)); TickSplitToggle(dd, ref _loadSm); }
        internal static SplitMode NewSplitMode()  => Read(_newSm);
        internal static SplitMode LoadSplitMode() => Read(_loadSm);
    }

    [HarmonyPatch(typeof(NewGameBox), "Update")]
    static class Patch_NewGameBox_Update_AddLocalCoop
    { static void Postfix(NewGameBox __instance) => LocalCoopMenu.TickNew(__instance); }

    [HarmonyPatch(typeof(LoadGameBox), "Update")]
    static class Patch_LoadGameBox_Update_AddLocalCoop
    { static void Postfix(LoadGameBox __instance) => LocalCoopMenu.TickLoad(__instance); }

    [HarmonyPatch(typeof(NewGameBox), "Button_CreateNewGame")]
    static class Patch_NewGameBox_Create_FlagLocalCoop
    {
        static void Prefix(NewGameBox __instance)
        {
            var dd = LocalCoopMenu.NewDd(__instance);
            Main.PendingLocalCoop = dd != null && dd.value == LocalCoopMenu.Index;
            if (Main.PendingLocalCoop) Main.PendingSplitMode = LocalCoopMenu.NewSplitMode();
        }
    }

    [HarmonyPatch(typeof(LoadGameBox), "Button_LoadGame")]
    static class Patch_LoadGameBox_Load_FlagLocalCoop
    {
        static void Prefix(LoadGameBox __instance)
        {
            var dd = LocalCoopMenu.LoadDd(__instance);
            Main.PendingLocalCoop = dd != null && dd.value == LocalCoopMenu.Index;
            if (Main.PendingLocalCoop) Main.PendingSplitMode = LocalCoopMenu.LoadSplitMode();
        }
    }

    [HarmonyPatch(typeof(PauseMenu), "Pause")]
    static class Patch_PauseMenu_Pause_NoFreezeInSplit
    { static void Postfix() { if (Main._splitActive) Time.timeScale = 1f; } }

    // PauseMenu's original unstuck branch reads ComponentManager<Network_Player>.
    // That singleton can briefly retain the destroyed P2 spawn object, so resolve
    // the real host player directly while split-screen is active.
    [HarmonyPatch(typeof(PauseMenu), "HandleToggleUnstuckPanel")]
    static class Patch_PauseMenu_UnstuckPanel_P1Scope
    {
        static bool Prefix(PauseMenu __instance)
        {
            if (!Main._splitActive) return true;

            if (__instance.unstuckPanel != null)
                __instance.unstuckPanel.SetActive(true);
            return false;
        }
    }

    [HarmonyPatch(typeof(PauseMenu), "UnstuckLocalPlayer")]
    static class Patch_PauseMenu_UnstuckPlayer_P1Scope
    {
        static bool Prefix(PauseMenu __instance)
        {
            if (!Main._splitActive) return true;

            var p1 = Main.player1;
            var tracker = p1 != null ? p1.UnstuckTracker : null;
            if (tracker != null)
                tracker.Unstuck();
            if (__instance.unstuckPanel != null)
                __instance.unstuckPanel.SetActive(false);
            return false;
        }
    }
}
