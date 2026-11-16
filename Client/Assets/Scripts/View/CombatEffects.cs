using System.Collections.Generic;
using OpenRTS.Client.Net;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Protocol;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.View
{
    /// <summary>
    /// 전투 이벤트 연출. 서버 판정을 흉내 내지 않고 보이기만 한다 (체력·사망은 스냅샷이 권위).
    /// <list type="bullet">
    /// <item>투사체: 공격자 → 대상(움직이면 따라감)으로 공격자 종류의 투사체 속도로 날아가는 작은 구. 닿으면 대상이 번쩍.</item>
    /// <item>근접 타격: 대상이 번쩍.</item>
    /// <item>사망: 그 자리에 잠깐 남는 납작한 원.</item>
    /// </list>
    /// 이벤트는 GameClient 가 렌더 시각(보간 지연 반영)에 맞춰 넘겨주므로 화면 위치와 맞는다.
    /// </summary>
    public sealed class CombatEffects : MonoBehaviour
    {
        private const float DeathMarkSeconds = 1.2f;
        private const float MaxFlightSeconds = 2f;

        public GameClient Client;
        public UnitViewManager Views;

        private struct Flight
        {
            public Transform Ball;
            public Vector2 Position;
            public int Target;
            public float Speed;
            public float Age;
            public Vector2 LastTargetPos;
        }

        private struct Mark
        {
            public Transform Disc;
            public float Age;
        }

        private readonly List<Flight> _flights = new List<Flight>();
        private readonly List<Mark> _marks = new List<Mark>();
        private readonly Stack<Transform> _ballPool = new Stack<Transform>();
        private Material _ballMaterial;
        private Material _markMaterial;

        public int ActiveProjectiles => _flights.Count;

        private void Update()
        {
            if (Client == null || Views == null)
            {
                return;
            }

            foreach (var e in Client.DueCombatEvents)
            {
                Play(e);
            }

            Client.DueCombatEvents.Clear();
            UpdateFlights(Time.deltaTime);
            UpdateMarks(Time.deltaTime);
        }

        private void Play(in CombatEvent e)
        {
            var positions = Views.InterpolatedPositions;
            switch (e.Type)
            {
                case CombatEventType.MeleeHit:
                    Views.Flash(e.Target);
                    break;

                case CombatEventType.ProjectileLaunch:
                    if (positions.TryGetValue(e.Attacker, out var from) && positions.TryGetValue(e.Target, out var to))
                    {
                        _flights.Add(new Flight
                        {
                            Ball = TakeBall(),
                            Position = from,
                            Target = e.Target,
                            Speed = UnitArchetypes.Ranged.ProjectileSpeed,
                            LastTargetPos = to,
                        });
                    }

                    break;

                case CombatEventType.Death:
                    if (positions.TryGetValue(e.Target, out var at))
                    {
                        AddMark(at);
                    }

                    break;
            }
        }

        private void UpdateFlights(float dt)
        {
            var height = Views.Radius * 1.6f;
            for (var i = _flights.Count - 1; i >= 0; i--)
            {
                var f = _flights[i];
                f.Age += dt;
                if (Views.InterpolatedPositions.TryGetValue(f.Target, out var target))
                {
                    f.LastTargetPos = target;
                }

                var to = f.LastTargetPos - f.Position;
                var dist = to.Length();
                var step = f.Speed * dt;
                if (dist <= step + Views.Radius || f.Age > MaxFlightSeconds)
                {
                    Views.Flash(f.Target);
                    ReturnBall(f.Ball);
                    _flights.RemoveAt(i);
                    continue;
                }

                f.Position += to / dist * step;
                f.Ball.position = UnitViewManager.ToWorld(f.Position, height);
                _flights[i] = f;
            }
        }

        private void UpdateMarks(float dt)
        {
            for (var i = _marks.Count - 1; i >= 0; i--)
            {
                var m = _marks[i];
                m.Age += dt;
                if (m.Age > DeathMarkSeconds)
                {
                    Destroy(m.Disc.gameObject);
                    _marks.RemoveAt(i);
                    continue;
                }

                var s = Views.Radius * 2 * Mathf.Lerp(1.4f, 0.6f, m.Age / DeathMarkSeconds);
                m.Disc.localScale = new Vector3(s, 0.01f, s);
                _marks[i] = m;
            }
        }

        private void AddMark(Vector2 at)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Death";
            Destroy(go.GetComponent<Collider>());
            _markMaterial ??= new Material(go.GetComponent<Renderer>().sharedMaterial) { color = new Color(0.25f, 0.05f, 0.05f) };
            go.GetComponent<Renderer>().sharedMaterial = _markMaterial;
            go.transform.position = UnitViewManager.ToWorld(at, 0.02f);
            _marks.Add(new Mark { Disc = go.transform });
        }

        private Transform TakeBall()
        {
            if (_ballPool.Count > 0)
            {
                var pooled = _ballPool.Pop();
                pooled.gameObject.SetActive(true);
                return pooled;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Projectile";
            Destroy(go.GetComponent<Collider>());
            _ballMaterial ??= new Material(go.GetComponent<Renderer>().sharedMaterial) { color = new Color(1f, 0.9f, 0.4f) };
            go.GetComponent<Renderer>().sharedMaterial = _ballMaterial;
            go.transform.localScale = Vector3.one * 0.25f;
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        private void ReturnBall(Transform ball)
        {
            ball.gameObject.SetActive(false);
            _ballPool.Push(ball);
        }
    }
}
