using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch: GamepadCursor.AfterUpdate — 修正相机画布下虚拟光标的位置
    //
    //  原版 AfterUpdate 末尾：ScreenPointToLocalPointInRectangle(canvasRectTransform, vector, null, ...)
    //  用 null 相机 → 仅对 ScreenSpaceOverlay 成立。分屏把 _CanvasGame_New 改成 ScreenSpaceCamera
    //  (左半屏 P1_UICamera) → 光标定位错误(跑到屏外)。这里在原版之后，用画布的 worldCamera 重新换算光标位置。
    //  仅在 P2 菜单打开时介入，不影响 P1 正常使用手柄菜单的情形。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(GamepadCursor), "AfterUpdate")]
    static class Patch_GamepadCursor_AfterUpdate
    {
        static FieldInfo _fCursor, _fCanvasRect, _fVMouse;

        static void Postfix(GamepadCursor __instance)
        {
            if (!Main.p2IsUsingMenu) return;
            try
            {
                if (_fCursor == null)
                {
                    var t = typeof(GamepadCursor);
                    _fCursor     = t.GetField("cursorTransform",     BindingFlags.Instance | BindingFlags.NonPublic);
                    _fCanvasRect = t.GetField("canvasRectTransform", BindingFlags.Instance | BindingFlags.NonPublic);
                    _fVMouse     = t.GetField("virtualMouse",        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                var cursorT    = _fCursor?.GetValue(__instance) as RectTransform;
                var canvasRect = _fCanvasRect?.GetValue(__instance) as RectTransform;
                var vmouse     = _fVMouse?.GetValue(__instance) as Mouse;
                if (cursorT == null || canvasRect == null || vmouse == null) return;
                if (!cursorT.gameObject.activeSelf) return;

                var canvas = canvasRect.GetComponentInParent<Canvas>();
                if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceCamera) return; // Overlay → 原版已正确

                Vector2 mp = vmouse.position.ReadValue();
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, mp, canvas.worldCamera, out var local))
                    cursorT.anchoredPosition = local;
            }
            catch (System.Exception e) { Main.LogV("[GamepadCursor] virtual cursor sync ignored: " + e.Message); }
        }
    }
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
}
