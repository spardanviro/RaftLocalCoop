using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using HarmonyLib;

namespace SplitScreen
{
    // Reuses the vanilla pause-panel presentation and the player's own
    // Unstuck_Tracker. Only the menu ownership and input are P2-specific.
    public sealed class P2UnstuckMenu
    {
        readonly SplitScreenRuntime _runtime;
        static readonly System.Reflection.FieldInfo HostGameModeTextField =
            AccessTools.Field(typeof(PauseMenu), "text_hostGameMode");
        static readonly System.Reflection.FieldInfo WorldCodeTextField =
            AccessTools.Field(typeof(PauseMenu), "text_worldCode");
        GameObject _panel;
        GameObject _unstuckPanel;
        bool _isOpen;

        public bool IsOpen => _isOpen;

        public P2UnstuckMenu(SplitScreenRuntime runtime)
        {
            _runtime = runtime;
        }

        public void Tick()
        {
            var slot = _runtime.P2;
            bool pausePressed = slot.ActionPause != null && slot.ActionPause.WasPressedThisFrame();

            if (!_isOpen)
            {
                if (pausePressed) Open();
                return;
            }

            var gamepad = _runtime.Input.GetP2Gamepad();
            bool cancel = pausePressed || (gamepad != null && gamepad.buttonEast.wasPressedThisFrame);
            if (cancel)
            {
                Close();
                return;
            }

            if (gamepad != null && gamepad.buttonSouth.wasPressedThisFrame)
                UnstuckP2();
        }

        public void Close()
        {
            if (!_isOpen) return;
            _isOpen = false;
            if (_panel != null) _panel.SetActive(false);
            _runtime.P2.IsUsingMenu = false;
            _runtime.Input.SetP2GameplayEnabled(true);
        }

        void Open()
        {
            // 暂停面板不与其他 P2 菜单叠开:Close() 会无条件恢复 gameplay,
            // 叠开再关会让 P2 带着打开的背包走动。
            if (Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen || Main.IsP2MenuOpen || _runtime.P2.IsUsingMenu) return;
            if (!EnsurePanel()) return;
            _isOpen = true;
            _panel.SetActive(true);
            if (_unstuckPanel != null) _unstuckPanel.SetActive(true);
            _runtime.P2.IsUsingMenu = true;
            _runtime.Input.SetP2GameplayEnabled(false);
        }

        void UnstuckP2()
        {
            var tracker = _runtime.P2.Player != null ? _runtime.P2.Player.UnstuckTracker : null;
            if (tracker != null) tracker.Unstuck();
            Close();
        }

        bool EnsurePanel()
        {
            if (_panel != null) return true;

            var canvas = Main.P2HudCanvas;
            var pauseMenu = ComponentManager<PauseMenu>.Value;
            if (canvas == null || pauseMenu == null || pauseMenu.pausePanel == null || pauseMenu.unstuckPanel == null)
                return false;

            _panel = Object.Instantiate(pauseMenu.pausePanel, canvas.transform, false);
            _panel.name = "P2_PausePanel";

            var clonedButtonHolder = FindCloneObject(
                pauseMenu.pausePanel.transform,
                pauseMenu.buttonHolderPanel != null ? pauseMenu.buttonHolderPanel.transform : null,
                _panel.transform);
            if (clonedButtonHolder != null) clonedButtonHolder.SetActive(false);

            _unstuckPanel = FindCloneObject(
                pauseMenu.pausePanel.transform,
                pauseMenu.unstuckPanel.transform,
                _panel.transform);
            if (_unstuckPanel == null)
            {
                _unstuckPanel = Object.Instantiate(pauseMenu.unstuckPanel, _panel.transform, false);
                var rect = _unstuckPanel.transform as RectTransform;
                if (rect != null)
                {
                    rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.anchoredPosition = Vector2.zero;
                }
            }

            foreach (var candidate in _panel.GetComponentsInChildren<Button>(true))
                if (candidate != null && !candidate.transform.IsChildOf(_unstuckPanel.transform))
                    candidate.gameObject.SetActive(false);

            HideCloneOf(pauseMenu.pausePanel.transform, HostGameModeTextField?.GetValue(pauseMenu) as Component, _panel.transform);
            HideCloneOf(pauseMenu.pausePanel.transform, WorldCodeTextField?.GetValue(pauseMenu) as Component, _panel.transform);

            var button = _unstuckPanel.GetComponentInChildren<Button>(true);
            if (button != null)
            {
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(UnstuckP2);
            }

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) Main.SetLayerRecursively(_panel.transform, uiLayer);
            _panel.SetActive(false);
            return true;
        }

        static void HideCloneOf(Transform sourceRoot, Component sourceComponent, Transform cloneRoot)
        {
            if (sourceComponent == null) return;
            var clone = FindCloneObject(sourceRoot, sourceComponent.transform, cloneRoot);
            if (clone != null) clone.SetActive(false);
        }

        static GameObject FindCloneObject(Transform sourceRoot, Transform sourceTarget, Transform cloneRoot)
        {
            if (sourceRoot == null || sourceTarget == null || cloneRoot == null || !sourceTarget.IsChildOf(sourceRoot))
                return null;
            if (sourceTarget == sourceRoot) return cloneRoot.gameObject;

            var path = new System.Collections.Generic.Stack<string>();
            for (var current = sourceTarget; current != null && current != sourceRoot; current = current.parent)
                path.Push(current.name);

            var clone = cloneRoot;
            while (path.Count > 0)
            {
                clone = clone.Find(path.Pop());
                if (clone == null) return null;
            }
            return clone.gameObject;
        }
    }
}
