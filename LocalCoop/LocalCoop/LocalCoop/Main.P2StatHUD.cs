using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P2 数值条 HUD —— 克隆原版 UI，渲染到"右半屏专用 UI 相机"(与 P1 左半屏同样的缩放方式)。
        //  - 血/饥/渴：克隆原版 "Stat sliders" 面板(UISlider_Stat)，每帧 SetValue 驱动。
        //  - 氧气：克隆原版 OxygenMeter(fatigueParent 径向扇形表)，独立材质实例，按 SetFatigueSlider 逻辑驱动
        //          (顶部居中、扇形百分比)；独立材质避免污染 P1 的氧气表。
        //  画布为 ScreenSpaceCamera 绑右半屏相机 + CanvasScaler match=0 → 与 P1 左半屏完全相同的缩放比例；
        //  保留原版锚点(不再 +0.5 平移)，由相机 rect 自动落在右半屏。
        // ══════════════════════════════════════════════════════════════════
        static Canvas _p2HudCanvas;
        internal static bool HasP2HudCanvas => _p2HudCanvas != null;
        internal static Canvas P2HudCanvas => _p2HudCanvas;
        static Canvas _p2MenuCanvas;    // 全屏菜单(建造菜单)专用：match=1 → 960x1080 逻辑，原版长宽比、铺满半屏
        static Camera _p2UiCamera;

        // 建造菜单等"原本全屏"的菜单需要正确长宽比；_p2HudCanvas 是 match=0(1920x2160 过高)会把
        // 横版菜单挤到顶部。这里用 match=1(按高)→ 逻辑 960x1080，菜单铺满 P2 半屏。
        internal static Canvas EnsureP2MenuCanvas()
        {
            if (_p2UiCamera == null) return null;
            // 每次都重新绑定相机：_p2MenuCanvas 是 DontDestroyOnLoad 缓存，重开分屏后 _p2UiCamera 会重建，
            // 旧引用变成已销毁(null) → ScreenSpaceCamera 退化成 ScreenSpaceOverlay(满屏) → 菜单铺满全屏。
            if (_p2MenuCanvas != null)
            {
                if (_p2MenuCanvas.worldCamera != _p2UiCamera) _p2MenuCanvas.worldCamera = _p2UiCamera;
                return _p2MenuCanvas;
            }
            _p2MenuCanvas = new GameObject("P2_MenuCanvas").AddComponent<Canvas>();
            Object.DontDestroyOnLoad(_p2MenuCanvas.gameObject);
            _p2MenuCanvas.renderMode    = RenderMode.ScreenSpaceCamera;
            _p2MenuCanvas.worldCamera   = _p2UiCamera;
            _p2MenuCanvas.planeDistance = 0.85f;        // 比 HUD(1.0) 更近 → 渲染在上层
            _p2MenuCanvas.sortingOrder  = 8;
            var sc = _p2MenuCanvas.gameObject.AddComponent<CanvasScaler>();
            sc.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920f, 1080f);
            sc.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            sc.matchWidthOrHeight  = 1f;                 // 按高 → 逻辑 960x1080(原版长宽比)
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) _p2MenuCanvas.gameObject.layer = uiLayer;
            LogV("[P2Menu] created build menu canvas");
            return _p2MenuCanvas;
        }
        static GameObject _p2StatPanel, _p2OxyMeter;
        static UISlider_Stat _p2HpSlider, _p2HungerSlider, _p2ThirstSlider;
        static Image _p2OxyRadial, _p2OxyBg;
        static Material _p2OxyMeterMat, _p2OxyBgMat;
        static PlayerStats _p2Stats;
        static int _nextP2HudValueFrame;

        // P2 快捷栏（右半屏底部居中，与 P1 同尺寸/位置）：显示 P2 工具列表 + 选中高亮。
        static bool _p2HotbarBuilt;
        static GameObject _p2HotbarGo, _p2KbLayout;
        static Image[] _p2SlotIcons, _p2SlotHighlights, _p2HotbarSlotBg, _p2SlotSliderFills;
        static Text[]  _p2SlotAmount;
        static UnityEngine.UI.Slider[] _p2SlotSliders;
        static Slot[]  _p2HotbarSlots;   // 保留的原版 Slot 脚本 → 用 SetItem 原生驱动图标/数量/耐久/水量条
        internal static RectTransform[] _p2HotbarSlotRects;   // 供背包光标命中检测/拖拽
        static readonly System.Collections.Generic.List<GameObject> _p2GpLayouts = new System.Collections.Generic.List<GameObject>();
        const float HotbarY = 20f;

        internal static void TickP2StatHud()
        {
            var p2 = player2;
            if (p2 == null) return;
            if (_p2Stats == null || _p2Stats.gameObject != p2.gameObject)
                _p2Stats = p2.GetComponent<PlayerStats>();
            if (_p2Stats == null) return;

            if (_p2HudCanvas == null) CreateP2StatHud();

            // 准心 + 视角切换(D-pad 上 = 第一/第三人称)
            EnsureP2Crosshair();
            bool showCross = !P2OwnMenuOpen;
            if (_p2Crosshair != null && _p2Crosshair.gameObject.activeSelf != showCross)
                _p2Crosshair.gameObject.SetActive(showCross);
            UpdateP2AimSprite();           // 准心图标随手持工具/瞄准目标切换(持斧瞄树=砍树图标)
            EnsureP2CameraRig();
            TickP2CameraBootstrap();
            TickP2View();
            
            
            
            
            
            if (P2IsDownedOrCarried && !p2FirstPerson) SetP2DownedThirdPersonCamera();
            else if (p2FirstPerson && player2 != null) P2CameraController.ApplyFirstPersonView(player2);
            EnsureP2Prompt();
            UpdateP2Prompt();
            ExpireP2SteerPrompt();   // 方向盘转向提示:离开方向盘后自动隐藏

            bool refreshHudValues = Time.frameCount >= _nextP2HudValueFrame;
            if (refreshHudValues)
            {
                _nextP2HudValueFrame = Time.frameCount + 6;
                if (_p2HpSlider != null && _p2Stats.stat_health != null)
                { float v = _p2Stats.stat_health.NormalValue; _p2HpSlider.SetValue(v); _p2HpSlider.SetTargetValue(v); }
                if (_p2HungerSlider != null && _p2Stats.stat_hunger != null)
                { float v = _p2Stats.stat_hunger.Normal.NormalValue; _p2HungerSlider.SetValue(v); _p2HungerSlider.SetTargetValue(v); }
                if (_p2ThirstSlider != null && _p2Stats.stat_thirst != null)
                { float v = _p2Stats.stat_thirst.Normal.NormalValue; _p2ThirstSlider.SetValue(v); _p2ThirstSlider.SetTargetValue(v); }
            }

            
            if (refreshHudValues && _p2OxyMeter != null)
            {
                float oxy = _p2Stats.stat_oxygen != null ? _p2Stats.stat_oxygen.NormalValue : 1f;
                bool show = oxy < 1f;
                if (_p2OxyMeter.activeSelf != show) _p2OxyMeter.SetActive(show);
                if (show)
                {
                    if (_p2OxyRadial != null) _p2OxyRadial.fillAmount = oxy;
                    if (oxy > 0f)
                    {
                        _p2OxyMeterMat?.SetFloat("_FatigueValue", 1f - oxy);
                        _p2OxyBgMat?.SetFloat("_FatigueValue", 0f);
                    }
                    else
                    {
                        _p2OxyMeterMat?.SetFloat("_FatigueValue", 1f);
                        _p2OxyBgMat?.SetFloat("_FatigueValue", 1f);
                    }
                }
            }

            UpdateP2Hotbar();
        }

        // ── P2 快捷栏 ─────────────────────────────────────────────────────
        static void UpdateP2Hotbar()
        {
            if (_p2Hotbar == null) return;
            if (!_p2HotbarBuilt || _p2SlotIcons == null) BuildP2Hotbar();   // 同上:标志与实际控件失同步时自愈
            if (_p2SlotIcons == null) return;

            int n = _p2SlotIcons.Length;
            int cur = 0;
            var rt = SplitScreenRuntime.Instance;
            if (rt != null && rt.P2 != null) cur = rt.P2.HotbarIndex;
            if (n > 0) cur = ((cur % n) + n) % n;   // 高亮跟随 HotbarIndex，可落在全部 10 个槽

            for (int i = 0; i < n; i++)
            {
                var item = (i < _p2Hotbar.Length) ? _p2Hotbar[i] : null;
                // 用原版 Slot.SetItem 驱动 图标/数量/耐久条/水量条(RefreshComponents 原生处理)；
                //  仅当与当前显示不一致时才 SetItem，避免每帧重克隆。
                if (_p2HotbarSlots != null && i < _p2HotbarSlots.Length && _p2HotbarSlots[i] != null)
                {
                    var curInst = _p2HotbarSlots[i].itemInstance;
                    if (!SameHotbarItem(curInst, item))
                        _p2HotbarSlots[i].SetItem(item);   // SetItem(null)清空；SetItem(inst)克隆+RefreshComponents
                }
                if (_p2SlotHighlights[i] != null && _p2SlotHighlights[i].gameObject.activeSelf != (i == cur))
                    _p2SlotHighlights[i].gameObject.SetActive(i == cur);
            }

            
            bool show = !P2OwnMenuOpen;
            for (int i = 0; i < _p2GpLayouts.Count; i++)
                if (_p2GpLayouts[i] != null && _p2GpLayouts[i].activeSelf != show) _p2GpLayouts[i].SetActive(show);
            if (_p2KbLayout != null && _p2KbLayout.activeSelf) _p2KbLayout.SetActive(false);
        }

        static bool SameHotbarItem(ItemInstance a, ItemInstance b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.UniqueIndex == b.UniqueIndex && a.Amount == b.Amount && a.Uses == b.Uses;
        }

        // 克隆"整套"原版热栏 widget（背景图 + 全部槽位 + 手柄 RB/LB 提示布局），全用原版图片/布局。
        //  关键：直接 Instantiate 活动的 Hotbar 会触发克隆体 Hotbar/Slot 的 Awake/Start（订阅静态事件 →
        //  删脚本后悬空引用报错）。故"先把源临时置 inactive 再 Instantiate"，克隆体不跑 Awake；趁 inactive
        //  删掉逻辑脚本(保留 Image/Text)，强制手柄布局，设 UI 层，再激活。源同帧恢复，P1 热栏不受影响。
        static void BuildP2Hotbar()
        {
            if (_p2HudCanvas == null) return;
            var inv = ComponentManager<PlayerInventory>.Value;
            var hotbar = inv != null ? inv.hotbar : null;
            if (hotbar == null) return; // 原版热栏未就绪，下帧重试
            int uiLayer = LayerMask.NameToLayer("UI");

            var src = hotbar.gameObject;
            bool wasActive = src.activeSelf;
            src.SetActive(false);                                   // 阻止克隆体 Awake/Start
            var clone = Object.Instantiate(src, _p2HudCanvas.transform);
            src.SetActive(wasActive);                               // 立刻恢复 P1 热栏
            clone.name = "P2_Hotbar";

            var cloneHotbar = clone.GetComponent<Hotbar>();
            // 收集全部槽位的图标/高亮(删脚本前)
            var slots = clone.GetComponentsInChildren<Slot>(true);
            _p2SlotIcons = new Image[slots.Length];
            _p2SlotHighlights = new Image[slots.Length];
            _p2SlotAmount = new Text[slots.Length];
            _p2HotbarSlotRects = new RectTransform[slots.Length];
            _p2HotbarSlotBg = new Image[slots.Length];
            _p2SlotSliders = new UnityEngine.UI.Slider[slots.Length];
            _p2SlotSliderFills = new Image[slots.Length];
            _p2HotbarSlots = slots;   // 保留 Slot 脚本(StripNonGraphicScripts 已放过 Slot/Slider)
            for (int i = 0; i < slots.Length; i++)
            {
                _p2SlotIcons[i]       = slots[i].imageComponent;
                _p2SlotHighlights[i]  = slots[i].imageFocused;
                _p2SlotSliders[i]     = slots[i].sliderComponent;             // 耐久条
                _p2SlotSliderFills[i] = slots[i].sliderFillComponent;
                _p2HotbarSlotRects[i] = slots[i].transform as RectTransform;   // 槽位 rect（命中检测）
                _p2HotbarSlotBg[i]    = slots[i].GetComponent<Image>();        // 槽位底图（悬停高亮）
                var amtT = FindChildByName(slots[i].transform, "AmountText");
                if (amtT != null) _p2SlotAmount[i] = amtT.GetComponent<Text>();
            }
            // 诊断证实手柄布局(gamepadLayoutLb/Rb 等)不是热栏子物体，不在克隆体内 → 下方单独克隆 RB/LB 进 widget。
            //  键盘布局(keyboardLayout, 含 Tab/T 等键盘键位提示)若是克隆体的子物体则关掉它(手柄不显示键盘提示)；
            //  仅关“克隆体内”的(IsChildOf clone)，绝不碰 P1 原件。
            _p2GpLayouts.Clear();
            var kb = LayoutGo(cloneHotbar, "keyboardLayout");
            bool kbChild = kb != null && kb.transform.IsChildOf(clone.transform);
            LogV($"[P2HUD] keyboardLayout: found={kb != null} childOfClone={kbChild}");
            _p2KbLayout = kbChild ? kb : null;
            if (_p2KbLayout != null) _p2KbLayout.SetActive(false);

            StripNonGraphicScripts(clone);                          // 删 Hotbar 等逻辑脚本，保留 Image/Text/Slot/Slider
            // 保留了 Slot 脚本(带点击/悬停接口)；禁用克隆体所有图形的 raycastTarget，防 P1 鼠标误触发 Slot 事件(inventory=null→NRE)。
            foreach (var g in clone.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            if (uiLayer >= 0) SetLayerRecursively(clone.transform, uiLayer);

            var rt = clone.transform as RectTransform;              // 整套 widget 移到右半屏底部居中
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, HotbarY);
            }

            clone.SetActive(true);
            _p2HotbarGo = clone;
            foreach (var hl in _p2SlotHighlights) if (hl != null) hl.gameObject.SetActive(false);

            // 手柄 RB/LB 提示不在热栏子树里 → 从 P1 原件单独克隆进 P2 widget，按其相对 P1 热栏根的偏移定位。
            CloneGamepadHintToWidget(hotbar, clone, "gamepadLayoutLb", uiLayer);
            CloneGamepadHintToWidget(hotbar, clone, "gamepadLayoutRb", uiLayer);

            _p2HotbarBuilt = true;
            LogV($"[P2HUD] cloned vanilla hotbar slots={slots.Length}");
        }

        // 把 P1 的某个手柄提示对象(gamepadLayoutLb/Rb)克隆进 P2 widget，定位到它相对 P1 热栏根的偏移处，强制显示。
        static void CloneGamepadHintToWidget(Hotbar hotbar, GameObject widget, string field, int uiLayer)
        {
            var src = LayoutGo(hotbar, field);
            if (src == null) { ModEntry.Logger.Log($"[P2HUD] 未找到 {field}"); return; }
            Vector3 lp = hotbar.transform.InverseTransformPoint(src.transform.position); // 相对 P1 热栏根的局部点
            var clone = Object.Instantiate(src, widget.transform);
            clone.name = "P2_" + field;
            var rt = clone.transform as RectTransform;
            if (rt != null) { rt.localRotation = Quaternion.identity; rt.localPosition = lp; }
            clone.SetActive(true);
            StripNonGraphicScripts(clone);
            if (uiLayer >= 0) SetLayerRecursively(clone.transform, uiLayer);
            _p2GpLayouts.Add(clone);
            LogV($"[P2HUD] cloned {field} to P2 widget, localPos={lp}");
        }

        // 删除非 Graphic 的 MonoBehaviour(逻辑脚本)，保留 Image/Text 等可视组件。
        // 剥离逻辑脚本，但【保留 Slot 与 Slider】：Slot 的 Awake/Start/OnEnable 安全(无静态事件订阅、
        //  不引用 playerNetwork)，其 SetItem→RefreshComponents 能原生驱动 图标/数量/耐久条/水量条；
        //  Slider 是 Selectable(非 Graphic)，若一并销毁则耐久/水量条无法显示。真正有问题的是 Hotbar 脚本。
        static void StripNonGraphicScripts(GameObject root)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && !(mb is Graphic) && !(mb is Slot) && !(mb is UnityEngine.UI.Slider))
                    Object.Destroy(mb);
        }

        // ── P2 建造提示 UI(独立于 P1)───────────────────────────────────────
        //  P2 的 BlockCreator.Update 本会往共享(P1)HUD 写提示；补丁改为转发到这里(SetP2Prompt)，
        //  在 P2 半屏底部独立显示，不影响 P1。
        static Text _p2PromptText;
        static readonly string[] _p2Prompts = new string[8];
        static int _p2PromptFrame;

        internal static void SetP2Prompt(int index, string text)
        {
            if (index < 0 || index >= _p2Prompts.Length) return;
            _p2Prompts[index] = string.IsNullOrEmpty(text) ? null : text;
            _p2PromptFrame = Time.frameCount;
        }

        internal static void ClearAllP2Prompts()
        {
            for (int i = 0; i < _p2Prompts.Length; i++) _p2Prompts[i] = null;
            _p2PromptFrame = Time.frameCount;
        }

        static void EnsureP2Prompt()
        {
            if (_p2PromptText != null || _p2HudCanvas == null) return;
            var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            var go = new GameObject("P2_BuildPrompt", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rt = go.GetComponent<RectTransform>(); rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 190f); rt.sizeDelta = new Vector2(760f, 130f);
            _p2PromptText = go.GetComponent<Text>(); _p2PromptText.alignment = TextAnchor.LowerCenter;
            _p2PromptText.fontSize = 26; _p2PromptText.color = Color.white; _p2PromptText.raycastTarget = false;
            if (arial) _p2PromptText.font = arial;
            var sh = go.AddComponent<Shadow>(); sh.effectColor = Color.black; sh.effectDistance = new Vector2(1f, -1f);
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) go.layer = uiLayer;
            go.SetActive(false);
        }

        // ── P2 拾取提示(右下角，克隆原版 InventoryPickupMenuItem：+N + 物品图标 + 名称 + 棕色底) ──────────
        //  原版 InventoryPickup.ShowItem 弹在共享 HUD(P1 半屏)；P2 拾取被屏蔽以免污染 P1。
        //  这里把原版 menuItemPrefab 克隆若干份到 P2 半屏右下角的容器，复用原版 SetItem(图标/名称/+N + 自带淡出/堆叠)。
        const int P2PickupPoolSize = 6;
        static InventoryPickupMenuItem[] _p2PickupPool;
        static RectTransform _p2PickupContainer;

        static void EnsureP2PickupPool()
        {
            if (_p2PickupPool != null || _p2HudCanvas == null) return;
            var src = Object.FindObjectOfType<InventoryPickup>();
            var prefab = src != null ? src.menuItemPrefab : null;
            if (prefab == null) return;   // 原版拾取系统还没好 → 下次再试

            var containerGo = new GameObject("P2_PickupContainer", typeof(RectTransform));
            _p2PickupContainer = containerGo.GetComponent<RectTransform>();
            _p2PickupContainer.SetParent(_p2HudCanvas.transform, false);
            _p2PickupContainer.anchorMin = _p2PickupContainer.anchorMax = new Vector2(1f, 0f);   // 右下角
            _p2PickupContainer.pivot = new Vector2(1f, 0f);
            _p2PickupContainer.anchoredPosition = new Vector2(-30f, 80f);
            int uiLayer = LayerMask.NameToLayer("UI");

            _p2PickupPool = new InventoryPickupMenuItem[P2PickupPoolSize];
            for (int i = 0; i < P2PickupPoolSize; i++)
            {
                var clone = Object.Instantiate(prefab, _p2PickupContainer);
                clone.name = "P2_PickupItem" + i;
                clone.gameObject.SetActive(false);
                // 把每个提示项的 RectTransform 锚点/轴心钉到【右下】，使 localPosition.x=0 时其右边缘贴住容器原点
                //  (容器在屏幕右下、距右边缘 30px)。原版 prefab 的轴心非右对齐 → 放到右锚容器会向右溢出屏幕。
                //  仅改自身轴心/锚点(不改 sizeDelta)，内部图标/文字按各自锚点布局不变；其 Update 只调 y 不动 x。
                var crt = clone.GetComponent<RectTransform>();
                if (crt != null)
                {
                    crt.anchorMin = crt.anchorMax = new Vector2(1f, 0f);
                    crt.pivot = new Vector2(1f, 0f);
                    crt.anchoredPosition = Vector2.zero;
                }
                if (uiLayer >= 0) SetLayerRecursively(clone.transform, uiLayer);
                _p2PickupPool[i] = clone;
            }
            LogV("[P2HUD] created P2 pickup prompt pool");
        }

        // 由 InventoryPickup.ShowItem 补丁在 P2 拾取上下文调用 —— 复刻原版 InventoryPickup.ShowItem 行为。
        internal static void ShowP2PickupToast(string uniqueItemName, int amount)
        {
            EnsureP2PickupPool();
            if (_p2PickupPool == null) return;
            var item = ItemManager.GetItemByName(uniqueItemName);
            if (item == null) return;

            // 现有可见项 index++(向上堆叠)。
            InventoryPickupMenuItem first = null;
            for (int i = 0; i < _p2PickupPool.Length; i++)
            {
                var it = _p2PickupPool[i];
                if (it == null) continue;
                if (it.gameObject.activeInHierarchy) it.index++;
                else if (first == null) first = it;     // 第一个空闲项
            }
            if (first == null)   // 全占用 → 复用 index 最大(最旧)的那个
            {
                int maxIdx = -1;
                for (int i = 0; i < _p2PickupPool.Length; i++)
                    if (_p2PickupPool[i] != null && _p2PickupPool[i].index > maxIdx) { maxIdx = _p2PickupPool[i].index; first = _p2PickupPool[i]; }
            }
            if (first == null) return;

            first.rect.localPosition = new Vector3(0f, -2f * first.rect.sizeDelta.y, 0f);   // 从底部滑入
            first.gameObject.SetActive(true);
            first.SetItem(item, amount);   // 原版：+N + 名称 + 图标 + alpha=1 + 3.5s 后自动淡出
            first.index = 0;
        }

        static void UpdateP2Prompt()
        {
            if (_p2PromptText == null) return;
            if (Time.frameCount - _p2PromptFrame > 3)   // 建造停止(几帧无更新)→ 清空
                for (int i = 0; i < _p2Prompts.Length; i++) _p2Prompts[i] = null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _p2Prompts.Length; i++)
                if (!string.IsNullOrEmpty(_p2Prompts[i])) { if (sb.Length > 0) sb.Append('\n'); sb.Append(_p2Prompts[i]); }
            string s = sb.ToString();
            bool show = s.Length > 0 && !P2OwnMenuOpen;
            if (_p2PromptText.text != s) _p2PromptText.text = s;
            if (_p2PromptText.gameObject.activeSelf != show) _p2PromptText.gameObject.SetActive(show);
        }

        
        static bool P2OwnMenuOpen => IsP2BackpackOpen || IsP2MenuOpen || IsP2BuildMenuOpen;

        static RectTransform _p2Crosshair;
        static Image _p2CrosshairImg;
        static bool _wasP2View;
        
        
        internal static bool IsP2FirstPerson
        {
            get => P2CameraController.FirstPerson;
            set => P2CameraController.FirstPerson = value;
        }
        internal static bool p2FirstPerson
        {
            get => IsP2FirstPerson;
            set => IsP2FirstPerson = value;
        }
        static float _p2DownedOrbitYaw, _p2DownedOrbitPitch = 18f;
        static Vector3 _p2DownedOrbitPivot;
        static Vector3 _p2DownedOrbitPivotVelocity;
        static bool _p2DownedOrbitPivotValid;
        const float P2DownedLookSensitivity = 150f;         
        const float P2DownedLookDeadzoneSqr = 0.04f;
        static bool _p2CameraBootstrapActive;
        static int _p2CameraBootstrapFrames;
        internal static bool P2CameraBootstrapActive => _p2CameraBootstrapActive;

        internal static void BeginP2CameraBootstrap()
        {
            _p2CameraBootstrapActive = true;
            _p2CameraBootstrapFrames = 0;
            p2FirstPerson = true;
            if (player2 != null)
            {
                P2CameraController.Yaw = player2.transform.eulerAngles.y;
                P2CameraController.Pitch = 0f;
            }
            SuppressP2CamerasForBootstrap();
            LogV("[P2CameraBootstrap] started");
        }

        internal static void TickP2CameraBootstrap()
        {
            if (!_p2CameraBootstrapActive) return;

            SuppressP2CamerasForBootstrap();

            var p2 = player2;
            if (p2 == null || p2.Camera == null || p2.currentModel == null || p2.currentModel.cameraHolder == null)
                return;

            p2FirstPerson = true;
            if (player2ArmMesh == null)
                player2ArmMesh = GetArmMesh(p2.currentModel);

            EnsureP2CameraRig();
            P2CameraController.EnforceFirstPersonCamera();
            if (p2.PlayerScript != null)
                p2.PlayerScript.SetMouseLookScripts(false);
            if (p2.playerPivot != null)
            {
                float rootYaw = p2.transform.eulerAngles.y;
                p2.playerPivot.localEulerAngles = new Vector3(0f, P2CameraController.Yaw - rootYaw, 0f);
            }
            EnforceMeshStates();
            bool p2Downed = P2IsDownedOrCarried;
            bool p2Sleeping = p2.BedComponent != null && p2.BedComponent.Sleeping;
            bool p2BedRestoring = SplitScreenDeathFlow.P2PostBedExitRestoring;
            if (p2Downed) EnforceP2DownedWorldVisual();
            else
            {
                SetP2BodyVisible(p2Sleeping || p2BedRestoring);
                SetP2ToolLayer(p2Sleeping ? -1 : LAYER_P2_HAND);
            }

            _p2CameraBootstrapFrames++;
            if (_p2CameraBootstrapFrames < 2) return;

            if (p2.Camera != null) p2.Camera.enabled = true;
            if (p2.HandCamera != null)
            {
                p2.HandCamera.enabled = !p2Downed && !p2Sleeping;
                if (!p2.HandCamera.gameObject.activeSelf)
                    p2.HandCamera.gameObject.SetActive(true);
            }

            _p2CameraBootstrapActive = false;
            LogV("[P2CameraBootstrap] completed");
        }

        static void SuppressP2CamerasForBootstrap()
        {
            var p2 = player2;
            if (p2 == null) return;
            if (p2.Camera != null && p2.Camera.enabled)
                p2.Camera.enabled = false;
            if (p2.HandCamera != null && p2.HandCamera.enabled)
                p2.HandCamera.enabled = false;
        }

        internal static bool P2IsDownedOrCarried
        {
            get
            {
                var p2 = player2;
                if (p2 == null) return false;
                bool sleeping = p2.BedComponent != null && p2.BedComponent.Sleeping;
                return (p2.PlayerScript != null && p2.PlayerScript.IsDead)
                       || (p2.RessurectComponent != null && p2.RessurectComponent.BeingCarried && !sleeping);
            }
        }

        static void EnsureP2Crosshair()
        {
            if (_p2Crosshair != null || _p2HudCanvas == null) return;
            var ch = ComponentManager<CanvasHelper>.Value;
            var spr = (ch != null && ch.centerAim != null) ? ch.centerAim.sprite : null;
            var go = new GameObject("P2_Crosshair", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _p2Crosshair = go.GetComponent<RectTransform>();
            _p2Crosshair.SetParent(_p2HudCanvas.transform, false);
            _p2Crosshair.anchorMin = _p2Crosshair.anchorMax = _p2Crosshair.pivot = new Vector2(0.5f, 0.5f);
            _p2Crosshair.anchoredPosition = Vector2.zero;
            var aimSize = new Vector2(32f, 32f);
            if (ch != null && ch.centerAim != null && ch.centerAim.rectTransform != null
                && ch.centerAim.rectTransform.rect.width > 1f)
                aimSize = ch.centerAim.rectTransform.rect.size;
            _p2Crosshair.sizeDelta = aimSize;
            _p2CrosshairImg = go.GetComponent<Image>(); _p2CrosshairImg.raycastTarget = false; _p2CrosshairImg.preserveAspect = true;
            if (spr != null) _p2CrosshairImg.sprite = spr; else _p2CrosshairImg.color = new Color(1f, 1f, 1f, 0.7f);
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) go.layer = uiLayer;
            LogV("[P2HUD] created P2 crosshair");
        }

        // P2 准心图标随【瞄准目标】切换(对齐原版 Pickup.Update→CanvasHelper.SetAimSprite，按目标而非手持工具)。
        //  原版 SetAimSprite 改的是【共享 P1 准心 centerAim】，分屏下不能用(会改 P1)；故反射出 CanvasHelper 的
        //  各 AimSprite 精灵字段，自己按 P2 相机射线命中的可交互物 tag 选图标设到 P2 准心。
        static readonly System.Collections.Generic.Dictionary<AimSprite, Sprite> _aimSprites = new System.Collections.Generic.Dictionary<AimSprite, Sprite>();
        // 判据直接读字典,不另设标志:Main.ResetUnityStatics(重载时清理)会 Clear() 掉持有
        // Unity 对象的静态集合,却清不到布尔标志 —— 一旦标志停在 true 而字典已空,
        // 解析就被早退跳过,取 Default 直接 KeyNotFoundException 刷屏。读字典本身天然自愈。
        static bool AimSpritesResolved => _aimSprites.ContainsKey(AimSprite.Default);

        static void ResolveAimSprites()
        {
            if (AimSpritesResolved) return;
            var ch = ComponentManager<CanvasHelper>.Value;
            if (ch == null) return;   // CanvasHelper 还没好，下帧再试
            var t = typeof(CanvasHelper);
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            void Map(AimSprite key, string field) { var s = t.GetField(field, bf)?.GetValue(ch) as Sprite; if (s != null) _aimSprites[key] = s; }
            Map(AimSprite.Default,    "defaultAimSprite");
            Map(AimSprite.Mineable,   "gatherHookAimSprite");
            Map(AimSprite.Shovelable, "gatherShovelAimSprite");
            Map(AimSprite.Shearable,  "gatherShearAimSprite");
            Map(AimSprite.Bucketable, "gatherBucketAimSprite");
            Map(AimSprite.Macheteable,"gatherMacheteAimSprite");
            Map(AimSprite.Choppable,  "gatherWoodAimSprite");
        }

        static void UpdateP2AimSprite()
        {
            if (_p2CrosshairImg == null || !_p2CrosshairImg.gameObject.activeSelf) return;
            ResolveAimSprites();
            if (!AimSpritesResolved) return;

            var want = AimSprite.Default;
            var ri = P2AimedInteractable();
            if (ri != null)
            {
                if (ri.CompareTag("Pickup_Shovel"))      want = AimSprite.Shovelable;
                else if (ri.CompareTag("PickupHook"))     want = AimSprite.Mineable;
                else if (ri.CompareTag("Resource"))       { var rr = ri.transform.GetComponentInChildren<ResourceRegenerative>(); if (rr != null && rr.resource != null) want = rr.resource.aimAtSprite; }
                else if (ri.CompareTag("MacheteInteract")) want = AimSprite.Macheteable;
                else if (ri.CompareTag("Tree"))           want = AimSprite.Choppable;
            }
            if (!_aimSprites.TryGetValue(want, out var spr)) spr = _aimSprites[AimSprite.Default];
            if (_p2CrosshairImg.sprite != spr) _p2CrosshairImg.sprite = spr;
        }

        // P2 相机正前方射线找可交互物(对齐原版 Helper.FindInteractable，但从 P2 相机出发并跳过 P2 自身碰撞体)。
        static RaycastInteractable P2AimedInteractable()
        {
            if (player2 == null || player2.Camera == null) return null;
            var ct = player2.Camera.transform;
            float dist = Player.UseDistance * 1.1f;
            var hits = Physics.RaycastAll(ct.position, ct.forward, dist, LayerMasks.MASK_RaycastInteractable);
            if (hits == null || hits.Length == 0) return null;
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var p2t = player2.transform;
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i].collider; var tr = hits[i].transform;
                if (col == null || tr == null || tr.IsChildOf(p2t)) continue;   // 跳过 P2 自身(第三人称相机在身后)
                var ri = col.GetComponent<RaycastInteractable>();
                if (ri == null) { var rd = col.GetComponent<RaycastInteractable_Redirect>(); if (rd != null) ri = rd.RaycastInteractable; }
                if (ri != null) return ri;
            }
            return null;
        }

        
        
        
        
        static System.Reflection.FieldInfo _fTpCamT, _fTpCurModel, _fTpRotT, _fTpCanvas;
        static Transform _p2ThirdPersonRotator;
        internal static void EnsureP2ThirdPerson(ThirdPerson tp)
        {
            if (tp == null || player2 == null) return;
            if (_fTpCamT == null)
            {
                var t = typeof(ThirdPerson);
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                _fTpCamT     = t.GetField("cameraTransform", bf);
                _fTpCurModel = t.GetField("currentModel", bf);
                _fTpRotT     = t.GetField("cameraRotateTransform", bf);
                _fTpCanvas   = t.GetField("canvas", bf);
            }
            if (_fTpCamT != null && player2.Camera != null && (_fTpCamT.GetValue(tp) as Transform) != player2.Camera.transform)
                _fTpCamT.SetValue(tp, player2.Camera.transform);
            if (_fTpCurModel != null && player2.currentModel != null && (_fTpCurModel.GetValue(tp) as CharacterModelModifications) != player2.currentModel)
                _fTpCurModel.SetValue(tp, player2.currentModel);
            // ThirdPerson.Start may already have created its rotator. Reuse that
            // instance so the TP orbit has one authoritative target transform.
            var assignedRotator = _fTpRotT != null ? _fTpRotT.GetValue(tp) as Transform : null;
            if (_p2ThirdPersonRotator == null)
            {
                _p2ThirdPersonRotator = assignedRotator;
                if (_p2ThirdPersonRotator == null)
                    _p2ThirdPersonRotator = new GameObject("P2_ThirdPersonCameraRotator").transform;
            }
            if (_fTpRotT != null && (_fTpRotT.GetValue(tp) as Transform) != _p2ThirdPersonRotator)
                _fTpRotT.SetValue(tp, _p2ThirdPersonRotator);
            if (_fTpCanvas != null && _fTpCanvas.GetValue(tp) == null)
                _fTpCanvas.SetValue(tp, ComponentManager<CanvasHelper>.Value);
        }

        internal static void EnsureP2CameraRig()
        {
            if (player2 == null || player2.Camera == null) return;
            var tp = player2.currentModel != null ? player2.currentModel.thirdPersonSettings : player2.GetComponentInChildren<ThirdPerson>();
            EnsureP2ThirdPerson(tp);
            if (_p2ThirdPersonRotator == null) return;

            var camT = player2.Camera.transform;
            var holder = player2.currentModel != null ? player2.currentModel.cameraHolder : null;

            if (P2IsDownedOrCarried)
            {
                if (p2FirstPerson && holder != null)
                {
                    if (camT.parent != holder)
                        camT.SetParent(holder, false);
                    camT.localPosition = Vector3.zero;
                    camT.localEulerAngles = Vector3.zero;
                }
                else if (camT.parent != null)
                {
                    camT.SetParent(null, true);
                }
                if (_p2ThirdPersonRotator.parent != null)
                    _p2ThirdPersonRotator.SetParent(null, true);
                return;
            }

            if (p2FirstPerson)
            {
                if (_p2ThirdPersonRotator.parent != null)
                    _p2ThirdPersonRotator.SetParent(null, true);
            }
            else
            {
                if (camT.parent != null)
                    camT.SetParent(null, true);
                if (_p2ThirdPersonRotator.parent != null)
                    _p2ThirdPersonRotator.SetParent(null, true);
            }
        }

        static void TickP2View()
        {
            var gp = GetP2BoundGamepad();
            if (gp == null) return;
            // 对齐原版：用手柄 View(切换界面键 = selectButton)切第一/第三人称。
            bool press = gp.selectButton.isPressed;
            if (P2IsDownedOrCarried)
            {
                if (press && !_wasP2View)
                {
                    p2FirstPerson = !p2FirstPerson;
                    if (!p2FirstPerson)
                        InitP2DownedOrbit();
                }

                if (!p2FirstPerson)
                {
                    var look = p2ActionLook != null ? p2ActionLook.ReadValue<Vector2>() : Vector2.zero;
                    if (look.sqrMagnitude >= P2DownedLookDeadzoneSqr)
                    {
                        float s = P2DownedLookSensitivity * Time.deltaTime;
                        _p2DownedOrbitYaw += look.x * s;
                        _p2DownedOrbitPitch = Mathf.Clamp(_p2DownedOrbitPitch - look.y * s, -20f, 65f);
                    }
                }
                _wasP2View = press;
                return;
            }

            P2CameraController.Tick();
        }

        static void SetP2DownedThirdPersonCamera()
        {
            if (player2 == null || player2.Camera == null) return;

            var cam = player2.Camera.transform;
            if (cam.parent != null)
                cam.SetParent(null, true);
            Vector3 rawPivot = GetP2DownedOrbitPivot();
            bool carried = player2.RessurectComponent != null && player2.RessurectComponent.BeingCarried;
            if (!_p2DownedOrbitPivotValid || Vector3.Distance(_p2DownedOrbitPivot, rawPivot) > 2f)
            {
                _p2DownedOrbitPivot = rawPivot;
                _p2DownedOrbitPivotVelocity = Vector3.zero;
                _p2DownedOrbitPivotValid = true;
            }
            else if (carried || Vector3.Distance(_p2DownedOrbitPivot, rawPivot) > 0.15f)
            {
                _p2DownedOrbitPivot = Vector3.SmoothDamp(_p2DownedOrbitPivot, rawPivot, ref _p2DownedOrbitPivotVelocity, 0.12f);
            }

            Vector3 pivot = _p2DownedOrbitPivot;
            Quaternion orbit = Quaternion.Euler(_p2DownedOrbitPitch, _p2DownedOrbitYaw, 0f);
            Vector3 target = pivot + orbit * new Vector3(0f, 0f, -3.0f);

            cam.position = target;
            cam.rotation = Quaternion.LookRotation(pivot - target, Vector3.up);
        }

        static Vector3 GetP2DownedOrbitPivot()
        {
            if (player2 == null) return Vector3.zero;
            return player2.transform.position + Vector3.up * 1.0f;
        }

        static void InitP2DownedOrbit()
        {
            if (player2 == null) return;
            _p2DownedOrbitYaw = player2.Camera != null ? player2.Camera.transform.eulerAngles.y : player2.transform.eulerAngles.y;
            _p2DownedOrbitPitch = 18f;
            _p2DownedOrbitPivot = GetP2DownedOrbitPivot();
            _p2DownedOrbitPivotVelocity = Vector3.zero;
            _p2DownedOrbitPivotValid = true;
        }

        
        
        
        
        
        internal static void BendP2SpineForP1View()
        {
            P2RemoteLookPose.Begin();
        }

        internal static void RestoreP2SpineForP1View()
        {
            P2RemoteLookPose.End();
        }

        static Transform FindChildByName(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindChildByName(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        // 反射取 Hotbar 的私有布局 GameObject 字段。
        static GameObject LayoutGo(Component comp, string field)
        {
            if (comp == null) return null;
            var f = comp.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f?.GetValue(comp) as GameObject;
        }

        internal static void DestroyP2StatHud()
        {
            DestroyP2Backpack();   // 清理挂在 _p2HudCanvas 下的 P2 背包光标/手持物 + 复位静态引用
            if (_p2MenuCanvas != null) Object.Destroy(_p2MenuCanvas.gameObject);
            _p2MenuCanvas = null;
            _p2Crosshair = null; _p2CrosshairImg = null; _p2PromptText = null;
            _p2PickupPool = null; _p2PickupContainer = null;   // 随 _p2HudCanvas 一起销毁，复位引用
            for (int i = 0; i < _p2Prompts.Length; i++) _p2Prompts[i] = null;
            if (_p2HudCanvas != null) Object.Destroy(_p2HudCanvas.gameObject);
            if (_p2UiCamera != null) Object.Destroy(_p2UiCamera.gameObject);
            _p2HudCanvas = null; _p2UiCamera = null; _p2StatPanel = null; _p2OxyMeter = null;
            _p2HpSlider = _p2HungerSlider = _p2ThirstSlider = null;
            _p2OxyRadial = _p2OxyBg = null; _p2OxyMeterMat = _p2OxyBgMat = null; _p2Stats = null;
            _p2HotbarBuilt = false; _p2HotbarGo = null; _p2SlotIcons = null; _p2SlotHighlights = null;
            _p2SlotAmount = null; _p2HotbarSlotRects = null; _p2HotbarSlotBg = null;
            _p2GpLayouts.Clear(); _p2KbLayout = null;
            // 提示 UI 随 _p2HudCanvas 一起销毁 → 清 static 引用,重进世界时干净重建(否则残留已销毁项致提示不显示)。
            DestroyP2PromptStrip();
            _p2InteractPrompt = null; _p2RemovePrompt = null; _p2BaitPrompt = null;
            _p2SteerPrompt = null; _p2SteerGlyph1 = _p2SteerGlyph2 = null; _p2SteerPlus = _p2SteerText = null; _p2SteerFrame = -1;
            _p2SleepOverlay = null;   // 睡眠黑屏遮罩随 _p2HudCanvas 一起销毁 → 清引用,重建时干净重生成
            ResetP2Binoc();           
        }

        static void CreateP2StatHud()
        {
            int uiLayer = LayerMask.NameToLayer("UI");
            var p2 = player2;

            // 右半屏专用 UI 相机（镜像 _p1UiCamera：clearFlags=Depth，只渲染 UI 层，rect=右半屏）。
            _p2UiCamera = new GameObject("P2_UICamera").AddComponent<Camera>();
            _p2UiCamera.clearFlags    = CameraClearFlags.Depth;
            _p2UiCamera.cullingMask   = uiLayer >= 0 ? (1 << uiLayer) : 0;
            ApplySplitViewport(_p2UiCamera, true);
            _p2UiCamera.depth         = (p2 != null && p2.Camera != null ? p2.Camera.depth : 2f) + 10f;
            _p2UiCamera.nearClipPlane = 0.1f;
            _p2UiCamera.farClipPlane  = 20f;

            // 画布：ScreenSpaceCamera 绑右半屏相机 + 与 P1 相同的 CanvasScaler(match=0)。
            _p2HudCanvas = new GameObject("P2_StatHUD").AddComponent<Canvas>();
            _p2HudCanvas.renderMode    = RenderMode.ScreenSpaceCamera;
            _p2HudCanvas.worldCamera   = _p2UiCamera;
            _p2HudCanvas.planeDistance = 1f;

            var ch = ComponentManager<CanvasHelper>.Value;
            var srcCanvas = ch != null && ch.healthSlider != null ? ch.healthSlider.GetComponentInParent<Canvas>() : null;
            var myScaler  = _p2HudCanvas.gameObject.AddComponent<CanvasScaler>();
            var srcScaler = srcCanvas != null ? srcCanvas.GetComponent<CanvasScaler>() : null;
            myScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            myScaler.referenceResolution    = srcScaler != null ? srcScaler.referenceResolution : new Vector2(1920f, 1080f);
            myScaler.screenMatchMode        = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            myScaler.matchWidthOrHeight     = 0f; // 同 P1：按宽度
            if (srcScaler != null) myScaler.referencePixelsPerUnit = srcScaler.referencePixelsPerUnit;

            // 克隆原版 "Stat sliders" 面板（保留原锚点 → 由相机 rect 落在右半屏左下）。
            if (ch != null && ch.healthSlider != null)
            {
                var panel = ch.healthSlider.transform.parent;
                if (panel != null)
                {
                    _p2StatPanel = Object.Instantiate(panel.gameObject, _p2HudCanvas.transform);
                    _p2StatPanel.name = "P2_StatSliders";
                    foreach (var s in _p2StatPanel.GetComponentsInChildren<UISlider_Stat>(true))
                    {
                        string n = s.gameObject.name;
                        if (n == "Health") _p2HpSlider = s;
                        else if (n == "HungerBar") _p2HungerSlider = s;
                        else if (n == "Thirst") _p2ThirstSlider = s;
                        else { s.SetValue(0f); s.SetTargetValue(0f); }
                    }
                }

                // 克隆原版氧气径向表（fatigueParent），独立材质实例。
                if (ch.fatigueParent != null)
                {
                    string radialName = ch.fatigueRadial != null ? ch.fatigueRadial.name : null;
                    string bgName     = ch.fatigueBackground != null ? ch.fatigueBackground.name : null;
                    _p2OxyMeter = Object.Instantiate(ch.fatigueParent, _p2HudCanvas.transform);
                    _p2OxyMeter.name = "P2_OxygenMeter";
                    foreach (var img in _p2OxyMeter.GetComponentsInChildren<Image>(true))
                    {
                        if (radialName != null && img.name == radialName) _p2OxyRadial = img;
                        else if (bgName != null && img.name == bgName)     _p2OxyBg     = img;
                    }
                    if (_p2OxyRadial != null && _p2OxyRadial.material != null)
                    { _p2OxyMeterMat = Object.Instantiate(_p2OxyRadial.material); _p2OxyRadial.material = _p2OxyMeterMat; }
                    if (_p2OxyBg != null && _p2OxyBg.material != null)
                    { _p2OxyBgMat = Object.Instantiate(_p2OxyBg.material); _p2OxyBg.material = _p2OxyBgMat; }
                    _p2OxyMeter.SetActive(false);
                }
            }

            // 整棵 HUD 设到 UI 层，供 P2_UICamera(只渲染 UI 层)渲染。
            if (uiLayer >= 0) SetLayerRecursively(_p2HudCanvas.transform, uiLayer);

            LogV($"[P2HUD] created P2 HUD hp={_p2HpSlider!=null} hunger={_p2HungerSlider!=null} thirst={_p2ThirstSlider!=null} oxygen={_p2OxyRadial!=null}");
        }
    }
}
