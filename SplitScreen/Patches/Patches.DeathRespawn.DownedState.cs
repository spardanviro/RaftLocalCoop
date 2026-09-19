using System;
using UnityEngine;

namespace SplitScreen
{
    static partial class SplitScreenDeathFlow
    {
        static bool P1DownedAnchorActive;
        static Transform P1DownedAnchorParent;
        static Vector3 P1DownedAnchorLocalPosition;

        internal static void PrepareP2DownedVisuals()
        {
            var p2 = Main.player2;
            if (p2 == null) return;

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
                EnsureP1DownedRaftParent();
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

        static Transform GetP1DownedRaftParent()
        {
            if (Main.player2 != null && Main.player2.transform.parent != null)
                return Main.player2.transform.parent;

            return SingletonGeneric<GameManager>.Singleton != null
                ? SingletonGeneric<GameManager>.Singleton.lockedPivot
                : null;
        }

        static void EnsureP1DownedRaftParent()
        {
            var p1 = Main.player1;
            if (p1 == null) return;
            var raftParent = GetP1DownedRaftParent();
            if (raftParent != null && p1.transform.parent != raftParent)
                p1.transform.SetParent(raftParent, true);
        }

        static void CaptureP1DownedAnchor()
        {
            var p1 = Main.player1;
            if (p1 == null) return;
            EnsureP1DownedRaftParent();
            P1DownedAnchorParent = p1.transform.parent;
            P1DownedAnchorLocalPosition = p1.transform.localPosition;
            P1DownedAnchorActive = P1DownedAnchorParent != null;
        }

        static void TickP1DownedStabilize()
        {
            var p1 = Main.player1;
            if (p1 == null || p1.PlayerScript == null || !p1.PlayerScript.IsDead) return;
            if (p1.RessurectComponent != null && p1.RessurectComponent.BeingCarried) return;

            if (!P1DownedAnchorActive)
                CaptureP1DownedAnchor();

            var desiredParent = GetP1DownedRaftParent();
            if (desiredParent != null && P1DownedAnchorParent != desiredParent)
            {
                p1.transform.SetParent(desiredParent, true);
                CaptureP1DownedAnchor();
            }

            if (P1DownedAnchorActive && p1.transform.parent != P1DownedAnchorParent && P1DownedAnchorParent != null)
                p1.transform.SetParent(P1DownedAnchorParent, false);

            if (P1DownedAnchorActive && p1.transform.parent == P1DownedAnchorParent)
                p1.transform.localPosition = P1DownedAnchorLocalPosition;

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
