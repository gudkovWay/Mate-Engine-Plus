using UnityEngine;

namespace MateEngine.Vision
{
    public static class VisionAudioPolicy
    {
        static Animator owner;
        public static bool Owns(Animator animator) => animator != null && owner == animator;
        public static void Claim(Animator animator) { owner = animator; }
        public static void Release(Animator animator) { if (owner == animator) owner = null; }
    }
}
