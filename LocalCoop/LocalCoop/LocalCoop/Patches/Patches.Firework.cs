using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  焰火方块(Block_Firework)点燃后火箭不发射 + 重载残留 —— 修 static localPlayer 串扰。
    //
    //  Block_Firework.Launch() 用 `static localPlayer = ComponentManager<Network_Player>.Value`
    //   取玩家,再 `localPlayer.FireworkHand.LaunchFireworkNetworked(...)`。分屏下该 FireworkHand
    //   的 fireworkPrefab 解析为 null → LaunchFireworkNetworked 内 NRE(日志:0x1a=fireworkPrefab.
    //   lifeTimeInterval)。NRE 在该调用处抛出 → Launch 中断 → ①火箭从未实例化(看似"直接消失"),
    //   ②后续 `Invoke("RemoveBlock", 5f)` 不执行 → 方块未移除 → 重载又出现在原地。两症状同源。
    //
    //  (同 PlayerSeat 的 static localPlayer 坑:静态缓存的"本地玩家"在分屏下可能是 P2 克隆,
    //   其手持 FireworkHand 未必带 fireworkPrefab。)
    //
    //  修:Prefix 把 static localPlayer 纠正为 FireworkHand.fireworkPrefab 有效的玩家(优先 P1
    //   真·本地主机),让原版 Launch 用有效的 FireworkHand 正常发射 + 之后正常移除方块。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Block_Firework), "Launch")]
    static class Patch_Block_Firework_Launch_FixPlayer
    {
        static readonly FieldInfo s_localPlayer = AccessTools.Field(typeof(Block_Firework), "localPlayer");

        static void Prefix()
        {
            if (s_localPlayer == null) return;
            var p1 = Main.player1;  var p2 = Main.player2;
            bool p1ok = HasFireworkPrefab(p1);
            bool p2ok = HasFireworkPrefab(p2);
            var pick = p1ok ? p1 : (p2ok ? p2 : null);
            var cur = s_localPlayer.GetValue(null) as Network_Player;
            Main.LogV($"[Firework] Launch fix: cur={(cur != null ? cur.name : "null")} p1ok={p1ok} p2ok={p2ok} pick={(pick == p1 ? "P1" : pick == p2 ? "P2" : "null")}");
            if (pick != null) s_localPlayer.SetValue(null, pick);
        }

        static bool HasFireworkPrefab(Network_Player np)
            => np != null && np.FireworkHand != null && np.FireworkHand.fireworkPrefab != null;
    }
}
