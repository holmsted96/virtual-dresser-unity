// WarudoBuildScript.cs
// Virtual Dresser → .warudo 변환 스크립트
//
// 실행 방법 (헤들리스):
//   Unity.exe -batchmode -nographics
//             -projectPath "C:/vd/vd-warudo-converter"
//             -executeMethod WarudoConverter.WarudoBuildScript.Build
//             -inputPath  "C:/Temp/vd-export/shinano"
//             -outputPath "C:/Users/.../Desktop/shinano.warudo"
//             -avatarName "shinano"
//             -quit
//
// 사전 준비:
//   1. Unity 2021.3.45f2 설치
//   2. Warudo SDK 설치:
//      Package Manager → Add from git URL:
//      https://github.com/HakuyaLabs/Warudo-Mod-Tool.git#0.14.3.10

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WarudoConverter
{
    public static class WarudoBuildScript
    {
        // ─── 커맨드라인 진입점 ───

        public static void Build()
        {
            var args      = Environment.GetCommandLineArgs();
            var inputPath  = GetArg(args, "-inputPath");
            var outputPath = GetArg(args, "-outputPath");
            var avatarName = GetArg(args, "-avatarName") ?? "Character";

            if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(outputPath))
            {
                Debug.LogError("[WarudoBuild] -inputPath / -outputPath 인수 누락");
                EditorApplication.Exit(1);
                return;
            }

            try
            {
                Debug.Log($"[WarudoBuild] 변환 시작: {avatarName}");
                RunBuild(inputPath, outputPath, avatarName);
                Debug.Log($"[WarudoBuild] ✅ 완료: {outputPath}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WarudoBuild] 실패: {e.Message}\n{e.StackTrace}");
                EditorApplication.Exit(1);
            }
        }

        // ─── 메인 변환 로직 ───

        private static void RunBuild(string inputPath, string outputPath, string avatarName)
        {
            // ── 1. 입력 에셋 임포트 ──
            var importRoot = $"Assets/VDImport_{avatarName}";
            ImportAssetsFromFolder(inputPath, importRoot);
            AssetDatabase.Refresh();

            // ── 2. FBX 로드 → Prefab 생성 ──
            var prefabPath = CreateCharacterPrefab(importRoot, avatarName);
            if (string.IsNullOrEmpty(prefabPath))
                throw new Exception("Character Prefab 생성 실패");

            // ── 3. Warudo SDK로 .warudo 빌드 ──
            BuildWarudoMod(prefabPath, outputPath, avatarName);

            // ── 4. 임시 임포트 에셋 정리 ──
            AssetDatabase.DeleteAsset(importRoot);
            AssetDatabase.Refresh();
        }

        // ─── 에셋 임포트 ───

        private static void ImportAssetsFromFolder(string srcFolder, string destAssetPath)
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath,
                destAssetPath.Replace("Assets/", "")));

            var files = Directory.GetFiles(srcFolder, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var ext  = Path.GetExtension(file).ToLowerInvariant();
                var name = Path.GetFileName(file);

                if (ext != ".fbx" && ext != ".png" && ext != ".jpg"
                    && ext != ".jpeg" && ext != ".tga") continue;

                var dest = Path.Combine(Application.dataPath,
                    destAssetPath.Replace("Assets/", ""), name);

                if (!File.Exists(dest))
                    File.Copy(file, dest, overwrite: false);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[WarudoBuild] 에셋 임포트 완료: {destAssetPath}");
        }

        // ─── Character Prefab 생성 ───

        private static string CreateCharacterPrefab(string importRoot, string avatarName)
        {
            // FBX 탐색 (가장 큰 파일 기준)
            var fbxGuids = AssetDatabase.FindAssets("t:Model", new[] { importRoot });
            if (fbxGuids.Length == 0)
            {
                Debug.LogError("[WarudoBuild] FBX 없음");
                return null;
            }

            // 가장 큰 FBX = 메인 아바타
            string bestFbxPath = null;
            long   bestSize    = 0;
            foreach (var guid in fbxGuids)
            {
                var path     = AssetDatabase.GUIDToAssetPath(guid);
                var fullPath = Path.Combine(Directory.GetCurrentDirectory(), path);
                var size     = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                if (size > bestSize) { bestSize = size; bestFbxPath = path; }
            }

            // FBX → Humanoid 리그 설정
            var importer = AssetImporter.GetAtPath(bestFbxPath) as ModelImporter;
            if (importer != null)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.SaveAndReimport();
            }

            // Prefab 생성 — Warudo SDK는 "Character.prefab" 이름을 요구
            var go          = AssetDatabase.LoadAssetAtPath<GameObject>(bestFbxPath);
            var instance    = (GameObject)PrefabUtility.InstantiatePrefab(go);
            instance.name   = avatarName;

            var prefabDir   = $"{importRoot}/Warudo";
            Directory.CreateDirectory(Path.Combine(Application.dataPath,
                prefabDir.Replace("Assets/", "")));
            var prefabPath  = $"{prefabDir}/Character.prefab";
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            UnityEngine.Object.DestroyImmediate(instance);
            AssetDatabase.Refresh();

            Debug.Log($"[WarudoBuild] Character.prefab 생성: {prefabPath}");
            return prefabPath;
        }

        // ─── Warudo SDK 빌드 ───

        private static void BuildWarudoMod(string prefabPath, string outputPath, string avatarName)
        {
#if WARUDO_SDK
            // ★ Warudo SDK 설치 후 활성화되는 경로
            // SDK: https://github.com/HakuyaLabs/Warudo-Mod-Tool.git#0.14.3.10

            var settings = ScriptableObject.CreateInstance<UMod.BuildEngine.ModSettings>();
            settings.modName    = avatarName;
            settings.modVersion = "1.0.0";
            settings.modAuthor  = "VirtualDresser";

            // Character.prefab을 mod 에셋으로 등록
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            settings.rootObjects = new GameObject[] { prefab };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            UMod.BuildEngine.ModToolsUtil.StartBuild(settings, outputPath);
            Debug.Log($"[WarudoBuild] Warudo SDK 빌드 완료: {outputPath}");
#else
            // ★ Warudo SDK 미설치 시 — AssetBundle 직접 빌드 (폴백)
            BuildWarudoAsAssetBundle(prefabPath, outputPath, avatarName);
#endif
        }

        // ─── AssetBundle 폴백 빌드 (Warudo SDK 없을 때) ───

        private static void BuildWarudoAsAssetBundle(
            string prefabPath, string outputPath, string avatarName)
        {
            Debug.LogWarning("[WarudoBuild] Warudo SDK 미설치 — AssetBundle 폴백 모드");

            var importer = AssetImporter.GetAtPath(prefabPath);
            if (importer == null) throw new Exception($"AssetImporter 없음: {prefabPath}");

            importer.assetBundleName = avatarName.ToLower();
            AssetDatabase.RemoveUnusedAssetBundleNames();

            var bundleDir = Path.Combine(Path.GetTempPath(), "vd-bundle-temp");
            Directory.CreateDirectory(bundleDir);

            var builds = new[]
            {
                new AssetBundleBuild
                {
                    assetBundleName = avatarName.ToLower() + ".warudo",
                    assetNames      = new[] { prefabPath }
                }
            };

            BuildPipeline.BuildAssetBundles(
                bundleDir, builds,
                BuildAssetBundleOptions.None,
                BuildTarget.StandaloneWindows64);

            var builtPath = Path.Combine(bundleDir, avatarName.ToLower() + ".warudo");
            if (File.Exists(builtPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.Copy(builtPath, outputPath, overwrite: true);
                Debug.Log($"[WarudoBuild] AssetBundle 폴백 빌드 완료: {outputPath}");
            }
            else
            {
                throw new Exception($"빌드 결과물 없음: {builtPath}");
            }
        }

        // ─── 유틸리티 ───

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
