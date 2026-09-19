using System.Reflection;
using HarmonyLib;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    //
    
    
    //
    
    
    
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
