using System.Collections.Generic;
using OpenRTS.Client.Net;
using OpenRTS.Shared;
using OpenRTS.Shared.Combat;
using OpenRTS.Shared.Map;
using UnityEngine;

namespace OpenRTS.Client.View
{
    /// <summary>
    /// Fog of War 표시. 서버와 같은 <see cref="VisionGrid"/> 로 내 팀 유닛(보간 위치)의 시야를 계산해
    /// 바닥 위에 반투명 검은 막을 씌운다: 지금 보이는 곳은 투명, 가 본 적 있는 곳은 반쯤, 처음 보는 곳은 짙게.
    /// 벽 너머는 서버와 같은 규칙으로 가려진다.
    ///
    /// 화면만 가리는 것이 아니라 서버가 시야 밖 적을 아예 보내지 않으므로(전송 필터), 이 막을 꺼도 적은 보이지 않는다.
    /// 서버 설정에서 Fog of War 를 끄면 막도 감춘다.
    /// </summary>
    public sealed class FogOfWarView : MonoBehaviour
    {
        private const float UpdateInterval = 0.1f;
        private const byte UnexploredAlpha = 215;
        private const byte ExploredAlpha = 130;

        public GameClient Client;
        public UnitViewManager Views;

        private readonly VisionGrid _vision = new VisionGrid();
        private Texture2D _texture;
        private Color32[] _pixels;
        private bool[] _explored;
        private GameObject _overlay;
        private float _timer;
        private MapKind? _wallsFor;
        private IReadOnlyList<WallRect> _walls = new WallRect[0];

        /// <summary>지금 내 팀에 보이는 위치인지 (마지막 계산 기준).</summary>
        public bool IsVisible(System.Numerics.Vector2 p) => _vision.IsVisible(p);

        /// <summary>지금 보이는 칸 수 (자동 확인·디버그용).</summary>
        public int VisibleCells { get; private set; }

        private void Start()
        {
            var size = _vision.Size;
            _pixels = new Color32[size * size];
            _explored = new bool[size * size];
            _texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            _overlay = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _overlay.name = "Fog of War";
            Destroy(_overlay.GetComponent<Collider>());
            _overlay.transform.SetParent(transform, false);

            // 바닥(XZ 평면)에 눕힌다. Quad 는 XY 평면이므로 x축으로 90도.
            var mapSize = SimConstants.MapHalfSize * 2;
            _overlay.transform.SetPositionAndRotation(new Vector3(0, 0.03f, 0), Quaternion.Euler(90, 0, 0));
            _overlay.transform.localScale = new Vector3(mapSize, mapSize, 1);

            // Sprites/Default 는 빌드에 항상 포함되는 불투명도 지원 셰이더 (조명 없음).
            var material = new Material(Shader.Find("Sprites/Default")) { mainTexture = _texture, renderQueue = 3000 };
            _overlay.GetComponent<Renderer>().material = material;
            _overlay.SetActive(false);
        }

        private void Update()
        {
            if (Client == null || _texture == null)
            {
                return;
            }

            var enabled = Client.Settings is { FogOfWar: true } && Client.MyTeam != 0;
            if (_overlay.activeSelf != enabled)
            {
                _overlay.SetActive(enabled);
            }

            if (!enabled)
            {
                return;
            }

            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0)
            {
                return;
            }

            _timer = UpdateInterval;
            Recompute();
        }

        private void Recompute()
        {
            var map = Client.Settings?.Map ?? MapKind.Open;
            if (_wallsFor != map)
            {
                _walls = MapLayouts.Create(map); // 서버와 같은 벽으로 가림 계산
                _wallsFor = map;
            }

            _vision.Clear();
            foreach (var kv in Views.InterpolatedPositions)
            {
                if (Client.Latest.TryGetValue(kv.Key, out var u) && u.Team == Client.MyTeam)
                {
                    _vision.Reveal(kv.Value, UnitArchetypes.Of(u.Kind).SightRange, _walls);
                }
            }

            // 텍스처 (x, y) = 맵 (x, z). Quad 를 x축 90도로 눕히면 텍스처 v 가 +z 방향이 된다.
            var size = _vision.Size;
            var visible = 0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var i = y * size + x;
                    byte alpha;
                    if (_vision.IsCellVisible(x, y))
                    {
                        _explored[i] = true;
                        alpha = 0;
                        visible++;
                    }
                    else
                    {
                        alpha = _explored[i] ? ExploredAlpha : UnexploredAlpha;
                    }

                    _pixels[i] = new Color32(0, 0, 0, alpha);
                }
            }

            VisibleCells = visible;
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);
        }
    }
}
