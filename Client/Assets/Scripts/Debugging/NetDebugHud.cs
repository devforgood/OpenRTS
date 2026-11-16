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
        public Controls.UnitSelection Selection;


        private GUIStyle _style;

        /// <summary>HUD 영역 (GUI 좌표). 이 위의 클릭은 유닛 선택으로 치지 않는다.</summary>
        public static Rect Area { get; private set; }

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
            var stats = Client.Stats;
            var text =
                $"<b>OpenRTS</b>  {Client.State}  player {Client.PlayerId} (team {Client.MyTeam})  my units {Client.MyUnits.Count}  selected {Selection?.Count ?? 0}\n" +
                $"RTT {Client.NetworkRttMs} ms + simulated {Client.SimulatedRttMs} ms   loss {Client.SimulatedLossPercent}%" +
                (stats.HasValue ? $"   command delay {stats.Value.CommandDelayMs:0} ms\n" : "\n") +
                (interp == null ? "" :
                    $"tick {interp.LatestTick}  buffered {interp.BufferedCount}  delay {interp.InterpolationDelay * 1000:0} ms (target {interp.TargetInterpolationDelay * 1000:0})\n" +
                    $"snapshots {Client.SnapshotsReceived} (delta {Client.DeltaSnapshotsReceived}, no baseline {Client.SnapshotsMissingBaseline})  out-of-order {interp.DroppedOutOfOrder}  sim-dropped {Client.SnapshotsDroppedBySimulation}\n" +
                    $"{(interp.IsExtrapolating ? "<color=orange>EXTRAPOLATING</color>" : "interpolating")}  units {Views?.InterpolatedPositions.Count}" +
                    (stats.HasValue ? $"  snapshot {stats.Value.SnapshotBytesPerSec / 1024f:0.0} KB/s (delta {stats.Value.DeltaRatio * 100:0}%)\n" : "\n")) +
                "LMB select/drag · RMB move / attack enemy · Ctrl+RMB attack-move · H stop · Ctrl+A all · Ctrl+1-9 group\n" +
                "WASD pan · wheel zoom · [ ] latency · - = loss · F1 raw · F2 settings";
            Area = new Rect(10, 10, 600, 150);
            GUI.Box(Area, text, _style);
            DrawBattle(stats);
        }

        private GUIStyle _bannerStyle;

        /// <summary>팀별 생존 수와 승패 (Battle 이거나 전투가 있을 때).</summary>
        private void DrawBattle(OpenRTS.Shared.Protocol.SimStats? stats)
        {
            if (!stats.HasValue || (stats.Value.Team1Alive == 0 && stats.Value.Team2Alive == 0))
            {
                return;
            }

            var s = stats.Value;
            _bannerStyle ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 18, richText = true };
            var my = Client.MyTeam;
            string Name(int team) => team == my ? $"<color=#6fa0ff>team {team} (you)</color>" : $"<color=#ff7a6a>team {team}</color>";
            string N(ushort v) => v == OpenRTS.Shared.Protocol.SimStats.Unknown ? "?" : v.ToString(); // 안개 때문에 모름
            var text = $"{Name(1)}  {N(s.Team1Alive)}  vs  {N(s.Team2Alive)}  {Name(2)}     deaths {N(s.Deaths)}  projectiles {N(s.Projectiles)}";
            if (s.Winner != 0)
            {
                text += $"\n<b>{(s.Winner == my ? "VICTORY" : my == 0 ? $"team {s.Winner} wins" : "DEFEAT")}</b>  — restarting soon";
            }

            GUI.Box(new Rect(Screen.width / 2f - 300, Screen.height - (s.Winner != 0 ? 70 : 44), 600, s.Winner != 0 ? 60 : 34), text, _bannerStyle);

        }
    }
}
