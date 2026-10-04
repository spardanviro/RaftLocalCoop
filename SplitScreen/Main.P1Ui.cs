using Object = UnityEngine.Object;
using UnityEngine;
using UnityEngine.UI;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P1 UI 搬到左半屏 —— 专用 UI 相机 + CanvasScaler 自适应
        //
        //  - 专用 UI 相机：rect=左半屏，clearFlags=Depth（叠加在主相机画面上、
        //    最后渲染），cullingMask=仅 UI 层 → UI 永远在最上，不被 3D 穿插。
        //  - 把所有 ScreenSpaceOverlay 的根 Canvas 改成 ScreenSpaceCamera 绑这个相机
        //    → 整套 UI（HUD/背包/暂停/死亡/聊天等）渲染进左半屏。
        //  - CanvasScaler(ScaleWithScreenSize, match=0 按宽) 按左半屏像素自适应、不变形。
        //  - 跳过 WorldSpace（笔记本，自带相机）和 Graphy 调试图表。
        // ══════════════════════════════════════════════════════════════════
        static bool _p1UiMoved;
        static Camera _p1UiCamera;

        internal static void MoveP1UiToLeftHalf()
        {
            if (_p1UiMoved) return;
            if (player1?.Camera == null) return;

            if (_p1UiCamera == null)
                _p1UiCamera = new GameObject("P1_UICamera").AddComponent<Camera>();
            _p1UiCamera.clearFlags    = CameraClearFlags.Depth;     // 叠加，不清色
            _p1UiCamera.cullingMask   = 1 << 5;                     // 仅 UI 层
            ApplySplitViewport(_p1UiCamera, false); 
            _p1UiCamera.depth         = player1.Camera.depth + 10f; // 最后渲染（最上层）
            _p1UiCamera.nearClipPlane = 0.1f;
            _p1UiCamera.farClipPlane  = 20f;

            int moved = 0;
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (!canvas.isRootCanvas) continue;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue; // 跳过 WorldSpace(笔记本)
                if (canvas.name.Contains("Graphy")) continue;
                if (canvas.name.Contains("P2_")) continue;

                canvas.renderMode    = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera   = _p1UiCamera;
                canvas.planeDistance = 1f;

                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null)
                {
                    scaler.uiScaleMode        = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    scaler.screenMatchMode    = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                    scaler.matchWidthOrHeight = 0f;
                    // 交易面板 Panel 是纵向 stretch 锚点(aMin.y=0,aMax.y=1),match=0(按宽)在半宽全高视口
                    // 会把它拉成竖长条。改成按【设计尺寸】固定居中(尺寸由锚点+参考分辨率算出,不写死)→
                    // 在 match=0 下等比缩放、自适应半屏分辨率、不拉伸。与 P2 面板一致。
                    if (canvas.name.Contains("TradingPost"))
                    {
                        var tpPanel = canvas.transform.Find("Panel") as RectTransform;
                        if (tpPanel != null)
                            FitTradingPanelDesignSize(tpPanel, scaler.referenceResolution.x, scaler.referenceResolution.y);
                    }
                }
                moved++;
            }

            _p1UiMoved = true;
            LogV($"[P1UI] {moved} 个 Overlay Canvas -> left half (P1_UICamera, scaler.match=0)");
        }

        internal static void ResetP1Ui() => _p1UiMoved = false;

        // ── P2 开背包时把主 UI 画布临时切回全屏 Overlay ───────────────────
        //  原版手柄虚拟光标 / 拖拽图标 / UI 射线全部假定 ScreenSpaceOverlay(全屏像素坐标)。
        //  分屏把 _CanvasGame_New 改成了 ScreenSpaceCamera(左半屏) → 光标/拖拽坐标全乱。
        //  P2 开背包期间临时切回 Overlay → 光标/拖拽/点击全部按原版正常工作；关闭后还原左半屏相机。
        //  (P2 菜单期间 P1 已被切到 UI 动作图、无法游戏，全屏背包可接受。)
        static Canvas _gameCanvas;
        static RenderMode _gameCanvasModeSaved;
        static Camera _gameCanvasCamSaved;
        static bool _gameCanvasOverlaid;

        internal static void SetGameCanvasOverlay(bool overlay)
        {
            var ch = ComponentManager<CanvasHelper>.Value;
            var cv = ch != null ? ch.GetComponent<Canvas>() : null;
            if (cv == null) return;

            if (overlay)
            {
                if (_gameCanvasOverlaid) return;
                _gameCanvas = cv;
                _gameCanvasModeSaved = cv.renderMode;
                _gameCanvasCamSaved  = cv.worldCamera;
                cv.renderMode = RenderMode.ScreenSpaceOverlay;
                _gameCanvasOverlaid = true;
                LogV("[P2Canvas] main UI canvas -> fullscreen overlay for vanilla cursor/drag");
            }
            else
            {
                if (!_gameCanvasOverlaid || _gameCanvas == null) return;
                _gameCanvas.renderMode = _gameCanvasModeSaved;
                if (_gameCanvasModeSaved == RenderMode.ScreenSpaceCamera)
                {
                    _gameCanvas.worldCamera   = _gameCanvasCamSaved != null ? _gameCanvasCamSaved : _p1UiCamera;
                    _gameCanvas.planeDistance = 1f;
                }
                _gameCanvasOverlaid = false;
                LogV("[P2Canvas] main UI canvas -> restored left-half camera");
            }
        }

        
        
        
        
        
        const float NotebookCameraFov = 70f;
        const float NotebookMainFov = 50f;
        const float NotebookOpenPitch = -10f;
        static System.Reflection.FieldInfo _nbEnhOffsetField;
        static System.Reflection.FieldInfo _nbEnhStaticField;
        static bool _nbEnhOffsetFixed;
        static Quaternion _restHolderLocal = Quaternion.identity;
        static bool _restHolderLocalSet;


        internal static void EnforceP1CameraPitch()
        {
            var p1 = player1;
            if (p1 == null || p1.currentModel == null) return;
            var holder = FirstPersonVisualRig.GetCameraHolder(p1) ?? p1.currentModel.cameraHolder;
            if (holder == null) return;

            if (p1.ZiplinePlayer != null && p1.ZiplinePlayer.IsAttachedToZipline)
            {
                return;
            }

            if (p1.PlayerScript != null && p1.PlayerScript.IsDead)
                return;

            // 睡觉中：用头骨(cameraHolder)的位置+朝向 = 躺姿视角(随全身躺动画),与 P2 同款。
            //  否则下面 mouseLook 覆盖会把视角冻结在入睡前朝向(常朝脚) → 视角与头部不一致。
            if (p1.BedComponent != null && p1.BedComponent.Sleeping && p1.Camera != null)
            {
                var tp = player1ThirdPerson != null ? player1ThirdPerson : p1.GetComponentInChildren<ThirdPerson>();
                bool tpModel = tp != null && tp.ThirdPersonModel; bool tpState = tp != null && tp.ThirdPersonState;
                if (!tpState)
                {
                    if (p1.playerPivot != null) p1.playerPivot.localEulerAngles = Vector3.zero;
                    var p2Holder = (player2 != null && player2.currentModel != null) ? player2.currentModel.cameraHolder : null;
                    if (p2Holder != null) { _restHolderLocal = p2Holder.localRotation; _restHolderLocalSet = true; }
                    if (_restHolderLocalSet) holder.localRotation = _restHolderLocal;
                    p1.Camera.transform.position = holder.position;
                    p1.Camera.transform.rotation = holder.rotation;
                                }
                return;
            }

            // 起身:还原 mouseLook(坐椅子时被锁过)。即使座椅 disableMouseLook=false、StopCarrying 不还原,也由此恢复。
            if (!P1Attached && _p1SeatLookLocked && p1.PlayerScript != null)
            { p1.PlayerScript.SetMouseLookScripts(true); _p1SeatLookLocked = false; }

            var nb = p1.NoteBookUI;
            if (nb != null && nb.isDisplayed)
            {
                var notebookPlayerScript = p1.PlayerScript;
                if (notebookPlayerScript != null && notebookPlayerScript.mouseLookYScript != null)
                {
                    notebookPlayerScript.mouseLookYScript.transform.localRotation = Quaternion.Euler(NotebookOpenPitch, 0f, 0f);
                    holder.rotation = notebookPlayerScript.mouseLookYScript.transform.rotation;
                }
                if (p1.Animator != null && p1.Animator.anim != null && !_p1AnimFrozen)
                {
                    if (_nbOpenSince <= 0f) _nbOpenSince = Time.unscaledTime;
                    if (Time.unscaledTime - _nbOpenSince > 1.1f)
                    {
                        _p1AnimSpeedSaved = p1.Animator.anim.speed;
                        p1.Animator.anim.speed = 0f;
                        _p1AnimFrozen = true;
                    }
                }
                if (_nbEnhStaticField == null)
                    _nbEnhStaticField = typeof(NoteBookNote_Enhancer).GetField("enhancePoint",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (_nbEnhStaticField != null && nb.enhancePoint != null)
                    _nbEnhStaticField.SetValue(null, nb.enhancePoint);

                if (!_nbEnhOffsetFixed && _nbEnhOffsetField == null)
                    _nbEnhOffsetField = typeof(NoteBookNote_Enhancer).GetField("enhanceOffset",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (!_nbEnhOffsetFixed && _nbEnhOffsetField != null)
                {
                    foreach (var e in nb.GetComponentsInChildren<NoteBookNote_Enhancer>(true))
                        _nbEnhOffsetField.SetValue(e, Vector3.zero);
                    _nbEnhOffsetFixed = true;
                }
                return;
            }

            // 坐椅子中:相机锁死(放头部 + 朝座椅前方),不随 mouseLook 转 —— 对齐 P2 坐姿固定视角。
            //  进入边沿关 mouseLook(防 root 偏航漂移 + 起身跳变);朝向用座椅挂点 forward(随木筏转,与 P2 视野里
            //  被钉向座椅前方的身体一致)。P1 自己看不到身体,此处只锁相机。
            if (P1Attached && p1.Camera != null)
            {
                var tp = player1ThirdPerson != null ? player1ThirdPerson : p1.GetComponentInChildren<ThirdPerson>();
                // P1 处于 TP 时一律让开,交回 vanilla ThirdPerson.HandleThirdPerson 驱动轨道相机
                // (相机锚在 playerNetwork.transform,人被 AttachPlayer 挂在座位/车上 → 天然跟随,
                //  localCameraRotation.y=0 即落在正后方)。
                // 原先只放行"自由视角座位",导致 P1 在雪橇车这类普通座位上即使切到 TP,
                // 相机仍被下面那段每帧钉回 FP 座位朝向 = 看起来根本没进 TP。
                if (tp != null && tp.ThirdPersonState)
                {
                    // 进 TP 时 vanilla 自己会把 mouseLook 打开。标志不跟着清,切回 FP 后
                    // 下面那段边沿触发的关闭就永远不再执行 -> 鼠标转身体、相机却被钉死 ->
                    // 两者拉锯 = 手臂晃动 + 海面渲染被打乱(正是本文件上方注释描述的老毛病)。
                    _p1SeatLookLocked = false;
                    return;
                }

                // 原版按【每个座位】的 disableMouseLook 决定就座时能否转视角(AttachPlayer.StartCarryingPlayer:
                // true -> SetMouseLookScripts(false);false -> mouseLookX.isChild=true,相对载具自由环视)。
                // 雪橇车三个座位实测都是 false = 原版本就允许环视,下面这段接管纯属污染:
                // 每帧 SetMouseLookScripts(false) + 把相机位姿钉成座位朝向,与 mouseLook 的写回互相拉锯
                // (实测就座时 rootEuler/pivotEuler 仍在被 mouseLook 更新)-> 胳膊抖动;
                // 且在正常渲染流程之外硬改相机位姿,会打乱依赖相机的水面渲染。
                // 这类座位一律让开,交回 vanilla 的 FP 视角装配(即下方常规 FP 分支)。
                // 椅子/床等 disableMouseLook=true 的座位仍走原有接管,行为完全不变。
                // disableMouseLook=false 的座位(雪橇车三座、椅子/沙发/浴缸)原版都允许相对座位
                // 自由环视,一律让开、交回 vanilla 的 FP 视角装配。椅子那条既有链路里两处专门
                // 关视角的地方已同步改成只在 TP 生效(见 Patches.Rendering / Patches.SeatPose)。
                //
                // 唯一例外:雪橇车【驾驶位】。
                // ⚠ 有意偏离原版(用户明确要求,两次提出):驾驶位实测同样是 disableMouseLook=false,
                // 原版开车时本可自由环视,但用户要求驾驶时视角锁定车头。故驾驶者走下面的原有接管。
                var seatAttach = FindAttachCarrying(p1);
                if (seatAttach != null && !seatAttach.disableMouseLook && !IsSnowmobileDriverSeat(seatAttach, p1))
                {
                    if (_p1SeatLookLocked && p1.PlayerScript != null)
                    { p1.PlayerScript.SetMouseLookScripts(true); _p1SeatLookLocked = false; }
                }
                else
                {
                // 每帧重申而非边沿触发:vanilla 在 TP<->FP 切换等时机会自行打开 mouseLook,
                // 边沿触发一旦错过就永久失守。SetMouseLookScripts 只是改组件 enabled,重复调用无代价。
                if (p1.PlayerScript != null)
                { p1.PlayerScript.SetMouseLookScripts(false); _p1SeatLookLocked = true; }
                var seat = p1.transform.parent;
                // 眼位按【FP 锚点 + 站姿眼位】算,而不是直接用 holder。
                // 自视图 rig 的 FullBodyIndex 已归零(站姿),而身体根被钉在 TP 锚点上,
                // 直接取 holder 就是"站姿眼高配 TP 锚点"= 偏高(驾驶位看不到仪表盘)。
                Vector3 seatedEye;
                p1.Camera.transform.position =
                    FirstPersonVisualRig.TryGetSeatedEyePosition(p1, seatAttach, out seatedEye)
                        ? seatedEye
                        : holder.position;
                p1.Camera.transform.rotation = (seat != null)
                    ? Quaternion.LookRotation(seat.forward, Vector3.up)
                    : holder.rotation;
                return;
                }
            }

            var p1ThirdPerson = player1ThirdPerson != null ? player1ThirdPerson : p1.GetComponentInChildren<ThirdPerson>();
            if (p1ThirdPerson != null && p1ThirdPerson.ThirdPersonState)
                return;

            var ps = p1.PlayerScript;
            if (ps != null && ps.mouseLookYScript != null)
            {
                holder.rotation = ps.mouseLookYScript.transform.rotation;

                // In normal first person the original rig keeps the gameplay camera
                // at the CameraHolder origin.  Leaving a local rotation behind from
                // a previous TP/attach state makes the view pitch on a second axis,
                // while the arms and use ray still follow CharacterPivot.
                var cameraTransform = p1.Camera != null ? p1.Camera.transform : null;
                if (cameraTransform != null)
                {
                    if (cameraTransform.parent != holder)
                        cameraTransform.SetParent(holder, false);
                    cameraTransform.localPosition = Vector3.zero;
                    cameraTransform.localRotation = Quaternion.identity;
                }
            }
        }

        static System.Reflection.FieldInfo _nbEvtCanvasField;
        internal static void SetNotebookCanvasEventCamera(Camera cam)
        {
            var nb = player1 != null ? player1.NoteBookUI : null;
            if (nb == null || cam == null) return;
            if (_nbEvtCanvasField == null)
                _nbEvtCanvasField = typeof(NoteBookUI).GetField("noteBookCanvas",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var canvas = _nbEvtCanvasField?.GetValue(nb) as Canvas;
            if (canvas != null && canvas.worldCamera != cam) canvas.worldCamera = cam;
        }

        static float _nbOpenSince;
        static float _p1AnimSpeedSaved = 1f;
        static bool _p1AnimFrozen;

        internal static void UnfreezeP1Animator()
        {
            if (_p1AnimFrozen && player1 != null && player1.Animator != null && player1.Animator.anim != null)
                player1.Animator.anim.speed = _p1AnimSpeedSaved > 0f ? _p1AnimSpeedSaved : 1f;
            _p1AnimFrozen = false;
            _nbOpenSince = 0f;
            _nbEnhOffsetFixed = false;
        }

        internal static void ForceCleanupP1NotebookVisuals(bool restoreController)
        {
            var p1 = player1;
            if (p1 != null && p1.NoteBookUI != null && p1.NoteBookUI.isDisplayed)
                p1.NoteBookUI.SetBookActive(false);
        }



    }
}
