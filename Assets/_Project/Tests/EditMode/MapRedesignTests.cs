using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class MapRedesignTests
    {
        [TestCase("Map_Stackyard", 42, 32)]
        [TestCase("Map_TrainingYard", 40, 30)]
        [TestCase("Map_Ridgeline", 46, 34)]
        [TestCase("Map_Depot55", 68, 46)]
        [TestCase("Map_NightRelay", 72, 50)]
        public void MapHasItsOwnArenaAndMirroredSpawnRegions(string name, int width, int depth)
        {
            var scene = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/" + name + ".unity");
            try
            {
                var roots = scene.GetRootGameObjects();
                var stage = roots.Single(r => r.name == "--- Environment ---").transform.Find("DesignedCombatSpace");
                Assert.NotNull(stage);
                Assert.That(stage.Find("Ground").localScale.x, Is.EqualTo(width));
                Assert.That(stage.Find("Ground").localScale.z, Is.EqualTo(depth));
                var spawns = roots.Single(r => r.name == "SpawnPoints").GetComponentsInChildren<Transform>()
                    .Where(t => t.name.StartsWith("Spawn_")).OrderBy(t => t.name).ToArray();
                Assert.That(spawns.Length, Is.EqualTo(8));
                for (int i = 0; i < 4; i++)
                {
                    Assert.That(spawns[i].position.x, Is.EqualTo(-spawns[i + 4].position.x).Within(.001f));
                    Assert.That(spawns[i].position.z, Is.EqualTo(spawns[i + 4].position.z).Within(.001f));
                    Assert.That(spawns[i].position.y, Is.EqualTo(.2f).Within(.001f));
                }
                Assert.That(roots.Single(r => r.name == "NetworkSystems"), Is.Not.Null);
                foreach (var collider in stage.GetComponentsInChildren<BoxCollider>())
                    Assert.That(collider.size.x > 0 && collider.size.y > 0 && collider.size.z > 0,
                        Is.True, name + "/" + collider.name + " has an invalid collision box");
                var blockers = stage.GetComponentsInChildren<Collider>().Where(c => c.name != "Ground").ToArray();
                foreach (var spawn in spawns)
                    Assert.That(blockers.Any(c => c.bounds.SqrDistance(spawn.position + Vector3.up * .9f) < .25f), Is.False,
                        name + "/" + spawn.name + " has less than 0.5 m clearance from cover");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void NightRelayHasOnlyFiveLocalLights()
        {
            var scene = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/Map_NightRelay.unity");
            try
            {
                var stage = scene.GetRootGameObjects().Single(r => r.name == "--- Environment ---")
                    .transform.Find("DesignedCombatSpace");
                Assert.That(stage.GetComponentsInChildren<Light>().Count(l => l.type == LightType.Point), Is.EqualTo(5));
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void ArenaHasContinuousFourSidedSafetyBarrier()
        {
            var scene = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/Arena.unity");
            try
            {
                var fence = scene.GetRootGameObjects().Single(r => r.name == "--- Environment ---")
                    .transform.Find("SafetyFence");
                Assert.NotNull(fence);
                foreach (var name in new[] { "Boundary_N", "Boundary_S", "Boundary_E", "Boundary_W" })
                    Assert.NotNull(fence.Find(name).GetComponent<BoxCollider>(), name);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
