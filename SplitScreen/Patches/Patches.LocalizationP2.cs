using HarmonyLib;

namespace SplitScreen
{
    // P2 向设备放原料的「放入 {CurrentItem}」提示,物品名被污染成 P1 快捷栏选中物
    //(P1 没选时显示原始占位 {CurrentItem})。
    // 根因:LocalizationParameters.GetParameterValue("CurrentItem") 返回
    //   playerInventory.GetSelectedHotbarItem().DisplayName,而 playerInventory 缓存成 P1 的
    //   共享背包 → 取的是 P1 选中槽(P2 手持在独立 _p2Hotbar,不在其中)。
    // 设备提示在 P2 设备射线内经 Helper.GetTerm(applyParameters:true) 同步解析,此时
    //   BeginP2DevicePromptScope 已捕获 P2 手持到 _p2DevicePromptHeldItem(整个设备提示窗口有效)。
    // 修:该窗口内把 {CurrentItem} 解析成 P2 手持物名;窗口外(P1 正常提示)一律走 vanilla。
    [HarmonyPatch(typeof(LocalizationParameters), "GetParameterValue")]
    static class Patch_LocalizationParameters_CurrentItem_P2
    {
        static bool Prefix(string parameter, ref string __result)
        {
            if (parameter != "CurrentItem") return true;
            var held = Main.P2DevicePromptHeldItem;          // 仅 P2 设备提示窗口内非空
            if (held == null || held.baseItem == null || held.baseItem.settings_Inventory == null)
                return true;                                  // 非 P2 提示上下文 → vanilla(P1)
            __result = held.baseItem.settings_Inventory.DisplayName;
            return false;
        }
    }
}
