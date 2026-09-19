using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    internal static class P2ThirdPersonCameraController
    {
        static readonly FieldInfo actionMoveField = VanillaAccessors.Field(typeof(ThirdPerson), "actionMove");
        static readonly FieldInfo actionLookField = VanillaAccessors.Field(typeof(ThirdPerson), "actionLook");
        static readonly FieldInfo actionRotateField = VanillaAccessors.Field(typeof(ThirdPerson), "actionRotate");
        static readonly FieldInfo thirdPersonStateField = VanillaAccessors.Field(typeof(ThirdPerson), "thirdPersonState");
        static readonly FieldInfo localCamRotField = VanillaAccessors.ThirdPersonLocalCameraRotation;
        static readonly FieldInfo camTransformField = VanillaAccessors.Field(typeof(ThirdPerson), "cameraTransform");
        static readonly FieldInfo camRotTransField = VanillaAccessors.Field(typeof(ThirdPerson), "cameraRotateTransform");
        static readonly FieldInfo zoomField = VanillaAccessors.Field(typeof(ThirdPerson), "zoom");
        static readonly FieldInfo targetZoomField = VanillaAccessors.Field(typeof(ThirdPerson), "targetZoom");
        static readonly FieldInfo lerpCamMoveField = VanillaAccessors.Field(typeof(ThirdPerson), "lerpCameraMovementSpeed");
        static readonly FieldInfo lerpCamRotField = VanillaAccessors.Field(typeof(ThirdPerson), "lerpCameraRotationSpeed");
        static readonly FieldInfo pivotYOffField = VanillaAccessors.Field(typeof(ThirdPerson), "pivotYOffset");
        static readonly FieldInfo crouchOffField = VanillaAccessors.Field(typeof(ThirdPerson), "crouchHeightOffset");
        static readonly FieldInfo localCamOffField = VanillaAccessors.Field(typeof(ThirdPerson), "localCameraOffset");
        static readonly FieldInfo zoomLerpField = VanillaAccessors.Field(typeof(ThirdPerson), "zoomLerpSpeed");
        static readonly FieldInfo rotIntervalField = VanillaAccessors.Field(typeof(ThirdPerson), "rotationInterval");
        static readonly FieldInfo sensitivityField = VanillaAccessors.Field(typeof(ThirdPerson), "fallbackSensitivity");
        static readonly MethodInfo handleMethod = VanillaAccessors.Method(typeof(ThirdPerson), "HandleThirdPerson");

        internal static void InvokeHandle(ThirdPerson thirdPerson)
        {
            handleMethod?.Invoke(thirdPerson, null);
        }

        internal static bool Handle(ThirdPerson thirdPerson, Network_Player np)
        {
            bool tpState = (bool)(thirdPersonStateField?.GetValue(thirdPerson) ?? false);
            if (!tpState) return false;

            if (Main.IsP2FirstPerson)
            {
                P2CameraController.ApplyFirstPersonView(np);
                if (np.PlayerScript != null) np.PlayerScript.SetMouseLookScripts(false);
                return false;
            }

            var actionMove = actionMoveField?.GetValue(thirdPerson) as InputAction;
            var actionLook = actionLookField?.GetValue(thirdPerson) as InputAction;
            var actionRotate = actionRotateField?.GetValue(thirdPerson) as InputAction;
            if (actionMove == null || actionLook == null) return false;

            var camTransform = camTransformField?.GetValue(thirdPerson) as Transform;
            var camRotTransform = camRotTransField?.GetValue(thirdPerson) as Transform;
            if (camTransform == null || camRotTransform == null) return false;

            var localCamRot = (Vector3)(localCamRotField?.GetValue(thirdPerson) ?? Vector3.zero);
            float zoom = (float)(zoomField?.GetValue(thirdPerson) ?? 1f);
            float targetZoom = (float)(targetZoomField?.GetValue(thirdPerson) ?? 1f);

            float lerpMove = (float)(lerpCamMoveField?.GetValue(thirdPerson) ?? 1f);
            float lerpRot = (float)(lerpCamRotField?.GetValue(thirdPerson) ?? 1f);
            float pivotY = (float)(pivotYOffField?.GetValue(thirdPerson) ?? 0.5f);
            float crouchOff = (float)(crouchOffField?.GetValue(thirdPerson) ?? 0.5f);
            var localOff = (Vector3)(localCamOffField?.GetValue(thirdPerson) ?? Vector3.zero);
            float zoomLerp = (float)(zoomLerpField?.GetValue(thirdPerson) ?? 3f);
            var rotInterval = (Interval_Float)(rotIntervalField?.GetValue(thirdPerson) ?? default(Interval_Float));

            var settings = ComponentManager<Settings>.Value;
            bool invertY = settings != null && settings.controls.InvertLookY;
            float sensitivity = settings != null
                ? settings.controls.LookSensitivity
                : (float)(sensitivityField?.GetValue(thirdPerson) ?? 1f);

            Vector2 moveRaw = actionMove.ReadValue<Vector2>();
            Vector3 move = new Vector3(moveRaw.x, 0f, moveRaw.y);
            bool moving = move != Vector3.zero;

            bool rotating = actionRotate != null && actionRotate.ReadValue<float>() > 0.5f;
            var p2bc = Main.GetP2BlockCreator();
            bool blockRotating = p2bc != null && p2bc.IsRotating;
            bool buildRotateModifierHeld = Main.IsP2BuildRotateModifierHeld();
            if (!rotating &&
                !blockRotating &&
                !buildRotateModifierHeld &&
                !Main.IsP2MenuOpen &&
                !Main.IsP2BackpackOpen &&
                !Main.IsP2Steering)
            {
                Vector2 lookRaw = actionLook.ReadValue<Vector2>();
                float xRot = invertY ? lookRaw.y : -lookRaw.y;
                localCamRot += new Vector3(xRot, lookRaw.x, 0f) * sensitivity;
                localCamRot.x = Mathf.Clamp(localCamRot.x, rotInterval.minValue, rotInterval.maxValue);
                localCamRotField?.SetValue(thirdPerson, localCamRot);
            }

            // 滑索期间身体跟视角走(有意偏离 vanilla 的 !IsAttached 守卫):这不是创可贴 ——
            // vanilla 电动马达的方向判定 CalculateInitialSpeedBetweenPathAndPlayer 读的正是
            // playerPivot.forward,身体冻住 = 马达失去转向,W 恒等于路径正方向、永远往杆子里怼。
            // 上索瞬间的朝向跳变由 Patch_ZiplinePlayer_Attach_KeepViewYaw 单独解决,不要靠冻结身体。
            if (P2ZiplineDriver.IsAttached && !Main.IsP2MenuOpen && !np.BedComponent.Sleeping)
                np.playerPivot.localEulerAngles = new Vector3(0f, localCamRot.y, 0f);
            else if (moving && !Main.IsP2MenuOpen && !np.BedComponent.Sleeping && !np.PlayerNetworkManager.IsAttached)
                np.playerPivot.localEulerAngles = new Vector3(0f, localCamRot.y, 0f);

            zoom = Mathf.Lerp(zoom, targetZoom, Time.deltaTime * zoomLerp);
            zoomField?.SetValue(thirdPerson, zoom);

            Vector3 pivot = np.transform.position + np.transform.up * pivotY;
            // 下蹲用平滑量(有意偏离 vanilla 的二值跳变,见 CrouchCameraOffset);P1 走同一个助手。
            pivot -= np.transform.up * CrouchCameraOffset.Get(np, crouchOff);

            // 照 vanilla ThirdPerson.HandleThirdPerson(P1 就这套)定位:camRotTransform 放到 pivot+基础偏移,
            // 再绕世界up做yaw、绕相机自身right做pitch。原先用 yawRot*np.transform.right 重建pitch轴——滑索期
            // HandleRotationOfPlayer 把人物根转向缆绳、与相机偏航不一致 → 重建的基向量跟人物根跳 → 镜头突然震一下。
            camRotTransform.position = pivot
                + np.transform.right * localOff.x
                + np.transform.up * localOff.y
                + np.transform.forward * (localOff.z * zoom);
            camRotTransform.RotateAround(pivot, Vector3.up, localCamRot.y);
            camRotTransform.RotateAround(pivot, camTransform.right, localCamRot.x);

            camTransform.position = Vector3.Lerp(
                camTransform.position, camRotTransform.position, Time.deltaTime * lerpMove);

            if (moving)
            {
                camTransform.LookAt(pivot);
            }
            else
            {
                Quaternion targetRot = Quaternion.LookRotation(pivot - camTransform.position);
                camTransform.rotation = Quaternion.Lerp(camTransform.rotation, targetRot, Time.deltaTime * lerpRot);
            }

            np.PlayerScript.SetMouseLookScripts(false);
            return false;
        }

        // 滑索期在移动之后(ZiplinePlayer.Update 的 Postfix)重设 TP 相机位置。Handle 在 ThirdPerson.Update 里跑、
        // 早于 ZiplinePlayer.Update 的移动 → 相机每帧盯"移动前"的旧根 → 相对当前根呼吸=一跳一跳(FP 从 TickP2StatHud
        // 在移动后刷所以不抖)。这里给 TP 补一次"移动后"的刚性重定位,只用已算好的 localCameraRotation(不重读输入,
        // 免灵敏度翻倍),数学同 Handle。
        internal static void RefreshZiplinePositionAfterMove(Network_Player np)
        {
            if (np == null || np.transform == null) return;
            var thirdPerson = np.currentModel != null ? np.currentModel.thirdPersonSettings : null;
            if (thirdPerson == null) return;
            bool tpState = (bool)(thirdPersonStateField?.GetValue(thirdPerson) ?? false);
            if (!tpState) return;
            var camTransform = camTransformField?.GetValue(thirdPerson) as Transform;
            var camRotTransform = camRotTransField?.GetValue(thirdPerson) as Transform;
            if (camTransform == null || camRotTransform == null) return;

            var localCamRot = (Vector3)(localCamRotField?.GetValue(thirdPerson) ?? Vector3.zero);
            float zoom = (float)(zoomField?.GetValue(thirdPerson) ?? 1f);
            float pivotY = (float)(pivotYOffField?.GetValue(thirdPerson) ?? 0.5f);
            float crouchOff = (float)(crouchOffField?.GetValue(thirdPerson) ?? 0.5f);
            var localOff = (Vector3)(localCamOffField?.GetValue(thirdPerson) ?? Vector3.zero);

            Vector3 pivot = np.transform.position + np.transform.up * pivotY;
            pivot -= np.transform.up * CrouchCameraOffset.Get(np, crouchOff);   // 同帧幂等,不会二次推进平滑
            camRotTransform.position = pivot
                + np.transform.right * localOff.x
                + np.transform.up * localOff.y
                + np.transform.forward * (localOff.z * zoom);
            camRotTransform.RotateAround(pivot, Vector3.up, localCamRot.y);
            camRotTransform.RotateAround(pivot, camTransform.right, localCamRot.x);
            camTransform.position = camRotTransform.position;   // 移动后刚性贴合当前根
            camTransform.LookAt(pivot);
        }
    }
}
