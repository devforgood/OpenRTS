using System.Numerics;
using OpenRTS.Server.Simulation;
using OpenRTS.Server.Simulation.Combat;
using OpenRTS.Shared;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Tests;

public class EconomyTests
{
    private const byte Player = 1;

    private static World CreateSkirmish()
    {
        var s = SimSettings.Default;
        s.Scenario = BotScenario.Skirmish;
        var world = new World(settings: s);
        world.ResetScenario();
        world.AddPlayer(Player);
        world.Step(SimConstants.TickDelta); // 시야 계산 (안 보이는 곳에는 지을 수 없다)
        return world;
    }

    private static List<Unit> Mine(World w, UnitKind kind) => w.UnitsOf(Player).Where(u => u.Kind == kind).ToList();

    private static int Cmd(World w, CommandType type, IEnumerable<Unit> units, Vector2 target = default, int targetUnit = 0, UnitKind kind = UnitKind.Melee) =>
        w.Command(Player, new UnitCommand { Type = type, Target = target, TargetUnit = targetUnit, Kind = kind, UnitIds = units.Select(u => u.Id).ToArray() });

    private static float RunUntil(World w, Func<bool> condition, float maxSeconds, Action? eachTick = null)
    {
        var t = 0f;
        while (!condition())
        {
            Assert.True(t < maxSeconds, $"not reached within {maxSeconds}s (ore {w.EconomySystem.OreOf(Player)})");
            w.Step(SimConstants.TickDelta);
            eachTick?.Invoke();
            t += SimConstants.TickDelta;
        }

        return t;
    }

    private static void GatherAll(World w)
    {
        var hq = Mine(w, UnitKind.Headquarters).Single();
        var fields = w.Units.Where(u => u.Kind == UnitKind.OreField).OrderBy(f => Vector2.Distance(f.Position, hq.Position)).ToList();
        var workers = Mine(w, UnitKind.Worker);
        for (var i = 0; i < workers.Count; i++)
        {
            Assert.Equal(1, Cmd(w, CommandType.Gather, new[] { workers[i] }, fields[i % fields.Count].Position, fields[i % fields.Count].Id));
        }
    }

    [Fact]
    public void Skirmish_GivesEachPlayerABase()
    {
        var world = CreateSkirmish();
        Assert.Single(Mine(world, UnitKind.Headquarters));
        Assert.Equal(EconomyRules.StartingWorkers, Mine(world, UnitKind.Worker).Count);
        Assert.Equal(EconomyRules.FieldsPerBase, world.Units.Count(u => u.Kind == UnitKind.OreField));
        Assert.Equal(EconomyRules.StartingOre, world.EconomySystem.OreOf(Player));
        Assert.True(world.Nav.Walls.Count >= 1 + EconomyRules.FieldsPerBase, "buildings and fields block pathing");
        Assert.Equal(1, world.TeamOf(Player));
        Assert.True(Mine(world, UnitKind.Headquarters)[0].Position.X < 0, "team 1 starts on the left");
    }

    [Fact]
    public void Workers_GatherAndDeliver_Repeatedly()
    {
        var world = CreateSkirmish();
        GatherAll(world);
        var wallHits = 0;
        RunUntil(world, () => world.EconomySystem.OreOf(Player) >= EconomyRules.StartingOre + 100, 60f,
            () => wallHits += world.ComputeStats().WallPenetrations);

        Assert.Equal(world.EconomySystem.OreOf(Player) - EconomyRules.StartingOre, world.EconomySystem.TotalGathered);
        Assert.True(world.Units.Where(u => u.Kind == UnitKind.OreField).Sum(f => EconomyRules.OrePerField - f.Ore) >= 100);
        Assert.Equal(0, wallHits); // 일꾼이 본진·광맥 발판으로 파고들지 않는다
        Assert.All(Mine(world, UnitKind.Worker), u => Assert.Equal(UnitOrder.Gather, u.Order));
    }

    [Fact]
    public void Train_DeductsCost_AndNewWorkerStartsGathering()
    {
        var world = CreateSkirmish();
        var hq = Mine(world, UnitKind.Headquarters).Single();
        Assert.Equal(1, Cmd(world, CommandType.Train, new[] { hq }, kind: UnitKind.Worker));
        Assert.Equal(EconomyRules.StartingOre - UnitArchetypes.Worker.Cost, world.EconomySystem.OreOf(Player));
        Assert.Equal(0, Cmd(world, CommandType.Train, new[] { hq }, kind: UnitKind.Melee)); // 본진은 병력을 못 만든다
        Assert.Equal(1, Cmd(world, CommandType.Train, new[] { hq }, kind: UnitKind.Worker)); // 남은 50 으로 하나 더 (대기열 2)
        Assert.Equal(0, Cmd(world, CommandType.Train, new[] { hq }, kind: UnitKind.Worker)); // 자원 0 → 거부
        Assert.Equal(2, hq.Queue.Count);

        var time = RunUntil(world, () => Mine(world, UnitKind.Worker).Count == EconomyRules.StartingWorkers + 1, 10f);
        Assert.InRange(time, UnitArchetypes.Worker.BuildTime - 0.1f, UnitArchetypes.Worker.BuildTime + 0.1f);
        var fresh = Mine(world, UnitKind.Worker).OrderBy(u => u.Id).Last();
        Assert.Equal(UnitOrder.Gather, fresh.Order);
    }

    [Fact]
    public void Build_ValidatesSiteAndCost_AndRefundsWhenCancelledBeforeStart()
    {
        var world = CreateSkirmish();
        var hq = Mine(world, UnitKind.Headquarters).Single();
        var worker = Mine(world, UnitKind.Worker)[0];
        world.EconomySystem.SetOre(Player, 400);

        Assert.Equal(0, Cmd(world, CommandType.Build, new[] { worker }, hq.Position, kind: UnitKind.Barracks)); // 본진 위
        Assert.Equal(0, Cmd(world, CommandType.Build, new[] { worker }, new Vector2(0, 30), kind: UnitKind.Headquarters)); // 지을 수 없는 종류
        Assert.Equal(0, Cmd(world, CommandType.Build, new[] { worker }, new Vector2(40, 40), kind: UnitKind.Barracks)); // 안 보이는 곳

        var site = hq.Position + new Vector2(7, 5);
        Assert.Equal(1, Cmd(world, CommandType.Build, new[] { worker }, site, kind: UnitKind.Barracks));
        Assert.Equal(400 - UnitArchetypes.Barracks.Cost, world.EconomySystem.OreOf(Player));

        Cmd(world, CommandType.Stop, new[] { worker }); // 짓기 전에 취소 → 돌려받음
        Assert.Equal(400, world.EconomySystem.OreOf(Player));
    }

    /// <summary>로드맵 6단계 완료 기준: 일꾼 생산 → 채집 → 병력 생산 흐름.</summary>
    [Fact]
    public void EconomyFlow_WorkerProduction_Gathering_Barracks_Army()
    {
        var world = CreateSkirmish();
        var hq = Mine(world, UnitKind.Headquarters).Single();
        GatherAll(world);

        // 1. 일꾼 생산
        Assert.Equal(1, Cmd(world, CommandType.Train, new[] { hq }, kind: UnitKind.Worker));
        RunUntil(world, () => Mine(world, UnitKind.Worker).Count == 5, 20f);

        // 2. 채집해서 병영 비용 모으기 → 일꾼 하나로 병영 건설
        RunUntil(world, () => world.EconomySystem.OreOf(Player) >= UnitArchetypes.Barracks.Cost, 60f);
        var builder = Mine(world, UnitKind.Worker).OrderBy(u => u.Id).First();
        var site = hq.Position + new Vector2(7, 5);
        Assert.Equal(1, Cmd(world, CommandType.Build, new[] { builder }, site, kind: UnitKind.Barracks));
        var rebuildsBefore = world.NavigationRebuilds;
        RunUntil(world, () => Mine(world, UnitKind.Barracks).Any(), 20f);
        var barracks = Mine(world, UnitKind.Barracks).Single();
        Assert.True(barracks.UnderConstruction);
        world.Step(SimConstants.TickDelta); // 다음 틱 시작에 길찾기 격자를 다시 만든다
        Assert.True(world.NavigationRebuilds > rebuildsBefore, "new building becomes an obstacle");
        Assert.Contains(world.Nav.Walls, w => w.Contains(site));
        RunUntil(world, () => !barracks.UnderConstruction, UnitArchetypes.Barracks.BuildTime + 5f);
        Assert.Equal(barracks.Stats.MaxHp, barracks.Hp, 1);
        Assert.Equal(UnitOrder.Idle, builder.Order);

        // 3. 병력 생산
        Cmd(world, CommandType.Gather, new[] { builder }, Vector2.Zero, world.Units.First(u => u.Kind == UnitKind.OreField).Id);
        RunUntil(world, () => world.EconomySystem.OreOf(Player) >= UnitArchetypes.Melee.Cost, 30f);
        Assert.Equal(1, Cmd(world, CommandType.Train, new[] { barracks }, kind: UnitKind.Melee));
        RunUntil(world, () => Mine(world, UnitKind.Melee).Any(), UnitArchetypes.Melee.BuildTime + 1f);

        Assert.Equal(1, world.EconomySystem.BuildingsCompleted);
        Assert.Equal(2, world.EconomySystem.UnitsProduced);
        Assert.True(world.EconomySystem.TotalGathered >= UnitArchetypes.Barracks.Cost);
    }

    [Fact]
    public void DepletedField_Disappears_AndWorkerMovesToNextField()
    {
        var world = CreateSkirmish();
        var worker = Mine(world, UnitKind.Worker)[0];
        var field = world.Units.First(u => u.Kind == UnitKind.OreField);
        field.Ore = EconomyRules.GatherAmount; // 한 번 캐면 끝
        Cmd(world, CommandType.Gather, new[] { worker }, field.Position, field.Id);

        RunUntil(world, () => world.Find(field.Id) is null, 15f);
        Assert.Equal(EconomyRules.FieldsPerBase - 1, world.Units.Count(u => u.Kind == UnitKind.OreField));
        RunUntil(world, () => worker.Carrying == 0 && worker.GatherFieldId != field.Id, 15f);
        Assert.Equal(UnitOrder.Gather, worker.Order);
    }

    [Fact]
    public void Enemy_CanDestroyBuilding_AndPathOpens()
    {
        var world = CreateSkirmish();
        var hq = Mine(world, UnitKind.Headquarters).Single();
        var site = hq.Position + new Vector2(7, 5);
        world.EconomySystem.SetOre(Player, 1000);
        Assert.Equal(1, Cmd(world, CommandType.Build, new[] { Mine(world, UnitKind.Worker)[0] }, site, kind: UnitKind.Barracks));
        RunUntil(world, () => Mine(world, UnitKind.Barracks).Any(), 20f);
        var barracks = Mine(world, UnitKind.Barracks).Single();

        // 적 근접 유닛 넷이 짓는 중인 병영을 부순다 (건물은 반격하지 않는다).
        for (var i = 0; i < 4; i++)
        {
            var enemy = world.Spawn(site + new Vector2(4, i - 1.5f), team: 2, kind: UnitKind.Melee);
            enemy.Order = UnitOrder.Attack;
            enemy.AttackTargetId = barracks.Id;
        }

        RunUntil(world, () => world.Find(barracks.Id) is null, 30f);
        world.Step(SimConstants.TickDelta);
        Assert.DoesNotContain(world.Nav.Walls, w => w.Contains(site));
    }
}
