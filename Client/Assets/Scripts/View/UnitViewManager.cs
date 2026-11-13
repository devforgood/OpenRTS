using System.Collections.Generic;
using OpenRTS.Client.Net;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.View
{
    /// <summary>보간된 위치로 유닛을 그린다. 서버 좌표 (x, y) → Unity (x, 0, y).</summary>
    public sealed class UnitViewManager : MonoBehaviour
    {
        public GameClient Client;

        [Tooltip("보간 전 최신 스냅샷 위치를 반투명 구로 함께 표시 (F1).")]
        public bool ShowRawSnapshot;

        private readonly Dictionary<int, Transform> _views = new Dictionary<int, Transform>();
        private readonly Dictionary<int, Transform> _ghosts = new Dictionary<int, Transform>();
        private readonly Dictionary<int, Vector2> _positions = new Dictionary<int, Vector2>();
        private readonly List<int> _removed = new List<int>();

        public IReadOnlyDictionary<int, Vector2> InterpolatedPositions => _positions;

        public static Vector3 ToWorld(Vector2 p, float height = 0f) => new Vector3(p.X, height, p.Y);

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

            foreach (var kv in _positions)
            {
                if (!_views.TryGetValue(kv.Key, out var view))
                {
                    view = CreateUnit(kv.Key);
                    _views.Add(kv.Key, view);
                }

                var next = ToWorld(kv.Value, 1f);
                var delta = next - view.position;
                delta.y = 0;
                if (delta.sqrMagnitude > 1e-6f)
                {
                    view.rotation = Quaternion.LookRotation(delta);
                }

                view.position = next;
            }

            RemoveMissing(_views, _positions.ContainsKey);
            UpdateGhosts();
        }

        private void UpdateGhosts()
        {
            if (!ShowRawSnapshot)
            {
                RemoveMissing(_ghosts, _ => false);
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

                ghost.position = ToWorld(kv.Value, 2.4f);
            }

            RemoveMissing(_ghosts, Client.LatestRaw.ContainsKey);
        }

        private Transform CreateUnit(int id)
        {
            var mine = id == Client.MyUnitId;
            var go = CreatePrimitive(PrimitiveType.Capsule, mine ? $"Unit {id} (me)" : $"Unit {id}",
                mine ? new Color(0.2f, 0.8f, 0.3f) : new Color(0.85f, 0.3f, 0.25f), false);

            // 진행 방향 표시용 코
            var nose = CreatePrimitive(PrimitiveType.Cube, "Nose", Color.white, false);
            nose.transform.SetParent(go.transform, false);
            nose.transform.localPosition = new Vector3(0, 0.4f, 0.5f);
            nose.transform.localScale = new Vector3(0.2f, 0.2f, 0.4f);
            return go.transform;
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

        private void RemoveMissing(Dictionary<int, Transform> views, System.Func<int, bool> keep)
        {
            _removed.Clear();
            foreach (var id in views.Keys)
            {
                if (!keep(id))
                {
                    _removed.Add(id);
                }
            }

            foreach (var id in _removed)
            {
                Destroy(views[id].gameObject);
                views.Remove(id);
            }
        }
    }
}
