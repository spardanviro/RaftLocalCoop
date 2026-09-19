using UnityEngine.InputSystem;

namespace SplitScreen
{
    public static partial class Main
    {
        internal static Gamepad GetP2BoundGamepad()
        {
            return SplitScreenRuntime.Instance?.Input?.GetP2Gamepad();
        }
    }
}
