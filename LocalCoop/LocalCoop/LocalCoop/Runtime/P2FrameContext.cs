using System;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    //
    
    
    
    
    //
    
    
    
    //
    
    
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
            _inv?.Dispose();   
            _scope.Dispose();  
        }
    }
}
