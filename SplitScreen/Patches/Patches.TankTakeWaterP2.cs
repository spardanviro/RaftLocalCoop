using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // 修 P2 从水箱/净化器取水:满瓶不进 P2 背包。
    // vanilla Tank.CollectItem 用 player.Inventory 的 RemoveSelectedHotSlotItem+AddItem(slot) 换瓶,
    // 但这套对 P2 的 swap 克隆库存不生效(克隆 hotbar 内部 selectedIndex/slot 与 mod 的镜像对不上,
    // 增删丢失)——诊断实测:CollectItem 返回 True 但选中槽仍是空瓶,满瓶消失。
    // 修法:P2 取水时绕开 vanilla 的克隆槽增删,改用 mod 已验证的 SetP2HeldHotbarItem 直接把 P2 手持
    // 从空瓶换成满瓶(海水取水 Main.P2FillWater 同法)。仍返回 true 让 HandleTakeFuel 继续扣减水箱量。
    // 水箱与高级净化器取水机制相同(都走 Tank.CollectItem),故一并修复。
    [HarmonyPatch(typeof(Tank), "CollectItem")]
    static class Patch_Tank_CollectItem_P2
    {
        static bool Prefix(Tank __instance, Network_Player player, ref bool __result)
        {
            if (player == null || player != Main.player2) return true;   // P1 走原版

            var held = Main.GetP2HeldHotbarItem();
            if (held == null || held.baseItem == null || !__instance.IsItemAcceptableOutput(held.baseItem))
            { __result = false; return false; }

            var cons = held.baseItem.settings_consumeable;
            if (cons == null || cons.FoodForm != FoodForm.Fluid)
            { __result = false; return false; }

            if (cons.FoodType == FoodType.None)
            {
                // 空瓶 → 装满(CookingResult),1 次用量,与 vanilla SetUses(1) 一致。
                var cook = held.baseItem.settings_cookable;
                var result = cook != null && cook.CookingResult != null ? cook.CookingResult.item : null;
                if (result == null) { __result = false; return false; }
                Main.SetP2HeldHotbarItem(new ItemInstance(result, 1, 1));
                __result = true;
                return false;
            }

            if (!held.HasMaxUses)
            {
                // 已是水瓶但未满 → 再加 1 次用量。
                int uses = Mathf.Min(held.Uses + 1, held.baseItem.MaxUses);
                Main.SetP2HeldHotbarItem(new ItemInstance(held.baseItem, held.Amount, uses));
                __result = true;
                return false;
            }

            __result = false;
            return false;
        }
    }
}
