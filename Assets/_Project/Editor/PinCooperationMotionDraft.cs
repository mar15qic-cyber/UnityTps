using System;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Combat;
using Game.Presentation.Animation;
using Game.UI;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    // Review assets only. Never changes the live clip references or gameplay clock.
    public static class PinCooperationMotionDraft
    {
        public const string Folder = "Assets/_Project/Review/PinCooperation";
        private static readonly MethodInfo Solve = typeof(FirstPersonThrowClipBuilder).GetMethod("SolveArm", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Grip = typeof(FirstPersonThrowClipBuilder).GetMethod("SetSupportGrip", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly Quaternion ReadyPalm = Quaternion.Euler(90,-20,-10);
        private static readonly Quaternion ContactPalm = Quaternion.LookRotation(new Vector3(-.1f,.82f,-.56f), new Vector3(.85f,.10f,.51f));
        private static readonly Quaternion ApproachPalm = Quaternion.LookRotation(new Vector3(-.16f,.78f,-.60f), new Vector3(.85f,.10f,.51f));
        private static readonly Vector3 Rest = new(-.20f,-.32f,.15f);

        public static void AlignModel(GameObject model)
        {
            FPWeaponAnimator.AlignHeldThrowable(model);
            // Grip indexing, present from ready state: not a 180-degree animated spin.
            if (model.GetComponent<FPThrowablePresentation>() == null)
                model.transform.localRotation *= Quaternion.Euler(0,180,0);
        }

        [MenuItem("Tools/Review/Build Pin Cooperation Motion Draft")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Review")) AssetDatabase.CreateFolder("Assets/_Project", "Review");
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/_Project/Review", "PinCooperation");
            var weapon = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == "weapon.m4").definition;
            var baseline = AssetDatabase.LoadAssetAtPath<AnimationClip>(FirstPersonThrowClipBuilder.ClipPath);
            foreach (var type in new[] { ThrowableType.Frag, ThrowableType.Flash, ThrowableType.Smoke })
            {
                var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(weapon.FirstPersonViewPrefab));
                try
                {
                    var model = (GameObject)PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("ThrowableViews/" + type), root.scene);
                    PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    model.transform.SetParent(root.transform.Find("Armature/arm_R/lower_arm_R/hand_R"), false);
                    AlignModel(model);
                    var rig = new Rig(root, model, baseline);
                    Bake(rig, true, type + "-Prepare");
                    Bake(rig, false, type + "-Throw");
                    // Calibrate this model at the unchanged visual extraction time.
                    rig.Pose(false, FPThrowablePinView.SeparationSeconds);
                    var pin = model.GetComponent<FPThrowablePinView>();
                    var finger = rig.Left.Find("finger_01_L/finger_02_L/finger_03_L");
                    pin.Configure(pin.Ring, pin.Grip, finger.InverseTransformPoint(pin.Ring.position), Quaternion.Inverse(finger.rotation) * pin.Ring.rotation);
                    model.transform.SetParent(null, false);
                    model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    PrefabUtility.SaveAsPrefabAsset(model, Folder + "/" + type + ".prefab");
                    Object.DestroyImmediate(model);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
        }

        private static void Bake(Rig rig, bool prepare, string name)
        {
            var clip = new AnimationClip { name = name, frameRate = 120 };
            var bones = rig.Root.GetComponentsInChildren<Transform>(true).Where(t =>
                t.name == "Armature" || AnimationUtility.CalculateTransformPath(t, rig.Root.transform).StartsWith("Armature/arm_") && !t.IsChildOf(rig.Model.transform)).ToArray();
            var curves = bones.Select(_ => Enumerable.Range(0,7).Select(i => new AnimationCurve()).ToArray()).ToArray();
            for (int frame=0; frame <= (prepare ? 48 : 138); frame++)
            {
                float t = frame/120f;
                rig.Pose(prepare,t);
                for(int b=0;b<bones.Length;b++)
                {
                    var p=bones[b].localPosition; var q=bones[b].localRotation;
                    var values=new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w};
                    for(int c=0;c<7;c++) curves[b][c].AddKey(t,values[c]);
                }
            }
            string[] properties={"m_LocalPosition.x","m_LocalPosition.y","m_LocalPosition.z","m_LocalRotation.x","m_LocalRotation.y","m_LocalRotation.z","m_LocalRotation.w"};
            for(int b=0;b<bones.Length;b++) for(int c=0;c<7;c++)
            {
                for(int key=0;key<curves[b][c].length;key++) curves[b][c].SmoothTangents(key,0);
                AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(bones[b],rig.Root.transform),typeof(Transform),properties[c]),curves[b][c]);
            }
            clip.EnsureQuaternionContinuity();
            string path=Folder+"/"+name+".anim";
            var existing=AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if(existing==null) AssetDatabase.CreateAsset(clip,path);
            else { EditorUtility.CopySerialized(clip,existing); Object.DestroyImmediate(clip); }
        }

        private sealed class Rig
        {
            public readonly GameObject Root, Model;
            public readonly Transform Left;
            private readonly Transform _ru,_re,_rw,_lu,_le,_index;
            private readonly AnimationClip _baseline;
            private readonly FPThrowablePinView _pin;
            private readonly Vector3 _contactWrist, _pinAxis, _contactForearm;

            public Rig(GameObject root, GameObject model, AnimationClip baseline)
            {
                Root=root; Model=model; _baseline=baseline;
                _ru=root.transform.Find("Armature/arm_R"); _re=_ru.Find("lower_arm_R"); _rw=_re.Find("hand_R");
                _lu=root.transform.Find("Armature/arm_L"); _le=_lu.Find("lower_arm_L"); Left=_le.Find("hand_L");
                _index=Left.Find("finger_01_L/finger_02_L/finger_03_L"); _pin=model.GetComponent<FPThrowablePinView>();
                baseline.SampleAnimation(root,.10f);
                CooperatingRight(1);
                LeftGrip(1);
                _lu.position=root.transform.TransformPoint(new Vector3(-.18f,-.115f,.035f));
                Left.rotation=root.transform.rotation*ContactPalm;
                var target=Left.position+_pin.Grip.position-_index.TransformPoint(FPThrowablePinView.FingerContact);
                Arm(_lu,_le,Left,target,new Vector3(-.34f,-.20f,.16f));
                Left.rotation=root.transform.rotation*ContactPalm;
                _contactWrist=root.transform.InverseTransformPoint(Left.position);
                _pinAxis=root.transform.InverseTransformDirection(model.transform.right);
                _contactForearm=root.transform.InverseTransformDirection(Left.position-_le.position).normalized;
            }

            private void Arm(Transform upper,Transform lower,Transform hand,Vector3 target,Vector3 pole)
                => Solve.Invoke(null,new object[]{upper,lower,hand,target,Root.transform.TransformPoint(pole)});

            private void CooperatingRight(float amount)
            {
                _ru.position=Root.transform.TransformPoint(Vector3.Lerp(new Vector3(.19f,-.12f,0),new Vector3(.19f,-.12f,.008f),amount));
                Arm(_ru,_re,_rw,Root.transform.TransformPoint(Vector3.Lerp(new Vector3(.16f,-.04f,.33f),new Vector3(.145f,-.005f,.325f),amount)),new Vector3(.38f,-.23f,.13f));
                _rw.rotation=Root.transform.rotation*Quaternion.Euler(0,-12*amount,30*amount)*ReadyPalm;
            }

            public void Pose(bool prepare,float t)
            {
                _baseline.SampleAnimation(Root,prepare?0:t);
                if(prepare) CooperatingRight(Smooth(.015f,.29f,t));
                else if(t<.21f)
                {
                    var shoulder=_ru.localPosition; var ur=_ru.localRotation; var er=_re.localRotation; var wr=_rw.localRotation;
                    CooperatingRight(1);
                    float blend=Smooth(.12f,.21f,t);
                    _ru.localPosition=Vector3.Lerp(_ru.localPosition,shoulder,blend);
                    _ru.localRotation=Quaternion.Slerp(_ru.localRotation,ur,blend);
                    _re.localRotation=Quaternion.Slerp(_re.localRotation,er,blend);
                    _rw.localRotation=Quaternion.Slerp(_rw.localRotation,wr,blend);
                }
                float pinch=prepare?Smooth(.13f,.31f,t):1-Smooth(.36f,.62f,t);
                LeftGrip(pinch);
                float reach=prepare?Smooth(.025f,.285f,t):1;
                float exit=prepare?0:Smooth(.12f,.52f,t);
                _lu.position=Root.transform.TransformPoint(Vector3.Lerp(new Vector3(-.19f,-.14f,0),new Vector3(-.18f,-.115f,.035f),reach*(1-exit)));
                Quaternion palm=Quaternion.Slerp(ApproachPalm,ContactPalm,prepare?Smooth(.19f,.31f,t):1);
                Left.rotation=Root.transform.rotation*palm;
                Vector3 target;
                if(prepare)
                {
                    var contact=Left.position+_pin.Grip.position-_index.TransformPoint(FPThrowablePinView.FingerContact);
                    target=Vector3.Lerp(Root.transform.TransformPoint(Rest),contact,reach);
                }
                else target=Root.transform.TransformPoint(Withdraw(t));
                Arm(_lu,_le,Left,target,Vector3.Lerp(new Vector3(-.34f,-.20f,.16f),new Vector3(-.36f,-.30f,.02f),exit));
                if(!prepare && t>.12f)
                {
                    // Hand follows the changing forearm direction, not a second palm-flip track.
                    var forearm=Root.transform.InverseTransformDirection(Left.position-_le.position).normalized;
                    palm=Quaternion.FromToRotation(_contactForearm,forearm)*ContactPalm;
                    palm=Quaternion.Slerp(palm,ApproachPalm,Smooth(.45f,.8f,t));
                }
                Left.rotation=Root.transform.rotation*palm;
            }

            private Vector3 Withdraw(float t)
            {
                float[] times={0,.12f,.16f,.19f,.27f,.36f,.48f,.65f,1.15f};
                Vector3[] values={_contactWrist,_contactWrist,_contactWrist+_pinAxis*.026f,_contactWrist+_pinAxis*.060f,
                    _contactWrist+new Vector3(-.145f,-.085f,-.055f),_contactWrist+new Vector3(-.215f,-.20f,-.125f),
                    _contactWrist+new Vector3(-.22f,-.29f,-.18f),Rest,Rest};
                int k=0; while(k<times.Length-2 && t>times[k+1]) k++;
                float dt=times[k+1]-times[k],u=Mathf.Clamp01((t-times[k])/dt);
                Vector3 Tangent(int i) => i==0 || i==times.Length-1 || values[i]==values[i-1] || values[i]==values[i+1]
                    ? Vector3.zero : (values[i+1]-values[i-1])/(times[i+1]-times[i-1]);
                return (2*u*u*u-3*u*u+1)*values[k]+(u*u*u-2*u*u+u)*Tangent(k)*dt
                    +(-2*u*u*u+3*u*u)*values[k+1]+(u*u*u-u*u)*Tangent(k+1)*dt;
            }

            private void LeftGrip(float pinch)
            {
                Grip.Invoke(null,new object[]{Left,pinch});
                Left.Find("middle_finger_01_L").localRotation=Quaternion.Euler(0,0,Mathf.Lerp(55,90,pinch));
                Left.Find("middle_finger_01_L/middle_finger_02_L").localRotation=Quaternion.Euler(0,180,Mathf.Lerp(300,270,pinch));
                Left.Find("middle_finger_01_L/middle_finger_02_L/middle_finger_03_L").localRotation=Quaternion.Euler(0,0,Mathf.Lerp(315,270,pinch));
            }
        }
        private static float Smooth(float a,float b,float t) => Mathf.SmoothStep(0,1,Mathf.InverseLerp(a,b,t));
    }
}
