using System.Numerics;
using OpenRTS.Server.Simulation.Avoidance;
using OpenRTS.Shared;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Simulation;

public sealed class Unit
{
    public Unit(int id, Vector2 position)
    {
        Id = id;
        Position = position;
    }

    public int Id { get; }
    public Vector2 Position { get; set; }
    public Vector2 Velocity { get; set; }
    public Vector2? Target { get; set; }

    /// <summary>서버가 조종하는 유닛 (충돌·동기화 확인용).</summary>
    public bool IsBot { get; init; }

    /// <summary>봇이 왕복하는 두 지점 (Wander 는 사용 안 함).</summary>
    public Vector2 HomeA { get; set; }
    public Vector2 HomeB { get; set; }
    public bool HeadingToB { get; set; }
    public float WaitTime { get; set; }

    // 막힘 감지
    internal float BestDistance { get; set; } = float.MaxValue;
    internal float NoProgressTime { get; set; }

    public bool IsMoving => Target is not null;

    public void MoveTo(Vector2 target)
    {
        Target = target;
        BestDistance = float.MaxValue;
        NoProgressTime = 0;
    }
}

/// <summary>
/// 서버 시뮬레이션 상태.
/// 틱마다: 봇 목표 갱신 → 선호 속도(목표 방향) → ORCA 회피 속도 → 위치 적분 → 도착·막힘 판정.
/// 경로 탐색(A*)은 로드맵 2단계에서 선호 속도 계산 자리에 들어온다.
/// </summary>
public sealed class World
{
    /// <summary>목표에 이만큼 가까워지면 도착으로 본다.</summary>
    public const float ArrivalDistance = 0.01f;

    /// <summary>
    /// 목표 근처(<see cref="NearTargetRadii"/> × 반지름 안)에서 이 시간 동안 <see cref="ProgressEpsilon"/> 이상
    /// 가까워지지 못하면 막힌 것으로 보고 멈춘다 — 도착 지점이 다른 유닛으로 차 있는 경우.
    /// </summary>
    public const float BlockedTime = 1.5f;

    /// <summary>목표에서 먼 곳(교차 중 정체 등)에서는 훨씬 오래 기다린 뒤에만 포기한다.</summary>
    public const float BlockedTimeFar = 6f;

    public const float NearTargetRadii = 8f;
    public const float ProgressEpsilon = 0.1f;

    /// <summary>봇이 도착 후 다음 목표로 출발하기 전 대기 시간.</summary>
    public const float BotWaitTime = 1f;

    /// <summary>ORCA 계산에 쓰는 반지름 배율. 이산 시간 오차로 살짝 파고드는 것을 막는 여유.</summary>
    public const float OrcaRadiusMargin = 1.05f;

    /// <summary>분리 보정이 한 번에 푸는 겹침 비율과 반복 횟수.</summary>
    public const float SeparationStrength = 1f;
    public const int SeparationIterations = 2;

    private readonly List<Unit> _units = new();
    private readonly Dictionary<int, Unit> _byId = new();
    private readonly List<UnitState> _stateBuffer = new();
    private readonly List<Vector2> _positions = new();
    private readonly List<Vector2> _newVelocities = new();
    private readonly List<int> _query = new();
    private readonly List<(float DistSq, int Index)> _nearest = new();
    private readonly List<OrcaNeighbor> _neighbors = new();
    private readonly SpatialGrid _grid = new();
    private readonly OrcaSolver _orca = new();
    private readonly Random _random;
    private int _nextId = 1;

    public World(int seed = 1234, SimSettings? settings = null)
    {
        _random = new Random(seed);
        Settings = (settings ?? SimSettings.Default).Clamped();
    }

    public SimSettings Settings { get; private set; }

    public IReadOnlyList<Unit> Units => _units;

    /// <summary>시나리오 시작 후 막혀서 멈춘 횟수.</summary>
    public int BlockedStops { get; private set; }

    public Unit Spawn(Vector2 position, bool isBot = false)
    {
        var unit = new Unit(AllocateId(), ClampToMap(position)) { IsBot = isBot };
        _units.Add(unit);
        _byId.Add(unit.Id, unit);
        return unit;
    }

    /// <summary>플레이어 유닛을 맵 중앙 근처 빈 곳에 만든다.</summary>
    public Unit SpawnRandom(bool isBot = false) => Spawn(FindFreeSpot(SimConstants.MapHalfSize * 0.5f), isBot);

    public bool Remove(int unitId)
    {
        if (!_byId.Remove(unitId, out var unit))
        {
            return false;
        }

        _units.Remove(unit);
        return true;
    }

    public Unit? Find(int unitId) => _byId.GetValueOrDefault(unitId);

    public void SetMoveTarget(int unitId, Vector2 target)
    {
        if (_byId.TryGetValue(unitId, out var unit))
        {
            unit.MoveTo(ClampToMap(target));
        }
    }

    /// <summary>설정을 바꾼다. 봇 수나 시나리오가 바뀌면 봇을 다시 배치한다.</summary>
    /// <returns>실제로 적용된(범위로 자른) 설정.</returns>
    public SimSettings ApplySettings(SimSettings requested)
    {
        var next = requested.Clamped();
        var respawn = next.BotCount != Settings.BotCount || next.Scenario != Settings.Scenario;
        Settings = next;
        if (respawn)
        {
            ResetScenario();
        }

        return Settings;
    }

    /// <summary>봇을 모두 지우고 현재 시나리오대로 다시 배치한다. 플레이어 유닛은 그대로 둔다.</summary>
    public void ResetScenario()
    {
        for (var i = _units.Count - 1; i >= 0; i--)
        {
            if (_units[i].IsBot)
            {
                _byId.Remove(_units[i].Id);
                _units.RemoveAt(i);
            }
        }

        BlockedStops = 0;
        foreach (var (a, b) in Scenarios.Layout(Settings, _random))
        {
            var bot = Spawn(a, isBot: true);
            bot.HomeA = bot.Position;
            bot.HomeB = ClampToMap(b);
            bot.HeadingToB = false;
            bot.WaitTime = 0.5f; // 시작 직후 잠깐 정지 상태를 보여준다
        }
    }

    public void Step(float dt)
    {
        var s = Settings;
        UpdateBots(dt);

        // 1. 선호 속도와 회피 속도
        _positions.Clear();
        foreach (var u in _units)
        {
            _positions.Add(u.Position);
        }

        if (s.Avoidance)
        {
            _grid.Build(_positions, s.NeighborDist);
        }

        _newVelocities.Clear();
        for (var i = 0; i < _units.Count; i++)
        {
            var u = _units[i];
            var pref = PreferredVelocity(u, s.UnitSpeed, dt);
            _newVelocities.Add(s.Avoidance ? Avoid(i, pref, dt) : pref);
        }

        // 2. 적용 (모든 유닛의 새 속도를 구한 뒤 한꺼번에 — ORCA 의 상호 가정)
        for (var i = 0; i < _units.Count; i++)
        {
            var u = _units[i];
            var v = _newVelocities[i];
            if (v.LengthSquared() > s.UnitSpeed * s.UnitSpeed)
            {
                v = Vector2.Normalize(v) * s.UnitSpeed;
            }

            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y))
            {
                v = Vector2.Zero;
            }

            u.Velocity = v;
            u.Position = ClampToMap(u.Position + v * dt);
        }

        if (s.Separation)
        {
            for (var k = 0; k < SeparationIterations; k++)
            {
                Separate(s.UnitRadius);
            }
        }

        foreach (var u in _units)
        {
            UpdateArrival(u, dt, s.StopWhenBlocked);
        }
    }

    /// <summary>남은 겹침을 두 유닛이 반씩 나눠 밀어낸다 (한 틱에 <see cref="SeparationStrength"/> 만큼).</summary>
    private void Separate(float radius)
    {
        var combined = 2 * radius;
        _positions.Clear();
        foreach (var u in _units)
        {
            _positions.Add(u.Position);
        }

        _grid.Build(_positions, combined);
        _newVelocities.Clear(); // 변위 누적 버퍼로 재사용
        for (var i = 0; i < _units.Count; i++)
        {
            _newVelocities.Add(Vector2.Zero);
        }

        for (var i = 0; i < _units.Count; i++)
        {
            _query.Clear();
            _grid.Query(_positions[i], combined, _query);
            foreach (var j in _query)
            {
                if (j <= i)
                {
                    continue;
                }

                var d = _positions[i] - _positions[j];
                var dist = d.Length();
                if (dist >= combined)
                {
                    continue;
                }

                // 완전히 겹치면 ID 로 정한 방향으로 민다 (결정적).
                var dir = dist > 1e-5f
                    ? d / dist
                    : new Vector2(MathF.Cos(_units[i].Id * 2.399963f), MathF.Sin(_units[i].Id * 2.399963f));
                var push = dir * ((combined - dist) * 0.5f * SeparationStrength);
                _newVelocities[i] += push;
                _newVelocities[j] -= push;
            }
        }

        for (var i = 0; i < _units.Count; i++)
        {
            if (_newVelocities[i] != Vector2.Zero)
            {
                _units[i].Position = ClampToMap(_units[i].Position + _newVelocities[i]);
            }
        }
    }

    /// <summary>스냅샷 전송용 상태 목록. 반환한 리스트는 다음 호출 때 재사용된다.</summary>
    public IReadOnlyList<UnitState> CaptureState()
    {
        _stateBuffer.Clear();
        foreach (var unit in _units)
        {
            _stateBuffer.Add(new UnitState(unit.Id, unit.Position));
        }

        return _stateBuffer;
    }

    /// <summary>겹침 지표를 계산한다 (매 틱이 아니라 주기적으로 호출).</summary>
    public SimStats ComputeStats()
    {
        var r = Settings.UnitRadius;
        var combined = 2 * r;
        _positions.Clear();
        foreach (var u in _units)
        {
            _positions.Add(u.Position);
        }

        _grid.Build(_positions, combined);
        var overlaps = 0;
        var minRatio = float.MaxValue;
        var moving = 0;
        for (var i = 0; i < _units.Count; i++)
        {
            if (_units[i].IsMoving)
            {
                moving++;
            }

            _query.Clear();
            _grid.Query(_positions[i], combined, _query);
            foreach (var j in _query)
            {
                if (j <= i)
                {
                    continue; // 쌍은 한 번만
                }

                var ratio = Vector2.Distance(_positions[i], _positions[j]) / combined;
                minRatio = Math.Min(minRatio, ratio);
                if (ratio < 0.95f)
                {
                    overlaps++;
                }
            }
        }

        return new SimStats
        {
            UnitCount = (ushort)_units.Count,
            OverlapPairs = (ushort)Math.Min(overlaps, ushort.MaxValue),
            MinSeparationRatio = minRatio == float.MaxValue ? 1f : Math.Min(minRatio, 1f),
            MovingUnits = (ushort)moving,
            BlockedStops = (ushort)Math.Min(BlockedStops, ushort.MaxValue),
        };
    }

    private void UpdateBots(float dt)
    {
        foreach (var u in _units)
        {
            if (!u.IsBot || u.IsMoving)
            {
                continue;
            }

            u.WaitTime -= dt;
            if (u.WaitTime > 0)
            {
                continue;
            }

            if (Settings.Scenario == BotScenario.Wander)
            {
                u.MoveTo(RandomPoint(SimConstants.MapHalfSize * 0.8f));
            }
            else
            {
                u.HeadingToB = !u.HeadingToB;
                u.MoveTo(u.HeadingToB ? u.HomeB : u.HomeA);
            }
        }
    }

    private static Vector2 PreferredVelocity(Unit u, float speed, float dt)
    {
        if (u.Target is not { } target)
        {
            return Vector2.Zero;
        }

        var to = target - u.Position;
        var dist = to.Length();
        if (dist < 1e-6f)
        {
            return Vector2.Zero;
        }

        // 이번 틱에 목표를 지나치지 않도록 감속
        return to / dist * MathF.Min(speed, dist / dt);
    }

    private Vector2 Avoid(int index, Vector2 pref, float dt)
    {
        var s = Settings;
        var self = _units[index];

        // 가까운 순으로 최대 MaxNeighbors 개
        _query.Clear();
        _grid.Query(self.Position, s.NeighborDist, _query);
        _nearest.Clear();
        foreach (var j in _query)
        {
            if (j != index)
            {
                _nearest.Add((Vector2.DistanceSquared(self.Position, _positions[j]), j));
            }
        }

        _nearest.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        _neighbors.Clear();
        for (var k = 0; k < _nearest.Count && k < s.MaxNeighbors; k++)
        {
            var other = _units[_nearest[k].Index];
            _neighbors.Add(new OrcaNeighbor(other.Position, other.Velocity, s.UnitRadius * OrcaRadiusMargin));
        }

        return _orca.ComputeNewVelocity(self.Position, self.Velocity, pref, s.UnitRadius * OrcaRadiusMargin, s.UnitSpeed, _neighbors, s.TimeHorizon, dt);
    }

    private void UpdateArrival(Unit u, float dt, bool stopWhenBlocked)
    {
        if (u.Target is not { } target)
        {
            return;
        }

        var dist = Vector2.Distance(u.Position, target);
        if (dist <= ArrivalDistance)
        {
            Arrive(u);
            return;
        }

        if (dist < u.BestDistance - ProgressEpsilon)
        {
            u.BestDistance = dist;
            u.NoProgressTime = 0;
        }
        else
        {
            u.NoProgressTime += dt;
            var limit = dist <= Settings.UnitRadius * NearTargetRadii ? BlockedTime : BlockedTimeFar;
            if (stopWhenBlocked && u.NoProgressTime >= limit)
            {
                BlockedStops++;
                Arrive(u);
            }
        }
    }

    private void Arrive(Unit u)
    {
        u.Target = null;
        u.WaitTime = BotWaitTime;
    }

    private int AllocateId()
    {
        // 스냅샷은 ID 를 16비트로 보낸다. 다 쓰면 1부터 다시 빈 번호를 찾는다.
        while (_byId.ContainsKey(_nextId) || _nextId > SnapshotMessage.MaxUnitId)
        {
            _nextId = _nextId > SnapshotMessage.MaxUnitId ? 1 : _nextId + 1;
        }

        return _nextId++;
    }

    private Vector2 FindFreeSpot(float halfSize)
    {
        var minDist = Settings.UnitRadius * 2.2f;
        var best = Vector2.Zero;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            best = RandomPoint(halfSize);
            if (_units.TrueForAll(u => Vector2.Distance(u.Position, best) >= minDist))
            {
                break;
            }
        }

        return best;
    }

    private Vector2 RandomPoint(float halfSize) => new(
        (float)(_random.NextDouble() * 2 - 1) * halfSize,
        (float)(_random.NextDouble() * 2 - 1) * halfSize);

    internal static Vector2 ClampToMap(Vector2 p) => Vector2.Clamp(
        p,
        new Vector2(-SimConstants.MapHalfSize),
        new Vector2(SimConstants.MapHalfSize));
}
