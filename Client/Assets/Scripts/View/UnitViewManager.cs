using System.Collections.Generic;
using OpenRTS.Client.Controls;
using OpenRTS.Client.Net;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Protocol;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.View
{
    /// <summary>
    /// 보간된 위치로 유닛을 그린다. 서버 좌표 (x, y) → Unity (x, 0, y).
    /// <list type="bullet">
    /// <item>색: 내 유닛 초록, 같은 팀 파랑, 적 빨강, 중립(전투 안 하는 봇) 회색. 선택한 내 유닛은 발밑에 고리.</item>
    /// <item>모양: 근접은 짧은 네모 코, 원거리는 긴 총신.</item>
    /// <item>체력 막대(전투 유닛만), 선딜 중에는 코가 노랗게, 맞으면 잠깐 하얗게.</item>
    /// </list>
    /// 체력·종류·공격 대상은 보간하지 않고 최신 스냅샷 값을 쓴다.
    /// </summary>
    public sealed class UnitViewManager : MonoBehaviour
    {
        private static readonly Color MineColor = new Color(0.2f, 0.8f, 0.3f);
        private static readonly Color AllyColor = new Color(0.25f, 0.5f, 0.95f);
        private static readonly Color EnemyColor = new Color(0.85f, 0.3f, 0.25f);
        private static readonly Color NeutralColor = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color NoseColor = Color.white;
        private static readonly Color WindupColor = new Color(1f, 0.85f, 0.2f);
        private const float FlashSeconds = 0.12f;

        public GameClient Client;
        public UnitSelection Selection;

        /// <summary>시전 즉시 표시 (있으면 예측 중인 유닛을 시전 자세로 그린다).</summary>
        public CastPrediction Prediction;

        /// <summary>지금 이 유닛을 시전 자세로 그리고 있는지 (자동 확인용).</summary>
        public bool IsShowingCast(int unitId) => _views.TryGetValue(unitId, out var v) && v.NoseState == 2;
        public Camera Camera;

        [Tooltip("보간 전 최신 스냅샷 위치를 반투명 구로 함께 표시 (F1).")]
        public bool ShowRawSnapshot;

        private sealed class View
        {
            public Transform Root;
            public Transform Body;
            public Renderer BodyRenderer;
            public Renderer NoseRenderer;
            public GameObject Ring;
            public Transform HpBar;
            public Transform HpFill;
            public Renderer HpFillRenderer;
            public byte Owner;
            public byte Team;
            public UnitKind Kind;
            public Color Color;
            public float FlashUntil;
            public int NoseState;
            public bool IsStatic;
            public float Footprint;
            public GameObject Carry;
            public GameObject StunMark;
            public GameObject ShieldMark;
            public UnitFlags AppliedStatus;
            public float AppliedHeight = -1f;
        }

        private readonly Dictionary<int, View> _views = new Dictionary<int, View>();
        private readonly Dictionary<int, Transform> _ghosts = new Dictionary<int, Transform>();
        private readonly Dictionary<int, Vector2> _positions = new Dictionary<int, Vector2>();
        private readonly List<int> _removed = new List<int>();
        private float _appliedRadius = -1f;

        /// <summary>서버 설정의 유닛 반지름 (설정을 받기 전에는 기본값).</summary>
        public float Radius => Client.Settings?.UnitRadius ?? SimSettings.Default.UnitRadius;

        public IReadOnlyDictionary<int, Vector2> InterpolatedPositions => _positions;

        public static Vector3 ToWorld(Vector2 p, float height = 0f) => new Vector3(p.X, height, p.Y);

        /// <summary>맞은 유닛을 잠깐 하얗게 (근접 타격·투사체 명중 연출).</summary>
        public void Flash(int unitId)
        {
            if (_views.TryGetValue(unitId, out var v))
            {
                v.FlashUntil = Time.time + FlashSeconds;
            }
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.F1))
            {
                ShowRawSnapshot = !ShowRawSnapshot;
            }
        }

        private void LateUpdate()
        {
            if (Client == null || Client.Interpolator == null)
            {
                return;
            }

            Client.Interpolator.Sample(_positions);

            // 캡슐은 지름 1, 높이 2 → 반지름 r 이면 2r 배. 서버에서 반지름이 바뀌면 전부 다시 맞춘다.
            var radius = Radius;
            if (!Mathf.Approximately(radius, _appliedRadius))
            {
                _appliedRadius = radius;
                foreach (var v in _views.Values)
                {
                    if (!v.IsStatic)
                    {
                        v.Root.localScale = Vector3.one * (2 * radius);
                    }
                }
            }

            var cameraRotation = Camera != null ? Camera.transform.rotation : Quaternion.identity;
            foreach (var kv in _positions)
            {
                Client.Latest.TryGetValue(kv.Key, out var state);
                if (_views.TryGetValue(kv.Key, out var view) && (view.Owner != state.Owner || view.Kind != state.Kind || view.Team != state.Team))
                {
                    // ID 가 재사용되었거나 정보가 바뀌었으면 다시 만든다.
                    Destroy(view.Root.gameObject);
                    _views.Remove(kv.Key);
                    view = null;
                }

                if (view == null)
                {
                    view = CreateUnit(kv.Key, state);
                    view.Root.localScale = view.IsStatic ? Vector3.one : Vector3.one * (2 * radius);
                    _views.Add(kv.Key, view);
                }

                if (view.IsStatic)
                {
                    // 건물·자원: 바닥에 붙어 있고 돌지 않는다
                    view.Root.position = ToWorld(kv.Value);
                    UpdateLook(view, state, cameraRotation);
                    continue;
                }

                // 위치·방향 (공격 대상이 있으면 그쪽을 본다)
                var next = ToWorld(kv.Value, 2 * radius);
                var look = next - view.Root.position;
                if (state.AttackTarget != 0 && _positions.TryGetValue(state.AttackTarget, out var targetPos))
                {
                    look = ToWorld(targetPos, 2 * radius) - next;
                }

                look.y = 0;
                if (look.sqrMagnitude > 1e-6f)
                {
                    view.Body.rotation = Quaternion.LookRotation(look);
                }

                view.Root.position = next;

                UpdateLook(view, state, cameraRotation);
            }

            RemoveMissing();
            UpdateGhosts();
        }

        private void UpdateLook(View view, in UnitState state, Quaternion cameraRotation)
        {
            var color = ColorFor(state);
            if ((state.Flags & UnitFlags.Slowed) != 0)
            {
                color = Color.Lerp(color, SlowTint, 0.45f); // 둔화: 푸르스름하게
            }

            var flashing = Time.time < view.FlashUntil;
            var body = flashing ? Color.white : color;
            if (view.Color != body)
            {
                view.Color = body;
                view.BodyRenderer.material.color = body;
            }

            // 코 색: 공격 선딜 노랑, 능력 시전(선딜·정신 집중·돌진) 보라.
            // 내가 방금 시전한 유닛은 서버 확인 전에도 보라 (CastPrediction — 지연을 감추는 즉시 표시).
            var casting = ((state.Flags & UnitFlags.Casting) != 0 && !Client.RecentlyReleased(state.Id))
                          || (Prediction != null && Prediction.IsPredicting(state.Id));
            var nose = casting ? 2 : (state.Flags & UnitFlags.WindingUp) != 0 ? 1 : 0;
            if (view.NoseState != nose)
            {
                view.NoseState = nose;
                view.NoseRenderer.material.color = nose == 2 ? CastColor : nose == 1 ? WindupColor : NoseColor;
            }

            var status = state.Flags & (UnitFlags.Stunned | UnitFlags.Shielded | UnitFlags.Casting);
            if (status != view.AppliedStatus)
            {
                view.AppliedStatus = status;
                view.StunMark?.SetActive((status & UnitFlags.Stunned) != 0);
                view.ShieldMark?.SetActive((status & UnitFlags.Shielded) != 0);
            }

            if (view.StunMark != null && view.StunMark.activeSelf)
            {
                view.StunMark.transform.Rotate(0, 360f * Time.deltaTime, 0, Space.World); // 빙글빙글
            }

            if (view.Ring != null)
            {
                view.Ring.SetActive(Selection != null && Selection.Contains(state.Id));
            }

            if (view.Carry != null)
            {
                var carrying = (state.Flags & UnitFlags.Carrying) != 0;
                if (view.Carry.activeSelf != carrying)
                {
                    view.Carry.SetActive(carrying);
                }
            }

            if (view.IsStatic)
            {
                UpdateStaticShape(view, state);
            }

            if (view.HpBar != null)
            {
                view.HpBar.rotation = cameraRotation; // 항상 화면을 향한다
                var hp = Mathf.Clamp01(state.Health);
                view.HpFill.localScale = new Vector3(hp, 1f, 1f);
                view.HpFill.localPosition = new Vector3((hp - 1f) / 2f, 0, -0.01f);
                view.HpFillRenderer.material.color = hp > 0.5f ? new Color(0.3f, 0.9f, 0.3f) : hp > 0.25f ? new Color(0.95f, 0.8f, 0.2f) : new Color(0.95f, 0.25f, 0.2f);
            }
        }

        /// <summary>건설 중이면 체력 비율만큼 낮게, 광맥은 남은 양만큼 작게.</summary>
        private static void UpdateStaticShape(View view, in UnitState state)
        {
            var size = view.Footprint * 2;
            float height;
            float width;
            if (state.Kind == UnitKind.OreField)
            {
                var left = Mathf.Clamp01(state.Health);
                width = size * Mathf.Lerp(0.45f, 1f, left);
                height = Mathf.Lerp(0.3f, 1.1f, left);
            }
            else
            {
                var full = state.Kind == UnitKind.Headquarters ? 2.2f : 1.6f;
                var building = (state.Flags & UnitFlags.UnderConstruction) != 0;
                height = building ? Mathf.Lerp(0.15f, full, Mathf.Clamp01(state.Health)) : full;
                width = size;
            }

            if (Mathf.Abs(height - view.AppliedHeight) < 0.01f)
            {
                return;
            }

            view.AppliedHeight = height;
            view.Body.localScale = new Vector3(width, height, width);
            view.Body.localPosition = new Vector3(0, height / 2, 0);
            if (view.HpBar != null)
            {
                view.HpBar.localPosition = new Vector3(0, height + 0.5f, 0);
            }
        }

        private Color ColorFor(in UnitState s)
        {
            if (s.Kind == UnitKind.OreField)
            {
                return OreColor;
            }

            if (s.Owner != 0 && s.Owner == Client.PlayerId)
            {
                return MineColor;
            }

            if (s.Team == UnitArchetypes.NeutralTeam)
            {
                return NeutralColor;
            }

            // 내 팀을 모르면(관전) 1팀 파랑, 2팀 빨강
            var myTeam = Client.MyTeam != 0 ? Client.MyTeam : (byte)1;
            return s.Team == myTeam ? AllyColor : EnemyColor;
        }

        private void UpdateGhosts()
        {
            if (!ShowRawSnapshot)
            {
                RemoveGhosts(_ => false);
                return;
            }

            foreach (var kv in Client.LatestRaw)
            {
                if (!_ghosts.TryGetValue(kv.Key, out var ghost))
                {
                    ghost = CreatePrimitive(PrimitiveType.Sphere, $"Raw {kv.Key}", new Color(1f, 1f, 0f, 0.35f), true).transform;
                    ghost.localScale = Vector3.one * 0.6f;
                    _ghosts.Add(kv.Key, ghost);
                }

                ghost.position = ToWorld(kv.Value, 4 * _appliedRadius + 0.4f);
            }

            RemoveGhosts(Client.LatestRaw.ContainsKey);
        }

        private static readonly Color OreColor = new Color(0.3f, 0.85f, 0.95f);
        private static readonly Color SlowTint = new Color(0.35f, 0.55f, 1f);
        private static readonly Color CastColor = new Color(0.85f, 0.35f, 1f);

        /// <summary>건물(정육면체, 발판 크기) · 광맥(45도 돌린 수정). 루트는 크기 1 이고 몸체를 직접 키운다.</summary>
        private View CreateStatic(int id, in UnitState state)
        {
            var stats = UnitArchetypes.Of(state.Kind);
            var mine = state.Owner != 0 && state.Owner == Client.PlayerId;
            var root = new GameObject($"{state.Kind} {id}").transform;
            root.SetParent(transform, false);
            var body = CreatePrimitive(PrimitiveType.Cube, "Body", NeutralColor, false);
            body.transform.SetParent(root, false);
            if (state.Kind == UnitKind.OreField)
            {
                body.transform.localRotation = Quaternion.Euler(0, 45, 0);
            }

            // 문(생산 유닛이 나오는 쪽 표시) 대신 지붕 위 작은 깃발: 종류 구분용
            var flag = CreatePrimitive(PrimitiveType.Cube, "Mark", NoseColor, false);
            flag.transform.SetParent(body.transform, false);
            flag.transform.localPosition = new Vector3(0, 0.55f, 0);
            flag.transform.localScale = state.Kind == UnitKind.Headquarters ? new Vector3(0.5f, 0.1f, 0.5f) : new Vector3(0.25f, 0.1f, 0.6f);
            flag.SetActive(state.Kind != UnitKind.OreField);

            var view = new View
            {
                Root = root,
                Body = body.transform,
                BodyRenderer = body.GetComponent<Renderer>(),
                NoseRenderer = flag.GetComponent<Renderer>(),
                Owner = state.Owner,
                Team = state.Team,
                Kind = state.Kind,
                Color = NeutralColor,
                IsStatic = true,
                Footprint = stats.Footprint,
            };

            if (mine)
            {
                var ring = CreatePrimitive(PrimitiveType.Cylinder, "Selection", new Color(0.4f, 1f, 0.5f), false);
                ring.transform.SetParent(root, false);
                ring.transform.localPosition = new Vector3(0, 0.02f, 0);
                ring.transform.localScale = new Vector3(stats.Footprint * 3f, 0.01f, stats.Footprint * 3f);
                ring.SetActive(false);
                view.Ring = ring;
            }

            if (state.Team != UnitArchetypes.NeutralTeam)
            {
                AddHpBar(view, root, stats.Footprint * 2f);
            }

            return view;
        }

        private static void AddHpBar(View view, Transform root, float width)
        {
            var bar = new GameObject("HpBar").transform;
            bar.SetParent(root, false);
            bar.localPosition = new Vector3(0, 1.45f, 0);
            bar.localScale = new Vector3(width, 0.14f * Mathf.Max(1f, width / 2.4f), 1f);
            var back = CreatePrimitive(PrimitiveType.Quad, "Back", new Color(0.1f, 0.1f, 0.1f), false);
            back.transform.SetParent(bar, false);
            var fill = CreatePrimitive(PrimitiveType.Quad, "Fill", Color.green, false);
            fill.transform.SetParent(bar, false);
            view.HpBar = bar;
            view.HpFill = fill.transform;
            view.HpFillRenderer = fill.GetComponent<Renderer>();
        }

        private View CreateUnit(int id, in UnitState state)
        {
            if (UnitArchetypes.Of(state.Kind).IsStatic)
            {
                return CreateStatic(id, state);
            }

            var mine = state.Owner != 0 && state.Owner == Client.PlayerId;
            var root = new GameObject(mine ? $"Unit {id} (mine)" : $"Unit {id} (team {state.Team})").transform;
            root.SetParent(transform, false);

            var body = CreatePrimitive(PrimitiveType.Capsule, "Body", NeutralColor, false);
            body.transform.SetParent(root, false);

            // 진행·공격 방향 표시용 코: 근접은 짧은 네모, 원거리는 긴 총신
            var nose = CreatePrimitive(PrimitiveType.Cube, "Nose", NoseColor, false);
            nose.transform.SetParent(body.transform, false);
            if (state.Kind == UnitKind.Ranged)
            {
                nose.transform.localPosition = new Vector3(0, 0.45f, 0.6f);
                nose.transform.localScale = new Vector3(0.12f, 0.12f, 0.8f);
            }
            else if (state.Kind == UnitKind.Worker)
            {
                // 일꾼: 작은 몸 + 짧은 도구. 자원을 들면 등 위에 작은 수정.
                body.transform.localScale = new Vector3(0.8f, 0.75f, 0.8f);
                body.transform.localPosition = new Vector3(0, -0.25f, 0);
                nose.transform.localPosition = new Vector3(0, 0.2f, 0.55f);
                nose.transform.localScale = new Vector3(0.15f, 0.15f, 0.4f);
            }
            else
            {
                nose.transform.localPosition = new Vector3(0, 0.4f, 0.5f);
                nose.transform.localScale = new Vector3(0.3f, 0.2f, 0.35f);
            }

            var view = new View
            {
                Root = root,
                Body = body.transform,
                BodyRenderer = body.GetComponent<Renderer>(),
                NoseRenderer = nose.GetComponent<Renderer>(),
                Owner = state.Owner,
                Team = state.Team,
                Kind = state.Kind,
                Color = NeutralColor,
            };

            if (mine)
            {
                // 선택 고리: 캡슐 바닥(로컬 y=-1)에 납작한 원기둥. 크기(2r)를 따라 함께 커진다.
                var ring = CreatePrimitive(PrimitiveType.Cylinder, "Selection", new Color(0.4f, 1f, 0.5f), false);
                ring.transform.SetParent(root, false);
                ring.transform.localPosition = new Vector3(0, -0.98f, 0);
                ring.transform.localScale = new Vector3(1.35f, 0.01f, 1.35f);
                ring.SetActive(false);
                view.Ring = ring;
            }

            if (state.Kind == UnitKind.Worker)
            {
                var carry = CreatePrimitive(PrimitiveType.Cube, "Ore", OreColor, false);
                carry.transform.SetParent(body.transform, false);
                carry.transform.localPosition = new Vector3(0, 0.7f, -0.35f);
                carry.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
                carry.transform.localRotation = Quaternion.Euler(0, 45, 0);
                carry.SetActive(false);
                view.Carry = carry;
            }

            if (state.Kind == UnitKind.Hero)
            {
                // 영웅 표시: 머리 위 금색 관
                var crown = CreatePrimitive(PrimitiveType.Cube, "Crown", new Color(1f, 0.8f, 0.2f), false);
                crown.transform.SetParent(body.transform, false);
                crown.transform.localPosition = new Vector3(0, 1.1f, 0);
                crown.transform.localScale = new Vector3(0.6f, 0.15f, 0.6f);
            }

            // 상태 표시: 기절(머리 위 도는 노란 상자), 보호막(몸을 두른 하늘색 고리)
            var stun = CreatePrimitive(PrimitiveType.Cube, "Stunned", new Color(1f, 0.95f, 0.3f), false);
            stun.transform.SetParent(root, false);
            stun.transform.localPosition = new Vector3(0, 1.75f, 0);
            stun.transform.localScale = new Vector3(0.35f, 0.12f, 0.35f);
            stun.SetActive(false);
            view.StunMark = stun;

            var shield = CreatePrimitive(PrimitiveType.Cylinder, "Shield", new Color(0.45f, 0.9f, 1f), false);
            shield.transform.SetParent(root, false);
            shield.transform.localPosition = new Vector3(0, -0.2f, 0);
            shield.transform.localScale = new Vector3(1.5f, 0.02f, 1.5f);
            shield.SetActive(false);
            view.ShieldMark = shield;

            if (state.Team != UnitArchetypes.NeutralTeam)
            {
                AddHpBar(view, root, 1.2f);
            }

            return view;
        }

        private static GameObject CreatePrimitive(PrimitiveType type, string name, Color color, bool transparent)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            var material = go.GetComponent<Renderer>().material;
            material.color = color;
            if (transparent)
            {
                MakeTransparent(material);
            }

            return go;
        }

        private static void MakeTransparent(Material m)
        {
            // Built-in Standard 셰이더를 Fade 모드로 전환
            m.SetFloat("_Mode", 2);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_ALPHABLEND_ON");
            m.renderQueue = 3000;
        }

        private void RemoveMissing()
        {
            _removed.Clear();
            foreach (var id in _views.Keys)
            {
                if (!_positions.ContainsKey(id))
                {
                    _removed.Add(id);
                }
            }

            foreach (var id in _removed)
            {
                Destroy(_views[id].Root.gameObject);
                _views.Remove(id);
            }
        }

        private void RemoveGhosts(System.Func<int, bool> keep)
        {
            _removed.Clear();
            foreach (var id in _ghosts.Keys)
            {
                if (!keep(id))
                {
                    _removed.Add(id);
                }
            }

            foreach (var id in _removed)
            {
                Destroy(_ghosts[id].gameObject);
                _ghosts.Remove(id);
            }
        }
    }
}
