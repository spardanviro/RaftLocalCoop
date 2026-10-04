using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    internal static class P2CameraController
    {
        internal static bool FirstPerson;
        internal static float Yaw;
        internal static float Pitch;

        const float LookSensitivity = 150f;
        const float MaxPitch = 60f;

        static bool _wasViewPressed;

        // 就座时的【相对座位】视角。vanilla 对 disableMouseLook=false 的座位走
        // mouseLookX.isChild = true,即相对载具/座位环视,而非世界固定偏航 ——
        // 用世界 Yaw 的话车一转向相机就不跟、身体却跟车转。P1 那边由 vanilla 自己处理,
        // P2 没有那套 rig,故在此复刻同样的语义。
        internal static float SeatRelYaw;
        internal static float SeatPitch;
        static AttachPlayer _lastSeatAttach;

        // 当前承载 P2 的座位(仅就座时)。
        internal static AttachPlayer CurrentSeatAttach()
        {
            var np = Main.player2;
            if (np == null) return null;
            if (np.BedComponent != null && np.BedComponent.Sleeping) return null;
            if (!(Main.P2IsSeated || Main.P2InSnowmobile)) return null;
            return Main.FindAttachCarrying(np);
        }

        // 该座位是否允许自由环视。判据用 vanilla 自己的 disableMouseLook。
        internal static bool SeatAllowsFreeLook(AttachPlayer a)
        {
            if (a == null || a.disableMouseLook) return false;
            if (Main.IsP2PianoActive || Main.IsP2Steering) return false;   // FP 弹琴/掌舵锁视角,原版意图
            // 驾驶位锁视角:与 P1 侧一致的有意偏离原版(原版驾驶位同样 disableMouseLook=false)。
            if (Main.IsSnowmobileDriverSeat(a, Main.player2)) return false;
            return true;
        }

        internal static void Reset()
        {
            FirstPerson = false;
            Yaw = 0f;
            Pitch = 0f;
            _wasViewPressed = false;
            SeatRelYaw = 0f;
            SeatPitch = 0f;
            _lastSeatAttach = null;
        }

        internal static void Tick()
        {
            var gp = Main.GetP2BoundGamepad();
            if (gp == null) return;
            // 对齐原版：用手柄 View(切换界面键 = selectButton)切第一/第三人称。
            bool press = gp.selectButton.isPressed;
            if (Main.P2IsDownedOrCarried)
            {
                _wasViewPressed = press;
                return;
            }
            // 钢琴不再强制第一人称 —— 与 P1 对齐:FP 弹琴锁视角(原版 Instrument 的设计),
            // TP 弹琴可自由转视角,且随时能按键切换。原先在这里钉死 FirstPerson=true 并 return,
            // 导致 P2 弹琴既用不了 TP、也切不了视角。FP 下的锁视角改由下方的 look 门控负责。

            if (press && !_wasViewPressed && !Main.IsP2BackpackOpen && !Main.IsP2MenuOpen)
            {
                FirstPerson = !FirstPerson;
                if (FirstPerson) InitFromCurrentCamera();
                else SyncOrbitFromFirstPerson();
                Main.LogV("[P2View] switch firstPerson=" + FirstPerson);
            }
            _wasViewPressed = press;

            bool sleeping = Main.player2 != null &&
                            Main.player2.BedComponent != null &&
                            Main.player2.BedComponent.Sleeping;
            // 雪橇车与椅子同列:vanilla AttachPlayer.StartCarryingPlayer 的 disableMouseLook 默认为 true,
            // 上车即 SetMouseLookScripts(false) —— 第一人称驾驶原版就是不能转视角的。
            // IsP2PianoActive:FP 弹琴锁视角,对齐原版 P1(Instrument 走 AttachPlayer.disableMouseLook)。
            // TP 弹琴不受此处约束 —— TP 视角走 P2ThirdPersonCameraController.Handle,那里允许自由环视。
            var seatAttach = CurrentSeatAttach();
            if (seatAttach != _lastSeatAttach)
            {
                _lastSeatAttach = seatAttach;
                SeatRelYaw = 0f;   // 新座位:朝向归座位正前方(同 vanilla keepRotationOnEnter 的效果)
                SeatPitch = 0f;
            }
            bool freeLookSeat = SeatAllowsFreeLook(seatAttach);

            if (!FirstPerson || Main.IsP2MenuOpen || sleeping || Main.IsP2Steering ||
                ((Main.P2IsSeated || Main.P2InSnowmobile) && !freeLookSeat) || Main.IsP2PianoActive) return;

            var bcFp = Main.GetP2BlockCreator();
            bool buildRotateLock = (bcFp != null && bcFp.IsRotating) || Main.IsP2BuildRotateModifierHeld();
            var look = buildRotateLock
                ? Vector2.zero
                : (Main.p2ActionLook != null ? Main.p2ActionLook.ReadValue<Vector2>() : Vector2.zero);
            float scale = LookSensitivity * Time.deltaTime;
            if (freeLookSeat)
            {
                // 相对座位累积,基向量由座位锚点提供 -> 载具转向时视角自然跟随
                SeatRelYaw += look.x * scale;
                SeatPitch = Mathf.Clamp(SeatPitch - look.y * scale, -MaxPitch, MaxPitch);
                return;
            }
            Yaw += look.x * scale;
            Pitch = Mathf.Clamp(Pitch - look.y * scale, -MaxPitch, MaxPitch);
        }

        internal static void EnforceFirstPersonCamera()
        {
            if (!FirstPerson) return;
            SetFirstPersonCameraTransform();
        }

        internal static void ApplyFirstPersonView(Network_Player np)
        {
            SetFirstPersonCameraTransform();
            if (np != null && np.PlayerScript != null)
                np.PlayerScript.SetMouseLookScripts(false);
            if (Main.P2IsDownedOrCarried) return;
            if (np != null && np.ZiplinePlayer != null && np.ZiplinePlayer.IsAttachedToZipline)
            {
                Patch_ThirdPerson_HandleThirdPerson_ZiplineBodyYaw.SyncBodyYaw(np);
                ZiplineRenderFix.SyncToolTransforms(np.ZiplinePlayer);
                return;
            }
            // 就座:身体【不】跟视角转。世界模型是给【对方】看的,应保持与 P2 处于 TP 时
            // 完全相同的静止坐姿;环视完全由相机承载(见 SetFirstPersonCameraTransform,
            // 那里按座位锚点为基向量 + SeatRelYaw/SeatPitch 显式计算朝向)。
            // 这里每帧把身体归零,同时补上先前的空缺:FP 就座走保留欧拉角的分支、
            // 跳过了 LockP2SeatPose,却没有人再给身体写角度。
            var seatAttachApply = CurrentSeatAttach();
            if (seatAttachApply != null && np != null && np.playerPivot != null)
            {
                np.transform.localEulerAngles = Vector3.zero;
                var pe0 = np.playerPivot.localEulerAngles;
                np.playerPivot.localEulerAngles = new Vector3(0f, 0f, pe0.z);
                return;
            }
            if (np != null && np.BedComponent != null && np.BedComponent.Sleeping) return;
            if (np == null || np.playerPivot == null) return;

            // Match the original two-axis look rig: MouseLookX turns the player
            // root in world space, while MouseLookY only pitches CharacterPivot.
            // Keeping yaw off the pivot prevents the FP arm bones and item holders
            // from being evaluated in different local spaces.
            Vector3 rootEuler = np.transform.eulerAngles;
            np.transform.eulerAngles = new Vector3(rootEuler.x, Yaw, rootEuler.z);
            Vector3 pivotEuler = np.playerPivot.localEulerAngles;
            np.playerPivot.localEulerAngles = new Vector3(Pitch, 0f, pivotEuler.z);
        }

        static void SetFirstPersonCameraTransform()
        {
            if (Main.player2 == null || Main.player2.Camera == null) return;
            Main.EnsureP2CameraRig();

            // The full body keeps the TP animator so it remains correct in P1's
            // viewport. Its camera holder therefore contains TP walk bob and is
            // not a stable FP eye anchor. Keep the original FP rig holder for
            // normal camera placement, then copy only the body head's crouch
            // displacement below.
            var holder = FirstPersonVisualRig.GetCameraHolder(Main.player2)
                         ?? (Main.player2.currentModel != null ? Main.player2.currentModel.cameraHolder : null);
            if (Main.P2IsDownedOrCarried)
            {
                var downedCam = Main.player2.Camera.transform;
                if (holder != null)
                {
                    if (downedCam.parent != holder)
                        downedCam.SetParent(holder, false);
                    downedCam.localPosition = Vector3.zero;
                    downedCam.localEulerAngles = Vector3.zero;
                }
                else
                {
                    downedCam.position = Main.player2.transform.position + Vector3.up * 1.5f;
                    downedCam.rotation = Main.player2.transform.rotation;
                }
                return;
            }

            // 雪橇车并入"贴座位"分支:位置与朝向都跟 holder(挂在人身上、人挂在车上)→ 随车转,
            // 且视角固定 = vanilla 上车 disableMouseLook 的效果。走下面的普通分支会用世界固定 Yaw,
            // 车一转向相机不跟、身体却跟车转 → 露出 FP 胳膊 rig 根部。
            if (holder != null &&
                ((Main.player2.BedComponent != null && Main.player2.BedComponent.Sleeping) || Main.P2IsSeated || Main.P2InSnowmobile || SplitScreenDeathFlow.P2PostBedExitRestoring))
            {
                var seatedCam = Main.player2.Camera.transform;
                if (seatedCam.parent != null) seatedCam.SetParent(null, true);
                var seatAttachCam = CurrentSeatAttach();

                // 床不是 AttachPlayer 座位,是另一套机制(Bed.AttachPlayer):把玩家挂到 lockedPivot、
                // 摆到 sleepPoint,并【无条件】播全身睡姿动画(不像座位那样按 IsLocalPlayer 分流),
                // 同时关掉 mouseLook。所以原版睡觉时相机就在躺姿身体的头骨上,没有 FP 锚点可言。
                // 我们的 rig 在睡觉时同样播躺姿(见 Sync 的 sleeping 例外),直接取它的头骨位姿即可。
                // 先前回退分支只取位置、朝向却按座位基向量算,而床的 basis 是 lockedPivot(木筏)-> 对不上原版。
                if (Main.player2.BedComponent != null && Main.player2.BedComponent.Sleeping)
                {
                    FirstPersonVisualRig.UnmountRigFromSeat(Main.player2);
                    seatedCam.position = holder.position;
                    seatedCam.rotation = holder.rotation;
                    return;
                }

                // 原版做法:玩家根挂 firstPersonParent、局部位姿归零、坐姿动画置站姿,相机在头骨。
                // 眼位与环视全由层级关系产生,原版一个坐标都不算。分屏里真实身体要留在 TP 锚点
                // 给对方看坐姿,所以改由自视图 rig 走这条链(MountRigToSeat 复刻 玩家根->CharacterPivot)。
                // 相机于是直接取 rig 的 cameraHolder —— 不再有"我们算的相机位置",
                // 采样漂移与正反馈这两类问题从结构上不可能再发生。
                if (seatAttachCam != null && seatAttachCam.firstPersonParent != null)
                {
                    FirstPersonVisualRig.MountRigToSeat(Main.player2, seatAttachCam, SeatRelYaw, SeatPitch);
                    var seatHolder = FirstPersonVisualRig.GetCameraHolder(Main.player2);
                    if (seatHolder != null)
                    {
                        seatedCam.position = seatHolder.position;
                        seatedCam.rotation = seatHolder.rotation;
                        return;
                    }
                }

                // 没有 FP 锚点的附着(床等):退回世界模型头骨,不动 rig。
                FirstPersonVisualRig.UnmountRigFromSeat(Main.player2);
                var worldHolder = Main.player2.currentModel != null ? Main.player2.currentModel.cameraHolder : null;
                seatedCam.position = worldHolder != null ? worldHolder.position : holder.position;
                var seatBasis = Main.player2.transform.parent;
                var basisRot = seatBasis != null ? seatBasis.rotation : Quaternion.identity;
                seatedCam.rotation = basisRot * Quaternion.Euler(SeatPitch, SeatRelYaw, 0f);
                return;
            }

            // 走到这里说明本帧不在就座分支:把 rig 从座位链上摘回原父级(还原义务同 UnmountHands)。
            FirstPersonVisualRig.UnmountRigFromSeat(Main.player2);

            var cam = Main.player2.Camera.transform;

            if (holder != null)
            {
                if (cam.parent != null)
                    cam.SetParent(null, true);

                // 下蹲不再由相机单独下移:vanilla 已按 FP 分支把 playerPivot 降到位
                // (见 Patch_PersonController_Update 的 Transpiler),holder 连同胳膊、帽子一起降,
                // 相机只要跟住 holder 就与原版一致。自算偏移只降相机 = 胳膊帽子掉队。
                cam.position = holder.position;
                cam.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            }
            else
            {
                if (cam.parent != null)
                    cam.SetParent(null, true);
                cam.position = Main.player2.transform.position + Vector3.up * 1.5f;
                cam.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            }
        }

        internal static void InitFromCurrentCamera()
        {
            if (Main.player2 == null || Main.player2.Camera == null) return;

            var e = Main.player2.Camera.transform.eulerAngles;
            Yaw = e.y;
            float pitch = e.x > 180f ? e.x - 360f : e.x;
            Pitch = Mathf.Clamp(pitch, -MaxPitch, MaxPitch);
        }

        internal static void SyncOrbitFromFirstPerson()
        {
            if (Main.player2 == null || Main.player2.currentModel == null) return;

            var tp = Main.player2.currentModel.thirdPersonSettings;
            if (tp == null) return;

            var fLocalCamRot = VanillaAccessors.ThirdPersonLocalCameraRotation;
            if (fLocalCamRot == null) return;

            // vanilla TP 的 localCameraRotation 是"相对人物朝向"的偏航/俯仰,人在车上时朝向=座位朝向,
            // 故 (0,0) 正好是车正后方、视线水平 —— 与原版上车后首次切 TP 的状态一致。
            // 车上 FP 的视角是锁死的(disableMouseLook),Yaw/Pitch 停留在上车前的陈旧值,
            // 拿它们反算会让相机卡在侧面、并且俯角过大看不到前路。
            if (Main.P2InSnowmobile)
            {
                fLocalCamRot.SetValue(tp, Vector3.zero);
                Pitch = 0f;
                return;
            }

            float rootYaw = Main.player2.transform.eulerAngles.y;
            float relYaw = Mathf.DeltaAngle(0f, Yaw - rootYaw);
            fLocalCamRot.SetValue(tp, new Vector3(Pitch, relYaw, 0f));

            if (Main.player2.playerPivot == null) return;
            var e = Main.player2.playerPivot.localEulerAngles;
            Main.player2.playerPivot.localEulerAngles = new Vector3(0f, e.y, e.z);
        }
                // 注：设备内部的 ReselectCurrentSlot 已被 Patch_Hotbar_ReselectCurrentSlot_P2 抑制，P1 手持模型不受影响。
    }
}
