using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // vanilla HideObjectByPlayerDistance(火焰/熔炉/烤架/火炉/火把等特效挂它)只按到"本地玩家 P1"的
    // 距离剔除:objectToHide.SetActiveSafe(Helper.LocalPlayerIsWithinDistance(pos, hideDistance))。
    // 分屏下 P1 远离木筏就关掉特效,哪怕 P2 还站在木筏上。应"P1 或 P2 任一在范围内"就保留。
    //
    // 修法(Postfix,不改 vanilla 关闭逻辑,只补 P2):vanilla 已按 P1 距离设好 objectToHide —— P1 近则开、
    // P1 远则关。若被关(P1 远)但 P2 在 hideDistance 内,则重新开启。P1 单独在场时行为与原版一致。
    [HarmonyPatch(typeof(HideObjectByPlayerDistance), "Update")]
    static class Patch_HideObjectByPlayerDistance_IncludeP2
    {
        static readonly FieldInfo s_objectToHide =
            AccessTools.Field(typeof(HideObjectByPlayerDistance), "objectToHide");
        static readonly FieldInfo s_hideDistance =
            AccessTools.Field(typeof(HideObjectByPlayerDistance), "hideDistance");

        static void Postfix(HideObjectByPlayerDistance __instance)
        {
            var p2 = Main.player2;
            if (p2 == null || s_objectToHide == null || s_hideDistance == null) return;

            var obj = s_objectToHide.GetValue(__instance) as GameObject;
            if (obj == null || obj.activeSelf) return;   // 无对象、或 vanilla 已开(P1 近)→ 无需补

            float hideDistance = (float)s_hideDistance.GetValue(__instance);
            if (Vector3.Distance(p2.transform.position, __instance.transform.position) <= hideDistance)
                obj.SetActive(true);
        }
    }
}
