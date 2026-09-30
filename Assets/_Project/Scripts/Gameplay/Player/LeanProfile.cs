using UnityEngine;

namespace Game.Gameplay.Player
{
    /// <summary>One geometric contract for owner, dedicated server, and observers.</summary>
    public static class LeanProfile
    {
        public const float EyeSideMeters = 0.34f;
        public const float EyeHeightMeters = 1.62f;
        public const float BodyCenterForwardMeters = 0.333f;
        public const float MaxBodyRollDegrees = 26f;
        public const float MaxCameraRollDegrees = 16f;
        public const float LeanInSeconds = 0.11f;
        public const float LeanOutSeconds = 0.095f;

        public static Vector3 Eye(Vector3 root, Quaternion yaw, float lean)
            => root + yaw * new Vector3(Mathf.Clamp(lean, -1f, 1f) * EyeSideMeters, EyeHeightMeters, BodyCenterForwardMeters);

        public static Vector3 Muzzle(Vector3 root, Quaternion yaw, float lean)
            => root + yaw * new Vector3(Mathf.Clamp(lean, -1f, 1f) * EyeSideMeters * .9f, 1.4f, .6f);

        public static Vector3 BodyAnchor(Vector3 root, Quaternion yaw, float lean)
            => root + yaw * new Vector3(Mathf.Clamp(lean, -1f, 1f) * EyeSideMeters * .55f, 1f, 0f);

        public static Vector3 HeadCenter(Vector3 root, Quaternion yaw, float lean)
            => root + yaw * new Vector3(Mathf.Clamp(lean, -1f, 1f) * EyeSideMeters, 1.6f, BodyCenterForwardMeters);

        public static Vector3 ChestCenter(Vector3 root, Quaternion yaw, float lean)
            => root + yaw * new Vector3(Mathf.Clamp(lean, -1f, 1f) * EyeSideMeters * .45f, 1.02f, BodyCenterForwardMeters);
    }
}
