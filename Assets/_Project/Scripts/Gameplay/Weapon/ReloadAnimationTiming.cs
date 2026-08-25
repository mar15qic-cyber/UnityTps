using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// Maps a reload clip onto the authoritative gameplay reload window.
    /// Gameplay duration stays in the balance config; presentation speed adapts to it.
    /// </summary>
    public static class ReloadAnimationTiming
    {
        public static float GetPlaybackSpeed(AnimationClip clip, float reloadDuration)
        {
            if (clip == null || clip.length <= 0f || reloadDuration <= 0f)
                return 1f;
            return clip.length / reloadDuration;
        }
    }
}
