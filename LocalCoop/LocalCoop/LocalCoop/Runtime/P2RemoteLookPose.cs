using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplitScreen
{
    // Temporary P2 full-body pose used only while P1 renders the P2 world model.
    // P2's own FP view keeps its original camera and arm rig untouched.
    internal static class P2RemoteLookPose
    {
        sealed class BonePose
        {
            internal Transform Transform;
            internal float Weight;
            internal Quaternion SavedLocalRotation;
        }

        static readonly List<BonePose> Bones = new List<BonePose>();
        static CharacterModelModifications _model;
        static bool _resolved;
        static bool _applied;
        static float _savedPivotX;

        internal static void Begin()
        {
            End();

            var p2 = Main.player2;
            if (!Main.IsP2FirstPerson || p2 == null || p2.playerPivot == null) return;
            if (Main.P2IsDownedOrCarried || Main.P2IsSeated || P2ZiplineDriver.IsAttached) return;
            // P2IsSeated 只覆盖 P2 家具路径(TakeP2Seat 写的 _p2CurrentSeat:椅子/马桶/浴缸)。
            // 雪橇车等载具座位是走 vanilla Snowmobile+AttachPlayer 上去的,那个簿记恒为 null ->
            // 判据漏掉 -> P1 每次渲染前都把就座的 P2 上半身按 FP 俯仰掰一遍,渲染完还原
            // (故帧末快照/UE 看不见,只在 P1 眼中表现为 FP 坐姿上半身错位+切 FP 闪烁)。
            // 改用真实附着状态兜底:任何 AttachPlayer 正承载 P2 时都不施加这个姿势。
            if (Main.FindAttachCarrying(p2) != null) return;
            if (p2.BedComponent != null && p2.BedComponent.Sleeping) return;

            Resolve(p2.currentModel);
            if (Bones.Count == 0) return;

            var pivotEuler = p2.playerPivot.localEulerAngles;
            _savedPivotX = pivotEuler.x;
            p2.playerPivot.localEulerAngles = new Vector3(0f, pivotEuler.y, pivotEuler.z);

            float pitch = P2CameraController.Pitch;
            Vector3 worldPitchAxis = p2.transform.right;
            for (int i = 0; i < Bones.Count; i++)
            {
                var bone = Bones[i];
                if (bone.Transform == null) continue;
                bone.SavedLocalRotation = bone.Transform.localRotation;
                Vector3 localPitchAxis = bone.Transform.InverseTransformDirection(worldPitchAxis);
                bone.Transform.localRotation = bone.SavedLocalRotation *
                    Quaternion.AngleAxis(pitch * bone.Weight, localPitchAxis);
            }
            _applied = true;
        }

        internal static void End()
        {
            if (!_applied) return;

            for (int i = 0; i < Bones.Count; i++)
            {
                var bone = Bones[i];
                if (bone.Transform != null) bone.Transform.localRotation = bone.SavedLocalRotation;
            }

            var p2 = Main.player2;
            if (p2 != null && p2.playerPivot != null)
            {
                var e = p2.playerPivot.localEulerAngles;
                p2.playerPivot.localEulerAngles = new Vector3(_savedPivotX, e.y, e.z);
            }
            _applied = false;
        }

        static void Resolve(CharacterModelModifications model)
        {
            if (_resolved && _model == model) return;
            Bones.Clear();
            _model = model;
            _resolved = true;
            if (model == null || model.fullBodyMesh == null || model.fullBodyMesh.bones == null) return;

            var allTransforms = model.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < model.fullBodyMesh.bones.Length; i++)
            {
                var defBone = model.fullBodyMesh.bones[i];
                if (defBone == null || !defBone.name.StartsWith("DEF-", StringComparison.OrdinalIgnoreCase)) continue;
                float weight = GetWeight(defBone.name);
                if (weight <= 0f) continue;

                AddBone(defBone, weight);
                string orgName = "ORG-" + defBone.name.Substring(4);
                for (int t = 0; t < allTransforms.Length; t++)
                {
                    var candidate = allTransforms[t];
                    if (candidate != null && string.Equals(candidate.name, orgName, StringComparison.OrdinalIgnoreCase))
                    {
                        AddBone(candidate, weight);
                        break;
                    }
                }
            }
        }

        static float GetWeight(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("spine")) return 0.30f;
            if (lower.Contains("chest")) return 0.45f;
            if (lower.Contains("neck")) return 0.25f;
            return 0f;
        }

        static void AddBone(Transform transform, float weight)
        {
            for (int i = 0; i < Bones.Count; i++)
                if (Bones[i].Transform == transform) return;
            Bones.Add(new BonePose { Transform = transform, Weight = weight });
        }
    }
}
