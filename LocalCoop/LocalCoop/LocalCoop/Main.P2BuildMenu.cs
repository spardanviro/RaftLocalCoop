using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P2 建造菜单(原版手柄效果)：按住 LT 打开真原版 BuildMenu(reparent 到 P2 半屏、
        //  保留原版位置/大小=和 P1 一致)，松开 LT 关闭。无光标，左摇杆在菜单项间【方向导航】，
        //  悬停项调 OnEnter()(展开子类/选方块)。reparent 时把 BuildMenu.blockCreator 换成 P2 的。
        // ══════════════════════════════════════════════════════════════════
        static bool _p2BuildMenuOpen;
        internal static bool IsP2BuildMenuOpen => _p2BuildMenuOpen;
        static FieldInfo _fBmPanel, _fBmBlockCreator;
        static GameObject _bmPanel;
        static Transform _bmOrigParent; static int _bmOrigSibling; static bool _bmOrigActive;
        static object _bmOrigBlockCreator;
        static RectTransform _bmContainer;              // 原版尺寸容器，居中在 P2 HUD 画布上(保留 buildPanel 原版锚点)
        static GameObject _bmBrownBg; static bool _bmBrownBgWasActive;
        static GameObject _bmBgClone;                   // 克隆的面板背景(剥离 WorldSpace 画布)
        static int _bmDefaultFrames;                    // 打开后头几帧反复展开默认分类(避开 Start 收起)

        static Component _p2BmCurrent;          // 当前选中的菜单项(三种之一)
        static Image     _p2BmHiImg; static Color _p2BmHiPrev;
        static float     _p2BmNavCd;
        static readonly Color P2BmHilite = new Color(1f, 0.85f, 0.3f, 1f);
        const float P2BmNavInterval = 0.16f;

        internal static void OpenP2BuildMenu()
        {
            if (_p2BuildMenuOpen) return;
            var bm = ComponentManager<BuildMenu>.Value;
            var bc = GetP2BlockCreator();
            if (bm == null || bc == null || _p2HudCanvas == null) return;
            if (_fBmPanel == null)
            {
                _fBmPanel        = typeof(BuildMenu).GetField("buildPanel", BindingFlags.Instance | BindingFlags.NonPublic);
                _fBmBlockCreator = typeof(BuildMenu).GetField("blockCreator", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            _bmPanel = _fBmPanel?.GetValue(bm) as GameObject;
            if (_bmPanel == null) return;
            if (_p2HudCanvas == null) return;                  // 用【已验证能限制在右半屏】的 HUD 画布

            _bmOrigBlockCreator = _fBmBlockCreator?.GetValue(bm);
            _fBmBlockCreator?.SetValue(bm, bc);                 // SelectBlock → P2 的建造器
            HideVanillaBuildCostCursor();

            var rt = _bmPanel.transform as RectTransform;
            _bmOrigParent = rt.parent; _bmOrigSibling = rt.GetSiblingIndex(); _bmOrigActive = _bmPanel.activeSelf;

            // 容器 = 原版画布的逻辑尺寸(buildPanel 子面板就是为这个尺寸排版的)，居中放在 P2 半屏 HUD 画布上。
            // 关键修正：【不再覆写 buildPanel 的锚点】(之前强行 stretch-fill 把信息/分类面板甩到两端)。保留原版
            // 锚点 → buildPanel 在 1920x1080 容器内还原成原版的【紧凑居中】布局，再随容器缩放/居中进半屏。
            var parentRT = _bmOrigParent as RectTransform;
            Vector2 vanillaSize = (parentRT != null && parentRT.rect.size.x > 1f) ? parentRT.rect.size : new Vector2(1920f, 1080f);
            var container = EnsureBmContainer(_p2HudCanvas, vanillaSize);

            rt.SetParent(container, false);                    // 保留 buildPanel 本地锚点/位置/大小(=原版布局)
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) SetLayerRecursively(rt, uiLayer);

            // 原版 BrownBackground 自带 WorldSpace 画布(P1 共享)→ 直接启用会糊满屏。改为：禁用它，
            // 用一张【复制其外观】的自有 Image 填满容器(=面板簇区域 402x608) 作为面板背景。
            var bg = FindChildByName(_bmPanel.transform, "BrownBackground");
            _bmBrownBg = bg != null ? bg.gameObject : null;
            EnsureBmBg(container, _bmBrownBg);     // 克隆原版背景(剥离其 WorldSpace 画布) → 精确材质
            if (_bmBrownBg != null) { _bmBrownBgWasActive = _bmBrownBg.activeSelf; _bmBrownBg.SetActive(false); }

            _bmPanel.SetActive(true);

            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(false);   // 左摇杆专控菜单导航
            _p2BuildMenuOpen = true;
            _p2BmCurrent = null; _p2BmNavCd = 0f;
            // 不在打开当帧选默认项：buildPanel.SetActive 首次会触发各分类 Start()→SetHorizontalParentState(false)
            // 把展开收掉。改为头几帧在 Tick 里反复展开(Start 已跑完后才生效)。
            _bmDefaultFrames = 4;
            LogV("[P2BuildMenu] 打开(按住LT)");
        }

        internal static void CloseP2BuildMenu()
        {
            if (!_p2BuildMenuOpen) return;
            _p2BuildMenuOpen = false;
            ClearBmHighlight();
            _p2BmCurrent = null;
            var bm = ComponentManager<BuildMenu>.Value;
            if (_bmPanel != null && _bmOrigParent != null)
            {
                var rt = _bmPanel.transform as RectTransform;
                rt.SetParent(_bmOrigParent, false); rt.SetSiblingIndex(_bmOrigSibling);   // 锚点未改，无需还原
                _bmPanel.SetActive(_bmOrigActive);
            }
            if (_bmBrownBg != null) { _bmBrownBg.SetActive(_bmBrownBgWasActive); _bmBrownBg = null; }   // 还原棕色背景给 P1
            if (_bmContainer != null) _bmContainer.gameObject.SetActive(false);                        // 连同自有背景一起隐藏
            if (bm != null && _fBmBlockCreator != null && _bmOrigBlockCreator != null)
                _fBmBlockCreator.SetValue(bm, _bmOrigBlockCreator);
            _bmOrigParent = null; _bmOrigBlockCreator = null; _bmPanel = null;
            try
            {
                if (BuildMenuItem_SelectMainCategory.CurrentCategory != null)
                { BuildMenuItem_SelectMainCategory.CurrentCategory.SetHorizontalParentState(false); BuildMenuItem_SelectMainCategory.CurrentCategory = null; }
            }
            catch (Exception e) { LogV("[P2BuildMenu] Clear current category ignored: " + e.Message); }
            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(true);
            LogV("[P2BuildMenu] 关闭(松开LT)");
        }

        internal static void ForceResetP2BuildMenu()
        {
            if (_p2BuildMenuOpen)
            {
                try { CloseP2BuildMenu(); }
                catch (Exception e) { LogV("[P2BuildMenu] ForceReset close ignored: " + e.Message); }
            }
            _p2BuildMenuOpen = false;
        }

        // 容器：放在 P2 HUD 画布(已验证限制在右半屏)上，居中。尺寸=原版画布逻辑尺寸(buildPanel 排版基准)，
        // scale=1。buildPanel 在其中保留原版锚点 → 还原原版【紧凑居中】布局，限制在 P2 半屏。
        static RectTransform EnsureBmContainer(Canvas hudCanvas, Vector2 size)
        {
            if (_bmContainer == null)
            {
                var go = new GameObject("P2BuildMenuContainer", typeof(RectTransform));
                _bmContainer = go.GetComponent<RectTransform>();
            }
            if (_bmContainer.parent != hudCanvas.transform) _bmContainer.SetParent(hudCanvas.transform, false);
            _bmContainer.anchorMin = _bmContainer.anchorMax = new Vector2(0.5f, 0.5f);
            _bmContainer.pivot = new Vector2(0.5f, 0.5f);
            _bmContainer.sizeDelta = size;
            _bmContainer.anchoredPosition = Vector2.zero;
            _bmContainer.localScale = Vector3.one;
            _bmContainer.gameObject.SetActive(true);
            return _bmContainer;
        }

        // 面板背景：克隆原版 BrownBackground 整个物体(含子物体=精确材质)，剥离其自带 WorldSpace 画布
        // (否则会糊满屏)，使其在 _p2HudCanvas 上嵌套渲染、填满容器(=面板簇区域)，置于最底层。
        static void EnsureBmBg(RectTransform container, GameObject src)
        {
            if (_bmBgClone == null && src != null)
            {
                _bmBgClone = Object.Instantiate(src, container);
                _bmBgClone.name = "P2BmBackground";
                foreach (var cv in _bmBgClone.GetComponentsInChildren<Canvas>(true)) Object.Destroy(cv);
                foreach (var gr in _bmBgClone.GetComponentsInChildren<GraphicRaycaster>(true)) Object.Destroy(gr);
                var rt = _bmBgClone.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero; rt.localScale = Vector3.one;
                int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) SetLayerRecursively(rt, uiLayer);
            }
            if (_bmBgClone == null) return;
            if (_bmBgClone.transform.parent != container) _bmBgClone.transform.SetParent(container, false);
            _bmBgClone.SetActive(true);
            _bmBgClone.transform.SetAsFirstSibling();   // 置底，buildPanel 内容在其上
        }

        // 持锤时按住 LT 开/松开关(由 TickP2Build 调用)。
        static void TickP2BuildMenuHold(Gamepad gp)
        {
            bool lt = gp.leftTrigger.isPressed;
            // 放锚那次 LT 的 release-gate:LT 仍按着(是放锚按压的延续)则不开菜单,须先松开。
            if (_p2BuildMenuBlockUntilLtRelease)
            {
                if (!lt) _p2BuildMenuBlockUntilLtRelease = false;
                else return;
            }
            if (lt && !_p2BuildMenuOpen) OpenP2BuildMenu();
            else if (!lt && _p2BuildMenuOpen) CloseP2BuildMenu();
        }

        // 每帧(Runtime.Tick):菜单打开时左摇杆方向导航 + 悬停 OnEnter。
        internal static void TickP2BuildMenu()
        {
            if (!_p2BuildMenuOpen || _bmPanel == null) return;
            var gp = GetP2BoundGamepad(); if (gp == null) return;

            // 头几帧反复选默认项(等各分类 Start() 跑完后再展开，否则会被 Start 收掉)。
            if (_bmDefaultFrames > 0) { _bmDefaultFrames--; SelectDefaultBmItem(); }
            else if (_p2BmCurrent == null || !_p2BmCurrent.gameObject.activeInHierarchy) SelectDefaultBmItem();

            _p2BmNavCd -= Time.unscaledDeltaTime;
            Vector2 stick = gp.leftStick.ReadValue();
            if (_p2BmNavCd <= 0f && stick.sqrMagnitude > 0.30f && _p2BmCurrent != null)
            {
                Vector2 dir = (Mathf.Abs(stick.x) >= Mathf.Abs(stick.y))
                    ? new Vector2(Mathf.Sign(stick.x), 0f) : new Vector2(0f, Mathf.Sign(stick.y));
                var next = FindNextBmItem(dir);
                if (next != null) { SetBmCurrent(next); BmOnEnter(next); _p2BmNavCd = P2BmNavInterval; }
            }
        }

        static void SelectDefaultBmItem()
        {
            if (_bmPanel == null) return;
            foreach (var c in _bmPanel.GetComponentsInChildren<BuildMenuItem_SelectMainCategory>(false))
            { SetBmCurrent(c); BmOnEnter(c); return; }
        }

        static void BmOnEnter(Component c)
        {
            try
            {
                if (c is BuildMenuItem_SelectMainCategory m) m.OnEnter();
                else if (c is BuildMenuItem_SelectSubCategory s) s.OnEnter();
                else if (c is BuildMenuItem_SelectBlock b) b.OnEnter();
            }
            catch (Exception e) { LogV("[P2BuildMenu] hover enter ignored: " + e.Message); }
        }

        static Vector2 BmScreen(Component c)
        {
            var rt = c.transform as RectTransform;
            return RectTransformUtility.WorldToScreenPoint(_p2UiCamera, rt.TransformPoint(rt.rect.center));
        }

        // 从当前项出发，找 dir 方向上最近的菜单项(原版 DPadNavigate 同思路)。
        static Component FindNextBmItem(Vector2 dir)
        {
            if (_p2BmCurrent == null) return null;
            Vector2 cur = BmScreen(_p2BmCurrent);
            Component best = null; float bestScore = float.MaxValue;
            foreach (var c in CollectBmItems())
            {
                if (c == _p2BmCurrent || c == null || !c.gameObject.activeInHierarchy) continue;
                Vector2 v = BmScreen(c) - cur;
                if (v.sqrMagnitude < 1f) continue;
                float dot = Vector2.Dot(v.normalized, dir);
                if (dot < 0.5f) continue;                       // 必须大致在该方向
                float score = v.sqrMagnitude / (dot * dot);     // 偏方向、近距离优先
                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        static List<Component> CollectBmItems()
        {
            var list = new List<Component>();
            if (_bmPanel == null) return list;
            foreach (var c in _bmPanel.GetComponentsInChildren<BuildMenuItem_SelectMainCategory>(false)) list.Add(c);
            foreach (var c in _bmPanel.GetComponentsInChildren<BuildMenuItem_SelectSubCategory>(false)) list.Add(c);
            foreach (var c in _bmPanel.GetComponentsInChildren<BuildMenuItem_SelectBlock>(false)) list.Add(c);
            return list;
        }

        static void SetBmCurrent(Component c)
        {
            ClearBmHighlight();
            _p2BmCurrent = c;
            if (c != null)
            {
                _p2BmHiImg = c.GetComponent<Image>();
                if (_p2BmHiImg != null) { _p2BmHiPrev = _p2BmHiImg.color; _p2BmHiImg.color = P2BmHilite; }
            }
        }

        static void ClearBmHighlight()
        {
            if (_p2BmHiImg != null) _p2BmHiImg.color = _p2BmHiPrev;
            _p2BmHiImg = null;
        }
    }
}
