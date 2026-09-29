using System.Linq;
using Animancer;
using Game.Gameplay.Combat;
using Game.Presentation.Animation;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Game.Gameplay.Tests
{
    public sealed class ThrowablePinPresentationTests
    {
        [TestCase(ThrowableType.Frag)]
        [TestCase(ThrowableType.Flash)]
        [TestCase(ThrowableType.Smoke)]
        public void RingContactsTransfersAndSurvivesBodyRelease(ThrowableType type)
        {
            var d = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == "weapon.m4").definition;
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(d.FirstPersonViewPrefab));
            try
            {
                var fp = root.GetComponent<FPWeaponAnimator>();
                var body = (GameObject)PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("ThrowableViews/" + type), root.scene);
                PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                body.transform.SetParent(root.transform.Find(FPWeaponAnimator.ThrowableHandPath), false);
                FPWeaponAnimator.AlignHeldThrowable(body);
                var presentation = body.GetComponent<FPThrowablePresentation>();
                var pin = body.GetComponent<FPThrowablePinView>();
                var left = root.transform.Find("Armature/arm_L/lower_arm_L/hand_L");
                var finger = left.Find("finger_01_L/finger_02_L/finger_03_L");
                var thumb = left.Find("thumb_01_L/thumb_02_L/thumb_03_L");
                pin.Bind(left);
                var graph = root.GetComponent<AnimancerComponent>();
                graph.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.Graph.UpdateMode = DirectorUpdateMode.Manual;
                var state = graph.Play(presentation.Prepare); state.Time = state.Length; state.Speed = 0;
                graph.Evaluate(0);
                pin.Present(false, 0, presentation.Prepare.length);
                Assert.That(Vector3.Distance(pin.Grip.position, finger.TransformPoint(FPThrowablePinView.FingerContact)), Is.LessThan(.002f));
                Assert.That(Vector3.Distance(thumb.TransformPoint(new Vector3(0,.016f,0)), finger.TransformPoint(FPThrowablePinView.FingerContact)), Is.LessThan(.008f), "thumb and index oppose around the ring");
                Assert.That(pin.Detached, Is.False, "holding input never extracts the pin or starts the throw");
                state = graph.Play(presentation.Throw, .08f, FadeMode.FromStart); state.Time = 0; state.Speed = 1;
                for (int i = 1; i <= 78; i++)
                {
                    graph.Evaluate(1f/120f);
                    float t = i / 120f;
                    var rightBefore = body.transform.parent.position;
                    pin.Present(true, t, 0);
                    Assert.That(Vector3.Distance(rightBefore, body.transform.parent.position), Is.LessThan(.000001f), "pin contact must not override the right throwing arm");
                    if (t < .10f) Assert.That(pin.Detached, Is.False);
                    if (t >= .13f)
                    {
                        Assert.That(pin.Ring.parent, Is.EqualTo(finger));
                        Assert.That(Vector3.Distance(pin.Grip.position, finger.TransformPoint(FPThrowablePinView.FingerContact)), Is.LessThan(.002f), "extracted ring remains at the gripping fingers");
                    }
                    if (t >= .35f)
                    {
                        body.SetActive(false);
                        Assert.That(pin.Ring.gameObject.activeInHierarchy, Is.True, "body release must not hide the carried ring");
                    }
                }
                var ring = pin.Ring;
                pin.Clear();
                Assert.That(ring == null, Is.True, "interrupt/death/switch cleanup must destroy the detached part");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [TestCase(ThrowableType.Frag)]
        [TestCase(ThrowableType.Flash)]
        [TestCase(ThrowableType.Smoke)]
        public void FirstPersonSplitConservesSourceGeometry(ThrowableType type)
        {
            var source = Resources.Load<ThrowableCatalog>("ThrowableCatalog").Get(type).ModelPrefab;
            var view = Resources.Load<GameObject>("ThrowableViews/" + type);
            Assert.That(view.GetComponent<FPThrowablePinView>(), Is.Not.Null);
            Assert.That(view.GetComponentsInChildren<MeshFilter>().Sum(m => m.sharedMesh.triangles.Length),
                Is.EqualTo(source.GetComponent<MeshFilter>().sharedMesh.triangles.Length));
            Assert.That(source.GetComponentsInChildren<FPThrowablePinView>().Length, Is.Zero, "world/vendor model remains unchanged");
            Assert.That(view.GetComponentsInChildren<Collider>().Length, Is.Zero);
        }
    }
}
