using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using UnityEngine;

namespace OpenRTS.Client.Debugging
{
    /// <summary>
    /// 동기화 상태 표시와 테스트용 조작.
    /// [ / ] : 시뮬레이션 지연 -/+ 50ms, - / = : 스냅샷 손실 -/+ 5%, F1 : 원본 스냅샷 위치 표시
    /// </summary>
    public sealed class NetDebugHud : MonoBehaviour
    {
        public GameClient Client;
        public UnitViewManager Views;

        private GUIStyle _style;

        private void Update()
        {
            if (Client == null)
            {
                return;
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.RightBracket)) Client.SimulatedRttMs += 50;
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftBracket)) Client.SimulatedRttMs = Mathf.Max(0, Client.SimulatedRttMs - 50);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Equals)) Client.SimulatedLossPercent = Mathf.Min(50, Client.SimulatedLossPercent + 5);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Minus)) Client.SimulatedLossPercent = Mathf.Max(0, Client.SimulatedLossPercent - 5);
        }

        private void OnGUI()
        {
            if (Client == null)
            {
                return;
            }

            _style ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, richText = true };
            var interp = Client.Interpolator;
            var text =
                $"<b>OpenRTS</b>  {Client.State}  unit {Client.MyUnitId}\n" +
                $"RTT {Client.NetworkRttMs} ms + simulated {Client.SimulatedRttMs} ms   loss {Client.SimulatedLossPercent}%\n" +
                (interp == null ? "" :
                    $"tick {interp.LatestTick}  buffered {interp.BufferedCount}  delay {interp.InterpolationDelay * 1000:0} ms (target {interp.TargetInterpolationDelay * 1000:0})\n" +
                    $"snapshots {Client.SnapshotsReceived}  out-of-order {interp.DroppedOutOfOrder}  sim-dropped {Client.SnapshotsDroppedBySimulation}\n" +
                    $"{(interp.IsExtrapolating ? "<color=orange>EXTRAPOLATING</color>" : "interpolating")}  units {Views?.InterpolatedPositions.Count}\n") +
                "RMB move · WASD pan · wheel zoom · [ ] latency · - = loss · F1 raw · F2 settings";
            GUI.Box(new Rect(10, 10, 470, 130), text, _style);
        }
    }
}
