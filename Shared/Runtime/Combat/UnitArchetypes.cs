namespace OpenRTS.Shared.Combat
{
    /// <summary>
    /// 유닛 종류. 움직이는 유닛(근접·원거리·일꾼)은 반지름·속도가 모두 같고(SimSettings) 전투 수치만 다르다.
    /// 건물·자원은 움직이지 않고, 사각형 발판(<see cref="UnitArchetype.Footprint"/>)이 길찾기·충돌에서 벽처럼 동작한다.
    /// </summary>
    public enum UnitKind : byte
    {
        /// <summary>근접: 체력이 많고 붙어서 즉시 타격.</summary>
        Melee = 0,

        /// <summary>원거리: 체력이 적고 투사체를 쏜다.</summary>
        Ranged = 1,

        /// <summary>일꾼: 자원을 캐서 본진에 나르고, 건물을 짓는다. 약한 근접 공격.</summary>
        Worker = 2,

        /// <summary>본진: 일꾼 생산, 자원 반납 지점.</summary>
        Headquarters = 3,

        /// <summary>병영: 일꾼이 짓는다. 근접·원거리 생산.</summary>
        Barracks = 4,

        /// <summary>광맥: 중립 자원. 캐면 줄어들고 다 캐면 사라진다.</summary>
        OreField = 5,
    }

    /// <summary>유닛 종류별 수치. 거리는 "가장자리 사이" 기준 (중심 거리 - 두 유닛의 크기).</summary>
    public readonly struct UnitArchetype
    {
        public readonly float MaxHp;
        public readonly float Damage;

        /// <summary>공격 사거리 (가장자리 사이 거리).</summary>
        public readonly float Range;

        /// <summary>공격 시작부터 다음 공격 시작까지(초).</summary>
        public readonly float Cooldown;

        /// <summary>선딜: 공격 시작 후 타격(또는 발사)까지(초). 이 동안 움직이지 않는다.</summary>
        public readonly float Windup;

        /// <summary>스스로 적을 찾는 거리 (가장자리 사이).</summary>
        public readonly float AcquisitionRange;

        /// <summary>투사체 속도. 0 이면 즉시 타격(근접).</summary>
        public readonly float ProjectileSpeed;

        /// <summary>시야 반지름 (유닛 중심 기준). 획득 거리보다 커야 보이지 않는 적을 노리지 않는다.</summary>
        public readonly float SightRange;

        /// <summary>생산·건설 비용 (자원).</summary>
        public readonly int Cost;

        /// <summary>생산·건설 시간(초).</summary>
        public readonly float BuildTime;

        /// <summary>건물·자원의 정사각 발판 반폭. 0 이면 움직이는 유닛.</summary>
        public readonly float Footprint;

        public UnitArchetype(float maxHp, float damage, float range, float cooldown, float windup, float acquisitionRange,
            float projectileSpeed, float sightRange, int cost = 0, float buildTime = 0, float footprint = 0)
        {
            MaxHp = maxHp;
            Damage = damage;
            Range = range;
            Cooldown = cooldown;
            Windup = windup;
            AcquisitionRange = acquisitionRange;
            ProjectileSpeed = projectileSpeed;
            SightRange = sightRange;
            Cost = cost;
            BuildTime = buildTime;
            Footprint = footprint;
        }

        public bool IsRanged => ProjectileSpeed > 0;

        /// <summary>움직이지 않는 건물·자원.</summary>
        public bool IsStatic => Footprint > 0;

        /// <summary>공격할 수 있는 유닛.</summary>
        public bool CanAttack => Damage > 0;
    }

    public static class UnitArchetypes
    {
        /// <summary>팀 0 은 중립(전투 안 함). 1 이상끼리 팀이 다르면 적.</summary>
        public const byte NeutralTeam = 0;

        public static readonly UnitArchetype Melee = new UnitArchetype(
            maxHp: 120, damage: 12, range: 0.3f, cooldown: 1.0f, windup: 0.25f, acquisitionRange: 8f, projectileSpeed: 0, sightRange: 10f,
            cost: 60, buildTime: 8f);

        public static readonly UnitArchetype Ranged = new UnitArchetype(
            maxHp: 70, damage: 9, range: 6f, cooldown: 1.25f, windup: 0.3f, acquisitionRange: 9f, projectileSpeed: 18f, sightRange: 11f,
            cost: 75, buildTime: 9f);

        public static readonly UnitArchetype Worker = new UnitArchetype(
            maxHp: 40, damage: 4, range: 0.3f, cooldown: 1.2f, windup: 0.25f, acquisitionRange: 3f, projectileSpeed: 0, sightRange: 8f,
            cost: 50, buildTime: 6f);

        public static readonly UnitArchetype Headquarters = new UnitArchetype(
            maxHp: 1500, damage: 0, range: 0, cooldown: 0, windup: 0, acquisitionRange: 0, projectileSpeed: 0, sightRange: 10f,
            cost: 400, buildTime: 60f, footprint: 2f);

        public static readonly UnitArchetype Barracks = new UnitArchetype(
            maxHp: 700, damage: 0, range: 0, cooldown: 0, windup: 0, acquisitionRange: 0, projectileSpeed: 0, sightRange: 8f,
            cost: 150, buildTime: 15f, footprint: 1.5f);

        public static readonly UnitArchetype OreField = new UnitArchetype(
            maxHp: 1, damage: 0, range: 0, cooldown: 0, windup: 0, acquisitionRange: 0, projectileSpeed: 0, sightRange: 0,
            footprint: 0.6f);

        public static UnitArchetype Of(UnitKind kind)
        {
            switch (kind)
            {
                case UnitKind.Ranged: return Ranged;
                case UnitKind.Worker: return Worker;
                case UnitKind.Headquarters: return Headquarters;
                case UnitKind.Barracks: return Barracks;
                case UnitKind.OreField: return OreField;
                default: return Melee;
            }
        }

        public static bool AreEnemies(byte teamA, byte teamB) => teamA != NeutralTeam && teamB != NeutralTeam && teamA != teamB;

        /// <summary><paramref name="producer"/> 건물이 <paramref name="product"/> 를 생산할 수 있는지.</summary>
        public static bool CanProduce(UnitKind producer, UnitKind product)
        {
            switch (producer)
            {
                case UnitKind.Headquarters: return product == UnitKind.Worker;
                case UnitKind.Barracks: return product == UnitKind.Melee || product == UnitKind.Ranged;
                default: return false;
            }
        }

        /// <summary>일꾼이 지을 수 있는 건물인지.</summary>
        public static bool CanBuild(UnitKind kind) => kind == UnitKind.Barracks;
    }

    /// <summary>경제 수치.</summary>
    public static class EconomyRules
    {
        public const int StartingOre = 100;
        public const int StartingWorkers = 4;
        public const int OrePerField = 800;
        public const int FieldsPerBase = 8;

        /// <summary>한 번에 캐서 나르는 양과 캐는 데 걸리는 시간(초).</summary>
        public const int GatherAmount = 5;
        public const float GatherTime = 1f;

        /// <summary>건물 하나의 생산 대기열 길이.</summary>
        public const int MaxQueue = 5;

        /// <summary>일꾼이 광맥·본진·건설 현장에 이만큼(가장자리 사이) 붙으면 작업한다.</summary>
        public const float WorkReach = 0.6f;
    }
}
