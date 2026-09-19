using HarmonyLib;

namespace SplitScreen
{
    
    
    //
    
    //
    
    
    
    
    
    //
    
    
    
    
    static class SmelterRecycle
    {
        static Item_Base _metalOre;
        internal static Item_Base MetalOre =>
            _metalOre != null ? _metalOre : (_metalOre = ItemManager.GetItemByName("MetalOre"));

        
        internal static bool IsRecyclable(Item_Base it)
            => it != null && (it.UniqueName == "Bolt" || it.UniqueName == "Hinge");

        
        internal static Item_Base Substitute(Block_CookingStand stand, Item_Base it)
        {
            if (stand == null || !IsRecyclable(it)) return it;
            var ore = MetalOre;
            if (ore == null) return it;
            var slots = stand.cookingSlots;
            if (slots != null)
                foreach (var s in slots)
                    if (s != null && s.CanCookItem(ore)) return ore;   
            return it;
        }
    }

    
    [HarmonyPatch(typeof(Block_CookingStand), "GetCookingSlotsForItem")]
    static class Patch_CookingStand_GetSlots_Recycle
    {
        static void Prefix(Block_CookingStand __instance, ref Item_Base itemToInsert)
            => itemToInsert = SmelterRecycle.Substitute(__instance, itemToInsert);
    }

    
    
    [HarmonyPatch(typeof(Block_CookingStand), "InsertItem")]
    static class Patch_CookingStand_Insert_Recycle
    {
        static void Prefix(Block_CookingStand __instance, ref Item_Base itemToInsert)
            => itemToInsert = SmelterRecycle.Substitute(__instance, itemToInsert);
    }
}
