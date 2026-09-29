using System.Linq;
using System.Reflection;
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
    public sealed class ThrowableDrawPresentationTests
    {
        private const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
        private GameObject _root,_owner;
        private FPWeaponAnimator _fp;
        private ThrowableController _throwables;
        private AnimancerComponent _graph;
        private AnimancerState State => (AnimancerState)typeof(FPWeaponAnimator).GetField("_throwState",Hidden).GetValue(_fp);
        private void Call(string name,params object[] args) => typeof(FPWeaponAnimator).GetMethod(name,Hidden).Invoke(_fp,args);

        [SetUp]
        public void Setup()
        {
            var weapon=Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e=>e.itemId=="weapon.m4").definition;
            _root=PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(weapon.FirstPersonViewPrefab));
            _owner=new GameObject("DrawTestOwner"); _owner.SetActive(false);
            _throwables=_owner.AddComponent<ThrowableController>();
            typeof(ThrowableController).GetField("_catalog",Hidden).SetValue(_throwables,Resources.Load<ThrowableCatalog>("ThrowableCatalog"));
            _fp=_root.GetComponent<FPWeaponAnimator>(); _graph=_root.GetComponent<AnimancerComponent>();
            _graph.Animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            _graph.Graph.UpdateMode=DirectorUpdateMode.Manual;
            typeof(FPWeaponAnimator).GetField("_animancer",Hidden).SetValue(_fp,_graph);
            typeof(FPWeaponAnimator).GetField("_throwables",Hidden).SetValue(_fp,_throwables);
            typeof(FPWeaponAnimator).GetField("_clipsReady",Hidden).SetValue(_fp,true);
        }

        [TearDown]
        public void Cleanup()
        {
            if(_fp!=null) Call("ClearThrowablePresentation");
            if(_root!=null) PrefabUtility.UnloadPrefabContents(_root);
            if(_owner!=null) Object.DestroyImmediate(_owner);
        }

        private void Select(ThrowableType kind)
        {
            typeof(ThrowableController).GetField("<IsEquipped>k__BackingField",Hidden).SetValue(_throwables,true);
            typeof(ThrowableController).GetField("<SelectedType>k__BackingField",Hidden).SetValue(_throwables,kind);
            Call("HandleThrowableSelection");
        }

        private void Step(float duration,bool holding=false)
        {
            for(int i=0;i<Mathf.CeilToInt(duration*120);i++)
            {
                _graph.Evaluate(1f/120f); _fp.TickThrowablePresentation(1f/120f,holding); Call("LateUpdate");
            }
        }

        [Test]
        public void AllTypesShareOneDrawAndJoinTheirReadyPose()
        {
            AnimationClip shared=null;
            foreach(var kind in new[]{ThrowableType.Frag,ThrowableType.Flash,ThrowableType.Smoke})
            {
                var config=Resources.Load<GameObject>("ThrowableViews/"+kind).GetComponent<FPThrowablePresentation>();
                Assert.That(config,Is.Not.Null);
                if(shared==null) shared=config.Draw;
                Assert.That(config.Draw,Is.SameAs(shared));
                Assert.That(config.Draw.length,Is.EqualTo(.42f).Within(.001f));
                Assert.That(config.Draw.events,Is.Empty);
                var wrist=_root.transform.Find(FPWeaponAnimator.ThrowableHandPath);
                config.Draw.SampleAnimation(_root,0); var start=wrist.position;
                config.Draw.SampleAnimation(_root,config.Draw.length); var end=wrist.position; var rotation=wrist.rotation;
                Assert.That(end.y-start.y,Is.GreaterThan(.25f));
                config.Prepare.SampleAnimation(_root,0);
                Assert.That(Vector3.Distance(end,wrist.position),Is.LessThan(.0001f));
                Assert.That(Quaternion.Angle(rotation,wrist.rotation),Is.LessThan(.05f));
                Assert.That(config.Throw.length,Is.EqualTo(_throwables.ThrowActionSeconds).Within(.0001f));
            }
            Assert.That(_throwables.ReleaseDelaySeconds,Is.EqualTo(.35f));
        }

        [Test]
        public void SelectionAndEachTypeSwitchReplayDrawAndRetireOutgoingModel()
        {
            AnimationClip shared=null; GameObject previous=null;
            foreach(var kind in new[]{ThrowableType.Frag,ThrowableType.Flash,ThrowableType.Smoke,ThrowableType.Frag})
            {
                Select(kind);
                var current=_fp.HeldThrowableTransform.gameObject;
                var config=current.GetComponent<FPThrowablePresentation>();
                if(shared==null) shared=config.Draw;
                Assert.That(_fp.IsThrowableDrawPlaying,Is.True);
                Assert.That(State.Clip,Is.SameAs(shared)); Assert.That(State.Time,Is.EqualTo(0));
                Assert.That(current.activeSelf,Is.False,"incoming model waits until the hand is below view");
                if(previous!=null) Assert.That(previous.activeSelf,Is.True,"outgoing model descends with the hand");
                Step(.10f);
                Assert.That(current.activeSelf,Is.True);
                Assert.That(previous==null,Is.True,"outgoing model is removed at reveal");
                Step(.40f);
                Assert.That(_fp.IsThrowableDrawPlaying,Is.False);
                Assert.That(State.Clip,Is.SameAs(config.Prepare)); Assert.That(State.Speed,Is.Zero);
                Assert.That(_fp.ThrowableClip,Is.SameAs(config.Throw),"accepted per-model throw is selected at runtime");
                Assert.That(current.GetComponent<FPThrowablePinView>().Detached,Is.False);
                previous=current;
            }
        }

        [Test]
        public void RapidCyclingAndCleanupDoNotLeaveModelsOrDrawState()
        {
            for(int i=0;i<12;i++)
            {
                Select((ThrowableType)(i%3)); Step(.02f);
                Assert.That(_root.GetComponentsInChildren<FPThrowablePresentation>(true).Length,Is.LessThanOrEqualTo(2));
            }
            Step(.10f);
            Assert.That(_root.GetComponentsInChildren<FPThrowablePresentation>(true).Length,Is.EqualTo(1));
            Call("ClearThrowablePresentation");
            Assert.That(_root.GetComponentsInChildren<FPThrowablePresentation>(true),Is.Empty);
            Assert.That(_fp.IsThrowableDrawPlaying,Is.False);
            Assert.That(_fp.IsThrowablePresentationActive,Is.False);
        }

        [Test]
        public void HoldingAndImmediateThrowInterruptDrawWithoutWaiting()
        {
            Select(ThrowableType.Flash); Step(.02f,true);
            Assert.That(_fp.IsThrowableDrawPlaying,Is.False);
            Assert.That(State.Clip,Is.SameAs(_fp.ThrowablePrepareClip));
            Select(ThrowableType.Smoke); Step(.02f);
            Call("HandleThrowStarted",ThrowableType.Smoke);
            Assert.That(_fp.IsThrowableDrawPlaying,Is.False);
            Assert.That(_fp.IsThrowPresentationPlaying,Is.True);
            Assert.That(State.Clip,Is.SameAs(_fp.ThrowableClip));
            Assert.That(State.Time,Is.Zero); Assert.That(State.Speed,Is.EqualTo(1));
            Assert.That(_fp.HeldThrowableTransform.gameObject.activeSelf,Is.True);
            Assert.That(_root.GetComponentsInChildren<FPThrowablePresentation>(true).Length,Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DrawParksLeftArmFromFirstFrameIncludingPriorSupportPose(bool fromPinContact)
        {
            var weapon=Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e=>e.itemId=="weapon.m4").definition;
            var config=Resources.Load<GameObject>("ThrowableViews/Smoke").GetComponent<FPThrowablePresentation>();
            var left=_root.transform.Find("Armature/arm_L").GetComponentsInChildren<Transform>(true);
            config.Draw.SampleAnimation(_root,0);
            var positions=left.Select(t=>t.localPosition).ToArray();
            var rotations=left.Select(t=>t.localRotation).ToArray();
            if(fromPinContact) {Select(ThrowableType.Frag); Step(.5f,true);}
            else {var idle=_graph.Play(weapon.FirstPersonAnimations.Idle); idle.Time=0; idle.Speed=0; _graph.Evaluate(0);}
            Select(ThrowableType.Smoke);
            // Inspect time zero too: the original 60 ms full-body fade leaked
            // the rifle support hand before the incoming model was visible.
            for(int frame=0;frame<50;frame++)
            {
                _graph.Evaluate(frame==0?0:1f/120f); _fp.TickThrowablePresentation(frame==0?0:1f/120f,false); Call("LateUpdate");
                for(int i=0;i<left.Length;i++)
                {
                    Assert.That(Vector3.Distance(left[i].localPosition,positions[i]),Is.LessThan(.0001f),left[i].name+" draw frame "+frame);
                    Assert.That(Quaternion.Angle(left[i].localRotation,rotations[i]),Is.LessThan(.1f),left[i].name+" draw frame "+frame);
                }
            }
            Step(.4f,true);
            var hand=_root.transform.Find("Armature/arm_L/lower_arm_L/hand_L");
            config.Prepare.SampleAnimation(_root,config.Prepare.length); var target=hand.position;
            _graph.Evaluate(0); Call("LateUpdate");
            Assert.That(Vector3.Distance(hand.position,target),Is.LessThan(.002f),"draw-only left pose must release for pin contact");
        }
    }
}
