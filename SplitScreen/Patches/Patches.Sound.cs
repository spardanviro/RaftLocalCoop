using System;
using System.Reflection;
using HarmonyLib;
using UltimateWater;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch 22: SoundManager.HandleUnderWaterFilter — P2 水下音效
    //
    //  P1 或 P2 任一入水则开滤镜，两者均出水才关滤镜。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(SoundManager), "HandleUnderWaterFilter")]
    static class Patch_SoundManager_HandleUnderWaterFilter
    {
        static readonly FieldInfo playerField =
            typeof(SoundManager).GetField("player",
                BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo underwaterFilterField =
            typeof(SoundManager).GetField("underwaterFilter",
                BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo eventInstanceField =
            typeof(SoundManager).GetField("eventInstance_snapshot_UnderWater",
                BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo emitterField =
            typeof(SoundManager).GetField("eventEmitter_PlayerUnderWater",
                BindingFlags.Instance | BindingFlags.NonPublic);

        static bool Prefix(SoundManager __instance)
        {
            if (Main.player2 == null) return true;

            var p1 = playerField?.GetValue(__instance) as Network_Player;
            bool p1Sub = p1?.PersonController?.SubmersionState == SubmersionState.Full;
            bool p2Sub = Main.player2.PersonController?.SubmersionState == SubmersionState.Full;
            bool shouldFilter = p1Sub || p2Sub;

            bool currentFilter = underwaterFilterField?.GetValue(__instance) is bool b && b;

            if (shouldFilter == currentFilter) return false;

            underwaterFilterField?.SetValue(__instance, shouldFilter);

            if (shouldFilter)
            {
                CallEventMethod(__instance, eventInstanceField, "start", null);
                CallEmitter(__instance, emitterField, "Play");
                Main.LogV("[Patch22] Underwater filter ON" +
                    $" (p1={p1Sub} p2={p2Sub})");
            }
            else
            {
                CallEventMethodStop(__instance, eventInstanceField);
                CallEmitter(__instance, emitterField, "Stop");
                Main.LogV("[Patch22] Underwater filter OFF");
            }

            return false;
        }

        static void CallEventMethod(SoundManager inst, FieldInfo field, string method, object[] args)
        {
            try
            {
                object evt = field?.GetValue(inst);
                if (evt == null) return;
                evt.GetType()
                   .GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
                   ?.Invoke(evt, args);
            }
            catch (Exception ex)
            {
                Main.ModEntry.Logger.Log($"[Patch22] EventInstance.{method}: {ex.Message}");
            }
        }

        static void CallEventMethodStop(SoundManager inst, FieldInfo field)
        {
            try
            {
                object evt = field?.GetValue(inst);
                if (evt == null) return;
                var stopMethod = evt.GetType()
                    .GetMethod("stop", BindingFlags.Public | BindingFlags.Instance);
                if (stopMethod == null) return;

                var parameters = stopMethod.GetParameters();
                if (parameters.Length == 1)
                {
                    object stopMode = Enum.ToObject(parameters[0].ParameterType, 0);
                    stopMethod.Invoke(evt, new[] { stopMode });
                }
                else
                {
                    stopMethod.Invoke(evt, null);
                }
            }
            catch (Exception ex)
            {
                Main.ModEntry.Logger.Log($"[Patch22] EventInstance.stop: {ex.Message}");
            }
        }

        static void CallEmitter(SoundManager inst, FieldInfo field, string method)
        {
            try
            {
                var emitter = field?.GetValue(inst) as UnityEngine.MonoBehaviour;
                emitter?.SendMessage(method, null, UnityEngine.SendMessageOptions.DontRequireReceiver);
            }
            catch (Exception ex)
            {
                Main.ModEntry.Logger.Log($"[Patch22] Emitter.{method}: {ex.Message}");
            }
        }
    }
}
