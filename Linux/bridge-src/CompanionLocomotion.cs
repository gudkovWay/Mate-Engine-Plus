using System;
using System.Collections.Generic;
using UnityEngine;
using APIs.Hyprland;

namespace MateEngine.Desktop
{

    public sealed class CompanionLocomotion : MonoBehaviour
    {
        static readonly HashSet<Animator> owners = new HashSet<Animator>();
        public static bool Owns(Animator value) { return value != null && owners.Contains(value); }
        public readonly CompanionJourney Journey = new CompanionJourney();
        public bool Leased { get { return animator != null && Owns(animator); } }
        public bool Ready { get { return controller != null; } }
        Animator animator;
        RuntimeAnimatorController controller, previousController;
        AnimatorUpdateMode previousUpdateMode;
        AnimatorCullingMode previousCulling;
        bool previousRootMotion;
        Transform root;
        Vector3 rootPosition;
        Quaternion rootRotation;
        MonoBehaviour[] suspended;
        bool[] enabledBefore;
        struct ParameterValue
        {
            public int Hash, Integer;
            public float Float;
            public bool Boolean;
            public AnimatorControllerParameterType Type;
        }
        ParameterValue[] previousParameters;
        AnimatorStateInfo[] previousStates;
        float[] previousWeights;
        CompanionMotionPhase applied;
        float cycleLength;
        bool auditionEpisode, auditionRestPending;
        int auditionSamples;
        float auditionNextSample;
        CompanionMotionPhase auditionPhase;
        Transform auditionLeft, auditionRight, auditionHips;
        Vector3 auditionRestLeft, auditionRestRight, auditionRestHips, auditionRestScale;
        HyprlandManager auditionManager;

        void Awake()
        {
            controller = Resources.Load<RuntimeAnimatorController>("CompanionMotion/Companion");
            if (controller == null) return;
            foreach (var clip in controller.animationClips)
                if (clip.name == "WalkRight") cycleLength = clip.length;
        }

        public bool TryGetFootAnchor(Animator avatar, Camera camera, Vector2Int windowSize, out Vector2 anchor)
        {
            anchor = Vector2.zero;
            if (avatar == null || !avatar.isHuman || camera == null || Screen.width <= 0 || Screen.height <= 0) return false;
            var left = avatar.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = avatar.GetBoneTransform(HumanBodyBones.RightFoot);
            if (left == null || right == null) return false;
            var point = camera.WorldToScreenPoint((left.position + right.position) * .5f);
            if (point.z <= 0f) return false;
            anchor = new Vector2(point.x * windowSize.x / Screen.width, (Screen.height - point.y) * windowSize.y / Screen.height);
            return true;
        }
        public float StridePixels(Animator avatar, Camera camera, Vector2Int windowSize)
        {
            if (avatar == null || camera == null || Screen.width <= 0) return 0;
            var origin = camera.WorldToScreenPoint(avatar.transform.position);
            var unit = camera.WorldToScreenPoint(avatar.transform.position + camera.transform.right);
            return Mathf.Abs(unit.x - origin.x) * windowSize.x / Screen.width *
                CompanionJourney.AuthoringAdvance * avatar.humanScale / CompanionJourney.AuthoringHumanScale;
        }

        public bool Begin(Animator avatar, Vector2 origin, Vector2 endpoint, float stridePixels, bool audition = false)
        {
            if (Leased || !Ready || cycleLength <= 0f || avatar == null || !avatar.isHuman || !avatar.isActiveAndEnabled) return false;
            animator = avatar;
            previousController = avatar.runtimeAnimatorController;
            var parameters = avatar.parameters;
            previousParameters = new ParameterValue[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var value = new ParameterValue { Hash = parameter.nameHash, Type = parameter.type };
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Float: value.Float = avatar.GetFloat(parameter.nameHash); break;
                    case AnimatorControllerParameterType.Int: value.Integer = avatar.GetInteger(parameter.nameHash); break;
                    case AnimatorControllerParameterType.Bool: value.Boolean = avatar.GetBool(parameter.nameHash); break;
                }
                previousParameters[i] = value;
            }
            previousStates = new AnimatorStateInfo[avatar.layerCount];
            previousWeights = new float[avatar.layerCount];
            for (int i = 0; i < previousStates.Length; i++)
            {
                previousStates[i] = avatar.GetCurrentAnimatorStateInfo(i);
                previousWeights[i] = avatar.GetLayerWeight(i);
            }
            previousRootMotion = avatar.applyRootMotion;
            previousUpdateMode = avatar.updateMode;
            previousCulling = avatar.cullingMode;
            root = avatar.transform;
            rootPosition = root.localPosition;
            rootRotation = root.localRotation;
            auditionEpisode = audition;
            if (audition)
            {
                auditionSamples = 0;
                auditionNextSample = 0f;
                auditionPhase = CompanionMotionPhase.Idle;
                auditionRestPending = true;
                auditionLeft = avatar.GetBoneTransform(HumanBodyBones.LeftFoot);
                auditionRight = avatar.GetBoneTransform(HumanBodyBones.RightFoot);
                auditionHips = avatar.GetBoneTransform(HumanBodyBones.Hips);
                auditionRestLeft = auditionLeft.position;
                auditionRestRight = auditionRight.position;
                auditionRestHips = auditionHips.position;
                auditionRestScale = root.localScale;
            }
            owners.Add(avatar);
            SuspendWriters();
            avatar.applyRootMotion = false;
            avatar.updateMode = AnimatorUpdateMode.UnscaledTime;
            avatar.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            avatar.runtimeAnimatorController = controller;
            Journey.Begin(origin, endpoint, cycleLength, stridePixels);
            applied = CompanionMotionPhase.Idle;
            ApplyPhase();
            return true;
        }

        void SuspendWriters()
        {
            // The receiver/controller can live above the imported VRM's Animator.
            var candidates = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var selected = new List<MonoBehaviour>();
            foreach (var item in candidates)
            {
                if (item is AvatarLocomotionController locomotion && (locomotion.Animator == animator || locomotion.GetComponent<Animator>() == animator) ||
                    item is AvatarSwayController sway && (sway.animator == animator || sway.GetComponent<Animator>() == animator) ||
                    item is AvatarMouseTracking tracking && tracking.GetComponent<Animator>() == animator ||
                    item is AvatarScaleController)
                    selected.Add(item);
            }
            suspended = selected.ToArray();
            enabledBefore = new bool[suspended.Length];
            for (int i = 0; i < suspended.Length; i++)
            {
                enabledBefore[i] = suspended[i].enabled;
                suspended[i].enabled = false;
            }
        }

        public void Tick(float delta)
        {
            if (!Leased) return;
            if (!animator.isActiveAndEnabled || animator.runtimeAnimatorController != controller) { Cancel(); return; }
            Journey.Tick(delta);
            if (!Journey.Active) { Cancel(); return; }
            ApplyPhase();
        }

        void ApplyPhase()
        {
            if (Journey.Phase == applied) return;
            applied = Journey.Phase;
            string state;
            switch (applied)
            {
                case CompanionMotionPhase.Turn: state = "Turn"; break;
                case CompanionMotionPhase.Start: state = "Start"; break;
                case CompanionMotionPhase.Steps: state = Journey.Facing < 0 ? "WalkLeft" : "WalkRight"; break;
                case CompanionMotionPhase.Stop: state = "Stop"; break;
                case CompanionMotionPhase.Invite: state = "Invite"; break;
                case CompanionMotionPhase.Wait: state = "Wait"; break;
                case CompanionMotionPhase.Settle: state = "Settle"; break;
                default: state = "Notice"; break;
            }
            animator.SetFloat("Direction", Journey.Facing);
            animator.CrossFadeInFixedTime(state, applied == CompanionMotionPhase.Notice ? 0f : .08f, 0, 0f);
        }

        public void RecordAudition(HyprlandManager manager, Camera camera)
        {
            if (!auditionEpisode || !Leased || auditionSamples >= 180 || !CompanionAudition.Allows(manager) ||
                camera == null || Screen.width <= 0 || Screen.height <= 0 ||
                auditionLeft == null || auditionRight == null || auditionHips == null) return;
            if (Journey.Phase == auditionPhase && Time.unscaledTime < auditionNextSample) return;
            auditionManager = manager;
            auditionPhase = Journey.Phase;
            auditionNextSample = Time.unscaledTime + .1f;
            var window = manager.CompanionWindow;
            var actual = window.atVector;
            var desired = manager.GetWindowPosition();
            var leftScreen = camera.WorldToScreenPoint(auditionLeft.position);
            var rightScreen = camera.WorldToScreenPoint(auditionRight.position);
            var size = window.sizeVector;
            var leftPixel = new Vector2(leftScreen.x * size.x / Screen.width, (Screen.height - leftScreen.y) * size.y / Screen.height);
            var rightPixel = new Vector2(rightScreen.x * size.x / Screen.width, (Screen.height - rightScreen.y) * size.y / Screen.height);
            var current = animator.GetCurrentAnimatorStateInfo(0);
            var next = animator.GetNextAnimatorStateInfo(0);
            if (auditionRestPending)
            {
                Debug.Log("[CompanionAudition] prelease_pose avatar=" + animator.gameObject.name + ", humanoid=" + animator.avatar.name +
                    ", humanScale=" + animator.humanScale + ", left_world=" + auditionRestLeft.ToString("F6") +
                    ", right_world=" + auditionRestRight.ToString("F6") + ", hips_world=" + auditionRestHips.ToString("F6") +
                    ", root_scale=" + auditionRestScale.ToString("F6"));
                auditionRestPending = false;
            }
            Debug.Log("[CompanionAudition] sample=" + auditionSamples++ + ", frame=" + Time.frameCount +
                ", time=" + Time.unscaledTime + ", phase=" + Journey.Phase + ", phase_time=" + Journey.PhaseTime +
                ", state=" + current.fullPathHash + ", normalized=" + current.normalizedTime +
                ", transition=" + animator.IsInTransition(0) + ", next_state=" + next.fullPathHash + ", next_normalized=" + next.normalizedTime +
                ", window_actual=" + actual + ", window_desired=" + desired + ", window_command=" + Journey.Position.ToString("F6") +
                ", left_world=" + auditionLeft.position.ToString("F6") + ", right_world=" + auditionRight.position.ToString("F6") +
                ", hips_world=" + auditionHips.position.ToString("F6") + ", hips_rotation=" + auditionHips.rotation.ToString("F6") +
                ", left_local_px=" + leftPixel.ToString("F6") + ", right_local_px=" + rightPixel.ToString("F6") +
                ", left_global_px=" + (leftPixel + (Vector2)actual).ToString("F6") +
                ", right_global_px=" + (rightPixel + (Vector2)actual).ToString("F6") +
                ", left_depth=" + leftScreen.z + ", right_depth=" + rightScreen.z +
                ", root_world=" + root.position.ToString("F6") + ", root_rotation=" + root.rotation.ToString("F6") +
                ", root_scale=" + root.localScale.ToString("F6") + ", world_scale=" + root.lossyScale.ToString("F6"));
        }

        public void Viewed() { Journey.Viewed(); }

        public void Cancel()
        {
            if (auditionEpisode && CompanionAudition.Allows(auditionManager))
                Debug.Log("[CompanionAudition] release frame=" + Time.frameCount + ", time=" + Time.unscaledTime +
                    ", phase=" + Journey.Phase + ", samples=" + auditionSamples);
            Journey.Cancel();
            var previous = animator;
            owners.Remove(previous);
            if (previous != null)
            {
                if (previous.runtimeAnimatorController == controller)
                {
                    previous.runtimeAnimatorController = previousController;
                    if (previousController != null)
                    {
                        foreach (var value in previousParameters)
                            switch (value.Type)
                            {
                                case AnimatorControllerParameterType.Float: previous.SetFloat(value.Hash, value.Float); break;
                                case AnimatorControllerParameterType.Int: previous.SetInteger(value.Hash, value.Integer); break;
                                case AnimatorControllerParameterType.Bool: previous.SetBool(value.Hash, value.Boolean); break;
                            }
                        for (int i = 0; i < previousStates.Length && i < previous.layerCount; i++)
                        {
                            previous.SetLayerWeight(i, previousWeights[i]);
                            if (previous.HasState(i, previousStates[i].fullPathHash))
                                previous.Play(previousStates[i].fullPathHash, i, previousStates[i].normalizedTime);
                        }
                    }
                }
                previous.applyRootMotion = previousRootMotion;
                previous.updateMode = previousUpdateMode;
                previous.cullingMode = previousCulling;
                if (root != null) { root.localPosition = rootPosition; root.localRotation = rootRotation; }
                if (previous.isActiveAndEnabled && previous.runtimeAnimatorController == previousController) previous.Update(0f);
            }
            if (suspended != null)
                for (int i = 0; i < suspended.Length; i++)
                    if (suspended[i] != null) suspended[i].enabled = enabledBefore[i];
            suspended = null;
            enabledBefore = null;
            previousParameters = null;
            previousStates = null;
            previousWeights = null;
            animator = null;
            auditionEpisode = false;
            auditionManager = null;
            auditionLeft = auditionRight = auditionHips = null;
        }
        void OnDisable() { Cancel(); }
        void OnDestroy() { Cancel(); }
        void OnApplicationQuit() { Cancel(); }
    }
}
