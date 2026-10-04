using HarmonyLib;
using UnityEngine;

namespace SplitScreen
{
    //
    
    
    //     flag = !carriedPlayer.IsLocalPlayer || currentModel.thirdPersonSettings.ThirdPersonModel;
    //     carriedPlayer.Animator.SetAnimation(flag ? fullBodyAnimation : None_0);
    
    
    
    //
    
    
    
    
    
    [HarmonyPatch(typeof(AttachPlayer), "Update")]
    static class Patch_AttachPlayer_Update_P1SeatPose
    {
        static void Postfix(AttachPlayer __instance)
        {
            Main.NoteAttachCarrying(__instance);   // 登记"本座位正承载谁",供 FindAttachCarrying 免扫全场景
            var p1 = Main.player1;
            var p2 = Main.player2;

            if (p2 != null && __instance.carriedPlayer == p2)
            {
                // P2 【不】按自己的视角切换世界模型的摆放:P2 的 ThirdPersonModel 恒为 true,
                // 世界模型是给 P1 看的,应与 P2 处于 TP 时完全一致。此前按 FirstPerson 分流,
                // 导致在车上切 TP->FP 时上半身闪烁几帧后跳到错误位置。
                // P2 的 FP 眼位由 P2CameraController 独立计算,不依赖这里。
                if (ForceFreeLookSeatVisibleBodyParent(__instance, p2))
                    return;

                if (p2.ZiplinePlayer != null && p2.ZiplinePlayer.IsAttachedToZipline)
                {
                    Patch_ThirdPerson_HandleThirdPerson_ZiplineBodyYaw.SyncBodyYaw(p2);
                    ZiplineRenderFix.SyncToolTransforms(p2.ZiplinePlayer);
                    return;
                }

                if (p2.Animator != null)
                {
                    if (__instance.fullBodyAnimation != PlayerFullBodyAnimation.None_0)
                        p2.Animator.SetAnimation(__instance.fullBodyAnimation);
                    if (!string.IsNullOrEmpty(__instance.animationBoolParameter) && p2.Animator.anim != null)
                        p2.Animator.anim.SetBool(__instance.animationBoolParameter, true);
                }
                Main.LockP2SeatPose();
                return;
            }

            if (p1 == null || __instance.carriedPlayer != p1 || p1.Animator == null) return;

            // 顺序要点:FP 时先走 ForceVehicleSeatBodyParent(它保留 root/pivot 欧拉角 = 保住自由环视)。
            // ForceFreeLookSeatVisibleBodyParent 会把这两个角度清零,只适合 TP。
            var p1Tp = p1.currentModel != null ? p1.currentModel.thirdPersonSettings : null;
            if (ForceVehicleSeatBodyParent(__instance, p1, p1Tp != null && !p1Tp.ThirdPersonModel))
                return;

            if (ForceFreeLookSeatVisibleBodyParent(__instance, p1))
                return;

            if (p1.ZiplinePlayer != null && p1.ZiplinePlayer.IsAttachedToZipline)
            {
                Patch_ThirdPerson_HandleThirdPerson_ZiplineBodyYaw.SyncBodyYaw(p1);
                ZiplineRenderFix.SyncToolTransforms(p1.ZiplinePlayer);
                return;
            }

            // 复刻 vanilla 的 flag=true(非本地玩家)分支:P1 可见身体播坐姿动画,与 P2 一致。
            if (__instance.fullBodyAnimation != PlayerFullBodyAnimation.None_0)
                p1.Animator.SetAnimation(__instance.fullBodyAnimation);
            if (!string.IsNullOrEmpty(__instance.animationBoolParameter) && p1.Animator.anim != null)
                p1.Animator.anim.SetBool(__instance.animationBoolParameter, true);

            // 钉死身体朝向(对齐 thirdPersonParent 分支,防随视角转)。
            p1.transform.localEulerAngles = Vector3.zero;
            if (p1.playerPivot != null) p1.playerPivot.localEulerAngles = Vector3.zero;
        }

        // 载具座位(雪橇车三座:disableMouseLook=false,允许自由环视)的身体锚点修正。
        //
        // vanilla AttachPlayer.Update 把"挂哪个锚点"和"放不放全身坐姿动画"绑在同一个 flag 上:
        //   本地玩家在 FP -> firstPersonParent + None_0(反正 FP 看不见自己的身体)
        //   其余(远程/TP)  -> thirdPersonParent + 坐姿动画
        // 两个锚点的位置差正是为了补偿坐姿动画的根偏移,所以它俩必须配套。
        // 分屏里世界模型对【对方】常驻可见,本文件的 Postfix 已无条件把坐姿动画补回来 ->
        // FP 玩家就成了"坐姿动画 + FP 锚点"的错配,身体整体偏出座位(实测 parent=Seat_1_FirstPerson
        // 而 clip=Snowmobile_Neutral)。这里把锚点也补成 TP,让两者重新配套 = 还原原版观感。
        //
        // 关键:【不清 root/pivot 的欧拉角】。实测 FP 就座时 rootEuler.y / pivotEuler.x 承载的正是
        // 自由环视的偏航与俯仰(mouseLook 在本 Update 之后写回),清零等于把视角锁死。
        // 判据用 vanilla 自己的 disableMouseLook 字段,不另维护座位白名单 —— 椅子/床那些
        // disableMouseLook=true 的座位走原有分支,行为完全不变。
        internal static bool ForceVehicleSeatBodyParent(AttachPlayer attach, Network_Player player, bool playerIsFirstPerson)
        {
            if (attach == null || player == null || !playerIsFirstPerson) return false;
            if (attach.disableMouseLook || attach.thirdPersonParent == null) return false;
            if (attach.fullBodyAnimation == PlayerFullBodyAnimation.None_0) return false;

            if (player.transform.parent != attach.thirdPersonParent)
                player.transform.SetParentSafe(attach.thirdPersonParent);
            player.transform.localPosition = Vector3.zero;

            if (player.Animator != null)
            {
                player.Animator.SetAnimation(attach.fullBodyAnimation);
                if (!string.IsNullOrEmpty(attach.animationBoolParameter) && player.Animator.anim != null)
                    player.Animator.anim.SetBool(attach.animationBoolParameter, true);
            }
            return true;
        }

        internal static bool ForceFreeLookSeatVisibleBodyParent(AttachPlayer attach, Network_Player player)
        {
            if (attach == null || player == null) return false;
            if (!Main.IsFreeLookAttach(attach)) return false;
            if (attach.thirdPersonParent == null) return false;

            player.transform.SetParentSafe(attach.thirdPersonParent);
            player.transform.localPosition = Vector3.zero;
            player.transform.localEulerAngles = Vector3.zero;
            if (player.playerPivot != null) player.playerPivot.localEulerAngles = Vector3.zero;

            if (player.Animator != null)
                player.Animator.SetAnimation(attach.fullBodyAnimation);
            if (!string.IsNullOrEmpty(attach.animationBoolParameter) && player.Animator != null && player.Animator.anim != null)
                player.Animator.anim.SetBool(attach.animationBoolParameter, true);

            return true;
        }
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
    }
}
