using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    [HarmonyPatch(typeof(Instrument), "Update")]
    static class Patch_Instrument_Update_P2
    {
        static bool Prefix(Instrument __instance)
        {
            if (Main.player2 == null) return true;
            if (Main.IsP2UsingInstrument(__instance))
            {
                Main.TickP2Instrument(__instance);
                return false;
            }
            Main.ReleaseP2Instrument(__instance);
            return true;
        }
    }

    [HarmonyPatch(typeof(GamepadCursor), "SetLayer")]
    static class Patch_GamepadCursor_SetLayer_P2Piano
    {
        static bool Prefix(GamepadCursorLayer layer)
        {
            return !(Main.IsP2OriginalInputActive && layer == GamepadCursorLayer.Piano);
        }
    }

    [HarmonyPatch(typeof(PianoUI), "Update")]
    static class Patch_PianoUI_Update_P2
    {
        static bool Prefix()
        {
            if (!Main.IsP2PianoActive) return true;
            Main.SetP2PianoCanvasEnabled(true);
            return false;
        }
    }

    // ⚠ 有意偏离原版(用户 2026-07-20 明确要求,勿当 bug "修"回去)。
    //
    // 原版:vanilla ThirdPerson.HandleThirdPerson 读视角输入的那段被
    //   `if (CanvasHelper.ActiveMenu == MenuType.None && !IsPressed(actionRotate,"Rotate"))` 门控。
    // 坐上钢琴会开 MenuType.Piano → 该段整个跳过 → localCameraRotation 冻结在坐下那一刻,
    // TP 相机仍绕人物、仍跟随,但朝向再也转不动(表现:视角卡在"交互前的朝向",每次都不同)。
    // 椅子不开菜单故不受影响 —— 这就是"椅子正常、钢琴卡住"的全部原因,原版即如此。
    //
    // 本补丁只在「P1 + TP + 正坐在乐器前」这一条极窄路径上,把 HandleThirdPerson 执行期间的
    // ActiveMenu 临时视作 None,让原版自己的视角累积照常跑。作用域仅覆盖这一次调用,
    // 出方法立即还原,不影响任何其它读 ActiveMenu 的逻辑(该方法内也只有这一处读它)。
    // 刻意不碰 FP:原版 FP 弹琴锁视角是明确设计(Instrument 走 AttachPlayer.disableMouseLook)。
    [HarmonyPatch(typeof(ThirdPerson), "HandleThirdPerson")]
    static class Patch_ThirdPerson_HandleThirdPerson_P1PianoFreeLook
    {
        static void Prefix(ThirdPerson __instance, ref CanvasActiveMenuScope __state)
        {
            __state = null;
            var p1 = Main.player1;
            if (p1 == null || __instance == null) return;
            if (CanvasHelper.ActiveMenu != MenuType.Piano) return;
            if (!__instance.ThirdPersonState) return;
            if (p1.PlayerNetworkManager == null || !p1.PlayerNetworkManager.IsAttached) return;
            if (__instance.GetComponentInParent<Network_Player>() != p1) return;

            __state = new CanvasActiveMenuScope(MenuType.None);
        }

        static void Postfix(CanvasActiveMenuScope __state) => __state?.Dispose();

        static System.Exception Finalizer(System.Exception __exception, CanvasActiveMenuScope __state)
        {
            __state?.Dispose();
            return __exception;
        }
    }

    // ⚠ 同上,有意偏离原版:让 P1 在钢琴前也能直接按 V 切视角,不必先站起来。
    //
    // 原版被同一个 ActiveMenu 门控卡了两道:
    //   ① Player.Update 的切换键要求 CanvasHelper.ActiveMenu == MenuType.None;
    //   ② 即便按下去,ThirdPersonState 的 setter 走 SetThirdPersonState(force:false),
    //      里面的 CanGoIntoThirdperson() 同样要求 ActiveMenu == None。
    // 所以这里自行接管这一次输入,并用 ForceThirdPersonState(force:true) 一次绕过两道门控 ——
    // 与 SplitScreenDeathFlow 处理「P1 搬运 P2 时切视角」用的是同一套做法。
    //
    // 附带好处:走 vanilla 的 SetThirdPersonState 会触发 OnThirdpersonStateChange,
    // 而 Instrument 订阅了它 → 切到 TP 时会把钢琴强制的 FOVStyle.Default 还原成玩家设置,
    // 顺手解决了"TP 弹琴视野被压到 50"的问题(原版因为按不动切换键而永远触发不到)。
    internal static class P1PianoViewToggle
    {
        static InputAction _viewAction;
        static bool _wasHeld;

        internal static void Tick()
        {
            var p1 = Main.player1;
            if (p1 == null || CanvasHelper.ActiveMenu != MenuType.Piano ||
                p1.PlayerNetworkManager == null || !p1.PlayerNetworkManager.IsAttached)
            { _wasHeld = false; return; }

            if (_viewAction == null)
            {
                var input = PlayerInput.GetPlayerByIndex(0);
                if (input == null) return;
                _viewAction = input.actions["View"];
                if (_viewAction == null) return;
            }

            // 手柄按住式与键盘按下式都认(仿 IsP1DownedViewHeld 的双判)
            bool held = _viewAction.ReadValue<float>() > 0.5f;
            bool pressed = SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance
                               .WasPressedThisFrame(_viewAction, "Thirdperson")
                           || (held && !_wasHeld);
            _wasHeld = held;
            if (!pressed) return;

            var tp = p1.currentModel != null ? p1.currentModel.thirdPersonSettings : null;
            if (tp == null) return;
            tp.ForceThirdPersonState(!tp.ThirdPersonState);
            Main.LogV("[P1Piano] 钢琴前切视角 thirdPerson=" + tp.ThirdPersonState);
        }
    }
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
}
