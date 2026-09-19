using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace SplitScreen
{
    // MeshPathBase keeps rope creation in static P1-oriented fields. P2 owns a
    // separate preview/state and only calls the shared path creation API when a
    // valid second pole is explicitly selected.
    internal static class P2ZiplineRopeBuilder
    {
        static MeshPathBase _startBase;
        static LineRenderer _preview;

        internal static bool IsBuilding => _startBase != null;

        internal static void Reset()
        {
            _startBase = null;
            if (_preview != null)
            {
                Object.Destroy(_preview.gameObject);
                _preview = null;
            }
        }

        internal static void HandleTarget(SplitScreenRuntime runtime, MeshPathBase target)
        {
            if (runtime?.P2.Player == null || target == null || target.connectPoint == null) return;

            if (!IsBuilding)
            {
                runtime.UI.ShowPrompt("Interact", Helper.GetTerm(target.termStartDraggingRope));
                if (runtime.P2.ActionInteract?.WasPressedThisFrame() == true)
                {
                    _startBase = target;
                    EnsurePreview(runtime.P2.Player);
                    runtime.UI.HidePrompt();
                }
                return;
            }

            List<Vector3> points = null;
            bool valid = target != _startBase && CanCreatePath(_startBase, target, out points);
            SetPreview(points, valid);
            runtime.UI.ShowPrompt("Interact", Helper.GetTerm(valid ? target.termEndDraggingRope : target.termStartDraggingRope));

            if (!valid || runtime.P2.ActionInteract?.WasPressedThisFrame() != true) return;
            CreatePath(runtime.P2.Player, _startBase, target);
            Reset();
            runtime.UI.HidePrompt();
        }

        // Continue showing the rope while P2 looks away from a valid endpoint.
        internal static bool HandleNoTarget(SplitScreenRuntime runtime)
        {
            if (!IsBuilding || runtime?.P2.Player == null) return false;
            var p2 = runtime.P2.Player;

            if (runtime.P2.ActionCancel?.WasPressedThisFrame() == true)
            {
                Main.SuppressP2CrouchOnExit();
                Reset();
                runtime.UI.HidePrompt();
                return true;
            }

            var start = _startBase;
            if (start == null || start.connectPoint == null)
            {
                Reset();
                runtime.UI.HidePrompt();
                return true;
            }

            Vector3 end = p2.transform.position + p2.transform.forward.XZOnly() * 1.5f;
            bool valid = CanCreatePath(start, end, out var points);
            SetPreview(points, valid);
            runtime.UI.ShowPrompt("Cancel", Helper.GetTerm(start.termEndDraggingRope));
            return true;
        }

        static void CreatePath(Network_Player p2, MeshPathBase start, MeshPathBase end)
        {
            if (p2 == null || start == null || end == null) return;

            if (Raft_Network.IsHost)
            {
                if (!MeshPath.CreateMeshPath(start, end, MeshPathBase.TargetSlackSetting, replicating: false)) return;
                var message = new Message_MeshPath(Messages.MeshPath_Create, p2.Network.NetworkIDManager, start, end, MeshPathBase.TargetSlackSetting);
                p2.Network.RPC(message, Target.Other, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
            }
            else
            {
                var message = new Message_MeshPath(Messages.MeshPath_Create, p2.Network.NetworkIDManager, start, end, MeshPathBase.TargetSlackSetting);
                p2.SendP2P(message, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
            }
        }

        static bool CanCreatePath(MeshPathBase start, MeshPathBase end, out List<Vector3> points)
        {
            return CanCreatePath(start, end != null ? end.connectPoint.position : Vector3.zero, end, out points);
        }

        static bool CanCreatePath(MeshPathBase start, Vector3 end, out List<Vector3> points)
        {
            return CanCreatePath(start, end, null, out points);
        }

        static bool CanCreatePath(MeshPathBase start, Vector3 end, MeshPathBase endBase, out List<Vector3> points)
        {
            points = null;
            if (start == null || start.connectPoint == null || start.pathPrefab == null) return false;

            Vector3 begin = start.connectPoint.position;
            float distance = Vector3.Distance(begin, end);
            if (distance > start.pathPrefab.maxLength) return false;
            if (endBase != null && MeshPath.DoesConnectionExist(start, endBase)) return false;

            points = MeshPath.CreatePointsForPath(begin, end, MeshPathBase.TargetSlackSetting);
            for (int i = 0; i < points.Count - 2; i++)
            {
                Vector3 a = points[i];
                Vector3 b = points[i + 1];
                if (Physics.Linecast(a, b, LayerMasks.MASK_Block, QueryTriggerInteraction.Ignore)) return false;
                if (i > 0 && Physics.Raycast(a, Vector3.down, 0.1f, LayerMasks.MASK_Block, QueryTriggerInteraction.Ignore)) return false;
            }
            return true;
        }

        static void EnsurePreview(Network_Player p2)
        {
            if (_preview != null) return;
            var source = p2?.ZiplinePlayer != null ? p2.ZiplinePlayer.buildZiplineRenderer : null;
            var go = new GameObject("P2_ZiplinePreview");
            int previewLayer = LayerMask.NameToLayer("RemotePlayer");
            go.layer = previewLayer >= 0 ? previewLayer : 0;
            _preview = go.AddComponent<LineRenderer>();
            _preview.useWorldSpace = true;
            _preview.positionCount = 0;

            if (source != null)
            {
                _preview.sharedMaterial = source.sharedMaterial;
                _preview.widthMultiplier = source.widthMultiplier;
                _preview.widthCurve = source.widthCurve;
                _preview.alignment = source.alignment;
                _preview.textureMode = source.textureMode;
                _preview.numCapVertices = source.numCapVertices;
                _preview.numCornerVertices = source.numCornerVertices;
            }
            else
            {
                _preview.widthMultiplier = 0.04f;
            }
        }

        static void SetPreview(List<Vector3> points, bool valid)
        {
            if (_preview == null || points == null) return;
            _preview.positionCount = points.Count;
            _preview.SetPositions(points.ToArray());
            _preview.startColor = _preview.endColor = valid ? Color.green : Color.red;
        }
    }
}
