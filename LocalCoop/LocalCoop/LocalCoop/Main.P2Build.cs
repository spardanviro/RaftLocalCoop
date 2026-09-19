using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        //
        
        
        
        
        
        
        
        
        
        
        
        
        internal static bool _p2BuildActive => P2Mode == P2OriginalMode.Build;
        
        
        internal static bool _p2RemoveActive;
        
        
        // 拿着连接的可扔锚时,底下的锤子/可放置工具仍在 → 会让 LT 同时开建造菜单(串线)。
        // 拿锚期间压制建造工具态,LT 只归锚放回;锚投掷 RT 走 p2UsingItemActive 路径不受影响。
        internal static bool P2BuildToolActive => (_p2BuildActive || _p2RemoveActive) && !IsP2AnchorBusy;

        static int _p2AnchorBusyFrame = -1;
        // 放锚那次 LT 的 release-gate(仿 vanilla Throwable.needToReleaseBeforeThrow):
        // 拿锚期间置真,放锚后锤子复活时若 LT 仍按着则不开建造菜单,须先松开 LT。
        internal static bool _p2BuildMenuBlockUntilLtRelease;
        // Patch_Anchor_Throwable_Update_P2 在 anchor_stand!=null(P2 持有连接锚)时每帧标记;允许 1 帧滞后。
        internal static void MarkP2AnchorBusy() { _p2AnchorBusyFrame = Time.frameCount; _p2BuildMenuBlockUntilLtRelease = true; }
        internal static bool IsP2AnchorBusy => _p2AnchorBusyFrame >= 0 && Time.frameCount - _p2AnchorBusyFrame <= 1;

        static BlockCreator _p2BlockCreator;
        static Hammer _p2Hammer;
        static Axe _p2Axe;
        static FieldInfo _fBcColliderEnabler, _fBcColliderPrefab, _fBcSelectedBlock;
        static FieldInfo _fBmCostCursor;
        static FieldInfo _fDtmDisplayTexts;
        static CostCollection _p2BuildCostCursor;

        
        
        internal static bool IsP2PlaceableGhostVisible(BlockCreator bc)
        {
            if (bc == null) return false;
            if (_fBcSelectedBlock == null)
                _fBcSelectedBlock = typeof(BlockCreator).GetField("selectedBlock", BindingFlags.Instance | BindingFlags.NonPublic);
            var sb = _fBcSelectedBlock?.GetValue(bc) as Block;
            return sb != null && sb.gameObject.activeInHierarchy;
        }

        internal static bool IsP2BuildRotateModifierHeld()
        {
            var gp = GetP2BoundGamepad();
            if (gp == null || !gp.dpad.right.isPressed) return false;
            if (IsP2BuildMenuOpen || IsP2BackpackOpen || IsP2MenuOpen) return false;

            var bc = GetP2BlockCreator();
            return bc != null &&
                   bc.gameObject.activeInHierarchy &&
                   bc.aimingWithPlaceable &&
                   IsP2PlaceableGhostVisible(bc);
        }

        internal static BlockCreator GetP2BlockCreator()
        {
            if (_p2BlockCreator != null) return _p2BlockCreator;
            if (player2 == null) return null;
            _p2BlockCreator = player2.GetComponentInChildren<BlockCreator>(true);
            return _p2BlockCreator;
        }

        internal static Hammer GetP2Hammer()
        {
            if (_p2Hammer != null) return _p2Hammer;
            if (player2 == null) return null;
            _p2Hammer = player2.GetComponentInChildren<Hammer>(true);
            return _p2Hammer;
        }

        internal static Axe GetP2Axe()
        {
            if (_p2Axe != null) return _p2Axe;
            if (player2 == null) return null;
            _p2Axe = player2.GetComponentInChildren<Axe>(true);
            return _p2Axe;
        }

        internal static void ResetP2Build()
        {
            _p2BlockCreator = null; _p2Hammer = null; _p2Axe = null;
            _p2RemoveActive = false;   
            _p2LoadCircle = null;
            if (_p2BuildCostCursor != null) Object.Destroy(_p2BuildCostCursor.gameObject);
            _p2BuildCostCursor = null;
        }

        
        internal static void TickP2Build()
        {
            var gp = GetP2BoundGamepad();
            if (gp == null) { ClearP2Prompts(); return; }

            var bc = GetP2BlockCreator();
            // 拿连接锚时仿 vanilla(ConnectStandWithThrowable 的 SelectUsable 把手持切走使锤子失活):
            // 视为未持锤 → 不进建造菜单/放置逻辑,LT 只归锚放回。
            bool hammer = bc != null && bc.gameObject.activeInHierarchy && !IsP2AnchorBusy;
            bool placeable = hammer && bc.aimingWithPlaceable;               

            TickP2BuildCostCursor(bc, hammer);

            if (hammer && !placeable && !IsP2BackpackOpen) TickP2BuildMenuHold(gp);  
            if (_p2BuildMenuOpen) { ClearP2Prompts(); return; }
            if (GlobalBlocksP2 || IsP2BackpackOpen) { ClearP2Prompts(); return; }

            
            
            if (placeable)
            {
                if (IsP2InteractPromptActive || IsP2RemovePromptActive || !IsP2PlaceableGhostVisible(bc)) ClearP2Prompts();
                else DrivePlaceablePrompts();
                return;
            }

            
            
            
            if (hammer) { if (IsP2InteractPromptActive || IsP2RemovePromptActive) ClearP2Prompts(); else PushCapturedPrompts(); return; }

            var axe = GetP2Axe();
            if (axe != null && axe.gameObject.activeInHierarchy) { DriveAxePrompts(); return; }

            
            var heldItem = GetP2HeldHotbarItem();
            var cc = heldItem?.baseItem?.settings_consumeable;
            if (cc != null && cc.FoodForm != FoodForm.None
                && (cc.HungerYield != 0f || cc.ThirstYield != 0f || cc.OxygenYield != 0f || cc.BonusHungerYield != 0f || cc.BonusThirstYield != 0f))
            {
                if (IsP2InteractPromptActive) { ClearP2Prompts(); return; }
                SetP2Prompts(new List<KeyValuePair<string, string>> {
                    new KeyValuePair<string, string>("Use", Helper.GetTerm(cc.FoodForm == FoodForm.Fluid ? "Game/Drink" : "Game/Eat"))
                });
                return;
            }

            
            
            ExpireP2BaitPrompt();   
            if (IsP2InteractPromptActive) { ClearP2Prompts(); return; }
            PushCapturedPrompts();
        }

        // BuildMenu normally owns this compact cost panel.  P2 gets a visual clone of
        // that panel, but its values must come from the independent P2 stores.
        static void TickP2BuildCostCursor(BlockCreator bc, bool hammer)
        {
            bool visible = hammer && !_p2BuildMenuOpen && !IsP2BackpackOpen && !IsP2MenuOpen &&
                           !IsP2InteractPromptActive && !IsP2RemovePromptActive;
            var item = visible && bc != null ? bc.GetCurrentBlockType() : null;
            visible = item != null && item.settings_buildable != null &&
                      !item.settings_buildable.Placeable && item.settings_recipe != null;

            if (!visible)
            {
                if (_p2BuildCostCursor != null && _p2BuildCostCursor.gameObject.activeSelf)
                    _p2BuildCostCursor.gameObject.SetActive(false);
                return;
            }

            EnsureP2BuildCostCursor();
            if (_p2BuildCostCursor == null) return;

            var costs = item.settings_recipe.NewCost;
            if (costs == null || costs.Length == 0)
            {
                if (_p2BuildCostCursor.gameObject.activeSelf)
                    _p2BuildCostCursor.gameObject.SetActive(false);
                return;
            }

            if (!_p2BuildCostCursor.gameObject.activeSelf)
                _p2BuildCostCursor.gameObject.SetActive(true);

            while (_p2BuildCostCursor.costBoxes.Count < costs.Length)
            {
                var prefab = _p2BuildCostCursor.costBoxPrefab;
                if (prefab == null) break;
                var box = Object.Instantiate(prefab, _p2BuildCostCursor.transform);
                var boxRt = box.transform as RectTransform;
                if (boxRt != null) boxRt.sizeDelta = _p2BuildCostCursor.prefabSizeDelta;
                _p2BuildCostCursor.costBoxes.Add(box);
            }

            for (int i = 0; i < _p2BuildCostCursor.costBoxes.Count; i++)
            {
                var box = _p2BuildCostCursor.costBoxes[i];
                bool show = i < costs.Length;
                if (box.gameObject.activeSelf != show) box.gameObject.SetActive(show);
                if (!show) continue;

                var cost = costs[i];
                box.SetRequiredItem(cost.items);
                box.SetRequiredAmount(cost.amount);

                // Match CostCollection.SetAmountInInventory: creative mode uses an
                // effectively infinite internal amount so the original disabled-box
                // component keeps its normal (white) background as well as its
                // required/required label.
                int amount = GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.unlimitedResources
                    ? int.MaxValue
                    : 0;
                if (amount == 0 && cost.items != null)
                    foreach (var material in cost.items)
                        if (material != null)
                            amount += P2InventoryStore.CountBackpackItem(material.UniqueIndex) +
                                      CountP2HotbarItem(material.UniqueIndex);
                box.SetAmount(amount);
            }
        }

        static void EnsureP2BuildCostCursor()
        {
            if (_p2BuildCostCursor != null || _p2HudCanvas == null) return;
            var menu = ComponentManager<BuildMenu>.Value;
            if (menu == null) return;
            if (_fBmCostCursor == null)
                _fBmCostCursor = typeof(BuildMenu).GetField("costColletionCursor", BindingFlags.Instance | BindingFlags.NonPublic);
            var source = _fBmCostCursor?.GetValue(menu) as CostCollection;
            if (source == null) return;

            var clone = Object.Instantiate(source.gameObject, _p2HudCanvas.transform, false);
            clone.name = "P2_BuildCostCursor";
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) SetLayerRecursively(clone.transform, uiLayer);
            _p2BuildCostCursor = clone.GetComponent<CostCollection>();
            clone.SetActive(false);
        }

        static void HideP2BuildCostCursor()
        {
            if (_p2BuildCostCursor != null && _p2BuildCostCursor.gameObject.activeSelf)
                _p2BuildCostCursor.gameObject.SetActive(false);
        }

        internal static void HideVanillaBuildCostCursor()
        {
            var menu = ComponentManager<BuildMenu>.Value;
            if (menu == null) return;
            if (_fBmCostCursor == null)
                _fBmCostCursor = typeof(BuildMenu).GetField("costColletionCursor", BindingFlags.Instance | BindingFlags.NonPublic);
            var cursor = _fBmCostCursor?.GetValue(menu) as CostCollection;
            if (cursor != null && cursor.gameObject.activeSelf)
                cursor.gameObject.SetActive(false);
        }

        // P1 keeps the native BuildMenu and DisplayText instances.  The target prompt
        // in slot zero is the authoritative signal that a world interaction owns the UI.
        internal static void ApplyP1HeldToolPromptPriority()
        {
            if (!IsP1TargetPromptActive()) return;

            var menu = ComponentManager<BuildMenu>.Value;
            if (menu != null)
            {
                if (_fBmCostCursor == null)
                    _fBmCostCursor = typeof(BuildMenu).GetField("costColletionCursor", BindingFlags.Instance | BindingFlags.NonPublic);
                var cursor = _fBmCostCursor?.GetValue(menu) as CostCollection;
                if (cursor != null && cursor.gameObject.activeSelf)
                    cursor.gameObject.SetActive(false);
            }

            var displayTextManager = ComponentManager<DisplayTextManager>.Value;
            if (displayTextManager == null) return;
            // Hammer/build placement hints occupy the auxiliary slots.  Do not let
            // them compete with the object action currently under P1's reticle.
            for (int i = 1; i <= 3; i++) displayTextManager.HideDisplayTexts(i);
        }

        static bool IsP1TargetPromptActive()
        {
            var displayTextManager = ComponentManager<DisplayTextManager>.Value;
            if (displayTextManager == null) return false;
            if (_fDtmDisplayTexts == null)
                _fDtmDisplayTexts = typeof(DisplayTextManager).GetField("displayTexts", BindingFlags.Instance | BindingFlags.NonPublic);
            var texts = _fDtmDisplayTexts?.GetValue(displayTextManager) as DisplayText[];
            return texts != null && texts.Length > 0 && texts[0] != null &&
                   texts[0].gameObject.activeInHierarchy && !texts[0].hide;
        }

        static void DriveAxePrompts()
        {
            var items = new List<KeyValuePair<string, string>>();
            if (player2 != null && player2.CameraTransform != null &&
                Physics.Raycast(player2.CameraTransform.position, player2.CameraTransform.forward,
                                out var hit, 5f, LayerMasks.MASK_Block))
            {
                var b = hit.transform.GetComponentInParent<Block>();
                if (b != null && b.buildableItem != null && !b.buildableItem.settings_buildable.Placeable)
                    items.Add(new KeyValuePair<string, string>("Use", Helper.GetTerm("Game/Remove")));
            }
            SetP2Prompts(items);
        }

        
        static Image _p2LoadCircle;
        static int _p2LoadCircleLastSetFrame;   // vanilla 每帧刷则更新;停刷超1帧→对账隐藏(防蓄满卡住)

        static void EnsureP2LoadCircle()
        {
            if (_p2LoadCircle != null || _p2HudCanvas == null) return;
            var ch = ComponentManager<CanvasHelper>.Value;
            if (ch == null || ch.removeBlockRadialImage == null) return;
            var clone = Object.Instantiate(ch.removeBlockRadialImage.gameObject, _p2HudCanvas.transform);
            clone.name = "P2_LoadCircle";
            var anim = clone.GetComponent<Animator>(); if (anim != null) Object.Destroy(anim);   
            var rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;            
            }
            _p2LoadCircle = clone.GetComponent<Image>() ?? clone.GetComponentInChildren<Image>();
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) SetLayerRecursively(clone.transform, uiLayer);
            clone.SetActive(false);
        }

        // 每帧对账:vanilla 工具(钩子/鱼竿/弓/长按收集/拆建)蓄力读条期间每帧调 SetLoadCircle→SetP2LoadCircle。
        // 一旦停调(蓄满释放/切换工具/vanilla 清零调用跑在 P2 scope 外没转到 P2)超过1帧,说明该隐藏了 →
        // 主动隐藏 P2 圈,防止读条圈卡在满值不消失。
        internal static void ReconcileP2LoadCircle()
        {
            if (_p2LoadCircle == null || !_p2LoadCircle.gameObject.activeSelf) return;
            if (Time.frameCount - _p2LoadCircleLastSetFrame >= 2)
                _p2LoadCircle.gameObject.SetActive(false);
        }

        internal static void SetP2LoadCircle(float value)
        {
            EnsureP2LoadCircle();
            if (_p2LoadCircle == null) return;
            bool on = value > 0f;
            if (_p2LoadCircle.gameObject.activeSelf != on) _p2LoadCircle.gameObject.SetActive(on);
            if (on) { _p2LoadCircle.fillAmount = value; _p2LoadCircleLastSetFrame = Time.frameCount; }
        }

        internal static void SetP2LoadCircle(bool on)
        {
            EnsureP2LoadCircle();
            if (_p2LoadCircle == null) return;
            if (_p2LoadCircle.gameObject.activeSelf != on) _p2LoadCircle.gameObject.SetActive(on);
        }

        
        internal static void EnsureP2BuildInit(BlockCreator bc)
        {
            if (bc == null) return;
            if (_fBcColliderEnabler == null)
            {
                var t = typeof(BlockCreator);
                _fBcColliderEnabler = t.GetField("colliderPrefabEnabler", BindingFlags.Instance | BindingFlags.NonPublic);
                _fBcColliderPrefab  = t.GetField("colliderPrefabEnablerPrefab", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            if (_fBcColliderEnabler == null) return;
            if (_fBcColliderEnabler.GetValue(bc) != null) return;          
            if (player2 == null || player2.Camera == null) return;
            var prefab = _fBcColliderPrefab?.GetValue(bc) as Component;
            if (prefab == null) return;
            var inst = Object.Instantiate(prefab, player2.Camera.transform);
            inst.transform.localPosition = Vector3.zero;
            _fBcColliderEnabler.SetValue(bc, inst);
            
            
            
            
            
            
            var cpe = inst as ColliderPrefabEnabler;
            if (cpe != null) cpe.SetColliderSizeFromThirdpersonState(true);
            
            
            
            if (bc.buildMenu == null)
            {
                var bm = ComponentManager<BuildMenu>.Value;
                if (bm != null) bc.buildMenu = bm;
            }
            LogV("[P2Build] 为 P2 BlockCreator 补建 colliderPrefabEnabler(第三人称半径) + buildMenu 引用");
        }
    }
}
