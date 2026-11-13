using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpenRTS.Client.Editor
{
    /// <summary>
    /// 메뉴 OpenRTS/Build Windows Player, 또는 배치 모드:
    /// Unity.exe -batchmode -quit -projectPath Client -executeMethod OpenRTS.Client.Editor.BuildScript.BuildWindows
    /// 출력: Client/Builds/Windows/OpenRTS.exe
    /// </summary>
    public static class BuildScript
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string OutputPath = "Builds/Windows/OpenRTS.exe";

        [MenuItem("OpenRTS/Build Windows Player")]
        public static void BuildWindows()
        {
            EnsureMainScene();

            // 창 두 개를 띄워 비교하기 쉽게 창 모드 + 백그라운드 실행
            PlayerSettings.runInBackground = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.resizableWindow = true;

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });

            var ok = report.summary.result == BuildResult.Succeeded;
            Debug.Log($"[OpenRTS] build {report.summary.result}: {Path.GetFullPath(OutputPath)} ({report.summary.totalSize / (1024 * 1024)} MB)");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        /// <summary>빈 메인 씬을 만들고 빌드 목록에 등록한다. 내용은 ClientBootstrap 이 런타임에 만든다.</summary>
        [MenuItem("OpenRTS/Create Main Scene")]
        public static void EnsureMainScene()
        {
            if (!File.Exists(ScenePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }

            if (!EditorBuildSettings.scenes.Any(s => s.path == ScenePath))
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            }
        }
    }
}
