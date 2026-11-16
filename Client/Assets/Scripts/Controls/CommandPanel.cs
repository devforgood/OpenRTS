using System.Text;
using OpenRTS.Client.Net;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Protocol;
using UnityEngine;

namespace OpenRTS.Client.Controls
{
    /// <summary>
    /// 화면 왼쪽 아래 명령 패널: 내 자원, 선택 구성, 선택에 맞는 버튼(일꾼 → 병영 짓기, 본진 → 일꾼, 병영 → 근접·원거리),
    /// 선택한 건물의 생산 대기열과 진행도. 자원·대기열은 서버가 보낸 값(PlayerState)을 그대로 보여 준다.
    /// </summary>
    public sealed class CommandPanel : MonoBehaviour
    {
        public GameClient Client;
        public RtsInput Input;

        private GUIStyle _box;
        private GUIStyle _button;
        private readonly StringBuilder _text = new StringBuilder();

        /// <summary>패널 영역 (GUI 좌표). 이 위의 클릭은 유닛 선택으로 치지 않는다.</summary>
        public static Rect Area { get; private set; }

        private void OnGUI()
        {
            if (Client == null || Input == null || !Client.HasPlayerState)
            {
                Area = Rect.zero;
                return;
            }

            _box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true, fontSize = 14 };
            _button ??= new GUIStyle(GUI.skin.button) { richText = true, fontSize = 13 };

            var workers = Input.SelectedCount(UnitKind.Worker);
            var hqs = Input.SelectedCount(UnitKind.Headquarters);
            var barracks = Input.SelectedCount(UnitKind.Barracks);

            var rect = new Rect(10, Screen.height - 190, 420, 180);
            Area = rect;
            GUILayout.BeginArea(rect, _box);
            GUILayout.Label($"<b><color=#7fe0f0>Ore {Client.Ore}</color></b>    selected {Input.Selection.Count}" +
                            (workers > 0 ? $" · workers {workers}" : "") + (hqs > 0 ? $" · HQ {hqs}" : "") + (barracks > 0 ? $" · barracks {barracks}" : ""), _box);

            GUILayout.BeginHorizontal();
            if (workers > 0)
            {
                Button($"Build Barracks ({UnitArchetypes.Barracks.Cost})  [B]", UnitArchetypes.Barracks.Cost, () => Input.BeginPlacement(UnitKind.Barracks));
            }

            if (hqs > 0)
            {
                Button($"Worker ({UnitArchetypes.Worker.Cost})  [Z]", UnitArchetypes.Worker.Cost, () => Input.Train(UnitKind.Worker));
            }

            if (barracks > 0)
            {
                Button($"Melee ({UnitArchetypes.Melee.Cost})  [Z]", UnitArchetypes.Melee.Cost, () => Input.Train(UnitKind.Melee));
                Button($"Ranged ({UnitArchetypes.Ranged.Cost})  [X]", UnitArchetypes.Ranged.Cost, () => Input.Train(UnitKind.Ranged));
            }

            GUILayout.EndHorizontal();

            if (Input.Placing.HasValue)
            {
                GUILayout.Label($"placing {Input.Placing.Value}: left-click to place (Shift: keep placing), right-click / Esc to cancel");
            }

            // 선택한 건물들의 생산 대기열
            _text.Clear();
            foreach (var id in Input.Selection.Ids)
            {
                if (!Client.Queues.TryGetValue(id, out var q) || q.Items == null || q.Items.Length == 0)
                {
                    continue;
                }

                _text.Append($"#{id}: ");
                for (var i = 0; i < q.Items.Length; i++)
                {
                    _text.Append(i == 0 ? $"<b>{q.Items[i]} {q.Progress * 100:0}%</b>" : $" · {q.Items[i]}");
                }

                _text.Append('\n');
            }

            if (_text.Length > 0)
            {
                GUILayout.Label(_text.ToString(), _box);
            }

            GUILayout.EndArea();
        }

        private void Button(string label, int cost, System.Action action)
        {
            var enabled = GUI.enabled;
            GUI.enabled = Client.Ore >= cost;
            if (GUILayout.Button(label, _button))
            {
                action();
            }

            GUI.enabled = enabled;
        }
    }
}
