using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using HarmonyLib;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Main.State — 全局状态垫片
    //
    //  职责划分：
    //  A) 通过属性代理到 SplitScreenRuntime / PlayerSlot（per-player 状态）
    //  B) 保留渲染专用静态字段（layer 常量、P1 相机辅助状态）
    //  C) 保留少量生成期临时字段（pendingP2Inventory）
    //
    //  目的：让现有 patch 代码在不修改调用点的前提下透明读写 Runtime 状态。
    //  后续可在 patch 中逐步直接引用 SplitScreenRuntime.Instance.P2.XXX，
    //  届时对应的兼容属性可安全删除。
    // ══════════════════════════════════════════════════════════════════════
    public static partial class Main
    {
        
        public static UmmShim ModEntry = new UmmShim();
        public static Harmony                  Harmony;
        static readonly P2UiState              P2Ui = new P2UiState();
        static readonly P2InteractionState     InteractionState = new P2InteractionState();

        // ══ A. per-player 状态 → 代理到 Runtime ═══════════════════════════

        // ── 玩家引用 ─────────────────────────────────────────────────────
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

        // ── 渲染用 ArmMesh（per-player，存入 Slot） ───────────────────────
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

        // ── 相机 cullingMask 原始值（per-player） ─────────────────────────
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

        // ── P2 生成期间屏蔽标志 ───────────────────────────────────────────
        public static bool isSpawningP2
        {
            get => SplitScreenRuntime.Instance?.IsSpawningP2 ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.IsSpawningP2 = value; }
        }

        // ── P2 射线交互上下文标志 ─────────────────────────────────────────
        public static bool isProcessingP2Ray
        {
            get => SplitScreenRuntime.Instance?.P2.IsProcessingRay ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.IsProcessingRay = value; }
        }

        // ── PersonController.Update 执行期间的 P2 激活标志 ───────────────
        public static bool p2PersonControllerActive
        {
            get => SplitScreenRuntime.Instance?.P2.IsInPersonController ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.IsInPersonController = value; }
        }

        
        
        public static bool p2UsingItemActive => P2Mode == P2OriginalMode.Tool && IsP2OriginalInputActive;

        // ── FillWaterComponent.Update 执行期间(P2 装/倒水)：已折叠进 P2Mode ────
        //  期间：相机→P2、"Interact"(装水)→P2 X、"RMB"(倒水)→P2 LT。写入改由 P2OriginalScope.FillWater() 设 P2Mode；
        //  历史消费者(AimRay/Helper.MainCamera/AimingAtTarget/SetLoadCircle/ReselectCurrentSlot 抑制/输入)继续读它，无需改动。
        internal static bool _p2FillWaterActive => P2Mode == P2OriginalMode.FillWater;

        // ── 统一的 P2 原版执行上下文模式(逐步替代上面分散的标志位) ──────────────
        //  由 P2OriginalScope 维护：scope 内原版代码"以为操作者是 P2"，退出恢复 P1。
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

        // ── P2 正在使用菜单 / 存储 ────────────────────────────────────────
        public static bool p2IsUsingMenu
        {
            get => SplitScreenRuntime.Instance?.P2.IsUsingMenu ?? false;
            internal set { if (SplitScreenRuntime.Instance != null) SplitScreenRuntime.Instance.P2.IsUsingMenu = value; }
        }

        // ── P2 输入 Actions（只读；由 InputRouter 管理生命周期） ──────────
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

        // ── P2 覆层 UI widget ─────────────────────────────────────────────
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

        // ── P2 死亡状态 ───────────────────────────────────────────────────
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

        // ══ B. 渲染专用静态字段（不属于任何 PlayerSlot） ═══════════════════

        // Layer 编号（运行时一次性分配，整个会话固定不变）
        public static int LAYER_P1_HAND = -1;
        public static int LAYER_P1_TOOL = -1;
        public static int LAYER_P1_BODY = -1;
        public static int LAYER_P2_HAND = -1;
        public static int LAYER_P1_NOTEBOOK = -1;

        // P1 相机渲染辅助状态（渲染帧间临时变量）
        internal static int     p1LocalPlayerLayer   = -1;
        internal static float   p1PivotXSaved        = 0f;
        public   static int     p1FPTransitionCooldown = 0;
        public   static Vector3 p1FPCameraEyeLocalPos  = Vector3.zero;

        // 原生 HandCamera 层（"HandCamera"=24）。原版手持工具 FP 时就在此层、由 HandCamera
        //  原生渲染并受光；mod 之前错误地把工具挪到 P1_TOOL → 脱离原生处理而发暗。
        internal static int     p1HandCameraLayer      = -1;
        internal static Rect    _p1HandCamRectSaved    = new Rect(0f, 0f, 0.5f, 1f);
        internal static bool    _nbFullscreenActive    = false;
        internal static Rect    _p1CamRectSaved        = new Rect(0f, 0f, 0.5f, 1f);
        internal static Rect    _p1UiCamRectSaved      = new Rect(0f, 0f, 0.5f, 1f);
        internal static float   _p1CamFovSaved         = -1f;

        // P1 站立时的 playerPivot 本地位置缓存（抵消下蹲时本地玩家专属的相机下沉 →
        //  防止 mod 强制可见的 P1 身体在 P2 视野里随下沉平移陷入木筏）。
        internal static Vector3 p1StandingPivotLocalPos = Vector3.zero;
        internal static bool    p1PivotCached           = false;

        // P1 第一人称相机解耦：眼高（相对玩家根节点）+ 是否已捕获。
        //  强制 thirdPersonController 会动画驱动 CameraHolder 头骨，
        //  导致挂在其下的 FP 相机俯仰失控、位置抖动。解法是把相机改挂玩家根节点，
        //  保持此眼高，俯仰由 mouseLookY.rotY 手动施加。
        public   static float   p1FPEyeLocalY          = 0.55f;
        internal static bool    p1EyeCaptured          = false;

        // P1 FP 自驱俯仰累加值（度）。游戏的 mouseLook 俯仰脚本在本 hybrid 状态下被冻结，
        //  故由 EnforceP1FpCamera 直接从鼠标 Y 累加驱动相机俯仰。
        internal static float   p1CamPitch             = 0f;

        // 每帧缓存 P1 在 LAYER_P1_BODY 上的 Renderer（避免重复 GetComponents）
        public static readonly List<Renderer> p1CachedBodyRenderers = new List<Renderer>();

        // P1 ThirdPerson 组件缓存（渲染 cullingMask 计算用）
        internal static ThirdPerson player1ThirdPerson;

        // ══ C. 生成期临时字段 ═══════════════════════════════════════════════

        // P2 Awake 期间 PlayerInventory 单例被覆写时的暂存值；
        // 由 Patch1 Postfix 写回 P2.Inventory 后置 null。
        internal static PlayerInventory pendingP2Inventory = null;
    }
}
