using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>Final camera offset from the same predicted lean sampled by gameplay.</summary>
    public sealed class CmFPCameraLean : CinemachineExtension
    {
        private Locomotor _locomotor;

        protected override void PostPipelineStageCallback(CinemachineVirtualCameraBase vcam,
            CinemachineCore.Stage stage, ref CameraState state, float deltaTime)
        {
            if (_locomotor == null) _locomotor = GetComponentInParent<Locomotor>();
            if (_locomotor == null) return;
            float lean = _locomotor.Lean.Amount;
            if (stage == CinemachineCore.Stage.Body)
                state.PositionCorrection += transform.root.right * (lean * LeanProfile.EyeSideMeters);
            else if (stage == CinemachineCore.Stage.Finalize)
                state.OrientationCorrection *= Quaternion.AngleAxis(
                    -lean * LeanProfile.MaxCameraRollDegrees, Vector3.forward);
        }
    }
}
