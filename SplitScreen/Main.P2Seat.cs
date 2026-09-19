using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    public static partial class Main
    {
        
        
        //
        
        
        
        //
        
        
        
        
        
        
        
        

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
                foreach (var a in Object.FindObjectsOfType<AttachPlayer>())
                    if (a != null && a.carriedPlayer == player) { found = a; break; }

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

        
        internal static bool SeatHasFree(PlayerSeat seat)
        {
            return P2FurnitureAdapter.SeatHasFree(seat);
        }

        
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

        
        internal static bool TickP2Seat()
        {
            if (player2 == null) return false;
            var seat = _p2CurrentSeat;
            if (seat == null) return false;

            
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

        static void ClearP2Seat()
        {
            CloseP2PianoUi();
            _p2CurrentSeat = null;
            ClearP2InteractPrompt();
            RefreshP2HeldItem();          
            P2CameraController.InitFromCurrentCamera();  
        }

    }
}
