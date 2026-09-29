using System.Linq;
using System.Reflection;
using Game.Presentation.Animation;
using Game.UI;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>Promotes the accepted cooperation clips and adds one shared draw.</summary>
    public static class FirstPersonThrowablePresentationBuilder
    {
        public const string Folder = "Assets/_Project/Animations/ThrowableViews";
        public const string DrawPath = Folder + "/SharedDraw.anim";

        [MenuItem("Tools/Review/Build Shared Throwable Draw And Apply Accepted Throw")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/_Project/Animations", "ThrowableViews");
            var draw = BakeDraw();
            foreach (var kind in new[] { "Frag", "Flash", "Smoke" })
            {
                var prepare = CopyClip(kind + "-Prepare");
                var throwing = CopyClip(kind + "-Throw");
                var model = PrefabUtility.LoadPrefabContents(PinCooperationMotionDraft.Folder + "/" + kind + ".prefab");
                try
                {
                    var config = model.GetComponent<FPThrowablePresentation>();
                    if (config == null) config = model.AddComponent<FPThrowablePresentation>();
                    config.Configure(draw, prepare, throwing);
                    PrefabUtility.SaveAsPrefabAsset(model, "Assets/_Project/Resources/ThrowableViews/" + kind + ".prefab");
                }
                finally { PrefabUtility.UnloadPrefabContents(model); }
            }
            AssetDatabase.SaveAssets();
        }

        private static AnimationClip CopyClip(string name)
        {
            var source = AssetDatabase.LoadAssetAtPath<AnimationClip>(PinCooperationMotionDraft.Folder + "/" + name + ".anim");
            if (source == null) throw new System.InvalidOperationException("Accepted cooperation clip missing: " + name);
            var copy = new AnimationClip(); EditorUtility.CopySerialized(source, copy); copy.name = name;
            if (name.EndsWith("-Throw")) ApplyEmptyHandRecovery(copy);
            return Save(copy, Folder + "/" + name + ".anim");
        }

        // Promote the accepted power stroke unchanged, then withdraw the empty
        // hand instead of restoring the grenade-ready fist for the final .25 s.
        // The action still ends at 1.15 s; only its visual recovery is replaced.
        [MenuItem("Tools/Review/Rebuild Throwable Empty Hand Recovery")]
        public static void RebuildThrowRecovery()
        {
            foreach (var kind in new[] { "Frag", "Flash", "Smoke" }) CopyClip(kind + "-Throw");
            AssetDatabase.SaveAssets();
        }

        private static void ApplyEmptyHandRecovery(AnimationClip clip)
        {
            const float start = .53f, end = .9f;
            var weapon = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == "weapon.m4").definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(weapon.FirstPersonViewPrefab));
            try
            {
                var upper = root.transform.Find("Armature/arm_R");
                var lower = upper.Find("lower_arm_R"); var hand = lower.Find("hand_R");
                var bones = upper.GetComponentsInChildren<Transform>(true);
                var fingers = hand.GetComponentsInChildren<Transform>(true).Where(t => t != hand).ToArray();
                clip.SampleAnimation(root, 0);
                var closed = fingers.Select(t => t.localRotation).ToArray();
                clip.SampleAnimation(root, .5f);
                var open = fingers.Select(t => t.localRotation).ToArray();
                clip.SampleAnimation(root, start - 1f / 120f); var previous = hand.position;
                clip.SampleAnimation(root, start);
                var wrist = hand.position; var velocity = (wrist - previous) * 120f;
                var shoulder = upper.position; var palm = hand.rotation;
                var finish = root.transform.TransformPoint(new Vector3(.26f, -.43f, .10f));
                var solve = typeof(FirstPersonThrowClipBuilder).GetMethod("SolveArm", BindingFlags.Static | BindingFlags.NonPublic);
                var curves = bones.Select(_ => Enumerable.Range(0, 7).Select(i => new AnimationCurve()).ToArray()).ToArray();
                string[] properties = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
                    "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
                // Preserve every earlier key and tangent. The left arm and pin
                // curves are never rewritten by this recovery pass.
                for (int b = 0; b < bones.Length; b++) for (int c = 0; c < 7; c++)
                {
                    var binding = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(bones[b], root.transform), typeof(Transform), properties[c]);
                    var original = AnimationUtility.GetEditorCurve(clip, binding);
                    foreach (var key in original.keys.Where(k => k.time <= start)) curves[b][c].AddKey(key);
                }
                for (int frame = 64; frame <= 138; frame++)
                {
                    float t = frame / 120f, u = Mathf.Clamp01((t - start) / (end - start));
                    float smooth = Mathf.SmoothStep(0, 1, u);
                    clip.SampleAnimation(root, t);
                    upper.position = Vector3.Lerp(shoulder, root.transform.TransformPoint(new Vector3(.19f, -.12f, 0)), smooth);
                    var target = (2*u*u*u-3*u*u+1)*wrist + (u*u*u-2*u*u+u)*velocity*(end-start)
                        + (-2*u*u*u+3*u*u)*finish;
                    solve.Invoke(null, new object[] { upper, lower, hand, target,
                        root.transform.TransformPoint(new Vector3(.43f, -.30f, .03f)) });
                    hand.rotation = Quaternion.Slerp(palm, root.transform.rotation * Quaternion.Euler(50, -38, 18), smooth);
                    for (int f = 0; f < fingers.Length; f++) fingers[f].localRotation = Quaternion.Slerp(open[f], closed[f], .2f * smooth);
                    for (int b = 0; b < bones.Length; b++)
                    {
                        var p = bones[b].localPosition; var q = bones[b].localRotation;
                        var values = new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w };
                        for (int c = 0; c < 7; c++) curves[b][c].AddKey(t, values[c]);
                    }
                }
                for (int b = 0; b < bones.Length; b++) for (int c = 0; c < 7; c++)
                {
                    for (int k = 0; k < curves[b][c].length; k++)
                        if (curves[b][c].keys[k].time > start) curves[b][c].SmoothTangents(k, 0);
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(
                        AnimationUtility.CalculateTransformPath(bones[b], root.transform), typeof(Transform), properties[c]), curves[b][c]);
                }
                clip.EnsureQuaternionContinuity();
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static AnimationClip BakeDraw()
        {
            var weapon = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == "weapon.m4").definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(weapon.FirstPersonViewPrefab));
            var prepare = AssetDatabase.LoadAssetAtPath<AnimationClip>(PinCooperationMotionDraft.Folder + "/Frag-Prepare.anim");
            var solve = typeof(FirstPersonThrowClipBuilder).GetMethod("SolveArm", BindingFlags.Static | BindingFlags.NonPublic);
            var clip = new AnimationClip { name = "SharedThrowableDraw", frameRate = 120 };
            try
            {
                var bones = root.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Armature"
                    || AnimationUtility.CalculateTransformPath(t, root.transform).StartsWith("Armature/arm_")).ToArray();
                var upper = root.transform.Find("Armature/arm_R"); var lower = upper.Find("lower_arm_R"); var hand = lower.Find("hand_R");
                var curves = bones.Select(_ => Enumerable.Range(0,7).Select(i => new AnimationCurve()).ToArray()).ToArray();
                float[] times = { 0, .09f, .18f, .30f, .42f };
                Vector3[] wrists = { new(.24f,-.43f,.12f), new(.27f,-.28f,.19f), new(.22f,-.105f,.285f), new(.158f,-.031f,.337f), new(.16f,-.04f,.33f) };
                for(int frame=0;frame<=51;frame++)
                {
                    float t=Mathf.Min(frame/120f,.42f);
                    prepare.SampleAnimation(root,0);
                    if(t<.42f)
                    {
                        int k=0; while(k<times.Length-2 && t>times[k+1]) k++;
                        float dt=times[k+1]-times[k],u=Mathf.Clamp01((t-times[k])/dt);
                        Vector3 Tangent(int i) => i==0 || i==times.Length-1 ? Vector3.zero : (wrists[i+1]-wrists[i-1])/(times[i+1]-times[i-1]);
                        var target=(2*u*u*u-3*u*u+1)*wrists[k]+(u*u*u-2*u*u+u)*Tangent(k)*dt
                            +(-2*u*u*u+3*u*u)*wrists[k+1]+(u*u*u-u*u)*Tangent(k+1)*dt;
                        float settle=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.03f,.34f,t));
                        var pole=Vector3.Lerp(new Vector3(.43f,-.30f,.03f),new Vector3(.38f,-.23f,.13f),settle);
                        solve.Invoke(null,new object[]{upper,lower,hand,root.transform.TransformPoint(target),root.transform.TransformPoint(pole)});
                        hand.rotation=root.transform.rotation*Quaternion.Slerp(Quaternion.Euler(50,-38,18),Quaternion.Euler(90,-20,-10),settle);
                    }
                    for(int b=0;b<bones.Length;b++)
                    {
                        var p=bones[b].localPosition; var q=bones[b].localRotation; var values=new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w};
                        for(int c=0;c<7;c++) curves[b][c].AddKey(t,values[c]);
                    }
                }
                string[] properties={"m_LocalPosition.x","m_LocalPosition.y","m_LocalPosition.z","m_LocalRotation.x","m_LocalRotation.y","m_LocalRotation.z","m_LocalRotation.w"};
                for(int b=0;b<bones.Length;b++) for(int c=0;c<7;c++)
                {
                    for(int k=0;k<curves[b][c].length;k++) curves[b][c].SmoothTangents(k,0);
                    AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(bones[b],root.transform),typeof(Transform),properties[c]),curves[b][c]);
                }
                clip.EnsureQuaternionContinuity();
                return Save(clip,DrawPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static AnimationClip Save(AnimationClip clip,string path)
        {
            var old=AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if(old==null) {AssetDatabase.CreateAsset(clip,path); return clip;}
            EditorUtility.CopySerialized(clip,old); Object.DestroyImmediate(clip); return old;
        }
    }
}
