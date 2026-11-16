using System.Numerics;
using OpenRTS.Server.Simulation;
using OpenRTS.Server.Simulation.Abilities;
using OpenRTS.Server.Simulation.Combat;
using OpenRTS.Shared;
using OpenRTS.Shared.Abilities;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;

namespace OpenRTS.Server.Tests;

public class AbilityTests
{
    private const float Dt = SimConstants.TickDelta;

    private static World Create(MapKind map = MapKind.Open)
    {
        var s = SimSettings.Default;
        s.BotCount = 0;
        s.Map = map;
        var world = new World(settings: s);
        world.ResetScenario();
        return world;
    }

    /// <summary>스스로 싸우거나 움직이지 않는 유닛.</summary>
    private static Unit Still(World w, Vector2 p, byte team, UnitKind kind = UnitKind.Melee, byte owner = 0)
    {
        var u = w.Spawn(p, team: team, kind: kind, owner: owner);
        u.HoldFire = true;
        return u;
    }

    private static void Run(World w, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt)
        {
            w.Step(Dt);
        }
    }

    private static float RunUntil(World w, Func<bool> condition, float maxSeconds)
    {
        var t = 0f;
        while (!condition())
        {
            Assert.True(t < maxSeconds, $"not reached within {maxSeconds}s");
            w.Step(Dt);
            t += Dt;
        }

        return t;
    }

    private static List<AbilityEvent> Collect(World w, float seconds)
    {
        var events = new List<AbilityEvent>();
        for (var t = 0f; t < seconds; t += Dt)
        {
            w.Step(Dt);
            events.AddRange(w.AbilitySystem.Events);
            w.AbilitySystem.ClearEvents();
        }

        return events;
    }

    [Fact]
    public void Catalog_EveryLoadoutAbilityIsDefined_AndUnitsGetThem()
    {
        foreach (UnitKind kind in Enum.GetValues(typeof(UnitKind)))
        {
            foreach (var id in AbilityCatalog.LoadoutOf(kind))
            {
                Assert.True(AbilityCatalog.TryGet(id, out var def), $"{kind}: {id} not defined");
                Assert.True(def.Cooldown > 0);
            }
        }

        var w = Create();
        var hero = w.Spawn(Vector2.Zero, team: 1, kind: UnitKind.Hero);
        Assert.Equal(new[] { AbilityId.ShockBolt, AbilityId.Nova, AbilityId.Leap, AbilityId.Mend }, hero.Abilities.Select(a => a.Id));
        Assert.Empty(w.Spawn(new Vector2(5, 5), team: 1, kind: UnitKind.Worker).Abilities);
    }

    [Fact]
    public void ShockBolt_HitsFirstEnemyOnly_StartsCooldown_AndCannotRecast()
    {
        var w = Create();
        var hero = Still(w, new Vector2(-10, 0), 1, UnitKind.Hero);
        var first = Still(w, new Vector2(-4, 0), 2);
        var second = Still(w, new Vector2(-1, 0), 2);
        w.Step(Dt);

        Assert.True(w.AbilitySystem.TryBegin(hero, AbilityId.ShockBolt, 0, new Vector2(10, 0)));
        var events = Collect(w, 1f);

        var def = AbilityCatalog.Get(AbilityId.ShockBolt);
        Assert.Equal(first.Stats.MaxHp - def.Effect.Damage, first.Hp, 3);
        Assert.Equal(second.Stats.MaxHp, second.Hp); // 관통 0 → 첫 적에서 멈춤
        Assert.Contains(events, e => e.Type == AbilityEventType.ProjectileLaunch);
        var end = Assert.Single(events, e => e.Type == AbilityEventType.ProjectileEnd);
        Assert.InRange(end.Point.X, -5.5f, -3.5f);
        Assert.InRange(hero.FindAbility(AbilityId.ShockBolt)!.Cooldown, def.Cooldown - 1.2f, def.Cooldown - 0.6f);
        Assert.False(w.AbilitySystem.TryBegin(hero, AbilityId.ShockBolt, 0, new Vector2(10, 0)));
    }

    [Fact]
    public void LineProjectile_IsBlockedByWalls_AndDoesNotTunnel()
    {
        var w = Create(MapKind.Gate);
        var hero = Still(w, new Vector2(-6, 20), 1, UnitKind.Hero);
        var behindWall = Still(w, new Vector2(4, 20), 2);
        w.Step(Dt);
        w.AbilitySystem.TryBegin(hero, AbilityId.ShockBolt, 0, behindWall.Position);
        var events = Collect(w, 1f);
        Assert.Equal(behindWall.Stats.MaxHp, behindWall.Hp);
        Assert.InRange(Assert.Single(events, e => e.Type == AbilityEventType.ProjectileEnd).Point.X, -1.2f, -0.8f); // 벽 왼쪽 면

        // 한 틱에 0.83 이동하는데 대상(지름 1)을 건너뛰지 않는다: 아주 가까이 붙은 작은 간격에서도 맞는다
        var w2 = Create();
        var shooter = Still(w2, new Vector2(0, 0), 1, UnitKind.Hero);
        var thin = Still(w2, new Vector2(3.3f, 0), 2);
        w2.Step(Dt);
        w2.AbilitySystem.TryBegin(shooter, AbilityId.ShockBolt, 0, new Vector2(20, 0));
        Run(w2, 1f);
        Assert.True(thin.Hp < thin.Stats.MaxHp);
    }

    [Fact]
    public void Nova_DamagesAndSlows_EnemiesInRadius_NotAllies()
    {
        var w = Create();
        var hero = Still(w, Vector2.Zero, 1, UnitKind.Hero);
        var enemyNear = Still(w, new Vector2(3, 0), 2);
        var enemyFar = Still(w, new Vector2(8, 0), 2);
        var ally = Still(w, new Vector2(-2, 0), 1);
        w.Step(Dt);

        w.AbilitySystem.TryBegin(hero, AbilityId.Nova, 0, default);
        Run(w, 0.5f);
        Assert.True(enemyNear.Hp < enemyNear.Stats.MaxHp);
        Assert.Equal(enemyFar.Stats.MaxHp, enemyFar.Hp);
        Assert.Equal(ally.Stats.MaxHp, ally.Hp);
        Assert.InRange(enemyNear.SpeedMultiplier, 0.59f, 0.61f);

        // 둔화된 유닛은 느리게 간다
        enemyNear.Order = UnitOrder.Move;
        w.SetMoveTarget(enemyNear.Id, new Vector2(3, 30));
        var y0 = enemyNear.Position.Y;
        Run(w, 1f);
        var moved = enemyNear.Position.Y - y0;
        Assert.InRange(moved, SimConstants.UnitSpeed * 0.6f - 0.6f, SimConstants.UnitSpeed * 0.6f + 0.3f);
    }

    [Fact]
    public void Volley_IsTelegraphed_AndOnlyHitsUnitsThereWhenItLands()
    {
        var w = Create();
        var archer = Still(w, Vector2.Zero, 1, UnitKind.Ranged);
        var stays = Still(w, new Vector2(7, 0), 2);
        var leaves = Still(w, new Vector2(7, 1), 2);
        w.Step(Dt);

        Assert.True(w.AbilitySystem.TryBegin(archer, AbilityId.Volley, 0, new Vector2(7, 0.5f)));
        var events = Collect(w, 0.35f); // 선딜 0.3 → 예고
        Assert.Contains(events, e => e.Type == AbilityEventType.AreaTelegraph);
        Assert.Equal(1, w.AbilitySystem.PendingAreas);

        leaves.Order = UnitOrder.Move;
        w.SetMoveTarget(leaves.Id, new Vector2(7, 10)); // 예고를 보고 피한다
        events = Collect(w, 1f);
        Assert.Contains(events, e => e.Type == AbilityEventType.AreaHit);
        Assert.True(stays.Hp < stays.Stats.MaxHp);
        Assert.Equal(leaves.Stats.MaxHp, leaves.Hp);
    }

    [Fact]
    public void OutOfRange_UnitWalksIntoRange_ThenCasts()
    {
        var w = Create();
        var archer = w.Spawn(Vector2.Zero, team: 1, kind: UnitKind.Ranged);
        archer.HoldFire = true;
        Still(w, new Vector2(20, 0), 2);
        w.Step(Dt);
        Assert.True(w.AbilitySystem.TryBegin(archer, AbilityId.Volley, 0, new Vector2(20, 0)));
        Assert.Equal(CastPhase.Approach, archer.Cast!.Phase);

        RunUntil(w, () => w.AbilitySystem.PendingAreas > 0, 5f);
        Assert.InRange(archer.Position.X, 20 - 9 - 0.5f - 0.6f, 20 - 9 - 0.5f + 0.6f); // 사거리 9 + 반지름 지점에서 멈춤
    }

    [Fact]
    public void Leap_DashesToPoint_StopsAtWalls_AndStunsOnArrival()
    {
        var w = Create();
        var hero = Still(w, Vector2.Zero, 1, UnitKind.Hero);
        var enemy = Still(w, new Vector2(6.5f, 0), 2);
        w.Step(Dt);

        Assert.True(w.AbilitySystem.TryBegin(hero, AbilityId.Leap, 0, new Vector2(6, 0)));
        var time = RunUntil(w, () => hero.Cast == null, 2f);
        Assert.True(time < 0.6f, $"dash took {time}s");
        Assert.True(hero.Position.X > 5f);
        Assert.True(enemy.IsStunned);
        Assert.True(enemy.Hp < enemy.Stats.MaxHp);

        // 벽 앞에서 멈춘다
        var g = Create(MapKind.Gate);
        var h2 = Still(g, new Vector2(-6, 20), 1, UnitKind.Hero);
        g.Step(Dt);
        g.AbilitySystem.TryBegin(h2, AbilityId.Leap, 0, new Vector2(1, 20));
        RunUntil(g, () => h2.Cast == null, 2f);
        Assert.InRange(h2.Position.X, -2.2f, -1.4f);
        Assert.True(g.Nav.Clearance(h2.Position) >= g.Settings.UnitRadius * 0.95f);
    }

    [Fact]
    public void Stun_StopsMovementAttacksAndCancelsCast_WithoutCooldown()
    {
        var w = Create();
        var hero = Still(w, Vector2.Zero, 1, UnitKind.Hero);
        Still(w, new Vector2(3, 0), 2);
        w.Step(Dt);

        Assert.True(w.AbilitySystem.TryBegin(hero, AbilityId.Nova, 0, default)); // 선딜 0.3
        w.Step(Dt);
        w.AbilitySystem.ApplyStatus(hero, StatusType.Stun, 1f, 0, 0);
        Assert.Null(hero.Cast);
        Assert.True(hero.FindAbility(AbilityId.Nova)!.IsReady); // 발동 전 취소 → 재사용 대기 없음
        Assert.False(w.AbilitySystem.TryBegin(hero, AbilityId.Nova, 0, default)); // 기절 중엔 못 씀

        // 기절 중에는 움직이지 않는다
        w.SetMoveTarget(hero.Id, new Vector2(0, 10));
        var p = hero.Position;
        Run(w, 0.5f);
        Assert.True(Vector2.Distance(p, hero.Position) < 0.05f);
        Run(w, 0.6f);
        Assert.False(hero.IsStunned);
    }

    [Fact]
    public void ShieldUp_AbsorbsDamageFirst()
    {
        var w = Create();
        var knight = Still(w, Vector2.Zero, 1);
        w.Step(Dt);
        Assert.True(w.AbilitySystem.TryBegin(knight, AbilityId.ShieldUp, 0, default));
        w.Step(Dt);
        Assert.Equal(40f, w.AbilitySystem.ShieldOf(knight), 3);

        w.Combat.ApplyDamage(0, 2, knight, 30);
        Assert.Equal(knight.Stats.MaxHp, knight.Hp);
        w.Combat.ApplyDamage(0, 2, knight, 30);
        Assert.Equal(knight.Stats.MaxHp - 20, knight.Hp, 3);
        w.Step(Dt);
        Assert.Equal(0f, w.AbilitySystem.ShieldOf(knight));
    }

    [Fact]
    public void Mend_ChannelsAndHealsAllies_AndIsInterruptedByStun()
    {
        var w = Create();
        var hero = Still(w, Vector2.Zero, 1, UnitKind.Hero);
        var hurt = Still(w, new Vector2(2, 0), 1);
        hurt.Hp = 50;
        w.Step(Dt);

        Assert.True(w.AbilitySystem.TryBegin(hero, AbilityId.Mend, 0, default));
        Run(w, 2.6f);
        // 선딜 0.2 + 정신 집중 2초, 0.5초마다 15 → 4~5번
        Assert.InRange(hurt.Hp, 50 + 15 * 4, 50 + 15 * 5);
        Assert.Null(hero.Cast);

        var w2 = Create();
        var h2 = Still(w2, Vector2.Zero, 1, UnitKind.Hero);
        var hurt2 = Still(w2, new Vector2(2, 0), 1);
        hurt2.Hp = 50;
        w2.Step(Dt);
        w2.AbilitySystem.TryBegin(h2, AbilityId.Mend, 0, default);
        Run(w2, 0.8f); // 2번 정도
        w2.AbilitySystem.ApplyStatus(h2, StatusType.Stun, 0.5f, 0, 0);
        var hpAtStun = hurt2.Hp;
        Run(w2, 2f);
        Assert.Equal(hpAtStun, hurt2.Hp);
        Assert.False(h2.FindAbility(AbilityId.Mend)!.IsReady); // 발동 후 끊김 → 재사용 대기는 남는다
    }

    [Fact]
    public void Command_UsesOneNearestCaster_ForSingleCaster_AndAllForShieldUp_OnlyOwnUnits()
    {
        var w = Create();
        w.AddPlayer(1);
        foreach (var u in w.Units.ToList())
        {
            if (u.Owner == 1) w.Remove(u.Id);
        }

        var near = w.Spawn(new Vector2(-5, 0), owner: 1, team: 1, kind: UnitKind.Ranged);
        var far = w.Spawn(new Vector2(-15, 0), owner: 1, team: 1, kind: UnitKind.Ranged);
        var k1 = w.Spawn(new Vector2(-5, 5), owner: 1, team: 1, kind: UnitKind.Melee);
        var k2 = w.Spawn(new Vector2(-6, 5), owner: 1, team: 1, kind: UnitKind.Melee);
        var foreign = w.Spawn(new Vector2(-5, -5), owner: 2, team: 2, kind: UnitKind.Melee);
        w.Step(Dt);

        int Cast(AbilityId id, Vector2 p, params Unit[] units) => w.Command(1, new UnitCommand { Type = CommandType.Cast, Ability = id, Target = p, UnitIds = units.Select(u => u.Id).ToArray() });

        Assert.Equal(1, Cast(AbilityId.Volley, new Vector2(2, 0), near, far));
        Assert.NotNull(near.Cast);
        Assert.Null(far.Cast);

        Assert.Equal(2, Cast(AbilityId.ShieldUp, default, k1, k2, foreign));
        Assert.Null(foreign.Cast);
        Assert.Equal(0, Cast(AbilityId.Nova, default, k1)); // 그 능력이 없음
    }

    [Fact]
    public void UnitTargetedAbility_RejectsHiddenOrWrongTargets()
    {
        // 데이터만으로 유닛 대상 능력을 만들어 확인 (등록부를 쓰지 않는 임시 정의)
        var w = Create();
        var caster = Still(w, Vector2.Zero, 1, UnitKind.Hero);
        var hidden = Still(w, new Vector2(40, 40), 2);
        var ally = Still(w, new Vector2(2, 0), 1);
        w.Step(Dt);

        var def = new AbilityDefinition
        {
            Id = AbilityId.ShockBolt, Name = "test", Targeting = AbilityTargeting.Unit, Delivery = AbilityDelivery.Projectile,
            Homing = true, ProjectileSpeed = 30, Range = 100, Cooldown = 1, Effect = new AbilityEffect { Damage = 10 },
        };
        caster.Abilities.Clear();
        caster.Abilities.Add(new AbilityState(def));

        Assert.False(w.AbilitySystem.TryBegin(caster, AbilityId.ShockBolt, hidden.Id, default)); // 안개 속
        Assert.False(w.AbilitySystem.TryBegin(caster, AbilityId.ShockBolt, ally.Id, default));  // 적 전용

        var visible = Still(w, new Vector2(6, 0), 2);
        w.Step(Dt);
        Assert.True(w.AbilitySystem.TryBegin(caster, AbilityId.ShockBolt, visible.Id, default));
        RunUntil(w, () => visible.Hp < visible.Stats.MaxHp, 2f); // 유도 투사체
    }

    private sealed class DoubleOnStunned : IAbilityScript
    {
        public int Hits;

        public AbilityEffect ModifyEffect(World world, Unit caster, Unit target, AbilityEffect effect)
        {
            if (target.IsStunned)
            {
                effect.Damage *= 2;
            }

            return effect;
        }

        public void OnHit(World world, Unit caster, Unit target, AbilityDefinition definition) => Hits++;
    }

    [Fact]
    public void Script_CanModifyEffect_AndObserveHits()
    {
        var w = Create();
        var script = new DoubleOnStunned();
        w.AbilitySystem.Scripts.Register(AbilityId.ShockBolt, script);
        var hero = Still(w, Vector2.Zero, 1, UnitKind.Hero);
        var target = Still(w, new Vector2(5, 0), 2);
        w.Step(Dt);
        w.AbilitySystem.ApplyStatus(target, StatusType.Stun, 3f, 0, 0);

        w.AbilitySystem.TryBegin(hero, AbilityId.ShockBolt, 0, target.Position);
        Run(w, 1f);
        Assert.Equal(target.Stats.MaxHp - 2 * AbilityCatalog.Get(AbilityId.ShockBolt).Effect.Damage, target.Hp, 3);
        Assert.Equal(1, script.Hits);
    }

    [Theory]
    [InlineData(0.2f, 0.5f, 0.1f)]   // 상한 0.1초
    [InlineData(0.2f, 0.05f, 0.05f)] // 지연만큼
    [InlineData(0.1f, 1f, 0.05f)]    // 선딜의 절반까지
    [InlineData(0f, 1f, 0f)]         // 선딜 없음
    [InlineData(0.3f, -0.2f, 0f)]    // 음수(미래 틱) 무시
    [InlineData(0.3f, float.NaN, 0f)]
    public void WindupCredit_IsCapped(float castTime, float delay, float expected)
    {
        Assert.Equal(expected, AbilitySystem.WindupCredit(castTime, delay), 4);
    }

    /// <summary>플레이어 명령으로 Shock Bolt 를 쓰고, 발사(ProjectileLaunch)까지 걸린 틱 수.</summary>
    private static int TicksToLaunch(bool lagCompensation, float commandDelay, Vector2? from = null)
    {
        var s = SimSettings.Default;
        s.BotCount = 0;
        s.LagCompensation = lagCompensation;
        var w = new World(settings: s);
        w.ResetScenario();
        var hero = w.Spawn(from ?? Vector2.Zero, owner: 1, team: 1, kind: UnitKind.Hero);
        hero.HoldFire = true;
        w.Step(Dt);
        w.AbilitySystem.ClearEvents();
        Assert.Equal(1, w.Command(1, new UnitCommand { Type = CommandType.Cast, Ability = AbilityId.ShockBolt, Target = new Vector2(10, 0), UnitIds = new[] { hero.Id } }, commandDelay));
        for (var tick = 1; tick <= 60; tick++)
        {
            w.Step(Dt);
            if (w.AbilitySystem.Events.Any(e => e.Type == AbilityEventType.ProjectileLaunch))
            {
                return tick;
            }

            w.AbilitySystem.ClearEvents();
        }

        throw new Xunit.Sdk.XunitException("never launched");
    }

    [Fact]
    public void CommandDelay_ShortensWindup_WithinCap_OnlyWhenEnabled()
    {
        var normal = TicksToLaunch(lagCompensation: true, commandDelay: 0f);
        var compensated = TicksToLaunch(lagCompensation: true, commandDelay: 0.25f);
        var disabled = TicksToLaunch(lagCompensation: false, commandDelay: 0.25f);

        Assert.Equal(6, normal); // 선딜 0.2초 = 6틱
        Assert.Equal(normal - 3, compensated); // 상한 min(0.2×0.5, 0.1) = 0.1초 = 3틱만 줄어듦 (0.25 를 다 주지 않음)
        Assert.Equal(normal, disabled);
    }

    [Fact]
    public void RejectedPlayerCast_EmitsCancel_SoClientCanDropPrediction()
    {
        var w = Create();
        var hero = w.Spawn(Vector2.Zero, owner: 1, team: 1, kind: UnitKind.Hero);
        hero.HoldFire = true;
        w.Step(Dt);
        UnitCommand Bolt() => new() { Type = CommandType.Cast, Ability = AbilityId.ShockBolt, Target = new Vector2(10, 0), UnitIds = new[] { hero.Id } };

        Assert.Equal(1, w.Command(1, Bolt()));
        Run(w, 0.5f); // 발동 → 재사용 대기
        w.AbilitySystem.ClearEvents();
        Assert.Equal(0, w.Command(1, Bolt())); // 재사용 대기 중 → 거부
        var cancel = Assert.Single(w.AbilitySystem.Events);
        Assert.Equal((AbilityEventType.CastCancel, hero.Id, AbilityId.ShockBolt), (cancel.Type, cancel.Caster, cancel.Ability));
    }

    [Fact]
    public void CommandDelay_NotCreditedWhenWalkingFirst_OrForAi()
    {
        // 사거리 밖 지점 범위(Volley): 걸어간 뒤 선딜 → 보상 없음
        var w = Create();
        var archer = w.Spawn(Vector2.Zero, owner: 1, team: 1, kind: UnitKind.Ranged);
        archer.HoldFire = true;
        w.Step(Dt);
        w.Command(1, new UnitCommand { Type = CommandType.Cast, Ability = AbilityId.Volley, Target = new Vector2(20, 0), UnitIds = new[] { archer.Id } }, 0.3f);
        RunUntil(w, () => archer.Cast?.Phase == CastPhase.Windup, 5f);
        Assert.Equal(0f, w.AbilitySystem.LastWindupCredit);

        // AI 시전 (TryBegin 기본값) → 보상 없음
        var hero = Still(w, new Vector2(-20, -20), 1, UnitKind.Hero);
        w.Step(Dt);
        Assert.True(w.AbilitySystem.TryBegin(hero, AbilityId.Nova, 0, default));
        Assert.Equal(0f, w.AbilitySystem.LastWindupCredit);
    }

    [Fact]
    public void BattleBots_UseTheirAbilities()
    {
        var s = SimSettings.Default;
        s.BotCount = 120;
        s.Scenario = BotScenario.Battle;
        var w = new World(settings: s);
        w.ResetScenario();
        Assert.True(w.Units.Count(u => u.Kind == UnitKind.Hero) >= 2);

        RunUntil(w, () => w.Winner != 0, 180f);
        var used = w.AbilitySystem.Releases;
        foreach (var id in new[] { AbilityId.ShockBolt, AbilityId.Nova, AbilityId.Volley, AbilityId.ShieldUp })
        {
            Assert.True(used.GetValueOrDefault(id) > 0, $"AI never used {id}: {string.Join(", ", used.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
    }
}
