using System;
using UnityEngine;

namespace MateEngine.Vision
{
    [Serializable]
    public sealed class VisionClip { public string name; public AnimationClip clip; public VisionPropEvent[] events; }
    [Serializable]
    public sealed class VisionPropEvent { public float time; public string operation, prop; public bool carry; }
    [Serializable]
    public sealed class VisionPropAsset
    {
        public string name, bone, carryBone;
        public GameObject prefab;
        public Vector3 position, carryPosition, boneGrip, meshGrip, scale = Vector3.one;
        public Quaternion rotation = Quaternion.identity, carryRotation = Quaternion.identity;
        public bool hasGrip;
    }
    [Serializable]
    public sealed class VisionBoneRest
    {
        public HumanBodyBones bone;
        public Vector3 position;
        public Quaternion rotation;
    }
    public sealed class VisionLibrary : ScriptableObject
    {
        public GameObject rig;
        public VisionBoneRest[] rest;
        public VisionClip[] clips;
        public VisionPropAsset[] props;
    }
}
