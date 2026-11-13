using System;
using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using OpenRTS.Shared;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.Debugging
{
    /// <summary>
    /// 명령줄 <c>-autotest</c> 로 켜지는 이동 동기화 자동 확인.
    /// 접속 → 내 유닛을 8유닛 옆으로 이동 → 화면(보간) 위치가 목표에 도착하는지, 중간에 튀지 않는지 확인 후 종료.
    /// 결과는 로그에 "AUTOTEST PASS/FAIL" 로 남고 종료 코드 0/1 로 끝난다.
    /// </summary>
    public sealed class MovementAutoTest : MonoBehaviour
    {
        private const float MoveDistance = 8f;
        private const float Timeout = 20f;

        public GameClient Client;
        public UnitViewManager Views;

        private float _elapsed;
        private Vector2? _start;
        private Vector2 _target;
        private Vector2 _last;
        private float _moveStartTime = -1f;
        private float _maxSpeed;
        private int _extrapolatedFrames;
        private int _frames;
        private int _maxOtherUnits;
        private bool _done;

        public static bool IsRequested() => Array.IndexOf(Environment.GetCommandLineArgs(), "-autotest") >= 0;

        private void LateUpdate()
        {
            if (_done)
            {
                return;
            }

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed > Timeout)
            {
                Finish(false, $"timeout (state {Client.State}, start {_start}, last {_last})");
                return;
            }

            if (Client.State != ClientState.Joined ||
                !Views.InterpolatedPositions.TryGetValue(Client.MyUnitId, out var pos))
            {
                return;
            }

            _maxOtherUnits = Math.Max(_maxOtherUnits, Views.InterpolatedPositions.Count - 1);

            if (_start == null)
            {
                // 보간 버퍼가 차고 적응형 지연이 망 상태를 파악할 때까지 기다린 뒤 출발
                if (_elapsed < 3f)
                {
                    return;
                }

                _start = pos;
                var dir = pos.X > 0 ? -1f : 1f; // 맵 안쪽으로
                _target = pos + new Vector2(dir * MoveDistance, 0);
                _last = pos;
                _moveStartTime = _elapsed;
                Client.SendMove(_target);
                Debug.Log($"[AUTOTEST] start {pos} → target {_target}");
                return;
            }

            var dt = Time.unscaledDeltaTime;
            if (dt > 0)
            {
                _maxSpeed = Math.Max(_maxSpeed, Vector2.Distance(pos, _last) / dt);
            }

            _last = pos;
            _frames++;
            if (Client.Interpolator.IsExtrapolating)
            {
                _extrapolatedFrames++;
            }

            var travelTime = MoveDistance / SimConstants.UnitSpeed;
            if (_elapsed - _moveStartTime < travelTime + 1.5f)
            {
                return;
            }

            var error = Vector2.Distance(pos, _target);
            var speedRatio = _maxSpeed / SimConstants.UnitSpeed;
            var ok = error < 0.01f && speedRatio < 1.5f;
            Finish(ok,
                $"final error {error:0.0000}, max on-screen speed {_maxSpeed:0.00} ({speedRatio:0.00}x unit speed), " +
                $"extrapolated {_extrapolatedFrames}/{_frames} frames, snapshots {Client.SnapshotsReceived}, " +
                $"other units seen {_maxOtherUnits}, rtt {Client.NetworkRttMs}+{Client.SimulatedRttMs} ms, loss {Client.SimulatedLossPercent}%, " +
                $"interp delay {Client.Interpolator.InterpolationDelay * 1000:0} ms");
        }

        private void Finish(bool ok, string detail)
        {
            _done = true;
            Debug.Log($"AUTOTEST {(ok ? "PASS" : "FAIL")}: {detail}");
            Application.Quit(ok ? 0 : 1);
        }
    }
}
