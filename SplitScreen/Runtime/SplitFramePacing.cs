using UnityEngine;

namespace SplitScreen
{
    // A 60 FPS game on a 75 Hz display can fall to 37.5 FPS when VSync misses
    // a refresh window. This override exists only for an active split session.
    internal static class SplitFramePacing
    {
        static int _savedVSync = -1;

        internal static void Enable()
        {
            if (_savedVSync >= 0) return;
            _savedVSync = QualitySettings.vSyncCount;
            if (_savedVSync == 0) return;
            QualitySettings.vSyncCount = 0;
            Main.ModEntry.Logger.Log($"[FramePacing] Split-screen: VSync {_savedVSync} -> 0 (Raft target FPS remains {GameManager.targetFrameRate})");
        }

        internal static void Restore()
        {
            if (_savedVSync < 0) return;
            QualitySettings.vSyncCount = _savedVSync;
            Main.ModEntry.Logger.Log($"[FramePacing] Restored VSync={_savedVSync}");
            _savedVSync = -1;
        }
    }
}
