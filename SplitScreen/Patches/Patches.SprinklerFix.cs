using HarmonyLib;

namespace SplitScreen
{
    // 修 vanilla 洒水器在分屏下偶发 Sprinkler.WaterCropplot NRE 刷屏(且过一会儿自愈)。
    // 根因(mod 污染 vanilla 全局单例):Sprinkler.Start 把
    // ComponentManager<Network_Player>.Value.PlantManager 缓存进私有字段 plantManager。而该 Value 在
    // 任何 P2 scope 活动期间会经 getter patch(Patches.PlayerContext.cs 的 PlayerContext.Active 分支)
    // 返回 P2 克隆体;若某洒水器的 Start 恰在此时跑,它缓存的是 P2 的 PlantManager。P2 在分屏
    // teardown/复活时被销毁 → 缓存的引用变成已销毁的 Unity 对象(假 null)→ 之后每帧 WaterCropplot
    // 解引用 null 崩。重载世界后 Start 重跑缓存到 P1 才自愈,故表现为"偶发+过会儿好+重载正常"。
    // 修法:Start 后把 plantManager 重新绑定到真正的本地玩家 P1(host、稳定、永不销毁),复原 vanilla
    // 意图(洒水器本就该用真本地玩家的 PlantManager)。这是消除 mod 对该缓存字段的污染,非新增机制。
    [HarmonyPatch(typeof(Sprinkler), "Start")]
    static class Patch_Sprinkler_Start_BindGenuineLocal
    {
        static readonly System.Reflection.FieldInfo s_plantManager =
            AccessTools.Field(typeof(Sprinkler), "plantManager");

        static void Postfix(Sprinkler __instance)
        {
            if (s_plantManager == null || __instance == null) return;
            var genuine = Main.player1 != null ? Main.player1 : PlayerContext.ResolveGenuineLocalPlayer();
            // 早期 P1 未定:vanilla 经 getter 的 strict 回退已缓存 P1,不动。
            if (genuine == null || genuine.PlantManager == null) return;
            var cur = s_plantManager.GetValue(__instance) as PlantManager;
            if (cur == genuine.PlantManager) return;
            s_plantManager.SetValue(__instance, genuine.PlantManager);
            Main.LogV("[SprinklerFix] rebound plantManager -> genuine P1");
        }
    }
}
