using System.Numerics;
using OpenRTS.Server.Simulation.Avoidance;
using OpenRTS.Server.Simulation.Combat;
using OpenRTS.Shared.Combat;
using OpenRTS.Server.Simulation.Navigation;
using OpenRTS.Shared;
using OpenRTS.Shared.Map;
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

    /// <summary>최종 목표. 경로의 마지막 점과 같다.</summary>
    public Vector2? Target { get; private set; }

    /// <summary>소유 플레이어 번호. 0 은 서버 봇.</summary>
    public byte Owner { get; init; }

    /// <summary>서버가 조종하는 유닛 (충돌·동기화 확인용).</summary>
    public bool IsBot { get; init; }

    // ── 전투 ──────────────────────────────────────────────

    /// <summary>팀. 0 은 중립(전투 안 함).</summary>
    public byte Team { get; set; }

    public UnitKind Kind { get; init; }

    public UnitArchetype Stats => UnitArchetypes.Of(Kind);

    public float Hp { get; set; }

    public bool IsDead => Team != UnitArchetypes.NeutralTeam && Hp <= 0;

    public UnitOrder Order { get; set; }

    /// <summary>공격 대상 (없으면 0).</summary>
    public int AttackTargetId { get; set; }

    /// <summary>공격 이동 목적지.</summary>
    public Vector2 Goal { get; set; }

    /// <summary>대기 중 스스로 싸우기 시작한 자리 (리쉬 기준).</summary>
    public Vector2 LeashAnchor { get; set; }

    public float Cooldown { get; set; }

    /// <summary>남은 선딜(초). 0 보다 크면 휘두르는 중.</summary>
    public float Windup { get; set; }

    public int WindupTargetId { get; set; }

    /// <summary>대상을 정하거나 마지막으로 공격을 시작한 뒤 지난 시간.</summary>
    public float TimeSinceAttack { get; set; }

    internal float ChaseTimer { get; set; }
    internal Vector2 ChasePoint { get; set; }

    /// <summary>사격 중지: 스스로 적을 찾거나 반격하지 않는다 (지정 공격 명령만 따른다).</summary>
    public bool HoldFire { get; set; }

    /// <summary>행동 층 AI(<see cref="BattleAi"/>)가 조종하는 유닛.</summary>

    public bool HasBrain { get; init; }

    internal float ThinkTimer { get; set; }
    internal int IgnoreTargetId { get; set; }
    internal float IgnoreTimer { get; set; }

    /// <summary>현재 경로 (출발점 제외, 마지막 점이 목표).</summary>
    public List<Vector2> Path { get; } = new();

    public int PathIndex { get; internal set; }

    /// <summary>봇이 왕복하는 두 지점 (Wander 는 사용 안 함).</summary>
    public Vector2 HomeA { get; set; }
    public Vector2 HomeB { get; set; }
    public bool HeadingToB { get; set; }
    public float WaitTime { get; set; }

    // 막힘 감지
    internal float BestDistance { get; set; } = float.MaxValue;
    internal float NoProgressTime { get; set; }
    internal int RepathCount { get; set; }

    public bool IsMoving => Target is not null;

    public Vector2? Waypoint => Target is null || Path.Count == 0 ? null : Path[Math.Min(PathIndex, Path.Count - 1)];

    public bool OnLastWaypoint => PathIndex >= Path.Count - 1;

    /// <summary>현재 위치에서 경로를 따라 목표까지 남은 거리.</summary>
    public float RemainingPathLength()
    {
        if (Target is null || Path.Count == 0)
        {
            return 0;
        }

        var length = Vector2.Distance(Position, Path[PathIndex]);
        for (var i = PathIndex; i < Path.Count - 1; i++)
        {
            length += Vector2.Distance(Path[i], Path[i + 1]);
        }

        return length;
    }

    internal void SetPath(Vector2 target, List<Vector2> path, bool resetRepaths)
    {
        Target = target;
        Path.Clear();
        Path.AddRange(path);
        if (Path.Count == 0)
        {
            Path.Add(target);
        }

        PathIndex = 0;
        BestDistance = float.MaxValue;
        NoProgressTime = 0;
        if (resetRepaths)
        {
            RepathCount = 0;
        }
    }

    public void Stop()
    {
        // 속도도 0으로: 남겨 두면 다음 틱 ORCA 가 이전 속도를 기준으로 제약을 세워 도착 지점에서 밀려난다.
        Velocity = Vector2.Zero;
        Target = null;

        Path.Clear();
        PathIndex = 0;
    }
}

/// <summary>
/// 서버 시뮬레이션 상태.
/// 틱마다: 봇 목표 갱신 → 경유점 갱신 → 선호 속도(다음 경유점 방향) → ORCA 회피 속도(벽 + 유닛) → 위치 적분
/// → 분리 보정 → 벽 밀어내기 → 도착·막힘(재경로) 판정.
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

    /// <summary>목표에서 먼 곳에서 이 시간 동안 진척이 없으면 경로를 다시 찾는다.</summary>
    public const float RepathTime = 1f;

    /// <summary>이동 명령 하나당 최대 재경로 횟수.</summary>
    public const int MaxRepathsPerMove = 3;

    /// <summary>재경로 때 멈춰 있는 유닛이 차지한 칸 하나를 지나는 추가 비용(거리 단위).</summary>
    public const float OccupiedCellCost = 1.5f;

    public const float NearTargetRadii = 8f;
    public const float ProgressEpsilon = 0.1f;

    /// <summary>봇이 도착 후 다음 목표로 출발하기 전 대기 시간.</summary>
    public const float BotWaitTime = 1f;

    /// <summary>ORCA 계산에 쓰는 반지름 배율. 이산 시간 오차로 살짝 파고드는 것을 막는 여유.</summary>
    public const float OrcaRadiusMargin = 1.05f;

    /// <summary>벽에 대한 ORCA 시간 지평(초). 짧을수록 벽에 가까이 붙어서 지나간다.</summary>
    public const float ObstacleTimeHorizon = 1f;

    /// <summary>
    /// 움직이는 유닛이 멈춰 있는 유닛을 피할 때 맡는 몫 (나머지는 멈춘 유닛이 비켜 준다).
    /// 원본 RVO2 는 항상 0.5.
    /// </summary>
    public const float MovingVsIdleResponsibility = 0.25f;

    /// <summary>분리 보정이 한 번에 푸는 겹침 비율과 반복 횟수.</summary>
    public const float SeparationStrength = 1f;
    public const int SeparationIterations = 2;

    /// <summary>대형 간격 = 지름 × 이 값.</summary>
    public const float FormationSpacing = 1.5f;

    /// <summary>막힘으로 멈출 때 목표와 이만큼(반지름 배율) 가까우면 도착으로 친다 (막힘 횟수에 넣지 않음).</summary>
    public const float CloseEnoughRadii = 1f;

    /// <summary>Battle 에서 한 팀이 전멸한 뒤 다시 시작할 때까지(초).</summary>
    public const float BattleRestartDelay = 5f;

    private readonly List<Unit> _units = new();
    private readonly Dictionary<int, Unit> _byId = new();
    private readonly Dictionary<byte, byte> _players = new(); // 플레이어 → 팀
    private readonly List<int> _dead = new();
    private readonly List<UnitState> _stateBuffer = new();
    private readonly List<Vector2> _positions = new();
    private readonly List<Vector2> _newVelocities = new();
    private readonly List<int> _query = new();
    private readonly List<(float DistSq, int Index)> _nearest = new();
    private readonly List<OrcaNeighbor> _neighbors = new();
    private readonly List<ObstacleVertex> _obstacleNeighbors = new();
    private readonly List<float> _obstacleDistances = new();
    private readonly List<Vector2> _pathBuffer = new();
    private readonly SpatialGrid _grid = new();
    private readonly OrcaSolver _orca = new();
    private readonly Random _random;
    private NavGrid _nav = null!;
    private IReadOnlyList<ObstacleVertex> _obstacles = Array.Empty<ObstacleVertex>();
    private float[]? _occupancy;
    private bool _occupancyFresh;
    private int _nextId = 1;

    public World(int seed = 1234, SimSettings? settings = null)
    {
        _random = new Random(seed);
        Settings = (settings ?? SimSettings.Default).Clamped();
        Combat = new CombatSystem(this);
        Ai = new BattleAi(this);
        BuildMap();
    }

    public CombatSystem Combat { get; }

    public BattleAi Ai { get; }

    /// <summary>현재 틱 번호 (전투 이벤트에 찍는다). 서버가 Step 에 넘기거나, 아니면 1씩 늘어난다.</summary>
    public uint CurrentTick { get; private set; }

    /// <summary>Battle 에서 이긴 팀 (진행 중이면 0).</summary>
    public byte Winner { get; private set; }

    private float _restartTimer;

    public SimSettings Settings { get; private set; }

    public IReadOnlyList<Unit> Units => _units;

    public NavGrid Nav => _nav;

    /// <summary>시나리오 시작 후 막혀서 멈춘 횟수.</summary>
    public int BlockedStops { get; private set; }

    /// <summary>시나리오 시작 후 막혀서 경로를 다시 찾은 횟수.</summary>
    public int Repaths { get; private set; }

    private float Radius => Settings.UnitRadius;

    public Unit Spawn(Vector2 position, bool isBot = false, byte owner = 0, byte team = UnitArchetypes.NeutralTeam, UnitKind kind = UnitKind.Melee, bool brain = false)
    {
        var id = AllocateId();
        var unit = new Unit(id, Standable(position))
        {
            IsBot = isBot,
            Owner = owner,
            Team = team,
            Kind = kind,
            HasBrain = brain,
            ThinkTimer = BattleAi.InitialThinkDelay(id),
        };
        unit.Hp = unit.Stats.MaxHp;
        unit.LeashAnchor = unit.Position;
        _units.Add(unit);
        _byId.Add(unit.Id, unit);
        return unit;
    }

    /// <summary>플레이어의 팀 (없으면 0).</summary>
    public byte TeamOf(byte owner) => _players.GetValueOrDefault(owner);

    /// <summary>맵 중앙 근처 빈 곳에 유닛 하나를 만든다.</summary>
    public Unit SpawnRandom(bool isBot = false, byte owner = 0) => Spawn(FindFreeSpot(SimConstants.MapHalfSize * 0.5f), isBot, owner);

    /// <summary>플레이어를 추가하고 설정된 수만큼 유닛을 만든다. 팀은 인원이 적은 쪽(1 또는 2)으로 정한다.</summary>
    public IReadOnlyList<Unit> AddPlayer(byte owner)
    {
        var team1 = _players.Values.Count(t => t == 1);
        var team2 = _players.Values.Count(t => t == 2);
        _players[owner] = team1 <= team2 ? (byte)1 : (byte)2;
        return SpawnSquad(owner, Settings.PlayerUnits);
    }

    /// <summary>플레이어와 그 유닛을 모두 지운다.</summary>
    public void RemovePlayer(byte owner)
    {
        _players.Remove(owner);
        RemoveWhere(u => u.Owner == owner && !u.IsBot);
    }

    public IEnumerable<Unit> UnitsOf(byte owner) => _units.Where(u => u.Owner == owner && !u.IsBot);

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

    /// <summary>유닛 하나를 목표로 보낸다 (경로 탐색 포함). 소유권은 확인하지 않는다.</summary>
    public void SetMoveTarget(int unitId, Vector2 target)
    {
        if (_byId.TryGetValue(unitId, out var unit))
        {
            MoveUnit(unit, target);
        }
    }

    /// <summary>플레이어 이동 명령 (<see cref="Command"/> 의 Move).</summary>
    public int CommandMove(byte owner, IReadOnlyList<int> unitIds, Vector2 target) =>
        Command(owner, new UnitCommand { Type = CommandType.Move, Target = target, UnitIds = unitIds });

    /// <summary>
    /// 플레이어 명령: <paramref name="owner"/> 가 소유한 유닛만 골라 실행한다.
    /// 이동·공격 이동은 대형을 짜서 각자 칸으로 보낸다. 명령은 하던 공격(선딜 포함)을 취소한다.
    /// </summary>
    /// <returns>실제로 명령을 받은 유닛 수.</returns>
    public int Command(byte owner, UnitCommand command)
    {
        var unitIds = command.UnitIds ?? Array.Empty<int>();
        var target = command.Target;
        var group = new List<Unit>();
        var seen = new HashSet<int>();
        foreach (var id in unitIds)
        {
            if (seen.Add(id) && _byId.TryGetValue(id, out var u) && u.Owner == owner && !u.IsBot)
            {
                group.Add(u);
            }
        }

        foreach (var u in group)
        {
            u.Windup = 0;
            u.AttackTargetId = 0;
        }

        switch (command.Type)
        {
            case CommandType.Stop:
                foreach (var u in group)
                {
                    u.Stop();
                    u.Order = UnitOrder.Idle;
                    u.LeashAnchor = u.Position;
                }

                return group.Count;

            case CommandType.Attack:
                var victim = Find(command.TargetUnit);
                foreach (var u in group)
                {
                    if (Combat.IsValidTarget(u, victim))
                    {
                        u.Order = UnitOrder.Attack;
                        u.AttackTargetId = victim!.Id;
                        u.TimeSinceAttack = 0;
                        u.ChaseTimer = 0;
                    }
                }

                return group.Count;
        }

        target = ClampToMap(target);
        var slots = group.Count == 1
            ? new[] { target }
            : Formation.Assign(group.Select(u => u.Position).ToList(), target, Radius * 2 * FormationSpacing);
        for (var i = 0; i < group.Count; i++)
        {
            var u = group[i];
            u.Order = command.Type == CommandType.AttackMove ? UnitOrder.AttackMove : UnitOrder.Move;
            u.Goal = slots[i];
            u.ChaseTimer = 0;
            MoveUnit(u, slots[i]);
        }

        return group.Count;
    }

    /// <summary>설정을 바꾼다. 맵·봇 수·시나리오·플레이어 유닛 수가 바뀌면 다시 배치한다.</summary>
    /// <returns>실제로 적용된(범위로 자른) 설정.</returns>
    public SimSettings ApplySettings(SimSettings requested)
    {
        var next = requested.Clamped();
        var prev = Settings;
        Settings = next;

        var mapChanged = next.Map != prev.Map;
        if (mapChanged)
        {
            BuildMap();
        }

        if (mapChanged || next.BotCount != prev.BotCount || next.Scenario != prev.Scenario)
        {
            ResetScenario();
        }

        if (mapChanged || next.PlayerUnits != prev.PlayerUnits)
        {
            RespawnPlayers();
        }

        return Settings;
    }

    /// <summary>
    /// 봇을 모두 지우고 현재 시나리오대로 다시 배치한다. 플레이어 유닛은 그대로 두되,
    /// Battle 에서는 플레이어 유닛도 처음 상태(체력 가득, 자기 팀 쪽)로 다시 만든다.
    /// </summary>
    public void ResetScenario()
    {
        RemoveWhere(u => u.IsBot);

        BlockedStops = 0;
        Repaths = 0;
        Winner = 0;
        _restartTimer = 0;
        Combat.Reset();

        if (Settings.Scenario == BotScenario.Battle)
        {
            foreach (var (position, team, kind) in Scenarios.BattleLayout(Settings))
            {
                var bot = Spawn(position, isBot: true, team: team, kind: kind, brain: true);
                bot.Order = UnitOrder.Idle;
            }

            RespawnPlayers();
            return;
        }

        foreach (var (a, b) in Scenarios.Layout(Settings, _random))
        {
            var bot = Spawn(a, isBot: true);
            bot.HomeA = bot.Position;
            bot.HomeB = Standable(b);
            bot.HeadingToB = false;
            bot.WaitTime = 0.5f; // 시작 직후 잠깐 정지 상태를 보여준다
        }
    }

    /// <summary>
    /// 한 틱 진행. 순서: 봇 왕복 → AI(행동 층) → 전투 엔진(대상·추적·선딜·타격) → 경유점 → 이동(선호 속도·ORCA·적분)
    /// → 분리·벽 → 도착·막힘 → 투사체 → 사망 처리 → Battle 승패.
    /// 판단이 이동보다 먼저라 명령이 같은 틱에 반영된다 (참고 서버는 이동이 먼저라 1틱 늦었다).
    /// </summary>
    public void Step(float dt, uint? tick = null)
    {
        CurrentTick = tick ?? CurrentTick + 1;
        var s = Settings;
        _occupancyFresh = false;
        UpdateBots(dt);
        Combat.BeginTick();
        Ai.Update(dt);
        Combat.UpdateUnits(dt);
        UpdateWaypoints();

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

        if (_nav.HasWalls)
        {
            foreach (var u in _units)
            {
                u.Position = PushOutOfWalls(u.Position, s.UnitRadius);
            }
        }

        foreach (var u in _units)
        {
            UpdateArrival(u, dt);
        }

        Combat.UpdateProjectiles(dt);
        Combat.CollectDead(_dead);
        foreach (var id in _dead)
        {
            Remove(id);
        }

        UpdateBattle(dt);
    }

    private void UpdateBattle(float dt)
    {
        if (Settings.Scenario != BotScenario.Battle)
        {
            return;
        }

        if (Winner != 0)
        {
            _restartTimer -= dt;
            if (_restartTimer <= 0)
            {
                ResetScenario();
            }

            return;
        }

        var alive1 = _units.Count(u => u.Team == 1);
        var alive2 = _units.Count(u => u.Team == 2);
        if (alive1 == 0 && alive2 > 0)
        {
            Winner = 2;
        }
        else if (alive2 == 0 && alive1 > 0)
        {
            Winner = 1;
        }

        if (Winner != 0)
        {
            _restartTimer = BattleRestartDelay;
        }
    }

    /// <summary>스냅샷 전송용 상태 목록. 반환한 리스트는 다음 호출 때 재사용된다.</summary>
    public IReadOnlyList<UnitState> CaptureState()
    {
        _stateBuffer.Clear();
        foreach (var unit in _units)
        {
            var target = unit.Windup > 0 ? unit.WindupTargetId : unit.AttackTargetId;
            _stateBuffer.Add(new UnitState
            {
                Id = unit.Id,
                Position = unit.Position,
                Owner = unit.Owner,
                Team = unit.Team,
                Kind = unit.Kind,
                Health = unit.Hp / unit.Stats.MaxHp,
                AttackTarget = target,
                Flags = unit.Windup > 0 ? UnitFlags.WindingUp : UnitFlags.None,
            });
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
        var wallHits = 0;
        for (var i = 0; i < _units.Count; i++)
        {
            if (_units[i].IsMoving)
            {
                moving++;
            }

            if (_nav.HasWalls && _nav.WallDistance(_positions[i]) < r * 0.95f)
            {
                wallHits++;
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
            OverlapPairs = Saturate(overlaps),
            MinSeparationRatio = minRatio == float.MaxValue ? 1f : Math.Min(minRatio, 1f),
            MovingUnits = (ushort)moving,
            BlockedStops = Saturate(BlockedStops),
            Repaths = Saturate(Repaths),
            WallPenetrations = Saturate(wallHits),
            Team1Alive = Saturate(_units.Count(u => u.Team == 1)),
            Team2Alive = Saturate(_units.Count(u => u.Team == 2)),
            Deaths = Saturate(Combat.Deaths),
            Projectiles = Saturate(Combat.ProjectileCount),
            Winner = Winner,
        };
    }

    private static ushort Saturate(int v) => (ushort)Math.Min(v, ushort.MaxValue);

    private void BuildMap()
    {
        var walls = MapLayouts.Create(Settings.Map);
        _nav = new NavGrid(walls);
        var obstacles = new List<ObstacleVertex>();
        foreach (var w in walls)
        {
            obstacles.AddRange(ObstacleVertex.CreatePolygon(w.CounterClockwise()));
        }

        _obstacles = obstacles;
        _occupancy = null;
    }

    private void RespawnPlayers()
    {
        foreach (var owner in _players.Keys.ToList())
        {
            RemoveWhere(u => u.Owner == owner && !u.IsBot);
            SpawnSquad(owner, Settings.PlayerUnits);
        }
    }

    /// <summary>플레이어 유닛 무리. 근접·원거리를 번갈아 만든다. Battle 에서는 자기 팀 진영(1팀 왼쪽, 2팀 오른쪽)에 놓는다.</summary>
    private IReadOnlyList<Unit> SpawnSquad(byte owner, int count)
    {
        var team = TeamOf(owner);
        var spacing = Radius * 2 * 1.5f;
        var center = Settings.Scenario == BotScenario.Battle
            ? Standable(new Vector2(team == 1 ? -38 : 38, (float)(_random.NextDouble() * 2 - 1) * 20))
            : FindFreeSpot(SimConstants.MapHalfSize * 0.5f);
        var result = new List<Unit>(count);
        var k = 0;
        foreach (var offset in Scenarios.Block(count, spacing))
        {
            var kind = k++ % 2 == 0 ? UnitKind.Melee : UnitKind.Ranged;
            var unit = Spawn(center + offset, owner: owner, team: team, kind: kind);
            unit.Order = UnitOrder.Idle;
            result.Add(unit);
        }

        return result;
    }

    private void RemoveWhere(Predicate<Unit> match)
    {
        for (var i = _units.Count - 1; i >= 0; i--)
        {
            if (match(_units[i]))
            {
                _byId.Remove(_units[i].Id);
                _units.RemoveAt(i);
            }
        }
    }

    internal void MoveUnit(Unit u, Vector2 target, bool repath = false)
    {
        target = Standable(ClampToMap(target));
        _nav.FindPath(u.Position, target, Radius, _pathBuffer, repath ? Occupancy() : null);
        var goal = _pathBuffer.Count > 0 ? _pathBuffer[^1] : target;
        u.SetPath(goal, _pathBuffer, resetRepaths: !repath);
    }

    /// <summary>앞의 경유점이 직선으로 보이면 지금 경유점을 건너뛴다 (회피로 경로에서 밀려났을 때 되돌아가지 않게).</summary>
    private void UpdateWaypoints()
    {
        foreach (var u in _units)
        {
            if (!u.IsMoving)
            {
                continue;
            }

            while (!u.OnLastWaypoint)
            {
                var next = u.Path[u.PathIndex + 1];
                var reached = Vector2.Distance(u.Position, u.Path[u.PathIndex]) <= Math.Max(Radius, _nav.CellSize);
                if (!reached && !_nav.HasLineOfSight(u.Position, next, Radius))
                {
                    break;
                }

                u.PathIndex++;
            }
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
                // ORCA 와 같은 몫으로 나눈다 (도착하는 유닛이 자리 잡은 유닛을 밀지 않게).
                var push = dir * ((combined - dist) * SeparationStrength);
                var share = Responsibility(_units[i], _units[j]);
                _newVelocities[i] += push * share;
                _newVelocities[j] -= push * (1f - share);
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

    /// <summary>벽과 겹친 원을 가장 가까운 벽 바깥으로 밀어낸다 (ORCA·분리 보정 뒤 최종 안전장치).</summary>
    private Vector2 PushOutOfWalls(Vector2 p, float radius)
    {
        foreach (var w in _nav.Walls)
        {
            if (w.Contains(p))
            {
                // 안쪽: 가장 가까운 변 밖으로
                var left = p.X - w.Min.X;
                var right = w.Max.X - p.X;
                var down = p.Y - w.Min.Y;
                var up = w.Max.Y - p.Y;
                var min = MathF.Min(MathF.Min(left, right), MathF.Min(down, up));
                if (min == left)
                {
                    p.X = w.Min.X - radius;
                }
                else if (min == right)
                {
                    p.X = w.Max.X + radius;
                }
                else if (min == down)
                {
                    p.Y = w.Min.Y - radius;
                }
                else
                {
                    p.Y = w.Max.Y + radius;
                }

                continue;
            }

            var q = w.ClosestPoint(p);
            var d = p - q;
            var dist = d.Length();
            if (dist < radius && dist > 1e-6f)
            {
                p = q + d / dist * radius;
            }
        }

        return ClampToMap(p);
    }

    private void UpdateBots(float dt)
    {
        foreach (var u in _units)
        {
            if (!u.IsBot || u.HasBrain || u.IsMoving)
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
                MoveUnit(u, RandomPoint(SimConstants.MapHalfSize * 0.8f));
            }
            else
            {
                u.HeadingToB = !u.HeadingToB;
                MoveUnit(u, u.HeadingToB ? u.HomeB : u.HomeA);
            }
        }
    }

    private static Vector2 PreferredVelocity(Unit u, float speed, float dt)
    {
        if (u.Waypoint is not { } waypoint)
        {
            return Vector2.Zero;
        }

        var to = waypoint - u.Position;
        var dist = to.Length();
        if (dist < 1e-6f)
        {
            return Vector2.Zero;
        }

        // 마지막 경유점(목표)에서만 이번 틱에 지나치지 않도록 감속
        return to / dist * (u.OnLastWaypoint ? MathF.Min(speed, dist / dt) : speed);
    }

    private Vector2 Avoid(int index, Vector2 pref, float dt)
    {
        var s = Settings;
        var self = _units[index];
        var radius = s.UnitRadius * OrcaRadiusMargin;

        // 가까운 순으로 최대 MaxNeighbors 개
        _query.Clear();
        _grid.Query(self.Position, s.NeighborDist, _query);
        _nearest.Clear();
        foreach (var j in _query)
        {
            // 공격하러 다가가는 대상은 피하지 않는다 — 피하면 시간 지평 때문에 다가갈수록 느려져
            // 사거리에 닿기까지 몇 초가 걸린다. 실제 겹침은 분리 보정이 막는다.
            if (j != index && (self.AttackTargetId == 0 || _units[j].Id != self.AttackTargetId))
            {
                _nearest.Add((Vector2.DistanceSquared(self.Position, _positions[j]), j));
            }
        }


        _nearest.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        _neighbors.Clear();
        for (var k = 0; k < _nearest.Count && k < s.MaxNeighbors; k++)
        {
            var other = _units[_nearest[k].Index];
            _neighbors.Add(new OrcaNeighbor(other.Position, other.Velocity, radius, Responsibility(self, other)));
        }

        if (_obstacles.Count == 0)
        {
            return _orca.ComputeNewVelocity(self.Position, self.Velocity, pref, radius, s.UnitSpeed, _neighbors, s.TimeHorizon, dt);
        }

        OrcaSolver.FindObstacleNeighbors(self.Position, ObstacleTimeHorizon * s.UnitSpeed + radius, _obstacles, _obstacleNeighbors, _obstacleDistances);
        return _orca.ComputeNewVelocity(self.Position, self.Velocity, pref, radius, s.UnitSpeed, _neighbors, s.TimeHorizon, dt, _obstacleNeighbors, ObstacleTimeHorizon);
    }

    /// <summary>
    /// self 가 other 와의 충돌을 피하는 데 맡는 몫. 두 유닛이 서로 계산한 값의 합이 항상 1 이 되게 한다.
    /// <list type="bullet">
    /// <item>둘 다 움직이거나 둘 다 멈춤: 반씩 (원본 RVO2).</item>
    /// <item>지나가는 유닛 ↔ 멈춘 유닛: 멈춘 유닛이 대부분 비켜 준다 (<see cref="MovingVsIdleResponsibility"/>).</item>
    /// <item>도착 직전 유닛 ↔ 멈춘 유닛: 도착하는 쪽이 전부 피한다. 먼저 도착해 자리 잡은 대형 유닛을 밀어내지 않게.</item>
    /// </list>
    /// </summary>
    private float Responsibility(Unit self, Unit other)
    {
        if (self.IsMoving == other.IsMoving)
        {
            return 0.5f;
        }

        var mover = self.IsMoving ? self : other;
        var moverShare = IsArriving(mover) ? 1f : MovingVsIdleResponsibility;
        return self.IsMoving ? moverShare : 1f - moverShare;
    }

    private bool IsArriving(Unit u) =>
        u.Target is { } t && u.OnLastWaypoint && Vector2.DistanceSquared(u.Position, t) <= Square(Settings.UnitRadius * NearTargetRadii);

    private static float Square(float v) => v * v;

    private void UpdateArrival(Unit u, float dt)

    {
        if (u.Target is not { } target)
        {
            return;
        }

        var direct = Vector2.Distance(u.Position, target);
        if (direct <= ArrivalDistance)
        {
            Arrive(u);
            return;
        }

        // 진척은 경로를 따라 남은 거리로 잰다 (벽을 돌아가는 동안 직선거리는 늘어날 수 있다).
        var remaining = u.RemainingPathLength();
        if (remaining < u.BestDistance - ProgressEpsilon)
        {
            u.BestDistance = remaining;
            u.NoProgressTime = 0;
            return;
        }

        u.NoProgressTime += dt;
        var near = direct <= Settings.UnitRadius * NearTargetRadii;

        if (!near && Settings.RepathWhenBlocked && u.NoProgressTime >= RepathTime && u.RepathCount < MaxRepathsPerMove)
        {
            var count = u.RepathCount + 1;
            MoveUnit(u, target, repath: true);
            u.RepathCount = count;
            Repaths++;
            return;
        }

        var limit = near ? BlockedTime : BlockedTimeFar;
        if (Settings.StopWhenBlocked && u.NoProgressTime >= limit)
        {
            if (direct > Settings.UnitRadius * CloseEnoughRadii)
            {
                BlockedStops++;
            }

            Arrive(u);
        }

    }

    /// <summary>멈춰 있는 유닛이 차지한 칸의 추가 비용 (틱마다 처음 필요할 때 한 번 만든다).</summary>
    private float[] Occupancy()
    {
        if (_occupancy == null || _occupancy.Length != _nav.CellCount)
        {
            _occupancy = new float[_nav.CellCount];
        }

        if (_occupancyFresh)
        {
            return _occupancy;
        }

        Array.Clear(_occupancy);
        var reach = Radius + _nav.CellSize;
        foreach (var u in _units)
        {
            if (u.IsMoving)
            {
                continue;
            }

            var min = u.Position - new Vector2(reach);
            var max = u.Position + new Vector2(reach);
            var c0 = _nav.CellIndex(min);
            var c1 = _nav.CellIndex(max);
            for (var y = c0 / _nav.Width; y <= c1 / _nav.Width; y++)
            {
                for (var x = c0 % _nav.Width; x <= c1 % _nav.Width; x++)
                {
                    _occupancy[y * _nav.Width + x] += OccupiedCellCost;
                }
            }
        }

        _occupancyFresh = true;
        return _occupancy;
    }

    private static void Arrive(Unit u)
    {
        u.Stop();
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
        var minDist = Radius * 2.2f;
        var best = Vector2.Zero;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            best = RandomPoint(halfSize);
            if (_nav.Clearance(best) >= Radius * 4 && _units.TrueForAll(u => Vector2.Distance(u.Position, best) >= minDist))
            {
                break;
            }
        }

        return Standable(best);
    }

    private Vector2 Standable(Vector2 p) => _nav.NearestStandable(ClampToMap(p), Radius);

    private Vector2 RandomPoint(float halfSize) => new(
        (float)(_random.NextDouble() * 2 - 1) * halfSize,
        (float)(_random.NextDouble() * 2 - 1) * halfSize);

    internal static Vector2 ClampToMap(Vector2 p) => Vector2.Clamp(
        p,
        new Vector2(-SimConstants.MapHalfSize),
        new Vector2(SimConstants.MapHalfSize));
}
