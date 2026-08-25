using System.Collections.Generic;
using System.Text;
using Game.Gameplay.Movement;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    public static class RootMotionProfileBaker
    {
        private const string OutputFolder = "Assets/_Project/ScriptableObjects/Movement";

        [MenuItem("Tools/UnityFps/Movement/Bake All Root Motion Profiles")]
        public static void BakeAllProfiles()
        {
            EnsureFolder(OutputFolder);
            string[] definitionGuids = AssetDatabase.FindAssets("t:WeaponDefinition", new[]
            {
                "Assets/_Project/ScriptableObjects/Weapons"
            });

            int bakedCount = 0;
            foreach (string guid in definitionGuids)
            {
                string definitionPath = AssetDatabase.GUIDToAssetPath(guid);
                var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(definitionPath);
                if (definition == null) continue;

                TpLocomotionSet clips = definition.ThirdPersonLocomotion;
                AnimationClip[] walkClips = GetWalkClips(clips);
                AnimationClip[] runClips = GetRunClips(clips);
                if (!AllAssigned(walkClips) || !AllAssigned(runClips))
                {
                    Debug.LogWarning($"[RootMotionProfileBaker] '{definition.name}' 的 Walk/Run 八方向 clip 不完整，已跳过。", definition);
                    continue;
                }

                var walkTracks = BakeTracks(walkClips);
                var runTracks = BakeTracks(runClips);
                string assetPath = $"{OutputFolder}/{definition.name}_RootMotionProfile.asset";
                var profile = AssetDatabase.LoadAssetAtPath<RootMotionProfile>(assetPath);
                if (profile == null)
                {
                    profile = ScriptableObject.CreateInstance<RootMotionProfile>();
                    AssetDatabase.CreateAsset(profile, assetPath);
                }

                string versionHash = BuildVersionHash(walkClips, runClips);
                profile.SetBakedData(versionHash, walkClips[0].length, runClips[0].length, walkTracks, runTracks);
                EditorUtility.SetDirty(profile);

                var serializedDefinition = new SerializedObject(definition);
                serializedDefinition.FindProperty("thirdPersonRootMotionProfile").objectReferenceValue = profile;
                serializedDefinition.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(definition);
                bakedCount++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[RootMotionProfileBaker] 已生成并接线 {bakedCount} 个确定性根运动 Profile。", null);
        }

        private static RootMotionTrack[] BakeTracks(AnimationClip[] clips)
        {
            var tracks = new RootMotionTrack[RootMotionProfile.DirectionCount];
            for (int i = 0; i < tracks.Length; i++) tracks[i] = BakeTrack(clips[i]);
            return tracks;
        }

        private static RootMotionTrack BakeTrack(AnimationClip clip)
        {
            AnimationCurve rootX = FindCurve(clip, "RootT.x");
            AnimationCurve rootZ = FindCurve(clip, "RootT.z");
            AnimationCurve rootQx = FindCurve(clip, "RootQ.x");
            AnimationCurve rootQy = FindCurve(clip, "RootQ.y");
            AnimationCurve rootQz = FindCurve(clip, "RootQ.z");
            AnimationCurve rootQw = FindCurve(clip, "RootQ.w");

            int sampleCount = RootMotionProfile.SamplesPerCycle + 1;
            var positions = new Vector2[sampleCount];
            var yaw = new float[sampleCount];
            Vector2 origin = EvaluatePosition(rootX, rootZ, 0f);
            float previousYaw = EvaluateYaw(rootQx, rootQy, rootQz, rootQw, 0f);
            float accumulatedYaw = 0f;

            for (int i = 0; i < sampleCount; i++)
            {
                float time = clip.length * i / RootMotionProfile.SamplesPerCycle;
                positions[i] = EvaluatePosition(rootX, rootZ, time) - origin;
                float currentYaw = EvaluateYaw(rootQx, rootQy, rootQz, rootQw, time);
                if (i > 0) accumulatedYaw += Mathf.DeltaAngle(previousYaw, currentYaw);
                yaw[i] = accumulatedYaw;
                previousYaw = currentYaw;
            }

            var track = new RootMotionTrack();
            track.SetBakedData(clip.name, clip.length, positions, yaw);
            return track;
        }

        private static AnimationCurve FindCurve(AnimationClip clip, string propertyName)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
                if (binding.path == string.Empty && binding.propertyName == propertyName)
                    return AnimationUtility.GetEditorCurve(clip, binding);
            return null;
        }

        private static Vector2 EvaluatePosition(AnimationCurve x, AnimationCurve z, float time)
            => new Vector2(x != null ? x.Evaluate(time) : 0f, z != null ? z.Evaluate(time) : 0f);

        private static float EvaluateYaw(
            AnimationCurve x,
            AnimationCurve y,
            AnimationCurve z,
            AnimationCurve w,
            float time)
        {
            if (x == null || y == null || z == null || w == null) return 0f;
            var rotation = new Quaternion(x.Evaluate(time), y.Evaluate(time), z.Evaluate(time), w.Evaluate(time));
            float sqrMagnitude = rotation.x * rotation.x + rotation.y * rotation.y +
                                 rotation.z * rotation.z + rotation.w * rotation.w;
            if (sqrMagnitude < 0.0001f) return 0f;
            rotation = Quaternion.Normalize(rotation);
            return rotation.eulerAngles.y;
        }

        private static string BuildVersionHash(AnimationClip[] walkClips, AnimationClip[] runClips)
        {
            var builder = new StringBuilder();
            AppendClipVersions(builder, walkClips);
            AppendClipVersions(builder, runClips);
            return Hash128.Compute(builder.ToString()).ToString();
        }

        private static void AppendClipVersions(StringBuilder builder, IEnumerable<AnimationClip> clips)
        {
            foreach (AnimationClip clip in clips)
            {
                string path = AssetDatabase.GetAssetPath(clip);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long localId);
                builder.Append(guid).Append(':').Append(localId).Append(':')
                    .Append(clip.name).Append(':').Append(clip.length).Append(':')
                    .Append(AssetDatabase.GetAssetDependencyHash(path)).Append('|');
            }
        }

        private static AnimationClip[] GetWalkClips(TpLocomotionSet c) => new[]
        {
            c.WalkForward, c.WalkForwardRight, c.WalkRight, c.WalkBackRight,
            c.WalkBackward, c.WalkBackLeft, c.WalkLeft, c.WalkForwardLeft
        };

        private static AnimationClip[] GetRunClips(TpLocomotionSet c) => new[]
        {
            c.RunForward, c.RunForwardRight, c.RunRight, c.RunBackRight,
            c.RunBackward, c.RunBackLeft, c.RunLeft, c.RunForwardLeft
        };

        private static bool AllAssigned(AnimationClip[] clips)
        {
            foreach (AnimationClip clip in clips) if (clip == null) return false;
            return true;
        }

        private static void EnsureFolder(string folder)
        {
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
