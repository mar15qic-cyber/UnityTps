using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    public static class PlayerGroundContactAudit
    {
        [MenuItem("Tools/Review/Probe Character Controller Grounding")]
        public static void ProbeGrounding()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var player = new GameObject("TemporaryGroundProbe");
            try
            {
                floor.transform.position = new Vector3(10000f, -.1f, 10000f);
                floor.transform.localScale = new Vector3(10f, .2f, 10f);
                player.transform.position = new Vector3(10000f, .15f, 10000f);
                var controller = player.AddComponent<CharacterController>();
                controller.height = 1.8f;
                controller.radius = .35f;
                controller.center = new Vector3(0f, .9f, 0f);
                controller.skinWidth = .08f;
                Physics.SyncTransforms();
                for (int i = 0; i < 20; i++) controller.Move(Vector3.down * .05f);
                float original = player.transform.position.y;
                controller.enabled = false;
                player.transform.position = new Vector3(10000f, .15f, 10000f);
                controller.center = new Vector3(0f, .98f, 0f);
                controller.enabled = true;
                Physics.SyncTransforms();
                for (int i = 0; i < 20; i++) controller.Move(Vector3.down * .05f);
                Debug.Log($"[GroundProbe] originalRootY={original:F5} adjustedRootY={player.transform.position.y:F5} ccGrounded={controller.isGrounded}");
            }
            finally
            {
                Object.DestroyImmediate(player);
                Object.DestroyImmediate(floor);
            }
        }

        [MenuItem("Tools/Review/Audit Player Ground Contact")]
        public static void Audit()
        {
            const string prefabPath = "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var output = new StringBuilder();
                var controller = root.GetComponent<CharacterController>();
                var model = root.transform.Find("TP_Model");
                var animator = model != null ? model.GetComponentInChildren<Animator>(true) : null;
                output.AppendLine($"root={root.transform.position} modelLocal={model?.localPosition}");
                output.AppendLine($"ccBottom={controller.center.y - controller.height * .5f:F4} skin={controller.skinWidth:F4}");
                if (animator != null && animator.isHuman)
                {
                    foreach (var bone in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                                 HumanBodyBones.LeftToes, HumanBodyBones.RightToes })
                    {
                        var transform = animator.GetBoneTransform(bone);
                        output.AppendLine($"{bone} localY={(transform != null ? root.transform.InverseTransformPoint(transform.position).y : float.NaN):F4}");
                    }
                }
                if (model != null)
                    foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                    {
                        output.AppendLine($"renderer {renderer.name}: minY={root.transform.InverseTransformPoint(renderer.bounds.min).y:F4} active={renderer.gameObject.activeInHierarchy}");
                        if (renderer is SkinnedMeshRenderer skinned && renderer.gameObject.activeInHierarchy)
                        {
                            var mesh = new Mesh();
                            try
                            {
                                skinned.BakeMesh(mesh, true);
                                float lowest = float.PositiveInfinity;
                                foreach (var vertex in mesh.vertices)
                                {
                                    float y = root.transform.InverseTransformPoint(skinned.transform.TransformPoint(vertex)).y;
                                    if (y < lowest) lowest = y;
                                }
                                output.AppendLine($"  bakedLowestVertexY={lowest:F4} vertices={mesh.vertexCount}");
                            }
                            finally { Object.DestroyImmediate(mesh); }
                        }
                    }
                Directory.CreateDirectory("Captures");
                File.WriteAllText("Captures/PlayerGroundContactAudit.txt", output.ToString());
                Debug.Log(output.ToString());
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
