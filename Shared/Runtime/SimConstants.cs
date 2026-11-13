namespace OpenRTS.Shared
{
    /// <summary>서버와 클라이언트가 동일하게 사용해야 하는 시뮬레이션 상수.</summary>
    public static class SimConstants
    {
        /// <summary>서버 시뮬레이션 틱 레이트(Hz).</summary>
        public const int TickRate = 30;

        /// <summary>틱 하나의 길이(초).</summary>
        public const float TickDelta = 1f / TickRate;

        /// <summary>기본 서버 포트.</summary>
        public const int DefaultPort = 7777;

        /// <summary>프로토콜 버전. 서버와 클라이언트가 다르면 접속을 거부한다.</summary>
        public const int ProtocolVersion = 1;
    }
}
