using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    // P2 金属探测器:按 RT 有使用动作,但探测功能不生效。
    // 根因是【更新顺序】而不是门控 —— MetalDetector 组件在 P2 手上是 enabled 的,Unity 每帧照常
    // 调它自己的 Update。那一次在 P2FrameContext 之外,IsLocalPlayer=false → 走 remote 分支:
    //   remote_shouldBeep 只由网络消息 Anim_ItemHit 赋值,而 P2 是本机第二玩家、永远收不到发给自己的
    //   消息 → 恒 false → 方法尾部把 useToolEmitter/flatLineEmitter/beepEmitter 全部 StopSafe,
    //   UpdateLamps 又按 remote_shouldBeep=false 把灯珠归零。
    // 于是 P2ToolRunner 在 scope 内驱动出来的那一帧结果(蜂鸣 + 灯珠)被紧随其后的这次裸调用整个抹掉,
    // 表现就是"有使用动作、却没有任何探测反馈"。
    // 与雪橇车手臂被 PlayerAnimator.Update 每帧抹成 0 同族:vanilla 靠执行顺序恰好成立,P2 是运行时
    // 克隆、顺序不成立。修法同样是【让结果与更新顺序无关】,而不是去调 ScriptExecutionOrder。
    // 具体做法:P2 那份实例只放行 P2ToolRunner 的带 scope 调用,Unity 的裸调用整个跳过 —— remote
    // 分支对 P2 本就没有意义(P2 不是网络远端玩家)。P1 的实例一概不碰,保持纯 vanilla。
    [HarmonyPatch(typeof(MetalDetector), "Update")]
    static class Patch_MetalDetector_Update_P2Order
    {
        static readonly FieldInfo PlayerNetworkField =
            AccessTools.Field(typeof(UsableTool), "playerNetwork");

        static bool Prefix(MetalDetector __instance)
        {
            if (Main.player2 == null || PlayerNetworkField == null) return true;
            var np = PlayerNetworkField.GetValue(__instance) as Network_Player;
            if (np == null || np != Main.player2) return true;   // P1 的实例:原样跑 vanilla

            return Main.p2UsingItemActive;                       // P2:仅放行 P2ToolRunner 那次
        }
    }
}
