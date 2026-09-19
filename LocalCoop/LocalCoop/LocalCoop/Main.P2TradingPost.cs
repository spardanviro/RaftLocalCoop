using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    public static partial class Main
    {
        // ── P2 交易站 ───────────────────────────────────────────────────────
        // 克隆 vanilla TradingPostUI 面板,把其 "Panel" 子物体挂到 P2 半屏画布(_p2HudCanvas,
        // ScreenSpaceCamera 只渲染 P2 半屏 → P1 不受影响、可同时用自己的交易站)。走 _p2MenuOpen
        // 独立菜单机制(IsP2MenuOpen 门控自动生效),不碰共享 CanvasHelper 的 MenuType.TradingPost。
        //
        // 交互(仿 vanilla 手柄):虚拟光标(左摇杆移动)选中项目、右摇杆上下滚动列表、
        // LT/RT 切级别(tier)、A 选中条目/在购买键上按 A 购买、B 退出。
        // (LB/RB 买卖切换 + 出售流程为下一步。)
        // 购买/库存判定为权威:扣 P2 背包(P2InventoryScope 换入)+ P2 手持栏(_p2Hotbar),奖励进 P2。
        // vanilla RefreshUI 只驱动右侧详情视觉。

        struct P2TradeEntry
        {
            public SO_TradingPost_Buyable.Instance inst;   // 买模式
            public SO_TradingPost_Sellable sell;           // 卖模式
            public UI_Cost_Interactable ui;
        }

        static TradingPost   _p2TradingPost;
        static TradingPostUI _p2TradingUiClone;
        static GameObject    _p2TradingUiGo;
        static TradingPostUI _p2TradingUiSingleton;   // 克隆前的真单例,活化后钉回
        static RectTransform _p2TradingPanel;         // 从克隆里搬到 _p2HudCanvas 的 "Panel"
        static ScrollRect    _p2TradingScroll;        // 当前 tier 的滚动视图
        static TabGroup      _p2TierTabs;             // tier 级别 tab 组(Buy 下,含 Button_Tier1/2/3)
        static TabGroup      _p2BuySellTabs;          // 买/卖 tab 组(Panel 下)
        static readonly List<GameObject> _p2TradingHints = new List<GameObject>();   // 常显手柄提示(LB/RB/LT/RT/GamepadBack)
        static GameObject    _p2GamepadBuyHint;       // A购买提示(仅购买页)
        static GameObject    _p2GamepadSellHint;      // A出售提示(仅出售页)
        static Button        _p2TradingBuyBtn;        // 键鼠购买键(手柄模式隐藏)
        static Button        _p2TradingSellBtn;       // 键鼠出售键(手柄模式隐藏)
        static int           _p2TradingTier;
        static int           _p2TradingSel = -1;      // 已选中(详情)条目在当前列表里的下标
        static bool          _p2TradingSellMode;      // false=购买, true=出售
        static int           _p2BuyTabIdx = 0;        // 买/卖 tab 的实际 tabIndex(结构里解析)
        static int           _p2SellTabIdx = 1;
        static readonly List<P2TradeEntry> _p2TradingItems = new List<P2TradeEntry>();

        internal static bool IsP2TradingOpen => _p2TradingPost != null;

        // vanilla 交易 Panel 锚点是纵向 stretch(aMin.y=0,aMax.y=1),在"按宽匹配(match=0)"的半宽全高
        // 视口下会被纵向拉伸。改成按【设计尺寸】固定居中(尺寸由 Panel 当前锚点 + 画布参考分辨率算出,
        // 不写死),这样在 match=0 画布下等比缩放、自适应分辨率、不拉伸。P1/P2 共用。幂等(重复调用同尺寸)。
        internal static void FitTradingPanelDesignSize(RectTransform panel, float refW, float refH)
        {
            if (panel == null) return;
            if (refW < 1f) refW = 1920f;
            if (refH < 1f) refH = 1080f;
            float w = (panel.anchorMax.x - panel.anchorMin.x) * refW + panel.sizeDelta.x;
            float h = (panel.anchorMax.y - panel.anchorMin.y) * refH + panel.sizeDelta.y;
            if (w < 1f) w = Mathf.Abs(panel.sizeDelta.x);
            if (h < 1f) h = Mathf.Abs(panel.sizeDelta.y);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(w, h);
            panel.anchoredPosition = Vector2.zero;
        }

        static readonly FieldInfo s_tpBuyParents =
            typeof(TradingPostUI).GetField("buyContentParent", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_tpSelBuyable =
            typeof(TradingPostUI).GetField("selectedBuyableItem", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_tpSelSellable =
            typeof(TradingPostUI).GetField("selectedSellableItem", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_tpLocalPlayer =
            typeof(TradingPostUI).GetField("localPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_tpBuyButton =
            typeof(TradingPostUI).GetField("button_purchase", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_tpReqCosts =
            typeof(TradingPostUI).GetField("rightPanelRequiredCostsUI", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_tpSellParent =
            typeof(TradingPostUI).GetField("sellContentParent", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_tpSellButton =
            typeof(TradingPostUI).GetField("button_sell", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_uciHoverImage =
            typeof(UI_Cost_Interactable).GetField("hoverImage", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void OpenP2TradingPost(TradingPost post)
        {
            if (post == null || _p2TradingPost != null) return;
            if (_p2MenuOpen || IsP2BackpackOpen || _p2HudCanvas == null) return;

            var clone = EnsureP2TradingClone();
            if (clone == null) { ModEntry.Logger.Log("[P2Trading] 克隆交易面板失败"); return; }

            _p2TradingPost    = post;
            _p2TradingUiClone = clone;

            // localPlayer 提前钉成 P2(避免 Open/RefreshUI 读空);Start 稍后可能覆盖,仅影响视觉。
            try { s_tpLocalPlayer?.SetValue(clone, player2 != null ? (object)player2 : ComponentManager<Network_Player>.Value); }
            catch (Exception e) { LogV("[P2Trading] set localPlayer ex: " + e.Message); }

            clone.gameObject.SetActive(true);
            // 激活可能触发被延迟的克隆 Awake(把单例劫持成克隆),钉回真单例。
            if (_p2TradingUiSingleton != null) ComponentManager<TradingPostUI>.Value = _p2TradingUiSingleton;

            try { clone.Open(post); }
            catch (Exception e) { ModEntry.Logger.Log("[P2Trading] Open ex: " + e.Message); }

            _p2TradingTier = 0;   // 开菜单固定从级别1(仿 vanilla)
            _p2TradingSel = -1;
            _p2TradingSellMode = false;   // 开菜单默认购买页
            ActivateP2TradingContent();
            SyncP2TradingReputation();          // 共享 P1 声誉/等级(解锁 tier)

            // 把 "Panel" 从克隆(自带 ScreenSpaceOverlay 画布=会覆盖整个屏幕影响 P1)搬到 _p2HudCanvas
            // (ScreenSpaceCamera 只渲染 P2 半屏),并改成固定尺寸居中(修纵向拉伸)。
            var panelT = clone.transform.Find("Panel") as RectTransform;
            if (panelT != null)
            {
                var srcScaler = clone.GetComponent<CanvasScaler>();
                float refW = srcScaler != null ? srcScaler.referenceResolution.x : 1920f;
                float refH = srcScaler != null ? srcScaler.referenceResolution.y : 1080f;
                panelT.SetParent(_p2HudCanvas.transform, false);
                // 按设计尺寸固定居中(不写死);_p2HudCanvas 是 match=0 → 等比缩放自适应 P2 半屏分辨率。
                FitTradingPanelDesignSize(panelT, refW, refH);
                panelT.localScale = Vector3.one;
                int uiLayer = LayerMask.NameToLayer("UI");
                if (uiLayer >= 0) SetLayerRecursively(panelT, uiLayer);
                foreach (var g in panelT.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                panelT.SetAsLastSibling();
                _p2TradingPanel = panelT;
            }

            SetupP2TradingGamepadUi();
            P2TradingSelectTier(0);   // 选中级别1(切内容+高亮)+ 收集条目 + 布局

            // 虚拟光标(复用背包光标)
            EnsureBpCursor();
            _p2InvCursorPos = Vector2.zero;
            if (_p2InvCursor != null) { _p2InvCursor.gameObject.SetActive(true); _p2InvCursor.anchoredPosition = Vector2.zero; _p2InvCursor.SetAsLastSibling(); }

            _p2MenuOpen         = true;
            _p2MenuType         = MenuType.TradingPost;
            _p2MenuCanvasHelper = ComponentManager<CanvasHelper>.Value;

            ComponentManager<SoundManager>.Value?.PlayUI_OpenMenu();
            ConsumeP2InteractThisFrame();
            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(false);
            LogV($"[P2Trading] 打开交易站(tier={_p2TradingTier}, 条目={_p2TradingItems.Count})");
        }

        static TradingPostUI EnsureP2TradingClone()
        {
            if (_p2TradingUiClone != null) return _p2TradingUiClone;
            var src = ComponentManager<TradingPostUI>.Value;
            if (src == null || _p2HudCanvas == null) return null;

            _p2TradingUiSingleton = src;
            bool wasActive = src.gameObject.activeSelf;
            _p2TradingUiGo = UnityEngine.Object.Instantiate(src.gameObject, _p2HudCanvas.transform, false);
            _p2TradingUiGo.name = "P2_TradingPostUI";
            SanitizeClonedUiRoot(_p2TradingUiGo);
            // 只删无依赖的 GraphicRaycaster(防 P1 鼠标命中 P2 面板)。不动 Canvas(CanvasScaler/
            // GraphicRaycaster 依赖它,Destroy 会报错;且不给它 overrideSorting —— 否则 Overlay 画布会变
            // 全屏覆盖层污染 P1)。真正显示的 "Panel" 会在 Open 时搬到 _p2HudCanvas,克隆根画布不再渲染内容。
            foreach (var rc in _p2TradingUiGo.GetComponentsInChildren<GraphicRaycaster>(true)) UnityEngine.Object.Destroy(rc);
            src.gameObject.SetActive(wasActive);
            // 克隆 Awake 会把 ComponentManager<TradingPostUI>.Value 劫持成克隆,钉回真单例。
            ComponentManager<TradingPostUI>.Value = src;

            _p2TradingUiClone = _p2TradingUiGo.GetComponent<TradingPostUI>();
            if (_p2TradingUiClone == null)
            {
                UnityEngine.Object.Destroy(_p2TradingUiGo);
                _p2TradingUiGo = null;
                return null;
            }
            _p2TradingUiGo.SetActive(false);
            LogV("[P2Trading] 克隆交易面板已创建");
            return _p2TradingUiClone;
        }

        internal static void CloseP2TradingPost()
        {
            if (_p2TradingPost == null && _p2TradingUiGo == null) return;
            if (_p2TradingPanel != null) UnityEngine.Object.Destroy(_p2TradingPanel.gameObject);
            if (_p2TradingUiGo != null) UnityEngine.Object.Destroy(_p2TradingUiGo);
            if (_p2InvCursor != null) _p2InvCursor.gameObject.SetActive(false);
            _p2TradingUiGo = null; _p2TradingUiClone = null; _p2TradingPost = null;
            _p2TradingPanel = null; _p2TradingScroll = null;
            _p2TierTabs = null; _p2BuySellTabs = null;
            _p2TradingBuyBtn = null; _p2TradingSellBtn = null;
            _p2GamepadBuyHint = null; _p2GamepadSellHint = null;
            _p2TradingHints.Clear();
            _p2TradingItems.Clear();
            _p2TradingSel = -1; _p2TradingTier = 0; _p2TradingSellMode = false;
            LogV("[P2Trading] 关闭交易站");
        }

        // vanilla 关菜单时 Panel 及其内容都是 inactive → 激活 Panel + 买列表各 tier Content 的整条祖先链
        // (引用均为内部,克隆已重映射,不碰 P1)。
        static void ActivateP2TradingContent()
        {
            var clone = _p2TradingUiClone;
            if (clone == null) return;
            var panel = clone.transform.Find("Panel");
            if (panel != null) panel.gameObject.SetActive(true);
            var parents = s_tpBuyParents?.GetValue(clone) as Transform[];
            if (parents != null)
                foreach (var bp in parents)
                {
                    var t = bp;
                    while (t != null && t != clone.transform)
                    {
                        if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                        t = t.parent;
                    }
                }
        }

        // 共享声誉/经验:P2 克隆的声誉镜像 P1 单例的声誉(设 CurrentReputation 会重算 CurrentTier +
        // 刷新声誉条 UI)→ P2 的等级/tier 解锁与 P1 一致,买得动。P2 买东西不改声誉(vanilla 只卖才加)。
        static void SyncP2TradingReputation()
        {
            if (_p2TradingUiClone == null || _p2TradingUiSingleton == null) return;
            int rep = _p2TradingUiSingleton.CurrentReputation;
            if (_p2TradingUiClone.CurrentReputation != rep)
            {
                try { _p2TradingUiClone.CurrentReputation = rep; }
                catch (Exception e) { LogV("[P2Trading] rep sync ex: " + e.Message); }
            }
        }

        // 解析 tier / 买卖 两个 vanilla TabGroup(切内容+高亮由它管);禁掉其 Update(否则读 P1 的
        // LT/RT/LB/RB 切我们的克隆),激活手柄键位提示,隐藏键鼠购买键。
        static void SetupP2TradingGamepadUi()
        {
            var clone = _p2TradingUiClone;
            if (clone == null) return;
            // 注意:"Panel" 此时已被搬到 _p2HudCanvas(不再是 clone 子级)→ 必须从 _p2TradingPanel 找
            // TabGroup,否则找不到(→ 之前 tier 不切换/高亮不动/三 tier 叠一起的回归根因)。
            var panel = _p2TradingPanel != null ? (Transform)_p2TradingPanel : clone.transform.Find("Panel");
            if (panel == null) return;
            _p2TierTabs = null; _p2BuySellTabs = null;
            foreach (var tg in panel.GetComponentsInChildren<TabGroup>(true))
            {
                var pn = tg.transform.parent != null ? tg.transform.parent.name : "";
                if (pn == "Buy") _p2TierTabs = tg;
                else if (pn == "Panel") _p2BuySellTabs = tg;
                tg.enabled = false;   // 别让 vanilla Update 读 P1 输入切我们的克隆
            }
            // 解析买/卖 tab 的实际 tabIndex(按哪个 tab 内容里含 sellContentParent 判定 sell,另一个为 buy)
            _p2SellTabIdx = 1; _p2BuyTabIdx = 0;
            var sellParentT = s_tpSellParent?.GetValue(clone) as Transform;
            if (_p2BuySellTabs != null && _p2BuySellTabs.tabButtons != null && sellParentT != null)
                foreach (var tb in _p2BuySellTabs.tabButtons)
                    if (tb != null && tb.Tab != null && sellParentT.IsChildOf(tb.Tab.transform))
                    { _p2SellTabIdx = tb.tabIndex; break; }
            if (_p2BuySellTabs != null && _p2BuySellTabs.tabButtons != null)
                foreach (var tb in _p2BuySellTabs.tabButtons)
                    if (tb != null && tb.tabIndex != _p2SellTabIdx) { _p2BuyTabIdx = tb.tabIndex; break; }
            _p2TradingHints.Clear();
            _p2GamepadBuyHint = null; _p2GamepadSellHint = null;
            if (panel != null)
                foreach (var t in panel.GetComponentsInChildren<Transform>(true))
                {
                    switch (t.name)
                    {
                        case "LB": case "RB": case "LT": case "RT": case "GamepadBack":
                            _p2TradingHints.Add(t.gameObject); break;
                        case "GamepadBuy":  _p2GamepadBuyHint = t.gameObject; break;
                        case "GamepadSell": _p2GamepadSellHint = t.gameObject; break;
                    }
                }
            _p2TradingBuyBtn  = s_tpBuyButton?.GetValue(clone) as Button;   // 键鼠购买/出售键 → 手柄模式隐藏
            _p2TradingSellBtn = s_tpSellButton?.GetValue(clone) as Button;
            EnsureP2TradingGamepadHints();
        }

        // vanilla TradingPostUI.ControlsChangedEvent 按 P1(键鼠)反复隐藏这些 gamepad 提示 → 每帧重开顶回去,
        // 并持续隐藏键鼠购买键(手柄模式改选中按 A 直接买)。
        static void EnsureP2TradingGamepadHints()
        {
            foreach (var go in _p2TradingHints)
                if (go != null && !go.activeSelf) go.SetActive(true);
            // A购买/A出售 提示按当前页显隐(避免重合)
            if (_p2GamepadBuyHint != null && _p2GamepadBuyHint.activeSelf == _p2TradingSellMode)
                _p2GamepadBuyHint.SetActive(!_p2TradingSellMode);
            if (_p2GamepadSellHint != null && _p2GamepadSellHint.activeSelf != _p2TradingSellMode)
                _p2GamepadSellHint.SetActive(_p2TradingSellMode);
            // 键鼠购买/出售键 → 手柄模式隐藏
            if (_p2TradingBuyBtn != null && _p2TradingBuyBtn.gameObject.activeSelf)
                _p2TradingBuyBtn.gameObject.SetActive(false);
            if (_p2TradingSellBtn != null && _p2TradingSellBtn.gameObject.activeSelf)
                _p2TradingSellBtn.gameObject.SetActive(false);
        }

        // 用 vanilla 的 tier TabGroup 选中某级别(切内容+高亮),再收集条目 + 强制布局(修首帧选不中)。
        static void P2TradingSelectTier(int tier)
        {
            tier = Mathf.Clamp(tier, 0, 2);
            _p2TradingTier = tier;
            _p2TradingSel = -1;
            if (_p2TierTabs != null && _p2TierTabs.tabButtons != null)
            {
                try
                {
                    TabButton target = null;
                    foreach (var tb in _p2TierTabs.tabButtons)
                        if (tb != null && tb.tabIndex == tier) { target = tb; break; }
                    // 切换时先取消旧 tab 高亮(OpenTab 不会 exit 旧的)
                    var prev = _p2TierTabs.SelectedTabButton;
                    if (prev != null && prev != target) prev.OnPointerExit(forceExit: true);
                    // forceOpen=true:即使已是当前 tab 也强制重走 OnPointerEnter → 修开菜单首帧不高亮
                    // (SelectTab→OpenTab 默认 forceOpen=false,SelectedTabButton.Tab==tab 时提前返回跳过高亮)
                    if (target != null) _p2TierTabs.OpenTab(target.Tab, forceOpen: true);
                }
                catch (Exception e) { LogV("[P2Trading] OpenTab ex: " + e.Message); }
            }
            // 可靠兜底:SelectTab 首帧偶发不生效(TabGroup 初始化时序)→ 手动按 "Buy Tier{N}" 强制
            // 只显当前 tier(内容切换可靠;SelectTab 仍负责顶部高亮)。
            EnforceP2TradingTierVisibility();
            BuildP2TradingTierItems();
            if (_p2TradingScroll != null && _p2TradingScroll.content != null)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_p2TradingScroll.content);
            }
            RefreshP2TradingSelection();
        }

        static void EnforceP2TradingTierVisibility()
        {
            var parents = s_tpBuyParents?.GetValue(_p2TradingUiClone) as Transform[];
            if (parents == null) return;
            for (int t = 0; t < parents.Length; t++)
            {
                if (parents[t] == null) continue;
                var sec = parents[t];   // Content → Viewport → Scroll View → Buy TierN(上 3 层)
                for (int k = 0; k < 3 && sec != null; k++) sec = sec.parent;
                if (sec != null && sec.gameObject.activeSelf != (t == _p2TradingTier))
                    sec.gameObject.SetActive(t == _p2TradingTier);
            }
        }

        // 收集当前 tier 的购买条目 + 缓存该 tier 的滚动视图。
        static void BuildP2TradingTierItems()
        {
            _p2TradingItems.Clear();
            _p2TradingScroll = null;
            var post = _p2TradingPost; var clone = _p2TradingUiClone;
            if (post == null || clone == null) return;
            var parents = s_tpBuyParents?.GetValue(clone) as Transform[];
            if (parents == null || _p2TradingTier < 0 || _p2TradingTier >= parents.Length) return;
            var parent = parents[_p2TradingTier];
            if (parent == null) return;

            _p2TradingScroll = parent.GetComponentInParent<ScrollRect>();

            var uis = parent.GetComponentsInChildren<UI_Cost_Interactable>(true);
            var insts = new List<SO_TradingPost_Buyable.Instance>();
            if (post.buyableItems != null)
                foreach (var b in post.buyableItems)
                    if (b != null && (int)b.tier == _p2TradingTier) insts.Add(b);

            int n = Mathf.Min(insts.Count, uis != null ? uis.Length : 0);
            for (int i = 0; i < n; i++)
                _p2TradingItems.Add(new P2TradeEntry { inst = insts[i], ui = uis[i] });
        }

        static P2TradeEntry CurrentP2TradeEntry()
        {
            if (_p2TradingSel >= 0 && _p2TradingSel < _p2TradingItems.Count) return _p2TradingItems[_p2TradingSel];
            return default;
        }

        // 设置右侧详情为当前已选条目(不做高亮 —— 高亮跟随光标 hover)。
        static void RefreshP2TradingSelection()
        {
            var clone = _p2TradingUiClone;
            if (clone == null) return;
            var entry = CurrentP2TradeEntry();
            try { s_tpSelBuyable?.SetValue(clone, _p2TradingSellMode ? null : entry.inst); } catch { }
            try { s_tpSelSellable?.SetValue(clone, _p2TradingSellMode ? entry.sell : null); } catch { }
            try { clone.RefreshUI(); }
            catch (Exception e) { LogV("[P2Trading] RefreshUI ex: " + e.Message); }
            // vanilla RefreshUI 用 localPlayer.Inventory(=P1)算数量/可用性 → 用 P2 实际数量覆盖。
            if (_p2TradingSellMode) OverrideP2SellDisplay(entry.sell);
            else                    OverrideP2TradingCostDisplay(entry.inst);
        }

        static int P2TradingCountItem(Item_Base item)
        {
            if (item == null) return 0;
            return P2InventoryStore.CountBackpackItem(item.UniqueIndex) + CountP2HotbarItem(item.UniqueIndex);
        }

        static void OverrideP2TradingCostDisplay(SO_TradingPost_Buyable.Instance inst)
        {
            var clone = _p2TradingUiClone;
            if (clone == null || inst == null) return;

            var costUIs = s_tpReqCosts?.GetValue(clone) as UI_Cost[];
            if (costUIs != null && inst.cost != null)
                for (int i = 0; i < costUIs.Length; i++)
                {
                    if (costUIs[i] == null || !costUIs[i].gameObject.activeSelf) continue;
                    if (i >= inst.cost.Length || inst.cost[i] == null || inst.cost[i].item == null) continue;
                    int have = P2InventoryStore.CountBackpackItem(inst.cost[i].item.UniqueIndex)
                             + CountP2HotbarItem(inst.cost[i].item.UniqueIndex);
                    try { costUIs[i].Refresh(inst.cost[i], have); } catch { }
                }

            var btn = s_tpBuyButton?.GetValue(clone) as Button;
            if (btn != null)
                btn.interactable = inst.stock > 0
                    && (int)clone.CurrentTier >= (int)inst.tier
                    && P2TradingCanAfford(inst.cost);
        }

        static void HighlightP2Trading(UI_Cost_Interactable sel)
        {
            if (_p2TradingPanel == null) return;
            foreach (var ui in _p2TradingPanel.GetComponentsInChildren<UI_Cost_Interactable>(true))
            {
                var hi = s_uciHoverImage?.GetValue(ui) as GameObject;
                if (hi != null) hi.SetActive(ui == sel);
            }
        }

        static void P2TradingCycleTier(int dir)
        {
            int t = Mathf.Clamp(_p2TradingTier + dir, 0, 2);
            if (t == _p2TradingTier) return;
            // 尊重锁定的等级(声誉不足则对应 tab 不可交互)
            if (_p2TierTabs != null && _p2TierTabs.tabButtons != null
                && t < _p2TierTabs.tabButtons.Length && _p2TierTabs.tabButtons[t] != null
                && !_p2TierTabs.tabButtons[t].IsInteractable) return;
            P2TradingSelectTier(t);
            ComponentManager<SoundManager>.Value?.PlayUI_OpenMenu();
        }

        static int P2TradingHoverIndex(Vector2 cursorScreen)
        {
            for (int i = 0; i < _p2TradingItems.Count; i++)
            {
                var ui = _p2TradingItems[i].ui;
                if (ui == null) continue;
                var rt = ui.transform as RectTransform;
                if (rt != null && rt.gameObject.activeInHierarchy
                    && RectTransformUtility.RectangleContainsScreenPoint(rt, cursorScreen, _p2UiCamera))
                    return i;
            }
            return -1;
        }

        internal static void TickP2TradingPost()
        {
            var gp = GetP2BoundGamepad();
            if (gp == null || _p2InvCursor == null) return;
            if (_p2TradingPanel != null && !_p2TradingPanel.gameObject.activeSelf) _p2TradingPanel.gameObject.SetActive(true);
            SyncP2TradingReputation();          // P1 卖东西升级时 P2 实时同步(解锁 tier + 刷新声誉条)
            EnsureP2TradingGamepadHints();      // 顶回被 P1 键鼠方案隐藏的手柄提示

            // B 退出(先挂起退出帧的蹲下误触)
            if (gp.buttonEast.wasPressedThisFrame)
            {
                SuppressP2CrouchOnExit();
                CloseP2Menu();
                return;
            }

            // LB/RB 切 购买/出售 页
            if (gp.leftShoulder.wasPressedThisFrame)        P2TradingSetSellMode(false);
            else if (gp.rightShoulder.wasPressedThisFrame)  P2TradingSetSellMode(true);

            // LT/RT 切级别(tier) —— 仅购买页
            if (!_p2TradingSellMode)
            {
                if (gp.leftTrigger.wasPressedThisFrame)       P2TradingCycleTier(-1);
                else if (gp.rightTrigger.wasPressedThisFrame)  P2TradingCycleTier(1);
            }

            // 虚拟光标(左摇杆)
            Vector2 stick = gp.leftStick.ReadValue();
            if (stick.sqrMagnitude < 0.02f) stick = Vector2.zero;
            _p2InvCursorPos += stick * P2BpCursorSpeed * Time.unscaledDeltaTime;
            var canvasRT = _p2HudCanvas.transform as RectTransform;
            Vector2 half = canvasRT.rect.size * 0.5f;
            _p2InvCursorPos.x = Mathf.Clamp(_p2InvCursorPos.x, -half.x, half.x);
            _p2InvCursorPos.y = Mathf.Clamp(_p2InvCursorPos.y, -half.y, half.y);
            _p2InvCursor.anchoredPosition = _p2InvCursorPos;

            // 右摇杆上下滚动当前 tier 列表
            float ry = gp.rightStick.ReadValue().y;
            if (Mathf.Abs(ry) > 0.15f && _p2TradingScroll != null)
            {
                var c = _p2TradingScroll.content; var vp = _p2TradingScroll.viewport;
                float scrollable = (c != null && vp != null) ? Mathf.Max(1f, c.rect.height - vp.rect.height) : 1000f;
                _p2TradingScroll.verticalNormalizedPosition =
                    Mathf.Clamp01(_p2TradingScroll.verticalNormalizedPosition + ry * 900f * Time.unscaledDeltaTime / scrollable);
            }

            Vector2 cursorScreen = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, _p2InvCursor.position);

            // 悬停即选中(右侧显示详情),仿 vanilla 手柄
            int hoverIdx = P2TradingHoverIndex(cursorScreen);
            if (hoverIdx >= 0 && hoverIdx != _p2TradingSel) { _p2TradingSel = hoverIdx; RefreshP2TradingSelection(); }
            HighlightP2Trading(hoverIdx >= 0 ? _p2TradingItems[hoverIdx].ui
                : (_p2TradingSel >= 0 && _p2TradingSel < _p2TradingItems.Count ? _p2TradingItems[_p2TradingSel].ui : null));

            // A:在选中条目上直接购买/出售(手柄模式无独立购买/出售键)
            if (gp.buttonSouth.wasPressedThisFrame && hoverIdx >= 0)
            {
                _p2TradingSel = hoverIdx;
                if (_p2TradingSellMode) P2TradingTrySell();
                else                    P2TradingTryBuy();
            }
        }

        static bool P2TradingCanAfford(Cost[] costs)
        {
            if (costs == null) return true;
            foreach (var c in costs)
            {
                if (c == null || c.item == null) continue;
                int have = P2InventoryStore.CountBackpackItem(c.item.UniqueIndex) + CountP2HotbarItem(c.item.UniqueIndex);
                if (have < c.amount) return false;
            }
            return true;
        }

        static void P2TradingTryBuy()
        {
            var post = _p2TradingPost;
            if (post == null) return;
            var entry = CurrentP2TradeEntry();
            var inst = entry.inst;
            if (inst == null || inst.stock <= 0) return;
            if (_p2TradingUiClone != null && (int)_p2TradingUiClone.CurrentTier < (int)inst.tier) return;   // tier 未解锁
            if (inst.reward == null || inst.reward.item == null) return;
            if (!P2TradingCanAfford(inst.cost)) return;

            // 扣费(P2 背包换入 + 手持栏)+ 奖励进 P2(RoutingPickup 由 scope 打开)。
            using (new P2InventoryScope())
            {
                var pinv = Cm.Get<PlayerInventory>();
                if (pinv == null) return;

                if (inst.cost != null)
                    foreach (var c in inst.cost)
                    {
                        if (c == null || c.item == null || c.amount <= 0) continue;
                        int remaining = c.amount;
                        int fromBp = Mathf.Min(remaining, pinv.GetItemCount(c.item));
                        if (fromBp > 0) { pinv.RemoveItem(c.item.UniqueName, fromBp); remaining -= fromBp; }
                        if (remaining > 0)
                        {
                            int fromHb = Mathf.Min(remaining, CountP2HotbarItem(c.item.UniqueIndex));
                            if (fromHb > 0) { RemoveP2HotbarItem(c.item.UniqueIndex, fromHb); remaining -= fromHb; }
                        }
                    }

                pinv.AddItem(inst.reward.item.UniqueName, inst.reward.amount);
                RefreshRealSlots(pinv);
            }

            inst.stock--;
            try { post.PlayBuySound(); } catch (Exception e) { LogV("[P2Trading] BuySound ex: " + e.Message); }
            RefreshP2TradingSelection();
            LogV($"[P2Trading] 购买 {inst.reward.item.UniqueName} x{inst.reward.amount} (剩余库存 {inst.stock})");
        }

        // ── 出售 ────────────────────────────────────────────────────────────
        // LB→购买页, RB→出售页(切 vanilla 买/卖 TabGroup + 重建列表)。
        static void P2TradingSetSellMode(bool sell)
        {
            if (_p2TradingSellMode == sell && _p2TradingItems.Count > 0) return;
            _p2TradingSellMode = sell;
            _p2TradingSel = -1;
            if (_p2BuySellTabs != null && _p2BuySellTabs.tabButtons != null)
            {
                try
                {
                    int want = sell ? _p2SellTabIdx : _p2BuyTabIdx;
                    TabButton target = null;
                    foreach (var tb in _p2BuySellTabs.tabButtons)
                        if (tb != null && tb.tabIndex == want) { target = tb; break; }
                    var prev = _p2BuySellTabs.SelectedTabButton;
                    if (prev != null && prev != target) prev.OnPointerExit(forceExit: true);
                    if (target != null) _p2BuySellTabs.OpenTab(target.Tab, forceOpen: true);
                }
                catch (Exception e) { LogV("[P2Trading] buy/sell tab ex: " + e.Message); }
            }
            if (sell) BuildP2SellItems();
            else      P2TradingSelectTier(_p2TradingTier);   // 重建买列表(含 tier 可见性/高亮/布局)
            if (sell)
            {
                if (_p2TradingScroll != null && _p2TradingScroll.content != null)
                {
                    Canvas.ForceUpdateCanvases();
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_p2TradingScroll.content);
                }
                RefreshP2TradingSelection();
            }
            ComponentManager<SoundManager>.Value?.PlayUI_OpenMenu();
        }

        // 收集出售条目(sellContentParent 下的 UI ↔ TradingPost.SellableItems)+ 用 P2 数量刷新每项拥有数。
        static void BuildP2SellItems()
        {
            _p2TradingItems.Clear();
            _p2TradingScroll = null;
            var clone = _p2TradingUiClone;
            if (clone == null) return;
            var parent = s_tpSellParent?.GetValue(clone) as Transform;
            if (parent == null) return;
            _p2TradingScroll = parent.GetComponentInParent<ScrollRect>();

            var uis = parent.GetComponentsInChildren<UI_Cost_Interactable>(true);
            var sellables = TradingPost.SellableItems;
            int n = Mathf.Min(sellables != null ? sellables.Count : 0, uis != null ? uis.Length : 0);
            for (int i = 0; i < n; i++)
            {
                var s = sellables[i];
                _p2TradingItems.Add(new P2TradeEntry { sell = s, ui = uis[i] });
                // 左侧列表显示 P2 拥有数(vanilla Open 里用的是 P1 数)
                if (s != null && s.sellableItem != null && uis[i] != null)
                    try { uis[i].Refresh(new Cost(s.sellableItem, P2TradingCountItem(s.sellableItem))); } catch { }
            }
        }

        static void OverrideP2SellDisplay(SO_TradingPost_Sellable sell)
        {
            var clone = _p2TradingUiClone;
            if (clone == null || sell == null) return;
            var btn = s_tpSellButton?.GetValue(clone) as Button;
            if (btn != null) btn.interactable = sell.sellableItem != null && P2TradingCountItem(sell.sellableItem) > 0;
        }

        static void P2TradingTrySell()
        {
            var post = _p2TradingPost;
            if (post == null) return;
            var entry = CurrentP2TradeEntry();
            var sell = entry.sell;
            if (sell == null || sell.sellableItem == null) return;
            if (P2TradingCountItem(sell.sellableItem) <= 0) return;

            using (new P2InventoryScope())
            {
                var pinv = Cm.Get<PlayerInventory>();
                if (pinv == null) return;
                // 从 P2 扣 1 个待售物(背包优先,再手持栏)
                int fromBp = Mathf.Min(1, pinv.GetItemCount(sell.sellableItem));
                if (fromBp > 0) pinv.RemoveItem(sell.sellableItem.UniqueName, 1);
                else RemoveP2HotbarItem(sell.sellableItem.UniqueIndex, 1);
                // 奖励进 P2
                if (sell.reward != null && sell.reward.item != null)
                    pinv.AddItem(sell.reward.item.UniqueName, sell.reward.amount);
                RefreshRealSlots(pinv);
            }

            // 共享声誉:写回单例(P1/P2 共用来源;P2 每帧从它同步)
            if (_p2TradingUiSingleton != null)
            {
                try { _p2TradingUiSingleton.CurrentReputation += sell.reputationReward; }
                catch (Exception e) { LogV("[P2Trading] sell rep ex: " + e.Message); }
            }
            try { post.PlaySellSound(); } catch (Exception e) { LogV("[P2Trading] SellSound ex: " + e.Message); }

            int keepSel = _p2TradingSel;
            BuildP2SellItems();
            _p2TradingSel = Mathf.Clamp(keepSel, -1, _p2TradingItems.Count - 1);
            RefreshP2TradingSelection();
            LogV($"[P2Trading] 出售 {sell.sellableItem.UniqueName} → +{sell.reputationReward} 声誉");
        }
    }
}
