using System;
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
    }

    /// <summary>
    /// 서버가 실행 중에 바꿀 수 있는 시뮬레이션 설정. 서버가 권위를 가지며,
    /// 클라이언트는 디버그 패널에서 변경 요청(SetSimSettings)만 보낸다.
    /// </summary>
    public struct SimSettings : IEquatable<SimSettings>
    {
        public const int MaxBots = 600;

        public ushort BotCount;
        public BotScenario Scenario;
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

        public static SimSettings Default => new SimSettings
        {
            BotCount = 3,
            Scenario = BotScenario.Wander,
            UnitRadius = 0.5f,
            UnitSpeed = SimConstants.UnitSpeed,
            Avoidance = true,
            NeighborDist = 10f,
            MaxNeighbors = 10,
            TimeHorizon = 2f,
            StopWhenBlocked = true,
            Separation = true,
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
            w.WriteFloat(UnitRadius);
            w.WriteFloat(UnitSpeed);
            w.WriteByte(Avoidance ? (byte)1 : (byte)0);
            w.WriteFloat(NeighborDist);
            w.WriteByte(MaxNeighbors);
            w.WriteFloat(TimeHorizon);
            w.WriteByte(StopWhenBlocked ? (byte)1 : (byte)0);
            w.WriteByte(Separation ? (byte)1 : (byte)0);
        }

        /// <summary>메시지 타입 바이트를 읽은 뒤 호출한다.</summary>
        public static SimSettings Read(ref PacketReader r) => new SimSettings
        {
            BotCount = r.ReadUShort(),
            Scenario = (BotScenario)r.ReadByte(),
            UnitRadius = r.ReadFloat(),
            UnitSpeed = r.ReadFloat(),
            Avoidance = r.ReadByte() != 0,
            NeighborDist = r.ReadFloat(),
            MaxNeighbors = r.ReadByte(),
            TimeHorizon = r.ReadFloat(),
            StopWhenBlocked = r.ReadByte() != 0,
            Separation = r.ReadByte() != 0,
        };

        public bool Equals(SimSettings o) =>
            BotCount == o.BotCount && Scenario == o.Scenario && UnitRadius.Equals(o.UnitRadius) &&
            UnitSpeed.Equals(o.UnitSpeed) && Avoidance == o.Avoidance && NeighborDist.Equals(o.NeighborDist) &&
            MaxNeighbors == o.MaxNeighbors && TimeHorizon.Equals(o.TimeHorizon) && StopWhenBlocked == o.StopWhenBlocked &&
            Separation == o.Separation;

        public override bool Equals(object obj) => obj is SimSettings o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(BotCount, Scenario, UnitRadius, UnitSpeed, Avoidance, NeighborDist, MaxNeighbors, TimeHorizon);

        public override string ToString() =>
            $"bots {BotCount} {Scenario}, r {UnitRadius:0.##}, speed {UnitSpeed:0.##}, avoidance {(Avoidance ? "on" : "off")} " +
            $"(neighborDist {NeighborDist:0.#}, maxNeighbors {MaxNeighbors}, horizon {TimeHorizon:0.##}), " +
            $"separation {(Separation ? "on" : "off")}, stopWhenBlocked {StopWhenBlocked}";

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

        public void Write(PacketWriter w)
        {
            w.WriteByte((byte)MessageType.SimStats);
            w.WriteUShort(UnitCount);
            w.WriteUShort(OverlapPairs);
            w.WriteFloat(MinSeparationRatio);
            w.WriteUShort(MovingUnits);
            w.WriteUShort(BlockedStops);
            w.WriteFloat(StepMs);
        }

        public static SimStats Read(ref PacketReader r) => new SimStats
        {
            UnitCount = r.ReadUShort(),
            OverlapPairs = r.ReadUShort(),
            MinSeparationRatio = r.ReadFloat(),
            MovingUnits = r.ReadUShort(),
            BlockedStops = r.ReadUShort(),
            StepMs = r.ReadFloat(),
        };
    }
}
