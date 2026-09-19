using System;
using HarmonyLib;

namespace SplitScreen
{
    [HarmonyPatch(typeof(Carry), "OnIsRayed")]
    static class Patch_Carry_OnIsRayed_P1BusyGuard
    {
        struct State
        {
            public bool Changed;
        }

        static void Prefix(Carry __instance, ref State __state)
        {
            __state = default;
            if (Main.isProcessingP2Ray) return;
            if (Main.player1 == null || __instance == null) return;
            if (!PlayerItemManager.IsBusy) return;
            if (CanvasHelper.ActiveMenu != MenuType.None) return;
            if (__instance.carryingPlayer != null || !__instance.AllowCarry) return;

            var p1 = Main.player1;
            if (p1.CarryingComponent != null && p1.CarryingComponent.IsCarrying) return;
            if (p1.RessurectComponent != null && (p1.RessurectComponent.IsCarrying || p1.RessurectComponent.BeingCarried)) return;
            if (p1.BedComponent != null && p1.BedComponent.Sleeping) return;
            if (p1.PlayerNetworkManager != null && p1.PlayerNetworkManager.IsAttached) return;

            __state.Changed = true;
            Main.P1CarryRayBusyBypass = true;
        }

        static void Postfix(Carry __instance, State __state) => Restore(__instance, __state);

        static Exception Finalizer(Exception __exception, Carry __instance, State __state)
        {
            Restore(__instance, __state);
            return __exception;
        }

        static void Restore(Carry carry, State state)
        {
            if (!state.Changed) return;
            Main.P1CarryRayBusyBypass = false;
        }
    }
}
