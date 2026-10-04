using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  InputRouter — P2 输入设备配对与 InputAction 生命周期管理
    //
    //  职责：
    //  - 创建 / 销毁 P2 的 InputActionMap
    //  - 配对手柄（排除已被 P1 占用的设备）
    //  - 监听设备插拔事件，自动重配对
    //  - 从 P1 的 PlayerInput user 中解除手柄绑定
    //
    //  不持有任何游戏逻辑；只管理 InputSystem 对象。
    // ══════════════════════════════════════════════════════════════════════
    public sealed class InputRouter
    {
        readonly SplitScreenRuntime _rt;
        int _nextP2PairProbeFrame;
        bool _warnedNoP2Gamepad;

        public InputRouter(SplitScreenRuntime rt) { _rt = rt; }

        // ── 创建 P2 的全套 InputAction ─────────────────────────────────
        public void Create()
        {
            Dispose(); // 保证从干净状态开始

            var slot = _rt.P2;
            slot.ActionMap      = new InputActionMap("P2Controls");
            slot.ActionMove     = slot.ActionMap.AddAction("Move",     InputActionType.Value);
            slot.ActionLook     = slot.ActionMap.AddAction("Look",     InputActionType.Value);
            slot.ActionRotate   = slot.ActionMap.AddAction("Rotate",   InputActionType.Button);
            slot.ActionJump     = slot.ActionMap.AddAction("Jump",     InputActionType.Button);
            slot.ActionSprint   = slot.ActionMap.AddAction("Sprint",   InputActionType.Button);
            slot.ActionCrouch   = slot.ActionMap.AddAction("Crouch",   InputActionType.Button);
            slot.ActionInteract = slot.ActionMap.AddAction("Interact", InputActionType.Button);
            slot.ActionMenu     = slot.ActionMap.AddAction("Menu",     InputActionType.Button);
            slot.ActionPause    = slot.ActionMap.AddAction("Pause",    InputActionType.Button);
            slot.ActionFire       = slot.ActionMap.AddAction("Fire",       InputActionType.Button);
            slot.ActionContext    = slot.ActionMap.AddAction("Context",    InputActionType.Button);
            slot.ActionHotbarPrev = slot.ActionMap.AddAction("HotbarPrev", InputActionType.Button);
            slot.ActionHotbarNext = slot.ActionMap.AddAction("HotbarNext", InputActionType.Button);
            // 复杂菜单/工具扩展用的命名动作(物理键与上面【按上下文复用】：仅在各自上下文读取)。
            slot.ActionSpecial    = slot.ActionMap.AddAction("Special",    InputActionType.Button);  // 镜像/油漆单面等
            slot.ActionCancel     = slot.ActionMap.AddAction("Cancel",     InputActionType.Button);  // 菜单取消(=B)
            slot.ActionBlockPick  = slot.ActionMap.AddAction("BlockPick",  InputActionType.Button);  // 取模块(=R3)
            slot.ActionTabLeft    = slot.ActionMap.AddAction("TabLeft",    InputActionType.Button);  // 菜单上一标签(=LB)
            slot.ActionTabRight   = slot.ActionMap.AddAction("TabRight",   InputActionType.Button);  // 菜单下一标签(=RB)

            slot.ActionMove    .AddBinding("<Gamepad>/leftStick") .WithProcessor("stickDeadzone(min=0.125,max=0.925)");
            slot.ActionLook    .AddBinding("<Gamepad>/rightStick").WithProcessor("stickDeadzone(min=0.125,max=0.925)");
            slot.ActionRotate  .AddBinding("<Gamepad>/rightStickButton");
            slot.ActionJump    .AddBinding("<Gamepad>/buttonSouth");    // A
            slot.ActionSprint  .AddBinding("<Gamepad>/leftStickPress"); // L3 = 奔跑（LB/RB 让位给热栏切换）
            slot.ActionCrouch  .AddBinding("<Gamepad>/buttonEast");     // B
            slot.ActionInteract.AddBinding("<Gamepad>/buttonWest");     // X
            slot.ActionMenu    .AddBinding("<Gamepad>/buttonNorth");    // Y
            slot.ActionPause   .AddBinding("<Gamepad>/start");          // Start
            slot.ActionFire    .AddBinding("<Gamepad>/rightTrigger");   // RT = 使用/攻击(LMB)
            slot.ActionContext .AddBinding("<Gamepad>/leftTrigger");    // LT = 副功能/瞄准(RMB)
            slot.ActionHotbarPrev.AddBinding("<Gamepad>/leftShoulder");  // LB = 上一个热栏（对齐原版）
            slot.ActionHotbarNext.AddBinding("<Gamepad>/rightShoulder"); // RB = 下一个热栏（对齐原版）
            slot.ActionSpecial   .AddBinding("<Gamepad>/dpad/down");     // 镜像/单面(建造/油漆上下文)
            slot.ActionCancel    .AddBinding("<Gamepad>/buttonEast");    // B = 取消(菜单上下文，与下蹲复用)
            slot.ActionBlockPick .AddBinding("<Gamepad>/rightStickButton"); // R3 = 取模块(建造上下文，与自由旋转复用)
            slot.ActionTabLeft   .AddBinding("<Gamepad>/leftShoulder");  // LB = 上一标签(菜单上下文，与热栏复用)
            slot.ActionTabRight  .AddBinding("<Gamepad>/rightShoulder"); // RB = 下一标签(菜单上下文，与热栏复用)

            slot.InputUser = InputUser.CreateUserWithoutPairedDevices();

            var gp = FindP2Gamepad();
            if (gp != null)
            {
                UnpairGamepadFromP1(gp);
                slot.InputUser = InputUser.PerformPairingWithDevice(gp, user: slot.InputUser);
                Main.ModEntry.Logger.Log($"[Input] P2 paired to: {gp.displayName} (id={gp.deviceId})");
            }
            else
            {
                Main.ModEntry.Logger.Log("[Input] WARNING: 未找到可用手柄，P2 输入暂停，插入手柄后自动配对");
            }

            slot.InputUser.AssociateActionsWithUser(slot.ActionMap);
            slot.ActionMap.Enable();

            InputSystem.onDeviceChange += OnDeviceChange;
            Main.ModEntry.Logger.Log("[Input] P2 ActionMap 已启用：LS=move RS=look A=jump L3=sprint B=crouch X=interact RT=use LB/RB=hotbar");
        }

        
        public void Tick()
        {
            EnsureP2GamepadPaired();
        }

        public void Dispose()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;

            var slot = _rt.P2;
            if (slot.InputUser.valid)
            {
                slot.InputUser.UnpairDevicesAndRemoveUser();
                slot.InputUser = default;
            }

            if (slot.ActionMap != null)
            {
                slot.ActionMap.Disable();
                slot.ActionMap.Dispose();
                slot.ActionMap = null;
            }

            slot.ActionMove     = null;
            slot.ActionLook     = null;
            slot.ActionRotate   = null;
            slot.ActionJump     = null;
            slot.ActionSprint   = null;
            slot.ActionCrouch   = null;
            slot.ActionInteract = null;
            slot.ActionMenu     = null;
            slot.ActionPause    = null;
            slot.ActionFire       = null;
            slot.ActionContext    = null;
            slot.ActionHotbarPrev = null;
            slot.ActionHotbarNext = null;
            slot.ActionSpecial    = null;
            slot.ActionCancel     = null;
            slot.ActionBlockPick  = null;
            slot.ActionTabLeft    = null;
            slot.ActionTabRight   = null;
        }

        // ── 从 P1 的 PlayerInput user 中解除手柄，交给 P2 ─────────────
        public void UnpairGamepadFromP1()
        {
            try
            {
                var p1Input = UnityEngine.InputSystem.PlayerInput.GetPlayerByIndex(0);
                if (p1Input == null) return;
                foreach (var d in new List<InputDevice>(p1Input.user.pairedDevices))
                {
                    if (!(d is Gamepad)) continue;
                    p1Input.user.UnpairDevice(d);
                    Main.LogV($"[Input] Unpaired gamepad from P1: {d.displayName}");
                }

                // 关键：把 P1 明确切到键鼠方案。否则若分屏前 P1 正处于 "Gamepad" 方案，
                //  解绑手柄后会被 neverAutoSwitchControlSchemes 锁死在 "Gamepad" →
                //  CanvasHelper.CursorPos 返回(已失效的)手柄虚拟光标 → P1 鼠标拖拽图标/光标全失效。
                //  方案名不可硬编码（资产里并非 "KeyboardMouse"）→ 动态从 controlSchemes 里找非 Gamepad 的那个。
                try
                {
                    var kbScheme = FindKeyboardSchemeName(p1Input);
                    if (kbScheme != null && Keyboard.current != null)
                    {
                        if (Mouse.current != null)
                            p1Input.SwitchCurrentControlScheme(kbScheme, Keyboard.current, Mouse.current);
                        else
                            p1Input.SwitchCurrentControlScheme(kbScheme, Keyboard.current);
                        Main.LogV($"[Input] P1 control scheme -> '{p1Input.currentControlScheme}' (scheme '{kbScheme}')");
                    }
                    else Main.LogV("[Input] keyboard/mouse scheme not found, skip switch");
                }
                catch (Exception e) { Main.ModEntry.Logger.Log("  P1 切键鼠方案异常: " + e.Message); }

                p1Input.neverAutoSwitchControlSchemes = true;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log($"  UnpairGamepad exception: {e.Message}");
            }
        }

        // ── 把手柄归还 P1（分屏结束/离开世界后调用） ─────────────────
        //  SpawnP2 时 UnpairGamepadFromP1 解绑了手柄并置 neverAutoSwitchControlSchemes=true，
        //  Input.Dispose 又移除了 P2 的 InputUser → 手柄此刻不属于任何人，P1 无法用手柄。
        //  这里把空闲手柄重新配对回 P1 并恢复自动切换。
        //  返回 false 表示 P1 PlayerInput 尚未就绪（主菜单/加载中）→ 调用方下一帧重试。
        public bool RepairGamepadToP1()
        {
            try
            {
                var p1Input = UnityEngine.InputSystem.PlayerInput.GetPlayerByIndex(0);
                if (p1Input == null) return false;   // P1 还没就绪 → 稍后重试

                p1Input.neverAutoSwitchControlSchemes = false;

                var gp = Gamepad.current ?? (Gamepad.all.Count > 0 ? Gamepad.all[0] : null);
                if (gp == null) return true;         // 没手柄，无需修复（也别无限重试）

                // 1) 把手柄配对回 P1（若未配对）
                bool paired = false;
                foreach (var d in p1Input.user.pairedDevices)
                    if (d.deviceId == gp.deviceId) { paired = true; break; }
                if (!paired)
                {
                    InputUser.PerformPairingWithDevice(gp, user: p1Input.user);
                    Main.LogV($"[Input] Repaired: paired gamepad to P1: {gp.displayName}");
                }

                // 2) 强制把 P1 切到 "Gamepad" 方案 → 分屏结束/重载单人后手柄立即可控。
                //    (实践证明只依赖 auto-switch 在单人重载后不生效，手柄虽配对但方案仍停在键鼠 → 手柄失灵。)
                //    本方法仅在【真正在游戏世界】时调用(OnUpdate 门控 !IsLeavingGame && scene==MainScene)，
                //    不会在主菜单触发；而之前担心的"主菜单鼠标消失"另有真因(CloseAllMenus)且已单独修复，
                //    所以这里恢复强制切方案是安全的。neverAutoSwitch=false 保证玩家一动鼠标即切回键鼠。
                try { p1Input.SwitchCurrentControlScheme("Gamepad", gp); }
                catch (Exception e2) { Main.ModEntry.Logger.Log("  强制切 Gamepad 方案异常: " + e2.Message); }
                Main.LogV($"[Input] Repaired: P1 gamepad scheme restored (current='{p1Input.currentControlScheme}')");
                return true;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log($"  RepairGamepadToP1 exception: {e.Message}");
                return true; // 异常也别无限重试
            }
        }

        // ── P2 菜单期间：让原版手柄虚拟光标对 P2 生效 ──────────────────────
        //  原版 GamepadCursor 门控 = (PlayerInput[0].actionMap=="UI" && currentControlScheme=="Gamepad")。
        //  P2 开菜单时 OpenMenu 已把 actionMap 切到 "UI"；只差把 P1 的控制方案标成 "Gamepad"。
        //  用 InputUser.ActivateControlScheme(只改方案字符串、不改设备配对) → P2 仍持有手柄(Y 关菜单照常)，
        //  而 GamepadCursor 读 Gamepad.current(=P2 手柄)驱动虚拟光标。neverAutoSwitch=true 保证不被自动切回。
        // 启/停 P2 的游戏内动作（移动/视角等）。打开 P2 独立背包时停掉 → 左摇杆专控背包光标。
        public void SetP2GameplayEnabled(bool enabled)
        {
            var s = _rt.P2;
            if (s.ActionMove == null) return;
            if (enabled)
            {
                s.ActionMove?.Enable(); s.ActionLook?.Enable(); s.ActionJump?.Enable();
                s.ActionSprint?.Enable(); s.ActionCrouch?.Enable(); s.ActionFire?.Enable(); s.ActionRotate?.Enable();
            }
            else
            {
                s.ActionMove?.Disable(); s.ActionLook?.Disable(); s.ActionJump?.Disable();
                s.ActionSprint?.Disable(); s.ActionCrouch?.Disable(); s.ActionFire?.Disable(); s.ActionRotate?.Disable();
            }
        }

        public void EnterP2MenuCursorMode()
        {
            // 1) 停掉 P2 的移动/视角动作，否则左摇杆既走人物又被光标读取（人物乱走）。
            //    GamepadCursor 读的是 Gamepad.current 原始设备，不经 P2 动作 → 禁用动作不影响光标。
            var s = _rt.P2;
            s.ActionMove?.Disable();
            s.ActionLook?.Disable();
            s.ActionJump?.Disable();
            s.ActionSprint?.Disable();
            s.ActionCrouch?.Disable();
            s.ActionFire?.Disable();
            s.ActionRotate?.Disable();

            // 2) 把 P1 标成 "Gamepad" 方案(不改设备配对) → 满足 GamepadCursor 门控、CanvasHelper.CursorPos 走手柄光标。
            var p1 = UnityEngine.InputSystem.PlayerInput.GetPlayerByIndex(0);
            if (p1 == null) return;
            try
            {
                p1.user.ActivateControlScheme("Gamepad");

                // 3) 把 P2 手柄【额外】配对到 P1 的 user → 原版 UI 动作(InputA 抓取/inputY/Dpad 导航/B 取消)
                //    读 PlayerInput[0] 的设备，否则收不到 P2 手柄的 A → 无法抓取。手柄同时仍在 P2 user 上(Y 关菜单照常)。
                var gp = GetP2Gamepad();
                if (gp != null && !PairedTo(p1.user, gp))
                {
                    UnityEngine.InputSystem.Users.InputUser.PerformPairingWithDevice(gp, p1.user);
                    _p2GpPairedToP1 = true;
                    Main.LogV($"[P2Cursor] P2 gamepad paired to P1.user for vanilla UI actions: {gp.displayName}");
                }
                Main.LogV($"[P2Cursor] enter: P1 scheme='{p1.currentControlScheme}'");
            }
            catch (Exception e) { Main.ModEntry.Logger.Log("[P2Cursor] 进入异常: " + e.Message); }
        }

        bool _p2GpPairedToP1;

        static bool PairedTo(UnityEngine.InputSystem.Users.InputUser user, InputDevice dev)
        {
            foreach (var d in user.pairedDevices) if (d.deviceId == dev.deviceId) return true;
            return false;
        }

        public Gamepad GetP2Gamepad()
        {
            var u = _rt.P2.InputUser;
            if (u.valid)
                foreach (var d in u.pairedDevices) if (d is Gamepad g) return g;
            return Gamepad.current;
        }

        void EnsureP2GamepadPaired()
        {
            var slot = _rt.P2;
            if (slot.ActionMap == null || !slot.InputUser.valid) return;
            if (Time.frameCount < _nextP2PairProbeFrame) return;
            _nextP2PairProbeFrame = Time.frameCount + 60;

            foreach (var d in slot.InputUser.pairedDevices)
            {
                if (d is Gamepad)
                {
                    if (!slot.ActionMap.enabled) slot.ActionMap.Enable();
                    if (!Main.IsP2BackpackOpen && !Main.IsP2BuildMenuOpen && !Main.IsP2MenuOpen && !Main.p2IsUsingMenu)
                        SetP2GameplayEnabled(true);
                    return;
                }
            }

            var gp = FindP2Gamepad();
            if (gp == null)
            {
                if (!_warnedNoP2Gamepad)
                {
                    Main.ModEntry.Logger.Log("[Input] WARNING: P2 gamepad not available; will keep probing");
                    _warnedNoP2Gamepad = true;
                }
                return;
            }
            // 诊断日志：每次 InitializeComponents 之后都报告关键组件状态
            try
            {
                UnpairGamepadFromP1(gp);
                slot.InputUser = InputUser.PerformPairingWithDevice(gp, user: slot.InputUser);
                slot.InputUser.AssociateActionsWithUser(slot.ActionMap);
                slot.ActionMap.Enable();
                if (!Main.IsP2BackpackOpen && !Main.IsP2BuildMenuOpen && !Main.IsP2MenuOpen && !Main.p2IsUsingMenu)
                    SetP2GameplayEnabled(true);
                _warnedNoP2Gamepad = false;
                Main.ModEntry.Logger.Log($"[Input] P2 gamepad recovered: {gp.displayName} (id={gp.deviceId})");
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log("[Input] P2 gamepad recovery failed: " + e.Message);
            }
        }

        public void ExitP2MenuCursorMode()
        {
            var s = _rt.P2;
            s.ActionMove?.Enable();
            s.ActionLook?.Enable();
            s.ActionJump?.Enable();
            s.ActionSprint?.Enable();
            s.ActionCrouch?.Enable();
            s.ActionFire?.Enable();
            s.ActionRotate?.Enable();

            var p1 = UnityEngine.InputSystem.PlayerInput.GetPlayerByIndex(0);
            if (p1 == null) return;
            try
            {
                // 解除 P2 手柄与 P1.user 的额外配对（手柄回到只属 P2）
                if (_p2GpPairedToP1)
                {
                    var gp = GetP2Gamepad();
                    if (gp != null && PairedTo(p1.user, gp)) p1.user.UnpairDevice(gp);
                    _p2GpPairedToP1 = false;
                }
                var kb = FindKeyboardSchemeName(p1);
                if (kb != null) p1.user.ActivateControlScheme(kb);
                Main.LogV($"[P2Cursor] exit: P1 scheme='{p1.currentControlScheme}'");
            }
            catch (Exception e) { Main.ModEntry.Logger.Log("[P2Cursor] 退出异常: " + e.Message); }
        }

        // 动态找出键鼠控制方案名（资产里不一定叫 "KeyboardMouse"）。
        //  优先名字含 "eyboard" 的；否则取第一个非 "Gamepad" 方案。并打印全部方案名以便核对。
        static string FindKeyboardSchemeName(UnityEngine.InputSystem.PlayerInput pi)
        {
            try
            {
                var schemes = pi.actions.controlSchemes;
                var names = new List<string>();
                foreach (var s in schemes) names.Add(s.name);
                Main.LogV("[Input] schemes: " + string.Join(", ", names));

                foreach (var n in names)
                    if (n.IndexOf("eyboard", StringComparison.OrdinalIgnoreCase) >= 0) return n;
                foreach (var n in names)
                    if (!n.Equals("Gamepad", StringComparison.OrdinalIgnoreCase)) return n;
            }
            catch (Exception e) { Main.ModEntry.Logger.Log("  FindKeyboardSchemeName 异常: " + e.Message); }
            return null;
        }

        // ── 找到未被任何 user 配对的手柄 ─────────────────────────────
        static Gamepad FindP2Gamepad()
        {
            if (Gamepad.all.Count == 0) return null;

            var pairedIds = new HashSet<int>();
            foreach (var user in InputUser.all)
                foreach (var d in user.pairedDevices)
                    pairedIds.Add(d.deviceId);

            foreach (var gp in Gamepad.all)
                if (!pairedIds.Contains(gp.deviceId)) return gp;

            Main.LogV("[Input] all gamepads are paired, using Gamepad.all[0] as P2 device");
            return Gamepad.all[0];
        }

        static void UnpairGamepadFromP1(Gamepad gp)
        {
            if (gp == null) return;
            try
            {
                var p1Input = UnityEngine.InputSystem.PlayerInput.GetPlayerByIndex(0);
                if (p1Input == null) return;
                foreach (var d in new List<InputDevice>(p1Input.user.pairedDevices))
                {
                    if (d.deviceId != gp.deviceId) continue;
                    p1Input.user.UnpairDevice(d);
                    Main.LogV($"[Input] Unpaired P2 gamepad from P1: {d.displayName}");
                }
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log("[Input] Unpair P2 gamepad from P1 failed: " + e.Message);
            }
        }

        // ── 设备插拔事件：自动重配对 ─────────────────────────────────
        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            var slot = _rt.P2;
            if (!slot.InputUser.valid) return;

            switch (change)
            {
                case InputDeviceChange.Disconnected:
                case InputDeviceChange.Removed:
                    foreach (var d in slot.InputUser.pairedDevices)
                        if (d.deviceId == device.deviceId)
                        {
                            Main.ModEntry.Logger.Log($"[Input] P2 手柄断开: {device.displayName}");
                            break;
                        }
                    break;

                case InputDeviceChange.Added:
                case InputDeviceChange.Reconnected:
                    if (!(device is Gamepad newGp)) break;
                    bool hasGp = false;
                    foreach (var d in slot.InputUser.pairedDevices)
                        if (d is Gamepad) { hasGp = true; break; }
                    if (!hasGp)
                    {
                        slot.InputUser = InputUser.PerformPairingWithDevice(newGp, user: slot.InputUser);
                        slot.InputUser.AssociateActionsWithUser(slot.ActionMap);
                        slot.ActionMap.Enable();
                        Main.ModEntry.Logger.Log($"[Input] P2 手柄自动配对: {newGp.displayName} (id={newGp.deviceId})");
                    }
                    break;
            }
        }
    }
}
