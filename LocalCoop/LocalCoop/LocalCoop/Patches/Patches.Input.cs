using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch 5: PersonController.Start — 尝试重绑 P2 移动输入
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(PersonController), "Start")]
    static class Patch_PersonController_Start
    {
        static readonly FieldInfo actionMoveField =
            typeof(PersonController).GetField("actionMove", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Postfix(PersonController __instance)
        {
            if (Main.player2 == null || Main.p2ActionMove == null) return;

            var np = __instance.GetComponent<Network_Player>();
            if (np != Main.player2) return;

            actionMoveField?.SetValue(__instance, Main.p2ActionMove);
            Main.LogV("[Patch5] PersonController P2: actionMove -> gamepad LS");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 9: PersonController.Update — P2 走本地玩家输入分支
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(PersonController), "Update")]
    static class Patch_PersonController_Update
    {
        static readonly FieldInfo actionMoveField =
            typeof(PersonController).GetField("actionMove", BindingFlags.Instance | BindingFlags.NonPublic);

        // isLocalPlayer 强制 + ActiveMenu 中和 + PlayerContext/P2Mode 现统一由 P2OriginalScope.Movement() 处理
        //  (经 __state 存到 Postfix/Finalizer 释放),不再手写 _p2LocalForced/_p2SavedMenu/_p2MenuCleared。

        // P2 菜单打开时把移动输入换成这个【永不启用】的动作 → ReadValue 恒为 0 → P2 站住不动
        //  (左摇杆此时用于鱼饵菜单等的导航)。菜单关闭再换回 p2ActionMove。
        static readonly InputAction _p2NullMove =
            new InputAction("P2NullMove", InputActionType.Value, expectedControlType: "Vector2");

        static bool Prefix(PersonController __instance, ref P2OriginalScope __state)
        {
            __state = null;
            if (Main.player2 == null) return true;
            var np = __instance.GetComponent<Network_Player>();
            if (np != Main.player2) return true;

            // 菜单打开 → 用空动作冻结移动；否则用 P2 左摇杆。
            var wantMove = Main.IsP2MenuOpen ? _p2NullMove : Main.p2ActionMove;
            if (wantMove != null && actionMoveField != null)
            {
                var cur = actionMoveField.GetValue(__instance) as InputAction;
                if (cur != wantMove)
                {
                    actionMoveField.SetValue(__instance, wantMove);
                    if (wantMove == Main.p2ActionMove)
                        Main.LogV("[Patch9] P2 PersonController: actionMove late-bound to gamepad LS");
                }
            }

            // ── 统一代理层：P2OriginalScope.Movement() ──────────────────────────
            //  scope 内:强制 isLocalPlayer=true(绕 Mono 对 IsLocalPlayer getter 的内联,让 P2 走本地移动分支,
            //  否则完全无法移动/动画不播/脚陷木筏)、中和全局 ActiveMenu(P1 开背包置 Inventory 会冻结 P2 的
            //  `ActiveMenu==None` 奔跑门控)、PlayerContext.Active=P2 + P2Mode=Movement(与 ThirdPerson.Update 同款)。
            //  Dispose 由 Postfix 正常释放、Finalizer 异常兜底(自存旧值,支持嵌套)。
            __state = P2OriginalScope.Movement();

            Main.p2PersonControllerActive = true;   // 显式标志(P2.IsInPersonController):驱动 IsLocalPlayer 补丁与输入门控
            return true;
        }

        // ---- 下蹲:让 vanilla 自己把 playerPivot 降到位(相机/胳膊/帽子同属 pivot 子树,一起降) ----
        // vanilla PersonController.Update(SourceCode/PersonController.cs:515-527)按
        // thirdPersonSettings.ThirdPersonModel 选下降量:FP 降 crouchHeightCamera(0.5),TP 只降 0.05。
        // mod 强制两个玩家的世界模型恒为 TP(FP 是纯视觉伪装,从不改 ThirdPersonModel),
        // 于是 P2 在 FP 时也被判成 TP -> pivot 只降 0.05,而相机另有 mod 自算的偏移单独降到位
        // -> 胳膊和帽子(挂在 FP rig 上、随 pivot 走)掉队。
        // 这里只改写 Update 自身 IL 里的那一处 getter 调用,让 P2 在 FP 时读到 false;
        // HandleRotationOfPlayer 等其它读取点在别的方法里,完全不受影响。
        // (改写调用方而非给 getter 挂 Postfix -> 不受 Mono 内联影响,见 memory project_raft_mono_inlining)
        static bool CrouchUsesThirdPersonModel(ThirdPerson tp)
        {
            bool vanilla = tp != null && tp.ThirdPersonModel;
            if (!vanilla || Main.player2 == null) return vanilla;
            var m2 = Main.player2.currentModel;
            if (m2 == null || tp != m2.thirdPersonSettings) return vanilla;   // 不是 P2 -> 原值
            return !P2CameraController.FirstPerson;                          // P2 在 FP -> 走 vanilla 的 FP 分支
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => TranspilerGuard.Verify(instructions, TranspilerCore);

        static IEnumerable<CodeInstruction> TranspilerCore(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.PropertyGetter(typeof(ThirdPerson), "ThirdPersonModel");
            var repl = AccessTools.Method(typeof(Patch_PersonController_Update), "CrouchUsesThirdPersonModel");
            foreach (var ins in instructions)
            {
                if (target != null && repl != null && ins.Calls(target))
                    yield return new CodeInstruction(OpCodes.Call, repl);
                else
                    yield return ins;
            }
        }

        // vanilla 降 pivot 是纯粹为了压低第一人称视点;身体下蹲的视觉由动画(Crouching)负责。
        // 但 mod 让两个玩家的世界模型都常驻可见(对方视角要看到),pivot 一降身体就跟着沉下去,
        // 故把世界模型按同样幅度抬回来抵消。P1 早有此处理,P2 现在与之对称。
        sealed class CrouchComp { internal float StandPivotY; internal float StandModelY; internal bool Cached; }
        static readonly CrouchComp _p1Comp = new CrouchComp();
        static readonly CrouchComp _p2Comp = new CrouchComp();

        static void TickCrouchCompensation(Network_Player np, PersonController pc, CrouchComp s)
        {
            if (np == null || pc == null || np.playerPivot == null || np.currentModel == null) return;
            var model = np.currentModel.transform;
            if (!s.Cached && !pc.crouching)
            {
                s.StandPivotY = np.playerPivot.localPosition.y;
                s.StandModelY = model.localPosition.y;
                s.Cached = true;
            }
            if (!s.Cached) return;

            float drop = s.StandPivotY - np.playerPivot.localPosition.y;
            if (drop < 0f) drop = 0f;
            var lp = model.localPosition;
            lp.y = s.StandModelY + drop;
            model.localPosition = lp;
            // 站直且已归位时重新校准基准(pivot 可能被别的系统改过)
            if (!pc.crouching && drop < 0.001f)
                s.StandPivotY = np.playerPivot.localPosition.y;
        }

        static void Postfix(PersonController __instance, P2OriginalScope __state)
        {
            var p1 = Main.player1;
            if (p1 != null && __instance == p1.PersonController) TickCrouchCompensation(p1, __instance, _p1Comp);
            var p2 = Main.player2;
            if (p2 != null && __instance == p2.PersonController) TickCrouchCompensation(p2, __instance, _p2Comp);

            if (!Main.p2PersonControllerActive) { __state?.Dispose(); return; }   // 非 P2(含 P1):__state 为 null

            __instance.SetNetworkPosition(__instance.transform.position);
            SplitScreenDeathFlow.TickP2RespawnRestore();
            Main.p2PersonControllerActive = false;
            __state?.Dispose();   // 还原 isLocalPlayer=false / ActiveMenu / PlayerContext / P2Mode
        }

        // Finalizer：即使 Update 或 Postfix 抛出异常，也保证 p2PersonControllerActive 复位、
        // scope 被释放(否则后续帧所有 IsLocalPlayer / Gamepad / IsPressed 永久走 P2 分支，
        // 或 P2 永久停留在 isLocalPlayer=true / P2Mode=Movement)。Dispose 幂等,与 Postfix 双路径安全。
        static Exception Finalizer(Exception __exception, P2OriginalScope __state)
        {
            Main.p2PersonControllerActive = false;
            __state?.Dispose();
            return __exception; // 原样抛出，不吞掉
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 10: Network_Player.IsLocalPlayer — P2 在 PersonController 期间视为本地
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Network_Player), "get_IsLocalPlayer")]
    static class Patch_Network_Player_IsLocalPlayer
    {
        static void Postfix(Network_Player __instance, ref bool __result)
        {
            if (Main.p2PersonControllerActive && __instance == Main.player2)
                __result = true;
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 11: CustomInputConfig.Gamepad — P2 PersonController 期间返回 true
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CustomInputConfig), "get_Gamepad")]
    static class Patch_CustomInputConfig_Gamepad
    {
        static void Postfix(ref bool __result)
        {
            // 雪橇车离座块读 P1 键鼠期间挂起本覆盖(见 P2SnowmobileDrive.VanillaLeavePressed)
            if (P2SnowmobileDrive.SuppressP2InputRouting) return;
            if (Main.p2PersonControllerActive || Main.p2UsingItemActive)
                __result = true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 12: CustomInputConfig.IsPressed — 路由到 P2 专属 action
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CustomInputConfig), "IsPressed", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CustomInputConfig_IsPressed
    {
        static bool Prefix(string key, ref bool __result)
        {
            // 挂起 P2 输入路由:某些 vanilla 代码块虽然跑在 P2 scope 内,读的却是【P1】的按键
            // (如 Snowmobile.Update 顶部的离座块)。那种地方须按 vanilla 语义读,不能被路由到 P2。
            // 见 P2SnowmobileDrive.VanillaLeavePressed。
            if (P2SnowmobileDrive.SuppressP2InputRouting) return true;

            if (Main.P2IsDownedOrCarried && (Main._p2FillWaterActive || Main.IsP2OriginalInputActive || Main.isProcessingP2Ray || Main.p2PersonControllerActive))
            {
                __result = false;
                return false;
            }
            
            
            if (Main.IsP2OriginalInputActive &&
                (Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen || Main.IsP2MenuOpen) && key == "RMB")
            {
                __result = false;
                return false;
            }
            if ((Main._p2FillWaterActive || Main.IsP2OriginalInputActive) && key == "RMB")
            {
                __result = Main.p2ActionContext?.IsPressed() ?? false;
                return false;
            }
            if (Main.IsP2OriginalInputActive && key == "Interact")
            {
                __result = Main.p2ActionInteract?.IsPressed() ?? false;
                return false;
            }
            if (!Main.p2PersonControllerActive) return true;
            
            if (Main.ShouldBlockP2CrouchInput && (key == "Crouch" || key == "LeftControl"))
            { __result = false; return false; }
            if (Main.IsP2MenuInputLocked && (key == "Jump" || key == "Sprint" || key == "Crouch" || key == "LeftControl"))
            { __result = false; return false; }
            switch (key)
            {
                case "Jump":
                    __result = Main.p2ActionJump?.IsPressed() ?? false;
                    return false;
                case "Sprint":
                    __result = Main.p2ActionSprint?.IsPressed() ?? false;
                    return false;
                case "Crouch":
                case "LeftControl":
                    __result = Main.p2ActionCrouch?.IsPressed() ?? false;
                    return false;
                default:
                    __result = MyInput.GetButton(key);
                    return false;
            }
        }
    }

    // P2 原版上下文/装水：松开 "RMB" → P2 LT 松开(原版很多工具靠 WasReleased 复位状态)。
    [HarmonyPatch(typeof(CustomInputConfig), "WasReleasedThisFrame", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CustomInputConfig_WasReleased_P2Fill
    {
        static bool Prefix(string key, ref bool __result)
        {
            if (Main.P2IsDownedOrCarried && (Main._p2FillWaterActive || Main.IsP2OriginalInputActive))
            {
                __result = false;
                return false;
            }

            if (Main.IsP2OriginalInputActive &&
                (Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen || Main.IsP2MenuOpen) && key == "RMB")
            {
                __result = false;
                return false;
            }
            if (!(Main._p2FillWaterActive || Main.IsP2OriginalInputActive) || key != "RMB") return true;
            __result = Main.p2ActionContext?.WasReleasedThisFrame() ?? false;
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 13: CustomInputConfig.WasPressedThisFrame — 同上 + P2 射线路由
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CustomInputConfig), "WasPressedThisFrame", new Type[] { typeof(InputAction), typeof(string) })]
    static class Patch_CustomInputConfig_WasPressedThisFrame
    {
        static bool Prefix(string key, ref bool __result)
        {
            // 挂起 P2 输入路由:某些 vanilla 代码块虽然跑在 P2 scope 内,读的却是【P1】的按键
            // (如 Snowmobile.Update 顶部的离座块)。那种地方须按 vanilla 语义读,不能被路由到 P2。
            // 见 P2SnowmobileDrive.VanillaLeavePressed。
            if (P2SnowmobileDrive.SuppressP2InputRouting) return true;

            if (Main.P2IsDownedOrCarried && (Main._p2FillWaterActive || Main.IsP2OriginalInputActive || Main.isProcessingP2Ray || Main.p2PersonControllerActive))
            {
                __result = false;
                return false;
            }

            if (Main.P2ConsumedInteractThisFrame && key == "Interact" &&
                (Main._p2FillWaterActive || Main.IsP2OriginalInputActive || Main.isProcessingP2Ray))
            {
                __result = false;
                return false;
            }
            
            
            if (Main.isProcessingP2Ray && key == "Interact")
            {
                __result = Main.p2ActionInteract?.WasPressedThisFrame() ?? false;
                return false;
            }
            // P2 装水：fill = "Interact" → P2 X
            if (Main._p2FillWaterActive && key == "Interact")
            {
                __result = Main.p2ActionInteract?.WasPressedThisFrame() ?? false;
                return false;
            }
            
            if (Main.IsP2OriginalInputActive &&
                (Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen || Main.IsP2MenuOpen) && key == "RMB")
            {
                __result = false;
                return false;
            }
            if ((Main._p2FillWaterActive || Main.IsP2OriginalInputActive) && key == "RMB")
            {
                __result = Main.p2ActionContext?.WasPressedThisFrame() ?? false;
                return false;
            }
            if (Main.IsP2OriginalInputActive && key == "Interact")
            {
                __result = Main.p2ActionInteract?.WasPressedThisFrame() ?? false;
                return false;
            }

            if (!Main.p2PersonControllerActive) return true;
            
            if (Main.ShouldBlockP2CrouchInput && (key == "Crouch" || key == "LeftControl"))
            { __result = false; return false; }
            if (Main.IsP2MenuInputLocked && (key == "Jump" || key == "Sprint" || key == "Crouch" || key == "LeftControl"))
            { __result = false; return false; }
            switch (key)
            {
                case "Jump":
                    __result = Main.p2ActionJump?.WasPressedThisFrame() ?? false;
                    return false;
                case "Sprint":
                    __result = Main.p2ActionSprint?.WasPressedThisFrame() ?? false;
                    return false;
                case "Crouch":
                    __result = Main.p2ActionCrouch?.WasPressedThisFrame() ?? false;
                    return false;
                default:
                    __result = MyInput.GetButtonDown(key);
                    return false;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch 20: UseItemController.Update — 让 P2 能使用手持工具/攻击（RT 扳机）
    //
    //  原版门控 `!playerNetwork.IsLocalPlayer` 使 P2(false) 直接 return；且 fireAction
    //  绑的是 P1 的 "Fire" action。与 P2 移动同款：
    //   Prefix 把 P2 的 isLocalPlayer 临时写 true（绕 Mono 内联）+ 设 p2UsingItemActive
    //          + 把 fireAction 字段替换为 P2 的 ActionFire（手柄 RT）。
    //   随后原版 IsPressed/WasPressedThisFrame(fireAction, useButtonName) 在
    //   Gamepad(被 patch 成 true) 分支读 P2 扳机 → 触发 Use()。
    //   Postfix/Finalizer 恢复 isLocalPlayer=false 与标志，使本帧其余阶段仍按远程处理。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(UseItemController), "Update")]
    static class Patch_UseItemController_Update
    {
        static readonly FieldInfo playerNetworkField =
            typeof(UseItemController).GetField("playerNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fireActionField =
            typeof(UseItemController).GetField("fireAction", BindingFlags.Instance | BindingFlags.NonPublic);

        // 改用统一的 P2OriginalScope.Tool()：scope 内设 PlayerContext.Active=P2 + isLocalPlayer=true + P2Mode=Tool，
        //  Dispose 自动恢复(支持嵌套，不用静态 _p2LocalForced)。fireAction 仍替换为 P2 扳机(RT)。
        static bool Prefix(UseItemController __instance, ref P2OriginalScope __state)
        {
            if (Main.player2 == null || Main.p2ActionFire == null) return true;
            var np = playerNetworkField?.GetValue(__instance) as Network_Player;
            if (np == null || np != Main.player2) return true;

            if (Main.P2IsDownedOrCarried)
            {
                try { Main.player2.PlayerItemManager?.SelectUsable(null); }
                catch (Exception e) { Main.LogV("[P2Input] downed SelectUsable(null) ignored: " + e.Message); }
                try { Main.player2.PlayerItemManager?.HideItemInHand(); }
                catch (Exception e) { Main.LogV("[P2Input] downed HideItemInHand ignored: " + e.Message); }
                try { __instance.Deselect(); }
                catch (Exception e) { Main.LogV("[P2Input] downed Deselect ignored: " + e.Message); }
                return false;
            }

            // P2 菜单(背包/研究台/建造菜单/设备菜单)打开时不使能 P2 工具 —— 否则 RT(如研究台的 RT 研究)
            //  会同时触发手持工具的使用。不设 Tool scope → 原版 Update 对 P2(isLocalPlayer=false) 早退,工具不动作。
            if (Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen || Main.IsP2MenuOpen) return true;

            var cur = fireActionField?.GetValue(__instance) as InputAction;
            if (cur != Main.p2ActionFire)
                fireActionField?.SetValue(__instance, Main.p2ActionFire);

            __state = P2OriginalScope.Tool();
            return true;
        }

        static void Postfix(P2OriginalScope __state) => __state?.Dispose();
        static Exception Finalizer(Exception __exception, P2OriginalScope __state) { __state?.Dispose(); return __exception; }
    }
}
