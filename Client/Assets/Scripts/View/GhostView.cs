using System.Collections.Generic;
using OpenRTS.Client.Net;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.View
{
    /// <summary>
    /// 안개 속 유령: 적 유닛이 시야 밖으로 나가 스냅샷에서 빠지면, 마지막으로 본 자리에 어두운 표시를 남긴다.
    /// 그 자리가 다시 보이면(유닛이 거기 없다는 뜻) 지운다. 죽어서 사라진 유닛은 남기지 않는다.
    ///
    /// 스냅샷에서 빠지는 시점과 사망 이벤트 재생 시점이 한두 스냅샷 어긋날 수 있어서,
    /// 사라진 뒤 <see cref="DeathWaitSeconds"/> 동안 사망 이벤트가 오지 않을 때만 유령을 만든다.
    /// 서버가 보낸 정보만 쓰므로 안개 속 실제 위치는 알 수 없다 (마지막으로 본 위치일 뿐).
    /// </summary>
    public sealed class GhostView : MonoBehaviour
    {
        public const float DeathWaitSeconds = 0.35f;
        private const float MinGhostAge = 0.5f;

        public GameClient Client;
        public UnitViewManager Views;
        public FogOfWarView Fog;

        private struct Pending
        {
            public Vector2 Position;
            public float Since;
        }

        private struct Ghost
        {
            public GameObject Marker;
            public Vector2 Position;
            public float Born;
        }

        private readonly Dictionary<int, Vector2> _lastSeenEnemies = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        private readonly Dictionary<int, Ghost> _ghosts = new Dictionary<int, Ghost>();
        private readonly HashSet<int> _died = new HashSet<int>();
        private readonly List<int> _removed = new List<int>();
        private Material _material;

        public int GhostCount => _ghosts.Count;

        /// <summary>사망 이벤트를 받으면 호출 (그 유닛은 유령을 남기지 않는다).</summary>
        public void NotifyDeath(int unitId)
        {
            _died.Add(unitId);
            _pending.Remove(unitId);
            RemoveGhost(unitId);
        }

        private void LateUpdate()
        {
            if (Client == null || Views == null)
            {
                return;
            }

            var fogOn = Client.Settings is { FogOfWar: true } && Client.MyTeam != 0;
            if (!fogOn)
            {
                ClearAll();
                return;
            }

            var positions = Views.InterpolatedPositions;

            // 보이는 적은 기록하고, 그 유닛의 유령은 지운다.
            foreach (var kv in positions)
            {
                if (Client.IsEnemy(kv.Key))
                {
                    _lastSeenEnemies[kv.Key] = kv.Value;
                    _pending.Remove(kv.Key);
                    RemoveGhost(kv.Key);
                }
            }

            // 방금 사라진 적 → 잠시 기다렸다가 유령으로
            _removed.Clear();
            foreach (var kv in _lastSeenEnemies)
            {
                if (!positions.ContainsKey(kv.Key))
                {
                    _removed.Add(kv.Key);
                    if (!_died.Contains(kv.Key))
                    {
                        _pending[kv.Key] = new Pending { Position = kv.Value, Since = Time.time };
                    }
                }
            }

            foreach (var id in _removed)
            {
                _lastSeenEnemies.Remove(id);
            }

            _removed.Clear();
            foreach (var kv in _pending)
            {
                if (Time.time - kv.Value.Since >= DeathWaitSeconds)
                {
                    _removed.Add(kv.Key);
                }
            }

            foreach (var id in _removed)
            {
                AddGhost(id, _pending[id].Position);
                _pending.Remove(id);
            }

            // 유령 자리가 다시 보이면 (그런데 유닛이 없으면) 지운다.
            _removed.Clear();
            foreach (var kv in _ghosts)
            {
                if (Time.time - kv.Value.Born > MinGhostAge && Fog != null && Fog.IsVisible(kv.Value.Position))
                {
                    _removed.Add(kv.Key);
                }
            }

            foreach (var id in _removed)
            {
                RemoveGhost(id);
            }
        }

        private void AddGhost(int id, Vector2 position)
        {
            if (_ghosts.ContainsKey(id))
            {
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = $"Ghost {id}";
            Destroy(go.GetComponent<Collider>());
            _material ??= new Material(go.GetComponent<Renderer>().sharedMaterial) { color = new Color(0.45f, 0.18f, 0.16f) };
            go.GetComponent<Renderer>().sharedMaterial = _material;
            go.transform.SetParent(transform, false);
            var d = Views.Radius * 2;
            go.transform.localScale = new Vector3(d * 0.8f, 0.05f, d * 0.8f);
            go.transform.position = UnitViewManager.ToWorld(position, 0.08f);
            _ghosts[id] = new Ghost { Marker = go, Position = position, Born = Time.time };
        }

        private void RemoveGhost(int id)
        {
            if (_ghosts.TryGetValue(id, out var g))
            {
                Destroy(g.Marker);
                _ghosts.Remove(id);
            }
        }

        private void ClearAll()
        {
            foreach (var g in _ghosts.Values)
            {
                Destroy(g.Marker);
            }

            _ghosts.Clear();
            _pending.Clear();
            _lastSeenEnemies.Clear();
        }
    }
}
