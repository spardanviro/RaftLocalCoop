using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityModManagerNet;
using HarmonyLib;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    //
    
    
    
    
    public static partial class Main
    {
        
        public static UnityModManager.ModEntry ModEntry;
        public static Harmony                  Harmony;
        static readonly P2UiState              P2Ui = new P2UiState();
        static readonly P2InteractionState     InteractionState = new P2InteractionState();

        

        
        public static Network_Player player1
        {
            get => SplitScreenRuntime.Instance?.P1.Player;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P1.Player = value; }
        }
        public static Network_Player player2
        {
            get => SplitScreenRuntime.Instance?.P2.Player;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.Player = value; }
        }

        
        public static SkinnedMeshRenderer player1ArmMesh
        {
            get => SplitScreenRuntime.Instance?.P1.ArmMesh;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P1.ArmMesh = value; }
        }
        public static SkinnedMeshRenderer player2ArmMesh
        {
            get => SplitScreenRuntime.Instance?.P2.ArmMesh;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.ArmMesh = value; }
        }

        
        public static int p1CamOriginalMask
        {
            get => SplitScreenRuntime.Instance?.P1.CamOriginalMask ?? -1;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P1.CamOriginalMask = value; }
        }
        public static int p2CamOriginalMask
        {
            get => SplitScreenRuntime.Instance?.P2.CamOriginalMask ?? -1;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.CamOriginalMask = value; }
        }

        
        public static bool isSpawningP2
        {
            get => SplitScreenRuntime.Instance?.IsSpawningP2 ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.IsSpawningP2 = value; }
        }

        
        public static bool isProcessingP2Ray
        {
            get => SplitScreenRuntime.Instance?.P2.IsProcessingRay ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.IsProcessingRay = value; }
        }

        
        public static bool p2PersonControllerActive
        {
            get => SplitScreenRuntime.Instance?.P2.IsInPersonController ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.IsInPersonController = value; }
        }

        
        
        public static bool p2UsingItemActive => P2Mode == P2OriginalMode.Tool && IsP2OriginalInputActive;

        
        
        
        internal static bool _p2FillWaterActive => P2Mode == P2OriginalMode.FillWater;

        
        
        internal static P2OriginalMode P2Mode = P2OriginalMode.None;
        internal static bool IsP2OriginalActive => P2Mode != P2OriginalMode.None;
        internal static bool IsP2OriginalInputActive => IsP2OriginalActive && player2 != null && PlayerContext.Active == player2;

        internal static void SuppressP2CrouchOnExit()
        {
            P2Ui.SuppressCrouchOnExit(Time.frameCount);
        }

        static bool P2CrouchExitSuppressed
        {
            get
            {
                bool held = (p2ActionCancel != null && p2ActionCancel.IsPressed())
                         || (p2ActionCrouch != null && p2ActionCrouch.IsPressed());
                return P2Ui.IsCrouchExitSuppressed(Time.frameCount, held);
            }
        }

        internal static bool ShouldBlockP2CrouchInput
        {
            get
            {
                var p2 = player2;
                if (p2 == null) return false;
                if (P2CrouchExitSuppressed) return true;
                if (IsP2BackpackOpen || IsP2BuildMenuOpen || IsP2MenuOpen) return true;
                if (IsP2StorageOpen || IsP2ResearchOpen) return true;
                if (IsP2PianoActive || P2IsSeated || P2IsCarrying || IsP2Steering) return true;
                if (Time.frameCount <= SplitScreenDeathFlow.P2BlockCrouchUntilFrame) return true;
                if (P2ZiplineDriver.IsAttached) return true;
                if (p2.BedComponent != null && p2.BedComponent.Sleeping) return true;
                if (p2.PlayerNetworkManager != null && p2.PlayerNetworkManager.IsAttached) return true;
                if (P2Mode != P2OriginalMode.None && P2Mode != P2OriginalMode.Movement) return true;
                if (p2IsUsingMenu) return true;
                return false;
            }
        }

        internal static WellBeing P1WellBeingFactor = WellBeing.Normal;
        internal static WellBeing P2WellBeingFactor = WellBeing.Normal;

        internal static void UpdateSplitWellBeingFactors()
        {
            P1WellBeingFactor = GetWellBeingForStats(player1 != null ? player1.Stats : null, Stat_WellBeing.Factor);
            P2WellBeingFactor = GetWellBeingForStats(player2 != null ? player2.Stats : null, WellBeing.Normal);
        }

        internal static WellBeing GetWellBeingForStats(PlayerStats stats)
        {
            return GetWellBeingForStats(stats, Stat_WellBeing.Factor);
        }

        static WellBeing GetWellBeingForStats(PlayerStats stats, WellBeing fallback)
        {
            if (GameModeValueManager.IsPlayerInvurnerable) return WellBeing.Good;
            if (stats == null || stats.stat_hunger == null || stats.stat_thirst == null)
                return fallback;

            float hunger = stats.stat_hunger.Normal != null ? stats.stat_hunger.Normal.NormalValue : 1f;
            float thirst = stats.stat_thirst.Normal != null ? stats.stat_thirst.Normal.NormalValue : 1f;
            float value = Mathf.Min(hunger, thirst);

            if (value < Stat_WellBeing.WellBeingLimit) return WellBeing.Bad;
            if (value < 0.5f) return WellBeing.Normal;
            return WellBeing.Good;
        }

        internal static WellBeing GetP2WellBeing()
        {
            UpdateSplitWellBeingFactors();
            return P2WellBeingFactor;
        }

        internal static WellBeing PushP2WellBeing()
        {
            var previous = Stat_WellBeing.Factor;
            UpdateSplitWellBeingFactors();
            Stat_WellBeing.Factor = P2WellBeingFactor;
            return previous;
        }

        internal static void RestoreWellBeing(WellBeing previous)
        {
            Stat_WellBeing.Factor = previous;
        }

        
        
        
        internal static bool GlobalBlocksP2 => CanvasHelper.ActiveMenu == MenuType.Cutscene;
        internal static bool VerboseDiagnostics = false;
        internal static void LogV(string msg) { if (VerboseDiagnostics) ModEntry.Logger.Log(msg); }
        internal static bool P1CarryRayBusyBypass
        {
            get => InteractionState.P1CarryRayBusyBypass;
            set => InteractionState.P1CarryRayBusyBypass = value;
        }
        
        
        
        internal static bool IsP2Steering
        {
            get => InteractionState.IsP2Steering;
            set => InteractionState.IsP2Steering = value;
        }

        
        
        
        
        internal static bool IsP2HoveringPickup
        {
            get => InteractionState.IsP2HoveringPickup;
            set => InteractionState.IsP2HoveringPickup = value;
        }

        
        public static bool p2IsUsingMenu
        {
            get => SplitScreenRuntime.Instance?.P2.IsUsingMenu ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.IsUsingMenu = value; }
        }

        
        public static InputAction p2ActionMove     => SplitScreenRuntime.Instance?.P2.ActionMove;
        public static InputAction p2ActionLook     => SplitScreenRuntime.Instance?.P2.ActionLook;
        public static InputAction p2ActionRotate   => SplitScreenRuntime.Instance?.P2.ActionRotate;
        public static InputAction p2ActionJump     => SplitScreenRuntime.Instance?.P2.ActionJump;
        public static InputAction p2ActionSprint   => SplitScreenRuntime.Instance?.P2.ActionSprint;
        public static InputAction p2ActionCrouch   => SplitScreenRuntime.Instance?.P2.ActionCrouch;
        public static InputAction p2ActionInteract => SplitScreenRuntime.Instance?.P2.ActionInteract;
        public static InputAction p2ActionMenu     => SplitScreenRuntime.Instance?.P2.ActionMenu;
        public static InputAction p2ActionFire     => SplitScreenRuntime.Instance?.P2.ActionFire;
        public static InputAction p2ActionContext  => SplitScreenRuntime.Instance?.P2.ActionContext;
        public static InputAction p2ActionSpecial   => SplitScreenRuntime.Instance?.P2.ActionSpecial;
        public static InputAction p2ActionCancel    => SplitScreenRuntime.Instance?.P2.ActionCancel;
        public static InputAction p2ActionBlockPick => SplitScreenRuntime.Instance?.P2.ActionBlockPick;
        public static InputAction p2ActionTabLeft   => SplitScreenRuntime.Instance?.P2.ActionTabLeft;
        public static InputAction p2ActionTabRight  => SplitScreenRuntime.Instance?.P2.ActionTabRight;

        
        public static Text p2PromptText
        {
            get => SplitScreenRuntime.Instance?.P2.PromptText;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.PromptText = value; }
        }
        public static Text p2DeathText
        {
            get => SplitScreenRuntime.Instance?.P2.DeathText;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.DeathText = value; }
        }

        
        public static bool p2DeathHandled
        {
            get => SplitScreenRuntime.Instance?.P2.DeathHandled ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.DeathHandled = value; }
        }
        public static float p2DeathTimer
        {
            get => SplitScreenRuntime.Instance?.P2.DeathTimer ?? 0f;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.DeathTimer = value; }
        }

        

        
        public static int LAYER_P1_HAND = -1;
        public static int LAYER_P1_TOOL = -1;
        public static int LAYER_P1_BODY = -1;
        public static int LAYER_P2_HAND = -1;
        public static int LAYER_P1_NOTEBOOK = -1;

        
        internal static int     p1LocalPlayerLayer   = -1;
        internal static float   p1PivotXSaved        = 0f;
        public   static int     p1FPTransitionCooldown = 0;
        public   static Vector3 p1FPCameraEyeLocalPos  = Vector3.zero;

        
        
        internal static int     p1HandCameraLayer      = -1;
        internal static Rect    _p1HandCamRectSaved    = new Rect(0f, 0f, 0.5f, 1f);
        internal static bool    _nbFullscreenActive    = false;
        internal static Rect    _p1CamRectSaved        = new Rect(0f, 0f, 0.5f, 1f);
        internal static Rect    _p1UiCamRectSaved      = new Rect(0f, 0f, 0.5f, 1f);
        internal static float   _p1CamFovSaved         = -1f;

        
        
        internal static Vector3 p1StandingPivotLocalPos = Vector3.zero;
        internal static bool    p1PivotCached           = false;

        
        
        
        
        public   static float   p1FPEyeLocalY          = 0.55f;
        internal static bool    p1EyeCaptured          = false;

        
        
        internal static float   p1CamPitch             = 0f;

        
        public static readonly List<Renderer> p1CachedBodyRenderers = new List<Renderer>();

        
        internal static ThirdPerson player1ThirdPerson;

        

        
        
        internal static PlayerInventory pendingP2Inventory = null;
    }
}
