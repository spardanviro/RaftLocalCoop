using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;
using UnityEngine.UI;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  PlayerSlot — 单个玩家参与者的完整上下文
    //
    //  P1 只用到 Player / ArmMesh / CamOriginalMask（原生游戏管其他的）。
    //  P2 拥有完整上下文：自定义输入、覆层 UI、交互状态、死亡状态。
    //
    //  状态原则：
    //  - 所有 "正在执行某帧操作" 的布尔（IsInPersonController / IsProcessingRay）
    //    在帧开始时置 true，帧结束或 Finalizer 时必须清 false。
    //  - IsUsingMenu 表示 P2 主动打开了菜单或箱子，Y 键可关闭。
    // ══════════════════════════════════════════════════════════════════════
    public sealed class PlayerSlot
    {
        // ── Identity ──────────────────────────────────────────────────────
        public Network_Player Player { get; internal set; }
        public bool           IsP2   { get; }

        // ── Input（P2 专属） ──────────────────────────────────────────────
        public InputAction ActionMove     { get; internal set; }
        public InputAction ActionLook     { get; internal set; }
        public InputAction ActionRotate   { get; internal set; }
        public InputAction ActionJump     { get; internal set; }
        public InputAction ActionSprint   { get; internal set; }
        public InputAction ActionCrouch   { get; internal set; }
        public InputAction ActionInteract { get; internal set; }
        public InputAction ActionMenu     { get; internal set; }
        public InputAction ActionPause    { get; internal set; }
        public InputAction ActionFire        { get; internal set; }
        public InputAction ActionContext     { get; internal set; }   // 原版 "RMB"(瞄准/副功能，如钓竿/油漆/倒水) = LT
        public InputAction ActionHotbarPrev  { get; internal set; }
        public InputAction ActionHotbarNext  { get; internal set; }
        public InputAction ActionSpecial     { get; internal set; }   // 镜像/油漆单面 等
        public InputAction ActionCancel      { get; internal set; }   // 菜单取消(=B)
        public InputAction ActionBlockPick   { get; internal set; }   // 取模块(=R3)
        public InputAction ActionTabLeft     { get; internal set; }   // 菜单上一标签(=LB)
        public InputAction ActionTabRight    { get; internal set; }   // 菜单下一标签(=RB)
        internal InputActionMap ActionMap { get; set; }

        // ── 独立手持（阶段1）：P2 自己的热栏选中索引 ──────────────────────
        public int HotbarIndex { get; internal set; } = 0;
        internal InputUser      InputUser { get; set; }

        // ── 帧级执行标志（同帧内有效，跨帧必须为 false） ─────────────────
        /// PersonController.Update 正在执行时为 true（Finalizer 保证清除）
        public bool IsInPersonController { get; internal set; }
        /// P2 pickup 射线正在处理时为 true（路由 "Interact" 键到手柄 X）
        public bool IsProcessingRay      { get; internal set; }
        /// UseItemController.Update 正在为 P2 执行时为 true（使用工具/攻击；Finalizer 保证清除）
        public bool IsUsingItem          { get; internal set; }

        // ── 菜单 / 存储 UI 状态 ───────────────────────────────────────────
        /// P2 主动打开背包、制作菜单或箱子时为 true；Y 键或外部关闭时清 false
        public bool IsUsingMenu { get; internal set; }
        /// P2 当前打开的箱子；非 null 时 Y / X 需先走 CloseStorage 完整路径
        public Storage_Small CurrentStorage { get; internal set; }

        // ── 覆层 UI ───────────────────────────────────────────────────────
        public Text PromptText { get; internal set; }
        public Text DeathText  { get; internal set; }

        // ── 渲染（每玩家独立） ────────────────────────────────────────────
        public SkinnedMeshRenderer ArmMesh        { get; internal set; }
        public int                 CamOriginalMask { get; internal set; } = -1;

        // ── 死亡 / 复活 ───────────────────────────────────────────────────
        public bool  DeathHandled { get; internal set; }
        public float DeathTimer   { get; internal set; }

        // ─────────────────────────────────────────────────────────────────
        public PlayerSlot(bool isP2) { IsP2 = isP2; }

        internal void AccumulateDeathTime(float dt) => DeathTimer += dt;

        internal void ResetDeathState()
        {
            DeathHandled = false;
            DeathTimer   = 0f;
        }
    }
}
