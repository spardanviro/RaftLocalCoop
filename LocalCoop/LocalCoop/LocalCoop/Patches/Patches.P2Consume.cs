using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    // P2 用种子种植不消耗种子数量:vanilla PlantManager.PlantSeed 里
    //   if (playerNetwork.IsLocalPlayer) playerNetwork.Inventory.RemoveItem(seedName, 1);
    // 扣的是【共享 Inventory 单例】(P2 与 P1 共享),而 P2 种子在【独立 _p2Hotbar】,
    // 共享 Inventory 的 allSlots 里根本没有 → RemoveItem 搜不到、P2 手持不减(还可能误扣 P1 同名种子)。
    // 【关键:不能广谱 patch Inventory.RemoveItem —— 会误伤放置/建造等其它消耗路径(上一版回归教训)。】
    // 改用转译器,只把【本方法体内那一处】RemoveItem 调用替换成 RouteSeedRemove:P2 scope 内改扣
    // _p2Hotbar,P1 原样走 inv.RemoveItem。种植盆放置根本不调 PlantManager.PlantSeed,天然不受影响。
    [HarmonyPatch(typeof(PlantManager), "PlantSeed")]
    static class Patch_PlantManager_PlantSeed_P2SeedConsume
    {
        static readonly MethodInfo _removeItem =
            AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(string), typeof(int) });

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insns)
        {
            var router = AccessTools.Method(typeof(Patch_PlantManager_PlantSeed_P2SeedConsume), nameof(RouteSeedRemove));
            foreach (var ins in insns)
            {
                if (_removeItem != null && ins.Calls(_removeItem))
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, router);
                else
                    yield return ins;
            }
        }

        // 栈布局与 inv.RemoveItem(string,int) 一致([inv, uniqueName, amount]),故静态方法首参接 inv。
        static void RouteSeedRemove(Inventory inv, string uniqueName, int amount)
        {
            if (Main.IsP2OriginalActive)
            {
                int n = amount < 1 ? 1 : amount;
                for (int i = 0; i < n; i++) Main.ConsumeP2HeldOne();
                return;
            }
            inv.RemoveItem(uniqueName, amount);
        }
    }

    // 设备加燃料(Battery/Tank/Purifier)在 P2 交互 scope 内走 vanilla
    // RemoveSelectedHotSlotItem(扣共享 hotbar 选中槽=P1),P2 手持在独立 _p2Hotbar →
    // 数量不减。P2 scope 内改道扣 P2 手持栏;P1 不受影响。
    [HarmonyPatch(typeof(PlayerInventory), "RemoveSelectedHotSlotItem")]
    static class Patch_PlayerInventory_RemoveSelectedHotSlotItem_P2
    {
        static bool Prefix(int amount)
        {
            if (!Main.IsP2OriginalActive) return true;
            int n = amount < 1 ? 1 : amount;
            for (int i = 0; i < n; i++) Main.ConsumeP2HeldOne();
            return false;
        }
    }

    // 鲨鱼饵消耗发生在 OnHitWater(入水),而入水比投掷晚几帧,且走 Throwable 的
    // 远程客户端路径触发 → 此时 P2 scope 已散、克隆 IsLocalPlayer=false,vanilla 的
    // 「if (IsLocalPlayer) RemoveSelectedHotSlotItem」被跳过,故手持栏不减。
    // 在该情形(thrower 属于 P2 且非本地)直接补扣 P2 手持栏;不 return false,
    // 让 vanilla 其余逻辑(水花/重建可投掷物)照常。isLocal 为真时说明仍在 P2 scope,
    // 交由上面的 RemoveSelectedHotSlotItem 改道处理,此处让路避免双扣。
    [HarmonyPatch(typeof(SharkBaitThrower), "OnHitWater")]
    static class Patch_SharkBaitThrower_OnHitWater_P2
    {
        static void Prefix(SharkBaitThrower __instance)
        {
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;
            if (np.IsLocalPlayer) return;   // 仍在 P2 scope,vanilla 消耗会跑,让路
            Main.ConsumeP2HeldOne();
        }
    }

    [HarmonyPatch(typeof(UseItemController), "Use")]
    static class Patch_UseItemController_Use_P2Consume
    {
        static bool Prefix(UseItemController __instance)
        {
            if (!Main.p2UsingItemActive) return true;
            var item = __instance.GetCurrentItemInHand();
            var c = item != null ? item.settings_consumeable : null;
            if (c == null || c.FoodForm == FoodForm.None) return true;
            bool hasYield = c.HungerYield != 0f || c.ThirstYield != 0f || c.OxygenYield != 0f
                          || c.BonusHungerYield != 0f || c.BonusThirstYield != 0f;
            if (!hasYield) return true;
            Main.ConsumeP2Held(item);
            return false;
        }
    }

    // P2 独有:净水器接水。vanilla CollectItem 用 player.Inventory.GetSelectedHotbarItem()/AddItem 到
    // 共享选中槽;P2 持杯在 _p2Hotbar → 空杯被 RemoveSelectedHotSlotItem 改道消耗、装水杯却加到共享槽丢失。
    // 照搬 vanilla 逻辑但改用 P2 持握:GetP2HeldHotbarItem 判定、SetP2HeldHotbarItem 收结果。
    [HarmonyPatch(typeof(Block_CookingStand_Purifier), "CollectItem")]
    static class Patch_Purifier_CollectItem_P2
    {
        static bool Prefix(CookingSlot cookingSlot, Network_Player player, ref bool __result)
        {
            if (player == null || player != Main.player2) return true;   // 仅接管 P2,P1 走 vanilla
            var held = Main.GetP2HeldHotbarItem();
            if (held == null || held.settings_consumeable == null
                || held.settings_consumeable.FoodForm != FoodForm.Fluid) { __result = false; return false; }
            if (cookingSlot == null || cookingSlot.CurrentItem == null
                || cookingSlot.CurrentItem.settings_cookable == null) { __result = false; return false; }

            if (held.settings_consumeable.FoodType == FoodType.None)
            {
                var result = held.settings_cookable != null ? held.settings_cookable.CookingResult.item : null;
                if (result == null) { __result = false; return false; }
                var inst = new ItemInstance(result, 1, result.MaxUses);
                inst.Uses = 1;                       // 仿 vanilla SetUses(1)
                Main.SetP2HeldHotbarItem(inst);
                ResetSlot(cookingSlot);
                __result = true; return false;
            }

            var cookingResult = cookingSlot.CurrentItem.settings_cookable.CookingResult;
            if (cookingResult.item != null && cookingResult.item.settings_consumeable != null
                && held.settings_consumeable.FoodType == cookingResult.item.settings_consumeable.FoodType
                && !held.HasMaxUses)
            {
                held.Uses += 1;                      // 仿 vanilla IncrementUses(1)
                Main.SetP2HeldHotbarItem(held);      // 刷新手持栏显示
                ResetSlot(cookingSlot);
                __result = true; return false;
            }

            __result = false; return false;
        }

        static void ResetSlot(CookingSlot slot)
        {
            if (slot.connectedSlot != null) slot.connectedSlot.ResetSlot();
            else slot.ResetSlot();
        }
    }
}