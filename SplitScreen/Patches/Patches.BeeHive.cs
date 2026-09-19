using HarmonyLib;
using System.Reflection;

namespace SplitScreen
{
    // Guard vanilla BeeHive cleanup during scene unload: PlantManager can already
    // be gone while the localPlayer field is still set.
    [HarmonyPatch(typeof(BeeHive), "OnDestroy")]
    static class Patch_BeeHive_OnDestroy_NullGuard
    {
        static void Prefix(BeeHive __instance)
        {
            var t = Traverse.Create(__instance).Field("localPlayer");
            var lp = t.GetValue<Network_Player>();
            if (lp != null && lp.PlantManager == null)
                t.SetValue(null);
        }
    }

    [HarmonyPatch(typeof(BeeHive), "Update")]
    static class Patch_BeeHive_Update_NullGuard
    {
        static readonly FieldInfo s_isPlaced =
            typeof(BeeHive).GetField("isPlaced", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_checkParticleTimer =
            typeof(BeeHive).GetField("checkParticleTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_productionTimer =
            typeof(BeeHive).GetField("productionTimer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static bool Prefix(BeeHive __instance)
        {
            if (__instance == null) return false;
            bool isPlaced = s_isPlaced != null && (bool)s_isPlaced.GetValue(__instance);
            if (!isPlaced) return true;

            if (s_checkParticleTimer != null && s_checkParticleTimer.GetValue(__instance) == null)
                return false;
            if (Raft_Network.IsHost && s_productionTimer != null && s_productionTimer.GetValue(__instance) == null)
                return false;

            return true;
        }
    }
}
