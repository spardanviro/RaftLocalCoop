using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch: ResourceCollector.Start — 给 P2 采集工具补订阅 OnHarvest
    //
    //  vanilla Start 里 `OnChannelFinished += OnHarvest` 只在 playerNetwork.IsLocalPlayer
    //  时执行。P2 是克隆体(Start 时 isLocalPlayer=false) → 永远不订阅 → 蓄力读满后
    //  OnChannelFinished.CallSafe() 是空回调 → 只播音效+转圈,不采集不扣耐久("读满也不掉")。
    //  这里在 Start 后,对属于 P2 的采集工具补挂 OnHarvest(去重)。采集完成在 P2ToolRunner 的
    //  Tool(routeInventory) scope 内执行 → IsLocalPlayer 被强制 true、AddItem 落 P2 背包。
    // ══════════════════════════════════════════════════════════════════════
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
            if (!_wired.Add(__instance)) return;        // 已挂过 → 防重复订阅
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
            if (!Main.p2UsingItemActive || Main.player2 == null) return true;   // 非 P2 工具上下文 → vanilla(查 P1)
            var so = __instance.resource;
            if (so == null) return true;
            var held = Main.GetP2HeldHotbarItem()?.baseItem;                    // P2 当前手持物(mod 权威)
            __result = held != null && held == so.collectionItem;
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  P2 采集蓄力【宽限窗口】—— 桥接 P2 瞄准射线的瞬时丢失
    //
    //  ResourceCollector.ChannelItem 要求射线【连续】命中目标 ~1s 才完成采集。P1 第一人称
    //  准心稳定;但 P2 第三人称相机在身后,对又近又会移动的羊驼,细射线会间歇 miss → 每次 miss
    //  调 ResetItemChannel 把蓄力清零 → 圆圈反复刷新却永远读不满,不掉羊毛(日志实测)。
    //
    //  修:P2 采集上下文中,若仍按住使用键且【最近 0.4s 内命中过】,跳过这次"丢失复位",
    //   保住蓄力进度直到读满;真正移开(超宽限)才复位。排除两类正常复位:松手复位(hold=false)、
    //   蓄满那次复位(cur>=orig,否则会卡满值每帧连发)。仅作用于 ResourceCollector + P2。
    // ══════════════════════════════════════════════════════════════════════
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
                P2ResourceChannel._lastHitTime = Time.time;   // 记录最近一次成功命中
        }
    }

    [HarmonyPatch(typeof(UseableItem), "ResetItemChannel")]
    static class Patch_UseableItem_ResetItemChannel_P2Grace
    {
        static bool Prefix(UseableItem __instance)
        {
            if (!Main.p2UsingItemActive) return true;                 
            if (!(__instance is ResourceCollector)) return true;      // 仅采集类(铲/神秘包等不介入)
            if (!P2ResourceChannel.IsP2(__instance)) return true;
            bool hold = (bool)(P2ResourceChannel.fHold?.GetValue(__instance) ?? false);
            if (!hold) return true;                                   // 松手/收工 → 正常复位
            float cur  = (float)(P2ResourceChannel.fCur?.GetValue(__instance) ?? 0f);
            float orig = (float)(P2ResourceChannel.fOrig?.GetValue(__instance) ?? 1f);
            if (cur >= orig) return true;                             // 蓄满那次复位 → 放行(否则卡满值连发)
            if (Time.time - P2ResourceChannel._lastHitTime > P2ResourceChannel.GraceSeconds) return true; // 超宽限 → 复位
            return false;                                            
        }
    }
}
