using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    [HarmonyPatch(typeof(CharacterModelModifications), "Start")]
    static class Patch_CharacterModelStart
    {
        static void Postfix(CharacterModelModifications __instance)
        {
            if (Main.player1 == null || Main.player2 == null) return;

            var np = __instance.GetComponentInParent<Network_Player>();
            if (np != Main.player1 && np != Main.player2) return;

            Main.LogV($"[Patch4] {np.name}.currentModel.Start -> reconfigure layers");

            if (np == Main.player2)
            {
                var anim = __instance.GetComponentInParent<Animator>();
                if (anim != null && __instance.thirdPersonController != null)
                {
                    anim.runtimeAnimatorController = __instance.thirdPersonController;
                    anim.applyRootMotion = false;
                    Main.LogV("[Patch4] P2: thirdPersonController forced, rootMotion=off");
                }

                __instance.ActivateHairStyle(HairStyle.Full, useAsCurrentHairStyle: true);
                Main.ApplyEquippedHeadVisuals(Main.player2);
                Main.LogV("[Patch4] P2: ActivateHairStyle(Full) called");
            }

            Main.ConfigureRendering();
            Main.SetCameraRects();
            if (np == Main.player2)
                Main.TickP2CameraBootstrap();
        }
    }

    
    
    
    [HarmonyPatch(typeof(ThirdPerson), "Start")]
    static class Patch_ThirdPerson_Start
    {
        static readonly FieldInfo actionMoveField =
            typeof(ThirdPerson).GetField("actionMove",   BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo actionLookField =
            typeof(ThirdPerson).GetField("actionLook",   BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo actionRotateField =
            typeof(ThirdPerson).GetField("actionRotate", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo camTransformInitField =
            typeof(ThirdPerson).GetField("cameraTransform",       BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo currentModelInitField =
            typeof(ThirdPerson).GetField("currentModel",          BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo canvasInitField =
            typeof(ThirdPerson).GetField("canvas",                BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo camRotTransInitField =
            typeof(ThirdPerson).GetField("cameraRotateTransform", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Postfix(ThirdPerson __instance)
        {
            if (Main.player2 == null || Main.p2ActionMove == null) return;

            var np = __instance.GetComponentInParent<Network_Player>();
            if (np != Main.player2) return;

            actionMoveField?.SetValue(__instance,   Main.p2ActionMove);
            actionLookField?.SetValue(__instance,   Main.p2ActionLook);
            actionRotateField?.SetValue(__instance, Main.p2ActionRotate);
            Main.LogV("[Patch6] ThirdPerson P2: inputs bound to gamepad");

            if (camTransformInitField?.GetValue(__instance) == null && Main.player2.Camera != null)
            {
                camTransformInitField?.SetValue(__instance, Main.player2.Camera.transform);
                Main.LogV("[Patch6] ThirdPerson P2: cameraTransform <- P2.Camera.transform");
            }

            if (camRotTransInitField?.GetValue(__instance) == null)
            {
                var rotator = new GameObject("P2CamRotator").transform;
                rotator.SetParent(np.transform);
                rotator.localPosition = Vector3.zero;
                camRotTransInitField?.SetValue(__instance, rotator);
                Main.LogV("[Patch6] ThirdPerson P2: cameraRotateTransform <- new P2CamRotator");
            }

            if (currentModelInitField?.GetValue(__instance) == null && Main.player2.currentModel != null)
                currentModelInitField?.SetValue(__instance, Main.player2.currentModel);

            if (canvasInitField?.GetValue(__instance) == null)
                canvasInitField?.SetValue(__instance, ComponentManager<CanvasHelper>.Value);

            __instance.ForceThirdPersonState(true);
            Main.LogV("[Patch6] ThirdPerson P2: ForceThirdPersonState(true)");
        }
    }

    
    
    
    [HarmonyPatch(typeof(ThirdPerson), "SetThirdPersonState")]
    static class Patch_ThirdPerson_SetThirdPersonState
    {
        static readonly FieldInfo camTransformField =
            typeof(ThirdPerson).GetField("cameraTransform", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo camRotateTransformField =
            typeof(ThirdPerson).GetField("cameraRotateTransform", BindingFlags.Instance | BindingFlags.NonPublic);

        static bool Prefix(ThirdPerson __instance, bool thirdPersonState)
        {
            if (Main.player2 == null) return true;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == Main.player1 && np.PlayerScript != null && np.PlayerScript.IsDead)
                return !thirdPersonState;
            if (np == Main.player2 && !thirdPersonState)
            {
                Main.LogV("[Patch8] Blocking P2 FP transition");
                return false;
            }
            return true;
        }

        static void Postfix(ThirdPerson __instance, bool thirdPersonState)
        {
            if (Main.player1 == null || Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == Main.player1 && !thirdPersonState && np.ZiplinePlayer != null && np.ZiplinePlayer.IsAttachedToZipline)
            {
                Patch_ZiplinePlayer_Detach_ViewState.AlignP1ZiplineFpMouseLookTargets(np);
            }
            if (np == Main.player1 && Main.IsPlayerInFreeLookSeat(np))
            {
                var attach = Main.FindFreeLookAttachForPlayer(np);
                Patch_AttachPlayer_Update_P1SeatPose.ForceFreeLookSeatVisibleBodyParent(attach, np);
                // 只有 TP 才关鼠标视角(TP 由 ThirdPerson.HandleThirdPerson 接管输入)。
                // 进 FP 时保持开启:这类座位原版 disableMouseLook=false,本就允许相对座位自由环视。
                // 原先无条件关闭,导致 TP 坐下再按 V 切 FP 后视角永久锁死。
                if (np.PlayerScript != null)
                    np.PlayerScript.SetMouseLookScripts(
                        !thirdPersonState && attach != null && !attach.disableMouseLook);
            }
            if (np != Main.player1) return;

            
            
            
        }
    }

    
    
    
    [HarmonyPatch(typeof(ThirdPerson), "HandleThirdPerson")]
    static class Patch_ThirdPerson_HandleThirdPerson
    {
        static bool Prefix(ThirdPerson __instance)
        {
            if (Main.player2 == null) return true;

            var np = __instance.GetComponentInParent<Network_Player>();
            if (np != Main.player2) return true;
            if (np.PlayerScript != null && np.PlayerScript.IsDead)
                return false;

            // 雪橇车排除在"锁座姿"之外:vanilla 的 TP 相机对附着状态并无特殊处理
            // (只跳过 playerPivot 偏航驱动),照跑轨道相机就是原版行为。
            // 早退会让 P2 在车上完全没有 TP 相机 —— 与滑索当初同一个坑。
            if ((Main.P2IsSeated && !Main.P2IsInFreeLookSeat) ||
                (np.PlayerNetworkManager.IsAttached && !P2ZiplineDriver.IsAttached &&
                 !Main.P2IsInFreeLookSeat && !Main.P2InSnowmobile && !Main.IsP2PianoActive))
            {
                Main.LockP2SeatPose();
                return false;
            }

            Main.EnsureP2CameraRig();
            return P2ThirdPersonCameraController.Handle(__instance, np);
        }

        // TP 下蹲注视点用平滑值,替换 vanilla 的二值跳变。见 CrouchCameraOffset 的说明。
        static float GetSmoothCrouchOffset(ThirdPerson tp)
        {
            if (tp == null) return 0f;
            Network_Player np = null;
            if (Main.player1 != null && Main.player1.currentModel != null &&
                Main.player1.currentModel.thirdPersonSettings == tp) np = Main.player1;
            else if (Main.player2 != null && Main.player2.currentModel != null &&
                     Main.player2.currentModel.thirdPersonSettings == tp) np = Main.player2;
            else np = tp.GetComponentInParent<Network_Player>();
            return CrouchCameraOffset.Get(np, tp.crouchHeightOffset);
        }

        // vanilla(ThirdPerson.cs:281-284):
        //     if (playerNetwork.PersonController.crouching)
        //         vector5 -= playerNetwork.transform.up * crouchHeightOffset;
        // 把 crouching 恒判为 true、把 crouchHeightOffset 换成平滑值(站直时=0),
        // 于是原版那行减法照跑,只是减的量连续变化 —— 不新增逻辑,只把阶跃改成连续。
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var fCrouching = AccessTools.Field(typeof(PersonController), "crouching");
            var fOffset = AccessTools.Field(typeof(ThirdPerson), "crouchHeightOffset");
            var mGet = AccessTools.Method(typeof(Patch_ThirdPerson_HandleThirdPerson), "GetSmoothCrouchOffset");
            foreach (var ins in instructions)
            {
                if (fCrouching != null && ins.LoadsField(fCrouching))
                {
                    ins.opcode = OpCodes.Pop;   // 弹掉 PersonController 引用(保留原指令上的标签)
                    ins.operand = null;
                    yield return ins;
                    yield return new CodeInstruction(OpCodes.Ldc_I4_1);
                }
                else if (fOffset != null && mGet != null && ins.LoadsField(fOffset))
                {
                    ins.opcode = OpCodes.Call;  // 栈上已是 this(ThirdPerson)
                    ins.operand = mGet;
                    yield return ins;
                }
                else yield return ins;
            }
        }
    }

    // 有意偏离原版(用户明确要求):TP 下蹲镜头平滑下移。
    // vanilla 是二值跳变 —— crouching 一变,注视点立刻跳 crouchHeightOffset(0.5),
    // 而相机位置走 Lerp,两者步调不一致 -> 观感是"边下移边上仰",很别扭。
    // 这里按玩家维护一个平滑量,P1 由 Patch_ThirdPerson_HandleThirdPerson 的 Transpiler 用,
    // P2 由 P2ThirdPersonCameraController 用同一个助手 -> 两人观感一致。
    // 按帧号做幂等:同一帧被取多次(P2 滑索还会在移动后重定位一次)只推进一次平滑。
    static class CrouchCameraOffset
    {
        const float SmoothTime = 0.12f;

        sealed class State { internal float Cur; internal float Vel; internal int Frame = -1; }
        static readonly Dictionary<int, State> _states = new Dictionary<int, State>();

        internal static float Get(Network_Player np, float crouchHeightOffset)
        {
            if (np == null || np.PersonController == null) return 0f;
            int key = np.GetInstanceID();
            State s;
            if (!_states.TryGetValue(key, out s)) { s = new State(); _states[key] = s; }
            if (s.Frame == Time.frameCount) return s.Cur;   // 同帧幂等
            s.Frame = Time.frameCount;

            float target = np.PersonController.crouching ? crouchHeightOffset : 0f;
            s.Cur = Mathf.SmoothDamp(s.Cur, target, ref s.Vel, SmoothTime);
            if (Mathf.Abs(s.Cur - target) < 0.001f) { s.Cur = target; s.Vel = 0f; }
            return s.Cur;
        }
    }

    [HarmonyPatch(typeof(PlayerAnimator), "SetAnimation", new[] { typeof(PlayerAnimation), typeof(bool) })]
    static class Patch_PlayerAnimator_SetAnimation_TwoArgs_FpVisual
    {
        static void Postfix(PlayerAnimator __instance, PlayerAnimation animation, bool triggering)
        {
            FirstPersonVisualRig.ForwardAnimation(__instance, animation, false, triggering);
        }
    }

    [HarmonyPatch(typeof(PlayerAnimator), "SetAnimation", new[] { typeof(PlayerAnimation), typeof(bool), typeof(bool) })]
    static class Patch_PlayerAnimator_SetAnimation_ThreeArgs_FpVisual
    {
        static void Postfix(PlayerAnimator __instance, PlayerAnimation animation, bool overrideIndex, bool triggering)
        {
            FirstPersonVisualRig.ForwardAnimation(__instance, animation, overrideIndex, triggering);
        }
    }

    [HarmonyPatch(typeof(PlayerAnimator), "ReselectAnimation")]
    static class Patch_PlayerAnimator_ReselectAnimation_FpVisual
    {
        static void Postfix(PlayerAnimator __instance)
        {
            FirstPersonVisualRig.ForwardReselect(__instance);
        }
    }
}
