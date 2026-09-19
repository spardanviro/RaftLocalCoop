using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // Event-driven trace for stale localPlayer fields on original P1 world devices.
    [HarmonyPatch(typeof(Sail), "OnIsRayed")]
    static class Patch_Sail_OnIsRayed_Diagnostics
    {
        static readonly FieldInfo LocalPlayerField = AccessTools.Field(typeof(Sail), "localPlayer");
        static readonly HashSet<int> Reported = new HashSet<int>();

        static void Prefix(Sail __instance)
        {
            if (__instance == null || !Reported.Add(__instance.GetInstanceID())) return;
            var player = LocalPlayerField?.GetValue(__instance) as Network_Player;
            Main.ModEntry.Logger.Log("[P1RayDiag:Sail] device=" + __instance.name
                + " local=" + (player != null ? player.name + "#" + player.GetInstanceID() : "null")
                + " isP1=" + ReferenceEquals(player, Main.player1)
                + " isP2=" + ReferenceEquals(player, Main.player2)
                + " playerScript=" + (player != null && player.PlayerScript != null)
                + " resurrect=" + (player != null && player.RessurectComponent != null)
                + " cm=" + (ComponentManager<Network_Player>.Value != null ? ComponentManager<Network_Player>.Value.name : "null"));
        }
    }

    // 定位专用(只读,不修改任何状态,异常照常重新抛出):抓 PlayerSeat.OnIsRayed NRE 发生瞬间的确切空字段。
    [HarmonyPatch]
    static class Patch_PlayerSeat_OnIsRayed_Diagnostics
    {
        static readonly FieldInfo LocalPlayerField = AccessTools.Field(typeof(PlayerSeat), "localPlayer");        // static
        static readonly FieldInfo CanvasField      = AccessTools.Field(typeof(PlayerSeat), "canvas");            // static
        static readonly FieldInfo AttachField      = AccessTools.Field(typeof(PlayerSeat), "attachPlayerScripts"); // instance

        static MethodBase TargetMethod() => AccessTools.Method(typeof(PlayerSeat), "IRaycastable.OnIsRayed");

        static string Dump(PlayerSeat inst)
        {
            var lp = LocalPlayerField?.GetValue(null) as Network_Player;
            var canvas = CanvasField?.GetValue(null);
            var arr = AttachField?.GetValue(inst) as AttachPlayer[];
            int nullElems = 0;
            if (arr != null) foreach (var a in arr) if ((UnityEngine.Object)a == null) nullElems++;
            bool lpAlive = (UnityEngine.Object)lp != null;
            return "seat=" + ((UnityEngine.Object)inst != null ? inst.name : "null")
                + " lp=" + (lpAlive ? lp.name : "null")
                + " lp.PlayerScript=" + (lpAlive && lp.PlayerScript != null)
                + " lp.BedComp=" + (lpAlive && lp.BedComponent != null)
                + " attach=" + (arr == null ? "NULL" : ("len" + arr.Length + " nullElems" + nullElems))
                + " canvas=" + ((UnityEngine.Object)(canvas as UnityEngine.Object) != null)
                + " cm=" + (ComponentManager<Network_Player>.Value != null ? ComponentManager<Network_Player>.Value.name : "null");
        }

        static int _lastBucket = -1;
        static System.Exception Finalizer(System.Exception __exception, PlayerSeat __instance)
        {
            if (__exception == null) return null;
            int bucket = Time.frameCount / 120;   // 约每 2 秒最多记一次,避免刷屏
            if (_lastBucket != bucket)
            {
                _lastBucket = bucket;
                Main.ModEntry.Logger.Log("[SeatNRE] " + __exception.GetType().Name + " @ " + Dump(__instance));
            }
            return __exception;   // 原样重新抛出,不吞(纯定位,不做任何修复/掩盖)
        }
    }
}
