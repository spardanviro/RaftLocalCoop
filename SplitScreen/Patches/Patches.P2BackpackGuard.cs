using HarmonyLib;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    [HarmonyPatch(typeof(CanvasHelper), "OpenMenu")]
    static class Patch_CanvasHelper_OpenMenu_BlockWhileP2Backpack
    {
        static bool Prefix(MenuType menuType, ref bool __result)
        {
            return true;
        }
    }
}
