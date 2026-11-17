using System.Collections.Generic;
using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using OpenRTS.Shared.Abilities;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Protocol;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.Controls
{
    /// <summary>
    /// 능력 시전의 즉시 표시 (판정 없는 연출 예측). 시전 명령을 보내는 그 프레임에:
    /// <list type="bullet">
    /// <item>시전할 유닛을 서버와 같은 규칙으로 골라(준비된 유닛, SingleCaster 면 대상에 가장 가까운 하나)</item>
    /// <item>시전 자세(코 보라색)·시전 막대·조준 표시(범위 원 / 직선 조준선 / 자기 주변 고리)를 바로 보여 주고</item>
    /// <item>재사용 대기를 미리 걸어 버튼을 끈다 (확인 전에 다시 눌러 시전이 다시 시작되지 않게).</item>
    /// </list>
    /// 서버의 시전 시작(CastStart)이 렌더 시각에 재생되면 예측을 내리고 서버 연출이 이어받는다.
    /// 서버가 거부하면(서버는 거부한 시전 명령에 CastCancel 을 보냄) 받은 즉시, 이벤트가 유실되면 제한 시간(왕복 + 보간 지연 + 선딜 + 여유) 뒤에
    /// 예측을 거두고 미리 건 재사용 대기를 푼다.
    ///
    /// 사거리 밖이라 서버가 먼저 걸어가게 하는 시전(유닛 대상, 지점 범위)은 자세를 예측하지 않는다
    /// (걸어가는 동안 시전 자세가 보이면 틀린 연출이므로) — 명령 표시만 나온다.
    /// </summary>
    public sealed class CastPrediction : MonoBehaviour
    {
        /// <summary>확인 대기 제한에 더하는 여유(초).</summary>
        public const float ConfirmSlack = 0.5f;

        public GameClient Client;
        public UnitViewManager Views;

        private sealed class Prediction
        {
            public int Unit;
            public AbilityId Ability;
            public AbilityDefinition Definition;
            public Vector2 Point;
            public float Started;
            public float Deadline;
            public Transform Indicator;
            public Transform Bar;
            public Transform BarFill;
        }

        private readonly List<Prediction> _active = new List<Prediction>();
        private readonly List<int> _casters = new List<int>();
        private Material _indicatorMaterial;
        private Material _barMaterial;
        private Material _barBackMaterial;

        public int Confirmed { get; private set; }
        public int Rejected { get; private set; }

        /// <summary>마지막 확인까지 걸린 시간(초, 보낸 순간 → 서버 시전 시작이 화면에 재생).</summary>
        public float LastConfirmSeconds { get; private set; }

        public bool IsPredicting(int unitId)
        {
            foreach (var p in _active)
            {
                if (p.Unit == unitId)
                {
                    return true;
                }
            }

            return false;
        }

        public int ActiveCount => _active.Count;

        private void Start()
        {
            Client.AbilityEventDue += OnDue;
            Client.AbilityEventReceived += OnReceived;
        }

        private void OnDestroy()
        {
            if (Client != null)
            {
                Client.AbilityEventDue -= OnDue;
                Client.AbilityEventReceived -= OnReceived;
            }
        }

        /// <summary>시전 명령을 보내면서 예측을 시작한다. 입력·명령 패널·자동 확인이 모두 이 길을 쓴다.</summary>
        /// <param name="force">true 면 로컬 재사용 대기를 무시하고 보낸다 (거부 처리를 확인하는 자동 확인 전용).</param>
        public void Cast(IReadOnlyList<int> selection, AbilityId ability, Vector2 point, int targetUnit, bool force = false)
        {
            if (!AbilityCatalog.TryGet(ability, out var def))
            {
                return;
            }

            var aim = targetUnit != 0 && Views.InterpolatedPositions.TryGetValue(targetUnit, out var tp) ? tp : point;
            PickCasters(selection, def, aim, force);
            Client.SendCast(selection, ability, point, targetUnit);

            var hold = Client.RoundTripSeconds + Client.Interpolator.InterpolationDelay + def.CastTime + ConfirmSlack;
            foreach (var unit in _casters)
            {
                Client.PredictCooldown(unit, ability, def.Cooldown, hold);
                if (!Views.InterpolatedPositions.TryGetValue(unit, out var pos) || NeedsApproach(def, pos, aim))
                {
                    continue; // 걸어가는 시전은 자세를 예측하지 않는다
                }

                Remove(unit, rejected: false); // 같은 유닛의 이전 예측은 새 시전이 대신한다
                var p = new Prediction
                {
                    Unit = unit,
                    Ability = ability,
                    Definition = def,
                    Point = aim,
                    Started = Time.realtimeSinceStartup,
                    Deadline = Time.realtimeSinceStartup + hold,
                };
                CreateVisuals(p, pos);
                _active.Add(p);
            }
        }

        /// <summary>서버와 같은 규칙으로 시전할 유닛을 고른다 (클라이언트가 아는 위치·재사용 대기 기준).</summary>
        private void PickCasters(IReadOnlyList<int> selection, AbilityDefinition def, Vector2 aim, bool force)
        {
            _casters.Clear();
            var best = -1;
            var bestDist = float.MaxValue;
            foreach (var id in selection)
            {
                if (!Client.Latest.TryGetValue(id, out var s) || s.Owner != Client.PlayerId || (s.Flags & UnitFlags.Stunned) != 0)
                {
                    continue;
                }

                if (!Has(AbilityCatalog.LoadoutOf(s.Kind), def.Id) || (!force && Client.CooldownOf(id, def.Id) > 0))
                {
                    continue;
                }

                if (!def.SingleCaster)
                {
                    _casters.Add(id);
                    continue;
                }

                var d = Vector2.DistanceSquared(Views.InterpolatedPositions.TryGetValue(id, out var p) ? p : s.Position, aim);
                if (d < bestDist)
                {
                    best = id;
                    bestDist = d;
                }
            }

            if (def.SingleCaster && best >= 0)
            {
                _casters.Add(best);
            }
        }

        /// <summary>서버 AbilitySystem.InCastRange 와 같은 판단 (위치는 화면 기준이라 경계에서는 다를 수 있음).</summary>
        private bool NeedsApproach(AbilityDefinition def, Vector2 casterPos, Vector2 aim)
        {
            switch (def.Targeting)
            {
                case AbilityTargeting.None:
                    return false;
                case AbilityTargeting.Unit:
                    return Vector2.Distance(casterPos, aim) - 2 * Views.Radius > def.Range;
                default:
                    return def.Delivery == AbilityDelivery.Area && Vector2.Distance(casterPos, aim) - Views.Radius > def.Range;
            }
        }

        private void Update()
        {
            var now = Time.realtimeSinceStartup;
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                var p = _active[i];
                if (now > p.Deadline || !Views.InterpolatedPositions.TryGetValue(p.Unit, out var pos))
                {
                    Client.ClearPredictedCooldown(p.Unit, p.Ability);
                    Remove(p.Unit, rejected: true); // 확인이 오지 않음 → 거부로 본다
                    continue;
                }

                UpdateVisuals(p, pos, now);
            }
        }

        private void OnDue(AbilityEvent e)
        {
            if (e.Type != AbilityEventType.CastStart)
            {
                return;
            }

            ConfirmFor(e);
        }

        private void ConfirmFor(AbilityEvent e)
        {
            foreach (var p in _active)
            {
                if (p.Unit == e.Caster && p.Ability == e.Ability)
                {
                    LastConfirmSeconds = Time.realtimeSinceStartup - p.Started;
                    Confirmed++;
                    Remove(p.Unit, rejected: false); // 서버 연출(시전 중 플래그)이 이어받는다
                    return;
                }
            }

            // 서버가 다른 유닛을 골랐으면(위치 차이) 같은 능력의 예측을 내린다.
            for (var i = 0; i < _active.Count; i++)
            {
                if (_active[i].Ability == e.Ability && Client.IsMine(e.Caster))
                {
                    Confirmed++;
                    Remove(_active[i].Unit, rejected: false);
                    return;
                }
            }
        }

        private void OnReceived(AbilityEvent e)
        {
            // 내 발사·예고를 바로 재생하는 경우: 시전은 이미 끝났으므로 렌더 시각의 CastStart 를 기다리지 않고 확인으로 친다
            if (Client.PlaysImmediately(e))
            {
                ConfirmFor(e);
                return;
            }

            if (e.Type != AbilityEventType.CastCancel)
            {
                return;
            }

            foreach (var p in _active)
            {
                if (p.Unit == e.Caster && p.Ability == e.Ability)
                {
                    // 서버가 거부(또는 시전이 끊김): 받은 즉시 거둔다 (렌더 시각까지 기다리지 않음)
                    Client.ClearPredictedCooldown(p.Unit, p.Ability);
                    Remove(p.Unit, rejected: true);
                    return;
                }
            }
        }

        private void Remove(int unit, bool rejected)
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i].Unit != unit)
                {
                    continue;
                }

                var p = _active[i];
                if (p.Indicator != null) Destroy(p.Indicator.gameObject);
                if (p.Bar != null) Destroy(p.Bar.gameObject);
                _active.RemoveAt(i);
                if (rejected)
                {
                    Rejected++;
                }
            }
        }

        // ── 연출 ──────────────────────────────────────────────

        private void CreateVisuals(Prediction p, Vector2 casterPos)
        {
            var def = p.Definition;
            var color = new Color(0.4f, 0.85f, 1f);
            _indicatorMaterial ??= NewMaterial(color);
            _barMaterial ??= NewMaterial(new Color(0.85f, 0.35f, 1f));
            _barBackMaterial ??= NewMaterial(new Color(0.1f, 0.1f, 0.1f));

            // 조준 표시: 직선 투사체 = 조준선, 지점 범위·돌진 = 도착 원, 자기 주변 범위 = 발밑 큰 고리
            GameObject indicator;
            if (def.Delivery == AbilityDelivery.Projectile && !def.Homing)
            {
                indicator = GameObject.CreatePrimitive(PrimitiveType.Cube);
            }
            else
            {
                indicator = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            }

            indicator.name = $"Predicted {def.Name}";
            Destroy(indicator.GetComponent<Collider>());
            indicator.GetComponent<Renderer>().sharedMaterial = _indicatorMaterial;
            indicator.transform.SetParent(transform, false);
            p.Indicator = indicator.transform;

            // 시전 막대 (선딜이 있을 때만): 머리 위, 카메라를 향함
            if (def.CastTime > 0)
            {
                var bar = new GameObject("Predicted Cast Bar").transform;
                bar.SetParent(transform, false);
                var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(back.GetComponent<Collider>());
                back.GetComponent<Renderer>().sharedMaterial = _barBackMaterial;
                back.transform.SetParent(bar, false);
                var fill = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(fill.GetComponent<Collider>());
                fill.GetComponent<Renderer>().sharedMaterial = _barMaterial;
                fill.transform.SetParent(bar, false);
                bar.localScale = new Vector3(1.4f, 0.12f, 1f);
                p.Bar = bar;
                p.BarFill = fill.transform;
            }

            UpdateVisuals(p, casterPos, Time.realtimeSinceStartup);
        }

        private void UpdateVisuals(Prediction p, Vector2 casterPos, float now)
        {
            var def = p.Definition;
            if (def.Delivery == AbilityDelivery.Projectile && !def.Homing)
            {
                var dir = p.Point - casterPos;
                dir = dir.LengthSquared() > 1e-4f ? Vector2.Normalize(dir) : Vector2.UnitX;
                var mid = casterPos + dir * (def.Range / 2);
                p.Indicator.position = UnitViewManager.ToWorld(mid, 0.05f);
                p.Indicator.rotation = Quaternion.LookRotation(new Vector3(dir.X, 0, dir.Y));
                p.Indicator.localScale = new Vector3(def.ProjectileWidth, 0.02f, def.Range);
            }
            else
            {
                var center = def.Targeting == AbilityTargeting.None ? casterPos : p.Point;
                var radius = def.AreaRadius > 0 ? def.AreaRadius : Views.Radius * 1.5f;
                p.Indicator.position = UnitViewManager.ToWorld(center, 0.05f);
                p.Indicator.localScale = new Vector3(radius * 2, 0.01f, radius * 2);
            }

            if (p.Bar != null)
            {
                var progress = Mathf.Clamp01((now - p.Started) / def.CastTime);
                p.Bar.position = UnitViewManager.ToWorld(casterPos, Views.Radius * 4 + 0.6f);
                if (Views.Camera != null)
                {
                    p.Bar.rotation = Views.Camera.transform.rotation;
                }

                p.BarFill.localScale = new Vector3(progress, 1f, 1f);
                p.BarFill.localPosition = new Vector3((progress - 1f) / 2f, 0, -0.01f);
            }
        }

        private static Material NewMaterial(Color color)
        {
            var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var m = new Material(probe.GetComponent<Renderer>().sharedMaterial) { color = color };
            Destroy(probe);
            return m;
        }

        private static bool Has(IReadOnlyList<AbilityId> list, AbilityId id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] == id)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
