using HarmonyLib;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch: CanvasHelper.OpenMenu — P2 借用真背包面板期间，拦截 P1 打开背包
    //
    //  混合方案下 P2 开背包时，真 Inventory_Player 面板被 reparent 到 P2 右半屏。
    //  若此刻 P1 按 Tab，vanilla 会再次 Open/激活同一对象 → 面板被抢回、状态错乱。
    //  这里在 P2 背包打开期间，直接拒绝打开 Inventory 菜单（其它菜单不受影响）。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CanvasHelper), "OpenMenu")]
    static class Patch_CanvasHelper_OpenMenu_BlockWhileP2Backpack
    {
        static bool Prefix(MenuType menuType, ref bool __result)
        {
            return true;
        }
    }
}
