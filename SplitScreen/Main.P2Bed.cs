using UnityEngine;
using UnityEngine.UI;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        //
        
        
        
        //
        
        
        
        
        
        
        
        //
        
        
        
        

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

        
        internal static bool TickP2Bed()
        {
            if (player2 == null) return false;
            var bc = player2.BedComponent;
            bool sleeping = bc != null && bc.Sleeping && bc.CurrentBed != null;

            
            
            if (sleeping != _p2BedWasSleeping)
            {
                if (sleeping) HideP2HeldModels();
                else { RefreshP2HeldItem(); P2CameraController.InitFromCurrentCamera(); }
                _p2BedWasSleeping = sleeping;
            }

            if (!sleeping) return false;

            UpdateP2SleepingCamera();

            
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
            var cv = go.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 30000;   
            _p2SleepOverlay = go.GetComponent<CanvasGroup>(); _p2SleepOverlay.alpha = 0f; _p2SleepOverlay.blocksRaycasts = false;
            int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) go.layer = uiLayer;
        }
    }
}
