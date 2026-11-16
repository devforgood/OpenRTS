using System.Collections.Generic;
using OpenRTS.Client.Net;
using OpenRTS.Shared.Map;
using OpenRTS.Shared.Protocol;
using UnityEngine;

namespace OpenRTS.Client.View
{
    /// <summary>
    /// 서버 설정의 맵(MapKind)에 맞춰 벽을 그린다. 벽 배치는 Shared 의 <see cref="MapLayouts"/> 가 정하므로
    /// 서버와 같은 함수로 만들고, 맵 데이터를 따로 받지 않는다.
    /// </summary>
    public sealed class MapView : MonoBehaviour
    {
        private const float WallHeight = 1.5f;

        public GameClient Client;

        private readonly List<GameObject> _walls = new List<GameObject>();
        private MapKind? _built;

        private void Start()
        {
            Client.SettingsChanged += OnSettings;
        }

        private void OnDestroy()
        {
            if (Client != null)
            {
                Client.SettingsChanged -= OnSettings;
            }
        }

        private void OnSettings(SimSettings settings)
        {
            if (_built == settings.Map)
            {
                return;
            }

            foreach (var w in _walls)
            {
                Destroy(w);
            }

            _walls.Clear();
            foreach (var rect in MapLayouts.Create(settings.Map))
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Wall {rect.Min}-{rect.Max}";
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(transform, false);
                var center = rect.Center;
                var size = rect.Size;
                go.transform.position = new Vector3(center.X, WallHeight / 2, center.Y);
                go.transform.localScale = new Vector3(size.X, WallHeight, size.Y);
                go.GetComponent<Renderer>().material.color = new Color(0.45f, 0.42f, 0.38f);
                _walls.Add(go);
            }

            _built = settings.Map;
        }
    }
}
