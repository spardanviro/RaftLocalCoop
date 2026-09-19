using HarmonyLib;
using UnityEngine.SceneManagement;

namespace SplitScreen
{
    [HarmonyPatch(typeof(GameManager), "OnWorldRecieved")]
    static class Patch_GameManager_OnWorldRecieved_SplitReady
    {
        static void Prefix()
        {
            Main.WorldReadyForSplit = false;
        }
    }

    [HarmonyPatch(typeof(GameManager), "OnWorldRecievedLate")]
    static class Patch_GameManager_OnWorldRecievedLate_SplitReady
    {
        static void Postfix()
        {
            bool ready = !GameManager.IsLeavingGame &&
                         SceneManager.GetActiveScene().name == "MainScene" &&
                         ComponentManager<Raft_Network>.Value != null &&
                         ComponentManager<SaveAndLoad>.Value != null &&
                         ComponentManager<Raft_Network>.Value.GetLocalPlayer() != null;
            Main.WorldReadyForSplit = ready;
            if (ready)
                Main.LogV("[SpawnGate] world ready for split");
        }
    }
}
