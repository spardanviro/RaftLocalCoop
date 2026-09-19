using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{

    //   if (localFovCamera == null) localFovCamera = localPlayer.Camera.transform.Find("HandCamera").GetComponent<Camera>();
    [HarmonyPatch(typeof(Helper), "WorldPointToFOVPointLocal")]
    static class Patch_Helper_WorldPointToFOVPointLocal_FixHandCam
    {
        static readonly FieldInfo f_localFovCam =
            typeof(Helper).GetField("localFovCamera", BindingFlags.Static | BindingFlags.NonPublic);
        static void Prefix()
        {
            if (f_localFovCam == null || Main.player1 == null) return;
            var cur = f_localFovCam.GetValue(null) as Camera;
            if (cur != null) return;
            var hc = Main.player1.HandCamera;
            if (hc != null) f_localFovCam.SetValue(null, hc);
        }
    }

    //   localFovCamera = localPlayer.Camera.transform.Find("HandCamera").GetComponent<Camera>();
    [HarmonyPatch(typeof(Helper), "OnWorldRecievedLate")]
    static class Patch_Helper_OnWorldRecievedLate_SafeHandCam
    {
        static readonly FieldInfo f_localPlayer = typeof(Helper).GetField("localPlayer", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo f_localFovCam = typeof(Helper).GetField("localFovCamera", BindingFlags.Static | BindingFlags.NonPublic);
        static bool Prefix()
        {
            if (f_localFovCam == null) return true;
            try
            {
                var lp = (f_localPlayer != null ? f_localPlayer.GetValue(null) as Network_Player : null) ?? Main.player1;
                if (lp != null && lp.Camera != null)
                {
                    var tr = lp.Camera.transform.Find("HandCamera");
                    if (tr == null) tr = FindDeep(lp.Camera.transform, "HandCamera");
                    if (tr != null) { var cam = tr.GetComponent<Camera>(); if (cam != null) f_localFovCam.SetValue(null, cam); }
                }
            }
            catch { }
            return false;
        }
        static Transform FindDeep(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c.name == name) return c;
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
