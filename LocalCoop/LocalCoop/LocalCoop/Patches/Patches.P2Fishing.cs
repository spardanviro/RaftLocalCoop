using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    
    
    
    
    
    
    
    
    
    [HarmonyPatch(typeof(FishingRod), "Update")]
    static class Patch_FishingRod_Update_P2
    {
        static void Prefix(FishingRod __instance, ref HookP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            if (Main.IsP2MenuOpen || Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen) return;   
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;

            __state = new HookP2State { Ctx = P2FrameContext.Tool(routeInventory: true) };
        }

        static void Postfix(HookP2State __state) => Cleanup(__state);
        static Exception Finalizer(Exception __exception, HookP2State __state) { Cleanup(__state); return __exception; }

        static void Cleanup(HookP2State st)
        {
            if (st == null) return;
            st.Ctx?.Dispose(); st.Ctx = null;
        }
    }

    
    
    
    
    
    
    
    
    [HarmonyPatch(typeof(FishingRod), "SetRopePositionAndLayer")]
    static class Patch_FishingRod_Rope_FP
    {
        static FieldInfo _fRopeMesh, _fThrow, _fRope, _fRopeParts, _fRopeMat;
        
        static readonly System.Collections.Generic.Dictionary<FishingRod, int> _ropeOrigLayer = new System.Collections.Generic.Dictionary<FishingRod, int>();
        static void Resolve()
        {
            if (_fRope != null) return;
            var t = typeof(FishingRod); var bf = BindingFlags.Instance | BindingFlags.NonPublic;
            _fRopeMesh  = t.GetField("ropeMesh", bf);
            _fThrow     = t.GetField("throwable", bf);
            _fRope      = t.GetField("rope", bf);
            _fRopeParts = typeof(Rope).GetField("ropeParts", bf);
            _fRopeMat   = typeof(Rope).GetField("ropeMaterial", bf);
        }

        
        
        
        
        
        
        static void TuneRopeSmr(SkinnedMeshRenderer smr)
        {
            if (smr == null) return;
            smr.updateWhenOffscreen = true;
            smr.forceMatrixRecalculationPerRender = true;
        }
        static void ForceRopeLayer(FishingRod rod, Rope rope, int layer)
        {
            if (layer < 0) return;
            var ropeMesh = _fRopeMesh?.GetValue(rod) as GameObject;
            if (ropeMesh != null)
            {
                Main.SetLayerRecursively(ropeMesh.transform, layer);
                foreach (var smr in ropeMesh.GetComponentsInChildren<SkinnedMeshRenderer>(true)) TuneRopeSmr(smr);
            }
            if (rope != null)
            {
                Main.SetLayerRecursively(rope.transform, layer);
                var parts = _fRopeParts?.GetValue(rope) as Transform[];
                if (parts != null) foreach (var p in parts) if (p != null) Main.SetLayerRecursively(p, layer);
                var mat = _fRopeMat?.GetValue(rope) as Renderer;
                if (mat != null)
                {
                    Main.SetLayerRecursively(mat.transform, layer);
                    TuneRopeSmr(mat as SkinnedMeshRenderer);
                }
                foreach (var smr in rope.GetComponentsInChildren<SkinnedMeshRenderer>(true)) TuneRopeSmr(smr);
            }
            if (rod.bobber != null) Main.SetLayerRecursively(rod.bobber.transform, layer);
        }

        static void Postfix(FishingRod __instance)
        {
            if (Main.player1 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null) return;
            Resolve();

            bool p1fp = np == Main.player1 && np.currentModel != null && np.currentModel.thirdPersonSettings != null
                        && !np.currentModel.thirdPersonSettings.ThirdPersonModel;   
            bool p2fp = np == Main.player2 && Main.p2FirstPerson;                     
            var thr  = _fThrow?.GetValue(__instance) as Throwable;
            var rope = _fRope?.GetValue(__instance) as Rope;
            var list = (np == Main.player2) ? Main.P2FishingExtra : Main.P1FishingExtra;

            
            
            if (thr == null || !thr.InHand || (!p1fp && !p2fp))
            {
                if (_ropeOrigLayer.TryGetValue(__instance, out int orig))
                {
                    ForceRopeLayer(__instance, rope, orig);   
                    list.Clear();                              
                    _ropeOrigLayer.Remove(__instance);
                }
                return;
            }

            
            if (!_ropeOrigLayer.ContainsKey(__instance))
                _ropeOrigLayer[__instance] = __instance.bobber != null ? __instance.bobber.gameObject.layer : 0;

            var lineStart = __instance.fishingLineStart;   
            
            if (rope != null && lineStart != null) rope.SetPosition(0, lineStart);
            
            
            
            int layer = p1fp ? Main.LAYER_P1_HAND : Main.LAYER_P2_HAND;
            ForceRopeLayer(__instance, rope, layer);
            CacheFishing(__instance, rope, list);   
        }

        
        static void CacheFishing(FishingRod rod, Rope rope, System.Collections.Generic.List<Transform> list)
        {
            list.Clear();
            var ropeMesh = _fRopeMesh?.GetValue(rod) as GameObject;
            if (ropeMesh != null) list.Add(ropeMesh.transform);
            if (rope != null)
            {
                list.Add(rope.transform);
                var parts = _fRopeParts?.GetValue(rope) as Transform[];
                if (parts != null) foreach (var p in parts) if (p != null) list.Add(p);
                var mat = _fRopeMat?.GetValue(rope) as Renderer;
                if (mat != null) list.Add(mat.transform);
            }
            if (rod.bobber != null) list.Add(rod.bobber.transform);
        }
    }
}
