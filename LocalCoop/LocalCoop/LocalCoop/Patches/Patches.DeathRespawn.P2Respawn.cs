using System.Collections;
using UltimateWater;
using UnityEngine;

namespace SplitScreen
{
    static partial class SplitScreenDeathFlow
    {
        internal static int P2RespawnRestoreFrames;
        static Bed P2RespawnRestoreBed;
        static Coroutine P2PostBedExitRestoreRoutine;
        static Bed P2CompletingRespawnBed;
        static Bed P2BedRespawnWaitBed;
        static Vector3 P2BedRespawnSleepPosition;
        static Quaternion P2BedRespawnSleepRotation;
        static bool P2BedRespawnWaitActive;
        internal static int P2BlockCrouchUntilFrame;
        internal static bool P2PostBedExitRestoring => P2PostBedExitRestoreRoutine != null;

        internal static IEnumerator CompleteP2BedReviveAfterDelay(Player player, Bed bed, float delay)
        {
            var p2 = Main.player2;
            if (player == null || p2 == null) yield break;

            if (p2.PersonController != null)
            {
                p2.PersonController.IsMovementFree = false;
                if (p2.PersonController.controller != null)
                    p2.PersonController.controller.enabled = false;
            }

            yield return new WaitForSeconds(delay);

            if (player == null || Main.player2 == null) yield break;

            RestoreP2InBedAfterRevive(player, bed);
        }

        internal static void EnterP2BedAfterRescue(Player player, Bed bed)
        {
            var p2 = Main.player2;
            if (player == null || p2 == null || bed == null) return;

            if (P2PostBedExitRestoreRoutine != null)
            {
                player.StopCoroutine(P2PostBedExitRestoreRoutine);
                P2PostBedExitRestoreRoutine = null;
            }
            P2RespawnRestoreFrames = 0;
            P2RespawnRestoreBed = null;

            RestoreP2RespawnStats(player, bed, keepInvulnerable: false);
            player.waitingForRespawn = false;
            if (player.incapacitatedCollider != null) player.incapacitatedCollider.enabled = false;
            if (player.characterControllerCollider != null) player.characterControllerCollider.enabled = true;

            if (p2.RessurectComponent != null)
            {
                p2.RessurectComponent.BeingCarried = false;
                p2.RessurectComponent.CarriedByPlayer = null;
            }
            p2.Animator?.anim?.SetBool("Carried", false);

            if (p2.BedComponent != null)
                p2.BedComponent.CurrentBed = bed;
            if (!bed.Busy || p2.BedComponent == null || !p2.BedComponent.Sleeping)
                bed.AttachPlayer(p2);

            if (p2.BedComponent != null)
            {
                p2.BedComponent.CurrentBed = bed;
                p2.BedComponent.Sleeping = true;
            }

            if (p2.PersonController != null)
            {
                p2.PersonController.IsMovementFree = false;
                p2.PersonController.enabled = false;
                if (p2.PersonController.controller != null)
                {
                    p2.PersonController.controller.enabled = false;
                    p2.PersonController.controller.detectCollisions = true;
                }
                if (p2.PersonController.waterFloat != null)
                    p2.PersonController.waterFloat.setToSurface = false;
                p2.PersonController.ResetExternalVelocity();
            }

            p2.PlayerItemManager?.SelectUsable(null);
            p2.PlayerItemManager?.HideItemInHand();
            Main.p2FirstPerson = true;
            Main.RestoreP2WorldVisualState();
            Main.ForceP2SleepVisualState();
            HideP2DeathOverlay();
            SyncP1LocalDeathFlag();
        }

        internal static void RestoreP2InBedAfterRevive(Player player, Bed bed)
        {
            var p2 = Main.player2;
            if (player == null || p2 == null || bed == null) return;
            if (P2PostBedExitRestoreRoutine != null)
            {
                player.StopCoroutine(P2PostBedExitRestoreRoutine);
                P2PostBedExitRestoreRoutine = null;
            }
            P2RespawnRestoreFrames = 0;
            P2RespawnRestoreBed = null;
            P2BedRespawnWaitBed = bed;
            P2BedRespawnSleepPosition = p2.transform.position;
            P2BedRespawnSleepRotation = p2.transform.rotation;
            P2BedRespawnWaitActive = true;
            player.waitingForRespawn = true;
            Main.p2FirstPerson = true;
            ApplyP2BedRespawnWaitLock(player, bed);
            Main.RestoreP2WorldVisualState();
            Main.ForceP2SleepVisualState();
            HideP2DeathOverlay();
            SyncP1LocalDeathFlag();
        }

        internal static void TickP2BedRespawnWaitLock()
        {
            var p2 = Main.player2;
            var player = p2 != null ? p2.PlayerScript : null;
            if (!P2BedRespawnWaitActive || p2 == null || player == null || !player.waitingForRespawn)
                return;
            ApplyP2BedRespawnWaitLock(player, P2BedRespawnWaitBed);
        }

        static void ApplyP2BedRespawnWaitLock(Player player, Bed bed)
        {
            var p2 = Main.player2;
            if (player == null || p2 == null || bed == null) return;

            var lockedPivot = SingletonGeneric<GameManager>.Singleton != null
                ? SingletonGeneric<GameManager>.Singleton.lockedPivot
                : null;
            if (lockedPivot != null && p2.transform.parent != lockedPivot)
                p2.transform.SetParent(lockedPivot);

            p2.transform.position = P2BedRespawnSleepPosition;
            p2.transform.rotation = P2BedRespawnSleepRotation;
            if (p2.BedComponent != null)
            {
                p2.BedComponent.CurrentBed = bed;
                p2.BedComponent.Sleeping = true;
            }
            if (player.incapacitatedCollider != null) player.incapacitatedCollider.enabled = false;
            if (player.characterControllerCollider != null) player.characterControllerCollider.enabled = true;
            if (p2.PersonController != null)
            {
                p2.PersonController.controllerType = ControllerType.Ground;
                if (p2.PersonController.waterFloat != null)
                    p2.PersonController.waterFloat.setToSurface = false;
                p2.PersonController.IsMovementFree = false;
                p2.PersonController.enabled = false;
                if (p2.PersonController.controller != null)
                {
                    p2.PersonController.controller.detectCollisions = true;
                    p2.PersonController.controller.enabled = false;
                }
                p2.PersonController.ResetExternalVelocity();
                p2.PersonController.SetNetworkPosition(p2.transform.position);
            }
            p2.PlayerItemManager?.SelectUsable(null);
            p2.PlayerItemManager?.HideItemInHand();
            if (p2.HandCamera != null) p2.HandCamera.enabled = false;
            player.SetMouseLookScripts(false);
        }

        internal static bool SuppressP2BedRespawnComplete(Player player)
        {
            return false;
        }

        internal static void BeforeP2RespawnComplete(Player player)
        {
            if (player == null || Main.player2 == null || Main.player2.PlayerScript != player) return;
            P2CompletingRespawnBed = Main.player2.BedComponent != null ? Main.player2.BedComponent.CurrentBed : null;
        }

        internal static void AfterP2RespawnComplete(Player player)
        {
            var p2 = Main.player2;
            if (player == null || p2 == null || p2.PlayerScript != player) return;

            var bed = P2CompletingRespawnBed;
            P2CompletingRespawnBed = null;
            P2BedRespawnWaitActive = false;
            P2BedRespawnWaitBed = null;
            P2BlockCrouchUntilFrame = Time.frameCount + 30;
            player.waitingForRespawn = false;
            HideP2DeathOverlay();
            SyncP1LocalDeathFlag();
            RestoreP2RootCollisionLayer(p2);
            Main.RestoreP2WorldVisualState();
            ForceP2CollisionState(player, bed, snapToRespawn: bed != null);
            StartP2PostBedExitRestore(player, bed);
        }

        internal static void RestoreP2AfterRespawn(Player player, Bed bed)
        {
            var p2 = Main.player2;
            if (player == null || p2 == null) return;

            RestoreP2RespawnStats(player, bed, keepInvulnerable: true);
            player.waitingForRespawn = false;
            ForceP2CollisionState(player, bed, snapToRespawn: true);
            P2RespawnRestoreBed = bed;
            P2RespawnRestoreFrames = 180;

            p2.Inventory?.hotbar?.ReselectCurrentSlot();
            player.MakeUnStuck();
            if (p2.currentModel != null) p2.currentModel.transform.localPosition = Vector3.zero;
            HideP2DeathOverlay();
            SyncP1LocalDeathFlag();

            player.StopCoroutine("ForceP2CollisionRestore");
            player.StartCoroutine(ForceP2CollisionRestore(player, bed));
        }

        internal static bool IsP2RespawnRestoring(Player player = null)
        {
            var p2Player = player ?? Main.player2?.PlayerScript;
            return p2Player != null && (p2Player.waitingForRespawn || P2RespawnRestoreFrames > 0);
        }

        internal static void RestoreP2RespawnStats(Player player, Bed bed, bool keepInvulnerable)
        {
            var p2 = Main.player2;
            var stats = p2?.Stats;
            if (player == null || p2 == null || stats == null) return;

            var respawnBed = bed != null ? bed : BedManager.RespawnPointBed;
            float healthPercent = respawnBed != null ? respawnBed.HealthRespawnPercentage : 0.5f;
            float hungerPercent = respawnBed != null ? respawnBed.HungerRespawnPercentage : 0.5f;
            float thirstPercent = respawnBed != null ? respawnBed.ThirstRespawnPercentage : 0.5f;

            player.IsDead = false;
            SyncP1LocalDeathFlag();

            p2.Animator?.anim?.SetBool("Dead", false);

            SetStat(stats.stat_health, stats.stat_health != null ? stats.stat_health.Max * healthPercent : 0f);
            SetStat(stats.stat_BonusHealth, 0f);

            if (stats.stat_hunger != null)
            {
                SetStat(stats.stat_hunger.Normal, stats.stat_hunger.Normal != null ? stats.stat_hunger.Normal.Max * hungerPercent : 0f);
                SetStat(stats.stat_hunger.Bonus, 0f);
            }

            if (stats.stat_thirst != null)
            {
                SetStat(stats.stat_thirst.Normal, stats.stat_thirst.Normal != null ? stats.stat_thirst.Normal.Max * thirstPercent : 0f);
                SetStat(stats.stat_thirst.Bonus, 0f);
            }

            stats.stat_oxygen?.SetToMaxValue();
            stats.IsInvurnerable = keepInvulnerable;
        }

        static void SetStat(Stat stat, float value)
        {
            if (stat == null) return;
            stat.Value = value;
            if (stat is Stat_Target target && target.statTarget != null)
                target.statTarget.Value = value;
        }

        internal static void TickP2RespawnRestore()
        {
            if (P2RespawnRestoreFrames <= 0) return;
            var p2 = Main.player2;
            var player = p2?.PlayerScript;
            if (player == null) { P2RespawnRestoreFrames = 0; return; }
            RestoreP2RespawnStats(player, P2RespawnRestoreBed, keepInvulnerable: P2RespawnRestoreFrames > 1);
            ForceP2CollisionState(player, P2RespawnRestoreBed, snapToRespawn: P2RespawnRestoreFrames > 170);
            P2RespawnRestoreFrames--;
            if (P2RespawnRestoreFrames == 0)
            {
                if (p2.Stats != null) p2.Stats.IsInvurnerable = false;
                P2RespawnRestoreBed = null;
            }
        }

        static IEnumerator ForceP2CollisionRestore(Player player, Bed bed)
        {
            for (int i = 0; i < 90; i++)
            {
                RestoreP2RespawnStats(player, bed, keepInvulnerable: true);
                ForceP2CollisionState(player, bed, snapToRespawn: i < 3);
                yield return null;
            }
            if (Main.player2?.Stats != null && P2RespawnRestoreFrames <= 0)
                Main.player2.Stats.IsInvurnerable = false;
        }

        internal static void StartP2PostBedExitRestore(Player player, Bed bed)
        {
            if (player == null) return;
            if (P2PostBedExitRestoreRoutine != null)
                player.StopCoroutine(P2PostBedExitRestoreRoutine);
            P2PostBedExitRestoreRoutine = player.StartCoroutine(ForceP2PostBedExitRestore(player, bed));
        }

        static IEnumerator ForceP2PostBedExitRestore(Player player, Bed bed)
        {
            int stableFrames = 0;
            for (int i = 0; i < 360; i++)
            {
                var p2 = Main.player2;
                var pc = p2 != null ? p2.PersonController : null;
                bool controllerBad = pc == null || pc.controller == null || !pc.enabled || !pc.controller.enabled || !pc.controller.detectCollisions;
                bool grounded = pc != null && pc.controller != null && pc.controller.isGrounded;
                bool groundContact = HasCharacterControllerGroundContact(p2, pc);
                bool fellBelowBed = p2 != null && bed != null && bed.RespawnPoint != null &&
                                    p2.transform.position.y < bed.RespawnPoint.position.y - 1.0f;
                bool snap = i < 8 || controllerBad || fellBelowBed || (!grounded && !groundContact);

                ForceP2CollisionState(player, bed, snapToRespawn: snap);
                if (p2 != null && p2.BedComponent != null)
                    p2.BedComponent.Sleeping = false;
                Main.RestoreP2WorldVisualState();

                if (!controllerBad && (grounded || groundContact) && !fellBelowBed && i > 12)
                    stableFrames++;
                else
                    stableFrames = 0;
                if (stableFrames >= 15)
                    break;

                yield return null;
            }
            P2PostBedExitRestoreRoutine = null;
        }

        internal static void ForceP2CollisionState(Player player, Bed bed, bool snapToRespawn)
        {
            var p2 = Main.player2;
            if (player == null || p2 == null) return;

            var lockedPivot = SingletonGeneric<GameManager>.Singleton != null
                ? SingletonGeneric<GameManager>.Singleton.lockedPivot
                : null;
            if (lockedPivot != null && p2.transform.parent != lockedPivot)
                p2.transform.SetParent(lockedPivot);

            var pc = p2.PersonController;
            RestoreP2RootCollisionLayer(p2);
            if (pc != null && pc.controller != null && snapToRespawn && bed != null && bed.RespawnPoint != null)
            {
                pc.controller.enabled = false;
                p2.transform.position = bed.RespawnPoint.position + Vector3.up * 0.15f;
                p2.transform.rotation = bed.RespawnPoint.rotation;
                pc.SetNetworkPosition(p2.transform.position);
            }

            if (player.characterControllerCollider != null)
                player.characterControllerCollider.enabled = true;

            if (player.incapacitatedCollider != null)
                player.incapacitatedCollider.enabled = false;

            if (p2.RessurectComponent != null)
            {
                p2.RessurectComponent.BeingCarried = false;
                p2.RessurectComponent.CarriedByPlayer = null;
            }
            p2.Animator?.anim?.SetBool("Carried", false);

            if (pc != null)
            {
                pc.controllerType = ControllerType.Ground;
                pc.crouching = false;
                if (pc.waterFloat != null) pc.waterFloat.setToSurface = false;
                pc.IsMovementFree = true;
                pc.enabled = true;
                if (pc.controller != null)
                {
                    pc.controller.enabled = true;
                    pc.controller.detectCollisions = true;
                    pc.controller.height = OriginalControllerHeight(pc);
                    pc.controller.center = Vector3.zero;
                }
                pc.ResetExternalVelocity();
                PersonMoveDirectionField?.SetValue(pc, Vector3.zero);
                PersonRecentlyJumpedField?.SetValue(pc, false);
                PersonResetVelocityTimerField?.SetValue(pc, 0f);
                pc.CameraSubmersionChanged(SubmersionState.None, setControllerType: false);
                RestoreP2RootCollisionLayer(p2);
                SnapP2ToGroundIfNeeded(p2, pc, bed, snapToRespawn);
                RefreshCharacterControllerGrounded(pc);
                pc.SetNetworkPosition(p2.transform.position);
            }
        }

        static void RestoreP2RootCollisionLayer(Network_Player p2)
        {
            if (p2 == null) return;
            int localLayer = LayerMask.NameToLayer("LocalPlayer");
            if (localLayer >= 0 && p2.gameObject.layer != localLayer)
                p2.gameObject.layer = localLayer;
        }

        static void SnapP2ToGroundIfNeeded(Network_Player p2, PersonController pc, Bed bed, bool force)
        {
            if (p2 == null || pc == null || pc.controller == null) return;
            if (!force && pc.controller.isGrounded) return;

            Vector3 origin = p2.transform.position + Vector3.up * 2.0f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, 8f, LayerMasks.MASK_GroundMask, QueryTriggerInteraction.Ignore))
            {
                float minY = CharacterControllerGroundedY(pc.controller, hit.point.y);
                if (force || p2.transform.position.y < minY - 0.02f)
                {
                    pc.controller.enabled = false;
                    p2.transform.position = new Vector3(p2.transform.position.x, minY, p2.transform.position.z);
                    pc.controller.enabled = true;
                }
            }
            else if (force && bed != null && bed.RespawnPoint != null)
            {
                pc.controller.enabled = false;
                p2.transform.position = bed.RespawnPoint.position + Vector3.up * 0.15f;
                pc.controller.enabled = true;
            }
        }

        static float CharacterControllerGroundedY(CharacterController controller, float groundY)
        {
            if (controller == null) return groundY + 0.08f;
            float skin = Mathf.Max(controller.skinWidth, 0.03f);
            return groundY - controller.center.y + controller.height * 0.5f + skin;
        }

        static float OriginalControllerHeight(PersonController pc)
        {
            if (pc == null) return 1.7f;
            object value = PersonOriginalControllerHeightField?.GetValue(pc);
            if (value is float f && f > 0.5f) return f;
            return pc.controller != null && pc.controller.height > 1.2f ? pc.controller.height : 1.7f;
        }

        static bool HasCharacterControllerGroundContact(Network_Player p2, PersonController pc)
        {
            if (p2 == null || pc == null || pc.controller == null) return false;

            Vector3 origin = p2.transform.position + Vector3.up * 2.0f;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 8f, LayerMasks.MASK_GroundMask, QueryTriggerInteraction.Ignore))
                return false;

            float bottomY = p2.transform.position.y + pc.controller.center.y - pc.controller.height * 0.5f;
            float skin = Mathf.Max(pc.controller.skinWidth, 0.03f);
            float delta = bottomY - hit.point.y;
            return delta >= -0.03f && delta <= skin + 0.08f;
        }

        static void RefreshCharacterControllerGrounded(PersonController pc)
        {
            if (pc == null || pc.controller == null || !pc.controller.enabled) return;
            try
            {
                pc.controller.Move(Vector3.down * 0.02f);
            }
            catch (System.Exception e) { Main.LogV("[DeathFlow] RefreshCharacterControllerGrounded ignored: " + e.Message); }
        }
    }
}
