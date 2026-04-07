// MeshCombiner.cs
// mesh-combiner.ts Unity C# 포팅
//
// 핵심 원리 (원본 주석 보존):
//   BOOTH 의상은 대응 아바타와 동일한 본 이름/구조를 가진다.
//   → 의상 SkinnedMeshRenderer.bones를 아바타의 동명 Transform으로 교체하면
//     Unity 스키닝이 자동으로 아바타 포즈를 따라간다.
//
//   ★ bind() / RecalculateBounds() 호출 금지
//     bindposes(boneInverses)를 원본 그대로 유지해야 뒤틀림 방지
//     mesh-combiner.ts bindClothingToAvatarSkeleton() v3 전략과 동일
//
// 재활용률: 약 75%

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    /// <summary>
    /// 메쉬 결합 결과 통계
    /// mesh-combiner.ts CombineStats 포팅
    /// </summary>
    public class CombineStats
    {
        public int AvatarMeshCount;
        public int ClothingMeshCount;
        public int TotalBoneCount;
        public int RemappedBoneCount;
        public int UnboundBoneCount;
        public int TotalVertexCount;
        public int BlendShapeCount;

        public override string ToString() =>
            $"아바타 메쉬:{AvatarMeshCount} 의상 메쉬:{ClothingMeshCount} " +
            $"본 재매핑:{RemappedBoneCount}/{TotalBoneCount} " +
            $"미매핑:{UnboundBoneCount} 정점:{TotalVertexCount}";
    }

    public static class MeshCombiner
    {
        // ─── 메인: 의상 스켈레톤 바인딩 ───

        /// <summary>
        /// 의상 GameObject의 모든 SkinnedMeshRenderer를
        /// 아바타 스켈레톤에 직접 바인딩.
        ///
        /// mesh-combiner.ts bindClothingToAvatarSkeleton() v3 완전 포팅.
        ///
        /// ★ 핵심: smr.bones 교체만 수행. bindposes 재계산 안 함.
        ///    Unity의 bindposes = Three.js의 boneInverses
        ///    원본 그대로 유지해야 rest pose가 보존됨.
        /// </summary>
        public static CombineStats BindClothingToAvatar(
            Transform avatarRoot,
            GameObject clothingGo)
        {
            // ── 1. 아바타 본 이름 → Transform 인덱스 구축 ──
            var avatarBoneByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            CollectBones(avatarRoot, avatarBoneByName);
            Debug.Log($"[MeshCombiner] 아바타 본 {avatarBoneByName.Count}개 인덱싱");

            var stats = new CombineStats();
            var boneNameSet = new HashSet<string>();

            // ── 2. 의상의 모든 SkinnedMeshRenderer 순회 ──
            foreach (var smr in clothingGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var oldBones = smr.bones;
                if (oldBones == null || oldBones.Length == 0) continue;

                stats.ClothingMeshCount++;

                var newBones = new Transform[oldBones.Length];
                int remapped = 0;

                for (int i = 0; i < oldBones.Length; i++)
                {
                    if (oldBones[i] == null)
                    {
                        newBones[i] = oldBones[i];
                        continue;
                    }

                    var boneName = oldBones[i].name;
                    boneNameSet.Add(boneName);

                    if (avatarBoneByName.TryGetValue(boneName, out var avatarBone))
                    {
                        // ★ 동명 아바타 본으로 교체
                        newBones[i] = avatarBone;
                        remapped++;
                    }
                    else
                    {
                        // 아바타에 없는 의상 전용 본 (PhysBone, 리본 등) → 원본 유지
                        newBones[i] = oldBones[i];
                        stats.UnboundBoneCount++;
                    }
                }

                // ★ bones만 교체 — bindposes(boneInverses)는 절대 건드리지 않음
                smr.bones = newBones;

                stats.RemappedBoneCount += remapped;

                // 정점 수 집계
                if (smr.sharedMesh != null)
                    stats.TotalVertexCount += smr.sharedMesh.vertexCount;

                // BlendShape 수 집계
                if (smr.sharedMesh != null)
                    stats.BlendShapeCount += smr.sharedMesh.blendShapeCount;

                // 그림자 활성화
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                smr.receiveShadows = true;

                Debug.Log($"[MeshCombiner] {smr.name}: {remapped}/{oldBones.Length}본 바인딩");
            }

            stats.TotalBoneCount = boneNameSet.Count;

            // 아바타 메쉬 수 집계
            stats.AvatarMeshCount = avatarRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;

            Debug.Log($"[MeshCombiner] 바인딩 완료 — {stats}");
            return stats;
        }

        // ─── 스케일 자동 매칭 ───

        /// <summary>
        /// 아바타와 의상의 바운딩 박스 높이를 비교해 스케일 보정값 계산.
        /// mesh-combiner.ts calculateScaleMatch() 포팅
        /// </summary>
        public static float CalculateScaleMatch(GameObject avatarGo, GameObject clothingGo)
        {
            var avatarBounds  = CalculateBounds(avatarGo);
            var clothingBounds = CalculateBounds(clothingGo);

            float avatarHeight   = avatarBounds.size.y;
            float clothingHeight = clothingBounds.size.y;

            if (clothingHeight <= 0f) return 1f;

            float ratio = avatarHeight / clothingHeight;

            // 0.5 ~ 2.0 범위면 보정 불필요
            return (ratio > 0.5f && ratio < 2.0f) ? 1f : ratio;
        }

        // ─── 아바타 헤어 메쉬 자동 숨김 ───

        /// <summary>
        /// 헤어 에셋 드롭 시 아바타 기본 헤어 메쉬를 숨김.
        /// DresserUI.SuggestHideAvatarHair()에서 호출.
        /// </summary>
        public static int HideAvatarHairMeshes(Transform avatarRoot)
        {
            var hairKeywords = new[] { "hair", "wig", "kami", "髪" };
            int count = 0;

            foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var lower = smr.name.ToLowerInvariant();
                if (hairKeywords.Any(k => lower.Contains(k)))
                {
                    smr.gameObject.SetActive(false);
                    count++;
                }
            }

            Debug.Log($"[MeshCombiner] 아바타 헤어 메쉬 {count}개 숨김");
            return count;
        }

        // ─── 유틸리티 ───

        private static void CollectBones(Transform root, Dictionary<string, Transform> result)
        {
            if (root == null) return;
            if (!string.IsNullOrEmpty(root.name))
                result.TryAdd(root.name, root);
            foreach (Transform child in root)
                CollectBones(child, result);
        }

        private static Bounds CalculateBounds(GameObject go)
        {
            var bounds = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                bounds.Encapsulate(r.bounds);
            return bounds;
        }
    }
}
