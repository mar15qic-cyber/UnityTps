using System;
using System.IO;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>Allowlisted records only: never serialize arbitrary logs, tickets or credentials.</summary>
    public static class PublicTestTelemetry
    {
        [Serializable]
        public sealed class Record
        {
            public string kind, sessionId, releaseId, map, reason, matchId, runId;
            public double time, displayTick, usedTick, rtt, frameMs, error, rawError, renderTick, bufferMs;
            public uint serverTick, inputTick, shotId, lifeEpoch;
            public int connection, targetConnection, queueDepth;
            public string weaponId, collider;
            public uint commandId, equipmentCommandId;
            public double shotSeconds;
            public int seed, damage, healthAfter, targetObjectId, pelletCount;
            public float ads, spread;
            public Vector3 origin, aimDirection, firedDirection, hitPoint;
            public string utc, role, protocolId, transport, networkState, statisticsScope;
            public int schemaVersion = 2, processId, connectedPeers, socketEpoch;
            public long packetsSent, packetsReceived, bytesSent, bytesReceived, packetsLost;
            public double packetLossPercent, fps, maxFrameMs, sampleSeconds;
            public bool statisticsAvailable;
        }
        private static readonly string Session = Guid.NewGuid().ToString("N");
        private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        private static StreamWriter _writer;
        private static bool _failed;
        private static int _segment;
        private static string _release, _run;
        private static double _flushedAt;
        public static bool Enabled { get; } =
#if FPS_PREVIEW_TELEMETRY
            true;
#else
            Array.IndexOf(Environment.GetCommandLineArgs(), "-publicTestTelemetry") >= 0;
#endif
        public static string EvidenceDirectory
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "-evidenceDir" && Path.IsPathRooted(args[i + 1])) return Path.GetFullPath(args[i + 1]);
                return Path.Combine(Application.persistentDataPath, "PublicTestEvidence");
            }
        }
        public static void Write(Record value)
        {
            if (!Enabled || _failed) return;
            try
            {
                if (_writer == null)
                {
                    var dir = EvidenceDirectory; Directory.CreateDirectory(dir);
                    _writer = new StreamWriter(Path.Combine(dir, Session + "-" + _segment++ + ".jsonl"), true) { AutoFlush = false };
                    Application.quitting -= Close;
                    Application.quitting += Close;
                }
                value.sessionId = Session;
                value.utc = DateTime.UtcNow.ToString("O");
                value.processId = ProcessId;
                value.protocolId = GameProtocolIdentity.ProtocolId;
#if UNITY_SERVER
                value.role = "server";
#else
                value.role = "client";
#endif
                value.matchId = MatchLifecycle.ClientMatchId;
                value.runId = _run ??= ReadRunId();
                value.releaseId = _release ??= Game.Core.ClientReleaseEnvironment.Current?.releaseId
                    ?? Environment.GetEnvironmentVariable("FPS_RELEASE_ID")
                    ?? GameProtocolIdentity.TryReadDeployedManifest()?.buildId ?? "unidentified";
                value.time = Time.realtimeSinceStartupAsDouble;
                value.map = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                _writer.WriteLine(JsonUtility.ToJson(value));
                if (value.time - _flushedAt >= 1 || value.kind == "rebase") { _writer.Flush(); _flushedAt = value.time; }
                if (_writer.BaseStream.Position > 64 * 1024 * 1024) Close();
            }
            catch (Exception) { _failed = true; Close(); Debug.LogWarning("[PublicTest] Evidence writer unavailable"); }
        }
        private static string ReadRunId()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-testRunId" && System.Text.RegularExpressions.Regex.IsMatch(args[i + 1], "^[a-zA-Z0-9_-]{1,80}$")) return args[i + 1];
            return "unspecified";
        }
        private static void Close() { var writer = _writer; _writer = null; try { writer?.Dispose(); } catch (IOException) { } }
    }
}
