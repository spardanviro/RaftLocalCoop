using System;
using System.Reflection;

namespace SplitScreen
{
    // P2 当前所处的原版执行上下文。统一替代分散的 p2UsingItemActive / P2BuildToolActive /
    //  _p2FillWaterActive / isProcessingP2Ray 等标志，便于补丁统一判断 Main.P2Mode。
    public enum P2OriginalMode
    {
        None,
        Movement,
        Tool,
        Build,
        Interaction,
        Menu,
        FillWater
    }

    // ══════════════════════════════════════════════════════════════════════
    //  P2OriginalScope — P2 原版执行代理 scope
    //
    //  在很短的窗口内让原版代码"以为当前操作者是 P2"，执行完立即恢复 P1：
    //    using (P2OriginalScope.Tool()) { /* 原版工具/物品/菜单逻辑 */ }
    //
    //  scope 内：
    //   · PlayerContext.Active = P2 → ComponentManager<Network_Player/Player/PlayerInventory>.Value 都返回 P2(scope 直接换入 CM 背后字段;早期的 getter 补丁已废弃,见下方字段注释)
    //   · Network_Player.isLocalPlayer 私有字段 = true → 原版的本地玩家门控通过
    //   · Main.P2Mode = 对应模式 → AimRay/输入补丁据此把相机/按键指向 P2
    //  Dispose 恢复(每个 scope 自存旧值，支持嵌套；不用静态 bool，避免多补丁互踩)。
    // ══════════════════════════════════════════════════════════════════════
    public sealed class P2OriginalScope : IDisposable
    {
        readonly P2OriginalMode _prevMode;
        readonly Network_Player _prevActive;
        readonly Network_Player _p2;
        readonly bool _forcedLocal;
        readonly bool _oldIsLocal;
        readonly WellBeing _prevWellBeing;
        readonly bool _wellBeingScoped;
        readonly PlayerLocalDeathScope _localDeathScope;
        readonly CanvasActiveMenuScope _menuNeutralize;
        readonly bool _busyNeutralized;
        readonly bool _prevBusyRaw;
        // 进入作用域时换入 P2 的 CM 三件套,Dispose 还原。替代原先给
        // ComponentManager<T>.Value 的 getter 打补丁的做法 —— 那条路因 Mono 泛型代码共享
        // 会让三个实例化串台(实测 Cm.Get<Network_Player>() 返回过 PlayerInventory)。
        readonly Network_Player _prevCmNp;
        readonly Player _prevCmPlayer;
        readonly PlayerInventory _prevCmInv;
        readonly bool _cmSwapped;
        static readonly System.Reflection.FieldInfo _isBusyField =
            HarmonyLib.AccessTools.Field(typeof(PlayerItemManager), "isBusy");
        bool _disposed;

        public static P2OriginalScope Tool()        => new P2OriginalScope(P2OriginalMode.Tool);
        public static P2OriginalScope Build()        => new P2OriginalScope(P2OriginalMode.Build);
        public static P2OriginalScope Menu()         => new P2OriginalScope(P2OriginalMode.Menu);
        public static P2OriginalScope Interaction()  => new P2OriginalScope(P2OriginalMode.Interaction);
        public static P2OriginalScope FillWater()    => new P2OriginalScope(P2OriginalMode.FillWater);
        public static P2OriginalScope Movement()     => new P2OriginalScope(P2OriginalMode.Movement);

        P2OriginalScope(P2OriginalMode mode)
        {
            _prevMode   = Main.P2Mode;
            _prevActive = PlayerContext.Active;
            _p2         = Main.player2;

            PlayerContext.Active = _p2;
            Main.P2Mode          = mode;

            // 上面两行已经改了全局状态。后面任何一步抛异常,对象都不会返回给 using,
            // 所以必须在这里自己回滚,否则 P2 上下文会漏给 P1。
            try
            {

            // vanilla 读的是 ComponentManager<T>.Value,直接把三件套换成 P2 的。
            // 生成 P2 期间不介入(那时正在建立 P2 本身,换了会自指)。
            if (_p2 != null && !Main.isSpawningP2)
            {
                Main.SwapCmToPlayer(_p2, out _prevCmNp, out _prevCmPlayer, out _prevCmInv);
                _cmSwapped = true;
            }

            if (_p2 != null)
            {
                _oldIsLocal = VanillaAccessors.GetIsLocalPlayer(_p2);
                if (!_oldIsLocal) { VanillaAccessors.SetIsLocalPlayer(_p2, true); _forcedLocal = true; }
            }

            
            
            
            
            
            if (mode != P2OriginalMode.None)
            {
                _prevWellBeing = Main.PushP2WellBeing();
                _wellBeingScoped = true;

                _localDeathScope = new PlayerLocalDeathScope(_p2 != null && _p2.PlayerScript != null && _p2.PlayerScript.IsDead);
            }

            // P1 打开背包/研究桌等会把共享的 CanvasHelper.ActiveMenu 置为非 None,导致 P2 在此 scope 内
            // 跑的 vanilla 工具/交互逻辑(如 Hook.Update 首行 if(ActiveMenu!=None) return)提前退出 →
            // P2 无法按 RT 用工具、无法按 X 交互。P2 的工具/交互本应独立于 P1 的菜单,故这几个玩法模式期间
            // 把 ActiveMenu 临时归 None(本就 None 时为 no-op),Dispose 还原,不影响 P1 自己的菜单。
            // Movement 本就正常、Menu 是 P2 自己的菜单,均不介入。
            if (mode == P2OriginalMode.Tool || mode == P2OriginalMode.Build
                || mode == P2OriginalMode.Interaction || mode == P2OriginalMode.FillWater
                || mode == P2OriginalMode.Movement)   // Movement: 修 P1 开面板时 P2 无法奔跑(sprint 读 ActiveMenu)
            {
                _menuNeutralize = new CanvasActiveMenuScope(MenuType.None);
            }

            // IsBusy 归一化: P1 搬运动物把共享静态 PlayerItemManager.isBusy 置 true,大量 vanilla 方法
            // 内联读该字段(getter patch 对内联无效)→ P2 用工具时被误判 busy 而无法使用。这几个玩法模式
            // 期间把 raw 字段压 false(存 P1 值→置 false→Dispose 还原)。Movement 冲刺不读 isBusy 故不含。
            if (_isBusyField != null && (mode == P2OriginalMode.Tool || mode == P2OriginalMode.Build
                || mode == P2OriginalMode.Interaction || mode == P2OriginalMode.FillWater))
            {
                _prevBusyRaw = (bool)_isBusyField.GetValue(null);
                _isBusyField.SetValue(null, false);
                _busyNeutralized = true;
            }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _menuNeutralize?.Dispose();
            if (_busyNeutralized && _isBusyField != null) _isBusyField.SetValue(null, _prevBusyRaw);
            _localDeathScope?.Dispose();
            if (_wellBeingScoped) Main.RestoreWellBeing(_prevWellBeing);
            if (_forcedLocal && _p2 != null) VanillaAccessors.SetIsLocalPlayer(_p2, _oldIsLocal);
            if (_cmSwapped) Main.RestoreCm(_prevCmNp, _prevCmPlayer, _prevCmInv);
            PlayerContext.Active = _prevActive;
            Main.P2Mode          = _prevMode;
        }
    }
}
