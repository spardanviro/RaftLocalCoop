using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    
    
    
    
    
    
    
    static class AimRay
    {
        
        
        public static Camera Cam()
        {
            
            
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

        
        public static bool UsingP2Cam =>
            (Main.IsP2OriginalActive || Main.P2BuildToolActive || Main._p2FillWaterActive || Main.isProcessingP2Ray)
            && Main.player2 != null && Main.player2.Camera != null;

        
        
        public static bool Raycast(Ray ray, float dist, LayerMask mask, QueryTriggerInteraction qti, out RaycastHit hit)
        {
            if (!UsingP2Cam) return Physics.Raycast(ray, out hit, dist, mask, qti);
            return FirstNonP2(Physics.RaycastAll(ray, dist, mask, qti), out hit);
        }

        
        public static bool SphereCast(Ray ray, float radius, float dist, LayerMask mask, out RaycastHit hit)
        {
            if (!UsingP2Cam) return Physics.SphereCast(ray, radius, out hit, dist, mask);
            return FirstNonP2(Physics.SphereCastAll(ray, radius, dist, mask), out hit);
        }

        
        public static bool FirstNonP2(RaycastHit[] hits, out RaycastHit hit)
        {
            hit = default;
            if (hits == null || hits.Length == 0) return false;
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var p2 = Main.player2 != null ? Main.player2.transform : null;
            for (int i = 0; i < hits.Length; i++)
            {
                var tr = hits[i].transform;
                if (p2 != null && tr != null && tr.IsChildOf(p2)) continue;   
                hit = hits[i];
                return true;
            }
            return false;
        }

        
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
            if (!AimRay.TryRay(out var ray)) return true;   
            
            
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
            allHits = AimRay.FilterNonP2(Physics.RaycastAll(ray, rayDistance, mask));   
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

    
    
    
    
    
    
    [HarmonyPatch(typeof(Hotbar), "ReselectCurrentSlot")]
    static class Patch_Hotbar_ReselectCurrentSlot_P2
    {
        static bool Prefix() => !(Main._p2FillWaterActive || Main.isProcessingP2Ray);
    }

    
    
    
}
