using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P2 建造系统(阶段1：核心闭环) —— 复用 P2 自己的 BlockCreator(每玩家一个)
        //
        //  原版 BlockCreator.Update 门控 playerNetwork.IsLocalPlayer，且输入走
        //  CustomInputConfig(读 PlayerInput[0]=P1)。做法(同 UseItemController 套路)：
        //   - Patch_BlockCreator_Update_P2：P2 的 BlockCreator.Update 期间临时把 P2 当本地玩家，
        //     并置 _p2BuildActive；结束还原。
        //   - Patch_CustomInputConfig_*_P2Build：_p2BuildActive 期间把建造按键("LMB"放置等)
        //     重定向到 P2 手柄(RT)。
        //   - colliderPrefabEnabler 原本仅本地玩家在 Start 创建 → 这里给 P2 补建(否则 Update 空引用)。
        //  阶段1：默认方块=地基(BlockCreator.Start 已 SetBlockTypeToBuild("Block_Foundation"))，
        //         先实现 幽灵预览 + RT 放置；旋转/拆除/建造菜单后续阶段。
        // ══════════════════════════════════════════════════════════════════
        // 折叠进 P2Mode：BlockCreator.Update 与 Hammer.Update 都走 P2OriginalScope.Build() → P2Mode=Build，
        //  故"建造/修理/加固期间"= P2Mode==Build(锤的旧标志 _p2HammerActive 并入此，已删字段)。读取点 Main._p2BuildActive 不变。
        internal static bool _p2BuildActive => P2Mode == P2OriginalMode.Build;
        // 移除工具特殊：RemovePlaceables.Update 对 P2 实例【始终】置真(含菜单内，用于屏蔽共享 DTM)，
        //  比 Build scope 窗口更宽 → 不能由 P2Mode 计算，保留为字段。
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

        // 幽灵方块当前是否可见(= 正对准一个可放置的有效面)。原版 SetGhostBlockVisibility(quadAtCursor!=null)
        //  控制 selectedBlock 的激活态；据此让 放置提示 只在"看向有效目标"时显示(按射线目标驱动)。
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
            _p2RemoveActive = false;   // (_p2BuildActive/_p2HammerActive 已折叠进 P2Mode，无字段可清)
            _p2LoadCircle = null;
            if (_p2BuildCostCursor != null) Object.Destroy(_p2BuildCostCursor.gameObject);
            _p2BuildCostCursor = null;
        }

        // 每帧(由 Runtime.Tick 调用)：持锤=展示原版捕获的建造提示(LT/旋转/模块选择)；持斧=拆除提示。
        internal static void TickP2Build()
        {
            var gp = GetP2BoundGamepad();
            if (gp == null) { ClearP2Prompts(); return; }

            var bc = GetP2BlockCreator();
            // 拿连接锚时仿 vanilla(ConnectStandWithThrowable 的 SelectUsable 把手持切走使锤子失活):
            // 视为未持锤 → 不进建造菜单/放置逻辑,LT 只归锚放回。
            bool hammer = bc != null && bc.gameObject.activeInHierarchy && !IsP2AnchorBusy;
            bool placeable = hammer && bc.aimingWithPlaceable;               // 持可放置物(椅子/床/作物盆…)→放置模式

            TickP2BuildCostCursor(bc, hammer);

            if (hammer && !placeable && !IsP2BackpackOpen) TickP2BuildMenuHold(gp);  // 按住 LT = 建造菜单(放置模式无菜单，对齐原版)
            if (_p2BuildMenuOpen) { ClearP2Prompts(); return; }
            if (GlobalBlocksP2 || IsP2BackpackOpen) { ClearP2Prompts(); return; }

            // 持可放置物：仅在【看向有效可放置面(幽灵可见)】时显示 RT放置+旋转提示(按射线目标驱动)。
            //  对准箱子(交互提示)或已放置可移除物(移除提示)时隐藏，避免重叠；看向无效处则不显示。
            if (placeable)
            {
                if (IsP2InteractPromptActive || IsP2RemovePromptActive || !IsP2PlaceableGhostVisible(bc)) ClearP2Prompts();
                else DrivePlaceablePrompts();
                return;
            }

            // 持锤：提示由【捕获原版 BlockCreator/Hammer.Update 发出的 DTM 提示】驱动(时机/逻辑与原版一致)。
            //  按射线目标优先：对准箱子(交互提示)或可移除的可放置物(移除提示)时，隐藏锤子建造提示条，
            //  只显示该目标对应的提示，避免锤子提示与目标提示并列。
            if (hammer) { if (IsP2InteractPromptActive || IsP2RemovePromptActive) ClearP2Prompts(); else PushCapturedPrompts(); return; }

            var axe = GetP2Axe();
            if (axe != null && axe.gameObject.activeInHierarchy) { DriveAxePrompts(); return; }

            // 持可食用消耗品(食物/水)：显示 吃/喝 提示(RT)。
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

            // 持杯/瓶等(非建造工具)：装水/倒水/喝吃提示由 FillWaterComponent 等捕获到 _capList →
            //  这里推送(新鲜则显示，过期则清空)。对准箱子时让位给交互提示。
            ExpireP2BaitPrompt();   // 鱼竿"选择诱饵"竖排提示过期则清(移开水面/收竿)
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

        // ── P2 拆除进度环(克隆原版 removeBlockRadialImage 到 P2 半屏中心) ──────
        static Image _p2LoadCircle;
        static int _p2LoadCircleLastSetFrame;   // vanilla 每帧刷则更新;停刷超1帧→对账隐藏(防蓄满卡住)

        static void EnsureP2LoadCircle()
        {
            if (_p2LoadCircle != null || _p2HudCanvas == null) return;
            var ch = ComponentManager<CanvasHelper>.Value;
            if (ch == null || ch.removeBlockRadialImage == null) return;
            var clone = Object.Instantiate(ch.removeBlockRadialImage.gameObject, _p2HudCanvas.transform);
            clone.name = "P2_LoadCircle";
            var anim = clone.GetComponent<Animator>(); if (anim != null) Object.Destroy(anim);   // 去掉动画器(脉冲)，只用 fillAmount
            var rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;            // 与准心重合(蓄力/进度环居中)
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

        // 为 P2 的 BlockCreator 补建 colliderPrefabEnabler(原版仅本地玩家在 Start 建)。
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
            if (_fBcColliderEnabler.GetValue(bc) != null) return;          // 已有
            if (player2 == null || player2.Camera == null) return;
            var prefab = _fBcColliderPrefab?.GetValue(bc) as Component;
            if (prefab == null) return;
            var inst = Object.Instantiate(prefab, player2.Camera.transform);
            inst.transform.localPosition = Vector3.zero;
            _fBcColliderEnabler.SetValue(bc, inst);
            // P2 强制第三人称：相机被拉远(~2.4m)，建造目标方块约在相机前 4m。
            //  colliderPrefabEnabler 是跟随相机的球形触发器，仅给【球内】方块挂上其细节碰撞体预制(含
            //  BuildQuad_Center/Quad_Foundation —— 可放置物要命中的那个 quad)。原版在 Awake 的本地玩家分支挂
            //  OnThirdpersonModelChange 委托来调 SetColliderSizeFromThirdpersonState；P2(克隆,非本地)跳过了，
            //  球半径停留在第一人称默认值(3.5m) → 4m 外的方块在球外 → BuildQuad_Center 没被挂 → 可放置物放不下。
            //  P2 恒为第三人称，这里直接把半径设为第三人称值(10m)。
            var cpe = inst as ColliderPrefabEnabler;
            if (cpe != null) cpe.SetColliderSizeFromThirdpersonState(true);
            // P2 的 buildMenu 未初始化(原版仅本地玩家 Awake 分支调 buildMenu.Initialize)。Hammer.Update 的
            //  HandleRepairingAndReinforcement 读 blockCreator.buildMenu.lastSelectedBuildabe → P2 每帧空引用刷屏。
            //  指给共享单例 BuildMenu(只读 lastSelectedBuildabe)即可消除 NRE，不调 Initialize(不抢 P1 绑定)。
            if (bc.buildMenu == null)
            {
                var bm = ComponentManager<BuildMenu>.Value;
                if (bm != null) bc.buildMenu = bm;
            }
            LogV("[P2Build] 为 P2 BlockCreator 补建 colliderPrefabEnabler(第三人称半径) + buildMenu 引用");
        }
    }
}
