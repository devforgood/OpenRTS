using System.Numerics;
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
    public Vector2? Target { get; set; }
    public float Speed { get; set; } = SimConstants.UnitSpeed;

    /// <summary>서버가 조종하는 유닛 (이동 동기화 확인용으로 무작위 배회).</summary>
    public bool IsBot { get; init; }
}

/// <summary>
/// 서버 시뮬레이션 상태. 지금은 목표 지점까지 직선 이동만 한다.
/// 경로 탐색(A*)과 RVO2 충돌 회피는 로드맵 2~3단계에서 이 자리에 들어온다.
/// </summary>
public sealed class World
{
    private readonly Dictionary<int, Unit> _units = new();
    private readonly List<UnitState> _stateBuffer = new();
    private readonly Random _random;
    private int _nextId = 1;

    public World(int seed = 1234)
    {
        _random = new Random(seed);
    }

    public IReadOnlyCollection<Unit> Units => _units.Values;

    public Unit Spawn(Vector2 position, bool isBot = false)
    {
        var unit = new Unit(_nextId++, ClampToMap(position)) { IsBot = isBot };
        _units.Add(unit.Id, unit);
        return unit;
    }

    public Unit SpawnRandom(bool isBot = false) => Spawn(RandomPoint(SimConstants.MapHalfSize * 0.5f), isBot);

    public bool Remove(int unitId) => _units.Remove(unitId);

    public Unit? Find(int unitId) => _units.GetValueOrDefault(unitId);

    public void SetMoveTarget(int unitId, Vector2 target)
    {
        if (_units.TryGetValue(unitId, out var unit))
        {
            unit.Target = ClampToMap(target);
        }
    }

    public void Step(float dt)
    {
        foreach (var unit in _units.Values)
        {
            if (unit.IsBot && unit.Target is null)
            {
                unit.Target = RandomPoint(SimConstants.MapHalfSize * 0.8f);
            }

            if (unit.Target is not { } target)
            {
                continue;
            }

            var toTarget = target - unit.Position;
            var distance = toTarget.Length();
            var stepLength = unit.Speed * dt;
            if (distance <= stepLength)
            {
                unit.Position = target;
                unit.Target = null;
            }
            else
            {
                unit.Position += toTarget / distance * stepLength;
            }
        }
    }

    /// <summary>스냅샷 전송용 상태 목록. 반환한 리스트는 다음 호출 때 재사용된다.</summary>
    public IReadOnlyList<UnitState> CaptureState()
    {
        _stateBuffer.Clear();
        foreach (var unit in _units.Values)
        {
            _stateBuffer.Add(new UnitState(unit.Id, unit.Position));
        }

        return _stateBuffer;
    }

    private Vector2 RandomPoint(float halfSize) => new(
        (float)(_random.NextDouble() * 2 - 1) * halfSize,
        (float)(_random.NextDouble() * 2 - 1) * halfSize);

    private static Vector2 ClampToMap(Vector2 p) => Vector2.Clamp(
        p,
        new Vector2(-SimConstants.MapHalfSize),
        new Vector2(SimConstants.MapHalfSize));
}
