using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>The view's authored camera position, without deforming its arms or gun.</summary>
    public sealed class FPViewFramingProfile : MonoBehaviour
    {
        [SerializeField] private Vector3 authoringCameraLocalPosition;
        public Vector3 AuthoringCameraLocalPosition => authoringCameraLocalPosition;

        public void Configure(Vector3 cameraLocalPosition)
        {
            if (!IsFinite(cameraLocalPosition))
                throw new System.ArgumentOutOfRangeException(nameof(cameraLocalPosition));
            authoringCameraLocalPosition = cameraLocalPosition;
        }

        internal static bool IsFinite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
