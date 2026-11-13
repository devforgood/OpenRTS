using OpenRTS.Shared;
using UnityEngine;

namespace OpenRTS.Client
{
    /// <summary>클라이언트 진입점. 지금은 Shared 패키지 연결 확인용 로그만 남긴다.</summary>
    public sealed class ClientBootstrap : MonoBehaviour
    {
        private void Start()
        {
            Debug.Log($"OpenRTS client (protocol v{SimConstants.ProtocolVersion}, server tick {SimConstants.TickRate}Hz)");
        }
    }
}
