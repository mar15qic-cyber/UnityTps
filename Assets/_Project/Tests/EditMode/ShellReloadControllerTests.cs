using System.Reflection;
using System.Linq;
using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class ShellReloadControllerTests
    {
        private GameObject root;
        private WeaponController weapon;
        private ActionSystem actions;
        private DemoBalanceConfig balance;
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        [SetUp] public void Setup()
        {
            root=new GameObject("ShellReload_Controller");root.transform.position=Vector3.one*4600;
            actions=root.AddComponent<ActionSystem>();root.AddComponent<CombatResolver>();weapon=root.AddComponent<WeaponController>();
            balance=ScriptableObject.CreateInstance<DemoBalanceConfig>();
            var so=new SerializedObject(balance);var entries=so.FindProperty("weapons");entries.arraySize=1;
            var entry=entries.GetArrayElementAtIndex(0);entry.FindPropertyRelative("WeaponId").stringValue="shotgun.01";
            var stat=entry.FindPropertyRelative("Stat");stat.FindPropertyRelative("MagSize").intValue=6;stat.FindPropertyRelative("ReserveAmmo").intValue=36;
            stat.FindPropertyRelative("ReloadTime").floatValue=2.8f;stat.FindPropertyRelative("Rpm").intValue=60;stat.FindPropertyRelative("MaxRange").floatValue=100;so.ApplyModifiedPropertiesWithoutUndo();
            var definition=Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog").Entries.First(e=>e.definition!=null&&e.definition.WeaponId=="shotgun.01").definition;
            typeof(WeaponController).GetMethod("OnDisable",Hidden).Invoke(weapon,null);
            typeof(WeaponController).GetField("actionSystem",Hidden).SetValue(weapon,actions);
            typeof(WeaponController).GetField("combatResolver",Hidden).SetValue(weapon,root.GetComponent<CombatResolver>());
            typeof(WeaponController).GetMethod("OnEnable",Hidden).Invoke(weapon,null);
            weapon.Initialize(definition,balance);
            typeof(WeaponController).GetField("aimPivot",Hidden).SetValue(weapon,root.transform);
        }
        [TearDown] public void Cleanup(){Object.DestroyImmediate(root);Object.DestroyImmediate(balance);}
        private void Ammo(int current,int reserve)=>typeof(WeaponRuntime).GetMethod("RestoreAmmo",Hidden).Invoke(weapon.Runtime,new object[]{current,reserve});
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void NativeClockMovesEveryShellExactlyOnce(int missing)
        {
            Ammo(6-missing,36);Assert.True(weapon.TryReload());
            for(int i=1;i<=missing;i++)
            {
                float target=ShellReloadState.OpenSeconds+i*ShellReloadState.InsertSeconds;
                actions.Tick(target-actions.Elapsed-.002f);Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(6-missing+i-1));
                actions.Tick(.002f);Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(6-missing+i));Assert.That(weapon.Runtime.ReserveAmmo,Is.EqualTo(36-i));
            }
            Assert.That(weapon.ReloadPhase,Is.EqualTo(ShellReloadPhase.Close));actions.Tick(ShellReloadState.CloseSeconds+.001f);
            Assert.False(actions.IsBusy);Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(6));Assert.That(weapon.Runtime.State,Is.EqualTo(WeaponRuntimeState.Ready));
        }
        [TestCase(ActionInterruptReason.SwitchWeapon)] [TestCase(ActionInterruptReason.Death)]
        public void CancelKeepsCommittedShellAndDiscardsPartialInsert(ActionInterruptReason reason)
        {
            Ammo(0,36);weapon.TryReload();actions.Tick(1.8f);Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(1));
            actions.Interrupt(reason);actions.Tick(10);Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(1));Assert.That(weapon.Runtime.ReserveAmmo,Is.EqualTo(35));Assert.That(weapon.ReloadPhase,Is.EqualTo(ShellReloadPhase.None));
        }
        [Test] public void EmptyFireInterruptWaitsFirstShellThenFiresOnce()
        {
            Ammo(0,36);int shots=0;weapon.OnShotFired+=_=>shots++;weapon.TryReload();actions.Tick(.2f);Assert.True(weapon.RequestShellReloadFinish(true,false));
            actions.Tick(1.466668f);Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(1));actions.Tick(.868f);
            Assert.That(shots,Is.EqualTo(1));Assert.That(weapon.Runtime.CurrentAmmo,Is.Zero);Assert.That(weapon.Runtime.ReserveAmmo,Is.EqualTo(35));
        }
        [Test] public void LimitedReserveAndLateOrderedSnapshotsRestoreInterruptedPhase()
        {
            Ammo(0,2);weapon.TryReload();actions.Tick(4);Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(2));Assert.That(weapon.Runtime.ReserveAmmo,Is.Zero);
            weapon.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot{WeaponId="shotgun.01",LifeEpoch=1,Sequence=10,CurrentAmmo=0,ReserveAmmo=2,ReloadState=WeaponRuntimeState.Reloading,ReloadPhase=ShellReloadPhase.Insert,ReloadPhaseElapsed=.4f,ReloadRemaining=1.2f,ReloadGeneration=5,ReloadFinishRequested=true});
            actions.Tick(.333334f);Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(1));Assert.That(weapon.ReloadPhase,Is.EqualTo(ShellReloadPhase.Close));
            weapon.ApplyAuthoritativeAmmoSnapshot(new AuthoritativeAmmoSnapshot{WeaponId="shotgun.01",LifeEpoch=1,Sequence=9,CurrentAmmo=6,ReserveAmmo=36,ReloadState=WeaponRuntimeState.Ready});
            Assert.That(weapon.Runtime.CurrentAmmo,Is.EqualTo(1));actions.Tick(.867f);Assert.That(weapon.Runtime.ReserveAmmo,Is.EqualTo(1));
        }
    }
}
