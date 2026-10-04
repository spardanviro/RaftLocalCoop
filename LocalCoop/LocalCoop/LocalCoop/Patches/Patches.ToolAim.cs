using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  准星射线修正：Helper 里所有"屏幕中心射线"(MainCamera.ScreenPointToRay(全屏中心))
    //  在分屏下都偏(全屏中心落在 P1 视图右边缘)。统一改为相机正前方
    //  new Ray(cam.position, cam.forward) —— 第一人称下即准星方向，绕开 Unity
    //  ScreenPointToRay/ViewportPointToRay 对 rect 非全屏相机算错的坑。
    //   - P2 用工具时(p2UsingItemActive) 用 P2 相机；其余用 Helper.MainCamera(P1)。
    // ══════════════════════════════════════════════════════════════════════
    static class AimRay
    {
        // 准星射线统一改为【对应相机的正前方】= 第一人称准心方向。绕开原版 ScreenPointToRay(全屏中心)
        //  在分屏下落到分割线(不在各自半屏准心)的坑：P1 用 P1 相机、P2(用工具/建造时)用 P2 相机。
        public static Camera Cam()
        {
            // P2 处于任意原版执行上下文(Tool/Build/Interaction/Menu/FillWater)、建造工具/装水、或【设备射线交互】(isProcessingP2Ray)
            //  期间 → 用 P2 相机。设备交互含此项:OnIsRayed 内 HitAtCursor 检测准心对着的 cookingSlot(取淡水)需用 P2 准心。
            if ((Main.IsP2OriginalActive || Main.P2BuildToolActive || Main._p2FillWaterActive || Main.isProcessingP2Ray) && Main.player2 != null && Main.player2.Camera != null)
                return Main.player2.Camera;
            return Helper.MainCamera;
        }

        public static bool TryRay(out Ray ray)
        {
            var cam = Cam();
            if (cam == null) { ray = default; return false; }
            ray = new Ray(cam.transform.position, cam.transform.forward);
            return true;
        }

        // 当前射线是否来自【P2 相机】(P2 恒第三人称、相机在身后)。
        public static bool UsingP2Cam =>
            (Main.IsP2OriginalActive || Main.P2BuildToolActive || Main._p2FillWaterActive || Main.isProcessingP2Ray)
            && Main.player2 != null && Main.player2.Camera != null;

        // P2 第三人称相机在身后 → 普通射线会【先打到 P2 自己的身体碰撞体】(诊断:Untagged 'Collider' @ ~3m)，
        //  导致工具(斧/锤)命中的是自己而非目标。这里用 RaycastAll 取第一个【非 P2 自身】命中；P1(第一人称)走普通 Raycast。
        public static bool Raycast(Ray ray, float dist, LayerMask mask, QueryTriggerInteraction qti, out RaycastHit hit)
        {
            if (!UsingP2Cam) return Physics.Raycast(ray, out hit, dist, mask, qti);
            return FirstNonP2(Physics.RaycastAll(ray, dist, mask, qti), out hit);
        }

        // SphereCast 版(部分交互用)。
        public static bool SphereCast(Ray ray, float radius, float dist, LayerMask mask, out RaycastHit hit)
        {
            if (!UsingP2Cam) return Physics.SphereCast(ray, radius, out hit, dist, mask);
            return FirstNonP2(Physics.SphereCastAll(ray, radius, dist, mask), out hit);
        }

        // 从命中数组里(按距离升序)取第一个不属于 P2 自身层级的命中。
        public static bool FirstNonP2(RaycastHit[] hits, out RaycastHit hit)
        {
            hit = default;
            if (hits == null || hits.Length == 0) return false;
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var p2 = Main.player2 != null ? Main.player2.transform : null;
            for (int i = 0; i < hits.Length; i++)
            {
                var tr = hits[i].transform;
                if (p2 != null && tr != null && tr.IsChildOf(p2)) continue;   // P2 自身(身体/手臂/工具)碰撞体 → 跳过
                hit = hits[i];
                return true;
            }
            return false;
        }

        // 过滤掉 P2 自身命中(供 HitAllAtCursor 用，保留多命中数组语义)。
        public static RaycastHit[] FilterNonP2(RaycastHit[] hits)
        {
            if (!UsingP2Cam || hits == null || hits.Length == 0) return hits;
            var p2 = Main.player2 != null ? Main.player2.transform : null;
            if (p2 == null) return hits;
            var list = new System.Collections.Generic.List<RaycastHit>(hits.Length);
            for (int i = 0; i < hits.Length; i++)
            {
                var tr = hits[i].transform;
                if (tr != null && tr.IsChildOf(p2)) continue;
                list.Add(hits[i]);
            }
            return list.ToArray();
        }
    }

    [HarmonyPatch]
    static class Patch_Helper_HitAtCursor_Mask
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Helper), "HitAtCursor",
            new[] { typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(LayerMask), typeof(QueryTriggerInteraction) });
        static bool Prefix(ref RaycastHit hit, float rayDistance, LayerMask mask,
                           QueryTriggerInteraction queryTriggerInteraction, ref bool __result)
        {
            if (!AimRay.TryRay(out var ray)) return true;
            __result = AimRay.Raycast(ray, rayDistance, mask, queryTriggerInteraction, out hit);
            return false;
        }
    }

    [HarmonyPatch]
    static class Patch_Helper_HitAtCursor_NoMask
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Helper), "HitAtCursor",
            new[] { typeof(RaycastHit).MakeByRefType(), typeof(float) });
        static bool Prefix(ref RaycastHit hit, float rayDistance, ref bool __result)
        {
            if (!AimRay.TryRay(out var ray)) return true;
            __result = AimRay.Raycast(ray, rayDistance, LayerMasks.MASK_IgnorePlayer, QueryTriggerInteraction.UseGlobal, out hit);
            return false;
        }
    }

    
    [HarmonyPatch]
    static class Patch_Helper_HitAtCursor_AbortHover
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Helper), "HitAtCursor",
            new[] { typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(LayerMask), typeof(bool) });
        static bool Prefix(ref RaycastHit hit, float rayDistance, LayerMask mask, bool abortOnItemHover, ref bool __result)
        {
            if (!AimRay.TryRay(out var ray)) return true;   // 非 P2 上下文 → 原版照常(含其原生 abort 逻辑)
            // P2 上下文:abort 判定必须用 P2 自己的悬停态。vanilla Pickup.isHoveringOverPickup 是全局单份、
            //  只被 P1 的 Pickup.Update 写 → 读它会因 P1 悬停拾取物而误 abort P2 工具。改读 P2 影子态。
            if (abortOnItemHover && Main.IsP2HoveringPickup) { hit = default; __result = false; return false; }
            __result = AimRay.Raycast(ray, rayDistance, mask, QueryTriggerInteraction.UseGlobal, out hit);
            return false;
        }
    }

    
    [HarmonyPatch]
    static class Patch_Helper_HitAllAtCursor
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Helper), "HitAllAtCursor",
            new[] { typeof(RaycastHit[]).MakeByRefType(), typeof(float), typeof(LayerMask) });
        static bool Prefix(ref RaycastHit[] allHits, float rayDistance, LayerMask mask, ref bool __result)
        {
            if (!AimRay.TryRay(out var ray)) return true;
            allHits = AimRay.FilterNonP2(Physics.RaycastAll(ray, rayDistance, mask));   // 去掉 P2 自身命中(第三人称相机在身后)
            __result = allHits.Length != 0;
            return false;
        }
    }

    
    [HarmonyPatch]
    static class Patch_Helper_SphereHitAtCursor
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Helper), "SphereHitAtCursor",
            new[] { typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(float), typeof(LayerMask) });
        static bool Prefix(ref RaycastHit hit, float rayDistance, float radius, LayerMask mask, ref bool __result)
        {
            if (!AimRay.TryRay(out var ray)) return true;
            __result = AimRay.SphereCast(ray, radius, rayDistance, mask, out hit);
            return false;
        }
    }

    
    [HarmonyPatch]
    static class Patch_Helper_FindInteractable
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Helper), "FindInteractable",
            new[] { typeof(float), typeof(QueryTriggerInteraction) });
        static bool Prefix(float interactDistance, QueryTriggerInteraction queryTriggerInteraction, ref RaycastInteractable __result)
        {
            if (!AimRay.TryRay(out var ray)) return true;
            var found = Cast(ray, interactDistance, queryTriggerInteraction);
            if (found == null)
            {
                // 反向射线兜底(匹配原版)：从远端往回打，抓近距离/低矮交互物(如脚边的箱子，正向射线易掠过)。
                var back = new Ray(ray.origin + ray.direction.normalized * interactDistance, -ray.direction);
                found = Cast(back, interactDistance, queryTriggerInteraction);
            }
            __result = found;
            return false;
        }

        static RaycastInteractable Cast(Ray ray, float dist, QueryTriggerInteraction qti)
        {
            if (Physics.Raycast(ray, out var hit, dist, LayerMasks.MASK_RaycastInteractable, qti) && hit.collider != null)
            {
                var ri = hit.collider.GetComponent<RaycastInteractable>();
                if (ri == null)
                {
                    var redirect = hit.collider.GetComponent<RaycastInteractable_Redirect>();
                    if (redirect != null) ri = redirect.RaycastInteractable;
                }
                return ri;
            }
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Patch: Hotbar.ReselectCurrentSlot — P2 设备/装水期间抑制(防 P1 手持模型被换成注入物)
    //   设备/FillWater 在操作选中槽后调 ReselectCurrentSlot，它对【共享热栏=P1】重选手持模型，
    //   而此刻槽里被注入了 P2 手持物 → P1 手里会变成 P2 的电池/水瓶。P2 手持模型走 _p2UseItemController，
    //   无需此重选。故 P2 上下文期间直接跳过；P2 上下文结束后 P1 的模型本就没被动过。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Hotbar), "ReselectCurrentSlot")]
    static class Patch_Hotbar_ReselectCurrentSlot_P2
    {
        static bool Prefix() => !(Main._p2FillWaterActive || Main.isProcessingP2Ray);
    }

    // 注：P2 盛水(装海水)已改为【无桥】实现 —— 见 Main.P2FillWater.cs 的 TickP2FillWater()。
    //  不再 hook FillWaterComponent.Update / AimingAtTarget / Helper.MainCamera、不注入 P1 共享选中槽，
    //  直接检测"P2 瞄水面 + 按 X → 转换 _p2Hotbar 当前格"，彻底拆掉对帧内时序敏感的注入/回写桥。
}
