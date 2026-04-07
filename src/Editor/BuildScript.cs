// BuildScript.cs
// 메뉴: VirtualDresser > Build Windows
// 또는 CLI: Unity.exe -batchmode -executeMethod VirtualDresser.Editor.BuildScript.BuildWindows -quit

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VirtualDresser.Editor
{
    public static class BuildScript
    {
        private static readonly string OutputDir = "c:/vd/build";
        private static readonly string ExeName   = "VirtualDresser.exe";

        [MenuItem("VirtualDresser/Build Windows")]
        public static void BuildWindows()
        {
            var outputPath = Path.Combine(OutputDir, ExeName);
            Directory.CreateDirectory(OutputDir);

            var scenes = new[]
            {
                "Assets/Scenes/SampleScene.unity"
            };

            // 실제 씬 파일 탐색 (이름이 다를 수 있으므로)
            var foundScenes = AssetDatabase.FindAssets("t:Scene");
            if (foundScenes.Length > 0)
                scenes = new[] { AssetDatabase.GUIDToAssetPath(foundScenes[0]) };

            var options = new BuildPlayerOptions
            {
                scenes            = scenes,
                locationPathName  = outputPath,
                target            = BuildTarget.StandaloneWindows64,
                options           = BuildOptions.None,
            };

            Debug.Log($"[Build] 빌드 시작 → {outputPath}");
            var report = BuildPipeline.BuildPlayer(options);

            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"[Build] ✅ 빌드 완료: {outputPath}");
                EditorUtility.RevealInFinder(outputPath);
            }
            else
            {
                Debug.LogError($"[Build] ❌ 빌드 실패: {report.summary.result}");
            }
        }
    }
}
