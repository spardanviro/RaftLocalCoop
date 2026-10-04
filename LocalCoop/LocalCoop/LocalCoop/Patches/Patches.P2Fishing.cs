using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    // ══════════════════════════════════════════════════════════════════════
    //  P2 钓鱼竿 —— 与塑料钩同构(都用 Throwable)。
    //   - 抛投/物理走 Throwable(已由 Patch_Throwable_Update_P2 包 scope + ThrowCam + thrownByAnimationEvent=false)。
    //   - FishingRod.Update 门控 IsLocalPlayer → 包进 P2OriginalScope.Tool() 跑本地分支(抛竿/咬钩/收线)。
    //   - 钓上的鱼/物 PullItemsFromSea→AddItem：换入 P2 背包 + RoutingPickup → 进 P2 背包。
    //   - 鱼饵菜单 MenuType.FishingBait：靠 OpenMenu 门控扩展(IsP2OriginalActive)落 P2 半屏(B 关)。
    //   - 输入：LMB(抛/收)→RT、RMB(开饵菜单)→P2 LT(ActionContext)，均已路由。
    //   - 菜单/背包打开时不进 scope(不强制本地)→ FishingRod.Update 走非本地分支(仅绳索)，避免每帧重开菜单。
    // ══════════════════════════════════════════════════════════════════════
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

    // ══════════════════════════════════════════════════════════════════════
    //  第一人称鱼线修复(P1 + P2) —— 让 鱼竿+鱼线+鱼钩 全在【同一相机】用【原始世界坐标】渲染(=第三人称的做法，TP 正常)
    //   FP 下出问题的根因：原版把 鱼竿/ropeMesh/浮标 搬到手部相机(FP 叠加)，但真正的线(Rope 的 ropeParts/ropeMaterial)
    //   留在主相机世界空间，再用 Helper.WorldPointToFOVPointLocal(屏幕投影 FOV 偏移)把线起点"桥接"到 FP 叠加的竿尖。
    //   分屏下各相机是半屏 viewport → 该屏幕换算出错 → 线与竿/钩脱离(P1)；且换算用的是 P1 相机，P2 不适用 → 线不显示(P2)。
    //   解决：FP 持竿时，把整条线的所有渲染器(ropeMesh + Rope.ropeParts + Rope.ropeMaterial + bobber)强制搬到
    //   "鱼竿所在的 FP 层"(P1=HandCamera 层 24；P2=LAYER_P2_HAND)，并去掉 FOV 偏移(线起点贴原始竿尖) → 全在一相机+原始世界 → 连在一起。
    // ══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(FishingRod), "SetRopePositionAndLayer")]
    static class Patch_FishingRod_Rope_FP
    {
        static FieldInfo _fRopeMesh, _fThrow, _fRope, _fRopeParts, _fRopeMat;
        // 持竿 FP 首帧记录钩/线的原始(世界)层 → 抛出/收回/切 TP 时据此还原，让钩落水后回世界空间正常渲染(有深度、被遮挡)。
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

        // 把整条鱼线相关的所有渲染对象搬到 layer(覆盖 ropeMesh + Rope 根 + 各线段 ropeParts + ropeMaterial + 浮标)。
        //  鱼线 model 是【蒙皮网格】(骨骼=Bottom/Top 两端，绑到竿尖/浮标)。两项关键设置：
        //   - updateWhenOffscreen=true → 每帧按当前骨骼重算包围盒，避免离轴相机(P2.HandCamera)误判视锥外不渲染。
        //   - forceMatrixRecalculationPerRender=true → 蒙皮默认每帧只算一次(在逐相机渲染之前)；而我们在 OnCameraPreCull 里
        //     对 P1 相机【逐相机】弯腰移动骨骼，竿/钩(普通网格)立刻跟随、但蒙皮线用的是弯腰前的旧蒙皮 → 线脱节("不绕同一中心")。
        //     开此项后每次渲染都重算蒙皮 → 线随当前骨骼(弯腰后)正确变形、贴住竿与钩。
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
                        && !np.currentModel.thirdPersonSettings.ThirdPersonModel;   // P1 真·FP 模型
            bool p2fp = np == Main.player2 && Main.p2FirstPerson;                     // P2 自定义 FP
            var thr  = _fThrow?.GetValue(__instance) as Throwable;
            var rope = _fRope?.GetValue(__instance) as Rope;
            var list = (np == Main.player2) ? Main.P2FishingExtra : Main.P1FishingExtra;

            // 抛出(钩飞向水面) / 收回 / 切第三人称 → 还原钩+线到原始世界层 + 清逐相机切层列表，
            //  否则 SetP2ToolLayer 会每帧把钩切到 FP 叠加层(P2.HandCamera) → 钩浮空、最上层、无世界深度。
            if (thr == null || !thr.InHand || (!p1fp && !p2fp))
            {
                if (_ropeOrigLayer.TryGetValue(__instance, out int orig))
                {
                    ForceRopeLayer(__instance, rope, orig);   // 钩/线搬回原始世界层 → 落水正常渲染
                    list.Clear();                              // 停止逐相机切到 FP 层
                    _ropeOrigLayer.Remove(__instance);
                }
                return;
            }

            // 持竿 FP：首帧记录原始(世界)层，供抛出后还原。
            if (!_ropeOrigLayer.ContainsKey(__instance))
                _ropeOrigLayer[__instance] = __instance.bobber != null ? __instance.bobber.gameObject.layer : 0;

            var lineStart = __instance.fishingLineStart;   // public 字段：之前用 NonPublic 反射取到 null → 偏移没被覆盖(P1 线脱离竿)
            // FP：去掉分屏下算错的 FOV 偏移(线起点贴原始竿尖) + 整条线搬到鱼竿所在 FP 层 → 全在一相机+原始世界 → 连在一起。
            if (rope != null && lineStart != null) rope.SetPosition(0, lineStart);
            // 镜像 P2 的成功做法(对称)：鱼线放在【该玩家自己的 FP 专属层】(P1=LAYER_P1_HAND / P2=LAYER_P2_HAND) →
            //  本人的手部相机渲染；再【逐相机搬到对方主相机可见的"世界层"】给对方看(不要放共享的 HandCamera 叠加层 24，
            //  否则蒙皮线经叠加层被对方主相机渲染会错位)。P1 用 LAYER_P1_HAND；跨视图搬到 P1 身体的 LocalPlayer 层(由 OnCameraPreCull 处理)。
            int layer = p1fp ? Main.LAYER_P1_HAND : Main.LAYER_P2_HAND;
            ForceRopeLayer(__instance, rope, layer);
            CacheFishing(__instance, rope, list);   // 收集鱼线变换供逐相机切层
        }

        // 收集鱼线相关的所有变换到 list → 交给 OnCameraPreCull 逐相机切层(对方主相机=世界层可见 / 本人手部相机=FP 专属层)。
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
