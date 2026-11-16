using System.Collections.Generic;
using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using OpenRTS.Shared.Abilities;
using OpenRTS.Shared.Combat;
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
    /// <item>우클릭 광맥: 일꾼 채집 / B: 병영 배치(좌클릭으로 놓기, Shift 로 여러 개) / Z·X: 선택한 건물에서 생산</item>
    /// <item>Q W E R D F: 선택한 유닛의 능력 (대상이 있으면 좌클릭으로 고름, 우클릭·Esc 취소)</item>
    /// <item>화살표: 카메라 이동, 휠: 줌</item>
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

            if (Placing.HasValue)
            {
                UpdatePlacement();
            }
            else if (Targeting.HasValue)
            {
                UpdateAbilityTargeting();
            }
            else
            {
                HandleSelection();
            }

            HandleGroups();
            HandleProductionKeys();
            HandleAbilityKeys();

            if (!Placing.HasValue && !Targeting.HasValue && UnityEngine.Input.GetMouseButtonDown(1) && TryGetGroundPoint(UnityEngine.Input.mousePosition, out var point) && Selection.Count > 0)
            {
                var ground = new Vector2(point.x, point.z);
                var enemy = PickUnit(UnityEngine.Input.mousePosition, Client.IsEnemy);
                var work = enemy < 0 ? PickUnit(UnityEngine.Input.mousePosition, IsWorkTarget) : -1;
                if (enemy >= 0)
                {
                    Client.SendCommand(CommandType.Attack, Selection.Ids, ground, enemy);
                    ShowMarker(UnitViewManager.ToWorld(Views.InterpolatedPositions[enemy]), AttackMarkerColor);
                }
                else if (work >= 0)
                {
                    // 광맥·짓다 만 내 건물: 일꾼은 캐거나 이어 짓고, 다른 유닛은 그 자리로 이동 (서버가 나눔)
                    Client.SendCommand(CommandType.Gather, Selection.Ids, ground, work);
                    ShowMarker(UnitViewManager.ToWorld(Views.InterpolatedPositions[work]), WorkMarkerColor);
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

            // 카메라는 화살표 키 (WASD 는 Q W E R D F 능력 키와 겹쳐서 쓰지 않는다)
            var pan = new Vector3(
                (UnityEngine.Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (UnityEngine.Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
                0,
                (UnityEngine.Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (UnityEngine.Input.GetKey(KeyCode.DownArrow) ? 1 : 0));
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
            var unitRadius = Client.Settings?.UnitRadius ?? 0.5f;
            foreach (var kv in Views.InterpolatedPositions)
            {
                if (!match(kv.Key))
                {
                    continue;
                }

                // 건물·광맥은 발판 크기로 집는다
                var radius = Client.Latest.TryGetValue(kv.Key, out var s) && UnitArchetypes.Of(s.Kind).IsStatic
                    ? UnitArchetypes.Of(s.Kind).Footprint
                    : unitRadius;
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
                if (!Client.IsMine(kv.Key) || IsStatic(kv.Key))
                {
                    continue; // 박스 선택은 움직이는 유닛만 (건물은 클릭으로)
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
            return Debugging.NetDebugHud.Area.Contains(gui) || Debugging.SimSettingsPanel.Area.Contains(gui) || CommandPanel.Area.Contains(gui);
        }

        // ── 능력 ──────────────────────────────────────────────

        /// <summary>능력 단축키 순서. 선택한 유닛들의 능력 목록(<see cref="AvailableAbilities"/>) 순서대로 붙는다.</summary>
        public static readonly KeyCode[] AbilityKeys = { KeyCode.Q, KeyCode.W, KeyCode.E, KeyCode.R, KeyCode.D, KeyCode.F };

        /// <summary>대상을 고르는 중인 능력 (없으면 null). 좌클릭으로 쓰고, 우클릭·Esc 로 취소.</summary>
        public AbilityId? Targeting { get; private set; }

        private readonly List<AbilityId> _abilities = new List<AbilityId>();
        private Transform _targetRing;
        private Renderer _targetRenderer;

        /// <summary>
        /// 선택한 내 유닛들이 가진 능력 (영웅 먼저, 종류별 보유 순서, 중복 없이). 단축키는 이 순서로 Q W E R D F.
        /// </summary>
        public IReadOnlyList<AbilityId> AvailableAbilities()
        {
            _abilities.Clear();
            var kinds = new List<UnitKind>();
            foreach (var id in Selection.Ids)
            {
                if (Client.Latest.TryGetValue(id, out var s) && s.Owner == Client.PlayerId && !kinds.Contains(s.Kind))
                {
                    kinds.Add(s.Kind);
                }
            }

            kinds.Sort((a, b) => (a == UnitKind.Hero ? 0 : 1 + (int)a).CompareTo(b == UnitKind.Hero ? 0 : 1 + (int)b));
            foreach (var kind in kinds)
            {
                foreach (var ability in AbilityCatalog.LoadoutOf(kind))
                {
                    if (!_abilities.Contains(ability))
                    {
                        _abilities.Add(ability);
                    }
                }
            }

            return _abilities;
        }

        /// <summary>선택한 유닛 중 이 능력을 가진 유닛들의 가장 짧은 남은 재사용 대기 (하나라도 준비됐으면 0).</summary>
        public float ReadyIn(AbilityId ability)
        {
            var best = float.MaxValue;
            foreach (var id in Selection.Ids)
            {
                if (Client.Latest.TryGetValue(id, out var s) && Contains(AbilityCatalog.LoadoutOf(s.Kind), ability))
                {
                    best = Mathf.Min(best, Client.CooldownOf(id, ability));
                }
            }

            return best == float.MaxValue ? 0 : best;
        }

        /// <summary>능력 사용: 대상이 없으면 바로, 있으면 대상 고르기 모드.</summary>
        public void BeginAbility(AbilityId ability)
        {
            if (!AbilityCatalog.TryGet(ability, out var def) || ReadyIn(ability) > 0)
            {
                return;
            }

            CancelPlacement();
            if (def.Targeting == AbilityTargeting.None)
            {
                Cast(ability, default, 0);
                return;
            }

            Targeting = ability;
            if (_targetRing == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "Ability Target";
                Destroy(go.GetComponent<Collider>());
                _targetRing = go.transform;
                _targetRenderer = go.GetComponent<Renderer>();
            }

            var size = def.AreaRadius > 0 ? def.AreaRadius * 2 : 0.8f;
            _targetRing.localScale = new Vector3(size, 0.02f, size);
            _targetRenderer.material.color = new Color(0.35f, 0.8f, 1f);
            _targetRing.gameObject.SetActive(true);
        }

        public void CancelAbility()
        {
            Targeting = null;
            if (_targetRing != null)
            {
                _targetRing.gameObject.SetActive(false);
            }
        }

        private void HandleAbilityKeys()
        {
            if (Ctrl())
            {
                return; // Ctrl+조합은 다른 기능
            }

            var abilities = AvailableAbilities();
            for (var i = 0; i < AbilityKeys.Length && i < abilities.Count; i++)
            {
                if (UnityEngine.Input.GetKeyDown(AbilityKeys[i]))
                {
                    BeginAbility(abilities[i]);
                }
            }
        }

        private void UpdateAbilityTargeting()
        {
            var ability = Targeting!.Value;
            var def = AbilityCatalog.Get(ability);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetMouseButtonDown(1) || ReadyIn(ability) > 0 || Selection.Count == 0)
            {
                CancelAbility();
                return;
            }

            if (!TryGetGroundPoint(UnityEngine.Input.mousePosition, out var point))
            {
                return;
            }

            _targetRing.position = point + Vector3.up * 0.03f;
            if (!UnityEngine.Input.GetMouseButtonDown(0) || IsOverDebugWindow(UnityEngine.Input.mousePosition))
            {
                return;
            }

            var ground = new Vector2(point.x, point.z);
            if (def.Targeting == AbilityTargeting.Unit)
            {
                var wantsAllies = (def.Affects & AbilityAffects.Allies) != 0;
                var target = PickUnit(UnityEngine.Input.mousePosition, id => wantsAllies ? IsFriendly(id) : Client.IsEnemy(id));
                if (target < 0)
                {
                    return; // 대상이 아니면 계속 고른다
                }

                Cast(ability, ground, target);
            }
            else
            {
                Cast(ability, ground, 0);
            }

            ShowMarker(point, new Color(0.35f, 0.8f, 1f));
            CancelAbility();
        }

        private bool IsFriendly(int id) => Client.Latest.TryGetValue(id, out var s) && s.Team != 0 && s.Team == Client.MyTeam;

        /// <summary>시전 즉시 표시 (없으면 명령만 보낸다).</summary>
        public CastPrediction Prediction;

        /// <summary>시전 명령: 즉시 표시를 시작하며 보낸다.</summary>
        private void Cast(AbilityId ability, Vector2 point, int target)
        {
            if (Prediction != null)
            {
                Prediction.Cast(Selection.Ids, ability, point, target);
            }
            else
            {
                Client.SendCast(Selection.Ids, ability, point, target);
            }
        }

        /// <summary>
        /// 자동 확인용: 지정한 유닛들로 입력과 같은 경로(즉시 표시 + 전송)로 시전한다.
        /// <paramref name="force"/> 면 로컬 재사용 대기를 무시한다 (서버 거부 처리를 확인할 때).
        /// </summary>
        public void CastForTest(IReadOnlyList<int> units, AbilityId ability, Vector2 point, bool force)
        {
            Prediction.Cast(units, ability, point, 0, force);
        }

        private static bool Contains(IReadOnlyList<AbilityId> list, AbilityId id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] == id)
                {
                    return true;
                }
            }

            return false;
        }

        // ── 경제: 건설 배치·생산 ──────────────────────────────

        private static readonly Color WorkMarkerColor = new Color(0.3f, 0.85f, 0.95f);

        /// <summary>건설 배치 중인 건물 종류 (없으면 null). 좌클릭으로 놓고, 우클릭·Esc 로 취소.</summary>
        public UnitKind? Placing { get; private set; }

        private Transform _placementGhost;
        private Renderer _placementRenderer;

        private bool IsStatic(int id) => Client.Latest.TryGetValue(id, out var s) && UnitArchetypes.Of(s.Kind).IsStatic;

        private bool IsWorkTarget(int id) =>
            Client.Latest.TryGetValue(id, out var s) &&
            (s.Kind == UnitKind.OreField || (s.Owner == Client.PlayerId && (s.Flags & UnitFlags.UnderConstruction) != 0));

        /// <summary>선택한 유닛 중 일꾼 수 / 건물(종류별) — 명령 패널이 버튼을 고를 때 쓴다.</summary>
        public int SelectedCount(UnitKind kind)
        {
            var n = 0;
            foreach (var id in Selection.Ids)
            {
                if (Client.Latest.TryGetValue(id, out var s) && s.Kind == kind && (s.Flags & UnitFlags.UnderConstruction) == 0)
                {
                    n++;
                }
            }

            return n;
        }

        public void BeginPlacement(UnitKind kind)
        {
            if (SelectedCount(UnitKind.Worker) == 0 || !UnitArchetypes.CanBuild(kind))
            {
                return;
            }

            CancelAbility();
            Placing = kind;
            if (_placementGhost == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Placement";
                Destroy(go.GetComponent<Collider>());
                _placementGhost = go.transform;
                _placementRenderer = go.GetComponent<Renderer>();
            }

            var size = UnitArchetypes.Of(kind).Footprint * 2;
            _placementGhost.localScale = new Vector3(size, 0.3f, size);
            _placementGhost.gameObject.SetActive(true);
        }

        public void CancelPlacement()
        {
            Placing = null;
            if (_placementGhost != null)
            {
                _placementGhost.gameObject.SetActive(false);
            }
        }

        /// <summary>생산: 고른 건물 중 그 종류를 만들 수 있는 것에 하나 넣는다 (서버가 대기열이 짧은 곳을 고름).</summary>
        public void Train(UnitKind kind)
        {
            Client.SendCommand(CommandType.Train, Selection.Ids, default, 0, kind);
        }

        private void HandleProductionKeys()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.B))
            {
                BeginPlacement(UnitKind.Barracks);
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.Z))
            {
                Train(SelectedCount(UnitKind.Barracks) > 0 ? UnitKind.Melee : UnitKind.Worker);
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.X) && SelectedCount(UnitKind.Barracks) > 0)
            {
                Train(UnitKind.Ranged);
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.C) && SelectedCount(UnitKind.Headquarters) > 0)
            {
                Train(UnitKind.Hero);
            }
        }

        private void UpdatePlacement()
        {
            var kind = Placing!.Value;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetMouseButtonDown(1) || SelectedCount(UnitKind.Worker) == 0)
            {
                CancelPlacement();
                return;
            }

            if (!TryGetGroundPoint(UnityEngine.Input.mousePosition, out var point))
            {
                return;
            }

            // 0.5 칸에 맞춘다
            var site = new Vector2(Mathf.Round(point.x * 2) / 2, Mathf.Round(point.z * 2) / 2);
            var ok = LooksBuildable(kind, site);
            _placementGhost.position = UnitViewManager.ToWorld(site, 0.15f);
            _placementRenderer.material.color = ok ? new Color(0.3f, 0.9f, 0.4f) : new Color(0.9f, 0.3f, 0.25f);

            if (UnityEngine.Input.GetMouseButtonDown(0) && !IsOverDebugWindow(UnityEngine.Input.mousePosition))
            {
                if (ok && Client.Ore >= UnitArchetypes.Of(kind).Cost)
                {
                    Client.SendCommand(CommandType.Build, Selection.Ids, site, 0, kind);
                    ShowMarker(UnitViewManager.ToWorld(site), WorkMarkerColor);
                }

                if (!(UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift)))
                {
                    CancelPlacement(); // Shift 를 누르고 있으면 계속 놓는다
                }
            }
        }

        /// <summary>
        /// 클라이언트가 아는 정보로 대충 판단 (맵 안, 맵 벽·보이는 건물·광맥과 안 겹침). 최종 판정은 서버가 한다.
        /// </summary>
        private bool LooksBuildable(UnitKind kind, Vector2 site)
        {
            var half = UnitArchetypes.Of(kind).Footprint;
            var gap = (Client.Settings?.UnitRadius ?? 0.5f) * 2;
            var h = OpenRTS.Shared.SimConstants.MapHalfSize - 1f;
            if (Mathf.Abs(site.X) + half > h || Mathf.Abs(site.Y) + half > h)
            {
                return false;
            }

            foreach (var wall in OpenRTS.Shared.Map.MapLayouts.Create(Client.Settings?.Map ?? OpenRTS.Shared.Map.MapKind.Open))
            {
                if (site.X + half > wall.Min.X - gap && site.X - half < wall.Max.X + gap && site.Y + half > wall.Min.Y - gap && site.Y - half < wall.Max.Y + gap)
                {
                    return false;
                }
            }

            foreach (var kv in Views.InterpolatedPositions)
            {
                if (Client.Latest.TryGetValue(kv.Key, out var s) && UnitArchetypes.Of(s.Kind).IsStatic)
                {
                    var other = UnitArchetypes.Of(s.Kind).Footprint;
                    if (Mathf.Abs(site.X - kv.Value.X) < half + other + gap && Mathf.Abs(site.Y - kv.Value.Y) < half + other + gap)
                    {
                        return false;
                    }
                }
            }

            return true;
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
