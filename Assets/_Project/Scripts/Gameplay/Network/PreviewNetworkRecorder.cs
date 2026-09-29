using System;
using System.Reflection;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using LiteNetLib;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>Local diagnostics only. No new RPCs, payload capture or remote upload.</summary>
    public sealed class PreviewNetworkRecorder : MonoBehaviour
    {
        private NetworkManager manager;
        private Transport transport;
        private double started, maxFrame;
        private int frames;
        private string match, scene;
        private NetManager lastServerSocket, lastClientSocket;
        private int serverEpoch, clientEpoch;
        private static readonly FieldInfo SocketManager = typeof(Tugboat).Assembly
            .GetType("FishNet.Transporting.Tugboat.CommonSocket")?.GetField("NetManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!PublicTestTelemetry.Enabled) return;
            var root = new GameObject("PreviewNetworkRecorder"); DontDestroyOnLoad(root);
            root.AddComponent<PreviewNetworkRecorder>();
        }
        private void Awake()
        {
            started = Time.realtimeSinceStartupAsDouble;
            PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "session-start", reason = Application.version });
            Application.logMessageReceived += OnLog;
        }
        private void Update()
        {
            // InstanceFinder logs every lookup while a menu scene has no manager.
            var instances = NetworkManager.Instances;
            var current = instances.Count > 0 ? instances[0] : null;
            if (current != manager) { Unbind(); manager = current; Bind(); }
            frames++; maxFrame = Math.Max(maxFrame, Time.unscaledDeltaTime * 1000d);
            var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (match != MatchLifecycle.ClientMatchId || scene != currentScene)
            {
                match = MatchLifecycle.ClientMatchId; scene = currentScene;
                PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "match-scene", reason = MatchLifecycle.Phase.ToString() });
            }
            var elapsed = Time.realtimeSinceStartupAsDouble - started;
            if (elapsed < 1d) return;
            if (transport is Tugboat tugboat)
            {
                var serverSocket = SocketManager?.GetValue(tugboat.ServerSocket) as NetManager;
                var clientSocket = SocketManager?.GetValue(tugboat.ClientSocket) as NetManager;
                Sample(serverSocket, true, elapsed);
                Sample(clientSocket, false, elapsed);
                if (serverSocket == null && clientSocket == null)
                    PublicTestTelemetry.Write(MakeRecord("network-sample", null, -1, elapsed));
            }
            else PublicTestTelemetry.Write(MakeRecord("network-sample", null, -1, elapsed));
            started = Time.realtimeSinceStartupAsDouble; frames = 0; maxFrame = 0;
        }
        private void Bind()
        {
            transport = manager != null ? manager.TransportManager.Transport : null;
            if (transport == null) return;
            transport.OnClientConnectionState += OnClient;
            transport.OnServerConnectionState += OnServer;
            transport.OnRemoteConnectionState += OnRemote;
            PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "transport-bind", transport = transport.GetType().Name });
        }
        private void Unbind()
        {
            if (transport == null) return;
            transport.OnClientConnectionState -= OnClient;
            transport.OnServerConnectionState -= OnServer;
            transport.OnRemoteConnectionState -= OnRemote;
            transport = null;
        }
        private void Sample(NetManager socket, bool server, double elapsed)
        {
            if (socket == null) return;
            if (server && socket != lastServerSocket) { lastServerSocket = socket; serverEpoch++; }
            if (!server && socket != lastClientSocket) { lastClientSocket = socket; clientEpoch++; }
            // LiteNetLib counters are opt-in even when GetPacketLoss() is callable.
            socket.EnableStatistics = true;
            var record = MakeRecord("network-sample", socket.Statistics, -1, elapsed);
            record.statisticsScope = server ? "server-socket-cumulative" : "client-socket-cumulative";
            record.connectedPeers = socket.ConnectedPeersCount;
            record.socketEpoch = server ? serverEpoch : clientEpoch;
            record.networkState = transport.GetConnectionState(server).ToString();
            PublicTestTelemetry.Write(record);
            if (server)
                foreach (var peer in socket.ConnectedPeerList)
                {
                    var perPeer = MakeRecord("network-peer", peer.Statistics, peer.Id, elapsed);
                    perPeer.rtt = peer.RoundTripTime;
                    perPeer.socketEpoch = serverEpoch;
                    perPeer.statisticsScope = "peer-cumulative";
                    perPeer.networkState = peer.ConnectionState.ToString();
                    PublicTestTelemetry.Write(perPeer);
                }
        }
        private PublicTestTelemetry.Record MakeRecord(string kind, NetStatistics stats, int connection, double elapsed)
        {
            return new PublicTestTelemetry.Record {
                kind = kind, connection = connection, transport = transport != null ? transport.GetType().Name : "none",
                serverTick = manager != null ? manager.TimeManager.Tick : 0,
                rtt = manager != null ? manager.TimeManager.RoundTripTime : 0,
                fps = frames / elapsed, frameMs = elapsed * 1000d / Math.Max(frames, 1), maxFrameMs = maxFrame, sampleSeconds = elapsed,
                statisticsAvailable = stats != null, packetsSent = stats?.PacketsSent ?? 0, packetsReceived = stats?.PacketsReceived ?? 0,
                bytesSent = stats?.BytesSent ?? 0, bytesReceived = stats?.BytesReceived ?? 0, packetsLost = stats?.PacketLoss ?? 0,
                packetLossPercent = stats != null && stats.PacketsSent > 0 ? 100d * stats.PacketLoss / stats.PacketsSent : 0
            };
        }
        private void OnClient(ClientConnectionStateArgs a) => PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "client-connection", networkState = a.ConnectionState.ToString() });
        private void OnServer(ServerConnectionStateArgs a) => PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "server-connection", networkState = a.ConnectionState.ToString() });
        private void OnRemote(RemoteConnectionStateArgs a) => PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "remote-connection", connection = a.ConnectionId, networkState = a.ConnectionState.ToString() });
        private void OnLog(string message, string stack, LogType type)
        {
            // Never copy arbitrary log text/passwords/chat into the exportable stream.
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "runtime-error", reason = type.ToString() });
        }
        private void OnApplicationQuit() { PublicTestTelemetry.Write(new PublicTestTelemetry.Record { kind = "session-end" }); }
        private void OnDestroy() { Unbind(); Application.logMessageReceived -= OnLog; }
    }
}
