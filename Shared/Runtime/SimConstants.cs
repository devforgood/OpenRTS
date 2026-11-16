namespace OpenRTS.Shared
{
    /// <summary>서버와 클라이언트가 동일하게 사용해야 하는 시뮬레이션 상수.</summary>
    public static class SimConstants
    {
        /// <summary>서버 시뮬레이션 틱 레이트(Hz).</summary>
        public const int TickRate = 30;

        /// <summary>틱 하나의 길이(초).</summary>
        public const float TickDelta = 1f / TickRate;

        /// <summary>위치 스냅샷 전송 주기(Hz). TickRate 의 약수여야 한다.</summary>
        public const int SnapshotRate = 15;

        /// <summary>스냅샷을 보내는 틱 간격.</summary>
        public const int TicksPerSnapshot = TickRate / SnapshotRate;

        /// <summary>기본 서버 포트.</summary>
        public const int DefaultPort = 7777;

        /// <summary>LiteNetLib 접속 키. 다른 앱의 접속을 걸러내는 용도이며 보안 수단이 아니다.</summary>
        public const string ConnectionKey = "OpenRTS";

        /// <summary>프로토콜 버전. 서버와 클라이언트가 다르면 접속을 거부한다.</summary>
        public const int ProtocolVersion = 9;

        /// <summary>유닛 기본 이동 속도(유닛/초). 실행 중 값은 SimSettings.UnitSpeed.</summary>
        public const float UnitSpeed = 6f;

        /// <summary>맵 반폭. 맵은 (-MapHalfSize, -MapHalfSize) ~ (MapHalfSize, MapHalfSize).</summary>
        public const float MapHalfSize = 50f;
    }
}
