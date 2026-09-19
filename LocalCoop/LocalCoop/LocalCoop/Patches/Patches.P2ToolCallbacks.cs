using System;
using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    // 【P2 工具失效·通道二】回调不走 Update,压根不在 scope 里。
    //
    // P2ToolRunner 每帧只驱动工具组件的 Update。凡是由【动画事件 / SendMessage / 协程 / 事件订阅】
    // 触发的 vanilla 逻辑,都在 P2FrameContext 之外执行 —— 那里 P2 克隆体 IsLocalPlayer=false,
    // 于是 if (!playerNetwork.IsLocalPlayer) return; 恒真,整段功能【静默】失效。
    // 典型症状:动作照播、音效照响,但没有任何结果 —— 音效常写在门控之前,故最具迷惑性。
    // 修法统一为:给这些回调打 Prefix/Postfix 各开一个 P2FrameContext,让 vanilla 原样跑,不复刻逻辑。
    //
    // 已收录:
    //   Shovel.DigDown / DigThrow / OnShovelDone   动画事件      —— P2 挖不出探测到的宝藏
    //   SweepNet.OnAnimationEvent                  动画事件订阅  —— P2 网兜抓不到蜜蜂
    // 同族已在别处修过:ThrowableComponent.Throw(协程,见 Patches.P2Combat.cs)。
    // 新增工具时的判据:功能入口是不是 Update?不是 → 必须单独包 scope。

    static class P2ToolCallback
    {
        static readonly FieldInfo PlayerNetworkField =
            AccessTools.Field(typeof(UsableTool), "playerNetwork");

        // 仅当这份实例挂在 P2 身上时才开 scope;P1 的工具一概不碰,保持纯 vanilla。
        internal static P2FrameContext BeginIfP2(UsableTool tool, bool routeInventory)
        {
            if (Main.player2 == null || PlayerNetworkField == null || tool == null) return null;
            var np = PlayerNetworkField.GetValue(tool) as Network_Player;
            if (np == null || np != Main.player2) return null;
            return P2FrameContext.Tool(routeInventory);
        }
    }

    // 铲子·挖:落箱 + 推进挖掘度 + 挖穿时扣耐久(扣的是 P2 手持栏,故需背包路由)。
    [HarmonyPatch(typeof(Shovel), "DigDown")]
    static class Patch_Shovel_DigDown_P2
    {
        static void Prefix(Shovel __instance, ref P2FrameContext __state)
            => __state = P2ToolCallback.BeginIfP2(__instance, routeInventory: true);
        static void Postfix(P2FrameContext __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2FrameContext __state)
        { __state?.Dispose(); return __exception; }
    }

    // 铲子·甩土:只有音效与 closestTreasurePoint 清理,不碰背包。
    [HarmonyPatch(typeof(Shovel), "DigThrow")]
    static class Patch_Shovel_DigThrow_P2
    {
        static void Prefix(Shovel __instance, ref P2FrameContext __state)
            => __state = P2ToolCallback.BeginIfP2(__instance, routeInventory: false);
        static void Postfix(P2FrameContext __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2FrameContext __state)
        { __state?.Dispose(); return __exception; }
    }

    // 铲子·蓄力挖完:拾取物品进 P2 背包 + 扣耐久,两者都要走 P2 容器。
    [HarmonyPatch(typeof(Shovel), "OnShovelDone")]
    static class Patch_Shovel_OnShovelDone_P2
    {
        static void Prefix(Shovel __instance, ref P2FrameContext __state)
            => __state = P2ToolCallback.BeginIfP2(__instance, routeInventory: true);
        static void Postfix(P2FrameContext __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2FrameContext __state)
        { __state?.Dispose(); return __exception; }
    }

    // 网兜·挥网命中:HitAtCursor 要用 P2 相机,AttemptCaptureWithNet 杀死目标实体,
    // 随后 AddItemToInventory 收进 P2 背包 + 扣 P2 手持栏耐久 → 需要背包路由。
    // 只在真正挥网那一帧开 scope:OnAnimationEvent 会被所有动画事件调用,vanilla 首行按
    // parameter 过滤,这里照它的判据提前让路,避免无谓的上下文切换。
    [HarmonyPatch(typeof(SweepNet), "OnAnimationEvent")]
    static class Patch_SweepNet_OnAnimationEvent_P2
    {
        static void Prefix(SweepNet __instance, string parameter, ref P2FrameContext __state)
        {
            if (parameter == null || !parameter.Equals("AnimationSweep")) return;
            __state = P2ToolCallback.BeginIfP2(__instance, routeInventory: true);
        }
        static void Postfix(P2FrameContext __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2FrameContext __state)
        { __state?.Dispose(); return __exception; }
    }
}
