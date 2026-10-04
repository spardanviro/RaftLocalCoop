using System;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2FrameContext — 「让 P2 跑 vanilla 逻辑」的统一入口(B 类·单例共享收口)
    //
    //  把一次 P2 代理执行所需的上下文一站式就位,取代到处手写的
    //   `Scope = P2OriginalScope.Tool(), Inv = new P2InventoryScope()` 组合:
    //   · P2OriginalScope(mode) —— PlayerContext.Active=P2(单例 getter 全返回 P2) +
    //       Network_Player.isLocalPlayer=true(过本地玩家门控) + Main.P2Mode(驱动 AimRay 相机 + 输入映射)
    //   · 可选 P2InventoryScope —— 换入 P2 背包 + 拾取路由(产出/扣减/返还都落 P2,不污染 P1)
    //
    //  约定:任何【新的】P2 vanilla 入口都用 P2FrameContext.Xxx(...);
    //   需要"物品进出 P2 背包"(采集/扣减/放置/返还)时传 routeInventory:true,纯门控/瞄准传 false。
    //   设备交互的额外特化(选中槽注入 / 设备 localPlayer override / IsBusy save-restore)仍在
    //   InteractionRouter.RunDeviceRayAsP2(设备专属,不通用)。
    //
    //  用法:
    //   using (P2FrameContext.Tool(routeInventory: true)) { /* 原版工具逻辑,产出落 P2 背包 */ }
    //   或存入 Harmony __state,在 Postfix/Finalizer 里 Dispose。
    //
    //  Dispose 顺序:先换出背包(路由复原),再还原本地/context/mode —— 与原手写 Cleanup 一致。
    // ══════════════════════════════════════════════════════════════════════
    public sealed class P2FrameContext : IDisposable
    {
        readonly P2OriginalScope _scope;
        readonly P2InventoryScope _inv;
        bool _disposed;

        P2FrameContext(P2OriginalScope scope, P2InventoryScope inv) { _scope = scope; _inv = inv; }

        public static P2FrameContext Tool(bool routeInventory = false)
            => new P2FrameContext(P2OriginalScope.Tool(), routeInventory ? new P2InventoryScope() : null);

        public static P2FrameContext Build(bool routeInventory = false)
            => new P2FrameContext(P2OriginalScope.Build(), routeInventory ? new P2InventoryScope() : null);

        public static P2FrameContext Interaction(bool routeInventory = false)
            => new P2FrameContext(P2OriginalScope.Interaction(), routeInventory ? new P2InventoryScope() : null);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inv?.Dispose();   // 先换出 P2 背包 + 路由复原
            _scope.Dispose();  // 再还原 isLocalPlayer / PlayerContext / P2Mode
        }
    }
}
