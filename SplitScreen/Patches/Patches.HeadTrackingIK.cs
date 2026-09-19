using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // 分屏 NPC 头部朝向硬崩溃根治(原生访问违例、游戏闪退)。
    // 崩溃栈:HeadTrackingIK.HeadTrackingOn/LateUpdate → IsAngleSolvable → target.position →
    //         UnityEngine.Transform.get_position_Injected(原生 NRE)。
    // HeadTrackingIK.target 恒被 vanilla 设为「交互玩家的 CameraTransform」
    //(DialogueHeadtrackingIKConnection / QuestInteractable_ComponentData_VoiceLine /
    //  CharacterUnlock / OlofFinalRoom 等)。分屏下 mod 会重建/切换相机,NPC 缓存的旧相机
    // Transform 变悬空——Unity 的 `== null` 因 m_CachedPtr 非零仍报「非空」,但 .position 直接
    // 原生崩(不是可捕获的托管异常)。故不能 try/catch,只能在【解引用发生前】把悬空 target 换掉。
    //
    // 守卫:进入 HeadTrackingOn / 每帧 LateUpdate 前,若 target 既不是 P1 也不是 P2 的【当前】
    // CameraTransform(object.ReferenceEquals 纯托管指针比较,绝不解引用 → 安全),即判定为悬空
    // 旧引用,兜底改指 P1 当前相机。仅分屏(player1 非空)介入;单人零影响。
    internal static class Patch_HeadTrackingIK_TargetGuard
    {
        static bool _logged;

        static void FixStaleTarget(HeadTrackingIK ik)
        {
            if (ik == null) return;
            var t = ik.target;
            if (t == null) return;                 // Unity ==:真正为空交给 vanilla 自身处理
            var p1 = Main.player1;
            if (p1 == null) return;                // 非分屏,不介入
            if (object.ReferenceEquals(t, p1.CameraTransform)) return;
            var p2 = Main.player2;
            if (p2 != null && object.ReferenceEquals(t, p2.CameraTransform)) return;

            // target 既非 P1 也非 P2 当前相机 → 悬空旧引用,换成 P1 当前相机以避免原生崩溃。
            var p1Cam = p1.CameraTransform;
            if (p1Cam == null) return;
            ik.target = p1Cam;
            if (!_logged)
            {
                _logged = true;
                Main.ModEntry.Logger.Log("[HeadTrackIK] 悬空 target 已改指 P1 当前相机(防 NPC 交互原生崩溃)");
            }
        }

        [HarmonyPatch(typeof(HeadTrackingIK), "HeadTrackingOn")]
        static class OnGuard
        {
            static void Prefix(HeadTrackingIK __instance) => FixStaleTarget(__instance);
        }

        [HarmonyPatch(typeof(HeadTrackingIK), "LateUpdate")]
        static class LateGuard
        {
            static void Prefix(HeadTrackingIK __instance) => FixStaleTarget(__instance);
        }
    }
}
