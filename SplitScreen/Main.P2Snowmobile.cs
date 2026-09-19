using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        // 雪橇车(Snowmobile)P2 上/下车。仿 Main.P2Seat 的椅子逻辑:
        // - 上车:InteractionRouter.HandleP2Snowmobile 瞄准显示"使用",P2 按 Interact → TakeP2Snowmobile
        // - 下车:每帧 TickP2Snowmobile 检测 P2 按 Interact(与 vanilla 一致)→ 离座
        // 就座期间 runtime Tick 早退(仿椅子),P2 无法用工具/建造;PersonController 被 vanilla 关闭故不会走动。
        // 驾驶(P2 手柄推进/转向)见阶段2 Patches.P2Snowmobile.cs。
        internal static Snowmobile _p2CurrentSnowmobile;
        static int _p2SnowmobileEnterFrame = -1;

        internal static bool P2InSnowmobile => _p2CurrentSnowmobile != null;

        internal static bool SnowmobileHasFree(Snowmobile sm)
        {
            return P2SnowmobileAdapter.HasFreeSeat(sm);
        }

        internal static bool P2IsSeatedInSnowmobile(Snowmobile sm)
        {
            return P2SnowmobileAdapter.IsPlayerSeated(sm, player2);
        }

        internal static bool TakeP2Snowmobile(Snowmobile sm)
        {
            var np2 = player2;
            if (sm == null || np2 == null) return false;

            bool ok = false;
            try
            {
                ok = P2SnowmobileAdapter.TryTakeSeat(sm, np2);
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[P2Snowmobile] take ex: " + e.Message); }

            if (ok)
            {
                _p2CurrentSnowmobile = sm;
                _p2SnowmobileEnterFrame = Time.frameCount;
                ConsumeP2InteractThisFrame();   // 上车这次按键当帧消费,别让下车/其它交互再读到
                HideP2HeldModels();            // 就座隐藏手持模型(手扶车把)
            }
            return ok;
        }

        internal static bool TickP2Snowmobile()
        {
            if (player2 == null) return false;
            var sm = _p2CurrentSnowmobile;
            if (sm == null) return false;

            // 外部原因(死亡/被顶下座)已不在座位 → 收尾
            if (!P2IsSeatedInSnowmobile(sm)) { ClearP2Snowmobile(); return false; }

            LockP2SeatPose();   // 复用椅子姿态锁定,防就座期间 P2 朝向乱飘

            // 下车:按 Interact(与 vanilla 相同),但不在上车那一帧误触发
            if (Time.frameCount != _p2SnowmobileEnterFrame)
            {
                var slot = SplitScreenRuntime.Instance?.P2;
                var interact = slot?.ActionInteract;
                if (interact != null && interact.WasPressedThisFrame())
                {
                    ConsumeP2InteractThisFrame();   // 防 router 同帧把 P2 又塞回座位
                    ClearP2InteractPrompt();
                    try
                    {
                        P2SnowmobileAdapter.LeaveSeat(sm, player2);
                    }
                    catch (System.Exception e) { ModEntry.Logger.Log("[P2Snowmobile] leave ex: " + e.Message); }
                    ClearP2Snowmobile();
                }
            }
            return true;
        }

        static void ClearP2Snowmobile()
        {
            _p2CurrentSnowmobile = null;
            _p2SnowmobileEnterFrame = -1;
            ClearP2InteractPrompt();
            RefreshP2HeldItem();
            P2CameraController.InitFromCurrentCamera();
        }
    }
}
