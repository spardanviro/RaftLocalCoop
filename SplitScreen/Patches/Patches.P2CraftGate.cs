using HarmonyLib;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    
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
