using System;
using System.Collections.Generic;
using MateEngine;
using UnityEditor;
using UnityEngine;
using VRM;

public static class CompanionTailChecks
{
    static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Tail clearance: " + message);
    }
    public static void Validate(GameObject prefab)
    {
        foreach (string pose in new[] { "FrameLie", "FrameWatch" })
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                instance.SetActive(true);
                var animator = instance.GetComponentInChildren<Animator>(true);
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    "Assets/AnimatorController/AvatarAnimatorControllerV2 1.controller");
                int originalColliderCount = instance.GetComponentsInChildren<VRMSpringBoneColliderGroup>(true).Length;
                animator.Rebind(); animator.Update(0);
                var springs = instance.GetComponentsInChildren<VRMSpringBone>(true);
                var original = new Dictionary<VRMSpringBone, VRMSpringBoneColliderGroup[]>();
                VRMSpringBone tail = null;
                foreach (var spring in springs)
                {
                    original.Add(spring, spring.ColliderGroups);
                    if (spring.RootBones.Count == 1 && spring.RootBones[0].name.Contains("Tail_Root")) tail = spring;
                }
                Require(tail != null, "actual selected model has its native tail spring");
                var clearance = animator.gameObject.AddComponent<CompanionTailClearance>();
                clearance.Configure(animator);
                Require(ReferenceEquals(original[tail], tail.ColliderGroups), "standing retains the existing collision set");
                animator.SetBool("isWindowSit", true);
                animator.Play("Base Layer." + pose, 0, 0); animator.Update(0);
                clearance.Refresh();
                Require(tail.ColliderGroups.Length == original[tail].Length + 4, "lying adds both thighs and calves to the tail spring");
                foreach (var spring in springs)
                    if (spring != tail) Require(ReferenceEquals(original[spring], spring.ColliderGroups), "hair and other springs remain unchanged");
                var root = tail.RootBones[0];
                tail.m_updateType = VRMSpringBone.SpringBoneUpdateType.Manual;
                tail.Setup(true);
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                var knee = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                var foot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                for (int i = 0; i < 180; i++)
                {
                    animator.Update(1f / 60);
                    var hipPosition = hips.position; var kneePosition = knee.position; var footPosition = foot.position;
                    tail.ManualUpdate(1f / 60);
                    Require(Vector3.Distance(hips.position, hipPosition) < .00001f &&
                        Vector3.Distance(knee.position, kneePosition) < .00001f && Vector3.Distance(foot.position, footPosition) < .00001f,
                        "clearance does not move the pelvis, knees or supporting foot");
                }
                float minimum = float.PositiveInfinity;
                foreach (var node in root.GetComponentsInChildren<Transform>())
                {
                    if (node == root) continue;
                    for (int i = original[tail].Length; i < tail.ColliderGroups.Length; i++)
                    {
                        var group = tail.ColliderGroups[i];
                        foreach (var sphere in group.Colliders)
                        {
                            float radius = sphere.Radius * Mathf.Abs(group.transform.lossyScale.x);
                            minimum = Mathf.Min(minimum, Vector3.Distance(node.position, group.transform.TransformPoint(sphere.Offset)) - radius);
                        }
                    }
                }
                Debug.Log($"Tail selected-avatar {pose} minimum leg surface clearance={minimum:F4}");
                Require(minimum >= tail.m_hitRadius * .5f, "simulated tail joints retain their thickness outside the actual leg collision envelope");
                var attached = tail.ColliderGroups;
                clearance.Refresh();
                Require(ReferenceEquals(attached, tail.ColliderGroups), "steady lying reuses the collision array");
                animator.SetBool("isWindowSit", false);
                animator.Play("Base Layer.Idle", 0, 0); animator.Update(0);
                clearance.Refresh();
                Require(ReferenceEquals(original[tail], tail.ColliderGroups), "leaving the pose restores original colliders without resetting physics");
                clearance.Dispose();
                UnityEngine.Object.DestroyImmediate(clearance);
                Debug.Log($"Tail selected-avatar collider teardown before={originalColliderCount} after={instance.GetComponentsInChildren<VRMSpringBoneColliderGroup>(true).Length}");
                Require(instance.GetComponentsInChildren<VRMSpringBoneColliderGroup>(true).Length == originalColliderCount,
                    "owned collider teardown removes every generated child");
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
}
