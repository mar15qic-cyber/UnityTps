#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Restores the third-person model after leaving Play Mode.
    ///
    /// The TP model is a nested model prefab.  Unity can serialize every animated
    /// bone as a scene prefab override when an Animancer graph is stopped while
    /// the scene is being reconstructed.  Those overrides are not authored pose
    /// data and make the edit-mode preview look crouched and partly underground.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayerPoseRecoveryEditor
    {
        private const string PlayerName = "Player";
        private const string ModelRootName = "TP_Model";
        private const string PlayerPrefabPath =
            "Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab";

        private static bool _queued;

        static PlayerPoseRecoveryEditor()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.delayCall += QueueRecovery;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                QueueRecovery();
        }

        private static void QueueRecovery()
        {
            if (_queued)
                return;

            _queued = true;
            EditorApplication.delayCall += Recover;
        }

        private static void Recover()
        {
            _queued = false;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            var player = GameObject.Find(PlayerName);
            if (player == null || !player.scene.IsValid())
                return;

            if (!PrefabUtility.IsPartOfPrefabInstance(player) ||
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(player) != PlayerPrefabPath)
                return;

            bool sceneChanged = RemoveModelTransformOverrides(player);
            RebindModel(player);

            if (sceneChanged)
            {
                EditorSceneManager.MarkSceneDirty(player.scene);
                EditorSceneManager.SaveScene(player.scene);
            }
        }

        private static bool RemoveModelTransformOverrides(GameObject player)
        {
            var modifications = PrefabUtility.GetPropertyModifications(player);
            if (modifications == null || modifications.Length == 0)
                return false;

            var retained = new System.Collections.Generic.List<PropertyModification>
                (modifications.Length);
            bool removed = false;

            foreach (var modification in modifications)
            {
                var target = modification != null ? modification.target as Transform : null;
                if (IsModelTransform(target))
                {
                    removed = true;
                    continue;
                }

                retained.Add(modification);
            }

            if (!removed)
                return false;

            Undo.RecordObject(player, "Restore Player TP bind pose");
            PrefabUtility.SetPropertyModifications(player, retained.ToArray());
            return true;
        }

        private static bool IsModelTransform(Transform target)
        {
            if (target == null)
                return false;

            for (var current = target; current != null; current = current.parent)
            {
                if (current.name == ModelRootName)
                    return true;
            }

            return false;
        }

        private static void RebindModel(GameObject player)
        {
            var model = player.transform.Find(ModelRootName);
            var animator = model != null
                ? model.GetComponentInChildren<Animator>(true)
                : null;
            if (animator == null)
                return;

            Undo.RecordObject(animator, "Restore Player TP bind pose");
            bool wasEnabled = animator.enabled;
            if (!wasEnabled)
                animator.enabled = true;

            animator.Rebind();
            animator.Update(0f);

            if (!wasEnabled)
                animator.enabled = false;
        }
    }
}
#endif
