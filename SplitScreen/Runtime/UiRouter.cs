using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  UiRouter — P2 UI 边界：覆层 widget + 菜单 / 存储 open-close 路由
    //
    //  核心原则：
    //  P1 继续使用原生的全局 CanvasHelper / CraftingMenu。
    //  P2 只通过此路由器打开菜单，并始终持有 IsUsingMenu 标志，
    //  以便 Y 键能可靠地关闭 P2 打开的任何 UI。
    //
    //  TickMenu() 在 SplitScreenRuntime.Tick() 内每帧调用一次。
    // ══════════════════════════════════════════════════════════════════════
    public sealed class UiRouter
    {
        readonly SplitScreenRuntime _rt;
        readonly P2UnstuckMenu _unstuckMenu;

        public UiRouter(SplitScreenRuntime rt)
        {
            _rt = rt;
            _unstuckMenu = new P2UnstuckMenu(rt);
        }

        // ── 覆层创建 ─────────────────────────────────────────────────────
        public void CreateOverlays()
        {
            CreatePromptText();
            CreateDeathOverlay();
        }

        void CreatePromptText()
        {
            if (_rt.P2.PromptText != null) return;
            var canvas = GetCanvasTransform();
            if (canvas == null) { Main.ModEntry.Logger.Log("[P2Prompt] Canvas not found, skip"); return; }

            var go = new GameObject("P2_InteractPrompt");
            go.transform.SetParent(canvas, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.75f, 0.08f);
            rt.pivot            = Vector2.one * 0.5f;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta        = new Vector2(480f, 64f);

            _rt.P2.PromptText           = go.AddComponent<Text>();
            _rt.P2.PromptText.fontSize  = 22;
            _rt.P2.PromptText.color     = Color.white;
            _rt.P2.PromptText.alignment = TextAnchor.MiddleCenter;
            var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (arial) _rt.P2.PromptText.font = arial;

            var ol = go.AddComponent<Outline>();
            ol.effectColor    = Color.black;
            ol.effectDistance = new Vector2(1f, -1f);

            go.SetActive(false);
            Main.ModEntry.Logger.Log("[P2Prompt] P2 interaction prompt overlay created");
        }

        void CreateDeathOverlay()
        {
            if (_rt.P2.DeathText != null) return;
            var canvas = GetCanvasTransform();
            if (canvas == null) { Main.ModEntry.Logger.Log("[P2Death] Canvas not found, skip"); return; }

            var go = new GameObject("P2_DeathOverlay");
            go.transform.SetParent(canvas, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.75f, 0.5f);
            rt.pivot            = Vector2.one * 0.5f;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta        = new Vector2(480f, 120f);

            _rt.P2.DeathText           = go.AddComponent<Text>();
            _rt.P2.DeathText.fontSize  = 28;
            _rt.P2.DeathText.color     = new Color(1f, 0.3f, 0.3f, 1f);
            _rt.P2.DeathText.alignment = TextAnchor.MiddleCenter;
            var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (arial) _rt.P2.DeathText.font = arial;

            var ol = go.AddComponent<Outline>();
            ol.effectColor    = Color.black;
            ol.effectDistance = new Vector2(1.5f, -1.5f);

            go.SetActive(false);
            Main.ModEntry.Logger.Log("[P2Death] P2 death overlay created");
        }

        
        
        
        
        public void ShowPrompt(string text) { if (!Main.IsP2FillPromptActive) Main.SetP2InteractPrompt("Interact", text); }
        public void ShowPrompt(string glyphKey, string text) { if (!Main.IsP2FillPromptActive) Main.SetP2InteractPrompt(glyphKey, text); }
        public void HidePrompt() { if (!Main.IsP2FillPromptActive) Main.ClearP2InteractPrompt(); }

        
        public void CloseUnstuckMenu() { _unstuckMenu.Close(); }

        public void TickMenu()
        {
            _unstuckMenu.Tick();
            if (_unstuckMenu.IsOpen) return;

            var slot = _rt.P2;
            var gp = _rt.Input.GetP2Gamepad();
            bool open = Main.IsP2BackpackOpen;   // 背包/箱子打开时 IsP2BackpackOpen 均为 true

            if (!open)
            {
                // 关闭状态：Y 打开背包。InputUser 动作映射在重载后偶尔不触发 → 同时读 raw 手柄 Y 兜底。
                bool yOpen = (slot.ActionMenu != null && slot.ActionMenu.WasPressedThisFrame())
                          || (gp != null && gp.buttonNorth.wasPressedThisFrame);
                if (!yOpen) return;
                if (slot.CurrentStorage != null) CloseStorage(slot.CurrentStorage);
                Main.OpenP2Backpack(openCrafting: true);
                slot.IsUsingMenu = true;
                return;
            }

            // 打开状态：B = 返回(关闭)。Y 不再关闭(改作快速移动，由 TickP2Backpack 处理)。
            if (gp != null && gp.buttonEast.wasPressedThisFrame)
            {
                Main.SuppressP2CrouchOnExit();



                if (Main.IsP2StorageOpen)
                {
                    if (slot.CurrentStorage != null) CloseStorage(slot.CurrentStorage);
                    else Main.CloseP2Storage();   // 兜底:无 CurrentStorage 引用时至少关 UI
                }
                else if (Main.IsP2ResearchOpen) { Main.CloseP2ResearchTable(); slot.IsUsingMenu = false; }   // 研究台(连背包一起关)
                else if (slot.CurrentStorage != null) CloseStorage(slot.CurrentStorage);
                else { Main.CloseP2Backpack(); slot.IsUsingMenu = false; }  // 仅背包
            }
        }

        // ── 存储 UI open / close ──────────────────────────────────────────
        public void OpenStorage(Storage_Small storage)
        {
            var slot = _rt.P2;
            if (slot.Player == null || storage == null) return;
            bool wasUsingMenu = slot.IsUsingMenu;
            var prevStorage = slot.CurrentStorage;
            slot.IsUsingMenu    = true;
            slot.CurrentStorage = storage;
            Main.OpenP2Storage(storage);
            if (!Main.IsP2StorageOpen)
            {
                // 打开失败(画布/背包未就绪等会静默返回):别留下"正在用菜单"的假状态。
                slot.IsUsingMenu    = wasUsingMenu;
                slot.CurrentStorage = prevStorage;
                return;
            }
            Main.LogV("[UiRouter] P2 OpenStorage -> " + storage.name);
        }

        public void CloseStorage(Storage_Small storage)
        {
            if (storage == null) return;
            var slot = _rt.P2;
            if (slot.Player == null) return;
            Main.CloseP2Storage();
            slot.IsUsingMenu    = false;
            slot.CurrentStorage = null;
            Main.LogV("[UiRouter] P2 CloseStorage -> " + storage.name);
        }

        /// 无论 CurrentStorage 是否已知，都安全地关闭 P2 当前箱子（用于 Destroy 清理）
        public void CloseCurrentStorageIfAny()
        {
            var s = _rt.P2.CurrentStorage;
            if (s != null) CloseStorage(s);
        }

        // ── 工具 ─────────────────────────────────────────────────────────
        static Transform GetCanvasTransform()
        {
            var ch = Cm.Get<CanvasHelper>();
            if (ch == null) return null;
            var c = ch.GetComponent<Canvas>() ?? ch.GetComponentInParent<Canvas>();
            return c != null ? c.transform : ch.transform;
        }
    }
}
