using UnityEngine;
using UnityEngine.InputSystem;

namespace SplitScreen
{
    static partial class SplitScreenDeathFlow
    {
        internal static bool P1DownedThirdPerson;
        static bool P1WasDeadLastFrame;
        static float P1DownedOrbitYaw;
        static float P1DownedOrbitPitch = 18f;
        static bool P1DownedViewWasHeld;
        static int P1DownedViewIgnoreFrames;
        static InputAction P1ViewAction;
        static InputAction P1LookAction;

        internal static void TickP1DownedCamera()
        {
            var p1 = Main.player1;
            if (p1 == null || p1.PlayerScript == null || !p1.PlayerScript.IsDead || p1.Camera == null)
            {
                P1WasDeadLastFrame = false;
                P1DownedThirdPerson = false;
                P1DownedViewWasHeld = false;
                P1DownedViewIgnoreFrames = 0;
                P1DownedAnchorActive = false;
                P1DownedAnchorParent = null;
                return;
            }

            TickP1DownedStabilize();

            if (!P1WasDeadLastFrame)
            {
                P1WasDeadLastFrame = true;
                P1DownedThirdPerson = false;
                var tp = p1.currentModel != null ? p1.currentModel.thirdPersonSettings : p1.GetComponentInChildren<ThirdPerson>();
                if (tp != null && tp.ThirdPersonState)
                    tp.ForceThirdPersonState(false);
                InitP1DownedOrbit();
                CacheP1DownedInputActions();
                P1DownedViewWasHeld = IsP1DownedViewHeld();
                P1DownedViewIgnoreFrames = 8;
            }

            TickP1DownedViewInput();

            var holder = p1.currentModel != null ? p1.currentModel.cameraHolder : null;
            if (holder == null) return;

            if (P1DownedThirdPerson)
            {
                SetP1DownedThirdPersonCamera();
                return;
            }

            var cam = p1.Camera.transform;
            if (cam.parent != holder)
                cam.SetParent(holder, false);
            cam.localPosition = Vector3.zero;
            cam.localEulerAngles = Vector3.zero;
        }

        static void TickP1DownedViewInput()
        {
            if (CanvasHelper.ActiveMenu != MenuType.None) return;

            CacheP1DownedInputActions();

            if (P1DownedViewIgnoreFrames > 0)
            {
                P1DownedViewIgnoreFrames--;
                P1DownedViewWasHeld = IsP1DownedViewHeld();
                return;
            }

            bool viewHeld = IsP1DownedViewHeld();
            bool viewPressed = P1ViewAction != null &&
                               SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.WasPressedThisFrame(P1ViewAction, "Thirdperson");
            if (!viewPressed)
                viewPressed = viewHeld && !P1DownedViewWasHeld;
            P1DownedViewWasHeld = viewHeld;
            if (viewPressed)
            {
                P1DownedThirdPerson = !P1DownedThirdPerson;
                if (P1DownedThirdPerson) InitP1DownedOrbit();
                return;
            }

            if (!P1DownedThirdPerson) return;

            Vector2 look = Vector2.zero;
            if (SimpleMonoBehaviourSingleton<CustomInputConfig>.Instance.Gamepad && P1LookAction != null)
                look = P1LookAction.ReadValue<Vector2>();
            else
                look = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));

            var settings = ComponentManager<Settings>.Value;
            float sensitivity = settings != null ? settings.controls.LookSensitivity : 1f;
            bool invertY = settings != null && settings.controls.InvertLookY;
            float pitchDelta = invertY ? look.y : -look.y;
            P1DownedOrbitYaw += look.x * sensitivity;
            P1DownedOrbitPitch = Mathf.Clamp(P1DownedOrbitPitch + pitchDelta * sensitivity, -20f, 65f);
        }

        static void CacheP1DownedInputActions()
        {
            if (P1ViewAction != null && P1LookAction != null) return;
            var input = PlayerInput.GetPlayerByIndex(0);
            if (input == null) return;
            if (P1ViewAction == null) P1ViewAction = input.actions["View"];
            if (P1LookAction == null) P1LookAction = input.actions["Look"];
        }

        static bool IsP1DownedViewHeld()
        {
            return P1ViewAction != null && P1ViewAction.ReadValue<float>() > 0.5f;
        }

        internal static void ApplyP1DownedThirdPersonCameraForRender(Camera renderCamera = null)
        {
            SetP1DownedThirdPersonCamera(renderCamera, preCull: true);
        }

        static void SetP1DownedThirdPersonCamera(Camera renderCamera = null, bool preCull = false)
        {
            var p1 = Main.player1;
            if (p1 == null || p1.Camera == null) return;

            var camera = renderCamera != null ? renderCamera : p1.Camera;
            var cam = camera.transform;
            if (cam.parent != null)
                cam.SetParent(null, true);

            var tp = p1.currentModel != null ? p1.currentModel.thirdPersonSettings : p1.GetComponentInChildren<ThirdPerson>();
            float pivotY = tp != null ? tp.pivotYOffset : 0.5f;
            float crouchOffset = tp != null ? tp.crouchHeightOffset : 0.5f;
            Vector3 localOffset = tp != null ? tp.localCameraOffset : new Vector3(0f, 0.2f, -3.0f);
            float zoom = 1f;

            Vector3 pivot = p1.transform.position + p1.transform.up * pivotY;
            if (p1.PersonController != null && p1.PersonController.crouching)
                pivot -= p1.transform.up * crouchOffset;

            Vector3 target = pivot
                + p1.transform.right * localOffset.x
                + p1.transform.up * localOffset.y
                + p1.transform.forward * (localOffset.z * zoom);

            Quaternion yaw = Quaternion.AngleAxis(P1DownedOrbitYaw, Vector3.up);
            Vector3 pitchAxis = yaw * p1.transform.right;
            target = RotatePointAroundPivot(target, pivot, yaw);
            target = RotatePointAroundPivot(target, pivot, Quaternion.AngleAxis(P1DownedOrbitPitch, pitchAxis));

            cam.position = target;
            cam.rotation = Quaternion.LookRotation(pivot - target, Vector3.up);
        }

        static Vector3 RotatePointAroundPivot(Vector3 point, Vector3 pivot, Quaternion rotation)
        {
            return pivot + rotation * (point - pivot);
        }

        static void InitP1DownedOrbit()
        {
            var p1 = Main.player1;
            if (p1 == null) return;
            P1DownedOrbitYaw = p1.Camera != null
                ? Mathf.DeltaAngle(p1.transform.eulerAngles.y, p1.Camera.transform.eulerAngles.y)
                : 0f;
            P1DownedOrbitPitch = 18f;
        }
    }
}
