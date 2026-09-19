using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    
    [HarmonyPatch(typeof(ResourceCollector), "Start")]
    static class Patch_ResourceCollector_Start_P2Harvest
    {
        static readonly FieldInfo fNet      = AccessTools.Field(typeof(UsableTool), "playerNetwork");
        static readonly FieldInfo fFinished = AccessTools.Field(typeof(UseableItem), "OnChannelFinished");
        static readonly HashSet<ResourceCollector> _wired = new HashSet<ResourceCollector>();

        static void Postfix(ResourceCollector __instance)
        {
            var np = fNet?.GetValue(__instance) as Network_Player;
            if (np == null || np != Main.player2) return;
            if (!_wired.Add(__instance)) return;        
            var cur = fFinished?.GetValue(__instance) as Action;
            fFinished?.SetValue(__instance, (Action)Delegate.Combine(cur, new Action(__instance.OnHarvest)));
        }
    }
    
    
    //
    
    
    //      IsHoldingRequiredCollectionItem => localPlayer.PlayerItemManager
    //                                          .useItemController.GetCurrentItemInHand() == resource.collectionItem
    
    
    
    //
    
    
    
    
    [HarmonyPatch(typeof(ResourceRegenerative), "get_IsHoldingRequiredCollectionItem")]
    static class Patch_ResourceRegen_IsHoldingRequiredCollectionItem_P2
    {
        static bool Prefix(ResourceRegenerative __instance, ref bool __result)
        {
            if (!Main.p2UsingItemActive || Main.player2 == null) return true;   
            var so = __instance.resource;
            if (so == null) return true;
            var held = Main.GetP2HeldHotbarItem()?.baseItem;                    
            __result = held != null && held == so.collectionItem;
            return false;
        }
    }

    
    
    //
    
    
    
    //
    
    
    
    
    static class P2ResourceChannel
    {
        internal static float _lastHitTime = -999f;
        internal const float GraceSeconds = 0.4f;

        internal static readonly FieldInfo fHold = AccessTools.Field(typeof(UsableTool), "isHoldingUseButton");
        internal static readonly FieldInfo fCur  = AccessTools.Field(typeof(UseableItem), "currentItemChannelTime");
        internal static readonly FieldInfo fOrig = AccessTools.Field(typeof(UseableItem), "originItemChannelTime");
        internal static readonly FieldInfo fNet  = AccessTools.Field(typeof(UsableTool), "playerNetwork");

        internal static bool IsP2(object inst)
        {
            var np = fNet?.GetValue(inst) as Network_Player;
            return np != null && np == Main.player2;
        }
    }

    [HarmonyPatch(typeof(ResourceCollector), "ChannelItem")]
    static class Patch_ResourceCollector_ChannelItem_P2Grace
    {
        static void Postfix(ResourceCollector __instance, bool __result)
        {
            if (__result && Main.p2UsingItemActive && P2ResourceChannel.IsP2(__instance))
                P2ResourceChannel._lastHitTime = Time.time;   
        }
    }

    [HarmonyPatch(typeof(UseableItem), "ResetItemChannel")]
    static class Patch_UseableItem_ResetItemChannel_P2Grace
    {
        static bool Prefix(UseableItem __instance)
        {
            if (!Main.p2UsingItemActive) return true;                 
            if (!(__instance is ResourceCollector)) return true;      
            if (!P2ResourceChannel.IsP2(__instance)) return true;
            bool hold = (bool)(P2ResourceChannel.fHold?.GetValue(__instance) ?? false);
            if (!hold) return true;                                   
            float cur  = (float)(P2ResourceChannel.fCur?.GetValue(__instance) ?? 0f);
            float orig = (float)(P2ResourceChannel.fOrig?.GetValue(__instance) ?? 1f);
            if (cur >= orig) return true;                             
            if (Time.time - P2ResourceChannel._lastHitTime > P2ResourceChannel.GraceSeconds) return true; 
            return false;                                            
        }
    }
}
