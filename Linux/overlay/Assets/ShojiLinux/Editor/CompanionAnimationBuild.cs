using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public sealed class CompanionAnimationImporter : AssetPostprocessor
{
    internal const string Folder = "Assets/ShojiLinux/Resources/CompanionMotion";
    internal const string Model = Folder + "/Companion.fbx";
    internal static readonly string[] Actions = { "Notice", "TurnLeft", "TurnRight", "StartLeft", "StartRight", "StopLeft", "StopRight", "Invite", "Wait", "Settle", "WalkLeft", "WalkRight" };
    static readonly string[] Bones = { "Hips", "Spine", "Chest", "Neck", "Head", "LeftShoulder", "RightShoulder", "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm", "LeftHand", "RightHand", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg", "LeftFoot", "RightFoot", "LeftToes", "RightToes" };

    void OnPreprocessModel()
    {
        if (assetPath != Model) return;
        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;
        importer.importCameras = importer.importLights = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.optimizeGameObjects = false;
        importer.animationCompression = ModelImporterAnimationCompression.Off;
        importer.bakeAxisConversion = true;
        // Rebuild the rest skeleton from this FBX, never from a prior import's unit basis.
        var description = new HumanDescription { skeleton = Array.Empty<SkeletonBone>() };
        description.human = Bones.Select(name => new HumanBone { boneName = name, humanName = name,
            limit = new HumanLimit { useDefaultValues = true } }).ToArray();
        description.upperArmTwist = description.lowerArmTwist = .5f;
        description.upperLegTwist = description.lowerLegTwist = .5f;
        description.armStretch = description.legStretch = .05f;
        description.feetSpacing = 0f;
        description.hasTranslationDoF = false;
        importer.humanDescription = description;
    }

    void OnPreprocessAnimation()
    {
        if (assetPath != Model) return;
        var importer = (ModelImporter)assetImporter;
        var clips = importer.defaultClipAnimations;
        foreach (var clip in clips)
        {
            var name = Actions.SingleOrDefault(action => clip.name == action || clip.name.EndsWith("|" + action, StringComparison.Ordinal));
            if (name == null) continue;
            clip.name = name;
            clip.loopTime = name == "Wait";
            clip.lockRootPositionXZ = true;
            clip.lockRootHeightY = true;
            clip.lockRootRotation = true;
            clip.keepOriginalOrientation = true;
            clip.keepOriginalPositionXZ = true;
            clip.keepOriginalPositionY = false;
            clip.heightFromFeet = true;
        }
        importer.clipAnimations = clips;
    }
}

public static class CompanionAnimationBuild
{
    const string Folder = CompanionAnimationImporter.Folder;

    public static void Prepare()
    {
        if (!File.Exists(CompanionAnimationImporter.Model))
            throw new InvalidOperationException("Blender-authored Companion.fbx is required before motion preparation");
        AssetDatabase.ImportAsset(CompanionAnimationImporter.Model, ImportAssetOptions.ForceSynchronousImport);
        var avatar = AssetDatabase.LoadAllAssetsAtPath(CompanionAnimationImporter.Model).OfType<Avatar>().SingleOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
            throw new InvalidOperationException("Companion Blender rest pose did not import as a valid humanoid");
        var imported = AssetDatabase.LoadAllAssetsAtPath(CompanionAnimationImporter.Model).OfType<AnimationClip>()
            .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal)).ToDictionary(clip => clip.name);
        var clips = new Dictionary<string, AnimationClip>();
        foreach (var name in CompanionAnimationImporter.Actions)
        {
            if (!imported.TryGetValue(name, out var clip)) throw new InvalidOperationException("Missing Blender action: " + name);
            clips.Add(name, SaveClip(clip, name));
        }
        string path = Folder + "/Companion.controller";
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(path);
        foreach (var layer in controller.layers)
            foreach (var state in layer.stateMachine.states) layer.stateMachine.RemoveState(state.state);
        foreach (var tree in AssetDatabase.LoadAllAssetsAtPath(path).OfType<BlendTree>()
            .Where(tree => tree.name == "Turn" || tree.name == "Start" || tree.name == "Stop"))
            UnityEngine.Object.DestroyImmediate(tree, true);
        foreach (var parameter in controller.parameters) controller.RemoveParameter(parameter);
        controller.AddParameter("Direction", AnimatorControllerParameterType.Float);
        var machine = controller.layers[0].stateMachine;
        foreach (var name in new[] { "Notice", "Turn", "Start", "WalkLeft", "WalkRight", "Stop", "Invite", "Wait", "Settle" })
        {
            var state = machine.AddState(name);
            state.writeDefaultValues = true;
            state.iKOnFeet = true;
            if (name == "Turn" || name == "Start" || name == "Stop")
            {
                var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = "Direction", useAutomaticThresholds = false };
                AssetDatabase.AddObjectToAsset(tree, controller);
                tree.AddChild(clips[name + "Left"], -1f);
                tree.AddChild(clips[name + "Right"], 1f);
                state.motion = tree;
            }
            else state.motion = clips[name];
            if (name == "Notice") machine.defaultState = state;
        }
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        ValidateJourney();
        ValidatePresence();
        PrepareFrames();
        ValidateAssets();
        Debug.Log("Companion motion: Blender humanoid actions and planted gait prepared; runtime remains separately unverified");
    }

    static void PrepareFrames()
    {
        var sitting = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/AnimationClip/PET_SIT_01.anim");
        var lying = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/AnimationClip/BETA_PET_WINDOW_LAY.anim");
        if (sitting == null || lying == null || !sitting.humanMotion || !lying.humanMotion)
            throw new InvalidOperationException("Real humanoid sitting and lying clips are required");
        var dangle = SaveClip(sitting, "FrameDangle");
        foreach (var side in new[] { "Left", "Right" })
        {
            var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), side + " Lower Leg Stretch");
            var source = AnimationUtility.GetEditorCurve(sitting, binding);
            if (source == null) throw new InvalidOperationException("Missing knee muscle: " + side);
            var curve = new AnimationCurve();
            for (int frame = 0, count = Mathf.CeilToInt(dangle.length * 30); frame <= count; frame++)
            {
                float t = dangle.length * frame / count;
                float phase = 2f * Mathf.PI * t / dangle.length + (side == "Left" ? 0 : Mathf.PI);
                curve.AddKey(t, source.Evaluate(t) + .12f * Mathf.Sin(phase));
            }
            AnimationUtility.SetEditorCurve(dangle, binding, curve);
        }
        var lie = SaveClip(lying, "FrameLie");
        var watch = SaveClip(lying, "FrameWatch");
        var rotationBindings = new[] { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" }
            .Select(name => EditorCurveBinding.FloatCurve("", typeof(Animator), name)).ToArray();
        var rotations = rotationBindings.Select(binding => AnimationUtility.GetEditorCurve(lying, binding)).ToArray();
        if (rotations.Any(curve => curve == null)) throw new InvalidOperationException("Lying clip lacks humanoid root orientation");
        var back = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
        for (int frame = 0, count = Mathf.CeilToInt(watch.length * 30); frame <= count; frame++)
        {
            float t = watch.length * frame / count;
            var q = Quaternion.AngleAxis(180, Vector3.up) * new Quaternion(rotations[0].Evaluate(t),
                rotations[1].Evaluate(t), rotations[2].Evaluate(t), rotations[3].Evaluate(t));
            back[0].AddKey(t, q.x); back[1].AddKey(t, q.y); back[2].AddKey(t, q.z); back[3].AddKey(t, q.w);
        }
        for (int i = 0; i < back.Length; i++) AnimationUtility.SetEditorCurve(watch, rotationBindings[i], back[i]);
        foreach (var clip in new[] { dangle, lie, watch })
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true; settings.heightFromFeet = false; settings.keepOriginalOrientation = true;
            settings.keepOriginalPositionY = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
        }
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/AnimatorController/AvatarAnimatorControllerV2 1.controller");
        if (controller == null) throw new InvalidOperationException("Missing native avatar controller");
        var machine = controller.layers[0].stateMachine;
        foreach (var clip in new[] { dangle, lie, watch })
        {
            var state = machine.states.Select(child => child.state).SingleOrDefault(item => item.name == clip.name)
                ?? machine.AddState(clip.name);
            state.motion = clip; state.iKOnFeet = false; state.writeDefaultValues = true;
            foreach (var transition in state.transitions) state.RemoveTransition(transition);
            var exit = state.AddTransition(machine.defaultState);
            exit.hasExitTime = false; exit.duration = .25f;
            exit.AddCondition(AnimatorConditionMode.IfNot, 0, "isWindowSit");
        }
        EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
        if (dangle == lie || lie == watch || !dangle.humanMotion || !lie.humanMotion || !watch.humanMotion ||
            AnimationUtility.GetEditorCurve(dangle, EditorCurveBinding.FloatCurve("", typeof(Animator), "Left Lower Leg Stretch")).length < 3)
            throw new InvalidOperationException("Frame poses must be distinct animated humanoid clips");
    }


    public static void ValidatePresence()
    {
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var movement = new APIs.Hyprland.CompanionMotionLease();
        var firstOwner = new object(); var secondOwner = new object();
        Require(movement.TryAcquire(firstOwner), "First movement owner acquires lease");
        Require(!movement.TryAcquire(secondOwner), "Concurrent portrait and locomotion owners are rejected");
        Require(!movement.Release(secondOwner) && movement.Owned, "Foreign cancellation cannot release movement");
        Require(movement.Release(firstOwner) && movement.TryAcquire(secondOwner), "Ownership transfers only after release");
        Require(!movement.Release(firstOwner) && movement.Owned, "Late cancellation cannot revoke a newer owner");
        Require(APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(165, true, false, false, 12, 9) == 60, "Passive ceiling must be 60");
        Require(APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(165, true, false, false, 10, 9) == 165, "Activity grace must preserve requested FPS");
        Require(APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(165, true, true, true, 12, 9) == 165, "Interaction must wake before hidden throttling");
        Require(APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(165, true, false, true, 12, 9) == 5, "Confirmed hidden ceiling must be 5");
        Require(APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(15, true, false, false, 12, 9) == 15, "Passive must respect lower requested limit");
        Require(APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(165, false, false, true, 12, 9) == 165, "Other compositors retain requested limit");
        Require(APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(165, true, false, false, 11, 9) == 60, "Grace ends exactly at two seconds");
        Require(APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(15, true, false, true, 12, 9) == 5, "Hidden policy remains transient below requested floor");
        Require(APIs.Hyprland.CompanionPresencePolicy.PointerEligible(true, false, false), "Visible idle avatar must receive clicks without a keyboard lease");
        Require(!APIs.Hyprland.CompanionPresencePolicy.PointerEligible(true, true, false), "Passive portrait remains click-through");
        Require(!APIs.Hyprland.CompanionPresencePolicy.PointerEligible(false, false, false), "Hidden avatar cannot receive pointer input");
        Require(!APIs.Hyprland.CompanionPresencePolicy.PointerEligible(true, false, true), "Special audition cannot acquire live compositor input");
        var pixels = new byte[4 * 3 * 4];
        pixels[(1 * 4 + 2) * 4 + 3] = 255;
        var input = new APIs.Hyprland.CompanionInputMask(pixels, 4, 3);
        var size = new Vector2Int(8, 6);
        Require(input.Contains(new Vector2(5, 3), size), "Scaled model pixels retain pointer eligibility");
        Require(!input.Contains(new Vector2(1, 3), size), "Transparent pixels inside the same window cannot acquire focus");
        Require(!input.Contains(new Vector2(-1, 3), size) && !input.Contains(new Vector2(8, 3), size),
            "Off-window and right boundary coordinates cannot wrap into the silhouette");
        Require(!input.Contains(new Vector2(5, 0), size), "Input mask uses X11 top-down coordinates, not mirrored Unity rows");
        Require(!APIs.Hyprland.CompanionPresencePolicy.KeyboardEligible(false, true, false), "Hover is not keyboard intent");
        Require(!APIs.Hyprland.CompanionPresencePolicy.KeyboardEligible(true, true, true), "Special audition must never gain focus");
        Require(!APIs.Hyprland.CompanionPresencePolicy.KeyboardEligible(true, false, false), "Hidden UI must never gain focus");
        Require(APIs.Hyprland.CompanionPresencePolicy.KeyboardEligible(true, true, false), "Deliberate presentable UI can gain focus");
        Require(APIs.Hyprland.CompanionPresencePolicy.KeyboardRequestCurrent(true, 10, 10), "Unchanged focus preserves deliberate request");
        Require(!APIs.Hyprland.CompanionPresencePolicy.KeyboardRequestCurrent(true, 10, 11), "Switch before worker acquisition cancels stale request");
        Require(!APIs.Hyprland.CompanionPresencePolicy.KeyboardRequestCurrent(true, 10, 12), "Switch away and back cannot revive an old request");
        Require(!APIs.Hyprland.CompanionPresencePolicy.KeyboardRequestCurrent(false, 10, 10), "UI release cancels pending keyboard request");
        Require(APIs.Hyprland.CompanionPresencePolicy.KeyboardRequestCurrent(true, 11, 11), "A fresh deliberate request after switching is eligible");
        var keyboardOwner = new APIs.Hyprland.CompanionKeyboardOwnership();
        keyboardOwner.Begin("0xa", 17);
        keyboardOwner.ObserveExternal();
        keyboardOwner.ObserveExternal();
        Require(keyboardOwner.Pending && keyboardOwner.Owned && keyboardOwner.PreviousWindow == "0xa" &&
            keyboardOwner.PreviousPid == 17, "A→B→A events retain an in-flight acquisition's restoration target");
        keyboardOwner.Reconcile(true);
        Require(keyboardOwner.Owned && !keyboardOwner.Pending && keyboardOwner.PreviousWindow == "0xa",
            "Successful canceled acquisition remains conditionally restorable");
        keyboardOwner.ObserveExternal();
        Require(keyboardOwner.Owned && keyboardOwner.Pending && keyboardOwner.PreviousWindow == "0xa",
            "A delayed external event cannot discard a reconciled acquisition's restore target");
        keyboardOwner.Reconcile(true);
        Require(keyboardOwner.Owned && !keyboardOwner.Pending, "Latest mascot observation retains conditional restoration");
        keyboardOwner.ObserveExternal();
        keyboardOwner.Reconcile(false);
        Require(!keyboardOwner.Owned && keyboardOwner.PreviousWindow == null, "Only a fresh external-owner observation releases the lease");
        keyboardOwner.Begin("0xb", 23);
        keyboardOwner.ObservationFailed();
        Require(keyboardOwner.Owned && keyboardOwner.Pending && keyboardOwner.PreviousPid == 23,
            "Failed post-unlock observation preserves restoration responsibility");
        keyboardOwner.Reconcile(false);
        Require(!keyboardOwner.Owned && keyboardOwner.PreviousWindow == null, "Observed external owner discards stale restoration");
        var area = new RectInt(0, 0, 1920, 1080);
        Require(APIs.Hyprland.CompanionPresencePolicy.SupportFits(new RectInt(100, 450, 800, 500), area, 180, 250), "Real window top must permit safe headroom");
        Require(!APIs.Hyprland.CompanionPresencePolicy.SupportFits(new RectInt(100, 20, 800, 500), area, 180, 250), "No seating through output top");
        Require(!APIs.Hyprland.CompanionPresencePolicy.SupportFits(new RectInt(100, 450, 80, 500), area, 180, 250), "No seat on insufficient support span");
        Require(APIs.Hyprland.CompanionPresencePolicy.BodyFits(area, new Vector2Int(12, 12), new Rect(0, 0, 100, 200)), "Safe complete body placement");
        Require(!APIs.Hyprland.CompanionPresencePolicy.BodyFits(area, new Vector2Int(12, 900), new Rect(0, 0, 100, 200)), "Do not silently clamp away seat contact");
        var body = new Rect(300, 100, 160, 300);
        var origin = new Vector2Int(700, 400);
        Require(APIs.Hyprland.CompanionPresencePolicy.RestorePosition(origin, area, area, body) == origin, "Portrait roundtrip preserves exact normal position");
        var replacement = new RectInt(-1280, 0, 1280, 1024);
        var restored = APIs.Hyprland.CompanionPresencePolicy.RestorePosition(origin, area, replacement, body);
        Require(APIs.Hyprland.CompanionPresencePolicy.BodyFits(replacement, restored, body), "Removed or resized output restores only within surviving geometry");
        Require(Mathf.Abs(APIs.Hyprland.CompanionPresencePolicy.PortraitViewportScale(new Vector2Int(1536, 1024),
            new RectInt(0, 0, 1280, 720)) - 1024f / 720f) < .0001f, "Portrait fits the visible output crop without resizing the user's window");
        Require(APIs.Hyprland.CompanionPresencePolicy.BodyFits(new RectInt(-1920, -100, 1920, 1080),
            new Vector2Int(-1908, -88), new Rect(0, 0, 100, 200)), "Negative-coordinate outputs retain safe placement");
        var workspace = new APIs.Hyprland.HyprlandWorkspace { id = 1, name = "1" };
        var monitor = new APIs.Hyprland.HyprlandMonitor { id = 0, dpmsStatus = true, activeWorkspace = workspace };
        var companion = new APIs.Hyprland.HyprlandClient { address = "0x1", pid = 1, monitor = 0, workspace = workspace };
        var support = new APIs.Hyprland.HyprlandClient { address = "0x2", pid = 2, monitor = 0, workspace = workspace, mapped = true };
        bool Eligible(bool fresh = true, string owned = null) => APIs.Hyprland.CompanionPresencePolicy.SeatClientEligible(support, companion, monitor, fresh, owned);
        Require(Eligible(), "Visible client on the same output is eligible");
        Require(!Eligible(false), "Stale support metadata releases ownership");
        support.hidden = true; Require(!Eligible(), "Hidden support is ineligible"); support.hidden = false;
        support.mapped = false; Require(!Eligible(), "Unmapped support is ineligible"); support.mapped = true;
        support.fullscreen = 2; Require(!Eligible(), "Fullscreen support is ineligible"); support.fullscreen = 0;
        support.fullscreen = 1; Require(!Eligible(), "Maximized support is ineligible"); support.fullscreen = 0;
        support.pid = companion.pid; Require(!Eligible(), "Self-process windows are ineligible"); support.pid = 2;
        support.workspace = new APIs.Hyprland.HyprlandWorkspace { id = 2, name = "2" };
        Require(!Eligible(), "Off-workspace support is ineligible");
        support.workspace = workspace;
        monitor.dpmsStatus = false; Require(!Eligible(), "Disabled surface cannot support a seat"); monitor.dpmsStatus = true;
        var ownWorkspace = new APIs.Hyprland.HyprlandWorkspace { id = -2, name = "special:omp-owned" };
        support.workspace = companion.workspace = ownWorkspace;
        Require(!Eligible(), "Ordinary hidden special is never admitted");
        Require(Eligible(true, ownWorkspace.name), "Explicit owned audition admits only its exact workspace");
        support.workspace = workspace;
        Require(!Eligible(true, ownWorkspace.name), "A hidden owned audition cannot attach to ordinary live workspace windows");
        support.workspace = new APIs.Hyprland.HyprlandWorkspace { id = -3, name = "special:omp-foreign" };
        Require(!Eligible(true, ownWorkspace.name), "Owned audition cannot admit foreign special support");
        ownWorkspace = JsonUtility.FromJson<APIs.Hyprland.HyprlandWorkspace>(
            "{\"address\":\"special:omp-owned\",\"type\":\"special\",\"name\":\"special:omp-owned\"}");
        support.workspace = companion.workspace = ownWorkspace;
        monitor.activeWorkspace = new APIs.Hyprland.HyprlandWorkspace { id = 0, name = "other" };
        Require(!Eligible(), "Special workspace without numeric ID must not become a normal visible workspace");
        Require(Eligible(true, ownWorkspace.name), "Named special workspace retains exact owned audition eligibility");
        support.workspace = JsonUtility.FromJson<APIs.Hyprland.HyprlandWorkspace>(
            "{\"type\":\"special\",\"name\":\"special:omp-foreign\"}");
        Require(!Eligible(true, ownWorkspace.name), "Missing numeric IDs cannot equate distinct special workspaces");
        support.workspace = companion.workspace = workspace; monitor.activeWorkspace = workspace;
        Require(APIs.Hyprland.CompanionPresencePolicy.PortraitPending("bigscreen"), "Timed portrait remains pending");
        Require(!APIs.Hyprland.CompanionPresencePolicy.PortraitPending("rest"), "Rest cancels portrait");
        Require(!APIs.Hyprland.CompanionPresencePolicy.PortraitPending("hold"), "Engaged hold cancels automatic portrait without acknowledging attention");
        var episodes = new APIs.Hyprland.CompanionPortraitEpisodes();
        Require(episodes.CanClaim("a", false) && episodes.CanClaim("a", false), "Admission inspection must not consume a rejected portrait");
        Require(episodes.CanClaim("a", true), "A rejected start must not consume the later escalation");
        Require(episodes.TryClaim("a", false), "First cross-output portrait");
        Require(!episodes.TryClaim("a", false), "Cross-output must not repeat");
        Require(episodes.TryClaim("a", true), "Timed escalation is independent of initial presentation");
        Require(!episodes.TryClaim("a", true), "Timed escalation must not repeat");
        Require(episodes.TryClaim("b", true), "New episode restores eligibility");
        Require(!episodes.TryClaim("b", false), "A timed portrait cannot be followed by a second immediate portrait");
        Require(APIs.Hyprland.CompanionFramePolicy.EdgeY(new RectInt(100, 20, 800, 700), true) == 720,
            "Bottom frame follows the real resized lower edge, not the top edge");
        Require(APIs.Hyprland.CompanionFramePolicy.SupportFits(new RectInt(100, 20, 800, 700), area, 300, 120, true),
            "Lower ledge remains usable without headroom above the upper ledge");
        Require(!APIs.Hyprland.CompanionFramePolicy.SupportFits(new RectInt(100, 20, 100, 700), area, 300, 120, true),
            "Lying requires enough horizontal ledge");
        Require(!APIs.Hyprland.CompanionFramePolicy.SupportFits(new RectInt(100, 20, 800, 40), area, 300, 120, true),
            "Lower ledge cannot place the body through the output top");
        var cornerFrame = new RectInt(100, 80, 800, 700);
        Require(APIs.Hyprland.CompanionFramePolicy.TryDrop(cornerFrame, new Vector2(897, 210),
            new Vector2(897, 82), 24, out var lowerDrop, out var dropX) && !lowerDrop && dropX == 897,
            "Dropping the held head at the upper-right corner seats at that corner, not a preferred X");
        Require(APIs.Hyprland.CompanionFramePolicy.TryDrop(cornerFrame, new Vector2(91, 200),
            new Vector2(101, 80), 24, out lowerDrop, out dropX) && !lowerDrop && dropX == 101,
            "A held corner point supports body overhang without recentering the complete avatar");
        Require(APIs.Hyprland.CompanionFramePolicy.TryDrop(cornerFrame, new Vector2(500, 779),
            new Vector2(500, 660), 24, out lowerDrop, out dropX) && lowerDrop && dropX == 500,
            "A lower-edge drop selects lying without changing horizontal contact");
        Require(!APIs.Hyprland.CompanionFramePolicy.TryDrop(cornerFrame, new Vector2(500, 400),
            new Vector2(500, 300), 24, out _, out _), "An ordinary free drop must not acquire a distant frame");
        Require(!APIs.Hyprland.CompanionFramePolicy.TryDrop(cornerFrame, new Vector2(950, 80),
            new Vector2(950, 80), 24, out _, out _), "A distant horizontal drop must not jump to the window");
        Require(APIs.Hyprland.CompanionContextPolicy.IsTelegram("com.ayugram.desktop"), "Exact AyuGram family identity");
        Require(!APIs.Hyprland.CompanionContextPolicy.IsTelegram("not-telegram"), "Title-like app substring is not Telegram");
        Require(APIs.Hyprland.CompanionContextPolicy.IsBrowser("Zen"), "Browser audio guard is case-insensitive");
        Require(!APIs.Hyprland.CompanionContextPolicy.IsBrowser("kitty"), "Unrelated audio retains its native policy");
    }

    static AnimationClip SaveClip(AnimationClip source, string name)
    {
        if (!source.humanMotion || source.length <= 0f) throw new InvalidOperationException("Not a real humanoid action: " + name);
        var path = Folder + "/" + name + ".anim";
        var copy = UnityEngine.Object.Instantiate(source);
        copy.name = name;
        var settings = AnimationUtility.GetAnimationClipSettings(copy);
        settings.loopTime = name == "Wait" || name.StartsWith("Walk", StringComparison.Ordinal);
        settings.loopBlendPositionXZ = settings.loopBlendPositionY = settings.loopBlendOrientation = true;
        settings.heightFromFeet = true;
        settings.keepOriginalPositionY = false;
        AnimationUtility.SetAnimationClipSettings(copy, settings);
        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (existing == null) AssetDatabase.CreateAsset(copy, path);
        else
        {
            EditorUtility.CopySerialized(copy, existing);
            UnityEngine.Object.DestroyImmediate(copy);
            EditorUtility.SetDirty(existing);
            copy = existing;
        }
        return copy;
    }

    public static void ValidateAssets()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(CompanionAnimationImporter.Model);
        if (model == null) throw new InvalidOperationException("Missing imported authoring rig");
        var instance = UnityEngine.Object.Instantiate(model);
        instance.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var animator = instance.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) throw new InvalidOperationException("No valid humanoid Animator on authoring rig");
            var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (left == null || right == null) throw new InvalidOperationException("Missing humanoid foot mapping");
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null || hips == null) throw new InvalidOperationException("Missing humanoid body mapping");
            float restFloor = Mathf.Min(left.position.y, right.position.y);
            float restHipHeight = hips.position.y - restFloor;
            float restBodyHeight = head.position.y - restFloor;
            if (restHipHeight <= 0f || restBodyHeight <= restHipHeight)
                throw new InvalidOperationException("Imported FBX rest skeleton is not upright in world Y");
            var bones = Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>()
                .Where(bone => bone != HumanBodyBones.LastBone && bone != HumanBodyBones.Hips)
                .Select(bone => animator.GetBoneTransform(bone)).Where(bone => bone != null && bone.parent != null)
                .ToArray();
            var restLengths = bones.Select(bone => Vector3.Distance(bone.position, bone.parent.position)).ToArray();
            Debug.Log("Companion rest dimensions: hipHeight=" + restHipHeight + ", bodyHeight=" + restBodyHeight +
                ", footSeparation=" + Vector3.Distance(left.position, right.position));
            Debug.Log("Companion rig: humanScale=" + animator.humanScale + ", rootScale=" + instance.transform.localScale.ToString("F6") +
                ", lossyScale=" + instance.transform.lossyScale.ToString("F6") + ", rootRotation=" + instance.transform.rotation.ToString("F6") +
                ", leftRest=" + left.position.ToString("F6") + ", rightRest=" + right.position.ToString("F6"));
            if ((instance.transform.localScale - Vector3.one).sqrMagnitude > .000001f)
                throw new InvalidOperationException("Authoring armature root must have unit scale; fix FBX units at export");
            if (Quaternion.Angle(instance.transform.localRotation, Quaternion.identity) > .01f)
                throw new InvalidOperationException("Authoring armature root must be axis-neutral; bake axis conversion during import");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Folder + "/Companion.controller");
            if (controller == null) throw new InvalidOperationException("Missing production motion controller");
            if (Mathf.Abs(animator.humanScale - MateEngine.Desktop.CompanionJourney.AuthoringHumanScale) > .00001f)
                throw new InvalidOperationException("Gait reference scale no longer matches the authoring avatar");
            var origin = instance.transform.position;
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.updateMode = AnimatorUpdateMode.UnscaledTime;
            animator.Rebind();
            animator.Update(0f);
            foreach (var name in CompanionAnimationImporter.Actions)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Folder + "/" + name + ".anim");
                if (clip == null || !clip.humanMotion || clip.length <= 0f) throw new InvalidOperationException("Invalid action: " + name);
                string state = name;
                if (name.StartsWith("Turn", StringComparison.Ordinal)) state = "Turn";
                else if (name.StartsWith("Start", StringComparison.Ordinal)) state = "Start";
                else if (name.StartsWith("Stop", StringComparison.Ordinal)) state = "Stop";
                int stateHash = Animator.StringToHash("Base Layer." + state);
                if (!animator.HasState(0, stateHash)) throw new InvalidOperationException("Missing production state: " + state);
                animator.SetFloat("Direction", name.EndsWith("Left", StringComparison.Ordinal) ? -1f : 1f);
                float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
                for (int sample = 0; sample <= 60; sample++)
                {
                    // Evaluate the production state's humanoid Foot IK, not raw property sampling.
                    animator.Play(stateHash, 0, sample / 60f);
                    animator.Update(0f);
                    if ((instance.transform.position - origin).sqrMagnitude > .000001f ||
                        (instance.transform.localScale - Vector3.one).sqrMagnitude > .000001f)
                        throw new InvalidOperationException("Motion action moved or scaled its root: " + name);
                    if (Vector3.Distance(hips.position, origin) > 2f * animator.humanScale)
                        throw new InvalidOperationException("Humanoid body has incompatible world units: " + name);
                    float floor = Mathf.Min(left.position.y, right.position.y);
                    if (float.IsNaN(floor) || float.IsInfinity(floor)) throw new InvalidOperationException("Non-finite foot pose: " + name);
                    float hipHeight = hips.position.y - floor;
                    float bodyHeight = head.position.y - floor;
                    if (hipHeight < .65f * restHipHeight || hipHeight > 1.4f * restHipHeight ||
                        bodyHeight < .65f * restBodyHeight || bodyHeight > 1.4f * restBodyHeight)
                        throw new InvalidOperationException("Animated humanoid lost upright rest dimensions: " + name +
                            ", hipHeight=" + hipHeight + ", bodyHeight=" + bodyHeight);
                    for (int bone = 0; bone < bones.Length; bone++)
                    {
                        float length = Vector3.Distance(bones[bone].position, bones[bone].parent.position);
                        if (Mathf.Abs(length - restLengths[bone]) > .06f * restLengths[bone] + .0001f * animator.humanScale)
                            throw new InvalidOperationException("Animated bone length differs from FBX rest: " + name +
                                "/" + bones[bone].name + ", rest=" + restLengths[bone] + ", actual=" + length);
                    }
                    minimum = Mathf.Min(minimum, floor);
                    maximum = Mathf.Max(maximum, floor);
                    if (sample == 0 || sample == 30 || sample == 60)
                        Debug.Log("Companion pose " + name + " t=" + (clip.length * sample / 60f) +
                            ": left=" + left.position.ToString("F6") + ", right=" + right.position.ToString("F6") +
                            ", hips=" + hips.position.ToString("F6") + ", hipsRotation=" + hips.rotation.ToString("F6") +
                            ", root=" + instance.transform.position.ToString("F6") + ", rootScale=" + instance.transform.lossyScale.ToString("F6"));
                }
                Debug.Log("Companion action " + name + ": duration=" + clip.length + ", foot-height range=" + (maximum - minimum));
                if (maximum - minimum > .06f * animator.humanScale)
                    throw new InvalidOperationException("Planted action foot baseline drift: " + name);
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
    }

    public static void ValidateJourney()
    {
        var journey = new MateEngine.Desktop.CompanionJourney();
        var origin = new Vector2(400, 250);
        var destination = new Vector2(130, 250);
        journey.Begin(origin, destination, 1.2f, 90f);
        float previous = origin.x;
        int invitations = 0;
        var old = journey.Phase;
        for (int frame = 0; frame < 1200 && journey.Phase != MateEngine.Desktop.CompanionMotionPhase.Wait; frame++)
        {
            journey.Tick(1f / 60f);
            if (journey.Position.x > previous + .001f || journey.Position.x < destination.x - .001f || journey.Position.y != origin.y)
                throw new InvalidOperationException("Journey reversed, overshot or changed foot baseline");
            if (journey.Phase == MateEngine.Desktop.CompanionMotionPhase.Invite && old != journey.Phase) invitations++;
            previous = journey.Position.x;
            old = journey.Phase;
        }
        if (journey.Phase != MateEngine.Desktop.CompanionMotionPhase.Wait || journey.Position != destination || invitations != 1)
            throw new InvalidOperationException("Journey did not arrive, invite once and wait");
        journey.Viewed();
        for (int frame = 0; frame < 60; frame++) journey.Tick(1f / 60f);
        if (journey.Active || journey.Position != destination) throw new InvalidOperationException("Viewed did not settle in place");
        journey.Begin(origin, destination, 1.2f, 90f);
        journey.Tick(.1f);
        journey.Cancel();
        journey.Tick(.1f);
        if (journey.Active || journey.Position != origin) throw new InvalidOperationException("Cancelled notice moved");
        journey.Begin(origin, destination, 1.2f, 90f);
        journey.Viewed();
        for (int frame = 0; frame < 1200 && journey.Active; frame++)
        {
            journey.Tick(1f / 60f);
            if (journey.Phase == MateEngine.Desktop.CompanionMotionPhase.Invite)
                throw new InvalidOperationException("Viewed-in-flight invitation was replayed");
        }
        if (journey.Active || journey.Position != destination) throw new InvalidOperationException("Viewed-in-flight did not plant and settle");
        journey.Begin(origin, new Vector2(690, 250), 1.2f, 90f);
        previous = origin.x;
        for (int frame = 0; frame < 1200 && journey.Phase != MateEngine.Desktop.CompanionMotionPhase.Wait; frame++)
        {
            var phase = journey.Phase;
            journey.Tick(1f / 60f);
            if (journey.Position.x < previous || journey.Position.y != origin.y)
                throw new InvalidOperationException("Rightward gait reversed or changed foot baseline");
            if ((phase == MateEngine.Desktop.CompanionMotionPhase.Start || phase == MateEngine.Desktop.CompanionMotionPhase.Stop) &&
                phase == journey.Phase && journey.Position.x != previous)
                throw new InvalidOperationException("Planted start or stop slid the window");
            previous = journey.Position.x;
        }
        if (journey.Position != new Vector2(670, 250)) throw new InvalidOperationException("Gait did not stop on a whole calibrated stride");
        journey.Begin(origin, new Vector2(420, 250), 1.2f, 90f);
        for (int frame = 0; frame < 600 && journey.Phase != MateEngine.Desktop.CompanionMotionPhase.Wait; frame++) journey.Tick(1f / 60f);
        if (journey.Position != origin) throw new InvalidOperationException("Sub-stride invitation slid instead of staying planted");
    }
}
