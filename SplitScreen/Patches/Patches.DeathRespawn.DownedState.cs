using System;
using UnityEngine;

namespace SplitScreen
{
    static partial class SplitScreenDeathFlow
    {
        static bool P1DownedAnchorActive;
        static Transform P1DownedAnchorParent;
        static Vector3 P1DownedAnchorLocalPosition;
        // 锚点只有两种:死在木筏上 -> 锚 lockedPivot 的局部坐标;否则 -> 锚世界坐标。
        static bool P1DownedAnchorOnRaft;
        static Vector3 P1DownedAnchorWorldPosition;
        static Vector3 _p1DownDiagLastPos; static bool _p1DownDiagInit;   // [诊断]满天乱飞

        internal static void PrepareP2DownedVisuals()
        {
            var p2 = Main.player2;
            if (p2 == null) return;

            Main.ForceP2ExitTransientStatesForDowned();

            if (p2.RessurectComponent != null && p2.RessurectComponent.IsCarrying)
                p2.RessurectComponent.StopCarryingPlayer(manipulatePosition: true);
            if (p2.BedComponent != null && p2.BedComponent.Sleeping)
                p2.BedComponent.StopSleep();
            if (p2.ZiplinePlayer != null && p2.ZiplinePlayer.IsAttachedToZipline)
                p2.ZiplinePlayer.DetachFromCurrentZipline(keepMomentum: false);
            P2ZiplineDriver.Reset();
            if (Main.IsP2BackpackOpen || Main.IsP2MenuOpen || Main.p2IsUsingMenu)
            {
                try { Main.ForceResetP2Backpack(); }
                catch (Exception e) { Main.LogV("[DeathFlow] ForceResetP2Backpack during P2 downed ignored: " + e.Message); }
                try { Main.ResetP2Menu(); }
                catch (Exception e) { Main.LogV("[DeathFlow] ResetP2Menu during P2 downed ignored: " + e.Message); }
                Main.p2IsUsingMenu = false;
            }

            Main.p2FirstPerson = true;
            PrepareDownedPlayer(p2.PlayerScript);
            p2.PlayerItemManager?.SelectUsable(null);
            p2.PlayerItemManager?.HideItemInHand();
            if (p2.PersonController != null)
            {
                p2.PersonController.ResetExternalVelocity();
                if (p2.PersonController.waterFloat != null)
                    p2.PersonController.waterFloat.setToSurface = false;
            }
            Main.RestoreP2WorldVisualState();
            SyncP1LocalDeathFlag();
        }

        internal static void PrepareP1DownedVisuals()
        {
            var p1 = Main.player1;
            if (p1 == null) return;

            if (p1.RessurectComponent != null && p1.RessurectComponent.IsCarrying)
                p1.RessurectComponent.StopCarryingPlayer(manipulatePosition: true);
            if (p1.BedComponent != null && p1.BedComponent.Sleeping)
                p1.BedComponent.StopSleep();
            if (p1.ZiplinePlayer != null && p1.ZiplinePlayer.IsAttachedToZipline)
                p1.ZiplinePlayer.DetachFromCurrentZipline(keepMomentum: false);

            PrepareDownedPlayer(p1.PlayerScript);
            p1.PlayerItemManager?.SelectUsable(null);
            p1.PlayerItemManager?.HideItemInHand();

            if (p1.RessurectComponent == null || !p1.RessurectComponent.BeingCarried)
            {
                NormalizeP1DownedVisualRoot();
                CaptureP1DownedAnchor();
            }

            StabilizeP1DownedController();
            SyncP1LocalDeathFlag();
        }

        internal static void RestoreP2AfterDrop()
        {
            var p2 = Main.player2;
            if (p2 == null) return;

            var lockedPivot = SingletonGeneric<GameManager>.Singleton != null
                ? SingletonGeneric<GameManager>.Singleton.lockedPivot
                : null;
            if (lockedPivot != null && p2.transform.parent != lockedPivot)
                p2.transform.SetParent(lockedPivot);

            p2.transform.localScale = Vector3.one;
            if (p2.currentModel != null)
            {
                p2.currentModel.transform.localPosition = Vector3.zero;
                p2.currentModel.transform.localEulerAngles = Vector3.zero;
            }

            if (p2.playerPivot != null)
                p2.playerPivot.localEulerAngles = Vector3.zero;

            Main.RestoreP2WorldVisualState();
            PrepareP2DownedVisuals();
            SyncP1LocalDeathFlag();
        }

        internal static void RestoreP1AfterDrop()
        {
            var p1 = Main.player1;
            if (p1 == null || p1.PlayerScript == null) return;

            var lockedPivot = SingletonGeneric<GameManager>.Singleton != null
                ? SingletonGeneric<GameManager>.Singleton.lockedPivot
                : null;
            if (lockedPivot != null && p1.transform.parent != lockedPivot)
                p1.transform.SetParent(lockedPivot);

            p1.transform.localScale = Vector3.one;
            if (p1.currentModel != null)
            {
                p1.currentModel.transform.localPosition = Vector3.zero;
                p1.currentModel.transform.localEulerAngles = Vector3.zero;
            }

            if (p1.playerPivot != null)
                p1.playerPivot.localEulerAngles = Vector3.zero;

            PrepareP1DownedVisuals();
            SyncP1LocalDeathFlag();
        }

        static Transform RaftPivot()
        {
            return SingletonGeneric<GameManager>.Singleton != null
                ? SingletonGeneric<GameManager>.Singleton.lockedPivot
                : null;
        }

        // P1 此刻是否在木筏上:已挂在 lockedPivot 下,或脚下 2m 内是木筏地面。
        // (原版死亡分支只打 1m,倒地姿态下容易打空,这里放宽一些。)
        static bool IsP1OverRaft(Network_Player p1)
        {
            var pivot = RaftPivot();
            if (pivot == null) return false;
            if (p1.transform.parent == pivot) return true;
            RaycastHit hit;
            return Physics.Raycast(p1.transform.position + Vector3.up * 0.5f, Vector3.down, out hit, 2.5f,
                       LayerMasks.MASK_GroundMask, QueryTriggerInteraction.Ignore)
                   && Helper.ObjectIsOnLayer(hit.transform.gameObject, LayerMasks.MASK_GroundMask_Raft);
        }

        static void CaptureP1DownedAnchor()
        {
            var p1 = Main.player1;
            if (p1 == null) return;

            var pivot = RaftPivot();
            P1DownedAnchorOnRaft = IsP1OverRaft(p1);
            if (P1DownedAnchorOnRaft)
            {
                if (p1.transform.parent != pivot) p1.transform.SetParent(pivot, true);
                P1DownedAnchorParent = pivot;
                P1DownedAnchorLocalPosition = p1.transform.localPosition;
            }
            else
            {
                // 死在水里/岛上:不挂木筏(否则尸体跟着木筏漂、随浮力摆),钉世界坐标。
                P1DownedAnchorParent = null;
                P1DownedAnchorWorldPosition = p1.transform.position;
            }
            P1DownedAnchorActive = true;
        }

        // 原版 PersonController 的死亡分支每帧会在"脚下 1m 没打到木筏"时 SetParent(null)。
        // 旧实现在同一帧用 SetParent(anchor, worldPositionStays:false) 挂回去:false 保持的是 local 值,
        // 上一帧的世界旋转被当成 local 旋转再乘一次父节点旋转,逐帧累积 -> 尸体(和跟着它的相机)满天乱飞。
        // 现在一律 worldPositionStays:true,只钉位置,不碰旋转;父节点只允许 lockedPivot 或 null。
        static void TickP1DownedStabilize()
        {
            var p1 = Main.player1;
            if (p1 == null || p1.PlayerScript == null || !p1.PlayerScript.IsDead) return;
            if (p1.RessurectComponent != null && p1.RessurectComponent.BeingCarried) return;

            if (!P1DownedAnchorActive || (P1DownedAnchorOnRaft && P1DownedAnchorParent == null))
                CaptureP1DownedAnchor();

            if (P1DownedAnchorOnRaft)
            {
                if (p1.transform.parent != P1DownedAnchorParent)
                    p1.transform.SetParent(P1DownedAnchorParent, true);
                p1.transform.localPosition = P1DownedAnchorLocalPosition;
            }
            else
            {
                if (p1.transform.parent != null)
                    p1.transform.SetParent(null, true);
                p1.transform.position = P1DownedAnchorWorldPosition;
            }

            if (Time.frameCount % 10 == 0)   // [诊断] P1死亡满天乱飞,实机确认修好后删
            {
                var pcD = p1.PersonController; var ccD = pcD != null ? pcD.controller : null;
                var posD = p1.transform.position;
                float dP = _p1DownDiagInit ? Vector3.Distance(posD, _p1DownDiagLastPos) : 0f;
                _p1DownDiagLastPos = posD; _p1DownDiagInit = true;
                Main.ModEntry.Logger.Log("[P1DownDiag] f=" + Time.frameCount + " pos=" + posD.ToString("F1")
                    + " d10=" + dP.ToString("F2")
                    + " rot=" + p1.transform.eulerAngles.ToString("F0")
                    + " onRaft=" + P1DownedAnchorOnRaft
                    + " par=" + (p1.transform.parent != null ? p1.transform.parent.name : "null")
                    + " ccEn=" + (ccD != null ? ccD.enabled.ToString() : "-")
                    + " moveFree=" + (pcD != null ? pcD.IsMovementFree.ToString() : "-"));
            }

            NormalizeP1DownedVisualRoot();
            StabilizeP1DownedController();
        }

        static void NormalizeP1DownedVisualRoot()
        {
            var p1 = Main.player1;
            if (p1 == null) return;

            p1.transform.localScale = Vector3.one;
            if (p1.currentModel != null)
            {
                p1.currentModel.transform.localPosition = Vector3.zero;
                p1.currentModel.transform.localEulerAngles = Vector3.zero;
            }

            if (p1.playerPivot != null)
                p1.playerPivot.localEulerAngles = Vector3.zero;
        }

        static void StabilizeP1DownedController()
        {
            var p1 = Main.player1;
            var pc = p1 != null ? p1.PersonController : null;
            if (pc == null) return;

            pc.IsMovementFree = false;
            if (pc.waterFloat != null)
                pc.waterFloat.setToSurface = false;
            pc.ResetExternalVelocity();
            PersonMoveDirectionField?.SetValue(pc, Vector3.zero);
            PersonRecentlyJumpedField?.SetValue(pc, false);
            PersonResetVelocityTimerField?.SetValue(pc, 0f);
            if (pc.controller != null)
                pc.controller.detectCollisions = true;
            pc.SetNetworkPosition(p1.transform.position);
        }
    }
}
