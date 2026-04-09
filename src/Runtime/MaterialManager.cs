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
using System.Linq;
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
        private static bool   _shaderSearchDone;

        /// <summary>
        /// lilToon 셰이더 버전별 이름 변형을 순차 탐색.
        /// v1.x: "lilToon", "Hidden/lilToonCutout", "Hidden/lilToonTransparent"
        /// v2.x+: "lilToon/lilToon", "lilToon/lilToon Cutout" etc.
        /// </summary>
        private static void EnsureShaders()
        {
            if (_shaderSearchDone) return;
            _shaderSearchDone = true;

            _shaderLilToon =
                Shader.Find("lilToon") ??
                Shader.Find("lilToon/lilToon") ??
                Shader.Find("_lil/lilToon");

            _shaderLilToonCutout =
                Shader.Find("Hidden/lilToonCutout") ??
                Shader.Find("lilToon/lilToon Cutout") ??
                Shader.Find("_lil/lilToon Cutout") ??
                _shaderLilToon;

            _shaderLilToonTransparent =
                Shader.Find("Hidden/lilToonTransparent") ??
                Shader.Find("lilToon/lilToon Transparent") ??
                Shader.Find("_lil/lilToon Transparent") ??
                _shaderLilToon;

            if (_shaderLilToon == null)
                Debug.LogWarning("[MaterialManager] lilToon 셰이더를 찾을 수 없음. lilToon 패키지가 임포트되었는지 확인하세요.");
            else
                Debug.Log($"[MaterialManager] lilToon 셰이더 탐지: {_shaderLilToon.name}");
        }

        private static Shader GetLilToonShader(string meshName)
        {
            EnsureShaders();
            var lower = meshName.ToLowerInvariant();

            // 눈/아이리스/동공: transparent (헤어보다 먼저 체크 — Eye_Hair 같은 경우 방지)
            // 단, "eyelash"는 cutout이므로 제외
            if ((lower.Contains("eye") || lower.Contains("iris") || lower.Contains("pupil"))
                && !lower.Contains("eyelash") && !lower.Contains("lash"))
                return _shaderLilToonTransparent;

            // 헤어/속눈썹: alpha cutout
            if (lower.Contains("hair") || lower.Contains("wig") || lower.Contains("kami")
                || lower.Contains("eyelash") || lower.Contains("lash"))
                return _shaderLilToonCutout;

            // Alpha/transparent 접미사 → transparent
            if (lower.EndsWith("_alpha") || lower.EndsWith("alpha") || lower.Contains("_trans")
                || lower.Contains("tights") || lower.Contains("stocking"))
                return _shaderLilToonTransparent;

            // 그 외(얼굴, 몸통, 의상 등): opaque
            return _shaderLilToon;
        }

        /// <summary>
        /// 셰이더 교체 후 lilToon 필수 기본값 초기화.
        /// _Color 미초기화 시 메시가 검게 렌더링되는 문제 방지.
        /// </summary>
        private static void InitLilToonDefaults(Material mat, Shader shader)
        {
            if (shader == null) return;

            // ─ 기본 색상: 반드시 white (lilToon은 MainTex × _Color 로 최종 색 결정) ─
            if (mat.HasProperty("_Color"))      mat.SetColor("_Color",      Color.white);
            if (mat.HasProperty("_MainColor"))  mat.SetColor("_MainColor",  Color.white); // v2+
            if (mat.HasProperty("_Color2nd"))   mat.SetColor("_Color2nd",   Color.white);
            if (mat.HasProperty("_Color3rd"))   mat.SetColor("_Color3rd",   Color.white);

            // ─ UV 스케일 기본값 ─
            if (mat.HasProperty("_MainTex"))
            {
                mat.SetTextureScale("_MainTex",  Vector2.one);
                mat.SetTextureOffset("_MainTex", Vector2.zero);
            }

            // ─ 최소 조도 (0이면 그림자 영역이 완전 검정) ─
            if (mat.HasProperty("_LightMinLimit")) mat.SetFloat("_LightMinLimit", 0.05f);

            // ─ 변형별 렌더 설정 ─
            var sname = shader.name;
            if (sname.Contains("Cutout") || sname.Contains("cutout"))
            {
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.5f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = 2450;
            }
            else if (sname.Contains("Transparent") || sname.Contains("transparent"))
            {
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.renderQueue = 3000;
                if (mat.HasProperty("_SrcBlend"))  mat.SetFloat("_SrcBlend",  (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (mat.HasProperty("_DstBlend"))  mat.SetFloat("_DstBlend",  (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (mat.HasProperty("_ZWrite"))    mat.SetFloat("_ZWrite", 0f);
            }
            else
            {
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = 2000;
                // ZWrite 활성화 (투명/반투명 설정이 남아있으면 opaque가 배경으로 렌더됨)
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
            }
        }

        public static async Task ApplyTexturesAsync(GameObject go, ParseResult parseResult, AvatarConfig config = null)
        {
            if (go == null || parseResult == null) return;

            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return;

            // ★ 씬 ambient 조도 확보 (기본값이 0이면 lilToon도 검정으로 보임)
            if (RenderSettings.ambientLight.r < 0.1f)
                RenderSettings.ambientLight = new Color(0.4f, 0.4f, 0.4f);

            // 텍스처 파일 목록 (임시 폴더, 1024 이하로 리사이즈)
            var textures = await LoadAllTexturesAsync(parseResult.TempDirPath,
                parseResult.ExtractedTextureNames);

            if (textures.Count == 0)
            {
                Debug.LogWarning("[MaterialManager] 텍스처 없음 — 임시 폴더 확인 필요");
                return;
            }

            Debug.Log($"[MaterialManager] 로드된 텍스처 {textures.Count}장");

            // 진단: 추출된 텍스처 이름 목록 (전체 출력)
            Debug.Log($"[MatMgr] 텍스처 파일 {textures.Count}장: " +
                      string.Join(", ", textures.Keys));
            Debug.Log($"[MatMgr] .mat 맵 키 {parseResult.MaterialTextureMap.Count}개 (전체): " +
                      string.Join(", ", parseResult.MaterialTextureMap.Keys));
            if (parseResult.FbxMaterialMap.Count > 0)
                Debug.Log($"[MatMgr] FbxMaterialMap {parseResult.FbxMaterialMap.Count}개: " +
                          string.Join(", ", parseResult.FbxMaterialMap.Select(kv => $"'{kv.Key}'→'{kv.Value}'")));

            int applied = 0;
            int totalMats = 0;
            foreach (var smr in renderers)
            {
                var mats = smr.sharedMaterials; // 멀티-머티리얼 지원
                if (mats == null || mats.Length == 0) continue;

                for (int mi = 0; mi < mats.Length; mi++)
                {
                    var mat = mats[mi];
                    if (mat == null) continue;
                    totalMats++;

                    // ── 셰이더 교체 (lilToon이 있을 때만) ──
                    // mat.name이 더 구체적이므로 우선 사용, fallback으로 smr.name
                    var shaderHint = !string.IsNullOrEmpty(mat.name) ? mat.name : smr.name;
                    var lilShader = GetLilToonShader(shaderHint);
                    if (lilShader != null)
                    {
                        if (mat.shader != lilShader)
                            mat.shader = lilShader;
                        InitLilToonDefaults(mat, lilShader);
                    }
                    // lilToon 없으면 TriLib이 설정한 셰이더 그대로 사용
                    // (교체 시 Standalone에서 셰이더 strip으로 메시 완전 비가시 발생)

                    // _Color 기본값: .mat에서 읽은 값으로 덮어씌워지므로 여기선 보장만
                    // (InitLilToonDefaults에서 이미 white 초기화됨)

                    // 매칭 키 결정 (우선순위 순)
                    var rawKey   = !string.IsNullOrEmpty(mat.name) ? mat.name : smr.name;
                    var matchKey = rawKey;

                    // 0단계: config.materialConfig.smrToMat — SMR 이름으로 직접 매핑 (최우선)
                    var matCfg = config?.materialConfig;
                    if (matCfg?.smrToMat != null &&
                        matCfg.smrToMat.TryGetValue(smr.name, out var smrMapped))
                    {
                        matchKey = smrMapped;
                    }
                    // 1단계: config.materialConfig.matNameToMat — FBX mat 이름 변환
                    else if (matCfg?.matNameToMat != null &&
                             matCfg.matNameToMat.TryGetValue(rawKey, out var matMapped))
                    {
                        matchKey = matMapped;
                    }
                    // 2단계: FbxMaterialMap (externalObjects 기반)
                    else if (parseResult.FbxMaterialMap.TryGetValue(rawKey, out var fbxMapped))
                    {
                        matchKey = fbxMapped;
                    }

                    Debug.Log($"[MatMgr] 처리: SMR='{smr.name}'[{mi}] → mat='{mat.name}' → matchKey='{matchKey}'" +
                              (matchKey != rawKey ? $" (변환됨)" : ""));
                    var matched = ApplyTexturesToMaterial(mat, matchKey, textures, parseResult);

                    // fallback 1: SMR 이름으로 재시도 (FBX mat 이름과 .mat 파일명이 완전히 다를 때)
                    if (!matched && smr.name != matchKey)
                        matched = ApplyTexturesToMaterial(mat, smr.name, textures, parseResult);

                    // fallback 2: SMR 이름의 prefix(Cloth_, Hair_ 등)로 .mat 맵 키 유사도 조회
                    if (!matched)
                    {
                        var smrBase = smr.name.Split('_')[0]; // "Cloth_Bra" → "Cloth"
                        if (smrBase.Length >= 3)
                        {
                            var bestKey = parseResult.MaterialTextureMap.Keys
                                .FirstOrDefault(k => k.StartsWith(smrBase, StringComparison.OrdinalIgnoreCase));
                            if (bestKey != null)
                                matched = ApplyTexturesToMaterial(mat, bestKey, textures, parseResult);
                        }
                    }

                    if (matched)
                        applied++;
                    else
                    {
                        // 텍스처 매칭 완전 실패 → _LightMinLimit 높여서 최소한 흰색으로 보이게
                        if (mat.HasProperty("_LightMinLimit")) mat.SetFloat("_LightMinLimit", 0.7f);
                    }
                }
            }

            Debug.Log($"[MaterialManager] {applied}/{totalMats}개 머티리얼에 lilToon + 텍스처 적용 완료");

            // 진단: SMR별 최종 텍스처 적용 상태 요약 (멀티-머티리얼 포함)
            var summaryParts = renderers.Select(r =>
            {
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) return $"{r.name}(NO MATS)";
                var texInfos = string.Join("|", mats.Select(m =>
                    m == null ? "null" : (m.mainTexture?.name ?? "NO TEX")));
                return $"{r.name}[{mats.Length}]({texInfos})";
            }).ToArray();
            Debug.Log($"[MatMgr] 적용 요약: {string.Join(", ", summaryParts)}");

            // NO TEX 목록만 별도 출력 (누락 SMR 빠른 진단용)
            var noTex = renderers
                .Where(r => r.sharedMaterials?.Any(m => m != null && m.mainTexture == null) == true)
                .Select(r => r.name)
                .ToArray();
            if (noTex.Length > 0)
                Debug.LogWarning($"[MatMgr] 텍스처 없는 SMR ({noTex.Length}개): {string.Join(", ", noTex)}");
        }

        private static bool ApplyTexturesToMaterial(
            Material mat, string matchKey,
            Dictionary<string, Texture2D> textures, ParseResult parseResult)
        {
            bool applied = false;

            // ── 1. .mat 파싱 결과 우선 (대소문자 무시 dict) ──
            var matInfo = FindMatInfo(parseResult.MaterialTextureMap, matchKey);
            if (matInfo != null)
            {
                if (matInfo.MainTex != null && textures.TryGetValue(matInfo.MainTex, out var t))
                { SetMainTex(mat, t); applied = true; }
                else if (matInfo.MainTex != null)
                    Debug.LogWarning($"[MatMgr] GUID매핑 성공 but 텍스처 없음: '{matchKey}' → '{matInfo.MainTex}'");

                if (matInfo.BumpMap != null && textures.TryGetValue(matInfo.BumpMap, out var b))
                    mat.SetTexture("_BumpMap", b);
                if (matInfo.EmissionMap != null && textures.TryGetValue(matInfo.EmissionMap, out var e))
                    mat.SetTexture("_EmissionMap", e);

                // ── .mat 프로퍼티 전체 적용 (Colors / Floats / Keywords / RenderQueue) ──
                ApplyMatProperties(mat, matInfo);

                if (!applied)
                    Debug.LogWarning($"[MatMgr] .mat 있으나 텍스처 미적용: '{matchKey}' / MainTex={matInfo.MainTex}");
                return applied;
            }

            // ── 2. 텍스처 파일명 × matchKey 유사도 매칭 ──
            Debug.Log($"[MatMgr] GUID매핑 없음 → 유사도 매칭: '{matchKey}'  (맵 키 수={parseResult.MaterialTextureMap.Count})");

            Texture2D bestMain = null;
            int bestScore = -1;
            string bestTexName = null;

            foreach (var (texName, tex) in textures)
            {
                var prop = ResolveProperty(texName, matchKey);
                if (prop is "_BumpMap" or "_EmissionMap")
                { mat.SetTexture(prop, tex); continue; }
                if (IsNonDiffuseTexture(texName)) continue;

                var score = SimilarityScore(texName, matchKey);
                if (score > bestScore) { bestScore = score; bestMain = tex; bestTexName = texName; }
            }

            if (bestMain != null && bestScore > 0)
            {
                SetMainTex(mat, bestMain);
                applied = true;
                Debug.Log($"[MatMgr] 유사도 매칭: '{matchKey}' → '{bestTexName}' (점수={bestScore})");
            }
            else
            {
                Debug.LogWarning($"[MatMgr] 매칭 실패: '{matchKey}' (텍스처 {textures.Count}장 중 점수>{bestScore} 없음)");
                // ── 진단: 맵 키와 텍스처 목록 출력 ──
                if (parseResult.MaterialTextureMap.Count > 0)
                    Debug.Log($"[MatMgr]   .mat 맵 키: {string.Join(", ", parseResult.MaterialTextureMap.Keys.Take(5))}");
                Debug.Log($"[MatMgr]   텍스처 목록: {string.Join(", ", textures.Keys.Take(8))}");
            }

            return applied;
        }

        /// <summary>
        /// MaterialTextureMap에서 matchKey를 찾되, 없으면 prefix 제거 후 재시도.
        /// FBX 머티리얼 이름(TriLib) ≠ .mat 파일명인 경우 대응.
        /// </summary>
        /// <summary>
        /// .mat 파일에서 파싱한 Colors/Floats/Keywords/RenderQueue를 머티리얼에 적용.
        /// InitLilToonDefaults 이후에 호출되므로 원본 값이 기본값을 올바르게 덮어씀.
        /// </summary>
        private static void ApplyMatProperties(Material mat, MaterialTextures info)
        {
            // Colors — _Color, _OutlineColor, _ShadowColor 등
            foreach (var kv in info.Colors)
            {
                if (mat.HasProperty(kv.Key))
                    mat.SetColor(kv.Key, kv.Value);
            }

            // Floats — _OutlineWidth, _LightMinLimit, _Cutoff, _TransparentMode 등
            foreach (var kv in info.Floats)
            {
                if (mat.HasProperty(kv.Key))
                    mat.SetFloat(kv.Key, kv.Value);
            }

            // ShaderKeywords
            if (!string.IsNullOrEmpty(info.Keywords))
            {
                foreach (var kw in info.Keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    mat.EnableKeyword(kw);
            }

            // RenderQueue
            if (info.RenderQueue >= 0)
                mat.renderQueue = info.RenderQueue;
        }

        private static MaterialTextures FindMatInfo(
            Dictionary<string, MaterialTextures> map, string matchKey)
        {
            if (map.Count == 0) return null;

            // 1) 직접 일치 (dict가 OrdinalIgnoreCase이므로 대소문자 무시)
            if (map.TryGetValue(matchKey, out var info)) return info;

            // 2) 공통 prefix 제거 후 재시도 (FBX_ / MTL_ / M_ 등)
            var stripped = StripMaterialSuffix(
                System.Text.RegularExpressions.Regex.Replace(
                    matchKey, @"^(FBX_|MTL_|Mat_|M_|mat_)", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (!string.IsNullOrEmpty(stripped) && stripped != matchKey
                && map.TryGetValue(stripped, out info)) return info;

            // 3) map 키 중에 matchKey를 포함하거나 포함되는 것 찾기
            var matchLower = matchKey.ToLowerInvariant();
            foreach (var kv in map)
            {
                var kLower = kv.Key.ToLowerInvariant();
                if (kLower.Contains(matchLower) || matchLower.Contains(kLower))
                    return kv.Value;
            }

            return null;
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
        /// 텍스처 파일명과 머티리얼/메시 이름 간 유사도 점수.
        /// 1) _mat/_d/_col 등 공통 suffix 제거 후 비교
        /// 2) 완전 포함 → 100
        /// 3) 토큰 공통 수 × 가중치
        /// </summary>
        private static int SimilarityScore(string texName, string matName)
        {
            var texLower = Path.GetFileNameWithoutExtension(texName).ToLowerInvariant();
            var matLower = matName.ToLowerInvariant();

            // 완전 포함
            if (texLower.Contains(matLower) || matLower.Contains(texLower)) return 100;

            // ★ 공통 suffix 제거 후 재비교
            var texStripped = StripTextureSuffix(texLower);
            var matStripped = StripMaterialSuffix(matLower);

            if (!string.IsNullOrEmpty(texStripped) && !string.IsNullOrEmpty(matStripped))
            {
                if (texStripped.Contains(matStripped) || matStripped.Contains(texStripped)) return 90;
            }

            // 토큰 공통 수 (양쪽 모두 stripped 버전으로 비교)
            var texTokens = System.Text.RegularExpressions.Regex.Split(texStripped ?? texLower, @"[\s_\-\.]+");
            int common = 0;
            var matCompare = matStripped ?? matLower;
            foreach (var t in texTokens)
                if (t.Length > 1 && matCompare.Contains(t)) common++;
            return common;
        }

        // 텍스처 파일명: prefix(tex_/t_) + suffix(_d/_col 등) 제거
        private static readonly System.Text.RegularExpressions.Regex TexPrefixRegex =
            new(@"^(tex_|t_|texture_)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex TexSuffixRegex =
            new(@"[_\-](d|col|color|albedo|main|diff|diffuse|base|0\d|1\d)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static string StripTextureSuffix(string name)
        {
            name = TexPrefixRegex.Replace(name, "");
            return TexSuffixRegex.Replace(name, "");
        }

        // 머티리얼 이름: prefix(FBX_/MTL_/M_) + suffix(_mat) 제거
        private static readonly System.Text.RegularExpressions.Regex MatPrefixRegex =
            new(@"^(FBX_|MTL_|Mat_|M_|mat_)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex MatSuffixRegex =
            new(@"[_\-](mat|material|m)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static string StripMaterialSuffix(string name)
        {
            name = MatPrefixRegex.Replace(name, "");
            return MatSuffixRegex.Replace(name, "");
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

            // PSD 파일은 LoadImage()가 지원 안 됨 → 전용 파서 사용
            if (filePath.EndsWith(".psd", StringComparison.OrdinalIgnoreCase))
            {
                var psdTex = LoadPsdTexture(bytes);
                if (psdTex == null)
                    Debug.LogWarning($"[MaterialManager] PSD 로드 실패: {Path.GetFileName(filePath)}");
                return psdTex;
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

        // ─── PSD 파서 (merged composite, 8-bit RGB/RGBA) ───

        /// <summary>
        /// PSD 파일의 merged composite 이미지를 Texture2D로 변환.
        /// Texture2D.LoadImage()는 PSD 미지원 → 수동 파싱.
        /// 지원: 8-bit RGB/RGBA, Raw 또는 PackBits RLE 압축.
        /// </summary>
        private static Texture2D LoadPsdTexture(byte[] bytes)
        {
            try
            {
                if (bytes.Length < 26) return null;
                // Signature check: "8BPS"
                if (bytes[0] != 0x38 || bytes[1] != 0x42 || bytes[2] != 0x50 || bytes[3] != 0x53) return null;

                int pos = 4;
                int version   = ReadBE16(bytes, pos); pos += 2;
                if (version != 1) return null;
                pos += 6; // reserved

                int channels  = ReadBE16(bytes, pos); pos += 2;
                int height    = ReadBE32(bytes, pos); pos += 4;
                int width     = ReadBE32(bytes, pos); pos += 4;
                int depth     = ReadBE16(bytes, pos); pos += 2;
                int colorMode = ReadBE16(bytes, pos); pos += 2;

                if (depth != 8 || colorMode != 3) // only 8-bit RGB
                {
                    Debug.LogWarning($"[MaterialManager] PSD: 지원 안 되는 포맷 depth={depth} colorMode={colorMode}");
                    return null;
                }

                // Skip color mode data
                int len = ReadBE32(bytes, pos); pos += 4 + len;
                // Skip image resources
                len = ReadBE32(bytes, pos); pos += 4 + len;
                // Skip layer and mask info
                len = ReadBE32(bytes, pos); pos += 4 + len;

                int compression = ReadBE16(bytes, pos); pos += 2;
                int pixCount    = width * height;

                byte[] rCh = new byte[pixCount];
                byte[] gCh = new byte[pixCount];
                byte[] bCh = new byte[pixCount];
                byte[] aCh = channels >= 4 ? new byte[pixCount] : null;

                if (compression == 0) // Raw
                {
                    Buffer.BlockCopy(bytes, pos, rCh, 0, pixCount); pos += pixCount;
                    Buffer.BlockCopy(bytes, pos, gCh, 0, pixCount); pos += pixCount;
                    Buffer.BlockCopy(bytes, pos, bCh, 0, pixCount); pos += pixCount;
                    if (aCh != null) Buffer.BlockCopy(bytes, pos, aCh, 0, pixCount);
                }
                else if (compression == 1) // PackBits RLE
                {
                    // Skip row byte counts table: 2 bytes * channels * height
                    pos += 2 * channels * height;

                    void DecodeRle(byte[] dest)
                    {
                        int written = 0;
                        while (written < pixCount && pos < bytes.Length)
                        {
                            int header = (sbyte)bytes[pos++];
                            if (header == -128) continue;
                            if (header >= 0)
                            {
                                int count = header + 1;
                                for (int i = 0; i < count && written < pixCount; i++)
                                    dest[written++] = bytes[pos++];
                            }
                            else
                            {
                                int count = -header + 1;
                                byte val  = bytes[pos++];
                                for (int i = 0; i < count && written < pixCount; i++)
                                    dest[written++] = val;
                            }
                        }
                    }

                    DecodeRle(rCh);
                    DecodeRle(gCh);
                    DecodeRle(bCh);
                    if (aCh != null) DecodeRle(aCh);
                }
                else
                {
                    Debug.LogWarning($"[MaterialManager] PSD: 지원 안 되는 압축 방식 {compression}");
                    return null;
                }

                // PSD = top-to-bottom, Unity = bottom-to-top → Y 뒤집기
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                var pixels = new Color32[pixCount];
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * width;
                    int dstRow = (height - 1 - y) * width;
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcRow + x;
                        int d = dstRow + x;
                        pixels[d] = new Color32(rCh[s], gCh[s], bCh[s], aCh != null ? aCh[s] : (byte)255);
                    }
                }
                tex.SetPixels32(pixels);
                tex.Apply();
                Debug.Log($"[MaterialManager] PSD 로드 성공: {width}×{height} ch={channels}");
                return tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaterialManager] PSD 파싱 오류: {ex.Message}");
                return null;
            }
        }

        private static int ReadBE16(byte[] b, int pos) => (b[pos] << 8) | b[pos + 1];
        private static int ReadBE32(byte[] b, int pos) => (b[pos] << 24) | (b[pos+1] << 16) | (b[pos+2] << 8) | b[pos+3];

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
