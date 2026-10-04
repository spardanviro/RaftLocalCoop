using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2 复杂设备通用兜底（B2 框架）—— 让走 OnIsRayed+OpenMenu 的设备默认对 P2 可用。
    //
    //  很多设备(研究台/接收器/烹饪锅/净水器/熔炉/交易站/衣柜…)在 OnIsRayed 里读【全局单例】判定，
    //  而非本地玩家：典型门控 `if (PlayerItemManager.IsBusy || !canvas.CanOpenMenu ||
    //   !Helper.LocalPlayerIsWithinDistance(pos, UseDistance)) return;`。
    //  这些全局值都以【P1】为准 → P2 站在设备前时距离判定用的是 P1 的位置(P1 不在旁)→设备对 P2 没反应。
    //
    //  做法：仅在【P2 设备射线窗口】(isProcessingP2Ray，由 RunDeviceRayAsP2 包住 OnIsRayed)内，
    //   把这三个全局判定改成以 P2 为准 / 放行。窗口外一律走原版，P1 完全不受影响。
    //   配合 RunDeviceRayAsP2(localPlayer→P2 + 手持注入) 与 OpenP2Menu(菜单搬 P2 半屏)，
    //   多数设备无需逐个写补丁即可对 P2 工作。
    //
    //  注：设备打开后把全局 PlayerItemManager.IsBusy 置真会卡住 P1(双人同时性=P3)，
    //   留待 P3 阶段按矩阵处理(save/restore 或 P2 专属忙标志)。
    // ══════════════════════════════════════════════════════════════════════

    // 距离判定：P2 设备射线期间用【P2 位置】判定，否则原版(P1)。
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

    // IsBusy：P2 设备射线期间视为不忙(P1 的忙不应挡住 P2 开设备)。窗口短、仅 P2 设备处理期生效。
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

    // CanOpenMenu：P2 设备射线期间放行(P1 当前菜单态不应挡住 P2 开设备菜单；P2 菜单走 OpenP2Menu 宿主)。
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
