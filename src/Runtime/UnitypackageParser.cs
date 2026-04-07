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
        public Dictionary<string, MaterialTextures> MaterialTextureMap = new();
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
        // src-tauri/src/lib.rs parse_unitypackage() 포팅
        public static async Task<ParseResult> ParseUnitypackageAsync(string packagePath)
        {
            var tempDir = Path.Combine(
                Path.GetTempPath(),
                "virtual-dresser",
                Path.GetRandomFileName()
            );
            Directory.CreateDirectory(tempDir);

            var result = new ParseResult
            {
                Filename = Path.GetFileName(packagePath),
                TempDirPath = tempDir
            };

            // GUID → pathname 매핑 구축
            var guidToPathname = new Dictionary<string, string>();
            var guidToAssetData = new Dictionary<string, byte[]>();
            var matFiles = new Dictionary<string, string>(); // guid → .mat 텍스트

            // tar.gz 해제
            using var fileStream = File.OpenRead(packagePath);
            using var gzStream = new GZipInputStream(fileStream);
            using var tar = TarArchive.CreateInputTarArchive(gzStream, Encoding.UTF8);

            // 1차 패스: pathname 수집
            var tarEntries = ReadAllTarEntries(packagePath);

            foreach (var (entryName, data) in tarEntries)
            {
                var parts = entryName.Split('/');
                if (parts.Length < 2) continue;

                var guid = parts[0];
                var filename = parts[parts.Length - 1];

                if (filename == "pathname")
                {
                    guidToPathname[guid] = Encoding.UTF8.GetString(data).Trim();
                }
                else if (filename == "asset")
                {
                    guidToAssetData[guid] = data;
                }
                else if (filename.EndsWith(".mat") || entryName.Contains(".mat"))
                {
                    matFiles[guid] = Encoding.UTF8.GetString(data);
                }
            }

            result.TotalEntries = guidToPathname.Count;

            // 2차 패스: FBX / 텍스처 추출
            var fbxBySize = new List<(string path, long size)>();

            foreach (var (guid, pathname) in guidToPathname)
            {
                var ext = Path.GetExtension(pathname).ToLowerInvariant();

                if (!guidToAssetData.TryGetValue(guid, out var assetData)) continue;

                if (ext == ".fbx")
                {
                    var outPath = Path.Combine(tempDir, Path.GetFileName(pathname));
                    await File.WriteAllBytesAsync(outPath, assetData);
                    fbxBySize.Add((outPath, assetData.Length));
                }
                else if (IsTextureExtension(ext))
                {
                    var outPath = Path.Combine(tempDir, Path.GetFileName(pathname));
                    await File.WriteAllBytesAsync(outPath, assetData);
                    result.ExtractedTextureNames.Add(Path.GetFileName(pathname));
                }
                else if (ext == ".mat" && matFiles.ContainsKey(guid))
                {
                    var matResult = ParseMatFile(matFiles[guid], Path.GetFileNameWithoutExtension(pathname));
                    if (matResult != null)
                        result.MaterialTextureMap[Path.GetFileNameWithoutExtension(pathname)] = matResult;
                }
            }

            // FBX를 크기 내림차순 정렬 (가장 큰 것 = 메인 아바타/의상)
            fbxBySize.Sort((a, b) => b.size.CompareTo(a.size));
            result.ExtractedFbxPaths = fbxBySize.Select(x => x.path).ToList();

            // 에셋 타입 자동 판별
            InferAssetType(result);

            Debug.Log($"[Parser] {result.Filename}: FBX {result.ExtractedFbxPaths.Count}개, " +
                      $"텍스처 {result.ExtractedTextureNames.Count}개, " +
                      $"타입={result.DetectedType}({result.Confidence:F2})");

            return result;
        }

        // ─── .mat 파일 파싱 ───
        // src-tauri/src/lib.rs parse_mat_file() 포팅
        private static MaterialTextures ParseMatFile(string matContent, string matName)
        {
            var result = new MaterialTextures();
            bool found = false;

            foreach (var line in matContent.Split('\n'))
            {
                var trimmed = line.Trim();

                // "_MainTex: {fileID: 0, guid: xxxx, type: 3}" 패턴
                if (trimmed.StartsWith("_MainTex:") && trimmed.Contains("guid:"))
                {
                    // NOTE: guid → 텍스처 파일명 역매핑 필요
                    // 현재는 matName 기반 휴리스틱 fallback
                    result.MainTex = matName + ".png"; // placeholder
                    found = true;
                }
                else if (trimmed.StartsWith("_BumpMap:") && trimmed.Contains("guid:"))
                {
                    result.BumpMap = matName + "_normal.png";
                }
                else if (trimmed.StartsWith("_EmissionMap:") && trimmed.Contains("guid:"))
                {
                    result.EmissionMap = matName + "_emission.png";
                }
            }

            return found ? result : null;
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

        // NOTE: 실제 구현에서는 스트리밍 방식으로 tar 읽기 필요
        private static List<(string name, byte[] data)> ReadAllTarEntries(string packagePath)
        {
            var entries = new List<(string, byte[])>();
            using var fs = File.OpenRead(packagePath);
            using var gz = new GZipInputStream(fs);
            using var tar = new TarInputStream(gz, Encoding.UTF8);

            TarEntry entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                if (entry.IsDirectory) continue;
                using var ms = new MemoryStream();
                tar.CopyEntryContents(ms);
                entries.Add((entry.Name, ms.ToArray()));
            }
            return entries;
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
