using System.Collections.Generic;
using OpenRTS.Client.Net;
using OpenRTS.Shared.Abilities;
using OpenRTS.Shared.Protocol;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.View
{
    /// <summary>
    /// 능력 연출. 서버가 보낸 능력 이벤트(렌더 시각에 맞춰 재생)만으로 그린다 — 판정은 서버가 한다.
    /// <list type="bullet">
    /// <item>직선 투사체: 시전자 위치에서 끝 지점 쪽으로 정의의 속도로 날아가는 구. 서버의 "투사체 끝" 이벤트가 오면 그 자리에서 사라진다.</item>
    /// <item>유도 투사체: 대상을 따라간다.</item>
    /// <item>범위 예고: 지연 시간 동안 바닥에 붉은 원판. 범위 발동: 짧게 번쩍이는 원판 (회복은 초록, 피해는 주황).</item>
    /// <item>효과가 들어간 유닛: 번쩍.</item>
    /// </list>
    /// 색이 있는 원판은 불투명 기본 재질만 쓴다 (빌드에서 빠지는 투명 셰이더 변형을 피함).
    /// </summary>
    public sealed class AbilityEffects : MonoBehaviour
    {
        private const float FlashSeconds = 0.3f;
        private const float MaxFlightSeconds = 3f;

        public GameClient Client;
        public UnitViewManager Views;

        private struct Flight
        {
            public Transform Ball;
            public int Caster;
            public AbilityId Ability;
            public int Target;
            public Vector2 Position;
            public Vector2 Direction;
            public float Remaining;
            public float Speed;
            public float Age;
        }

        private struct Disc
        {
            public Transform Transform;
            public float Life;
            public float Age;
            public bool Shrink;
            public float Diameter;
        }

        private readonly List<Flight> _flights = new List<Flight>();
        private readonly List<Disc> _discs = new List<Disc>();
        private readonly Dictionary<Color, Material> _materials = new Dictionary<Color, Material>();
        private Material _baseMaterial;

        public int ActiveFlights => _flights.Count;

        /// <summary>시작 후 그린 능력 연출 수 (자동 확인용).</summary>
        public int Played { get; private set; }

        private void Update()
        {
            if (Client == null || Views == null)
            {
                return;
            }

            foreach (var e in Client.DueAbilityEvents)
            {
                Play(e);
            }

            Client.DueAbilityEvents.Clear();
            UpdateFlights(Time.deltaTime);
            UpdateDiscs(Time.deltaTime);
        }

        private void Play(in AbilityEvent e)
        {
            if (!AbilityCatalog.TryGet(e.Ability, out var def))
            {
                return;
            }

            Played++;
            var positions = Views.InterpolatedPositions;
            switch (e.Type)
            {
                case AbilityEventType.ProjectileLaunch:
                    if (e.Caster != 0 && positions.TryGetValue(e.Caster, out var from))
                    {
                        var to = e.Point - from;
                        var length = to.Length();
                        _flights.Add(new Flight
                        {
                            Ball = MakeBall(),
                            Caster = e.Caster,
                            Ability = e.Ability,
                            Target = def.Homing ? e.Target : 0,
                            Position = from,
                            Direction = length > 1e-4f ? to / length : Vector2.UnitX,
                            Remaining = def.Homing ? float.MaxValue : length,
                            Speed = def.ProjectileSpeed,
                        });
                    }

                    break;

                case AbilityEventType.ProjectileEnd:
                    // 같은 시전자·능력의 가장 오래된 투사체를 그 자리에서 끝낸다
                    for (var i = 0; i < _flights.Count; i++)
                    {
                        if (_flights[i].Ability == e.Ability && (e.Caster == 0 || _flights[i].Caster == e.Caster))
                        {
                            Destroy(_flights[i].Ball.gameObject);
                            _flights.RemoveAt(i);
                            break;
                        }
                    }

                    break;

                case AbilityEventType.AreaTelegraph:
                    AddDisc(e.Point, def.AreaRadius, new Color(0.45f, 0.08f, 0.06f), def.AreaDelay, shrink: false);
                    break;

                case AbilityEventType.AreaHit:
                    var heal = def.Effect.Heal > 0;
                    AddDisc(e.Point, def.AreaRadius, heal ? new Color(0.35f, 0.9f, 0.45f) : new Color(1f, 0.6f, 0.2f), FlashSeconds, shrink: true);
                    break;

                case AbilityEventType.Hit:
                    if (e.Target != 0)
                    {
                        Views.Flash(e.Target);
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
                var step = f.Speed * dt;
                if (f.Target != 0 && Views.InterpolatedPositions.TryGetValue(f.Target, out var target))
                {
                    var to = target - f.Position;
                    var dist = to.Length();
                    if (dist <= step)
                    {
                        f.Remaining = 0;
                    }
                    else
                    {
                        f.Direction = to / dist;
                    }
                }

                var move = Mathf.Min(step, f.Remaining);
                f.Position += f.Direction * move;
                f.Remaining -= move;
                if (f.Remaining <= 0 || f.Age > MaxFlightSeconds)
                {
                    Destroy(f.Ball.gameObject);
                    _flights.RemoveAt(i);
                    continue;
                }

                f.Ball.position = UnitViewManager.ToWorld(f.Position, height);
                _flights[i] = f;
            }
        }

        private void UpdateDiscs(float dt)
        {
            for (var i = _discs.Count - 1; i >= 0; i--)
            {
                var d = _discs[i];
                d.Age += dt;
                if (d.Age >= d.Life)
                {
                    Destroy(d.Transform.gameObject);
                    _discs.RemoveAt(i);
                    continue;
                }

                if (d.Shrink)
                {
                    // 번쩍인 뒤 절반 크기까지 줄어든다
                    var size = d.Diameter * (1f - 0.5f * d.Age / d.Life);
                    d.Transform.localScale = new Vector3(size, 0.01f, size);
                }

                _discs[i] = d;
            }
        }

        private void AddDisc(Vector2 center, float radius, Color color, float life, bool shrink)
        {
            if (radius <= 0 || life <= 0)
            {
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Ability Area";
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = MaterialFor(color, go);
            go.transform.SetParent(transform, false);
            go.transform.position = UnitViewManager.ToWorld(center, shrink ? 0.06f : 0.04f);
            go.transform.localScale = new Vector3(radius * 2, 0.01f, radius * 2);
            _discs.Add(new Disc { Transform = go.transform, Life = life, Shrink = shrink, Diameter = radius * 2 });
        }

        private Transform MakeBall()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Ability Projectile";
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = MaterialFor(new Color(0.5f, 0.85f, 1f), go);
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * 0.35f;
            return go.transform;
        }

        private Material MaterialFor(Color color, GameObject template)
        {
            if (!_materials.TryGetValue(color, out var m))
            {
                _baseMaterial ??= template.GetComponent<Renderer>().sharedMaterial;
                m = new Material(_baseMaterial) { color = color };
                _materials[color] = m;
            }

            return m;
        }
    }
}
