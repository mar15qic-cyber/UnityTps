using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Weapon
{
    /// <summary>
    /// 观察者远端弹道表现（ADS 审计 S2，2026-09-19）：消费 NetworkCombatAuthority.OnRemoteShotGlobal，
    /// 为"非本地玩家"的每一发权威结果补 Default 层世界弹道（起点=AimOrigin、终点=权威最终点，
    /// 霰弹逐弹丸）。Owner 本地弹道由 WeaponView 的 FP/世界段承担（按 shooter.IsOwnerPlayer 过滤，
    /// 不双画）。旧实现 ObserversShot 只传 4 标量且 OnRemoteShot 零订阅者——远端玩家开火没有任何
    /// 弹道表现；本组件补齐该缺口，且远端看到的弹着=服务器权威结果（非各自本地预测）。
    /// 离线/无广播时零行为。DS 无头端生成的是不可见 LineRenderer，无渲染成本。
    /// </summary>
    public sealed class RemoteShotFxView : MonoBehaviour
    {
        private const int PoolSize = 8;
        private const float TracerLifeSeconds = 0.08f;

        private readonly LineRenderer[] _lines = new LineRenderer[PoolSize];
        private readonly float[] _timers = new float[PoolSize];
        private Material _material;
        private int _cursor;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Mount()
        {
            if (FindFirstObjectByType<RemoteShotFxView>() != null) return;
            var host = new GameObject("RemoteShotFxHost");
            DontDestroyOnLoad(host);
            host.AddComponent<RemoteShotFxView>();
        }

        private void OnEnable()
        {
            NetworkCombatAuthority.OnRemoteShotGlobal += HandleRemoteShot;
            BuildPool();
        }

        private void OnDisable()
        {
            NetworkCombatAuthority.OnRemoteShotGlobal -= HandleRemoteShot;
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }

        private void Update()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                if (_timers[i] <= 0f) continue;
                _timers[i] -= Time.deltaTime;
                if (_lines[i] != null) _lines[i].enabled = _timers[i] > 0f;
            }
        }

        private void HandleRemoteShot(NetworkCombatAuthority shooter, RemoteShotPresentation shot)
        {
            // Owner 本地已有 FP 表现（WeaponView）；远端替身/其它玩家才补世界弹道
            if (shooter != null && shooter.IsOwnerPlayer) return;
            if (shot.PelletCount <= 1)
            {
                SpawnTracer(shot.Origin, shot.FinalPoint);
                return;
            }
            if (shot.PelletPoints == null) return;
            for (int i = 0; i < shot.PelletPoints.Length && i < shot.PelletCount; i++)
                SpawnTracer(shot.Origin, shot.PelletPoints[i]);
        }

        private void SpawnTracer(Vector3 start, Vector3 end)
        {
            int index = _cursor;
            _cursor = (_cursor + 1) % PoolSize;
            var line = _lines[index];
            if (line == null) return;
            line.SetPosition(0, start);
            line.SetPosition(1, end);
            line.enabled = true;
            _timers[index] = TracerLifeSeconds;
        }

        private void BuildPool()
        {
            if (_lines[0] != null) return;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null) _material = new Material(shader) { color = new Color(1f, 0.78f, 0.15f, 0.9f) };
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"Remote_Tracer_{i}");
                go.transform.SetParent(transform, false);
                go.layer = 0; // Default：所有世界相机/倍率镜 RT 都渲染（scope mask 仅剔 FP/身体/UI）
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = 0.02f;
                line.endWidth = 0.004f;
                line.startColor = new Color(1f, 0.78f, 0.15f, 0.9f);
                line.endColor = new Color(1f, 0.78f, 0.15f, 0f);
                if (_material != null) line.material = _material;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.enabled = false;
                _lines[i] = line;
            }
        }
    }
}
