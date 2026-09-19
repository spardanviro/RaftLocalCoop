namespace SplitScreen
{
    internal sealed class P2UiState
    {
        int _exitClosedFrame = -1000;
        bool _suppressExitCrouch;

        internal void SuppressCrouchOnExit(int frame)
        {
            _exitClosedFrame = frame;
            _suppressExitCrouch = true;
        }

        internal bool IsCrouchExitSuppressed(int frame, bool cancelOrCrouchHeld)
        {
            if (frame <= _exitClosedFrame + 1) return true;
            if (!_suppressExitCrouch) return false;
            if (!cancelOrCrouchHeld)
            {
                _suppressExitCrouch = false;
                return false;
            }
            return true;
        }
    }
}
