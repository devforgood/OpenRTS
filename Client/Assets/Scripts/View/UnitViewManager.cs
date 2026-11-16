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
            public bool Windup;
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
                    v.Root.localScale = Vector3.one * (2 * radius);
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
                    view.Root.localScale = Vector3.one * (2 * radius);
                    _views.Add(kv.Key, view);
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
            var flashing = Time.time < view.FlashUntil;
            var body = flashing ? Color.white : color;
            if (view.Color != body)
            {
                view.Color = body;
                view.BodyRenderer.material.color = body;
            }

            var windup = (state.Flags & UnitFlags.WindingUp) != 0;
            if (view.Windup != windup)
            {
                view.Windup = windup;
                view.NoseRenderer.material.color = windup ? WindupColor : NoseColor;
            }

            if (view.Ring != null)
            {
                view.Ring.SetActive(Selection != null && Selection.Contains(state.Id));
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

        private Color ColorFor(in UnitState s)
        {
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

        private View CreateUnit(int id, in UnitState state)
        {
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

            if (state.Team != UnitArchetypes.NeutralTeam)
            {
                var bar = new GameObject("HpBar").transform;
                bar.SetParent(root, false);
                bar.localPosition = new Vector3(0, 1.45f, 0);
                bar.localScale = new Vector3(1.2f, 0.14f, 1f);
                var back = CreatePrimitive(PrimitiveType.Quad, "Back", new Color(0.1f, 0.1f, 0.1f), false);
                back.transform.SetParent(bar, false);
                var fill = CreatePrimitive(PrimitiveType.Quad, "Fill", Color.green, false);
                fill.transform.SetParent(bar, false);
                view.HpBar = bar;
                view.HpFill = fill.transform;
                view.HpFillRenderer = fill.GetComponent<Renderer>();
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
