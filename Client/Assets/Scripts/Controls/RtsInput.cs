using OpenRTS.Client.Net;
using UnityEngine;
using Vector2 = System.Numerics.Vector2;

namespace OpenRTS.Client.Controls
{
    /// <summary>우클릭 이동 명령, WASD/화살표 카메라 이동, 휠 줌.</summary>
    public sealed class RtsInput : MonoBehaviour
    {
        public GameClient Client;
        public Camera Camera;
        public float PanSpeed = 30f;
        public float ZoomSpeed = 8f;

        private Transform _marker;
        private float _markerTime;

        private void Update()
        {
            if (Camera == null)
            {
                return;
            }

            if (UnityEngine.Input.GetMouseButtonDown(1) && TryGetGroundPoint(UnityEngine.Input.mousePosition, out var point))
            {
                Client.SendMove(new Vector2(point.x, point.z));
                ShowMarker(point);
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

            if (_marker != null && _marker.gameObject.activeSelf)
            {
                _markerTime += Time.unscaledDeltaTime;
                _marker.localScale = Vector3.one * Mathf.Lerp(1.2f, 0.2f, _markerTime / 0.5f);
                if (_markerTime > 0.5f)
                {
                    _marker.gameObject.SetActive(false);
                }
            }
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

        private void ShowMarker(Vector3 point)
        {
            if (_marker == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "Move Marker";
                Destroy(go.GetComponent<Collider>());
                go.GetComponent<Renderer>().material.color = new Color(0.3f, 1f, 0.4f);
                _marker = go.transform;
            }

            _marker.position = point + Vector3.up * 0.02f;
            _marker.gameObject.SetActive(true);
            _markerTime = 0;
        }
    }
}
