from pathlib import Path

src=Path('Tools/Diagnostics/Render-PinPullQuick.body.cs').read_text(encoding='utf-8-sig')
src=src.replace('var phase = "quick";', 'var phase = "hold";\nvar quick = false;\nvar kind = "Frag";')
src=src.replace('"Logs/PinPull0929/" + phase', '"Logs/PinMotion0929/" + kind + "/" + phase')
src=src.replace('(UnityEngine.AnimationClip)pose.FindProperty("throwablePresentationClip").objectReferenceValue',
    'UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(Game.EditorTools.PinCooperationMotionDraft.Folder + "/" + kind + "-Throw.anim")')
src=src.replace('(UnityEngine.AnimationClip)pose.FindProperty("throwablePrepareClip").objectReferenceValue',
    'UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(Game.EditorTools.PinCooperationMotionDraft.Folder + "/" + kind + "-Prepare.anim")')
src=src.replace('UnityEngine.Resources.Load<UnityEngine.GameObject>("ThrowableViews/Flash")',
    'UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(Game.EditorTools.PinCooperationMotionDraft.Folder + "/" + kind + ".prefab")')
a=src.index('    grenade.transform.localRotation =')
b=src.index('    var pin =',a)
src=src[:a]+'    Game.EditorTools.PinCooperationMotionDraft.AlignModel(grenade);\n'+src[b:]
src=src.replace('        graph.Evaluate(frame==60?0:1f/60f);\n        pin.Present(t >= 1f, t - 1f, 0);',
'''        float preparation = quick ? 0 : UnityEngine.Mathf.Clamp01((t-.15f)/.36f)*prepareClip.length;
        if (t<1f) state.Time=preparation;
        graph.Evaluate(frame<=60?0:1f/60f);
        pin.Present(t >= 1f, t - 1f, preparation);''')
# A wide oblique view keeps the throwing arm's power stroke visible too.
src=src.replace('new UnityEngine.Vector3(1.15f,.06f,.52f)', 'new UnityEngine.Vector3(-1.05f,.16f,.22f)')
src=src.replace('new UnityEngine.Vector3(.20f,-.10f,.40f)', 'new UnityEngine.Vector3(0,-.12f,.43f)')
src=src.replace('var points = bones.Select', 'var points = (side ? new[]{v.transform.Find("Armature/arm_L"),v.transform.Find("Armature/arm_L/lower_arm_L"),v.transform.Find("Armature/arm_L/lower_arm_L/hand_L")} : bones).Select')
Path('Tools/Diagnostics/Render-PinMotion.body.cs').write_text(src,encoding='utf-8')
print('Draft renderer ready')
