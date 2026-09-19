using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    [HarmonyPatch(typeof(PlayerStats), "Update")]
    static class Patch_PlayerStats_Update_P2
    {
        static readonly FieldInfo fDrown = AccessTools.Field(typeof(PlayerStats), "healthLostPerSecondDrowning");
        static readonly FieldInfo fWell  = AccessTools.Field(typeof(PlayerStats), "healthLostPerSecondWellbeing");
        static readonly FieldInfo fZero  = AccessTools.Field(typeof(PlayerStats), "healthLostPerSecondZeroed");

        static void Postfix(PlayerStats __instance)
        {
            var p2 = Main.player2;
            if (p2 == null) return;
            if (__instance.GetComponent<Network_Player>() != p2) return; 
            if (SplitScreenDeathFlow.IsP2RespawnRestoring(p2.PlayerScript))
                return;
            if (__instance.IsDead)
            {
                KillP2IfNeeded(p2);
                return;
            }
            if (GameModeValueManager.IsPlayerInvurnerable) return;

            float dt = Time.deltaTime;
            float num = 0f;
            var wellBeing = Main.GetWellBeingForStats(__instance);
            if (__instance.stat_oxygen.IsZero)
                num += GetRate(fDrown, __instance) * dt;
            else if (__instance.stat_hunger.Normal.IsZero || __instance.stat_thirst.Normal.IsZero)
                num += GetRate(fZero, __instance) * dt;
            else if (!__instance.stat_thirst.Normal.MeetsWellBeing && !__instance.stat_hunger.Normal.MeetsWellBeing)
                num += GetRate(fWell, __instance) * dt;
            else if (wellBeing != WellBeing.Bad)
                __instance.stat_health.Regenerate();

            if (num > 0f)
            {
                float remain = num;
                float bonus = __instance.stat_BonusHealth.Value;
                if (bonus > 0f)
                {
                    if (num > bonus) { __instance.stat_BonusHealth.Value -= bonus; remain -= bonus; }
                    else { __instance.stat_BonusHealth.Value -= num; remain = 0f; }
                }
                if (remain > 0f) __instance.stat_health.Value -= remain;
            }

            if (__instance.IsDead)
            {
                KillP2IfNeeded(p2);
                return;
            }

            Main.DriveP2SurvivalFx(__instance);
        }

        static void KillP2IfNeeded(Network_Player p2)
        {
            if (p2?.PlayerScript == null || p2.PlayerScript.IsDead) return;
            p2.PlayerScript.Kill();
            Main.ModEntry.Logger.Log("[P2Stats] P2 health reached zero; invoked vanilla Player.Kill");
        }

        static float GetRate(FieldInfo f, PlayerStats inst) => f != null ? (float)f.GetValue(inst) : 1f;
    }

    [HarmonyPatch(typeof(PlayerStats), nameof(PlayerStats.Damage))]
    static class Patch_PlayerStats_Damage_P2Death
    {
        static void Postfix(PlayerStats __instance)
        {
            var p2 = Main.player2;
            if (p2 == null || __instance == null) return;
            if (__instance.GetComponent<Network_Player>() != p2) return;
            if (SplitScreenDeathFlow.IsP2RespawnRestoring(p2.PlayerScript)) return;
            if (!__instance.IsDead || p2.PlayerScript == null || p2.PlayerScript.IsDead) return;

            p2.PlayerScript.Kill();
            Main.ModEntry.Logger.Log("[P2Stats] P2 damage killed player; invoked vanilla Player.Kill");
        }
    }

    [HarmonyPatch(typeof(Stat_Oxygen), "Update")]
    static class Patch_StatOxygen_Update_P2WellBeing
    {
        struct State
        {
            internal bool Active;
            internal WellBeing Previous;
        }

        static void Prefix(Stat_Oxygen __instance, ref State __state)
        {
            __state = default;
            var p2 = Main.player2;
            if (p2 == null || __instance == null || p2.Stats == null || __instance != p2.Stats.stat_oxygen) return;

            __state.Active = true;
            __state.Previous = Main.PushP2WellBeing();
        }

        static System.Exception Finalizer(System.Exception __exception, State __state)
        {
            if (__state.Active)
                Main.RestoreWellBeing(__state.Previous);
            return __exception;
        }
    }
}
