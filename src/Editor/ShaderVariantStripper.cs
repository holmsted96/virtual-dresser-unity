// ShaderVariantStripper.cs
// 빌드 시 불필요한 셰이더 변형을 제거해 빌드 시간을 단축.
//
// 핵심 전략:
//   URP/Lit — TriLib 로딩용으로만 사용, 로드 완료 직후 lilToon으로 교체됨
//             → 기본 변형(키워드 없음) 1개만 유지, 나머지 strip
//   lilToon  — 실제 렌더링에 사용
//             → 우리가 쓰는 3가지(불투명/Cutout/Transparent) + 기본 유지
//             → 사용 안 하는 lilToon 변형(Fur, Gem, FakeShadow 등)은 제거

using System.Collections.Generic;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualDresser.Editor
{
    public class ShaderVariantStripper : IPreprocessShaders
    {
        // 기본 변형만 남기고 모든 키워드 변형을 제거할 셰이더
        // TriLib이 로딩 중 잠깐 사용 → 바로 lilToon으로 교체되므로 기본만 있으면 충분
        static readonly HashSet<string> _stripToBaseOnly = new HashSet<string>
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Unlit",
        };

        // 실제 사용하는 lilToon 셰이더 — 이 목록에 없는 lilToon 변형은 제거
        // (ltsmulti, ltsfur, ltsgem, ltsfakeshadow 등 수십 종 제거)
        static readonly HashSet<string> _keepLilToon = new HashSet<string>
        {
            "lilToon",            // 불투명
            "Hidden/lilToonCutout",      // Cutout
            "Hidden/lilToonTransparent", // Transparent
        };

        public int callbackOrder => 0;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            if (shader == null) return;

            // ── URP/Lit 계열: 기본 변형만 유지 ──────────────────────────────
            if (_stripToBaseOnly.Contains(shader.name))
            {
                StripAllExceptBase(shader.name, data);
                return;
            }

            // ── lilToon 계열: 사용하지 않는 변형 제거 ──────────────────────
            if (shader.name.StartsWith("lilToon") || shader.name.StartsWith("Hidden/lilToon") ||
                shader.name.StartsWith("_lil/"))
            {
                if (!_keepLilToon.Contains(shader.name))
                {
                    // 사용 안 하는 lilToon 변형 (Fur, Gem, FakeShadow, Multi 등) → 전부 제거
                    int removed = data.Count;
                    data.Clear();
                    Debug.Log($"[ShaderStrip] {shader.name}: {removed}개 변형 제거 (미사용)");
                }
                return;
            }
        }

        static void StripAllExceptBase(string shaderName, IList<ShaderCompilerData> data)
        {
            int before = data.Count;
            for (int i = data.Count - 1; i >= 0; i--)
            {
                var keywords = data[i].shaderKeywordSet.GetShaderKeywords();
                // 키워드가 1개라도 있으면 제거 (기본 변형만 남김)
                if (keywords.Length > 0)
                    data.RemoveAt(i);
            }
            int after = data.Count;
            if (before != after)
                Debug.Log($"[ShaderStrip] {shaderName}: {before}개 → {after}개 (기본 변형만 유지)");
        }
    }
}
