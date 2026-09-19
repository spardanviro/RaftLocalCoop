using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SplitScreen
{
    public static partial class Main
    {
        static readonly FieldInfo s_instrumentCurrentUser =
            typeof(Instrument).GetField("currentUser", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_instrumentKeys =
            typeof(Instrument).GetField("keys", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_pianoUiCanvas =
            typeof(PianoUI).GetField("canvas", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_pianoUiImage =
            typeof(PianoUI).GetField("image", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_pianoUiAPressed =
            typeof(PianoUI).GetField("ApressedTransform", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_pianoUiLTPressed =
            typeof(PianoUI).GetField("LTpressedTransform", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_pianoUiRTPressed =
            typeof(PianoUI).GetField("RTpressedTransform", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_pianoUiNotes =
            typeof(PianoUI).GetField("notes", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_pianoUiDefaultSprite =
            typeof(PianoUI).GetField("defaultSprite", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_pianoUiSingleton =
            typeof(SimpleMonoBehaviourSingleton<PianoUI>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic);

        static readonly Dictionary<Instrument, object> _p2PianoPressed = new Dictionary<Instrument, object>();
        static readonly Dictionary<object, int> _p2PianoKeyIndexCache = new Dictionary<object, int>();
        static FieldInfo _p2PianoKeyIndexField, _p2PianoMinAngleField, _p2PianoMaxAngleField;
        static MethodInfo _p2PianoOnPressed, _p2PianoOnReleased;
        static Instrument _p2PianoInstrument;
        static bool _p2PianoUiOpen;
        static readonly List<GameObject> _p2PianoObjects = new List<GameObject>();
        static readonly Dictionary<Transform, Transform> _p2PianoCloneMap = new Dictionary<Transform, Transform>();
        static Canvas _p2PianoOriginalCanvas;
        static bool _p2PianoOriginalCanvasEnabled;
        static Image _p2PianoOriginalImage;
        static RectTransform _p2PianoOriginalAPressed;
        static RectTransform _p2PianoOriginalLTPressed;
        static RectTransform _p2PianoOriginalRTPressed;
        static Canvas _p2PianoCloneCanvas;
        static RectTransform _p2PianoCloneRoot;
        static Image _p2PianoCloneImage;
        static RectTransform _p2PianoCloneAPressed;
        static RectTransform _p2PianoCloneLTPressed;
        static RectTransform _p2PianoCloneRTPressed;
        static System.Array _p2PianoNotes;
        static Sprite _p2PianoDefaultSprite;
        static FieldInfo _p2PianoNoteSpriteField;
        static FieldInfo _p2PianoNoteIndexField;
        static int _p2PianoLastTickLogFrame = -1000;

        internal static bool IsP2PianoActive => _p2PianoUiOpen;

        static void LogP2Piano(string message)
        {
            LogV("[P2 Piano] " + message);
        }

        internal static bool IsP2UsingInstrument(Instrument instrument)
        {
            return instrument != null && player2 != null && ReferenceEquals(s_instrumentCurrentUser?.GetValue(instrument), player2);
        }

        internal static bool TickP2Instrument(Instrument instrument)
        {
            if (!IsP2UsingInstrument(instrument)) { ReleaseP2Instrument(instrument); return false; }
            EnsureP2PianoUi(instrument);
            SetP2PianoCanvasEnabled(true);
            ResolveP2InstrumentKeyAccess(instrument);

            var keys = s_instrumentKeys?.GetValue(instrument) as IList;
            if (keys == null || keys.Count == 0) return true;

            var ui = P2PianoUiSource();
            if (Time.frameCount - _p2PianoLastTickLogFrame >= 120)
            {
                _p2PianoLastTickLogFrame = Time.frameCount;
                LogP2Piano(
                    "Tick instrument=" + SafeName(instrument) +
                    " ui=" + SafeName(ui) +
                    " cloneCanvas=" + CanvasInfo(_p2PianoCloneCanvas) +
                    " originalCanvas=" + CanvasInfo(_p2PianoOriginalCanvas) +
                    " keys=" + keys.Count +
                    " p2Move=" + ActionInfo(p2ActionMove) +
                    " p2Jump=" + ActionInfo(p2ActionJump) +
                    " p2Cancel=" + ActionInfo(p2ActionCancel) +
                    " currentUserIsP2=" + ReferenceEquals(s_instrumentCurrentUser?.GetValue(instrument), player2) +
                    " isAttached=" + (player2 != null && player2.PlayerNetworkManager != null && player2.PlayerNetworkManager.IsAttached));
            }
            if (P2PianoCancelPressed())
            {
                SuppressP2CrouchOnExit();
                LogP2Piano("B/cancel pressed; leaving instrument=" + SafeName(instrument));
                StopP2UsingInstrument(instrument);
                ReleaseP2Instrument(instrument);
                return true;
            }

            bool press = p2ActionJump != null && p2ActionJump.IsPressed();
            bool lower = p2ActionContext != null && p2ActionContext.IsPressed();
            bool higher = p2ActionFire != null && p2ActionFire.IsPressed();
            SetP2PianoButtonScales(press, lower, higher);

            Vector2 stick = p2ActionMove != null ? p2ActionMove.ReadValue<Vector2>() : Vector2.zero;
            object selected = null;
            int selectedIndex = -1;
            if (stick.sqrMagnitude > 0.01f)
            {
                float angle = Mathf.Atan2(stick.y, stick.x) * Mathf.Rad2Deg;
                bool negativeY = angle < 0f;
                if (negativeY) angle += 360f;

                foreach (var key in keys)
                {
                    if (key == null) continue;
                    float min = _p2PianoMinAngleField != null ? (float)_p2PianoMinAngleField.GetValue(key) : 0f;
                    float max = _p2PianoMaxAngleField != null ? (float)_p2PianoMaxAngleField.GetValue(key) : 0f;
                    bool wrap = !negativeY && max > 360f;
                    float lo = wrap ? min - 360f : min;
                    float hi = wrap ? max - 360f : max;
                    if (angle > lo && angle < hi)
                    {
                        selected = key;
                        selectedIndex = P2PianoKeyIndex(key);
                        break;
                    }
                }
            }
            else
            {
                ResetP2PianoNote();
            }

            if (selected != null) SetP2PianoNote(selectedIndex);

            _p2PianoPressed.TryGetValue(instrument, out var previous);
            if (!press || selected == null)
            {
                if (previous != null) ReleaseP2PianoKey(instrument, sendNetwork: true);
                return true;
            }

            if (previous != null && !ReferenceEquals(previous, selected))
                ReleaseP2PianoKey(instrument, sendNetwork: true);

            if (!_p2PianoPressed.TryGetValue(instrument, out previous) || !ReferenceEquals(previous, selected))
            {
                int pitch = selectedIndex + 1 + 7;
                if (lower) pitch -= 7;
                else if (higher) pitch += 7;
                if (pitch > 24) pitch = 24;
                if (pitch < 0) pitch = 0;

                var msg = new Message_InstrumentKeyModified
                {
                    Type = Messages.Instrument_KeyModified,
                    NetworkID_ObjectIndex = instrument.ObjectIndex,
                    keyIndex = selectedIndex,
                    pressed = true,
                    pitchIndex = pitch
                };
                LogP2Piano(
                    "Press note selectedIndex=" + selectedIndex +
                    " pitch=" + pitch +
                    " lower=" + lower +
                    " higher=" + higher +
                    " onPressed=" + (_p2PianoOnPressed != null) +
                    " uiImage=" + SafeName(s_pianoUiImage?.GetValue(ui) as Object));
                _p2PianoOnPressed?.Invoke(selected, new object[] { msg });
                _p2PianoPressed[instrument] = selected;
                AchievementHandler.AddPlayerInstrumentNotesPlayed();
            }
            return true;
        }

        internal static void ReleaseP2Instrument(Instrument instrument)
        {
            if (instrument == null) return;
            if (_p2PianoInstrument != instrument && !_p2PianoPressed.ContainsKey(instrument)) return;
            LogP2Piano("Release instrument=" + SafeName(instrument) + " hasPressed=" + _p2PianoPressed.ContainsKey(instrument));
            ReleaseP2PianoKey(instrument, sendNetwork: true);
            if (_p2PianoInstrument == instrument) CloseP2PianoUi();
        }

        static void ReleaseP2PianoKey(Instrument instrument, bool sendNetwork)
        {
            if (instrument == null) return;
            if (_p2PianoPressed.TryGetValue(instrument, out var key) && key != null)
            {
                ResolveP2InstrumentKeyAccess(instrument);
                _p2PianoOnReleased?.Invoke(key, new object[] { sendNetwork });
            }
            _p2PianoPressed.Remove(instrument);
        }

        internal static void CloseP2PianoUi()
        {
            LogP2Piano("Close UI instrument=" + SafeName(_p2PianoInstrument) + " cloneCanvas=" + CanvasInfo(_p2PianoCloneCanvas));
            var canvas = ComponentManager<CanvasHelper>.Value;
            var gm = canvas != null ? canvas.GetMenu(MenuType.Piano) : null;

            foreach (var go in _p2PianoObjects)
                if (go != null) Object.Destroy(go);
            _p2PianoObjects.Clear();
            _p2PianoCloneMap.Clear();
            RestoreP2PianoUiFields();

            if (gm != null && gm.IsOpen && !P1AttachedForPianoRestore()) gm.Close();
            if (canvas != null && CanvasHelper.ActiveMenu == MenuType.Piano && !P1AttachedForPianoRestore())
            {
                canvas.CloseMenu(MenuType.Piano);
            }
            var ui = P2PianoUiSource();
            if (ui != null)
            {
                ui.ResetNote();
            }
            ResetP2PianoNote();
            SetP2PianoButtonScales(false, false, false);
            if (player1 != null && player1.PlayerScript != null && !P1AttachedForPianoRestore())
                player1.PlayerScript.SetMouseLookScripts(true);
            _p2PianoUiOpen = false;
            _p2PianoInstrument = null;
        }

        internal static void SetP2PianoCanvasEnabled(bool enabled)
        {
            var canvas = _p2PianoCloneCanvas != null ? _p2PianoCloneCanvas : s_pianoUiCanvas?.GetValue(P2PianoUiSource()) as Canvas;
            if (canvas != null) canvas.enabled = enabled;
        }

        static void EnsureP2PianoUi(Instrument instrument)
        {
            if (_p2HudCanvas == null) return;
            var ui = P2PianoUiSource();
            if (ui == null)
            {
                LogP2Piano("Ensure failed: PianoUI source not found. CanvasHelperActive=" + (ComponentManager<CanvasHelper>.Value != null));
                return;
            }

            if (_p2PianoUiOpen && _p2PianoInstrument == instrument && _p2PianoCloneCanvas != null)
            {
                PositionP2PianoUi();
                _p2PianoCloneCanvas.enabled = true;
                return;
            }

            RestoreP2PianoUiFields();
            foreach (var go in _p2PianoObjects)
                if (go != null) Object.Destroy(go);
            _p2PianoObjects.Clear();
            _p2PianoCloneMap.Clear();

            var sourceCanvas = s_pianoUiCanvas?.GetValue(ui) as Canvas;
            if (sourceCanvas == null)
            {
                LogP2Piano("Ensure failed: source PianoUI.canvas is null. ui=" + SafeName(ui));
                return;
            }

            _p2PianoOriginalCanvas = sourceCanvas;
            _p2PianoOriginalCanvasEnabled = sourceCanvas.enabled;
            _p2PianoOriginalImage = s_pianoUiImage?.GetValue(ui) as Image;
            _p2PianoOriginalAPressed = s_pianoUiAPressed?.GetValue(ui) as RectTransform;
            _p2PianoOriginalLTPressed = s_pianoUiLTPressed?.GetValue(ui) as RectTransform;
            _p2PianoOriginalRTPressed = s_pianoUiRTPressed?.GetValue(ui) as RectTransform;
            LogP2Piano(
                "Ensure begin sourceCanvas=" + CanvasInfo(sourceCanvas) +
                " sourceChildren=" + sourceCanvas.transform.childCount +
                " image=" + PathOf(_p2PianoOriginalImage != null ? _p2PianoOriginalImage.transform : null) +
                " A=" + PathOf(_p2PianoOriginalAPressed) +
                " LT=" + PathOf(_p2PianoOriginalLTPressed) +
                " RT=" + PathOf(_p2PianoOriginalRTPressed) +
                " hud=" + SafeName(_p2HudCanvas));

            // 不再强制第一人称:与 P1 对齐 —— 弹琴时保留玩家当前视角,FP/TP 都可用且随时能切。
            // (FP 下的锁视角由 P2CameraController 的 look 门控负责,TP 下允许自由环视。)

            bool sourceWasActive = sourceCanvas.gameObject.activeSelf;
            var originalPianoSingleton = SimpleMonoBehaviourSingleton<PianoUI>.Instance;
            sourceCanvas.gameObject.SetActive(false);
            GameObject rootGo = null;
            try
            {
                rootGo = Object.Instantiate(sourceCanvas.gameObject, _p2HudCanvas.transform, false);
            }
            finally
            {
                sourceCanvas.gameObject.SetActive(sourceWasActive);
                if (originalPianoSingleton != null)
                    s_pianoUiSingleton?.SetValue(null, originalPianoSingleton);
                else if (ui != null)
                    s_pianoUiSingleton?.SetValue(null, ui);
            }
            rootGo.name = "P2 Piano UI";
            SanitizeClonedUiRoot(rootGo);
            foreach (var cloneUi in rootGo.GetComponentsInChildren<PianoUI>(true))
                Object.DestroyImmediate(cloneUi);
            if (originalPianoSingleton != null)
                s_pianoUiSingleton?.SetValue(null, originalPianoSingleton);
            else
                s_pianoUiSingleton?.SetValue(null, ui);
            rootGo.SetActive(true);
            ActivateP2PianoUiClone(rootGo.transform);

            _p2PianoObjects.Add(rootGo);
            _p2PianoCloneRoot = rootGo.GetComponent<RectTransform>();
            _p2PianoCloneCanvas = rootGo.GetComponent<Canvas>();
            if (_p2PianoCloneCanvas == null)
                _p2PianoCloneCanvas = rootGo.AddComponent<Canvas>();
            if (rootGo.GetComponent<GraphicRaycaster>() == null)
                rootGo.AddComponent<GraphicRaycaster>();
            _p2PianoCloneCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _p2PianoCloneCanvas.worldCamera = _p2UiCamera;
            _p2PianoCloneCanvas.planeDistance = 1f;
            _p2PianoCloneCanvas.overrideSorting = true;
            _p2PianoCloneCanvas.sortingOrder = 30000;
            _p2PianoCloneMap[sourceCanvas.transform] = _p2PianoCloneRoot;
            BuildP2PianoCloneMap(sourceCanvas.transform, _p2PianoCloneRoot);
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) SetLayerRecursively(rootGo.transform, uiLayer);

            var sourceRect = sourceCanvas.transform as RectTransform;
            if (sourceRect != null)
            {
                _p2PianoCloneRoot.sizeDelta = sourceRect.rect.size;
            }
            _p2PianoCloneRoot.anchorMin = _p2PianoCloneRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _p2PianoCloneRoot.pivot = new Vector2(0.5f, 0.5f);

            CaptureP2PianoCloneFields(ui);
            PositionP2PianoUi();
            _p2PianoCloneCanvas.enabled = true;
            if (_p2PianoOriginalCanvas != null) _p2PianoOriginalCanvas.enabled = false;
            _p2PianoUiOpen = true;
            _p2PianoInstrument = instrument;
            LogP2Piano(
                "Ensure done cloneCanvas=" + CanvasInfo(_p2PianoCloneCanvas) +
                " cloneRoot=" + RectInfo(_p2PianoCloneRoot) +
                " imageClone=" + SafeName(_p2PianoCloneImage) +
                " AClone=" + SafeName(_p2PianoCloneAPressed) +
                " LTClone=" + SafeName(_p2PianoCloneLTPressed) +
                " RTClone=" + SafeName(_p2PianoCloneRTPressed));
        }

        static PianoUI P2PianoUiSource()
        {
            var ui = FindScenePianoUiLoose();
            if (ui != null)
            {
                RepairScenePianoUi(ui);
                s_pianoUiSingleton?.SetValue(null, ui);
            }
            return ui;
        }

        internal static void EnsureVanillaPianoUiReady()
        {
            var ui = FindScenePianoUiLoose();
            if (ui == null) return;
            RepairScenePianoUi(ui);
            s_pianoUiSingleton?.SetValue(null, ui);
        }

        static PianoUI FindScenePianoUiLoose()
        {
            var instance = SimpleMonoBehaviourSingleton<PianoUI>.Instance;
            if (IsUsableScenePianoUi(instance)) return instance;

            PianoUI fallback = null;
            var all = Resources.FindObjectsOfTypeAll<PianoUI>();
            if (all == null) return null;
            foreach (var candidate in all)
            {
                if (candidate == null || candidate.gameObject == null) continue;
                if (!candidate.gameObject.scene.IsValid()) continue;
                if (IsP2PianoClone(candidate.transform)) continue;

                var canvas = candidate.GetComponentInParent<Canvas>(true);
                if (canvas != null && canvas.gameObject != null && canvas.gameObject.scene.IsValid() && canvas.name == "_CanvasPiano")
                    return candidate;

                if (fallback == null) fallback = candidate;
            }
            return fallback;
        }

        static bool IsUsableScenePianoUi(PianoUI ui)
        {
            if (ui == null || ui.gameObject == null) return false;
            if (!ui.gameObject.scene.IsValid()) return false;
            if (IsP2PianoClone(ui.transform)) return false;
            return true;
        }

        static bool IsScenePianoUi(PianoUI ui)
        {
            if (ui == null || ui.gameObject == null) return false;
            if (!ui.gameObject.scene.IsValid()) return false;
            var canvas = s_pianoUiCanvas?.GetValue(ui) as Canvas;
            if (canvas == null || canvas.gameObject == null) return false;
            if (!canvas.gameObject.scene.IsValid()) return false;
            return true;
        }

        static bool IsP2PianoClone(Transform transform)
        {
            while (transform != null)
            {
                if (transform.name == "P2 Piano UI") return true;
                transform = transform.parent;
            }
            return false;
        }

        static void RepairScenePianoUi(PianoUI ui)
        {
            if (ui == null) return;

            var canvas = ui.GetComponentInParent<Canvas>(true);
            if (canvas == null || canvas.name != "_CanvasPiano" || IsP2PianoClone(canvas.transform))
                canvas = FindScenePianoCanvas();
            if (canvas != null && s_pianoUiCanvas != null)
                s_pianoUiCanvas.SetValue(ui, canvas);

            var root = ui.transform;
            if (canvas != null)
            {
                var pianoRoot = FindChildRecursive(canvas.transform, "Piano");
                if (pianoRoot != null) root = pianoRoot;
            }

            var image = root != null ? root.GetComponent<Image>() : null;
            if (image != null && s_pianoUiImage != null)
                s_pianoUiImage.SetValue(ui, image);

            var a = FindRectRecursive(root, "X");
            var lt = FindRectRecursive(root, "LT");
            var rt = FindRectRecursive(root, "RT");
            if (a != null && s_pianoUiAPressed != null) s_pianoUiAPressed.SetValue(ui, a);
            if (lt != null && s_pianoUiLTPressed != null) s_pianoUiLTPressed.SetValue(ui, lt);
            if (rt != null && s_pianoUiRTPressed != null) s_pianoUiRTPressed.SetValue(ui, rt);
        }

        static Canvas FindScenePianoCanvas()
        {
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
            if (canvases == null) return null;
            foreach (var canvas in canvases)
            {
                if (canvas == null || canvas.gameObject == null) continue;
                if (!canvas.gameObject.scene.IsValid()) continue;
                if (IsP2PianoClone(canvas.transform)) continue;
                if (canvas.name == "_CanvasPiano") return canvas;
            }
            return null;
        }

        static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChildRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        static RectTransform FindRectRecursive(Transform root, string name)
        {
            var found = FindChildRecursive(root, name);
            return found as RectTransform;
        }

        static void ActivateP2PianoUiClone(Transform root)
        {
            if (root == null) return;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.SetActive(true);
        }

        static void RestoreGlobalInputAfterP2Piano()
        {
            var canvas = ComponentManager<CanvasHelper>.Value;
            if (canvas != null && CanvasHelper.ActiveMenu == MenuType.Piano && !P1AttachedForPianoRestore())
            {
                canvas.CloseMenu(MenuType.Piano);
            }
            Helper.SetCursorVisibleAndLockState(false, CursorLockMode.Locked);
            if (SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance != null)
                SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.SwitchCurrentActionMap("Player");
            if (player1 != null && player1.PlayerScript != null)
                player1.PlayerScript.SetMouseLookScripts(true);
        }

        static bool P2PianoCancelPressed()
        {
            if (p2ActionCancel != null && p2ActionCancel.WasPressedThisFrame()) return true;
            var gp = GetP2BoundGamepad();
            return gp != null && gp.buttonEast.wasPressedThisFrame;
        }

        static void StopP2UsingInstrument(Instrument instrument)
        {
            if (instrument == null || player2 == null) return;
            var seats = instrument.GetComponentsInChildren<AttachPlayer>(true);
            LogP2Piano("StopP2UsingInstrument seats=" + seats.Length);
            foreach (var seat in seats)
            {
                LogP2Piano("Seat " + SafeName(seat) + " carried=" + SafeName(seat != null ? seat.carriedPlayer : null));
                if (P2FurnitureAdapter.StopAttachedPlayer(seat, player2, manipulatePosition: true))
                {
                    RestoreP2AfterInstrumentExit();
                    LogP2Piano("Stopped seat. attached=" + (player2.PlayerNetworkManager != null && player2.PlayerNetworkManager.IsAttached) +
                               " pcEnabled=" + (player2.PersonController != null && player2.PersonController.enabled) +
                               " movementFree=" + (player2.PersonController != null && player2.PersonController.IsMovementFree));
                    return;
                }
            }
            RestoreP2AfterInstrumentExit();
            LogP2Piano("No matching seat found; forced restore only.");
        }

        static void BuildP2PianoCloneMap(Transform sourceRoot, Transform cloneRoot)
        {
            if (sourceRoot == null || cloneRoot == null) return;
            for (int i = 0; i < sourceRoot.childCount; i++)
            {
                var sourceChild = sourceRoot.GetChild(i);
                var cloneChild = cloneRoot.Find(sourceChild.name);
                if (cloneChild == null && i < cloneRoot.childCount)
                    cloneChild = cloneRoot.GetChild(i);
                if (cloneChild == null) continue;
                _p2PianoCloneMap[sourceChild] = cloneChild;
                BuildP2PianoCloneMap(sourceChild, cloneChild);
            }

            foreach (var c in cloneRoot.GetComponentsInChildren<Canvas>(true))
            {
                if (c == null) continue;
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = _p2UiCamera;
                c.planeDistance = 1f;
            }
        }

        static void CaptureP2PianoCloneFields(PianoUI ui)
        {
            if (ui == null) return;
            _p2PianoCloneImage = CloneP2PianoComponent(_p2PianoOriginalImage);
            _p2PianoCloneAPressed = CloneP2PianoComponent(_p2PianoOriginalAPressed);
            _p2PianoCloneLTPressed = CloneP2PianoComponent(_p2PianoOriginalLTPressed);
            _p2PianoCloneRTPressed = CloneP2PianoComponent(_p2PianoOriginalRTPressed);
            _p2PianoNotes = s_pianoUiNotes?.GetValue(ui) as System.Array;
            _p2PianoDefaultSprite = s_pianoUiDefaultSprite?.GetValue(ui) as Sprite;

            if (_p2PianoNotes != null && _p2PianoNotes.Length > 0 && _p2PianoNoteSpriteField == null)
            {
                var note = _p2PianoNotes.GetValue(0);
                var type = note != null ? note.GetType() : null;
                if (type != null)
                {
                    _p2PianoNoteSpriteField = type.GetField("sprite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _p2PianoNoteIndexField = type.GetField("index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }

            ResetP2PianoNote();
            SetP2PianoButtonScales(false, false, false);
        }

        static T CloneP2PianoComponent<T>(T source) where T : Component
        {
            if (source == null) return null;
            if (_p2PianoCloneMap.TryGetValue(source.transform, out var cloneTransform))
                return cloneTransform.GetComponent<T>();

            var sourceCanvas = _p2PianoOriginalCanvas != null ? _p2PianoOriginalCanvas.transform : null;
            if (sourceCanvas == null || !source.transform.IsChildOf(sourceCanvas)) return null;
            if (source.transform == sourceCanvas)
                return _p2PianoCloneRoot != null ? _p2PianoCloneRoot.GetComponent<T>() : null;
            string path = RelativePath(sourceCanvas, source.transform);
            var found = !string.IsNullOrEmpty(path) && _p2PianoCloneRoot != null ? _p2PianoCloneRoot.Find(path) : null;
            return found != null ? found.GetComponent<T>() : null;
        }

        static void RestoreP2PianoUiFields()
        {
            if (_p2PianoOriginalCanvas != null) _p2PianoOriginalCanvas.enabled = _p2PianoOriginalCanvasEnabled;
            var ui = FindScenePianoUiLoose();
            if (ui != null)
                s_pianoUiSingleton?.SetValue(null, ui);
            _p2PianoOriginalCanvas = null;
            _p2PianoOriginalCanvasEnabled = false;
            _p2PianoOriginalImage = null;
            _p2PianoOriginalAPressed = null;
            _p2PianoOriginalLTPressed = null;
            _p2PianoOriginalRTPressed = null;
            _p2PianoCloneCanvas = null;
            _p2PianoCloneRoot = null;
            _p2PianoCloneImage = null;
            _p2PianoCloneAPressed = null;
            _p2PianoCloneLTPressed = null;
            _p2PianoCloneRTPressed = null;
            _p2PianoNotes = null;
            _p2PianoDefaultSprite = null;
        }

        static void SetP2PianoNote(int index)
        {
            if (_p2PianoCloneImage == null || _p2PianoNotes == null || _p2PianoNoteSpriteField == null) return;
            for (int i = 0; i < _p2PianoNotes.Length; i++)
            {
                var note = _p2PianoNotes.GetValue(i);
                if (note == null) continue;
                int noteIndex = _p2PianoNoteIndexField != null ? (int)_p2PianoNoteIndexField.GetValue(note) : i;
                if (noteIndex != index) continue;
                var sprite = _p2PianoNoteSpriteField.GetValue(note) as Sprite;
                if (sprite != null) _p2PianoCloneImage.sprite = sprite;
                return;
            }
        }

        static void ResetP2PianoNote()
        {
            if (_p2PianoCloneImage != null && _p2PianoDefaultSprite != null)
                _p2PianoCloneImage.sprite = _p2PianoDefaultSprite;
        }

        static void SetP2PianoButtonScales(bool a, bool lt, bool rt)
        {
            SetP2PianoButtonScale(_p2PianoCloneAPressed, a);
            SetP2PianoButtonScale(_p2PianoCloneLTPressed, lt);
            SetP2PianoButtonScale(_p2PianoCloneRTPressed, rt);
        }

        static void SetP2PianoButtonScale(RectTransform target, bool pressed)
        {
            if (target != null)
                target.localScale = pressed ? new Vector3(0.75f, 0.75f, 1f) : Vector3.one;
        }

        static void PositionP2PianoUi()
        {
            if (_p2PianoCloneRoot == null) return;
            _p2PianoCloneRoot.anchoredPosition = P2PianoCenterLocal();
            _p2PianoCloneRoot.localRotation = Quaternion.identity;
            _p2PianoCloneRoot.localScale = Vector3.one;
        }

        static void RestoreP2AfterInstrumentExit()
        {
            if (player2 == null) return;
            if (player2.PlayerScript != null)
                player2.PlayerScript.SetMouseLookScripts(false);
            if (player2.PlayerNetworkManager != null)
                player2.PlayerNetworkManager.IsAttached = false;
            if (player2.PersonController != null)
            {
                player2.PersonController.enabled = true;
                player2.PersonController.IsMovementFree = true;
                if (player2.PersonController.controller != null)
                    player2.PersonController.controller.enabled = true;
            }
            // 进琴时不再改视角,故这里也无需还原(见 EnsureP2PianoUi 处注释)。
            Helper.SetCursorVisibleAndLockState(false, CursorLockMode.Locked);
            RestoreGlobalInputAfterP2Piano();
        }

        static string ActionInfo(UnityEngine.InputSystem.InputAction action)
        {
            if (action == null) return "null";
            Vector2 v = Vector2.zero;
            try
            {
                if (action.expectedControlType == "Vector2")
                    v = action.ReadValue<Vector2>();
            }
            catch (System.Exception e) { LogV("[P2Piano] action info read ignored: " + e.Message); }
            return action.name + "(enabled=" + action.enabled + ",phase=" + action.phase + ",pressed=" + action.IsPressed() + ",v=" + v + ")";
        }

        static string CanvasInfo(Canvas canvas)
        {
            if (canvas == null) return "null";
            return SafeName(canvas) + "(active=" + canvas.gameObject.activeSelf +
                   ",hier=" + canvas.gameObject.activeInHierarchy +
                   ",enabled=" + canvas.enabled +
                   ",mode=" + canvas.renderMode +
                   ",cam=" + SafeName(canvas.worldCamera) +
                   ",sorting=" + canvas.sortingOrder + ")";
        }

        static string RectInfo(RectTransform rt)
        {
            if (rt == null) return "null";
            return SafeName(rt) + "(pos=" + rt.anchoredPosition + ",size=" + rt.rect.size + ",scale=" + rt.localScale + ")";
        }

        static string SafeName(Object obj)
        {
            return obj != null ? obj.name : "null";
        }

        static Vector2 P2PianoCenterLocal()
        {
            var parent = _p2HudCanvas != null ? _p2HudCanvas.transform as RectTransform : null;
            if (parent == null) return Vector2.zero;

            Vector2 screenCenter = new Vector2(Screen.width * 0.75f, Screen.height * 0.5f);
            if (_p2UiCamera != null)
                screenCenter = _p2UiCamera.pixelRect.center;

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenCenter, _p2UiCamera, out var local))
                return local;

            var rect = parent.rect;
            return new Vector2(rect.width * 0.25f, 0f);
        }

        static void BindP2PianoCanvases(RectTransform root)
        {
            if (root == null) return;
            foreach (var c in root.GetComponentsInChildren<Canvas>(true))
            {
                if (c == null) continue;
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = _p2UiCamera;
                c.planeDistance = 1f;
            }
        }

        internal static bool SuppressVanillaP2PianoMenu(MenuType menuType)
        {
            return menuType == MenuType.Piano && IsP2OriginalInputActive;
        }

        static bool P1AttachedForPianoRestore()
        {
            return player1 != null && player1.PlayerNetworkManager != null && player1.PlayerNetworkManager.IsAttached;
        }

        static void ResolveP2InstrumentKeyAccess(Instrument instrument)
        {
            if (_p2PianoOnPressed != null) return;
            var keys = s_instrumentKeys?.GetValue(instrument) as IList;
            var keyType = keys != null && keys.Count > 0 && keys[0] != null ? keys[0].GetType() : null;
            if (keyType == null) return;
            var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _p2PianoKeyIndexField = keyType.GetField("keyIndex", bf);
            _p2PianoMinAngleField = keyType.GetField("minAngle", bf);
            _p2PianoMaxAngleField = keyType.GetField("maxAngle", bf);
            _p2PianoOnPressed = keyType.GetMethod("OnPressed", bf);
            _p2PianoOnReleased = keyType.GetMethod("OnReleased", bf);
        }

        static int P2PianoKeyIndex(object key)
        {
            if (key == null) return -1;
            if (_p2PianoKeyIndexCache.TryGetValue(key, out var idx)) return idx;
            idx = _p2PianoKeyIndexField != null ? (int)_p2PianoKeyIndexField.GetValue(key) : -1;
            _p2PianoKeyIndexCache[key] = idx;
            return idx;
        }
    }
}
