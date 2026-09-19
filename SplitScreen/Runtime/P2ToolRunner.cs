using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SplitScreen
{
    
    
    //
    
    
    
    
    
    
    
    //
    
    
    
    
    //
    
    
    
    
    internal static class P2ToolRunner
    {
        
        
        
        
        
        
        
        
        
        static readonly Type[] s_allowTools = { typeof(Axe), typeof(UsableTool), typeof(PlantComponent) };
        static readonly HashSet<Type> s_denyTools = new HashSet<Type>
        {
            
            
            
            typeof(Binoculars),
        };

        static UseItemController _uic;
        static FieldInfo _fUsable, _fActive, _fHoldingUse, _fPlantManager;
        static readonly Dictionary<Type, MethodInfo> _updateCache = new Dictionary<Type, MethodInfo>();
        static bool _loggedError;
        static GameObject _lastToolObj;
        static FieldInfo _fPlantPrefab;
        static MethodInfo _miGetCropplot;   

        internal static void Reset()
        {
            _uic = null;
            _loggedError = false;
            _lastToolObj = null;
        }

        internal static void Tick()
        {
            if (Main.player2 == null) return;
            
            if (Main.IsP2BackpackOpen || Main.IsP2MenuOpen || Main.IsP2BuildMenuOpen) return;

            var uic = ResolveUic();
            if (uic == null) return;
            EnsureFields();

            var usable = _fUsable?.GetValue(uic) as Item_Base;
            if (usable == null) return;                              

            var conn = _fActive?.GetValue(uic) as ItemConnection;
            var obj = conn?.obj;
            if (obj == null || !obj.activeInHierarchy) return;       

            
            
            if (obj != _lastToolObj) { _lastToolObj = obj; return; }

            for (int i = 0; i < s_allowTools.Length; i++)
            {
                var comp = obj.GetComponent(s_allowTools[i]) as MonoBehaviour
                           ?? obj.GetComponentInChildren(s_allowTools[i], true) as MonoBehaviour;
                if (comp == null || !comp.isActiveAndEnabled) continue;
                if (s_denyTools.Contains(comp.GetType())) continue;   
                InvokeUpdate(comp);
            }
        }

        static UseItemController ResolveUic()
        {
            if (_uic != null) return _uic;
            if (Main.player2 == null) return null;
            _uic = Main.player2.GetComponentInChildren<UseItemController>(true);
            return _uic;
        }

        static void EnsureFields()
        {
            if (_fUsable != null) return;
            var t = typeof(UseItemController);
            _fUsable = t.GetField("usableItem", BindingFlags.Instance | BindingFlags.NonPublic);
            _fActive = t.GetField("activeObject", BindingFlags.Instance | BindingFlags.NonPublic);
            _fHoldingUse = typeof(UsableTool).GetField("isHoldingUseButton", BindingFlags.Instance | BindingFlags.NonPublic);
            _fPlantManager = typeof(PlantComponent).GetField("plantManager", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        static void InvokeUpdate(MonoBehaviour comp)
        {
            var mi = GetUpdateMethod(comp.GetType());
            if (mi == null) return;

            if (comp is PlantComponent && Main.player2?.PlantManager != null)
                _fPlantManager?.SetValue(comp, Main.player2.PlantManager);

            
            
            
            bool routeInv = comp is UsableTool && _fHoldingUse != null
                            && (_fHoldingUse.GetValue(comp) as bool?) == true;

            using (P2FrameContext.Tool(routeInventory: routeInv))   
            {
                try { mi.Invoke(comp, null); if (comp is PlantComponent pc) TryShowP2PlantPrompt(pc); }
                catch (Exception e)
                {
                    if (!_loggedError)
                    {
                        _loggedError = true;
                        var real = e.InnerException ?? e;
                        Main.ModEntry.Logger.Log($"[P2ToolRunner] {comp.GetType().Name}.Update 异常: {real.Message}\n{real.StackTrace}");
                    }
                }
            }
        }

        static MethodInfo GetUpdateMethod(Type t)
        {
            if (_updateCache.TryGetValue(t, out var mi)) return mi;
            
            
            
            mi = null;
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(object); cur = cur.BaseType)
            {
                mi = cur.GetMethod("Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (mi != null) break;
            }
            _updateCache[t] = mi;
            return mi;
        }

        static void TryShowP2PlantPrompt(PlantComponent pc)
        {
            if (Main.GlobalBlocksP2) return;
            if (_fPlantPrefab == null)
                _fPlantPrefab = typeof(PlantComponent).GetField("plantPrefab", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_miGetCropplot == null)
                _miGetCropplot = typeof(PlantComponent).GetMethod("GetCropplot", BindingFlags.Instance | BindingFlags.NonPublic);

            var plantPrefab = _fPlantPrefab?.GetValue(pc) as Plant;
            if (plantPrefab == null || plantPrefab.item == null) return;
            var cropplot = _miGetCropplot?.Invoke(pc, null) as Cropplot;
            if (cropplot == null || cropplot.IsFull || !cropplot.AcceptsPlantType(plantPrefab.item)) return;

            LocalizationParameters.itemX = plantPrefab.item.settings_Inventory.DisplayName;
            Main.CaptureDevicePrompt(Helper.GetTerm("Game/PlantItemX", applyParameters: true));
        }
    }
}
