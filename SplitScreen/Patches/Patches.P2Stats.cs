using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2 数值系统（照搬原版 PlayerStats 逻辑）
    //  原版 PlayerStats.Update 的"数值→生命"后果逻辑被 `if (!IsLocalPlayer) return;` 门控，
    //  P2 被当作远程玩家 → 这段不跑 → P2 缺氧/饥渴都不掉血、也不回血。
    //  而饥饿/口渴/氧气的"消耗"在各自 Stat 组件的 Update(不门控)里照常运行 → P2 数值本身会消耗。
    //  本 Postfix 仅给 P2 补上被门控的"后果"，完全照搬原版公式：
    //   - 氧气=0 → 溺水掉血
    //   - 饥饿或口渴归零 → 掉血
    //   - 口渴且饥饿都低于安康 → 掉血
    //   - 否则 → 回血(Regenerate)
    //   掉血先扣额外生命(BonusHealth)再扣本体。死亡由 SplitScreenRuntime.TickDeath 检测 IsDead 处理。
    //  不触碰 UI/后处理/音效(那些是 P1 的、会污染 P1)，故不复用原版 Update 的 UI 部分(原版对 P2 已 early-return)。
    // ══════════════════════════════════════════════════════════════════════
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
            if (__instance.GetComponent<Network_Player>() != p2) return; // 只处理 P2；P1 走原版 Update
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
