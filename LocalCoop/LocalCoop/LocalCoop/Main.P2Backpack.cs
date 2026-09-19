using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        //
        
        //
        
        
        
        
        
        
        
        //
        
        
        static bool _p2BackpackOpen;
        internal static bool IsP2BackpackOpen => _p2BackpackOpen;

        
        static int _p1BackpackActiveSlots = -1;

        
        internal static void SanitizeClone(Inventory clone)
        {
            if (clone == null) return;
            if (clone is PlayerInventory pinv) { SanitizePlayerInventoryClone(pinv); return; }
            SanitizePlainInventoryClone(clone);
        }

        static void SanitizePlainInventoryClone(Inventory clone)
        {
            var own = clone.GetComponentsInChildren<Slot>(true);
            var seen = new HashSet<Slot>();
            var rebuilt = new List<Slot>(own.Length);
            foreach (var s in own)
                if (s != null && seen.Add(s)) rebuilt.Add(s);
            clone.allSlots = rebuilt;
        }

        static void SanitizePlayerInventoryClone(PlayerInventory clone)
        {
            if (clone == null) return;
            var own = clone.GetComponentsInChildren<Slot>(true);
            var seen = new HashSet<Slot>();
            var normals = new List<Slot>();
            var backpacks = new List<Slot>();
            var equips = new List<Slot_Equip>();
            foreach (var s in own)
            {
                if (s == null || !seen.Add(s)) continue;
                if (s.slotType == SlotType.Hotbar) continue;
                if (s.slotType == SlotType.Equipment) { if (s is Slot_Equip se) equips.Add(se); continue; }
                if (s.slotType == SlotType.Backpack) backpacks.Add(s);
                else normals.Add(s);
            }
            var rebuilt = new List<Slot>(normals.Count + backpacks.Count);
            rebuilt.AddRange(normals);
            rebuilt.AddRange(backpacks);
            clone.allSlots = rebuilt;
            clone.equipSlots = equips;
        }

        static int CountActiveBackpackSlots(PlayerInventory pinv)
        {
            int n = 0;
            if (pinv?.allSlots == null) return 0;
            foreach (var s in pinv.allSlots)
                if (s != null && s.slotType == SlotType.Backpack && s.active) n++;
            return n;
        }

        static PlayerInventory ActiveP2BackpackInventory()
        {
            return _p2BackpackOpen && _p2BackpackView != null
                ? _p2BackpackView
                : ComponentManager<PlayerInventory>.Value;
        }

        internal static void RefreshP2BackpackSlots()
        {
            if (!_p2BackpackOpen) return;
            var pinv = ActiveP2BackpackInventory();
            if (pinv == null) return;
            pinv.SetBackpackActiveSlots(P2EquipmentStore.P2BackpackExtraSlots());
        }

        
        static PlayerInventory _p2BackpackView;
        static GameObject _p2BackpackViewGo;

        
        static RectTransform _p2InvCursor;
        static Vector2       _p2InvCursorPos;
        static Image         _p2HeldIcon;
        static Text          _p2HeldCount;
        static ItemInstance  _p2Held;
        static Slot          _p2HeldOriginSlot;
        static int           _p2HeldOriginHotbar = -1;
        static bool          _wasABp;
        static bool          _aPending;          
        static float         _aPressT;           
        static bool          _p2HeldDropPromptShown;
        const float P2HoldHalfDelay = 0.3f;      

        
        static Slot  _p2HoverSlot;       // kind 1
        static int   _p2HoverHotbar = -1;
        static Image _p2HoverSlotBg;
        static Color _p2HoverPrevColor;
        static readonly Color P2SlotHoverTint = new Color(1f, 0.85f, 0.3f, 0.55f);

        const float P2BpCursorSpeed = 1100f;

        static bool OpenP2BackpackClone(bool openCrafting)
        {
            if (P2InventoryStore.IsSwapped)
            {
                try { P2InventoryStore.SwapOutP2(); }
                catch (Exception e) { LogV("[P2Backpack] SwapOut before open ignored: " + e.Message); }
            }
            var sourceInv = ComponentManager<PlayerInventory>.Value;
            if (sourceInv == null || _p2HudCanvas == null)
            {
                ModEntry.Logger.Log("[P2Backpack] open failed: PlayerInventory or canvas missing");
                return false;
            }

            var pinv = EnsureP2BackpackView(sourceInv);
            if (pinv == null)
            {
                ModEntry.Logger.Log("[P2Backpack] open failed: P2 backpack clone missing");
                return false;
            }

            P2InventoryStore.RestoreInto(pinv);
            ClearP2EquipmentSlots(pinv);

            var panel = pinv.transform as RectTransform;
            if (panel == null) return false;
            if (panel.parent != _p2HudCanvas.transform)
                panel.SetParent(_p2HudCanvas.transform, false);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;
            panel.localScale = Vector3.one;
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) SetLayerRecursively(panel, uiLayer);
            pinv.gameObject.SetActive(true);

            _p1BackpackActiveSlots = CountActiveBackpackSlots(pinv);
            pinv.SetBackpackActiveSlots(P2EquipmentStore.P2BackpackExtraSlots());
            RefreshRealSlots(pinv);
            // The UI is cloned from P1's inventory, including its slot contents.
            // Never use that cloned state as P2 equipment data; only the P2
            // world-save-backed equipment store is authoritative.
            P2EquipmentStore.ShowInSlots(pinv);
            if (openCrafting) OpenP2Crafting();

            EnsureBpCursor();
            _p2InvCursorPos = Vector2.zero;
            if (_p2InvCursor != null) { _p2InvCursor.gameObject.SetActive(true); _p2InvCursor.anchoredPosition = Vector2.zero; }
            if (_p2HeldIcon != null) _p2HeldIcon.transform.SetAsLastSibling();
            if (_p2InvCursor != null) _p2InvCursor.SetAsLastSibling();

            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(false);
            _p2BackpackOpen = true;
            LogV("[P2Backpack] opened independent cloned backpack view for P2");
            return true;
        }

        internal static PlayerInventory P2ViewOrNull => _p2BackpackView;

        static Network_Player _p2InventoryFieldOwner;
        static PlayerInventory _p2InventoryFieldOriginal;

        // Inventory.localPlayerInventory 是【static】字段,语义上是"本地玩家的背包"。
        // P2 是 isLocalPlayer=false 的克隆,克隆的 Inventory.Start()(无条件
        // localPlayerInventory = ComponentManager<PlayerInventory>.Value)/懒初始化会把它
        // 覆盖成 P2 克隆。之后 P1 悬浮 vanilla Inventory.HoverEnter → localPlayerInventory
        // .SetItemDescription 就把物品详情/图标写到 P2 面板(P1 自己反而完全不显示)。
        // 每帧把它钉回 P1;不影响 P2:P2 描述走 pinv.SetItemDescription 直接对克隆。
        static System.Reflection.FieldInfo _invLocalPlayerInvField =
            typeof(Inventory).GetField("localPlayerInventory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        internal static void GuardInventoryLocalPlayerStatic()
        {
            if (_invLocalPlayerInvField == null) return;
            var p1Inv = player1 != null ? player1.Inventory : null;
            if ((UnityEngine.Object)p1Inv == null) return;
            var cur = _invLocalPlayerInvField.GetValue(null) as PlayerInventory;
            if (ReferenceEquals(cur, p1Inv)) return;
            // 仅当当前值确实是我们挂在 P2 HUD 下的克隆时才纠正,避免误伤其它合法状态。
            if ((UnityEngine.Object)cur != null && IsP2HudClone(cur))
                _invLocalPlayerInvField.SetValue(null, p1Inv);
        }

        internal static void MaintainP2InventoryField()
        {
            GuardInventoryLocalPlayerStatic();
            var p2 = player2;
            if ((UnityEngine.Object)p2 == null) { RestoreP2InventoryField(); return; }
            var view = _p2BackpackView != null ? _p2BackpackView : EnsureP2View();
            if ((UnityEngine.Object)view == null) return;

            if (!ReferenceEquals(_p2InventoryFieldOwner, p2))
            {
                _p2InventoryFieldOwner = p2;
                _p2InventoryFieldOriginal = !ReferenceEquals(p2.Inventory, view)
                    ? p2.Inventory
                    : (player1 != null ? player1.Inventory : ComponentManager<PlayerInventory>.Value);
            }

            var singleton = ComponentManager<PlayerInventory>.Value;
            if (ReferenceEquals(singleton, view))
            {
                var p1Inv = player1 != null ? player1.Inventory : null;
                P2InventoryGlobalScope.RestoreSingletonIfCurrent(view, p1Inv != null ? p1Inv : _p2InventoryFieldOriginal);
            }

            if (!ReferenceEquals(p2.Inventory, view))
                p2.Inventory = view;
        }

        internal static void RestoreP2InventoryField()
        {
            var owner = _p2InventoryFieldOwner;
            var view = _p2BackpackView;
            var original = _p2InventoryFieldOriginal;

            if ((UnityEngine.Object)owner != null && (UnityEngine.Object)original != null && ReferenceEquals(owner.Inventory, view))
                owner.Inventory = original;

            if ((UnityEngine.Object)view != null && ReferenceEquals(ComponentManager<PlayerInventory>.Value, view))
            {
                var p1Inv = player1 != null ? player1.Inventory : null;
                P2InventoryGlobalScope.RestoreSingletonIfCurrent(view, p1Inv != null ? p1Inv : original);
            }

            _p2InventoryFieldOwner = null;
            _p2InventoryFieldOriginal = null;
        }

        internal static PlayerInventory EnsureP2View()
        {
            if (_p2BackpackView != null) return _p2BackpackView;
            if (_p2HudCanvas == null) return null;
            var src = ComponentManager<PlayerInventory>.Value;
            if (src == null) return null;
            return EnsureP2BackpackView(src);
        }

        static PlayerInventory EnsureP2BackpackView(PlayerInventory sourceInv)
        {
            if (_p2BackpackView != null) return _p2BackpackView;
            if (sourceInv == null || _p2HudCanvas == null) return null;

            GameObject clone;
            using (P2InventoryGlobalScope.PreserveCurrentSingleton())
            {
                clone = UnityEngine.Object.Instantiate(sourceInv.gameObject, _p2HudCanvas.transform, false);
                clone.name = "P2_BackpackView";
                SanitizeClonedUiRoot(clone);
                clone.SetActive(true);
            }
            // PlayerInventory.Awake registers the clone globally. Restore the
            // real host inventory before any vanilla P1 UI can initialize.
            if (player1 != null && player1.Inventory != null)
                ComponentManager<PlayerInventory>.Value = player1.Inventory;
            _p2BackpackViewGo = clone;
            _p2BackpackView = clone.GetComponent<PlayerInventory>();
            if (_p2BackpackView == null)
            {
                UnityEngine.Object.Destroy(clone);
                _p2BackpackViewGo = null;
                return null;
            }

            SanitizeClone(_p2BackpackView);

            foreach (var g in clone.GetComponentsInChildren<Graphic>(true))
                g.raycastTarget = false;

            P2InventoryStore.RestoreInto(_p2BackpackView);
            RefreshRealSlots(_p2BackpackView);
            clone.SetActive(false);
            return _p2BackpackView;
        }

        internal static void OpenP2Backpack(bool openCrafting = false)
        {
            if (_p2BackpackOpen) return;
            OpenP2BackpackClone(openCrafting);
        }

        internal static void CloseP2Backpack()
        {
            if (!_p2BackpackOpen) return;
            CloseP2BackpackClone();
        }

        static bool CloseP2BackpackClone()
        {
            if (_p2BackpackView == null) return false;
            _p2BackpackOpen = false;

            var pinv = _p2BackpackView;
            if (_p2Held != null)
            {
                foreach (var s in pinv.allSlots)
                    if (s != null && s.slotType != SlotType.Hotbar && s.IsEmpty) { s.SetItem(_p2Held); _p2Held = null; break; }
            }

            _p2Held = null;
            ClearP2HeldOrigin();
            ClearHoverSlot();
            ClearP2HeldDropPrompt();
            ShowBpHints(false);
            CloseP2Crafting();
            if (_p2HeldIcon != null) _p2HeldIcon.enabled = false;
            if (_p2HeldCount != null) _p2HeldCount.text = "";
            if (_p2InvCursor != null) _p2InvCursor.gameObject.SetActive(false);

            P2InventoryStore.CaptureFrom(pinv);
            pinv.gameObject.SetActive(false);

            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(true);
            if (_p1BackpackActiveSlots >= 0)
            {
                pinv.SetBackpackActiveSlots(_p1BackpackActiveSlots);
                _p1BackpackActiveSlots = -1;
            }

            RefreshP2HeldItem();
            LogV("[P2Backpack] closed independent cloned backpack view for P2");
            return true;
        }

        
        internal static void ForceResetP2Backpack()
        {
            RestoreP2InventoryField();
            if (_p2ResearchTable != null)
            {
                try { CloseP2ResearchTable(); }
                catch (Exception e) { LogV("[P2Backpack] ForceReset CloseP2ResearchTable ignored: " + e.Message); }
            }
            if (_p2BackpackOpen)
            {
                try { CloseP2Backpack(); }
                catch (Exception e) { LogV("[P2Backpack] ForceReset CloseP2Backpack ignored: " + e.Message); }
            }
            try { CloseP2Storage(); }
            catch (Exception e) { LogV("[P2Backpack] ForceReset CloseP2Storage ignored: " + e.Message); }
            try { P2InventoryStore.Reset(); }
            catch (Exception e) { LogV("[P2Backpack] ForceReset inventory store reset ignored: " + e.Message); }
            _p2BackpackOpen = false; _p2Held = null;
            ClearP2HeldOrigin();
            ClearP2HeldDropPrompt();
        }

        
        
        
        static Inventory _p2StorageInv;
        static Inventory _p2StorageSourceInv;
        static GameObject _p2StorageViewGo;
        static Storage_Small _p2StorageObject;
        internal static bool IsP2StorageOpen => _p2StorageInv != null;
        internal static bool IsP2UsingStorage(Storage_Small storage) => storage != null && _p2StorageInv != null && _p2StorageObject == storage;

        internal static void OpenP2Storage(Storage_Small storage)
        {
            if (storage == null || _p2HudCanvas == null || _p2StorageInv != null) return;
            var inv = storage.GetInventoryReference();
            var panel = inv != null ? inv.transform as RectTransform : null;
            if (panel == null) return;

            if (!_p2BackpackOpen) OpenP2Backpack(openCrafting: true);

            _p2StorageObject = storage;
            SetP2StorageVisualOpen(storage, true);

            if (OpenP2StorageClone(inv)) return;
            ModEntry.Logger.Log("[P2Storage] clone open failed");
        }

        static bool OpenP2StorageClone(Inventory source)
        {
            if (source == null || _p2HudCanvas == null) return false;
            if (_p2StorageViewGo != null) Object.Destroy(_p2StorageViewGo);

            bool wasActive = source.gameObject.activeSelf;
            _p2StorageViewGo = Object.Instantiate(source.gameObject, _p2HudCanvas.transform, false);
            _p2StorageViewGo.name = "P2_StorageInventory";
            SanitizeClonedUiRoot(_p2StorageViewGo);
            source.gameObject.SetActive(wasActive);

            var cloneInv = _p2StorageViewGo.GetComponent<Inventory>();
            var panel = _p2StorageViewGo.transform as RectTransform;
            if (cloneInv == null || panel == null)
            {
                Object.Destroy(_p2StorageViewGo);
                _p2StorageViewGo = null;
                return false;
            }

            SanitizeClone(cloneInv);
            SyncInventorySlots(source, cloneInv);
            _p2StorageSourceInv = source;
            _p2StorageInv = cloneInv;

            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = new Vector2(430f, 0f);
            panel.localScale = Vector3.one;
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) SetLayerRecursively(panel, uiLayer);
            foreach (var g in _p2StorageViewGo.GetComponentsInChildren<Graphic>(true))
                g.raycastTarget = false;
            foreach (var s in cloneInv.allSlots) if (s != null) s.RefreshComponents();
            _p2StorageViewGo.SetActive(true);

            if (_p2HeldIcon != null) _p2HeldIcon.transform.SetAsLastSibling();
            if (_p2InvCursor != null) _p2InvCursor.SetAsLastSibling();
            LogV("[P2Storage] opened independent cloned storage inventory for P2");
            return true;
        }

        static void SyncInventorySlots(Inventory from, Inventory to)
        {
            if (from?.allSlots == null || to?.allSlots == null) return;
            int count = Mathf.Min(from.allSlots.Count, to.allSlots.Count);
            for (int i = 0; i < count; i++)
            {
                var src = from.allSlots[i];
                var dst = to.allSlots[i];
                if (src == null || dst == null) continue;
                dst.SetItem(src.itemInstance != null ? src.itemInstance.Clone() : null);
            }
        }

        static bool IsP1UsingStorage(Storage_Small storage)
        {
            if (storage == null) return false;
            var p1 = player1 != null ? player1 : ComponentManager<Network_Player>.Value;
            return p1 != null && p1.StorageManager != null && p1.StorageManager.currentStorage == storage;
        }

        static void SetP2StorageVisualOpen(Storage_Small storage, bool open)
        {
            if (storage != null && storage.anim != null) storage.anim.SetBool("IsOpen", open);
        }

        internal static void CloseP2Storage()
        {
            if (_p2StorageInv == null) return;
            var storage = _p2StorageObject;
            if (_p2StorageViewGo != null)
            {
                Object.Destroy(_p2StorageViewGo);
                _p2StorageViewGo = null;
                _p2StorageSourceInv = null;
                _p2StorageInv = null;
                _p2StorageObject = null;
                if (storage != null && !IsP1UsingStorage(storage)) SetP2StorageVisualOpen(storage, false);
                if (_p2BackpackOpen) CloseP2Backpack();
                LogV("[P2Storage] closed independent cloned storage inventory for P2");
                return;
            }
            _p2StorageInv = null;
            _p2StorageObject = null;
            if (storage != null && !IsP1UsingStorage(storage)) SetP2StorageVisualOpen(storage, false);
            if (_p2BackpackOpen) CloseP2Backpack();            
            LogV("[P2Storage] 关闭箱子");
        }

        
        internal static void TickP2Backpack()
        {
            if (!_p2BackpackOpen || _p2InvCursor == null) return;

            if (_p2BackpackView != null && !_p2BackpackView.gameObject.activeSelf) _p2BackpackView.gameObject.SetActive(true);
            if (!IsP2ResearchOpen && _p2CraftMenuGo != null && !_p2CraftMenuGo.activeSelf) _p2CraftMenuGo.SetActive(true);
            if (IsP2StorageOpen && _p2StorageViewGo != null && !_p2StorageViewGo.activeSelf) _p2StorageViewGo.SetActive(true);
            if (IsP2ResearchOpen && _p2ResearchCloneGo != null && !_p2ResearchCloneGo.activeSelf) _p2ResearchCloneGo.SetActive(true);

            var gp = GetP2BoundGamepad();
            if (gp == null) return;

            RefreshP2StorageView();

            
            Vector2 stick = gp.leftStick.ReadValue();
            if (stick.sqrMagnitude < 0.02f) stick = Vector2.zero;
            _p2InvCursorPos += stick * P2BpCursorSpeed * Time.unscaledDeltaTime;
            var canvasRT = _p2HudCanvas.transform as RectTransform;
            Vector2 half = canvasRT.rect.size * 0.5f;
            _p2InvCursorPos.x = Mathf.Clamp(_p2InvCursorPos.x, -half.x, half.x);
            _p2InvCursorPos.y = Mathf.Clamp(_p2InvCursorPos.y, -half.y, half.y);
            _p2InvCursor.anchoredPosition = _p2InvCursorPos;
            if (_p2HeldIcon != null && _p2HeldIcon.transform is RectTransform hrt)
                hrt.anchoredPosition = _p2InvCursorPos + new Vector2(34f, -34f);

            Vector2 cursorScreen = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, _p2InvCursor.position);
            TickP2Crafting(gp, cursorScreen);   
            if (_p2ResearchTable != null) TickP2Research(gp, cursorScreen);   
            
            if (_p2HoverSub == null && _p2HoverSkin == null && _p2HoverCraftCost == null && _p2HoverResearch == null)
                TickP2GridMagnet(gp, RectTransformUtility.WorldToScreenPoint(_p2UiCamera, _p2InvCursor.position));
            UpdateBpHover();

            
            bool hoveringItem = _p2Held != null
                || (_p2HoverSlot != null && _p2HoverSlot.itemInstance != null)
                || (_p2HoverHotbar >= 0 && _p2Hotbar != null && _p2HoverHotbar < _p2Hotbar.Length && _p2Hotbar[_p2HoverHotbar] != null);
            ShowBpHints(hoveringItem);

            bool lt = gp.leftTrigger.isPressed;   

            if (TickP2HeldOutsideDrop(gp))
            {
                _wasABp = gp.buttonSouth.isPressed;
                return;
            }
            
            
            bool a = gp.buttonSouth.isPressed;
            if (a && !_wasABp)
            {
                if (P2CraftTryQuickCraftHovered() || P2CraftTrySelectSkin() || P2CraftTrySelectHovered() || P2ResearchTryLearnHovered()) _aPending = false;   
                else if (lt) { ClickHoveredSlot(MoveMode.One); _aPending = false; }            
                else { _aPending = true; _aPressT = Time.unscaledTime; }                       
            }
            if (_aPending && a && !lt && Time.unscaledTime - _aPressT > P2HoldHalfDelay)
                { ClickHoveredSlot(MoveMode.Half); _aPending = false; }                        
            if (_aPending && (!a || lt))
                { ClickHoveredSlot(lt ? MoveMode.One : MoveMode.Whole); _aPending = false; }    
            _wasABp = a;

            
            if (gp.buttonNorth.wasPressedThisFrame) { if (lt) DropHovered(); else QuickMoveHovered(); }

            
            bool x = gp.buttonWest.isPressed;
            if (x && !_wasXBp) P2CraftTryCraft();   
            _wasXBp = x;

            
            if (gp.rightStickButton.wasPressedThisFrame) { P2CraftTrySelectHovered(); P2CraftTryCraft(); }
        }
        static bool _wasXBp;

        static void UpdateBpHover()
        {
            var pinv = ActiveP2BackpackInventory();
            if (pinv == null || _p2UiCamera == null) { ClearHoverSlot(); return; }
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, _p2InvCursor.position);

            Slot hitSlot = null; int hitHotbar = -1; Image hitImg = null;
            
            foreach (var s in pinv.allSlots)
            {
                if (s == null || s.slotType == SlotType.Hotbar) continue;
                var rt = s.rectTransform != null ? s.rectTransform : s.transform as RectTransform;
                if (rt != null && rt.gameObject.activeInHierarchy &&
                    RectTransformUtility.RectangleContainsScreenPoint(rt, screen, _p2UiCamera))
                { hitSlot = s; hitImg = s.GetComponent<Image>(); break; }
            }
            
            if (hitSlot == null && pinv.equipSlots != null)
                foreach (var s in pinv.equipSlots)
                {
                    if (s == null) continue;
                    var rt = s.rectTransform != null ? s.rectTransform : s.transform as RectTransform;
                    if (rt != null && rt.gameObject.activeInHierarchy &&
                        RectTransformUtility.RectangleContainsScreenPoint(rt, screen, _p2UiCamera))
                    { hitSlot = s; hitImg = s.GetComponent<Image>(); break; }
                }
            
            if (hitSlot == null && _p2StorageInv != null && _p2StorageInv.allSlots != null)
                foreach (var s in _p2StorageInv.allSlots)
                {
                    if (s == null) continue;
                    var rt = s.rectTransform != null ? s.rectTransform : s.transform as RectTransform;
                    if (rt != null && rt.gameObject.activeInHierarchy &&
                        RectTransformUtility.RectangleContainsScreenPoint(rt, screen, _p2UiCamera))
                    { hitSlot = s; hitImg = s.GetComponent<Image>(); break; }
                }
            
            if (hitSlot == null && _p2ResearchSlot != null)
            {
                var rt = ResearchSlotRect();
                if (rt != null && rt.gameObject.activeInHierarchy &&
                    RectTransformUtility.RectangleContainsScreenPoint(rt, screen, _p2UiCamera))
                { hitSlot = _p2ResearchSlot; hitImg = _p2ResearchSlot.GetComponent<Image>(); }
            }
            
            if (hitSlot == null && _p2HotbarSlotRects != null)
                for (int i = 0; i < _p2HotbarSlotRects.Length; i++)
                {
                    var rt = _p2HotbarSlotRects[i];
                    if (rt != null && rt.gameObject.activeInHierarchy &&
                        RectTransformUtility.RectangleContainsScreenPoint(rt, screen, _p2UiCamera))
                    { hitHotbar = i; hitImg = (_p2HotbarSlotBg != null && i < _p2HotbarSlotBg.Length) ? _p2HotbarSlotBg[i] : null; break; }
                }

            if (hitSlot == _p2HoverSlot && hitHotbar == _p2HoverHotbar) return;
            ClearHoverSlot();
            _p2HoverSlot = hitSlot; _p2HoverHotbar = hitHotbar; _p2HoverSlotBg = hitImg;
            if (hitImg != null) { _p2HoverPrevColor = hitImg.color; hitImg.color = P2SlotHoverTint; }

            
            
            Item_Base desc = null;
            if (hitSlot != null) desc = hitSlot.GetItemBase();
            else if (hitHotbar >= 0 && _p2Hotbar != null && hitHotbar < _p2Hotbar.Length && _p2Hotbar[hitHotbar] != null)
                desc = _p2Hotbar[hitHotbar].baseItem;
            pinv.SetItemDescription(desc);
        }

        
        static readonly List<RectTransform> _gridMagnetCands = new List<RectTransform>();
        static void TickP2GridMagnet(Gamepad gp, Vector2 cursorScreen)
        {
            var pr = _p2InvCursor.parent as RectTransform;
            if (pr == null) return;

            _gridMagnetCands.Clear();
            if (_p2HotbarSlotRects != null)
                foreach (var rt in _p2HotbarSlotRects) if (rt != null) _gridMagnetCands.Add(rt);
            var pinv = ActiveP2BackpackInventory();
            if (pinv?.allSlots != null)
                foreach (var s in pinv.allSlots)
                    if (s != null && s.slotType != SlotType.Hotbar)
                    { var rt = s.rectTransform ?? s.transform as RectTransform; if (rt != null) _gridMagnetCands.Add(rt); }
            if (pinv?.equipSlots != null)
                foreach (var s in pinv.equipSlots)
                    if (s != null) { var rt = s.rectTransform ?? s.transform as RectTransform; if (rt != null) _gridMagnetCands.Add(rt); }
            if (_p2StorageInv?.allSlots != null)
                foreach (var s in _p2StorageInv.allSlots)
                    if (s != null) { var rt = s.rectTransform ?? s.transform as RectTransform; if (rt != null) _gridMagnetCands.Add(rt); }
            if (_p2ResearchSlot != null) { var rrt = ResearchSlotRect(); if (rrt != null) _gridMagnetCands.Add(rrt); }
            if (_gridMagnetCands.Count == 0) return;

            float scale = (_p2HudCanvas != null && _p2HudCanvas.scaleFactor > 0f) ? _p2HudCanvas.scaleFactor : 1f;
            float radius = P2MagnetRadiusPx * scale;
            bool found = false, inside = false;
            float bestSq = radius * radius; Vector2 bestCenter = default, bestDiff = default;
            foreach (var rt in _gridMagnetCands)
            {
                if (!rt.gameObject.activeInHierarchy) continue;
                Vector2 c = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, rt.TransformPoint(rt.rect.center));
                if (!inside && RectTransformUtility.RectangleContainsScreenPoint(rt, cursorScreen, _p2UiCamera))
                { bestCenter = c; bestDiff = c - cursorScreen; found = inside = true; continue; }   
                if (inside) continue;
                float d = (c - cursorScreen).sqrMagnitude;
                if (d < bestSq) { bestSq = d; bestCenter = c; bestDiff = c - cursorScreen; found = true; }
            }
            if (!found) return;

            Vector2 stick = gp.leftStick.ReadValue();
            float escape = P2MagnetEscape * scale;
            if (bestDiff.sqrMagnitude < (stick * escape).sqrMagnitude) return;   
            float thr = P2MagnetCenterThr * scale;
            Vector2 target = (bestDiff.sqrMagnitude > thr * thr)
                ? cursorScreen + bestDiff.normalized * (P2MagnetPullSpeed * scale) * Time.unscaledDeltaTime
                : bestCenter;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(pr, target, _p2UiCamera, out var local))
            { _p2InvCursorPos = local; _p2InvCursor.anchoredPosition = local; }
        }

        static void ClearHoverSlot()
        {
            if (_p2HoverSlotBg != null) _p2HoverSlotBg.color = _p2HoverPrevColor;
            _p2HoverSlotBg = null; _p2HoverSlot = null; _p2HoverHotbar = -1;
            var pinv = ActiveP2BackpackInventory();
            if (pinv != null) pinv.SetItemDescription(null);   
        }

        static bool P2HeldIsOutsideItemSlots()
        {
            return _p2Held != null
                && _p2HoverSlot == null
                && _p2HoverHotbar < 0
                && _p2HoverSub == null
                && _p2HoverSkin == null
                && _p2HoverResearch == null;
        }

        static bool TickP2HeldOutsideDrop(Gamepad gp)
        {
            bool show = P2HeldIsOutsideItemSlots();
            if (!show)
            {
                ClearP2HeldDropPrompt();
                return false;
            }

            LocalizationParameters.itemX = P2ItemDisplayName(_p2Held);
            SetP2InteractPrompt("Confirm", Helper.GetTerm("Game/DropX", applyParameters: true));
            _p2HeldDropPromptShown = true;

            if (gp == null || !gp.buttonSouth.wasPressedThisFrame) return false;
            DropP2HeldStack();
            ClearP2HeldDropPrompt();
            return true;
        }

        static void ClearP2HeldDropPrompt()
        {
            if (!_p2HeldDropPromptShown) return;
            ClearP2InteractPrompt();
            _p2HeldDropPromptShown = false;
        }

        static string P2ItemDisplayName(ItemInstance item)
        {
            var baseItem = item != null ? item.baseItem : null;
            if (baseItem == null && item != null) baseItem = ItemManager.GetItemByIndex(item.UniqueIndex);
            var inv = baseItem != null ? baseItem.settings_Inventory : null;
            if (inv != null && !string.IsNullOrEmpty(inv.DisplayName)) return inv.DisplayName;
            return baseItem != null ? baseItem.UniqueName : "";
        }

        
        internal enum MoveMode { Whole, Half, One }
        static int CountFor(MoveMode m, int amt) =>
            m == MoveMode.Whole ? amt : m == MoveMode.Half ? Mathf.CeilToInt(amt * 0.5f) : 1;

        static void ClearP2HeldOrigin()
        {
            _p2HeldOriginSlot = null;
            _p2HeldOriginHotbar = -1;
        }

        static void RememberP2HeldOrigin(Slot slot, int hotbar)
        {
            _p2HeldOriginSlot = slot;
            _p2HeldOriginHotbar = hotbar;
        }

        static bool P2HeldOriginIsCurrent(Slot slot, int hotbar)
        {
            return (_p2HeldOriginSlot != null && _p2HeldOriginSlot == slot)
                || (_p2HeldOriginHotbar >= 0 && _p2HeldOriginHotbar == hotbar);
        }

        static bool TrySetP2HeldOrigin(ItemInstance item)
        {
            if (_p2HeldOriginSlot != null)
            {
                _p2HeldOriginSlot.SetItem(item);
                RefreshP2StorageView();
                return true;
            }
            if (_p2HeldOriginHotbar >= 0 && _p2Hotbar != null && _p2HeldOriginHotbar < _p2Hotbar.Length)
            {
                _p2Hotbar[_p2HeldOriginHotbar] = item;
                UpdateP2Hotbar();
                RefreshP2HeldItem();
                return true;
            }
            return false;
        }

        
        
        
        static void ClickHoveredSlot(MoveMode mode = MoveMode.Whole)
        {
            
            if (_p2HoverSlot is Slot_Equip eq) { ClickEquipSlot(eq); return; }

            System.Func<ItemInstance> get; System.Action<ItemInstance> set; System.Action refresh;
            Slot targetSlot = null;
            int targetHotbar = -1;
            if (_p2HoverSlot != null)
            {
                var viewSlot = _p2HoverSlot;
                var dataSlot = ResolveP2StorageSourceSlot(viewSlot) ?? viewSlot;
                targetSlot = dataSlot;
                get = () => dataSlot.itemInstance;
                set = v =>
                {
                    dataSlot.SetItem(v);
                    if (viewSlot != dataSlot)
                        viewSlot.SetItem(v != null ? v.Clone() : null);
                };
                refresh = () => { if (viewSlot != dataSlot) RefreshP2StorageView(); };
            }
            else if (_p2HoverHotbar >= 0)
            {
                int i = _p2HoverHotbar;
                targetHotbar = i;
                get = () => (_p2Hotbar != null && i < _p2Hotbar.Length) ? _p2Hotbar[i] : null;
                set = v => { if (_p2Hotbar != null && i < _p2Hotbar.Length) _p2Hotbar[i] = v; };
                refresh = () => { UpdateP2Hotbar(); RefreshP2HeldItem(); };
            }
            else return;

            var cell = get();
            if (_p2Held == null)
            {
                if (cell != null && cell.Amount > 0)
                {
                    int take = CountFor(mode, cell.Amount);
                    if (take >= cell.Amount)
                    {
                        _p2Held = cell;
                        RememberP2HeldOrigin(targetSlot, targetHotbar);
                        set(null);
                    }   
                    else { var h = cell.Clone(); h.Amount = take; _p2Held = h; ClearP2HeldOrigin(); cell.Amount -= take; set(cell); }
                }
            }
            else if (cell == null)
            {
                int put = CountFor(mode, _p2Held.Amount);
                if (put >= _p2Held.Amount) { set(_p2Held); _p2Held = null; ClearP2HeldOrigin(); }   
                else { var p = _p2Held.Clone(); p.Amount = put; set(p); _p2Held.Amount -= put; ClearP2HeldOrigin(); }
            }
            else if (cell.UniqueIndex == _p2Held.UniqueIndex && cell.settings_Inventory.Stackable)
            {
                int space = cell.settings_Inventory.StackSize - cell.Amount;
                if (space > 0)
                {
                    int move = Mathf.Min(space, CountFor(mode, _p2Held.Amount));
                    cell.Amount += move; set(cell);
                    _p2Held.Amount -= move; if (_p2Held.Amount <= 0) { _p2Held = null; ClearP2HeldOrigin(); }
                }
                else if (!P2HeldOriginIsCurrent(targetSlot, targetHotbar) && TrySetP2HeldOrigin(cell))
                {
                    set(_p2Held);
                    _p2Held = null;
                    ClearP2HeldOrigin();
                }
                else { var tmp = cell; set(_p2Held); _p2Held = tmp; ClearP2HeldOrigin(); }   
            }
            else if (!P2HeldOriginIsCurrent(targetSlot, targetHotbar) && TrySetP2HeldOrigin(cell))
            {
                set(_p2Held);
                _p2Held = null;
                ClearP2HeldOrigin();
            }
            else { var tmp = cell; set(_p2Held); _p2Held = tmp; ClearP2HeldOrigin(); }       

            refresh();
            
            
            
            SetHeldVisual(_p2Held);
        }

        
        
        static void ClickEquipSlot(Slot_Equip eq)
        {
            var shown = eq.itemInstance;   
            if (_p2Held == null)
            {
                
                if (shown != null && shown.Valid)
                {
                    var removed = P2EquipmentStore.UnEquip(P2EquipmentStore.TypeOf(shown));
                    _p2Held = removed ?? shown;
                    eq.itemInstance = null; eq.RefreshComponents();
                }
            }
            else if (P2EquipmentStore.IsEquippable(_p2Held))
            {
                var type = P2EquipmentStore.TypeOf(_p2Held);
                var inv2 = ActiveP2BackpackInventory();
                if (inv2?.equipSlots != null)
                    foreach (var s in inv2.equipSlots)
                        if (s != null && s != eq && s.itemInstance != null && s.itemInstance.Valid
                            && P2EquipmentStore.TypeOf(s.itemInstance) == type)
                        { s.itemInstance = null; s.RefreshComponents(); }
                var old = P2EquipmentStore.Equip(_p2Held);
                eq.itemInstance = P2EquipmentStore.GetEquipped(type);
                eq.RefreshComponents();
                _p2Held = old;
            }
            else return;   

            RefreshP2BackpackSlots();   
            SetHeldVisual(_p2Held);
        }

        
        static System.Collections.Generic.IEnumerable<Slot> NonHotbarSlots(PlayerInventory pinv)
        {
            if (pinv?.allSlots == null) yield break;
            foreach (var s in pinv.allSlots)
                if (s != null && s.slotType != SlotType.Hotbar) yield return s;
        }

        static bool SlotBelongs(Inventory inv, Slot slot)
        {
            if (inv?.allSlots == null) return false;
            foreach (var s in inv.allSlots) if (s == slot) return true;
            return false;
        }

        static Slot ResolveP2StorageSourceSlot(Slot viewSlot)
        {
            if (viewSlot == null || _p2StorageInv?.allSlots == null || _p2StorageSourceInv?.allSlots == null) return null;
            int count = Mathf.Min(_p2StorageInv.allSlots.Count, _p2StorageSourceInv.allSlots.Count);
            for (int i = 0; i < count; i++)
                if (_p2StorageInv.allSlots[i] == viewSlot)
                    return _p2StorageSourceInv.allSlots[i];
            return null;
        }

        static IEnumerable<Slot> P2StorageDataSlots()
        {
            if (_p2StorageSourceInv?.allSlots != null) return _p2StorageSourceInv.allSlots;
            if (_p2StorageInv?.allSlots != null) return _p2StorageInv.allSlots;
            return null;
        }

        static void RefreshP2StorageView()
        {
            if (_p2StorageSourceInv != null && _p2StorageInv != null)
                SyncInventorySlots(_p2StorageSourceInv, _p2StorageInv);
        }

        
        static void MoveStackIntoSlots(ItemInstance src, System.Collections.Generic.IEnumerable<Slot> slots)
        {
            if (src == null || slots == null) return;
            foreach (var s in slots)
            {
                if (src.Amount <= 0) return;
                if (s == null || s.IsEmpty || s.itemInstance == null) continue;
                if (s.itemInstance.UniqueIndex == src.UniqueIndex && s.itemInstance.settings_Inventory.Stackable)
                {
                    int space = s.itemInstance.settings_Inventory.StackSize - s.itemInstance.Amount;
                    if (space <= 0) continue;
                    int mv = Mathf.Min(space, src.Amount);
                    var ni = s.itemInstance.Clone(); ni.Amount += mv; s.SetItem(ni);
                    src.Amount -= mv;
                }
            }
            foreach (var s in slots)
            {
                if (src.Amount <= 0) return;
                if (s == null || !s.IsEmpty) continue;
                int put = Mathf.Min(src.settings_Inventory.StackSize, src.Amount);
                var ni = src.Clone(); ni.Amount = put; s.SetItem(ni);
                src.Amount -= put;
            }
        }

        
        // P2 背包放不下时的溢出去向。vanilla 的对应分支(Inventory.cs:350)要求
        // localPlayerInventory == this 才掉地面,而 P2 加物品的目标是克隆的背包视图
        // (P2_BackpackView),该判定恒假 -> 物品既不进手持栏也不掉地面,静默消失。
        // 另外 P2 的手持栏不是 vanilla 的 Slot 而是 _p2Hotbar 数组,FindSuitableSlot 看不见它。
        // 这里按原版语义补齐 P2 侧:先进 P2 自己的手持栏,仍放不下则掉在 P2 脚下。
        internal static void HandleP2AddOverflow(string uniqueItemName, ref int amount)
        {
            if (amount <= 0) return;
            var item = ItemManager.GetItemByName(uniqueItemName);
            if (item == null) return;
            var inst = new ItemInstance(item, amount, item.MaxUses);
            MoveStackIntoHotbar(inst);
            if (inst.Amount > 0 && player2 != null && player2.CameraTransform != null && player2.PersonController != null)
                Helper.DropItem(inst, player2.transform.position, player2.CameraTransform.forward,
                                player2.PersonController.HasRaftAsParent);
            amount = 0;
        }

        static void MoveStackIntoHotbar(ItemInstance src)
        {
            if (src == null || _p2Hotbar == null) return;
            for (int i = 0; i < _p2Hotbar.Length && src.Amount > 0; i++)
            {
                var c = _p2Hotbar[i];
                if (c == null || c.UniqueIndex != src.UniqueIndex || !c.settings_Inventory.Stackable) continue;
                int space = c.settings_Inventory.StackSize - c.Amount;
                if (space <= 0) continue;
                int mv = Mathf.Min(space, src.Amount); c.Amount += mv; src.Amount -= mv;
            }
            for (int i = 0; i < _p2Hotbar.Length && src.Amount > 0; i++)
            {
                if (_p2Hotbar[i] != null) continue;
                int put = Mathf.Min(src.settings_Inventory.StackSize, src.Amount);
                var ni = src.Clone(); ni.Amount = put; _p2Hotbar[i] = ni; src.Amount -= put;
            }
        }

        
        static void QuickMoveHovered()
        {
            var pinv = ActiveP2BackpackInventory();
            if (_p2HoverHotbar >= 0)   
            {
                var inst = (_p2Hotbar != null && _p2HoverHotbar < _p2Hotbar.Length) ? _p2Hotbar[_p2HoverHotbar] : null;
                if (inst == null) return;
                if (_p2StorageInv != null) MoveStackIntoSlots(inst, P2StorageDataSlots());
                else                       MoveStackIntoSlots(inst, NonHotbarSlots(pinv));
                if (inst.Amount <= 0) _p2Hotbar[_p2HoverHotbar] = null;
                RefreshP2StorageView();
                UpdateP2Hotbar(); RefreshP2HeldItem();
            }
            else if (_p2HoverSlot != null && _p2HoverSlot.itemInstance != null)
            {
                var dataSlot = ResolveP2StorageSourceSlot(_p2HoverSlot) ?? _p2HoverSlot;
                if (dataSlot.itemInstance == null) { RefreshP2StorageView(); return; }
                var work = dataSlot.itemInstance.Clone();
                bool chestSlot = _p2StorageInv != null && ResolveP2StorageSourceSlot(_p2HoverSlot) != null;
                if (chestSlot) { MoveStackIntoSlots(work, NonHotbarSlots(pinv)); if (work.Amount > 0) MoveStackIntoHotbar(work); }
                else if (_p2StorageInv != null) MoveStackIntoSlots(work, P2StorageDataSlots());  
                else MoveStackIntoHotbar(work);                                                    
                if (work.Amount <= 0) dataSlot.SetItem(null);
                else { var rem = dataSlot.itemInstance.Clone(); rem.Amount = work.Amount; dataSlot.SetItem(rem); }
                RefreshP2StorageView();
                UpdateP2Hotbar();
            }
        }

        
        // LT+Y 丢弃【整组】—— 对齐原版:P1 把物品拖出背包松手走的是
        // PlayerInventory.DropItem(Slot),丢的是整个 slot.itemInstance 而非逐个。
        // 旧实现每次只丢 1 个,一组 20 根木头要按 20 次。
        static void DropHovered()
        {
            if (player2 == null) return;
            if (_p2Held != null) { DropP2HeldStack(); return; }   // 光标上拿着的:复用整组丢弃路径

            ItemInstance toDrop = null;
            if (_p2HoverHotbar >= 0 && _p2Hotbar != null && _p2Hotbar[_p2HoverHotbar] != null)
            {
                toDrop = _p2Hotbar[_p2HoverHotbar].Clone();
                _p2Hotbar[_p2HoverHotbar] = null;
                UpdateP2Hotbar();
                RefreshP2HeldItem();   // 整格清空后若正是当前手持格,须立刻收起手上模型
            }
            else if (_p2HoverSlot != null && _p2HoverSlot.itemInstance != null)
            {
                var dataSlot = ResolveP2StorageSourceSlot(_p2HoverSlot) ?? _p2HoverSlot;
                var c = dataSlot.itemInstance;
                if (c == null) { RefreshP2StorageView(); return; }
                toDrop = c.Clone();
                dataSlot.SetItem(null);
                RefreshP2StorageView();
            }
            if (toDrop == null) return;
            Helper.DropItem(toDrop, player2.transform.position, player2.CameraTransform.forward,
                            player2.PersonController.HasRaftAsParent);
        }

        static void DropP2HeldStack()
        {
            if (player2 == null || _p2Held == null) return;
            var toDrop = _p2Held.Clone();
            _p2Held = null;
            ClearP2HeldOrigin();
            SetHeldVisual(null);
            Helper.DropItem(toDrop, player2.transform.position, player2.CameraTransform.forward,
                            player2.PersonController.HasRaftAsParent);
        }

        static void RefreshRealSlots(PlayerInventory pinv)
        {
            if (pinv.allSlots == null) return;
            foreach (var s in pinv.allSlots) if (s != null) s.RefreshComponents();
        }

        static void SetHeldVisual(ItemInstance it)
        {
            if (_p2HeldIcon == null) return;
            bool has = it != null && it.settings_Inventory != null && it.settings_Inventory.Sprite != null;
            _p2HeldIcon.enabled = has;
            if (has) _p2HeldIcon.sprite = it.settings_Inventory.Sprite;
            if (_p2HeldCount != null) _p2HeldCount.text = (has && it.Amount > 1) ? it.Amount.ToString() : "";
        }

        
        static void EnsureBpCursor()
        {
            if (_p2InvCursor != null) return;
            int uiLayer = LayerMask.NameToLayer("UI");
            var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");

            var held = new GameObject("P2_BpHeldIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var hrt = held.GetComponent<RectTransform>(); hrt.SetParent(_p2HudCanvas.transform, false);
            hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0.5f); hrt.sizeDelta = new Vector2(72f, 72f);
            _p2HeldIcon = held.GetComponent<Image>(); _p2HeldIcon.raycastTarget = false; _p2HeldIcon.preserveAspect = true; _p2HeldIcon.enabled = false;
            var hc = new GameObject("Count", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var hcrt = hc.GetComponent<RectTransform>(); hcrt.SetParent(hrt, false);
            hcrt.anchorMin = Vector2.zero; hcrt.anchorMax = Vector2.one; hcrt.offsetMin = Vector2.zero; hcrt.offsetMax = Vector2.zero;
            _p2HeldCount = hc.GetComponent<Text>(); _p2HeldCount.alignment = TextAnchor.LowerRight; _p2HeldCount.fontSize = 22; _p2HeldCount.color = Color.white; _p2HeldCount.raycastTarget = false;
            if (arial) _p2HeldCount.font = arial;

            var cur = new GameObject("P2_BpCursor", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _p2InvCursor = cur.GetComponent<RectTransform>(); _p2InvCursor.SetParent(_p2HudCanvas.transform, false);
            
            
            _p2InvCursor.anchorMin = _p2InvCursor.anchorMax = new Vector2(0.5f, 0.5f); _p2InvCursor.pivot = new Vector2(0.5f, 0.5f);
            _p2InvCursor.anchoredPosition = Vector2.zero;
            var curImg = cur.GetComponent<Image>(); curImg.raycastTarget = false;
            var spr = GetVanillaCursorSprite();
            if (spr != null) { curImg.sprite = spr; curImg.preserveAspect = true; _p2InvCursor.sizeDelta = new Vector2(48f, 48f); }
            else { curImg.color = new Color(1f, 1f, 1f, 0.95f); _p2InvCursor.sizeDelta = new Vector2(26f, 26f); }

            if (uiLayer >= 0) { SetLayerRecursively(hrt, uiLayer); SetLayerRecursively(_p2InvCursor, uiLayer); }

            
            
            var heldCanvas = held.AddComponent<Canvas>();
            heldCanvas.overrideSorting = true; heldCanvas.sortingOrder = 30000;
            var curCanvas = cur.AddComponent<Canvas>();
            curCanvas.overrideSorting = true; curCanvas.sortingOrder = 30001;

            _p2InvCursor.gameObject.SetActive(false);
        }

        
        static Sprite GetVanillaCursorSprite()
        {
            try
            {
                var ch = ComponentManager<CanvasHelper>.Value;
                var gc = ch != null ? ch.GetComponent<GamepadCursor>() : null;
                if (gc == null) return null;
                var f = typeof(GamepadCursor).GetField("cursorSprite",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return f?.GetValue(gc) as Sprite;
            }
            catch { return null; }
        }

        
        static RectTransform _p2BpHints;
        static string _p2BpHintLanguage;
        static readonly string[][] P2BackpackHintTermCandidates =
        {
            new[] { "Game/Move", "Controls/Move", "Menu/Move" },
            new[] { "Game/MoveHalf", "Controls/MoveHalf", "Menu/MoveHalf" },
            new[] { "Game/RepeatMove", "Controls/RepeatMove", "Menu/RepeatMove" },
            new[] { "Game/Drop", "Controls/Drop", "Menu/Drop" },
            new[] { "Game/QuickMove", "Controls/QuickMove", "Menu/QuickMove" },
            new[] { "Game/Back", "Controls/Back", "Menu/Back" }
        };
        static readonly string[] P2BackpackHintEnglish =
        {
            "Move",
            "Move half [Hold]",
            "Repeat move [Hold]",
            "Drop [Hold]",
            "Quick move",
            "Back"
        };
        static readonly string[] P2BackpackHintChinese =
        {
            "移动",
            "移动一半 [按住]",
            "重复移动 [按住]",
            "丢弃 [按住]",
            "快速移动",
            "返回"
        };

        static void EnsureBpHints()
        {
            if (_p2BpHints != null || _p2HudCanvas == null) return;
            Font arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            int uiLayer = LayerMask.NameToLayer("UI");

            var go = new GameObject("P2_BpHints", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>(); rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f); rt.pivot = new Vector2(1f, 0f);   
            rt.anchoredPosition = new Vector2(-24f, 24f); rt.sizeDelta = new Vector2(380f, 340f);
            var v = go.AddComponent<VerticalLayoutGroup>();
            v.childAlignment = TextAnchor.LowerRight; v.spacing = 10f;
            v.childForceExpandWidth = false; v.childForceExpandHeight = false;
            v.childControlWidth = true; v.childControlHeight = true;
            if (uiLayer >= 0) go.layer = uiLayer;
            var cv = go.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 29000;  
            _p2BpHints = rt;

            if (TryBuildVanillaBackpackHints(rt, arial, uiLayer))
            {
                go.SetActive(false);
                return;
            }

            BuildHintRow(rt, arial, uiLayer, LocalizedBpHint(0), null, "Confirm");
            BuildHintRow(rt, arial, uiLayer, LocalizedBpHint(1), null, "Confirm");
            BuildHintRow(rt, arial, uiLayer, LocalizedBpHint(2), "Context", "Confirm");
            BuildHintRow(rt, arial, uiLayer, LocalizedBpHint(3), "Context", "Menu");
            BuildHintRow(rt, arial, uiLayer, LocalizedBpHint(4), null, "Menu");
            BuildHintRow(rt, arial, uiLayer, LocalizedBpHint(5), null, "Cancel");

            go.SetActive(false);
        }

        static bool TryBuildVanillaBackpackHints(RectTransform parent, Font arial, int uiLayer)
        {
            var labels = ReadVanillaBackpackHintLabels();
            if (labels == null || labels.Count < 6) return false;

            BuildHintRow(parent, arial, uiLayer, labels[0], null, "Confirm");
            BuildHintRow(parent, arial, uiLayer, labels[1], null, "Confirm");
            BuildHintRow(parent, arial, uiLayer, labels[2], "Context", "Confirm");
            BuildHintRow(parent, arial, uiLayer, labels[3], "Context", "Menu");
            BuildHintRow(parent, arial, uiLayer, labels[4], null, "Menu");
            BuildHintRow(parent, arial, uiLayer, labels[5], null, "Cancel");
            LogV("[P2Backpack] using vanilla backpack localized hint text");
            return true;
        }

        static void ClearP2EquipmentSlots(PlayerInventory inventory)
        {
            if (inventory?.equipSlots == null) return;
            foreach (var slot in inventory.equipSlots)
            {
                if (slot == null || slot.itemInstance == null) continue;
                slot.itemInstance = null;
                slot.RefreshComponents();
            }
        }

        static List<string> ReadVanillaBackpackHintLabels()
        {
            var source = FindVanillaBackpackHintRoot();
            if (source == null) return null;

            var rows = new List<KeyValuePair<float, string>>();
            foreach (var text in source.GetComponentsInChildren<Text>(true))
            {
                if (text == null) continue;
                var value = text.text;
                if (string.IsNullOrWhiteSpace(value)) continue;
                value = value.Trim();
                if (value.Length > 48) continue;
                var rt = text.transform as RectTransform;
                float y = rt != null ? rt.position.y : text.transform.position.y;
                rows.Add(new KeyValuePair<float, string>(y, value));
            }

            rows.Sort((a, b) => b.Key.CompareTo(a.Key));
            var labels = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                var text = rows[i].Value;
                if (labels.Contains(text)) continue;
                labels.Add(text);
                if (labels.Count >= 6) break;
            }
            return labels;
        }

        static string LocalizedBpHint(int index)
        {
            string language = CurrentBpHintLanguage();
            if (_p2BpHintLanguage != language)
                _p2BpHintLanguage = language;

            if (index >= 0 && index < P2BackpackHintTermCandidates.Length)
            {
                var candidates = P2BackpackHintTermCandidates[index];
                for (int i = 0; i < candidates.Length; i++)
                {
                    var text = SafeTerm(candidates[i]);
                    if (!string.IsNullOrWhiteSpace(text) && text != candidates[i])
                        return text;
                }

                var found = FindTermByEnglishText(P2BackpackHintEnglish[index]);
                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }

            if (IsChineseLanguage())
                return P2BackpackHintChinese[index];
            return P2BackpackHintEnglish[index];
        }

        static string CurrentBpHintLanguage()
        {
            return I2.Loc.LocalizationManager.CurrentLanguageCode ?? I2.Loc.LocalizationManager.CurrentLanguage ?? string.Empty;
        }

        static string SafeTerm(string term)
        {
            try { return Helper.GetTerm(term); }
            catch { return null; }
        }

        static string FindTermByEnglishText(string english)
        {
            if (string.IsNullOrWhiteSpace(english)) return null;
            try
            {
                var terms = I2.Loc.LocalizationManager.GetTermsList();
                if (terms == null) return null;
                for (int i = 0; i < terms.Count; i++)
                {
                    var term = terms[i];
                    if (string.IsNullOrEmpty(term)) continue;
                    var en = I2.Loc.LocalizationManager.GetTranslation(term, overrideLanguage: "English");
                    if (!string.Equals(en, english, StringComparison.OrdinalIgnoreCase)) continue;
                    var translated = I2.Loc.LocalizationManager.GetTranslation(term);
                    if (!string.IsNullOrWhiteSpace(translated))
                        return translated;
                }
            }
            catch { }
            return null;
        }

        static bool IsChineseLanguage()
        {
            var code = I2.Loc.LocalizationManager.CurrentLanguageCode;
            if (!string.IsNullOrEmpty(code) && code.ToLowerInvariant().StartsWith("zh"))
                return true;
            var lang = I2.Loc.LocalizationManager.CurrentLanguage;
            return !string.IsNullOrEmpty(lang) && lang.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static RectTransform FindVanillaBackpackHintRoot()
        {
            RectTransform best = null;
            int bestScore = int.MinValue;
            TryFindVanillaBackpackHintRoot(_p2BackpackView != null ? _p2BackpackView.gameObject : null, ref best, ref bestScore);
            var p1Inv = ComponentManager<PlayerInventory>.Value;
            TryFindVanillaBackpackHintRoot(p1Inv != null ? p1Inv.gameObject : null, ref best, ref bestScore);
            return best;
        }

        static void TryFindVanillaBackpackHintRoot(GameObject owner, ref RectTransform best, ref int bestScore)
        {
            if (owner == null) return;
            var roots = owner.GetComponentsInChildren<RectTransform>(true);
            foreach (var rt in roots)
            {
                if (rt == null || rt.gameObject.name == "P2_BpHints" || rt.gameObject.name == "VanillaBackpackHints")
                    continue;

                int score = ScoreVanillaBackpackHintRoot(rt);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = rt;
                }
            }
        }

        static int ScoreVanillaBackpackHintRoot(RectTransform rt)
        {
            int textCount = 0;
            int glyphCount = 0;
            foreach (var text in rt.GetComponentsInChildren<Text>(true))
            {
                if (text != null && !string.IsNullOrWhiteSpace(text.text) && text.text.Length <= 48)
                    textCount++;
            }
            foreach (var image in rt.GetComponentsInChildren<Image>(true))
            {
                if (image != null && image.sprite != null)
                    glyphCount++;
            }
            if (textCount < 4 || glyphCount < 3 || textCount > 16 || glyphCount > 40)
                return int.MinValue;

            string path = TransformPath(rt).ToLowerInvariant();
            int score = textCount * 10 + glyphCount;
            if (path.Contains("gamepad") || path.Contains("controller")) score += 80;
            if (path.Contains("hint") || path.Contains("help") || path.Contains("control") || path.Contains("legend")) score += 60;
            if (path.Contains("backpack") || path.Contains("inventory")) score += 20;
            if (path.Contains("slot") || path.Contains("iteminfo") || path.Contains("tooltip") || path.Contains("craft")) score -= 100;
            return score;
        }

        static string TransformPath(Transform t)
        {
            if (t == null) return string.Empty;
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        static void BuildHintRow(RectTransform parent, Font arial, int uiLayer, string label, string modKey, string mainKey)
        {
            var row = new GameObject("Row", typeof(RectTransform));
            var rrt = row.GetComponent<RectTransform>(); rrt.SetParent(parent, false);
            var h = row.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleRight; h.spacing = 6f;
            h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            h.childControlWidth = true; h.childControlHeight = true;
            if (uiLayer >= 0) row.layer = uiLayer;

            AddHintText(rrt, arial, uiLayer, label);
            if (!string.IsNullOrEmpty(modKey)) { AddHintGlyph(rrt, uiLayer, modKey); AddHintText(rrt, arial, uiLayer, "+"); }
            AddHintGlyph(rrt, uiLayer, mainKey);
        }

        static void AddHintText(RectTransform parent, Font arial, int uiLayer, string text)
        {
            var ti = new GameObject("T", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ti.GetComponent<RectTransform>().SetParent(parent, false);
            var t = ti.GetComponent<Text>(); t.text = text; t.fontSize = 22; t.color = Color.white;
            t.alignment = TextAnchor.MiddleRight; t.raycastTarget = false; t.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (arial) t.font = arial;
            ti.AddComponent<LayoutElement>().minHeight = 36f;
            var sh = ti.AddComponent<Shadow>(); sh.effectColor = Color.black; sh.effectDistance = new Vector2(1f, -1f);
            if (uiLayer >= 0) ti.layer = uiLayer;
        }

        static void AddHintGlyph(RectTransform parent, int uiLayer, string key)
        {
            var gi = new GameObject("G", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gi.GetComponent<RectTransform>().SetParent(parent, false);
            var img = gi.GetComponent<Image>(); img.preserveAspect = true; img.raycastTarget = false;
            var sp = GlyphFor(key); if (sp != null) img.sprite = sp; else img.color = new Color(1f, 1f, 1f, 0.4f);
            var le = gi.AddComponent<LayoutElement>(); le.preferredWidth = 40f; le.preferredHeight = 40f;
            if (uiLayer >= 0) gi.layer = uiLayer;
        }

        internal static void ShowBpHints(bool show)
        {
            if (_p2BpHints != null && _p2BpHintLanguage != CurrentBpHintLanguage())
            {
                Object.Destroy(_p2BpHints.gameObject);
                _p2BpHints = null;
            }
            EnsureBpHints();
            if (_p2BpHints == null) return;
            if (_p2BpHints.gameObject.activeSelf != show) _p2BpHints.gameObject.SetActive(show);
        }

        internal static void DestroyP2Backpack()
        {
            if (_p2BackpackViewGo != null) Object.Destroy(_p2BackpackViewGo);
            if (_p2InvCursor != null) Object.Destroy(_p2InvCursor.gameObject);
            if (_p2HeldIcon != null) Object.Destroy(_p2HeldIcon.gameObject);
            if (_p2BpHints != null) Object.Destroy(_p2BpHints.gameObject);
            DestroyP2ResearchClone();
            DestroyP2ResearchPrompt();
            _p2BackpackViewGo = null; _p2BackpackView = null;
            _p2InvCursor = null; _p2HeldIcon = null; _p2HeldCount = null; _p2BpHints = null;
            _p2BackpackOpen = false; _p2Held = null; _p2HoverSlot = null; _p2HoverSlotBg = null;
            _p2HeldDropPromptShown = false;
        }
    }
}
