using System;

namespace SplitScreen
{
    public sealed class CanvasActiveMenuScope : IDisposable
    {
        readonly MenuType _previous;
        readonly bool _changed;
        bool _disposed;

        public CanvasActiveMenuScope(MenuType desired)
        {
            _previous = CanvasHelper.ActiveMenu;
            if (_previous == desired) return;

            CanvasHelper.ActiveMenu = desired;
            _changed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_changed) CanvasHelper.ActiveMenu = _previous;
        }
    }

    public sealed class PlayerLocalDeathScope : IDisposable
    {
        readonly bool _previous;
        bool _disposed;

        public PlayerLocalDeathScope(bool value)
        {
            _previous = Player.LocalPlayerIsDead;
            Player.LocalPlayerIsDead = value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Player.LocalPlayerIsDead = _previous;
        }
    }

    public sealed class PlayerItemBusyScope : IDisposable
    {
        readonly bool _previous;
        bool _disposed;

        public PlayerItemBusyScope()
        {
            _previous = PlayerItemManager.IsBusy;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PlayerItemManager.IsBusy = _previous;
        }
    }

    internal static class PlayerItemBusyState
    {
        internal static void SetForCarryVisibility(bool busy)
        {
            PlayerItemManager.IsBusy = busy;
        }
    }
}
