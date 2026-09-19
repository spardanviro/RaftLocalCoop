using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    [HarmonyPatch(typeof(CanvasHelper), "OpenMenu")]
    static class Patch_DeathMenu_Open_SplitScreenGate
    {
        static bool Prefix(MenuType menuType, ref bool __result)
        {
            if (!SplitScreenDeathFlow.ShouldSuppressDeathMenu(menuType)) return true;

            __result = true;
            Main.LogV("[DeathFlow] Suppressed DeathMenu until both players are downed");
            return false;
        }
    }

    [HarmonyPatch(typeof(CanvasHelper), "OpenMenuCloseOther")]
    static class Patch_DeathMenu_OpenCloseOther_SplitScreenGate
    {
        static bool Prefix(CanvasHelper __instance, MenuType menuType, ref bool __result)
        {
            if (!SplitScreenDeathFlow.ShouldSuppressDeathMenu(menuType)) return true;

            __instance.CloseAllMenus();
            __result = true;
            Main.LogV("[DeathFlow] Suppressed DeathMenuCloseOther until both players are downed");
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Kill))]
    static class Patch_Player_Kill_SplitScreenDeath
    {
        static void Postfix(Player __instance)
        {
            if (!SplitScreenDeathFlow.SplitActive || __instance == null) return;

            if (__instance == Main.player2?.PlayerScript)
            {
                SplitScreenDeathFlow.PrepareP2DownedVisuals();
                Main.ModEntry.Logger.Log("[DeathFlow] P2 downed and exposed as carry target");
            }
            else if (__instance == Main.player1?.PlayerScript)
            {
                Main.ForceCleanupP1NotebookVisuals(restoreController: false);
                SplitScreenDeathFlow.PrepareP1DownedVisuals();
                Main.ModEntry.Logger.Log("[DeathFlow] P1 downed; P2 can rescue");
            }
            else
            {
                SplitScreenDeathFlow.PrepareDownedPlayer(__instance);
            }

            if (SplitScreenDeathFlow.BothPlayersDead)
            {
                var canvas = ComponentManager<CanvasHelper>.Value;
                if (canvas != null && CanvasHelper.ActiveMenu != MenuType.DeathMenu)
                {
                    Main.ModEntry.Logger.Log("[DeathFlow] Both players downed; opening vanilla DeathMenu");
                    canvas.OpenMenuCloseOther(MenuType.DeathMenu, force: true);
                }
            }
            else
            {
                SplitScreenDeathFlow.SyncP1LocalDeathFlag();
            }
        }
    }

    [HarmonyPatch(typeof(RessurectComponent), nameof(RessurectComponent.StartCarryingPlayer))]
    static class Patch_RessurectComponent_StartCarry_P2WorldAnchor
    {
        static readonly FieldInfo ThirdPersonCarryTransform =
            AccessTools.Field(typeof(RessurectComponent), "thirdPersonCarryTransform");

        sealed class CarryState
        {
            public Network_Player Carrier;
            public Network_Player Carried;
            public bool WasCarrierThirdPerson;
            public bool ForcedCarrierRemote;
            public bool OldCarrierLocal;
            public bool P1CarriesP2;
            public bool P2CarriesP1;
        }

        static void Prefix(RessurectComponent __instance, Network_Player otherPlayer, ref CarryState __state)
        {
            if (Main.player1 == null || Main.player2 == null) return;
            if (__instance == null || otherPlayer == null) return;

            var carrier = __instance.GetComponentInParent<Network_Player>();
            bool p1CarriesP2 = carrier == Main.player1 && otherPlayer == Main.player2;
            bool p2CarriesP1 = carrier == Main.player2 && otherPlayer == Main.player1;
            if (!p1CarriesP2 && !p2CarriesP1) return;

            var tp = carrier.currentModel != null ? carrier.currentModel.thirdPersonSettings : carrier.GetComponentInChildren<ThirdPerson>();
            __state = new CarryState
            {
                Carrier = carrier,
                Carried = otherPlayer,
                WasCarrierThirdPerson = tp != null && tp.ThirdPersonState,
                OldCarrierLocal = VanillaAccessors.GetIsLocalPlayer(carrier),
                P1CarriesP2 = p1CarriesP2,
                P2CarriesP1 = p2CarriesP1
            };

            if (__state.OldCarrierLocal)
            {
                VanillaAccessors.SetIsLocalPlayer(carrier, false);
                __state.ForcedCarrierRemote = true;
            }
        }

        static void Postfix(RessurectComponent __instance, Network_Player otherPlayer, CarryState __state)
        {
            if (__state != null && __state.ForcedCarrierRemote && __state.Carrier != null)
                VanillaAccessors.SetIsLocalPlayer(__state.Carrier, __state.OldCarrierLocal);

            if (Main.player1 == null || Main.player2 == null) return;
            if (__instance == null || otherPlayer == null || __state == null) return;

            var carrier = __instance.GetComponentInParent<Network_Player>();
            if (carrier != __state.Carrier || otherPlayer != __state.Carried) return;

            var thirdPersonCarry = ThirdPersonCarryTransform?.GetValue(__instance) as Transform;
            if (thirdPersonCarry == null) return;

            otherPlayer.transform.SetParent(thirdPersonCarry);
            otherPlayer.transform.localPosition = Vector3.zero;
            otherPlayer.transform.localEulerAngles = Vector3.zero;
            if (otherPlayer.playerPivot != null) otherPlayer.playerPivot.localEulerAngles = Vector3.zero;

            PlayerItemBusyState.SetForCarryVisibility(__state.P1CarriesP2);
            carrier.PlayerItemManager?.HideItemInHand();

            if (__state.WasCarrierThirdPerson)
            {
                var tp = carrier.currentModel != null ? carrier.currentModel.thirdPersonSettings : carrier.GetComponentInChildren<ThirdPerson>();
                if (tp != null) tp.ForceThirdPersonState(true);
            }

            if (__state.P1CarriesP2)
            {
                SplitScreenDeathFlow.PrepareP2DownedVisuals();
                Main.LogV("[DeathFlow] P1 carrying P2 uses third-person carry transform");
            }
            else if (__state.P2CarriesP1)
            {
                SplitScreenDeathFlow.PrepareDownedPlayer(Main.player1.PlayerScript);
                Main.LogV("[DeathFlow] P2 carrying P1 uses third-person carry transform");
            }
        }
    }

    [HarmonyPatch(typeof(RessurectComponent), nameof(RessurectComponent.StopCarryingPlayer))]
    static class Patch_RessurectComponent_StopCarry_P2RecoverDownedState
    {
        static void Prefix(RessurectComponent __instance, ref Network_Player __state)
        {
            __state = __instance?.CarriedPlayer;
        }

        static void Postfix(Network_Player __state)
        {
            bool droppedP2 = __state == Main.player2;
            if (!droppedP2 && Main.player1 != null && Main.player2 != null && Main.player2.transform.IsChildOf(Main.player1.transform))
                droppedP2 = true;
            if (!droppedP2) return;
            if (Main.player2?.PlayerScript == null || !Main.player2.PlayerScript.IsDead) return;

            SplitScreenDeathFlow.RestoreP2AfterDrop();
            Main.LogV("[DeathFlow] P2 dropped while downed; carry target restored");
        }
    }

    [HarmonyPatch(typeof(RessurectComponent), nameof(RessurectComponent.StopCarryingPlayer))]
    static class Patch_RessurectComponent_StopCarry_P1RecoverDownedState
    {
        static void Prefix(RessurectComponent __instance, ref Network_Player __state)
        {
            __state = __instance?.CarriedPlayer;
        }

        static void Postfix(Network_Player __state)
        {
            if (__state != Main.player1) return;
            if (Main.player1?.PlayerScript == null || !Main.player1.PlayerScript.IsDead) return;

            SplitScreenDeathFlow.RestoreP1AfterDrop();
            Main.LogV("[DeathFlow] P1 dropped while downed; carry target restored");
        }
    }

    [HarmonyPatch(typeof(BedManager), nameof(BedManager.Button_Respawn))]
    static class Patch_BedManager_ButtonRespawn_AllSplitPlayers
    {
        static void Postfix()
        {
            if (!SplitScreenDeathFlow.SplitActive) return;

            var p1 = Main.player1;
            var p2 = Main.player2;

            if (p1?.PlayerScript != null && p1.PlayerScript.IsDead)
            {
                if (p1.RessurectComponent != null && p1.RessurectComponent.BeingCarried && p1.RessurectComponent.CarriedByPlayer != null)
                    p1.RessurectComponent.CarriedByPlayer.RessurectComponent.StopCarryingPlayer(manipulatePosition: false);

                Bed p1Bed = BedManager.FindClosestBedToPlayer(p1);
                p1.PlayerScript.StartRespawn(p1Bed, clearInventory: true);
                Main.ModEntry.Logger.Log("[DeathFlow] DeathMenu respawn fallback respawned P1");
            }

            if (p2?.PlayerScript == null || !p2.PlayerScript.IsDead) return;

            if (p2.RessurectComponent != null && p2.RessurectComponent.BeingCarried && p2.RessurectComponent.CarriedByPlayer != null)
                p2.RessurectComponent.CarriedByPlayer.RessurectComponent.StopCarryingPlayer(manipulatePosition: false);

            Bed bed = BedManager.FindClosestBedToPlayer(p2);
            using (P2OriginalScope.Interaction())
                p2.PlayerScript.StartRespawn(bed, clearInventory: true);
            SplitScreenDeathFlow.RestoreP2RespawnStats(p2.PlayerScript, bed, keepInvulnerable: true);
            SplitScreenDeathFlow.SyncP1LocalDeathFlag();
            Main.ModEntry.Logger.Log("[DeathFlow] DeathMenu respawn also respawned P2");
        }
    }

    [HarmonyPatch(typeof(RessurectComponent), nameof(RessurectComponent.PlaceInBed))]
    static class Patch_RessurectComponent_PlaceInBed_P2UsesSleepFlow
    {
        static bool Prefix(RessurectComponent __instance, Bed bed, ref bool __result)
        {
            if (!SplitScreenDeathFlow.SplitActive || __instance == null || bed == null) return true;
            if (__instance.CarriedPlayer != Main.player2) return true;

            var p2 = Main.player2;
            if (p2 == null || p2.PlayerScript == null) return true;

            __instance.StopCarryingPlayer(manipulatePosition: false);
            p2.PlayerScript.StartRespawn(bed, clearInventory: false);
            __result = true;
            Main.ModEntry.Logger.Log("[DeathFlow] P2 rescue placed into vanilla StartRespawn bed flow");
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.StartRespawn))]
    static class Patch_Player_StartRespawn_P2BedFlow
    {
        static void Postfix(Player __instance, Bed bed)
        {
            if (__instance == Main.player1?.PlayerScript)
            {
                Main.ForceCleanupP1NotebookVisuals(restoreController: true);
                Main.player1.PlayerItemManager?.SelectUsable(null);
                Main.player1.PlayerItemManager?.HideItemInHand();
                Main.player1.Inventory?.hotbar?.ReselectCurrentSlot();
            }

            if (Main.player2 == null || Main.player2.PlayerScript != __instance) return;

            if (Main.p2IsUsingMenu)
            {
                ComponentManager<CanvasHelper>.Value?.CloseAllMenus();
                Main.p2IsUsingMenu = false;
            }

            SplitScreenDeathFlow.HideP2DeathOverlay();
            SplitScreenDeathFlow.SyncP1LocalDeathFlag();

            bool respawnPoint = bed == null || bed.transform == null || bed.transform.CompareTag("RespawnPointBed");
            if (respawnPoint)
            {
                SplitScreenDeathFlow.RestoreP2AfterRespawn(__instance, bed);
                Main.ModEntry.Logger.Log("[DeathFlow] P2 respawned at fallback respawn point");
                return;
            }

            SplitScreenDeathFlow.RestoreP2InBedAfterRevive(__instance, bed);
            Main.ModEntry.Logger.Log("[DeathFlow] P2 StartRespawn attached to bed; waiting for P2 wake input");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnRespawnComplete))]
    static class Patch_Player_OnRespawnComplete_CleanupP1Notebook
    {
        static bool Prefix(Player __instance)
        {
            SplitScreenDeathFlow.BeforeP2RespawnComplete(__instance);
            return !SplitScreenDeathFlow.SuppressP2BedRespawnComplete(__instance);
        }

        static void Postfix(Player __instance)
        {
            SplitScreenDeathFlow.AfterP2RespawnComplete(__instance);
            if (!SplitScreenDeathFlow.SplitActive || __instance != Main.player1?.PlayerScript) return;

            Main.ForceCleanupP1NotebookVisuals(restoreController: true);
            Main.player1.PlayerItemManager?.SelectUsable(null);
            Main.player1.PlayerItemManager?.HideItemInHand();
            Main.player1.Inventory?.hotbar?.ReselectCurrentSlot();
            Main.RefreshP2HeldItem();
            Main._p2RefreshHeldNextFrame = true;
        }
    }

    [HarmonyPatch]
    static class Patch_Helper_HitAtCursor_P2Camera
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Helper), nameof(Helper.HitAtCursor), new[] { typeof(RaycastHit).MakeByRefType(), typeof(float) });

        static bool Prefix(ref RaycastHit hit, float rayDistance, ref bool __result)
        {
            return SplitScreenDeathRaycast.TryP2Raycast(ref hit, rayDistance, LayerMasks.MASK_IgnorePlayer, QueryTriggerInteraction.UseGlobal, ref __result);
        }
    }

    [HarmonyPatch]
    static class Patch_Helper_HitAtCursor_Mask_P2Camera
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Helper), nameof(Helper.HitAtCursor), new[] { typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(LayerMask), typeof(QueryTriggerInteraction) });

        static bool Prefix(ref RaycastHit hit, float rayDistance, LayerMask mask, QueryTriggerInteraction queryTriggerInteraction, ref bool __result)
        {
            return SplitScreenDeathRaycast.TryP2Raycast(ref hit, rayDistance, mask, queryTriggerInteraction, ref __result);
        }
    }

    static class SplitScreenDeathRaycast
    {
        internal static bool TryP2Raycast(ref RaycastHit hit, float rayDistance, LayerMask mask, QueryTriggerInteraction queryTriggerInteraction, ref bool result)
        {
            if (!Main.IsP2OriginalActive && !Main.isProcessingP2Ray) return true;

            var p2 = Main.player2;
            var camT = p2?.CameraTransform != null ? p2.CameraTransform : p2?.Camera?.transform;
            if (camT == null) return true;

            result = Physics.Raycast(camT.position, camT.forward, out hit, rayDistance, mask, queryTriggerInteraction);
            return false;
        }
    }
}
