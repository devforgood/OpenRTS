using System.Numerics;
using OpenRTS.Server.Simulation.Combat;
using OpenRTS.Shared.Abilities;

namespace OpenRTS.Server.Simulation.Abilities;

/// <summary>유닛이 가진 능력 하나의 실행 상태 (재사용 대기).</summary>
public sealed class AbilityState
{
    public AbilityState(AbilityDefinition definition)
    {
        Definition = definition;
    }

    public AbilityDefinition Definition { get; }

    public AbilityId Id => Definition.Id;

    /// <summary>남은 재사용 대기(초). 0 이면 준비됨.</summary>
    public float Cooldown { get; internal set; }

    public bool IsReady => Cooldown <= 0;
}

public enum CastPhase : byte
{
    /// <summary>사거리 밖: 대상 쪽으로 걸어가는 중.</summary>
    Approach,

    /// <summary>선딜: 서서 기다리는 중. 끝나면 발동.</summary>
    Windup,

    /// <summary>정신 집중: 주기적으로 범위 효과.</summary>
    Channel,

    /// <summary>돌진 중.</summary>
    Dash,
}

/// <summary>진행 중인 시전. 끝나면(또는 취소되면) 시전 전 명령으로 돌아간다.</summary>
public sealed class ActiveCast
{
    public ActiveCast(AbilityState ability, int targetUnit, Vector2 point)
    {
        Ability = ability;
        TargetUnit = targetUnit;
        Point = point;
    }

    public AbilityState Ability { get; }
    public AbilityDefinition Definition => Ability.Definition;
    public int TargetUnit { get; }
    public Vector2 Point { get; internal set; }
    public CastPhase Phase { get; internal set; } = CastPhase.Approach;

    /// <summary>현재 단계에 남은 시간(초).</summary>
    public float Timer { get; internal set; }

    /// <summary>정신 집중 다음 판정까지(초).</summary>
    public float TickTimer { get; internal set; }

    internal float RepathTimer { get; set; }

    // 시전 전 명령 (끝나면 복귀)
    internal UnitOrder ResumeOrder { get; set; }
    internal Vector2 ResumeGoal { get; set; }
    internal int ResumeTarget { get; set; }
}

/// <summary>유닛에 걸린 상태 효과 하나.</summary>
public sealed class StatusInstance
{
    public StatusType Type { get; init; }

    /// <summary>남은 시간(초).</summary>
    public float Remaining { get; internal set; }

    /// <summary>둔화 비율 또는 남은 보호막 양.</summary>
    public float Magnitude { get; internal set; }

    public int SourceId { get; init; }
}

/// <summary>
/// 데이터로 표현하기 어려운 능력 동작을 덧붙이는 서버 스크립트 (선택). <c>world.AbilitySystem.Scripts.Register</c> 로 등록한다.
/// 모든 메서드에 기본 동작이 있어서 필요한 것만 구현하면 된다.
/// 참고 서버의 ISpellScript 처럼 "스크립트가 피해를 하드코딩" 하지 않도록, 피해·회복·상태는 데이터(<see cref="AbilityEffect"/>)가 기본이고
/// 스크립트는 그 값을 바꾸거나 추가 동작을 넣는다.
/// </summary>
public interface IAbilityScript
{
    /// <summary>선딜이 끝나 발동하기 직전. false 를 돌려주면 발동하지 않는다(재사용 대기는 시작됨).</summary>
    bool OnRelease(World world, Unit caster, ActiveCast cast) => true;

    /// <summary>효과를 적용하기 직전. 바꾼 효과를 돌려준다 (예: 기절한 대상에게 두 배 피해).</summary>
    AbilityEffect ModifyEffect(World world, Unit caster, Unit target, AbilityEffect effect) => effect;

    /// <summary>효과를 적용한 뒤.</summary>
    void OnHit(World world, Unit caster, Unit target, AbilityDefinition definition) { }
}

/// <summary>
/// 능력별 스크립트 등록부 (World 마다 하나 — 테스트끼리 섞이지 않게). 스크립트가 없는 능력은 데이터만으로 동작한다.
/// 서버 시작 때 기본 스크립트를 등록하려면 <see cref="AbilitySystem"/> 생성자에서 한다.
/// </summary>
public sealed class AbilityScriptRegistry
{
    private readonly Dictionary<AbilityId, IAbilityScript> _scripts = new();

    public void Register(AbilityId id, IAbilityScript script) => _scripts[id] = script;

    public void Unregister(AbilityId id) => _scripts.Remove(id);

    public IAbilityScript? Get(AbilityId id) => _scripts.GetValueOrDefault(id);
}
