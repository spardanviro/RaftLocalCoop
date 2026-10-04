using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2ToolRunner —— 统一的「P2 手持工具自驱 Update」运行器(阶段 7)
    //
    //  动机：原版很多工具(Axe/FishingRod/PaintBrush…)在【自己的 MonoBehaviour.Update】
    //   里跑核心逻辑，且首行 `if (!playerNetwork.IsLocalPlayer) return;` 把 P2(克隆,非本地)挡掉。
    //   以前每把工具都要写一份 Patch_XXX_Update_P2(反射强制本地 + 置标志位 + Finalizer 还原)，
    //   重复且易错。本运行器改为【通用】方案：
    //     每帧解析 P2 当前手持工具对象(UseItemController.activeObject.obj)，
    //     对其上【允许集】内的工具组件，在 P2OriginalScope.Tool() 内反射调用其私有 Update()。
    //   scope 内 isLocalPlayer 被强制为 true → 原版 Update 整段以「操作者=P2」跑通；退出还原。
    //
    //  为什么不会双跑：Unity 仍会原生调用 P2 工具的 Update，但那一刻不在 scope 内
    //   (isLocalPlayer=false) → 原版首行直接 return(空操作)；只有本运行器在 scope 内的调用
    //   才真正执行。故每帧恰好执行一次。【前提：该工具不得再有会强制本地的 Patch_XXX_Update_P2，
    //   否则原生那次也会真跑 → 双跑。迁移一把工具到本运行器时必须删除其专用 Update 补丁。】
    //
    //  允许集(s_allowTools)：逐把验证、逐步扩充。先放 Axe(阶段 8 验证)。
    //   仍由专用补丁驱动的工具(Hammer/BlockCreator/RemovePlaceables/BuildComponent)【不要】加入，
    //   以免与其专用 Update 补丁双跑。
    // ══════════════════════════════════════════════════════════════════════
    internal static class P2ToolRunner
    {
        
        
        
        
        
        
        
        
        
        static readonly Type[] s_allowTools = { typeof(Axe), typeof(UsableTool), typeof(PlantComponent) };
        static readonly HashSet<Type> s_denyTools = new HashSet<Type>
        {
            // 例：typeof(SomeUsableToolWithDedicatedUpdatePatch),
            // 望远镜:vanilla Update 用共享 canvas(binocularImage/genericBlackFade)+全局灵敏度 →
            //  串到 P1 屏幕。改由 Main.TickP2Binoculars 用 P2 半屏遮罩 + P2 相机 FOV 完整接管(见 Main.P2Binoculars.cs)。
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
            // P2 自己的背包/建造菜单/通用设备菜单打开时不驱动工具(避免与菜单交互冲突)。
            if (Main.IsP2BackpackOpen || Main.IsP2MenuOpen || Main.IsP2BuildMenuOpen) return;

            var uic = ResolveUic();
            if (uic == null) return;
            EnsureFields();

            var usable = _fUsable?.GetValue(uic) as Item_Base;
            if (usable == null) return;                              // 空手 → 无工具可跑

            var conn = _fActive?.GetValue(uic) as ItemConnection;
            var obj = conn?.obj;
            if (obj == null || !obj.activeInHierarchy) return;       // 手持模型未激活

            // 工具对象刚激活的首帧：跳过，让 Unity 先跑该组件的 Start(否则手动调 Update 时
            //  canvas/playerInventory 等 Start 里初始化的字段还是 null → 首帧 NRE)。下一帧起正常驱动。
            if (obj != _lastToolObj) { _lastToolObj = obj; return; }

            for (int i = 0; i < s_allowTools.Length; i++)
            {
                var comp = obj.GetComponent(s_allowTools[i]) as MonoBehaviour
                           ?? obj.GetComponentInChildren(s_allowTools[i], true) as MonoBehaviour;
                if (comp == null || !comp.isActiveAndEnabled) continue;
                if (s_denyTools.Contains(comp.GetType())) continue;   // 有专用补丁/不适合通用调用 → 跳过(防双跑)
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

            // UsableTool 蓄力工具(铲/采集/捞网…)在【按住使用】期间可能产出物品(OnChannelFinished→PickupItem/AddItem)。
            //  这些 AddItem 落到共享单例(=当前显示 P1 数据)→会进 P1。故按住使用时把单例临时换成 P2 背包 + RoutingPickup，
            //  使产出落进 P2 背包；松开/非蓄力帧不换(省开销、避免每帧换入换出)。Axe 走自己的 OnAxeHit 处理，不在此换。
            bool routeInv = comp is UsableTool && _fHoldingUse != null
                            && (_fHoldingUse.GetValue(comp) as bool?) == true;

            using (P2FrameContext.Tool(routeInventory: routeInv))   // 按住使用时产出落 P2 背包(routeInv);否则仅门控/瞄准
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
            // 向上遍历基类查找 Update(无参)。GetMethod(NonPublic) 不返回【继承的】非公开方法，
            //  而 Shovel 等并未自己声明 Update(在基类 UseableItem/UsableTool 里)，故须逐层 DeclaredOnly 查找。
            //  对虚方法，拿到基类声明的 MethodInfo 后 Invoke 仍按实例的最派生重写虚分发，行为正确。
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
