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
        
        
        
        
        
        static bool _p2BuildMenuOpen;
        internal static bool IsP2BuildMenuOpen => _p2BuildMenuOpen;
        static FieldInfo _fBmPanel, _fBmBlockCreator;
        static GameObject _bmPanel;
        static Transform _bmOrigParent; static int _bmOrigSibling; static bool _bmOrigActive;
        static object _bmOrigBlockCreator;
        static RectTransform _bmContainer;              
        static GameObject _bmBrownBg; static bool _bmBrownBgWasActive;
        static GameObject _bmBgClone;                   
        static int _bmDefaultFrames;                    

        static Component _p2BmCurrent;          
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
            if (_p2HudCanvas == null) return;                  

            _bmOrigBlockCreator = _fBmBlockCreator?.GetValue(bm);
            _fBmBlockCreator?.SetValue(bm, bc);                 
            HideVanillaBuildCostCursor();

            var rt = _bmPanel.transform as RectTransform;
            _bmOrigParent = rt.parent; _bmOrigSibling = rt.GetSiblingIndex(); _bmOrigActive = _bmPanel.activeSelf;

            
            
            
            var parentRT = _bmOrigParent as RectTransform;
            Vector2 vanillaSize = (parentRT != null && parentRT.rect.size.x > 1f) ? parentRT.rect.size : new Vector2(1920f, 1080f);
            var container = EnsureBmContainer(_p2HudCanvas, vanillaSize);

            rt.SetParent(container, false);                    
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) SetLayerRecursively(rt, uiLayer);

            
            
            var bg = FindChildByName(_bmPanel.transform, "BrownBackground");
            _bmBrownBg = bg != null ? bg.gameObject : null;
            EnsureBmBg(container, _bmBrownBg);     
            if (_bmBrownBg != null) { _bmBrownBgWasActive = _bmBrownBg.activeSelf; _bmBrownBg.SetActive(false); }

            _bmPanel.SetActive(true);

            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(false);   
            _p2BuildMenuOpen = true;
            _p2BmCurrent = null; _p2BmNavCd = 0f;
            
            
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
                rt.SetParent(_bmOrigParent, false); rt.SetSiblingIndex(_bmOrigSibling);   
                _bmPanel.SetActive(_bmOrigActive);
            }
            if (_bmBrownBg != null) { _bmBrownBg.SetActive(_bmBrownBgWasActive); _bmBrownBg = null; }   
            if (_bmContainer != null) _bmContainer.gameObject.SetActive(false);                        
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
            _bmBgClone.transform.SetAsFirstSibling();   
        }

        
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

        
        internal static void TickP2BuildMenu()
        {
            if (!_p2BuildMenuOpen || _bmPanel == null) return;
            var gp = GetP2BoundGamepad(); if (gp == null) return;

            
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
                if (dot < 0.5f) continue;                       
                float score = v.sqrMagnitude / (dot * dot);     
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
