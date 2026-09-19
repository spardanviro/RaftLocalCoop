using UnityEngine;

namespace SplitScreen
{
    static partial class SplitScreenDeathFlow
    {
        internal static void TickDeathMenuGate()
        {
            if (!SplitActive) return;

            if (Main.player1?.PlayerScript != null && Main.player1.PlayerScript.IsDead)
            {
                PrepareDownedPlayer(Main.player1.PlayerScript);
                TickP1DownedStabilize();
            }
            if (Main.player2?.PlayerScript != null && Main.player2.PlayerScript.IsDead)
                PrepareDownedPlayer(Main.player2.PlayerScript);

            TickP1CarryViewInput();

            if (CanvasHelper.ActiveMenu == MenuType.DeathMenu && !BothPlayersDead)
            {
                var canvas = ComponentManager<CanvasHelper>.Value;
                if (canvas != null)
                    canvas.CloseAllMenus();
                SyncP1LocalDeathFlag();
                Main.LogV("[DeathFlow] Closed stale DeathMenu because only one split player is downed");
            }

            SyncP1LocalDeathFlag();
        }

        static void TickP1CarryViewInput()
        {
            var p1 = Main.player1;
            if (p1 == null || p1.RessurectComponent == null || !p1.RessurectComponent.IsCarrying || p1.RessurectComponent.CarriedPlayer != Main.player2)
            {
                P1CarryViewWasHeld = false;
                return;
            }

            if (CanvasHelper.ActiveMenu != MenuType.None) return;
            CacheP1DownedInputActions();
            if (P1ViewAction == null) return;

            bool viewHeld = IsP1DownedViewHeld();
            bool viewPressed = SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.WasPressedThisFrame(P1ViewAction, "Thirdperson");
            if (!viewPressed)
                viewPressed = viewHeld && !P1CarryViewWasHeld;
            P1CarryViewWasHeld = viewHeld;
            if (!viewPressed) return;

            var tp = p1.currentModel != null ? p1.currentModel.thirdPersonSettings : p1.GetComponentInChildren<ThirdPerson>();
            if (tp == null) return;

            bool next = !tp.ThirdPersonState;
            tp.ForceThirdPersonState(next);
            if (next) InitP1DownedOrbit();
            Main.LogV("[DeathFlow] P1 carry P2 forced view switch thirdPerson=" + next);
        }
    }
}
