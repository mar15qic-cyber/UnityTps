using Game.Presentation.Animation;
using UnityEngine;

namespace Game.Presentation.Camera
{
    /// <summary>Restores the authored overlay viewpoint; the world aim camera stays unchanged.</summary>
    [DefaultExecutionOrder(-45)]
    public sealed class FPWeaponCameraFraming : MonoBehaviour
    {
        private FPWeaponRig _rig;
        private UnityEngine.Camera _camera;
        private Vector3 _defaultPosition;
        private bool _capturedDefault;

        public static void EnsureMounted(FPWeaponRig rig)
        {
            if (rig != null && rig.GetComponent<FPWeaponCameraFraming>() == null)
                rig.gameObject.AddComponent<FPWeaponCameraFraming>();
        }

        private void OnEnable()
        {
            _rig = GetComponent<FPWeaponRig>();
            if (_rig == null) return;
            _rig.OnActiveViewChanged += Apply;
            Apply(_rig.ActiveView);
        }

        private void LateUpdate()
        {
            if (_rig != null) Apply(_rig.ActiveView);
        }

        private void OnDisable()
        {
            if (_rig != null) _rig.OnActiveViewChanged -= Apply;
            if (_camera != null && _capturedDefault) _camera.transform.localPosition = _defaultPosition;
        }

        private void Apply(GameObject view)
        {
            if (_camera == null)
            {
                int fpLayer = LayerMask.NameToLayer("FirstPersonView");
                if (fpLayer < 0) return;
                foreach (var candidate in GetComponentsInChildren<UnityEngine.Camera>(true))
                    if (candidate.transform != transform && candidate.cullingMask == 1 << fpLayer)
                    { _camera = candidate; break; }
                if (_camera == null) return;
            }
            if (!_capturedDefault)
            {
                _defaultPosition = _camera.transform.localPosition;
                _capturedDefault = true;
            }
            var profile = view != null ? view.GetComponent<FPViewFramingProfile>() : null;
            _camera.transform.localPosition = ResolvePosition(_defaultPosition, profile);
        }

        internal static Vector3 ResolvePosition(Vector3 baseline, FPViewFramingProfile profile)
        {
            return profile != null && FPViewFramingProfile.IsFinite(profile.AuthoringCameraLocalPosition)
                ? profile.AuthoringCameraLocalPosition : baseline;
        }
    }
}
