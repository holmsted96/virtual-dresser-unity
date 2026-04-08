// BuildScript.cs
// 메뉴: VirtualDresser > Build Windows
// 또는 CLI: Unity.exe -batchmode -executeMethod VirtualDresser.Editor.BuildScript.BuildWindows -quit

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualDresser.Editor
{
    public static class BuildScript
    {
        private static readonly string OutputDir = "c:/vd/build";
        private static readonly string ExeName   = "VirtualDresser.exe";

        // Standalone 빌드에 반드시 포함시킬 셰이더 이름 목록
        // Shader.Find()로만 참조하는 셰이더는 Unity가 strip하므로 여기서 강제 등록
        private static readonly string[] RequiredShaders =
        {
            "lilToon",
            "Hidden/lilToonCutout",
            "Hidden/lilToonTransparent",
            "lilToon/lilToon",
            "lilToon/lilToon Cutout",
            "lilToon/lilToon Transparent",
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
        };

        [MenuItem("VirtualDresser/Build Windows")]
        public static void BuildWindows()
        {
            var outputPath = Path.Combine(OutputDir, ExeName);
            Directory.CreateDirectory(OutputDir);

            // ── lilToon 셰이더를 Always Included Shaders에 등록 ──
            RegisterAlwaysIncludedShaders();

            var scenes = new[] { "Assets/Scenes/SampleScene.unity" };
            var foundScenes = AssetDatabase.FindAssets("t:Scene");
            if (foundScenes.Length > 0)
                scenes = new[] { AssetDatabase.GUIDToAssetPath(foundScenes[0]) };

            PlayerSettings.fullScreenMode     = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth  = 1280;
            PlayerSettings.defaultScreenHeight = 800;
            PlayerSettings.resizableWindow     = true;

            var options = new BuildPlayerOptions
            {
                scenes           = scenes,
                locationPathName = outputPath,
                target           = BuildTarget.StandaloneWindows64,
                options          = BuildOptions.None,
            };

            Debug.Log($"[Build] Building → {outputPath}");
            var report = BuildPipeline.BuildPlayer(options);

            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"[Build] Build complete: {outputPath}");
                EditorUtility.RevealInFinder(outputPath);
            }
            else
            {
                Debug.LogError($"[Build] Build failed: {report.summary.result}");
            }
        }

        /// <summary>
        /// RequiredShaders 목록을 Graphics Settings > Always Included Shaders에 추가.
        /// 이미 포함된 항목은 중복 추가하지 않음.
        /// </summary>
        private static void RegisterAlwaysIncludedShaders()
        {
            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>(
                "ProjectSettings/GraphicsSettings.asset");

            // SerializedObject로 alwaysIncludedShaders 배열 직접 편집
            var so  = new UnityEditor.SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");

            // 현재 등록된 셰이더 이름 수집
            var existing = new HashSet<string>();
            for (int i = 0; i < arr.arraySize; i++)
            {
                var s = arr.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (s != null) existing.Add(s.name);
            }

            int added = 0;
            foreach (var shaderName in RequiredShaders)
            {
                if (existing.Contains(shaderName)) continue;
                var shader = Shader.Find(shaderName);
                if (shader == null) continue; // 미설치면 스킵

                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = shader;
                existing.Add(shaderName);
                added++;
                Debug.Log($"[Build] Always Included Shaders에 추가: {shaderName}");
            }

            if (added > 0)
            {
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                Debug.Log($"[Build] {added}개 셰이더 등록 완료");
            }
            else
            {
                Debug.Log("[Build] Always Included Shaders — 추가 항목 없음 (이미 등록됨)");
            }
        }
    }
}
