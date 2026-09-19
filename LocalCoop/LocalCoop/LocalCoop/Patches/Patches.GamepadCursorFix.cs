using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
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
                if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceCamera) return; 

                Vector2 mp = vmouse.position.ReadValue();
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, mp, canvas.worldCamera, out var local))
                    cursorT.anchoredPosition = local;
            }
            catch (System.Exception e) { Main.LogV("[GamepadCursor] virtual cursor sync ignored: " + e.Message); }
        }
    }

}
