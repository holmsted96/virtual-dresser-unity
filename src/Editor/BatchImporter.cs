// BatchImporter.cs
// Unity Editor 전용 — -batchmode 실행 시 .unitypackage → AssetBundle 변환
//
// 실행 방법:
//   Unity.exe -batchmode -nographics
//             -projectPath "C:\VD\unity-project"
//             -executeMethod VirtualDresser.Editor.BatchImporter.ImportFromArgs
//             -packagePath "C:\Users\...\Shinano.unitypackage"
//             -outputPath  "C:\AppData\VirtualDresser\cache\{hash}"
//             -quit
//
// 이 파일은 Assets/Editor/ 폴더에 배치

#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VirtualDresser.Editor
{
    /// <summary>
    /// Unity Headless 배치 임포터
    /// 현재 Rust import_with_unity 명령의 C# 완전 대체
    /// </summary>
    public static class BatchImporter
    {
        // ─── 커맨드라인 진입점 ───

        /// <summary>
        /// -batchmode에서 호출되는 메인 메서드
        /// -executeMethod VirtualDresser.Editor.BatchImporter.ImportFromArgs
        /// </summary>
        public static void ImportFromArgs()
        {
            var args = Environment.GetCommandLineArgs();
            string packagePath = GetArg(args, "-packagePath");
            string outputPath  = GetArg(args, "-outputPath");

            if (string.IsNullOrEmpty(packagePath) || string.IsNullOrEmpty(outputPath))
            {
                Debug.LogError("[BatchImporter] -packagePath / -outputPath 인수 누락");
                EditorApplication.Exit(1);
                return;
            }

            try
            {
                ImportPackage(packagePath, outputPath);
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BatchImporter] 임포트 실패: {e.Message}\n{e.StackTrace}");
                EditorApplication.Exit(1);
            }
        }

        // ─── 핵심 임포트 로직 ───

        public static void ImportPackage(string packagePath, string outputPath)
        {
            Debug.Log($"[BatchImporter] 임포트 시작: {packagePath}");

            Directory.CreateDirectory(outputPath);

            // 1. .unitypackage 임포트 (Unity Editor 네이티브)
            //    AssetDatabase가 FBX/텍스처/머티리얼을 자동 임포트
            AssetDatabase.ImportPackage(packagePath, interactive: false);
            AssetDatabase.Refresh();
            AssetDatabase.SaveAssets();

            Debug.Log("[BatchImporter] 패키지 임포트 완료, 에셋 수집 중...");

            // 2. 임포트된 FBX 찾기
            var fbxGuids = AssetDatabase.FindAssets("t:Model");
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab");

            // 3. Manifest JSON 생성 (현재 UnityManifest 타입 호환)
            var manifest = BuildManifest(fbxGuids, prefabGuids, packagePath);
            var manifestJson = JsonUtility.ToJson(manifest, prettyPrint: true);
            File.WriteAllText(Path.Combine(outputPath, "manifest.json"), manifestJson);

            Debug.Log($"[BatchImporter] manifest.json 저장 완료: {manifest.meshes.Length}개 메쉬");

            // 4. AssetBundle 빌드 (선택적 — 런타임 로드용)
            BuildAssetBundles(outputPath);

            Debug.Log($"[BatchImporter] ✅ 완료: {outputPath}");
        }

        // ─── Manifest 생성 ───

        private static VDManifest BuildManifest(string[] fbxGuids, string[] prefabGuids, string packagePath)
        {
            var meshInfos = new System.Collections.Generic.List<VDMeshInfo>();

            foreach (var guid in fbxGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;

                // 메쉬 정보 수집
                foreach (var renderer in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var meshInfo = new VDMeshInfo
                    {
                        name = renderer.name,
                        path = path,
                        isSkinned = true,
                        vertexCount = renderer.sharedMesh?.vertexCount ?? 0,
                        boneCount = renderer.bones?.Length ?? 0,
                        visible = renderer.gameObject.activeSelf,
                    };

                    // 머티리얼 이름 수집
                    if (renderer.sharedMaterials != null)
                    {
                        var matNames = new string[renderer.sharedMaterials.Length];
                        var matTextures = new VDMaterialTexture[renderer.sharedMaterials.Length];

                        for (int i = 0; i < renderer.sharedMaterials.Length; i++)
                        {
                            var mat = renderer.sharedMaterials[i];
                            if (mat == null) continue;

                            matNames[i] = mat.name;

                            // Unity Material에서 텍스처 정보 추출
                            matTextures[i] = new VDMaterialTexture
                            {
                                materialName = mat.name,
                                mainTexture = mat.mainTexture?.name,
                                isTransparent = mat.renderQueue > 2500,
                            };
                        }
                        meshInfo.materialNames = matNames;
                        meshInfo.materialTextures = matTextures;
                    }

                    meshInfos.Add(meshInfo);
                }
            }

            return new VDManifest
            {
                packageName = Path.GetFileNameWithoutExtension(packagePath),
                timestamp = DateTime.UtcNow.ToString("o"),
                meshes = meshInfos.ToArray(),
            };
        }

        // ─── AssetBundle 빌드 ───

        private static void BuildAssetBundles(string outputPath)
        {
            var bundlePath = Path.Combine(outputPath, "bundles");
            Directory.CreateDirectory(bundlePath);

            BuildPipeline.BuildAssetBundles(
                bundlePath,
                BuildAssetBundleOptions.None,
                BuildTarget.StandaloneWindows64
            );

            Debug.Log($"[BatchImporter] AssetBundle 빌드 완료: {bundlePath}");
        }

        // ─── Warudo 익스포트 ───

        /// <summary>
        /// 현재 씬의 아바타+의상을 Warudo SDK 프로젝트에 임포트 가능한 형태로 익스포트.
        ///
        /// 출력 구조:
        ///   [exportDir]/
        ///     avatar/   — 아바타 FBX + 텍스처
        ///     clothing/ — 의상 FBX + 텍스처 (있을 경우)
        ///     Character.prefab  — 씬에서 생성한 Prefab (Warudo Setup Character 용)
        ///     warudo_mod.json   — 아바타 메타데이터
        ///     README_WARUDO.txt — Warudo SDK 임포트 가이드
        /// </summary>
        public static void ExportForWarudo(
            string exportDir,
            string avatarName,
            VirtualDresser.Runtime.ParseResult avatarParse,
            VirtualDresser.Runtime.ParseResult clothingParse,
            GameObject combinedRoot)
        {
            Directory.CreateDirectory(exportDir);
            Debug.Log($"[Exporter] Warudo 익스포트 시작: {exportDir}");

            // ── 1. 에셋 복사 ──
            if (avatarParse != null)
                CopyParseAssets(avatarParse, Path.Combine(exportDir, "avatar"));
            if (clothingParse != null)
                CopyParseAssets(clothingParse, Path.Combine(exportDir, "clothing"));

            // ── 2. 씬 GO → Prefab 저장 ──
            if (combinedRoot != null)
            {
                var prefabDir = "Assets/VirtualDresserExports";
                Directory.CreateDirectory(Path.Combine(Application.dataPath,
                    "VirtualDresserExports"));
                AssetDatabase.Refresh();

                var prefabPath = $"{prefabDir}/{avatarName}_Character.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(combinedRoot, prefabPath);
                AssetDatabase.Refresh();

                if (prefab != null)
                {
                    // Prefab을 exportDir에도 복사
                    var prefabSrc = Path.Combine(Application.dataPath,
                        "VirtualDresserExports", $"{avatarName}_Character.prefab");
                    var prefabDst = Path.Combine(exportDir, "Character.prefab");
                    if (File.Exists(prefabSrc))
                        File.Copy(prefabSrc, prefabDst, overwrite: true);

                    // .unitypackage 빌드
                    var unitypackagePath = Path.Combine(exportDir, $"{avatarName}_warudo.unitypackage");
                    AssetDatabase.ExportPackage(
                        prefabPath,
                        unitypackagePath,
                        ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies
                    );
                    Debug.Log($"[Exporter] .unitypackage 생성: {unitypackagePath}");
                }
            }

            // ── 3. 메타데이터 JSON ──
            var json = CreateWarudoJson(avatarName, avatarParse, clothingParse);
            File.WriteAllText(Path.Combine(exportDir, "warudo_mod.json"), json);

            // ── 4. README ──
            File.WriteAllText(Path.Combine(exportDir, "README_WARUDO.txt"), GetWarudoReadme(avatarName));

            AssetDatabase.Refresh();
            Debug.Log($"[Exporter] ✅ Warudo 익스포트 완료: {exportDir}");
            EditorUtility.RevealInFinder(exportDir);
        }

        private static void CopyParseAssets(VirtualDresser.Runtime.ParseResult parse, string destDir)
        {
            if (string.IsNullOrEmpty(parse.TempDirPath) || !Directory.Exists(parse.TempDirPath))
                return;
            Directory.CreateDirectory(destDir);

            foreach (var fbx in parse.ExtractedFbxPaths)
                if (File.Exists(fbx))
                    File.Copy(fbx, Path.Combine(destDir, Path.GetFileName(fbx)), overwrite: true);

            foreach (var tex in parse.ExtractedTextureNames)
            {
                var src = Path.Combine(parse.TempDirPath, tex);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(destDir, tex), overwrite: true);
            }

            Debug.Log($"[Exporter] 에셋 복사 완료: {destDir} " +
                      $"(FBX {parse.ExtractedFbxPaths.Count}개, 텍스처 {parse.ExtractedTextureNames.Count}개)");
        }

        private static string CreateWarudoJson(
            string avatarName,
            VirtualDresser.Runtime.ParseResult avatarParse,
            VirtualDresser.Runtime.ParseResult clothingParse)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{avatarName}\",");
            sb.AppendLine($"  \"version\": \"1.0.0\",");
            sb.AppendLine($"  \"author\": \"VirtualDresser\",");
            sb.AppendLine($"  \"exportedAt\": \"{DateTime.UtcNow:o}\",");
            sb.AppendLine($"  \"unityVersion\": \"2022.3\",");
            sb.AppendLine($"  \"warudoSdkNote\": \"Unity 2021.3.45f2 + Warudo SDK 로 빌드하세요\",");
            sb.AppendLine($"  \"avatar\": {{");
            sb.AppendLine($"    \"filename\": \"{Path.GetFileName(avatarParse?.Filename ?? "")}\",");
            sb.AppendLine($"    \"detectedName\": \"{avatarParse?.DetectedName ?? "\""}\"");
            sb.AppendLine($"  }},");
            sb.AppendLine($"  \"clothing\": {{");
            sb.AppendLine($"    \"filename\": \"{Path.GetFileName(clothingParse?.Filename ?? "")}\",");
            sb.AppendLine($"    \"meshCount\": {clothingParse?.ExtractedFbxPaths?.Count ?? 0}");
            sb.AppendLine($"  }}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GetWarudoReadme(string avatarName) => $@"=== Virtual Dresser → Warudo 익스포트 가이드 ===

아바타명: {avatarName}
익스포트: {DateTime.Now:yyyy-MM-dd HH:mm}

─── Warudo SDK로 .warudo 파일 만들기 ───

1. Unity 2021.3.45f2 설치
2. 새 Unity 프로젝트 생성
3. Warudo SDK 설치:
   Package Manager → Add from git URL:
   https://github.com/HakuyaLabs/Warudo-Mod-Tool.git#0.14.3.10

4. 이 폴더의 {avatarName}_warudo.unitypackage 임포트:
   Assets → Import Package → Custom Package

5. Warudo → Setup Character... 실행
   → Character.prefab 선택

6. Warudo → Build Mod (Ctrl+Shift+B)
   → {avatarName}.warudo 생성

7. 생성된 .warudo 파일을:
   [Warudo 설치폴더]/Warudo_Data/StreamingAssets/Characters/ 에 복사

─── VRM으로 바로 사용하기 (SDK 불필요) ───
   다음 버전에서 VRM 직접 익스포트 지원 예정
";

        // ─── 유틸리티 ───

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }

    // ─── Manifest 타입 정의 (현재 UnityManifest 타입과 호환) ───

    [Serializable]
    public class VDManifest
    {
        public string packageName;
        public string timestamp;
        public VDMeshInfo[] meshes;
    }

    [Serializable]
    public class VDMeshInfo
    {
        public string name;
        public string path;
        public bool isSkinned;
        public int vertexCount;
        public int boneCount;
        public bool visible;
        public string[] materialNames;
        public VDMaterialTexture[] materialTextures;
    }

    [Serializable]
    public class VDMaterialTexture
    {
        public string materialName;
        public string mainTexture;
        public string normalMap;
        public string emissionMap;
        public bool isTransparent;
        public float cutoff;
    }
}
#endif
