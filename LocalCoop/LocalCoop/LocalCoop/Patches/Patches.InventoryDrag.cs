using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    //
    
    
    
    
    [HarmonyPatch(typeof(Inventory), "Update")]
    static class Patch_Inventory_Update_DragIcon
    {
        static void Postfix(Inventory __instance)
        {
            var img = __instance.draggingImage;
            if (img == null || !img.gameObject.activeSelf) return;     

            var cv = img.canvas;
            
            
            var root = cv != null ? cv.rootCanvas : null;
            if (root == null || root.renderMode != RenderMode.ScreenSpaceCamera) return; 
            var cam = root.worldCamera;
            var rootRect = root.transform as RectTransform;
            if (cam == null || rootRect == null) return;

            Vector2 screen = Mouse.current != null ? Mouse.current.position.ReadValue() : (Vector2)Input.mousePosition;
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(rootRect, screen, cam, out var world))
            {
                
                img.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                img.rectTransform.position = world;

                var ch = ComponentManager<CanvasHelper>.Value;
                if (ch != null && ch.dropText != null && ch.dropText.gameObject.activeInHierarchy)
                    ch.dropText.rectTransform.position = world;
            }
        }
    }
}
