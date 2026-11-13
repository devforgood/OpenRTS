using System;
using System.Collections.Generic;
using System.Numerics;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Shared.Interpolation
{
    /// <summary>
    /// 클라이언트 쪽 스냅샷 보간기. 받은 스냅샷을 버퍼에 쌓고,
    /// 추정한 서버 시각보다 <see cref="InterpolationDelay"/> 만큼 과거 시점을 두 스냅샷 사이에서 보간해 그린다.
    /// UnityEngine 에 의존하지 않아 서버 테스트 프로젝트에서 검증한다.
    /// </summary>
    public sealed class SnapshotInterpolator
    {
        private sealed class Entry
        {
            public uint Tick;
            public double Time;
            public readonly Dictionary<int, Vector2> Positions = new Dictionary<int, Vector2>();
        }

        private struct GapSample
        {
            public double At;
            public float Need;
        }

        private const int MaxBuffered = 64;

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Stack<Entry> _pool = new Stack<Entry>();
        private readonly Queue<GapSample> _gaps = new Queue<GapSample>();
        private readonly float _tickDelta;
        private bool _hasClock;

        /// <param name="tickDelta">서버 틱 길이(초).</param>
        /// <param name="interpolationDelay">최소 렌더 지연(초). 스냅샷 간격의 2배 정도.</param>
        public SnapshotInterpolator(float tickDelta, float interpolationDelay)
        {
            _tickDelta = tickDelta;
            MinInterpolationDelay = interpolationDelay;
            InterpolationDelay = interpolationDelay;
        }

        /// <summary>
        /// 현재 렌더 지연(초). <see cref="AdaptiveDelay"/> 가 켜져 있으면 망 상태에 따라
        /// <see cref="MinInterpolationDelay"/> ~ <see cref="MaxInterpolationDelay"/> 사이에서 서서히 바뀐다.
        /// </summary>
        public float InterpolationDelay { get; private set; }

        /// <summary>지연의 하한 (정상 망에서의 값).</summary>
        public float MinInterpolationDelay { get; set; }

        public float MaxInterpolationDelay { get; set; } = 0.5f;

        /// <summary>스냅샷이 늦거나 빠질 때 렌더 지연을 늘려 외삽·튐을 피한다.</summary>
        public bool AdaptiveDelay { get; set; } = true;

        /// <summary>망 상태를 판단하는 최근 구간(초).</summary>
        public float GapWindow { get; set; } = 3f;

        /// <summary>지연을 늘리는 속도(초/초). 늘리는 동안 화면 속도가 (1 - 이 값) 배로 느려진다.</summary>
        public float DelayIncreaseRate { get; set; } = 0.25f;

        /// <summary>지연을 줄이는 속도(초/초). 줄이는 동안 화면 속도가 (1 + 이 값) 배로 빨라진다.</summary>
        public float DelayDecreaseRate { get; set; } = 0.05f;

        /// <summary>적응형 지연이 목표로 하는 값(초).</summary>
        public float TargetInterpolationDelay { get; private set; }

        /// <summary>스냅샷이 끊겼을 때 마지막 속도로 이어서 그리는 최대 시간(초).</summary>
        public float MaxExtrapolation { get; set; } = 0.25f;

        /// <summary>스냅샷이 올 때마다 시계 오차를 이 비율만큼 보정한다.</summary>
        public float ClockCorrectionGain { get; set; } = 0.1f;

        /// <summary>시계 오차가 이보다 크면 보정 대신 바로 맞춘다(초).</summary>
        public float ClockResetThreshold { get; set; } = 1f;

        /// <summary>추정한 현재 서버 시각(초) — 가장 최근 스냅샷 도착 기준.</summary>
        public double ServerClock { get; private set; }

        public double RenderTime => ServerClock - InterpolationDelay;

        public uint LatestTick { get; private set; }

        public int BufferedCount => _entries.Count;

        /// <summary>마지막 <see cref="Sample"/> 이 최신 스냅샷을 넘어 외삽했는지.</summary>
        public bool IsExtrapolating { get; private set; }

        /// <summary>순서가 늦게 도착해 버린 스냅샷 수.</summary>
        public int DroppedOutOfOrder { get; private set; }

        /// <summary>프레임마다 경과 시간만큼 시계를 진행한다.</summary>
        public void Advance(float deltaTime)
        {
            if (!_hasClock)
            {
                return;
            }

            ServerClock += deltaTime;
            UpdateDelay(deltaTime);
        }

        private void UpdateDelay(float deltaTime)
        {
            if (!AdaptiveDelay)
            {
                TargetInterpolationDelay = MinInterpolationDelay;
                InterpolationDelay = MinInterpolationDelay;
                return;
            }

            while (_gaps.Count > 0 && ServerClock - _gaps.Peek().At > GapWindow)
            {
                _gaps.Dequeue();
            }

            // 최근 구간에서 "다음 스냅샷이 올 때까지 버퍼를 얼마나 앞질렀나"의 최댓값 + 1틱 여유
            var worst = 0f;
            foreach (var g in _gaps)
            {
                worst = Math.Max(worst, g.Need);
            }

            TargetInterpolationDelay = Math.Min(MaxInterpolationDelay, Math.Max(MinInterpolationDelay, worst + _tickDelta));

            if (InterpolationDelay < TargetInterpolationDelay)
            {
                InterpolationDelay = Math.Min(TargetInterpolationDelay, InterpolationDelay + DelayIncreaseRate * deltaTime);
            }
            else if (InterpolationDelay > TargetInterpolationDelay)
            {
                InterpolationDelay = Math.Max(TargetInterpolationDelay, InterpolationDelay - DelayDecreaseRate * deltaTime);
            }
        }

        public void AddSnapshot(uint tick, IReadOnlyList<UnitState> units)
        {
            if (_entries.Count > 0 && tick <= LatestTick)
            {
                DroppedOutOfOrder++;
                return;
            }

            var time = tick * (double)_tickDelta;
            if (_hasClock && _entries.Count > 0)
            {
                // 이 스냅샷이 오기 직전, 추정 시계는 직전 스냅샷보다 이만큼 앞서 있었다.
                // 렌더 지연이 이보다 짧았다면 그동안 외삽해야 했다는 뜻이다.
                var need = (float)(ServerClock - _entries[_entries.Count - 1].Time);
                _gaps.Enqueue(new GapSample { At = ServerClock, Need = need });
            }

            var entry = _pool.Count > 0 ? _pool.Pop() : new Entry();
            entry.Tick = tick;
            entry.Time = time;
            entry.Positions.Clear();
            for (var i = 0; i < units.Count; i++)
            {
                entry.Positions[units[i].Id] = units[i].Position;
            }

            _entries.Add(entry);
            LatestTick = tick;
            SyncClock(entry.Time);
            Prune();
        }

        /// <summary>렌더 시점의 유닛 위치를 <paramref name="output"/> 에 채운다 (기존 내용은 지움).</summary>
        public void Sample(Dictionary<int, Vector2> output)
        {
            output.Clear();
            IsExtrapolating = false;
            if (_entries.Count == 0)
            {
                return;
            }

            var render = RenderTime;
            var first = _entries[0];
            if (render <= first.Time)
            {
                Copy(first, output);
                return;
            }

            var last = _entries[_entries.Count - 1];
            if (render >= last.Time)
            {
                Extrapolate(render, last, output);
                return;
            }

            for (var i = 0; i < _entries.Count - 1; i++)
            {
                var from = _entries[i];
                var to = _entries[i + 1];
                if (render >= to.Time)
                {
                    continue;
                }

                var t = (float)((render - from.Time) / (to.Time - from.Time));
                foreach (var kv in to.Positions)
                {
                    // 새로 생긴 유닛은 이전 스냅샷에 없으므로 그대로 둔다.
                    output[kv.Key] = from.Positions.TryGetValue(kv.Key, out var a)
                        ? Vector2.Lerp(a, kv.Value, t)
                        : kv.Value;
                }

                return;
            }
        }

        private void Extrapolate(double render, Entry last, Dictionary<int, Vector2> output)
        {
            if (_entries.Count < 2 || render - last.Time < 1e-6)
            {
                Copy(last, output);
                return;
            }

            IsExtrapolating = true;
            var prev = _entries[_entries.Count - 2];
            var span = (float)(last.Time - prev.Time);
            var ahead = (float)Math.Min(render - last.Time, MaxExtrapolation);
            foreach (var kv in last.Positions)
            {
                if (prev.Positions.TryGetValue(kv.Key, out var p))
                {
                    var velocity = (kv.Value - p) / span;
                    output[kv.Key] = kv.Value + velocity * ahead;
                }
                else
                {
                    output[kv.Key] = kv.Value;
                }
            }
        }

        private void SyncClock(double snapshotTime)
        {
            var error = snapshotTime - ServerClock;
            if (!_hasClock || Math.Abs(error) > ClockResetThreshold)
            {
                ServerClock = snapshotTime;
                _hasClock = true;
                _gaps.Clear();
                return;
            }

            ServerClock += error * ClockCorrectionGain;
        }

        /// <summary>렌더 시점보다 완전히 지난 스냅샷을 버린다. 보간용으로 렌더 시점 직전 1개는 남긴다.</summary>
        private void Prune()
        {
            var render = RenderTime;
            while (_entries.Count > 2 && (_entries[1].Time <= render || _entries.Count > MaxBuffered))
            {
                _pool.Push(_entries[0]);
                _entries.RemoveAt(0);
            }
        }

        private static void Copy(Entry entry, Dictionary<int, Vector2> output)
        {
            foreach (var kv in entry.Positions)
            {
                output[kv.Key] = kv.Value;
            }
        }
    }
}
