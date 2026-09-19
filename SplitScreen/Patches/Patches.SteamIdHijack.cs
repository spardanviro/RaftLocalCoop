using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace SplitScreen
{
    
    
    
    public static class PlayerContext
    {
        
        public static Network_Player Active;

        public static void SetP2()  => Active = Main.player2;
        public static void Clear()  => Active = null;

        
        
        
        public static Network_Player ResolveGenuineLocalPlayer()
        {
            foreach (var np in UnityEngine.Object.FindObjectsOfType<Network_Player>())
            {
                if (np == null) continue;
                // PersonController!=null 排除 UI 层那个 isLocalPlayer=true 的假玩家(Inventory_Player):世界里的真玩家才有 PersonController。
                if (!np.IsLocalPlayer || np.Stats == null || np.PersonController == null) continue;
                return np;
            }
            return null;
        }

        
        
        
        public static Network_Player ResolveGenuineLocalPlayerStrict()
        {
            foreach (var np in UnityEngine.Object.FindObjectsOfType<Network_Player>())
            {
                if (np == null) continue;
                if (!np.IsLocalPlayer || np.Stats == null || np.StorageManager == null || np.PersonController == null) continue;
                return np;
            }
            return null;
        }

        public static ScopedContext As(Network_Player player) => new ScopedContext(player);
        public static ScopedContext AsP2() => new ScopedContext(Main.player2);

        public struct ScopedContext : System.IDisposable
        {
            readonly Network_Player _prev;
            internal ScopedContext(Network_Player next) { _prev = Active; Active = next; }
            public void Dispose() => Active = _prev;
        }
    }

    
    
    
    [HarmonyPatch(typeof(Network_Player), nameof(Network_Player.OnPlayerCreated))]
    static class Patch_OnPlayerCreated
    {
        static FieldInfo steamIDField;
        static FieldInfo isLocalPlayerField;

        
        
        static void Prefix(Network_Player __instance, ref Network_UserId steamID, Raft_Network network, ref Network_UserId __state)
        {
            if (!Main.isSpawningP2) return;

            if (steamIDField == null)
                steamIDField = typeof(Network_Player).GetField(
                    "steamID", BindingFlags.Instance | BindingFlags.Public);

            __state   = steamID;
            steamID   = network.LocalSteamID;
            Main.LogV($"[Patch1] steamID spoofed for P2 init, real id={__state}");
        }

        static void Postfix(Network_Player __instance, Network_UserId __state)
        {
            if (!Main.isSpawningP2) return;

            steamIDField.SetValue(__instance, __state);

            if (isLocalPlayerField == null)
                isLocalPlayerField = typeof(Network_Player).GetField(
                    "isLocalPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
            isLocalPlayerField?.SetValue(__instance, false);

            if (Main.pendingP2Inventory != null)
            {
                __instance.Inventory = Main.pendingP2Inventory;
                Main.LogV($"[Patch1] P2.Inventory fixed -> {Main.pendingP2Inventory.name}");
                Main.pendingP2Inventory = null;
            }

            __instance.gameObject.layer = UnityEngine.LayerMask.NameToLayer("RemotePlayer");

            var shadowList = new System.Collections.Generic.List<UnityEngine.Renderer>();
            if (__instance.rightHandParent != null)
            {
                shadowList.AddRange(__instance.rightHandParent.GetComponentsInChildren<UnityEngine.MeshRenderer>(true));
                shadowList.AddRange(__instance.rightHandParent.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true));
            }
            if (__instance.leftHandParent != null)
            {
                shadowList.AddRange(__instance.leftHandParent.GetComponentsInChildren<UnityEngine.MeshRenderer>(true));
                shadowList.AddRange(__instance.leftHandParent.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true));
            }
            if (__instance.currentModel?.hatParent != null)
            {
                shadowList.AddRange(__instance.currentModel.hatParent.GetComponentsInChildren<UnityEngine.MeshRenderer>(true));
                shadowList.AddRange(__instance.currentModel.hatParent.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true));
            }
            foreach (var r in shadowList)
                r.shadowCastingMode = ShadowCastingMode.On;

            Main.LogV(
                $"[Patch1] 复原完成: steamID={__state} isLocalPlayer=false layer=0 shadow×{shadowList.Count}");
        }
    }

    
    
    
    [HarmonyPatch(typeof(Network_Player), "InitializeComponents")]
    static class Patch_InitializeComponents
    {
        static void Postfix(Network_Player __instance)
        {
            
            try
            {
                var bcField = typeof(Network_Player).GetField("blockCreator",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var bc = bcField?.GetValue(__instance);
                Main.LogV(
                    $"[InitComp] {__instance?.name} IsLocal={__instance?.IsLocalPlayer} blockCreator={(bc==null?"NULL":"ok")} stats={(__instance?.Stats==null?"NULL":"ok")} isSpawningP2={Main.isSpawningP2}");
            }
            catch (System.Exception e) { Main.LogV("[InitComp] diagnostic snapshot ignored: " + e.Message); }

            if (!Main.isSpawningP2) return;
            var vlr = __instance.Camera?.gameObject.GetComponent("VolumetricLightRenderer") as UnityEngine.MonoBehaviour;
            if (vlr != null)
            {
                vlr.enabled = false;
                Main.LogV("[Patch1a] VolumetricLightRenderer blocked on P2 Camera");
            }
        }
    }

    
    
    
    
    [HarmonyPatch(typeof(EZCameraShake.CameraShaker), "Awake")]
    static class Patch_CameraShaker_Awake
    {
        static bool Prefix() => !Main.isSpawningP2;
    }

    
    
    
    [HarmonyPatch]
    static class Patch_LocalPlayerCamTransform_Set
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.PropertySetter(typeof(Network_Player), "LocalPlayerCameraTransform");
        static bool Prefix() => !Main.isSpawningP2;
    }

    
    
    //
    
    
    
    
    
    //
    
    
    
    [HarmonyPatch(typeof(SaveAndLoad), "RestoreUser")]
    static class Patch_SaveAndLoad_RestoreUser
    {
        static void Prefix()
        {
            var current = ComponentManager<Network_Player>.Value;
            bool destroyed = (UnityEngine.Object)current == null;
            bool statsNull = !destroyed && current.Stats == null;

            // 重载后 RestorePlayer 会 NRE(vanilla 在这里连点 Stats/Inventory/BuffManager 多个字段),
            // 而本 Prefix 原来的分支日志走 LogV(verbose,默认关)-> 看不出它判定了什么。
            // 字段清单保留(定位哪个为空很有用),但排查结束后降级回 LogV,平时不刷屏。
            Main.LogV("[RestoreUser] cm=" + (destroyed ? "destroyed/null" : current.name)
                + " realType=" + (ReferenceEquals(current, null) ? "-" : current.GetType().FullName)
                + " stats=" + (destroyed ? "-" : (current.Stats == null ? "NULL" : "ok"))
                + " inv=" + (destroyed ? "-" : (current.Inventory == null ? "NULL" : "ok"))
                + " buffMgr=" + (destroyed ? "-" : (current.BuffManager == null ? "NULL" : "ok"))
                + " script=" + (destroyed ? "-" : (current.PlayerScript == null ? "NULL" : "ok"))
                + " isLocal=" + (destroyed ? "-" : current.IsLocalPlayer.ToString()));

            if (!destroyed && !statsNull)
            {
                return;
            }

            Main.LogV(
                $"[RestoreUser] CM stale ({(destroyed ? "destroyed" : $"stats=null on {current.name}")}), scanning for valid local player");

            foreach (var p in UnityEngine.Object.FindObjectsOfType<Network_Player>())
            {
                if ((UnityEngine.Object)p == null) continue;
                if (!p.IsLocalPlayer) continue;
                if (p.Stats == null) continue;

                ComponentManager<Network_Player>.Value = p;
                Main.ModEntry.Logger.Log("[RestoreUser] Fixed: CM -> " + p.name);
                return;
            }

            Main.ModEntry.Logger.Log("[RestoreUser] WARNING: no valid local player with Stats found — RestorePlayer may crash");
        }
    }

    
    
    //
    
    
    
    //
    
    
    
    
    
    
    [HarmonyPatch(typeof(BlockCollisionConsolidator), "UpdateActiveCollision")]
    static class Patch_BlockCollisionConsolidator_LocalCoopActiveCells
    {
        static readonly Vector3 SpaceSize = new Vector3(500f, 200f, 500f);
        static readonly Vector3 SpaceOffset = new Vector3(-250f, -1f, -250f);
        static readonly Vector3 SpaceAbs = SpaceSize * 8f;
        static readonly int[] NeighbourOffsets = {
            0, 1, -1, -500, 500, -501, 499, -499, 501,
            -100000, -99999, -100001, -100500, -99500, -100501, -99501, -100499, -99499,
            100000, 100001, 99999, 99500, 100500, 99499, 100499, 99501, 100501
        };

        static readonly System.Reflection.FieldInfo localPlayerField =
            typeof(BlockCollisionConsolidator).GetField(
                "localPlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        static readonly System.Reflection.FieldInfo lastPlayerSpatialIndexField =
            typeof(BlockCollisionConsolidator).GetField(
                "lastPlayerSpatialIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        static readonly System.Reflection.FieldInfo blockMapField =
            typeof(BlockCollisionConsolidator).GetField(
                "blockMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        static readonly Dictionary<BlockCollisionConsolidator, HashSet<int>> activeCellsByInstance =
            new Dictionary<BlockCollisionConsolidator, HashSet<int>>();

        static bool Prefix(BlockCollisionConsolidator __instance)
        {
            // vanilla 的 UpdateActiveCollision 只判 localPlayer == null,却直接解引用
            // localPlayer.CameraTransform.position。Transform.position 直通原生 get_position_Injected,
            // 【不做 Unity 的假 null 检查】-> 对已销毁/悬垂的 Transform 取值不是抛托管异常,
            // 而是原生崩溃(日志里只有 Crash!!! 和一段无符号栈)。原版永远碰不到:
            // 本地玩家与其相机同生共死;但分屏要重建相机装配,重载 mod 后会撞上。
            //
            // 【无条件】把缓存换成 FindObjectsOfType 验证过的活玩家,而不是只在缓存非空时才动手——
            // 上一版留了个洞:缓存为 null 的那一帧直接放行,vanilla 自己去取 ComponentManager 的值,
            // 而那个值可能就是坏的。FindObjectsOfType 只返回真正活着的对象,
            // ReferenceEquals 是纯托管比较(不碰原生内存,对悬垂引用也安全)。
            var live = ResolveLiveLocalPlayer();
            var cachedLocal = localPlayerField != null
                ? localPlayerField.GetValue(__instance) as Network_Player : null;

            if (live == null)
            {
                if (localPlayerField != null) localPlayerField.SetValue(__instance, null);
                return false;   // 本帧跳过,等 vanilla 下一帧自己重新取
            }
            if (live.CameraTransform == null) return false;   // 相机还没装配好,别让 vanilla 去裸取
            if (localPlayerField != null && !ReferenceEquals(cachedLocal, live))
                localPlayerField.SetValue(__instance, live);

            if (Main.player2 == null) return true;
            if (localPlayerField == null || lastPlayerSpatialIndexField == null || blockMapField == null) return true;

            var blockMap = blockMapField.GetValue(__instance) as Dictionary<int, List<Block>>;
            if (blockMap == null) return true;

            var wanted = BuildWantedCells(__instance);
            if (wanted.Count == 0) return true;

            if (!activeCellsByInstance.TryGetValue(__instance, out var activeCells))
            {
                activeCells = new HashSet<int>();
                activeCellsByInstance[__instance] = activeCells;
                SeedOriginalActiveCells(__instance, activeCells);
            }

            foreach (int cell in activeCells)
            {
                if (!wanted.Contains(cell))
                    SetCellBlocksActive(blockMap, cell, false);
            }

            foreach (int cell in wanted)
            {
                SetCellBlocksActive(blockMap, cell, true);
            }

            activeCells.Clear();
            foreach (int cell in wanted)
                activeCells.Add(cell);

            var p1 = Main.player1 ?? ComponentManager<Network_Player>.Value;
            if (p1 != null)
                localPlayerField.SetValue(__instance, p1);
            lastPlayerSpatialIndexField.SetValue(__instance, GetCoreCell(__instance, p1) ?? GetCoreCell(__instance, Main.player2) ?? 0);

            return false;
        }

        static Network_Player _sessionLocal;
        static int _sessionLocalFrame = -9999;

        // 当前会话里真正活着的本地玩家(P1)。节流缓存,避免每帧全场扫描。
        static Network_Player ResolveLiveLocalPlayer()
        {
            if (_sessionLocal != null && Time.frameCount - _sessionLocalFrame < 300)
                return _sessionLocal;
            _sessionLocal = null;
            var all = UnityEngine.Object.FindObjectsOfType<Network_Player>();
            for (int i = 0; i < all.Length; i++)
            {
                var np = all[i];
                if (np == null || np == Main.player2) continue;
                if (np.IsLocalPlayer) { _sessionLocal = np; break; }
            }
            _sessionLocalFrame = Time.frameCount;
            return _sessionLocal;
        }

        internal static void Reapply(BlockCollisionConsolidator instance)
        {
            if (Main.player2 == null || instance == null || blockMapField == null) return;
            if (!activeCellsByInstance.TryGetValue(instance, out var activeCells)) return;
            var blockMap = blockMapField.GetValue(instance) as Dictionary<int, List<Block>>;
            if (blockMap == null) return;

            foreach (int cell in activeCells)
                SetCellBlocksActive(blockMap, cell, true);
        }

        static HashSet<int> BuildWantedCells(BlockCollisionConsolidator instance)
        {
            var wanted = new HashSet<int>();
            AddPlayerCells(instance, Main.player1, wanted);
            AddPlayerCells(instance, Main.player2, wanted);
            return wanted;
        }

        static void AddPlayerCells(BlockCollisionConsolidator instance, Network_Player player, HashSet<int> cells)
        {
            int? core = GetCoreCell(instance, player);
            if (!core.HasValue) return;

            for (int i = 0; i < NeighbourOffsets.Length; i++)
                cells.Add(core.Value + NeighbourOffsets[i]);
        }

        static int? GetCoreCell(BlockCollisionConsolidator instance, Network_Player player)
        {
            if (instance == null || player == null) return null;
            var t = player.CameraTransform != null ? player.CameraTransform : player.transform;
            if (t == null) return null;

            Vector3 localPos = instance.transform.InverseTransformPoint(t.position);
            var cell = BlockCollisionConsolidator.PositionToCellIndex(localPos, SpaceSize, SpaceAbs, 8f, SpaceOffset);
            return cell.Item1 ? cell.Item2 : (int?)null;
        }

        static void SeedOriginalActiveCells(BlockCollisionConsolidator instance, HashSet<int> activeCells)
        {
            if (instance == null || lastPlayerSpatialIndexField == null) return;
            int original = 0;
            try { original = (int)lastPlayerSpatialIndexField.GetValue(instance); }
            catch { return; }

            for (int i = 0; i < NeighbourOffsets.Length; i++)
                activeCells.Add(original + NeighbourOffsets[i]);
        }

        static void SetCellBlocksActive(Dictionary<int, List<Block>> blockMap, int cell, bool active)
        {
            if (!blockMap.TryGetValue(cell, out var blocks) || blocks == null) return;

            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                var block = blocks[i];
                if (block == null)
                {
                    blocks.RemoveAt(i);
                    continue;
                }

                var colliders = block.blockColliders;
                if (colliders == null) continue;
                for (int j = 0; j < colliders.Length; j++)
                {
                    if (colliders[j] != null)
                        colliders[j].enabled = active;
                }
            }
        }
    }

    [HarmonyPatch(typeof(BlockCollisionConsolidator), "Update")]
    static class Patch_BlockCollisionConsolidator_Update_ReapplyLocalCoopCells
    {
        static void Postfix(BlockCollisionConsolidator __instance)
        {
            Patch_BlockCollisionConsolidator_LocalCoopActiveCells.Reapply(__instance);
        }
    }

    
    
    
    
    
    
    [HarmonyPatch(typeof(SaveAndLoad), "SaveUser")]
    static class Patch_SaveAndLoad_SaveUser
    {
        static readonly System.Reflection.FieldInfo localPlayerField =
            typeof(SaveAndLoad).GetField("localPlayer",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        static void Prefix()
        {
            if (localPlayerField == null) return;
            var p1 = Main.player1;
            if (p1 == null) return;
            
            var cur = localPlayerField.GetValue(null) as Network_Player;
            bool curBad = (UnityEngine.Object)cur == null || cur.Stats == null || !cur.IsLocalPlayer;
            if (curBad)
            {
                localPlayerField.SetValue(null, p1);
                Main.ModEntry.Logger.Log($"[SaveUser] localPlayer 被污染，强制改回真正 P1 = {p1.name}");
            }
        }

        
        static void Postfix()
        {
            if (Main.player2 != null)
                P2SaveStore.SaveP2(Main.player2);
        }
    }

    
    
    
    
    
    
    
    
    
    [HarmonyPatch(typeof(SaveAndLoad), "SaveWorld")]
    static class Patch_SaveAndLoad_SaveWorld
    {
        public static bool SavingWorld;

        static void Prefix()
        {
            PlayerContext.Clear();
            SavingWorld = true;
            var genuine = PlayerContext.ResolveGenuineLocalPlayer();
            if (genuine != null) ComponentManager<Network_Player>.Value = genuine;
        }

        static void Finalizer()
        {
            SavingWorld = false;
        }
    }

}
