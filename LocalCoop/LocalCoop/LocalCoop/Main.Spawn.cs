using System;
using System.Reflection;
using Object = UnityEngine.Object;
using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        // ── F9：生成 P2 ──────────────────────────────────────────────────
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

            // 保存 P1 的 ComponentManager 单例，防止 P2 生成时覆盖。
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

                // 恢复 P1 的单例（P2 生成过程可能已覆盖）
                if (savedCM_NP  != null) ComponentManager<Network_Player>.Value   = savedCM_NP;
                if (savedCM_P   != null) ComponentManager<Player>.Value           = savedCM_P;
                if (savedCM_Eq  != null) ComponentManager<PlayerEquipment>.Value  = savedCM_Eq;
                if (savedCM_Inv != null) ComponentManager<PlayerInventory>.Value  = savedCM_Inv;
                log.Log($"[Spawn] restored CM: NP={savedCM_NP?.name} P={savedCM_P?.name} Eq={savedCM_Eq?.name} Inv={savedCM_Inv?.name}");
            }

            if (p2 == null) { log.Log("ERROR: InternalAddPlayer returned null"); return; }

            player2 = p2;

            // P1 身份必须全局唯一且可信:禁止"第一个非 P2"扫描(会选中临时玩家/host 自连接残留对象,
            //  如不完整的 "Inventory_Player")。优先用原版 GetLocalPlayer()(=真·本地玩家);失效则回退
            //  ResolveGenuineLocalPlayerStrict(IsLocalPlayer && Stats && StorageManager,天然排除
            //  isLocalPlayer=false 的 P2 与无 StorageManager 的残留对象)。
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

            // 解绑 P1 手柄 → 创建 P2 输入
            SplitScreenRuntime.Instance.Input.UnpairGamepadFromP1();
            SplitScreenRuntime.Instance.Input.Create();

            SetCameraRects();

            // 创建 P2 覆层 UI（prompt + death overlay）
            SplitScreenRuntime.Instance.UI.CreateOverlays();

            p2.transform.position = player1.transform.position + Vector3.right * 3f;

            if (p2.currentModel != null)
                ConfigureRendering();
            else
                log.Log("currentModel not ready yet, will configure in CharacterModelModifications.Start");

            // P2 生成完毕，所有 Awake 已跑完，现在注册 CM getter patch
            PatchCMGetters();

            
            
            
            try { DestroyP2Backpack(); }
            catch (Exception e) { LogV("[Spawn] DestroyP2Backpack before restore ignored: " + e.Message); }
            P2SaveStore.LoadAndRestoreP2(player2);

            InitP2Held();   // 立即手持锤子(自定义快捷栏)

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

            // 本地分屏只支持单机会话。P2 以伪 ID 直接插进 remoteUsers,不走 Message_Player_Create 广播,
            // 远端并不知道它存在;它之后发出的采集/建造消息会引用远端不认识的对象。
            if (!Raft_Network.IsHost) return "joined someone else's world (local co-op is single-machine only)";
            if (raftNet.remoteUsers != null && raftNet.remoteUsers.Count > 1)
                return "other players are connected (local co-op is single-machine only)";

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
