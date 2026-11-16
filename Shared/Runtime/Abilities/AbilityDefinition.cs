using System;
using System.Collections.Generic;
using OpenRTS.Shared.Combat;

namespace OpenRTS.Shared.Abilities
{
    /// <summary>능력(스킬) 종류. 값은 프로토콜에 실리므로 바꾸지 말고 끝에 추가한다. 0 은 없음.</summary>
    public enum AbilityId : byte
    {
        None = 0,

        /// <summary>영웅 Q: 직선 투사체. 처음 맞은 적에게 피해, 벽에 막힌다.</summary>
        ShockBolt = 1,

        /// <summary>영웅 W: 자기 주변 원형 피해 + 둔화.</summary>
        Nova = 2,

        /// <summary>영웅 E: 지점으로 돌진, 도착하면 주변 적 기절.</summary>
        Leap = 3,

        /// <summary>영웅 R: 정신 집중하며 주변 아군을 주기적으로 회복.</summary>
        Mend = 4,

        /// <summary>근접: 자기에게 보호막.</summary>
        ShieldUp = 5,

        /// <summary>원거리: 지점에 예고 후 떨어지는 범위 피해.</summary>
        Volley = 6,
    }

    /// <summary>시전할 때 무엇을 고르는지.</summary>
    public enum AbilityTargeting : byte
    {
        /// <summary>대상 없음 (자기 자신·자기 주변).</summary>
        None = 0,

        /// <summary>유닛 하나.</summary>
        Unit = 1,

        /// <summary>지점 (투사체는 방향으로 쓴다).</summary>
        Point = 2,
    }

    /// <summary>효과를 받는 쪽.</summary>
    [Flags]
    public enum AbilityAffects : byte
    {
        None = 0,
        Enemies = 1,
        Allies = 2,

        /// <summary>시전자 자신 (Allies 와 별개로 지정).</summary>
        Self = 4,
    }

    /// <summary>효과를 전달하는 방식.</summary>
    public enum AbilityDelivery : byte
    {
        /// <summary>대상(Unit) 또는 자신(None)에게 즉시.</summary>
        Instant = 0,

        /// <summary>원 범위. 중심은 자신 또는 지점. 지연(예고)·정신 집중 주기 판정 가능.</summary>
        Area = 1,

        /// <summary>투사체. 지점 방향 직선(벽에 막힘, 관통 수 제한) 또는 유닛 유도.</summary>
        Projectile = 2,

        /// <summary>돌진. 지점까지(벽 앞에서 멈춤) 빠르게 이동하고, 도착하면 자기 주변 범위 효과.</summary>
        Dash = 3,
    }

    public enum StatusType : byte
    {
        None = 0,

        /// <summary>기절: 움직이지도, 공격·시전하지도 못한다 (하던 시전은 취소).</summary>
        Stun = 1,

        /// <summary>둔화: 이동 속도 × (1 - 크기).</summary>
        Slow = 2,

        /// <summary>보호막: 크기만큼 피해를 먼저 흡수.</summary>
        Shield = 3,
    }

    /// <summary>AI(행동 층)가 이 능력을 언제 쓰는지.</summary>
    public enum AbilityAiHint : byte
    {
        /// <summary>AI 가 쓰지 않는다.</summary>
        Never = 0,

        /// <summary>지금 싸우는 대상이 사거리 안이면 그 대상에게.</summary>
        OnTarget = 1,

        /// <summary>범위 안에 적이 <see cref="AbilityDefinition.AiMinCount"/> 명 이상 모인 곳에.</summary>
        EnemyCluster = 2,

        /// <summary>자기 체력 비율이 <see cref="AbilityDefinition.AiThreshold"/> 아래이고 싸우는 중일 때.</summary>
        SelfLowHealth = 3,

        /// <summary>범위 안에 체력이 <see cref="AbilityDefinition.AiThreshold"/> 아래인 아군이 <see cref="AbilityDefinition.AiMinCount"/> 명 이상일 때.</summary>
        AlliesHurt = 4,
    }

    /// <summary>한 번 맞을 때 주는 효과. 피해·회복·상태를 함께 줄 수 있다.</summary>
    public struct AbilityEffect
    {
        public float Damage;
        public float Heal;
        public StatusType Status;
        public float StatusDuration;

        /// <summary>둔화 비율(0~1) 또는 보호막 양.</summary>
        public float StatusMagnitude;
    }

    /// <summary>
    /// 능력 하나의 데이터. 동작은 데이터만으로 정해지고(엔진이 해석), 데이터로 표현하기 어려운 부분만
    /// 서버의 능력 스크립트(IAbilityScript)가 덧붙인다.
    /// 서버·클라이언트가 같은 정의를 쓴다 (클라이언트는 이름·사거리·범위 표시와 연출에 사용).
    /// </summary>
    public sealed class AbilityDefinition
    {
        public AbilityId Id;
        public string Name = "";
        public AbilityTargeting Targeting;
        public AbilityDelivery Delivery;

        /// <summary>시전 거리 (시전자 가장자리 기준). 밖이면 걸어가서 쓴다. 대상 없음이면 무시.</summary>
        public float Range;

        public float Cooldown;

        /// <summary>선딜: 시전 시작부터 효과 발동까지(초). 이동하지 않으며 기절하면 취소(쿨다운 없음).</summary>
        public float CastTime;

        /// <summary>정신 집중 시간(초). 0 보다 크면 발동 후 이 시간 동안 <see cref="ChannelInterval"/> 마다 범위 효과를 반복한다.</summary>
        public float ChannelTime;

        public float ChannelInterval = 0.5f;

        /// <summary>효과를 받는 쪽.</summary>
        public AbilityAffects Affects = AbilityAffects.Enemies;

        public AbilityEffect Effect;

        // ── 범위 ──
        public float AreaRadius;

        /// <summary>범위가 터지기까지 지연(예고, 초). 0 이면 즉시.</summary>
        public float AreaDelay;

        // ── 투사체 ──
        public float ProjectileSpeed;

        /// <summary>직선 투사체 폭 (지름).</summary>
        public float ProjectileWidth = 0.5f;

        /// <summary>추가로 뚫고 지나가는 적 수 (0 이면 처음 맞은 적에서 멈춤).</summary>
        public int Pierce;

        /// <summary>true 면 유닛 대상 유도 투사체, false 면 지점 방향 직선 (사거리만큼 날아감).</summary>
        public bool Homing;

        // ── 돌진 ──
        public float DashSpeed;

        // ── 사용 방식 ──

        /// <summary>
        /// 여러 유닛을 골라 쓰면 그중 하나(대상에 가장 가까운 준비된 유닛)만 쓴다. false 면 고른 유닛 모두가 쓴다.
        /// </summary>
        public bool SingleCaster = true;

        public AbilityAiHint AiHint;
        public int AiMinCount = 1;
        public float AiThreshold = 0.5f;

        public bool IsChanneled => ChannelTime > 0;
    }

    /// <summary>
    /// 능력 목록과 유닛 종류별 보유 능력. 새 능력은 <see cref="AbilityId"/> 에 값을 추가하고 여기에 정의를 넣는다.
    /// (수치를 JSON 으로 빼는 것은 이후 과제 — 지금은 코드가 곧 데이터)
    /// </summary>
    public static class AbilityCatalog
    {
        private static readonly Dictionary<AbilityId, AbilityDefinition> Definitions = new Dictionary<AbilityId, AbilityDefinition>();
        private static readonly AbilityId[] NoAbilities = new AbilityId[0];

        private static readonly Dictionary<UnitKind, AbilityId[]> Loadouts = new Dictionary<UnitKind, AbilityId[]>
        {
            { UnitKind.Hero, new[] { AbilityId.ShockBolt, AbilityId.Nova, AbilityId.Leap, AbilityId.Mend } },
            { UnitKind.Melee, new[] { AbilityId.ShieldUp } },
            { UnitKind.Ranged, new[] { AbilityId.Volley } },
        };

        static AbilityCatalog()
        {
            Add(new AbilityDefinition
            {
                Id = AbilityId.ShockBolt,
                Name = "Shock Bolt",
                Targeting = AbilityTargeting.Point,
                Delivery = AbilityDelivery.Projectile,
                Range = 12f,
                Cooldown = 4f,
                CastTime = 0.2f,
                ProjectileSpeed = 25f,
                ProjectileWidth = 0.6f,
                Pierce = 0,
                Effect = new AbilityEffect { Damage = 45f },
                AiHint = AbilityAiHint.OnTarget,
            });
            Add(new AbilityDefinition
            {
                Id = AbilityId.Nova,
                Name = "Nova",
                Targeting = AbilityTargeting.None,
                Delivery = AbilityDelivery.Area,
                Cooldown = 8f,
                CastTime = 0.3f,
                AreaRadius = 4f,
                Effect = new AbilityEffect { Damage = 30f, Status = StatusType.Slow, StatusDuration = 2f, StatusMagnitude = 0.4f },
                AiHint = AbilityAiHint.EnemyCluster,
                AiMinCount = 2,
            });
            Add(new AbilityDefinition
            {
                Id = AbilityId.Leap,
                Name = "Leap",
                Targeting = AbilityTargeting.Point,
                Delivery = AbilityDelivery.Dash,
                Range = 7f,
                Cooldown = 10f,
                CastTime = 0.1f,
                DashSpeed = 20f,
                AreaRadius = 1.8f,
                Effect = new AbilityEffect { Damage = 20f, Status = StatusType.Stun, StatusDuration = 0.75f },
                AiHint = AbilityAiHint.EnemyCluster,
                AiMinCount = 2,
            });
            Add(new AbilityDefinition
            {
                Id = AbilityId.Mend,
                Name = "Mend",
                Targeting = AbilityTargeting.None,
                Delivery = AbilityDelivery.Area,
                Cooldown = 25f,
                CastTime = 0.2f,
                ChannelTime = 2f,
                ChannelInterval = 0.5f,
                AreaRadius = 5f,
                Affects = AbilityAffects.Allies | AbilityAffects.Self,
                Effect = new AbilityEffect { Heal = 15f },
                AiHint = AbilityAiHint.AlliesHurt,
                AiMinCount = 2,
                AiThreshold = 0.7f,
            });
            Add(new AbilityDefinition
            {
                Id = AbilityId.ShieldUp,
                Name = "Shield Up",
                Targeting = AbilityTargeting.None,
                Delivery = AbilityDelivery.Instant,
                Cooldown = 12f,
                CastTime = 0f,
                Affects = AbilityAffects.Self,
                Effect = new AbilityEffect { Status = StatusType.Shield, StatusDuration = 3f, StatusMagnitude = 40f },
                SingleCaster = false,
                AiHint = AbilityAiHint.SelfLowHealth,
                AiThreshold = 0.6f,
            });
            Add(new AbilityDefinition
            {
                Id = AbilityId.Volley,
                Name = "Volley",
                Targeting = AbilityTargeting.Point,
                Delivery = AbilityDelivery.Area,
                Range = 9f,
                Cooldown = 9f,
                CastTime = 0.3f,
                AreaRadius = 2.5f,
                AreaDelay = 0.8f,
                Effect = new AbilityEffect { Damage = 25f },
                AiHint = AbilityAiHint.EnemyCluster,
                AiMinCount = 3,
            });
        }

        public static IEnumerable<AbilityDefinition> All => Definitions.Values;

        /// <summary>정의가 없으면 예외 (없는 ID 는 TryGet 으로 확인).</summary>
        public static AbilityDefinition Get(AbilityId id) => Definitions[id];

        public static bool TryGet(AbilityId id, out AbilityDefinition definition) => Definitions.TryGetValue(id, out definition);

        /// <summary>유닛 종류가 가진 능력 (슬롯 순서).</summary>
        public static IReadOnlyList<AbilityId> LoadoutOf(UnitKind kind) => Loadouts.TryGetValue(kind, out var l) ? l : NoAbilities;

        private static void Add(AbilityDefinition d) => Definitions.Add(d.Id, d);
    }
}
