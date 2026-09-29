using System.Collections;
using System.Linq;
using System.Reflection;
using Animancer;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Game.Gameplay.Tests
{
    public sealed class ThrowableWeaponReturnTests
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        private GameObject _root, _owner;
        private FPWeaponAnimator _fp;
        private AnimancerComponent _graph;
        private ThrowableController _throwable;
        private ActionSystem _actions;
        private WeaponController _weapon;
        private WeaponDefinition _definition;

        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden).SetValue(target, value);
        private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden).Invoke(target, args);
        private void SelectionChanged() => Call(_fp, "HandleThrowableSelection");

        public static IEnumerable ReturnCases()
        {
            foreach (string id in new[] { "weapon.m4", "weapon.service_pistol" })
            foreach (var type in new[] { ThrowableType.Frag, ThrowableType.Flash, ThrowableType.Smoke })
            foreach (bool automatic in new[] { false, true }) yield return new TestCaseData(id, type, automatic);
        }

        private void Setup(string id)
        {
            _definition = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == id).definition;
            _root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(_definition.FirstPersonViewPrefab));
            _owner = new GameObject("ThrowableReturnTestOwner"); _owner.SetActive(false);
            _actions = _owner.AddComponent<ActionSystem>();
            _weapon = _owner.AddComponent<WeaponController>(); Set(_weapon, "definition", _definition);
            _throwable = _owner.AddComponent<ThrowableController>();
            Set(_throwable, "_catalog", Resources.Load<ThrowableCatalog>("ThrowableCatalog"));
            Set(_throwable, "_actions", _actions); Call(_throwable, "OnEnable");
            _fp = _root.GetComponent<FPWeaponAnimator>(); _graph = _root.GetComponent<AnimancerComponent>();
            _graph.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _graph.Graph.UpdateMode = DirectorUpdateMode.Manual;
            Set(_fp, "_animancer", _graph); Set(_fp, "controller", _weapon);
            Set(_fp, "actionSystem", _actions); Set(_fp, "_throwables", _throwable);
            _throwable.OnSelectionChanged += SelectionChanged;
            Call(_fp, "LoadClips");
            var idle = _graph.Play(_definition.FirstPersonAnimations.Idle); idle.Speed = 1;
            _graph.Evaluate(0);
        }

        [TearDown]
        public void Cleanup()
        {
            if (_throwable != null) { _throwable.OnSelectionChanged -= SelectionChanged; Call(_throwable, "OnDisable"); }
            if (_fp != null) Call(_fp, "ClearThrowablePresentation");
            if (_root != null) PrefabUtility.UnloadPrefabContents(_root);
            if (_owner != null) Object.DestroyImmediate(_owner);
            _root = _owner = null; _fp = null; _throwable = null;
        }

        private void Select(ThrowableType type)
        {
            Set(_throwable, "<IsEquipped>k__BackingField", true);
            Set(_throwable, "<SelectedType>k__BackingField", type); SelectionChanged();
            for (int i = 0; i < 60; i++) { _graph.Evaluate(1f/120f); _fp.TickThrowablePresentation(1f/120f, false); }
        }

        [TestCaseSource(nameof(ReturnCases))]
        public void ManualAndAutomaticReturnPlayTheNativeWeaponDraw(string id, ThrowableType type, bool automatic)
        {
            Setup(id); Select(type);
            if (automatic)
            {
                Assert.That(_actions.TryStart(PlayerActionType.GrenadeThrow, _throwable.ThrowActionSeconds), Is.True);
                Call(_fp, "HandleThrowStarted", type);
                _graph.Evaluate(.9f); _actions.Tick(.9f);
                Assert.That(_throwable.IsEquipped, Is.True, "visual recovery must not finish the gameplay action early");
                Assert.That(_actions.CurrentAction, Is.EqualTo(PlayerActionType.GrenadeThrow));
                _actions.Tick(.26f);
            }
            else _throwable.Unequip();
            Call(_fp, "Update"); _graph.Evaluate(0);
            var draw = (AnimancerState)typeof(FPWeaponAnimator).GetField("_drawState", Hidden).GetValue(_fp);
            Assert.That(draw, Is.Not.Null, "same-slot and automatic return have no WeaponEquipped event");
            Assert.That(draw.Clip, Is.SameAs(_definition.FirstPersonAnimations.Draw));
            Assert.That(draw.Time, Is.EqualTo(0).Within(.001));
            Assert.That(draw.Speed, Is.GreaterThan(0));
            Assert.That(_fp.IsWeaponTransitionAnimationActive, Is.True);
            Assert.That(_fp.IsThrowablePresentationActive, Is.False);
            Assert.That(_root.GetComponentsInChildren<FPThrowablePresentation>(true), Is.Empty);
            Assert.That(_actions.IsBusy, Is.False, "return draw must not add a gameplay lock");
            _graph.Evaluate(.1f); Call(_fp, "Update");
            Assert.That(draw.Time, Is.GreaterThan(0), "draw must not restart each Update");
            Assert.That(_graph.States.Current.Clip, Is.SameAs(_definition.FirstPersonAnimations.Draw));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ARealSlotSwitchOwnsTheTransitionRegardlessOfEventOrder(bool switchFirst)
        {
            Setup("weapon.m4"); Select(ThrowableType.Frag);
            if (!switchFirst) _throwable.Unequip();
            Assert.That(_actions.TryStart(PlayerActionType.SwitchWeapon, 2f), Is.True);
            _fp.PlayHolster();
            if (switchFirst) _throwable.Unequip();
            Call(_fp, "Update"); _graph.Evaluate(.1f);
            Assert.That(_graph.States.Current.Clip, Is.SameAs(_definition.FirstPersonAnimations.Holster));
            var pistol = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e => e.itemId == "weapon.service_pistol").definition;
            Set(_weapon, "definition", pistol); _fp.PlayDraw(); Call(_fp, "Update");
            Assert.That(_graph.States.Current.Clip, Is.SameAs(pistol.FirstPersonAnimations.Draw));
            Assert.That(_actions.CurrentAction, Is.EqualTo(PlayerActionType.SwitchWeapon));
        }

        [Test]
        public void AllWeaponDefinitionsUseTheirOwnDrawOnReturn()
        {
            var entries = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.Where(e => e.definition != null).ToArray();
            Assert.That(entries.Length, Is.GreaterThanOrEqualTo(16));
            foreach (var entry in entries)
            {
                Setup(entry.itemId); Select(ThrowableType.Smoke);
                _throwable.Unequip(); Call(_fp, "Update");
                Assert.That(_graph.States.Current.Clip, Is.SameAs(entry.definition.FirstPersonAnimations.Draw), entry.itemId);
                Cleanup();
            }
        }

        [TestCase("Frag")]
        [TestCase("Flash")]
        [TestCase("Smoke")]
        public void EmptyHandWithdrawsWithoutChangingPowerStrokeOrLeftArm(string kind)
        {
            Setup("weapon.m4");
            var before = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/_Project/Review/PinCooperation/" + kind + "-Throw.anim");
            var after = Resources.Load<GameObject>("ThrowableViews/" + kind).GetComponent<FPThrowablePresentation>().Throw;
            Assert.That(after.length, Is.EqualTo(_throwable.ThrowActionSeconds).Within(.0001f));
            Assert.That(_throwable.ReleaseDelaySeconds, Is.EqualTo(.35f));
            var bones = _root.transform.Find("Armature").GetComponentsInChildren<Transform>();
            for (int f = 0; f <= 60; f++)
            {
                float t = f / 120f; before.SampleAnimation(_root, t);
                var p = bones.Select(b => b.localPosition).ToArray(); var q = bones.Select(b => b.localRotation).ToArray();
                after.SampleAnimation(_root, t);
                for (int i = 0; i < bones.Length; i++)
                {
                    Assert.That(Vector3.Distance(bones[i].localPosition, p[i]), Is.LessThan(.00001f));
                    Assert.That(Quaternion.Angle(bones[i].localRotation, q[i]), Is.LessThan(.1f));
                }
            }
            var left = AnimationUtility.GetCurveBindings(before).Where(b => b.path.StartsWith("Armature/arm_L"));
            foreach (var b in left) Assert.That(AnimationUtility.GetEditorCurve(after, b).keys, Is.EqualTo(AnimationUtility.GetEditorCurve(before, b).keys));
            var wrist = _root.transform.Find(FPWeaponAnimator.ThrowableHandPath);
            for (float t = .9f; t <= 1.151f; t += .025f)
            {
                after.SampleAnimation(_root, t);
                Assert.That(_root.transform.InverseTransformPoint(wrist.position).y, Is.LessThan(-.35f), "empty hand must stay below view");
                Assert.That(Quaternion.Angle(wrist.Find("finger_01_R").localRotation, Quaternion.identity), Is.LessThan(25), "must not return to grenade-ready fist");
            }
        }
    }
}
