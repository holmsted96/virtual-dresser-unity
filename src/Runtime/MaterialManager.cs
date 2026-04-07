// MaterialManager.cs
// material-manager.ts Unity C# 포팅
//
// Three.js MeshStandardMaterial → Unity Material (lilToon 셰이더)
// ParseResult의 임시 폴더에서 텍스처를 로드하여 SkinnedMeshRenderer에 적용
//
// 재활용률: 약 70% (텍스처 매핑 전략 동일, Three.js → Unity API 교체)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualDresser.Runtime
{
    public static class MaterialManager
    {
        // ─── lilToon 셰이더 프로퍼티 → Unity 텍스처 슬롯 매핑 ───
        // material-manager.ts LILTOON_TEXTURE_MAP 포팅
        private static readonly Dictionary<string, string> LilToonTextureMap = new()
        {
            ["_MainTex"]              = "_MainTex",
            ["_BumpMap"]              = "_BumpMap",
            ["_EmissionMap"]          = "_EmissionMap",
            ["_MainColorAdjustMask"]  = "_ShadowColorTex",
        };

        // ─── 파일명 패턴 → 셰이더 프로퍼티 추론 ───
        // material-manager.ts TEXTURE_NAME_PATTERNS 포팅
        private static readonly (Regex pattern, string property)[] TextureNamePatterns =
        {
            (new Regex(@"diffuse|diff|_d\b|_col|_albedo|_maintex|_base",   RegexOptions.IgnoreCase), "_MainTex"),
            (new Regex(@"normal|norm|_n\b|_nrm|_bumpmap",                  RegexOptions.IgnoreCase), "_BumpMap"),
            (new Regex(@"emiss|_e\b|_emission|_glow",                      RegexOptions.IgnoreCase), "_EmissionMap"),
            (new Regex(@"shadow|_shadow|shadetex",                         RegexOptions.IgnoreCase), "_ShadowColorTex"),
        };

        // ─── 텍스처 캐시 (같은 파일 중복 로드 방지) ───
        private static readonly Dictionary<string, Texture2D> TextureCache = new();

        // ─── 메인 진입점 ───

        // 프리뷰용 최대 텍스처 크기 (OOM 방지)
        private const int MaxPreviewSize = 1024;

        /// <summary>
        /// ParseResult를 바탕으로 GameObject의 모든 SkinnedMeshRenderer에 텍스처 적용.
        /// 기존 TriLib 머티리얼에 텍스처만 덮어씌움 (새 머티리얼 생성 안 함).
        /// </summary>
        // ─── lilToon 셰이더 캐시 ───
        private static Shader _shaderLilToon;
        private static Shader _shaderLilToonCutout;
        private static Shader _shaderLilToonTransparent;

        private static Shader GetLilToonShader(string meshName)
        {
            _shaderLilToon            ??= Shader.Find("lilToon");
            _shaderLilToonCutout      ??= Shader.Find("Hidden/lilToonCutout");
            _shaderLilToonTransparent ??= Shader.Find("Hidden/lilToonTransparent");

            var lower = meshName.ToLowerInvariant();

            // 헤어: alpha cutout
            if (lower.Contains("hair") || lower.Contains("wig") || lower.Contains("kami")
                || lower.Contains("eyelash") || lower.Contains("lash"))
                return _shaderLilToonCutout ?? _shaderLilToon;

            // 눈: transparent
            if (lower.Contains("eye") || lower.Contains("iris") || lower.Contains("pupil"))
                return _shaderLilToonTransparent ?? _shaderLilToon;

            return _shaderLilToon;
        }

        public static async Task ApplyTexturesAsync(GameObject go, ParseResult parseResult)
        {
            if (go == null || parseResult == null) return;

            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return;

            // 텍스처 파일 목록 (임시 폴더, 1024 이하로 리사이즈)
            var textures = await LoadAllTexturesAsync(parseResult.TempDirPath,
                parseResult.ExtractedTextureNames);

            if (textures.Count == 0)
            {
                Debug.LogWarning("[MaterialManager] 텍스처 없음 — 임시 폴더 확인 필요");
                return;
            }

            Debug.Log($"[MaterialManager] 로드된 텍스처 {textures.Count}장");

            int applied = 0;
            foreach (var smr in renderers)
            {
                var mat = smr.sharedMaterial;
                if (mat == null) continue;

                // ── lilToon 셰이더로 교체 ──
                var lilShader = GetLilToonShader(smr.name);
                if (lilShader != null && mat.shader != lilShader)
                {
                    mat.shader = lilShader;
                    // lilToon 기본값: 양면 렌더링 끄기, 알파 클리핑 활성화
                    if (mat.HasProperty("_Cutoff"))
                        mat.SetFloat("_Cutoff", 0.5f);
                }
                else if (lilShader == null)
                {
                    Debug.LogWarning("[MaterialManager] lilToon 셰이더를 찾을 수 없음 — URP/Lit 폴백 사용");
                }

                // 매칭 키: 머티리얼 이름 우선, 없으면 메시 이름
                var matchKey = !string.IsNullOrEmpty(mat.name) ? mat.name : smr.name;
                var matched = ApplyTexturesToMaterial(mat, matchKey, textures, parseResult);
                if (matched) applied++;
            }

            Debug.Log($"[MaterialManager] {applied}/{renderers.Length}개 메쉬에 lilToon + 텍스처 적용 완료");
        }

        private static bool ApplyTexturesToMaterial(
            Material mat, string matchKey,
            Dictionary<string, Texture2D> textures, ParseResult parseResult)
        {
            bool applied = false;

            // ── 1. .mat 파싱 결과 우선 ──
            if (parseResult.MaterialTextureMap.TryGetValue(matchKey, out var matInfo))
            {
                if (matInfo.MainTex != null && textures.TryGetValue(matInfo.MainTex, out var t))
                { SetMainTex(mat, t); applied = true; }
                if (matInfo.BumpMap != null && textures.TryGetValue(matInfo.BumpMap, out var b))
                    mat.SetTexture("_BumpMap", b);
                if (matInfo.EmissionMap != null && textures.TryGetValue(matInfo.EmissionMap, out var e))
                    mat.SetTexture("_EmissionMap", e);
                return applied;
            }

            // ── 2. 텍스처 파일명 × matchKey 유사도 매칭 ──
            Texture2D bestMain = null;
            int bestScore = -1;

            foreach (var (texName, tex) in textures)
            {
                var prop = ResolveProperty(texName, matchKey);

                // 노말/이미션/마스크/섀도우는 전용 슬롯에 적용하고 MainTex 후보에서 제외
                if (prop is "_BumpMap" or "_EmissionMap")
                {
                    mat.SetTexture(prop, tex);
                    continue;
                }
                if (IsNonDiffuseTexture(texName)) continue;

                var score = SimilarityScore(texName, matchKey);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMain  = tex;
                }
            }

            if (bestMain != null)
            {
                SetMainTex(mat, bestMain);
                applied = true;
            }

            return applied;
        }

        /// <summary>
        /// 노말맵, 마스크, 섀도우 등 MainTex 후보에서 제외할 파일명 판별.
        /// </summary>
        private static bool IsNonDiffuseTexture(string texName)
        {
            var lower = Path.GetFileNameWithoutExtension(texName).ToLowerInvariant();
            return lower.EndsWith("_normal") || lower.EndsWith("_nrm")
                || lower.Contains("_normal_") || lower.Contains("shadow")
                || lower.Contains("_mask") || lower.Contains("mask_")
                || lower.Contains("emission_mask") || lower.Contains("lim_mask")
                || lower.Contains("color_mask") || lower.Contains("alpha_mask")
                || lower.Contains("outline_mask");
        }

        /// <summary>
        /// 텍스처 파일명과 머티리얼/메시 이름 간 간단한 유사도 점수.
        /// 공통 토큰 수 기반.
        /// </summary>
        private static int SimilarityScore(string texName, string matName)
        {
            var texLower = Path.GetFileNameWithoutExtension(texName).ToLowerInvariant();
            var matLower = matName.ToLowerInvariant();

            // 완전 포함
            if (texLower.Contains(matLower) || matLower.Contains(texLower)) return 100;

            // 토큰 공통 수
            var texTokens = System.Text.RegularExpressions.Regex.Split(texLower, @"[\s_\-\.]+");
            var matTokens = System.Text.RegularExpressions.Regex.Split(matLower, @"[\s_\-\.]+");
            int common = 0;
            foreach (var t in texTokens)
                if (t.Length > 1 && matLower.Contains(t)) common++;
            return common;
        }

        // ─── 텍스처 로드 ───

        /// <summary>
        /// 임시 폴더의 텍스처 파일들을 Texture2D로 비동기 로드.
        /// material-manager.ts loadTexture() 포팅
        /// </summary>
        private static async Task<Dictionary<string, Texture2D>> LoadAllTexturesAsync(
            string tempDir, List<string> textureNames)
        {
            var result = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in textureNames)
            {
                var path = Path.Combine(tempDir, name);
                if (!File.Exists(path)) continue;

                if (TextureCache.TryGetValue(path, out var cached))
                {
                    result[name] = cached;
                    continue;
                }

                var tex = await LoadTextureFromFileAsync(path);
                if (tex != null)
                {
                    tex.name = name;
                    TextureCache[path] = tex;
                    result[name] = tex;
                }
            }

            return result;
        }

        /// <summary>
        /// 로컬 파일에서 Texture2D 로드. MaxPreviewSize 초과 시 다운샘플.
        /// File.ReadAllBytes + LoadImage 방식으로 OOM 위험 최소화.
        /// </summary>
        private static async Task<Texture2D> LoadTextureFromFileAsync(string filePath)
        {
            byte[] bytes;
            try
            {
                bytes = await Task.Run(() => File.ReadAllBytes(filePath));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaterialManager] 파일 읽기 실패: {filePath} — {ex.Message}");
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes, false))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            bytes = null; // GC 해제

            // MaxPreviewSize 초과 시 RenderTexture로 다운샘플
            if (tex.width > MaxPreviewSize || tex.height > MaxPreviewSize)
            {
                var scale  = (float)MaxPreviewSize / Mathf.Max(tex.width, tex.height);
                var tw     = Mathf.RoundToInt(tex.width  * scale);
                var th     = Mathf.RoundToInt(tex.height * scale);
                var rt     = RenderTexture.GetTemporary(tw, th, 0);
                Graphics.Blit(tex, rt);
                var prev   = RenderTexture.active;
                RenderTexture.active = rt;
                var small  = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                small.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                small.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(tex);
                return small;
            }

            tex.Apply();
            return tex;
        }

        // ─── 유틸리티 ───

        /// <summary>
        /// 파일명과 메쉬명을 조합해 셰이더 프로퍼티 추론.
        /// material-manager.ts resolveMapType() 포팅
        /// </summary>
        private static string ResolveProperty(string texName, string meshName)
        {
            var lower = texName.ToLowerInvariant();

            // 파일명 패턴 매칭
            foreach (var (pattern, prop) in TextureNamePatterns)
                if (pattern.IsMatch(lower)) return prop;

            // 메쉬 이름이 파일명에 포함되면 MainTex로 간주
            if (!string.IsNullOrEmpty(meshName) &&
                lower.Contains(meshName.ToLowerInvariant()))
                return "_MainTex";

            return null;
        }

        /// <summary>
        /// lilToon은 _MainTex만 사용, URP/Lit 폴백은 _BaseMap + _MainTex 모두 세팅.
        /// </summary>
        private static void SetMainTex(Material mat, Texture2D tex)
        {
            mat.SetTexture("_MainTex", tex);
            // URP/Lit 폴백용 (lilToon에는 없지만 세팅해도 무해)
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", tex);
        }

        /// <summary>
        /// 텍스처 캐시 초기화 (새 에셋 로드 시 호출).
        /// material-manager.ts cleanupBlobUrls() 상당
        /// </summary>
        public static void ClearCache()
        {
            foreach (var tex in TextureCache.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            TextureCache.Clear();
        }
    }
}
