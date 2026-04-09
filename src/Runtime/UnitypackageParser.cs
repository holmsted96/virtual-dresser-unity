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
    }

    public class MaterialTextures
    {
        public string MainTex;
        public string BumpMap;
        public string EmissionMap;
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
            var (guidToPathname, guidToTempAsset) =
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
            Dictionary<string, string> guidToTempAsset)
            StreamTarToDisk(string packagePath, string tempDir)
        {
            var guidToPathname  = new Dictionary<string, string>();
            var guidToTempAsset = new Dictionary<string, string>();

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
                    // asset.meta는 불필요 → 스킵
                    tar.CopyEntryContents(Stream.Null);
                }
                else
                {
                    // preview 등 그 외 항목 스킵 (스트림 소비 필수)
                    tar.CopyEntryContents(Stream.Null);
                }
            }

            return (guidToPathname, guidToTempAsset);
        }

        // ─── .mat 파일 파싱 ───
        // .mat YAML에서 GUID 추출 → guidToPathname으로 실제 텍스처 파일명 역매핑
        private static readonly System.Text.RegularExpressions.Regex GuidRegex =
            new(@"guid:\s*([a-f0-9]{32})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static MaterialTextures ParseMatFile(
            string matContent, string matName,
            Dictionary<string, string> guidToPathname)
        {
            var result = new MaterialTextures();
            bool found = false;

            string currentProp = null;
            foreach (var line in matContent.Split('\n'))
            {
                var trimmed = line.Trim();

                // ★ .mat YAML 구조: "    - _MainTex:" → trim → "- _MainTex:"
                //   리스트 항목 접두사 "- " 를 제거해야 StartsWith 가 올바르게 동작
                var propKey = trimmed.TrimStart('-', ' ');

                // 프로퍼티 키 감지
                if (propKey.StartsWith("_MainTex:"))          currentProp = "main";
                else if (propKey.StartsWith("_BumpMap:") ||
                         propKey.StartsWith("_NormalMap:"))    currentProp = "bump";
                else if (propKey.StartsWith("_EmissionMap:"))  currentProp = "emission";
                else if (propKey.StartsWith("_") && propKey.Contains(":"))
                    currentProp = null; // 다른 프로퍼티 → 리셋

                if (currentProp == null) continue;
                if (!trimmed.Contains("guid:")) continue;

                var match = GuidRegex.Match(trimmed);
                if (!match.Success) continue;

                var texGuid = match.Groups[1].Value;
                // GUID → 실제 pathname 역매핑
                string texFilename = null;
                if (guidToPathname.TryGetValue(texGuid, out var texPathname))
                    texFilename = Path.GetFileName(texPathname);

                if (texFilename == null) continue; // guid=0 또는 패키지 외부 텍스처

                switch (currentProp)
                {
                    case "main":
                        result.MainTex = texFilename;
                        found = true;
                        break;
                    case "bump":
                        result.BumpMap = texFilename;
                        break;
                    case "emission":
                        result.EmissionMap = texFilename;
                        break;
                }
                currentProp = null;
            }

            if (!found)
            {
                // 진단: 실패 원인 상세 출력
                int guidCount = 0;
                int resolvedCount = 0;
                foreach (var line in matContent.Split('\n'))
                {
                    if (!line.Contains("guid:")) continue;
                    var m = GuidRegex.Match(line);
                    if (!m.Success) continue;
                    guidCount++;
                    if (guidToPathname.ContainsKey(m.Groups[1].Value)) resolvedCount++;
                    else Debug.LogWarning($"[Parser] .mat '{matName}': 외부 GUID 참조 → {m.Groups[1].Value} (패키지 내 없음)");
                }
                Debug.LogWarning($"[Parser] .mat '{matName}': GUID 매핑 실패 — " +
                                 $"GUID 참조 {guidCount}개 중 {resolvedCount}개만 이 패키지 내 존재. " +
                                 "텍스처가 별도 패키지로 분리됐거나 외부 참조일 가능성.");
                return null;
            }

            return result;
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
    }
}
