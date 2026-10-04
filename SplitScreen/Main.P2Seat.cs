using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        // ══════════════════════════════════════════════════════════════════
        //  P2 椅子(PlayerSeat)落座/起身 —— 修复"P2 与椅子交互却让 P1 坐上去"。
        //
        //  根因:PlayerSeat 用【静态】localPlayer(=ComponentManager<Network_Player>.Value=P1),
        //   OnIsRayed 的门控/发占座消息/TryTakingSeat 全用它 → P2 瞄椅子按 X,坐的是 P1(被传送+站姿重叠)。
        //   通用设备路由 RunDeviceRayAsP2 的 localPlayer 覆盖只认【实例】字段,static 覆盖不到。
        //
        //  做法(对齐床 Main.P2Bed):不跑 vanilla OnIsRayed,由 mod 专门驱动——
        //   · 占座:InteractionRouter 命中空椅 → [X] 提示 → TakeP2Seat() 在 P2OriginalScope.Interaction()
        //     内反射调私有 TryTakingSeat(P2)。scope 强制 P2 isLocalPlayer=true,使 AttachPlayer/PlayerSeat
        //     的 IsLocalPlayer 块作用于 P2(座姿/相机/mouselook/canLeave)。host(P1)本机直跑。
        //   · 起身:每帧 TickP2Seat 监听 P2 自己的 X → P2OriginalScope.Interaction() 内反射调 LeaveSeat(P2)。
        //   · AttachPlayer 对任何玩家都 PersonController.enabled=false → P2 落座移动天然冻结。
        //   · AttachPlayer/PlayerSeat 内 PlayerItemManager.IsBusy(全局静态)会泄漏给 P1 → 前后 save/restore。
        // ══════════════════════════════════════════════════════════════════

        internal static PlayerSeat _p2CurrentSeat;
        internal static bool P2IsSeated => _p2CurrentSeat != null;
        internal static bool P2IsInFreeLookSeat => IsFreeLookSeat(_p2CurrentSeat);
        internal static bool P2IsInBathtub => IsBathtubSeat(_p2CurrentSeat);

        
        
        internal static bool P2IsSeatedIn(PlayerSeat seat)
        {
            var np2 = player2;
            if (seat == null || np2 == null) return false;
            var seats = seat.Seats;
            if (seats != null)
                foreach (var a in seats)
                    if (a != null && a.carriedPlayer == np2) return true;
            return false;
        }

        internal static bool IsBathtubSeat(PlayerSeat seat)
        {
            var seats = seat?.Seats;
            if (seats == null) return false;
            foreach (var a in seats)
                if (a != null && a.fullBodyAnimation == PlayerFullBodyAnimation.Bathtub_15)
                    return true;
            return false;
        }

        internal static bool IsFreeLookSeat(PlayerSeat seat)
        {
            var seats = seat?.Seats;
            if (seats == null) return false;
            foreach (var a in seats)
                if (IsFreeLookAttach(a))
                    return true;
            return false;
        }

        internal static bool IsBathtubAttach(AttachPlayer attach)
        {
            return attach != null && attach.fullBodyAnimation == PlayerFullBodyAnimation.Bathtub_15;
        }

        internal static bool IsFreeLookAttach(AttachPlayer attach)
        {
            if (attach == null) return false;
            if (attach.thirdPersonParent == null) return false;
            switch (attach.fullBodyAnimation)
            {
                case PlayerFullBodyAnimation.Bathtub_15:
                case PlayerFullBodyAnimation.Sitting_1:
                case PlayerFullBodyAnimation.SittingArmchair_11:
                case PlayerFullBodyAnimation.SittingSofa_12:
                case PlayerFullBodyAnimation.SittingStoolHigh_13:
                case PlayerFullBodyAnimation.SittingStoolLow_14:
                case PlayerFullBodyAnimation.SittingSofaCorner_16:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsPlayerInBathtub(Network_Player player)
        {
            return FindBathtubAttachForPlayer(player) != null;
        }

        internal static bool IsPlayerInFreeLookSeat(Network_Player player)
        {
            return FindFreeLookAttachForPlayer(player) != null;
        }

        internal static AttachPlayer FindBathtubAttachForPlayer(Network_Player player)
        {
            if (player == null) return null;
            foreach (var attach in Object.FindObjectsOfType<AttachPlayer>())
                if (IsBathtubAttach(attach) && attach.carriedPlayer == player)
                    return attach;
            return null;
        }

        // 找到当前正搬运/承载该玩家的 AttachPlayer(座位)。先沿父链上溯(玩家被挂在座位锚点下,
        // AttachPlayer 在其祖先上),失败再全场扫描兜底。按帧缓存,避免每帧 FindObjectsOfType。
        static AttachPlayer _attachCache;
        static Network_Player _attachCachePlayer;
        static int _attachCacheFrame = -1;
        // 父链找不到时的兜底:座位自己在 AttachPlayer.Update 的 Postfix 里登记"我正承载谁",
        // 这里只需验证登记是否仍成立。全场景扫描降级为每个玩家最多 30 帧一次的保险。
        static readonly System.Collections.Generic.Dictionary<Network_Player, AttachPlayer> _attachLast =
            new System.Collections.Generic.Dictionary<Network_Player, AttachPlayer>();
        static readonly System.Collections.Generic.Dictionary<Network_Player, int> _attachScanFrame =
            new System.Collections.Generic.Dictionary<Network_Player, int>();

        internal static void NoteAttachCarrying(AttachPlayer attach)
        {
            var who = attach != null ? attach.carriedPlayer : null;
            if (who != null) _attachLast[who] = attach;
        }

        // 该座位是否是雪橇车的驾驶位(且坐的正是该玩家)。
        // ⚠ 有意偏离原版的判据:原版驾驶位也允许自由环视(disableMouseLook=false),
        // 用户要求驾驶时锁定视角,故单独认出驾驶位。乘客位不受影响。
        internal static bool IsSnowmobileDriverSeat(AttachPlayer attach, Network_Player player)
        {
            if (attach == null || player == null) return false;
            var sm = attach.GetComponentInParent<Snowmobile>();
            return sm != null && sm.DrivingPlayer == player;
        }

        internal static AttachPlayer FindAttachCarrying(Network_Player player)
        {
            if (player == null) return null;
            if (_attachCacheFrame == Time.frameCount && _attachCachePlayer == player) return _attachCache;

            AttachPlayer found = null;
            var t = player.transform != null ? player.transform.parent : null;
            if (t != null)
            {
                var a = t.GetComponentInParent<AttachPlayer>();
                if (a != null && a.carriedPlayer == player) found = a;
            }
            if (found == null)
            {
                AttachPlayer last;
                if (_attachLast.TryGetValue(player, out last) && last != null && last.carriedPlayer == player)
                    found = last;
                else
                {
                    int lastScan;
                    if (!_attachScanFrame.TryGetValue(player, out lastScan) || Time.frameCount - lastScan >= 30)
                    {
                        _attachScanFrame[player] = Time.frameCount;
                        foreach (var a in Object.FindObjectsOfType<AttachPlayer>())
                            if (a != null && a.carriedPlayer == player) { found = a; break; }
                    }
                }
            }
            _attachLast[player] = found;

            _attachCacheFrame = Time.frameCount;
            _attachCachePlayer = player;
            _attachCache = found;
            return found;
        }

        internal static AttachPlayer FindFreeLookAttachForPlayer(Network_Player player)
        {
            if (player == null) return null;
            foreach (var attach in Object.FindObjectsOfType<AttachPlayer>())
                if (IsFreeLookAttach(attach) && attach.carriedPlayer == player)
                    return attach;
            return null;
        }

        // 椅子是否有空座。
        internal static bool SeatHasFree(PlayerSeat seat)
        {
            return P2FurnitureAdapter.SeatHasFree(seat);
        }

        // 占座(由 InteractionRouter 命中空椅且 P2 按 X 时调)。返回 true=已坐下。
        internal static bool TakeP2Seat(PlayerSeat seat)
        {
            var np2 = player2;
            if (seat == null || np2 == null) return false;

            bool ok = false;
            try
            {
                ok = P2FurnitureAdapter.TryTakeSeat(seat, np2);
            }
            catch (System.Exception e) { ModEntry.Logger.Log("[P2Seat] take ex: " + e.Message); }

            if (ok)
            {
                _p2CurrentSeat = seat;
                if (IsBathtubSeat(seat))
                    p2FirstPerson = true;
                HideP2HeldModels();   
            }
            return ok;
        }

        // 每帧:P2 坐着 → 准心上方「X 起身」提示 + 监听起身。返回 true 表示正坐着(调用方暂停其余 P2 交互)。
        internal static bool TickP2Seat()
        {
            if (player2 == null) return false;
            var seat = _p2CurrentSeat;
            if (seat == null) return false;

            // 被异常脱离(死亡/椅子销毁/被他者移走)→ 清状态 + 恢复手持/视角。
            if (!P2IsSeatedIn(seat)) { ClearP2Seat(); return false; }
            LockP2SeatPose();

            
            
            var slot = SplitScreenRuntime.Instance?.P2;
            var cancel = slot?.ActionCancel;
            bool leavePressed = cancel != null && cancel.WasPressedThisFrame();
            if (leavePressed)
            {
                SuppressP2CrouchOnExit();
                ClearP2InteractPrompt();
                try
                {
                    P2FurnitureAdapter.LeaveSeat(seat, player2);
                }
                catch (System.Exception e) { ModEntry.Logger.Log("[P2Seat] leave ex: " + e.Message); }
                ClearP2Seat();
            }
            return true;
        }

        internal static void LockP2SeatPose()
        {
            var p2 = player2;
            if (p2 == null) return;
            p2.transform.localEulerAngles = Vector3.zero;
            if (p2.playerPivot != null) p2.playerPivot.localEulerAngles = Vector3.zero;
        }

        // P2 倒地时调用。原版只对"本地玩家"在死亡时做这些收尾(Carry.Update / AttachPlayer 的
        // IsLocalPlayer 分支),P2 平时是非本地克隆,全被跳过 -> 复活后残留:不能蹲、TP 身体不跟视角、
        // 动物还挂在身上、望远镜遮罩留在倒地画面上。这里以 P2 作用域把它们正常退出一遍。
        // 不调 ClearP2Seat/ClearP2Snowmobile:它们会 RefreshP2HeldItem,倒地时不该把手持模型亮出来。
        internal static void ForceP2ExitTransientStatesForDowned()
        {
            var p2 = player2;
            if (p2 == null) return;

            var carried = P2CarriedObject;
            if (carried != null)
            {
                try
                {
                    using (new PlayerItemBusyScope())
                    using (P2OriginalScope.Interaction())
                        carried.OnStopCarry?.Invoke(p2, true);   // 与原版 Carry.Update 的死亡分支同参
                }
                catch (System.Exception e) { ModEntry.Logger.Log("[P2Downed] 放下搬运物异常: " + e.Message); }
            }

            if (_p2CurrentSeat != null)
            {
                try { P2FurnitureAdapter.LeaveSeat(_p2CurrentSeat, p2); }
                catch (System.Exception e) { ModEntry.Logger.Log("[P2Downed] 离座异常: " + e.Message); }
                CloseP2PianoUi();
                _p2CurrentSeat = null;
                ClearP2InteractPrompt();
            }

            if (_p2CurrentSnowmobile != null)
            {
                try { P2SnowmobileAdapter.LeaveSeat(_p2CurrentSnowmobile, p2); }
                catch (System.Exception e) { ModEntry.Logger.Log("[P2Downed] 下车异常: " + e.Message); }
                _p2CurrentSnowmobile = null;
                _p2SnowmobileEnterFrame = -1;
                ClearP2InteractPrompt();
            }

            // 兜底:座位若已被原版以"非本地"身份脱离,IsAttached 不会被复位。
            if (p2.PlayerNetworkManager != null) p2.PlayerNetworkManager.IsAttached = false;

            ForceP2BinocOff();
            ForceResetP2BuildMenu();
            SplitScreenRuntime.Instance?.UI.CloseUnstuckMenu();
        }

        static void ClearP2Seat()
        {
            CloseP2PianoUi();
            _p2CurrentSeat = null;
            ClearP2InteractPrompt();
            RefreshP2HeldItem();          // 重装 P2 手持模型(对齐起床)
            P2CameraController.InitFromCurrentCamera();  
        }
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
    }
}
