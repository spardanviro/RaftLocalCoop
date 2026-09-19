using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{

    // 雪橇车阶段2:P2 驾驶。阶段1(上/下车)见 Main.P2Snowmobile.cs + P2SnowmobileAdapter.cs。
    //
    // vanilla Snowmobile.Update 全程按 DrivingPlayer.IsLocalPlayer 分叉,两处决定"车动不动":
    //   1. body.isKinematic —— host 下 = (驾驶者非本地) → P2 驾驶时车被判远端、交给网络位置 lerp,
    //      而单机没有网络来源 → 车原地不动;
    //   2. HandleDrivingUpdate() —— 真正的推进/转向/动画,只在驾驶者是本地玩家时跑。
    // P2 是克隆(IsLocalPlayer=false)→ 两处都走远端分支 = 坐上去开不动。
    //
    // 修法沿用项目既有套路(同 Patch_ZiplinePlayer_Update_P2 / Patch_Paddle_OnPaddle_P2):
    // Prefix 在"P2 正是驾驶者"时开 P2FrameContext.Tool() 临时强制本地,让 vanilla 自己走 local 分支;
    // 驾驶物理一行不复刻,只把它读的 P1 输入用 Transpiler 换成 P2 手柄。
    internal static class P2SnowmobileDrive
    {
        // 仅在 Update 的 P2 scope 内为 true。scope 内 IsLocalPlayer 已被强制成 true,
        // 无法再靠它辨认身份,故用这个显式标志给 Transpiler 的替身方法判断。
        internal static bool IsP2Driving;

        // vanilla Update 顶部的"按 Interact 离座"块读的是 PlayerInput.GetPlayerByIndex(0)(=P1 输入),
        // 循环里却按 carriedPlayer.IsLocalPlayer 认人。scope 内 P2 也算本地 → P1 按 E 会把 P2 顶下车,
        // 且驾驶位(座位0)排在前面,循环 break 后连 P1 自己都下不来。
        // 这里让该块只认真正的本地玩家;P2 的离座另走 Main.TickP2Snowmobile(读 P2 手柄 Interact)。
        internal static bool IsVanillaLeaveOwner(Network_Player p)
        {
            return p != null && p != Main.player2 && p.IsLocalPlayer;
        }

        // 离座块读按键期间挂起【全部】P2 输入接管(Gamepad 覆盖 + Interact 路由)。见 VanillaLeavePressed。
        internal static bool SuppressP2InputRouting;

        // 同 IsVanillaLeaveOwner:离座块要按 vanilla 语义认 P1 的键鼠。
        // Prefix 开的 P2FrameContext.Tool() 会让 Main.p2UsingItemActive=true,进而由
        // Patch_CustomInputConfig_Gamepad 把 CustomInputConfig.Gamepad 强制成 true;而离座块在
        // Update 最顶部、同样被这个 scope 罩住 -> WasPressedThisFrame 走手柄分支、不看 P1 键鼠绑定,
        // P1 按 E 读不到,只能等 P2 下车(scope 不再开启)才出得来。
        // 这里在这一次读取期间挂起该覆盖,读完立刻还原;P2 自己的离座另走 Main.TickP2Snowmobile。
        internal static bool VanillaLeavePressed(CustomInputConfig cfg, InputAction action, string key)
        {
            if (cfg == null) return false;
            bool prev = SuppressP2InputRouting;
            SuppressP2InputRouting = true;
            bool r;
            try { r = cfg.WasPressedThisFrame(action, key); }
            finally { SuppressP2InputRouting = prev; }

            // 真的要离座了:vanilla 随后会在【本 scope 内】跑 LeaveSeat -> StopCarryingPlayer,
            // 其中一句 PlayerItemManager.IsBusy = ShouldItBeBusy() 是【静态量】,在 P2 上下文里
            // 求值会算成 P2 的忙碌状态(P2 还在车上)而卡死 -> P1 下车后换不了快捷栏/开不了背包/ESC。
            // 标记一下,等 Postfix 里 scope 关闭后按 P1 上下文重算。
            if (r) PendingLeaveFixup = true;
            return r;
        }

        internal static bool PendingLeaveFixup;
        static System.Reflection.MethodInfo _shouldBeBusy;

        // 在 P2 scope 之外(Postfix)按 vanilla 上下文重算 PlayerItemManager.IsBusy。
        internal static void FixupAfterLeave()
        {
            if (!PendingLeaveFixup) return;
            PendingLeaveFixup = false;
            if (_shouldBeBusy == null)
                _shouldBeBusy = AccessTools.Method(typeof(PlayerItemManager), "ShouldItBeBusy");
            if (_shouldBeBusy == null) return;
            try
            {
                PlayerItemManager.IsBusy = (bool)_shouldBeBusy.Invoke(null, null);
            }
            catch (Exception e) { if (Main.ModEntry != null && Main.ModEntry.Logger != null) Main.ModEntry.Logger.Log("[P2Snowmobile] IsBusy 重算失败: " + e.Message); }
        }


        // HandleDrivingUpdate 用 CustomInputConfig.Gamepad 选输入源:false 时走 MyInput.GetAxis(P1 键鼠)。
        // P1 用键鼠时它恒为 false → P2 驾驶会读到 P1 的 WASD。P2 驾驶时强制走 InputAction 分支,
        // 那条分支的 ReadValue 已被下面换成 P2 摇杆。
        internal static bool UseGamepadPath(bool vanilla)
        {
            return IsP2Driving || vanilla;
        }

        // 驾驶摇杆:P2 驾驶时读 P2 左摇杆原值。不套 P2HookAim.ReadMove——那个为滑索量化成 -1/0/1,
        // 雪橇车的油门/转向是模拟量,量化会变成一档到底。
        internal static Vector2 ReadDriveMove(InputAction a)
        {
            if (IsP2Driving && Main.p2ActionMove != null)
                return Main.p2ActionMove.ReadValue<Vector2>();
            return a != null ? a.ReadValue<Vector2>() : Vector2.zero;
        }
    }

    [HarmonyPatch(typeof(Snowmobile), "Update")]
    static class Patch_Snowmobile_Update_P2Drive
    {
        sealed class State { public P2FrameContext Ctx; }

        static void Prefix(Snowmobile __instance, ref State __state)
        {
            __state = null;
            if (Main.player2 == null || __instance == null) return;
            if (__instance.DrivingPlayer != Main.player2) return;

            __state = new State { Ctx = P2FrameContext.Tool() };
            P2SnowmobileDrive.IsP2Driving = true;
        }

        static void Postfix(Snowmobile __instance, State __state)
        {
            if (__state != null) SyncP2DriverSteerAnim(__instance);
            Cleanup(__state);
        }

        static System.Reflection.FieldInfo _smAnimator;

        // vanilla 把驾驶者的手臂转向做成"Snowmobile.Update 写 VelocityX -> 坐姿混合树摆手臂"。
        // 但 PlayerAnimator.Update 每帧【无条件】用自己的 animVelocity 覆盖 VelocityX,
        // vanilla 本地驾驶能生效纯粹是靠 Snowmobile 的 Update 排在 PlayerAnimator 之后。
        // P2 是运行时克隆,注册顺序反过来 -> 写进去的转向值当帧就被抹成 0,
        // 于是 P1 眼里 P2 开车时胳膊不跟握把动(FP/TP 都一样,因为世界模型是同一具)。
        // 把值写进 animVelocity/animTargetVelocity 本身,让 PlayerAnimator 自己的写回也是同一个值
        // = 与更新顺序无关,不必去调 ScriptExecutionOrder。
        static void SyncP2DriverSteerAnim(Snowmobile sm)
        {
            var p2 = Main.player2;
            if (sm == null || p2 == null || p2.Animator == null || p2.Animator.anim == null) return;
            if (_smAnimator == null) _smAnimator = AccessTools.Field(typeof(Snowmobile), "animator");
            var smAnim = _smAnimator != null ? _smAnimator.GetValue(sm) as Animator : null;
            if (smAnim == null) return;
            float steer = smAnim.GetFloat("Steer");
            p2.Animator.animVelocity.x = steer;
            p2.Animator.animTargetVelocity.x = steer;
            p2.Animator.anim.SetFloat("VelocityX", steer);
        }

        static Exception Finalizer(Exception __exception, State __state) { Cleanup(__state); return __exception; }

        static void Cleanup(State st)
        {
            if (st == null) return;
            P2SnowmobileDrive.IsP2Driving = false;
            st.Ctx?.Dispose();
            st.Ctx = null;
            P2SnowmobileDrive.FixupAfterLeave();   // 必须在 Dispose 之后:要的就是 vanilla 上下文
        }

        // 把离座块那次 IsLocalPlayer(方法体内第一次出现)换成 IsVanillaLeaveOwner。
        // 后面几次(isKinematic / HandleDrivingUpdate 分叉 / 音效)要保持原样——正是靠它们
        // 在 scope 内认出 P2 是本地驾驶者。
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getLocal = AccessTools.PropertyGetter(typeof(Network_Player), "IsLocalPlayer");
            var repl = AccessTools.Method(typeof(P2SnowmobileDrive), "IsVanillaLeaveOwner");
            var wasPressed = AccessTools.Method(typeof(CustomInputConfig), "WasPressedThisFrame",
                                                new[] { typeof(InputAction), typeof(string) });
            var pressRepl = AccessTools.Method(typeof(P2SnowmobileDrive), "VanillaLeavePressed");
            bool replaced = false;
            bool pressReplaced = false;
            foreach (var ins in instructions)
            {
                if (!replaced && getLocal != null && repl != null && ins.Calls(getLocal))
                {
                    replaced = true;
                    yield return new CodeInstruction(OpCodes.Call, repl);
                }
                // 方法体内第一次 WasPressedThisFrame 就是离座块那次(其余读输入的在 HandleDrivingUpdate 等别的方法里)
                else if (!pressReplaced && wasPressed != null && pressRepl != null && ins.Calls(wasPressed))
                {
                    pressReplaced = true;
                    yield return new CodeInstruction(OpCodes.Call, pressRepl);
                }
                else yield return ins;
            }
        }
    }

    [HarmonyPatch(typeof(Snowmobile), "HandleDrivingUpdate")]
    static class Patch_Snowmobile_HandleDrivingUpdate_P2Input
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getGamepad = AccessTools.PropertyGetter(typeof(CustomInputConfig), "Gamepad");
            var gamepadRepl = AccessTools.Method(typeof(P2SnowmobileDrive), "UseGamepadPath");
            var readVec = AccessTools.Method(typeof(InputAction), "ReadValue", Type.EmptyTypes, new[] { typeof(Vector2) });
            var moveRepl = AccessTools.Method(typeof(P2SnowmobileDrive), "ReadDriveMove");

            foreach (var ins in instructions)
            {
                if (getGamepad != null && gamepadRepl != null && ins.Calls(getGamepad))
                {
                    yield return ins;                                          // 原值留在栈上
                    yield return new CodeInstruction(OpCodes.Call, gamepadRepl); // P2 驾驶时改判 true
                }
                else if (readVec != null && moveRepl != null && ins.Calls(readVec))
                {
                    yield return new CodeInstruction(OpCodes.Call, moveRepl);   // 换 P2 左摇杆
                }
                else yield return ins;
            }
        }
    }

    // P1 用不了雪橇车(能打着火、人却坐不上,且 TryTakingSeat 里 NRE 刷屏)。
    // 根因与 Patch_Sail_OnIsRayed_P2Safe 记录的船帆 #1 完全同型:
    // vanilla Snowmobile.Start 把 localPlayer 缓存成 ComponentManager<Network_Player>.Value,
    // 而分屏给这个 getter 挂了 P2 路由([CM-Get]) —— 雪橇车 Start 的时机若落在 P2 上下文里,
    // 就把 localPlayer 钉死成 P2 克隆。之后 P1 瞄准上车,OnIsRayed 拿这个字段去 TryTakingSeat,
    // 于是"打火"(seatIndex==0 先 Play 引擎音)发生了,紧接着在 P2 克隆的空子组件上 NRE,人自然没坐上。
    // 这里把 P1 路径的 localPlayer 纠正回 player1 = 消除 mod 污染、还原 vanilla 语义
    // (vanilla 里这个字段本就等于本地玩家)。P2 上车另走 InteractionRouter.HandleP2Snowmobile。
    [HarmonyPatch(typeof(Snowmobile))]
    static class Patch_Snowmobile_OnIsRayed_P1Fix
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            // 显式接口实现 void IRaycastable.OnIsRayed(),反射名带接口前缀
            return AccessTools.Method(typeof(Snowmobile), "IRaycastable.OnIsRayed")
                ?? AccessTools.Method(typeof(Snowmobile), "OnIsRayed");
        }

        static readonly System.Reflection.FieldInfo LocalPlayerField =
            AccessTools.Field(typeof(Snowmobile), "localPlayer");

        static bool Prefix(Snowmobile __instance)
        {
            // P2 设备射线期间整段跳过 vanilla(同船帆 #2):这条 vanilla 路径的按键判定读的是
            // PlayerInput.GetPlayerByIndex(0)=P1 输入,P2 本就用不了,跑进去只会拿 P2 克隆碰空引用。
            if (Main.isProcessingP2Ray) return false;
            if (__instance == null || LocalPlayerField == null) return true;

            var lp = LocalPlayerField.GetValue(__instance) as Network_Player;
            if (Main.player1 != null && lp != Main.player1)
                LocalPlayerField.SetValue(__instance, Main.player1);
            return true;
        }
    }

    // 有意偏离原版:vanilla 这段把"非本地玩家"当成网络插值的幽灵,主动关掉车与其的碰撞
    // (OnTriggerBoxEnter -> Physics.IgnoreCollision)。P2 克隆体 IsLocalPlayer=false,于是被误判成
    // 远程玩家 -> 车与 P2 永久不碰撞、人直接从车里穿过去。P2 在本机是真实存在的物理玩家,
    // 应当走 vanilla 给本地玩家的那条分支(什么都不做)—— 与 P1 完全对称。
    // 顺带根治:vanilla 内层会遍历 component.currentModel.entityColliders,而 P2 克隆自本地 P1,
    // 那些 collider 的 GameObject 已被 vanilla Destroy(Network_Player.cs:788)-> 数组元素全是空引用
    // -> 原先在这里 NRE 刷屏。P2 整体不进这段后,当初的空安全复刻与 0.5s 节流字典一并作废。
    [HarmonyPatch(typeof(Snowmobile), "OnTriggerBoxEnter")]
    static class Patch_Snowmobile_OnTriggerBoxEnter_P2Safe
    {
        static bool Prefix(Collider other)
        {
            if (other == null) return true;
            var np = other.GetComponent<Network_Player>();
            if (np != null && np == Main.player2) return false;   // 同 vanilla 对本地玩家:早退
            return true;
        }
    }
}
