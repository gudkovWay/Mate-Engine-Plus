using System;
using System.Collections.Generic;
using MateEngine.Vision;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public static class CompanionVisionChecks
{
    static VisionPacket Packet(string routine = "read", string accent = "none", long sequence = 1, int epoch = 1) =>
        new VisionPacket { version = 2, lease = "native-check", observer = "native-check", sequence = sequence, identity = "0xa:123", epoch = epoch,
            observedUtc = 100, expiresUtc = 145, permitted = true, providerReady = true, confidence = .9f,
            routine = routine, accent = accent, targetX = .1f, targetY = .2f, targetWidth = .5f, targetHeight = .4f,
            windowWidth = 800, windowHeight = 600 };
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Vision choreography: " + message);
    }
    static void Advance(VisionChoreography value, float seconds, bool suspended = false)
    {
        for (int i = 0; i < Mathf.CeilToInt(seconds * 100); i++) value.Tick(.01f, suspended);
    }
    public static void Validate()
    {
        var lease = new VisionLease();
        lease.Update("0xa:123", "Channel", 1, true, 99);
        var bound = Packet();
        bound.lease = lease.Control.lease; bound.epoch = lease.Control.epoch;
        Require(lease.Accepts(bound, 100), "current native permission lease accepts its captured frame");
        lease.Update("0xa:123", "Channel", 3, true, 100);
        Require(!lease.Accepts(bound, 100), "native A→B→A revision rejects a missed foreground round trip");
        bound.epoch = lease.Control.epoch;
        bound.observedUtc = 99;
        Require(!lease.Accepts(bound, 100), "a new heartbeat cannot relabel an old captured frame");
        bound.observedUtc = 100;
        lease.Update("0xa:123", "Channel", 3, false, 100);
        Require(!lease.Accepts(bound, 100), "disabled native permission revokes an in-flight result");

        var monotonic = new VisionChoreography();
        monotonic.Observe(Packet(sequence: 5), "0xa:123", 100);
        Require(!monotonic.Observe(Packet(sequence: 4), "0xa:123", 100) && !monotonic.CurrentObservation,
            "a regressing producer packet cannot leave observation current");
        var read = new VisionChoreography();
        Require(read.Observe(Packet(), "0xa:123", 100), "current permitted context accepted");
        Advance(read, 1.3f);
        Require(read.Clip == "MagnifierEquip" && read.Prop == VisionProp.Magnifier, "stable reading equips the real magnifier");
        Advance(read, 1.9f);
        Require(read.Phase == VisionPhase.Hold && read.Clip == "MagnifierHold", "reading enters sustained hold without re-equipping");
        var scrolling = Packet(sequence: 2, epoch: 2); scrolling.scrolling = true;
        read.Observe(scrolling, "0xa:123", 100);
        Advance(read, .1f);
        Require(read.Clip == "MagnifierLower", "scrolling lowers the lens before content settles");
        Advance(read, .7f);
        Require(read.Lowered, "fast scrolling holds the lens at the chest");
        var stopped = Packet(sequence: 3, epoch: 2);
        read.Observe(stopped, "0xa:123", 100);
        Advance(read, .1f);
        Require(read.Clip == "MagnifierRaise", "stopped scrolling raises the existing lens rather than equipping another");
        Advance(read, .7f);
        Require(read.Clip == "MagnifierHold", "reading resumes after the raise");
        var readingAccent = new VisionChoreography();
        readingAccent.Observe(Packet(accent: "think"), "0xa:123", 100); Advance(readingAccent, 3.2f);
        Require(readingAccent.Clip == "MagnifierLower", "a reading accent frees the face by lowering the existing lens");
        Advance(readingAccent, 2.6f);
        Require(readingAccent.Clip == "MagnifierHold", "reading accent finishes by raising the same lens");
        foreach (var pair in new[] { ("study", "think", "NotebookThink"), ("film", "surprise", "PopcornPause") })
        {
            var accent = new VisionChoreography(); accent.Observe(Packet(pair.Item1, pair.Item2), "0xa:123", 100);
            Advance(accent, 2.8f);
            Require(accent.Clip == pair.Item3, "content accent uses its held prop: " + pair.Item1);
        }

        read.Observe(Packet("study", sequence: 4, epoch: 3), "0xa:123", 100);
        Advance(read, 1.4f);
        Require(read.Prop == VisionProp.Magnifier && read.Phase == VisionPhase.Exit, "old prop remains owned through stow");
        Advance(read, 2f);
        Require(read.Prop == VisionProp.Notebook && read.Clip == "NotebookEquip", "notebook starts only after magnifier exit");
        Advance(read, 1.5f);
        Require(read.Active == VisionRoutine.Study && read.Clip == "NotebookHold", "study holds the notebook");
        read.Observe(Packet("work", sequence: 5, epoch: 4), "0xa:123", 100);
        Advance(read, 1.4f);
        Require(read.Active == VisionRoutine.Work && read.Phase == VisionPhase.Hold, "shared notebook survives study-to-work change");

        Advance(read, .1f, true);
        Require(read.Prop == VisionProp.None && read.Phase == VisionPhase.Idle, "manual ownership immediately clears auto props");
        Advance(read, 1.3f);
        Require(read.Clip == "NotebookEquip", "valid context resumes through a clean entry after manual release");
        read.Observe(Packet(sequence: 6), "0xb:124", 100);
        Advance(read, 4);
        Require(!read.ActivePerformance, "a switched-away context cannot keep acting");

        foreach (var pair in new[] {
            ("chat", VisionProp.None, "ChatHold"), ("work", VisionProp.Notebook, "NotebookHold"),
            ("podcast", VisionProp.Headphones, "HeadphonesHold"), ("film", VisionProp.Popcorn, "PopcornHold"),
            ("entertainment", VisionProp.None, "WatchHold") })
        {
            var routine = new VisionChoreography(); routine.Observe(Packet(pair.Item1), "0xa:123", 100);
            Advance(routine, 4);
            Require(routine.Prop == pair.Item2 && routine.Clip == pair.Item3, "distinct activity performance: " + pair.Item1);
        }
        var stale = new VisionChoreography();
        Require(!stale.Observe(Packet(), "0xa:123", 146), "expired inference rejected");
        var invalid = Packet(); invalid.targetX = float.NaN;
        Require(!stale.Observe(invalid, "0xa:123", 100), "non-finite gaze rejected");
        invalid = Packet(); invalid.confidence = .1f;
        Require(!stale.Observe(invalid, "0xa:123", 100), "uncertain classification remains neutral");
        invalid = Packet(); invalid.permitted = false;
        Require(!stale.Observe(invalid, "0xa:123", 100), "privacy denial rejects even an otherwise valid frame");
        invalid = Packet(); invalid.providerReady = false;
        Require(!stale.Observe(invalid, "0xa:123", 100), "provider failure cannot masquerade as observation");
        var happy = new VisionChoreography();
        happy.Observe(Packet("entertainment", "laugh"), "0xa:123", 100); Advance(happy, 2);
        Require(happy.Accent == "laugh", "a supported one-shot accent is visible");
        Advance(happy, 3);
        Require(happy.Accent == "none", "one-shot accent terminates");
        happy.Observe(Packet("entertainment", "laugh", 2), "0xa:123", 100); Advance(happy, 1);
        Require(happy.Accent == "none", "observer heartbeat cannot replay the same inferred joke");
        var first = new GameObject("Vision audio first").AddComponent<Animator>();
        var second = new GameObject("Vision audio second").AddComponent<Animator>();
        try
        {
            VisionAudioPolicy.Claim(first);
            Require(VisionAudioPolicy.Owns(first) && !VisionAudioPolicy.Owns(second), "semantic audio has one avatar owner");
            VisionAudioPolicy.Release(second);
            Require(VisionAudioPolicy.Owns(first), "an unrelated release cannot restart automatic dance");
            VisionAudioPolicy.Release(first);
            Require(!VisionAudioPolicy.Owns(first), "semantic audio ownership releases cleanly");
        }
        finally { UnityEngine.Object.DestroyImmediate(first.gameObject); UnityEngine.Object.DestroyImmediate(second.gameObject); }
        Debug.Log("Vision choreography behavioral checks passed");
    }
    public static void ValidateSamplingRig(GameObject rig, string[] bones)
    {
        var instance = UnityEngine.Object.Instantiate(rig);
        try
        {
            var animator = instance.GetComponent<Animator>();
            Require(animator != null && animator.avatar != null && animator.avatar.isValid && !animator.isHuman,
                "imported source retains raw skeletal animation");
            var transforms = new Dictionary<string, Transform>();
            foreach (var node in instance.GetComponentsInChildren<Transform>(true)) transforms.Add(node.name, node);
            foreach (var name in bones)
                Require(Enum.TryParse(name, out HumanBodyBones bone) && transforms.ContainsKey(name),
                    "authored upper-body/finger mapping exists: " + name);
            ValidateReadingPose(animator, transforms);
        }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
    }
    static void ValidateReadingPose(Animator animator, Dictionary<string, Transform> bones)
    {
        var graph = PlayableGraph.Create("Vision reading pose regression");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        var output = AnimationPlayableOutput.Create(graph, "Authored reading pose", animator);
        animator.runtimeAnimatorController = null;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        try
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(CompanionVisionImporter.Model))
            {
                if (!(asset is AnimationClip clip) || clip.name != "MagnifierHold") continue;
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false); playable.SetSpeed(0);
                output.SetSourcePlayable(playable); graph.Play();
                foreach (float time in new[] { 0f, clip.length * .5f, clip.length })
                {
                    playable.SetTime(time); graph.Evaluate(0);
                    var hips = bones["Hips"];
                    var head = bones["Head"];
                    var left = bones["LeftHand"];
                    var right = bones["RightHand"];
                    var elbow = bones["RightLowerArm"];
                    var fingers = bones["RightMiddleProximal"];
                    var up = animator.transform.up;
                    float height = Vector3.Dot(head.position - hips.position, up);
                    float leftHeight = Vector3.Dot(left.position - hips.position, up) / height;
                    float rightHeight = Vector3.Dot(right.position - hips.position, up) / height;
                    float wrist = Vector3.Dot((right.position - elbow.position).normalized, (fingers.position - right.position).normalized);
                    Debug.Log($"Vision reading sample {animator.name} t={time:F2} leftHeight={leftHeight:F3} rightHeight={rightHeight:F3} wristForward={wrist:F3}");
                    Require(height > 0 && leftHeight < .55f, "reading leaves the free hand below the chest");
                    Require(rightHeight > .55f && rightHeight < 1.2f, "magnifier wrist stays between chest and eye level");
                    Require(wrist > .05f, "magnifier wrist does not fold backwards against the forearm");
                    var middle = bones["RightMiddleIntermediate"];
                    var distal = bones["RightMiddleDistal"];
                    float curl = Vector3.Dot((fingers.position - right.position).normalized, (distal.position - middle.position).normalized);
                    Debug.Log($"Vision source magnifier grip curl={curl:F3}");
                    Require(curl < .25f, "imported authored fingers retain the closed magnifier grip");
                }
                return;
            }
            Require(false, "reading clip is available for pose regression");
        }
        finally { graph.Destroy(); }
    }
    public static void ValidateSelectedAvatar(string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Require(prefab != null, "selected-avatar pose fixture exists");
        CompanionTailChecks.Validate(prefab);
        var previousCamera = Camera.main;
        GameObject ownedCamera = null;
        try
        {
            if (previousCamera != null) previousCamera.tag = "Untagged";
            ownedCamera = new GameObject("Vision pose check camera", typeof(Camera));
            ownedCamera.tag = "MainCamera";
            Require(Camera.main == ownedCamera.GetComponent<Camera>(), "pose checks use their owned camera");
            var left = ValidateSelectedPose(prefab, false, -1);
            var right = ValidateSelectedPose(prefab, false, 1);
            Require(Quaternion.Angle(left, right) > 30, "reading turns the torso toward either side of the text");
            ValidateSelectedPose(prefab, true, -1);
            ValidateSelectedPose(prefab, false, -1, true);
            ValidateSelectedPose(prefab, false, -1, routine: "podcast");
            ValidateSelectedPose(prefab, true, -1, routine: "podcast");
            ValidateSelectedPose(prefab, true, -1, frame: "FrameLie");
            ValidateSelectedPose(prefab, true, 1, frame: "FrameWatch");
            ValidateSelectedPose(prefab, true, -1, routine: "film", frame: "FrameLie");
            ValidateSelectedPose(prefab, true, 1, routine: "film", frame: "FrameWatch");
            ValidateSelectedPose(prefab, true, -1, routine: "podcast", frame: "FrameLie");
        }
        finally
        {
            if (ownedCamera != null) UnityEngine.Object.DestroyImmediate(ownedCamera);
            if (previousCamera != null) previousCamera.tag = "MainCamera";
        }
    }
    static Quaternion ValidateSelectedPose(GameObject prefab, bool seated, int side, bool accent = false, string routine = "read", string frame = null)
    {
        var instance = UnityEngine.Object.Instantiate(prefab);
        VisionPerformance performance = null;
        try
        {
            var animator = instance.GetComponentInChildren<Animator>(true);
            Require(animator != null && animator.isHuman, "selected-avatar fixture is humanoid");
            instance.SetActive(true); animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                "Assets/AnimatorController/AvatarAnimatorControllerV2 1.controller");
            Require(animator.runtimeAnimatorController != null, "selected-avatar uses the native base controller");
            animator.Rebind(); animator.Update(0);
            var initialHead = animator.GetBoneTransform(HumanBodyBones.Head);
            var initialEyeCenter = (animator.GetBoneTransform(HumanBodyBones.LeftEye).position +
                animator.GetBoneTransform(HumanBodyBones.RightEye).position) * .5f;
            var initialFace = Vector3.ProjectOnPlane(initialEyeCenter - initialHead.position, animator.transform.up);
            Require(initialFace.sqrMagnitude > 1e-10f, "initialized selected-avatar has a physical face direction");
            Camera.main.transform.position = initialHead.position + initialFace.normalized * 3;
            Camera.main.transform.rotation = Quaternion.LookRotation(-initialFace, animator.transform.up);
            Require(Vector3.Dot(initialFace, Camera.main.transform.position - initialHead.position) > 0,
                "selected-avatar fixture camera views the initialized face, not the back of the prefab");
            animator.SetBool("isWindowSit", seated);
            animator.Play(frame != null ? "Base Layer." + frame : seated ? "Base Layer.WindowSit" : "Base Layer.Idle", 0, 0);
            animator.Update(0);
            var policy = new VisionChoreography();
            performance = new VisionPerformance(policy);
            Require(performance.Ready, "selected-avatar uses the real vision library");
            policy.Observe(Packet(routine: routine, accent: accent ? "think" : "none"), "0xa:123", 100);
            if (routine == "podcast") ValidateHeadphoneHandoff(animator, performance, policy);
            Advance(policy, accent ? 4 : 3.2f);
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            var eye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            var eyeNeutral = eye != null ? eye.localRotation : Quaternion.identity;
            var lowerBody = new[] { animator.transform, hips,
                animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg), animator.GetBoneTransform(HumanBodyBones.RightLowerLeg),
                animator.GetBoneTransform(HumanBodyBones.LeftFoot), animator.GetBoneTransform(HumanBodyBones.RightFoot) };
            var positions = new Vector3[lowerBody.Length];
            var rotations = new Quaternion[lowerBody.Length];
            Quaternion settled = Quaternion.identity;
            for (int i = 0; i < 120; i++)
            {
                animator.Update(1f / 60);
                var spineBefore = animator.GetBoneTransform(HumanBodyBones.Spine);
                var neckBefore = animator.GetBoneTransform(HumanBodyBones.Neck);
                var nativeTorso = (neckBefore.position - spineBefore.position).normalized;
                for (int bone = 0; bone < lowerBody.Length; bone++)
                {
                    positions[bone] = lowerBody[bone].position;
                    rotations[bone] = lowerBody[bone].rotation;
                }
                var point = Camera.main.WorldToScreenPoint(head.position);
                performance.Apply(animator, policy, new Vector2(point.x + (i < 60 ? side : -side) * 10000, point.y), 1f / 60);
                for (int bone = 0; bone < lowerBody.Length; bone++)
                    Require(Vector3.Distance(positions[bone], lowerBody[bone].position) < .00001f &&
                        Quaternion.Angle(rotations[bone], lowerBody[bone].rotation) < .01f,
                        "contextual upper body preserves native root, hips, knees and foot contact");
                if (frame != null)
                    Require(Vector3.Dot(nativeTorso, (neckBefore.position - spineBefore.position).normalized) > .85f,
                        "contextual activity preserves the native lying torso instead of straightening or over-rolling the supported model");
                if (i == 59) settled = chest.rotation;
            }
            if (routine == "podcast")
            {
                Require(performance.Applied && performance.VisibleProps == 1, "real podcast performance and headphones are applied");
                ValidateHeadphones(animator);
                policy.Observe(Packet(routine: "film", sequence: 2), "0xa:123", 100);
                ValidateHeadphoneHandoff(animator, performance, policy, true);
                return chest.rotation;
            }
            Require(Quaternion.Angle(settled, chest.rotation) < 15, "reading keeps its chosen side instead of oscillating with the gaze target");
            Require(eye == null || Quaternion.Angle(eyeNeutral, eye.localRotation) < 15, "reading gaze does not accumulate eye rotation");
            if (frame != null) return chest.rotation;
            var left = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var right = animator.GetBoneTransform(HumanBodyBones.RightHand);
            var elbow = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            var fingers = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            var up = animator.transform.up;
            float height = Vector3.Dot(head.position - hips.position, up);
            var spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            Require(Vector3.Dot((head.position - spine.position).normalized, up) > .94f,
                "reading keeps a restrained torso lean instead of inheriting the seated hip roll");
            float leftHeight = Vector3.Dot(left.position - hips.position, up) / height;
            float rightHeight = Vector3.Dot(right.position - hips.position, up) / height;
            float wrist = Vector3.Dot((right.position - elbow.position).normalized, (fingers.position - right.position).normalized);
            Debug.Log($"Vision selected-avatar sample {prefab.name} seated={seated} accent={accent} side={side} leftHeight={leftHeight:F3} rightHeight={rightHeight:F3} wristForward={wrist:F3}");
            Require(performance.Applied && performance.VisibleProps == 1, "real reading performance and magnifier are applied");
            Require(height > 0 && leftHeight < .55f, "selected-avatar free hand remains below the chest over native idle or seating");
            Require(rightHeight > (accent ? .1f : .55f) && rightHeight < (accent ? .85f : 1.2f),
                "selected-avatar magnifier stays raised for reading and held at the chest during an accent");
            Require(wrist > .05f, "selected-avatar magnifier wrist does not fold backwards");
            var middle = animator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate);
            var distal = animator.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
            float curl = Vector3.Dot((fingers.position - right.position).normalized, (distal.position - middle.position).normalized);
            Debug.Log($"Vision magnifier grip curl={curl:F3} lens={performance.MagnifierCenter}");
            Require(curl < .25f, "magnifier grip folds the fingers around the handle instead of leaving an open palm");
            if (!accent)
            {
                var rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
                Require(rightEye != null, "selected-avatar has the actual reading-eye landmark");
                var lensOffset = performance.MagnifierCenter - rightEye.position;
                Require(Mathf.Abs(Vector3.Dot(lensOffset, head.up)) < height * .04f,
                    $"magnifier lens stays in the reading eye plane rather than at the forehead: eye={rightEye.position}, lens={performance.MagnifierCenter}, offset={lensOffset}, height={height:F3}, hips={hips.eulerAngles}, head={head.eulerAngles}");
                var leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
                var facing = Vector3.ProjectOnPlane((leftEye.position + rightEye.position) * .5f - head.position, up);
                Require(Vector3.Dot(facing, Camera.main.transform.right) * side > 0,
                    $"reading faces the selected text side instead of the native idle heading: side={side}, facing={facing}, head={head.eulerAngles}");
                Require(lensOffset.magnitude > height * .1f && lensOffset.magnitude < height * .5f,
                    "magnifier stays clear of the face without floating beyond the reading reach");
            }
            return chest.rotation;
        }
        finally { performance?.Dispose(); UnityEngine.Object.DestroyImmediate(instance); }
    }
    static void ValidateHeadphoneHandoff(Animator animator, VisionPerformance performance, VisionChoreography policy, bool stow = false)
    {
        var library = AssetDatabase.LoadAssetAtPath<VisionLibrary>(CompanionVisionImporter.Folder + "/VisionLibrary.asset");
        float handoff = -1;
        string motion = stow ? "HeadphonesStow" : "HeadphonesEquip";
        foreach (var clip in library.clips)
            if (clip.name == motion && clip.events != null)
                foreach (var item in clip.events)
                    if (item.prop == "Headphones" && item.operation == "reparent" && item.carry == stow) handoff = item.time;
        Require(handoff > .03f, "headphone motion has a timed attachment handoff");
        for (int i = 0; i < 400 && (policy.Clip != motion || policy.PhaseTime < handoff - .015f); i++)
        {
            animator.Update(.01f); policy.Tick(.01f, false);
            var point = Camera.main.WorldToScreenPoint(animator.GetBoneTransform(HumanBodyBones.Head).position);
            performance.Apply(animator, policy, point, .01f);
        }
        Require(policy.Clip == motion && policy.PhaseTime < handoff,
            "handoff regression reaches the source attachment immediately before the event");
        var root = animator.transform.Find("Vision Headphones");
        Require(root != null && root.gameObject.activeSelf, "headphones are visible before handoff");
        var position = root.position;
        var rotation = root.rotation;
        for (int i = 0; i < 8 && policy.PhaseTime < handoff + .015f; i++)
        {
            animator.Update(.01f); policy.Tick(.01f, false);
            var point = Camera.main.WorldToScreenPoint(animator.GetBoneTransform(HumanBodyBones.Head).position);
            performance.Apply(animator, policy, point, .01f);
        }
        Require(policy.Clip == motion && policy.PhaseTime > handoff,
            "handoff regression crosses the real attachment event");
        float distance = Vector3.Distance(position, root.position);
        float angle = Quaternion.Angle(rotation, root.rotation);
        float eyeSpan = Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.LeftEye).position,
            animator.GetBoneTransform(HumanBodyBones.RightEye).position);
        Debug.Log($"Vision selected-avatar {motion} handoff distance={distance:F4} angle={angle:F2}");
        Require(distance < eyeSpan * .4f && angle < 15,
            "headphone attachment handoff preserves the visible pose instead of jumping or twisting");
    }
    static void ValidateHeadphones(Animator animator)
    {
        var root = animator.transform.Find("Vision Headphones");
        Require(root != null && root.gameObject.activeSelf, "worn headphones exist on the selected avatar");
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        var leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
        var rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
        var up = (head.position - neck.position).normalized;
        var across = Vector3.ProjectOnPlane(rightEye.position - leftEye.position, up).normalized;
        var forward = Vector3.Cross(across, up);
        Require(up.sqrMagnitude > .9f && across.sqrMagnitude > .9f, "headphone checks use actual head landmarks");
        var extent = new Bounds();
        bool first = true;
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            var bounds = renderer.localBounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                    (i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var world = renderer.transform.TransformPoint(corner) - head.position;
                var point = new Vector3(Vector3.Dot(world, across), Vector3.Dot(world, up), Vector3.Dot(world, forward));
                if (first) { extent = new Bounds(point, Vector3.zero); first = false; }
                else extent.Encapsulate(point);
            }
        }
        Require(!first, "headphones have actual rendered bounds");
        Debug.Log($"Vision selected-avatar headphone extent={extent.size} headMax={extent.max.y:F3}");
        Require(extent.size.y > extent.size.z * 1.5f, "headphone arch rises over the head instead of lying across the eyes");
        float eyeHeight = Vector3.Dot((leftEye.position + rightEye.position) * .5f - head.position, up);
        Require(extent.max.y > eyeHeight + Vector3.Distance(leftEye.position, rightEye.position) * .5f,
            "headphone arch clears the actual eye line");
    }
}
