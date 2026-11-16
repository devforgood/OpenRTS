using OpenRTS.Client.Net;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;
using UnityEngine;

namespace OpenRTS.Client.Debugging
{
    /// <summary>
    /// F2: 서버 시뮬레이션 설정 패널. 값을 고친 뒤 Apply 를 누르면 서버에 요청하고,
    /// 서버가 적용한 값이 모든 클라이언트에 전파된다. 아래쪽에 서버가 계산한 충돌 지표를 보여준다.
    /// </summary>
    public sealed class SimSettingsPanel : MonoBehaviour
    {
        private const int WindowId = 0x0E75;

        public GameClient Client;
        public bool Visible = true;

        private Rect _rect = new Rect(0, 10, 380, 0);
        private SimSettings _draft;
        private bool _hasDraft;
        private bool _dirty;
        private GUIStyle _statsStyle;

        /// <summary>창 영역 (GUI 좌표, 숨기면 빈 영역). 이 위의 클릭은 유닛 선택으로 치지 않는다.</summary>
        public static Rect Area { get; private set; }

        private void Start()
        {
            _rect.x = Screen.width - _rect.width - 10;
            Client.SettingsChanged += OnServerSettings;
        }

        private void OnDestroy()
        {
            if (Client != null)
            {
                Client.SettingsChanged -= OnServerSettings;
            }
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.F2))
            {
                Visible = !Visible;
            }
        }

        private void OnServerSettings(SimSettings settings)
        {
            if (!_dirty)
            {
                _draft = settings;
                _hasDraft = true;
            }
        }

        private void OnGUI()
        {
            if (!Visible || Client == null)
            {
                Area = Rect.zero;
                return;
            }

            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "Simulation (F2)", GUILayout.Width(_rect.width));
            Area = _rect;
        }

        private void DrawWindow(int id)
        {
            if (!_hasDraft)
            {
                GUILayout.Label(Client.State == ClientState.Joined ? "waiting for server settings..." : $"{Client.State}");
                GUI.DragWindow();
                return;
            }

            var before = _draft;

            GUILayout.Label("<b>Map</b>", Rich());
            _draft.Map = (MapKind)GUILayout.Toolbar((int)_draft.Map, new[] { "Open", "Gate", "Blocks" });
            _draft.PlayerUnits = (byte)IntSlider("my units", _draft.PlayerUnits, 1, SimSettings.MaxPlayerUnits);

            GUILayout.Space(4);
            GUILayout.Label("<b>Bots</b>", Rich());
            _draft.BotCount = (ushort)IntSlider("count", _draft.BotCount, 0, SimSettings.MaxBots);
            _draft.Scenario = (BotScenario)GUILayout.Toolbar((int)_draft.Scenario, new[] { "Wander", "Conv", "Circle", "Cross", "Pass", "Battle" });

            GUILayout.Space(4);
            GUILayout.Label("<b>Unit</b>", Rich());
            _draft.UnitRadius = FloatSlider("radius", _draft.UnitRadius, 0.1f, 2f);
            _draft.UnitSpeed = FloatSlider("speed", _draft.UnitSpeed, 0.5f, 20f);

            GUILayout.Space(4);
            GUILayout.Label("<b>Collision</b>", Rich());
            _draft.Avoidance = GUILayout.Toggle(_draft.Avoidance, " RVO2 avoidance (ORCA)");
            GUI.enabled = _draft.Avoidance;
            _draft.NeighborDist = FloatSlider("neighbor dist", _draft.NeighborDist, 1f, 30f);
            _draft.MaxNeighbors = (byte)IntSlider("max neighbors", _draft.MaxNeighbors, 1, 32);
            _draft.TimeHorizon = FloatSlider("time horizon", _draft.TimeHorizon, 0.1f, 10f);
            GUI.enabled = true;
            _draft.Separation = GUILayout.Toggle(_draft.Separation, " separation (push residual overlap)");
            _draft.StopWhenBlocked = GUILayout.Toggle(_draft.StopWhenBlocked, " stop when blocked");
            _draft.RepathWhenBlocked = GUILayout.Toggle(_draft.RepathWhenBlocked, " repath around idle units when blocked");

            GUILayout.Space(4);
            GUILayout.Label("<b>Network</b>", Rich());
            _draft.DeltaSnapshots = GUILayout.Toggle(_draft.DeltaSnapshots, " delta snapshots (send changed units only)");

            if (!_draft.Equals(before))
            {
                _dirty = true;
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUI.enabled = _dirty;
            if (GUILayout.Button(_dirty ? "<b>Apply</b>" : "Apply", Button()))
            {
                Client.SendSettings(_draft);
                _dirty = false; // 서버 값이 오면 그 값으로 덮어쓴다
            }

            if (GUILayout.Button("Revert") && Client.Settings.HasValue)
            {
                _draft = Client.Settings.Value;
                _dirty = false;
            }

            GUI.enabled = true;
            if (GUILayout.Button("Defaults"))
            {
                var keep = _draft;
                _draft = SimSettings.Default;
                _draft.BotCount = keep.BotCount;
                _draft.Scenario = keep.Scenario;
                _draft.Map = keep.Map;
                _draft.PlayerUnits = keep.PlayerUnits;
                _dirty = true;
            }

            if (GUILayout.Button("Restart"))
            {
                Client.SendResetScenario();
            }

            GUILayout.EndHorizontal();

            GUILayout.Label("Presets (apply immediately)");
            GUILayout.BeginHorizontal();
            Preset("Circle 100", BotScenario.CircleSwap, 100, MapKind.Open);
            Preset("Circle 300", BotScenario.CircleSwap, 300, MapKind.Open);
            Preset("Cross 200", BotScenario.Crossing, 200, MapKind.Open);
            Preset("Conv 60", BotScenario.Converge, 60, MapKind.Open);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Preset("Gate 50", BotScenario.Pass, 50, MapKind.Gate);
            Preset("Gate 150", BotScenario.Pass, 150, MapKind.Gate);
            Preset("Blocks 200", BotScenario.Wander, 200, MapKind.Blocks);
            Preset("Gate (me)", BotScenario.Wander, 0, MapKind.Gate);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Preset("Battle 60", BotScenario.Battle, 60, MapKind.Open);
            Preset("Battle 200", BotScenario.Battle, 200, MapKind.Open);
            Preset("Battle Gate", BotScenario.Battle, 200, MapKind.Gate);
            Preset("Battle Blocks", BotScenario.Battle, 200, MapKind.Blocks);
            GUILayout.EndHorizontal();


            DrawStats();
            GUI.DragWindow();
        }

        private void Preset(string label, BotScenario scenario, int bots, MapKind map)
        {
            if (GUILayout.Button(label))
            {
                _draft.Scenario = scenario;
                _draft.BotCount = (ushort)bots;
                _draft.Map = map;
                Client.SendSettings(_draft);
                _dirty = false;
            }
        }

        private void DrawStats()
        {
            GUILayout.Space(6);
            _statsStyle ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true };
            if (!Client.Stats.HasValue)
            {
                GUILayout.Box("no stats yet", _statsStyle);
                return;
            }

            var s = Client.Stats.Value;
            var sepColor = s.MinSeparationRatio >= 0.95f ? "lime" : s.MinSeparationRatio >= 0.8f ? "yellow" : "red";
            var overlapColor = s.OverlapPairs == 0 ? "lime" : "orange";
            GUILayout.Box(
                $"units {s.UnitCount}  moving {s.MovingUnits}\n" +
                $"min separation <color={sepColor}>{s.MinSeparationRatio:0.000}</color>  (distance / 2r, ≥1 no overlap)\n" +
                $"overlapping pairs <color={overlapColor}>{s.OverlapPairs}</color>  (>5% penetration)\n" +
                $"blocked stops {s.BlockedStops}   repaths {s.Repaths}   wall hits <color={(s.WallPenetrations == 0 ? "lime" : "red")}>{s.WallPenetrations}</color>\n" +
                $"server step {s.StepMs:0.00} ms\n" +
                $"snapshot {s.SnapshotBytesPerSec / 1024f:0.0} KB/s per client (delta {s.DeltaRatio * 100:0}%)\n" +
                $"snapshot parts lost {Client.SnapshotsIncomplete}  no baseline {Client.SnapshotsMissingBaseline}",

                _statsStyle);
        }

        private static int IntSlider(string label, int value, int min, int max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(95));
            var v = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max));
            var text = GUILayout.TextField(v.ToString(), GUILayout.Width(45));
            if (int.TryParse(text, out var typed))
            {
                v = Mathf.Clamp(typed, min, max);
            }

            GUILayout.EndHorizontal();
            return v;
        }

        private static float FloatSlider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(95));
            var v = GUILayout.HorizontalSlider(value, min, max);
            v = Mathf.Round(v * 100f) / 100f;
            GUILayout.Label(v.ToString("0.00"), GUILayout.Width(45));
            GUILayout.EndHorizontal();
            return v;
        }

        private static GUIStyle Rich() => new GUIStyle(GUI.skin.label) { richText = true };

        private static GUIStyle Button() => new GUIStyle(GUI.skin.button) { richText = true };
    }
}
