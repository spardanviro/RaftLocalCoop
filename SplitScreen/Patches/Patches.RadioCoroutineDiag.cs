using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // 收音机放置报错的可靠修复(与 Sail/PlayerSeat 的 localPlayer 修回同性质:纠正被污染的引用,非掩盖)。
    // HandHeld_Radio.player 在 Start 缓存 ComponentManager<Network_Player>.Value;分屏下该单例会被一个
    // 挂在 UI 层、isLocalPlayer=true 的 Network_Player("Inventory_Player")污染,且它随背包停用 →
    // 缓存到它的收音机 TurnOffRadio 在 inactive 对象启协程报错。这里在 Start 后/关机前把 player 修成
    // 当前真正活跃的本地玩家(P2 上下文→P2,否则→真 P1)。
    // (注:仍在追 CM 单例被污染的源头 setter;根治后本文件可撤。)
    static class RadioPlayerFix
    {
        internal static Network_Player Genuine()
        {
            var p = PlayerContext.Active != null ? PlayerContext.Active : Main.player1;
            if ((UnityEngine.Object)p == null) return null;
            return p.gameObject.activeInHierarchy ? p : null;
        }

        internal static bool Bad(Network_Player p)
        {
            return (UnityEngine.Object)p == null || !p.gameObject.activeInHierarchy;
        }
    }

    [HarmonyPatch(typeof(HandHeld_Radio), "Start")]
    static class Patch_HandHeld_Radio_Start_FixPlayer
    {
        static void Postfix(HandHeld_Radio __instance)
        {
            if (__instance == null) return;
            if (!RadioPlayerFix.Bad(__instance.player)) return;
            var real = RadioPlayerFix.Genuine();
            if (real != null) __instance.player = real;
        }
    }

    [HarmonyPatch(typeof(HandHeld_Radio), "TurnOffRadio")]
    static class Patch_HandHeld_Radio_TurnOffRadio_FixPlayer
    {
        static void Prefix(HandHeld_Radio __instance)
        {
            if (__instance == null) return;
            if (!RadioPlayerFix.Bad(__instance.player)) return;
            var real = RadioPlayerFix.Genuine();
            if (real != null) __instance.player = real;
        }
    }
}
