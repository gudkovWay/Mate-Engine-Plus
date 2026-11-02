using System;
using System.Collections.Generic;
using UnityEngine;
using VRM;

namespace MateEngine
{
    [DefaultExecutionOrder(10000), DisallowMultipleComponent]
    public sealed class CompanionTailClearance : MonoBehaviour, IDisposable
    {
        sealed class SpringBinding
        {
            public VRMSpringBone Spring;
            public VRMSpringBoneColliderGroup[] Original, Attached;
        }
        static readonly int Sitting = Animator.StringToHash("isWindowSit");
        readonly List<SpringBinding> springs = new List<SpringBinding>();
        readonly List<VRMSpringBoneColliderGroup> owned = new List<VRMSpringBoneColliderGroup>();
        Animator avatar;
        Transform spine, neck;
        bool active;

        public static bool IsLying(Animator animator, Transform spine, Transform neck)
        {
            if (!animator.GetBool(Sitting)) return false;
            var state = animator.GetCurrentAnimatorStateInfo(0);
            if (state.IsName("Base Layer.FrameLie") || state.IsName("Base Layer.FrameWatch")) return true;
            return state.IsName("Base Layer.WindowSit") && spine != null && neck != null &&
                Mathf.Abs(Vector3.Dot((neck.position - spine.position).normalized, animator.transform.up)) < .5f;
        }
        public void Configure(Animator value)
        {
            if (avatar != null || value == null || !value.isHuman) return;
            avatar = value;
            var hips = avatar.GetBoneTransform(HumanBodyBones.Hips);
            spine = avatar.GetBoneTransform(HumanBodyBones.Spine);
            neck = avatar.GetBoneTransform(HumanBodyBones.Neck);
            if (hips == null || spine == null) return;
            foreach (var spring in avatar.GetComponentsInChildren<VRMSpringBone>(true))
            {
                bool tail = spring.RootBones != null && spring.RootBones.Count > 0;
                if (tail)
                    foreach (var root in spring.RootBones)
                        if (root == null || root.name.IndexOf("tail", StringComparison.OrdinalIgnoreCase) < 0 ||
                            !root.IsChildOf(hips) || root.IsChildOf(spine)) { tail = false; break; }
                if (tail) springs.Add(new SpringBinding { Spring = spring });
            }
        }
        void AddLeg(HumanBodyBones start, HumanBodyBones end, float thickness)
        {
            var bone = avatar.GetBoneTransform(start);
            var tip = avatar.GetBoneTransform(end);
            if (bone == null || tip == null) return;
            var offset = bone.InverseTransformPoint(tip.position);
            float length = offset.magnitude;
            if (length < .0001f) return;
            float radius = length * thickness;
            int count = Mathf.CeilToInt(length / (radius * 1.2f)) + 1;
            var node = new GameObject("Companion tail clearance " + start);
            node.transform.SetParent(bone, false);
            var group = node.AddComponent<VRMSpringBoneColliderGroup>();
            group.Colliders = new VRMSpringBoneColliderGroup.SphereCollider[count];
            for (int i = 0; i < count; i++)
                group.Colliders[i] = new VRMSpringBoneColliderGroup.SphereCollider {
                    Offset = offset * ((float)i / (count - 1)), Radius = radius };
            owned.Add(group);
        }
        bool Owns(VRMSpringBoneColliderGroup group)
        {
            foreach (var item in owned) if (item == group) return true;
            return false;
        }
        void SetActive(bool value)
        {
            if (active == value) return;
            if (value && owned.Count == 0)
            {
                AddLeg(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, .18f);
                AddLeg(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, .18f);
                AddLeg(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, .13f);
                AddLeg(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, .13f);
            }
            foreach (var binding in springs)
            {
                if (binding.Spring == null) continue;
                var current = binding.Spring.ColliderGroups;
                if (value)
                {
                    if (!ReferenceEquals(current, binding.Original) || binding.Attached == null)
                    {
                        binding.Original = current;
                        int count = current == null ? 0 : current.Length;
                        binding.Attached = new VRMSpringBoneColliderGroup[count + owned.Count];
                        if (count > 0) Array.Copy(current, binding.Attached, count);
                        owned.CopyTo(binding.Attached, count);
                    }
                    binding.Spring.ColliderGroups = binding.Attached;
                }
                else if (ReferenceEquals(current, binding.Attached)) binding.Spring.ColliderGroups = binding.Original;
                else if (current != null)
                {
                    var retained = new List<VRMSpringBoneColliderGroup>(current.Length);
                    foreach (var group in current) if (!Owns(group)) retained.Add(group);
                    binding.Spring.ColliderGroups = retained.ToArray();
                }
            }
            active = value;
        }
        public void Refresh()
        {
            if (avatar != null && springs.Count > 0) SetActive(IsLying(avatar, spine, neck));
        }
        void LateUpdate() { Refresh(); }
        void OnDisable() { SetActive(false); }
        public void Dispose()
        {
            SetActive(false);
            foreach (var group in owned)
                if (group != null)
                {
                    if (Application.isPlaying) Destroy(group.gameObject);
                    else DestroyImmediate(group.gameObject);
                }
            owned.Clear();
            springs.Clear(); avatar = null; spine = neck = null;
        }
        void OnDestroy() { Dispose(); }
    }
}
