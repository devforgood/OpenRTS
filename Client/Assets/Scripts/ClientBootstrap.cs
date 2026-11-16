using OpenRTS.Client.Controls;
using OpenRTS.Client.Debugging;
using OpenRTS.Client.Net;
using OpenRTS.Client.View;
using OpenRTS.Shared;
using UnityEngine;

namespace OpenRTS.Client
{
    /// <summary>
    /// 클라이언트 진입점. 어떤 씬에서 Play 해도 카메라·바닥·네트워크·뷰 객체를 코드로 만든다.
    /// (씬 편집 없이 이동 동기화를 확인하기 위한 구성. 정식 씬 구성은 이후 단계에서.)
    /// </summary>
    public static class ClientBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Object.FindAnyObjectByType<GameClient>() != null)
            {
                return;
            }

            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            Debug.Log($"[OpenRTS] client boot (protocol v{SimConstants.ProtocolVersion})");

            var camera = SetupCamera();
            SetupLight();
            SetupGround();

            var root = new GameObject("OpenRTS");
            var client = root.AddComponent<GameClient>();

            var map = root.AddComponent<MapView>();
            map.Client = client;

            var views = root.AddComponent<UnitViewManager>();
            views.Client = client;
            views.Camera = camera;

            var effects = root.AddComponent<CombatEffects>();
            effects.Client = client;
            effects.Views = views;

            var input = root.AddComponent<RtsInput>();
            input.Client = client;
            input.Views = views;
            input.Camera = camera;
            views.Selection = input.Selection;

            var hud = root.AddComponent<NetDebugHud>();
            hud.Client = client;
            hud.Views = views;
            hud.Selection = input.Selection;


            var panel = root.AddComponent<SimSettingsPanel>();
            panel.Client = client;

            if (MovementAutoTest.IsRequested())
            {
                var test = root.AddComponent<MovementAutoTest>();
                test.Client = client;
                test.Views = views;
            }

            if (CollisionAutoTest.IsRequested())
            {
                var test = root.AddComponent<CollisionAutoTest>();
                test.Client = client;
                test.Views = views;
            }

            if (BattleAutoTest.IsRequested())
            {
                var test = root.AddComponent<BattleAutoTest>();
                test.Client = client;
                test.Views = views;
                test.Effects = effects;
            }

        }

        private static Camera SetupCamera()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                camera = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            }

            camera.transform.SetPositionAndRotation(new Vector3(0, 45, -35), Quaternion.Euler(55, 0, 0));
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
            camera.farClipPlane = 500;
            return camera;
        }

        private static void SetupLight()
        {
            if (Object.FindAnyObjectByType<Light>() != null)
            {
                return;
            }

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50, -30, 0);
        }

        private static void SetupGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            Object.Destroy(ground.GetComponent<Collider>());

            // Plane 은 10×10 이므로 맵 크기에 맞춰 늘린다.
            var size = SimConstants.MapHalfSize * 2f;
            ground.transform.localScale = new Vector3(size / 10f, 1, size / 10f);

            // 1유닛 간격 바둑판 텍스처로 이동량이 보이게 한다.
            var tex = new Texture2D(2, 2) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            var a = new Color(0.32f, 0.36f, 0.32f);
            var b = new Color(0.28f, 0.31f, 0.28f);
            tex.SetPixels(new[] { a, b, b, a });
            tex.Apply();

            var material = ground.GetComponent<Renderer>().material;
            material.mainTexture = tex;
            material.mainTextureScale = new Vector2(size / 2f, size / 2f);
        }
    }
}
