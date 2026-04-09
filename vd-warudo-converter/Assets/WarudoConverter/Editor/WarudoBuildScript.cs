// WarudoBuildScript.cs
// Virtual Dresser → .warudo 변환 스크립트

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

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
            ReadManifest(inputPath, out var excludedMeshes, out var matTexMap, out var matProps, out var smrBindings);
            Debug.Log($"[WarudoBuild] 제외: {excludedMeshes.Count}개, 머티리얼: {matTexMap.Count}개, matProps: {matProps.Count}개, SMR바인딩: {smrBindings.Count}개");

            var importRoot  = $"Assets/VDImport_{avatarName}";
            var avatarDir   = $"{importRoot}/Avatar";
            var clothingDir = $"{importRoot}/Clothing";
            var hairDir     = $"{importRoot}/Hair";

            // ── 입력 해시 확인: 동일 입력이면 임포트 단계 전부 스킵 ──
            // VDImport 폴더는 삭제하지 않고 캐시로 유지.
            // CopyFiles는 기존 파일을 덮어쓰지 않으므로 변경된 파일만 복사됨.
            // ConfigureFbxImporters / OptimizeTextureImportSettings 는 이미 설정됐으면 no-op.
            var hashFile     = Path.Combine(Application.dataPath, $"VDImport_{avatarName}.hash");
            var inputHash    = ComputeInputHash(inputPath);
            var cachedHash   = File.Exists(hashFile) ? File.ReadAllText(hashFile).Trim() : "";
            var cacheHit     = (cachedHash == inputHash) && AssetDatabase.IsValidFolder(importRoot);

            if (cacheHit)
            {
                Debug.Log($"[WarudoBuild] 입력 해시 일치 — 임포트 캐시 재사용 (스킵: 복사/임포트/설정)");
            }
            else
            {
                // 입력이 바뀐 경우만 폴더 초기화
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

                // ── [3] 텍스처 임포트 설정 최속화 (압축 없음, 밉맵 없음) ──
                OptimizeTextureImportSettings(importRoot);

                // ── [4] FBX 설정을 한 번에 묶어서 적용 (ExtractMaterials + HumanoidRig) ──
                AssetDatabase.StartAssetEditing();
                try
                {
                    var avatarFbxPathTemp = FindLargestFbx(avatarDir);
                    if (!string.IsNullOrEmpty(avatarFbxPathTemp))
                        ConfigureFbxImporters(avatarFbxPathTemp, clothingDir, hairDir);
                }
                finally { AssetDatabase.StopAssetEditing(); }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                // 해시 저장 (성공 시)
                File.WriteAllText(hashFile, inputHash);
            }

            // ── [2] 아바타 FBX 찾기 ──
            var avatarFbxPath = FindLargestFbx(avatarDir);
            if (string.IsNullOrEmpty(avatarFbxPath))
                throw new Exception("아바타 FBX 없음");

            // ── [5] 텍스처 → 머티리얼 할당 ──
            var texByName = BuildTexByName(importRoot);
            AssignTexturesToMaterials(importRoot, matTexMap, matProps, smrBindings, texByName);
            // ★ AssignTexturesToMaterials가 SetDirty+SaveAssets 했으므로
            //   AssetDatabase를 강제 동기화해 FBX 프리팹이 최신 .mat를 참조하도록 함
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // ── [6] 결합 Prefab 생성 ──
            var prefabPath = BuildCombinedPrefab(
                importRoot, avatarFbxPath, clothingDir, hairDir, avatarName,
                excludedMeshes, smrBindings, texByName);

            // ── [7] .warudo 빌드 ──
            BuildWarudoMod(prefabPath, outputPath, avatarName);

            // ── [8] Prefab만 정리 (VDImport 폴더는 캐시로 유지) ──
            if (AssetDatabase.IsValidFolder(prefabPath.Replace(Path.GetFileName(prefabPath), "").TrimEnd('/')))
            {
                // prefab만 삭제 (임포트 캐시 폴더는 유지)
            }
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// inputPath 안의 모든 FBX/텍스처 파일 크기 합산으로 빠른 해시 생성.
        /// 파일 내용 해시(SHA256)는 너무 느리므로 크기+수정시각 조합 사용.
        /// </summary>
        private static string ComputeInputHash(string inputPath)
        {
            long total = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var f in Directory.GetFiles(inputPath, "*", SearchOption.AllDirectories)
                                        .OrderBy(x => x))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".fbx" && ext != ".png" && ext != ".jpg"
                    && ext != ".jpeg" && ext != ".tga" && ext != ".psd"
                    && ext != ".json") continue;
                var info = new FileInfo(f);
                total += info.Length;
                sb.Append(info.Name).Append(':').Append(info.Length).Append(':')
                  .Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
            // 단순 합산 해시 (암호화 목적 아님)
            return $"{total}_{sb.Length}_{sb.ToString().GetHashCode()}";
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

        private static Dictionary<string, string> BuildTexByName(string importRoot)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { importRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var key  = Path.GetFileNameWithoutExtension(path);
                if (!map.ContainsKey(key)) map[key] = path;
            }
            Debug.Log($"[WarudoBuild] 텍스처 {map.Count}개 발견");
            return map;
        }

        private static void AssignTexturesToMaterials(
            string importRoot,
            Dictionary<string, string> matTexMap,
            Dictionary<string, MatProps> matProps,
            List<SmrBinding> smrBindings,
            Dictionary<string, string> texByName)
        {
            Debug.Log($"[WarudoBuild] AssignTextures: matTexMap={matTexMap.Count}개, texByName={texByName.Count}개, smrBindings={smrBindings.Count}개");
            if (matTexMap.Count > 0)
                Debug.Log($"[WarudoBuild] matTexMap 샘플: {string.Join(", ", matTexMap.Take(5).Select(kv => $"'{kv.Key}'→'{kv.Value}'"))}");
            if (texByName.Count > 0)
                Debug.Log($"[WarudoBuild] texByName 샘플: {string.Join(", ", texByName.Keys.Take(10))}");

            if (texByName.Count == 0) return;

            // smrBindings에서 matName → textureName 역매핑 미리 구축
            // (matTexMap에 없는 경우 smrBindings.materials[i] 기반 fallback에 사용)
            var bindingMatTexMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in smrBindings)
            {
                if (b.Materials == null || b.Textures == null) continue;
                for (int si = 0; si < b.Materials.Length; si++)
                {
                    var mName = b.Materials[si];
                    var tName = si < b.Textures.Length ? b.Textures[si] : "null";
                    if (string.IsNullOrEmpty(mName) || mName == "null") continue;
                    if (string.IsNullOrEmpty(tName) || tName == "null") continue;
                    if (!bindingMatTexMap.ContainsKey(mName))
                        bindingMatTexMap[mName] = tName;
                }
            }
            Debug.Log($"[WarudoBuild] bindingMatTexMap: {bindingMatTexMap.Count}개");

            // 외부 .mat 파일에 텍스처 할당
            int assigned = 0;
            int skipped  = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { importRoot }))
            {
                // Warudo/Mats 에 있는 .mat은 ApplySlotTextures에서 처리하므로 건너뜀
                var matPath = AssetDatabase.GUIDToAssetPath(guid);
                if (matPath.Contains("/Warudo/")) continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) continue;

                string texPath = null;

                // 1) manifest matTexMap 우선 (DresserUI에서 실제 적용된 텍스처명)
                if (matTexMap.TryGetValue(mat.name, out var manifestTexName))
                    texPath = ResolveTexName(manifestTexName, texByName);

                // 2) smrBindings.materials[] 기반 fallback
                if (texPath == null && bindingMatTexMap.TryGetValue(mat.name, out var bindingTexName))
                    texPath = ResolveTexName(bindingTexName, texByName);

                // 3) 이름 기반 마지막 fallback
                if (texPath == null)
                    texPath = FindBestTexture(mat.name, texByName);

                if (texPath == null)
                {
                    bool inManifest   = matTexMap.ContainsKey(mat.name);
                    bool inBindingMap = bindingMatTexMap.ContainsKey(mat.name);
                    Debug.LogWarning($"[WarudoBuild] 텍스처 없음: mat='{mat.name}' " +
                        $"manifest={inManifest} binding={inBindingMap}");
                    skipped++;
                    continue;
                }

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (tex == null) { skipped++; continue; }

                if (mat.HasProperty("_MainTex"))   mat.SetTexture("_MainTex", tex);
                if (mat.HasProperty("_BaseMap"))   mat.SetTexture("_BaseMap",  tex);

                // matProps에서 셰이더 + 속성 적용 (DresserUI에서 실제 적용된 값)
                if (matProps.TryGetValue(mat.name, out var props))
                    ApplyMatProps(mat, props);
                else
                {
                    FixBlackColor(mat, "_Color");
                    FixBlackColor(mat, "_BaseColor");
                }

                EditorUtility.SetDirty(mat);
                assigned++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[WarudoBuild] 텍스처 할당 완료: {assigned}개 성공, {skipped}개 실패");
        }

        /// <summary>
        /// 텍스처 이름(확장자 있음/없음 모두)으로 texByName에서 경로 탐색.
        /// 정확 일치 → 부분 일치 순서.
        /// </summary>
        private static string ResolveTexName(string texName, Dictionary<string, string> texByName)
        {
            if (string.IsNullOrEmpty(texName) || texName == "null") return null;
            var key = Path.GetFileNameWithoutExtension(texName);

            // 정확 일치
            if (texByName.TryGetValue(key,     out var p)) return p;
            if (texByName.TryGetValue(texName, out     p)) return p;

            // 부분 일치
            foreach (var kv in texByName)
            {
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    key.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;
            }
            return null;
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

        /// <summary>
        /// 머티리얼의 색상 프로퍼티가 거의 검정(rgba 모두 0.05 미만)인 경우에만 white로 교정.
        /// </summary>
        private static void FixBlackColor(Material mat, string prop)
        {
            if (!mat.HasProperty(prop)) return;
            var c = mat.GetColor(prop);
            if (c.r < 0.05f && c.g < 0.05f && c.b < 0.05f)
                mat.SetColor(prop, Color.white);
        }

        // ── DresserUI에서 export된 머티리얼 속성 ──

        public class MatProps
        {
            public string ShaderName   = "Standard";
            public int    RenderQueue  = -1;
            public string Keywords     = "";
            public Dictionary<string, float[]> Colors = new();  // RGBA float[4]
            public Dictionary<string, float>   Floats = new();
        }

        /// <summary>
        /// DresserUI manifest의 matProperties를 머티리얼에 적용.
        /// 셰이더를 lilToon으로 전환하고, 색상/float 속성을 원본 그대로 복원.
        /// </summary>
        private static void ApplyMatProps(Material mat, MatProps props)
        {
            bool shaderSwitched = false;

            // ── 셰이더 전환 ──
            if (!string.IsNullOrEmpty(props.ShaderName) && props.ShaderName != "Standard")
            {
                var shader = Shader.Find(props.ShaderName);
                if (shader != null)
                {
                    mat.shader = shader;
                    shaderSwitched = true;
                    Debug.Log($"[WarudoBuild] 셰이더 전환: {mat.name} → {props.ShaderName}");
                }
                else
                {
                    Debug.LogWarning($"[WarudoBuild] 셰이더 없음: '{props.ShaderName}' — " +
                                     "현재 셰이더로 색상 속성만 적용합니다.");
                    // ⚠️ return 하지 않음 — 셰이더 전환 실패해도 색상은 반드시 적용
                }
            }

            // ── Keywords (셰이더 전환 성공 시에만) ──
            if (shaderSwitched && !string.IsNullOrEmpty(props.Keywords))
                foreach (var kw in props.Keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    mat.EnableKeyword(kw);

            // ── RenderQueue ──
            if (props.RenderQueue >= 0) mat.renderQueue = props.RenderQueue;

            // ── Colors — 셰이더 전환 성공 여부와 무관하게 항상 적용 ──
            // 사용자가 컬러 피커로 변경한 색상을 .warudo 에도 반영하기 위해 필수
            bool anyColorApplied = false;
            foreach (var kv in props.Colors)
            {
                if (mat.HasProperty(kv.Key) && kv.Value?.Length >= 4)
                {
                    var c = new Color(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]);
                    mat.SetColor(kv.Key, c);
                    anyColorApplied = true;
                }
            }

            // 색상이 하나도 적용 안 됐으면 검정 방지 폴백
            if (!anyColorApplied)
            {
                FixBlackColor(mat, "_Color");
                FixBlackColor(mat, "_BaseColor");
            }

            // ── Floats ──
            foreach (var kv in props.Floats)
                if (mat.HasProperty(kv.Key))
                    mat.SetFloat(kv.Key, kv.Value);
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
            List<SmrBinding> smrBindings,
            Dictionary<string, string> texByName)
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

            // ── 의상/헤어 FBX에서 누락 본을 아바타 계층에 이식 (물리/천 시뮬레이션 본 등) ──
            TransplantMissingBones(clothingAssetDir, root, boneMap);
            TransplantMissingBones(hairAssetDir,     root, boneMap);

            // ── 의상 / 헤어 SMR 이식 ──
            // ★ matSaveDir을 TransplantSmrs 전에 AssetDatabase에 등록해야
            //   ApplySlotTextures의 AssetDatabase.CreateAsset이 성공함.
            //   Directory.CreateDirectory 후 AssetDatabase.Refresh 없으면
            //   CreateAsset이 "unknown folder" 오류로 조용히 실패함.
            var prefabDir  = $"{importRoot}/Warudo";
            var matSaveDir = $"{prefabDir}/Mats";
            var fullPrefabDir = Path.Combine(Application.dataPath, prefabDir.Replace("Assets/", ""));
            var fullMatDir    = Path.Combine(Application.dataPath, matSaveDir.Replace("Assets/", ""));
            Directory.CreateDirectory(fullPrefabDir);
            Directory.CreateDirectory(fullMatDir);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            TransplantSmrs(clothingAssetDir, root, boneMap, bindingMap, texByName, "Clothing", excludedMeshes, matSaveDir);
            TransplantSmrs(hairAssetDir,     root, boneMap, bindingMap, texByName, "Hair",     excludedMeshes, matSaveDir);

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
            // (prefabDir/matSaveDir은 위에서 이미 생성 및 refresh 완료)
            AssetDatabase.SaveAssets();  // TransplantSmrs에서 생성한 .mat 파일 확정 저장
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
        /// <summary>
        /// 의상/헤어 FBX에 있지만 아바타 계층에 없는 본을 재귀적으로 이식.
        /// 물리 본, 스커트 본, 리본 본 등 아바타에 없는 시뮬레이션 본들을 추가해
        /// 의상 SMR의 bone 참조가 null이 되지 않도록 한다.
        /// </summary>
        /// <summary>
        /// SMR의 sharedMaterials를 복제해 슬롯별 텍스처를 적용한 배열 반환.
        /// texNames[i] = "null" 이거나 텍스처를 못 찾으면 원본 머티리얼 유지.
        /// </summary>
        /// <summary>
        /// SMR 슬롯별 텍스처를 적용한 Material[] 반환.
        /// 반드시 .mat 파일로 저장해야 prefab serialization이 올바르게 됨.
        /// 메모리 인스턴스(Instantiate)는 SaveAsPrefabAsset 시 텍스처 참조가 유실됨.
        /// </summary>
        private static Material[] ApplySlotTextures(
            Material[] srcMaterials,
            string[] texNames,
            Dictionary<string, string> texByName,
            string matSaveDir,
            string smrName)
        {
            // matSaveDir 폴더 보장
            Directory.CreateDirectory(Path.Combine(
                Application.dataPath, matSaveDir.Replace("Assets/", "")));

            var result = new Material[srcMaterials.Length];
            for (int i = 0; i < srcMaterials.Length; i++)
            {
                var src     = srcMaterials[i];
                var texName = i < texNames.Length ? texNames[i] : "null";

                if (src == null || texName == "null" || string.IsNullOrEmpty(texName))
                {
                    result[i] = src;
                    continue;
                }

                // 텍스처 경로 탐색 (확장자 제거 후 exact → partial)
                var key = Path.GetFileNameWithoutExtension(texName);
                if (!texByName.TryGetValue(key, out var texPath) &&
                    !texByName.TryGetValue(texName, out texPath))
                {
                    foreach (var kv in texByName)
                    {
                        if (kv.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            key.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        { texPath = kv.Value; break; }
                    }
                }

                if (texPath == null) { result[i] = src; continue; }

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (tex == null) { result[i] = src; continue; }

                // ★ 머티리얼을 .mat Asset으로 저장
                // Instantiate() 인스턴스는 SaveAsPrefabAsset 시 텍스처 참조 유실
                var safeName = string.Concat(
                    (smrName + "_slot" + i).Select(c =>
                        Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var matPath = $"{matSaveDir}/{safeName}.mat";

                // 기존 .mat 재사용 (중복 생성 방지)
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    mat = new Material(src.shader);
                    AssetDatabase.CreateAsset(mat, matPath);
                    // ★ CreateAsset 후 실제로 Asset으로 등록됐는지 확인
                    if (AssetDatabase.LoadAssetAtPath<Material>(matPath) == null)
                    {
                        Debug.LogError($"[WarudoBuild] CreateAsset 실패: {matPath} — matSaveDir이 AssetDatabase에 미등록 상태. Refresh 필요.");
                        result[i] = src;
                        continue;
                    }
                }
                else
                {
                    mat.shader = src.shader;
                }

                // 원본 속성 복사 후 텍스처 덮어쓰기
                mat.CopyPropertiesFromMaterial(src);
                if (mat.HasProperty("_MainTex"))   mat.SetTexture("_MainTex",  tex);
                if (mat.HasProperty("_BaseMap"))   mat.SetTexture("_BaseMap",  tex);
                // matProps에서 셰이더 + 속성 적용 (DresserUI에서 실제 적용된 값)
                // ApplySlotTextures는 외부에서 matProps에 접근 못하므로 src mat 이름 기반으로 처리
                FixBlackColor(mat, "_Color");
                FixBlackColor(mat, "_BaseColor");
                EditorUtility.SetDirty(mat);

                result[i] = mat;
                Debug.Log($"[WarudoBuild] 슬롯[{i}] .mat 저장: {matPath} ← {Path.GetFileName(texPath)}");
            }

            AssetDatabase.SaveAssets();
            return result;
        }

        private static void TransplantMissingBones(
            string srcAssetDir,
            GameObject avatarRoot,
            Dictionary<string, Transform> boneMap)
        {
            var fbxPaths = FindAllFbx(srcAssetDir);
            if (fbxPaths.Count == 0) return;

            int added = 0;
            foreach (var fbxPath in fbxPaths)
            {
                var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (srcPrefab == null) continue;

                var temp = UnityEngine.Object.Instantiate(srcPrefab);
                temp.name = "__bonecheck__";

                // 의상 FBX의 모든 Transform을 BFS로 순회 → 아바타에 없는 것만 추가
                AddMissingBonesRecursive(temp.transform, null, boneMap, ref added);

                UnityEngine.Object.DestroyImmediate(temp);
            }

            if (added > 0)
                Debug.Log($"[WarudoBuild] 누락 본 {added}개 아바타 계층에 추가");
        }

        private static void AddMissingBonesRecursive(
            Transform src, Transform avatarParent,
            Dictionary<string, Transform> boneMap, ref int added)
        {
            // 이 본이 이미 아바타에 있으면 → 아바타의 해당 본을 부모로 사용
            if (boneMap.TryGetValue(src.name, out var existing))
            {
                // 자식들 처리 (아바타의 해당 본 아래에서 계속)
                for (int i = 0; i < src.childCount; i++)
                    AddMissingBonesRecursive(src.GetChild(i), existing, boneMap, ref added);
            }
            else if (avatarParent != null)
            {
                // 부모는 아바타에 있는데 이 본은 없음 → 아바타 계층에 새로 생성
                var newBone = new GameObject(src.name);
                newBone.transform.SetParent(avatarParent, false);
                newBone.transform.localPosition = src.localPosition;
                newBone.transform.localRotation = src.localRotation;
                newBone.transform.localScale    = src.localScale;

                boneMap[src.name] = newBone.transform;
                added++;

                // 자식도 새 본 아래로
                for (int i = 0; i < src.childCount; i++)
                    AddMissingBonesRecursive(src.GetChild(i), newBone.transform, boneMap, ref added);
            }
            // avatarParent == null 이면 최상위 GO (FBX root) → 스킵하고 자식만 처리
            else
            {
                for (int i = 0; i < src.childCount; i++)
                    AddMissingBonesRecursive(src.GetChild(i), null, boneMap, ref added);
            }
        }

        private static void TransplantSmrs(
            string srcAssetDir,
            GameObject avatarRoot,
            Dictionary<string, Transform> boneMap,
            Dictionary<string, SmrBinding> bindingMap,
            Dictionary<string, string> texByName,
            string groupName,
            HashSet<string> excludedMeshes,
            string matSaveDir)
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

                    // binding 탐색: 정확 일치 → 퍼지 매칭 (TriLib vs Unity FBX importer 이름 차이 대응)
                    SmrBinding binding = null;
                    bindingMap.TryGetValue(smr.name, out binding);
                    if (binding == null)
                    {
                        // 퍼지: 공백/숫자 suffix 무시하고 포함 관계 확인
                        var nameLow = smr.name.ToLowerInvariant();
                        foreach (var kv in bindingMap)
                        {
                            var kLow = kv.Key.ToLowerInvariant();
                            if (nameLow.Contains(kLow) || kLow.Contains(nameLow))
                            {
                                binding = kv.Value;
                                Debug.Log($"[WarudoBuild] SMR 퍼지 매칭: '{smr.name}' → binding='{kv.Key}'");
                                break;
                            }
                        }
                    }

                    if (binding != null &&
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
                    int mappedCount = reboundBones.Count(b => b != null);

                    // 0본 매핑 = 소품(prop): 스켈레톤 바인딩 대신 rootBone에 고정 부착
                    if (mappedCount == 0)
                    {
                        var anchor = reboundRoot ?? avatarRoot.transform;
                        var propGo = new GameObject($"{groupName}_{smr.name}");
                        propGo.transform.SetParent(anchor, false);
                        propGo.transform.localPosition = smr.transform.localPosition;
                        propGo.transform.localRotation = smr.transform.localRotation;
                        propGo.transform.localScale    = smr.transform.localScale;
                        var mr = propGo.AddComponent<MeshRenderer>();
                        var mf = propGo.AddComponent<MeshFilter>();
                        mf.sharedMesh      = smr.sharedMesh;
                        mr.sharedMaterials = smr.sharedMaterials;
                        Debug.Log($"[WarudoBuild] 소품 부착(MeshRenderer): {smr.name} → {anchor.name}");
                        count++;
                        continue;
                    }

                    var newGo  = new GameObject($"{groupName}_{smr.name}");
                    newGo.transform.SetParent(avatarRoot.transform, false);
                    var newSmr = newGo.AddComponent<SkinnedMeshRenderer>();
                    newSmr.sharedMesh      = smr.sharedMesh;
                    newSmr.bones           = reboundBones;
                    newSmr.rootBone        = reboundRoot;
                    newSmr.localBounds     = smr.localBounds;

                    // ── 슬롯별 텍스처 직접 적용 ──
                    if (binding != null &&
                        binding.Textures != null &&
                        binding.Textures.Length == smr.sharedMaterials.Length)
                    {
                        // manifest의 textures[] 인덱스 기반으로 정확 적용
                        newSmr.sharedMaterials = ApplySlotTextures(
                            smr.sharedMaterials, binding.Textures, texByName,
                            matSaveDir, smr.name);
                    }
                    else if (binding != null && binding.Materials != null &&
                             binding.Textures != null)
                    {
                        // 슬롯 수 불일치 시 materials[] 이름 기반으로 재매핑
                        var slotTexNames = smr.sharedMaterials.Select(m =>
                        {
                            if (m == null) return "null";
                            for (int si = 0; si < binding.Materials.Length; si++)
                            {
                                if (string.Equals(binding.Materials[si], m.name,
                                    StringComparison.OrdinalIgnoreCase) &&
                                    si < binding.Textures.Length)
                                    return binding.Textures[si];
                            }
                            return "null";
                        }).ToArray();
                        newSmr.sharedMaterials = ApplySlotTextures(
                            smr.sharedMaterials, slotTexNames, texByName,
                            matSaveDir, smr.name);
                    }
                    else
                    {
                        // binding 없음: AssignTexturesToMaterials가 이미 External .mat에
                        // 텍스처를 할당했으므로 sharedMaterials를 그대로 사용
                        newSmr.sharedMaterials = smr.sharedMaterials;
                        Debug.Log($"[WarudoBuild] 바인딩 없음 ({groupName}/{smr.name}) — External mat 직접 사용");
                    }
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
            out Dictionary<string, MatProps> matProps,
            out List<SmrBinding> smrBindings)
        {
            var ex  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mtm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var mp  = new Dictionary<string, MatProps>(StringComparer.OrdinalIgnoreCase);
            var sb  = new List<SmrBinding>();

            excludedMeshes = ex;
            matTexMap      = mtm;
            matProps       = mp;
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

            // ── matProperties: 머티리얼별 셰이더/속성 ──
            ParseMatProperties(json, mp);

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
                        var texs  = new List<string>();
                        ParseJsonArray(obj, "materials",  ts => mats.AddRange(ts));
                        ParseJsonArray(obj, "textures",   ts => texs.AddRange(ts));
                        ParseJsonArray(obj, "boneNames",  ts => bones.AddRange(ts));
                        binding.Materials = mats.ToArray();
                        binding.Textures  = texs.ToArray();
                        binding.BoneNames = bones.ToArray();
                        if (!string.IsNullOrEmpty(binding.SmrName))
                            sb.Add(binding);
                    }
                }
            }
        }

        /// <summary>
        /// manifest.json의 "matProperties" 섹션을 파싱해 MatProps 딕셔너리에 저장.
        /// </summary>
        private static void ParseMatProperties(string json, Dictionary<string, MatProps> result)
        {
            try
            {
                var root = JObject.Parse(json);
                var matProps = root["matProperties"] as JObject;
                if (matProps == null) return;

                foreach (var kv in matProps)
                {
                    var matName = kv.Key;
                    var obj     = kv.Value as JObject;
                    if (obj == null) continue;

                    var p = new MatProps();
                    p.ShaderName  = obj["shader"]?.Value<string>() ?? "Standard";
                    p.Keywords    = obj["keywords"]?.Value<string>() ?? "";
                    p.RenderQueue = obj["renderQueue"]?.Value<int>() ?? -1;

                    // colors: { "_Color": [r,g,b,a], ... }
                    if (obj["colors"] is JObject colors)
                        foreach (var ckv in colors)
                            if (ckv.Value is JArray arr && arr.Count >= 4)
                                p.Colors[ckv.Key] = new float[]
                                {
                                    arr[0].Value<float>(), arr[1].Value<float>(),
                                    arr[2].Value<float>(), arr[3].Value<float>()
                                };

                    // floats: { "_Prop": value, ... }
                    if (obj["floats"] is JObject floats)
                        foreach (var fkv in floats)
                            p.Floats[fkv.Key] = fkv.Value.Value<float>();

                    result[matName] = p;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WarudoBuild] matProperties 파싱 실패: {e.Message}");
            }
            Debug.Log($"[WarudoBuild] matProperties 로드: {result.Count}개");
        }

        private static int FindMatchingBrace(string s, int open, char openChar, char closeChar)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == openChar) depth++;
                else if (s[i] == closeChar) { depth--; if (depth == 0) return i; }
            }
            return -1;
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
            public string[] Textures;   // 슬롯별 텍스처 파일명 (인덱스 기반)
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
