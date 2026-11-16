namespace OpenRTS.Shared.Combat
{
    /// <summary>유닛 종류. 반지름·속도는 모든 종류가 같고(SimSettings), 전투 수치만 다르다.</summary>
    public enum UnitKind : byte
    {
        /// <summary>근접: 체력이 많고 붙어서 즉시 타격.</summary>
        Melee = 0,

        /// <summary>원거리: 체력이 적고 투사체를 쏜다.</summary>
        Ranged = 1,
    }

    /// <summary>유닛 종류별 전투 수치. 거리는 "가장자리 사이" 기준 (중심 거리 - 두 반지름).</summary>
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

        public UnitArchetype(float maxHp, float damage, float range, float cooldown, float windup, float acquisitionRange, float projectileSpeed)
        {
            MaxHp = maxHp;
            Damage = damage;
            Range = range;
            Cooldown = cooldown;
            Windup = windup;
            AcquisitionRange = acquisitionRange;
            ProjectileSpeed = projectileSpeed;
        }

        public bool IsRanged => ProjectileSpeed > 0;
    }

    public static class UnitArchetypes
    {
        /// <summary>팀 0 은 중립(전투 안 함). 1 이상끼리 팀이 다르면 적.</summary>
        public const byte NeutralTeam = 0;

        public static readonly UnitArchetype Melee = new UnitArchetype(
            maxHp: 120, damage: 12, range: 0.3f, cooldown: 1.0f, windup: 0.25f, acquisitionRange: 8f, projectileSpeed: 0);

        public static readonly UnitArchetype Ranged = new UnitArchetype(
            maxHp: 70, damage: 9, range: 6f, cooldown: 1.25f, windup: 0.3f, acquisitionRange: 9f, projectileSpeed: 18f);

        public static UnitArchetype Of(UnitKind kind) => kind == UnitKind.Ranged ? Ranged : Melee;

        public static bool AreEnemies(byte teamA, byte teamB) => teamA != NeutralTeam && teamB != NeutralTeam && teamA != teamB;
    }
}
