using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class SpawnGroundingTests
    {
        [Test]
        public void ProductionControllerAndShoeSoleRestOnGround()
        {
            const string path = "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";
            GameObject player = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var controller = player.GetComponent<CharacterController>();
                Assert.That(SpawnGrounding.RootYForGround(0f, controller), Is.EqualTo(0f).Within(.002f));
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                try
                {
                    floor.transform.position = new Vector3(10000f, -.1f, 10000f);
                    floor.transform.localScale = new Vector3(10f, .2f, 10f);
                    Physics.SyncTransforms();
                    var grounded = SpawnGrounding.Align(new Vector3(10000f, .15f, 10000f), controller);
                    Assert.That(grounded.y, Is.EqualTo(0f).Within(.002f));
                }
                finally { Object.DestroyImmediate(floor); }
                var skinned = player.transform.Find("TP_Model").GetComponentInChildren<SkinnedMeshRenderer>();
                var baked = new Mesh();
                try
                {
                    skinned.BakeMesh(baked, true);
                    float lowest = float.PositiveInfinity;
                    foreach (var vertex in baked.vertices)
                        lowest = Mathf.Min(lowest, player.transform.InverseTransformPoint(
                            skinned.transform.TransformPoint(vertex)).y);
                    Assert.That(lowest, Is.EqualTo(0f).Within(.01f),
                        "authored shoes must meet the controller's grounded root");
                }
                finally { Object.DestroyImmediate(baked); }
            }
            finally { PrefabUtility.UnloadPrefabContents(player); }
        }
    }
}
