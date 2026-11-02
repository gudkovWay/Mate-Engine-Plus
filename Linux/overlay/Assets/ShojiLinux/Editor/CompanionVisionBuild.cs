using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MateEngine.Vision;
using UnityEditor;
using UnityEngine;

[Serializable]
public sealed class VisionImportDescription
{
    public string[] bones;
    public VisionImportBone[] rest;
    public VisionImportClip[] clips;
    public VisionImportProp[] props;
}
[Serializable]
public sealed class VisionImportClip { public string name; public float seconds; public bool loop; public VisionPropEvent[] events; }
[Serializable]
public sealed class VisionImportBone { public string name; public float[] matrix; }
[Serializable]
public sealed class VisionImportProp
{
    public string name, bone, carryBone;
    public Vector3 position, carryPosition, boneGrip, meshGrip, size;
    public Quaternion rotation, carryRotation;
    public bool hasGrip;
}

public sealed class CompanionVisionImporter : AssetPostprocessor
{
    public const string Folder = "Assets/ShojiLinux/Resources/CompanionVision";
    public const string Model = Folder + "/Vision.fbx";
    public const string Props = Folder + "/VisionProps.fbx";
    public const string Reference = Folder + "/Editor/VisionRest.fbx";
    public static VisionImportDescription Description() => JsonUtility.FromJson<VisionImportDescription>(File.ReadAllText(Folder + "/VisionUnity.json"));
    void OnPreprocessModel()
    {
        if (assetPath != Model && assetPath != Props && assetPath != Reference) return;
        var importer = (ModelImporter)assetImporter;
        importer.importCameras = importer.importLights = false;
        importer.bakeAxisConversion = true;
        importer.optimizeGameObjects = false;
        importer.optimizeBones = false;
        importer.preserveHierarchy = true;
        importer.globalScale = 1;
        if (assetPath == Reference)
        {
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.importAnimation = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            return;
        }
        if (assetPath == Props)
        {
            importer.importAnimation = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            return;
        }
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.animationCompression = ModelImporterAnimationCompression.Off;
    }
    void OnPreprocessAnimation()
    {
        if (assetPath != Model) return;
        var importer = (ModelImporter)assetImporter;
        var specs = Description().clips.ToDictionary(item => item.name);
        var clips = importer.defaultClipAnimations;
        foreach (var clip in clips)
        {
            string name = clip.name.StartsWith("VisionRig|", StringComparison.Ordinal) ? clip.name.Substring(10) : clip.name;
            if (!specs.TryGetValue(name, out var spec)) continue;
            clip.name = name; clip.loopTime = spec.loop;
            clip.lockRootPositionXZ = clip.lockRootHeightY = clip.lockRootRotation = true;
            clip.keepOriginalOrientation = clip.keepOriginalPositionXZ = true;
            clip.keepOriginalPositionY = false; clip.heightFromFeet = true;
        }
        importer.clipAnimations = clips;
    }
}

public static class CompanionVisionBuild
{
    const string Folder = CompanionVisionImporter.Folder;
    static readonly Dictionary<string, Color> Colors = new Dictionary<string, Color> {
        { "VisionCharcoal", new Color(.105f,.12f,.125f) }, { "VisionSlateCover", new Color(.22f,.27f,.28f) },
        { "VisionBlankPaper", new Color(.72f,.70f,.63f) }, { "VisionMutedWood", new Color(.43f,.34f,.23f) },
        { "VisionGraphite", new Color(.075f,.08f,.08f) }, { "VisionSoftCushion", new Color(.17f,.18f,.18f) },
        { "VisionCarton", new Color(.47f,.43f,.35f) }, { "VisionCartonBand", new Color(.29f,.31f,.30f) },
        { "VisionPopcorn", new Color(.78f,.74f,.59f) }, { "VisionLensTransparent", new Color(.66f,.74f,.73f,.16f) }
    };
    static Material Material(string name)
    {
        name = name.Replace(" (Instance)", "");
        if (!Colors.TryGetValue(name, out var color)) throw new InvalidOperationException("Unknown authored prop material: " + name);
        var path = Folder + "/" + name + ".mat";
        var shader = Shader.Find("MateEngine/CompanionProp");
        if (shader == null) throw new InvalidOperationException("Vision prop shader is missing");
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
        material.shader = shader; material.color = color;
        material.SetInt("_ZWrite", color.a < 1 ? 0 : 1);
        material.renderQueue = color.a < 1 ? 3000 : 2000;
        EditorUtility.SetDirty(material);
        return material;
    }
    static Matrix4x4 RestMatrix(VisionImportBone bone)
    {
        if (bone.matrix == null || bone.matrix.Length != 16) throw new InvalidOperationException("Missing authored rest basis: " + bone.name);
        var result = new Matrix4x4();
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++) result[row, column] = bone.matrix[row * 4 + column];
        return result;
    }
    static Matrix4x4 SourceToUnity(Dictionary<string, Transform> bones, Dictionary<string, Matrix4x4> rest)
    {
        var up = (bones["Head"].position - bones["Hips"].position).normalized;
        var side = Vector3.ProjectOnPlane(bones["LeftHand"].position - bones["RightHand"].position, up).normalized;
        var back = -Vector3.ProjectOnPlane(bones["LeftToes"].position - bones["LeftFoot"].position, up).normalized;
        float unit = Vector3.Distance(bones["Head"].position, bones["Hips"].position) /
            Vector3.Distance(rest["Head"].GetColumn(3), rest["Hips"].GetColumn(3));
        var result = Matrix4x4.identity;
        result.SetColumn(0, side * unit); result.SetColumn(1, back * unit); result.SetColumn(2, up * unit);
        var origin = bones["Hips"].position - result.MultiplyVector(rest["Hips"].GetColumn(3));
        result.SetColumn(3, new Vector4(origin.x, origin.y, origin.z, 1));
        foreach (var pair in rest)
            if (Vector3.Distance(result.MultiplyPoint3x4(pair.Value.GetColumn(3)), bones[pair.Key].position) > .002f * unit)
                throw new InvalidOperationException("FBX rest-space conversion mismatch: " + pair.Key);
        return result;
    }
    static Matrix4x4 BoneAttachment(Transform bone, Matrix4x4 rest, Matrix4x4 conversion, Matrix4x4 meshInverse,
        Vector3 position, Quaternion rotation)
    {
        // FBX bone-local axes and units differ from armature/mesh space.
        return bone.worldToLocalMatrix * conversion * rest * Matrix4x4.TRS(position, rotation, Vector3.one) * meshInverse;
    }
    public static void Prepare()
    {
        CompanionVisionChecks.Validate();
        var description = CompanionVisionImporter.Description();
        if (description.clips.Length != 26 || description.props.Length != 6 || description.bones.Length != 51 ||
            description.rest == null || description.rest.Length != 51)
            throw new InvalidOperationException("Incomplete vision export contract");
        AssetDatabase.ImportAsset(CompanionVisionImporter.Model, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(CompanionVisionImporter.Props, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(CompanionVisionImporter.Reference, ImportAssetOptions.ForceSynchronousImport);
        var reference = AssetDatabase.LoadAssetAtPath<GameObject>(CompanionVisionImporter.Reference);
        if (reference == null) throw new InvalidOperationException("Neutral FBX bind reference is missing");
        var rig = AssetDatabase.LoadAssetAtPath<GameObject>(CompanionVisionImporter.Model);
        if (rig == null) throw new InvalidOperationException("Vision rig is missing");
        var avatar = AssetDatabase.LoadAllAssetsAtPath(CompanionVisionImporter.Model).OfType<Avatar>().SingleOrDefault();
        if (rig == null || avatar == null || avatar.isHuman || !avatar.isValid)
            throw new InvalidOperationException("Vision skeleton did not import as a valid raw sampling rig");
        CompanionVisionChecks.ValidateSamplingRig(rig, description.bones);
        var transforms = reference.GetComponentsInChildren<Transform>(true);
        var bones = description.bones.ToDictionary(name => name, name => transforms.Single(item => item.name == name));
        var rest = description.rest.ToDictionary(item => item.name, RestMatrix);
        var conversion = SourceToUnity(bones, rest);
        var meshBasis = conversion; meshBasis.SetColumn(3, new Vector4(0, 0, 0, 1));
        var meshInverse = meshBasis.inverse;
        var imported = AssetDatabase.LoadAllAssetsAtPath(CompanionVisionImporter.Model).OfType<AnimationClip>()
            .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal)).ToDictionary(clip => clip.name);
        var clipAssets = new List<VisionClip>();
        foreach (var spec in description.clips)
        {
            if (!imported.TryGetValue(spec.name, out var clip) || clip.humanMotion || clip.legacy || Mathf.Abs(clip.length - spec.seconds) > .04f)
                throw new InvalidOperationException("Missing or incorrectly timed vision clip: " + spec.name);
            var path = Folder + "/" + spec.name + ".anim";
            var saved = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (saved == null) { saved = UnityEngine.Object.Instantiate(clip); AssetDatabase.CreateAsset(saved, path); }
            else EditorUtility.CopySerialized(clip, saved);
            saved.name = spec.name;
            var settings = AnimationUtility.GetAnimationClipSettings(saved);
            settings.loopTime = spec.loop; AnimationUtility.SetAnimationClipSettings(saved, settings);
            EditorUtility.SetDirty(saved);
            clipAssets.Add(new VisionClip { name = spec.name, clip = saved, events = spec.events });
        }
        var libraryModel = AssetDatabase.LoadAssetAtPath<GameObject>(CompanionVisionImporter.Props);
        if (libraryModel == null) throw new InvalidOperationException("Vision prop FBX is missing");
        var propAssets = new List<VisionPropAsset>();
        foreach (var spec in description.props)
        {
            var root = libraryModel.GetComponentsInChildren<Transform>(true).Single(item => item.name == spec.name);
            var bone = bones[spec.bone];
            var held = BoneAttachment(bone, rest[spec.bone], conversion, meshInverse, spec.position, spec.rotation);
            var carried = string.IsNullOrEmpty(spec.carryBone) ? Matrix4x4.identity :
                BoneAttachment(bones[spec.carryBone], rest[spec.carryBone], conversion, meshInverse, spec.carryPosition, spec.carryRotation);
            var grip = bone.InverseTransformPoint(conversion.MultiplyPoint3x4(rest[spec.bone].MultiplyPoint3x4(spec.boneGrip)));
            var size = meshBasis.MultiplyVector(spec.size);
            size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            var copy = new GameObject(spec.name);
            try
            {
                var geometry = UnityEngine.Object.Instantiate(root.gameObject, copy.transform, false);
                geometry.name = "Geometry";
                geometry.transform.localPosition = Vector3.zero;
                geometry.transform.localRotation = root.rotation;
                geometry.transform.localScale = root.lossyScale;
                foreach (var renderer in copy.GetComponentsInChildren<MeshRenderer>(true))
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(item => Material(item.name)).ToArray();
                var renderers = copy.GetComponentsInChildren<MeshRenderer>(true);
                if (renderers.Length == 0) throw new InvalidOperationException("Prop has no renderable geometry: " + spec.name);
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                if (size.sqrMagnitude <= 0 || (bounds.size - size).magnitude > size.magnitude * .03f)
                    throw new InvalidOperationException("Imported prop dimensions changed: " + spec.name +
                        " expected=" + size.ToString("F5") + " actual=" + bounds.size.ToString("F5"));
                var prefab = PrefabUtility.SaveAsPrefabAsset(copy, Folder + "/" + spec.name + ".prefab");
                if (prefab == null || prefab.GetComponentsInChildren<MeshFilter>().Any(item => item.sharedMesh == null || item.sharedMesh.vertexCount == 0))
                    throw new InvalidOperationException("Invalid prop geometry: " + spec.name);
                propAssets.Add(new VisionPropAsset { name = spec.name, bone = spec.bone, prefab = prefab,
                    position = held.GetColumn(3), rotation = held.rotation, carryBone = spec.carryBone,
                    carryPosition = carried.GetColumn(3), carryRotation = carried.rotation,
                    hasGrip = spec.hasGrip, boneGrip = grip, meshGrip = meshBasis.MultiplyVector(spec.meshGrip) });
            }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
        }
        var libraryPath = Folder + "/VisionLibrary.asset";
        var library = AssetDatabase.LoadAssetAtPath<VisionLibrary>(libraryPath);
        if (library == null) { library = ScriptableObject.CreateInstance<VisionLibrary>(); AssetDatabase.CreateAsset(library, libraryPath); }
        library.rig = rig; library.clips = clipAssets.ToArray(); library.props = propAssets.ToArray();
        library.rest = description.bones.Select(name => new VisionBoneRest {
            bone = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), name),
            position = reference.transform.InverseTransformPoint(bones[name].position),
            rotation = Quaternion.Inverse(reference.transform.rotation) * bones[name].rotation }).ToArray();
        EditorUtility.SetDirty(library); AssetDatabase.SaveAssets();
        string selectedAvatar = Environment.GetEnvironmentVariable("MATEENGINE_VISION_CHECK_AVATAR");
        if (!string.IsNullOrEmpty(selectedAvatar)) CompanionVisionChecks.ValidateSelectedAvatar(selectedAvatar);
        Debug.Log("Vision native assets: raw 51-bone sampler, 26 timed motions and 6 real prop prefabs prepared");
    }
}
