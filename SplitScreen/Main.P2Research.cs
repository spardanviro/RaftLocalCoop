using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        
        //
        
        
        
        
        
        
        
        
        
        
        
        //
        
        
        
        static ResearchTable           _p2ResearchTable;
        static Inventory_ResearchTable _p2ResearchInv;
        static Inventory_ResearchTable _p2ResearchCloneInv;
        static GameObject              _p2ResearchCloneGo;
        static Slot                    _p2ResearchSlot;
        static Transform               _p2ResearchSlotOrigParent;
        static int                     _p2ResearchSlotOrigSibling;
        static int                     _p2ResearchedMirrored = -1;
        static bool                    _p2ResearchViewFixed;
        static ScrollRect              _p2ResearchScroll;
        static Scrollbar               _p2ResearchScrollbar;
        static RectTransform           _p2ResearchContent;
        static FieldInfo               s_riScrollbar, s_riContent;
        static FieldInfo               s_rtAnimator;
        static ResearchMenuItem        _p2HoverResearch;

        static Inventory_ResearchTable VanillaResearchInv => ComponentManager<Inventory_ResearchTable>.Value;

        
        static RectTransform _p2ResearchPrompt;
        static Image         _p2ResearchPromptGlyph;
        static Text          _p2ResearchPromptText;

        
        static FieldInfo s_riItemImage;

        

        internal static bool IsP2ResearchOpen => _p2ResearchTable != null;
        internal static bool IsP2ResearchClone(Inventory_ResearchTable inventory)
        {
            return inventory != null && ReferenceEquals(inventory, _p2ResearchCloneInv);
        }

        
        
        static RectTransform ResearchSlotRect()
        {
            if (_p2ResearchSlot == null) return null;
            return _p2ResearchSlot.rectTransform != null ? _p2ResearchSlot.rectTransform : _p2ResearchSlot.transform as RectTransform;
        }

        internal static void OpenP2ResearchTable(ResearchTable table)
        {
            if (_p2ResearchTable != null || table == null) return;
            var src = table.InventoryReference;
            if (src == null || table.Slot == null) { ModEntry.Logger.Log("[P2Research] 打开失败：研究面板/槽为空"); return; }
            if (table.IsOccupied) { ModEntry.Logger.Log("[P2Research] 该研究台已被占用,P2 不打开"); return; }
            var pinv = ComponentManager<PlayerInventory>.Value;
            if (pinv == null || _p2HudCanvas == null) { ModEntry.Logger.Log("[P2Research] 打开失败：背包/画布为空"); return; }

            ComponentManager<SoundManager>.Value?.PlayUI_OpenMenu();
            OpenP2Backpack(openCrafting: false);

            var clone = EnsureP2ResearchClone();
            if (clone == null) { ModEntry.Logger.Log("[P2Research] 克隆研究面板失败,仅开背包"); return; }

            _p2ResearchTable    = table;
            _p2ResearchCloneInv = clone;
            _p2ResearchInv      = clone;
            _p2ResearchSlot     = table.Slot;

            SetResearchAnimatorOpen(table, true);

            var srt = _p2ResearchSlot.transform;
            _p2ResearchSlotOrigParent  = srt.parent;
            _p2ResearchSlotOrigSibling = srt.GetSiblingIndex();
            if (clone.gridLayoutGroup != null)
            {
                srt.SetParent(clone.gridLayoutGroup.transform, false);
                srt.localScale = clone.slotPrefab != null ? clone.slotPrefab.transform.localScale : Vector3.one;
            }
            _p2ResearchSlot.gameObject.SetActive(true);
            _p2ResearchSlot.RefreshComponents();

            var panel = clone.transform as RectTransform;
            clone.gameObject.SetActive(true);
            clone.Show();
            float bpH = (ActiveP2BackpackInventory().transform as RectTransform).rect.height;
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 1f);
            panel.anchoredPosition = new Vector2(480f, bpH * 0.5f);
            panel.localScale = Vector3.one;
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) SetLayerRecursively(panel, uiLayer);
            foreach (var g in clone.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;

            if (s_riScrollbar == null) s_riScrollbar = typeof(Inventory_ResearchTable).GetField("scrollbar", BindingFlags.Instance | BindingFlags.NonPublic);
            if (s_riContent == null)   s_riContent   = typeof(Inventory_ResearchTable).GetField("content",   BindingFlags.Instance | BindingFlags.NonPublic);
            _p2ResearchScrollbar = s_riScrollbar?.GetValue(clone) as Scrollbar;
            _p2ResearchContent   = s_riContent?.GetValue(clone) as RectTransform;
            _p2ResearchScroll    = _p2ResearchContent != null ? _p2ResearchContent.GetComponentInParent<ScrollRect>()
                                                              : clone.GetComponentInChildren<ScrollRect>(true);

            _p2ResearchedMirrored = -1;
            _p2ResearchViewFixed = false;
            SyncP2ResearchMirror();

            if (_p2HeldIcon  != null) _p2HeldIcon.transform.SetAsLastSibling();
            if (_p2InvCursor != null) _p2InvCursor.SetAsLastSibling();
            LogV("[P2Research] 打开研究台(P2半屏克隆面板 + 设备真槽 + 镜像全局进度)");
        }

        static Inventory_ResearchTable EnsureP2ResearchClone()
        {
            if (_p2ResearchCloneInv != null) return _p2ResearchCloneInv;
            var srcInv = VanillaResearchInv;
            if (srcInv == null || _p2HudCanvas == null) return null;

            var savedValue = ComponentManager<Inventory_ResearchTable>.Value;
            bool wasActive = srcInv.gameObject.activeSelf;
            _p2ResearchCloneGo = UnityEngine.Object.Instantiate(srcInv.gameObject, _p2HudCanvas.transform, false);
            _p2ResearchCloneGo.name = "P2_ResearchPanel";
            SanitizeClonedUiRoot(_p2ResearchCloneGo);
            srcInv.gameObject.SetActive(wasActive);
            if (savedValue != null) ComponentManager<Inventory_ResearchTable>.Value = savedValue;

            _p2ResearchCloneInv = _p2ResearchCloneGo.GetComponent<Inventory_ResearchTable>();
            if (_p2ResearchCloneInv == null)
            {
                UnityEngine.Object.Destroy(_p2ResearchCloneGo);
                _p2ResearchCloneGo = null;
                return null;
            }

            if (s_riContent == null) s_riContent = typeof(Inventory_ResearchTable).GetField("content", BindingFlags.Instance | BindingFlags.NonPublic);
            var sContent = typeof(Inventory_ResearchTable).GetField("researchItemContent", BindingFlags.Instance | BindingFlags.NonPublic);
            DestroyChildrenImmediate(s_riContent?.GetValue(_p2ResearchCloneInv) as RectTransform);
            DestroyChildrenImmediate(sContent?.GetValue(_p2ResearchCloneInv) as RectTransform);

            _p2ResearchCloneGo.SetActive(false);
            LogV("[P2Research] 克隆研究面板已创建(待 Start 重建配方列表)");
            return _p2ResearchCloneInv;
        }

        static void DestroyChildrenImmediate(RectTransform parent)
        {
            if (parent == null) return;
            for (int i = parent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(parent.GetChild(i).gameObject);
        }

        internal static CraftingMenu GetVanillaCraftingMenuForP2Research()
        {
            foreach (var menu in Resources.FindObjectsOfTypeAll<CraftingMenu>())
            {
                if (menu == null || IsP2CraftingMenu(menu)) continue;
                if (menu.AllRecipes != null && menu.AllRecipes.Count > 0) return menu;
            }
            return null;
        }

        static void SyncP2ResearchMirror()
        {
            var clone = _p2ResearchCloneInv;
            var vanilla = VanillaResearchInv;
            if (clone == null || vanilla == null) return;
            var cloneItems = clone.GetMenuItems();
            var vanillaItems = vanilla.GetMenuItems();
            if (cloneItems == null || vanillaItems == null || cloneItems.Count == 0) return;

            if (!_p2ResearchViewFixed)
            {
                int uiLayer = LayerMask.NameToLayer("UI");
                if (uiLayer >= 0) SetLayerRecursively(clone.transform as RectTransform, uiLayer);
                foreach (var cm in cloneItems)
                    if (cm != null)
                    {
                        var lp = cm.transform.localPosition;
                        if (lp.z != 0f) cm.transform.localPosition = new Vector3(lp.x, lp.y, 0f);
                    }
                foreach (var g in clone.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                _p2ResearchViewFixed = true;
            }

            bool dirty = false;

            var researched = vanilla.GetResearchedItems();
            int rc = researched != null ? researched.Count : 0;
            if (rc != _p2ResearchedMirrored)
            {
                if (researched != null)
                    foreach (var ri in researched)
                        foreach (var cm in cloneItems)
                            if (cm != null) cm.Research(ri);
                _p2ResearchedMirrored = rc;
                dirty = true;
            }

            foreach (var cm in cloneItems)
            {
                if (cm == null || cm.GetItem() == null) continue;
                ResearchMenuItem vm = null;
                for (int i = 0; i < vanillaItems.Count; i++)
                    if (vanillaItems[i] != null && vanillaItems[i].GetItem() == cm.GetItem()) { vm = vanillaItems[i]; break; }
                if (vm == null) continue;
                if (vm.Learned && !cm.Learned) { cm.Learn(); dirty = true; }
                if (cm.gameObject.activeSelf != vm.gameObject.activeSelf) { cm.gameObject.SetActive(vm.gameObject.activeSelf); dirty = true; }
            }

            if (dirty)
            {
                clone.SortMenuItems();
                if (_p2ResearchContent != null)
                {
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_p2ResearchContent);
                    var vp = _p2ResearchScroll != null ? _p2ResearchScroll.viewport : null;
                    if (vp != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(vp);
                }
            }
        }

        internal static void CloseP2ResearchTable()
        {
            if (_p2ResearchTable == null) return;
            SetResearchAnimatorOpen(_p2ResearchTable, false);

            if (_p2ResearchSlot != null)
            {
                var srt = _p2ResearchSlot.transform;
                if (_p2ResearchSlotOrigParent != null)
                {
                    srt.SetParent(_p2ResearchSlotOrigParent, false);
                    srt.SetSiblingIndex(_p2ResearchSlotOrigSibling);
                }
                _p2ResearchSlot.gameObject.SetActive(false);
            }

            if (_p2ResearchCloneGo != null) _p2ResearchCloneGo.SetActive(false);
            if (_p2ResearchPrompt != null) _p2ResearchPrompt.gameObject.SetActive(false);

            _p2ResearchTable = null; _p2ResearchInv = null;
            _p2ResearchSlot = null; _p2ResearchSlotOrigParent = null;
            _p2ResearchScroll = null; _p2ResearchScrollbar = null; _p2ResearchContent = null; _p2HoverResearch = null;

            if (_p2BackpackOpen) CloseP2Backpack();
            LogV("[P2Research] 关闭研究台");
        }

        
        
        static void SetResearchAnimatorOpen(ResearchTable table, bool open)
        {
            if (table == null) return;
            try
            {
                if (s_rtAnimator == null)
                    s_rtAnimator = typeof(ResearchTable).GetField("animator", BindingFlags.Instance | BindingFlags.NonPublic);
                var anim = s_rtAnimator?.GetValue(table) as Animator;
                if (anim != null) anim.SetBool("Open", open);
            }
            catch (Exception e) { ModEntry.Logger.Log("[P2Research] animator ex: " + e.Message); }
        }

        
        static RectTransform ResearchItemIconRect(ResearchMenuItem mi)
        {
            if (mi == null) return null;
            if (s_riItemImage == null)
                s_riItemImage = typeof(ResearchMenuItem).GetField("itemImage", BindingFlags.Instance | BindingFlags.NonPublic);
            var img = s_riItemImage?.GetValue(mi) as Image;
            return img != null ? img.rectTransform : mi.transform as RectTransform;
        }

        
        static void TickP2Research(Gamepad gp, Vector2 cursorScreen)
        {
            _p2HoverResearch = null;
            if (_p2ResearchInv == null || _p2InvCursor == null) return;

            SyncP2ResearchMirror();
            UpdateResearchPrompt();
            if (gp.rightTrigger.wasPressedThisFrame) P2ResearchTryResearch();

            var pr = _p2InvCursor.parent as RectTransform;
            
            
            var viewport = (_p2ResearchScroll != null && _p2ResearchScroll.viewport != null) ? _p2ResearchScroll.viewport
                         : (_p2ResearchContent != null ? _p2ResearchContent.parent as RectTransform : _p2ResearchInv.transform as RectTransform);
            bool inPanel = viewport != null && RectTransformUtility.RectangleContainsScreenPoint(viewport, cursorScreen, _p2UiCamera);

            
            float ry = gp.rightStick.ReadValue().y;
            if (inPanel && Mathf.Abs(ry) > 0.15f)
            {
                const float pxPerSec = 700f;
                if (_p2ResearchScroll != null)
                {
                    var c = _p2ResearchScroll.content; var vp = _p2ResearchScroll.viewport;
                    float scrollable = (c != null && vp != null) ? Mathf.Max(1f, c.rect.height - vp.rect.height) : 1000f;
                    _p2ResearchScroll.verticalNormalizedPosition =
                        Mathf.Clamp01(_p2ResearchScroll.verticalNormalizedPosition + ry * pxPerSec * Time.unscaledDeltaTime / scrollable);
                }
                else if (_p2ResearchScrollbar != null)
                    _p2ResearchScrollbar.value = Mathf.Clamp01(_p2ResearchScrollbar.value + ry * 0.5f * Time.unscaledDeltaTime);
                else if (_p2ResearchContent != null && viewport != null)
                {
                    
                    float maxScroll = Mathf.Max(0f, _p2ResearchContent.rect.height - viewport.rect.height);
                    float ny = Mathf.Clamp(_p2ResearchContent.anchoredPosition.y - ry * pxPerSec * Time.unscaledDeltaTime, 0f, maxScroll);
                    _p2ResearchContent.anchoredPosition = new Vector2(_p2ResearchContent.anchoredPosition.x, ny);
                }
            }

            var items = _p2ResearchInv.GetMenuItems();
            if (items == null || pr == null) return;

            float scale  = (_p2HudCanvas != null && _p2HudCanvas.scaleFactor > 0f) ? _p2HudCanvas.scaleFactor : 1f;
            float radius = P2MagnetRadiusPx * scale;
            ResearchMenuItem hit = null, nearest = null;
            float bestSq = radius * radius; Vector2 bestCenter = default, bestDiff = default;
            foreach (var mi in items)
            {
                if (mi == null || !mi.gameObject.activeInHierarchy) continue;
                var rt = mi.transform as RectTransform; if (rt == null) continue;
                var iconRt = ResearchItemIconRect(mi);                              
                Vector2 c = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, iconRt.TransformPoint(iconRt.rect.center));
                if (RectTransformUtility.RectangleContainsScreenPoint(rt, cursorScreen, _p2UiCamera))   
                { hit = mi; bestCenter = c; bestDiff = c - cursorScreen; break; }
                float d = (c - cursorScreen).sqrMagnitude;
                if (d < bestSq) { bestSq = d; nearest = mi; bestCenter = c; bestDiff = c - cursorScreen; }
            }
            var chosen = hit ?? nearest;
            if (chosen == null) return;

            
            
            
            var srt = ResearchSlotRect();
            if (srt != null && srt.gameObject.activeInHierarchy)
            {
                Vector2 sc = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, srt.TransformPoint(srt.rect.center));
                if (RectTransformUtility.RectangleContainsScreenPoint(srt, cursorScreen, _p2UiCamera)
                    || (sc - cursorScreen).sqrMagnitude <= bestDiff.sqrMagnitude)
                    return;   
            }

            _p2HoverResearch = chosen;

            Vector2 stick = gp.leftStick.ReadValue();
            float escape = P2MagnetEscape * scale;
            if (bestDiff.sqrMagnitude >= (stick * escape).sqrMagnitude)   
            {
                float thr = P2MagnetCenterThr * scale;
                Vector2 target = (bestDiff.sqrMagnitude > thr * thr)
                    ? cursorScreen + bestDiff.normalized * (P2MagnetPullSpeed * scale) * Time.unscaledDeltaTime
                    : bestCenter;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(pr, target, _p2UiCamera, out var local))
                { _p2InvCursorPos = local; _p2InvCursor.anchoredPosition = local; }
            }
        }

        
        static bool P2ResearchTryLearnHovered()
        {
            if (_p2ResearchTable == null || _p2HoverResearch == null) return false;
            try
            {
                var cm = _p2HoverResearch;
                if (cm.Learned || cm.SortBingoPercent < 1f) return true;
                var vitems = VanillaResearchInv?.GetMenuItems();
                if (vitems != null)
                    foreach (var vm in vitems)
                        if (vm != null && vm.GetItem() == cm.GetItem())
                        {
                            if (!vm.Learned) { vm.LearnButton(); _p2ResearchedMirrored = -1; }
                            break;
                        }
            }
            catch (Exception e) { ModEntry.Logger.Log("[P2Research] learn ex: " + e.Message); }
            return true;
        }

        
        static void P2ResearchTryResearch()
        {
            if (_p2ResearchTable == null || _p2ResearchSlot == null) return;
            var inst = _p2ResearchSlot.itemInstance;
            if (inst == null || inst.baseItem == null) return;
            var vanilla = VanillaResearchInv;
            if (vanilla == null) return;
            try
            {
                var bi = inst.baseItem;
                if (vanilla.CanResearchItem(bi))
                {
                    vanilla.Research(bi);
                    _p2ResearchSlot.RemoveItem(1);
                    _p2ResearchedMirrored = -1;
                }
            }
            catch (Exception e) { ModEntry.Logger.Log("[P2Research] research ex: " + e.Message); }
        }

        
        static void UpdateResearchPrompt()
        {
            EnsureResearchPrompt();
            if (_p2ResearchPrompt == null) return;

            bool show = false;
            var vanilla = VanillaResearchInv;
            if (_p2ResearchSlot != null && _p2ResearchSlot.itemInstance != null
                && _p2ResearchSlot.itemInstance.baseItem != null && vanilla != null)
                show = vanilla.CanResearchItem(_p2ResearchSlot.itemInstance.baseItem);

            if (_p2ResearchPrompt.gameObject.activeSelf != show) _p2ResearchPrompt.gameObject.SetActive(show);
            if (!show) return;

            var srt = ResearchSlotRect();
            var pr  = _p2InvCursor != null ? _p2InvCursor.parent as RectTransform : null;
            if (srt != null && pr != null)
            {
                Vector2 sc = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, srt.TransformPoint(srt.rect.center));
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(pr, sc, _p2UiCamera, out var local))
                {
                    
                    float halfW = srt.rect.width * 0.5f;   
                    _p2ResearchPrompt.anchoredPosition = local + new Vector2(halfW + 8f, 0f);
                }
            }
            _p2ResearchPrompt.SetAsLastSibling();
        }

        static void EnsureResearchPrompt()
        {
            if (_p2ResearchPrompt != null || _p2HudCanvas == null) return;
            var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            int uiLayer = LayerMask.NameToLayer("UI");

            var go = new GameObject("P2_ResearchPrompt", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>(); rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);   
            rt.sizeDelta = new Vector2(180f, 48f);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleLeft; h.spacing = 6f;
            h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            h.childControlWidth = true; h.childControlHeight = true;
            if (uiLayer >= 0) go.layer = uiLayer;
            var cv = go.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 29500;
            _p2ResearchPrompt = rt;

            var gi = new GameObject("G", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gi.GetComponent<RectTransform>().SetParent(rt, false);
            _p2ResearchPromptGlyph = gi.GetComponent<Image>();
            _p2ResearchPromptGlyph.preserveAspect = true; _p2ResearchPromptGlyph.raycastTarget = false;
            var sp = GlyphFor("Use");
            if (sp != null) _p2ResearchPromptGlyph.sprite = sp; else _p2ResearchPromptGlyph.color = new Color(1f, 1f, 1f, 0.4f);
            var le = gi.AddComponent<LayoutElement>(); le.preferredWidth = 40f; le.preferredHeight = 40f;
            if (uiLayer >= 0) gi.layer = uiLayer;

            var ti = new GameObject("T", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ti.GetComponent<RectTransform>().SetParent(rt, false);
            _p2ResearchPromptText = ti.GetComponent<Text>();
            _p2ResearchPromptText.text = Helper.GetTerm("Research"); _p2ResearchPromptText.fontSize = 22; _p2ResearchPromptText.color = Color.white;
            _p2ResearchPromptText.alignment = TextAnchor.MiddleLeft; _p2ResearchPromptText.raycastTarget = false;
            _p2ResearchPromptText.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (arial) _p2ResearchPromptText.font = arial;
            ti.AddComponent<LayoutElement>().minHeight = 36f;
            var sh = ti.AddComponent<Shadow>(); sh.effectColor = Color.black; sh.effectDistance = new Vector2(1f, -1f);
            if (uiLayer >= 0) ti.layer = uiLayer;

            go.SetActive(false);
        }

        internal static void DestroyP2ResearchPrompt()
        {
            if (_p2ResearchPrompt != null) UnityEngine.Object.Destroy(_p2ResearchPrompt.gameObject);
            _p2ResearchPrompt = null; _p2ResearchPromptGlyph = null; _p2ResearchPromptText = null;
        }

        internal static void DestroyP2ResearchClone()
        {
            if (_p2ResearchSlot != null && _p2ResearchSlotOrigParent != null)
            {
                _p2ResearchSlot.transform.SetParent(_p2ResearchSlotOrigParent, false);
                _p2ResearchSlot.gameObject.SetActive(false);
            }
            if (_p2ResearchCloneGo != null) UnityEngine.Object.Destroy(_p2ResearchCloneGo);
            _p2ResearchCloneGo = null; _p2ResearchCloneInv = null;
            _p2ResearchTable = null; _p2ResearchInv = null; _p2ResearchSlot = null;
            _p2ResearchSlotOrigParent = null; _p2ResearchedMirrored = -1;
        }
    }
}
