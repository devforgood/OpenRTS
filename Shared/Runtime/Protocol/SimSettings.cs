using System;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Serialization;

namespace OpenRTS.Shared.Protocol
{
    /// <summary>봇 유닛 배치·이동 패턴. 충돌 회피 확인용.</summary>
    public enum BotScenario : byte
    {
        /// <summary>무작위 지점을 돌아다닌다.</summary>
        Wander = 0,

        /// <summary>원 위에서 중앙으로 모였다가 돌아간다 (도착 지점 혼잡).</summary>
        Converge = 1,

        /// <summary>원 위에서 반대편으로 동시에 이동한다 (정면 교차, RVO 표준 시나리오).</summary>
        CircleSwap = 2,

        /// <summary>두 무리가 직각으로 교차한다.</summary>
        Crossing = 3,

        /// <summary>한 무리가 맵 왼쪽에서 오른쪽으로 건너간다 (Gate 맵에서 좁은 통로 통과).</summary>
        Pass = 4,

        /// <summary>
        /// 봇을 두 팀(왼쪽 1팀, 오른쪽 2팀)으로 나눠 근접·원거리 반반으로 배치하고, 서로 공격 이동시킨다.
        /// 한 팀이 전멸하면 몇 초 뒤 다시 시작한다. 플레이어는 들어온 순서대로 1팀·2팀에 나뉘어 함께 싸운다.
        /// </summary>
        Battle = 5,
    }

    /// <summary>
    /// 서버가 실행 중에 바꿀 수 있는 시뮬레이션 설정. 서버가 권위를 가지며,
    /// 클라이언트는 디버그 패널에서 변경 요청(SetSimSettings)만 보낸다.
    /// </summary>
    public struct SimSettings : IEquatable<SimSettings>
    {
        public const int MaxBots = 600;
        public const int MaxPlayerUnits = 100;

        public ushort BotCount;
        public BotScenario Scenario;

        /// <summary>맵(벽 배치). 바꾸면 모든 유닛을 다시 배치한다.</summary>
        public MapKind Map;

        /// <summary>플레이어 한 명이 받는 유닛 수. 바꾸면 플레이어 유닛을 다시 만든다.</summary>
        public byte PlayerUnits;
        public float UnitRadius;
        public float UnitSpeed;

        /// <summary>RVO2(ORCA) 충돌 회피 사용. 끄면 서로 통과한다.</summary>
        public bool Avoidance;

        /// <summary>ORCA 이웃 탐색 거리.</summary>
        public float NeighborDist;

        /// <summary>ORCA 가 고려하는 최대 이웃 수.</summary>
        public byte MaxNeighbors;

        /// <summary>ORCA 시간 지평(초). 길수록 일찍 피하지만 소극적으로 움직인다.</summary>
        public float TimeHorizon;

        /// <summary>일정 시간 목표에 가까워지지 못하면 이동을 멈춘다 (혼잡한 도착 지점에서 영원히 비비는 것 방지).</summary>
        public bool StopWhenBlocked;

        /// <summary>
        /// ORCA 이후에도 남은 겹침을 틱마다 일부 밀어서 푼다. ORCA 는 해가 없을 만큼 밀집하면
        /// "덜 겹치는" 속도를 고르므로, 한 점에 많이 모이는 상황에서는 이 보정이 필요하다.
        /// </summary>
        public bool Separation;

        /// <summary>목표에서 먼 곳에서 막히면 멈춰 있는 유닛이 차지한 칸을 피하는 경로를 다시 찾는다 (최대 3회).</summary>
        public bool RepathWhenBlocked;

        /// <summary>클라이언트가 확인한 스냅샷 대비 바뀐 유닛만 보낸다 (끄면 매번 전체 스냅샷, 대역폭 비교용).</summary>
        public bool DeltaSnapshots;

        public static SimSettings Default => new SimSettings
        {
            BotCount = 3,
            Scenario = BotScenario.Wander,
            Map = MapKind.Open,
            PlayerUnits = 8,
            UnitRadius = 0.5f,
            UnitSpeed = SimConstants.UnitSpeed,
            Avoidance = true,
            NeighborDist = 10f,
            MaxNeighbors = 10,
            TimeHorizon = 2f,
            StopWhenBlocked = true,
            Separation = true,
            RepathWhenBlocked = true,
            DeltaSnapshots = true,
        };

        /// <summary>허용 범위로 자른 값. 서버는 받은 값을 반드시 이걸 거쳐 적용한다.</summary>
        public SimSettings Clamped()
        {
            var s = this;
            s.BotCount = (ushort)Math.Min((int)s.BotCount, MaxBots);
            if (!Enum.IsDefined(typeof(BotScenario), s.Scenario))
            {
                s.Scenario = BotScenario.Wander;
            }

            if (!Enum.IsDefined(typeof(MapKind), s.Map))
            {
                s.Map = MapKind.Open;
            }

            s.PlayerUnits = (byte)Math.Max(1, Math.Min((int)s.PlayerUnits, MaxPlayerUnits));

            s.UnitRadius = Clamp(s.UnitRadius, 0.1f, 3f);
            s.UnitSpeed = Clamp(s.UnitSpeed, 0.5f, 20f);
            s.NeighborDist = Clamp(s.NeighborDist, 1f, 30f);
            s.MaxNeighbors = (byte)Math.Max(1, Math.Min((int)s.MaxNeighbors, 32));
            s.TimeHorizon = Clamp(s.TimeHorizon, 0.1f, 10f);
            return s;
        }

        public void Write(PacketWriter w, MessageType type)
        {
            w.WriteByte((byte)type);
            w.WriteUShort(BotCount);
            w.WriteByte((byte)Scenario);
            w.WriteByte((byte)Map);
            w.WriteByte(PlayerUnits);
            w.WriteFloat(UnitRadius);
            w.WriteFloat(UnitSpeed);
            w.WriteByte(Avoidance ? (byte)1 : (byte)0);
            w.WriteFloat(NeighborDist);
            w.WriteByte(MaxNeighbors);
            w.WriteFloat(TimeHorizon);
            w.WriteByte(StopWhenBlocked ? (byte)1 : (byte)0);
            w.WriteByte(Separation ? (byte)1 : (byte)0);
            w.WriteByte(RepathWhenBlocked ? (byte)1 : (byte)0);
            w.WriteByte(DeltaSnapshots ? (byte)1 : (byte)0);
        }

        /// <summary>메시지 타입 바이트를 읽은 뒤 호출한다.</summary>
        public static SimSettings Read(ref PacketReader r) => new SimSettings
        {
            BotCount = r.ReadUShort(),
            Scenario = (BotScenario)r.ReadByte(),
            Map = (MapKind)r.ReadByte(),
            PlayerUnits = r.ReadByte(),
            UnitRadius = r.ReadFloat(),
            UnitSpeed = r.ReadFloat(),
            Avoidance = r.ReadByte() != 0,
            NeighborDist = r.ReadFloat(),
            MaxNeighbors = r.ReadByte(),
            TimeHorizon = r.ReadFloat(),
            StopWhenBlocked = r.ReadByte() != 0,
            Separation = r.ReadByte() != 0,
            RepathWhenBlocked = r.ReadByte() != 0,
            DeltaSnapshots = r.ReadByte() != 0,
        };

        public bool Equals(SimSettings o) =>
            BotCount == o.BotCount && Scenario == o.Scenario && Map == o.Map && PlayerUnits == o.PlayerUnits && UnitRadius.Equals(o.UnitRadius) &&
            UnitSpeed.Equals(o.UnitSpeed) && Avoidance == o.Avoidance && NeighborDist.Equals(o.NeighborDist) &&
            MaxNeighbors == o.MaxNeighbors && TimeHorizon.Equals(o.TimeHorizon) && StopWhenBlocked == o.StopWhenBlocked &&
            Separation == o.Separation && RepathWhenBlocked == o.RepathWhenBlocked && DeltaSnapshots == o.DeltaSnapshots;

        public override bool Equals(object obj) => obj is SimSettings o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(BotCount, Scenario, Map, PlayerUnits, UnitRadius, UnitSpeed, Avoidance, NeighborDist);

        public override string ToString() =>
            $"map {Map}, bots {BotCount} {Scenario}, player units {PlayerUnits}, r {UnitRadius:0.##}, speed {UnitSpeed:0.##}, avoidance {(Avoidance ? "on" : "off")} " +
            $"(neighborDist {NeighborDist:0.#}, maxNeighbors {MaxNeighbors}, horizon {TimeHorizon:0.##}), " +
            $"separation {(Separation ? "on" : "off")}, stopWhenBlocked {StopWhenBlocked}, repath {RepathWhenBlocked}, delta {DeltaSnapshots}";

        private static float Clamp(float v, float min, float max) => float.IsNaN(v) ? min : Math.Max(min, Math.Min(v, max));
    }

    /// <summary>서버가 주기적으로 보내는 충돌 확인 지표.</summary>
    public struct SimStats
    {
        public ushort UnitCount;

        /// <summary>반지름 합의 5% 넘게 파고든 유닛 쌍 수.</summary>
        public ushort OverlapPairs;

        /// <summary>가장 가까운 두 유닛의 거리 / 반지름 합. 1 이상이면 겹침 없음.</summary>
        public float MinSeparationRatio;

        /// <summary>이동 중인 유닛 수.</summary>
        public ushort MovingUnits;

        /// <summary>시나리오 시작 후 막혀서 멈춘 횟수.</summary>
        public ushort BlockedStops;

        /// <summary>서버 시뮬레이션 1틱 평균 처리 시간(ms).</summary>
        public float StepMs;

        /// <summary>시나리오 시작 후 막혀서 경로를 다시 찾은 횟수.</summary>
        public ushort Repaths;

        /// <summary>벽에 반지름의 5% 넘게 파고든 유닛 수.</summary>
        public ushort WallPenetrations;

        /// <summary>클라이언트 한 명에게 보내는 스냅샷 대역폭(바이트/초, 최근 지표 주기 평균).</summary>
        public float SnapshotBytesPerSec;

        /// <summary>1팀·2팀 생존 유닛 수 (플레이어 유닛 포함).</summary>
        public ushort Team1Alive;
        public ushort Team2Alive;

        /// <summary>시나리오 시작 후 죽은 유닛 수.</summary>
        public ushort Deaths;

        /// <summary>날아가는 중인 투사체 수.</summary>
        public ushort Projectiles;

        /// <summary>Battle 에서 이긴 팀 (진행 중이면 0).</summary>
        public byte Winner;

        /// <summary>최근 받은 명령의 지연 (서버 틱 - 명령의 ClientTick, ms). 대략 편도 지연.</summary>
        public float CommandDelayMs;

        /// <summary>최근 지표 주기 동안 보낸 스냅샷 중 델타(baseline 기준) 비율.</summary>
        public float DeltaRatio;

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)MessageType.SimStats);
            w.WriteUShort(UnitCount);
            w.WriteUShort(OverlapPairs);
            w.WriteFloat(MinSeparationRatio);
            w.WriteUShort(MovingUnits);
            w.WriteUShort(BlockedStops);
            w.WriteFloat(StepMs);
            w.WriteUShort(Repaths);
            w.WriteUShort(WallPenetrations);
            w.WriteFloat(SnapshotBytesPerSec);
            w.WriteUShort(Team1Alive);
            w.WriteUShort(Team2Alive);
            w.WriteUShort(Deaths);
            w.WriteUShort(Projectiles);
            w.WriteByte(Winner);
            w.WriteFloat(CommandDelayMs);
            w.WriteFloat(DeltaRatio);
        }

        public static SimStats Read(ref PacketReader r) => new SimStats
        {
            UnitCount = r.ReadUShort(),
            OverlapPairs = r.ReadUShort(),
            MinSeparationRatio = r.ReadFloat(),
            MovingUnits = r.ReadUShort(),
            BlockedStops = r.ReadUShort(),
            StepMs = r.ReadFloat(),
            Repaths = r.ReadUShort(),
            WallPenetrations = r.ReadUShort(),
            SnapshotBytesPerSec = r.ReadFloat(),
            Team1Alive = r.ReadUShort(),
            Team2Alive = r.ReadUShort(),
            Deaths = r.ReadUShort(),
            Projectiles = r.ReadUShort(),
            Winner = r.ReadByte(),
            CommandDelayMs = r.ReadFloat(),

            DeltaRatio = r.ReadFloat(),
        };
    }
}
