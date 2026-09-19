using HarmonyLib;
using UnityEngine;
using UnityEngine.AzureSky;

namespace SplitScreen
{
    [HarmonyPatch(typeof(PlayerEnvironmentLightManager), "Update")]
    static class Patch_PlayerEnvLight_Update_KeepSplitLayers
    {
        static void Postfix()
        {
            if (!Main._splitActive) return;
            var sky = ComponentManager<AzureSkyController>.Value;
            if (sky == null || sky.m_lightComponent == null) return;
            sky.m_lightComponent.cullingMask |= Main.SplitLightingLayerMask();
        }
    }
}