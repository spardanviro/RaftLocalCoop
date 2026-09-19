using HarmonyLib;

namespace SplitScreen
{
    [HarmonyPatch(typeof(Raft_Network), "GetLocalPlayer")]
    static class Patch_RaftNetwork_GetLocalPlayer_P2Context
    {
        static bool Prefix(ref Network_Player __result)
        {
            if (Main.isSpawningP2) return true;

            var active = PlayerContext.Active;
            if (active != null)
            {
                __result = active;
                return false;
            }

            if (Main.player1 != null)
            {
                __result = Main.player1;
                return false;
            }

            return true;
        }
    }
}
