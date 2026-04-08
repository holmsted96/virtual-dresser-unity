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

        // Always Included에는 아무 셰이더도 넣지 않음
        // URP/Lit도 변형이 수백 개라 Always Included 시 빌드 수 시간 소요
        // lilToon + URP 모두 ShaderVariantCollection으로 관리
        private static readonly string[] UrpRequiredShaders = Array.Empty<string>();

        private static readonly string LilToonSvcPath = "Assets/Resources/LilToonVariants.shadervariants";

        [MenuItem("VirtualDresser/Build Windows")]
        public static void BuildWindows()
        {
            var outputPath = Path.Combine(OutputDir, ExeName);
            Directory.CreateDirectory(OutputDir);

            // Always Included에서 lilToon 제거, URP만 유지
            CleanupAlwaysIncludedShaders();

            // lilToon ShaderVariantCollection 생성 (3가지 변형만)
            CreateLilToonVariantCollection();

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
        /// Always Included Shaders에서 lilToon 계열을 제거하고 URP만 유지.
        /// 이전 세션에서 잘못 추가된 lilToon 엔트리도 정리.
        /// </summary>
        private static void CleanupAlwaysIncludedShaders()
        {
            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>(
                "ProjectSettings/GraphicsSettings.asset");
            var so  = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");

            // lilToon 계열 제거
            for (int i = arr.arraySize - 1; i >= 0; i--)
            {
                var s = arr.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (s != null && (s.name.Contains("lil") || s.name.Contains("Lil")))
                {
                    arr.DeleteArrayElementAtIndex(i);
                    Debug.Log($"[Build] Always Included에서 제거: {s.name}");
                }
            }

            // URP 셰이더 추가 (없으면)
            var existing = new HashSet<string>();
            for (int i = 0; i < arr.arraySize; i++)
            {
                var s = arr.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (s != null) existing.Add(s.name);
            }

            foreach (var shaderName in UrpRequiredShaders)
            {
                if (existing.Contains(shaderName)) continue;
                var shader = Shader.Find(shaderName);
                if (shader == null) continue;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = shader;
                Debug.Log($"[Build] Always Included 추가: {shaderName}");
            }

            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// lilToon의 실제 사용 변형 3가지만 ShaderVariantCollection으로 등록.
        /// Always Included 대신 이 방식을 쓰면 빌드 시간이 대폭 단축됨.
        /// </summary>
        private static void CreateLilToonVariantCollection()
        {
            // Resources 폴더 생성 확인
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");

            // 기존 컬렉션 로드 or 새로 생성
            var svc = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(LilToonSvcPath);
            if (svc == null)
            {
                svc = new ShaderVariantCollection();
                AssetDatabase.CreateAsset(svc, LilToonSvcPath);
            }
            else
            {
                svc.Clear();
            }

            int added = 0;

            // ── lilToon 변형 ──
            var lilToon = Shader.Find("lilToon");
            if (lilToon != null)
            {
                var lilKeywordSets = new[]
                {
                    new string[0],                 // 불투명
                    new[] { "_ALPHATEST_ON" },     // Cutout
                    new[] { "_ALPHABLEND_ON" },    // Transparent
                };
                var lilPassTypes = new[] { PassType.ScriptableRenderPipeline, PassType.ShadowCaster };

                foreach (var kw in lilKeywordSets)
                    foreach (var pt in lilPassTypes)
                    {
                        try { svc.Add(new ShaderVariantCollection.ShaderVariant(lilToon, pt, kw)); added++; }
                        catch { }
                    }
            }
            else
            {
                Debug.LogWarning("[Build] lilToon 셰이더를 찾을 수 없음");
            }

            // URP/Lit은 SVC에 넣지 않음 — multi_compile 수백 개 → 빌드 느림
            // TriLib이 런타임에 URP/Lit 머티리얼 생성해도 MaterialManager가 즉시 lilToon으로 교체하므로 문제없음

            EditorUtility.SetDirty(svc);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Build] ShaderVariantCollection 생성 완료: {added}개 변형 → {LilToonSvcPath}");

            RegisterPreloadedShaderCollection(svc);
        }

        /// <summary>
        /// ShaderVariantCollection을 GraphicsSettings의 Preloaded Shaders에 등록.
        /// Preloaded Shaders에 포함된 변형은 빌드에 반드시 포함됨.
        /// </summary>
        private static void RegisterPreloadedShaderCollection(ShaderVariantCollection svc)
        {
            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>(
                "ProjectSettings/GraphicsSettings.asset");
            var so  = new SerializedObject(gs);
            var arr = so.FindProperty("m_PreloadedShaders");

            // 이미 등록된 경우 스킵
            for (int i = 0; i < arr.arraySize; i++)
            {
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == svc)
                {
                    Debug.Log("[Build] ShaderVariantCollection 이미 등록됨");
                    return;
                }
            }

            arr.InsertArrayElementAtIndex(arr.arraySize);
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = svc;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log("[Build] ShaderVariantCollection → Graphics Settings Preloaded Shaders 등록 완료");
        }
    }
}
