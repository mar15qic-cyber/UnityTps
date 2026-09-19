using System;
using System.Collections;
using System.Text;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 客户端运行时不变量探针（2026-09-08 追加 P0 §6 一.3，审计 §5.1 缺口 1）：
    /// 在线认证生成 Owner 玩家后延迟扫描本进程唯一性——启用的本地 InputReader、
    /// 主相机（tag=MainCamera）、AudioListener、Owner Player 恰好各为 1；违例输出
    /// 结构化错误并列出对象名/instanceId（绝不静默容忍）。正常通过也打一行 OK 证据
    ///（实机验收判据）。仅 Development/Editor 编译运行；离线/authored 模式不挂载。
    /// 挂载点：PlayerNetworkAdapter.OnStartClient owner 分支（联网生成路径专属）。
    /// </summary>
    public sealed class GameplayClientInvariantProbe : MonoBehaviour
    {
        private const float ScanDelaySeconds = 1f;

        /// <summary>幂等挂载（联网 Owner 玩家生成时调用；同对象重复调用安全）。</summary>
        public static void EnsureAttachedTo(GameObject ownerPlayer)
        {
            if (ownerPlayer == null) return;
            if (ownerPlayer.GetComponent<GameplayClientInvariantProbe>() != null) return;
            ownerPlayer.AddComponent<GameplayClientInvariantProbe>();
        }

        private void Start()
        {
            StartCoroutine(ScanAfterDelay());
        }

        private IEnumerator ScanAfterDelay()
        {
            yield return new WaitForSeconds(ScanDelaySeconds);
            try
            {
                ScanAndReport();
            }
            finally
            {
                // 探针自销毁：一次性诊断，不占运行时开销
                if (gameObject != null) Destroy(this);
            }
        }

        /// <summary>扫描 + 结构化报告（internal 供 EditMode 直调断言；不依赖协程）。</summary>
        internal static void ScanAndReport()
        {
            var scan = Scan();
            bool violation = scan.Violation;
            var sb = new StringBuilder();
            sb.Append(violation ? "[Invariants] VIOLATION " : "[Invariants] OK ");
            sb.Append($"ownerPlayers={scan.OwnerPlayers} inputReaders={scan.InputReaders} mainCameras={scan.MainCameras} audioListeners={scan.AudioListeners}");
            if (violation)
            {
                AppendObjects(sb, "ownerPlayers", CollectEnabled<PlayerNetworkAdapter>(IsOwnerAdapter));
                AppendObjects(sb, "inputReaders", CollectEnabled<InputReader>());
                AppendObjects(sb, "mainCameras", CollectEnabled<Camera>(IsMainCamera));
                AppendObjects(sb, "audioListeners", CollectEnabled<AudioListener>());
                Debug.LogError(sb.ToString());
            }
            else
            {
                // 正常证据行（实机验收）：列出唯一实例名，供与 Hierarchy 比对
                var ownerPlayers = CollectEnabled<PlayerNetworkAdapter>(IsOwnerAdapter);
                var mainCameras = CollectEnabled<Camera>(IsMainCamera);
                sb.Append($" player='{(ownerPlayers.Count > 0 ? ownerPlayers[0].name : "-")}'");
                sb.Append($" camera='{(mainCameras.Count > 0 ? mainCameras[0].name : "-")}'");
                Debug.Log(sb.ToString());
            }
        }

        /// <summary>扫描结果（internal 供 EditMode 断言精确计数，不依赖日志文本）。</summary>
        internal readonly struct InvariantScanResult
        {
            public InvariantScanResult(int ownerPlayers, int inputReaders, int mainCameras, int audioListeners)
            {
                OwnerPlayers = ownerPlayers;
                InputReaders = inputReaders;
                MainCameras = mainCameras;
                AudioListeners = audioListeners;
            }

            public int OwnerPlayers { get; }
            public int InputReaders { get; }
            public int MainCameras { get; }
            public int AudioListeners { get; }

            public bool Violation
                => OwnerPlayers != 1 || InputReaders != 1 || MainCameras != 1 || AudioListeners != 1;
        }

        /// <summary>扫描（internal，EditMode 可测）。</summary>
        internal static InvariantScanResult Scan() => new InvariantScanResult(
            CollectEnabled<PlayerNetworkAdapter>(IsOwnerAdapter).Count,
            CollectEnabled<InputReader>().Count,
            CollectEnabled<Camera>(IsMainCamera).Count,
            CollectEnabled<AudioListener>().Count);

        private static bool IsMainCamera(Camera camera) =>
            camera != null && camera.CompareTag("MainCamera");

        private static bool IsOwnerAdapter(PlayerNetworkAdapter adapter) =>
            adapter != null && FishNetLifecycleGuard.IsLocalOwner(adapter);

        private static System.Collections.Generic.List<T> CollectEnabled<T>(Func<T, bool> predicate = null) where T : UnityEngine.Behaviour
        {
            var result = new System.Collections.Generic.List<T>();
            foreach (var found in UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (found == null) continue;
                // Gate A-1（2026-09-08 P0 追加复审 §1.1）：必须按 isActiveAndEnabled 计数——
                // FindObjectsInactive.Exclude 只按“对象激活态”过滤，激活对象上的禁用组件仍会被
                // 返回（远端玩家的 InputReader 正是这种形态），只查 activeInHierarchy 会把正常
                // 双人局误报成 VIOLATION。isActiveAndEnabled = 对象激活 + 组件启用，二者都要。
                if (!found.isActiveAndEnabled) continue;
                if (predicate != null && !predicate(found)) continue;
                result.Add(found);
            }
            return result;
        }

        private static void AppendObjects<T>(StringBuilder sb, string label, System.Collections.Generic.List<T> objects) where T : UnityEngine.Component
        {
            sb.Append($" {label}=[");
            for (int i = 0; i < objects.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                var o = objects[i];
                sb.Append($"{o.gameObject.name}(id:{o.GetInstanceID()})");
            }
            sb.Append("]");
        }
    }
}
