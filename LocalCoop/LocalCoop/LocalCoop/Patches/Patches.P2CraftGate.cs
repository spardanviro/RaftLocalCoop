using HarmonyLib;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch: SelectedRecipeBox.Update — 让 P2 制造在不冻结 P1 的前提下刷新材料/可制造态
    //
    //  原版 Update 仅当 CanvasHelper.ActiveMenu == MenuType.Inventory 才更新 costCollection
    //  和 craftButton.interactable。混合方案为保 P1 自由不设全局 ActiveMenu(保持 None)，
    //  所以这里在 P2 背包打开时【仅在本方法执行期间】把 ActiveMenu 临时设为 Inventory，
    //  方法结束立即还原 → 其它系统(MouseLook/工具)那一帧仍读到 None，P1 不受影响。
    //  （与本 mod 既有的 isLocalPlayer 临时翻转同款手法。）
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(SelectedRecipeBox), "Update")]
    static class Patch_SelectedRecipeBox_Update_P2Gate
    {
        static CanvasActiveMenuScope _menuScope;

        static void Prefix()
        {
            _menuScope = null;
            if (!Main.IsP2BackpackOpen) return;
            if (CanvasHelper.ActiveMenu == MenuType.Inventory) return;
            _menuScope = new CanvasActiveMenuScope(MenuType.Inventory);
        }

        static void Postfix()
        {
            DisposeScope();
        }

        static System.Exception Finalizer(System.Exception __exception)
        {
            DisposeScope();
            return __exception;
        }

        static void DisposeScope()
        {
            _menuScope?.Dispose();
            _menuScope = null;
        }
    }
}
