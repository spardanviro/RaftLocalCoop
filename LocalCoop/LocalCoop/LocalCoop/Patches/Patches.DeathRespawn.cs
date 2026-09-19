using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    static partial class SplitScreenDeathFlow
    {
        static readonly int RemotePlayerLayer = LayerMask.NameToLayer("RemotePlayer");
        static readonly FieldInfo PersonMoveDirectionField = AccessTools.Field(typeof(PersonController), "moveDirection");
        static readonly FieldInfo PersonRecentlyJumpedField = AccessTools.Field(typeof(PersonController), "recentlyJumped");
        static readonly FieldInfo PersonResetVelocityTimerField = AccessTools.Field(typeof(PersonController), "resetVelocityOnGroundedTimer");
        static readonly FieldInfo PersonOriginalControllerHeightField = AccessTools.Field(typeof(PersonController), "originalControllerColliderHeight");
        static readonly SplitScreenDeathState DeathState = new SplitScreenDeathState();

        internal static bool SplitActive => Main.player1 != null && Main.player2 != null;

        internal static bool BothPlayersDead =>
            Main.player1?.PlayerScript != null && Main.player1.PlayerScript.IsDead &&
            Main.player2?.PlayerScript != null && Main.player2.PlayerScript.IsDead;

        internal static bool ShouldSuppressDeathMenu(MenuType menuType) =>
            SplitActive && menuType == MenuType.DeathMenu && !BothPlayersDead;

        internal static void SyncP1LocalDeathFlag()
        {
            DeathState.SyncVanillaLocalDeathFlag(BothPlayersDead);
        }

        internal static bool P1CarryViewWasHeld
        {
            get => DeathState.P1CarryViewWasHeld;
            set => DeathState.P1CarryViewWasHeld = value;
        }

        internal static void PrepareDownedPlayer(Player player)
        {
            if (player == null) return;

            if (player.incapacitatedCollider != null)
            {
                player.incapacitatedCollider.enabled = true;
                if (RemotePlayerLayer >= 0)
                    player.incapacitatedCollider.gameObject.layer = RemotePlayerLayer;
            }
        }

        internal static void HideP2DeathOverlay()
        {
            if (Main.p2DeathText != null && Main.p2DeathText.gameObject.activeSelf)
                Main.p2DeathText.gameObject.SetActive(false);

            Main.p2DeathHandled = false;
            Main.p2DeathTimer = 0f;
        }
    }
}
