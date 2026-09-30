using System;
using System.Linq;
using System.Reflection;
using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Gameplay.Tests
{
    public class RealTest0930Tests
    {
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void ShellsCommitOnlyAtInsertEndAndDurationMatchesNativeClips(int shells)
        {
            var state=new ShellReloadState();state.Begin(shells,36);
            Assert.That(state.Duration,Is.EqualTo(.9333333f+shells*.7333333f+.8666667f).Within(.0001));
            Assert.That(state.Advance(.933f,false),Is.Zero);
            for(int i=1;i<=shells;i++)
            {
                float end=ShellReloadState.OpenSeconds+i*ShellReloadState.InsertSeconds;
                Assert.That(state.Advance(end-.001f,i>1),Is.Zero);
                Assert.That(state.Advance(end,i>1),Is.EqualTo(1));
            }
            Assert.That(state.Phase,Is.EqualTo(ShellReloadPhase.Close));
            Assert.That(state.Remaining(state.Duration-ShellReloadState.CloseSeconds),Is.EqualTo(ShellReloadState.CloseSeconds).Within(.0001));
        }
        [Test]
        public void FinishWithAmmoDiscardsPartialShellAndEmptyFinishWaitsForFirstShell()
        {
            var loaded=new ShellReloadState();loaded.Begin(6,36);loaded.Advance(1.2f,true);loaded.RequestFinish(1.2f,true);
            Assert.That(loaded.Phase,Is.EqualTo(ShellReloadPhase.Close));Assert.That(loaded.Advance(1.7f,true),Is.Zero);
            var empty=new ShellReloadState();empty.Begin(6,36);empty.RequestFinish(.2f,false);
            Assert.That(empty.Advance(1.6f,false),Is.Zero);
            Assert.That(empty.Advance(1.666667f,false),Is.EqualTo(1));Assert.That(empty.Phase,Is.EqualTo(ShellReloadPhase.Close));
        }
        [Test]
        public void LimitedReserveAndRestoredPhaseDoNotManufactureShells()
        {
            var state=new ShellReloadState();state.Begin(6,2);
            Assert.That(state.Advance(9,false),Is.EqualTo(2));
            var restored=new ShellReloadState();restored.Restore(ShellReloadPhase.Insert,.4f,10,2);
            Assert.That(restored.Advance(.3f,true),Is.Zero);
            Assert.That(restored.Advance(.333334f,true),Is.EqualTo(1));
            restored.Cancel();Assert.That(restored.Advance(99,true),Is.Zero);
        }
        [Test]
        public void DuplicateModelsAndEmptySlotsCarryModelSpecificStats()
        {
            var catalog=Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            var slots=ThrowableSlots.Create(new[]{"throwable.frag_02","throwable.frag_02",null},catalog);
            Assert.That(slots.Count(0),Is.EqualTo(1));Assert.That(slots.Count(1),Is.EqualTo(1));Assert.That(slots.Count(2),Is.Zero);
            Assert.That(catalog.Frag.FragMaxDamage,Is.EqualTo(90));Assert.That(catalog.Frag2.FragMaxDamage,Is.EqualTo(100));Assert.That(catalog.Frag3.FragMaxDamage,Is.EqualTo(80));
            foreach(var id in new[]{"throwable.frag","throwable.frag_02","throwable.frag_03","throwable.flash","throwable.smoke"})Assert.That(Resources.Load<Sprite>("UI/WeaponIcons/"+id),Is.Not.Null);
        }
        [TestCase("Arena")] [TestCase("Map_Stackyard")] [TestCase("Map_Depot55")] [TestCase("Map_Ridgeline")] [TestCase("Map_TrainingYard")] [TestCase("Map_NightRelay")]
        public void ComplexMapModelsHaveExactCollisionAndPositiveMovementBounds(string name)
        {
            var scene=EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/"+name+".unity");
            try
            {
                foreach(var box in scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<BoxCollider>(true)))
                {
                    Assert.That(box.size.x,Is.GreaterThanOrEqualTo(0),box.name);Assert.That(box.size.y,Is.GreaterThanOrEqualTo(0),box.name);Assert.That(box.size.z,Is.GreaterThanOrEqualTo(0),box.name);
                    var mesh=box.GetComponent<MeshFilter>();
                    if(mesh!=null&&mesh.sharedMesh!=null&&AssetDatabase.GetAssetPath(mesh.sharedMesh).StartsWith("Assets/")) Assert.Fail("Aggregate model box remains: "+box.name);
                }
            }
            finally{EditorSceneManager.ClosePreviewScene(scene);}
        }
    }
}
