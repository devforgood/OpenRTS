using System;
using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;
using UnityEngine;

namespace OpenRTS.Client.Debugging
{
    /// <summary>
    /// 명령줄 <c>-autotest-collision [bots] [scenario] [map]</c> 로 켜지는 충돌 설정 경로 자동 확인.
    /// 클라이언트에서 설정 변경을 요청 → 서버 적용·전파 확인 → 일정 시간 동안 서버 지표와 화면 유닛 수를 확인 후 종료.
    /// </summary>
    public sealed class CollisionAutoTest : MonoBehaviour
    {
        private const float ObserveSeconds = 12f;
        private const float Timeout = 30f;
        private const float MinSeparationToPass = 0.8f;

        public GameClient Client;
        public UnitViewManager Views;

        private ushort _bots = 100;
        private BotScenario _scenario = BotScenario.CircleSwap;
        private MapKind _map = MapKind.Open;
        private int _maxWallHits;
        private float _elapsed;
        private float _observeStart = -1f;
        private bool _requested;
        private float _worstSeparation = float.MaxValue;
        private int _maxOverlaps;
        private int _maxVisible;
        private float _maxStepMs;
        private bool _done;

        public static bool IsRequested() => Array.IndexOf(Environment.GetCommandLineArgs(), "-autotest-collision") >= 0;

        private void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-autotest-collision");
            if (i + 1 < args.Length && ushort.TryParse(args[i + 1], out var bots))
            {
                _bots = bots;
            }

            if (i + 2 < args.Length && Enum.TryParse(args[i + 2], true, out BotScenario scenario))
            {
                _scenario = scenario;
            }

            if (i + 3 < args.Length && Enum.TryParse(args[i + 3], true, out MapKind map))
            {
                _map = map;
            }
        }

        private void LateUpdate()
        {
            if (_done)
            {
                return;
            }

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed > Timeout)
            {
                Finish(false, $"timeout (state {Client.State}, settings {Client.Settings})");
                return;
            }

            if (Client.State != ClientState.Joined || !Client.Settings.HasValue)
            {
                return;
            }

            if (!_requested)
            {
                var s = Client.Settings.Value;
                s.BotCount = _bots;
                s.Scenario = _scenario;
                s.Map = _map;
                Client.SendSettings(s);
                _requested = true;
                Debug.Log($"[AUTOTEST] requested {_bots} bots {_scenario} on {_map}");
                return;
            }

            var applied = Client.Settings.Value;
            if (applied.BotCount != _bots || applied.Scenario != _scenario || applied.Map != _map)
            {
                return; // 아직 서버 응답 전
            }

            if (_observeStart < 0)
            {
                _observeStart = _elapsed;
                Debug.Log($"[AUTOTEST] server applied: {applied}");
                return;
            }

            _maxVisible = Math.Max(_maxVisible, Views.InterpolatedPositions.Count);
            if (Client.Stats is { } stats && stats.UnitCount >= _bots)
            {
                _worstSeparation = Math.Min(_worstSeparation, stats.MinSeparationRatio);
                _maxOverlaps = Math.Max(_maxOverlaps, stats.OverlapPairs);
                _maxStepMs = Math.Max(_maxStepMs, stats.StepMs);
                _maxWallHits = Math.Max(_maxWallHits, stats.WallPenetrations);
            }

            if (_elapsed - _observeStart < ObserveSeconds)
            {
                return;
            }

            var expected = _bots + applied.PlayerUnits; // 봇 + 내 유닛 (다른 클라이언트가 있으면 더 많을 수 있음)

            var ok = _maxVisible >= expected && _worstSeparation >= MinSeparationToPass && _maxWallHits == 0;
            Finish(ok,
                $"{_map} {_scenario} {_bots} bots: visible units {_maxVisible} (expected ≥ {expected}), wall hits {_maxWallHits}, " +
                $"worst separation {_worstSeparation:0.000}, max overlapping pairs {_maxOverlaps}, " +
                $"max server step {_maxStepMs:0.00} ms, snapshots {Client.SnapshotsReceived} (delta {Client.DeltaSnapshotsReceived}), " +
                $"incomplete {Client.SnapshotsIncomplete}, no baseline {Client.SnapshotsMissingBaseline}, " +
                $"bandwidth {(Client.Stats?.SnapshotBytesPerSec ?? 0) / 1024f:0.0} KB/s");

        }

        private void Finish(bool ok, string detail)
        {
            _done = true;
            Debug.Log($"AUTOTEST {(ok ? "PASS" : "FAIL")}: {detail}");
            Application.Quit(ok ? 0 : 1);
        }
    }
}
