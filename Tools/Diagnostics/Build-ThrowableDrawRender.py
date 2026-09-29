from pathlib import Path

src=Path('Tools/Diagnostics/Render-PinMotion.body.cs').read_text(encoding='utf-8-sig')
start=src.index('var phase =')
end=src.index('System.IO.Directory.CreateDirectory')
src='var quick = false;\nvar side = false;\nvar dir = "Logs/ThrowableDraw0929/" + (quick ? "interrupt" : "cycle");\n'+src[end:]
src=src.replace('    var throwClip = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(Game.EditorTools.PinCooperationMotionDraft.Folder + "/" + kind + "-Throw.anim");\n','')
start=src.index('    var grenade =')
end=src.index('    var display =',start)
src=src[:start]+'''    var owner = new UnityEngine.GameObject("PreviewThrowableOwner");
    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, v.scene); owner.SetActive(false);
    var throwable = owner.AddComponent<Game.Gameplay.Combat.ThrowableController>();
    var hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    typeof(Game.Gameplay.Combat.ThrowableController).GetField("_catalog",hidden).SetValue(throwable,UnityEngine.Resources.Load<Game.Gameplay.Combat.ThrowableCatalog>("ThrowableCatalog"));
    var fp = v.GetComponent<Game.Presentation.Animation.FPWeaponAnimator>();
    var graph = v.GetComponent<Animancer.AnimancerComponent>();
    graph.Animator.cullingMode = UnityEngine.AnimatorCullingMode.AlwaysAnimate;
    graph.Graph.UpdateMode = UnityEngine.Playables.DirectorUpdateMode.Manual;
    typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_animancer",hidden).SetValue(fp,graph);
    typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_throwables",hidden).SetValue(fp,throwable);
    typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_clipsReady",hidden).SetValue(fp,true);
    var selected=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("HandleThrowableSelection",hidden);
    var tick=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("TickThrowablePresentation",hidden);
    var late=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("LateUpdate",hidden);
    var throwStart=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetMethod("HandleThrowStarted",hidden);
    var startedAt=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_throwStartedAt",hidden);
    var heldField=typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_heldThrowable",hidden);
    System.Action<int> select = index => {
        typeof(Game.Gameplay.Combat.ThrowableController).GetField("<IsEquipped>k__BackingField",hidden).SetValue(throwable,true);
        typeof(Game.Gameplay.Combat.ThrowableController).GetField("<SelectedType>k__BackingField",hidden).SetValue(throwable,(Game.Gameplay.Combat.ThrowableType)index);
        selected.Invoke(fp,null);
    };
    var initial=graph.Play(d.FirstPersonAnimations.Idle); initial.Time=0; initial.Speed=0; graph.Evaluate(0);
''' + src[end:]
start=src.index('    var prepareClip =')
end=src.index('        var points =',start)
src=src[:start]+'''    int throwFrame=quick?35:252;
    for (int frame = 0; frame < (quick?112:322); frame++)
    {
        float t=frame/60f;
        if(quick) {
            if(frame==0) select(0); if(frame==7) select(1); if(frame==13) select(2); if(frame==20) select(0); if(frame==32) select(1);
        } else {
            if(frame==0) select(0); if(frame==60) select(1); if(frame==120) select(2); if(frame==180) select(0);
        }
        if(frame==throwFrame) throwStart.Invoke(fp,new object[]{throwable.SelectedType});
        bool holding=!quick && frame>=228 && frame<throwFrame;
        tick.Invoke(fp,new object[]{1f/60f,holding});
        graph.Evaluate(frame==0 || frame==throwFrame ? 0 : 1f/60f);
        if(frame>=throwFrame) {
            float elapsed=(frame-throwFrame)/60f;
            startedAt.SetValue(fp,UnityEngine.Time.time-elapsed);
            var body=(UnityEngine.GameObject)heldField.GetValue(fp);
            if(body!=null && elapsed>=throwable.ReleaseDelaySeconds) body.SetActive(false);
        }
        late.Invoke(fp,null);
''' + src[end:]
Path('Tools/Diagnostics/Render-ThrowableDraw.body.cs').write_text(src,encoding='utf-8')
print('Runtime selection renderer ready')
