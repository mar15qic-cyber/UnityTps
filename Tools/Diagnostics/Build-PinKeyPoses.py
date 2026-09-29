from pathlib import Path

# Review-only static posing. Does not bake or save any production asset.
src = Path('Tools/Diagnostics/Render-PinPull.body.cs').read_text(encoding='utf-8-sig')
src = src.replace('var phase = "after";', 'var phase = "draft";')
src = src.replace('Logs/PinPull0929/', 'Logs/PinKeyPoses0929/')
src = src.replace('"ThrowableViews/Flash"', '"ThrowableViews/Frag"')
src = src.replace('    var pin = grenade.GetComponent', '    // Review grip indexing: orient the native ring/pin toward the supporting hand.\n    grenade.transform.localRotation *= UnityEngine.Quaternion.Euler(0,180,0);\n    var pin = grenade.GetComponent')
start = src.index('    for (int frame = 0; frame < 144; frame++)')
end = src.index('        var points = bones.Select', start)
src = src[:start] + '''    var ru = bones[0]; var re = bones[1]; var rw = bones[2];
    var lu = v.transform.Find("Armature/arm_L");
    var le = lu.Find("lower_arm_L"); var lw = le.Find("hand_L");
    var index = lw.Find("finger_01_L/finger_02_L/finger_03_L");
    var builder = typeof(Game.EditorTools.FirstPersonThrowClipBuilder);
    var gripMethod = builder.GetMethod("SetSupportGrip", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    var solve = builder.GetMethod("SolveArm", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    var ringParent = pin.Ring.parent;
    var ringPosition = pin.Ring.localPosition; var ringRotation = pin.Ring.localRotation;
    var evidence = new System.Collections.Generic.List<object>();
    for (int frame = 0; frame < 3; frame++)
    {
        float t = frame;
        pin.Ring.SetParent(ringParent, false);
        pin.Ring.SetLocalPositionAndRotation(ringPosition, ringRotation);
        // The three frames are pose decisions, not a new timing curve.
        throwClip.SampleAnimation(v, .10f);
        var rightWrist = v.transform.TransformPoint(new UnityEngine.Vector3(.145f, -.005f, .325f));
        ru.position = v.transform.TransformPoint(new UnityEngine.Vector3(.19f,-.12f,.008f));
        solve.Invoke(null, new object[] { ru, re, rw, rightWrist, v.transform.TransformPoint(new UnityEngine.Vector3(.38f,-.23f,.13f)) });
        rw.rotation = v.transform.rotation * UnityEngine.Quaternion.Euler(0,-12,frame == 0 ? 22 : 30)
            * UnityEngine.Quaternion.Euler(90,-20,-10);
        gripMethod.Invoke(null, new object[] { lw, frame == 0 ? .70f : 1f });
        // Relax the other three fingers as a group; keep the thumb-index pinch.
        lw.Find("middle_finger_01_L").localRotation = UnityEngine.Quaternion.Euler(0,0,90);
        lw.Find("middle_finger_01_L/middle_finger_02_L").localRotation = UnityEngine.Quaternion.Euler(0,180,270);
        lw.Find("middle_finger_01_L/middle_finger_02_L/middle_finger_03_L").localRotation = UnityEngine.Quaternion.Euler(0,0,270);
        lu.position = v.transform.TransformPoint(new UnityEngine.Vector3(-.18f,-.115f,.035f));
        // Aim the whole forearm/hand line diagonally inward, with the dorsum up.
        var palmNormal = (frame == 0 ? new UnityEngine.Vector3(-.16f,.78f,-.60f) : new UnityEngine.Vector3(-.1f,.82f,-.56f)).normalized;
        var fingersForward = new UnityEngine.Vector3(.85f,.10f,.51f).normalized;
        var palm = v.transform.rotation * UnityEngine.Quaternion.LookRotation(palmNormal,fingersForward);
        lw.rotation = palm;
        var targetContact = pin.Grip.position + (frame == 0 ? v.transform.TransformVector(new UnityEngine.Vector3(-.022f,-.003f,-.008f)) : UnityEngine.Vector3.zero);
        var targetWrist = lw.position + targetContact - index.TransformPoint(Game.Presentation.Animation.FPThrowablePinView.FingerContact);
        solve.Invoke(null, new object[] { lu, le, lw, targetWrist, v.transform.TransformPoint(new UnityEngine.Vector3(-.34f,-.20f,.16f)) });
        lw.rotation = palm;
        if (frame == 2)
        {
            // Preserve contact-relative ring transform at extraction, then move
            // the arm along the pull vector without an independent palm flip.
            pin.Ring.SetParent(index, true);
            var pull = grenade.transform.right * .06f;
            lu.position += v.transform.TransformVector(new UnityEngine.Vector3(-.004f,-.003f,0));
            solve.Invoke(null, new object[] { lu, le, lw, targetWrist + pull, v.transform.TransformPoint(new UnityEngine.Vector3(-.36f,-.22f,.15f)) });
            lw.rotation = v.transform.rotation * UnityEngine.Quaternion.AngleAxis(-5, UnityEngine.Vector3.forward) * UnityEngine.Quaternion.Inverse(v.transform.rotation) * palm;
        }
        evidence.Add(new { stage = frame, contactError = UnityEngine.Vector3.Distance(pin.Grip.position,index.TransformPoint(Game.Presentation.Animation.FPThrowablePinView.FingerContact)),
            wristBend = UnityEngine.Vector3.Angle(lw.up,lw.position-le.position),
            shoulder=lu.position.ToString("F5"), elbow=le.position.ToString("F5"), wrist=lw.position.ToString("F5"), palmNormal=lw.forward.ToString("F5"),
            pinOutAxis=grenade.transform.right.ToString("F5"),
            fingers=lw.GetComponentsInChildren<UnityEngine.Transform>().Where(b => b.name.Contains("_L")).Select(b => new {name=b.name, p=b.position.ToString("F5")}).ToArray(),
            ringParent=pin.Ring.parent.name,
            bones=v.GetComponentsInChildren<UnityEngine.Transform>(true).Where(b => UnityEditor.AnimationUtility.CalculateTransformPath(b,v.transform).StartsWith("Armature/arm_")).Select(b => new {path=UnityEditor.AnimationUtility.CalculateTransformPath(b,v.transform), position=new[]{b.localPosition.x,b.localPosition.y,b.localPosition.z}, rotation=new[]{b.localRotation.x,b.localRotation.y,b.localRotation.z,b.localRotation.w}}).ToArray() });
''' + src[end:]
src = src.replace('    System.IO.File.WriteAllText(dir + "/joints.json", Newtonsoft.Json.JsonConvert.SerializeObject(samples));', '    System.IO.File.WriteAllText(dir + "/evidence.json", Newtonsoft.Json.JsonConvert.SerializeObject(evidence, new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore }));')
src = src.replace('var points = bones.Select', 'var points = (side ? new[] { lu, le, lw } : bones).Select')
src = src.replace('new UnityEngine.Vector3(1.15f,.06f,.52f)', 'new UnityEngine.Vector3(-.85f,.13f,.16f)').replace('new UnityEngine.Vector3(.20f,-.10f,.40f)', 'new UnityEngine.Vector3(0,-.17f,.43f)').replace('cam.orthographicSize = .37f', 'cam.orthographicSize = .27f')
Path('Tools/Diagnostics/Render-PinKeyPoses.body.cs').write_text(src,encoding='utf-8')
print('Created review-only renderer')
