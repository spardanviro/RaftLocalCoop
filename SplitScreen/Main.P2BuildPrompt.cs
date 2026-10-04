using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P2 建造/工具 手柄按键提示条(复刻原版手柄提示外观)
        //  底部居中横排，每项 = [手柄按键字形][说明文字]。字形复用原版
        //  SO_ControllerActions(DisplayText.xboxControllerActions)的精灵。
        //  由 TickP2Build 每帧按上下文驱动(对着幽灵方块=旋转；对着已有方块=模块选择…)。
        // ══════════════════════════════════════════════════════════════════
        static SO_ControllerActions _ctrlActions;
        static RectTransform _p2PromptStrip;
        struct PromptItem { public RectTransform rt; public Image glyph; public Text text; }
        static readonly List<PromptItem> _p2PromptItems = new List<PromptItem>();
        const int P2PromptMax = 4;

        internal const string P2PadConfirm = "A";
        internal const string P2PadCancel = "B";
        internal const string P2PadInteract = "X";
        internal const string P2PadMenu = "Y";
        internal const string P2PadUse = "RT";
        internal const string P2PadContext = "LT";
        internal const string P2PadRemove = "D-Pad Left";
        internal const string P2PadRotate = "D-Pad Right";
        internal const string P2PadBlockPick = "Right Stick Press";
        internal const string P2PadRotateAxis = "RSH";

        // 反射拿到原版手柄字形表(从任一 DisplayText 的 xboxControllerActions)。
        static SO_ControllerActions GetCtrlActions()
        {
            if (_ctrlActions != null) return _ctrlActions;
            var dtm = ComponentManager<DisplayTextManager>.Value;
            if (dtm == null) return null;
            var fDT = typeof(DisplayTextManager).GetField("displayTexts", BindingFlags.Instance | BindingFlags.NonPublic);
            var arr = fDT?.GetValue(dtm) as DisplayText[];
            if (arr == null || arr.Length == 0) return null;
            var fXbox = typeof(DisplayText).GetField("xboxControllerActions", BindingFlags.Instance | BindingFlags.NonPublic);
            _ctrlActions = fXbox?.GetValue(arr[0]) as SO_ControllerActions;
            return _ctrlActions;
        }

        static string NormalizeP2GlyphKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            switch (key)
            {
                case "Confirm":
                case "Jump":
                    return P2PadConfirm;
                case "Cancel":
                case "Back":
                    return P2PadCancel;
                case "Interact":
                    return P2PadInteract;
                case "Menu":
                    return P2PadMenu;
                case "Use":
                case "Fire":
                case "Primary":
                    return P2PadUse;
                case "Context":
                case "BuildMenu":
                case "Alt":
                    return P2PadContext;
                case "Remove":
                    return P2PadRemove;
                case "Rotate":
                    return P2PadRotate;
                case "BlockPick":
                    return P2PadBlockPick;
                case "RotateAxis":
                    return P2PadRotateAxis;
                default:
                    return key;
            }
        }

        static Sprite GlyphFor(string key)
        {
            var a = GetCtrlActions();
            key = NormalizeP2GlyphKey(key);
            if (a != null && a.GetSprite(key, out var s)) return s;
            return null;
        }

        // ── 捕获原版每帧发出的建造/工具提示(在 BlockCreator/Hammer.Update 内正确时机发出)，映射成字形 ──
        //  解决时序矛盾：原版先射线→生成幽灵→才决定显示旋转提示；我们不另算，直接拿它的结果。
        static int _capFrame = -1;
        static readonly List<KeyValuePair<string, string>> _capList = new List<KeyValuePair<string, string>>();
        static string _tBuildMenu, _tRotate, _tRotateSmooth, _tBlockPick;

        static string _tFillSalt, _tFillWater, _tPour, _tDrink, _tEat, _tSelectBait;
        static int _p2BaitPromptFrame = -1;   // 最近一次捕获"选择诱饵"的帧
        static void EnsurePromptTerms()
        {
            if (_tBuildMenu != null) return;
            _tBuildMenu    = Helper.GetTerm("Game/BuildMenu");
            _tRotate       = Helper.GetTerm("Controls/Rotate");
            _tRotateSmooth = Helper.GetTerm("Game/RotateSmooth");
            _tBlockPick    = Helper.GetTerm("Controls/BlockPick");
            _tFillSalt     = Helper.GetTerm("Game/FillSaltWater");
            _tFillWater    = Helper.GetTerm("Game/FillWater");
            _tPour         = Helper.GetTerm("Game/PourLiquid");
            _tDrink        = Helper.GetTerm("Game/Drink");
            _tEat          = Helper.GetTerm("Game/Eat");
            _tSelectBait   = Helper.GetTerm("Menu/Fishing/OpenBaitMenu");   // 持竿"选择诱饵"提示(vanilla 用 RMB)
        }

        static string MapTermToGlyph(string text)
        {
            EnsurePromptTerms();
            if (text == _tBuildMenu) return "Context";
            if (text == _tRotate || text == _tRotateSmooth) return "Rotate";
            if (text == _tBlockPick) return "BlockPick";
            if (text == _tFillSalt || text == _tFillWater) return "Interact";   
            if (text == _tPour) return "Context";                              
            if (text == _tDrink || text == _tEat) return "Use";           
            if (text == _tSelectBait) return "Context";                        
            return null;   // 其它 → 纯文字，无字形
        }

        // 由 DTM 补丁在 P2 建造工具激活时调用：按帧收集本帧原版要显示的提示。
        internal static void CaptureBuildPrompt(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (IsP2DevicePromptThisFrame) return;
            if (text == Helper.GetTerm("Game/Remove")) return;   // "移除"由专用提示(快捷栏上方)显示，不进建造提示条
            EnsurePromptTerms();
            if (text == _tSelectBait)   // 选择诱饵：改走快捷栏【上方竖排】提示(对齐原版位置/样式)，不进底部横排条
            {
                SetP2BaitPrompt("Context", text);
                _p2BaitPromptFrame = Time.frameCount;
                return;
            }
            if (Time.frameCount != _capFrame) { _capFrame = Time.frameCount; _capList.Clear(); }
            foreach (var kv in _capList) if (kv.Value == text) return;   // 去重
            _capList.Add(new KeyValuePair<string, string>(MapTermToGlyph(text), text));
        }

        // 持可放置物：RT【放置】+ 原版本帧发出的【旋转】提示(D-pad右/右摇杆)。
        static void DrivePlaceablePrompts()
        {
            EnsurePromptTerms();
            var items = new List<KeyValuePair<string, string>>();
            items.Add(new KeyValuePair<string, string>("Use", Helper.GetTerm("Game/PlaceItem", applyParameters: true)));
            if (Time.frameCount - _capFrame <= 2)
                foreach (var kv in _capList)
                    if (kv.Value == _tRotate || kv.Value == _tRotateSmooth)
                        items.Add(new KeyValuePair<string, string>("Rotate", kv.Value));
            SetP2Prompts(items);
        }

        // 推送本帧(或上一帧)捕获的提示到字形条。陈旧(>2帧无更新)则清空。
        static void PushCapturedPrompts()
        {
            if (Time.frameCount - _capFrame <= 2 && _capList.Count > 0) SetP2Prompts(_capList);
            else ClearP2Prompts();
        }

        static void EnsureP2PromptStrip()
        {
            if (_p2PromptStrip != null || _p2HudCanvas == null) return;
            Font arial = Resources.GetBuiltinResource<Font>("Arial.ttf");

            var go = new GameObject("P2_BuildPromptStrip", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 150f); rt.sizeDelta = new Vector2(900f, 56f);
            var hl = go.AddComponent<HorizontalLayoutGroup>();
            hl.childAlignment = TextAnchor.MiddleCenter; hl.spacing = 44f;
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            hl.childControlWidth = true; hl.childControlHeight = true;
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) go.layer = uiLayer;
            _p2PromptStrip = rt;
            _p2PromptItems.Clear();   // 重建前清旧：世界重载后旧 item 随 canvas 销毁,残留引用会让 SetP2Prompts 用到已销毁项 → 提示不显示

            for (int i = 0; i < P2PromptMax; i++)
            {
                var item = new GameObject("PromptItem" + i, typeof(RectTransform));
                var irt = item.GetComponent<RectTransform>(); irt.SetParent(rt, false);
                var ih = item.AddComponent<HorizontalLayoutGroup>();
                ih.childAlignment = TextAnchor.MiddleCenter; ih.spacing = 8f;
                ih.childForceExpandWidth = false; ih.childForceExpandHeight = false;
                ih.childControlWidth = true; ih.childControlHeight = true;
                if (uiLayer >= 0) item.layer = uiLayer;

                var gi = new GameObject("Glyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                gi.GetComponent<RectTransform>().SetParent(irt, false);
                var img = gi.GetComponent<Image>(); img.preserveAspect = true; img.raycastTarget = false;
                var gle = gi.AddComponent<LayoutElement>(); gle.preferredWidth = 46f; gle.preferredHeight = 46f;
                if (uiLayer >= 0) gi.layer = uiLayer;

                var ti = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                ti.GetComponent<RectTransform>().SetParent(irt, false);
                var txt = ti.GetComponent<Text>();
                txt.alignment = TextAnchor.MiddleLeft; txt.fontSize = 24; txt.color = Color.white; txt.raycastTarget = false;
                if (arial) txt.font = arial;
                var sh = ti.AddComponent<Shadow>(); sh.effectColor = Color.black; sh.effectDistance = new Vector2(1f, -1f);
                if (uiLayer >= 0) ti.layer = uiLayer;

                _p2PromptItems.Add(new PromptItem { rt = irt, glyph = img, text = txt });
                item.SetActive(false);
            }
            _p2PromptStrip.gameObject.SetActive(false);
        }

        // items: (glyphKey, text)；glyphKey 为 null/无字形时只显示文字。每帧调用。
        static void SetP2Prompts(List<KeyValuePair<string, string>> items)
        {
            EnsureP2PromptStrip();
            if (_p2PromptStrip == null) return;
            int n = items != null ? items.Count : 0;
            for (int i = 0; i < _p2PromptItems.Count; i++)
            {
                var it = _p2PromptItems[i];
                if (i < n)
                {
                    var kv = items[i];
                    var sp = string.IsNullOrEmpty(kv.Key) ? null : GlyphFor(kv.Key);
                    if (sp != null) { it.glyph.sprite = sp; it.glyph.enabled = true; it.glyph.gameObject.SetActive(true); }
                    else it.glyph.gameObject.SetActive(false);
                    it.text.text = kv.Value ?? "";
                    it.rt.gameObject.SetActive(true);
                }
                else it.rt.gameObject.SetActive(false);
            }
            bool show = n > 0;
            if (_p2PromptStrip.gameObject.activeSelf != show) _p2PromptStrip.gameObject.SetActive(show);
        }

        internal static void ClearP2Prompts()
        {
            if (_p2PromptStrip != null && _p2PromptStrip.gameObject.activeSelf)
                _p2PromptStrip.gameObject.SetActive(false);
        }

        // ── P2 交互提示(拾取/开箱…)：底部居中 [手柄字形][文字]，在 P2 半屏 HUD 上(独立于建造提示条) ──
        static RectTransform _p2InteractPrompt; static Image _p2InteractGlyph; static Text _p2InteractText;

        static void EnsureP2InteractPrompt()
        {
            if (_p2InteractPrompt != null || _p2HudCanvas == null) return;
            Font arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            int uiLayer = LayerMask.NameToLayer("UI");

            // 原版位置：准心【上方】居中，竖排(字形在上、文字在下)。
            var go = new GameObject("P2_InteractPrompt", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>(); rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 55f); rt.sizeDelta = new Vector2(420f, 90f);
            var hl = go.AddComponent<VerticalLayoutGroup>();
            hl.childAlignment = TextAnchor.MiddleCenter; hl.spacing = 4f;
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            hl.childControlWidth = true; hl.childControlHeight = true;
            if (uiLayer >= 0) go.layer = uiLayer;
            _p2InteractPrompt = rt;

            var gi = new GameObject("Glyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gi.GetComponent<RectTransform>().SetParent(rt, false);
            _p2InteractGlyph = gi.GetComponent<Image>(); _p2InteractGlyph.preserveAspect = true; _p2InteractGlyph.raycastTarget = false;
            var gle = gi.AddComponent<LayoutElement>(); gle.preferredWidth = 46f; gle.preferredHeight = 46f;
            if (uiLayer >= 0) gi.layer = uiLayer;

            var ti = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ti.GetComponent<RectTransform>().SetParent(rt, false);
            _p2InteractText = ti.GetComponent<Text>();
            _p2InteractText.alignment = TextAnchor.MiddleCenter; _p2InteractText.fontSize = 26; _p2InteractText.color = Color.white; _p2InteractText.raycastTarget = false;
            _p2InteractText.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (arial) _p2InteractText.font = arial;
            var sh = ti.AddComponent<Shadow>(); sh.effectColor = Color.black; sh.effectDistance = new Vector2(1f, -1f);
            if (uiLayer >= 0) ti.layer = uiLayer;

            go.SetActive(false);
        }

        internal static void SetP2InteractPrompt(string glyphKey, string text)
        {
            EnsureP2InteractPrompt();
            if (_p2InteractPrompt == null) return;
            ClearP2Prompts();
            HideP2BuildCostCursor();
            var sp = string.IsNullOrEmpty(glyphKey) ? null : GlyphFor(glyphKey);
            if (sp != null) { _p2InteractGlyph.sprite = sp; _p2InteractGlyph.gameObject.SetActive(true); }
            else _p2InteractGlyph.gameObject.SetActive(false);
            _p2InteractText.text = text ?? "";
            if (!_p2InteractPrompt.gameObject.activeSelf) _p2InteractPrompt.gameObject.SetActive(true);
        }

        internal static void ClearP2InteractPrompt()
        {
            if (_p2InteractPrompt != null && _p2InteractPrompt.gameObject.activeSelf)
                _p2InteractPrompt.gameObject.SetActive(false);
        }

        // 是否正在显示交互提示(对准箱子/可拾取物等) → 持锤时据此隐藏建造提示条，避免与之并列。
        internal static bool IsP2InteractPromptActive => _p2InteractPrompt != null && _p2InteractPrompt.gameObject.activeSelf;

        // 鱼竿"选择诱饵"竖排提示：捕获超过 2 帧未刷新(移开水面/收竿)则清除。由 TickP2Build 每帧调用。
        internal static void ExpireP2BaitPrompt()
        {
            if (_p2BaitPromptFrame >= 0 && Time.frameCount - _p2BaitPromptFrame > 2)
            {
                _p2BaitPromptFrame = -1;
                ClearP2BaitPrompt();
            }
        }

        // ── 鱼竿"选择诱饵"提示：快捷栏【上方】居中竖排(字形在上、文字在下)，对齐原版位置 ──
        static RectTransform _p2BaitPrompt; static Image _p2BaitGlyph; static Text _p2BaitText;

        static void EnsureP2BaitPrompt()
        {
            if (_p2BaitPrompt != null || _p2HudCanvas == null) return;
            Font arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            int uiLayer = LayerMask.NameToLayer("UI");

            var go = new GameObject("P2_BaitPrompt", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>(); rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 130f); rt.sizeDelta = new Vector2(420f, 96f);   // 快捷栏上方
            var hl = go.AddComponent<VerticalLayoutGroup>();
            hl.childAlignment = TextAnchor.MiddleCenter; hl.spacing = 4f;
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            hl.childControlWidth = true; hl.childControlHeight = true;
            if (uiLayer >= 0) go.layer = uiLayer;
            _p2BaitPrompt = rt;

            var gi = new GameObject("Glyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gi.GetComponent<RectTransform>().SetParent(rt, false);
            _p2BaitGlyph = gi.GetComponent<Image>(); _p2BaitGlyph.preserveAspect = true; _p2BaitGlyph.raycastTarget = false;
            var gle = gi.AddComponent<LayoutElement>(); gle.preferredWidth = 46f; gle.preferredHeight = 46f;
            if (uiLayer >= 0) gi.layer = uiLayer;

            var ti = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ti.GetComponent<RectTransform>().SetParent(rt, false);
            _p2BaitText = ti.GetComponent<Text>();
            _p2BaitText.alignment = TextAnchor.MiddleCenter; _p2BaitText.fontSize = 26; _p2BaitText.color = Color.white; _p2BaitText.raycastTarget = false;
            _p2BaitText.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (arial) _p2BaitText.font = arial;
            var sh = ti.AddComponent<Shadow>(); sh.effectColor = Color.black; sh.effectDistance = new Vector2(1f, -1f);
            if (uiLayer >= 0) ti.layer = uiLayer;

            go.SetActive(false);
        }

        static void SetP2BaitPrompt(string glyphKey, string text)
        {
            EnsureP2BaitPrompt();
            if (_p2BaitPrompt == null) return;
            var sp = string.IsNullOrEmpty(glyphKey) ? null : GlyphFor(glyphKey);
            if (sp != null) { _p2BaitGlyph.sprite = sp; _p2BaitGlyph.gameObject.SetActive(true); }
            else _p2BaitGlyph.gameObject.SetActive(false);
            _p2BaitText.text = text ?? "";
            if (!_p2BaitPrompt.gameObject.activeSelf) _p2BaitPrompt.gameObject.SetActive(true);
        }

        static void ClearP2BaitPrompt()
        {
            if (_p2BaitPrompt != null && _p2BaitPrompt.gameObject.activeSelf)
                _p2BaitPrompt.gameObject.SetActive(false);
        }

        // ── P2 方向盘"长按转向"提示:准心上方居中,双字形横排([D-pad右][+][右摇杆])在上、红字在下,复刻 vanilla DisplayText ──
        static RectTransform _p2SteerPrompt; static Image _p2SteerGlyph1, _p2SteerGlyph2; static Text _p2SteerPlus, _p2SteerText;
        static int _p2SteerFrame = -1;

        static void EnsureP2SteerPrompt()
        {
            if (_p2SteerPrompt != null || _p2HudCanvas == null) return;
            Font arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            int uiLayer = LayerMask.NameToLayer("UI");

            var go = new GameObject("P2_SteerPrompt", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>(); rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 150f); rt.sizeDelta = new Vector2(520f, 110f);
            var vl = go.AddComponent<VerticalLayoutGroup>();
            vl.childAlignment = TextAnchor.MiddleCenter; vl.spacing = 6f;
            vl.childForceExpandWidth = false; vl.childForceExpandHeight = false;
            vl.childControlWidth = true; vl.childControlHeight = true;
            if (uiLayer >= 0) go.layer = uiLayer;
            _p2SteerPrompt = rt;

            // 字形横排:[glyph1] [+] [glyph2]
            var row = new GameObject("Glyphs", typeof(RectTransform));
            var rrt = row.GetComponent<RectTransform>(); rrt.SetParent(rt, false);
            var hl = row.AddComponent<HorizontalLayoutGroup>();
            hl.childAlignment = TextAnchor.MiddleCenter; hl.spacing = 8f;
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            hl.childControlWidth = true; hl.childControlHeight = true;
            if (uiLayer >= 0) row.layer = uiLayer;

            _p2SteerGlyph1 = MakeSteerGlyph(rrt, uiLayer);
            _p2SteerPlus   = MakeSteerPlus(rrt, arial, uiLayer);
            _p2SteerGlyph2 = MakeSteerGlyph(rrt, uiLayer);

            var ti = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ti.GetComponent<RectTransform>().SetParent(rt, false);
            _p2SteerText = ti.GetComponent<Text>();
            _p2SteerText.alignment = TextAnchor.MiddleCenter; _p2SteerText.fontSize = 28;
            _p2SteerText.color = new Color(0.86f, 0.78f, 0.58f);
            _p2SteerText.horizontalOverflow = HorizontalWrapMode.Overflow;
            if (arial) _p2SteerText.font = arial;
            var sh = ti.AddComponent<Shadow>(); sh.effectColor = Color.black; sh.effectDistance = new Vector2(1f, -1f);
            if (uiLayer >= 0) ti.layer = uiLayer;

            go.SetActive(false);
        }

        static Image MakeSteerGlyph(RectTransform parent, int uiLayer)
        {
            var gi = new GameObject("Glyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gi.GetComponent<RectTransform>().SetParent(parent, false);
            var img = gi.GetComponent<Image>(); img.preserveAspect = true; img.raycastTarget = false;
            var le = gi.AddComponent<LayoutElement>(); le.preferredWidth = 48f; le.preferredHeight = 48f;
            if (uiLayer >= 0) gi.layer = uiLayer;
            return img;
        }

        static Text MakeSteerPlus(RectTransform parent, Font arial, int uiLayer)
        {
            var ti = new GameObject("Plus", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ti.GetComponent<RectTransform>().SetParent(parent, false);
            var t = ti.GetComponent<Text>();
            t.alignment = TextAnchor.MiddleCenter; t.fontSize = 30; t.color = Color.white; t.raycastTarget = false;
            t.text = "+"; t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            if (arial) t.font = arial;
            var le = ti.AddComponent<LayoutElement>(); le.preferredWidth = 24f; le.preferredHeight = 48f;
            if (uiLayer >= 0) ti.layer = uiLayer;
            return t;
        }

        // glyphKey1/2:SO_ControllerActions 字形键(与 vanilla 同源);任一解析不到则隐藏该字形(及"+")。每帧调用,帧戳驱动过期。
        internal static void SetP2SteerPrompt(string glyphKey1, string glyphKey2, string text)
        {
            EnsureP2SteerPrompt();
            if (_p2SteerPrompt == null) return;
            var s1 = string.IsNullOrEmpty(glyphKey1) ? null : GlyphFor(glyphKey1);
            var s2 = string.IsNullOrEmpty(glyphKey2) ? null : GlyphFor(glyphKey2);
            if (s1 != null) { _p2SteerGlyph1.sprite = s1; _p2SteerGlyph1.gameObject.SetActive(true); } else _p2SteerGlyph1.gameObject.SetActive(false);
            if (s2 != null) { _p2SteerGlyph2.sprite = s2; _p2SteerGlyph2.gameObject.SetActive(true); } else _p2SteerGlyph2.gameObject.SetActive(false);
            _p2SteerPlus.gameObject.SetActive(s1 != null && s2 != null);   // 两字形都在才显示连接的 "+"
            _p2SteerText.color = new Color(0.86f, 0.78f, 0.58f);
            _p2SteerText.text = text ?? "";
            if (!_p2SteerPrompt.gameObject.activeSelf) _p2SteerPrompt.gameObject.SetActive(true);
            _p2SteerFrame = Time.frameCount;
        }

        // 离开方向盘后(>1 帧未刷新)自动隐藏。由 TickP2StatHud 每帧调用。
        internal static void ExpireP2SteerPrompt()
        {
            if (_p2SteerFrame >= 0 && Time.frameCount - _p2SteerFrame > 1)
            {
                _p2SteerFrame = -1;
                if (_p2SteerPrompt != null && _p2SteerPrompt.gameObject.activeSelf) _p2SteerPrompt.gameObject.SetActive(false);
            }
        }

        // ── 设备交互提示(研究台/烹饪/熔炉等)：P2 射线处理期间，捕获设备 OnIsRayed 写入共享 DTM 的提示文本，
        //    转成 P2 交互提示([X][文字])。原版输入/上下文/距离已路由到 P2，仅提示需搬到 P2 半屏。 ──
        static int _p2DevicePromptFrame = -1;
        static Block_CookingStand _p2DevicePromptCookingStand;
        static ItemInstance _p2DevicePromptHeldItem;
        // P2 设备提示窗口内 P2 手持物(供 LocalizationParameters {CurrentItem} 解析用)。
        internal static ItemInstance P2DevicePromptHeldItem => _p2DevicePromptHeldItem;
        internal static bool IsP2DevicePromptThisFrame => _p2DevicePromptFrame == Time.frameCount;

        internal static IDisposable BeginP2DevicePromptScope(IEnumerable<IRaycastable> raycastables)
        {
            var previousStand = _p2DevicePromptCookingStand;
            var previousHeld = _p2DevicePromptHeldItem;
            _p2DevicePromptCookingStand = null;
            _p2DevicePromptHeldItem = GetP2HeldHotbarItem();

            if (raycastables != null)
            {
                foreach (var obj in raycastables)
                {
                    if (obj is Block_CookingStand stand)
                    {
                        _p2DevicePromptCookingStand = stand;
                        break;
                    }
                }
            }

            return new ScopeAction(() =>
            {
                _p2DevicePromptCookingStand = previousStand;
                _p2DevicePromptHeldItem = previousHeld;
            });
        }

        static string NormalizeP2DevicePromptText(string text)
        {
            var stand = _p2DevicePromptCookingStand;
            var held = _p2DevicePromptHeldItem;
            var item = held != null ? held.baseItem : null;
            if (stand != null && item != null && item.settings_cookable != null)
            {
                if (stand is Block_CookingStand_Purifier
                    && item.settings_consumeable != null
                    && item.settings_consumeable.FoodForm == FoodForm.Fluid
                    && stand.GetCookingSlotsForItem(item) != null)
                    return Helper.GetTerm("Game/FillWater", applyParameters: true);

                if (!(stand is Block_CookingStand_Purifier) && stand.GetCookingSlotsForItem(item) != null)
                    return Helper.GetTerm("Game/PlaceItem", applyParameters: true);
            }
            return text;
        }

        internal static void CaptureDevicePrompt(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = NormalizeP2DevicePromptText(text);
            ClearP2Prompts();
            SetP2InteractPrompt("Interact", text);          
            _p2DevicePromptFrame = Time.frameCount;
        }

        sealed class ScopeAction : IDisposable
        {
            readonly Action _onDispose;
            bool _disposed;

            internal ScopeAction(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _onDispose?.Invoke();
            }
        }

        // ── P2 "移除可放置物" 提示：快捷栏【上方】居中 [字形][文字] ──────────────
        static RectTransform _p2RemovePrompt; static Image _p2RemoveGlyph; static Text _p2RemoveText;

        static void EnsureP2RemovePrompt()
        {
            if (_p2RemovePrompt != null || _p2HudCanvas == null) return;
            Font arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            int uiLayer = LayerMask.NameToLayer("UI");

            var go = new GameObject("P2_RemovePrompt", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>(); rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 130f); rt.sizeDelta = new Vector2(420f, 56f);   // 快捷栏上方
            var hl = go.AddComponent<HorizontalLayoutGroup>();
            hl.childAlignment = TextAnchor.MiddleCenter; hl.spacing = 10f;
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            hl.childControlWidth = true; hl.childControlHeight = true;
            if (uiLayer >= 0) go.layer = uiLayer;
            _p2RemovePrompt = rt;

            var gi = new GameObject("Glyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            gi.GetComponent<RectTransform>().SetParent(rt, false);
            _p2RemoveGlyph = gi.GetComponent<Image>(); _p2RemoveGlyph.preserveAspect = true; _p2RemoveGlyph.raycastTarget = false;
            var gle = gi.AddComponent<LayoutElement>(); gle.preferredWidth = 44f; gle.preferredHeight = 44f;
            if (uiLayer >= 0) gi.layer = uiLayer;

            var ti = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ti.GetComponent<RectTransform>().SetParent(rt, false);
            _p2RemoveText = ti.GetComponent<Text>();
            _p2RemoveText.alignment = TextAnchor.MiddleLeft; _p2RemoveText.fontSize = 24; _p2RemoveText.color = Color.white; _p2RemoveText.raycastTarget = false;
            if (arial) _p2RemoveText.font = arial;
            var sh = ti.AddComponent<Shadow>(); sh.effectColor = Color.black; sh.effectDistance = new Vector2(1f, -1f);
            if (uiLayer >= 0) ti.layer = uiLayer;

            go.SetActive(false);
        }

        internal static void SetP2RemovePrompt(bool show)
        {
            EnsureP2RemovePrompt();
            if (_p2RemovePrompt == null) return;
            if (show)
            {
                var sp = GlyphFor("Remove");                 
                if (sp != null) { _p2RemoveGlyph.sprite = sp; _p2RemoveGlyph.gameObject.SetActive(true); }
                else _p2RemoveGlyph.gameObject.SetActive(false);
                _p2RemoveText.text = Helper.GetTerm("Game/Remove");
            }
            if (_p2RemovePrompt.gameObject.activeSelf != show) _p2RemovePrompt.gameObject.SetActive(show);
        }

        // 是否正在显示"移除"提示(对准已放置的可移除物) → 持可放置物时据此隐藏放置提示条，避免与移除提示重叠。
        internal static bool IsP2RemovePromptActive => _p2RemovePrompt != null && _p2RemovePrompt.gameObject.activeSelf;

        static void DestroyP2PromptStrip()
        {
            if (_p2PromptStrip != null) UnityEngine.Object.Destroy(_p2PromptStrip.gameObject);
            _p2PromptStrip = null; _p2PromptItems.Clear();
        }
    }
}
