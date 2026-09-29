var catalog = UnityEngine.Resources.Load<Game.UI.WeaponAssetCatalog>("WeaponAssetCatalog");
var rows = new System.Collections.Generic.List<object>();
foreach (var id in new[] { "weapon.m4", "weapon.sniper03", "weapon.lpw.rifle.01" })
{
    var definition = catalog.Entries.First(e => e.itemId == id).definition;
    var view = UnityEditor.PrefabUtility.LoadPrefabContents(UnityEditor.AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab));
    try
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var fp = view.GetComponent<Game.Presentation.Animation.FPWeaponAnimator>();
        var so = new UnityEditor.SerializedObject(fp);
        var clip = (UnityEngine.AnimationClip)so.FindProperty("throwablePresentationClip").objectReferenceValue;
        var prepare = (UnityEngine.AnimationClip)so.FindProperty("throwablePrepareClip").objectReferenceValue;
        var bones = new[] { view.transform.Find("Armature/arm_R"), view.transform.Find("Armature/arm_R/lower_arm_R"), view.transform.Find("Armature/arm_R/lower_arm_R/hand_R") };
        var graph = view.GetComponent<Animancer.AnimancerComponent>();
        graph.Animator.applyRootMotion = false;
        graph.Animator.cullingMode = UnityEngine.AnimatorCullingMode.AlwaysAnimate;
        graph.Graph.UpdateMode = UnityEngine.Playables.DirectorUpdateMode.Manual;
        typeof(Game.Presentation.Animation.FPWeaponAnimator).GetField("_throwPlaying", flags).SetValue(fp, true);
        var leftIK = view.GetComponent<Game.Presentation.Animation.FPLeftHandIK>();
        var rightIK = view.GetComponent<Game.Presentation.Animation.FPRightHandIK>();
        if (leftIK != null) typeof(Game.Presentation.Animation.FPLeftHandIK).GetField("_weaponAnimator", flags).SetValue(leftIK, fp);
        var state = graph.Play(prepare); state.Time = prepare.length; state.Speed = 0;
        graph.Evaluate(0);
        state = graph.Play(clip, .08f, Animancer.FadeMode.FromStart); state.Time = 0; state.Speed = 1;
        float graphError = 0, ikError = 0, rotationError = 0;
        var graphSamples = new System.Collections.Generic.List<object>();
        for (int i = 0; i < 70; i++)
        {
            graph.Evaluate(1f / 60f);
            var actual = bones.Select(b => b.position).ToArray();
            var rotations = bones.Select(b => b.rotation).ToArray();
            if (leftIK != null) typeof(Game.Presentation.Animation.FPLeftHandIK).GetMethod("LateUpdate", flags).Invoke(leftIK, null);
            if (rightIK != null) typeof(Game.Presentation.Animation.FPRightHandIK).GetMethod("LateUpdate", flags).Invoke(rightIK, null);
            for (int j = 0; j < bones.Length; j++) ikError = UnityEngine.Mathf.Max(ikError, UnityEngine.Vector3.Distance(actual[j], bones[j].position));
            float time = (float)state.Time;
            clip.SampleAnimation(view, UnityEngine.Mathf.Min(time, clip.length));
            if (time >= .12f)
                for (int j = 0; j < bones.Length; j++)
                {
                    graphError = UnityEngine.Mathf.Max(graphError, UnityEngine.Vector3.Distance(actual[j], bones[j].position));
                    rotationError = UnityEngine.Mathf.Max(rotationError, UnityEngine.Quaternion.Angle(rotations[j], bones[j].rotation));
                }
            graphSamples.Add(new { time = time, layerWeight = graph.Layers[0].Weight, clipWeight = state.Weight });
        }
        var constraints = view.GetComponentsInChildren<UnityEngine.Component>(true)
            .Where(c => c != null && (c.GetType().Name.Contains("Constraint") || c.GetType().Name == "RigBuilder"))
            .Select(c => c.GetType().FullName).ToArray();
        rows.Add(new { id = id, graphPositionError = graphError, graphRotationErrorDegrees = rotationError,
            ikPositionError = ikError, leftIK = leftIK != null, rightIK = rightIK != null,
            constraints = constraints, samples = graphSamples });
    }
    finally { UnityEditor.PrefabUtility.UnloadPrefabContents(view); }
}
System.IO.File.WriteAllText("Logs/ThrowKinetics0928/runtime-ownership.json", Newtonsoft.Json.JsonConvert.SerializeObject(rows, Newtonsoft.Json.Formatting.Indented));
return rows;
