var folder = Game.EditorTools.PinCooperationMotionDraft.Folder;
var baseline = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(Game.EditorTools.FirstPersonThrowClipBuilder.ClipPath);
var weapon = UnityEngine.Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e=>e.itemId=="weapon.m4").definition;
var results = new System.Collections.Generic.List<object>();
foreach (var kind in new[]{"Frag","Flash","Smoke"})
foreach (bool quick in new[]{false,true})
{
    var root = UnityEditor.PrefabUtility.LoadPrefabContents(UnityEditor.AssetDatabase.GetAssetPath(weapon.FirstPersonViewPrefab));
    try
    {
        var prep=UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(folder+"/"+kind+"-Prepare.anim");
        var clip=UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(folder+"/"+kind+"-Throw.anim");
        var right=root.transform.Find("Armature/arm_R/lower_arm_R/hand_R");
        var left=root.transform.Find("Armature/arm_L/lower_arm_L/hand_L");
        var index=left.Find("finger_01_L/finger_02_L/finger_03_L");
        if(UnityEngine.Mathf.Abs(clip.length-baseline.length)>.00001f || clip.events.Length!=0) throw new System.Exception("Timing/event change");
        float maxRight=0;
        for(int i=27;i<=138;i++)
        {
            float t=i/120f;
            baseline.SampleAnimation(root,t); var p=right.position; var q=right.rotation;
            clip.SampleAnimation(root,t);
            maxRight=UnityEngine.Mathf.Max(maxRight,UnityEngine.Vector3.Distance(p,right.position));
            if(UnityEngine.Quaternion.Angle(q,right.rotation)>.05f) throw new System.Exception("Right power-stroke rotation changed");
        }
        if(maxRight>.00002f) throw new System.Exception("Right power stroke displaced: "+maxRight);
        prep.SampleAnimation(root,prep.length); var holdLeft=left.position; var holdRight=right.position;
        clip.SampleAnimation(root,0);
        float join=UnityEngine.Vector3.Distance(holdLeft,left.position)+UnityEngine.Vector3.Distance(holdRight,right.position);
        if(join>.0001f) throw new System.Exception("Prepared hold discontinuity "+join);
        var model=(UnityEngine.GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(folder+"/"+kind+".prefab"),root.scene);
        UnityEditor.PrefabUtility.UnpackPrefabInstance(model,UnityEditor.PrefabUnpackMode.Completely,UnityEditor.InteractionMode.AutomatedAction);
        model.transform.SetParent(right,false); Game.EditorTools.PinCooperationMotionDraft.AlignModel(model);
        var pin=model.GetComponent<Game.Presentation.Animation.FPThrowablePinView>(); pin.Bind(left);
        var graph=root.GetComponent<Animancer.AnimancerComponent>();
        graph.Animator.cullingMode=UnityEngine.AnimatorCullingMode.AlwaysAnimate;
        graph.Graph.UpdateMode=UnityEngine.Playables.DirectorUpdateMode.Manual;
        var state=graph.Play(prep); state.Time=quick?0:prep.length; state.Speed=0; graph.Evaluate(0);
        pin.Present(false,0,quick?0:prep.length);
        if(pin.Detached) throw new System.Exception("Holding input extracted ring");
        state=graph.Play(clip,.08f,Animancer.FadeMode.FromStart); state.Time=0; state.Speed=1;
        float maxContact=0,maxStep=0;
        var last=left.position;
        for(int i=1;i<=138;i++)
        {
            float t=i/120f; graph.Evaluate(1f/120f);
            pin.Present(true,t,0);
            float step=UnityEngine.Vector3.Distance(last,left.position); last=left.position;
            if(t>=.10f && t<=.18f) maxStep=UnityEngine.Mathf.Max(maxStep,step);
            if(t>=.0834f && t<.12f || t>=.13f && t<=.34f)
                maxContact=UnityEngine.Mathf.Max(maxContact,UnityEngine.Vector3.Distance(pin.Grip.position,index.TransformPoint(Game.Presentation.Animation.FPThrowablePinView.FingerContact)));
            if(t<.12f && pin.Detached) throw new System.Exception("Early extraction");
            if(t>=.13f && pin.Ring.parent!=index) throw new System.Exception("Ring lost finger parent");
            if(t>=.35f) {model.SetActive(false); if(!pin.Ring.gameObject.activeInHierarchy) throw new System.Exception("Ring hidden with body");}
        }
        if(maxContact>.002f) throw new System.Exception("Pin contact error "+maxContact);
        if(maxStep>.025f) throw new System.Exception("Left wrist jump at separation "+maxStep);
        var ring=pin.Ring; pin.Clear();
        if(ring!=null) throw new System.Exception("Detached ring leaked");
        results.Add(new{kind=kind,quick=quick,maxRightError=maxRight,holdJoinError=join,maxPinContactError=maxContact,maxExtractionFrameStep=maxStep,passed=true});
    }
    finally {UnityEditor.PrefabUtility.UnloadPrefabContents(root);}
}
System.IO.Directory.CreateDirectory("Logs/PinMotion0929");
System.IO.File.WriteAllText("Logs/PinMotion0929/verification.json",Newtonsoft.Json.JsonConvert.SerializeObject(results));
return results;
