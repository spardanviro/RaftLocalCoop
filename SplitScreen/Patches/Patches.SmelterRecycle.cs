using HarmonyLib;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  熔炉回收 —— 把【螺栓 Bolt / 铰链 Hinge】投入熔炉冶炼成【金属锭 MetalIngot】。
    //
    //  用途：创造/测试时缺金属(矿石)，借此把可获得的螺栓/铰链回炉成金属锭，方便测试熔炉。
    //
    //  机制：熔炉(Block_CookingStand_Smelter)的"可冶炼判定/出料模型/冶炼时间/产出"全靠预置的
    //   MetalOre 槽位连接(CookItemConnection + settings_cookable.CookingResult=MetalIngot)。
    //   螺栓/铰链没有 cookable 设置、也没有槽位模型 → 直接加配方会缺模型且产出为空。
    //   故在【插入入口】把投入物替换成 MetalOre：StartCooking/模型/时间/产出全走 vanilla 矿石路径，
    //   而实际【消耗】的仍是手持选中槽里的螺栓/铰链(InsertItem 内 IncrementUses(-1) 扣选中热栏槽，与替换无关)。
    //
    //  类型无关判定：基础熔炉/电力熔炉的具体类不同(Block_CookingStand 基类 vs Block_CookingStand_Smelter)，
    //   不靠类名，而是"该灶台本就能冶炼 MetalOre"(任一槽位 CanCookItem(MetalOre)=true)→ 即为熔炉才替换。
    //   食物锅/净水器/榨汁机不能冶炼矿石 → CanCookItem(MetalOre)=false → 自然不替换，无副作用。
    // ══════════════════════════════════════════════════════════════════════
    static class SmelterRecycle
    {
        static Item_Base _metalOre;
        internal static Item_Base MetalOre =>
            _metalOre != null ? _metalOre : (_metalOre = ItemManager.GetItemByName("MetalOre"));

        // 可回炉物：螺栓 / 铰链。
        internal static bool IsRecyclable(Item_Base it)
            => it != null && (it.UniqueName == "Bolt" || it.UniqueName == "Hinge");

        // 可回炉物 + 该灶台能冶炼 MetalOre(=熔炉) → 替换成 MetalOre；否则原样返回。
        internal static Item_Base Substitute(Block_CookingStand stand, Item_Base it)
        {
            if (stand == null || !IsRecyclable(it)) return it;
            var ore = MetalOre;
            if (ore == null) return it;
            var slots = stand.cookingSlots;
            if (slots != null)
                foreach (var s in slots)
                    if (s != null && s.CanCookItem(ore)) return ore;   // 能冶炼矿石=熔炉 → 替换
            return it;
        }
    }

    // 查可用冶炼槽：螺栓/铰链按 MetalOre 找槽(否则 GetCookingSlotsForItem 对它返回 null → 提示"不能放")。
    [HarmonyPatch(typeof(Block_CookingStand), "GetCookingSlotsForItem")]
    static class Patch_CookingStand_GetSlots_Recycle
    {
        static void Prefix(Block_CookingStand __instance, ref Item_Base itemToInsert)
            => itemToInsert = SmelterRecycle.Substitute(__instance, itemToInsert);
    }

    // 实际插入：把投入物替换成 MetalOre → StartCooking 用 MetalOre(有模型/时间/产出金属锭)。
    //  消耗仍落到手持选中槽(螺栓/铰链)，因 InsertItem 内 IncrementUses(-1) 扣的是 GetSelectedHotbarSlot()，与 itemToInsert 无关。
    [HarmonyPatch(typeof(Block_CookingStand), "InsertItem")]
    static class Patch_CookingStand_Insert_Recycle
    {
        static void Prefix(Block_CookingStand __instance, ref Item_Base itemToInsert)
            => itemToInsert = SmelterRecycle.Substitute(__instance, itemToInsert);
    }
}
