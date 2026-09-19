using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    //
    
    
    public sealed class InputRouter
    {
        readonly SplitScreenRuntime _rt;
        int _nextP2PairProbeFrame;
        bool _warnedNoP2Gamepad;

        public InputRouter(SplitScreenRuntime rt) { _rt = rt; }

        
        public void Create()
        {
            Dispose(); 

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
            
            slot.ActionSpecial    = slot.ActionMap.AddAction("Special",    InputActionType.Button);  
            slot.ActionCancel     = slot.ActionMap.AddAction("Cancel",     InputActionType.Button);  
            slot.ActionBlockPick  = slot.ActionMap.AddAction("BlockPick",  InputActionType.Button);  
            slot.ActionTabLeft    = slot.ActionMap.AddAction("TabLeft",    InputActionType.Button);  
            slot.ActionTabRight   = slot.ActionMap.AddAction("TabRight",   InputActionType.Button);  

            slot.ActionMove    .AddBinding("<Gamepad>/leftStick") .WithProcessor("stickDeadzone(min=0.125,max=0.925)");
            slot.ActionLook    .AddBinding("<Gamepad>/rightStick").WithProcessor("stickDeadzone(min=0.125,max=0.925)");
            slot.ActionRotate  .AddBinding("<Gamepad>/rightStickButton");
            slot.ActionJump    .AddBinding("<Gamepad>/buttonSouth");    // A
            slot.ActionSprint  .AddBinding("<Gamepad>/leftStickPress"); 
            slot.ActionCrouch  .AddBinding("<Gamepad>/buttonEast");     // B
            slot.ActionInteract.AddBinding("<Gamepad>/buttonWest");     // X
            slot.ActionMenu    .AddBinding("<Gamepad>/buttonNorth");    // Y
            slot.ActionPause   .AddBinding("<Gamepad>/start");          // Start
            slot.ActionFire    .AddBinding("<Gamepad>/rightTrigger");   
            slot.ActionContext .AddBinding("<Gamepad>/leftTrigger");    
            slot.ActionHotbarPrev.AddBinding("<Gamepad>/leftShoulder");  
            slot.ActionHotbarNext.AddBinding("<Gamepad>/rightShoulder"); 
            slot.ActionSpecial   .AddBinding("<Gamepad>/dpad/down");     
            slot.ActionCancel    .AddBinding("<Gamepad>/buttonEast");    
            slot.ActionBlockPick .AddBinding("<Gamepad>/rightStickButton"); 
            slot.ActionTabLeft   .AddBinding("<Gamepad>/leftShoulder");  
            slot.ActionTabRight  .AddBinding("<Gamepad>/rightShoulder"); 

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

        
        
        
        
        
        public bool RepairGamepadToP1()
        {
            try
            {
                var p1Input = UnityEngine.InputSystem.PlayerInput.GetPlayerByIndex(0);
                if (p1Input == null) return false;   

                p1Input.neverAutoSwitchControlSchemes = false;

                var gp = Gamepad.current ?? (Gamepad.all.Count > 0 ? Gamepad.all[0] : null);
                if (gp == null) return true;         

                
                bool paired = false;
                foreach (var d in p1Input.user.pairedDevices)
                    if (d.deviceId == gp.deviceId) { paired = true; break; }
                if (!paired)
                {
                    InputUser.PerformPairingWithDevice(gp, user: p1Input.user);
                    Main.LogV($"[Input] Repaired: paired gamepad to P1: {gp.displayName}");
                }

                
                
                
                
                
                try { p1Input.SwitchCurrentControlScheme("Gamepad", gp); }
                catch (Exception e2) { Main.ModEntry.Logger.Log("  强制切 Gamepad 方案异常: " + e2.Message); }
                Main.LogV($"[Input] Repaired: P1 gamepad scheme restored (current='{p1Input.currentControlScheme}')");
                return true;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Log($"  RepairGamepadToP1 exception: {e.Message}");
                return true; 
            }
        }

        
        
        
        
        
        
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
            
            
            var s = _rt.P2;
            s.ActionMove?.Disable();
            s.ActionLook?.Disable();
            s.ActionJump?.Disable();
            s.ActionSprint?.Disable();
            s.ActionCrouch?.Disable();
            s.ActionFire?.Disable();
            s.ActionRotate?.Disable();

            
            var p1 = UnityEngine.InputSystem.PlayerInput.GetPlayerByIndex(0);
            if (p1 == null) return;
            try
            {
                p1.user.ActivateControlScheme("Gamepad");

                
                
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
                    if (!Main.IsP2BackpackOpen && !Main.IsP2BuildMenuOpen && !Main.IsP2MenuOpen)
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

            try
            {
                UnpairGamepadFromP1(gp);
                slot.InputUser = InputUser.PerformPairingWithDevice(gp, user: slot.InputUser);
                slot.InputUser.AssociateActionsWithUser(slot.ActionMap);
                slot.ActionMap.Enable();
                if (!Main.IsP2BackpackOpen && !Main.IsP2BuildMenuOpen && !Main.IsP2MenuOpen)
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
