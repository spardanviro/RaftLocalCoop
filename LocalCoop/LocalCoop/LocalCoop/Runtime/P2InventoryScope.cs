using System;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2InventoryScope —— 统一「P2 库存换入 + 拾取路由」窗口(收束散落的 SwapInP2/RoutingPickup/try-finally)。
    //
    //  用法：
    //    using (new P2InventoryScope()) { /* 这段里的 AddItem/采集产物落进 P2 背包 */ }
    //  或存入 Harmony __state，在 Postfix/Finalizer 里 Dispose(配合 P2OriginalScope 一起)。
    //
    //  语义(与原先各处手写一致)：
    //   - 幂等换入：仅当尚未换入(IsSwapped==false)时才 SwapInP2，并在 Dispose 时由【本作用域】SwapOutP2；
    //     若进入时已是换入态(别处已换)，则不重复换、Dispose 也不换出(谁换入谁换出)。
    //   - RoutingPickup 保存/恢复前值 → 可安全嵌套。
    //   - SwapIn/Out 异常吞掉(与原先 try/catch 行为一致)，不让库存换入失败打断工具/设备主流程。
    // ══════════════════════════════════════════════════════════════════════
    public sealed class P2InventoryScope : IDisposable
    {
        readonly bool _swapped;
        readonly bool _prevRouting;
        bool _disposed;

        public P2InventoryScope()
        {
            if (!P2InventoryStore.IsSwapped)
            {
                try { _swapped = P2InventoryStore.TrySwapInP2(); }
                catch (Exception e) { Main.ModEntry.Logger.Log("[P2InvScope] SwapIn failed: " + e.Message); }
            }
            _prevRouting = P2InventoryStore.RoutingPickup;
            // RoutingPickup 只在【确实是 P2 背包活】时开(本作用域换入成功 或 外层已换入);
            //  换入失败(inv 为空)时不开 → 物品不会错落 P1。
            if (P2InventoryStore.IsSwapped) P2InventoryStore.RoutingPickup = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            P2InventoryStore.RoutingPickup = _prevRouting;
            if (_swapped)
            {
                try { P2InventoryStore.SwapOutP2(); }
                catch (Exception e) { Main.ModEntry.Logger.Log("[P2InvScope] SwapOut failed: " + e.Message); }
            }
        }
    }
}
