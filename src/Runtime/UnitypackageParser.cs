// UnitypackageParser.cs
// src-tauri/src/lib.rs의 parse_zip / parse_unitypackage 로직 C# 포팅
//
// 의존성:
//   ICSharpCode.SharpZipLib (NuGet 또는 Unity Package Manager)
//   https://github.com/icsharpcode/SharpZipLib

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    /// <summary>
    /// 파싱 결과 — 현재 Rust ParseResult 구조체와 동일
    /// </summary>
    public class ParseResult
    {
        public string Filename;
        public int TotalEntries;
        public string DetectedType;     // "avatar" / "clothing" / "hair" / "unknown"
        public float Confidence;
        public string DetectedName;
        public string TargetAvatar;
        public List<string> ExtractedFbxPaths = new();
        public List<string> ExtractedTextureNames = new();
        /// <summary>
        /// .mat 파싱 시 발견된 모든 텍스처 파일명 (lilToon 포함 모든 속성).
        /// 텍스처 필터링에 사용.
        /// </summary>
        public HashSet<string> ReferencedTextureFiles = new(StringComparer.OrdinalIgnoreCase);
        public string TempDirPath;
        /// <summary>
        /// .mat 파싱 결과: 머티리얼 이름 → 텍스처 매핑
        /// 현재 Rust의 material_texture_map과 동일
        /// </summary>
        // ★ OrdinalIgnoreCase: TriLib 머티리얼 이름 대소문자가 .mat 파일명과 다를 수 있음
        public Dictionary<string, MaterialTextures> MaterialTextureMap =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// FBX externalObjects에서 추출: FBX 내부 머티리얼 이름 → .mat 파일 이름
        /// (예: "マテリアル" → "Body_2")
        /// TriLib이 로드한 mat.name이 FBX 내부 이름이므로, 이를 .mat 파일명으로 변환할 때 사용
        /// </summary>
        public Dictionary<string, string> FbxMaterialMap =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// .prefab 파일에서 추출한 SkinnedMeshRenderer별 블렌드쉐이프 웨이트.
        /// 키: 메시 GUID (FBX fileID에서 추출) 또는 순서 인덱스 "idx:N"
        /// 값: float[] — 블렌드쉐이프 인덱스 순서대로
        /// </summary>
        public List<PrefabSmrData> PrefabSmrDataList = new();

        /// <summary>
        /// .prefab에서 파싱한 VRCPhysBone 컴포넌트 목록.
        /// 루트 본 이름 기준으로 저장됨.
        /// </summary>
        public List<PhysBoneData> PhysBoneDataList = new();

        /// <summary>
        /// .prefab에서 m_IsActive: 0 인 GameObject 이름 목록.
        /// 임포트 시 해당 이름의 SMR을 자동으로 숨김 처리.
        /// </summary>
        public HashSet<string> InactiveGoNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// VRCAvatarDescriptor → FX AnimatorController → 기본 상태 AnimationClip에서 읽은
        /// 블렌드쉐이프 기본값. GO 경로 끝 이름 → (블렌드쉐이프 이름 → 값).
        /// TriLib 로드 후 SMR 이름 기준으로 적용.
        /// </summary>
        public Dictionary<string, Dictionary<string, float>> AvatarDefaultBlendShapes = new();

        /// <summary>
        /// AnimationClip m_EulerCurves / m_RotationCurves에서 추출한 본 포즈 오버라이드.
        /// 본 이름(path 끝 세그먼트) → (x, y, z) Euler 각도.
        /// TriLib 로드 후 해당 본의 localEulerAngles에 적용 (힐 각도 등).
        /// </summary>
        public Dictionary<string, Vector3> AnimClipBonePoses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 본 이름을 알 수 없는 힐 회전 (fileID 미해결 + 힐 패턴).
        /// DresserUI에서 아바타 Foot 본을 이름으로 찾아 직접 적용.
        /// </summary>
        public float[] UnresolvedHeelRotation; // [x, y, z, w] quaternion, null if none

        /// <summary>
        /// FBX .meta의 fileIDToRecycleName에서 추출: fileID → 본/오브젝트 이름.
        /// PrefabInstance m_Modifications의 fileID를 본 이름으로 역매핑할 때 사용.
        /// </summary>
        public Dictionary<string, string> FbxFileIdToName = new();

        /// <summary>
        /// PrefabInstance m_Modifications에서 파싱한 본 Transform 수정 목록.
        /// Shrink 본 scale=0 등 cross-package 본 변형에 사용.
        /// DresserUI에서 avatarFbxFileIdToName으로 bone 이름 해석 후 적용.
        /// </summary>
        public List<PrefabBoneModData> PrefabBoneMods = new();

        /// <summary>
        /// 내부 임시 저장: prefab 파싱 시 수집한 fileID → quaternion[x,y,z,w].
        /// FBX meta 파싱(1.5패스) 이후 ResolveRotationsToBonePoses()로 이름 매핑 완료.
        /// </summary>
        internal Dictionary<string, float[]> _PendingRotationMap = new();
    }

    /// <summary>
    /// AnimationClip에서 파싱한 본 포즈 데이터.
    /// m_EulerCurves의 X/Y/Z 컴포넌트를 별도 파싱 후 조합.
    /// </summary>
    public class BonePoseData
    {
        public float? X, Y, Z;
        public Vector3 ToEuler() => new Vector3(X ?? 0f, Y ?? 0f, Z ?? 0f);
        public bool HasAny => X.HasValue || Y.HasValue || Z.HasValue;
    }

    /// <summary>
    /// .prefab YAML에서 파싱한 SkinnedMeshRenderer 블렌드쉐이프 데이터.
    ///
    /// 두 가지 prefab 포맷을 모두 지원:
    ///   A) 일반 Prefab (type 137 SMR):  BlendShapeWeights 밀집 배열
    ///   B) PrefabInstance (type 1001):  SparseWeights 스파스 딕셔너리 {index → value}
    /// </summary>
    public class PrefabSmrData
    {
        public string GoName;             // SMR이 붙은 GameObject 이름 (매칭 키, B형은 null 가능)
        public string MeshGuid;           // FBX 파일 GUID (target.guid)
        public string TargetFileId;       // FBX 내 컴포넌트 fileID (B형 그룹핑 키)
        public float[] BlendShapeWeights; // A형: 밀집 배열 (index 0부터 순서대로)
        public Dictionary<int, float> SparseWeights; // B형: {blendshape index → value}
    }

    /// <summary>
    /// PrefabInstance m_Modifications에서 파싱한 본(Transform) 스케일/포지션 수정 데이터.
    /// Shrink 본 (Shrink_Upper_leg 등) scale=0 적용에 사용.
    /// </summary>
    public class PrefabBoneModData
    {
        public string TargetFileId;  // FBX 내 Transform의 fileID
        public string TargetGuid;    // FBX 파일 GUID (아바타 FBX GUID와 일치해야 함)
        public float? ScaleX, ScaleY, ScaleZ;
        public float? PosX,   PosY,   PosZ;

        public bool HasScale => ScaleX.HasValue || ScaleY.HasValue || ScaleZ.HasValue;
        public bool HasPos   => PosX.HasValue   || PosY.HasValue   || PosZ.HasValue;

        public Vector3 GetScale(Vector3 fallback) => new Vector3(
            ScaleX ?? fallback.x, ScaleY ?? fallback.y, ScaleZ ?? fallback.z);
        public Vector3 GetPos(Vector3 fallback) => new Vector3(
            PosX ?? fallback.x, PosY ?? fallback.y, PosZ ?? fallback.z);
    }

    /// <summary>
    /// .prefab YAML에서 파싱한 VRCPhysBone 컴포넌트 데이터.
    /// WarudoBuildScript가 MagicaCloth2 BoneCloth로 변환할 때 사용.
    /// </summary>
    public class PhysBoneData
    {
        public string RootBoneName;               // rootTransform fileID → GO 이름
        public List<string> IgnoreBoneNames = new(); // ignoreTransforms 역참조
        public float Pull       = 0.2f;           // stiffness
        public float Spring     = 0.6f;           // elasticity / damping 역수
        public float Stiffness  = 0.2f;
        public float Gravity    = 0f;
        public float GravityFalloff = 0f;
        public float Immobile   = 0f;             // inertia
        public float Radius     = 0.02f;          // capsule radius
    }

    public class MaterialTextures
    {
        public string MainTex;
        public string BumpMap;
        public string EmissionMap;

        // lilToon / Standard 셰이더 프로퍼티 전체 — .mat 파일에서 직접 읽음
        public Dictionary<string, Color>  Colors   = new();   // m_Colors 섹션
        public Dictionary<string, float>  Floats   = new();   // m_Floats 섹션
        public string                     Keywords = "";      // m_ShaderKeywords
        public int                        RenderQueue = -1;   // m_CustomRenderQueue (-1=미설정)
    }

    /// <summary>
    /// .unitypackage (tar.gz) 및 .zip 파일 파서
    /// 현재 Rust lib.rs의 parse_unitypackage / parse_zip 완전 포팅
    /// </summary>
    public static class UnitypackageParser
    {
        // ─── .zip 파일 파싱 ───
        // src-tauri/src/lib.rs parse_zip() 포팅
        public static async Task<ParseResult> ParseZipAsync(string zipPath)
        {
            var tempDir = Path.Combine(
                Path.GetTempPath(),
                "virtual-dresser",
                Path.GetRandomFileName()
            );
            Directory.CreateDirectory(tempDir);

            var result = new ParseResult
            {
                Filename = Path.GetFileName(zipPath),
                TempDirPath = tempDir
            };

            using var zipStream = File.OpenRead(zipPath);
            using var zip = new ZipFile(zipStream);

            // .zip 내 .unitypackage 탐색
            string unitypackageName = null;
            foreach (ZipEntry entry in zip)
            {
                if (entry.IsFile && entry.Name.EndsWith(".unitypackage",
                    StringComparison.OrdinalIgnoreCase))
                {
                    unitypackageName = entry.Name;
                    break;
                }
            }

            if (unitypackageName != null)
            {
                // .unitypackage 추출 후 파싱
                var unitypackagePath = Path.Combine(tempDir, Path.GetFileName(unitypackageName));
                using var entryStream = zip.GetInputStream(zip.GetEntry(unitypackageName));
                using var outStream = File.Create(unitypackagePath);
                await entryStream.CopyToAsync(outStream);

                var innerResult = await ParseUnitypackageAsync(unitypackagePath);
                innerResult.Filename = result.Filename;
                return innerResult;
            }
            else
            {
                // .zip 내 FBX/텍스처 직접 탐색
                await ExtractFbxFromZipAsync(zip, tempDir, result);
            }

            return result;
        }

        // ─── .unitypackage 파일 파싱 ───
        // ★ 단일 패스 디스크 스트리밍: RAM에 에셋 데이터를 올리지 않음
        //   .unitypackage = tar.gz, 구조: {guid}/pathname, {guid}/asset
        public static async Task<ParseResult> ParseUnitypackageAsync(string packagePath)
        {
            var tempDir = Path.Combine(
                Path.GetTempPath(), "virtual-dresser", Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            var result = new ParseResult
            {
                Filename   = Path.GetFileName(packagePath),
                TempDirPath = tempDir
            };

            // 백그라운드 스레드에서 tar 스트리밍 (메인 스레드 블로킹 방지)
            var (guidToPathname, guidToTempAsset, guidToTempMeta, guidToMemAsset, guidToMemMeta) =
                await Task.Run(() => StreamTarToDisk(packagePath, tempDir));

            result.TotalEntries = guidToPathname.Count;

            // ── 헬퍼: guid로 텍스트 읽기 (메모리 우선, 디스크 폴백) ──
            string ReadAssetText(string guid)
            {
                if (guidToMemAsset.TryGetValue(guid, out var bytes))
                    return Encoding.UTF8.GetString(bytes);
                if (guidToTempAsset.TryGetValue(guid, out var path))
                    return File.ReadAllText(path);
                return null;
            }

            // ── 1패스: .mat 파일 파싱 (병렬) ──
            // 메모리 버퍼 우선 사용 → 디스크 I/O 없이 파싱
            var matEntries = guidToPathname
                .Where(kv => Path.GetExtension(kv.Value)
                    .Equals(".mat", StringComparison.OrdinalIgnoreCase)
                    && (guidToMemAsset.ContainsKey(kv.Key) || guidToTempAsset.ContainsKey(kv.Key)))
                .ToList();

            if (matEntries.Count > 0)
            {
                var parsedMats = new System.Collections.Concurrent.ConcurrentDictionary<string, MaterialTextures>(
                    StringComparer.OrdinalIgnoreCase);

                // 모든 .mat에서 참조된 텍스처 파일명 수집용 (thread-safe)
                var allTexSet = new System.Collections.Concurrent.ConcurrentBag<string>();

                await Task.Run(() =>
                    System.Threading.Tasks.Parallel.ForEach(matEntries, entry =>
                    {
                        var (guid, pathname) = entry;
                        try
                        {
                            string matText;
                            if (guidToMemAsset.TryGetValue(guid, out var buf))
                                matText = Encoding.UTF8.GetString(buf);
                            else if (guidToTempAsset.TryGetValue(guid, out var tmpPath))
                                matText = File.ReadAllText(tmpPath);
                            else return;

                            // 스레드별 로컬 세트 → ConcurrentBag에 추가
                            var localTex  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var matName   = Path.GetFileNameWithoutExtension(pathname);
                            var matResult = ParseMatFile(matText, matName, guidToPathname, localTex);
                            if (matResult != null)
                                parsedMats[matName] = matResult;
                            foreach (var t in localTex) allTexSet.Add(t);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[Parser] .mat 읽기 실패: {pathname} — {ex.Message}");
                        }
                    })
                );

                // 참조 텍스처 목록을 ParseResult에 저장
                foreach (var t in allTexSet) result.ReferencedTextureFiles.Add(t);

                // 결과 병합 + 디스크 임시 파일 삭제 (메모리 버퍼는 GC에 맡김)
                foreach (var (guid, pathname) in matEntries)
                {
                    var matName = Path.GetFileNameWithoutExtension(pathname);
                    if (parsedMats.TryGetValue(matName, out var matResult))
                        result.MaterialTextureMap[matName] = matResult;
                    guidToMemAsset.Remove(guid);
                    if (guidToTempAsset.TryGetValue(guid, out var tmpPath))
                    {
                        try { File.Delete(tmpPath); } catch { }
                        guidToTempAsset.Remove(guid);
                    }
                }
            }

            Debug.Log($"[Parser] .mat 파싱 완료: {result.MaterialTextureMap.Count}개");

            // ── 1.2패스: .prefab / .controller / .anim 파일 파싱 ──
            // 메모리 버퍼 우선 사용
            var guidToTempController = new Dictionary<string, string>(); // guid → temp path (대형)
            var guidToMemController  = new Dictionary<string, byte[]>();  // guid → mem buf (소형)
            var guidToTempAnim       = new Dictionary<string, string>();
            var guidToMemAnim        = new Dictionary<string, byte[]>();

            foreach (var (guid, pathname) in guidToPathname)
            {
                var ext = Path.GetExtension(pathname);
                if (ext.Equals(".controller", StringComparison.OrdinalIgnoreCase))
                {
                    if (guidToMemAsset.TryGetValue(guid, out var buf)) guidToMemController[guid] = buf;
                    else if (guidToTempAsset.TryGetValue(guid, out var p)) guidToTempController[guid] = p;
                }
                else if (ext.Equals(".anim", StringComparison.OrdinalIgnoreCase))
                {
                    if (guidToMemAsset.TryGetValue(guid, out var buf)) guidToMemAnim[guid] = buf;
                    else if (guidToTempAsset.TryGetValue(guid, out var p)) guidToTempAnim[guid] = p;
                }
            }
            Debug.Log($"[Parser] animator controller {guidToMemController.Count + guidToTempController.Count}개, " +
                      $"anim clip {guidToMemAnim.Count + guidToTempAnim.Count}개 발견");

            var prefabPaths = guidToPathname
                .Where(kv => Path.GetExtension(kv.Value)
                    .Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Debug.Log($"[Parser] .prefab 파일 {prefabPaths.Count}개 발견: " +
                      string.Join(", ", prefabPaths.Select(kv => Path.GetFileName(kv.Value))));

            foreach (var (guid, pathname) in prefabPaths)
            {
                string prefabText;
                string prefabTmpPath = null; // finally에서 디스크 파일 삭제용
                if (guidToMemAsset.TryGetValue(guid, out var prefabBuf))
                    prefabText = Encoding.UTF8.GetString(prefabBuf);
                else if (guidToTempAsset.TryGetValue(guid, out prefabTmpPath))
                    prefabText = File.ReadAllText(prefabTmpPath);
                else
                {
                    Debug.LogWarning($"[Parser] .prefab 데이터 없음 (이미 삭제?): {pathname}");
                    continue;
                }

                try
                {
                    Debug.Log($"[Parser] .prefab 읽기: {pathname} ({prefabText.Length}자)");

                    // 진단: m_BlendShapeWeights 포함 여부 확인
                    // hasWeightsSparse: PrefabInstance 블록 안에서만 체크 (MonoBehaviour 오탐 방지)
                    bool hasWeightsDense  = prefabText.Contains("m_BlendShapeWeights:");
                    bool hasSMR           = prefabText.Contains("!u!137");
                    bool hasPrefabInst    = prefabText.Contains("!u!1001");
                    // PrefabInstance 내부에서만 sparse 패턴 검색
                    bool hasWeightsSparse = false;
                    if (hasPrefabInst)
                    {
                        int instIdx = 0;
                        while ((instIdx = prefabText.IndexOf("!u!1001 &", instIdx)) >= 0)
                        {
                            int nextBlock = prefabText.IndexOf("\n---", instIdx + 1);
                            int end = nextBlock < 0 ? prefabText.Length : nextBlock;
                            if (prefabText.IndexOf("m_BlendShapeWeights.Array.data[", instIdx, end - instIdx) >= 0)
                            {
                                hasWeightsSparse = true;
                                break;
                            }
                            instIdx++;
                        }
                    }
                    Debug.Log($"[Parser] .prefab 분석: hasSMR={hasSMR}, hasPrefabInst={hasPrefabInst}, " +
                              $"hasWeightsDense={hasWeightsDense}, hasWeightsSparse={hasWeightsSparse}");

                    var smrDataList = ParsePrefabBlendShapes(prefabText, result._PendingRotationMap, result.PrefabBoneMods);
                    result.PrefabSmrDataList.AddRange(smrDataList);

                    // m_IsActive: 0 인 GO 이름 수집
                    var inactiveNames = ParsePrefabInactiveGoNames(prefabText);
                    if (inactiveNames.Count > 0)
                    {
                        foreach (var n in inactiveNames) result.InactiveGoNames.Add(n);
                        Debug.Log($"[Parser] 비활성 GO {inactiveNames.Count}개: {string.Join(", ", inactiveNames)}");
                    }

                    var pbDataList = ParsePrefabPhysBones(prefabText);
                    if (pbDataList.Count > 0)
                    {
                        result.PhysBoneDataList.AddRange(pbDataList);
                        Debug.Log($"[Parser] PhysBone {pbDataList.Count}개 파싱: " +
                                  string.Join(", ", pbDataList.Select(pb => pb.RootBoneName ?? "(null)")));
                    }

                    // VRCAvatarDescriptor → FX AnimatorController → 모든 레이어 기본 상태 AnimationClip 체인
                    if (result.AvatarDefaultBlendShapes.Count == 0) // 첫 번째 성공한 prefab만 사용
                    {
                        var fxGuid = ParseFxControllerGuid(prefabText);
                        string ctrlText = null;
                        if (fxGuid != null)
                        {
                            if (guidToMemController.TryGetValue(fxGuid, out var ctrlBuf))
                                ctrlText = Encoding.UTF8.GetString(ctrlBuf);
                            else if (guidToTempController.TryGetValue(fxGuid, out var ctrlTmp))
                                ctrlText = File.ReadAllText(ctrlTmp);
                        }
                        if (ctrlText != null)
                        {
                            var clipGuids = ParseAnimatorAllDefaultClipGuids(ctrlText);
                            Debug.Log($"[Parser] FX 전체 레이어 기본 클립 {clipGuids.Count}개 수집");

                            var merged = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
                            int missingClips = 0;
                            foreach (var clipGuid in clipGuids)
                            {
                                string animText = null;
                                if (guidToMemAnim.TryGetValue(clipGuid, out var animBuf))
                                    animText = Encoding.UTF8.GetString(animBuf);
                                else if (guidToTempAnim.TryGetValue(clipGuid, out var animTmp))
                                    animText = File.ReadAllText(animTmp);
                                if (animText == null)
                                { missingClips++; continue; }
                                var bsFromClip = ParseAnimClipBlendShapes(animText);
                                // 레이어별 결과를 merged에 합산 (나중 레이어가 앞 레이어를 덮어씀)
                                foreach (var (goName, bsMap) in bsFromClip)
                                {
                                    if (!merged.TryGetValue(goName, out var target))
                                        merged[goName] = target = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var (bs, val) in bsMap)
                                    {
                                        // MAX 집계: 여러 클립이 같은 BS를 설정할 때 가장 큰 값 유지
                                        if (!target.TryGetValue(bs, out var existing) || val > existing)
                                            target[bs] = val;
                                    }
                                }
                            }
                            if (missingClips > 0)
                                Debug.Log($"[Parser] FX 클립 {missingClips}개 패키지 내 없음 (외부 에셋)");

                            if (merged.Count > 0)
                            {
                                result.AvatarDefaultBlendShapes = merged;
                                Debug.Log($"[Parser] AnimClip 기본 블렌드쉐이프 {merged.Values.Sum(d => d.Count)}개 " +
                                          $"({merged.Count}개 SMR): " +
                                          string.Join(", ", merged.Select(kv =>
                                              $"{kv.Key}[{string.Join(",", kv.Value.Select(b => $"{b.Key}={b.Value}"))}]")));
                            }
                            else
                                Debug.Log("[Parser] FX AnimClip에서 비zero 블렌드쉐이프 없음 (또는 모두 외부 에셋)");
                        }

                        // ── fallback: 모든 .anim 파일 스캔 (바디 non-zero 블렌드쉐이프 + Transform 커브 수집) ──
                        // VRC에서는 SavedParameter로 FX 레이어가 힐 등을 활성화하므로
                        // default state 체인만으로 잡히지 않는 경우 전체 스캔으로 보완.
                        int totalAnimCount = guidToMemAnim.Count + guidToTempAnim.Count;
                        if (totalAnimCount > 0)
                        {
                            Debug.Log($"[Parser] 전체 .anim {totalAnimCount}개 스캔 (BS + Transform 커브)");
                            var allAnimBs    = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
                            var allBonePoses = new Dictionary<string, BonePoseData>(StringComparer.OrdinalIgnoreCase);
                            // 얼굴 관련 경로 키워드 (블렌드쉐이프 제외 대상)
                            var faceKeywords = new[]{ "face", "eye", "mouth", "brow", "tongue", "tear", "blush",
                                                      "顔", "目", "口", "眉" };
                            // 발 관련 본 이름 키워드 (Transform 커브 포함 대상)
                            var footKeywords = new[]{ "foot", "toe", "ankle", "heel",
                                                      "Foot", "Toe", "Ankle" };

                            // 메모리 버퍼 + 디스크 파일 합산 이터레이션
                            var allAnimEntries = guidToMemAnim.Keys.Select(g => (g, isMemory: true))
                                .Concat(guidToTempAnim.Keys.Select(g => (g, isMemory: false)));
                            foreach (var (animGuid, isMemory) in allAnimEntries)
                            {
                                try
                                {
                                    var animText = isMemory
                                        ? Encoding.UTF8.GetString(guidToMemAnim[animGuid])
                                        : File.ReadAllText(guidToTempAnim[animGuid]);

                                    // ── 블렌드쉐이프 커브 ──
                                    var bsFromClip = ParseAnimClipBlendShapes(animText);
                                    foreach (var (goName, bsMap) in bsFromClip)
                                    {
                                        bool isFace = System.Array.Exists(faceKeywords,
                                            k => goName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                                        if (isFace) continue;
                                        if (!allAnimBs.TryGetValue(goName, out var target))
                                            allAnimBs[goName] = target = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                                        foreach (var (bs, val) in bsMap)
                                        {
                                            // 블렌드쉐이프 이름 자체가 얼굴 관련이면 제외
                                            bool bsIsFace = System.Array.Exists(faceKeywords,
                                                k => bs.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                                            if (bsIsFace) continue;
                                            if (!target.TryGetValue(bs, out var existing) || val > existing)
                                                target[bs] = val;
                                        }
                                    }

                                    // ── Transform(Euler) 커브 — 발/발가락 본만 수집 ──
                                    var eulerCurves = ParseAnimClipEulerCurves(animText);
                                    foreach (var (boneName, bpd) in eulerCurves)
                                    {
                                        if (!bpd.HasAny) continue;
                                        bool isFoot = System.Array.Exists(footKeywords,
                                            k => boneName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                                        if (!isFoot) continue;
                                        // 더 큰 절댓값 우선 (더 강한 포즈)
                                        if (!allBonePoses.TryGetValue(boneName, out var existing))
                                            allBonePoses[boneName] = bpd;
                                        else
                                        {
                                            if (bpd.X.HasValue && (!existing.X.HasValue || Math.Abs(bpd.X.Value) > Math.Abs(existing.X.Value))) existing.X = bpd.X;
                                            if (bpd.Y.HasValue && (!existing.Y.HasValue || Math.Abs(bpd.Y.Value) > Math.Abs(existing.Y.Value))) existing.Y = bpd.Y;
                                            if (bpd.Z.HasValue && (!existing.Z.HasValue || Math.Abs(bpd.Z.Value) > Math.Abs(existing.Z.Value))) existing.Z = bpd.Z;
                                        }
                                    }
                                }
                                catch { /* 개별 .anim 파싱 실패 무시 */ }
                            }

                            if (allAnimBs.Count > 0 && result.AvatarDefaultBlendShapes.Count == 0)
                            {
                                result.AvatarDefaultBlendShapes = allAnimBs;
                                Debug.Log($"[Parser] 전체 .anim BS: {allAnimBs.Values.Sum(d => d.Count)}개 " +
                                          $"({allAnimBs.Count}개 SMR): " +
                                          string.Join(", ", allAnimBs.Select(kv =>
                                              $"{kv.Key}[{string.Join(",", kv.Value.Select(b => $"{b.Key}={b.Value}"))}]")));
                            }

                            if (allBonePoses.Count > 0)
                            {
                                foreach (var (bn, bpd) in allBonePoses)
                                    result.AnimClipBonePoses[bn] = bpd.ToEuler();
                                Debug.Log($"[Parser] 전체 .anim Transform 커브 (발 본): " +
                                          string.Join(", ", allBonePoses.Select(kv =>
                                              $"{kv.Key}({kv.Value.ToEuler()})")));
                            }
                            else
                                Debug.Log("[Parser] 전체 .anim 스캔: 발 관련 Transform 커브 없음");
                        }
                    }

                    if (smrDataList.Count > 0)
                    {
                        int denseCount  = smrDataList.Count(d => d.BlendShapeWeights != null);
                        int sparseCount = smrDataList.Count(d => d.SparseWeights != null);
                        Debug.Log($"[Parser] .prefab 블렌드쉐이프: dense={denseCount}개, sparse={sparseCount}개 / {pathname}\n" +
                                  string.Join("\n", smrDataList.Select(d =>
                                      d.SparseWeights != null
                                          ? $"  [sparse] fileID={d.TargetFileId} 항목={d.SparseWeights.Count}개: " +
                                            string.Join(", ", d.SparseWeights.Take(5).Select(kv => $"[{kv.Key}]={kv.Value}"))
                                          : $"  [dense]  GoName='{d.GoName}' 웨이트수={d.BlendShapeWeights.Length} " +
                                            $"비0={d.BlendShapeWeights.Count(w => w != 0)}")));
                    }
                    else if (hasWeightsDense || hasWeightsSparse)
                    {
                        // 파싱 결과 0개 = 모든 블렌드쉐이프가 0 (prefab 기본 상태) → 정상
                        Debug.Log($"[Parser] .prefab 블렌드쉐이프 전부 0 (정상 기본상태): {pathname}");
                    }
                    else
                    {
                        Debug.Log($"[Parser] .prefab 블렌드쉐이프 없음 (정상): {pathname}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Parser] .prefab 읽기 실패: {pathname} — {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    guidToMemAsset.Remove(guid);
                    if (prefabTmpPath != null)
                    {
                        try { File.Delete(prefabTmpPath); } catch { }
                        guidToTempAsset.Remove(guid);
                    }
                }
            }

            // ── 1.5패스: FBX .meta 파싱 → externalObjects로 FBX 내부 mat명 → .mat 파일명 매핑 ──
            Debug.Log($"[Parser] meta 파일 저장 수: disk={guidToTempMeta.Count}개, mem={guidToMemMeta.Count}개");
            foreach (var (guid, pathname) in guidToPathname)
            {
                if (!Path.GetExtension(pathname).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool hasMeta = guidToMemMeta.ContainsKey(guid) || guidToTempMeta.ContainsKey(guid);
                Debug.Log($"[Parser] FBX 발견: {Path.GetFileName(pathname)}, meta 있음={hasMeta}");

                // FBX 바이너리에서 nodeId → 본 이름 파싱 (PrefabInstance fileID 해결용)
                if (guidToTempAsset.TryGetValue(guid, out var fbxBinPath))
                {
                    try
                    {
                        byte[] fbxBytes = File.ReadAllBytes(fbxBinPath);
                        var binaryMap = ParseFbxBinaryNodeIds(fbxBytes);
                        foreach (var kv in binaryMap)
                            result.FbxFileIdToName[kv.Key] = kv.Value;
                        Debug.Log($"[Parser] FBX 바이너리 파싱: {binaryMap.Count}개 nodeId→이름 추출");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Parser] FBX 바이너리 파싱 실패: {ex.Message}");
                    }
                }

                // .meta: 메모리 우선, 디스크 폴백
                string metaText = null;
                if (guidToMemMeta.TryGetValue(guid, out var metaBuf))
                {
                    metaText = Encoding.UTF8.GetString(metaBuf);
                    guidToMemMeta.Remove(guid);
                }
                else if (guidToTempMeta.TryGetValue(guid, out var metaPath))
                {
                    try { metaText = File.ReadAllText(metaPath); }
                    catch (Exception ex) { Debug.LogWarning($"[Parser] FBX meta 읽기 실패: {pathname} — {ex.Message}"); }
                    finally
                    {
                        try { File.Delete(metaPath); } catch { }
                        guidToTempMeta.Remove(guid);
                    }
                }

                if (metaText == null) continue;
                try
                {
                    bool hasExtObj = metaText.Contains("externalObjects:");
                    Debug.Log($"[Parser] {Path.GetFileName(pathname)}.meta: {metaText.Length}자, externalObjects={hasExtObj}");
                    ParseFbxMeta(metaText, guidToPathname, result.FbxMaterialMap, result.FbxFileIdToName);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Parser] FBX meta 파싱 실패: {pathname} — {ex.Message}");
                }
            }

            // 남은 meta 파일 정리
            foreach (var mp in guidToTempMeta.Values)
                try { if (File.Exists(mp)) File.Delete(mp); } catch { }

            if (result.FbxMaterialMap.Count > 0)
                Debug.Log($"[Parser] FBX externalObjects 매핑: {result.FbxMaterialMap.Count}개 " +
                          $"(예: {string.Join(", ", result.FbxMaterialMap.Take(3).Select(kv => $"'{kv.Key}'→'{kv.Value}'"))})");

            // ── 1.6패스: Prefab에서 수집한 회전 데이터를 FBX fileIDToRecycleName으로 본 이름 매핑 ──
            if (result._PendingRotationMap.Count > 0)
            {
                int resolvedRot = 0;
                int directRot   = 0;
                Debug.Log($"[Parser] 회전 데이터 resolve: {result._PendingRotationMap.Count}개 항목, " +
                          $"fileIdToName {result.FbxFileIdToName.Count}개");

                foreach (var (key, rot) in result._PendingRotationMap)
                {
                    string boneName;

                    if (key.StartsWith("__name__"))
                    {
                        // type=4 Transform 직접 파싱 결과 — 이름이 이미 키에 포함
                        boneName = key.Substring("__name__".Length);
                        directRot++;
                    }
                    else
                    {
                        // fileID 기반 — fileIDToRecycleName으로 이름 해결
                        if (!result.FbxFileIdToName.TryGetValue(key, out boneName))
                        {
                            // 힐 패턴 감지: |x|≈0.7071, y≈0, z≈0 (≈ -90° around X axis)
                            // 본 이름을 알 수 없어도 회전값은 파싱됨 → UnresolvedHeelRotation에 저장
                            if (Mathf.Abs(rot[0]) > 0.5f && Mathf.Abs(rot[1]) < 0.15f && Mathf.Abs(rot[2]) < 0.15f)
                            {
                                result.UnresolvedHeelRotation = rot;
                                Debug.Log($"[Parser] 힐 회전 감지 (fileID={key}): x={rot[0]:F4}, y={rot[1]:F4}, z={rot[2]:F4}, w={rot[3]:F4}");
                            }
                            continue;
                        }
                    }

                    var q = new Quaternion(rot[0], rot[1], rot[2], rot[3]);
                    var euler = q.eulerAngles;
                    // 이미 있는 항목은 덮어쓰지 않음 (직접 파싱이 더 정확한 경우 우선)
                    if (!result.AnimClipBonePoses.ContainsKey(boneName))
                        result.AnimClipBonePoses[boneName] = euler;
                    resolvedRot++;
                    Debug.Log($"[Parser] Prefab 본 회전 확정: '{boneName}' euler=({euler.x:F1},{euler.y:F1},{euler.z:F1})");
                }
                Debug.Log($"[Parser] Prefab 본 회전 resolve: 직접={directRot}개, fileID해결={resolvedRot - directRot}개/{result._PendingRotationMap.Count - directRot}개");
            }

            // ── 2패스: FBX + 텍스처 temp 파일을 최종 위치로 이동 ──
            // ★ 텍스처 필터링: .mat 파싱으로 확인된 실제 사용 텍스처만 이동, 나머지 삭제
            // ReferencedTextureFiles = mat에서 guid 추적으로 수집된 모든 텍스처 파일명
            var usedTextureNames = result.ReferencedTextureFiles;
            Debug.Log($"[Parser] 사용 텍스처 {usedTextureNames.Count}개 (전체 텍스처에서 필터링)");

            var fbxBySize = new List<(string path, long size)>();
            int texSkipped = 0;

            foreach (var (guid, pathname) in guidToPathname)
            {
                var ext = Path.GetExtension(pathname).ToLowerInvariant();
                if (!guidToTempAsset.TryGetValue(guid, out var tmpPath)) continue;

                bool needed = ext == ".fbx" || IsTextureExtension(ext);
                if (!needed)
                {
                    try { File.Delete(tmpPath); } catch { }
                    continue;
                }

                if (IsTextureExtension(ext))
                {
                    var texName = Path.GetFileName(pathname);
                    // usedTextureNames가 0개(의상 패키지 등 .mat 없음)면 전체 허용
                    if (usedTextureNames.Count > 0 && !usedTextureNames.Contains(texName))
                    {
                        try { File.Delete(tmpPath); } catch { }
                        texSkipped++;
                        continue;
                    }
                }

                var finalName = SafeFileName(tempDir, Path.GetFileName(pathname));
                var finalPath = Path.Combine(tempDir, finalName);

                try { File.Move(tmpPath, finalPath); }
                catch { finalPath = tmpPath; }

                if (ext == ".fbx")
                    fbxBySize.Add((finalPath, new FileInfo(finalPath).Length));
                else
                    result.ExtractedTextureNames.Add(finalName);
            }

            if (texSkipped > 0)
                Debug.Log($"[Parser] 미사용 텍스처 {texSkipped}개 스킵 (디스크 삭제)");

            // 남은 임시 asset 파일 정리
            foreach (var tmp in guidToTempAsset.Values)
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            fbxBySize.Sort((a, b) => b.size.CompareTo(a.size));
            result.ExtractedFbxPaths = fbxBySize.Select(x => x.path).ToList();

            InferAssetType(result);

            Debug.Log($"[Parser] {result.Filename}: FBX {result.ExtractedFbxPaths.Count}개, " +
                      $"텍스처 {result.ExtractedTextureNames.Count}개 (필터링 후), " +
                      $"타입={result.DetectedType}({result.Confidence:F2})");

            return result;
        }

        /// <summary>
        /// tar.gz를 단일 패스로 스트리밍.
        /// ★ 최적화: 소형 파일(.mat/.prefab/.controller/.anim/.meta, ≤512KB)은 메모리에 보관.
        ///   FBX·텍스처 등 대형 파일만 디스크 임시 파일로 저장.
        ///   디스크 쓰기/읽기 이중 I/O 제거로 파싱 속도 개선.
        /// </summary>
        private static (
            Dictionary<string, string>  guidToPathname,
            Dictionary<string, string>  guidToTempAsset,   // 디스크 임시 파일 (FBX·텍스처)
            Dictionary<string, string>  guidToTempMeta,    // .meta 디스크 임시 파일 (대형) or 없음
            Dictionary<string, byte[]>  guidToMemAsset,    // 메모리 버퍼 (소형 에셋)
            Dictionary<string, byte[]>  guidToMemMeta)     // 메모리 버퍼 (소형 .meta)
            StreamTarToDisk(string packagePath, string tempDir)
        {
            var guidToPathname  = new Dictionary<string, string>();
            var guidToTempAsset = new Dictionary<string, string>();
            var guidToTempMeta  = new Dictionary<string, string>();
            var guidToMemAsset  = new Dictionary<string, byte[]>();
            var guidToMemMeta   = new Dictionary<string, byte[]>();

            // 소형 에셋으로 판단하는 크기 상한 (512KB)
            const long SmallFileThreshold = 512 * 1024;

            using var fs  = File.OpenRead(packagePath);
            using var gz  = new GZipInputStream(fs);
            using var tar = new TarInputStream(gz, Encoding.UTF8);

            TarEntry entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                if (entry.IsDirectory) continue;

                var name  = entry.Name.TrimStart('/');
                var slash = name.IndexOf('/');
                if (slash < 0) continue;

                var guid     = name[..slash];
                var filename = name[(slash + 1)..];

                if (filename == "pathname")
                {
                    using var ms = new MemoryStream((int)Math.Min(entry.Size, 4096));
                    tar.CopyEntryContents(ms);
                    guidToPathname[guid] = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                }
                else if (filename == "asset")
                {
                    if (entry.Size > 0 && entry.Size <= SmallFileThreshold)
                    {
                        // 소형 → 메모리 버퍼 (디스크 I/O 없음)
                        using var ms = new MemoryStream((int)entry.Size);
                        tar.CopyEntryContents(ms);
                        guidToMemAsset[guid] = ms.ToArray();
                    }
                    else
                    {
                        // 대형(FBX·텍스처 등) → 디스크
                        var tmpPath = Path.Combine(tempDir, guid + "_asset.tmp");
                        using var outFile = File.Create(tmpPath);
                        tar.CopyEntryContents(outFile);
                        guidToTempAsset[guid] = tmpPath;
                    }
                }
                else if (filename == "asset.meta")
                {
                    if (entry.Size > 0 && entry.Size <= SmallFileThreshold)
                    {
                        using var ms = new MemoryStream((int)entry.Size);
                        tar.CopyEntryContents(ms);
                        guidToMemMeta[guid] = ms.ToArray();
                    }
                    else
                    {
                        var metaPath = Path.Combine(tempDir, guid + "_meta.tmp");
                        using var outMeta = File.Create(metaPath);
                        tar.CopyEntryContents(outMeta);
                        guidToTempMeta[guid] = metaPath;
                    }
                }
                else
                {
                    tar.CopyEntryContents(Stream.Null);
                }
            }

            return (guidToPathname, guidToTempAsset, guidToTempMeta, guidToMemAsset, guidToMemMeta);
        }

        // ─── FBX 바이너리 파싱: Objects 섹션의 Model 노드에서 nodeId(int64) → 본 이름 추출 ───
        // FBX binary format: magic(27B) → 반복 노드 { endOffset(4/8B) numProps(4/8B) propsLen(4/8B) nameLen(1B) name propsData children }
        // Model 노드의 첫 번째 prop = int64 (unique nodeId), 두 번째 prop = string ("BoneName\0\x01Model")
        private static Dictionary<string, string> ParseFbxBinaryNodeIds(byte[] data)
        {
            var result = new Dictionary<string, string>();
            if (data == null || data.Length < 27) return result;

            // Magic check: "Kaydara FBX Binary  \0\x1a\x00"
            var magic = "Kaydara FBX Binary  ";
            for (int i = 0; i < magic.Length; i++)
                if (data[i] != (byte)magic[i]) return result;

            int version = BitConverter.ToInt32(data, 23);
            bool is64 = version >= 7500;
            int headerSize = is64 ? 25 : 13; // per-node header size (before nameLen)

            // Helper: read node header, return false if null/end node
            bool ReadNodeHeader(ref int p, out long nodeEnd, out long numProps, out long propsLen, out string name)
            {
                nodeEnd = numProps = propsLen = 0; name = "";
                if (p + headerSize + 1 > data.Length) return false;
                if (is64)
                {
                    nodeEnd  = (long)BitConverter.ToUInt64(data, p); p += 8;
                    numProps = (long)BitConverter.ToUInt64(data, p); p += 8;
                    propsLen = (long)BitConverter.ToUInt64(data, p); p += 8;
                }
                else
                {
                    nodeEnd  = BitConverter.ToUInt32(data, p); p += 4;
                    numProps = BitConverter.ToUInt32(data, p); p += 4;
                    propsLen = BitConverter.ToUInt32(data, p); p += 4;
                }
                int nameLen = data[p++];
                if (nodeEnd == 0 && numProps == 0 && propsLen == 0 && nameLen == 0) return false; // null record
                if (p + nameLen > data.Length) return false;
                name = System.Text.Encoding.ASCII.GetString(data, p, nameLen);
                p += nameLen;
                return true;
            }

            // Try to read int64 prop value (type='L')
            bool ReadInt64Prop(ref int p, out long val)
            {
                val = 0;
                if (p >= data.Length || data[p] != 'L') return false;
                p++;
                if (p + 8 > data.Length) return false;
                val = BitConverter.ToInt64(data, p); p += 8;
                return true;
            }

            // Try to read string prop value (type='S')
            bool ReadStringProp(ref int p, out string val)
            {
                val = "";
                if (p >= data.Length || data[p] != 'S') return false;
                p++;
                if (p + 4 > data.Length) return false;
                int len = BitConverter.ToInt32(data, p); p += 4;
                if (len < 0 || p + len > data.Length) return false;
                val = System.Text.Encoding.UTF8.GetString(data, p, len); p += len;
                return true;
            }

            // Skip one property (any type)
            void SkipProp(ref int p)
            {
                if (p >= data.Length) return;
                char t = (char)data[p++];
                switch (t)
                {
                    case 'Y': p += 2; break;
                    case 'C': p += 1; break;
                    case 'I': case 'F': p += 4; break;
                    case 'L': case 'D': p += 8; break;
                    case 'S': case 'R':
                        if (p + 4 <= data.Length) { int len = BitConverter.ToInt32(data, p); p += 4 + Math.Max(0, len); }
                        break;
                    case 'f': case 'd': case 'l': case 'i': case 'b':
                        if (p + 12 <= data.Length)
                        {
                            int arrLen = BitConverter.ToInt32(data, p);
                            int encoding = BitConverter.ToInt32(data, p + 4);
                            int compLen  = BitConverter.ToInt32(data, p + 8);
                            p += 12;
                            p += encoding == 1 ? compLen : arrLen * (t == 'd' || t == 'l' ? 8 : t == 'b' ? 1 : 4);
                        }
                        break;
                }
            }

            int pos = 27;
            // Parse top-level nodes to find "Objects"
            while (pos < data.Length)
            {
                int nodeStart = pos;
                if (!ReadNodeHeader(ref pos, out long nodeEnd, out long numProps, out long propsLen, out string nodeName))
                    break;
                if (nodeName == "Objects")
                {
                    // Skip props of Objects itself
                    int propsEnd = pos + (int)propsLen;
                    pos = propsEnd;
                    // Parse children (Model nodes)
                    while (pos < (int)nodeEnd)
                    {
                        int childStart = pos;
                        if (!ReadNodeHeader(ref pos, out long childEnd, out long childNumProps, out long childPropsLen, out string childName))
                            break;
                        int childPropsStart = pos;
                        if (childName == "Model" && childNumProps >= 2)
                        {
                            int pp = childPropsStart;
                            if (ReadInt64Prop(ref pp, out long nodeId) && ReadStringProp(ref pp, out string fullName))
                            {
                                // fullName: "BoneName\x00\x01Model" or just "BoneName"
                                int sep = fullName.IndexOf('\x00');
                                string boneName = sep >= 0 ? fullName.Substring(0, sep) : fullName;
                                if (!string.IsNullOrEmpty(boneName))
                                    result[nodeId.ToString()] = boneName;
                            }
                        }
                        pos = (int)childEnd; // jump to next child
                    }
                    break; // done
                }
                else
                {
                    pos = (int)nodeEnd;
                }
            }
            return result;
        }

        // ─── FBX .meta 파싱 (externalObjects: FBX 내부 머티리얼명 → 외부 .mat GUID) ───
        private static void ParseFbxMeta(
            string metaText,
            Dictionary<string, string> guidToPathname,
            Dictionary<string, string> fbxMaterialMap,
            Dictionary<string, string> fileIdToName = null)
        {
            // externalObjects 블록 라인 기반 파싱
            // 구조:
            //   - first:
            //       type: UnityEngine:Material
            //       assembly: ...
            //       name: マテリアル
            //     second:
            //       guid: abc123...
            //       type: 2

            bool inExternalObjects = false;
            bool inMaterialEntry   = false;
            string pendingName     = null;
            int mappedCount        = 0;

            foreach (var rawLine in metaText.Split('\n'))
            {
                var line = rawLine.TrimEnd();

                if (!inExternalObjects)
                {
                    if (line.TrimStart().StartsWith("externalObjects:"))
                        inExternalObjects = true;
                    continue;
                }

                // externalObjects 종료 감지 (들여쓰기 0 또는 다른 최상위 키)
                if (line.Length > 0 && line[0] != ' ' && line[0] != '-')
                {
                    inExternalObjects = false;
                    continue;
                }

                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("- first:"))
                {
                    inMaterialEntry = false;
                    pendingName     = null;
                    continue;
                }

                if (trimmed.StartsWith("type: UnityEngine:Material"))
                {
                    inMaterialEntry = true;
                    continue;
                }

                if (!inMaterialEntry) continue;

                if (trimmed.StartsWith("name:"))
                {
                    pendingName = trimmed.Substring(5).Trim();
                    continue;
                }

                if (trimmed.StartsWith("guid:") && pendingName != null)
                {
                    var matGuid = trimmed.Substring(5).Trim();
                    if (guidToPathname.TryGetValue(matGuid, out var matPath))
                    {
                        var matName = Path.GetFileNameWithoutExtension(matPath);
                        fbxMaterialMap[pendingName] = matName;
                        mappedCount++;
                        Debug.Log($"[Parser] externalObj: '{pendingName}' → '{matName}'");
                    }
                    else
                    {
                        Debug.Log($"[Parser] externalObj GUID 미해결: '{pendingName}' guid={matGuid}");
                    }
                    pendingName     = null;
                    inMaterialEntry = false;
                }
            }

            // ── fileIDToRecycleName 파싱 (fileID → 본/오브젝트 이름) ──
            // 구조:
            //   fileIDToRecycleName:
            //     6866332237670371181: Foot.L
            //     -8679921383154817045: Foot.R
            // 진단: 목표 fileID가 meta 텍스트에 있는지 확인
            if (fileIdToName != null)
            {
                var _metaDiagIds = new[] { "6866332237670371181", "-8679921383154817045" };
                foreach (var did in _metaDiagIds)
                {
                    var idx = metaText.IndexOf(did, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var ctx = metaText.Substring(Math.Max(0, idx - 30), Math.Min(120, metaText.Length - Math.Max(0, idx - 30)));
                        Debug.Log($"[Parser-Diag] Meta에서 {did} 발견: ...{ctx}...");
                    }
                    else
                        Debug.Log($"[Parser-Diag] Meta에서 {did} 없음 (metaText 길이={metaText.Length})");
                }
            }

            if (fileIdToName != null && metaText.Contains("fileIDToRecycleName:"))
            {
                bool inSection = false;
                int parsed = 0;
                foreach (var rawLine in metaText.Split('\n'))
                {
                    var line = rawLine.TrimEnd();
                    if (!inSection)
                    {
                        if (line.TrimStart().StartsWith("fileIDToRecycleName:"))
                        { inSection = true; continue; }
                        continue;
                    }
                    // 섹션 종료: 들여쓰기 없는 새 키
                    if (line.Length > 0 && line[0] != ' ' && line[0] != '\t')
                    { break; }

                    var trimmed = line.TrimStart();
                    // 형식: "  6866332237670371181: Foot.L"  또는  "  -8679921383154817045: Foot.R"
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx <= 0) continue;
                    var idPart   = trimmed.Substring(0, colonIdx).Trim();
                    var namePart = trimmed.Substring(colonIdx + 1).Trim();
                    if (string.IsNullOrEmpty(idPart) || string.IsNullOrEmpty(namePart)) continue;
                    fileIdToName[idPart] = namePart;
                    parsed++;
                }
                Debug.Log($"[Parser] fileIDToRecycleName: {parsed}개 파싱");
            }
        }

        // ─── .mat 파일 파싱 ───
        // .mat YAML에서 GUID 추출 → guidToPathname으로 실제 텍스처 파일명 역매핑
        private static readonly System.Text.RegularExpressions.Regex GuidRegex =
            new(@"guid:\s*([a-f0-9]{32})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // {r: 0.8, g: 0.6, b: 0.6, a: 1} 또는 줄바꿈 형식 파싱용
        private static readonly Regex ColorInlineRx = new Regex(
            @"\{r:\s*([\d.eE+-]+),\s*g:\s*([\d.eE+-]+),\s*b:\s*([\d.eE+-]+),\s*a:\s*([\d.eE+-]+)\}",
            RegexOptions.Compiled);

        private static MaterialTextures ParseMatFile(
            string matContent, string matName,
            Dictionary<string, string> guidToPathname,
            HashSet<string> allReferencedTextures = null)
        {
            var result = new MaterialTextures();
            bool foundTex = false;

            // ── 섹션 상태 머신 ──
            // section: "texenvs" | "floats" | "colors" | null
            string section      = null;
            string currentProp  = null;   // texenvs 안 현재 프로퍼티 키 (main/bump/emission)
            string pendingColorProp = null; // colors 섹션에서 값을 기다리는 프로퍼티명

            foreach (var rawLine in matContent.Split('\n'))
            {
                var line    = rawLine.TrimEnd('\r');
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                // ── 최상위 m_ShaderKeywords / m_CustomRenderQueue ──
                if (trimmed.StartsWith("m_ShaderKeywords:"))
                {
                    result.Keywords = trimmed.Substring("m_ShaderKeywords:".Length).Trim();
                    continue;
                }
                if (trimmed.StartsWith("m_CustomRenderQueue:"))
                {
                    if (int.TryParse(trimmed.Substring("m_CustomRenderQueue:".Length).Trim(), out var rq))
                        result.RenderQueue = rq;
                    continue;
                }

                // ── 섹션 전환 ──
                if (trimmed.StartsWith("m_TexEnvs:"))  { section = "texenvs"; currentProp = null; continue; }
                if (trimmed.StartsWith("m_Floats:"))   { section = "floats";  continue; }
                if (trimmed.StartsWith("m_Colors:"))   { section = "colors";  pendingColorProp = null; continue; }
                // 다른 최상위 키가 나오면 섹션 종료
                if (!line.StartsWith(" ") && !line.StartsWith("\t") && trimmed.Contains(":"))
                    section = null;

                if (section == null) continue;

                var propKey = trimmed.TrimStart('-', ' ');

                // ──────────────────────────
                // m_TexEnvs 섹션
                // ──────────────────────────
                if (section == "texenvs")
                {
                    if (propKey.StartsWith("_MainTex:"))          currentProp = "main";
                    else if (propKey.StartsWith("_BumpMap:") ||
                             propKey.StartsWith("_NormalMap:"))    currentProp = "bump";
                    else if (propKey.StartsWith("_EmissionMap:"))  currentProp = "emission";
                    else if (propKey.StartsWith("_") && propKey.Contains(":"))
                        currentProp = "other";

                    if (!trimmed.Contains("guid:")) continue;

                    var gm = GuidRegex.Match(trimmed);
                    if (!gm.Success) continue;

                    string texFilename = null;
                    if (guidToPathname.TryGetValue(gm.Groups[1].Value, out var texPathname))
                        texFilename = Path.GetFileName(texPathname);
                    if (texFilename == null) { currentProp = null; continue; }

                    // ★ 모든 텍스처 파일명 수집 (lilToon 포함)
                    allReferencedTextures?.Add(texFilename);

                    switch (currentProp)
                    {
                        case "main":     result.MainTex     = texFilename; foundTex = true; break;
                        case "bump":     result.BumpMap     = texFilename; break;
                        case "emission": result.EmissionMap = texFilename; break;
                        // "other": allReferencedTextures에만 추가, result 필드 없음
                    }
                    currentProp = null;
                    continue;
                }

                // ──────────────────────────
                // m_Floats 섹션: "- _PropName: 0.5"
                // ──────────────────────────
                if (section == "floats")
                {
                    // propKey = "_PropName: 0.5"
                    var colonIdx = propKey.IndexOf(':');
                    if (colonIdx <= 0) continue;
                    var key = propKey.Substring(0, colonIdx).Trim();
                    var val = propKey.Substring(colonIdx + 1).Trim();
                    if (key.StartsWith("_") && float.TryParse(val,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var fv))
                    {
                        result.Floats[key] = fv;
                    }
                    continue;
                }

                // ──────────────────────────
                // m_Colors 섹션
                // 형식 1 (inline): "- _Color: {r: 1, g: 1, b: 1, a: 1}"
                // 형식 2 (block):  "- _Color:\n    r: 1\n    g: 1\n    b: 1\n    a: 1"
                // ──────────────────────────
                if (section == "colors")
                {
                    // 새 컬러 프로퍼티 시작
                    if (propKey.StartsWith("_"))
                    {
                        var colonIdx = propKey.IndexOf(':');
                        if (colonIdx <= 0) continue;
                        var key   = propKey.Substring(0, colonIdx).Trim();
                        var rest  = propKey.Substring(colonIdx + 1).Trim();

                        // inline 형식
                        var cm = ColorInlineRx.Match(rest);
                        if (cm.Success)
                        {
                            result.Colors[key] = ParseColorMatch(cm);
                            pendingColorProp = null;
                        }
                        else
                        {
                            pendingColorProp = key; // block 형식 — 다음 줄에서 r/g/b/a 읽기
                        }
                        continue;
                    }

                    // block 형식 fallback: inline으로 합쳐서 파싱 시도
                    if (pendingColorProp != null && trimmed.Contains("{"))
                    {
                        var cm = ColorInlineRx.Match(trimmed);
                        if (cm.Success)
                        {
                            result.Colors[pendingColorProp] = ParseColorMatch(cm);
                            pendingColorProp = null;
                        }
                    }
                    continue;
                }
            }

            if (!foundTex)
            {
                int guidCount = 0, resolvedCount = 0;
                foreach (var line in matContent.Split('\n'))
                {
                    if (!line.Contains("guid:")) continue;
                    var m = GuidRegex.Match(line);
                    if (!m.Success) continue;
                    guidCount++;
                    if (guidToPathname.ContainsKey(m.Groups[1].Value)) resolvedCount++;
                }
                Debug.LogWarning($"[Parser] .mat '{matName}': 텍스처 GUID 없음 — " +
                                 $"GUID {guidCount}개 중 {resolvedCount}개만 패키지 내 존재.");
                // Colors/Floats는 있을 수 있으므로 null 반환하지 않음
                if (result.Colors.Count == 0 && result.Floats.Count == 0) return null;
            }

            return result;
        }

        private static Color ParseColorMatch(Match m)
        {
            float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r);
            float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var g);
            float.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var b);
            float.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var a);
            return new Color(r, g, b, a);
        }

        // ─── 에셋 타입 판별 ───
        // src-tauri/src/lib.rs infer_asset_type() 포팅
        private static void InferAssetType(ParseResult result)
        {
            var name = result.Filename.ToLowerInvariant();

            var avatarKeywords = new[] {
                "avatar", "character", "body", "base",
                "manuka", "moe", "shinano", "shio", "mao", "lumina", "shinra"
            };
            var clothingKeywords = new[] {
                "outfit", "cloth", "dress", "costume", "wear",
                "skirt", "shirt", "jacket", "coat", "pants", "uniform", "bikini"
            };
            var hairKeywords = new[] { "hair", "wig", "kami" };

            if (avatarKeywords.Any(k => name.Contains(k)))
            {
                result.DetectedType = "avatar";
                result.Confidence = 0.85f;
                // 대응 아바타 판별
                foreach (var k in new[] { "manuka", "moe", "shinano", "shio", "mao", "lumina", "shinra" })
                    if (name.Contains(k)) result.DetectedName = k;
            }
            else if (clothingKeywords.Any(k => name.Contains(k)))
            {
                result.DetectedType = "clothing";
                result.Confidence = 0.8f;
            }
            else if (hairKeywords.Any(k => name.Contains(k)))
            {
                result.DetectedType = "hair";
                result.Confidence = 0.8f;
            }
            else
            {
                result.DetectedType = "unknown";
                result.Confidence = 0.3f;
            }
        }

        private static bool IsTextureExtension(string ext) =>
            ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".psd" or ".exr";

        /// <summary>파일명 충돌 방지: 같은 이름이 있으면 _1, _2 ... 붙임</summary>
        private static string SafeFileName(string dir, string filename)
        {
            if (!File.Exists(Path.Combine(dir, filename))) return filename;
            var name = Path.GetFileNameWithoutExtension(filename);
            var ext  = Path.GetExtension(filename);
            for (int i = 1; i < 100; i++)
            {
                var candidate = $"{name}_{i}{ext}";
                if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
            }
            return filename;
        }

        private static async Task ExtractFbxFromZipAsync(ZipFile zip, string tempDir, ParseResult result)
        {
            foreach (ZipEntry entry in zip)
            {
                if (!entry.IsFile) continue;
                var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (ext == ".fbx")
                {
                    var outPath = Path.Combine(tempDir, Path.GetFileName(entry.Name));
                    using var entryStream = zip.GetInputStream(entry);
                    using var outStream = File.Create(outPath);
                    await entryStream.CopyToAsync(outStream);
                    result.ExtractedFbxPaths.Add(outPath);
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        // .prefab YAML 파싱 — SkinnedMeshRenderer 블렌드쉐이프 웨이트 추출
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Unity .prefab(YAML) 텍스트를 2패스로 파싱해
        /// SkinnedMeshRenderer별 GO이름 + 블렌드쉐이프 웨이트를 추출합니다.
        ///
        /// ── Unity YAML 구조 ──
        ///   --- !u!1 &1111          ← GameObject 블록 (type 1)
        ///   GameObject:
        ///     m_Name: Body
        ///     m_Component:
        ///     - component: {fileID: 2222}
        ///
        ///   --- !u!137 &2222        ← SkinnedMeshRenderer 블록 (type 137)
        ///   SkinnedMeshRenderer:
        ///     m_GameObject: {fileID: 1111}
        ///     m_Mesh: {fileID: 4300000, guid: abc123, type: 3}
        ///     m_BlendShapeWeights:
        ///     - 0
        ///     - 50
        ///     - 100
        ///
        /// ── 매칭 흐름 ──
        ///   1패스: fileID → (type, 내용) 수집
        ///   2패스: SMR.m_GameObject.fileID → GO.m_Name 역참조 → GoName 확정
        /// </summary>
        private static List<PrefabSmrData> ParsePrefabBlendShapes(
            string prefabYaml,
            Dictionary<string, float[]> outRotationMap = null,
            List<PrefabBoneModData> outBoneMods = null)
        {
            if (string.IsNullOrEmpty(prefabYaml)) return new List<PrefabSmrData>();

            // ── 1패스: 모든 블록을 fileID → 줄 목록으로 수집 ──
            var fileIdLines = new Dictionary<string, (int type, List<string> lines)>();

            string currentId   = null;
            int    currentType = 0;
            List<string> currentLines = null;

            var allLines = prefabYaml.Split('\n');
            var headerRx = new System.Text.RegularExpressions.Regex(
                @"^---\s+!u!(\d+)\s+&(-?\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

            foreach (var raw in allLines)
            {
                var line = raw.TrimEnd('\r');
                var m = headerRx.Match(line);
                if (m.Success)
                {
                    if (currentId != null)
                        fileIdLines[currentId] = (currentType, currentLines);

                    currentType  = int.Parse(m.Groups[1].Value);
                    currentId    = m.Groups[2].Value;
                    currentLines = new List<string>();
                    continue;
                }
                currentLines?.Add(line);
            }
            if (currentId != null)
                fileIdLines[currentId] = (currentType, currentLines);

            // ── 1패스 진단 ──
            var typeCounts = fileIdLines.GroupBy(kv => kv.Value.type)
                .ToDictionary(g => g.Key, g => g.Count());
            Debug.Log($"[Parser] 1패스 블록수: 전체={fileIdLines.Count} " +
                      string.Join(", ", typeCounts.OrderBy(kv => kv.Key)
                          .Select(kv => $"type{kv.Key}={kv.Value}")));

            // ── 2패스: GameObject(type=1) 이름 맵 구축 ──
            // fileID → m_Name
            var goNames = new Dictionary<string, string>();
            foreach (var kv in fileIdLines)
            {
                if (kv.Value.type != 1) continue;   // 1 = GameObject
                foreach (var l in kv.Value.lines)
                {
                    var t = l.TrimStart();
                    if (t.StartsWith("m_Name:"))
                    {
                        goNames[kv.Key] = t.Substring("m_Name:".Length).Trim();
                        break;
                    }
                }
            }

            // ── 2.5패스: stripped Transform의 m_CorrespondingSourceObject 역추적 ──
            // FBX fileID → GO 이름 매핑을 fileIDToRecycleName 없이 구성.
            //
            // 구조 (Unity stripped prefab):
            //   --- !u!4 &localFileId stripped
            //   Transform:
            //     m_CorrespondingSourceObject: {fileID: 6866332..., guid: FBX_GUID, type: 3}
            //     m_GameObject: {fileID: localGoFileId}
            //
            //   --- !u!1 &localGoFileId
            //   GameObject:
            //     m_Name: Foot.L
            //
            // → FBX fileID 6866332... → localGoFileId → "Foot.L"
            var fbxIdToName = new Dictionary<string, string>(); // FBX fileID → bone name
            var correspRx   = new System.Text.RegularExpressions.Regex(
                @"fileID:\s*(-?\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

            foreach (var kv in fileIdLines)
            {
                if (kv.Value.type != 4) continue; // Transform만

                string fbxFileId    = null;
                string goLocalFileId = null;

                foreach (var l in kv.Value.lines)
                {
                    var t = l.TrimStart();
                    if (fbxFileId == null && t.StartsWith("m_CorrespondingSourceObject:"))
                    {
                        var fm = correspRx.Match(t);
                        if (fm.Success && fm.Groups[1].Value != "0")
                            fbxFileId = fm.Groups[1].Value;
                    }
                    if (goLocalFileId == null && t.StartsWith("m_GameObject:"))
                    {
                        var gm = correspRx.Match(t);
                        if (gm.Success)
                            goLocalFileId = gm.Groups[1].Value;
                    }
                    if (fbxFileId != null && goLocalFileId != null) break;
                }

                if (fbxFileId != null && goLocalFileId != null
                    && goNames.TryGetValue(goLocalFileId, out var boneName)
                    && !string.IsNullOrEmpty(boneName))
                {
                    fbxIdToName.TryAdd(fbxFileId, boneName);
                }
            }
            Debug.Log($"[Parser] FBX fileID→이름 역추적: {fbxIdToName.Count}개" +
                      (fbxIdToName.Count > 0
                          ? $" (예: {string.Join(", ", fbxIdToName.Take(5).Select(kv => $"{kv.Key}→'{kv.Value}'"))})"
                          : ""));

            // ── 3패스: SkinnedMeshRenderer(type=137) 파싱 ──
            var result = new List<PrefabSmrData>();

            foreach (var kv in fileIdLines)
            {
                if (kv.Value.type != 137) continue;  // 137 = SkinnedMeshRenderer

                string goFileId   = null;
                string meshGuid   = null;
                var    weights    = new List<float>();
                bool   inWeights  = false;

                foreach (var l in kv.Value.lines)
                {
                    var t = l.TrimStart();

                    // m_GameObject 역참조
                    if (goFileId == null && t.StartsWith("m_GameObject:"))
                    {
                        var gm = System.Text.RegularExpressions.Regex.Match(t, @"fileID:\s*(-?\d+)");
                        if (gm.Success) goFileId = gm.Groups[1].Value;
                        continue;
                    }

                    // m_Mesh guid
                    if (meshGuid == null && t.StartsWith("m_Mesh:") && t.Contains("guid:"))
                    {
                        var gm = System.Text.RegularExpressions.Regex.Match(t, @"guid:\s*([0-9a-f]{32})");
                        if (gm.Success) meshGuid = gm.Groups[1].Value;
                        continue;
                    }

                    // m_BlendShapeWeights 섹션
                    if (t.StartsWith("m_BlendShapeWeights:"))
                    {
                        inWeights = true;
                        continue;
                    }

                    if (inWeights)
                    {
                        if (t.StartsWith("- "))
                        {
                            var valStr = t.Substring(2).Trim();
                            if (float.TryParse(valStr,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var val))
                                weights.Add(val);
                        }
                        else if (t.Length > 0 && !t.StartsWith("#"))
                        {
                            inWeights = false;
                        }
                    }
                }

                if (weights.Count == 0) continue;

                // GO 이름 역참조
                var goName = (goFileId != null && goNames.TryGetValue(goFileId, out var n)) ? n : null;

                result.Add(new PrefabSmrData
                {
                    GoName            = goName,
                    MeshGuid          = meshGuid,
                    BlendShapeWeights = weights.ToArray()
                });
            }

            // ── 4패스: PrefabInstance(type=1001) 스파스 블렌드쉐이프 파싱 ──
            // YAML 구조:
            //   PrefabInstance:
            //     m_Modification:
            //       m_Modifications:
            //       - target: {fileID: 4937543, guid: abc123, type: 3}
            //         propertyPath: m_BlendShapeWeights.Array.data[12]
            //         value: 100
            //
            // fileID 그룹별로 스파스 딕셔너리를 모아서 PrefabSmrData 생성.
            var bsIndexRx = new System.Text.RegularExpressions.Regex(
                @"m_BlendShapeWeights\.Array\.data\[(\d+)\]",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            var targetRx = new System.Text.RegularExpressions.Regex(
                @"fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-f]{32}))?",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            // fileId → quaternion (x,y,z,w) 수집용 (outRotationMap이 null이면 내부 임시 사용)
            var rotationMap = outRotationMap ?? new Dictionary<string, float[]>();

            foreach (var kv in fileIdLines)
            {
                if (kv.Value.type != 1001) continue;  // 1001 = PrefabInstance

                // {targetFileId → {bsIndex → value}}
                var sparseMap = new Dictionary<string, Dictionary<int, float>>();
                // {targetFileId → meshGuid}
                var guidMap   = new Dictionary<string, string>();

                string currentTargetFileId = null;
                int    pendingBsIndex      = -1;   // propertyPath에서 읽은 BS 인덱스 (value 대기 중)
                bool   pendingBsHandled    = false; // value: 를 이미 읽었는지
                bool   inModifications     = false;
                string pendingRotComp      = null;  // "x","y","z","w" — m_LocalRotation 파싱 중
                string pendingScaleComp    = null;  // "x","y","z" — m_LocalScale 파싱 중
                string pendingPosComp      = null;  // "x","y","z" — m_LocalPosition 파싱 중
                // fileId → PrefabBoneModData (scale/pos 누적용)
                var boneModMap = new Dictionary<string, PrefabBoneModData>();

                foreach (var l in kv.Value.lines)
                {
                    var t = l.TrimStart();

                    if (t.StartsWith("m_Modifications:"))
                    {
                        inModifications = true;
                        continue;
                    }
                    if (!inModifications) continue;

                    // 새 수정 항목 시작: "- target: {fileID: N, guid: G, type: 3}"
                    // 또는 들여쓰기 없는 "target:" (YAML 포맷 변형)
                    if (t.StartsWith("- target:") || t.StartsWith("target:"))
                    {
                        pendingBsIndex   = -1;
                        pendingBsHandled = false;
                        pendingRotComp   = null;
                        pendingScaleComp = null;
                        pendingPosComp   = null;
                        var tm = targetRx.Match(t);
                        if (tm.Success)
                        {
                            currentTargetFileId = tm.Groups[1].Value;
                            if (tm.Groups[2].Success)
                                guidMap[currentTargetFileId] = tm.Groups[2].Value;
                        }
                        continue;
                    }

                    if (currentTargetFileId == null) continue;

                    // propertyPath: m_BlendShapeWeights / m_LocalRotation / m_LocalScale / m_LocalPosition
                    if (t.StartsWith("propertyPath:"))
                    {
                        var path = t.Substring("propertyPath:".Length).Trim();
                        var bm = bsIndexRx.Match(path);
                        pendingBsIndex   = bm.Success ? int.Parse(bm.Groups[1].Value) : -1;
                        pendingBsHandled = false;
                        pendingRotComp   = null;
                        pendingScaleComp = null;
                        pendingPosComp   = null;

                        if (path.StartsWith("m_LocalRotation."))
                            pendingRotComp = path.Substring("m_LocalRotation.".Length).Trim();
                        else if (path.StartsWith("m_LocalScale."))
                            pendingScaleComp = path.Substring("m_LocalScale.".Length).Trim();
                        else if (path.StartsWith("m_LocalPosition."))
                            pendingPosComp = path.Substring("m_LocalPosition.".Length).Trim();
                        continue;
                    }

                    // value: N  — pendingBsIndex가 유효하고 아직 처리 안 된 경우만
                    if (t.StartsWith("value:") && !pendingBsHandled)
                    {
                        var valStr = t.Substring("value:".Length).Trim();
                        if (float.TryParse(valStr,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var val))
                        {
                            // 블렌드쉐이프 처리
                            if (pendingBsIndex >= 0)
                            {
                                pendingBsHandled = true;
                                if (val != 0f)
                                {
                                    if (!sparseMap.ContainsKey(currentTargetFileId))
                                        sparseMap[currentTargetFileId] = new Dictionary<int, float>();
                                    sparseMap[currentTargetFileId][pendingBsIndex] = val;
                                    Debug.Log($"[Parser] sparse BS 발견: fileID={currentTargetFileId} idx={pendingBsIndex} val={val}");
                                }
                            }
                            // m_LocalRotation 처리
                            else if (pendingRotComp != null)
                            {
                                pendingBsHandled = true;
                                if (!rotationMap.ContainsKey(currentTargetFileId))
                                    rotationMap[currentTargetFileId] = new float[4]; // [x,y,z,w]
                                var rot = rotationMap[currentTargetFileId];
                                switch (pendingRotComp)
                                {
                                    case "x": rot[0] = val; break;
                                    case "y": rot[1] = val; break;
                                    case "z": rot[2] = val; break;
                                    case "w": rot[3] = val; break;
                                }
                            }
                            // m_LocalScale 처리
                            else if (pendingScaleComp != null)
                            {
                                pendingBsHandled = true;
                                if (!boneModMap.ContainsKey(currentTargetFileId))
                                    boneModMap[currentTargetFileId] = new PrefabBoneModData();
                                var bmod = boneModMap[currentTargetFileId];
                                switch (pendingScaleComp)
                                {
                                    case "x": bmod.ScaleX = val; break;
                                    case "y": bmod.ScaleY = val; break;
                                    case "z": bmod.ScaleZ = val; break;
                                }
                            }
                            // m_LocalPosition 처리
                            else if (pendingPosComp != null)
                            {
                                pendingBsHandled = true;
                                if (!boneModMap.ContainsKey(currentTargetFileId))
                                    boneModMap[currentTargetFileId] = new PrefabBoneModData();
                                var bmod = boneModMap[currentTargetFileId];
                                switch (pendingPosComp)
                                {
                                    case "x": bmod.PosX = val; break;
                                    case "y": bmod.PosY = val; break;
                                    case "z": bmod.PosZ = val; break;
                                }
                            }
                        }
                        else if (pendingBsIndex >= 0)
                        {
                            Debug.LogWarning($"[Parser] sparse BS 값 파싱 실패: idx={pendingBsIndex} raw='{valStr}'");
                        }
                        // pendingBsIndex는 유지 — objectReference: 등이 와도 이미 처리됨
                        continue;
                    }

                    // objectReference: 는 같은 항목의 일부 — pendingBsIndex 리셋하지 않음
                    if (t.StartsWith("objectReference:")) continue;

                    // 새 "- target:" 이 아닌 다른 최상위 필드(들여쓰기 없음)가 오면 리셋
                    if (!l.StartsWith(" ") && !l.StartsWith("\t") && t.Length > 0 &&
                        !t.StartsWith("propertyPath:") && !t.StartsWith("value:"))
                    {
                        pendingBsIndex   = -1;
                        pendingBsHandled = false;
                        pendingRotComp   = null;
                        pendingScaleComp = null;
                        pendingPosComp   = null;
                    }
                }

                // boneModMap → outBoneMods 추가
                if (outBoneMods != null)
                {
                    foreach (var bm in boneModMap)
                    {
                        if (bm.Value.HasScale || bm.Value.HasPos)
                        {
                            guidMap.TryGetValue(bm.Key, out var bmGuid);
                            bm.Value.TargetFileId = bm.Key;
                            bm.Value.TargetGuid   = bmGuid;
                            outBoneMods.Add(bm.Value);
                        }
                    }
                    if (outBoneMods.Count > 0)
                        Debug.Log($"[Parser] 본 Transform 수정 {outBoneMods.Count}개 파싱 완료 " +
                                  $"(scale: {outBoneMods.Count(b => b.HasScale)}, pos: {outBoneMods.Count(b => b.HasPos)})");
                }

                // sparse 전체 카운트 진단
                int totalSparseEntries = sparseMap.Values.Sum(d => d.Count);
                if (totalSparseEntries == 0 && guidMap.Count > 0)
                    Debug.Log($"[Parser] sparse 파싱: fileID그룹={guidMap.Count}개, BS entries=0 (전부 0값)");
                else if (totalSparseEntries > 0)
                    Debug.Log($"[Parser] sparse 파싱: fileID그룹={sparseMap.Count}개, non-zero entries={totalSparseEntries}개");

                // 스파스 데이터가 0이 아닌 항목만 유의미하므로 필터링 후 PrefabSmrData 생성
                foreach (var sm in sparseMap)
                {
                    var nonZero = sm.Value.Where(p => p.Value != 0f)
                                         .ToDictionary(p => p.Key, p => p.Value);
                    if (nonZero.Count == 0) continue;

                    guidMap.TryGetValue(sm.Key, out var meshGuid);

                    // targetFileId가 stripped type 137 블록의 fileID와 일치하면 GO 이름 추출 시도
                    string sparseGoName = null;
                    if (fileIdLines.TryGetValue(sm.Key, out var smrBlock) && smrBlock.type == 137)
                    {
                        foreach (var bl in smrBlock.lines)
                        {
                            var bt = bl.TrimStart();
                            if (bt.StartsWith("m_GameObject:"))
                            {
                                var bgm = System.Text.RegularExpressions.Regex.Match(bt, @"fileID:\s*(-?\d+)");
                                if (bgm.Success && goNames.TryGetValue(bgm.Groups[1].Value, out var bgn))
                                    sparseGoName = bgn;
                                break;
                            }
                        }
                    }

                    result.Add(new PrefabSmrData
                    {
                        GoName         = sparseGoName,
                        MeshGuid       = meshGuid,
                        TargetFileId   = sm.Key,
                        SparseWeights  = nonZero
                    });
                }
            }

            Debug.Log($"[Parser] ParsePrefabBlendShapes: type137={result.Count(r => r.SparseWeights == null)}개, " +
                      $"type1001={result.Count(r => r.SparseWeights != null)}개");

            // ── 4.5패스: rotationMap의 fileID 키 → 본 이름 해결 (3단계 폴백) ──
            if (outRotationMap != null)
            {
                var goIdRx2 = new System.Text.RegularExpressions.Regex(
                    @"m_GameObject:\s*\{fileID:\s*(-?\d+)\}",
                    System.Text.RegularExpressions.RegexOptions.Compiled);

                // 진단: 목표 fileID가 fileIdLines에 있는지 확인
                var _targetIds = new[] { "6866332237670371181", "-8679921383154817045" };
                foreach (var tid in _targetIds)
                {
                    bool inLines = fileIdLines.ContainsKey(tid);
                    string blockType = inLines ? fileIdLines[tid].type.ToString() : "N/A";
                    bool inRotMap = outRotationMap.ContainsKey(tid);
                    Debug.Log($"[Parser-Diag] fileID={tid}: fileIdLines={inLines}(type={blockType}), rotMap={inRotMap}");
                }
                Debug.Log($"[Parser-Diag] fileIdLines 총 키: {fileIdLines.Count}, rotMap 키: {string.Join(", ", outRotationMap.Keys)}");

                int resolved = 0;
                foreach (var fileId in outRotationMap.Keys.ToList())
                {
                    if (fileId.StartsWith("__name__")) continue;

                    string boneName = null;

                    // 방법 1: m_CorrespondingSourceObject 역추적 맵 (stripped prefab)
                    fbxIdToName.TryGetValue(fileId, out boneName);

                    // 방법 2: 모델 프리팹에서 FBX fileID = 로컬 fileID
                    // → fileIdLines에 해당 fileID가 type=4(Transform)으로 직접 있음
                    if (boneName == null && fileIdLines.TryGetValue(fileId, out var block) && block.type == 4)
                    {
                        foreach (var bl in block.lines)
                        {
                            var bt = bl.TrimStart();
                            if (bt.StartsWith("m_GameObject:"))
                            {
                                var gm = goIdRx2.Match(bt);
                                if (gm.Success)
                                    goNames.TryGetValue(gm.Groups[1].Value, out boneName);
                                break;
                            }
                        }
                        if (boneName != null)
                            Debug.Log($"[Parser] 모델 프리팹 직접 해결: fileID={fileId} → '{boneName}'");
                    }

                    if (boneName == null) continue;

                    outRotationMap[$"__name__{boneName}"] = outRotationMap[fileId];
                    outRotationMap.Remove(fileId);
                    resolved++;
                    Debug.Log($"[Parser] fileID→이름 최종 해결: {fileId} → '{boneName}'");
                }
                Debug.Log($"[Parser] fileID 이름 해결 완료: {resolved}/{outRotationMap.Count + resolved}개");
            }

            // ── 5패스: type=4(Transform) 블록에서 non-identity m_LocalRotation 직접 추출 ──
            // 핵심: prefab에 type=4 Transform 블록이 있으면 GO이름 → rotation을 바로 얻을 수 있음
            // fileIDToRecycleName 없이도 동작하는 폴백 방식
            if (outRotationMap != null)
            {
                // inline quaternion: m_LocalRotation: {x: 0.5, y: 0, z: 0, w: 0.866}
                var inlineRotRx = new System.Text.RegularExpressions.Regex(
                    @"m_LocalRotation:\s*\{x:\s*([\d.eE+-]+),\s*y:\s*([\d.eE+-]+),\s*z:\s*([\d.eE+-]+),\s*w:\s*([\d.eE+-]+)\}",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                var goIdRx = new System.Text.RegularExpressions.Regex(
                    @"m_GameObject:\s*\{fileID:\s*(-?\d+)\}",
                    System.Text.RegularExpressions.RegexOptions.Compiled);

                int directRotFound = 0;
                foreach (var kv in fileIdLines)
                {
                    if (kv.Value.type != 4) continue; // 4 = Transform

                    string rotLine  = null;
                    string goIdLine = null;
                    foreach (var l in kv.Value.lines)
                    {
                        var t = l.TrimStart();
                        if (t.StartsWith("m_LocalRotation:")) rotLine  = t;
                        if (t.StartsWith("m_GameObject:"))   goIdLine = t;
                        if (rotLine != null && goIdLine != null) break;
                    }
                    if (rotLine == null || goIdLine == null) continue;

                    var rm = inlineRotRx.Match(rotLine);
                    if (!rm.Success) continue;

                    if (!float.TryParse(rm.Groups[1].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float rx)) continue;
                    if (!float.TryParse(rm.Groups[2].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float ry)) continue;
                    if (!float.TryParse(rm.Groups[3].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float rz)) continue;
                    if (!float.TryParse(rm.Groups[4].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float rw)) continue;

                    // identity에 가까우면 스킵 (w≈1이고 x,y,z≈0)
                    float angle = Quaternion.Angle(new Quaternion(rx, ry, rz, rw), Quaternion.identity);
                    if (angle < 1f) continue;

                    var gm = goIdRx.Match(goIdLine);
                    if (!gm.Success) continue;
                    string goId = gm.Groups[1].Value;
                    if (!goNames.TryGetValue(goId, out var boneName) || string.IsNullOrEmpty(boneName)) continue;

                    // boneName을 key로 직접 저장 (fileID 대신) — 1.6패스 resolve 불필요
                    // 구분: 이름이 숫자로만 이루어진 fileID와 충돌 없음
                    outRotationMap[$"__name__{boneName}"] = new float[] { rx, ry, rz, rw };
                    directRotFound++;
                    Debug.Log($"[Parser] Prefab Transform 직접 회전: '{boneName}' angle={angle:F1}° q=({rx:F3},{ry:F3},{rz:F3},{rw:F3})");
                }
                Debug.Log($"[Parser] type=4 Transform 직접 회전 발견: {directRotFound}개");
            }

            return result;
        }

        // ─── PhysBone 파싱 ───

        // 알려진 VRCPhysBone 스크립트 GUID (SDK 버전별)
        private static readonly System.Collections.Generic.HashSet<string> KnownPhysBoneGuids =
            new(System.StringComparer.OrdinalIgnoreCase)
        {
            "b5b91f6a99e085e45a3ac7e0f9d52b66",  // SDK3 Avatars 3.4.x
            "a19745fe6a28f2f4fbe2b87efbe1bdbc",  // SDK3 Avatars 3.5.x
            "c00b24272c19b4b42b36b561f27fef78",  // SDK3 Avatars 3.3.x
        };

        /// <summary>
        /// type=114 MonoBehaviour 블록이 VRCPhysBone인지 판별.
        /// GUID 화이트리스트 우선, 실패 시 속성 패턴으로 판별.
        /// </summary>
        private static bool IsVRCPhysBone(List<string> lines)
        {
            bool hasPull = false, hasSpring = false, hasRootTransform = false;
            foreach (var l in lines)
            {
                var t = l.TrimStart();
                if (t.StartsWith("m_Script:") && t.Contains("guid:"))
                {
                    var gm = System.Text.RegularExpressions.Regex.Match(t, @"guid:\s*([0-9a-f]{32})",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (gm.Success && KnownPhysBoneGuids.Contains(gm.Groups[1].Value))
                        return true;  // GUID 확정
                }
                if (t.StartsWith("pull:"))            hasPull = true;
                if (t.StartsWith("spring:"))          hasSpring = true;
                if (t.StartsWith("rootTransform:"))   hasRootTransform = true;
            }
            // GUID 미매칭 → 속성 패턴으로 fallback
            return hasPull && hasSpring && hasRootTransform;
        }

        /// <summary>
        /// fileID → 본 이름 역참조.
        /// type=1(GameObject) 직접 매핑 또는 type=4(Transform) 경유 해결.
        /// </summary>
        private static string ResolveFileIdToBoneName(
            string fileId,
            Dictionary<string, (int type, List<string> lines)> fileIdLines,
            Dictionary<string, string> goNames)
        {
            if (string.IsNullOrEmpty(fileId) || fileId == "0") return null;

            // type=1 GameObject 직접 매핑
            if (goNames.TryGetValue(fileId, out var name)) return name;

            // type=4 Transform 경유 → m_GameObject.fileID → GO 이름
            if (fileIdLines.TryGetValue(fileId, out var block) && block.type == 4)
            {
                foreach (var l in block.lines)
                {
                    var t = l.TrimStart();
                    if (t.StartsWith("m_GameObject:"))
                    {
                        var gm = System.Text.RegularExpressions.Regex.Match(t, @"fileID:\s*(-?\d+)");
                        if (gm.Success && goNames.TryGetValue(gm.Groups[1].Value, out var goName))
                            return goName;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// prefab YAML에서 m_IsActive: 0 인 GameObject 이름 목록 반환.
        /// type=1(GameObject) 블록에서 m_IsActive와 m_Name을 함께 파싱.
        /// </summary>
        // ────────────────────────────────────────────────────────────────
        // VRCAvatarDescriptor → FX AnimatorController → 기본 AnimationClip
        // → blendShape 커브 기본값 파싱 체인
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// prefab YAML에서 VRCAvatarDescriptor의 FX 레이어(type=4)
        /// AnimatorController GUID를 추출합니다.
        /// </summary>
        private static string ParseFxControllerGuid(string prefabYaml)
        {
            // type=114 (MonoBehaviour) 블록 중 baseAnimationLayers 필드를 가진 것 탐색
            var headerRx = new System.Text.RegularExpressions.Regex(
                @"^---\s+!u!(\d+)\s+&(-?\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            var guidRx = new System.Text.RegularExpressions.Regex(
                @"guid:\s*([0-9a-f]{32})",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            bool inMono = false, inBaseLayers = false;
            int  currentLayerType = -1;

            foreach (var raw in prefabYaml.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var hm = headerRx.Match(line);
                if (hm.Success)
                {
                    inMono       = hm.Groups[1].Value == "114";
                    inBaseLayers = false;
                    currentLayerType = -1;
                    continue;
                }
                if (!inMono) continue;

                var t = line.TrimStart();
                if (t.StartsWith("baseAnimationLayers:"))  { inBaseLayers = true; continue; }
                if (!inBaseLayers) continue;

                // 다른 최상위 필드가 오면 레이어 섹션 종료
                if (!line.StartsWith(" ") && !line.StartsWith("\t") &&
                    t.Length > 0 && !t.StartsWith("-") && !t.StartsWith("type:") &&
                    !t.StartsWith("animatorController:") && !t.StartsWith("isEnabled:"))
                {
                    inBaseLayers = false; continue;
                }

                if (t.StartsWith("type:"))
                {
                    int.TryParse(t.Substring("type:".Length).Trim(), out currentLayerType);
                    continue;
                }

                // FX 레이어 (type=4) animatorController guid 추출
                if (currentLayerType == 4 && t.StartsWith("animatorController:"))
                {
                    var gm = guidRx.Match(t);
                    if (gm.Success)
                    {
                        var fx = gm.Groups[1].Value;
                        Debug.Log($"[Parser] VRCAvatarDescriptor FX controller GUID={fx}");
                        return fx;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// AnimatorController YAML에서 모든 레이어의 기본 상태(DefaultState) AnimationClip GUID를 수집.
        /// VRC FX 컨트롤러는 레이어가 여러 개이고(Base/Gesture/Action/Foot 등),
        /// 힐 블렌드쉐이프 같은 것은 Base Layer가 아닌 별도 레이어에 있을 수 있음.
        /// BlendTree motion은 내부 첫 번째 child clip GUID를 사용.
        /// </summary>
        private static List<string> ParseAnimatorAllDefaultClipGuids(string controllerYaml)
        {
            var result = new List<string>();
            var headerRx = new System.Text.RegularExpressions.Regex(
                @"^---\s+!u!(\d+)\s+&(-?\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            var fileIdRx = new System.Text.RegularExpressions.Regex(
                @"fileID:\s*(-?\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
            var guidRx = new System.Text.RegularExpressions.Regex(
                @"guid:\s*([0-9a-f]{32})", System.Text.RegularExpressions.RegexOptions.Compiled);

            // 1패스: fileID → (type, lines) 맵 구축
            var blocks = new Dictionary<string, (int type, List<string> lines)>();
            string curId = null; int curType = 0; var curLines = new List<string>();

            foreach (var raw in controllerYaml.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var hm = headerRx.Match(line);
                if (hm.Success)
                {
                    if (curId != null) blocks[curId] = (curType, curLines);
                    curType  = int.Parse(hm.Groups[1].Value);
                    curId    = hm.Groups[2].Value;
                    curLines = new List<string>();
                    continue;
                }
                curLines?.Add(line);
            }
            if (curId != null) blocks[curId] = (curType, curLines);

            // AnimatorController (type=91) → 모든 레이어의 StateMachine fileID 수집
            var smFileIds = new List<string>();
            foreach (var kv in blocks)
            {
                if (kv.Value.type != 91) continue;
                bool inLayers = false;
                foreach (var l in kv.Value.lines)
                {
                    var t = l.TrimStart();
                    if (t.StartsWith("m_AnimatorLayers:")) { inLayers = true; continue; }
                    if (!inLayers) continue;
                    // 다른 최상위 필드가 오면 레이어 섹션 종료
                    if (!l.StartsWith(" ") && !l.StartsWith("\t") && t.Length > 0
                        && !t.StartsWith("-") && !t.StartsWith("m_Name:")
                        && !t.StartsWith("m_StateMachine:") && !t.StartsWith("m_Mask:")
                        && !t.StartsWith("m_Motions:") && !t.StartsWith("m_Behaviours:")
                        && !t.StartsWith("m_BlendingMode:") && !t.StartsWith("m_SyncedLayerIndex:")
                        && !t.StartsWith("m_DefaultWeight:") && !t.StartsWith("m_IKPass:")
                        && !t.StartsWith("m_SyncedLayerAffectsTiming:") && !t.StartsWith("m_Controller:"))
                    { inLayers = false; continue; }
                    if (t.StartsWith("m_StateMachine:"))
                    {
                        var fm = fileIdRx.Match(t);
                        if (fm.Success) smFileIds.Add(fm.Groups[1].Value);
                    }
                }
            }

            Debug.Log($"[Parser] FX AnimatorController: {smFileIds.Count}개 레이어 StateMachine 발견");

            // 각 StateMachine → DefaultState → Motion GUID 수집
            foreach (var smId in smFileIds)
            {
                if (!blocks.TryGetValue(smId, out var smBlock) || smBlock.type != 1107) continue;

                string defaultStateId = null;
                foreach (var l in smBlock.lines)
                {
                    var t = l.TrimStart();
                    if (t.StartsWith("m_DefaultState:"))
                    {
                        var fm = fileIdRx.Match(t);
                        if (fm.Success) { defaultStateId = fm.Groups[1].Value; break; }
                    }
                }
                if (defaultStateId == null) continue;

                if (!blocks.TryGetValue(defaultStateId, out var stateBlock) || stateBlock.type != 1102)
                    continue;

                foreach (var l in stateBlock.lines)
                {
                    var t = l.TrimStart();
                    if (!t.StartsWith("m_Motion:")) continue;

                    var gm = guidRx.Match(t);
                    if (gm.Success)
                    {
                        var clipGuid = gm.Groups[1].Value;
                        Debug.Log($"[Parser] FX 레이어 기본 클립 GUID={clipGuid}");
                        result.Add(clipGuid);
                    }
                    else
                    {
                        // BlendTree: fileID 비zero, guid 없음 → 내부 child 클립 탐색
                        var fm = fileIdRx.Match(t);
                        if (fm.Success && fm.Groups[1].Value != "0")
                        {
                            var btId = fm.Groups[1].Value;
                            if (blocks.TryGetValue(btId, out var btBlock))
                            {
                                bool inChildren = false;
                                foreach (var bl in btBlock.lines)
                                {
                                    var bt = bl.TrimStart();
                                    if (bt.StartsWith("m_Motions:")) { inChildren = true; continue; }
                                    if (!inChildren) continue;
                                    if (!bl.StartsWith(" ") && !bl.StartsWith("\t") && bt.Length > 0
                                        && !bt.StartsWith("-") && !bt.StartsWith("m_MotionTimeParameter:"))
                                    { inChildren = false; continue; }
                                    var cgm = guidRx.Match(bl);
                                    if (cgm.Success)
                                    {
                                        Debug.Log($"[Parser] FX BlendTree child 클립 GUID={cgm.Groups[1].Value}");
                                        result.Add(cgm.Groups[1].Value);
                                        break; // 첫 번째 child만
                                    }
                                }
                            }
                        }
                    }
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// AnimationClip YAML의 m_FloatCurves에서 blendShape 커브를 읽어
        /// GO 경로 끝 이름 → (blendshape 이름 → 값) 형태로 반환합니다.
        /// classID=137 (SkinnedMeshRenderer) 커브만 대상으로 합니다.
        /// </summary>
        private static Dictionary<string, Dictionary<string, float>> ParseAnimClipBlendShapes(string animYaml)
        {
            var result = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(animYaml)) return result;

            // m_FloatCurves 섹션을 라인 단위로 파싱
            // 각 커브 항목 구조:
            //   - curve: { ... m_Curve: [ {time:0, value:100, ...} ] }
            //     attribute: blendShape.NAME
            //     path: Body_base
            //     classID: 137
            bool inFloatCurves = false;
            float   curValue    = 0f;
            bool    hasValue    = false;
            string  curAttr     = null;
            string  curPath     = null;
            int     curClassId  = -1;
            bool    firstKey    = true;  // m_Curve의 첫 번째 keyframe만 사용

            foreach (var raw in animYaml.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var t    = line.TrimStart();

                if (t.StartsWith("m_FloatCurves:")) { inFloatCurves = true; continue; }
                if (!inFloatCurves) continue;

                // 다른 최상위 필드가 오면 섹션 종료
                if (!line.StartsWith(" ") && !line.StartsWith("\t") && t.Length > 0 &&
                    !t.StartsWith("-") && t.Contains(":") && !t.StartsWith("curve:") &&
                    !t.StartsWith("attribute:") && !t.StartsWith("path:") && !t.StartsWith("classID:"))
                {
                    inFloatCurves = false; continue;
                }

                // 새 커브 항목 시작 "- curve:"
                if (t.StartsWith("- curve:"))
                {
                    // 이전 커브 결과 저장
                    if (curAttr != null && curPath != null && curClassId == 137 &&
                        hasValue && curAttr.StartsWith("blendShape."))
                    {
                        var bsName = curAttr.Substring("blendShape.".Length);
                        var goName = curPath.Split('/').Last(); // 경로 끝 이름만
                        if (!result.ContainsKey(goName))
                            result[goName] = new Dictionary<string, float>(StringComparer.Ordinal);
                        if (curValue != 0f) // 0인 값은 기본값이므로 생략
                            result[goName][bsName] = curValue;
                    }
                    // 리셋
                    curAttr = curPath = null; curClassId = -1;
                    hasValue = false; curValue = 0f; firstKey = true;
                    continue;
                }

                // attribute: blendShape.NAME
                if (t.StartsWith("attribute:"))
                    curAttr = t.Substring("attribute:".Length).Trim();

                // path: Body_base (또는 Armature/Hips/Body_base 등)
                else if (t.StartsWith("path:"))
                    curPath = t.Substring("path:".Length).Trim();

                // classID: 137 → SkinnedMeshRenderer
                else if (t.StartsWith("classID:"))
                    int.TryParse(t.Substring("classID:".Length).Trim(), out curClassId);

                // keyframe value (첫 번째만 사용)
                else if (firstKey && t.StartsWith("value:") && curClassId == 137)
                {
                    var vs = t.Substring("value:".Length).Trim();
                    // "value: 100 {tangentMode:" 같은 뒤에 다른 내용이 올 수 있음
                    var sp = vs.IndexOf(' ');
                    if (sp > 0) vs = vs.Substring(0, sp);
                    if (float.TryParse(vs, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        curValue = v; hasValue = true; firstKey = false;
                    }
                }
            }

            // 마지막 커브 처리
            if (curAttr != null && curPath != null && curClassId == 137 &&
                hasValue && curAttr.StartsWith("blendShape.") && curValue != 0f)
            {
                var bsName = curAttr.Substring("blendShape.".Length);
                var goName = curPath.Split('/').Last();
                if (!result.ContainsKey(goName))
                    result[goName] = new Dictionary<string, float>(StringComparer.Ordinal);
                result[goName][bsName] = curValue;
            }

            return result;
        }

        /// <summary>
        /// AnimationClip YAML의 m_EulerCurves / m_RotationCurves에서
        /// Transform(classID=4) 본 회전 커브를 읽어 본 이름 → Euler 각도로 반환.
        /// 힐 각도 등 bone-driven 포즈 보정에 사용.
        /// </summary>
        private static Dictionary<string, BonePoseData> ParseAnimClipEulerCurves(string animYaml)
        {
            var result = new Dictionary<string, BonePoseData>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(animYaml)) return result;

            // m_EulerCurves 또는 m_EditorCurves 섹션 파싱
            // 각 항목 구조:
            //   - curve:
            //       m_Curve:
            //       - time: 0
            //         value: -30
            //         ...
            //     attribute: localEulerAnglesRaw.x   (또는 .y .z)
            //     path: Armature/Hips/.../Foot.L
            //     classID: 4

            bool inEulerCurves = false;
            float   curValue   = 0f;
            bool    hasValue   = false;
            string  curAttr    = null;
            string  curPath    = null;
            int     curClassId = -1;
            bool    firstKey   = true;

            foreach (var raw in animYaml.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var t    = line.TrimStart();

                // 섹션 시작
                if (t.StartsWith("m_EulerCurves:")) { inEulerCurves = true; continue; }

                // 다른 최상위 섹션 → 종료
                if (inEulerCurves && !line.StartsWith(" ") && !line.StartsWith("\t")
                    && t.Length > 0 && !t.StartsWith("-") && t.Contains(":"))
                { inEulerCurves = false; continue; }

                if (!inEulerCurves) continue;

                // 새 커브 항목 시작
                if (t.StartsWith("- curve:"))
                {
                    // 이전 커브 저장
                    if (curAttr != null && curPath != null && curClassId == 4 && hasValue)
                    {
                        var boneName = curPath.Split('/').Last();
                        if (!result.TryGetValue(boneName, out var bpd))
                            result[boneName] = bpd = new BonePoseData();

                        if (curAttr.EndsWith(".x", StringComparison.OrdinalIgnoreCase)) bpd.X = curValue;
                        else if (curAttr.EndsWith(".y", StringComparison.OrdinalIgnoreCase)) bpd.Y = curValue;
                        else if (curAttr.EndsWith(".z", StringComparison.OrdinalIgnoreCase)) bpd.Z = curValue;
                    }
                    curAttr = curPath = null; curClassId = -1; hasValue = false; curValue = 0f; firstKey = true;
                    continue;
                }

                if (t.StartsWith("attribute:"))
                    curAttr = t.Substring("attribute:".Length).Trim();
                else if (t.StartsWith("path:"))
                    curPath = t.Substring("path:".Length).Trim();
                else if (t.StartsWith("classID:"))
                    int.TryParse(t.Substring("classID:".Length).Trim(), out curClassId);
                else if (firstKey && t.StartsWith("value:") && curClassId == 4)
                {
                    var vs = t.Substring("value:".Length).Trim();
                    var sp = vs.IndexOf(' ');
                    if (sp > 0) vs = vs.Substring(0, sp);
                    if (float.TryParse(vs, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    { curValue = v; hasValue = true; firstKey = false; }
                }
            }

            // 마지막 커브 처리
            if (curAttr != null && curPath != null && curClassId == 4 && hasValue)
            {
                var boneName = curPath.Split('/').Last();
                if (!result.TryGetValue(boneName, out var bpd))
                    result[boneName] = bpd = new BonePoseData();
                if (curAttr.EndsWith(".x", StringComparison.OrdinalIgnoreCase)) bpd.X = curValue;
                else if (curAttr.EndsWith(".y", StringComparison.OrdinalIgnoreCase)) bpd.Y = curValue;
                else if (curAttr.EndsWith(".z", StringComparison.OrdinalIgnoreCase)) bpd.Z = curValue;
            }

            return result;
        }

        private static HashSet<string> ParsePrefabInactiveGoNames(string prefabYaml)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(prefabYaml)) return result;

            var headerRx = new System.Text.RegularExpressions.Regex(
                @"^---\s+!u!(\d+)\s+&(-?\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            int  currentType = 0;
            bool isGo        = false;
            string pendingName = null;
            bool   pendingInactive = false;

            foreach (var raw in prefabYaml.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = headerRx.Match(line);
                if (m.Success)
                {
                    // 이전 블록 결과 처리
                    if (isGo && pendingInactive && pendingName != null)
                        result.Add(pendingName);

                    currentType    = int.Parse(m.Groups[1].Value);
                    isGo           = (currentType == 1);
                    pendingName    = null;
                    pendingInactive = false;
                    continue;
                }

                if (!isGo) continue;

                var t = line.TrimStart();
                if (t.StartsWith("m_Name:"))
                    pendingName = t.Substring("m_Name:".Length).Trim();
                else if (t.StartsWith("m_IsActive:"))
                {
                    var valStr = t.Substring("m_IsActive:".Length).Trim();
                    pendingInactive = (valStr == "0");
                }
            }

            // 마지막 블록
            if (isGo && pendingInactive && pendingName != null)
                result.Add(pendingName);

            return result;
        }

        /// prefab YAML에서 VRCPhysBone 컴포넌트(type=114)를 파싱해 PhysBoneData 목록 반환.
        /// </summary>
        private static List<PhysBoneData> ParsePrefabPhysBones(string prefabYaml)
        {
            var result = new List<PhysBoneData>();

            // 1패스: fileID → (type, lines) 맵 구축
            var fileIdLines = new Dictionary<string, (int type, List<string> lines)>();
            string currentId = null; int currentType = 0; List<string> currentLines = null;
            var headerRx = new System.Text.RegularExpressions.Regex(
                @"^---\s+!u!(\d+)\s+&(-?\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            foreach (var raw in prefabYaml.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = headerRx.Match(line);
                if (m.Success)
                {
                    if (currentId != null) fileIdLines[currentId] = (currentType, currentLines);
                    currentType  = int.Parse(m.Groups[1].Value);
                    currentId    = m.Groups[2].Value;
                    currentLines = new List<string>();
                    continue;
                }
                currentLines?.Add(line);
            }
            if (currentId != null) fileIdLines[currentId] = (currentType, currentLines);

            // 2패스: GO 이름 맵
            var goNames = new Dictionary<string, string>();
            foreach (var kv in fileIdLines)
            {
                if (kv.Value.type != 1) continue;
                foreach (var l in kv.Value.lines)
                {
                    var t = l.TrimStart();
                    if (t.StartsWith("m_Name:"))
                    { goNames[kv.Key] = t.Substring("m_Name:".Length).Trim(); break; }
                }
            }

            // 3패스: type=114 MonoBehaviour → VRCPhysBone 판별 및 파싱
            var ignoreFileRx = new System.Text.RegularExpressions.Regex(
                @"fileID:\s*(-?\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

            foreach (var kv in fileIdLines)
            {
                if (kv.Value.type != 114) continue;
                if (!IsVRCPhysBone(kv.Value.lines)) continue;

                var pb = new PhysBoneData();
                bool inIgnore = false;

                foreach (var l in kv.Value.lines)
                {
                    var t = l.TrimStart();

                    // rootTransform → 본 이름 역참조
                    if (t.StartsWith("rootTransform:"))
                    {
                        var gm = ignoreFileRx.Match(t);
                        if (gm.Success)
                        {
                            var fid = gm.Groups[1].Value;
                            if (fid == "0")
                            {
                                // fileID:0 = self → 이 MonoBehaviour의 m_GameObject 역참조
                                foreach (var bl in kv.Value.lines)
                                {
                                    var bt = bl.TrimStart();
                                    if (bt.StartsWith("m_GameObject:"))
                                    {
                                        var bgm = ignoreFileRx.Match(bt);
                                        if (bgm.Success)
                                            pb.RootBoneName = ResolveFileIdToBoneName(bgm.Groups[1].Value, fileIdLines, goNames);
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                pb.RootBoneName = ResolveFileIdToBoneName(fid, fileIdLines, goNames);
                            }
                        }
                        inIgnore = false;
                        continue;
                    }

                    // ignoreTransforms 섹션
                    if (t.StartsWith("ignoreTransforms:")) { inIgnore = true; continue; }
                    if (inIgnore)
                    {
                        if (t.StartsWith("- {fileID:") || t.StartsWith("-{fileID:"))
                        {
                            var gm = ignoreFileRx.Match(t);
                            if (gm.Success)
                            {
                                var boneName = ResolveFileIdToBoneName(gm.Groups[1].Value, fileIdLines, goNames);
                                if (!string.IsNullOrEmpty(boneName))
                                    pb.IgnoreBoneNames.Add(boneName);
                            }
                            continue;
                        }
                        // 다른 섹션 시작이면 종료
                        if (!t.StartsWith("-") && t.Contains(":")) inIgnore = false;
                    }

                    // float 파라미터 파싱
                    TryParseFloat(t, "pull:",           ref pb.Pull);
                    TryParseFloat(t, "spring:",         ref pb.Spring);
                    TryParseFloat(t, "stiffness:",      ref pb.Stiffness);
                    TryParseFloat(t, "gravity:",        ref pb.Gravity);
                    TryParseFloat(t, "gravityFalloff:", ref pb.GravityFalloff);
                    TryParseFloat(t, "immobile:",       ref pb.Immobile);
                    TryParseFloat(t, "radius:",         ref pb.Radius);
                }

                if (!string.IsNullOrEmpty(pb.RootBoneName))
                    result.Add(pb);
                // rootBone이 외부 FBX Transform을 참조하면 fileID 역참조 불가 — 정상 동작, 로그 생략
            }

            return result;
        }

        private static void TryParseFloat(string trimmedLine, string prefix, ref float target)
        {
            if (!trimmedLine.StartsWith(prefix)) return;
            var valStr = trimmedLine.Substring(prefix.Length).Trim();
            // 커브 참조 등 비숫자 제거 ("0 {curveType..." 형태)
            var spaceIdx = valStr.IndexOf(' ');
            if (spaceIdx > 0) valStr = valStr.Substring(0, spaceIdx);
            if (float.TryParse(valStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
                target = v;
        }
    }
}
