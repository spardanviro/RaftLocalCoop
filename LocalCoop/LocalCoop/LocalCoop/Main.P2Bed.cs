using UnityEngine;
using UnityEngine.UI;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P2 起床 + 跳过黑夜黑屏 —— 修复"P2 能按 X 躺下但起不来""跳夜 P2 无黑屏"。
        //
        //  躺下：走通用设备交互(InteractionRouter.RunDeviceRayAsP2 把 Bed.localPlayer 临时
        //   覆盖成 P2 + 强制本地)→ Bed.OnIsRayed→HandleAwake→P2.BedComponent.Sleep→AttachPlayer(P2)。
        //   于是 P2.BedComponent.Sleeping=true、CurrentBed 已设。
        //
        //  起床(原版)：Bed.HandleAsleep 由 BedComponent.Update 每帧调用,但被 playerNetwork.IsLocalPlayer
        //   门控 → P2 克隆(IsLocalPlayer=false)永不触发；内部还读 PlayerInput[0](=P1) 的 Interact、
        //   用 localPlayer(=P1) 发 RPC,对 P2 完全不适用。
        //   做法：P2 睡觉时每帧在准心上方显示「X 站起」提示 + 监听 P2 自己的 Interact,按下 →
        //    在 P2OriginalScope.Interaction() 内调 P2.BedComponent.StopSleep()(→ Bed.DettachPlayer)。
        //    scope 强制 P2 isLocalPlayer=true,使 DettachPlayer 的 IsLocalPlayer 块以 P2 身份执行
        //    (归位到 RespawnPoint、恢复 P2 控制/视角),与躺下时 AttachPlayer 对称。host(P1)本机直跑。
        //
        //  跳夜黑屏(原版)：BedManager.Slumber 协程把 GameManager.sleepFadePanel(全屏 CanvasGroup)淡到 1
        //   再淡回 0 —— 它只在 P1 视图。P2 半屏(_p2HudCanvas)看不到 → 缺黑屏。
        //   做法：每帧读 sleepFadePanel.canvasGroup.alpha 镜像到 P2 半屏的全屏黑色遮罩 → 与 P1 完全同步。
        // ══════════════════════════════════════════════════════════════════

        static bool _p2BedWasSleeping;

        internal static void ForceP2SleepVisualState()
        {
            if (player2 == null) return;
            RestoreP2WorldVisualState();
            SetP2BodyVisible(true);
            SetP2ToolLayer(-1);
            if (player2.HandCamera != null)
                player2.HandCamera.enabled = false;
            HideP2HeldModels();
            _p2BedWasSleeping = true;
        }

        // 睡觉中 → 准心上方「X 站起」提示 + 监听起身。返回 true 表示 P2 正在睡觉(调用方暂停其余交互)。
        internal static bool TickP2Bed()
        {
            if (player2 == null) return false;
            var bc = player2.BedComponent;
            bool sleeping = bc != null && bc.Sleeping && bc.CurrentBed != null;

            // 进/出睡眠边沿：进入→隐藏 P2 手持模型(vanilla SelectUsable(null) 隐的是 PlayerItemManager,
            //  动不到 P2 由 _p2UseItemController 管理的持握模型,故手动清);退出→重装手持 + 重置 FP 朝向(防跳变)。
            if (sleeping != _p2BedWasSleeping)
            {
                if (sleeping) HideP2HeldModels();
                else { RefreshP2HeldItem(); P2CameraController.InitFromCurrentCamera(); }
                _p2BedWasSleeping = sleeping;
            }

            SplitScreenDeathFlow.ReleaseP2BedRespawnWaitIfBedGone(sleeping);
            if (!sleeping) return false;

            UpdateP2SleepingCamera();

            // 集体快进睡眠(夜晚全员入睡)由 BedManager 统一唤醒,P2 不手动起、也不显示站起提示(黑屏镜像另行处理)。
            if (BedManager.Slumbering) { ClearP2InteractPrompt(); return true; }

            bool waitingRespawn = player2.PlayerScript != null && player2.PlayerScript.waitingForRespawn;
            if (waitingRespawn)
                SplitScreenDeathFlow.TickP2BedRespawnWaitLock();
            SetP2InteractPrompt("Cancel", Helper.GetTerm("Game/StopSleep"));

            var cancel = SplitScreenRuntime.Instance?.P2?.ActionCancel;
            if (cancel != null && cancel.WasPressedThisFrame())
            {
                SuppressP2CrouchOnExit();
                ClearP2InteractPrompt();
                var bed = bc.CurrentBed;
                if (waitingRespawn)
                {
                    P2FurnitureAdapter.CompleteBedRespawn(player2.PlayerScript);
                    RestoreP2WorldVisualState();
                    RefreshP2HeldItem();
                    _p2RefreshHeldNextFrame = true;
                    _p2HeldWarmup = 1; _p2HeldWarmupTime = Time.time + 0.3f;
                    return true;
                }

                P2FurnitureAdapter.StopSleeping(bc);
                if (player1 != null && player1.BedComponent != null && player1.BedComponent.Sleeping
                    && player1.PlayerItemManager != null)
                    player1.PlayerItemManager.SelectUsable(null);

                if (player2.PlayerScript != null)
                {
                    SplitScreenDeathFlow.ForceP2CollisionState(player2.PlayerScript, bed, snapToRespawn: false);
                }
                RestoreP2WorldVisualState();
                RefreshP2HeldItem();
                _p2RefreshHeldNextFrame = true;
                _p2HeldWarmup = 1; _p2HeldWarmupTime = Time.time + 0.3f;
            }
            return true;
        }

        static void UpdateP2SleepingCamera()
        {
            if (player2 == null || player2.Camera == null) return;

            if (!player2.Camera.enabled)
                player2.Camera.enabled = true;
            if (player2.HandCamera != null && player2.HandCamera.enabled)
                player2.HandCamera.enabled = false;
            if (player2.PlayerScript != null)
                player2.PlayerScript.SetMouseLookScripts(false);

            EnsureP2CameraRig();
            if (p2FirstPerson)
            {
                P2CameraController.ApplyFirstPersonView(player2);
                return;
            }

            var tp = player2.currentModel != null
                ? player2.currentModel.thirdPersonSettings
                : player2.GetComponentInChildren<ThirdPerson>();
            if (tp != null)
            {
                EnsureP2ThirdPerson(tp);
                P2ThirdPersonCameraController.Handle(tp, player2);
            }
        }

        
        internal static void HideP2HeldModels()
        {
            if (_p2UseItemController == null) return;
            try
            {
                _p2UseItemController.Deselect();
                foreach (var c in _p2UseItemController.allConnections)
                    if (c != null && c.obj != null) c.SetActive(false);
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[P2Held] hide held ex: " + e.Message); }
            HideP2HeldPlastic();   
        }

        // 每帧(无条件)镜像 P1 跳夜黑屏到 P2 半屏 —— alpha 跟随 sleepFadePanel,完全同步。
        static CanvasGroup _p2SleepOverlay;
        internal static void TickP2SleepFade()
        {
            if (player2 == null || _p2HudCanvas == null) return;
            bool p2Sleeping = player2.BedComponent != null && player2.BedComponent.Sleeping && player2.BedComponent.CurrentBed != null;
            bool p2ShouldMirrorFade = p2Sleeping || BedManager.Slumbering;
            if (!p2ShouldMirrorFade)
            {
                if (_p2SleepOverlay != null && _p2SleepOverlay.alpha != 0f) _p2SleepOverlay.alpha = 0f;
                return;
            }

            var gm = SingletonGeneric<GameManager>.Singleton;
            var fp = gm != null ? gm.sleepFadePanel : null;
            float a = (fp != null && fp.canvasGroup != null) ? fp.canvasGroup.alpha : 0f;
            if (a <= 0f)
            {
                if (_p2SleepOverlay != null && _p2SleepOverlay.alpha != 0f) _p2SleepOverlay.alpha = 0f;
                return;
            }
            EnsureP2SleepOverlay();
            if (_p2SleepOverlay != null) _p2SleepOverlay.alpha = a;
        }

        static void EnsureP2SleepOverlay()
        {
            if (_p2SleepOverlay != null || _p2HudCanvas == null) return;
            var go = new GameObject("P2_SleepFade", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(_p2HudCanvas.transform, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var img = go.GetComponent<Image>(); img.color = Color.black; img.raycastTarget = false;
            var cv = go.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 30000;   // 盖住一切(准心/提示)
            _p2SleepOverlay = go.GetComponent<CanvasGroup>(); _p2SleepOverlay.alpha = 0f; _p2SleepOverlay.blocksRaycasts = false;
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) go.layer = uiLayer;
        }
    }
}
