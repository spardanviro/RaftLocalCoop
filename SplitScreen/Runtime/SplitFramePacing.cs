using HarmonyLib;
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

        internal static bool Active => _savedVSync >= 0;

        // 原版保存设置时直接读 QualitySettings.vSyncCount 写盘。分屏期间那是我们压成的 0,
        // 所以保存前先换回用户真实的值,保存完再压回去。
        internal static void BeginVanillaSave()
        {
            if (Active) QualitySettings.vSyncCount = _savedVSync;
        }

        internal static void EndVanillaSave()
        {
            if (Active) QualitySettings.vSyncCount = 0;
        }

        // 原版读入设置 / 用户拨动 VSync 开关后,运行时值就是用户想要的值:
        // 记下来当作"离开分屏时还原的目标",然后继续保持分屏的 0。
        internal static void AdoptVanillaValue()
        {
            if (!Active) return;
            _savedVSync = QualitySettings.vSyncCount;
            QualitySettings.vSyncCount = 0;
        }
    }

    [HarmonyPatch(typeof(GraphicsSettingsBox), "Save")]
    static class Patch_GraphicsSettingsBox_Save_KeepUserVSync
    {
        static void Prefix() => SplitFramePacing.BeginVanillaSave();
        static void Finalizer() => SplitFramePacing.EndVanillaSave();
    }

    [HarmonyPatch(typeof(GraphicsSettingsBox), "Load")]
    static class Patch_GraphicsSettingsBox_Load_KeepSplitPacing
    {
        static void Postfix() => SplitFramePacing.AdoptVanillaValue();
    }

    [HarmonyPatch(typeof(GraphicsSettingsBox), "OnVsyncChange")]
    static class Patch_GraphicsSettingsBox_OnVsyncChange_KeepSplitPacing
    {
        static void Postfix() => SplitFramePacing.AdoptVanillaValue();
    }
}
