using System;
using System.Collections.Generic;
using System.Linq;
using OpenRTS.Client.Controls;
using OpenRTS.Client.Net;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.Debugging
{
    /// <summary>
    /// 명령줄 <c>-autotest-economy [map]</c> 로 켜지는 경제 자동 확인 (로드맵 6단계 완료 기준).
    /// Skirmish 를 요청한 뒤 실제 클라이언트 명령으로: 일꾼 채집 → 일꾼 생산 → 병영 건설 → 근접 유닛 생산까지 진행하고,
    /// 각 단계에 걸린 시간을 로그에 남긴다. 화면에서 보이는 상태(스냅샷)와 서버가 보낸 자원(PlayerState)만 쓴다.
    /// </summary>
    public sealed class EconomyAutoTest : MonoBehaviour
    {
        private const float Timeout = 180f;

        public GameClient Client;
        public RtsInput Input;

        private enum Step
        {
            RequestSkirmish,
            WaitBase,
            Gather,
            TrainWorker,
            WaitWorker,
            WaitOreForBarracks,
            WaitBarracks,
            WaitBarracksDone,
            WaitOreForMelee,
            WaitMelee,
            Done,
        }

        private Step _step;
        private float _elapsed;
        private float _stepStart;
        private MapKind _map = MapKind.Open;
        private readonly List<string> _log = new List<string>();
        private int _barracksId;

        public static bool IsRequested() => Array.IndexOf(Environment.GetCommandLineArgs(), "-autotest-economy") >= 0;

        private void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-autotest-economy");
            if (i + 1 < args.Length && Enum.TryParse(args[i + 1], true, out MapKind map))
            {
                _map = map;
            }
        }

        private List<UnitState> Mine(UnitKind kind, bool completedOnly = false) =>
            Client.Latest.Values.Where(u => u.Owner == Client.PlayerId && u.Kind == kind &&
                                            (!completedOnly || (u.Flags & UnitFlags.UnderConstruction) == 0)).ToList();

        private void Next(Step step)
        {
            _log.Add($"{_step} {_elapsed - _stepStart:0.0}s");
            Debug.Log($"[AUTOTEST] {_step} done after {_elapsed - _stepStart:0.0}s (ore {Client.Ore}) → {step}");
            _step = step;
            _stepStart = _elapsed;
        }

        private void LateUpdate()
        {
            if (_step == Step.Done)
            {
                return;
            }

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed > Timeout)
            {
                Finish(false, $"timeout at {_step} (ore {Client.Ore}, workers {Mine(UnitKind.Worker).Count})");
                return;
            }

            if (Client.State != ClientState.Joined || !Client.Settings.HasValue)
            {
                return;
            }

            switch (_step)
            {
                case Step.RequestSkirmish:
                    var s = Client.Settings.Value;
                    s.Scenario = BotScenario.Skirmish;
                    s.Map = _map;
                    Client.SendSettings(s);
                    Client.SendResetScenario(); // 이미 Skirmish 였어도 새 기지로 시작
                    Next(Step.WaitBase);
                    break;

                case Step.WaitBase:
                    if (Client.Settings.Value.Scenario == BotScenario.Skirmish && Client.Settings.Value.Map == _map && Client.HasPlayerState &&
                        Client.Ore == EconomyRules.StartingOre && Client.Queues.Count == 0 && Mine(UnitKind.Barracks).Count == 0 &&
                        Mine(UnitKind.Headquarters).Count == 1 && Mine(UnitKind.Worker).Count == EconomyRules.StartingWorkers &&
                        _elapsed - _stepStart > 1f)
                    {
                        Next(Step.Gather);
                    }

                    break;

                case Step.Gather:
                    GatherAll();
                    Next(Step.TrainWorker);
                    break;

                case Step.TrainWorker:
                    Client.SendCommand(CommandType.Train, new[] { Mine(UnitKind.Headquarters)[0].Id }, default, 0, UnitKind.Worker);
                    Next(Step.WaitWorker);
                    break;

                case Step.WaitWorker:
                    if (Mine(UnitKind.Worker).Count > EconomyRules.StartingWorkers)
                    {
                        Next(Step.WaitOreForBarracks);
                    }

                    break;

                case Step.WaitOreForBarracks:
                    if (Client.Ore >= UnitArchetypes.Barracks.Cost)
                    {
                        var hq = Mine(UnitKind.Headquarters)[0];
                        var inward = hq.Position.X > 0 ? -1f : 1f;
                        var site = hq.Position + new Vector2(inward * 7f, 5f);
                        var workers = Mine(UnitKind.Worker).Select(u => u.Id).ToArray();
                        Client.SendCommand(CommandType.Build, workers, site, 0, UnitKind.Barracks);
                        Next(Step.WaitBarracks);
                    }

                    break;

                case Step.WaitBarracks:
                    var placed = Mine(UnitKind.Barracks);
                    if (placed.Count > 0)
                    {
                        _barracksId = placed[0].Id;
                        Next(Step.WaitBarracksDone);
                    }

                    break;

                case Step.WaitBarracksDone:
                    if (Mine(UnitKind.Barracks, completedOnly: true).Any(u => u.Id == _barracksId))
                    {
                        GatherAll(); // 건설을 마친 일꾼도 다시 캐러
                        Next(Step.WaitOreForMelee);
                    }

                    break;

                case Step.WaitOreForMelee:
                    if (Client.Ore >= UnitArchetypes.Melee.Cost)
                    {
                        Client.SendCommand(CommandType.Train, new[] { _barracksId }, default, 0, UnitKind.Melee);
                        Next(Step.WaitMelee);
                    }

                    break;

                case Step.WaitMelee:
                    if (Mine(UnitKind.Melee).Count > 0)
                    {
                        Next(Step.Done);
                        Finish(true, $"{_map}: {string.Join(", ", _log)}; total {_elapsed:0.0}s, ore {Client.Ore}, " +
                                     $"workers {Mine(UnitKind.Worker).Count}, snapshots {Client.SnapshotsReceived}");
                    }

                    break;
            }
        }

        /// <summary>일꾼마다 본진에서 가까운 광맥을 하나씩 나눠 준다 (같은 광맥에 몰리지 않게).</summary>
        private void GatherAll()
        {
            var hq = Mine(UnitKind.Headquarters)[0];
            var fields = Client.Latest.Values.Where(u => u.Kind == UnitKind.OreField)
                .OrderBy(f => Vector2.Distance(f.Position, hq.Position)).ToList();
            var workers = Mine(UnitKind.Worker);
            for (var i = 0; i < workers.Count && fields.Count > 0; i++)
            {
                var field = fields[i % fields.Count];
                Client.SendCommand(CommandType.Gather, new[] { workers[i].Id }, field.Position, field.Id);
            }
        }

        private void Finish(bool ok, string detail)
        {
            _step = Step.Done;
            Debug.Log($"AUTOTEST {(ok ? "PASS" : "FAIL")}: {detail}");
            Application.Quit(ok ? 0 : 1);
        }
    }
}
