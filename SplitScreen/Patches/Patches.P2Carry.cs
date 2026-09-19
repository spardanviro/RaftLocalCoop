using System;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    
    
    
    
    
    
    
    
    
    
    
    [HarmonyPatch(typeof(RessurectComponent), "Update")]
    static class Patch_RessurectComponent_Update_P2
    {
        static bool Prefix(RessurectComponent __instance, ref ThrowableCompP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return true;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return true;

            if (__instance.IsCarrying)
                return false;

            __state = new ThrowableCompP2State { Busy = new PlayerItemBusyScope(), Ctx = P2FrameContext.Tool() };
            return true;
        }

        static void Postfix(RessurectComponent __instance, ThrowableCompP2State __state)
        {
            Cleanup(__state);
        }

        static Exception Finalizer(Exception __exception, RessurectComponent __instance, ThrowableCompP2State __state)
        {
            if (__exception != null)
                Main.ModEntry.Logger.Log("[P2Carry] RessurectComponent.Update exception " + __exception);
            Cleanup(__state);
            return __exception;
        }

        static void Cleanup(ThrowableCompP2State st)
        {
            if (st == null) return;
            st.Ctx?.Dispose(); st.Ctx = null;
            st.Busy?.Dispose(); st.Busy = null;
        }

    }

    [HarmonyPatch(typeof(LocalizationParameters), "GetParameterValue")]
    static class Patch_LocalizationParameters_CarrySafe
    {
        static bool Prefix(string parameter, ref string __result)
        {
            if (parameter != "CarriedPlayer" && parameter != "IncapacitatedPlayer") return true;
            try
            {
                var cands = new[] { ComponentManager<Network_Player>.Value, Main.player1, Main.player2 };
                foreach (var np in cands)
                {
                    if ((UnityEngine.Object)np == null || np.RessurectComponent == null) continue;
                    var target = parameter == "CarriedPlayer"
                        ? np.RessurectComponent.CarriedPlayer
                        : np.RessurectComponent.IncapacitatedPlayerAtCursor;
                    if (target != null) { __result = target.visualName; return false; }
                }
            }
            catch (System.Exception e) { Main.LogV("[P2Carry] NamePlayerAtCursor lookup ignored: " + e.Message); }
            __result = "";
            return false;
        }
    }
}
