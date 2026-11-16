using System.Collections.Generic;
using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using OpenRTS.Shared.Protocol;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.Controls
{
    /// <summary>
    /// RTS 조작.
    /// <list type="bullet">
    /// <item>좌클릭: 내 유닛 하나 선택 / 좌드래그: 박스 선택 / Shift: 추가 선택, Ctrl+A: 내 유닛 전체</item>
    /// <item>우클릭: 땅이면 이동(가는 동안 적 무시, 서버가 대형을 짠다), 적 유닛이면 그 유닛 공격</item>
    /// <item>Ctrl+우클릭: 공격 이동 (가면서 만나는 적과 싸움) / H: 정지</item>
    /// <item>Ctrl+1~9: 부대 지정, 1~9: 부대 선택</item>
    /// <item>WASD/화살표: 카메라 이동, 휠: 줌</item>
    /// </list>
    /// </summary>
    public sealed class RtsInput : MonoBehaviour
    {
        private const float DragThreshold = 6f; // 픽셀
        private const float MarkerSeconds = 0.5f;

        public GameClient Client;
        public UnitViewManager Views;
        public Camera Camera;
        public float PanSpeed = 30f;
        public float ZoomSpeed = 8f;

        private readonly List<int> _hits = new List<int>();
        private Transform _marker;
        private float _markerTime;
        private Vector3 _dragStart;
        private bool _dragging;
        private bool _autoSelected;
        private Texture2D _boxTexture;

        public UnitSelection Selection { get; } = new UnitSelection();

        private void Update()
        {
            if (Camera == null || Client == null)
            {
                return;
            }

            Selection.Prune(Client.IsMine);

            // 처음 내 유닛이 보이면 전부 선택해 둔다 (바로 우클릭으로 움직여 볼 수 있게).
            if (!_autoSelected && Client.MyUnits.Count > 0)
            {
                Selection.Set(Client.MyUnits, additive: false);
                _autoSelected = true;
            }

            HandleSelection();
            HandleGroups();

            if (UnityEngine.Input.GetMouseButtonDown(1) && TryGetGroundPoint(UnityEngine.Input.mousePosition, out var point) && Selection.Count > 0)
            {
                var ground = new Vector2(point.x, point.z);
                var enemy = PickUnit(UnityEngine.Input.mousePosition, Client.IsEnemy);
                if (enemy >= 0)
                {
                    Client.SendCommand(CommandType.Attack, Selection.Ids, ground, enemy);
                    ShowMarker(UnitViewManager.ToWorld(Views.InterpolatedPositions[enemy]), AttackMarkerColor);
                }
                else if (Ctrl())
                {
                    Client.SendCommand(CommandType.AttackMove, Selection.Ids, ground, 0);
                    ShowMarker(point, AttackMarkerColor);
                }
                else
                {
                    Client.SendMove(Selection.Ids, ground);
                    ShowMarker(point, MoveMarkerColor);
                }
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.H) && Selection.Count > 0)
            {
                Client.SendCommand(CommandType.Stop, Selection.Ids, default, 0);
            }

            var pan = new Vector3(UnityEngine.Input.GetAxisRaw("Horizontal"), 0, UnityEngine.Input.GetAxisRaw("Vertical"));
            Camera.transform.position += pan * (PanSpeed * Time.unscaledDeltaTime);

            var scroll = UnityEngine.Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                var pos = Camera.transform.position + Camera.transform.forward * (scroll * ZoomSpeed);
                if (pos.y > 5f && pos.y < 120f)
                {
                    Camera.transform.position = pos;
                }
            }

            UpdateMarker();
        }

        private void HandleSelection()
        {
            var mouse = UnityEngine.Input.mousePosition;
            var additive = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);

            if (UnityEngine.Input.GetMouseButtonDown(0) && !IsOverDebugWindow(mouse))
            {
                _dragStart = mouse;
                _dragging = true;
            }

            if (_dragging && UnityEngine.Input.GetMouseButtonUp(0))
            {
                _dragging = false;
                if ((mouse - _dragStart).magnitude < DragThreshold)
                {
                    ClickSelect(mouse, additive);
                }
                else
                {
                    BoxSelect(ScreenRect(_dragStart, mouse), additive);
                }
            }

            if (Ctrl() && UnityEngine.Input.GetKeyDown(KeyCode.A))
            {
                Selection.Set(Client.MyUnits, additive: false);
            }
        }

        private void HandleGroups()
        {
            for (var n = 1; n <= 9; n++)
            {
                if (!UnityEngine.Input.GetKeyDown(KeyCode.Alpha0 + n))
                {
                    continue;
                }

                if (Ctrl())
                {
                    Selection.SaveGroup(n);
                }
                else
                {
                    Selection.RecallGroup(n, UnityEngine.Input.GetKey(KeyCode.LeftShift));
                }
            }
        }

        private void ClickSelect(Vector3 mouse, bool additive)
        {
            var best = PickUnit(mouse, Client.IsMine);
            if (best < 0)
            {
                if (!additive)
                {
                    Selection.Clear();
                }
            }
            else if (additive)
            {
                Selection.Toggle(best);
            }
            else
            {
                Selection.Set(new[] { best }, additive: false);
            }
        }

        /// <summary>마우스 아래(화면 거리 기준)에서 조건에 맞는 가장 가까운 유닛. 없으면 -1.</summary>
        private int PickUnit(Vector3 mouse, System.Func<int, bool> match)
        {
            var best = -1;
            var bestDist = float.MaxValue;
            var radius = Client.Settings?.UnitRadius ?? 0.5f;
            foreach (var kv in Views.InterpolatedPositions)
            {
                if (!match(kv.Key))
                {
                    continue;
                }

                var center = Camera.WorldToScreenPoint(UnitViewManager.ToWorld(kv.Value, radius));
                var edge = Camera.WorldToScreenPoint(UnitViewManager.ToWorld(kv.Value + new Vector2(radius, 0), radius));
                var pickRadius = Mathf.Max(12f, Vector3.Distance(center, edge) * 1.5f);
                var d = Vector2.Distance(new Vector2(center.x, center.y), new Vector2(mouse.x, mouse.y));
                if (center.z > 0 && d < pickRadius && d < bestDist)
                {
                    best = kv.Key;
                    bestDist = d;
                }
            }

            return best;
        }

        private void BoxSelect(Rect rect, bool additive)
        {
            _hits.Clear();
            var radius = Client.Settings?.UnitRadius ?? 0.5f;
            foreach (var kv in Views.InterpolatedPositions)
            {
                if (!Client.IsMine(kv.Key))
                {
                    continue;
                }

                var p = Camera.WorldToScreenPoint(UnitViewManager.ToWorld(kv.Value, radius));
                if (p.z > 0 && rect.Contains(new UnityEngine.Vector2(p.x, p.y)))
                {
                    _hits.Add(kv.Key);
                }
            }

            Selection.Set(_hits, additive);
        }

        private void OnGUI()
        {
            if (!_dragging)
            {
                return;
            }

            var mouse = UnityEngine.Input.mousePosition;
            if ((mouse - _dragStart).magnitude < DragThreshold)
            {
                return;
            }

            if (_boxTexture == null)
            {
                _boxTexture = new Texture2D(1, 1);
                _boxTexture.SetPixel(0, 0, Color.white);
                _boxTexture.Apply();
            }

            // 화면 좌표(원점 왼쪽 아래) → GUI 좌표(원점 왼쪽 위)
            var r = ScreenRect(_dragStart, mouse);
            var gui = new Rect(r.x, Screen.height - r.yMax, r.width, r.height);
            var old = GUI.color;
            GUI.color = new Color(0.3f, 1f, 0.4f, 0.15f);
            GUI.DrawTexture(gui, _boxTexture);
            GUI.color = new Color(0.3f, 1f, 0.4f, 0.9f);
            GUI.DrawTexture(new Rect(gui.x, gui.y, gui.width, 1), _boxTexture);
            GUI.DrawTexture(new Rect(gui.x, gui.yMax - 1, gui.width, 1), _boxTexture);
            GUI.DrawTexture(new Rect(gui.x, gui.y, 1, gui.height), _boxTexture);
            GUI.DrawTexture(new Rect(gui.xMax - 1, gui.y, 1, gui.height), _boxTexture);
            GUI.color = old;
        }

        private static Rect ScreenRect(Vector3 a, Vector3 b) =>
            Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        private static bool Ctrl() => UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);

        /// <summary>디버그 창(HUD·설정 패널) 위의 클릭은 선택으로 치지 않는다.</summary>
        private static bool IsOverDebugWindow(Vector3 mouse)
        {
            var gui = new UnityEngine.Vector2(mouse.x, Screen.height - mouse.y);
            return Debugging.NetDebugHud.Area.Contains(gui) || Debugging.SimSettingsPanel.Area.Contains(gui);
        }


        private bool TryGetGroundPoint(Vector3 screen, out Vector3 point)
        {
            var ray = Camera.ScreenPointToRay(screen);
            var ground = new Plane(Vector3.up, Vector3.zero);
            if (ground.Raycast(ray, out var distance))
            {
                point = ray.GetPoint(distance);
                return true;
            }

            point = default;
            return false;
        }

        private static readonly Color MoveMarkerColor = new Color(0.3f, 1f, 0.4f);
        private static readonly Color AttackMarkerColor = new Color(1f, 0.3f, 0.25f);

        private void ShowMarker(Vector3 point, Color color)
        {
            if (_marker == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "Command Marker";
                Destroy(go.GetComponent<Collider>());
                _marker = go.transform;
            }

            _marker.GetComponent<Renderer>().material.color = color;
            _marker.position = point + Vector3.up * 0.02f;

            _marker.gameObject.SetActive(true);
            _markerTime = 0;
        }

        private void UpdateMarker()
        {
            if (_marker == null || !_marker.gameObject.activeSelf)
            {
                return;
            }

            // 명령 즉시 피드백: 서버 응답(RTT) 전에도 찍은 자리가 바로 보인다.
            _markerTime += Time.unscaledDeltaTime;
            var s = Mathf.Lerp(1.2f, 0.2f, _markerTime / MarkerSeconds);
            _marker.localScale = new Vector3(s, 0.02f, s);
            if (_markerTime > MarkerSeconds)
            {
                _marker.gameObject.SetActive(false);
            }
        }
    }
}
