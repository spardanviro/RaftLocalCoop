using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    //
    
    
    
    [HarmonyPatch(typeof(Storage_Small), "OnIsRayed")]
    static class Patch_Storage_OnIsRayed_FixManager
    {
        static readonly FieldInfo s_sm = AccessTools.Field(typeof(Storage_Small), "storageManager");

        static void Prefix(Storage_Small __instance)
        {
            if (s_sm == null || Main.player1 == null) return;
            if (s_sm.GetValue(__instance) == null && Main.player1.StorageManager != null)
                s_sm.SetValue(__instance, Main.player1.StorageManager);
        }
    }
}
