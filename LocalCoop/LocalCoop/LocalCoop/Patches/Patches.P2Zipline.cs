using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch: ZiplinePlayer.AttachToZipline — 修 P2 上索 NRE。
    //   症状:P2 看滑索有提示、按 X 触发挂接,但 AttachToZipline 内 NRE(表面=按X没反应)。
    //   根因:ZiplinePlayer.Start 用 GetComponentInParent 设 player/lockedPivot/canvas/soundManager,
    //    P2 克隆在层级就绪前跑了 Start → 这些运行时字段为 null → 挂接首个 player.IsLocalPlayer 即崩。
    //   修复:挂接前对【P2 的 ZiplinePlayer】补齐这四个字段(空才补) + 按 P2 装备的滑索工具预设模型索引。
    //   (FP 专属分支因 P2 恒第三人称被跳过;currentModel 由 EnsureP2ThirdPerson 每帧补,通常已就绪。)
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ZiplinePlayer), "AttachToZipline")]
    static class Patch_ZiplinePlayer_Attach_P2Fix
    {
        static FieldInfo _fPlayer, _fLockedPivot, _fCanvas, _fSound, _fModelIdx;
        static void Prefix(ZiplinePlayer __instance)
        {
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;   

            if (_fPlayer == null)
            {
                var t = typeof(ZiplinePlayer);
                var bf = BindingFlags.Instance | BindingFlags.NonPublic;
                _fPlayer      = t.GetField("player", bf);
                _fLockedPivot = t.GetField("lockedPivot", bf);
                _fCanvas      = t.GetField("canvas", bf);
                _fSound       = t.GetField("soundManager", bf);
                _fModelIdx    = t.GetField("currentZiplineModelIndex", bf);
            }

            // 补齐 P2 的运行时字段(Start 时层级未就绪 → 这些为 null,挂索会 NRE)。
            if (_fPlayer?.GetValue(__instance) == null) _fPlayer?.SetValue(__instance, Main.player2);
            if (_fLockedPivot?.GetValue(__instance) == null)
            {
                var gm = SingletonGeneric<GameManager>.Singleton;
                if (gm != null) _fLockedPivot?.SetValue(__instance, gm.lockedPivot);
            }
            if (_fCanvas?.GetValue(__instance) == null)
                _fCanvas?.SetValue(__instance, ComponentManager<CanvasHelper>.Value);
            if (_fSound?.GetValue(__instance) == null)
                _fSound?.SetValue(__instance, ComponentManager<SoundManager>.Value);

            // 模型索引:原版靠 GetEquipmentSlotFromEquipmentType(ZiplineTool) 选(UniqueIndex==549→马达索1,否则基础索0)。
            //  对 P2 那个调用返回 null(P2 装备不在共享单例槽)→ if 被跳过 → 保留此处预设值。
            //  据 P2 实际装备的滑索工具预设:不设的话默认恒为 1(马达索)→ 不重力滑、需推杆、马达模型在 P2 身上没装好。
            var tool = P2EquipmentStore.GetEquipped(EquipSlotType.ZiplineTool);
            int idx = (tool != null && tool.baseItem != null && tool.baseItem.UniqueIndex == 549) ? 1 : 0;
            _fModelIdx?.SetValue(__instance, idx);
        }
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
    }

    // ══════════════════════════════════════════════════════════════════════
    //  P2 滑索 —— 上索已由设备框架覆盖(MeshPath_Zipline 是 IRaycastable：localPlayer→P2 + "Interact"→X +
    //   HasEquipmentOfTypeEquipped(ZiplineTool) 走共享装备槽，已装则通过 → P2.ZiplinePlayer.AttachToZipline)。
    //  剩下的缺口是【骑乘】：ZiplinePlayer.Update 门控 IsLocalPlayer，对 P2 跑远端显示分支(UpdateZiplineRemote)。
    //  做法：把 ZiplinePlayer.Update 整段包进 P2OriginalScope.Tool()(强制本地 → 跑本地骑乘分支：移动/马达/脱离)。
    //   - 马达 inputMove.ReadValue<Vector2>()(读 P1 移动轴=0)→ transpiler 换 P2HookAim.ReadMove(读 P2 左摇杆)。
    //   - 脱离 WasPressedThisFrame("Cancel"/"Jump")→ 下面 CIC 补丁在 IsP2OriginalActive 时路由到 P2 B/A。
    //  (P2 恒第三人称：AttachToZipline 里 FP 专属分支被跳过，走第三人称路径。)
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ZiplinePlayer), "Update")]
    static class Patch_ZiplinePlayer_Update_P2
    {
        static bool Prefix(ZiplinePlayer __instance, ref HookP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return true;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return true;

            // P1's original rope preview is stored in MeshPathBase static state.
            // Do not let P2's vanilla update render a second preview from it.
            if (!__instance.IsAttachedToZipline && MeshPathBase.IsCreatingMeshPath)
            {
                if (__instance.buildZiplineRenderer != null)
                    __instance.buildZiplineRenderer.enabled = false;
                return false;
            }
            __state = new HookP2State { Ctx = P2FrameContext.Tool() };
            return true;
        }

        static void Postfix(ZiplinePlayer __instance, HookP2State __state)
        {
            ZiplineRenderFix.SyncToolTransforms(__instance);
            var np = __instance != null ? __instance.GetComponentInParent<Network_Player>() : null;
            if (np != null && np == Main.player2 && !Main.IsP2FirstPerson)
                P2ThirdPersonCameraController.RefreshZiplinePositionAfterMove(np);   // 移动后重定位TP相机(修一跳一跳)
            Cleanup(__state);
        }

        static Exception Finalizer(ZiplinePlayer __instance, Exception __exception, HookP2State __state)
        {
            if (__exception == null) ZiplineRenderFix.SyncToolTransforms(__instance);
            Cleanup(__state);
            return __exception;
        }
        static void Cleanup(HookP2State st) { if (st == null) return; st.Ctx?.Dispose(); st.Ctx = null; }

        // 马达模拟量：inputMove.ReadValue<Vector2>() → P2HookAim.ReadMove(读 P2 左摇杆)。
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => TranspilerGuard.Verify(instructions, TranspilerCore);

        static IEnumerable<CodeInstruction> TranspilerCore(IEnumerable<CodeInstruction> instructions)
        {
            var readVec = AccessTools.Method(typeof(InputAction), "ReadValue", Type.EmptyTypes, new[] { typeof(UnityEngine.Vector2) });
            var repl    = AccessTools.Method(typeof(P2HookAim), "ReadMove");
            foreach (var ins in instructions)
            {
                if (readVec != null && ins.Calls(readVec)) yield return new CodeInstruction(OpCodes.Call, repl);
                else yield return ins;
            }
        }
    }

    //  P2 脱离滑索:① 下一帧重装手持(EquipP2)→ 解决"脱离后手持工具模型不显示,需切栏才出现"
    //   (滑索时模型被 HideItemInHand 隐藏,原版 ReselectCurrentSlot 重显的是共享热栏,非 P2 自定义手持)。
    //  ② 保留诊断 [ZipDetach](排查偶发"脱离穿筏掉水":位置/碰撞体/外部速度/父节点)。
    [HarmonyPatch(typeof(ZiplinePlayer), "DetachFromCurrentZipline")]
    static class Patch_ZiplinePlayer_Detach_ViewState
    {
        static void Postfix(ZiplinePlayer __instance)
        {
            var np = __instance.GetComponentInParent<Network_Player>();
            bool isP1 = np != null && np == Main.player1;
            bool isP2 = np != null && np == Main.player2;
            if (!isP1 && !isP2) return;
            if (isP1) AlignP1RootYawToCamera(np);
            if (isP2)
            {
                Main._p2RefreshHeldNextFrame = true;
                Main.SuppressP2CrouchOnExit();   // B 同时绑着取消和蹲:按 B 脱离滑索不应落地即蹲
            }
        }

        internal static void AlignP1RootYawToCamera(Network_Player np)
        {
            if (np == null || np != Main.player1 || np.Camera == null) return;
            var tp = np.GetComponentInChildren<ThirdPerson>();
            if (tp != null && tp.ThirdPersonState) return;

            float yaw = np.Camera.transform.eulerAngles.y;
            np.transform.eulerAngles = new Vector3(0f, yaw, 0f);
            if (np.playerPivot != null)
            {
                var e = np.playerPivot.localEulerAngles;
                np.playerPivot.localEulerAngles = new Vector3(e.x, 0f, e.z);
            }
            AlignP1MouseLookTargets(np);
        }

        internal static void AlignP1MouseLookTargets(Network_Player np)
        {
            if (np == null || np != Main.player1 || np.PlayerScript == null) return;
            var mx = np.PlayerScript != null ? np.PlayerScript.mouseLookXScript : null;
            if (mx != null)
            {
                mx.isChild = false;
                mx.SetTargetRotX(mx.transform.eulerAngles.y, true);
            }
        }

        internal static void AlignP1ZiplineFpMouseLookTargets(Network_Player np)
        {
            if (np == null || np != Main.player1 || np.PlayerScript == null) return;
            var mx = np.PlayerScript.mouseLookXScript;
            if (mx == null) return;
            float yaw = mx.isChild ? mx.transform.localEulerAngles.y : mx.transform.eulerAngles.y;
            mx.SetTargetRotX(yaw, false);
        }
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
    }

    
    
    
    
    
    
    
    [HarmonyPatch(typeof(ZiplinePlayer), "Update")]
    static class Patch_ZiplinePlayer_Update_ViewState
    {
        static void Postfix(ZiplinePlayer __instance)
        {
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null) return;
            if (np == Main.player1) P1ZiplineViewState.Tick(__instance, np);
        }
    }

    [HarmonyPatch(typeof(ZiplinePlayer), "Update")]
    static class Patch_ZiplinePlayer_BuildPreviewLayer
    {
        static void Postfix(ZiplinePlayer __instance)
        {
            var np = __instance != null ? __instance.GetComponentInParent<Network_Player>() : null;
            if (np != Main.player1 || !MeshPathBase.IsCreatingMeshPath || __instance.buildZiplineRenderer == null) return;

            // The original preview belongs to P1's player layer, which P1 FP
            // correctly hides with the body. Render only the temporary rope on
            // the shared remote-world layer so both world cameras see it intact.
            int layer = LayerMask.NameToLayer("RemotePlayer");
            if (layer >= 0) __instance.buildZiplineRenderer.gameObject.SetLayerSafe(layer);
        }
    }

    static class P1ZiplineViewState
    {
        static bool _mouseLookPrepared;

        internal static void Tick(ZiplinePlayer ziplinePlayer, Network_Player np)
        {
            if (np == null || np != Main.player1 || np.PlayerScript == null) return;
            bool attached = ziplinePlayer != null && ziplinePlayer.IsAttachedToZipline;
            var tp = np.GetComponentInChildren<ThirdPerson>();
            bool fpZipline = attached && (tp == null || !tp.ThirdPersonState);
            if (!fpZipline)
            {
                _mouseLookPrepared = false;
                return;
            }

            var ps = np.PlayerScript;
            var lookY = ps.mouseLookYScript;
            var lookYCam = ps.mouseLookYCameraScript;

            if (!_mouseLookPrepared)
            {
                if (lookY != null && lookYCam != null)
                {
                    lookY.CopyRotationToOther(lookYCam);
                    lookY.ResetRotations();
                }
                _mouseLookPrepared = true;
            }

            if (lookY != null) lookY.enabled = false;
            if (lookYCam != null) lookYCam.enabled = true;

            if (np.playerPivot != null)
            {
                var e = np.playerPivot.localEulerAngles;
                np.playerPivot.localEulerAngles = new Vector3(0f, 0f, e.z);
            }

            if (np.ZiplinePlayer != null)
                ZiplineRenderFix.SyncToolTransforms(np.ZiplinePlayer);
        }
    }

    // 上滑索保持上索前的视角朝向。
    // 原版 AttachPlayer.StartCarryingPlayer 在附着瞬间 transform.localEulerAngles = Vector3.zero 且
    // playerPivot.localEulerAngles = Vector3.zero,把朝向归到父级(滑索的 firstPersonParent 通常是
    // LockedPivot=筏体)基准;而 TP 相机的基向量取自根节点、camYaw = rootYaw + localCameraRotation.y,
    // localCameraRotation 又留着上索前的旧值 —— 于是上索瞬间人和镜头一起跳到筏体正方向,背对缆绳是常态。
    //
    // ⚠ 有意偏离原版(2026-07-20 用户明确要求,勿当污染修回去):把轨道偏航按归零后的新根重算,
    // 使相机世界偏航 == 上索前那一刻;身体每帧跟视角走(见 SyncBodyYaw / P2ThirdPersonCameraController),
    // 所以只要镜头不跳,人也就不跳。
    // 注意:别改成"冻结身体朝向"来实现不跳 —— vanilla 电动马达的方向判定
    // CalculateInitialSpeedBetweenPathAndPlayer 读的就是 playerPivot.forward,冻住身体 = 马达失去转向,
    // W 恒等于路径正方向、永远往末端杆子里怼(2026-07-20 实测踩过这个坑)。
    [HarmonyPatch(typeof(ZiplinePlayer), "AttachToZipline")]
    static class Patch_ZiplinePlayer_Attach_KeepViewYaw
    {
        internal struct KeepYaw { internal bool Valid; internal float CamYaw; }

        static void Prefix(ZiplinePlayer __instance, ref KeepYaw __state)
        {
            __state = new KeepYaw();
            var np = __instance != null ? __instance.GetComponentInParent<Network_Player>() : null;
            if (np == null || (np != Main.player1 && np != Main.player2)) return;
            if (np.currentModel == null || np.Camera == null) return;
            var tp = np.currentModel.thirdPersonSettings;
            if (tp == null || !tp.ThirdPersonState) return;   // FP 身体跟 mouseLookX,原版没这问题
            __state.Valid  = true;
            __state.CamYaw = np.Camera.transform.eulerAngles.y;
        }

        static void Postfix(ZiplinePlayer __instance, KeepYaw __state)
        {
            if (!__state.Valid) return;
            var np = __instance != null ? __instance.GetComponentInParent<Network_Player>() : null;
            if (np == null || !__instance.IsAttachedToZipline || np.currentModel == null) return;
            var tp = np.currentModel.thirdPersonSettings;
            var fRot = VanillaAccessors.ThirdPersonLocalCameraRotation;
            if (tp == null || fRot == null) return;

            var rot = (Vector3)fRot.GetValue(tp);
            rot.y = Mathf.DeltaAngle(0f, __state.CamYaw - np.transform.eulerAngles.y);
            fRot.SetValue(tp, rot);
        }
    }

    [HarmonyPatch(typeof(ThirdPerson), "SetThirdPersonModel")]
    static class Patch_ThirdPerson_SetModel_ZiplineNoReequip
    {
        static FieldInfo _fNet;
        struct LayerState { public int hash; public float time; }
        static readonly System.Collections.Generic.List<LayerState> _saved = new System.Collections.Generic.List<LayerState>();
        static bool _cap;

        static Network_Player Net(ThirdPerson tp)
        {
            if (_fNet == null) _fNet = typeof(ThirdPerson).GetField("playerNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
            return _fNet?.GetValue(tp) as Network_Player;
        }

        static void Prefix(ThirdPerson __instance)
        {
            _cap = false; _saved.Clear();
            var np = Net(__instance);
            if (np == null || np.ZiplinePlayer == null || !np.ZiplinePlayer.IsAttachedToZipline) return;
            var anim = np.Animator != null ? np.Animator.anim : null;
            if (anim == null || anim.runtimeAnimatorController == null) return;
            for (int i = 0; i < anim.layerCount; i++)
            {
                var st = anim.GetCurrentAnimatorStateInfo(i);
                _saved.Add(new LayerState { hash = st.shortNameHash, time = st.normalizedTime });
            }
            _cap = true;
        }

        static void Postfix(ThirdPerson __instance)
        {
            if (!_cap) return; _cap = false;
            var np = Net(__instance);
            var anim = np != null && np.Animator != null ? np.Animator.anim : null;
            if (anim == null) return;
            for (int i = 0; i < _saved.Count && i < anim.layerCount; i++)
                anim.Play(_saved[i].hash, i, _saved[i].time);   // 跳回切换前的状态(滑索姿势),不走重入过渡
            anim.Update(0f);                                    // 立即应用,消除一帧默认态(否则仍闪一下重装动画)
        }
    }

    [HarmonyPatch(typeof(ThirdPerson), "HandleThirdPerson")]
    static class Patch_ThirdPerson_HandleThirdPerson_ZiplineBodyYaw
    {
        static readonly FieldInfo _fCameraTransform =
            typeof(ThirdPerson).GetField("cameraTransform", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _fThirdPersonState =
            typeof(ThirdPerson).GetField("thirdPersonState", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Postfix(ThirdPerson __instance)
        {
            var np = __instance != null ? __instance.GetComponentInParent<Network_Player>() : null;
            if (np == null || np.ZiplinePlayer == null || !np.ZiplinePlayer.IsAttachedToZipline) return;
            if (np.playerPivot == null || np.currentModel == null) return;
            if (np != Main.player1 && np != Main.player2) return;

            bool thirdPersonState = (bool)(_fThirdPersonState?.GetValue(__instance) ?? false);
            if (!thirdPersonState) return;

            // 滑索期间身体跟视角走(有意偏离 vanilla 的 !IsAttached 守卫,P1 侧)。理由同 P2:
            // vanilla 马达方向判定读 playerPivot.forward,冻住身体就等于冻住转向。
            SyncBodyYaw(np, __instance);
            ZiplineRenderFix.SyncToolTransforms(np.ZiplinePlayer);
        }

        internal static void SyncBodyYaw(Network_Player np, ThirdPerson thirdPerson = null)
        {
            if (np == null || np.playerPivot == null || np.ZiplinePlayer == null || !np.ZiplinePlayer.IsAttachedToZipline) return;
            if (np != Main.player1 && np != Main.player2) return;

            float yaw;
            if (np == Main.player2 && Main.IsP2FirstPerson)
            {
                yaw = P2CameraController.Yaw;   // P2 没有 vanilla mouseLookX,FP 身体跟自建相机
            }
            else
            {
                if (thirdPerson == null) thirdPerson = np.GetComponentInChildren<ThirdPerson>();
                var cam = thirdPerson != null ? _fCameraTransform?.GetValue(thirdPerson) as Transform : null;
                if (cam != null) yaw = cam.eulerAngles.y;
                else if (np.Camera != null) yaw = np.Camera.transform.eulerAngles.y;
                else return;
            }

            var e = np.playerPivot.localEulerAngles;
            np.playerPivot.localEulerAngles =
                new Vector3(0f, Mathf.DeltaAngle(0f, yaw - np.transform.eulerAngles.y), e.z);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Phase B 功能门控:P2 上下文(设备射线/工具)里 HasEquipmentOfTypeEquipped 读【P2 装备集】。
    //   原版读 localPlayer.Inventory(共享单例=P1)的 equipSlots → P2 滑索判定会误读 P1 的装备。
    //   滑索 OnIsRayed(isProcessingP2Ray 期)的 HasEquipmentOfTypeEquipped(ZiplineTool) 据此对 P2 正确:
    //    P2 装了滑索工具 → 显示 Attach(X);没装 → "需要滑索工具",不显示 X。
    //   (护甲减伤无需门控:Equipment_ArmorPiece 走 P2 自己的 ArmorHandler,Phase A 的 EquipItemNetwork 已使其生效。)
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(PlayerInventory), "HasEquipmentOfTypeEquipped")]
    static class Patch_PlayerInv_HasEquip_P2
    {
        static bool Prefix(EquipSlotType equipmentType, ref bool __result)
        {
            if (!(Main.isProcessingP2Ray || Main.IsP2OriginalActive)) return true;   // 非 P2 上下文 → 原版(P1)
            __result = P2EquipmentStore.HasEquipped(equipmentType);
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: PlayerItemManager.ShouldItBeBusy — 对 P2 做 null 安全。
    //   原版逐个访问 value.BedComponent/CarryingComponent/ZiplinePlayer/PlayerNetworkManager.xxx,
    //   P2 克隆体某些子组件为空 → 滑索脱离(StopCarryingPlayer→ShouldItBeBusy)NRE → 脱离不完成 →
    //   IsBusy(静态共享)卡住 → P1 也开不了背包/ESC + 双方姿势残留。
    //   修复:仅当 value==P2 时逐项 null 安全计算;P1 走原版(零影响)。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(PlayerItemManager), "ShouldItBeBusy")]
    static class Patch_PIM_ShouldItBeBusy_P2Safe
    {
        static bool Prefix(ref bool __result)
        {
            var v = ComponentManager<Network_Player>.Value;
            if (v == null || v != Main.player2) return true;   // 非 P2 → 原版
            __result = (v.BedComponent != null && v.BedComponent.Sleeping)
                    || (v.CarryingComponent != null && v.CarryingComponent.IsCarrying)
                    || (v.ZiplinePlayer != null && v.ZiplinePlayer.IsAttachedToZipline)
                    || (v.PlayerNetworkManager != null && v.PlayerNetworkManager.IsAttached);
            return false;
        }
    }

    [HarmonyPatch(typeof(ZiplinePlayer), "Update")]
    static class Patch_ZiplinePlayer_Update_P1PauseGuard
    {
        static void Prefix(ZiplinePlayer __instance)
        {
            P1ZiplinePauseGuard.InP1ZiplineUpdate = false;
            var np = __instance != null ? __instance.GetComponentInParent<Network_Player>() : null;
            P1ZiplinePauseGuard.InP1ZiplineUpdate =
                np == Main.player1 && __instance != null && __instance.IsAttachedToZipline;
        }

        static void Postfix()
        {
            P1ZiplinePauseGuard.InP1ZiplineUpdate = false;
        }

        static Exception Finalizer(Exception __exception)
        {
            P1ZiplinePauseGuard.InP1ZiplineUpdate = false;
            return __exception;
        }
    }

    static class P1ZiplinePauseGuard
    {
        internal static bool InP1ZiplineUpdate;

        internal static bool ShouldBlockCancel(string key)
        {
            return InP1ZiplineUpdate
                   && key == "Cancel"
                   && Input.GetKeyDown(KeyCode.Escape);
        }
    }

    // P2 工具/原版上下文期间，把 "Jump"/"Cancel" 的 WasPressedThisFrame 路由到 P2 A/B(滑索脱离用)。
    //  其它键不动；窗口外(P1)走原版。与建造 CIC 补丁键不重叠(LMB/Rotate/Remove/BlockPick)，无冲突。
    [HarmonyPatch(typeof(CustomInputConfig), "WasPressedThisFrame", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CIC_WasPressed_P2JumpCancel
    {
        static bool Prefix(string key, ref bool __result)
        {
            if (P1ZiplinePauseGuard.ShouldBlockCancel(key))
            {
                __result = false;
                return false;
            }
            if (!Main.IsP2OriginalActive) return true;
            if (key == "Jump")   { __result = Main.p2ActionJump?.WasPressedThisFrame() ?? false; return false; }
            if (key == "Cancel") { __result = Main.p2ActionCancel?.WasPressedThisFrame() ?? false; return false; }
            return true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  P2ZiplineDriver —— 每帧手动驱动 P2 的 ZiplinePlayer.Update。
    //   根因(对比源码):P2 是 Network_Player 克隆,其 ZiplinePlayer 物体未激活 → Unity 不调它的 Update
    //    → 持续骑乘逻辑(移动/模型维持/脱离/相机锁,全在 Update 里)从未运行;只有一次性的 AttachToZipline 跑了。
    //   做法(同 P2ToolRunner):反射调 ZiplinePlayer.Update()。该调用会触发已挂的 Patch_ZiplinePlayer_Update_P2
    //    (Prefix 设 P2FrameContext.Tool() 强制本地 → 跑本地骑乘分支;Transpiler 把马达输入换 P2 左摇杆)。
    //   单次执行:物体未激活 → Unity 不会另调一次,无双跑。
    // ══════════════════════════════════════════════════════════════════════
    internal static class P2ZiplineDriver
    {
        static ZiplinePlayer _zp;

        static ZiplinePlayer Zip()
        {
            if (Main.player2 == null) { _zp = null; return null; }
            // 用 vanilla 的 Network_Player.ZiplinePlayer 属性(InitializeComponents 设的真实那个)。不用
            // GetComponentInChildren:FP rig 克隆(P2_FirstPersonArms)也带一个 ZiplinePlayer(behaviours 禁用、
            // 永不 attach),GetComponentInChildren 可能命中克隆那个 → IsAttachedToZipline 恒 false → 误判 P2 未上滑索
            // (HandleThirdPerson 把滑索误当网络附着座位而锁座姿跳过相机驱动 = TP相机冻结根因)。
            var real = Main.player2.ZiplinePlayer;
            _zp = real != null ? real : Main.player2.GetComponentInChildren<ZiplinePlayer>(true);   
            // 激活该物体:① 子模型(ziplineTool/Electric)activeInHierarchy→true 才能渲染;② Unity 接管 tick 它的 Update
            //  (经 Patch_ZiplinePlayer_Update_P2 在 P2 上下文跑骑乘);③ 其 Start 跑→player/lockedPivot/canvas/sound 自动就位。
            //  克隆体默认未激活(故之前 Unity 不 tick、模型不渲染、字段为 null)。
            if (_zp != null && !_zp.gameObject.activeSelf) _zp.gameObject.SetActive(true);
            return _zp;
        }

        // P2 是否正挂在滑索上(供 Hammer/工具/建造 tick 据此让路)。
        internal static bool IsAttached { get { var zp = Zip(); return zp != null && zp.IsAttachedToZipline; } }

        internal static void Reset()
        {
            _zp = null;
            P2ZiplineRopeBuilder.Reset();
        }

        // 每帧调用:确保 ZiplinePlayer 物体已激活(Zip 内做)→ Unity 接管 tick 其 Update(经补丁在 P2 上下文跑骑乘),无需手动反射驱动。
        internal static void Tick() { Zip(); }

        // P2 挂滑索时保持 ZiplinePlayer GO 激活。vanilla 恒 active(只 toggle 子物体);mod 把它当
        // "克隆预挂模型残留"关掉,FP 有渲染路径重激活但 TP 没有 → GO 停用 → Unity 跳过 Update →
        // 不移动/读不到退出输入/工具层不刷(卡住+下不来+不显)。每帧在所有早退之前调用(Tick 顶部),
        // 恢复 vanilla"该 GO 恒 active"不变量。诊断已证父级全 active,只需激活 GO 本身。
        internal static void EnsureActiveIfAttached()
        {
            if (Main.player2 == null) return;
            var zp = Main.player2.ZiplinePlayer;
            if (zp == null || !zp.IsAttachedToZipline) return;
            if (!zp.gameObject.activeSelf) zp.gameObject.SetActive(true);
        }
    }

    // 电动滑索末端顶死解救。
    // 原版 UpdateZiplineMovement 把加速/马达推力和位移一起写在 if (!raycastHit) 里:一旦贴上末端
    // 杆子(Placeable_ZiplineBase,层 Block,本就在 MASK_ZiplineCollision 内),射线每帧命中 → 推力
    // 永远执行不到;而 BounceZiplineSpeed 要求 |speed| > 0.5 才弹,速度被历次弹开砍到阈值以下就
    // 彻底冻结 → 马达按到底也开不出来。单人不开分屏实测同样卡死,已确认是原版死角、非分屏污染。
    //
    // ⚠ 有意偏离原版(2026-07-20 用户拍板,勿当污染修回去):仅在"确已顶死 + 玩家正朝远离该端的
    // 方向推"时给一个刚过弹开阈值的速度,让 vanilla 下一帧自己接管;朝杆子里推则不管(保留原版
    // 手感)。不带马达的普通滑索完全不碰 —— 那种情况原版的出路本来就是按跳跃键脱离。
    // 滑索碰撞射线的方向修正(修 vanilla 自身的坐标空间错用)。
    // vanilla UpdateZiplineMovement:
    //     Vector3 vector3 = vector2 - localPlayerZiplinePosition;              // 路径【局部】坐标差
    //     Physics.Raycast(path.transform.TransformPoint(...), vector3, ...);   // 起点世界、方向却没转
    // 滑索挂在木筏上时 path.transform 的父级是 LockedPivot(带旋转),方向就被整个拧走。实测
    // 2026-07-20:pathRot.y=180.44 → 前后判定精确反转(该走的一侧判成堵、撞杆子的一侧判成通),
    // 表现为末端吸附:越想出来越被推回末端,还伴随反复撞击音。不在筏上的滑索转角约等于零,
    // 所以原版自测碰不到 —— 这是原版 bug,不是分屏污染(单人不开分屏同样复现)。
    [HarmonyPatch(typeof(ZiplinePlayer), "UpdateZiplineMovement")]
    static class Patch_ZiplinePlayer_RayDirWorldSpace
    {
        static FieldInfo _fPath;
        [ThreadStatic] static Transform _current;

        internal static Transform CurrentPathTransform { get { return _current; } }

        static void Prefix(ZiplinePlayer __instance)
        {
            if (_fPath == null) _fPath = AccessTools.Field(typeof(ZiplinePlayer), "ziplinePath");
            var path = _fPath != null ? _fPath.GetValue(__instance) as MeshPath_Zipline : null;
            _current = path != null ? path.transform : null;
        }
        static void Postfix() { _current = null; }
        static Exception Finalizer(Exception __exception) { _current = null; return __exception; }

        internal static bool RaycastWorldDir(Vector3 origin, Vector3 localDir, out RaycastHit hit,
                                            float maxDistance, int layerMask, QueryTriggerInteraction qti)
        {
            var tr = _current;
            Vector3 dir = tr != null ? tr.TransformDirection(localDir) : localDir;
            return Physics.Raycast(origin, dir, out hit, maxDistance, layerMask, qti);
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => TranspilerGuard.Verify(instructions, TranspilerCore);

        static IEnumerable<CodeInstruction> TranspilerCore(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.Method(typeof(Physics), "Raycast", new[] {
                typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(),
                typeof(float), typeof(int), typeof(QueryTriggerInteraction) });
            var repl = AccessTools.Method(typeof(Patch_ZiplinePlayer_RayDirWorldSpace), "RaycastWorldDir");
            foreach (var ins in instructions)
            {
                if (target != null && repl != null && ins.Calls(target))
                    yield return new CodeInstruction(OpCodes.Call, repl);
                else
                    yield return ins;
            }
        }
    }

    [HarmonyPatch(typeof(ZiplinePlayer), "UpdateZiplineMovement")]
    static class Patch_ZiplinePlayer_MotorUnstuck
    {
        const float PinnedSpeed    = 0.5f;   // 与 BounceZiplineSpeed 的弹开阈值一致
        const float PinnedDuration = 0.2f;   // 位置连续不动多久算顶死
        const float EscapeSpeed    = 0.6f;   // 刚过阈值即可,后续加速交回 vanilla
        const float RayLength      = 0.4f;   // 与 vanilla UpdateZiplineMovement 的射线一致

        static FieldInfo _fSpeed, _fLocalPos, _fInput, _fPath, _fPlayer, _fMoveInfo, _fYOffset;
        static MethodInfo _mInitialSpeed;
        static readonly Dictionary<int, Vector3> _lastPos   = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, float>   _pinnedFor = new Dictionary<int, float>();
        static float _lastLog;

        static void EnsureMembers()
        {
            if (_fSpeed != null) return;
            var t = typeof(ZiplinePlayer);
            var bf = BindingFlags.Instance | BindingFlags.NonPublic;
            _fSpeed        = t.GetField("ziplineSpeed", bf);
            _fLocalPos     = t.GetField("localPlayerZiplinePosition", bf);
            _fInput        = t.GetField("movementInput", bf);
            _fPath         = t.GetField("ziplinePath", bf);
            _fPlayer       = t.GetField("player", bf);
            _fMoveInfo     = t.GetField("moveInfo", bf);
            _fYOffset      = t.GetField("zipplineYOffset", bf);
            _mInitialSpeed = t.GetMethod("CalculateInitialSpeedBetweenPathAndPlayer", bf);
        }

        // 复刻 vanilla 那条射线,但方向按 Patch_ZiplinePlayer_RayDirWorldSpace 的修正转成世界空间。
        // 目标点与当前点重合 = 已经顶到路径端点,同样算堵死。
        static bool Blocked(Transform pathTr, Vector3 origin, Vector3 delta)
        {
            if (delta.sqrMagnitude < 1e-8f) return true;
            Vector3 dir = pathTr != null ? pathTr.TransformDirection(delta) : delta;
            RaycastHit hit;
            return Physics.Raycast(origin, dir, out hit, RayLength,
                                   LayerMasks.MASK_ZiplineCollision, QueryTriggerInteraction.Ignore);
        }

        static void Postfix(ZiplinePlayer __instance)
        {
            if (__instance == null || !__instance.IsAttachedToZipline || !__instance.ViableForMotorUsage) return;
            EnsureMembers();
            if (_fSpeed == null || _fLocalPos == null || _fInput == null || _fPath == null ||
                _fPlayer == null || _fMoveInfo == null || _fYOffset == null || _mInitialSpeed == null) return;

            int key = __instance.GetInstanceID();
            float input = (float)_fInput.GetValue(__instance);
            float speed = (float)_fSpeed.GetValue(__instance);
            Vector3 pos = (Vector3)_fLocalPos.GetValue(__instance);

            Vector3 prev;
            bool moved = !_lastPos.TryGetValue(key, out prev) || (pos - prev).sqrMagnitude > 1e-8f;
            _lastPos[key] = pos;
            if (input == 0f || Mathf.Abs(speed) > PinnedSpeed || moved) { _pinnedFor[key] = 0f; return; }

            float pinned;
            _pinnedFor.TryGetValue(key, out pinned);
            pinned += Time.deltaTime;
            _pinnedFor[key] = pinned;
            if (pinned < PinnedDuration) return;

            var path = _fPath.GetValue(__instance) as MeshPath_Zipline;
            var np   = _fPlayer.GetValue(__instance) as Network_Player;
            if (path == null || np == null) return;

            // 用 vanilla 自己的前/后目标点各探一条射线,通的那侧就是脱困方向。
            // (moveInfo 是 internal struct,跨程序集只能按名字反射取字段。)
            object mi = _fMoveInfo.GetValue(__instance);
            if (mi == null) return;
            var miT = mi.GetType();
            var fFwd = miT.GetField("forwardPosition");
            var fBwd = miT.GetField("backwardPosition");
            if (fFwd == null || fBwd == null) return;
            Vector3 fwdTarget = (Vector3)fFwd.GetValue(mi);
            Vector3 bwdTarget = (Vector3)fBwd.GetValue(mi);

            float yOff = (float)_fYOffset.GetValue(__instance);
            Vector3 localUp = path.transform.InverseTransformDirection(path.transform.up).normalized;
            Vector3 origin = path.transform.TransformPoint(pos + localUp * yOff);
            bool fwdBlocked = Blocked(path.transform, origin, fwdTarget - pos);
            bool bwdBlocked = Blocked(path.transform, origin, bwdTarget - pos);

            // 速度为正 = 朝 forwardPosition 走(见 ZiplineMovementInfo.GeLocalTargetPositionFromSpeed)
            int escapeSign = 0;
            if (!fwdBlocked && bwdBlocked) escapeSign = 1;
            else if (fwdBlocked && !bwdBlocked) escapeSign = -1;

            // 玩家意图方向完全复用 vanilla 马达那套算法(朝向 vs 路径方向 × 前后输入)。
            float initial = (float)_mInitialSpeed.Invoke(__instance, new object[] { path, np });
            int wanted = (initial > 0f ? 1 : -1) * (int)Mathf.Sign(input);
            bool rescue = escapeSign != 0 && wanted == escapeSign;

            if (Main.VerboseDiagnostics && Time.time - _lastLog > 0.5f)
            {
                _lastLog = Time.time;
                Main.LogV("[ZipUnstuck] " + (np == Main.player2 ? "P2" : "P1")
                    + " spd=" + speed.ToString("F3") + " in=" + input.ToString("F2")
                    + " pinned=" + pinned.ToString("F2")
                    + " fwdBlocked=" + fwdBlocked + " bwdBlocked=" + bwdBlocked
                    + " escape=" + escapeSign + " wanted=" + wanted + " initial=" + initial.ToString("F2")
                    + " => " + (rescue ? "RESCUE" : "skip"));
            }

            if (!rescue) return;
            _fSpeed.SetValue(__instance, EscapeSpeed * escapeSign);
            _pinnedFor[key] = 0f;
        }
    }

    static class ZiplineRenderFix
    {
        static FieldInfo _fPath, _fTool, _fElectricTool, _fToolPivot, _fElectricToolPivot, _fModelIdx;

        internal struct RemoteVisualState
        {
            internal bool Active;
            internal Network_Player Player;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
        }

        internal static void SyncToolTransforms(ZiplinePlayer ziplinePlayer, int forcedLayer = -1)
        {
            if (ziplinePlayer == null || !ziplinePlayer.IsAttachedToZipline) return;
            EnsureFields();

            var path = _fPath?.GetValue(ziplinePlayer) as MeshPath_Zipline;
            if (path == null) return;
            var np = ziplinePlayer.GetComponentInParent<Network_Player>();
            if (np == null) return;

            Quaternion rotation;
            if (!TryGetZiplineRotation(ziplinePlayer, out rotation)) return;

            var tool = _fTool?.GetValue(ziplinePlayer) as GameObject;
            var electricTool = _fElectricTool?.GetValue(ziplinePlayer) as GameObject;
            var toolPivot = _fToolPivot?.GetValue(ziplinePlayer) as Transform;
            var electricPivot = _fElectricToolPivot?.GetValue(ziplinePlayer) as Transform;

            if (np == Main.player2 && Main.IsP2FirstPerson && !Main.P2IsDownedOrCarried)
                EnsureP2FirstPersonToolModel(tool, electricTool, ziplinePlayer);

            SyncOne(tool, toolPivot, rotation, np, forcedLayer);
            SyncOne(electricTool, electricPivot, rotation, np, forcedLayer);
        }

        internal static bool BeginRemoteVisual(Network_Player np, int forcedLayer, out RemoteVisualState state)
        {
            state = new RemoteVisualState();
            if (np == null || np.transform == null || np.ZiplinePlayer == null || !np.ZiplinePlayer.IsAttachedToZipline)
                return false;

            SyncToolTransforms(np.ZiplinePlayer, forcedLayer);

            Transform anchor = GetActiveToolPivot(np.ZiplinePlayer);
            if (anchor == null) return false;

            float bodyYaw = GetRenderedBodyYaw(np);
            Quaternion pathRotation;
            if (!TryGetZiplineRotation(np.ZiplinePlayer, out pathRotation)) return false;

            state.Active = true;
            state.Player = np;
            state.LocalPosition = np.transform.localPosition;
            state.LocalRotation = np.transform.localRotation;

            Vector3 rootPosition = np.transform.position;
            Vector3 currentOffset = anchor.position - rootPosition;
            Quaternion bodyYawRotation = Quaternion.AngleAxis(bodyYaw, Vector3.up);
            Vector3 offsetWithoutBodyYaw = Quaternion.Inverse(bodyYawRotation) * currentOffset;
            Vector3 targetAnchorPosition = rootPosition + pathRotation * offsetWithoutBodyYaw;
            np.transform.position += targetAnchorPosition - anchor.position;

            SyncToolTransforms(np.ZiplinePlayer, forcedLayer);
            return true;
        }

        internal static void EndRemoteVisual(ref RemoteVisualState state)
        {
            if (!state.Active || state.Player == null || state.Player.transform == null)
            {
                state.Active = false;
                return;
            }

            state.Player.transform.localPosition = state.LocalPosition;
            state.Player.transform.localRotation = state.LocalRotation;
            if (state.Player.ZiplinePlayer != null)
                SyncToolTransforms(state.Player.ZiplinePlayer);
            state.Active = false;
            state.Player = null;
        }

        static bool TryGetZiplineRotation(ZiplinePlayer ziplinePlayer, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            var path = _fPath?.GetValue(ziplinePlayer) as MeshPath_Zipline;
            if (path == null) return false;
            Vector3 dir = path.GetDirectionFromStartPointToEndPoint();
            if (dir.sqrMagnitude < 0.0001f) return false;
            rotation = Quaternion.LookRotation(dir, path.transform.up);
            return true;
        }

        static Transform GetActiveToolPivot(ZiplinePlayer ziplinePlayer)
        {
            EnsureFields();
            var tool = _fTool?.GetValue(ziplinePlayer) as GameObject;
            var electricTool = _fElectricTool?.GetValue(ziplinePlayer) as GameObject;
            var toolPivot = _fToolPivot?.GetValue(ziplinePlayer) as Transform;
            var electricPivot = _fElectricToolPivot?.GetValue(ziplinePlayer) as Transform;

            if (electricTool != null && electricTool.activeInHierarchy && electricPivot != null)
                return electricPivot;
            if (tool != null && tool.activeInHierarchy && toolPivot != null)
                return toolPivot;

            int modelIndex = 0;
            if (_fModelIdx != null)
            {
                try { modelIndex = (int)_fModelIdx.GetValue(ziplinePlayer); }
                catch { modelIndex = 0; }
            }
            return modelIndex == 1 && electricPivot != null ? electricPivot : toolPivot;
        }

        static float GetRenderedBodyYaw(Network_Player np)
        {
            if (np == Main.player2 && Main.IsP2FirstPerson)
                return P2CameraController.Yaw;

            if (np != null && np.currentModel != null)
                return np.currentModel.transform.eulerAngles.y;

            if (np != null && np.playerPivot != null)
                return np.playerPivot.eulerAngles.y;

            return np != null ? np.transform.eulerAngles.y : 0f;
        }

        static void EnsureP2FirstPersonToolModel(GameObject tool, GameObject electricTool, ZiplinePlayer ziplinePlayer)
        {
            if (ziplinePlayer != null && !ziplinePlayer.gameObject.activeSelf)
                ziplinePlayer.gameObject.SetActive(true);

            int modelIndex = 0;
            if (_fModelIdx != null)
            {
                try { modelIndex = (int)_fModelIdx.GetValue(ziplinePlayer); }
                catch { modelIndex = 0; }
            }

            var active = modelIndex == 1 ? electricTool : tool;
            var inactive = modelIndex == 1 ? tool : electricTool;
            if (active != null && !active.activeSelf) active.SetActive(true);
            if (inactive != null && inactive.activeSelf) inactive.SetActive(false);
        }

        static void SyncOne(GameObject tool, Transform pivot, Quaternion rotation, Network_Player np, int forcedLayer)
        {
            if (tool == null || pivot == null) return;

            pivot.rotation = rotation;

            int layer = forcedLayer >= 0 ? forcedLayer : DesiredToolLayer(np);
            if (layer >= 0) Main.SetLayerRecursively(tool.transform, layer);
        }

        static int DesiredToolLayer(Network_Player np)
        {
            if (np == Main.player2)
            {
                if (Main.IsP2FirstPerson && !Main.P2IsDownedOrCarried) return Main.LAYER_P2_HAND;
                int remote = LayerMask.NameToLayer("RemotePlayer");
                return remote >= 0 ? remote : Main.LAYER_P2_HAND;
            }

            if (np == Main.player1)
            {
                var tp = Main.player1ThirdPerson != null
                    ? Main.player1ThirdPerson
                    : Main.player1.GetComponentInChildren<ThirdPerson>();
                bool p1ThirdPerson = tp != null && tp.ThirdPersonState;
                return p1ThirdPerson ? Main.LAYER_P1_BODY : Main.p1HandCameraLayer;
            }

            return -1;
        }

        static void EnsureFields()
        {
            if (_fPath != null) return;
            var t = typeof(ZiplinePlayer);
            var bf = BindingFlags.Instance | BindingFlags.NonPublic;
            _fPath = t.GetField("ziplinePath", bf);
            _fTool = t.GetField("ziplineTool", bf);
            _fElectricTool = t.GetField("ziplineToolEletric", bf);
            _fToolPivot = t.GetField("ziplineToolPivot", bf);
            _fElectricToolPivot = t.GetField("ziplineElectricToolPivot", bf);
            _fModelIdx = t.GetField("currentZiplineModelIndex", bf);
        }
    }
}
