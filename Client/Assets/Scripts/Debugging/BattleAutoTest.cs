using System;
using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;
using UnityEngine;

namespace OpenRTS.Client.Debugging
{
    /// <summary>
    /// 명령줄 <c>-autotest-battle [bots] [map]</c> 로 켜지는 전투 자동 확인 (로드맵 4단계 완료 기준).
    /// Battle 시나리오를 요청 → 두 팀이 싸워 한쪽이 전멸할 때까지 지켜보고,
    /// 서버가 승자를 알린 뒤 화면에서도 진 팀 유닛이 모두 사라졌는지, 전투 연출(이벤트·투사체·체력 감소)이 보였는지 확인한다.
    /// </summary>
    public sealed class BattleAutoTest : MonoBehaviour
    {
        private const float Timeout = 200f;
        private const float VisibleCheckSeconds = 2f;

        public GameClient Client;
        public UnitViewManager Views;
        public CombatEffects Effects;

        private ushort _bots = 60;
        private MapKind _map = MapKind.Open;
        private float _elapsed;
        private bool _requested;
        private float _battleStart = -1f;
        private float _winnerSeen = -1f;
        private int _maxProjectiles;
        private bool _sawDamage;
        private int _maxUnits;
        private float _maxStepMs;
        private bool _done;

        public static bool IsRequested() => Array.IndexOf(Environment.GetCommandLineArgs(), "-autotest-battle") >= 0;

        private void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-autotest-battle");
            if (i + 1 < args.Length && ushort.TryParse(args[i + 1], out var bots))
            {
                _bots = bots;
            }

            if (i + 2 < args.Length && Enum.TryParse(args[i + 2], true, out MapKind map))
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
                Finish(false, $"timeout (state {Client.State}, stats team1 {Client.Stats?.Team1Alive} team2 {Client.Stats?.Team2Alive})");
                return;
            }

            if (Client.State != ClientState.Joined || !Client.Settings.HasValue)
            {
                return;
            }

            if (!_requested)
            {
                var s = Client.Settings.Value;
                s.Scenario = BotScenario.Battle;
                s.BotCount = _bots;
                s.Map = _map;
                Client.SendSettings(s);
                _requested = true;
                Debug.Log($"[AUTOTEST] requested battle {_bots} bots on {_map}");
                return;
            }

            var applied = Client.Settings.Value;
            if (applied.Scenario != BotScenario.Battle || applied.BotCount != _bots || applied.Map != _map || Client.Stats is not { } stats)
            {
                return;
            }

            if (_battleStart < 0)
            {
                if (stats.Winner != 0)
                {
                    return; // 이전 판의 결과 — 새 판이 시작될 때까지
                }

                _battleStart = _elapsed;
                Debug.Log($"[AUTOTEST] battle started: {applied}, my team {Client.MyTeam}");
            }

            _maxProjectiles = Math.Max(_maxProjectiles, Effects.ActiveProjectiles);
            _maxUnits = Math.Max(_maxUnits, Views.InterpolatedPositions.Count);
            _maxStepMs = Math.Max(_maxStepMs, stats.StepMs);
            foreach (var u in Client.Latest.Values)
            {
                if (u.Team != 0 && u.Health < 0.999f)
                {
                    _sawDamage = true;
                    break;
                }
            }

            if (stats.Winner == 0)
            {
                return;
            }

            if (_winnerSeen < 0)
            {
                _winnerSeen = _elapsed;
            }

            // 승자 발표 뒤, 보간 지연만큼 지나면 화면에서도 진 팀이 다 사라져야 한다.
            var loser = stats.Winner == 1 ? 2 : 1;
            var loserVisible = 0;
            var winnerVisible = 0;
            foreach (var kv in Views.InterpolatedPositions)
            {
                if (Client.Latest.TryGetValue(kv.Key, out var u))
                {
                    if (u.Team == loser) loserVisible++;
                    if (u.Team == stats.Winner) winnerVisible++;
                }
            }

            if (loserVisible > 0 && _elapsed - _winnerSeen < VisibleCheckSeconds)
            {
                return;
            }

            var ok = loserVisible == 0 && winnerVisible > 0 && Client.CombatEventsReceived > 0 && _maxProjectiles > 0 && _sawDamage;
            Finish(ok,
                $"{_map} battle {_bots} bots: team {stats.Winner} won after {_winnerSeen - _battleStart:0.0}s, " +
                $"visible winner {winnerVisible} / loser {loserVisible}, deaths {stats.Deaths}, " +
                $"combat events {Client.CombatEventsReceived}, max projectiles on screen {_maxProjectiles}, damage seen {_sawDamage}, " +
                $"max units {_maxUnits}, max server step {_maxStepMs:0.00} ms, snapshots {Client.SnapshotsReceived} (delta {Client.DeltaSnapshotsReceived}), " +
                $"no baseline {Client.SnapshotsMissingBaseline}, bandwidth {stats.SnapshotBytesPerSec / 1024f:0.0} KB/s, my team {Client.MyTeam}");
        }

        private void Finish(bool ok, string detail)
        {
            _done = true;
            Debug.Log($"AUTOTEST {(ok ? "PASS" : "FAIL")}: {detail}");
            Application.Quit(ok ? 0 : 1);
        }
    }
}
