using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P2 望远镜(Binoculars)分屏隔离 —— 完整还原 vanilla 体验(P2 半屏)。
        //
        //  问题:Binoculars : UsableTool,P2 装备后经 P2ToolRunner 在 scope 内跑 vanilla
        //   Binoculars.Update。但其视觉效果全用【共享单例】(=P1 那份):
        //     · canvas.binocularImage(全屏望远镜遮罩)、canvas.genericBlackFade(黑屏渐变)
        //       → 挂在共享 CanvasHelper(P1 主 canvas)→ 显示到 P1 屏幕(串扰);
        //     · settings.controls.*Sensitivity(全局)→ 污染 P1;
        //     · 激活靠 inputUse → CustomInputConfig.Gamepad 判定 P1 控制方案(被锁键鼠)→ 实际读 P1 LMB。
        //   故 deny vanilla 望远镜(P2ToolRunner.s_denyTools),由本状态机用【P2 自己的资源】完整接管。
        //
        //  完整还原(对齐 vanilla Activate/Deactivate 协程,用每帧 lerp 状态机复刻):
        //   按住 RT 举镜 → P2 半屏黑屏淡入 → 全黑时【隐藏 P2 FP 手臂+望远镜模型 + 显示遮罩 + 应用缩放 FOV】
        //   → 淡出;松开反向(2 倍速)。dpad 上/下调倍率。全程不碰共享 canvas/settings → P1 干净。
        //   EnforceP2FpArms 的 P1→P2 FOV 同步在举镜期间避让(P2BinocActive);手臂激活在隐藏期避让(_binocHidesArms)。
        // ══════════════════════════════════════════════════════════════════
        const int PH_OFF = 0, PH_IN_UP = 1, PH_IN_DOWN = 2, PH_ON = 3, PH_OUT_UP = 4, PH_OUT_DOWN = 5;
        const float ZoomStep = 9f;        // gamepad 单次缩放步进(对齐 vanilla inputZoomIn/Out 的 9 度)

        static Binoculars   _p2Binoc;       // 当前 P2 手持的望远镜组件(null=未持握)
        static MeshRenderer _binocModel;    // 望远镜模型(反射 Binoculars.binocularModel),举镜隐藏
        static GameObject   _p2BinocOverlay;
        static CanvasGroup  _p2BinocFade;   // P2 半屏黑屏过渡(镜像 _p2SleepOverlay)
        static int   _binocPhase;           // PH_* 状态机
        static bool  _binocHidesArms;       // 举镜视觉生效期(全黑切换后)→ EnforceP2FpArms 避让手臂激活
        static bool  _p2BinocPrevFP;        // 举镜前 p2FirstPerson 原值(退出还原)
        static float _binocFadeAlpha;
        static float _binocEnterFov;        // 举镜前 P2 相机 FOV(=P1 同步值,退出还原基准)
        static float _binocActiveFov, _binocTargetFov, _binocMinFov, _binocMaxFov, _binocZoomSpeed, _binocFadeSpeed = 1f;

        // P1 望远镜(走 vanilla 原生 Update,本模块只负责"举镜强制 FP"——否则 P1 在 TP 下从拉远相机放大)。
        static bool _p1BinocActive, _p1BinocWasTP;
        internal static bool P1BinocActive => _p1BinocActive;

        // 任意非 Off 状态 → 占用(EnforceP2FpArms 的 P2 主相机 FOV 同步避让,防覆盖缩放)。
        internal static bool P2BinocActive    => _binocPhase != PH_OFF;
        // 举镜视觉生效期 → EnforceP2FpArms 不重新激活 P2 FP 手臂网格(否则盖不住遮罩透明区)。
        internal static bool P2BinocHidesArms => _binocHidesArms;

        // 反射读 Binoculars 私有调参/模型/激活态,还原 vanilla 手感。
        static FieldInfo _fBinActiveFOV, _fBinMinFOV, _fBinZoomSpeed, _fBinFadeSpeed, _fBinModel, _fBinActive, _fBinocActiveObj;

        internal static void ResetP2Binoc()
        {
            ForceOffImmediate();
            _p2Binoc = null; _binocModel = null;
            _p2BinocOverlay = null; _p2BinocFade = null;   // 随 _p2HudCanvas 一起销毁,仅复位引用
            _p1BinocActive = false; _p1BinocWasTP = false;
        }

        // 每帧无条件调用(SplitScreenRuntime.Tick,在睡觉/座椅等提前 return 之前)。
        internal static void TickP2Binoculars()
        {
            if (player2 == null || _p2HudCanvas == null) { if (_binocPhase != PH_OFF) ForceOffImmediate(); _p2Binoc = null; return; }

            // 被其他独占态占用(背包/菜单/建造菜单/座椅/滑索/睡眠)→ 立即收起(不走 fade)。
            bool blocked = IsP2BackpackOpen || IsP2MenuOpen || IsP2BuildMenuOpen
                           || P2IsSeated || P2ZiplineDriver.IsAttached
                           || (player2.BedComponent != null && player2.BedComponent.Sleeping);

            var bino = blocked ? null : ResolveP2Binoculars();
            if (bino == null) { if (_binocPhase != PH_OFF) ForceOffImmediate(); _p2Binoc = null; return; }
            _p2Binoc = bino;

            bool want = p2ActionFire != null && p2ActionFire.IsPressed();   // 按住 RT 举镜(对齐 vanilla 按住 inputUse)
            float dt = Time.deltaTime;
            float sp = Mathf.Max(_binocFadeSpeed, 0.5f);

            switch (_binocPhase)
            {
                case PH_OFF:
                    if (want) { EnterBegin(bino); _binocPhase = PH_IN_UP; }
                    break;
                case PH_IN_UP:                                   
                    _binocFadeAlpha = Mathf.MoveTowards(_binocFadeAlpha, 1f, dt * sp); SetFade(_binocFadeAlpha);
                    if (_binocFadeAlpha >= 1f) { ApplyBinocVisual(true); _binocPhase = PH_IN_DOWN; }
                    break;
                case PH_IN_DOWN:                                 
                    _binocFadeAlpha = Mathf.MoveTowards(_binocFadeAlpha, 0f, dt * sp); SetFade(_binocFadeAlpha);
                    if (_binocFadeAlpha <= 0f) _binocPhase = PH_ON;
                    break;
                case PH_ON:                                      
                    DriveP2BinocZoom();
                    if (!want) _binocPhase = PH_OUT_UP;
                    break;
                case PH_OUT_UP:                                  
                    _binocFadeAlpha = Mathf.MoveTowards(_binocFadeAlpha, 1f, dt * sp * 2f); SetFade(_binocFadeAlpha);
                    if (_binocFadeAlpha >= 1f) { ApplyBinocVisual(false); _binocPhase = PH_OUT_DOWN; }
                    break;
                case PH_OUT_DOWN:                                
                    _binocFadeAlpha = Mathf.MoveTowards(_binocFadeAlpha, 0f, dt * sp * 2f); SetFade(_binocFadeAlpha);
                    if (_binocFadeAlpha <= 0f) _binocPhase = PH_OFF;
                    break;
            }
        }

        // 全黑瞬间切换:state=true 进入举镜视觉(隐藏手臂/模型+显示遮罩+应用 FOV);false 反之。
        static void ApplyBinocVisual(bool state)
        {
            if (state)
            {
                EnsureP2BinocOverlay();
                if (_p2BinocOverlay != null) _p2BinocOverlay.SetActive(true);
                _binocHidesArms = true;
                p2FirstPerson = true;          // 全黑期再确认 FP(防 View 键中途翻转)
                HideP2Arms(true);
                if (player2 != null && player2.Camera != null) player2.Camera.fieldOfView = _binocActiveFov;
            }
            else
            {
                if (_p2BinocOverlay != null) _p2BinocOverlay.SetActive(false);
                _binocHidesArms = false;
                HideP2Arms(false);   // 手臂网格由 EnforceP2FpArms 接管重新激活;望远镜模型显式还原
                if (player2 != null && player2.Camera != null && _binocEnterFov > 0f)
                    player2.Camera.fieldOfView = _binocEnterFov;
                p2FirstPerson = _p2BinocPrevFP;   // 还原举镜前视角(TP/FP)
            }
        }

        
        internal static void ForceP2BinocOff() { ForceOffImmediate(); }

        static void ForceOffImmediate()
        {
            if (_binocPhase != PH_OFF) ApplyBinocVisual(false);   // 幂等还原遮罩/手臂/FOV/视角
            _binocHidesArms = false;
            _binocFadeAlpha = 0f; SetFade(0f);
            _binocPhase = PH_OFF;
        }

        static void DriveP2BinocZoom()
        {
            if (player2 == null || player2.Camera == null) return;
            p2FirstPerson = true;   // 举镜锁第一人称(否则从拉远的 TP 相机放大)
            var gp = GetP2Gamepad();
            if (gp != null)
            {
                if (gp.dpad.up.wasPressedThisFrame)        _binocTargetFov -= ZoomStep;  // 拉近
                else if (gp.dpad.down.wasPressedThisFrame) _binocTargetFov += ZoomStep;  // 拉远
            }
            _binocTargetFov = Mathf.Clamp(_binocTargetFov, _binocMinFov, _binocMaxFov);
            _binocActiveFov = Mathf.Lerp(_binocActiveFov, _binocTargetFov, Time.deltaTime * Mathf.Max(_binocZoomSpeed, 1f));
            player2.Camera.fieldOfView = _binocActiveFov;
        }

        // 举镜前置:反射读调参/模型,算 FOV 区间(对齐 vanilla:maxFOV=clamp(defaultFOV-10,minFOV,defaultFOV))。
        static void EnterBegin(Binoculars bino)
        {
            if (player2 == null || player2.Camera == null) return;
            EnsureBinocFields();
            EnsureP2BinocOverlay();
            _binocModel = _fBinModel?.GetValue(bino) as MeshRenderer;
            _p2BinocPrevFP = p2FirstPerson; p2FirstPerson = true;   // 锁第一人称(黑屏期切换,退出还原)
            _binocEnterFov  = player2.Camera.fieldOfView;
            float activeFov = _fBinActiveFOV != null ? (float)_fBinActiveFOV.GetValue(bino) : 30f;
            _binocMinFov    = _fBinMinFOV    != null ? (float)_fBinMinFOV.GetValue(bino)    : 10f;
            _binocZoomSpeed = _fBinZoomSpeed != null ? (float)_fBinZoomSpeed.GetValue(bino) : 5f;
            _binocFadeSpeed = _fBinFadeSpeed != null ? (float)_fBinFadeSpeed.GetValue(bino) : 1f;
            _binocMaxFov    = Mathf.Clamp(_binocEnterFov - 10f, _binocMinFov, _binocEnterFov);
            _binocActiveFov = Mathf.Clamp(activeFov, _binocMinFov, _binocMaxFov);
            _binocTargetFov = _binocActiveFov;
        }

        // 隐藏/还原 P2 第一人称手臂网格 + 望远镜模型(对齐 vanilla SetRenderState)。
        static void HideP2Arms(bool hide)
        {
            if (player2ArmMesh != null && player2ArmMesh.gameObject.activeSelf == hide)
                player2ArmMesh.gameObject.SetActive(!hide);
            if (_binocModel != null) _binocModel.enabled = !hide;
        }

        static Binoculars ResolveP2Binoculars() => ResolveHeldBinoculars(player2);

        // 解析某玩家当前手持工具对象上的 Binoculars 组件(无则 null)。
        static Binoculars ResolveHeldBinoculars(Network_Player np)
        {
            if (np == null) return null;
            var uic = np.GetComponentInChildren<UseItemController>(true);
            if (uic == null) return null;
            if (_fBinocActiveObj == null)
                _fBinocActiveObj = typeof(UseItemController).GetField("activeObject", BindingFlags.Instance | BindingFlags.NonPublic);
            var conn = _fBinocActiveObj?.GetValue(uic) as ItemConnection;
            var obj = conn?.obj;
            if (obj == null || !obj.activeInHierarchy) return null;
            return obj.GetComponent<Binoculars>() ?? obj.GetComponentInChildren<Binoculars>(true);
        }

        static void EnsureBinocFields()
        {
            if (_fBinActiveFOV != null) return;
            var t = typeof(Binoculars);
            var bf = BindingFlags.Instance | BindingFlags.NonPublic;
            _fBinActiveFOV = t.GetField("activeFOV", bf);
            _fBinMinFOV    = t.GetField("minFOV", bf);
            _fBinZoomSpeed = t.GetField("zoomSpeed", bf);
            _fBinFadeSpeed = t.GetField("fadeSpeed", bf);
            _fBinModel     = t.GetField("binocularModel", bf);
            _fBinActive    = t.GetField("active", bf);
        }

        // ── P1 望远镜:举镜强制 FP ────────────────────────────────────────
        //  P1 走 vanilla 原生 Binoculars.Update(本地玩家,正常跑;只是 mod 允许 P1 处于 TP)。
        //  在 TP 下举镜会从拉远相机放大 → 这里检测 vanilla 的 active 边沿,举镜时强制 FP、收起还原。
        //  (问题2"TP 下残留 FP 手臂"由 EnforceMeshStates 每帧 SetActive(!p1IsTP && !P1BinocActive) 根治。)
        internal static void EnforceP1Binoculars(bool p1IsTP)
        {
            if (player1 == null) return;
            var b = ResolveHeldBinoculars(player1);
            bool active = b != null && ReadBinocActive(b);
            if (active && !_p1BinocActive)
            {
                _p1BinocWasTP = p1IsTP;
                if (p1IsTP) ForceP1ThirdPerson(false);   // 举镜:TP → 强制 FP
            }
            else if (!active && _p1BinocActive)
            {
                if (_p1BinocWasTP) ForceP1ThirdPerson(true);   // 收起:还原 TP
                _p1BinocWasTP = false;
            }
            _p1BinocActive = active;
        }

        static bool ReadBinocActive(Binoculars b)
        {
            EnsureBinocFields();
            return _fBinActive != null && b != null && (bool)_fBinActive.GetValue(b);
        }

        static void ForceP1ThirdPerson(bool toTP)
        {
            var tp = player1 != null ? player1.GetComponentInChildren<ThirdPerson>() : null;
            if (tp != null) tp.ForceThirdPersonState(toTP);   // 完整切换(相机回头骨/orbit + 鼠标 + 模型)
        }

        // P2 半屏望远镜遮罩(克隆 binocularImage,铺满,高 sortingOrder)。
        static void EnsureP2BinocOverlay()
        {
            if (_p2BinocOverlay != null || _p2HudCanvas == null) return;
            var ch = ComponentManager<CanvasHelper>.Value;
            if (ch == null || ch.binocularImage == null) return;

            _p2BinocOverlay = Object.Instantiate(ch.binocularImage, _p2HudCanvas.transform);
            _p2BinocOverlay.name = "P2_BinocularOverlay";
            FillParent(_p2BinocOverlay.GetComponent<RectTransform>());
            var cv = _p2BinocOverlay.GetComponent<Canvas>() ?? _p2BinocOverlay.AddComponent<Canvas>();
            cv.overrideSorting = true; cv.sortingOrder = 25000;   // 盖准心/提示,低于黑屏过渡
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) SetLayerRecursively(_p2BinocOverlay.transform, uiLayer);
            _p2BinocOverlay.SetActive(false);
        }

        // P2 半屏黑屏过渡(镜像 _p2SleepOverlay:全黑 Image + CanvasGroup,sortingOrder 盖住遮罩)。
        static void EnsureP2BinocFade()
        {
            if (_p2BinocFade != null || _p2HudCanvas == null) return;
            var go = new GameObject("P2_BinocularFade", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            FillParent(go.GetComponent<RectTransform>());
            var img = go.GetComponent<Image>(); img.color = Color.black; img.raycastTarget = false;
            var cv = go.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 29000;  
            _p2BinocFade = go.GetComponent<CanvasGroup>(); _p2BinocFade.alpha = 0f; _p2BinocFade.blocksRaycasts = false;
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) go.layer = uiLayer;
        }

        static void SetFade(float a)
        {
            if (a > 0f) EnsureP2BinocFade();
            if (_p2BinocFade != null) _p2BinocFade.alpha = a;
        }

        static void FillParent(RectTransform rt)
        {
            if (rt == null) return;
            if (rt.parent == null && _p2HudCanvas != null) rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        static Gamepad GetP2Gamepad()
        {
            return GetP2BoundGamepad();
        }
    }
}
