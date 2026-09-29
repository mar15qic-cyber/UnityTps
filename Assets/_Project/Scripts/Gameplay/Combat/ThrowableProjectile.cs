using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Core;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using UnityEngine;

namespace Game.Gameplay.Combat
{
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public sealed class ThrowableProjectile : NetworkBehaviour
    {
        private readonly SyncVar<ThrowableType> _type = new();
        private readonly SyncVar<bool> _detonated = new();
        private readonly SyncVar<Vector3> _effectPosition = new();
        private ThrowableCatalog _catalog;
        private ThrowableDefinition _definition;
        private Rigidbody _body;
        private SphereCollider _sphere;
        private NetworkCombatAuthority _thrower;
        private Vector3 _initialVelocity;
        private float _releaseTime;
        private float _lastImpactTime;
        private bool _offline;
        private bool _visualCreated;
        private GameObject _model;
        private GameObject _effect;
        private AudioSource _audio;
        private PhysicsMaterial _runtimeMaterial;

        public static event System.Action<ThrowableType, Vector3, ThrowableDefinition> OnEffectGlobal;

        private void Awake()
        {
            _catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            _body = GetComponent<Rigidbody>();
            _sphere = GetComponent<SphereCollider>();
            string reason = _catalog == null ? "catalog asset missing" : null;
            if (_catalog == null || !_catalog.IsValid(out reason))
                Debug.LogError($"[ThrowableProjectile] formal catalog invalid: {reason}", this);
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
        }

        public void ServerInitialize(ThrowableType type, Vector3 velocity, NetworkCombatAuthority thrower)
        {
            _offline = false;
            _type.Value = type;
            _detonated.Value = false;
            _effectPosition.Value = Vector3.zero;
            _thrower = thrower;
            _initialVelocity = velocity;
            _releaseTime = Time.time;
            _lastImpactTime = float.NegativeInfinity;
            _definition = _catalog.Get(type);
        }

        public void OfflineInitialize(ThrowableType type, Vector3 velocity, NetworkCombatAuthority thrower)
        {
            _offline = true;
            _type.Value = type;
            _thrower = thrower;
            _initialVelocity = velocity;
            _releaseTime = Time.time;
            _definition = _catalog.Get(type);
            ConfigurePhysics();
            CreateVisual();
            StartCoroutine(Fuse());
        }

        public override void OnStartServer()
        {
            if (_definition == null) _definition = _catalog.Get(_type.Value);
            ConfigurePhysics();
            MatchLifecycle.OnServerMatchEnded += ServerMatchEnded;
            StartCoroutine(Fuse());
        }

        public override void OnStopServer()
        {
            MatchLifecycle.OnServerMatchEnded -= ServerMatchEnded;
            StopAllCoroutines();
            if (_runtimeMaterial != null) Destroy(_runtimeMaterial);
            _runtimeMaterial = null;
        }

        public override void OnStopClient()
        {
            if (_effect != null) Destroy(_effect);
            _effect = null;
            if (_model != null) Destroy(_model);
            _model = null;
            _visualCreated = false;
        }

        private void OnDestroy()
        {
            if (_effect != null) Destroy(_effect);
            if (_runtimeMaterial != null) Destroy(_runtimeMaterial);
        }

        public override void OnStartClient()
        {
            _definition = _catalog.Get(_type.Value);
            if (!IsServerInitialized)
            {
                _body.isKinematic = true;
                _body.interpolation = RigidbodyInterpolation.None; // NetworkTransform owns remote interpolation.
                _sphere.enabled = false; // Client proxy cannot collide against its own interpolated path.
            }
            CreateVisual();
            if (_detonated.Value) CreateEffect(_effectPosition.Value);
        }

        private void ConfigurePhysics()
        {
            if (_definition == null) return;
            _sphere.enabled = true;
            _body.mass = _definition.Mass;
            _body.linearDamping = _definition.AirDrag;
            _body.useGravity = true;
            _body.isKinematic = false;
            _body.linearVelocity = _initialVelocity;
            _body.angularVelocity = Vector3.Cross(Vector3.up, _initialVelocity.normalized) * 14f;
            _runtimeMaterial = new PhysicsMaterial($"Throwable_{_type.Value}")
            {
                bounciness = _definition.Bounciness,
                dynamicFriction = _definition.DynamicFriction,
                staticFriction = _definition.DynamicFriction,
                bounceCombine = PhysicsMaterialCombine.Maximum,
                frictionCombine = PhysicsMaterialCombine.Average
            };
            _sphere.material = _runtimeMaterial;
            if (_thrower != null)
                foreach (var ownerCollider in _thrower.GetComponentsInChildren<Collider>())
                    Physics.IgnoreCollision(_sphere, ownerCollider, true);
        }

        private void CreateVisual()
        {
            if (_definition == null || Application.isBatchMode) return;
            if (_definition.ModelPrefab == null)
            {
                Debug.LogError($"[ThrowableProjectile] {_type.Value} model missing", this);
                return;
            }
            _model = Instantiate(_definition.ModelPrefab, transform);
            _model.transform.localPosition = Vector3.zero;
            _model.transform.localRotation = Quaternion.identity;
        }

        // PhysX sleeps only after sustained contact. A downward ray used to stop a
        // slow grenade while still airborne, including immediately after a wall hit.

        private void OnCollisionEnter(Collision collision)
        {
            if (_definition == null || _detonated.Value || !_offline && !IsServerInitialized) return;
            var audioConfig = Resources.Load<CombatAudioConfig>("CombatAudioConfig");
            if (audioConfig == null) { Debug.LogError("[ThrowableProjectile] CombatAudioConfig missing", this); return; }
            if (collision.relativeVelocity.magnitude < audioConfig.ImpactMinSpeed
                || Time.time - _lastImpactTime < audioConfig.ImpactCooldownSeconds) return;
            _lastImpactTime = Time.time;
            PlayAudio(_definition.ImpactClip, audioConfig.ImpactVolume, audioConfig.ImpactMaxDistance);
            if (!_offline) ObserversImpact();
        }

        [ObserversRpc(ExcludeServer = true)]
        private void ObserversImpact()
        {
            var config = Resources.Load<CombatAudioConfig>("CombatAudioConfig");
            if (config != null) PlayAudio(_definition.ImpactClip, config.ImpactVolume, config.ImpactMaxDistance);
        }

        private IEnumerator Fuse()
        {
            float remaining = _definition.FuseSeconds - (Time.time - _releaseTime);
            if (remaining > 0f) yield return new WaitForSeconds(remaining);
            if (_detonated.Value) yield break;
            Vector3 center = transform.position;
            if (_type.Value == ThrowableType.Smoke) center = ResolveSmokeGround(center);
            _detonated.Value = true;
            _effectPosition.Value = center;
            _sphere.enabled = false;
            _body.isKinematic = true;
            if (_type.Value == ThrowableType.Frag) ApplyExplosionDamage(center);
            if (!Application.isBatchMode) CreateEffect(center);
            if (!_offline) ObserversDetonate(center);
            yield return new WaitForSeconds(_definition.EffectSeconds);
            if (_offline) Destroy(gameObject);
            else if (NetworkObject != null && NetworkObject.IsServerInitialized) NetworkObject.Despawn();
        }

        private Vector3 ResolveSmokeGround(Vector3 origin)
        {
            var hits = Physics.RaycastAll(origin + Vector3.up * 0.1f, Vector3.down, 20f, ~0,
                QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
                if (hit.collider != _sphere && hit.normal.y > 0.45f
                    && hit.collider.GetComponentInParent<NetworkCombatAuthority>() == null
                    && hit.collider.GetComponentInParent<DamageableTarget>() == null)
                    return hit.point;
            return origin;
        }

        [ObserversRpc(ExcludeServer = true)]
        private void ObserversDetonate(Vector3 center) => CreateEffect(center);

        private void CreateEffect(Vector3 center)
        {
            if (_visualCreated || _definition == null || Application.isBatchMode) return;
            _visualCreated = true;
            if (_sphere != null) _sphere.enabled = false;
            if (_model != null) _model.SetActive(false);
            if (_definition.EffectPrefab != null)
                _effect = Instantiate(_definition.EffectPrefab, center, Quaternion.identity);
            if (_effect != null && _type.Value == ThrowableType.Smoke)
            {
                _effect.transform.localScale = Vector3.one * (_definition.Radius / 3f);
                _effect.transform.position = center + Vector3.up * (1.25f * _definition.Radius / 3f);
                foreach (var particles in _effect.GetComponentsInChildren<ParticleSystem>())
                {
                    var emission = particles.emission;
                    emission.rateOverTimeMultiplier *= _definition.SmokeDensity;
                }
            }
            if (_effect != null) Destroy(_effect, _definition.EffectSeconds);
            OnEffectGlobal?.Invoke(_type.Value, center, _definition);
            var config = Resources.Load<CombatAudioConfig>("CombatAudioConfig");
            if (config != null) PlayDetachedAudio(_definition.DetonateClip, center, config.DetonateVolume,
                config.DetonateMaxDistance);
        }

        private void PlayDetachedAudio(AudioClip clip, Vector3 position, float volume, float maxDistance)
        {
            if (clip == null || Application.isBatchMode) return;
            var host = new GameObject("ThrowableDetonationAudio");
            host.transform.position = position;
            var source = host.AddComponent<AudioSource>();
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1.5f;
            source.maxDistance = maxDistance;
            source.volume = AudioBus.ComputeVolume(volume, AudioBus.Category.Sfx);
            source.clip = clip;
            source.Play();
            Destroy(host, clip.length + 0.1f);
        }

        private void PlayAudio(AudioClip clip, float volume, float maxDistance)
        {
            if (clip == null || Application.isBatchMode) return;
            if (_audio == null)
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.spatialBlend = 1f;
                _audio.rolloffMode = AudioRolloffMode.Linear;
                _audio.minDistance = 1.5f;
            }
            _audio.maxDistance = maxDistance;
            _audio.PlayOneShot(clip, AudioBus.ComputeVolume(volume, AudioBus.Category.Sfx));
        }

        public static int DamageAtDistance(float maxDamage, float radius, float distance)
            => radius <= 0f ? 0 : Mathf.RoundToInt(maxDamage * Mathf.Clamp01(1f - distance / radius));

        private void ApplyExplosionDamage(Vector3 center)
        {
            var closest = new Dictionary<DamageableTarget, (Collider collider, float distance)>();
            foreach (var collider in Physics.OverlapSphere(center, _definition.Radius, ~0,
                QueryTriggerInteraction.Collide))
            {
                var target = collider.GetComponentInParent<DamageableTarget>();
                if (target == null || !target.IsAlive) continue;
                float distance = Vector3.Distance(center, collider.ClosestPoint(center));
                if (!closest.TryGetValue(target, out var old) || distance < old.distance)
                    closest[target] = (collider, distance);
            }
            foreach (var pair in closest)
            {
                var target = pair.Key;
                var collider = pair.Value.collider;
                var victim = target.GetComponentInParent<NetworkCombatAuthority>();
                if (_thrower != null && victim != null
                    && !CanDamageTarget(victim == _thrower, _thrower.TeamId, victim.TeamId)) continue;
                Vector3 hitPoint = collider.ClosestPoint(center);
                int damage = DamageAtDistance(_definition.FragMaxDamage, _definition.Radius, pair.Value.distance);
                if (damage <= 0 || Occluded(center, target.transform.position + Vector3.up * 0.9f)) continue;
                target.ApplyDamage(damage, hitPoint, (hitPoint - center).normalized, _thrower);
            }
        }

        public static bool CanDamageTarget(bool samePlayer, string shooterTeam, string victimTeam)
            => samePlayer || MatchRules.IsDamageAllowed(shooterTeam, victimTeam);

        private bool Occluded(Vector3 from, Vector3 to)
            => IsBlastBlockedByWorld(from, to, _sphere, _thrower);

        public static bool IsBlastBlockedByWorld(Vector3 from, Vector3 to,
            Collider projectileCollider = null, NetworkCombatAuthority thrower = null)
        {
            var hits = Physics.RaycastAll(from, (to - from).normalized, Vector3.Distance(from, to), ~0,
                QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                if (hit.collider == projectileCollider || CombatResolver.IsMovementBlocker(hit.collider)
                    || hit.collider.GetComponentInParent<NetworkCombatAuthority>() != null
                    || hit.collider.GetComponentInParent<DamageableTarget>() != null
                    || thrower != null && hit.collider.GetComponentInParent<NetworkCombatAuthority>() == thrower)
                    continue;
                return true;
            }
            return false;
        }

        private void ServerMatchEnded()
        {
            if (NetworkObject != null && NetworkObject.IsServerInitialized) NetworkObject.Despawn();
        }
    }
}
