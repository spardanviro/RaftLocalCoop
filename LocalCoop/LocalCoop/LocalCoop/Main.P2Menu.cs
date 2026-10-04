using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  通用 P2 菜单宿主
        //
        //  所有菜单都走统一入口 CanvasHelper.OpenMenu / OpenMenuCloseOther，且每个菜单的面板都挂在
        //  GameMenu.menuObjects 上。故做一套通用宿主：P2(设备上下文 isProcessingP2Ray)开任意菜单时——
        //   1. 调 gameMenu.Open() 激活面板(设备自身 Open 流程已设占用标志)；
        //   2. 把 menuObjects 搬到 P2 半屏画布(居中)；
        //   3. 用 P2 虚拟光标 + uGUI GraphicRaycaster + ExecuteEvents 通用驱动(任意 Button/Slot/可点元素)；
        //   4. 跳过原版 OpenMenu 的【P1 全局副作用】(ActiveMenu/锁光标/切输入)，P1 不被冻结；
        //   5. B 关闭 → CanvasHelper.CloseMenu(触发 MenuCloseEvent → 设备清理占用) + 还原面板。
        //  这样凡走 OpenMenu 的设备 UI(研究台/染色台/衣柜/交易站…)自动在 P2 半屏可用，无需逐个适配。
        // ══════════════════════════════════════════════════════════════════
        static bool _p2MenuOpen;
        static MenuType _p2MenuType;
        static CanvasHelper _p2MenuCanvasHelper;
        internal static bool IsP2MenuOpen => _p2MenuOpen;
        static Block_Wardrobe _p2WardrobeOpened;

        static readonly FieldInfo s_wardrobeMenuLocalPlayerField =
            typeof(WardrobeMenu).GetField("localPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_wardrobeGamepadBackField =
            typeof(WardrobeMenu).GetField("gamepadBack", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_wardrobeSelectedHighlightField =
            typeof(WardrobeMenu).GetField("selectedHighlight", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_wardrobeWholeOutfitImageField =
            typeof(WardrobeMenu).GetField("wholeOutfitImage", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_wardrobeOutfitButtonsField =
            typeof(WardrobeMenu).GetField("outfitButtons", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo s_wardrobeOutfitButtonPressedMethod =
            typeof(WardrobeMenu).GetMethod("OutfitButtonPressed", BindingFlags.Instance | BindingFlags.Public);

        // 菜单关闭那一帧也要锁住 P2 的跳/蹲/冲刺：B 关菜单的同一帧，PersonController 若在 TickP2Menu(关菜单)之后
        //  运行，会把这次 B 读成蹲下 → 退出时人物蹲一下。用关闭帧号把锁延到关闭帧(+1)覆盖任意执行顺序。
        static int _p2MenuClosedFrame = -1000;
        static bool _p2SuppressMenuCloseButton;
        internal static bool IsP2MenuInputLocked
        {
            get
            {
                if (_p2MenuOpen || Time.frameCount <= _p2MenuClosedFrame + 1) return true;
                if (!_p2SuppressMenuCloseButton) return false;

                bool held = (p2ActionCancel != null && p2ActionCancel.IsPressed())
                         || (p2ActionCrouch != null && p2ActionCrouch.IsPressed());
                if (!held) _p2SuppressMenuCloseButton = false;
                return held;
            }
        }

        // ── 菜单宿主策略：哪些 MenuType 才搬 P2 半屏 ──────────────────────────
        //  黑名单(不拦截，交回原版/各自专门处理)：
        //   - 全局/单人专属：PauseMenu/Cheat/Cutscene/ChatField/TextWriter —— 绝不搬 P2(避免 P2 工具/设备误触发把暂停/过场塞到 P2 半屏)。
        //   - 已有专门通道：BuildMenu(Main.P2BuildMenu)、Inventory(Main.P2Backpack)、DeathMenu(P2 自有死亡处理)。
        //  其余(PaintMenu/TradingPost/FishingBait/Wardrobe/Piano/Journal 及未来设备菜单)默认放行 → 搬 P2 半屏。
        static readonly HashSet<MenuType> _p2MenuBlacklist = new HashSet<MenuType>
        {
            MenuType.PauseMenu, MenuType.Cheat, MenuType.Cutscene, MenuType.ChatField,
            MenuType.TextWriter, MenuType.BuildMenu, MenuType.Inventory, MenuType.DeathMenu,
            MenuType.Piano,
        };
        internal static bool ShouldHostP2Menu(MenuType type) => !_p2MenuBlacklist.Contains(type);

        
        static GraphicRaycaster _p2MenuRaycaster;
        static GameObject _p2MenuHover;
        static bool _wasAMenu;
        static bool _openingP2MenuInternally;
        static readonly List<RaycastResult> _p2MenuRayResults = new List<RaycastResult>();
        internal static bool IsOpeningP2MenuInternally => _openingP2MenuInternally;
        static readonly List<GameObject> _p2WardrobeObjects = new List<GameObject>();
        static WardrobeMenu _p2WardrobeMenu;
        static Button[] _p2WardrobeButtons;
        static UiMagnet[] _p2WardrobeMagnets;
        static RectTransform _p2WardrobeSelectedHighlight;
        static Image _p2WardrobeWholeOutfitImage;
        static int _p2WardrobeIndex;
        static Vector2 _p2WardrobeCursorPos;
        static float _p2WardrobeCursorVelocity = 100f;
        static bool _p2WardrobeStickActive;
        static int _p2WardrobeLayoutRefreshFrames;
        static readonly Dictionary<GameObject, GameObject> _p2WardrobeCloneMap = new Dictionary<GameObject, GameObject>();
        static readonly List<GameObject> _p2BaitClones = new List<GameObject>();

        internal static bool OpenP2Menu(CanvasHelper canvas, MenuType type)
        {
            if (canvas == null || !ShouldHostP2Menu(type) || _p2MenuOpen) return false;
            if (CanvasHelper.ActiveMenu != MenuType.None) return false;
            if (type == MenuType.FishingBait) return OpenP2FishingBaitMenu(canvas);

            bool opened;
            _openingP2MenuInternally = true;
            try
            {
                opened = canvas.OpenMenu(type, force: true);
            }
            finally
            {
                _openingP2MenuInternally = false;
            }

            if (!opened) return false;
            Helper.SetCursorVisibleAndLockState(false, CursorLockMode.Locked);
            SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.SwitchCurrentActionMap("Player");

            _p2MenuOpen = true;
            _p2MenuType = type;
            _p2MenuCanvasHelper = canvas;
            _p2MenuHover = null;
            _wasAMenu = false;

            EnsureBpCursor();
            _p2InvCursorPos = Vector2.zero;
            if (_p2InvCursor != null)
            {
                _p2InvCursor.gameObject.SetActive(true);
                _p2InvCursor.anchoredPosition = Vector2.zero;
                _p2InvCursor.SetAsLastSibling();
            }

            _p2MenuRaycaster = canvas.GetComponent<GraphicRaycaster>();
            if (_p2MenuRaycaster == null)
                _p2MenuRaycaster = canvas.gameObject.AddComponent<GraphicRaycaster>();

            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(false);
            return true;
        }

        internal static void CloseP2Menu()
        {
            if (!_p2MenuOpen) return;
            
            _p2BaitItems.Clear();
            _p2MenuClosedFrame = Time.frameCount;   // 锁住本帧(+1)的 P2 跳/蹲，避免关菜单的 B 漏成蹲下
            _p2SuppressMenuCloseButton = true;
            var canvas = _p2MenuCanvasHelper; var type = _p2MenuType;
            _p2MenuOpen = false; _p2MenuCanvasHelper = null; _p2MenuHover = null;
            if (_p2InvCursor != null) _p2InvCursor.gameObject.SetActive(false);
            CloseP2MenuBackend(canvas, type);
            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(true);
        }

        static void CloseP2MenuBackend(CanvasHelper canvas, MenuType type)
        {
            if (type == MenuType.TradingPost)
            {
                CloseP2TradingPost();
                return;
            }
            if (type == MenuType.FishingBait)
            {
                DestroyP2FishingBaitClones();
                return;
            }
            if (type == MenuType.Wardrobe && _p2WardrobeOpened != null)
            {
                var wardrobe = _p2WardrobeOpened;
                _p2WardrobeOpened = null;
                P2FurnitureAdapter.CloseWardrobe(wardrobe, player2);
                DestroyP2WardrobeClone();
                return;
            }
            if (canvas != null) canvas.CloseMenu(type);
        }

        static bool OpenP2FishingBaitMenu(CanvasHelper canvas)
        {
            if (_p2HudCanvas == null || _p2MenuOpen)
            {
                LogV($"[P2Bait] open rejected canvas={(_p2HudCanvas != null)} menuOpen={_p2MenuOpen}");
                return false;
            }
            var menu = canvas.GetMenu(MenuType.FishingBait);
            if (menu == null || menu.menuObjects == null)
            {
                LogV("[P2Bait] FishingBait GameMenu or menuObjects missing");
                return false;
            }

            LogV($"[P2Bait] opening sourceObjects={menu.menuObjects.Count} canvas={canvas.name}");

            DestroyP2FishingBaitClones();
            foreach (var source in menu.menuObjects)
            {
                if (source == null) continue;
                var clone = Object.Instantiate(source, _p2HudCanvas.transform, false);
                clone.name = "P2_" + source.name;
                SanitizeClonedUiRoot(clone);
                // FishBaitMenu.Start disables its own root and relies on the
                // vanilla GameMenu lifecycle to reactivate it. P2 owns this
                // clone directly, so its original controller must stay off.
                foreach (var baitMenu in clone.GetComponentsInChildren<FishBaitMenu>(true))
                    baitMenu.enabled = false;
                foreach (var bait in clone.GetComponentsInChildren<UI_Cost_Interactable_FishingBait>(true))
                    bait.enabled = false;
                foreach (var nestedCanvas in clone.GetComponentsInChildren<Canvas>(true)) Object.Destroy(nestedCanvas);
                foreach (var raycaster in clone.GetComponentsInChildren<GraphicRaycaster>(true)) Object.Destroy(raycaster);
                int uiLayer = LayerMask.NameToLayer("UI");
                if (uiLayer >= 0) SetLayerRecursively(clone.transform, uiLayer);
                var rect = clone.transform as RectTransform;
                if (rect != null) rect.SetAsLastSibling();
                clone.SetActive(true);
                _p2BaitClones.Add(clone);
            }

            if (_p2BaitClones.Count == 0)
            {
                LogV("[P2Bait] clone produced zero roots");
                return false;
            }
            _p2MenuOpen = true;
            _p2MenuType = MenuType.FishingBait;
            _p2MenuCanvasHelper = canvas;
            _p2MenuHover = null;
            _wasAMenu = false;
            // Fishing bait uses the original controller layout: left stick
            // selects horizontally, so no virtual cursor is shown for P2.
            if (_p2InvCursor != null) _p2InvCursor.gameObject.SetActive(false);
            SetupP2BaitMenu();
            SplitScreenRuntime.Instance?.Input.SetP2GameplayEnabled(false);
            LogV($"[P2Bait] opened clones={_p2BaitClones.Count} baitItems={_p2BaitItems.Count}");
            return true;
        }

        static void DestroyP2FishingBaitClones()
        {
            foreach (var clone in _p2BaitClones)
                if (clone != null) Object.Destroy(clone);
            _p2BaitClones.Clear();
        }

        internal static bool OpenP2Wardrobe(Block_Wardrobe wardrobe)
        {
            if (wardrobe == null || player2 == null) return false;
            var canvas = ComponentManager<CanvasHelper>.Value;
            if (canvas == null || _p2HudCanvas == null || _p2MenuOpen) return false;
            if (!OpenP2WardrobeClone(canvas)) return false;
            if (!P2FurnitureAdapter.OpenWardrobe(wardrobe, player2))
            {
                DestroyP2WardrobeClone();
                return false;
            }

            _p2WardrobeOpened = wardrobe;
            _p2MenuOpen = true;
            _p2MenuType = MenuType.Wardrobe;
            _p2MenuCanvasHelper = canvas;
            _p2MenuHover = null;
            _wasAMenu = false;
            SetP2WardrobeInputEnabled(true);
            ConsumeP2InteractThisFrame();
            return true;
        }

        static bool OpenP2WardrobeClone(CanvasHelper canvas)
        {
            DestroyP2WardrobeClone();

            var menu = canvas.GetMenu(MenuType.Wardrobe);
            if (menu == null || _p2HudCanvas == null || player2 == null) return false;
            var sourceMenu = menu.messageReciever != null
                ? menu.messageReciever.GetComponent<WardrobeMenu>()
                : null;
            if (sourceMenu == null) return false;

            foreach (var src in menu.menuObjects)
            {
                if (src == null) continue;
                var clone = Object.Instantiate(src, _p2HudCanvas.transform, false);
                clone.name = "P2_" + src.name;
                SanitizeClonedUiRoot(clone);
                foreach (var c in clone.GetComponentsInChildren<Canvas>(true)) Object.Destroy(c);
                foreach (var r in clone.GetComponentsInChildren<GraphicRaycaster>(true)) Object.Destroy(r);
                var rt = clone.transform as RectTransform;
                if (rt != null)
                {
                    rt.localScale = Vector3.one;
                    rt.SetAsLastSibling();
                }
                clone.SetActive(true);
                _p2WardrobeObjects.Add(clone);
                _p2WardrobeCloneMap[src] = clone;
            }

            _p2WardrobeMenu = null;
            foreach (var go in _p2WardrobeObjects)
            {
                if (go == null) continue;
                _p2WardrobeMenu = go.GetComponentInChildren<WardrobeMenu>(true);
                if (_p2WardrobeMenu != null) break;
            }
            foreach (var wm in _p2HudCanvas.GetComponentsInChildren<WardrobeMenu>(true))
                if (wm != null && wm.gameObject.name.StartsWith("P2_")) wm.enabled = false;

            var gamepadBack = FindP2WardrobeCloneObject(s_wardrobeGamepadBackField?.GetValue(sourceMenu) as GameObject);
            if (gamepadBack != null) gamepadBack.SetActive(true);
            _p2WardrobeSelectedHighlight = FindP2WardrobeCloneComponent(s_wardrobeSelectedHighlightField?.GetValue(sourceMenu) as RectTransform);
            _p2WardrobeWholeOutfitImage = FindP2WardrobeCloneComponent(s_wardrobeWholeOutfitImageField?.GetValue(sourceMenu) as Image);
            _p2WardrobeButtons = CloneWardrobeButtons(s_wardrobeOutfitButtonsField?.GetValue(sourceMenu) as Button[]);
            if (_p2WardrobeButtons == null || _p2WardrobeButtons.Length == 0)
            {
                DestroyP2WardrobeClone();
                return false;
            }
            _p2WardrobeMagnets = new UiMagnet[_p2WardrobeButtons.Length];
            for (int i = 0; i < _p2WardrobeButtons.Length; i++)
                _p2WardrobeMagnets[i] = _p2WardrobeButtons[i] != null ? _p2WardrobeButtons[i].GetComponent<UiMagnet>() : null;

            PopulateP2WardrobeButtons();
            _p2WardrobeIndex = Mathf.Clamp(player2.characterSettings != null ? player2.characterSettings.OutfitIndex : 0, 0, _p2WardrobeButtons.Length - 1);
            SelectP2WardrobeIndex(_p2WardrobeIndex);
            _p2WardrobeCursorPos = P2WardrobeButtonCenter(_p2WardrobeIndex);
            _p2WardrobeCursorVelocity = 100f;
            _p2WardrobeStickActive = false;
            _p2WardrobeLayoutRefreshFrames = 3;
            LogV($"[P2Wardrobe] clone opened objects={_p2WardrobeObjects.Count} buttons={_p2WardrobeButtons.Length} highlight={_p2WardrobeSelectedHighlight != null} preview={_p2WardrobeWholeOutfitImage != null} model={player2.currentModel?.name}");
            return true;
        }

        static void DestroyP2WardrobeClone()
        {
            SetP2WardrobeInputEnabled(false);
            foreach (var go in _p2WardrobeObjects)
                if (go != null) Object.Destroy(go);
            _p2WardrobeObjects.Clear();
            _p2WardrobeCloneMap.Clear();
            _p2WardrobeMenu = null;
            _p2WardrobeButtons = null;
            _p2WardrobeMagnets = null;
            _p2WardrobeSelectedHighlight = null;
            _p2WardrobeWholeOutfitImage = null;
            _p2WardrobeIndex = 0;
            _p2WardrobeCursorPos = Vector2.zero;
            _p2WardrobeCursorVelocity = 100f;
            _p2WardrobeStickActive = false;
            _p2WardrobeLayoutRefreshFrames = 0;
        }

        static Button[] CloneWardrobeButtons(Button[] sourceButtons)
        {
            if (sourceButtons == null || sourceButtons.Length == 0) return null;
            var result = new Button[sourceButtons.Length];
            for (int i = 0; i < sourceButtons.Length; i++)
                result[i] = FindP2WardrobeCloneComponent(sourceButtons[i]);
            return result;
        }

        static T FindP2WardrobeCloneComponent<T>(T source) where T : Component
        {
            if (source == null) return null;
            var cloneObject = FindP2WardrobeCloneObject(source.gameObject);
            return cloneObject != null ? cloneObject.GetComponent<T>() : null;
        }

        static GameObject FindP2WardrobeCloneObject(GameObject source)
        {
            if (source == null) return null;
            if (_p2WardrobeCloneMap.TryGetValue(source, out var direct)) return direct;

            foreach (var pair in _p2WardrobeCloneMap)
            {
                var root = pair.Key != null ? pair.Key.transform : null;
                var cloneRoot = pair.Value != null ? pair.Value.transform : null;
                if (root == null || cloneRoot == null || !source.transform.IsChildOf(root)) continue;
                string path = RelativePath(root, source.transform);
                var cloneTransform = string.IsNullOrEmpty(path) ? cloneRoot : cloneRoot.Find(path);
                if (cloneTransform != null) return cloneTransform.gameObject;
            }
            return null;
        }

        static string RelativePath(Transform root, Transform child)
        {
            if (root == null || child == null || child == root) return string.Empty;
            var names = new List<string>();
            var t = child;
            while (t != null && t != root)
            {
                names.Insert(0, t.name);
                t = t.parent;
            }
            return t == root ? string.Join("/", names.ToArray()) : string.Empty;
        }

        static void SetP2WardrobeInputEnabled(bool enabled)
        {
            SetActionEnabled(p2ActionMove, enabled);
            SetActionEnabled(p2ActionJump, enabled);
            SetActionEnabled(p2ActionCancel, enabled);
            SetActionEnabled(p2ActionTabLeft, enabled);
            SetActionEnabled(p2ActionTabRight, enabled);
        }

        static void SetActionEnabled(InputAction action, bool enabled)
        {
            if (action == null) return;
            if (enabled)
            {
                if (!action.enabled) action.Enable();
            }
        }

        static void PopulateP2WardrobeButtons()
        {
            if (_p2WardrobeButtons == null || player2 == null || player2.currentModel == null) return;
            var outfits = player2.currentModel.outfits;
            for (int i = 0; i < _p2WardrobeButtons.Length; i++)
            {
                var button = _p2WardrobeButtons[i];
                if (button == null) continue;
                bool valid = outfits != null && i < outfits.Length;
                button.gameObject.SetActive(valid);
                if (!valid) continue;
                var image = button.GetComponent<Image>();
                if (image != null) image.sprite = outfits[i].sprite;
            }
        }

        static void SelectP2WardrobeIndex(int index)
        {
            if (_p2WardrobeButtons == null || player2 == null || player2.currentModel == null) return;
            var outfits = player2.currentModel.outfits;
            if (outfits == null || outfits.Length == 0) return;
            int max = Mathf.Min(_p2WardrobeButtons.Length, outfits.Length) - 1;
            _p2WardrobeIndex = Mathf.Clamp(index, 0, max);
            var button = _p2WardrobeButtons[_p2WardrobeIndex];
            if (button == null) return;
            var rt = button.GetComponent<RectTransform>();
            if (_p2WardrobeSelectedHighlight != null && rt != null)
                _p2WardrobeSelectedHighlight.position = rt.position;
            if (_p2WardrobeWholeOutfitImage != null)
                _p2WardrobeWholeOutfitImage.sprite = outfits[_p2WardrobeIndex].fullBodySprite;
            if (!_p2WardrobeStickActive)
                _p2WardrobeCursorPos = P2WardrobeButtonCenter(_p2WardrobeIndex);
        }

        static void ConfirmP2WardrobeIndex()
        {
            if (_p2WardrobeButtons == null || player2 == null || player2.currentModel == null) return;
            if (_p2WardrobeIndex < 0 || _p2WardrobeIndex >= _p2WardrobeButtons.Length) return;
            var button = _p2WardrobeButtons[_p2WardrobeIndex];
            if (button == null || !button.gameObject.activeInHierarchy) return;

            player2.ApplyOutfit(_p2WardrobeIndex);
            if (player2.characterSettings != null)
                player2.characterSettings.OutfitIndex = _p2WardrobeIndex;
            SelectP2WardrobeIndex(_p2WardrobeIndex);
        }

        static void TickP2WardrobeMenu()
        {
            var gp = GetP2MenuGamepad();
            RefreshP2WardrobeInitialLayout();
            bool cancel = (p2ActionCancel != null && p2ActionCancel.WasPressedThisFrame())
                       || (gp != null && gp.buttonEast.wasPressedThisFrame);
            if (cancel) { CloseP2Menu(); return; }

            if (_p2WardrobeOpened != null && player2 != null)
            {
                if (Vector3.Distance(player2.transform.position, _p2WardrobeOpened.transform.position) > 5f || player2.Stats == null || player2.Stats.IsDead)
                {
                    CloseP2Menu();
                    return;
                }
            }

            Vector2 move = p2ActionMove != null && p2ActionMove.enabled ? p2ActionMove.ReadValue<Vector2>() : Vector2.zero;
            if (move.sqrMagnitude < 0.01f && gp != null)
                move = gp.leftStick.ReadValue();
            TickP2WardrobeAnalogCursor(move, gp);
            bool confirm = (p2ActionJump != null && p2ActionJump.WasPressedThisFrame())
                        || (gp != null && gp.buttonSouth.wasPressedThisFrame);
            if (confirm) ConfirmP2WardrobeIndex();
        }

        static void RefreshP2WardrobeInitialLayout()
        {
            if (_p2WardrobeLayoutRefreshFrames <= 0) return;
            _p2WardrobeLayoutRefreshFrames--;
            Canvas.ForceUpdateCanvases();
            _p2WardrobeStickActive = false;
            SelectP2WardrobeIndex(_p2WardrobeIndex);
            _p2WardrobeCursorPos = P2WardrobeButtonCenter(_p2WardrobeIndex);
        }

        static void TickP2WardrobeAnalogCursor(Vector2 move, Gamepad gp)
        {
            if (_p2WardrobeButtons == null || _p2WardrobeButtons.Length == 0) return;

            if (move.sqrMagnitude < 0.04f)
            {
                _p2WardrobeStickActive = false;
                _p2WardrobeCursorVelocity = 100f;
                PullP2WardrobeCursorToSelected();
                return;
            }

            _p2WardrobeStickActive = true;
            _p2WardrobeCursorVelocity += 2200f * Time.unscaledDeltaTime;
            _p2WardrobeCursorVelocity = Mathf.Clamp(_p2WardrobeCursorVelocity, 650f, 1600f);
            _p2WardrobeCursorPos += move * _p2WardrobeCursorVelocity * Time.unscaledDeltaTime;
            ClampP2WardrobeCursorToButtons();

            int hover = P2WardrobeMagnetUnderCursor(_p2WardrobeCursorPos);
            if (hover < 0) hover = P2WardrobeClosestMagnetWithinRadius(_p2WardrobeCursorPos);
            if (hover >= 0 && hover != _p2WardrobeIndex)
            {
                SelectP2WardrobeIndex(hover);
                _p2WardrobeCursorPos = P2WardrobeButtonCenter(hover);
            }
        }

        static void PullP2WardrobeCursorToSelected()
        {
            if (_p2WardrobeIndex < 0) return;
            Vector2 target = P2WardrobeButtonCenter(_p2WardrobeIndex);
            Vector2 delta = target - _p2WardrobeCursorPos;
            if (delta.sqrMagnitude > 64f)
                _p2WardrobeCursorPos += delta.normalized * 450f * Time.unscaledDeltaTime;
            else
                _p2WardrobeCursorPos = target;
        }

        static void ClampP2WardrobeCursorToButtons()
        {
            if (_p2WardrobeButtons == null || _p2WardrobeButtons.Length == 0) return;
            bool any = false;
            float minX = 0f, maxX = 0f, minY = 0f, maxY = 0f;
            for (int i = 0; i < _p2WardrobeButtons.Length; i++)
            {
                var button = _p2WardrobeButtons[i];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                Vector2 p = P2WardrobeButtonCenter(i);
                if (!any)
                {
                    minX = maxX = p.x;
                    minY = maxY = p.y;
                    any = true;
                }
                else
                {
                    minX = Mathf.Min(minX, p.x);
                    maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y);
                    maxY = Mathf.Max(maxY, p.y);
                }
            }
            if (!any) return;
            _p2WardrobeCursorPos.x = Mathf.Clamp(_p2WardrobeCursorPos.x, minX - 80f, maxX + 80f);
            _p2WardrobeCursorPos.y = Mathf.Clamp(_p2WardrobeCursorPos.y, minY - 80f, maxY + 80f);
        }

        static void MoveP2WardrobeSelection(Vector2 direction)
        {
            if (_p2WardrobeButtons == null || _p2WardrobeButtons.Length == 0) return;
            if (_p2WardrobeIndex < 0 || _p2WardrobeIndex >= _p2WardrobeButtons.Length) return;
            var current = _p2WardrobeButtons[_p2WardrobeIndex];
            if (current == null) return;
            var currentMagnet = _p2WardrobeMagnets != null && _p2WardrobeIndex < _p2WardrobeMagnets.Length
                ? _p2WardrobeMagnets[_p2WardrobeIndex]
                : null;
            var currentRt = current.GetComponent<RectTransform>();
            Vector2 origin = currentMagnet != null ? currentMagnet.GetCenterPosition() : (Vector2)currentRt.position;
            float bestDistance = float.MaxValue;
            int best = -1;
            for (int i = 0; i < _p2WardrobeButtons.Length; i++)
            {
                if (i == _p2WardrobeIndex) continue;
                var button = _p2WardrobeButtons[i];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                var magnet = _p2WardrobeMagnets != null && i < _p2WardrobeMagnets.Length ? _p2WardrobeMagnets[i] : null;
                if (magnet != null && !magnet.AllowDPad) continue;
                Vector2 center;
                if (magnet != null)
                {
                    var rt = magnet.GetComponent<RectTransform>();
                    if (magnet.Viewport != null && rt != null && !RectTransformUtility.RectangleContainsScreenPoint(magnet.Viewport, rt.position))
                        continue;
                    center = magnet.GetCenterPosition();
                }
                else
                {
                    var rt = button.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    center = rt.position;
                }

                Vector2 delta = center - origin;
                if (delta.sqrMagnitude < 0.01f) continue;
                float dot = Vector2.Dot(delta.normalized, direction);
                if (dot > 0.5f && delta.sqrMagnitude < bestDistance)
                {
                    bestDistance = delta.sqrMagnitude;
                    best = i;
                }
            }

            if (best >= 0) SelectP2WardrobeIndex(best);
            else if (direction.x > 0.5f) SelectP2WardrobeIndex(_p2WardrobeIndex + 1);
            else if (direction.x < -0.5f) SelectP2WardrobeIndex(_p2WardrobeIndex - 1);
        }

        static Vector2 P2WardrobeButtonCenter(int index)
        {
            if (_p2WardrobeButtons == null || index < 0 || index >= _p2WardrobeButtons.Length) return _p2WardrobeCursorPos;
            var magnet = _p2WardrobeMagnets != null && index < _p2WardrobeMagnets.Length ? _p2WardrobeMagnets[index] : null;
            var canvasRt = _p2HudCanvas != null ? _p2HudCanvas.transform as RectTransform : null;
            if (canvasRt == null) return _p2WardrobeCursorPos;
            if (magnet != null) return canvasRt.InverseTransformPoint(magnet.GetCenterPosition());
            var rt = _p2WardrobeButtons[index] != null ? _p2WardrobeButtons[index].GetComponent<RectTransform>() : null;
            return rt != null ? (Vector2)canvasRt.InverseTransformPoint(rt.TransformPoint(rt.rect.center)) : _p2WardrobeCursorPos;
        }

        static int P2WardrobeMagnetUnderCursor(Vector2 cursor)
        {
            if (_p2WardrobeButtons == null) return -1;
            var canvasRt = _p2HudCanvas != null ? _p2HudCanvas.transform as RectTransform : null;
            if (canvasRt == null) return -1;
            Vector3 cursorWorld = canvasRt.TransformPoint(cursor);
            for (int i = 0; i < _p2WardrobeButtons.Length; i++)
            {
                var button = _p2WardrobeButtons[i];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                var rt = button.GetComponent<RectTransform>();
                if (rt == null) continue;
                Vector2 local = rt.InverseTransformPoint(cursorWorld);
                if (rt.rect.Contains(local)) return i;
            }
            return -1;
        }

        static int P2WardrobeClosestMagnetWithinRadius(Vector2 cursor)
        {
            if (_p2WardrobeButtons == null) return -1;
            float best = float.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < _p2WardrobeButtons.Length; i++)
            {
                var button = _p2WardrobeButtons[i];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                var magnet = _p2WardrobeMagnets != null && i < _p2WardrobeMagnets.Length ? _p2WardrobeMagnets[i] : null;
                if (magnet != null && !magnet.AllowStick) continue;
                Vector2 center = P2WardrobeButtonCenter(i);
                float radius = magnet != null ? magnet.Radius : 100f;
                float d = (center - cursor).sqrMagnitude;
                if (d < radius * radius && d < best)
                {
                    best = d;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        static Vector2 P2WardrobeClosestMagnetDelta(Vector2 cursor, out int index)
        {
            index = -1;
            float best = float.MaxValue;
            Vector2 bestDelta = Vector2.zero;
            if (_p2WardrobeButtons == null) return bestDelta;
            for (int i = 0; i < _p2WardrobeButtons.Length; i++)
            {
                var button = _p2WardrobeButtons[i];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                var magnet = _p2WardrobeMagnets != null && i < _p2WardrobeMagnets.Length ? _p2WardrobeMagnets[i] : null;
                if (magnet != null && !magnet.AllowStick) continue;
                Vector2 delta = P2WardrobeButtonCenter(i) - cursor;
                float d = delta.sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    bestDelta = delta;
                    index = i;
                }
            }
            return bestDelta;
        }

        static Gamepad GetP2MenuGamepad()
        {
            return GetP2BoundGamepad();
        }

        static void EnsureP2MenuRaycaster()
        {
            if (_p2MenuRaycaster != null || _p2HudCanvas == null) return;
            _p2MenuRaycaster = _p2HudCanvas.GetComponent<GraphicRaycaster>() ?? _p2HudCanvas.gameObject.AddComponent<GraphicRaycaster>();
        }

        // ── 鱼饵菜单(原版风格：按住 LT 打开、松开关闭；左摇杆左右切换，切到即装备) ──────────────
        //  原版 FishingRod：按住 RMB(=P2 LT/ActionContext)打开 FishingBait 菜单、松开关闭(FishingRod.Update)。
        //  UI_Cost_Interactable_FishingBait 显式屏蔽 Pointer 事件 → 通用虚拟光标点不动 → 这里专门做左右导航 + 直接 EquipBait。
        //  不用 A/B(=跳/蹲，会漏触发)：左右切换即时 EquipBait + 刷新浮标模型，松开 LT 关闭。
        const float P2BaitMenuYOffset = 180f;   // 准心(屏幕中心)上方一点
        static readonly List<UI_Cost_Interactable_FishingBait> _p2BaitItems = new List<UI_Cost_Interactable_FishingBait>();
        static int _p2BaitIndex;
        static bool _p2BaitStickNeutral = true;
        static FishingRod _p2Rod;
        static MeshRenderer _p2BobberRenderer;
        static MeshFilter _p2BobberFilter;
        static FieldInfo _fBobberRend, _fBobberFilt;
        static FieldInfo _fHoverImage;   // UI_Cost_Interactable.hoverImage(protected) —— 真正可见的高亮框

        static void SetupP2BaitMenu()
        {
            _p2BaitItems.Clear();
            foreach (var root in _p2BaitClones)
                if (root != null)
                    _p2BaitItems.AddRange(root.GetComponentsInChildren<UI_Cost_Interactable_FishingBait>(true));
            // 不预排序：导航时按【实时屏幕 X】找方向上的相邻项(见 TickP2BaitMenu)，自洽不依赖列表顺序/相机朝向，避免左右颠倒。
            // 缓存 P2 鱼竿的浮标渲染器/网格 → 切饵时即时刷新浮标模型(否则要等菜单关闭 FishingRod.Update 才刷)。
            _p2Rod = player2 != null ? player2.GetComponentInChildren<FishingRod>(true) : null;
            if (_p2Rod != null)
            {
                if (_fBobberRend == null)
                {
                    var t = typeof(FishingRod); var bf = BindingFlags.Instance | BindingFlags.NonPublic;
                    _fBobberRend = t.GetField("bobberMeshRenderer", bf);
                    _fBobberFilt = t.GetField("bobberMeshFilter", bf);
                }
                _p2BobberRenderer = _fBobberRend?.GetValue(_p2Rod) as MeshRenderer;
                _p2BobberFilter   = _fBobberFilt?.GetValue(_p2Rod) as MeshFilter;
            }
            // 索引初始化到当前已装备的鱼饵
            var cur = player2 != null && player2.FishingBaitHandler != null ? player2.FishingBaitHandler.CurrentBait : null;
            _p2BaitIndex = 0;
            for (int i = 0; i < _p2BaitItems.Count; i++)
                if (_p2BaitItems[i].baitToEquip == cur) { _p2BaitIndex = i; break; }
            _p2BaitStickNeutral = true;
            RefreshP2BaitItemCounts();
            HighlightP2Bait(_p2BaitIndex);
            LogV("[P2Bait] menu items=" + _p2BaitItems.Count + ", index=" + _p2BaitIndex);
        }

        static void HighlightP2Bait(int idx)
        {
            var cm = ComponentManager<CraftingMenu>.Value;
            var hoover = cm != null ? cm.spriteHoover : null;
            var normal = cm != null ? cm.spriteNormal : null;
            if (_fHoverImage == null)
                _fHoverImage = typeof(UI_Cost_Interactable).GetField("hoverImage",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < _p2BaitItems.Count; i++)
            {
                var bb = _p2BaitItems[i].backgroundButton;
                if (bb != null && bb.image != null) bb.image.sprite = (i == idx) ? hoover : normal;
                // 真正可见的高亮是 hoverImage：原版 OnEnable/OnClick 把它点在已装备项上，导航不动它 → 看着"第一个一直高亮"。
                //  这里让 hoverImage 跟随当前导航项。
                var hi = _fHoverImage?.GetValue(_p2BaitItems[i]) as GameObject;
                if (hi != null) hi.SetActive(i == idx);
            }
        }

        static void SelectP2Bait(int idx)
        {
            if (idx < 0 || idx >= _p2BaitItems.Count || player2 == null || player2.FishingBaitHandler == null) return;
            var item = _p2BaitItems[idx];
            var bait = item.baitToEquip;
            int count = bait == null ? 1 : CountP2Bait(bait);
            if (count <= 0) return;   // 没有该鱼饵 → 不选(每次导航都会触发，不打日志)
            player2.FishingBaitHandler.EquipBait(bait);
            UI_Cost_Interactable_FishingBait.currentSelected = item;
            // 即时刷新浮标(鱼钩)模型——否则要等菜单关闭、FishingRod.Update 本地分支才刷新，体感"没立即切换"。
            if (_p2BobberRenderer != null && _p2BobberFilter != null)
                player2.FishingBaitHandler.SetBaitModelFromCurrentBait(_p2BobberRenderer, _p2BobberFilter);
            RefreshP2BaitItemCounts();
        }

        static int CountP2Bait(Item_Base bait)
        {
            if (bait == null) return 0;
            if (GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.unlimitedResources) return int.MaxValue;
            return P2InventoryStore.CountBackpackItem(bait.UniqueIndex) + CountP2HotbarItem(bait.UniqueIndex);
        }

        static void RefreshP2BaitItemCounts()
        {
            foreach (var item in _p2BaitItems)
            {
                if (item == null || item.baitToEquip == null) continue;
                item.RefreshDisplayInventoryCount(new Cost(item.baitToEquip, 1), CountP2Bait(item.baitToEquip));
            }
        }

        static void TickP2BaitMenu()
        {
            var gp = GetP2BoundGamepad(); if (gp == null) return;
            
            if (!gp.leftTrigger.isPressed) { CloseP2Menu(); return; }
            if (_p2BaitItems.Count == 0) return;

            float x = gp.leftStick.ReadValue().x;
            int dir = 0;   // +1 = 向屏幕右、-1 = 向屏幕左
            bool right = gp.dpad.right.wasPressedThisFrame || (x > 0.5f && _p2BaitStickNeutral);
            bool left  = gp.dpad.left.wasPressedThisFrame  || (x < -0.5f && _p2BaitStickNeutral);
            if (right) dir = 1;
            else if (left) dir = -1;
            if (Mathf.Abs(x) < 0.3f) _p2BaitStickNeutral = true;
            else if (Mathf.Abs(x) > 0.5f) _p2BaitStickNeutral = false;

            if (dir != 0)
            {
                // 按【画布本地 X】找该方向上最近的相邻鱼饵：右 → 本地 X 更大者中最近的；左 → 更小者中最近的。
                //  画布本地 X 即视觉左右(+X=右)，与相机朝向无关 → 恒"看哪边按哪边"，不会左右颠倒。
                float curX = BaitVisualX(_p2BaitIndex);
                int best = -1; float bestD = float.MaxValue;
                for (int i = 0; i < _p2BaitItems.Count; i++)
                {
                    if (i == _p2BaitIndex) continue;
                    float signed = (BaitVisualX(i) - curX) * dir;   // 目标方向上为正
                    if (signed > 0.1f && signed < bestD) { bestD = signed; best = i; }
                }
                if (best >= 0)
                {
                    _p2BaitIndex = best;
                    HighlightP2Bait(best);
                    SelectP2Bait(best);   // 切到即装备 + 刷新浮标(即时反馈)
                }
            }
        }

        // 鱼饵项在 P2 画布本地空间的 X(+X=视觉右)，与相机无关 → 导航方向稳定不颠倒。
        static float BaitVisualX(int idx)
        {
            if (idx < 0 || idx >= _p2BaitItems.Count || _p2BaitItems[idx] == null) return 0f;
            var t = _p2BaitItems[idx].transform.position;
            return _p2HudCanvas != null ? _p2HudCanvas.transform.InverseTransformPoint(t).x : t.x;
        }

        // 每帧(Runtime.Tick)：菜单打开时驱动光标 + 通用 uGUI 事件(hover/click)。
        internal static void TickP2Menu()
        {
            if (!_p2MenuOpen) return;
            if (_p2MenuType == MenuType.FishingBait) { TickP2BaitMenu(); return; }
            if (_p2MenuType == MenuType.Wardrobe) { TickP2WardrobeMenu(); return; }
            if (_p2MenuType == MenuType.TradingPost) { TickP2TradingPost(); return; }
            var gp = GetP2BoundGamepad(); if (gp == null) return;

            if (gp.buttonEast.wasPressedThisFrame) { CloseP2Menu(); return; }   // B 返回

            Vector2 stick = gp.leftStick.ReadValue(); if (stick.sqrMagnitude < 0.02f) stick = Vector2.zero;
            _p2InvCursorPos += stick * P2BpCursorSpeed * Time.unscaledDeltaTime;
            var canvasRT = _p2HudCanvas.transform as RectTransform;
            Vector2 halfsz = canvasRT.rect.size * 0.5f;
            _p2InvCursorPos.x = Mathf.Clamp(_p2InvCursorPos.x, -halfsz.x, halfsz.x);
            _p2InvCursorPos.y = Mathf.Clamp(_p2InvCursorPos.y, -halfsz.y, halfsz.y);
            if (_p2InvCursor != null) _p2InvCursor.anchoredPosition = _p2InvCursorPos;

            if (_p2MenuRaycaster == null || EventSystem.current == null || _p2InvCursor == null) return;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(_p2UiCamera, _p2InvCursor.position);
            var ped = new PointerEventData(EventSystem.current) { position = screen };
            _p2MenuRayResults.Clear();
            _p2MenuRaycaster.Raycast(ped, _p2MenuRayResults);
            GameObject top = _p2MenuRayResults.Count > 0 ? _p2MenuRayResults[0].gameObject : null;

            if (top != _p2MenuHover)
            {
                if (_p2MenuHover != null) ExecuteEvents.Execute(_p2MenuHover, ped, ExecuteEvents.pointerExitHandler);
                if (top != null) ExecuteEvents.Execute(top, ped, ExecuteEvents.pointerEnterHandler);
                _p2MenuHover = top;
            }

            bool a = gp.buttonSouth.isPressed;
            if (a && !_wasAMenu && top != null)
            {
                ExecuteEvents.Execute(top, ped, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(top, ped, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(top, ped, ExecuteEvents.pointerClickHandler);
            }
            _wasAMenu = a;
        }

        internal static void ResetP2Menu()
        {
            if (_p2MenuOpen) CloseP2Menu();
            _p2MenuRaycaster = null;
        }
    }
}
