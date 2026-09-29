using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.UI;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>Bakes a readable first-person throw into an owned clip; never edits the vendor FBX.</summary>
    public static class FirstPersonThrowClipBuilder
    {
        public const string ClipPath = "Assets/_Project/Animations/FP_Throwable_VisibleArc.anim";
        public const string PrepareClipPath = "Assets/_Project/Animations/FP_Throwable_Prepare.anim";
        private const float Duration = 1.15f;
        // Preserve preparation/support-hand timing. Right-arm drive has its own
        // shoulder/upper/forearm tracks below, all in the same stable view frame.
        private static readonly float[] Times = { 0f, .08f, .12f, .21f, .34f, .50f, .70f, .9f, Duration };
        private static readonly Vector3 ReadyWrist = new(.16f,-.04f,.33f);
        private static readonly Quaternion ReadyPalm = Quaternion.Euler(90, -20, -10);
        private static readonly Quaternion ForwardPalm = Quaternion.LookRotation(Vector3.right, Vector3.forward);
        private static readonly Vector3 SupportRest = new(-.20f, -.32f, .15f);
        private static readonly Vector3 SupportContact = new(.072f, -.015f, .33f);
        private static readonly Vector3[] SupportWrists = { SupportContact, SupportContact, SupportContact,
            new(-.095f,-.025f,.35f), new(-.22f,-.09f,.31f), new(-.31f,-.23f,.22f),
            new(-.28f,-.39f,.12f), SupportRest, SupportRest };
        private static readonly Quaternion SupportPalm = Quaternion.LookRotation(Vector3.up, Vector3.forward);

        [MenuItem("Tools/Review/Bake Visible First Person Throw")]
        public static void Build()
        {
            Bake(false);
            Bake(true);
            FirstPersonPinAssetBuilder.Build();
            PinCooperationMotionDraft.Build();
            FirstPersonThrowablePresentationBuilder.Build();
        }

        private static void Bake(bool prepare)
        {
            var catalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            var source = catalog.Entries.First(e => e.itemId == "weapon.m4").definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(source.FirstPersonViewPrefab));
            var clip = new AnimationClip { name = prepare ? "FP_Throwable_Prepare" : "FP_Throwable_Overhand", frameRate = 60f };
            try
            {
                source.FirstPersonAnimations.Idle.SampleAnimation(root, 0f);
                var bones = root.GetComponentsInChildren<Transform>(true).Where(t =>
                    t.name == "Armature" || AnimationUtility.CalculateTransformPath(t, root.transform).StartsWith("Armature/arm_")).ToArray();
                var positions = bones.Select(t => t.localPosition).ToArray();
                var rotations = bones.Select(t => t.localRotation).ToArray();
                var upper = root.transform.Find("Armature/arm_R");
                var lower = upper.Find("lower_arm_R");
                var hand = lower.Find("hand_R");
                var leftUpper = root.transform.Find("Armature/arm_L");
                var leftLower = leftUpper.Find("lower_arm_L");
                var leftHand = leftLower.Find("hand_L");
                var curves = bones.Select(_ => Enumerable.Range(0, 7).Select(i => new AnimationCurve()).ToArray()).ToArray();
                for (int frame = 0; frame <= (prepare ? 24 : 69); frame++)
                {
                    float time = frame / 60f;
                    for (int i = 0; i < bones.Length; i++)
                    { bones[i].localPosition = positions[i]; bones[i].localRotation = rotations[i]; }
                    if (prepare)
                    {
                        upper.position = root.transform.TransformPoint(new Vector3(.19f, -.12f, 0f));
                        SolveArm(upper, lower, hand, root.transform.TransformPoint(ReadyWrist),
                            root.transform.TransformPoint(new Vector3(.39f, -.22f, .12f)));
                        hand.rotation = root.transform.rotation * ReadyPalm;
                    }
                    else PoseThrowingArm(root.transform, upper, lower, hand, time);
                    // Reach while holding the input, then separate before the right arm extends.
                    float reach = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, .26f, time));
                    var support = prepare ? Vector3.Lerp(SupportRest, SupportContact, reach)
                        : Sample(time, Times, SupportWrists);
                    leftUpper.position = root.transform.TransformPoint(new Vector3(-.19f, -.14f, 0f));
                    SolveArm(leftUpper, leftLower, leftHand, root.transform.TransformPoint(support),
                        root.transform.TransformPoint(new Vector3(-.36f, -.3f, .02f)));
                    float turn = prepare ? 1f - reach : Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.12f, .30f, time));
                    leftHand.rotation = root.transform.rotation * Quaternion.AngleAxis((prepare ? 25f : 55f) * turn, Vector3.up) * SupportPalm;
                    SetGrip(hand, prepare ? 0f : time);
                    float pinch = prepare ? Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.17f, .32f, time))
                        : 1f - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.55f, .78f, time));
                    SetSupportGrip(leftHand, pinch);
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var p = bones[i].localPosition; var q = bones[i].localRotation;
                        var values = new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w };
                        for (int n = 0; n < 7; n++) curves[i][n].AddKey(time, values[n]);
                    }
                }
                string[] properties = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
                    "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
                for (int i = 0; i < bones.Length; i++)
                for (int n = 0; n < 7; n++)
                {
                    var path = AnimationUtility.CalculateTransformPath(bones[i], root.transform);
                    // AddKey defaults to zero tangents; without this, slow playback
                    // repeatedly brakes at every 60 Hz sample of the right arm.
                    if (!prepare && path.StartsWith("Armature/arm_R"))
                        for (int key = 0; key < curves[i][n].length; key++) curves[i][n].SmoothTangents(key, 0f);
                    if (path.StartsWith("Armature/arm_L"))
                        for (int key = 0; key < curves[i][n].length; key++) curves[i][n].SmoothTangents(key, 0f);
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(
                        path, typeof(Transform), properties[n]), curves[i][n]);
                }
                clip.EnsureQuaternionContinuity();
                if (!AssetDatabase.IsValidFolder("Assets/_Project/Animations")) AssetDatabase.CreateFolder("Assets/_Project", "Animations");
                var pathForClip = prepare ? PrepareClipPath : ClipPath;
                var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(pathForClip);
                if (existing == null) AssetDatabase.CreateAsset(clip, pathForClip);
                else { EditorUtility.CopySerialized(clip, existing); Object.DestroyImmediate(clip); clip = existing; }
                foreach (var definition in catalog.Entries.Where(e => e.definition != null).Select(e => e.definition).Distinct())
                {
                    var path = AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab);
                    if (string.IsNullOrEmpty(path)) continue;
                    var view = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        var animator = view.GetComponent<FPWeaponAnimator>();
                        if (animator == null) continue;
                        var so = new SerializedObject(animator);
                        so.FindProperty(prepare ? "throwablePrepareClip" : "throwablePresentationClip").objectReferenceValue = clip;
                        so.FindProperty("throwViewLocalPosition").vector3Value = new Vector3(0f, -.09f, .18f);
                        so.FindProperty("throwViewLocalEuler").vector3Value = Vector3.zero;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        PrefabUtility.SaveAsPrefabAsset(view, path);
                    }
                    finally { PrefabUtility.UnloadPrefabContents(view); }
                }
                AssetDatabase.SaveAssets();
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // This rig has no clavicle. arm_R is the shoulder pivot, not a wrist IK
        // target. Protraction, upper-arm swing and forearm extension have separate
        // overlapping clocks; local bone lengths remain the imported lengths.
        private static void PoseThrowingArm(Transform root, Transform upper, Transform lower, Transform hand, float time)
        {
            var shoulder = Sample(time, ShoulderTimes, Shoulders);
            var upperDirection = Sample(time, UpperTimes, UpperDirections).normalized;
            var forearmDirection = Sample(time, ForearmTimes, ForearmDirections).normalized;
            upper.position = root.TransformPoint(shoulder);
            // A small opposing brace precedes the existing shoulder-led throw.
            // It is gone by 0.18s, leaving the previous power stroke unchanged.
            float brace = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.035f, .10f, time))
                * (1f - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.12f, .18f, time)));
            upper.position += root.TransformVector(new Vector3(.008f, 0, -.004f) * brace);
            upper.rotation = Quaternion.FromToRotation(lower.position - upper.position,
                root.TransformDirection(upperDirection)) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position,
                root.TransformDirection(forearmDirection)) * lower.rotation;
            hand.rotation = root.rotation * SamplePalm(time);
        }

        private static readonly float[] ShoulderTimes = { 0, .10f, .21f, .28f, .35f, .40f, .53f, .90f, Duration };
        private static readonly Vector3[] Shoulders = { new(.19f,-.12f,0), new(.19f,-.12f,0),
            new(.205f,-.105f,-.012f), new(.185f,-.096f,.025f), new(.165f,-.088f,.046f),
            new(.150f,-.10f,.065f), new(.16f,-.13f,.025f), new(.19f,-.12f,0), new(.19f,-.12f,0) };
        private static readonly Vector3 ReadyUpper = new Vector3(.1224f,-.0393f,.1908f).normalized;
        private static readonly Vector3 ReadyForearm = new Vector3(-.1524f,.1193f,.1392f).normalized;
        private static readonly float[] UpperTimes = { 0, .10f, .21f, .28f, .32f, .38f, .48f, .66f, .90f, Duration };
        private static readonly Vector3[] UpperDirections = { ReadyUpper, ReadyUpper,
            new(.82f,.10f,.56f), new(.36f,.10f,.928f), new(-.02f,.07f,.997f),
            new(-.30f,.04f,.953f), new(-.42f,-.23f,.878f), new(.38f,-.30f,.875f), ReadyUpper, ReadyUpper };
        private static readonly float[] ForearmTimes = { 0, .10f, .235f, .295f, .33f, .365f, .415f, .49f, .68f, .90f, Duration };
        private static readonly Vector3[] ForearmDirections = { ReadyForearm, ReadyForearm,
            new(-.75f,.64f,.16f), new(-.60f,.75f,-.27f), new(-.45f,.80f,.40f),
            new(-.33f,.26f,.907f), new(-.25f,0,.968f), new(-.45f,-.27f,.85f),
            new(-.66f,.17f,.732f), ReadyForearm, ReadyForearm };
        private static readonly float[] PalmTimes = { 0, .10f, .21f, .27f, .31f, .35f, .41f, .53f, .72f, .90f, Duration };
        private static readonly Quaternion CockedPalm = Quaternion.Euler(40,-25,-15);
        private static readonly Quaternion[] Palms = { ReadyPalm, ReadyPalm, CockedPalm, CockedPalm,
            Quaternion.Slerp(CockedPalm, ForwardPalm, .3f), ForwardPalm,
            Quaternion.Euler(20,0,-10) * ForwardPalm, Quaternion.Euler(40,0,0) * ForwardPalm,
            ReadyPalm, ReadyPalm, ReadyPalm };

        // Nonuniform cubic Hermite interpolation carries velocity through keys.
        // Per-segment SmoothStep would stop every joint at every authored pose,
        // including release, which destroys the overlapping acceleration.
        private static Vector3 Sample(float time, float[] times, Vector3[] values)
        {
            int k = 0; while (k < times.Length - 2 && time > times[k + 1]) k++;
            float dt = times[k + 1] - times[k], u = Mathf.Clamp01((time - times[k]) / dt);
            var a = Tangent(k, times, values) * dt;
            var b = Tangent(k + 1, times, values) * dt;
            return (2*u*u*u-3*u*u+1)*values[k] + (u*u*u-2*u*u+u)*a
                + (-2*u*u*u+3*u*u)*values[k+1] + (u*u*u-u*u)*b;
        }

        private static Vector3 Tangent(int i, float[] times, Vector3[] values)
        {
            if (i == 0 || i == times.Length - 1 || values[i] == values[i-1] || values[i] == values[i+1]) return Vector3.zero;
            return (values[i+1] - values[i-1]) / (times[i+1] - times[i-1]);
        }

        private static Quaternion SamplePalm(float time)
        {
            int k = 0; while (k < PalmTimes.Length - 2 && time > PalmTimes[k+1]) k++;
            float u = Mathf.InverseLerp(PalmTimes[k], PalmTimes[k+1], time);
            return Quaternion.Slerp(Palms[k], Palms[k+1], u);
        }

        private static void SetGrip(Transform hand, float time)
        {
            float open = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.31f, .37f, time));
            open *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.65f, .9f, time));
            foreach (string prefix in new[] { "finger", "middle_finger" })
            {
                var first = hand.Find(prefix + "_01_R");
                var second = first.Find(prefix + "_02_R");
                var third = second.Find(prefix + "_03_R");
                first.localRotation = Quaternion.Slerp(Quaternion.Euler(0, 0, 88), Quaternion.identity, open);
                second.localRotation = Quaternion.Slerp(Quaternion.Euler(0, 180, 316), Quaternion.Euler(0, 180, 0), open);
                third.localRotation = Quaternion.Slerp(Quaternion.Euler(0, 0, 314), Quaternion.identity, open);
            }
            // Explicit opposition: do not inherit the rifle's trigger/support thumb pose.
            var thumb = hand.Find("thumb_01_R");
            thumb.localRotation = Quaternion.Slerp(Quaternion.Euler(-12, 175, 275), Quaternion.Euler(-15, 165, 315), open);
            thumb.Find("thumb_02_R").localRotation = Quaternion.Euler(0, 180, 306);
            thumb.Find("thumb_02_R/thumb_03_R").localRotation = Quaternion.Euler(0, 0, 320);
        }

        private static void SetSupportGrip(Transform hand, float contact)
        {
            foreach (string prefix in new[] { "finger", "middle_finger" })
            {
                var first = hand.Find(prefix + "_01_L");
                bool index = prefix == "finger";
                first.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(index ? 5 : 24, index ? 60 : 35, contact));
                first.Find(prefix + "_02_L").localRotation = Quaternion.Euler(0, 180, Mathf.Lerp(index ? 355 : 335, index ? 288 : 330, contact));
                first.Find(prefix + "_02_L/" + prefix + "_03_L").localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(350, index ? 302 : 338, contact));
            }
            var thumb = hand.Find("thumb_01_L");
            thumb.localRotation = Quaternion.Euler(0, 180, Mathf.Lerp(335, 285, contact));
            thumb.Find("thumb_02_L").localRotation = Quaternion.Euler(0, 180, Mathf.Lerp(335, 310, contact));
            thumb.Find("thumb_02_L/thumb_03_L").localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(350, 320, contact));
        }

        private static void SolveArm(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole)
        {
            Vector3 origin = upper.position;
            float a = Vector3.Distance(origin, lower.position), b = Vector3.Distance(lower.position, hand.position);
            Vector3 axis = (target - origin).normalized;
            float d = Mathf.Clamp(Vector3.Distance(origin, target), Mathf.Abs(a - b) + .0001f, a + b - .0001f);
            float along = (a * a + d * d - b * b) / (2f * d);
            Vector3 bend = Vector3.ProjectOnPlane(pole - origin, axis).normalized;
            Vector3 elbow = origin + axis * along + bend * Mathf.Sqrt(Mathf.Max(0f, a * a - along * along));
            upper.rotation = Quaternion.FromToRotation(lower.position - origin, elbow - origin) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, origin + axis * d - lower.position) * lower.rotation;
        }
    }
}
