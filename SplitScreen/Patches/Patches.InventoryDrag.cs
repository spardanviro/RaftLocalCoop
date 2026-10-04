using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  Patch: Inventory.Update — 修正拖拽图标位置（分屏把 UI 画布改成了相机模式）
    //
    //  原版 Inventory.Update 里：draggingImage.rectTransform.position = CursorPos(屏幕像素)。
    //  这只对 ScreenSpaceOverlay 画布成立。分屏后 MoveP1UiToLeftHalf 把背包所在的
    //  _CanvasGame_New 改成 ScreenSpaceCamera(绑左半屏 P1_UICamera)，此时 .position 是世界坐标，
    //  把屏幕像素当世界坐标 → 图标飞到屏幕外 → 拖拽时看不到跟随的物品图标。
    //
    //  修复：在原版 Update 之后，若画布是相机模式且正在拖拽，用 RectTransformUtility 把
    //  鼠标屏幕点转成画布所在平面的世界点，重设 draggingImage / dropText 的世界位置。
    //  (P2 手柄虚拟光标的处理留待手柄光标阶段。)
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Inventory), "Update")]
    static class Patch_Inventory_Update_DragIcon
    {
        static void Postfix(Inventory __instance)
        {
            var img = __instance.draggingImage;
            if (img == null || !img.gameObject.activeSelf) return;     // 没在拖拽

            var cv = img.canvas;
            // img.canvas 可能是 draggingImage 自带的小子画布 → 其 rect 太小，射线打不中转换失败。
            //  改用【根画布】(_CanvasGame_New，铺满全屏)做屏幕→世界转换，射线必中。
            var root = cv != null ? cv.rootCanvas : null;
            if (root == null || root.renderMode != RenderMode.ScreenSpaceCamera) return; // Overlay → 原版已正确
            var cam = root.worldCamera;
            var rootRect = root.transform as RectTransform;
            if (cam == null || rootRect == null) return;

            Vector2 screen = Mouse.current != null ? Mouse.current.position.ReadValue() : (Vector2)Input.mousePosition;
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(rootRect, screen, cam, out var world))
            {
                // pivot=居中 → position(=世界点) 就是图标正中心 → 中心正好落在鼠标尖上
                img.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                img.rectTransform.position = world;

                var ch = ComponentManager<CanvasHelper>.Value;
                if (ch != null && ch.dropText != null && ch.dropText.gameObject.activeInHierarchy)
                    ch.dropText.rectTransform.position = world;
            }
        }
    }
}
