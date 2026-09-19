using System;
using System.Reflection;
using Object = UnityEngine.Object;
using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        
        static void TrySpawnP2()
        {
            var log = ModEntry.Logger;
            string blockReason = GetSplitSpawnBlockReason();
            if (blockReason != null)
            {
                log.Log("[SpawnGate] spawn blocked: " + blockReason);
                return;
            }

            log.Log("=== F9: Spawn P2 ===");
            ActiveSplitMode = PendingSplitMode;

            if (LAYER_P1_HAND < 0) { log.Log("ERROR: Layers not allocated"); return; }
            if (player2 != null)   { log.Log("P2 already exists, skip"); return; }

            var raftNet = ComponentManager<Raft_Network>.Value;
            if (raftNet == null) { log.Log("ERROR: Raft_Network not found"); return; }

            var p2Id = new Network_UserId(999UL);
            if (raftNet.remoteUsers.ContainsKey(p2Id)) { log.Log("ERROR: ID 999 in use"); return; }

            var addMethod = typeof(Raft_Network).GetMethod(
                "InternalAddPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (addMethod == null) { log.Log("ERROR: InternalAddPlayer not found"); return; }

            
            var savedCM_NP  = ComponentManager<Network_Player>.Value;
            var savedCM_P   = ComponentManager<Player>.Value;
            var savedCM_Eq  = ComponentManager<PlayerEquipment>.Value;
            var savedCM_Inv = ComponentManager<PlayerInventory>.Value;
            log.Log($"[Spawn] saved CM: NP={savedCM_NP?.name} P={savedCM_P?.name} Eq={savedCM_Eq?.name} Inv={savedCM_Inv?.name}");

            isSpawningP2 = true;
            Network_Player p2 = null;
            try
            {
                var p2Character = P2CharacterSettings.LoadOrDefault();
                p2 = addMethod.Invoke(raftNet, new object[]
                {
                    p2Id,
                    p2Character
                }) as Network_Player;
            }
            catch (Exception e)
            {
                log.Log($"ERROR spawn: {e.Message}");
                if (e.InnerException != null) log.Log($"  Inner: {e.InnerException.Message}");
            }
            finally
            {
                isSpawningP2 = false;

                
                if (savedCM_NP  != null) ComponentManager<Network_Player>.Value   = savedCM_NP;
                if (savedCM_P   != null) ComponentManager<Player>.Value           = savedCM_P;
                if (savedCM_Eq  != null) ComponentManager<PlayerEquipment>.Value  = savedCM_Eq;
                if (savedCM_Inv != null) ComponentManager<PlayerInventory>.Value  = savedCM_Inv;
                log.Log($"[Spawn] restored CM: NP={savedCM_NP?.name} P={savedCM_P?.name} Eq={savedCM_Eq?.name} Inv={savedCM_Inv?.name}");
            }

            if (p2 == null) { log.Log("ERROR: InternalAddPlayer returned null"); return; }

            player2 = p2;

            
            
            
            
            player1 = raftNet.GetLocalPlayer();
            if ((UnityEngine.Object)player1 == null || player1 == p2 || player1.Stats == null || player1.StorageManager == null)
                player1 = PlayerContext.ResolveGenuineLocalPlayerStrict();

            if (player1 == null || player1 == p2) { log.Log("ERROR: P1 not found"); return; }

            log.Log($"OK P1={player1.name} P2={player2.name}");
            RestoreP1CraftingForCurrentWorld();
            _splitActive = true;
            SplitFramePacing.Enable();
            _pendingP1GamepadRepair = false;

            p1CamOriginalMask = -1;
            p2CamOriginalMask = -1;
            player1ThirdPerson = null;
            ResetP2Hotbar();

            BeginP2CameraBootstrap();

            
            SplitScreenRuntime.Instance.Input.UnpairGamepadFromP1();
            SplitScreenRuntime.Instance.Input.Create();

            SetCameraRects();

            
            SplitScreenRuntime.Instance.UI.CreateOverlays();

            p2.transform.position = player1.transform.position + Vector3.right * 3f;

            if (p2.currentModel != null)
                ConfigureRendering();
            else
                log.Log("currentModel not ready yet, will configure in CharacterModelModifications.Start");

            
            PatchCMGetters();

            
            
            
            try { DestroyP2Backpack(); }
            catch (Exception e) { LogV("[Spawn] DestroyP2Backpack before restore ignored: " + e.Message); }
            P2SaveStore.LoadAndRestoreP2(player2);

            InitP2Held();   

            MoveP1UiToLeftHalf();
            log.Log("=== Spawn done ===");
        }

        static string GetSplitSpawnBlockReason()
        {
            if (!WorldReadyForSplit) return "world is not ready";
            if (GameManager.IsLeavingGame) return "leaving game";
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "MainScene") return "not in MainScene";
            if (LAYER_P1_HAND < 0) return "layers are not allocated";
            if (player2 != null) return "P2 already exists";

            var raftNet = ComponentManager<Raft_Network>.Value;
            if (raftNet == null) return "Raft_Network is null";

            var p1 = raftNet.GetLocalPlayer();
            if (p1 == null) return "local player is null";
            if (p1.BlockCreator == null) return "local player BlockCreator is null";
            if (p1.Inventory == null) return "local player Inventory is null";

            var save = ComponentManager<SaveAndLoad>.Value;
            if (save == null) return "SaveAndLoad is null";

            return null;
        }
    }
}
