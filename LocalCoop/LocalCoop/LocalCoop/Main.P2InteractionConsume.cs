using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        static int _p2ConsumedInteractFrame = -1;

        internal static bool P2ConsumedInteractThisFrame => _p2ConsumedInteractFrame == Time.frameCount;

        internal static void ConsumeP2InteractThisFrame()
        {
            _p2ConsumedInteractFrame = Time.frameCount;
        }
    }
}
