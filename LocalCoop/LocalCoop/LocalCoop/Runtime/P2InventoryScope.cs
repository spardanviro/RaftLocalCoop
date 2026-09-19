using System;

namespace SplitScreen
{
    
    
    //
    
    
    
    //
    
    
    
    
    
    
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
