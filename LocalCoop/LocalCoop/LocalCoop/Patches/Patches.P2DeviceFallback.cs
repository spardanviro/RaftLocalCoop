using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    //
    
    
    
    
    //
    
    
    

    
    [HarmonyPatch(typeof(Helper), "LocalPlayerIsWithinDistance")]
    static class Patch_Helper_LocalPlayerWithinDistance_P2
    {
        static bool Prefix(Vector3 position, float requiredDistance, ref bool __result)
        {
            if (!Main.isProcessingP2Ray || Main.player2 == null) return true;
            __result = Vector3.Distance(Main.player2.transform.position, position) <= requiredDistance;
            return false;
        }
    }

    
    [HarmonyPatch(typeof(PlayerItemManager), "IsBusy", MethodType.Getter)]
    static class Patch_PlayerItemManager_IsBusy_P2
    {
        static bool Prefix(ref bool __result)
        {
            if (!Main.isProcessingP2Ray && !Main.IsP2OriginalInputActive && !Main.P1CarryRayBusyBypass) return true;
            __result = false;
            return false;
        }
    }

    
    [HarmonyPatch(typeof(CanvasHelper), "CanOpenMenu", MethodType.Getter)]
    static class Patch_CanvasHelper_CanOpenMenu_P2
    {
        static bool Prefix(ref bool __result)
        {
            if (!Main.isProcessingP2Ray && !Main.IsP2OriginalInputActive) return true;
            __result = true;
            return false;
        }
    }
}
