using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  箱子(Storage_Small)P1 偶发打不开的隐患兜底。
    //
    //  Storage_Small.OnIsRayed 把 storageManager 缓存自 ComponentManager<Network_Player>.Value
    //  .StorageManager。但分屏下 ComponentManager<T> 所有泛型共用同一 backing,该单例会漂移到
    //  "Inventory_Player"(持 PlayerInventory 的对象,其 StorageManager=null)。若某箱子【首次被瞄】时
    //  恰好撞上漂移态,storageManager 缓存成 null 且不再有有效来源 → P1 永远开不了这个箱(无提示)。
    //  (同 PlayerSeat/Block_Firework/Carry 的 static-localPlayer/CM.Value 串扰一类。)
    //
    //  兜底:storageManager 为 null 时,直接用真·P1(Main.player1)的 StorageManager,不依赖漂移的 CM.Value。
    //  仅在 null 时介入(已正确缓存的不动);P2 开箱走 mod 的 _rt.UI.OpenStorage 独立路径,不受影响。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Storage_Small), "OnIsRayed")]
    static class Patch_Storage_OnIsRayed_FixManager
    {
        static readonly FieldInfo s_sm = AccessTools.Field(typeof(Storage_Small), "storageManager");

        static void Prefix(Storage_Small __instance)
        {
            if (s_sm == null || Main.player1 == null) return;
            if (s_sm.GetValue(__instance) == null && Main.player1.StorageManager != null)
                s_sm.SetValue(__instance, Main.player1.StorageManager);
        }
    }
}
