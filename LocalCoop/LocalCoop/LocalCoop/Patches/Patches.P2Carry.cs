using System;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2 搬运/复活队友 —— RessurectComponent。
    //   原版 Update→CheckForPlayerToCarry/HandleCarrying 门控 IsLocalPlayer：瞄准倒地队友(HitAtCursor
    //   MASK_RemotePlayer) + "Interact" 抱起；抱着时找床 + "Interact" 放床复活。对 P2 原版直接 return。
    //   做法：把 P2 的 RessurectComponent.Update 整段包进 P2OriginalScope.Tool()(每帧，scope 随方法返回即释放，
    //   与 UseItemController.Update 同套路 → 不污染其它系统)：isLocalPlayer 强制真过门控；HitAtCursor 走 P2 相机；
    //   "Interact"→P2 X；DTM 提示→P2 半屏(IsP2OriginalActive 捕获)。StartCarryingPlayer 会置全局 IsBusy →
    //   save/restore 防泄漏给 P1(放床/停搬运时它本会复位，这里多一层保险且覆盖中途帧)。
    //  限制(v1)：抱起点用 firstPersonCarryTransform(scope 内 isLocalPlayer=true)，P2 第三人称下被抱队友位置可能略偏；
    //   功能(抱起→放床→复活)可用，视觉细节后续再修。
    // ══════════════════════════════════════════════════════════════════════
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
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
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
