using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRM;

namespace MateEngine.Vision
{
    public sealed class VisionPerformance : IDisposable
    {
        sealed class Anchor
        {
            public readonly Transform Bone;
            readonly Transform middle, index, little;
            readonly Quaternion basis;
            public Anchor(Transform bone, Quaternion basis)
            {
                Bone = bone;
                if (Bone == null) throw new InvalidOperationException("Missing vision attachment bone");
                this.basis = basis;
            }
            public Anchor(Transform bone, Transform middle = null, Transform index = null, Transform little = null)
                : this(bone, Quaternion.identity)
            {
                this.middle = middle; this.index = index; this.little = little;
            }
            public Quaternion Rotation
            {
                get
                {
                    if (middle == null || index == null || little == null) return Bone.rotation * basis;
                    var forward = middle.position - Bone.position;
                    var normal = Vector3.Cross(forward, index.position - little.position);
                    return normal.sqrMagnitude < 1e-10f || forward.sqrMagnitude < 1e-10f ?
                        Bone.rotation : Quaternion.LookRotation(forward, normal);
                }
            }
        }
        sealed class PropInstance
        {
            public VisionPropAsset Asset;
            public Transform Root;
            public Anchor Source, Target, CarrySource, CarryTarget;
        }
        sealed class RotationBinding
        {
            public Transform Source, Target;
            public Quaternion SourceRestInverse, TargetRest;
            public Quaternion Current = Quaternion.identity, Previous = Quaternion.identity;
            public int Depth;
        }
        struct NeutralPose
        {
            public Matrix4x4 Matrix;
            public Quaternion Rotation;
            public int Depth;
            public Vector3 Position => Matrix.GetColumn(3);
        }
        readonly Dictionary<string, VisionClip> clips = new Dictionary<string, VisionClip>();
        readonly Dictionary<string, PropInstance> props = new Dictionary<string, PropInstance>();
        readonly List<RotationBinding> bones = new List<RotationBinding>();
        readonly Dictionary<HumanBodyBones, Transform> sourceBones = new Dictionary<HumanBodyBones, Transform>();
        readonly VisionLibrary library;
        Animator avatar, sampler;
        GameObject proxy;
        Transform sourceHips, targetHips, targetSpine, targetNeck;
        Quaternion sourceHipToBody, targetHipToBody, targetBodyNeutral, sourceHeadToBody, targetHeadToBody;
        PlayableGraph graph;
        AnimationClipPlayable playable;
        AnimationPlayableOutput output;
        string playing;
        float blend, transition, lookYaw, lookPitch, readingTurn;
        int readingSide;
        float bodyScale;
        VRMBlendShapeProxy face;
        BlendShapeKey joy, sorrow, fun, mouth;
        float previousJoy, previousSorrow, previousFun, previousMouth;
        bool faceOwned;
        AvatarMouseTracking mouseTracking;
        bool trackingBefore;
        Transform head, leftEye, rightEye;
        Quaternion leftEyeBase, rightEyeBase;
        bool eyesOwned;
        Transform rightArm, rightElbow, rightHand;
        Transform leftArm, leftElbow, leftHand, sourceLeftHand;
        Vector3 readingFaceForward;
        bool readingFaceReady;
        const float MagnifierClearance = .173f;
        public bool Ready => library != null && library.rig != null && library.rest != null && library.rest.Length == 51;
        public string Error { get; private set; }
        public bool Applied { get; private set; }
        public int VisibleProps { get; private set; }
        public Vector3 RightHand => avatar == null ? Vector3.zero : avatar.GetBoneTransform(HumanBodyBones.RightHand).position;
        public Vector3 LeftHand => avatar == null ? Vector3.zero : avatar.GetBoneTransform(HumanBodyBones.LeftHand).position;
        public Vector3 MagnifierCenter => props.TryGetValue("Magnifier", out var prop) && prop.Root != null ? prop.Root.position : Vector3.zero;

        public VisionPerformance(VisionChoreography choreography)
        {
            library = Resources.Load<VisionLibrary>("CompanionVision/VisionLibrary");
            if (!Ready) { Error = "Vision animation library is unavailable"; return; }
            foreach (var item in library.clips)
            {
                if (item.clip == null || item.clip.humanMotion || item.clip.legacy) throw new InvalidOperationException("Invalid raw vision clip: " + item.name);
                clips.Add(item.name, item); choreography.SetDuration(item.name, item.clip.length);
            }
        }
        static NeutralPose Neutral(Transform node, Transform root, Dictionary<string, SkeletonBone> skeleton,
            Dictionary<Transform, NeutralPose> cache)
        {
            if (cache.TryGetValue(node, out var saved)) return saved;
            if (node == root) return new NeutralPose { Matrix = Matrix4x4.identity, Rotation = Quaternion.identity };
            if (node.parent == null || !skeleton.TryGetValue(node.name, out var local))
                throw new InvalidOperationException("Missing neutral avatar joint: " + node.name);
            var parent = Neutral(node.parent, root, skeleton, cache);
            var result = new NeutralPose {
                Matrix = parent.Matrix * Matrix4x4.TRS(local.position, local.rotation, local.scale),
                Rotation = parent.Rotation * local.rotation, Depth = parent.Depth + 1 };
            cache.Add(node, result);
            return result;
        }
        static Quaternion BodyFrame(Vector3 hips, Vector3 head, Vector3 leftArm, Vector3 rightArm)
        {
            var up = head - hips;
            var side = Vector3.ProjectOnPlane(leftArm - rightArm, up);
            if (up.sqrMagnitude < 1e-8f || side.sqrMagnitude < 1e-8f)
                throw new InvalidOperationException("Degenerate humanoid neutral body frame");
            return Quaternion.LookRotation(Vector3.Cross(side, up).normalized, up.normalized);
        }
        static bool UpperBody(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.Hips:
                case HumanBodyBones.LeftUpperLeg: case HumanBodyBones.RightUpperLeg:
                case HumanBodyBones.LeftLowerLeg: case HumanBodyBones.RightLowerLeg:
                case HumanBodyBones.LeftFoot: case HumanBodyBones.RightFoot:
                case HumanBodyBones.LeftToes: case HumanBodyBones.RightToes: return false;
                default: return true;
            }
        }
        static Quaternion PalmFrame(Vector3 wrist, Vector3 middle, Vector3 index, Vector3 little)
        {
            var forward = middle - wrist;
            var normal = Vector3.Cross(forward, index - little);
            if (forward.sqrMagnitude < 1e-10f || normal.sqrMagnitude < 1e-10f)
                throw new InvalidOperationException("Degenerate humanoid palm frame");
            return Quaternion.LookRotation(forward, normal);
        }
        static HumanBodyBones HandFor(HumanBodyBones bone)
        {
            int index = (int)bone;
            if (bone == HumanBodyBones.LeftHand || index >= (int)HumanBodyBones.LeftThumbProximal && index <= (int)HumanBodyBones.LeftLittleDistal)
                return HumanBodyBones.LeftHand;
            if (bone == HumanBodyBones.RightHand || index >= (int)HumanBodyBones.RightThumbProximal && index <= (int)HumanBodyBones.RightLittleDistal)
                return HumanBodyBones.RightHand;
            return HumanBodyBones.LastBone;
        }
        Transform BoneFor(HumanBodyBones bone, bool source)
        {
            if (!source) return avatar.GetBoneTransform(bone);
            if (!sourceBones.TryGetValue(bone, out var node)) throw new InvalidOperationException("Missing authored source bone: " + bone);
            return node;
        }
        Anchor CreateAnchor(HumanBodyBones bone, bool source)
        {
            if (bone == HumanBodyBones.Head)
                return new Anchor(BoneFor(bone, source), source ? sourceHeadToBody : targetHeadToBody);
            bool right = bone == HumanBodyBones.RightHand;
            if (!right && bone != HumanBodyBones.LeftHand) return new Anchor(BoneFor(bone, source));
            return new Anchor(BoneFor(bone, source),
                BoneFor(right ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal, source),
                BoneFor(right ? HumanBodyBones.RightIndexProximal : HumanBodyBones.LeftIndexProximal, source),
                BoneFor(right ? HumanBodyBones.RightLittleProximal : HumanBodyBones.LeftLittleProximal, source));
        }
        void BindRotations()
        {
            var sourceRest = new Dictionary<HumanBodyBones, VisionBoneRest>();
            foreach (var item in library.rest) sourceRest.Add(item.bone, item);
            var skeleton = new Dictionary<string, SkeletonBone>();
            foreach (var item in avatar.avatar.humanDescription.skeleton) skeleton.Add(item.name, item);
            var targetRest = new Dictionary<Transform, NeutralPose>();
            sourceHips = BoneFor(HumanBodyBones.Hips, true);
            targetHips = avatar.GetBoneTransform(HumanBodyBones.Hips);
            targetSpine = avatar.GetBoneTransform(HumanBodyBones.Spine);
            targetNeck = avatar.GetBoneTransform(HumanBodyBones.Neck);
            var targetHipRest = Neutral(targetHips, avatar.transform, skeleton, targetRest);
            var targetHeadRest = Neutral(avatar.GetBoneTransform(HumanBodyBones.Head), avatar.transform, skeleton, targetRest);
            var targetLeftRest = Neutral(avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm), avatar.transform, skeleton, targetRest);
            var targetRightRest = Neutral(avatar.GetBoneTransform(HumanBodyBones.RightUpperArm), avatar.transform, skeleton, targetRest);
            var sourceBody = BodyFrame(sourceRest[HumanBodyBones.Hips].position, sourceRest[HumanBodyBones.Head].position,
                sourceRest[HumanBodyBones.LeftUpperArm].position, sourceRest[HumanBodyBones.RightUpperArm].position);
            var targetBody = BodyFrame(targetHipRest.Position, targetHeadRest.Position, targetLeftRest.Position, targetRightRest.Position);
            targetBodyNeutral = targetBody;
            sourceHipToBody = Quaternion.Inverse(sourceRest[HumanBodyBones.Hips].rotation) * sourceBody;
            targetHipToBody = Quaternion.Inverse(targetHipRest.Rotation) * targetBody;
            sourceHeadToBody = Quaternion.Inverse(sourceRest[HumanBodyBones.Head].rotation) * sourceBody;
            targetHeadToBody = Quaternion.Inverse(targetHeadRest.Rotation) * targetBody;
            var sourceBodyInverse = Quaternion.Inverse(sourceBody);
            var targetBodyInverse = Quaternion.Inverse(targetBody);
            Quaternion leftPalm = Quaternion.identity, rightPalm = Quaternion.identity;
            for (int side = 0; side < 2; side++)
            {
                var hand = side == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
                var middle = side == 0 ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal;
                var index = side == 0 ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal;
                var little = side == 0 ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal;
                var sourcePalm = PalmFrame(sourceRest[hand].position, sourceRest[middle].position, sourceRest[index].position, sourceRest[little].position);
                var targetPalm = PalmFrame(Neutral(avatar.GetBoneTransform(hand), avatar.transform, skeleton, targetRest).Position,
                    Neutral(avatar.GetBoneTransform(middle), avatar.transform, skeleton, targetRest).Position,
                    Neutral(avatar.GetBoneTransform(index), avatar.transform, skeleton, targetRest).Position,
                    Neutral(avatar.GetBoneTransform(little), avatar.transform, skeleton, targetRest).Position);
                var correction = sourceBodyInverse * sourcePalm * Quaternion.Inverse(targetBodyInverse * targetPalm);
                if (side == 0) leftPalm = correction; else rightPalm = correction;
            }
            rightArm = avatar.GetBoneTransform(HumanBodyBones.RightUpperArm);
            rightElbow = avatar.GetBoneTransform(HumanBodyBones.RightLowerArm);
            rightHand = avatar.GetBoneTransform(HumanBodyBones.RightHand);
            leftArm = avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            leftElbow = avatar.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            leftHand = avatar.GetBoneTransform(HumanBodyBones.LeftHand);
            sourceLeftHand = BoneFor(HumanBodyBones.LeftHand, true);
            var eyeLeft = avatar.GetBoneTransform(HumanBodyBones.LeftEye);
            var eyeRight = avatar.GetBoneTransform(HumanBodyBones.RightEye);
            readingFaceReady = false;
            if (eyeLeft != null && eyeRight != null)
            {
                var eyeCenter = (Neutral(eyeLeft, avatar.transform, skeleton, targetRest).Position +
                    Neutral(eyeRight, avatar.transform, skeleton, targetRest).Position) * .5f;
                var forward = Vector3.ProjectOnPlane(eyeCenter - targetHeadRest.Position, targetBody * Vector3.up);
                if (forward.sqrMagnitude > 1e-10f)
                {
                    readingFaceForward = Quaternion.Inverse(targetHeadRest.Rotation) * forward.normalized;
                    readingFaceReady = true;
                }
            }
            foreach (var rest in library.rest)
            {
                if (!UpperBody(rest.bone)) continue;
                var targetBone = avatar.GetBoneTransform(rest.bone);
                if (targetBone == null) continue;
                var neutral = Neutral(targetBone, avatar.transform, skeleton, targetRest);
                var hand = HandFor(rest.bone);
                var correction = hand == HumanBodyBones.LeftHand ? leftPalm :
                    hand == HumanBodyBones.RightHand ? rightPalm : Quaternion.identity;
                bones.Add(new RotationBinding { Source = BoneFor(rest.bone, true), Target = targetBone,
                    SourceRestInverse = Quaternion.Inverse(sourceBodyInverse * rest.rotation),
                    TargetRest = correction * targetBodyInverse * neutral.Rotation, Depth = neutral.Depth });
            }
            bones.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            float sourceLength = Vector3.Distance(sampler.transform.TransformPoint(sourceRest[HumanBodyBones.Spine].position),
                sampler.transform.TransformPoint(sourceRest[HumanBodyBones.Neck].position));
            var spine = Neutral(avatar.GetBoneTransform(HumanBodyBones.Spine), avatar.transform, skeleton, targetRest);
            var neck = Neutral(avatar.GetBoneTransform(HumanBodyBones.Neck), avatar.transform, skeleton, targetRest);
            float targetLength = Vector3.Distance(avatar.transform.TransformPoint(spine.Position), avatar.transform.TransformPoint(neck.Position));
            if (sourceLength <= 0 || targetLength <= 0) throw new InvalidOperationException("Invalid humanoid attachment scale");
            bodyScale = targetLength / sourceLength;
        }
        void Bind(Animator value)
        {
            ReleaseAvatar(); avatar = value;
            proxy = UnityEngine.Object.Instantiate(library.rig);
            proxy.name = "Vision Skeletal Sampler"; proxy.hideFlags = HideFlags.HideAndDontSave;
            foreach (var renderer in proxy.GetComponentsInChildren<Renderer>()) renderer.enabled = false;
            sampler = proxy.GetComponent<Animator>();
            if (sampler == null || sampler.avatar == null || !sampler.avatar.isValid || sampler.isHuman)
                throw new InvalidOperationException("Vision source is not a raw skeletal sampling rig");
            foreach (var node in proxy.GetComponentsInChildren<Transform>(true))
                if (Enum.TryParse(node.name, out HumanBodyBones bone) && bone != HumanBodyBones.LastBone)
                    sourceBones.Add(bone, node);
            foreach (var rest in library.rest)
                if (!sourceBones.ContainsKey(rest.bone)) throw new InvalidOperationException("Incomplete source mapping: " + rest.bone);
            sampler.runtimeAnimatorController = null;
            sampler.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            BindRotations();
            graph = PlayableGraph.Create("Companion Vision Sampler");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            output = AnimationPlayableOutput.Create(graph, "Vision upper body", sampler);
            graph.Play();
            mouseTracking = avatar.GetComponent<AvatarMouseTracking>();
            trackingBefore = mouseTracking != null && mouseTracking.enabled;
            head = avatar.GetBoneTransform(HumanBodyBones.Head);
            leftEye = avatar.GetBoneTransform(HumanBodyBones.LeftEye);
            rightEye = avatar.GetBoneTransform(HumanBodyBones.RightEye);
            if (leftEye != null) leftEyeBase = leftEye.localRotation;
            if (rightEye != null) rightEyeBase = rightEye.localRotation;
            face = avatar.GetComponent<VRMBlendShapeProxy>();
            joy = BlendShapeKey.CreateFromPreset(BlendShapePreset.Joy);
            sorrow = BlendShapeKey.CreateFromPreset(BlendShapePreset.Sorrow);
            fun = BlendShapeKey.CreateFromPreset(BlendShapePreset.Fun);
            mouth = BlendShapeKey.CreateFromPreset(BlendShapePreset.O);
            foreach (var item in library.props)
            {
                if (!Enum.TryParse(item.bone, out HumanBodyBones bone)) throw new InvalidOperationException("Invalid prop attachment: " + item.name);
                var sourceAnchor = CreateAnchor(bone, true);
                var targetAnchor = CreateAnchor(bone, false);
                Anchor carrySource = null, carryTarget = null;
                if (!string.IsNullOrEmpty(item.carryBone))
                {
                    if (!Enum.TryParse(item.carryBone, out HumanBodyBones carry)) throw new InvalidOperationException("Invalid carried prop bone");
                    carrySource = CreateAnchor(carry, true); carryTarget = CreateAnchor(carry, false);
                }
                var instance = UnityEngine.Object.Instantiate(item.prefab, avatar.transform, false);
                instance.name = "Vision " + item.name;
                foreach (var collider in instance.GetComponentsInChildren<Collider>()) UnityEngine.Object.Destroy(collider);
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
                { renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false; }
                var parentScale = avatar.transform.lossyScale;
                instance.transform.localScale = Vector3.Scale(item.scale * bodyScale, new Vector3(
                    1 / Mathf.Max(.0001f, Mathf.Abs(parentScale.x)), 1 / Mathf.Max(.0001f, Mathf.Abs(parentScale.y)), 1 / Mathf.Max(.0001f, Mathf.Abs(parentScale.z))));
                instance.SetActive(false);
                props.Add(item.name, new PropInstance { Asset = item, Root = instance.transform,
                    Source = sourceAnchor, Target = targetAnchor, CarrySource = carrySource, CarryTarget = carryTarget });
            }
            Error = null;
        }
        void Sample(string clip, float time)
        {
            if (!clips.TryGetValue(clip, out var asset)) throw new InvalidOperationException("Missing vision motion: " + clip);
            if (playing != clip)
            {
                foreach (var bone in bones) bone.Previous = bone.Current;
                if (playable.IsValid()) graph.DestroyPlayable(playable);
                playable = AnimationClipPlayable.Create(graph, asset.clip);
                playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false); playable.SetSpeed(0);
                output.SetSourcePlayable(playable);
                transition = 0; playing = clip;
            }
            playable.SetTime(Mathf.Clamp(time, 0, asset.clip.length));
            graph.Evaluate(0);
        }
        public void Apply(Animator value, VisionChoreography choreography, Vector2 lookPoint, float delta)
        {
            Applied = false;
            if (!Ready || value == null || !value.isHuman || !value.isActiveAndEnabled) { Hide(); return; }
            if (avatar != value) Bind(value);
            bool active = choreography.ActivePerformance;
            blend = Mathf.MoveTowards(blend, active ? 1 : 0, delta / .25f);
            if (!active && blend <= 0) { Hide(); return; }
            if (choreography.Clip == null) { Hide(); return; }
            if (mouseTracking != null) mouseTracking.enabled = false;
            float clipTime = choreography.PhaseTime;
            if (choreography.Phase == VisionPhase.Hold)
                clipTime = choreography.Lowered ? choreography.Duration(choreography.Clip) : Mathf.Repeat(clipTime, choreography.Duration(choreography.Clip));
            Sample(choreography.Clip, clipTime);
            transition = Mathf.Min(1, transition + delta / .2f);
            var sourceBodyInverse = Quaternion.Inverse(sourceHips.rotation * sourceHipToBody);
            var camera = Camera.main;
            var targetBody = targetHips.rotation * targetHipToBody;
            if (CompanionTailClearance.IsLying(avatar, targetSpine, targetNeck))
                targetBody = BodyFrame(targetSpine.position, targetNeck.position, leftArm.position, rightArm.position);
            else if (choreography.Active == VisionRoutine.Read)
                targetBody = avatar.transform.rotation * targetBodyNeutral;
            targetBody = ReadingTurn(camera, lookPoint, choreography, delta) * targetBody;
            foreach (var bone in bones)
            {
                bone.Current = sourceBodyInverse * bone.Source.rotation * bone.SourceRestInverse;
                var desired = targetBody * Quaternion.Slerp(bone.Previous, bone.Current, transition) * bone.TargetRest;
                bone.Target.rotation = Quaternion.Slerp(bone.Target.rotation, desired, blend);
            }
            Aim(camera, lookPoint, choreography, delta);
            FocusMagnifier(choreography);
            FocusHeadphones(choreography);
            ApplyFace(choreography);
            SetProps(choreography);
            Applied = true;
        }
        void FocusMagnifier(VisionChoreography choreography)
        {
            if (choreography.Prop != VisionProp.Magnifier) return;
            float progress = Mathf.Clamp01(choreography.PhaseTime / choreography.Duration(choreography.Clip));
            float weight;
            switch (choreography.Clip)
            {
                case "MagnifierEquip": weight = Mathf.InverseLerp(.68f, 1, progress); break;
                case "MagnifierHold": weight = 1; break;
                case "MagnifierLower": weight = 1 - progress; break;
                case "MagnifierRaise": weight = progress; break;
                default: return;
            }
            weight = Mathf.SmoothStep(0, 1, weight) * blend;
            if (weight <= 0) return;
            if (!readingFaceReady || rightEye == null || rightArm == null || rightElbow == null || rightHand == null)
                throw new InvalidOperationException("Reading needs valid eye landmarks and a complete right arm");
            var prop = props["Magnifier"];
            Place(prop, false);
            var lensOffset = prop.Root.position - rightHand.position;
            var lensTarget = rightEye.position + head.rotation * readingFaceForward * (MagnifierClearance * bodyScale);
            var wristTarget = Vector3.Lerp(rightHand.position, lensTarget - lensOffset, weight);
            SolveArm(rightArm, rightElbow, rightHand, wristTarget);
        }
        static void SolveArm(Transform arm, Transform elbowJoint, Transform hand, Vector3 wristTarget)
        {
            var root = arm.position;
            var elbow = elbowJoint.position;
            var wrist = hand.position;
            float upper = Vector3.Distance(root, elbow), lower = Vector3.Distance(elbow, wrist);
            var axis = wristTarget - root;
            float distance = axis.magnitude;
            if (distance < 1e-6f || upper < 1e-6f || lower < 1e-6f)
                throw new InvalidOperationException("Degenerate companion arm lengths");
            axis /= distance;
            var pole = Vector3.ProjectOnPlane(elbow - root, axis);
            if (pole.sqrMagnitude < 1e-10f) throw new InvalidOperationException("Degenerate companion elbow plane");
            distance = Mathf.Clamp(distance, Mathf.Abs(upper - lower) + .00001f, upper + lower - .00001f);
            float along = (upper * upper + distance * distance - lower * lower) / (2 * distance);
            var elbowTarget = root + axis * along + pole.normalized * Mathf.Sqrt(Mathf.Max(0, upper * upper - along * along));
            var rotation = hand.rotation;
            arm.rotation = Quaternion.FromToRotation(elbow - root, elbowTarget - root) * arm.rotation;
            elbowJoint.rotation = Quaternion.FromToRotation(hand.position - elbowJoint.position,
                root + axis * distance - elbowJoint.position) * elbowJoint.rotation;
            hand.rotation = rotation;
        }
        void FocusHeadphones(VisionChoreography choreography)
        {
            if (choreography.Prop != VisionProp.Headphones) return;
            float progress = Mathf.Clamp01(choreography.PhaseTime / choreography.Duration(choreography.Clip));
            float weight;
            bool both = true;
            switch (choreography.Clip)
            {
                case "HeadphonesEquip":
                    weight = progress <= .82f ? Mathf.InverseLerp(.58f, .72f, progress) : 1 - Mathf.InverseLerp(.82f, 1, progress);
                    break;
                case "HeadphonesStow":
                    weight = progress <= .28f ? Mathf.InverseLerp(0, .18f, progress) : 1 - Mathf.InverseLerp(.28f, .42f, progress);
                    break;
                case "HeadphonesAdjust":
                    weight = progress <= .64f ? Mathf.InverseLerp(0, .36f, progress) : 1 - Mathf.InverseLerp(.64f, 1, progress);
                    both = false;
                    break;
                default: return;
            }
            weight = Mathf.SmoothStep(0, 1, weight) * blend;
            if (weight <= 0) return;
            if (rightArm == null || rightElbow == null || rightHand == null ||
                leftArm == null || leftElbow == null || leftHand == null || sourceLeftHand == null)
                throw new InvalidOperationException("Headphone contact needs complete articulated arms");
            var prop = props["Headphones"];
            Place(prop, false);
            var position = prop.Root.position;
            var rotation = prop.Root.rotation;
            Place(prop, true);
            var correction = Quaternion.Slerp(Quaternion.identity, rotation * Quaternion.Inverse(prop.Root.rotation), weight);
            rightHand.rotation = correction * rightHand.rotation;
            if (both) leftHand.rotation = correction * leftHand.rotation;
            Place(prop, true);
            SolveArm(rightArm, rightElbow, rightHand, rightHand.position + (position - prop.Root.position) * weight);
            if (!both) return;
            var sourceHead = prop.Source.Bone;
            var sourcePosition = sourceHead.TransformPoint(prop.Asset.position);
            var sourceRotation = sourceHead.rotation * prop.Asset.rotation;
            var relative = Quaternion.Inverse(sourceRotation) * (sourceLeftHand.position - sourcePosition);
            var target = position + rotation * relative * bodyScale;
            SolveArm(leftArm, leftElbow, leftHand, Vector3.Lerp(leftHand.position, target, weight));
        }
        Quaternion ReadingTurn(Camera camera, Vector2 point, VisionChoreography choreography, float delta)
        {
            bool reading = choreography.Active == VisionRoutine.Read;
            if (!reading) readingSide = 0;
            if (reading && readingSide == 0 && choreography.CurrentObservation && camera != null && head != null)
                readingSide = point.x < camera.WorldToScreenPoint(head.position).x ? -1 : 1;
            float target = reading && choreography.Phase != VisionPhase.Exit ? readingSide * 24 : 0;
            readingTurn = Mathf.MoveTowards(readingTurn, target, delta * 48);
            return camera == null ? Quaternion.identity : Quaternion.AngleAxis(-readingTurn, camera.transform.up);
        }
        void Aim(Camera camera, Vector2 point, VisionChoreography choreography, float delta)
        {
            if (camera == null || head == null || !choreography.CurrentObservation || choreography.Observation == null) return;
            var headScreen = camera.WorldToScreenPoint(head.position);
            var relative = point - new Vector2(headScreen.x, headScreen.y);
            bool reading = choreography.Active == VisionRoutine.Read;
            float yawLimit = reading ? 8 : 18, pitchLimit = reading ? 6 : 10;
            float yaw = Mathf.Clamp(relative.x / Mathf.Max(1, Screen.width) * 30, -yawLimit, yawLimit);
            float pitch = Mathf.Clamp(-relative.y / Mathf.Max(1, Screen.height) * 18, -pitchLimit, pitchLimit);
            if (choreography.Active == VisionRoutine.Read && !choreography.Lowered)
            {
                yaw += Mathf.Sin(choreography.PhaseTime * .75f) * 1.4f;
                pitch += Mathf.Sin(choreography.PhaseTime * .3f) * .5f;
            }
            float amount = 1 - Mathf.Exp(-delta * 5);
            lookYaw = Mathf.Lerp(lookYaw, yaw, amount); lookPitch = Mathf.Lerp(lookPitch, pitch, amount);
            head.localRotation *= Quaternion.Euler(lookPitch, lookYaw, 0);
            var eyeRotation = Quaternion.Euler(lookPitch * .3f, lookYaw * .3f, 0);
            if (leftEye != null) leftEye.localRotation = leftEyeBase * eyeRotation;
            if (rightEye != null) rightEye.localRotation = rightEyeBase * eyeRotation;
            eyesOwned = true;
        }
        void ApplyFace(VisionChoreography choreography)
        {
            if (face == null) return;
            if (!faceOwned)
            {
                previousJoy = face.GetValue(joy); previousSorrow = face.GetValue(sorrow);
                previousFun = face.GetValue(fun); previousMouth = face.GetValue(mouth); faceOwned = true;
            }
            float strength = choreography.Accent == "none" ? 0 : Mathf.Sin(Mathf.Clamp01(choreography.AccentTime / 1.8f) * Mathf.PI);
            face.ImmediatelySetValue(joy, Mathf.Max(previousJoy, choreography.Accent == "smile" || choreography.Accent == "laugh" ? strength * .7f : 0));
            face.ImmediatelySetValue(sorrow, Mathf.Max(previousSorrow, choreography.Accent == "concern" ? strength * .35f : 0));
            face.ImmediatelySetValue(fun, Mathf.Max(previousFun, choreography.Accent == "think" ? strength * .15f : 0));
            face.ImmediatelySetValue(mouth, Mathf.Max(previousMouth, choreography.Accent == "surprise" ? strength * .35f : 0));
        }
        void Place(PropInstance prop, bool carry)
        {
            var sourceAnchor = carry && prop.CarrySource != null ? prop.CarrySource : prop.Source;
            var targetAnchor = carry && prop.CarryTarget != null ? prop.CarryTarget : prop.Target;
            var sourceBone = sourceAnchor.Bone;
            var targetBone = targetAnchor.Bone;
            var correction = targetAnchor.Rotation * Quaternion.Inverse(sourceAnchor.Rotation);
            var localRotation = carry ? prop.Asset.carryRotation : prop.Asset.rotation;
            var rotation = correction * sourceBone.rotation * localRotation;
            Vector3 position;
            if (prop.Asset.hasGrip && !carry)
            {
                var grip = targetBone.position + correction * sourceBone.TransformVector(prop.Asset.boneGrip) * bodyScale;
                position = grip - rotation * prop.Asset.meshGrip * bodyScale;
            }
            else
            {
                var offset = carry ? prop.Asset.carryPosition : prop.Asset.position;
                position = targetBone.position + correction * sourceBone.TransformVector(offset) * bodyScale;
            }
            prop.Root.SetPositionAndRotation(position, rotation);
        }
        void SetProps(VisionChoreography choreography)
        {
            VisibleProps = 0;
            var events = clips[choreography.Clip].events;
            foreach (var pair in props)
            {
                bool required =
                    choreography.Prop == VisionProp.Magnifier && pair.Key == "Magnifier" ||
                    choreography.Prop == VisionProp.Notebook && (pair.Key == "Notebook" || pair.Key == "Pencil") ||
                    choreography.Prop == VisionProp.Headphones && pair.Key == "Headphones" ||
                    choreography.Prop == VisionProp.Popcorn && (pair.Key == "PopcornBox" || pair.Key == "PopcornKernel");
                bool visible = required && choreography.Phase != VisionPhase.Enter && pair.Key != "PopcornKernel";
                bool carry = false;
                if (required && events != null)
                    foreach (var item in events)
                    {
                        if (item.prop != pair.Key || choreography.PhaseTime < item.time) continue;
                        if (item.operation == "attach_show") visible = true;
                        if (item.operation == "hide_detach") visible = false;
                        carry = item.carry;
                    }
                var prop = pair.Value;
                if (prop.Root.gameObject.activeSelf != visible) prop.Root.gameObject.SetActive(visible);
                if (visible) { Place(prop, carry); VisibleProps++; }
            }
        }
        public void Hide()
        {
            Applied = false; VisibleProps = 0; blend = 0;
            foreach (var prop in props.Values) if (prop.Root != null) prop.Root.gameObject.SetActive(false);
            if (mouseTracking != null) mouseTracking.enabled = trackingBefore;
            readingSide = 0; readingTurn = 0;
            if (eyesOwned)
            {
                if (leftEye != null) leftEye.localRotation = leftEyeBase;
                if (rightEye != null) rightEye.localRotation = rightEyeBase;
                eyesOwned = false;
            }
            if (faceOwned && face != null)
            {
                face.ImmediatelySetValue(joy, previousJoy); face.ImmediatelySetValue(sorrow, previousSorrow);
                face.ImmediatelySetValue(fun, previousFun); face.ImmediatelySetValue(mouth, previousMouth);
            }
            faceOwned = false;
        }
        static void DestroyOwned(GameObject value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
        void ReleaseAvatar()
        {
            Hide();
            foreach (var prop in props.Values) if (prop.Root != null) DestroyOwned(prop.Root.gameObject);
            props.Clear();
            if (graph.IsValid()) graph.Destroy();
            bones.Clear(); sourceBones.Clear(); sourceHips = targetHips = null;
            targetSpine = targetNeck = null;
            head = leftEye = rightEye = null;
            rightArm = rightElbow = rightHand = null; readingFaceReady = false;
            leftArm = leftElbow = leftHand = sourceLeftHand = null;
            if (proxy != null) DestroyOwned(proxy);
            avatar = sampler = null; proxy = null; playing = null; lookYaw = lookPitch = 0;
        }
        public void Dispose() { ReleaseAvatar(); }
    }
}
