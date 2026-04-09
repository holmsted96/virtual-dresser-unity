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
            // manifest.json 읽기
            ReadManifest(inputPath, out var excludedMeshes, out var matTexMap, out var smrBindings);
            Debug.Log($"[WarudoBuild] 제외: {excludedMeshes.Count}개, 머티리얼: {matTexMap.Count}개, SMR바인딩: {smrBindings.Count}개");

            var importRoot  = $"Assets/VDImport_{avatarName}";
            var avatarDir   = $"{importRoot}/Avatar";
            var clothingDir = $"{importRoot}/Clothing";
            var hairDir     = $"{importRoot}/Hair";

            if (AssetDatabase.IsValidFolder(importRoot))
                AssetDatabase.DeleteAsset(importRoot);

            // ── [1] 파일 복사: StartAssetEditing으로 묶어서 한 번만 임포트 ──
            AssetDatabase.StartAssetEditing();
            try
            {
                CopyFiles(inputPath,                           avatarDir,   topOnly: true);
                CopyFiles(Path.Combine(inputPath, "clothing"), clothingDir, topOnly: false);
                CopyFiles(Path.Combine(inputPath, "hair"),     hairDir,     topOnly: false);
            }
            finally { AssetDatabase.StopAssetEditing(); }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // ── [2] 아바타 FBX 찾기 ──
            var avatarFbxPath = FindLargestFbx(avatarDir);
            if (string.IsNullOrEmpty(avatarFbxPath))
                throw new Exception("아바타 FBX 없음");

            // ── [3] 텍스처 임포트 설정 최속화 (압축 없음, 밉맵 없음) ──
            //    .warudo 패키징용이므로 최고속 설정 사용
            OptimizeTextureImportSettings(importRoot);

            // ── [4] FBX 설정을 한 번에 묶어서 적용 (ExtractMaterials + HumanoidRig) ──
            //    SaveAndReimport()를 FBX당 1회만 호출
            AssetDatabase.StartAssetEditing();
            try
            {
                ConfigureFbxImporters(avatarFbxPath, clothingDir, hairDir);
            }
            finally { AssetDatabase.StopAssetEditing(); }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // ── [5] 텍스처 → 머티리얼 할당 ──
            AssignTexturesToMaterials(importRoot, matTexMap);

            // ── [6] 결합 Prefab 생성 ──
            var prefabPath = BuildCombinedPrefab(
                importRoot, avatarFbxPath, clothingDir, hairDir, avatarName, excludedMeshes, smrBindings);

            // ── [7] .warudo 빌드 ──
            BuildWarudoMod(prefabPath, outputPath, avatarName);

            // ── [8] 정리 ──
            AssetDatabase.DeleteAsset(importRoot);
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// 텍스처 임포트 설정을 빌드용 최속으로 변경.
        /// 압축 없음 + 밉맵 없음 → 임포트 시간 대폭 단축.
        /// </summary>
        private static void OptimizeTextureImportSettings(string importRoot)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { importRoot }))
                {
                    var path     = AssetDatabase.GUIDToAssetPath(guid);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null) continue;

                    // 변경이 필요할 때만 저장 (불필요한 reimport 방지)
                    bool changed = false;
                    if (importer.mipmapEnabled)           { importer.mipmapEnabled = false;                          changed = true; }
                    if (importer.textureCompression != TextureImporterCompression.Uncompressed)
                                                          { importer.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
                    if (importer.maxTextureSize > 1024)   { importer.maxTextureSize = 1024;                          changed = true; }
                    if (changed) importer.SaveAndReimport();
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            Debug.Log("[WarudoBuild] 텍스처 임포트 최속화 완료");
        }

        /// <summary>
        /// 모든 FBX에 대해 머티리얼 External 추출 + 아바타는 Humanoid 리그를
        /// 한 번의 SaveAndReimport()로 처리 (기존: FBX당 2회 → 1회로 통합).
        /// </summary>
        private static void ConfigureFbxImporters(
            string avatarFbxPath, string clothingDir, string hairDir)
        {
            // 아바타: External 머티리얼 + Humanoid 한 번에
            var avatarImporter = AssetImporter.GetAtPath(avatarFbxPath) as ModelImporter;
            if (avatarImporter != null)
            {
                avatarImporter.materialLocation = ModelImporterMaterialLocation.External;
                avatarImporter.animationType    = ModelImporterAnimationType.Human;
                avatarImporter.avatarSetup      = ModelImporterAvatarSetup.CreateFromThisModel;
                avatarImporter.SaveAndReimport();
                Debug.Log($"[WarudoBuild] 아바타 FBX 설정 완료: {avatarFbxPath}");
            }

            // 의상/헤어: External 머티리얼만 (리그 불필요)
            foreach (var dir in new[] { clothingDir, hairDir })
            {
                foreach (var fbxPath in FindAllFbx(dir))
                {
                    var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                    if (imp == null) continue;
                    if (imp.materialLocation != ModelImporterMaterialLocation.External)
                    {
                        imp.materialLocation = ModelImporterMaterialLocation.External;
                        imp.SaveAndReimport();
                    }
                }
            }
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
            string avatarName,
            HashSet<string> excludedMeshes,
            List<SmrBinding> smrBindings)
        {
            // ── 아바타 인스턴스 ──
            var avatarPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(avatarFbxPath);
            var root         = UnityEngine.Object.Instantiate(avatarPrefab);
            root.name        = avatarName;

            // ── 아바타 본 맵 (이름 → Transform) ──
            var boneMap = root
                .GetComponentsInChildren<Transform>(true)
                .ToDictionary(t => t.name, t => t, StringComparer.OrdinalIgnoreCase);

            // ── smrBindings 인덱스: smrName → SmrBinding ──
            var bindingMap = smrBindings.ToDictionary(b => b.SmrName, StringComparer.OrdinalIgnoreCase);

            // ── 의상 / 헤어 SMR 이식 ──
            TransplantSmrs(clothingAssetDir, root, boneMap, bindingMap, "Clothing", excludedMeshes);
            TransplantSmrs(hairAssetDir,     root, boneMap, bindingMap, "Hair",     excludedMeshes);

            // ── 아바타 SMR 중 제외 목록 비활성화 ──
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (excludedMeshes.Contains(smr.name))
                {
                    smr.gameObject.SetActive(false);
                    Debug.Log($"[WarudoBuild] 제외(아바타): {smr.name}");
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
        /// manifest smrBindings의 본 이름 배열을 우선 사용해 아바타 스켈레톤에 바인딩.
        /// manifest에 없으면 FBX의 smr.bones 이름으로 fallback.
        /// </summary>
        private static void TransplantSmrs(
            string srcAssetDir,
            GameObject avatarRoot,
            Dictionary<string, Transform> boneMap,
            Dictionary<string, SmrBinding> bindingMap,
            string groupName,
            HashSet<string> excludedMeshes)
        {
            var fbxPaths = FindAllFbx(srcAssetDir);
            if (fbxPaths.Count == 0) return;

            // hips fallback: rootBone 못찾을 때 대신 사용
            Transform hipsFallback = null;
            foreach (var key in new[] { "Hips", "J_Bip_C_Hips", "hips", "pelvis", "Pelvis" })
                if (boneMap.TryGetValue(key, out hipsFallback)) break;

            int count = 0;
            foreach (var fbxPath in fbxPaths)
            {
                var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (srcPrefab == null) continue;

                var temp = UnityEngine.Object.Instantiate(srcPrefab);
                temp.name = "__temp__";

                foreach (var smr in temp.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.sharedMesh == null) continue;
                    if (excludedMeshes.Contains(smr.name))
                    {
                        Debug.Log($"[WarudoBuild] 제외({groupName}): {smr.name}");
                        continue;
                    }

                    // ── 본 바인딩: manifest 우선, fallback은 FBX smr.bones 이름 ──
                    Transform[] reboundBones;
                    Transform   reboundRoot;

                    if (bindingMap.TryGetValue(smr.name, out var binding) &&
                        binding.BoneNames.Length == smr.bones.Length)
                    {
                        // manifest에 기록된 본 이름 순서 그대로 사용
                        reboundBones = new Transform[binding.BoneNames.Length];
                        for (int i = 0; i < binding.BoneNames.Length; i++)
                        {
                            var bn = binding.BoneNames[i];
                            if (bn != "null" && !string.IsNullOrEmpty(bn))
                                boneMap.TryGetValue(bn, out reboundBones[i]);
                        }
                        boneMap.TryGetValue(binding.RootBone ?? "", out reboundRoot);
                        reboundRoot = reboundRoot ?? hipsFallback;

                        int mapped = reboundBones.Count(b => b != null);
                        Debug.Log($"[WarudoBuild] manifest 바인딩: {smr.name} ({mapped}/{reboundBones.Length}본)");
                    }
                    else
                    {
                        // fallback: FBX에서 읽은 본 이름으로 매핑
                        reboundBones = new Transform[smr.bones.Length];
                        for (int i = 0; i < smr.bones.Length; i++)
                        {
                            if (smr.bones[i] != null)
                                boneMap.TryGetValue(smr.bones[i].name, out reboundBones[i]);
                        }
                        reboundRoot = null;
                        if (smr.rootBone != null)
                            boneMap.TryGetValue(smr.rootBone.name, out reboundRoot);
                        reboundRoot = reboundRoot ?? hipsFallback;

                        int mapped = reboundBones.Count(b => b != null);
                        Debug.Log($"[WarudoBuild] FBX 바인딩(fallback): {smr.name} ({mapped}/{reboundBones.Length}본)");
                    }

                    // ── 새 GO에 SMR 이식 ──
                    var newGo  = new GameObject($"{groupName}_{smr.name}");
                    newGo.transform.SetParent(avatarRoot.transform, false);
                    var newSmr = newGo.AddComponent<SkinnedMeshRenderer>();
                    newSmr.sharedMesh      = smr.sharedMesh;
                    newSmr.sharedMaterials = smr.sharedMaterials;
                    newSmr.bones           = reboundBones;
                    newSmr.rootBone        = reboundRoot;
                    newSmr.localBounds     = smr.localBounds;
                    count++;
                }

                UnityEngine.Object.DestroyImmediate(temp);
            }

            Debug.Log($"[WarudoBuild] {groupName} 완료: {count}개 SMR");
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
            out Dictionary<string, string> matTexMap,
            out List<SmrBinding> smrBindings)
        {
            var ex  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mtm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sb  = new List<SmrBinding>();

            excludedMeshes = ex;
            matTexMap      = mtm;
            smrBindings    = sb;

            var manifestPath = Path.Combine(inputPath, "manifest.json");
            if (!File.Exists(manifestPath)) return;

            var json = File.ReadAllText(manifestPath);

            // ── excludedMeshes ──
            ParseJsonArray(json, "excludedMeshes", tokens =>
            {
                foreach (var t in tokens)
                    if (!string.IsNullOrEmpty(t)) ex.Add(t);
            });

            // ── materialTextures ──
            ParseJsonObject(json, "materialTextures", (k, v) =>
            {
                if (!string.IsNullOrEmpty(k)) mtm[k] = v;
            });

            // ── smrBindings: JSON 배열 내 각 객체를 파싱 ──
            var sbStart = json.IndexOf("\"smrBindings\"", StringComparison.Ordinal);
            if (sbStart >= 0)
            {
                var arrOpen = json.IndexOf('[', sbStart);
                // 중첩 괄호를 추적하며 배열 끝 찾기
                int depth = 0; int arrClose = -1;
                for (int i = arrOpen; i < json.Length; i++)
                {
                    if (json[i] == '[') depth++;
                    else if (json[i] == ']') { depth--; if (depth == 0) { arrClose = i; break; } }
                }
                if (arrOpen >= 0 && arrClose > arrOpen)
                {
                    // 각 { ... } 객체를 분리
                    var inner = json.Substring(arrOpen + 1, arrClose - arrOpen - 1);
                    var objects = SplitJsonObjects(inner);
                    foreach (var obj in objects)
                    {
                        var binding = new SmrBinding();
                        ParseJsonObject(obj, "smrName",   (_, v) => binding.SmrName  = v);
                        ParseJsonObject(obj, "layer",     (_, v) => binding.Layer    = v);
                        ParseJsonObject(obj, "rootBone",  (_, v) => binding.RootBone = v);
                        var mats  = new List<string>();
                        var bones = new List<string>();
                        ParseJsonArray(obj, "materials",  ts => mats.AddRange(ts));
                        ParseJsonArray(obj, "boneNames",  ts => bones.AddRange(ts));
                        binding.Materials = mats.ToArray();
                        binding.BoneNames = bones.ToArray();
                        if (!string.IsNullOrEmpty(binding.SmrName))
                            sb.Add(binding);
                    }
                }
            }
        }

        // ── 간단한 JSON 파싱 헬퍼 ──

        private static void ParseJsonArray(string json, string key, Action<IEnumerable<string>> handler)
        {
            var keyIdx = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            if (keyIdx < 0) return;
            var arrOpen  = json.IndexOf('[', keyIdx);
            var arrClose = json.IndexOf(']', arrOpen);
            if (arrOpen < 0 || arrClose < 0) return;
            var inner  = json.Substring(arrOpen + 1, arrClose - arrOpen - 1);
            var tokens = inner.Split(',').Select(t => t.Trim().Trim('"'));
            handler(tokens);
        }

        private static void ParseJsonObject(string json, string key, Action<string, string> kvHandler)
        {
            // "key": "value" ペア (simple string values only)
            var keyStr = $"\"{key}\"";
            int pos = 0;
            while (true)
            {
                var ki = json.IndexOf(keyStr, pos, StringComparison.Ordinal);
                if (ki < 0) break;
                var colon = json.IndexOf(':', ki + keyStr.Length);
                if (colon < 0) break;
                // 값이 문자열인지 객체인지 확인
                var valStart = colon + 1;
                while (valStart < json.Length && json[valStart] == ' ') valStart++;
                if (valStart >= json.Length) break;
                if (json[valStart] == '"')
                {
                    var valEnd = json.IndexOf('"', valStart + 1);
                    if (valEnd < 0) break;
                    var k = key;
                    var v = json.Substring(valStart + 1, valEnd - valStart - 1);
                    kvHandler(k, v);
                    pos = valEnd + 1;
                }
                else
                {
                    // 객체/배열 → 각 줄 파싱
                    var objOpen  = json.IndexOf('{', colon);
                    var objClose = json.IndexOf('}', objOpen > 0 ? objOpen : colon);
                    if (objOpen < 0 || objClose < 0) break;
                    var inner = json.Substring(objOpen + 1, objClose - objOpen - 1);
                    foreach (var line in inner.Split('\n'))
                    {
                        var l = line.Trim().TrimEnd(',');
                        var c = l.IndexOf(':');
                        if (c < 0) continue;
                        var k2 = l.Substring(0, c).Trim().Trim('"');
                        var v2 = l.Substring(c + 1).Trim().Trim('"');
                        if (!string.IsNullOrEmpty(k2)) kvHandler(k2, v2);
                    }
                    pos = objClose + 1;
                    break; // object 파싱은 한번만
                }
            }
        }

        private static List<string> SplitJsonObjects(string json)
        {
            var result = new List<string>();
            int depth = 0, start = -1;
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{') { if (depth++ == 0) start = i; }
                else if (json[i] == '}') { if (--depth == 0 && start >= 0) result.Add(json.Substring(start, i - start + 1)); }
            }
            return result;
        }

        // ──────────────────────────────────────────────────────
        // 데이터 클래스
        // ──────────────────────────────────────────────────────

        private class SmrBinding
        {
            public string   SmrName;
            public string   Layer;
            public string   RootBone;
            public string[] Materials;
            public string[] BoneNames;
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
