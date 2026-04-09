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
            var (guidToPathname, guidToTempAsset, guidToTempMeta) =
                await Task.Run(() => StreamTarToDisk(packagePath, tempDir));

            result.TotalEntries = guidToPathname.Count;

            // ── 1패스: .mat 파일을 temp 파일에서 읽어 파싱 ──
            // (.mat asset은 텍스트 YAML이므로 ReadAllText 가능)
            foreach (var (guid, pathname) in guidToPathname)
            {
                if (!Path.GetExtension(pathname).Equals(".mat", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!guidToTempAsset.TryGetValue(guid, out var tmpPath)) continue;

                try
                {
                    var matText = File.ReadAllText(tmpPath);
                    var matResult = ParseMatFile(matText,
                        Path.GetFileNameWithoutExtension(pathname),
                        guidToPathname);
                    if (matResult != null)
                        result.MaterialTextureMap[Path.GetFileNameWithoutExtension(pathname)] = matResult;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Parser] .mat 읽기 실패: {pathname} — {ex.Message}");
                }
                finally
                {
                    try { File.Delete(tmpPath); } catch { }
                    guidToTempAsset.Remove(guid);
                }
            }

            Debug.Log($"[Parser] .mat 파싱 완료: {result.MaterialTextureMap.Count}개");

            // ── 1.2패스: .prefab 파일 파싱 → SkinnedMeshRenderer 블렌드쉐이프 웨이트 추출 ──
            var prefabPaths = guidToPathname
                .Where(kv => Path.GetExtension(kv.Value)
                    .Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Debug.Log($"[Parser] .prefab 파일 {prefabPaths.Count}개 발견: " +
                      string.Join(", ", prefabPaths.Select(kv => Path.GetFileName(kv.Value))));

            foreach (var (guid, pathname) in prefabPaths)
            {
                if (!guidToTempAsset.TryGetValue(guid, out var tmpPath))
                {
                    Debug.LogWarning($"[Parser] .prefab temp 파일 없음 (이미 삭제?): {pathname}");
                    continue;
                }

                try
                {
                    var prefabText = File.ReadAllText(tmpPath);
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

                    var smrDataList = ParsePrefabBlendShapes(prefabText);
                    result.PrefabSmrDataList.AddRange(smrDataList);

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
                        // 블렌드쉐이프가 있어야 하는데 파싱 결과가 0개인 경우만 경고
                        Debug.LogWarning($"[Parser] .prefab 파싱 결과 0개 (블렌드쉐이프 있음): {pathname} — " +
                                         $"hasSMR={hasSMR}, hasPrefabInst={hasPrefabInst}, " +
                                         $"hasWeightsDense={hasWeightsDense}, hasWeightsSparse={hasWeightsSparse}");
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
                    try { File.Delete(tmpPath); } catch { }
                    guidToTempAsset.Remove(guid);
                }
            }

            // ── 1.5패스: FBX .meta 파싱 → externalObjects로 FBX 내부 mat명 → .mat 파일명 매핑 ──
            Debug.Log($"[Parser] meta 파일 저장 수: {guidToTempMeta.Count}개");
            foreach (var (guid, pathname) in guidToPathname)
            {
                if (!Path.GetExtension(pathname).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                    continue;

                Debug.Log($"[Parser] FBX 발견: {Path.GetFileName(pathname)}, meta 있음={guidToTempMeta.ContainsKey(guid)}");
                if (!guidToTempMeta.TryGetValue(guid, out var metaPath)) continue;

                try
                {
                    var metaText = File.ReadAllText(metaPath);
                    // externalObjects 섹션 존재 여부 진단
                    bool hasExtObj = metaText.Contains("externalObjects:");
                    Debug.Log($"[Parser] {Path.GetFileName(pathname)}.meta: {metaText.Length}자, externalObjects={hasExtObj}");
                    ParseFbxMeta(metaText, guidToPathname, result.FbxMaterialMap);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Parser] FBX meta 읽기 실패: {pathname} — {ex.Message}");
                }
                finally
                {
                    try { File.Delete(metaPath); } catch { }
                    guidToTempMeta.Remove(guid);
                }
            }

            // 남은 meta 파일 정리
            foreach (var mp in guidToTempMeta.Values)
                try { if (File.Exists(mp)) File.Delete(mp); } catch { }

            if (result.FbxMaterialMap.Count > 0)
                Debug.Log($"[Parser] FBX externalObjects 매핑: {result.FbxMaterialMap.Count}개 " +
                          $"(예: {string.Join(", ", result.FbxMaterialMap.Take(3).Select(kv => $"'{kv.Key}'→'{kv.Value}'"))})");

            // ── 2패스: FBX + 텍스처 temp 파일을 최종 위치로 이동 ──
            var fbxBySize = new List<(string path, long size)>();

            foreach (var (guid, pathname) in guidToPathname)
            {
                var ext = Path.GetExtension(pathname).ToLowerInvariant();
                if (!guidToTempAsset.TryGetValue(guid, out var tmpPath)) continue;

                // ★ 속도 개선: FBX/텍스처가 아니면 temp 파일 즉시 삭제 (이동 불필요)
                bool needed = ext == ".fbx" || IsTextureExtension(ext);
                if (!needed)
                {
                    try { File.Delete(tmpPath); } catch { }
                    continue;
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

            // 남은 임시 asset 파일 정리
            foreach (var tmp in guidToTempAsset.Values)
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            fbxBySize.Sort((a, b) => b.size.CompareTo(a.size));
            result.ExtractedFbxPaths = fbxBySize.Select(x => x.path).ToList();

            InferAssetType(result);

            Debug.Log($"[Parser] {result.Filename}: FBX {result.ExtractedFbxPaths.Count}개, " +
                      $"텍스처 {result.ExtractedTextureNames.Count}개, " +
                      $"타입={result.DetectedType}({result.Confidence:F2})");

            return result;
        }

        /// <summary>
        /// tar.gz를 단일 패스로 스트리밍하면서 asset을 임시 파일로 직접 저장.
        /// RAM에 에셋 데이터를 올리지 않음.
        /// </summary>
        private static (
            Dictionary<string, string> guidToPathname,
            Dictionary<string, string> guidToTempAsset,
            Dictionary<string, string> guidToTempMeta)
            StreamTarToDisk(string packagePath, string tempDir)
        {
            var guidToPathname  = new Dictionary<string, string>();
            var guidToTempAsset = new Dictionary<string, string>();
            var guidToTempMeta  = new Dictionary<string, string>();

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
                    // pathname은 소형 텍스트 → 메모리로 읽기
                    using var ms = new MemoryStream((int)Math.Min(entry.Size, 4096));
                    tar.CopyEntryContents(ms);
                    guidToPathname[guid] = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                }
                else if (filename == "asset")
                {
                    // ★ 속도 개선: pathname이 이미 수집된 경우 불필요한 파일은 스킵
                    // tar는 보통 pathname → asset 순이 아닐 수 있으므로 일단 크기로 판단
                    // 10MB 이하 소형 파일은 무조건 저장 (mat/meta 포함), 대형은 저장
                    // → 실제 필터링은 2패스에서 pathname 보고 결정
                    var tmpPath = Path.Combine(tempDir, guid + "_asset.tmp");
                    using var outFile = File.Create(tmpPath);
                    tar.CopyEntryContents(outFile);
                    guidToTempAsset[guid] = tmpPath;
                }
                else if (filename == "asset.meta")
                {
                    // FBX meta에 externalObjects(머티리얼 매핑) 정보가 있으므로 저장
                    var metaPath = Path.Combine(tempDir, guid + "_meta.tmp");
                    using var outMeta = File.Create(metaPath);
                    tar.CopyEntryContents(outMeta);
                    guidToTempMeta[guid] = metaPath;
                }
                else
                {
                    // preview 등 그 외 항목 스킵 (스트림 소비 필수)
                    tar.CopyEntryContents(Stream.Null);
                }
            }

            return (guidToPathname, guidToTempAsset, guidToTempMeta);
        }

        // ─── FBX .meta 파싱 (externalObjects: FBX 내부 머티리얼명 → 외부 .mat GUID) ───
        private static void ParseFbxMeta(
            string metaText,
            Dictionary<string, string> guidToPathname,
            Dictionary<string, string> fbxMaterialMap)
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
            Dictionary<string, string> guidToPathname)
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
                        currentProp = null;

                    if (currentProp == null) continue;
                    if (!trimmed.Contains("guid:")) continue;

                    var gm = GuidRegex.Match(trimmed);
                    if (!gm.Success) continue;

                    string texFilename = null;
                    if (guidToPathname.TryGetValue(gm.Groups[1].Value, out var texPathname))
                        texFilename = Path.GetFileName(texPathname);
                    if (texFilename == null) continue;

                    switch (currentProp)
                    {
                        case "main":     result.MainTex     = texFilename; foundTex = true; break;
                        case "bump":     result.BumpMap     = texFilename; break;
                        case "emission": result.EmissionMap = texFilename; break;
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
        private static List<PrefabSmrData> ParsePrefabBlendShapes(string prefabYaml)
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

            foreach (var kv in fileIdLines)
            {
                if (kv.Value.type != 1001) continue;  // 1001 = PrefabInstance

                // {targetFileId → {bsIndex → value}}
                var sparseMap = new Dictionary<string, Dictionary<int, float>>();
                // {targetFileId → meshGuid}
                var guidMap   = new Dictionary<string, string>();

                string currentTargetFileId = null;
                int    pendingBsIndex      = -1;   // propertyPath에서 읽은 BS 인덱스 (value 대기 중)
                bool   inModifications     = false;

                foreach (var l in kv.Value.lines)
                {
                    var t = l.TrimStart();

                    if (t.StartsWith("m_Modifications:"))
                    {
                        inModifications = true;
                        continue;
                    }
                    if (!inModifications) continue;

                    // 새 수정 항목: "- target: {fileID: N, guid: G, type: 3}"
                    if (t.StartsWith("- target:"))
                    {
                        pendingBsIndex = -1;   // 이전 항목 리셋
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

                    // propertyPath: m_BlendShapeWeights.Array.data[N]
                    if (t.StartsWith("propertyPath:"))
                    {
                        var bm = bsIndexRx.Match(t);
                        pendingBsIndex = bm.Success ? int.Parse(bm.Groups[1].Value) : -1;
                        continue;
                    }

                    // value: N  — pendingBsIndex가 유효할 때만 처리
                    if (t.StartsWith("value:") && pendingBsIndex >= 0)
                    {
                        var valStr = t.Substring("value:".Length).Trim();
                        if (float.TryParse(valStr,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var val) && val != 0f)
                        {
                            if (!sparseMap.ContainsKey(currentTargetFileId))
                                sparseMap[currentTargetFileId] = new Dictionary<int, float>();
                            sparseMap[currentTargetFileId][pendingBsIndex] = val;
                        }
                        pendingBsIndex = -1;
                        continue;
                    }

                    // objectReference 등 다른 필드는 pendingBsIndex 리셋
                    if (!t.StartsWith("value:") && !t.StartsWith("propertyPath:") &&
                        !t.StartsWith("- target:") && t.Length > 0)
                    {
                        pendingBsIndex = -1;
                    }
                }


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

            return result;
        }
    }
}
