using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2 塑料钩(收集漂浮物) —— 让 P2 能抛钩、拉钩、采集水里资源。
    //
    //  原版 Hook/Throwable 的 Update 门控 playerNetwork.IsLocalPlayer：本地玩家跑【抛投/拉扯/采集】
    //  分支，非本地玩家跑【远端显示/网络插值】分支。P2 是克隆(isLocalPlayer=false)，故原生只跑远端分支。
    //  且 Throwable 的抛投力用 Camera.main(=P1 相机)、收集物 AddItem 落到共享单例(=P1)。
    //
    //  做法(复用统一代理层 + 专用包裹)：
    //   - Hook.Update / Throwable.Update：单次原生 Update 整段包进 P2OriginalScope.Tool()(强制本地
    //     → 跑本地分支；IsP2OriginalActive → 准星射线/GetMineableItemAtCursor 用 P2 相机并跳过 P2 自身)。
    //     这里【不能】走通用 P2ToolRunner：它们的远端分支不是空 return(有可见副作用/网络插值)，
    //     若再额外调一次会与原生那次冲突；故改为【包裹原生 Update】(等价旧 Axe.Update 专用补丁套路)。
    //   - 抛投相机：Throwable.HandleLocalClient 里 Camera.main → P2HookAim.ThrowCam()(P2 上下文用 P2 相机)。
    //   - 收集路由：Hook.Update 期间换入 P2 背包 + RoutingPickup → 钩到的漂浮物进 P2 背包。
    //   - PlayerItemManager.IsBusy 是全局静态：钩子出手期间 HandleLocalClient 会置真 → 会卡住 P1 用工具。
    //     故在 P2 的 Throwable.Update 前后 save/restore，把 P2 对它的改动限制在本窗口，不泄漏给 P1。
    //   - 输入：LMB(抛/蓄力/拉) → P2 RT(由 CIC "LMB" Tool-scope 路由)；RMB(取消) → P2 LT(由 RMB→ActionContext 路由)。
    // ══════════════════════════════════════════════════════════════════════

    static class P2HookAim
    {
        // 抛投方向相机：P2 原版执行上下文中用 P2 相机；否则原版 Camera.main(P1)。
        public static Camera ThrowCam()
        {
            if (Main.IsP2OriginalActive && Main.player2 != null && Main.player2.Camera != null)
                return Main.player2.Camera;
            return Camera.main;
        }

        // 模拟扳机【模拟量】读取：原版抛投蓄力/拉扯/采集用 action.ReadValue<float>() 读 P1 Fire 轴(P2 操作时=0)。
        //  CIC 补丁只改了布尔 IsPressed/WasPressed，没改 ReadValue → 蓄力恒为 0(没蓄力条/抛不出)。
        //  P2 上下文里改读 P2 手柄右扳机模拟量。
        public static float ReadFire(InputAction a)
        {
            var gp = Main.GetP2BoundGamepad();
            if (Main.IsP2OriginalActive && gp != null)
                return gp.rightTrigger.ReadValue();
            return a != null ? a.ReadValue<float>() : 0f;
        }

        // 移动读取(滑索马达等)：P2 上下文里改读 P2 左摇杆，否则原版动作(P1)。
        //  数字化为 ±1(过死区):滑索马达 vanilla 用 (int)movementInput,模拟量(0.99)会被截断成 0 → 不加速/卡住。
        //  量化后摇杆推到位即 ±1 → 全速;原版键盘 GetAxis 本就是 ±1,行为对齐。
        public static Vector2 ReadMove(InputAction a)
        {
            if (Main.IsP2OriginalActive && Main.p2ActionMove != null)
            {
                var v = Main.p2ActionMove.ReadValue<Vector2>();
                const float dz = 0.5f;
                return new Vector2(Mathf.Abs(v.x) > dz ? Mathf.Sign(v.x) : 0f,
                                   Mathf.Abs(v.y) > dz ? Mathf.Sign(v.y) : 0f);
            }
            return a != null ? a.ReadValue<Vector2>() : Vector2.zero;
        }
    }

    // 把 method 内所有 InputAction.ReadValue<float>() 调用替换为 P2HookAim.ReadFire(该 action)。
    static class HookReadValueTranspiler
    {
        internal static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions)
        {
            var readValF = AccessTools.Method(typeof(InputAction), "ReadValue", Type.EmptyTypes, new[] { typeof(float) });
            var repl     = AccessTools.Method(typeof(P2HookAim), "ReadFire");
            foreach (var ins in instructions)
            {
                if (readValF != null && ins.Calls(readValF)) yield return new CodeInstruction(OpCodes.Call, repl);
                else yield return ins;
            }
        }
    }

    sealed class HookP2State { public P2FrameContext Ctx; }
    sealed class ThrowableP2State { public P2FrameContext Ctx; public PlayerItemBusyScope Busy; }

    [HarmonyPatch(typeof(Hook), "Update")]
    static class Patch_Hook_Update_P2
    {
        static void Prefix(Hook __instance, ref HookP2State __state)
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

    [HarmonyPatch(typeof(Throwable), "Update")]
    static class Patch_Throwable_Update_P2
    {
        static void Prefix(Throwable __instance, ref ThrowableP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            if (Main.IsP2MenuOpen || Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen) return;   
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;
            // P2 的抛投改为【松手即抛】(不依赖抛投动画事件)：原版 thrownByAnimationEvent=true 时，松手只调
            //  Action_OnThrow(重置动画 bool)，真正 Throw() 靠抛投动画帧的事件触发——而 P2 的抛投动画/事件不触发
            //  → 有音效却没抛出、且 HookInHand 被重置致二次无蓄力动画。置 false 后松手直接 Animation_HookThrow()→Throw()
            //  (Throw 内部自行重置动画 bool + chargeMeter)。仅改 P2 自己的 Throwable 实例，P1 不受影响。
            if (__instance.thrownByAnimationEvent) __instance.thrownByAnimationEvent = false;
            __state = new ThrowableP2State { Busy = new PlayerItemBusyScope(), Ctx = P2FrameContext.Tool() };
        }

        static void Postfix(ThrowableP2State __state) => Cleanup(__state);
        static Exception Finalizer(Exception __exception, ThrowableP2State __state) { Cleanup(__state); return __exception; }

        static void Cleanup(ThrowableP2State st)
        {
            if (st == null) return;
            st.Ctx?.Dispose(); st.Ctx = null;
            st.Busy?.Dispose(); st.Busy = null;
        }
    }

    
    
    // 一次性可扔锚:Anchor_Throwable.Update 首行 if(!IsLocalPlayer) return,P2 克隆被挡在外,
    // 世界锚的拾取/收回(HitAtCursor + Interact 键)整段对 P2 从不执行。包进 P2 scope:
    // IsLocalPlayer 强制为真、HitAtCursor 走 AimRay(P2 相机)、Interact 键路由到 p2ActionInteract。
    [HarmonyPatch(typeof(Anchor_Throwable), "Update")]
    static class Patch_Anchor_Throwable_Update_P2
    {
        static void Prefix(Anchor_Throwable __instance, ref ThrowableP2State __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            if (__instance.anchor_stand != null && __instance.GetComponentInParent<Network_Player>() == Main.player2)
                Main.MarkP2AnchorBusy();   // 持有连接锚 → 压制建造工具态(LT 只归锚放回)
            if (Main.IsP2MenuOpen || Main.IsP2BackpackOpen || Main.IsP2BuildMenuOpen) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || np != Main.player2) return;
            __state = new ThrowableP2State { Busy = new PlayerItemBusyScope(), Ctx = P2FrameContext.Tool() };
        }

        // Postfix 仍在 P2 scope 内(Cleanup 前):HitAtCursor 走 P2 相机,读锚状态,
        // 用 P2 提示门面(rt.UI.ShowPrompt/HidePrompt,拾取X/拆除D-Pad Left 均走通用交互提示)驱动,不碰通用 ShowText 路由。
        static void Postfix(Anchor_Throwable __instance, ThrowableP2State __state)
        {
            if (__state != null) DriveAnchorPrompt(__instance);
            Cleanup(__state);
        }
        static Exception Finalizer(Exception __exception, ThrowableP2State __state) { Cleanup(__state); return __exception; }

        static void DriveAnchorPrompt(Anchor_Throwable inst)
        {
            var rt = SplitScreenRuntime.Instance;
            if (rt == null || rt.UI == null) return;
            // 搬着走(已连接 stand):无拾取/拆除提示
            if (inst.anchor_stand != null) { ClearAnchorPrompts(rt); return; }
            if (Helper.HitAtCursor(out var hit, Player.UseDistance, LayerMasks.MASK_Item)
                && hit.transform != null && hit.transform.CompareTag("ThrowableAnchor"))
            {
                var stand = hit.transform.GetComponent<Anchor_Throwable_Stand>();
                if (stand != null && stand.FullyAnchored) { rt.UI.ShowPrompt("Remove", Helper.GetTerm("Game/Remove")); _anchorOwnsInteractPrompt = true; return; }   // D-Pad Left 拆除
                if (stand != null && !stand.IsBusy() && !stand.HasThrownAnchor)
                { rt.UI.ShowPrompt("Interact", Helper.GetTerm("Game/PickUp")); _anchorOwnsInteractPrompt = true; return; }   // X 拾取
            }
            ClearAnchorPrompts(rt);
        }

        // 锚是否当前占用 _p2InteractPrompt(仅在其亲自 ShowPrompt 时置真)。
        // P2 的 Anchor_Throwable 每帧都在跑 Update,旧实现每帧无条件 HidePrompt →
        // 清掉 InteractionRouter 刚为箱子/座位/设备设好的通用提示(「只有锚有提示」病根)。
        static bool _anchorOwnsInteractPrompt;

        // 只清「锚自己点亮的」交互提示,绝不碰其他系统设的通用提示。
        static void ReleaseAnchorInteractPrompt(SplitScreenRuntime rt)
        {
            if (_anchorOwnsInteractPrompt) { rt?.UI?.HidePrompt(); _anchorOwnsInteractPrompt = false; }
        }

        internal static void ClearAnchorPrompts(SplitScreenRuntime rt)
        {
            ReleaseAnchorInteractPrompt(rt);
        }

        static void Cleanup(ThrowableP2State st)
        {
            if (st == null) return;
            st.Ctx?.Dispose(); st.Ctx = null;
            st.Busy?.Dispose(); st.Busy = null;
        }
    }

    // P2 放下/切走可扔锚后,Anchor_Throwable.Update 停跑,最后一次的拾取/拆除提示会残留卡住
    //(上次"只剩锚有提示"的病根)→ 在 OnDisable 主动清掉 P2 的锚提示。
    [HarmonyPatch(typeof(Anchor_Throwable), "OnDisable")]
    static class Patch_Anchor_Throwable_OnDisable_P2
    {
        static void Postfix(Anchor_Throwable __instance)
        {
            if (Main.player2 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np != null && np != Main.player2) return;   // 确系 P1 的锚才不清
            Patch_Anchor_Throwable_Update_P2.ClearAnchorPrompts(SplitScreenRuntime.Instance);
        }
    }

    // 一次性可扔锚的投掷/放回在 Anchor_Throwable_Stand.Update 里读 PlayerInput.GetPlayerByIndex(0)
    // (P1 全局输入)、对每个 stand 都跑且不认连接者 → 分屏下 P1 按键会连带驱动 P2 的 stand
    // (两人同持时 P1 右键把两个都放下、留幽灵锚)。修法:仅当 P2 正持有此 stand 时把它的 Update
    // 包进 P2 scope → IsLocalPlayer 强制真(Disconnect 的复位块对 P2 生效)、CustomInputConfig 的
    // LMB/RMB 经 Patches.Input 路由到 P2 手柄(投掷=RT、放回=Context=LT)。P1 的 stand 不包 scope
    // 仍读 P1,故 P1 输入不再影响 P2 的 stand,两人互不连带。
    sealed class P2StandState { public P2FrameContext Ctx; }

    [HarmonyPatch(typeof(Anchor_Throwable_Stand), "Update")]
    static class Patch_Anchor_Stand_Update_P2
    {
        static void Prefix(Anchor_Throwable_Stand __instance, ref P2StandState __state)
        {
            __state = null;
            if (Main.player2 == null) return;
            var at = Main.player2.GetComponentInChildren<Anchor_Throwable>();
            if (at == null || at.anchor_stand != __instance) return;   // P2 未持有此 stand
            __state = new P2StandState { Ctx = P2FrameContext.Tool() };
        }

        static void Postfix(P2StandState __state) => __state?.Ctx?.Dispose();
        static Exception Finalizer(Exception __exception, P2StandState __state) { __state?.Ctx?.Dispose(); return __exception; }
    }

    [HarmonyPatch(typeof(Throwable), "HandleLocalClient")]
    static class Patch_Throwable_HandleLocalClient_P2
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => TranspilerGuard.Verify(instructions, TranspilerCore);

        static IEnumerable<CodeInstruction> TranspilerCore(IEnumerable<CodeInstruction> instructions)
        {
            var camMain  = AccessTools.PropertyGetter(typeof(Camera), "main");
            var camRepl  = AccessTools.Method(typeof(P2HookAim), "ThrowCam");
            var readValF = AccessTools.Method(typeof(InputAction), "ReadValue", Type.EmptyTypes, new[] { typeof(float) });
            var readRepl = AccessTools.Method(typeof(P2HookAim), "ReadFire");
            foreach (var ins in instructions)
            {
                if (camMain != null && ins.Calls(camMain))        yield return new CodeInstruction(OpCodes.Call, camRepl);
                else if (readValF != null && ins.Calls(readValF)) yield return new CodeInstruction(OpCodes.Call, readRepl);
                else yield return ins;
            }
        }
    }

    // Hook.Update 内的拉扯/采集力 ReadValue<float>() 同样改读 P2 右扳机。
    [HarmonyPatch(typeof(Hook), "Update")]
    static class Patch_Hook_Update_ReadValueP2
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => TranspilerGuard.Verify(instructions, TranspilerCore);

        static IEnumerable<CodeInstruction> TranspilerCore(IEnumerable<CodeInstruction> instructions)
            => HookReadValueTranspiler.Replace(instructions);
    }

    // 海底采集读条累计在私有方法 HandleGathering(gatherTimer += dt * ThrowableAction.ReadValue<float>()),
    // 与 Update 分属两个方法 → 上面只转译了 Update,HandleGathering 的 ReadValue 对 P2 仍取 P1 动作值=0,
    // gatherTimer 永不累计 → 有采集动画(走已路由的 IsPressed)却无读条、无法收集。这里同样把
    // HandleGathering 内的 ReadValue<float>() 转译为 ReadFire(P2 scope 内取 P2 手柄 RT 值)。
    [HarmonyPatch(typeof(Hook), "HandleGathering")]
    static class Patch_Hook_HandleGathering_ReadValueP2
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => TranspilerGuard.Verify(instructions, TranspilerCore);

        static IEnumerable<CodeInstruction> TranspilerCore(IEnumerable<CodeInstruction> instructions)
            => HookReadValueTranspiler.Replace(instructions);
    }

    [HarmonyPatch(typeof(Hook), "Update")]
    static class Patch_Hook_Rope_FP
    {
        static System.Reflection.FieldInfo _fRope, _fConnect, _fHookMesh, _fRopeMesh;
        static readonly Dictionary<Rope, SkinnedMeshRenderer[]> _smrCache = new Dictionary<Rope, SkinnedMeshRenderer[]>();
        static void Resolve()
        {
            if (_fRope != null) return;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            _fRope    = typeof(Hook).GetField("rope", bf);
            _fConnect = typeof(Hook).GetField("ropeConnectTransform", bf);
            _fHookMesh = typeof(Hook).GetField("hookMesh", bf);
            _fRopeMesh = typeof(Hook).GetField("ropeMesh", bf);
        }
        static void TuneSmr(Rope rope)
        {
            if (!_smrCache.TryGetValue(rope, out var smrs))
            {
                smrs = rope.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                _smrCache[rope] = smrs;
            }
            for (int i = 0; i < smrs.Length; i++)
            {
                var s = smrs[i];
                if (s == null) continue;
                s.updateWhenOffscreen = true;
                s.forceMatrixRecalculationPerRender = true;
            }
        }
        static void Postfix(Hook __instance)
        {
            if (Main.player1 == null) return;
            var np = __instance.GetComponentInParent<Network_Player>();
            if (np == null || (np != Main.player1 && np != Main.player2)) return;

            bool fp = np == Main.player1
                ? (np.currentModel != null && np.currentModel.thirdPersonSettings != null && !np.currentModel.thirdPersonSettings.ThirdPersonModel)
                : Main.p2FirstPerson;
            if (!fp) return;

            Resolve();
            if (np == Main.player2 && __instance.throwable != null && __instance.throwable.InHand)
            {
                // The primary P2 model remains on the TP controller so the other
                // player sees correct animation. Vanilla Hook.Update therefore
                // assigns its held meshes to the world layer; restore them for
                // P2's dedicated FP hand camera after the vanilla update.
                var hookMesh = _fHookMesh?.GetValue(__instance) as GameObject;
                var ropeMesh = _fRopeMesh?.GetValue(__instance) as GameObject;
                if (hookMesh != null) hookMesh.SetLayerSafe(Main.LAYER_P2_HAND);
                if (ropeMesh != null) ropeMesh.SetLayerSafe(Main.LAYER_P2_HAND);
            }
            var rope     = _fRope?.GetValue(__instance) as Rope;
            var connectT = _fConnect?.GetValue(__instance) as Transform;
            if (rope == null || connectT == null || !rope.gameObject.activeInHierarchy) return;

            rope.SetPosition(0, connectT);
            TuneSmr(rope);
        }
    }
}
