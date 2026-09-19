using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    /// <summary>
    /// 远端视觉缓冲插值（2026-09-17 实机"远端走动掉帧/不丝滑"修复；纯逻辑可离线测试）。
    ///
    /// 背景：方案A 把远端根/CC 钉在最新权威位姿（NT 插值压到 1）后，根以到达节奏（≈服务器 tick
    /// 30Hz）阶梯式瞬移；原指数追随（τ=0.07s）在两段样本之间先快后慢，视觉上呈现"一顿一顿"的
    /// 脉冲式移动。本缓冲改为经典快照插值：记录根位姿样本（仅在其变化时），以"当前时间 −
    /// 自适应延迟"为渲染时刻在两样本间线性插值——远端模型以渲染帧率丝滑移动，代价是
    /// ≤RemoteVisualInterpMaxDelaySeconds 的固定视觉延迟（碰撞根不动，方案A 语义不变）。
    ///
    /// 延迟自适应：到达间隔 EMA × 2.1，钳 [minDelay, maxDelay]；样本饥饿时保持最新位姿不后撤。
    /// 只读时间参数，不持有 Unity 对象引用，稳态零分配（固定上限的 List 复用）。
    /// </summary>
    public sealed class RemoteVisualInterpolationBuffer
    {
        private struct Sample
        {
            public float Time;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private readonly List<Sample> _samples = new(24);
        private float _intervalEma;
        private float _lastArrival = -1f;

        /// <summary>当前自适应渲染延迟（秒；EMA(到达间隔)×2.1 经钳制）。</summary>
        public float CurrentDelay { get; private set; } = 0.066f;

        public bool HasSamples => _samples.Count > 0;
        public int Count => _samples.Count;
        public Vector3 LatestPosition => _samples.Count > 0 ? _samples[_samples.Count - 1].Position : Vector3.zero;
        public Quaternion LatestRotation => _samples.Count > 0 ? _samples[_samples.Count - 1].Rotation : Quaternion.identity;

        public void Reset()
        {
            _samples.Clear();
            _intervalEma = 0f;
            _lastArrival = -1f;
            CurrentDelay = 0.066f;
        }

        /// <summary>推入一个新位姿样本（调用方已按"位姿有变化"过滤；time 单调递增）。</summary>
        public void Push(float time, Vector3 position, Quaternion rotation, float minDelay, float maxDelay)
        {
            if (_lastArrival >= 0f)
            {
                float dt = time - _lastArrival;
                // 只统计正常到达间隔：过小=同帧重复调用，过大=断流/暂停，均不污染延迟估计
                if (dt > 0.0005f && dt < 1f)
                {
                    _intervalEma = _intervalEma <= 0f ? dt : Mathf.Lerp(_intervalEma, dt, 0.15f);
                    CurrentDelay = Mathf.Clamp(_intervalEma * 2.1f, minDelay, maxDelay);
                }
            }
            _lastArrival = time;

            // 同一时刻重复推入（同帧多次调用且目标变了）→ 覆盖最新样本而非追加（保持时间严格递增）
            if (_samples.Count > 0 && time - _samples[_samples.Count - 1].Time < 0.0005f)
                _samples[_samples.Count - 1] = new Sample { Time = time, Position = position, Rotation = rotation };
            else
                _samples.Add(new Sample { Time = time, Position = position, Rotation = rotation });

            // 只保留最近 ~1.5s（缓冲插值窗口远小于此；防断流后陈旧样本参与插值）
            while (_samples.Count > 0 && time - _samples[0].Time > 1.5f)
                _samples.RemoveAt(0);
        }

        /// <summary>求 now−CurrentDelay 渲染时刻的插值位姿；无样本返回 false；饥饿保持最新，超前保持最老。</summary>
        public bool Evaluate(float now, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (_samples.Count == 0) return false;

            float renderTime = now - CurrentDelay;
            var last = _samples[_samples.Count - 1];
            if (renderTime >= last.Time)
            {
                position = last.Position;
                rotation = last.Rotation;
                return true;
            }

            for (int i = _samples.Count - 1; i > 0; i--)
            {
                var b = _samples[i];
                var a = _samples[i - 1];
                if (a.Time <= renderTime && renderTime <= b.Time)
                {
                    float span = b.Time - a.Time;
                    float t = span > 1e-5f ? (renderTime - a.Time) / span : 1f;
                    position = Vector3.LerpUnclamped(a.Position, b.Position, t);
                    rotation = Quaternion.SlerpUnclamped(a.Rotation, b.Rotation, t);
                    return true;
                }
            }

            var first = _samples[0];
            position = first.Position;
            rotation = first.Rotation;
            return true;
        }
    }
}
