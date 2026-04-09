// WarudoBuildScript.cs
// Virtual Dresser → .warudo 변환 스크립트

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

#if WARUDO_SDK
using UMod.ModTools.Export;
using UMod.BuildEngine;
#endif

namespace WarudoConverter
{
    public static class WarudoBuildScript
    {
        public static void Build()
        {
            var args       = Environment.GetCommandLineArgs();
            var inputPath  = GetArg(args, "-inputPath");
            var outputPath = GetArg(args, "-outputPath");
            var avatarName = GetArg(args, "-avatarName") ?? "Character";

            if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(outputPath))
            {
                Debug.LogError("[WarudoBuild] -inputPath / -outputPath 누락");
                EditorApplication.Exit(1);
                return;
            }

            try
            {
                Debug.Log($"[WarudoBuild] 시작: {avatarName}");
                RunBuild(inputPath, outputPath, avatarName);
                Debug.Log($"[WarudoBuild] 완료: {outputPath}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WarudoBuild] 실패: {e.Message}\n{e.StackTrace}");
                EditorApplication.Exit(1);
            }
        }

        // ──────────────────────────────────────────────────────
        // 메인 파이프라인
        // ──────────────────────────────────────────────────────

        private static void RunBuild(string inputPath, string outputPath, string avatarName)
        {
            // manifest.json 읽기 (제외 메시 + 머티리얼-텍스처 매핑)
            ReadManifest(inputPath, out var excludedMeshes, out var matTexMap);
            Debug.Log($"[WarudoBuild] 제외 메시: {excludedMeshes.Count}개 — " +
                      string.Join(", ", excludedMeshes));
            Debug.Log($"[WarudoBuild] 머티리얼 매핑: {matTexMap.Count}개");

            var importRoot = $"Assets/VDImport_{avatarName}";
            if (AssetDatabase.IsValidFolder(importRoot))
                AssetDatabase.DeleteAsset(importRoot);

            // 1. 파일 복사 + 임포트
            var avatarDir   = $"{importRoot}/Avatar";
            var clothingDir = $"{importRoot}/Clothing";
            var hairDir     = $"{importRoot}/Hair";

            CopyFiles(inputPath,                             avatarDir,   topOnly: true);
            CopyFiles(Path.Combine(inputPath, "clothing"),   clothingDir, topOnly: false);
            CopyFiles(Path.Combine(inputPath, "hair"),       hairDir,     topOnly: false);

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // 2. 아바타 FBX 찾기
            var avatarFbxPath = FindLargestFbx(avatarDir);
            if (string.IsNullOrEmpty(avatarFbxPath))
                throw new Exception("아바타 FBX 없음");

            // 3. FBX 머티리얼 외부 추출 (텍스처 할당을 위해 필수)
            //    External 모드로 설정하면 Materials/ 폴더에 .mat 파일 생성됨
            ExtractMaterials(avatarFbxPath);
            foreach (var p in FindAllFbx(clothingDir)) ExtractMaterials(p);
            foreach (var p in FindAllFbx(hairDir))     ExtractMaterials(p);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // 4. Humanoid 리그 설정 (아바타만)
            SetupHumanoidRig(avatarFbxPath);

            // 5. 텍스처 → 외부 머티리얼 할당 (manifest 매핑 우선 사용)
            AssignTexturesToMaterials(importRoot, matTexMap);

            // 6. 결합 Prefab 생성
            var prefabPath = BuildCombinedPrefab(
                importRoot, avatarFbxPath, clothingDir, hairDir, avatarName, excludedMeshes);

            // 7. .warudo 빌드
            BuildWarudoMod(prefabPath, outputPath, avatarName);

            // 8. 정리
            AssetDatabase.DeleteAsset(importRoot);
            AssetDatabase.Refresh();
        }

        // ──────────────────────────────────────────────────────
        // 파일 복사
        // ──────────────────────────────────────────────────────

        private static void CopyFiles(string srcDir, string destAssetPath, bool topOnly)
        {
            if (!Directory.Exists(srcDir)) return;

            var fullDest = Path.Combine(Application.dataPath,
                destAssetPath.Replace("Assets/", ""));
            Directory.CreateDirectory(fullDest);

            var option = topOnly
                ? SearchOption.TopDirectoryOnly
                : SearchOption.AllDirectories;

            foreach (var file in Directory.GetFiles(srcDir, "*", option))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".fbx" && ext != ".png" && ext != ".jpg"
                    && ext != ".jpeg" && ext != ".tga" && ext != ".psd") continue;

                var dest = Path.Combine(fullDest, Path.GetFileName(file));
                if (!File.Exists(dest)) File.Copy(file, dest);
            }
        }

        // ──────────────────────────────────────────────────────
        // FBX 탐색
        // ──────────────────────────────────────────────────────

        private static string FindLargestFbx(string assetDir)
        {
            if (!AssetDatabase.IsValidFolder(assetDir)) return null;
            string best = null; long bestSize = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { assetDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var full = Path.Combine(Directory.GetCurrentDirectory(), path);
                var size = File.Exists(full) ? new FileInfo(full).Length : 0;
                if (size > bestSize) { bestSize = size; best = path; }
            }
            return best;
        }

        private static List<string> FindAllFbx(string assetDir)
        {
            var result = new List<string>();
            if (!AssetDatabase.IsValidFolder(assetDir)) return result;
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { assetDir }))
                result.Add(AssetDatabase.GUIDToAssetPath(guid));
            return result;
        }

        // ──────────────────────────────────────────────────────
        // FBX 머티리얼 외부 추출
        // ──────────────────────────────────────────────────────

        private static void ExtractMaterials(string fbxPath)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null) return;
            // External: Unity가 fbxPath 옆 Materials/ 폴더에 .mat 파일 생성
            importer.materialLocation = ModelImporterMaterialLocation.External;
            importer.SaveAndReimport();
            Debug.Log($"[WarudoBuild] 머티리얼 추출: {fbxPath}");
        }

        // ──────────────────────────────────────────────────────
        // Humanoid 리그 설정
        // ──────────────────────────────────────────────────────

        private static void SetupHumanoidRig(string fbxPath)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null) return;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();
            Debug.Log($"[WarudoBuild] Humanoid 설정: {fbxPath}");
        }

        // ──────────────────────────────────────────────────────
        // 텍스처 → 머티리얼 할당
        // ──────────────────────────────────────────────────────

        private static void AssignTexturesToMaterials(
            string importRoot,
            Dictionary<string, string> matTexMap)
        {
            // importRoot 내 모든 텍스처 수집 (파일명 → 에셋 경로)
            var texByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { importRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var key  = Path.GetFileNameWithoutExtension(path);
                if (!texByName.ContainsKey(key)) texByName[key] = path;
            }

            Debug.Log($"[WarudoBuild] 텍스처 {texByName.Count}개 발견");
            if (texByName.Count == 0) return;

            // 외부 .mat 파일에 텍스처 할당
            int assigned = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { importRoot }))
            {
                var matPath = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) continue;

                // 1) manifest 매핑 우선 (앱에서 실제로 적용된 텍스처명)
                string texPath = null;
                if (matTexMap.TryGetValue(mat.name, out var manifestTexName))
                {
                    texByName.TryGetValue(manifestTexName, out texPath);
                    if (texPath == null)
                    {
                        // 파일명에 접두사/접미사 변형이 있을 수 있으므로 부분 일치 시도
                        foreach (var kv in texByName)
                        {
                            if (kv.Key.IndexOf(manifestTexName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                manifestTexName.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                texPath = kv.Value;
                                break;
                            }
                        }
                    }
                    if (texPath != null)
                        Debug.Log($"[WarudoBuild] manifest 매핑: {mat.name} ← {manifestTexName}");
                    else
                        Debug.LogWarning($"[WarudoBuild] manifest에 '{manifestTexName}'이 있지만 텍스처 파일 없음");
                }

                // 2) manifest에 없으면 이름 기반 fallback
                if (texPath == null)
                    texPath = FindBestTexture(mat.name, texByName);

                if (texPath == null)
                {
                    Debug.LogWarning($"[WarudoBuild] 텍스처 없음: {mat.name}");
                    continue;
                }

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (tex == null) continue;

                if (mat.HasProperty("_MainTex"))   mat.SetTexture("_MainTex", tex);
                if (mat.HasProperty("_BaseMap"))   mat.SetTexture("_BaseMap",  tex);
                if (mat.HasProperty("_Color"))     mat.SetColor("_Color",    Color.white);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);

                EditorUtility.SetDirty(mat);
                Debug.Log($"[WarudoBuild] 텍스처 할당: {mat.name} ← {Path.GetFileName(texPath)}");
                assigned++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[WarudoBuild] 텍스처 할당 완료: {assigned}개 머티리얼");
        }

        private static string FindBestTexture(
            string matName, Dictionary<string, string> texMap)
        {
            if (texMap.TryGetValue(matName, out var v)) return v;

            // 접두사 제거 후 재시도
            var stripped = StripPrefixes(matName);
            if (stripped != null && texMap.TryGetValue(stripped, out var s)) return s;

            // 부분 일치
            var key = stripped ?? matName;
            foreach (var kv in texMap)
                if (kv.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;

            return null;
        }

        private static string StripPrefixes(string name)
        {
            foreach (var p in new[] { "FBX_", "MTL_", "M_", "Mat_", "mat_" })
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(p.Length);
            return null;
        }

        // ──────────────────────────────────────────────────────
        // 결합 Prefab 생성
        // 핵심: Object.Instantiate 사용 (PrefabUtility.InstantiatePrefab X)
        //   → 중첩 프리팹 구조 방지, Humanoid 리그 충돌 방지
        //   의상 SMR만 아바타 루트에 직접 이식 (뼈대는 아바타 스켈레톤 사용)
        // ──────────────────────────────────────────────────────

        private static string BuildCombinedPrefab(
            string importRoot, string avatarFbxPath,
            string clothingAssetDir, string hairAssetDir,
            string avatarName, HashSet<string> excludedMeshes)
        {
            // ── 아바타 인스턴스 (Instantiate → 독립 GO) ──
            var avatarPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(avatarFbxPath);
            var root         = UnityEngine.Object.Instantiate(avatarPrefab);
            root.name        = avatarName;

            // ── 아바타 본 맵 ──
            var boneMap = root
                .GetComponentsInChildren<Transform>(true)
                .ToDictionary(t => t.name, t => t, StringComparer.OrdinalIgnoreCase);

            // ── 의상 / 헤어 SMR 이식 (제외 목록 반영) ──
            TransplantSmrs(clothingAssetDir, root, boneMap, "Clothing", excludedMeshes);
            TransplantSmrs(hairAssetDir,     root, boneMap, "Hair",     excludedMeshes);

            // ── 아바타 SMR 중 제외 목록 비활성화 ──
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (excludedMeshes.Contains(smr.name))
                {
                    smr.gameObject.SetActive(false);
                    Debug.Log($"[WarudoBuild] 제외(아바타 SMR): {smr.name}");
                }
            }

            // ── Prefab 저장 ──
            var prefabDir  = $"{importRoot}/Warudo";
            Directory.CreateDirectory(Path.Combine(
                Application.dataPath, prefabDir.Replace("Assets/", "")));
            AssetDatabase.Refresh();

            var prefabPath = $"{prefabDir}/Character.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.Refresh();

            Debug.Log($"[WarudoBuild] Prefab 저장: {prefabPath}");
            return prefabPath;
        }

        /// <summary>
        /// srcAssetDir의 FBX에서 SkinnedMeshRenderer만 꺼내
        /// 아바타 스켈레톤에 본 바인딩 후 avatarRoot 직하에 추가.
        /// PrefabUtility.InstantiatePrefab 대신 Object.Instantiate 사용 →
        /// 중첩 프리팹 없는 깨끗한 GO 생성.
        /// </summary>
        private static void TransplantSmrs(
            string srcAssetDir,
            GameObject avatarRoot,
            Dictionary<string, Transform> boneMap,
            string groupName,
            HashSet<string> excludedMeshes)
        {
            var fbxPaths = FindAllFbx(srcAssetDir);
            if (fbxPaths.Count == 0) return;

            int count = 0;
            foreach (var fbxPath in fbxPaths)
            {
                var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (srcPrefab == null) continue;

                // 임시 인스턴스에서 SMR 데이터 추출 후 즉시 폐기
                var temp = UnityEngine.Object.Instantiate(srcPrefab);
                temp.name = "__temp__";

                foreach (var smr in temp.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.sharedMesh == null) continue;

                    // 앱에서 숨기거나 삭제한 메시 제외
                    if (excludedMeshes.Contains(smr.name))
                    {
                        Debug.Log($"[WarudoBuild] 제외({groupName}): {smr.name}");
                        continue;
                    }

                    // 아바타 스켈레톤으로 본 재매핑
                    var reboundBones = RemapBones(smr.bones, boneMap);
                    var reboundRoot  = (smr.rootBone != null &&
                                       boneMap.TryGetValue(smr.rootBone.name, out var rb))
                                       ? rb : null;

                    // 새 GO에 SMR 이식
                    var newGo  = new GameObject($"{groupName}_{smr.name}");
                    newGo.transform.SetParent(avatarRoot.transform, false);
                    var newSmr = newGo.AddComponent<SkinnedMeshRenderer>();
                    newSmr.sharedMesh      = smr.sharedMesh;
                    newSmr.sharedMaterials = smr.sharedMaterials;
                    newSmr.bones           = reboundBones;
                    newSmr.rootBone        = reboundRoot;
                    newSmr.localBounds     = smr.localBounds;

                    int mapped = reboundBones.Count(b => b != null);
                    Debug.Log($"[WarudoBuild] SMR 이식: {smr.name} " +
                              $"({mapped}/{reboundBones.Length}개 본 매핑)");
                    count++;
                }

                UnityEngine.Object.DestroyImmediate(temp);
            }

            Debug.Log($"[WarudoBuild] {groupName} 완료: {count}개 SMR 이식");
        }

        private static Transform[] RemapBones(
            Transform[] srcBones, Dictionary<string, Transform> boneMap)
        {
            var result = new Transform[srcBones.Length];
            for (int i = 0; i < srcBones.Length; i++)
            {
                if (srcBones[i] != null &&
                    boneMap.TryGetValue(srcBones[i].name, out var mapped))
                    result[i] = mapped;
                // 매핑 실패 시 null → skinning이 일부 깨질 수 있지만 크래시는 방지
            }
            return result;
        }

        // ──────────────────────────────────────────────────────
        // .warudo 빌드
        // ──────────────────────────────────────────────────────

        private static void BuildWarudoMod(
            string prefabPath, string outputPath, string avatarName)
        {
#if WARUDO_SDK
            var modAssetsPath = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var outputDir     = Path.GetDirectoryName(outputPath);
            Directory.CreateDirectory(outputDir);

            const string settingsPath = "Assets/Packages/UMod/ExportSettings.asset";
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Packages/UMod"));

            var settings = AssetDatabase.LoadAssetAtPath<ExportSettings>(settingsPath)
                           ?? CreateAndSaveSettings(settingsPath);

            var profile = settings.ActiveExportProfile ?? settings.CreateNewExportProfile();
            profile.ModName        = avatarName;
            profile.ModAuthor      = "VirtualDresser";
            profile.ModVersion     = "1.0.0";
            profile.ModDescription = $"Exported by VirtualDresser: {avatarName}";
            profile.ModAssetsPath  = modAssetsPath;
            profile.ModExportPath  = outputDir;

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            Debug.Log($"[WarudoBuild] UMod 빌드 시작 → {outputDir}/{avatarName}.warudo");
            ModToolsUtil.StartBuild(settings);
#else
            BuildAssetBundle(prefabPath, outputPath, avatarName);
#endif
        }

#if WARUDO_SDK
        private static ExportSettings CreateAndSaveSettings(string path)
        {
            var s = ScriptableObject.CreateInstance<ExportSettings>();
            AssetDatabase.CreateAsset(s, path);
            AssetDatabase.SaveAssets();
            return s;
        }
#endif

        private static void BuildAssetBundle(
            string prefabPath, string outputPath, string avatarName)
        {
            Debug.LogWarning("[WarudoBuild] Warudo SDK 없음 — AssetBundle 폴백");
            var tmpDir = Path.Combine(Path.GetTempPath(), "vd-bundle-tmp");
            Directory.CreateDirectory(tmpDir);

            AssetImporter.GetAtPath(prefabPath).assetBundleName = avatarName.ToLower();
            BuildPipeline.BuildAssetBundles(tmpDir,
                new[] { new AssetBundleBuild {
                    assetBundleName = avatarName.ToLower() + ".warudo",
                    assetNames      = new[] { prefabPath }
                }},
                BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

            var built = Path.Combine(tmpDir, avatarName.ToLower() + ".warudo");
            if (!File.Exists(built)) throw new Exception($"빌드 결과물 없음: {built}");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.Copy(built, outputPath, overwrite: true);
        }

        // ──────────────────────────────────────────────────────
        // 유틸
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// inputPath/manifest.json 에서 제외 메시 목록 + 머티리얼-텍스처 매핑을 읽음.
        /// </summary>
        private static void ReadManifest(
            string inputPath,
            out HashSet<string> excludedMeshes,
            out Dictionary<string, string> matTexMap)
        {
            excludedMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            matTexMap      = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var manifestPath = Path.Combine(inputPath, "manifest.json");
            if (!File.Exists(manifestPath)) return;

            var json = File.ReadAllText(manifestPath);

            // ── excludedMeshes 파싱 ──
            var exStart = json.IndexOf("\"excludedMeshes\"", StringComparison.Ordinal);
            if (exStart >= 0)
            {
                var arrOpen  = json.IndexOf('[', exStart);
                var arrClose = json.IndexOf(']', arrOpen);
                if (arrOpen >= 0 && arrClose > arrOpen)
                {
                    var inner = json.Substring(arrOpen + 1, arrClose - arrOpen - 1);
                    foreach (var token in inner.Split(','))
                    {
                        var name = token.Trim().Trim('"');
                        if (!string.IsNullOrEmpty(name)) excludedMeshes.Add(name);
                    }
                }
            }

            // ── materialTextures 파싱 ("matName": "texName" ペア) ──
            var mtStart = json.IndexOf("\"materialTextures\"", StringComparison.Ordinal);
            if (mtStart >= 0)
            {
                var objOpen  = json.IndexOf('{', mtStart);
                var objClose = json.IndexOf('}', objOpen);
                if (objOpen >= 0 && objClose > objOpen)
                {
                    var inner = json.Substring(objOpen + 1, objClose - objOpen - 1);
                    // 각 줄: "key": "value"
                    foreach (var line in inner.Split('\n'))
                    {
                        var l = line.Trim().TrimEnd(',');
                        var colon = l.IndexOf(':');
                        if (colon < 0) continue;
                        var k = l.Substring(0, colon).Trim().Trim('"');
                        var v = l.Substring(colon + 1).Trim().Trim('"');
                        if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                            matTexMap[k] = v;
                    }
                }
            }
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
