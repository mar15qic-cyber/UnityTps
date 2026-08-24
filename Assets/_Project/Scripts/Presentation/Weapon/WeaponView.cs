using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>WeaponController 事件的只读表现端：弹道、枪口光、命中标记、Day2 调试 HUD。</summary>
    public sealed class WeaponView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private Transform muzzle;
        [SerializeField] private Color tracerColor = new(1f, 0.78f, 0.15f, 1f);
        [SerializeField, Min(0.01f)] private float tracerDuration = 0.045f;
        [SerializeField, Min(0.01f)] private float muzzleFlashDuration = 0.035f;

        private LineRenderer _tracer;
        private Light _muzzleLight;
        private Material _tracerMaterial;
        private float _tracerTimer;
        private float _flashTimer;
        private float _hitMarkerTimer;
        private GUIStyle _ammoStyle;
        private GUIStyle _hintStyle;

        private void Awake()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (muzzle == null) muzzle = transform;
            BuildEffects();
        }

        private void OnEnable()
        {
            if (controller == null) return;
            controller.OnShotFired += HandleShot;
            controller.OnDryFire += HandleDryFire;
        }

        private void OnDisable()
        {
            if (controller == null) return;
            controller.OnShotFired -= HandleShot;
            controller.OnDryFire -= HandleDryFire;
        }

        private void OnDestroy()
        {
            if (_tracerMaterial != null) Destroy(_tracerMaterial);
        }

        private void Update()
        {
            _tracerTimer -= Time.deltaTime;
            _flashTimer -= Time.deltaTime;
            _hitMarkerTimer -= Time.deltaTime;
            if (_tracer != null) _tracer.enabled = _tracerTimer > 0f;
            if (_muzzleLight != null) _muzzleLight.enabled = _flashTimer > 0f;
        }

        private void HandleShot(WeaponShot shot)
        {
            Vector3 start = muzzle != null ? muzzle.position : shot.Origin;
            _tracer.SetPosition(0, start);
            _tracer.SetPosition(1, shot.Result.Point);
            _tracer.enabled = true;
            _tracerTimer = tracerDuration;
            _muzzleLight.enabled = true;
            _flashTimer = muzzleFlashDuration;
            if (shot.Result.Damaged) _hitMarkerTimer = 0.12f;
        }

        private void HandleDryFire() => _flashTimer = 0f;

        private void BuildEffects()
        {
            var tracerObject = new GameObject("Runtime_Tracer");
            tracerObject.transform.SetParent(transform, false);
            _tracer = tracerObject.AddComponent<LineRenderer>();
            _tracer.useWorldSpace = true;
            _tracer.positionCount = 2;
            _tracer.startWidth = 0.012f;
            _tracer.endWidth = 0.003f;
            _tracer.startColor = tracerColor;
            _tracer.endColor = new Color(tracerColor.r, tracerColor.g, tracerColor.b, 0f);
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                _tracerMaterial = new Material(shader) { color = tracerColor };
                _tracer.material = _tracerMaterial;
            }
            _tracer.enabled = false;

            var lightObject = new GameObject("Runtime_MuzzleFlash");
            lightObject.transform.SetParent(muzzle, false);
            _muzzleLight = lightObject.AddComponent<Light>();
            _muzzleLight.type = LightType.Point;
            _muzzleLight.color = new Color(1f, 0.62f, 0.18f);
            _muzzleLight.intensity = 3f;
            _muzzleLight.range = 2f;
            _muzzleLight.enabled = false;
        }

        private void OnGUI()
        {
            if (controller == null || !controller.IsInitialized) return;
            EnsureStyles();

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            GUI.DrawTexture(new Rect(cx - 8f, cy - 1f, 16f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 8f, 2f, 16f), Texture2D.whiteTexture);
            if (_hitMarkerTimer > 0f)
            {
                GUI.Label(new Rect(cx - 20f, cy - 22f, 40f, 40f), "×", _ammoStyle);
            }

            var runtime = controller.Runtime;
            string ammo = $"{runtime.CurrentAmmo:00} / {runtime.ReserveAmmo:000}";
            if (runtime.State == WeaponRuntimeState.Reloading)
                ammo += $"  RELOAD {controller.Actions.NormalizedProgress * 100f:0}%";
            GUI.Label(new Rect(Screen.width - 360f, Screen.height - 80f, 340f, 50f), ammo, _ammoStyle);
            string weapon = controller.Definition != null ? controller.Definition.DisplayName : "";
            GUI.Label(new Rect(Screen.width - 360f, Screen.height - 118f, 340f, 30f), weapon, _hintStyle);
            GUI.Label(new Rect(18f, 18f, 760f, 80f),
                "LMB FIRE    R RELOAD    WASD MOVE    SHIFT SPRINT    1/2 SWITCH", _hintStyle);
        }

        private void EnsureStyles()
        {
            if (_ammoStyle != null) return;
            _ammoStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white }
            };
            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = new Color(1f, 1f, 1f, 0.85f) }
            };
        }
    }
}
