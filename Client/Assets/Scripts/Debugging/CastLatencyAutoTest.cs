using System;
using System.Collections.Generic;
using System.Linq;
using OpenRTS.Client.Controls;
using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using OpenRTS.Shared.Abilities;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Protocol;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.Debugging
{
    /// <summary>
    /// 명령줄 <c>-autotest-cast [on|off]</c>: 능력 시전 지연 감추기 확인.
    /// 내 영웅으로 Shock Bolt 를 3번 쓰면서 "보낸 순간"부터
    /// ① 화면에 시전 자세가 보일 때(즉시 표시), ② 서버 시전 시작이 화면에 재생될 때(확인), ③ 투사체가 보일 때(발사)를 잰다.
    /// 마지막으로 재사용 대기 중에 강제로 한 번 더 보내 서버가 거부했을 때 예측이 거둬지는지 확인한다.
    /// on/off 는 서버 선딜 지연 보상(LagCompensation) 설정. 같은 지연(-latency)으로 둘을 돌려 ③ 의 차이를 본다.
    /// </summary>
    public sealed class CastLatencyAutoTest : MonoBehaviour
    {
        private const int Rounds = 4;

        /// <summary>측정에 쓸 최소 라운드 수 (능력 이벤트는 unreliable 이라 손실 시험에서 일부가 빠질 수 있다).</summary>
        private const int MinMeasuredRounds = 2;
        private const float Timeout = 90f;

        /// <summary>즉시 표시로 인정하는 최대 지연 (몇 프레임).</summary>
        private const float MaxPoseSeconds = 0.1f;

        public GameClient Client;
        public UnitViewManager Views;
        public RtsInput Input;
        public CastPrediction Prediction;
        public AbilityEffects Effects;

        private enum Phase
        {
            Setup,
            Settle,
            Ready,
            Waiting,
            Reject,
            Done,
        }

        private Phase _phase;
        private bool _compensation = true;
        private float _elapsed;
        private float _phaseStart;
        private int _hero;
        private int _round;
        private float _sent;
        private float _pose = -1;
        private float _confirm = -1;
        private float _launch = -1;
        private readonly List<(float Pose, float Confirm, float Launch)> _results = new List<(float, float, float)>();
        private int _lostRounds;
        private int _rejectedBefore;
        private bool _serverStartedDuringReject;
        private float _rejectCleared = -1;

        public static bool IsRequested() => Array.IndexOf(Environment.GetCommandLineArgs(), "-autotest-cast") >= 0;

        private void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-autotest-cast");
            if (i + 1 < args.Length && args[i + 1].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _compensation = false;
            }
        }

        private void Start()
        {
            Client.AbilityEventDue += OnDue;
            Effects.ProjectileShown += OnProjectileShown;
        }

        /// <summary>③ 발사: 투사체를 실제로 그리기 시작한 순간 (내 시전은 받은 즉시, 아니면 렌더 시각).</summary>
        private void OnProjectileShown(int caster, AbilityId ability)
        {
            if (caster == _hero && ability == AbilityId.ShockBolt && _phase == Phase.Waiting && _launch < 0)
            {
                _launch = Time.realtimeSinceStartup - _sent;
            }
        }

        private void OnDue(AbilityEvent e)
        {
            if (e.Caster != _hero || e.Ability != AbilityId.ShockBolt)
            {
                return;
            }

            var t = Time.realtimeSinceStartup - _sent;
            if (_phase == Phase.Waiting)
            {
                if (e.Type == AbilityEventType.CastStart && _confirm < 0) _confirm = t;
            }
            else if (_phase == Phase.Reject && e.Type == AbilityEventType.CastStart)
            {
                _serverStartedDuringReject = true;
            }
        }

        private void Next(Phase phase)
        {
            _phase = phase;
            _phaseStart = _elapsed;
        }

        private void LateUpdate()
        {
            if (_phase == Phase.Done)
            {
                return;
            }

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed > Timeout)
            {
                Finish(false, $"timeout at {_phase} round {_round}");
                return;
            }

            if (Client.State != ClientState.Joined || !Client.Settings.HasValue)
            {
                return;
            }

            switch (_phase)
            {
                case Phase.Setup:
                    var s = Client.Settings.Value;
                    s.Scenario = BotScenario.Wander;
                    s.BotCount = 0;
                    s.PlayerUnits = 1; // 영웅 하나
                    s.LagCompensation = _compensation;
                    Client.SendSettings(s);
                    Next(Phase.Settle);
                    break;

                case Phase.Settle:
                    var applied = Client.Settings.Value;
                    var hero = Client.Latest.Values.FirstOrDefault(u => u.Owner == Client.PlayerId && u.Kind == UnitKind.Hero);
                    if (applied.PlayerUnits == 1 && applied.BotCount == 0 && applied.LagCompensation == _compensation && hero.Id != 0
                        && _elapsed - _phaseStart > 2.5f)
                    {
                        _hero = hero.Id;
                        Debug.Log($"[AUTOTEST] hero {_hero}, lag compensation {_compensation}, rtt {Client.NetworkRttMs}+{Client.SimulatedRttMs} ms");
                        Next(Phase.Ready);
                    }

                    break;

                case Phase.Ready:
                    if (_round >= Rounds)
                    {
                        // 마지막 발사 직후, 서버 재사용 대기가 걸린 상태에서 강제로 보낸다 → 서버가 거부 → 예측이 거둬져야 한다
                        if (_elapsed - _phaseStart > 0.8f && Prediction.ActiveCount == 0 && Client.CooldownOf(_hero, AbilityId.ShockBolt) > 1f)
                        {
                            _rejectedBefore = Prediction.Rejected;
                            Fire(force: true);
                            Next(Phase.Reject);
                        }

                        break;
                    }

                    if (Client.CooldownOf(_hero, AbilityId.ShockBolt) > 0 || Prediction.ActiveCount > 0 || _elapsed - _phaseStart < 0.5f
                        || !Client.Latest.TryGetValue(_hero, out var h) || (h.Flags & UnitFlags.Casting) != 0)
                    {
                        break;
                    }

                    Fire(force: false);
                    Next(Phase.Waiting);
                    break;

                case Phase.Waiting:
                    if (_pose < 0 && Views.IsShowingCast(_hero))
                    {
                        _pose = Time.realtimeSinceStartup - _sent;
                    }

                    if (_confirm >= 0 && _launch >= 0)
                    {
                        _results.Add((_pose, _confirm, _launch));
                        Debug.Log($"[AUTOTEST] round {_round + 1}: pose {_pose * 1000:0} ms, confirm {_confirm * 1000:0} ms, launch {_launch * 1000:0} ms");
                        _round++;
                        Next(Phase.Ready);
                    }
                    else if (_elapsed - _phaseStart > Client.RoundTripSeconds + Client.Interpolator.InterpolationDelay + 1.5f)
                    {
                        // 이벤트(unreliable)가 손실됨: 측정에서 빼고 다음 라운드로
                        _lostRounds++;
                        Debug.Log($"[AUTOTEST] round {_round + 1}: event lost (pose {_pose * 1000:0} ms, confirm {_confirm * 1000:0}, launch {_launch * 1000:0})");
                        _round++;
                        Next(Phase.Ready);
                    }

                    break;

                case Phase.Reject:
                    if (_pose < 0 && Views.IsShowingCast(_hero))
                    {
                        _pose = Time.realtimeSinceStartup - _sent;
                    }

                    if (_rejectCleared < 0 && Prediction.Rejected > _rejectedBefore && !Prediction.IsPredicting(_hero))
                    {
                        _rejectCleared = Time.realtimeSinceStartup - _sent;
                    }

                    if (_rejectCleared >= 0 && _elapsed - _phaseStart > 1.5f)
                    {
                        Report();
                    }
                    else if (_elapsed - _phaseStart > 4f)
                    {
                        Report();
                    }

                    break;
            }
        }

        private void Fire(bool force)
        {
            var heroPos = Views.InterpolatedPositions.TryGetValue(_hero, out var p) ? p : Vector2.Zero;
            var dir = heroPos.X > 0 ? -1f : 1f;
            _pose = _confirm = _launch = -1;
            _sent = Time.realtimeSinceStartup;
            Input.CastForTest(new[] { _hero }, AbilityId.ShockBolt, heroPos + new Vector2(dir * 8f, 0), force);
        }

        private void Report()
        {
            var rejectPose = _pose;
            var limit = Client.RoundTripSeconds + Client.Interpolator.InterpolationDelay + AbilityCatalog.Get(AbilityId.ShockBolt).CastTime + CastPrediction.ConfirmSlack + 0.2f;
            var poseOk = _results.Count >= MinMeasuredRounds && _results.All(r => r.Pose >= 0 && r.Pose <= MaxPoseSeconds);
            var rejectOk = _rejectCleared >= 0 && _rejectCleared <= limit && !_serverStartedDuringReject;
            var ok = poseOk && rejectOk && Prediction.ActiveCount == 0;
            Finish(ok,
                $"lag compensation {(_compensation ? "on" : "off")}, own events {(Client.ImmediateOwnEvents ? "immediate" : "delayed")}, " +
                $"rtt {Client.NetworkRttMs}+{Client.SimulatedRttMs} ms, interp delay {Client.Interpolator.InterpolationDelay * 1000:0} ms: " +
                $"pose avg {Avg(r => r.Pose)} ms (max {_results.Max(r => r.Pose) * 1000:0}), " +
                $"server confirm avg {Avg(r => r.Confirm)} ms, projectile visible avg {Avg(r => r.Launch)} ms " +
                $"[{string.Join(" / ", _results.Select(r => $"{r.Launch * 1000:0}"))}], event-lost rounds {_lostRounds}; " +
                $"rejected cast: pose {rejectPose * 1000:0} ms, cleared after {_rejectCleared * 1000:0} ms (limit {limit * 1000:0}), server started {_serverStartedDuringReject}; " +
                $"predictions confirmed {Prediction.Confirmed}, rejected {Prediction.Rejected}, active {Prediction.ActiveCount}");
        }

        private string Avg(Func<(float Pose, float Confirm, float Launch), float> f) =>
            _results.Count == 0 ? "-" : $"{_results.Average(f) * 1000:0}";

        private void Finish(bool ok, string detail)
        {
            _phase = Phase.Done;
            Debug.Log($"AUTOTEST {(ok ? "PASS" : "FAIL")}: {detail}");
            Application.Quit(ok ? 0 : 1);
        }
    }
}
