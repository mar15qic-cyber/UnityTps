using System.Linq;
using System.Reflection;
using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Gameplay.Tests
{
    public sealed class PreviewVisualRegressionTests
    {
        private static readonly string[] NativeWeapons = {
            "weapon.m4", "weapon.ak", "weapon.rifle03", "weapon.service_pistol",
            "weapon.handgun02", "weapon.handgun03", "weapon.handgun04", "weapon.smg01",
            "weapon.smg02", "weapon.smg03", "weapon.smg04", "weapon.smg05",
            "weapon.shotgun01", "weapon.sniper01", "weapon.sniper02", "weapon.sniper03" };

        [TestCaseSource(nameof(NativeWeapons))]
        public void NativeOutletTracksCircularBoreThroughIdleAimFireAndAimFire(string id)
        {
            var d = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == id).definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
            var mesh = new Mesh();
            try
            {
                var view = root.GetComponent<WeaponView>();
                var renderer = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r =>
                    System.Text.RegularExpressions.Regex.IsMatch(r.name, "^(assault_rifle|smg|handgun|sniper|sniper_rifle|shotgun)_\\d+$"));
                d.FirstPersonAnimations.Idle.SampleAnimation(root, 0); renderer.BakeMesh(mesh);
                var reference = mesh.vertices.Select(p => view.Muzzle.InverseTransformPoint(renderer.transform.TransformPoint(p))).ToArray();
                float referenceFront = reference.Max(p => p.z);
                float depth = id == "weapon.m4" ? .0257f : id == "weapon.smg04" ? .02745f : 0f;
                // Freeze actual vertex identities, then follow their skinning in each animation.
                var ringIndices = Enumerable.Range(0, reference.Length)
                    .Where(i => Mathf.Abs(reference[i].z - (referenceFront - depth)) < .0003f).ToArray();
                var frontIndices = Enumerable.Range(0, reference.Length).Where(i => reference[i].z > referenceFront - .0001f).ToArray();
                Assert.That(ringIndices.Length, Is.GreaterThan(12));
                var weights = renderer.sharedMesh.boneWeights;
                foreach (int index in ringIndices)
                {
                    Assert.That(weights[index].weight0, Is.GreaterThan(.999f), id);
                    Assert.That(view.Muzzle.parent, Is.SameAs(renderer.bones[weights[index].boneIndex0]),
                        id + " exit must follow the visible barrel, never the lid/slider");
                }
                foreach (var clip in new[] { d.FirstPersonAnimations.Idle, d.FirstPersonAnimations.AimIdle,
                             d.FirstPersonAnimations.Fire, d.FirstPersonAnimations.AimFire })
                foreach (float t in new[] { 0f, .1f, .25f, .5f, .75f, .95f })
                {
                    Assert.That(clip, Is.Not.Null, id);
                    clip.SampleAnimation(root, clip.length * t); renderer.BakeMesh(mesh);
                    var points = mesh.vertices.Select(p => view.Muzzle.InverseTransformPoint(renderer.transform.TransformPoint(p))).ToArray();
                    float front = frontIndices.Max(i => points[i].z);
                    var ring = ringIndices.Select(i => points[i]).ToArray();
                    var expected = new Vector3((ring.Min(p => p.x) + ring.Max(p => p.x)) * .5f,
                        (ring.Min(p => p.y) + ring.Max(p => p.y)) * .5f, front);
                    var actual = view.Muzzle.InverseTransformPoint(view.ResolvePresentationMuzzle().position);
                    Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.0005f), clip.name + " @" + t);
                    // The corrected attachment thread must stay on the same bore axis.
                    var socket = root.GetComponentsInChildren<AttachmentSocket>(true).FirstOrDefault(s => s.Slot == AttachmentSlotType.Muzzle);
                    if (socket != null)
                        Assert.That(Vector3.Distance(socket.transform.position, view.ResolvePresentationMuzzle().position), Is.LessThan(.0005f), id);
                }
            }
            finally { Object.DestroyImmediate(mesh); PrefabUtility.UnloadPrefabContents(root); }
        }

        [Test]
        public void RealSuppressorsShareOneExitForTracerAndFlashAcrossNativeWeapons()
        {
            var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            foreach (var row in Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.Where(e => e.IsLpfp && e.definition != null && e.slotCapabilities.Contains("Muzzle")))
            foreach (var device in catalog.Entries.Where(e => e.isSuppressor && e.HasModel && AttachmentCompatibilityPolicy.IsAllowed(row.itemId, e)))
            {
                var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(row.definition.FirstPersonViewPrefab));
                try
                {
                    var view = root.GetComponent<WeaponView>();
                    var attachments = root.GetComponent<WeaponAttachmentView>() ?? root.AddComponent<WeaponAttachmentView>();
                    attachments.ApplyAttachments(catalog, row.itemId, new[] { device }, false);
                    var spawned = attachments.FindSpawned(device.itemId);
                    Assert.That(spawned, Is.Not.Null, row.itemId + "/" + device.itemId);
                    var exit = spawned.Find("MuzzleExit");
                    Assert.That(exit, Is.Not.Null);
                    var filter = spawned.GetComponentInChildren<MeshFilter>();
                    float meshFront = filter.sharedMesh.vertices.Min(p => spawned.InverseTransformPoint(filter.transform.TransformPoint(p)).z);
                    Assert.That(exit.localPosition.z, Is.EqualTo(meshFront).Within(.0001f));
                    foreach (var clip in new[] { row.definition.FirstPersonAnimations.Idle, row.definition.FirstPersonAnimations.AimIdle,
                                 row.definition.FirstPersonAnimations.Fire, row.definition.FirstPersonAnimations.AimFire })
                    foreach (float t in new[] { 0f, .1f, .25f, .5f, .75f, .95f })
                    {
                        clip.SampleAnimation(root, clip.length * t);
                        Assert.That(view.ResolvePresentationMuzzle(), Is.SameAs(exit), row.itemId);
                        Assert.That(Vector3.Dot(exit.position - view.Muzzle.position, view.Muzzle.forward), Is.GreaterThan(.02f), row.itemId);
                    }
                    typeof(WeaponView).GetMethod("SpawnMuzzleFlash", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);
                    Assert.That(exit.childCount, Is.GreaterThan(0), "flash must be attached to the same outlet");
                    var flash = exit.GetChild(exit.childCount - 1);
                    Assert.That(flash.localPosition.sqrMagnitude, Is.LessThan(1e-8f));
                    attachments.Clear();
                    Assert.That(view.ResolvePresentationMuzzle().IsChildOf(view.Muzzle), Is.True, "removing the suppressor restores the bare exit");
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        [TestCaseSource(nameof(NativeWeapons))]
        public void ActualTracerStartsAtNativeExitAcrossDifferentCameraFovs(string id)
        {
            var d = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == id).definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
            var host = new GameObject("NativeTracerCameras");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var view = root.GetComponent<WeaponView>();
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, root.scene);
                root.transform.position = new Vector3(0f, 10f, 0f);
                host.transform.position = root.transform.position;
                var world = host.AddComponent<Camera>(); world.fieldOfView = 60; world.aspect = 16f / 9f;
                var fpNode = new GameObject("NativeFPCamera"); fpNode.transform.SetParent(host.transform, false);
                fpNode.transform.localPosition = new Vector3(-.047f, .082f, -.299f);
                var fp = fpNode.AddComponent<Camera>(); fp.fieldOfView = 45; fp.aspect = world.aspect;
                int fpBit = 1 << LayerMask.NameToLayer("FirstPersonView");
                world.cullingMask = ~fpBit; fp.cullingMask = fpBit;
                typeof(WeaponView).GetField("_worldCamera", flags).SetValue(view, world);
                typeof(WeaponView).GetField("_fpCamera", flags).SetValue(view, fp);
                typeof(WeaponView).GetMethod("BuildEffects", flags).Invoke(view, null);
                // Reproduce the real rig lifecycle: Awake creates FX, then the rig
                // recursively assigns the view layer. Projection alone missed this bug.
                foreach (var node in root.GetComponentsInChildren<Transform>(true))
                    node.gameObject.layer = LayerMask.NameToLayer("FirstPersonView");
                Vector3 hit = host.transform.position + Vector3.forward * 20f;
                typeof(WeaponView).GetMethod("SpawnTracer", flags).Invoke(view,
                    new object[] { view.ResolvePresentationMuzzle().position, hit, 1u });
                foreach (var clip in new[] { d.FirstPersonAnimations.Idle, d.FirstPersonAnimations.AimIdle,
                             d.FirstPersonAnimations.Fire, d.FirstPersonAnimations.AimFire })
                foreach (float t in new[] { 0f, .1f, .25f, .5f, .75f, .95f })
                {
                    clip.SampleAnimation(root, clip.length * t);
                    typeof(WeaponView).GetMethod("LateUpdate", flags).Invoke(view, null);
                    var lines = root.GetComponentsInChildren<LineRenderer>().Where(l => l.enabled).ToArray();
                    Assert.That(lines.Length, Is.EqualTo(1), id + " " + clip.name);
                    Assert.That(world.cullingMask & (1 << lines[0].gameObject.layer), Is.Not.Zero, "world camera must render the tracer");
                    Assert.That(fp.cullingMask & (1 << lines[0].gameObject.layer), Is.Zero, "FP camera must not reproject world tracer vertices");
                    var a = world.WorldToViewportPoint(lines[0].GetPosition(0));
                    var b = fp.WorldToViewportPoint(view.ResolvePresentationMuzzle().position);
                    var pixelError = Vector2.Scale(new Vector2(a.x - b.x, a.y - b.y), new Vector2(1920, 1080)).magnitude;
                    Assert.That(pixelError, Is.LessThan(.25f), id + " " + clip.name + " @" + t);
                    Assert.That(Vector3.Distance(lines[0].GetPosition(1), hit), Is.LessThan(.0001f));
                }
            }
            finally
            {
                var material = typeof(WeaponView).GetField("_tracerMaterial", flags).GetValue(view) as Material;
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(host); PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void NativeThrowableReadyPoseUsesAuthoredCameraAndVisiblePalm()
        {
            var catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            foreach (var row in Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.Where(e => e.IsLpfp && e.definition != null))
            {
                var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(row.definition.FirstPersonViewPrefab));
                var cameraObject = new GameObject("ThrowReferenceCamera");
                try
                {
                    var animator = root.GetComponent<FPWeaponAnimator>();
                    root.transform.SetPositionAndRotation(animator.ThrowViewLocalPosition, animator.ThrowViewLocalRotation);
                    var clip = animator.ThrowableClip;
                    Assert.That(clip, Is.Not.Null);
                    Assert.That(catalog.ThrowActionSeconds, Is.GreaterThanOrEqualTo(clip.length), row.itemId + " recovery must not be truncated");
                    clip.SampleAnimation(root, clip.length);
                    var held = (GameObject)PrefabUtility.InstantiatePrefab(catalog.Frag.ModelPrefab, root.scene);
                    held.transform.SetParent(root.transform.Find(FPWeaponAnimator.ThrowableHandPath), false);
                    held.transform.localRotation = FPWeaponAnimator.HeldThrowableRotation;
                    held.transform.localPosition = FPWeaponAnimator.HeldThrowablePosition(held);
                    var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.fieldOfView = 45; camera.aspect = 16f / 9f;
                    var vp = camera.WorldToViewportPoint(held.GetComponentInChildren<Renderer>().bounds.center);
                    Assert.That(vp.z, Is.GreaterThan(.1f), row.itemId);
                    Assert.That(vp.x, Is.InRange(.05f, .95f), row.itemId);
                    Assert.That(vp.y, Is.InRange(.02f, .8f), row.itemId);
                }
                finally { Object.DestroyImmediate(cameraObject); PrefabUtility.UnloadPrefabContents(root); }
            }
        }

        [TestCaseSource(nameof(NativeWeapons))]
        public void ThrowableUsesRightHandOverhandArcWithVisibleGrip(string id)
        {
            var definition = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == id).definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab));
            var cameraNode = new GameObject("OverhandProjection");
            try
            {
                var animator = root.GetComponent<FPWeaponAnimator>();
                root.transform.SetPositionAndRotation(animator.ThrowViewLocalPosition, animator.ThrowViewLocalRotation);
                var hand = root.transform.Find(FPWeaponAnimator.ThrowableHandPath);
                Assert.That(hand.name, Is.EqualTo("hand_R"), "grenade must use the right throwing hand");
                var clip = animator.ThrowableClip;
                clip.SampleAnimation(root, .24f);
                float cockedDepth = hand.position.z;
                clip.SampleAnimation(root, .35f);
                Assert.That(hand.position.z - cockedDepth, Is.GreaterThan(.15f), "release extends forward from shoulder");
                Assert.That(Vector3.Dot(hand.rotation * Vector3.left, Vector3.down), Is.GreaterThan(.9f),
                    "release pronates the palm downward; a palm-up backhand/scoop is not accepted");
                var camera = cameraNode.AddComponent<Camera>(); camera.enabled = false; camera.fieldOfView = 45; camera.aspect = 16f / 9f;
                var catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
                foreach (var throwable in new[] { catalog.Frag, catalog.Flash, catalog.Smoke })
                {
                    var model = (GameObject)PrefabUtility.InstantiatePrefab(throwable.ModelPrefab, root.scene);
                    model.transform.SetParent(hand, false);
                    model.transform.localRotation = FPWeaponAnimator.HeldThrowableRotation;
                    model.transform.localPosition = FPWeaponAnimator.HeldThrowablePosition(model);
                    try
                    {
                        foreach (float time in new[] { 0f, .05f, .12f, .20f, .24f, .30f, .345f })
                        {
                            clip.SampleAnimation(root, time);
                            var center = model.GetComponentInChildren<Renderer>().bounds.center;
                            var viewport = camera.WorldToViewportPoint(center);
                            Assert.That(viewport.z, Is.GreaterThan(.2f), id + "/" + throwable.name + " @" + time);
                            Assert.That(viewport.x, Is.InRange(.02f, .98f), "grip must remain in the view");
                            Assert.That(viewport.y, Is.InRange(.02f, .98f), "grip must remain in the view");
                        }
                    }
                    finally { Object.DestroyImmediate(model); }
                }
            }
            finally { Object.DestroyImmediate(cameraNode); PrefabUtility.UnloadPrefabContents(root); }
        }

        [TestCaseSource(nameof(NativeWeapons))]
        public void ThrowableUpperArmLeadsForearmAndCarriesThroughRelease(string id)
        {
            var definition = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == id).definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab));
            try
            {
                var clip = root.GetComponent<FPWeaponAnimator>().ThrowableClip;
                var shoulder = root.transform.Find("Armature/arm_R");
                var elbow = shoulder.Find("lower_arm_R");
                var wrist = elbow.Find("hand_R");
                clip.SampleAnimation(root, .235f);
                float earlyElbow = elbow.position.z, earlyWrist = wrist.position.z;
                var cockedShoulder = shoulder.position;
                float upperLength = Vector3.Distance(shoulder.position, elbow.position);
                float forearmLength = Vector3.Distance(elbow.position, wrist.position);
                clip.SampleAnimation(root, .275f);
                Assert.That(elbow.position.z - earlyElbow, Is.GreaterThan(.03f), "elbow leads forward while the forearm remains cocked");
                Assert.That(wrist.position.z - earlyWrist, Is.LessThan(.025f), "wrist must not lead the elbow's drive");

                float upperPeak = 0, forearmPeak = 0, upperTime = 0, forearmTime = 0;
                Vector3 previousUpper = Vector3.zero, previousForearm = Vector3.zero;
                for (int i = 0; i <= 120; i++)
                {
                    float t = .20f + i / 600f;
                    clip.SampleAnimation(root, t);
                    var u = (elbow.position - shoulder.position).normalized;
                    var f = (wrist.position - elbow.position).normalized;
                    if (i > 0)
                    {
                        float a = Vector3.Angle(previousUpper, u), b = Vector3.Angle(previousForearm, f);
                        if (a > upperPeak) { upperPeak = a; upperTime = t; }
                        if (b > forearmPeak) { forearmPeak = b; forearmTime = t; }
                    }
                    Assert.That(Vector3.Distance(shoulder.position, elbow.position), Is.EqualTo(upperLength).Within(.0002f));
                    Assert.That(Vector3.Distance(elbow.position, wrist.position), Is.EqualTo(forearmLength).Within(.0002f));
                    previousUpper = u; previousForearm = f;
                }
                Assert.That(forearmTime - upperTime, Is.InRange(.015f, .10f), "upper-arm acceleration must precede the overlapping forearm acceleration");
                clip.SampleAnimation(root, .34f); var beforeRelease = wrist.position;
                clip.SampleAnimation(root, .35f); var release = wrist.position;
                Assert.That(Vector3.Distance(cockedShoulder, shoulder.position), Is.GreaterThan(.03f), "shoulder must participate");
                clip.SampleAnimation(root, .36f); var afterRelease = wrist.position;
                Assert.That(Vector3.Dot((release-beforeRelease).normalized, (afterRelease-release).normalized), Is.GreaterThan(.9f), "no reversal at release");
                Assert.That((afterRelease-beforeRelease).magnitude / .02f, Is.GreaterThan(1f), "release must not be a stopped pose");
                clip.SampleAnimation(root, .39f);
                Assert.That(wrist.position.z, Is.GreaterThan(release.z + .015f), "follow-through continues forward after letting go");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [TestCaseSource(nameof(NativeWeapons))]
        public void ThrowablePreparationUsesThumbAxisAndVisibleSupportHand(string id)
        {
            var definition = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == id).definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab));
            try
            {
                var animator = root.GetComponent<FPWeaponAnimator>();
                var prepare = animator.ThrowablePrepareClip;
                Assert.That(prepare, Is.Not.Null, id + " preparation must be wired on every view");
                var right = root.transform.Find(FPWeaponAnimator.ThrowableHandPath);
                var left = root.transform.Find("Armature/arm_L/lower_arm_L/hand_L");
                prepare.SampleAnimation(root, 0f);
                var leftRest = left.position;
                var rightRest = right.position;
                var rightRotation = right.rotation;
                prepare.SampleAnimation(root, prepare.length);
                var leftContact = left.position;
                Assert.That(leftContact.y - leftRest.y, Is.GreaterThan(.2f), "support hand must enter the view during preparation");
                Assert.That(Vector3.Distance(leftContact, right.position), Is.LessThan(.13f), "hands meet around the held object");
                Assert.That(Vector3.Distance(rightRest, right.position), Is.LessThan(.001f), "held grenade remains stable during preparation");
                Assert.That(Quaternion.Angle(rightRotation, right.rotation), Is.LessThan(.1f));

                var catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
                foreach (var definitionOfThrow in new[] { catalog.Frag, catalog.Flash, catalog.Smoke })
                {
                    var held = (GameObject)PrefabUtility.InstantiatePrefab(definitionOfThrow.ModelPrefab, root.scene);
                    try
                    {
                        held.transform.SetParent(right, false);
                        held.transform.localRotation = FPWeaponAnimator.HeldThrowableRotation;
                        held.transform.localPosition = FPWeaponAnimator.HeldThrowablePosition(held);
                        Assert.That(Vector3.Dot(held.transform.up, right.TransformDirection(Vector3.back)), Is.GreaterThan(.99f),
                            "the canister axis must pass toward the thumb, across rather than along the fingers");
                        Assert.That(Vector3.Dot(held.transform.up, Vector3.up), Is.GreaterThan(.9f), "ready grip stays upright");
                        var bounds = held.GetComponentInChildren<Renderer>().bounds;
                        var thumb = right.Find("thumb_01_R/thumb_02_R/thumb_03_R");
                        Assert.That(Vector3.Distance(bounds.ClosestPoint(thumb.position), thumb.position), Is.LessThan(.025f),
                            "thumb must oppose the held body, not float beside it");
                    }
                    finally { Object.DestroyImmediate(held); }
                }

                animator.ThrowableClip.SampleAnimation(root, 0f);
                Assert.That(Vector3.Distance(leftContact, left.position), Is.LessThan(.001f), "prepared hold joins the throw without a support-hand jump");
                animator.ThrowableClip.SampleAnimation(root, .24f);
                Assert.That(left.position.x, Is.LessThan(leftContact.x - .2f), "support hand separates before release");
                animator.ThrowableClip.SampleAnimation(root, animator.ThrowableClip.length);
                Assert.That(Vector3.Distance(leftRest, left.position), Is.LessThan(.001f), "support hand recovers to its ready position");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
