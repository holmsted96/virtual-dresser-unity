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
            GameObject clothingGo,
            AvatarConfig avatarConfig = null)
        {
            // ── 1. 아바타 본 이름 → Transform 인덱스 구축 ──
            // OrdinalIgnoreCase: 의상 FBX 본명 대소문자가 아바타와 다를 때 직접 매칭
            var avatarBoneByName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            CollectBones(avatarRoot, avatarBoneByName);
            Debug.Log($"[MeshCombiner] 아바타 본 {avatarBoneByName.Count}개 인덱싱");

            // ── 2. AvatarConfig boneMap → alias 역방향 맵 구축 ──
            // alias → 아바타 실제 본 Transform
            // 예: "J_Bip_L_Shoulder", "Shoulder_L" → 아바타의 "Shoulder.L" Transform
            var aliasMap = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            if (avatarConfig?.boneMap != null)
            {
                foreach (var (humanoidKey, aliases) in avatarConfig.boneMap)
                {
                    if (humanoidKey.StartsWith("_comment") || aliases == null) continue;

                    // aliases 중 아바타에 실제 존재하는 본 찾기
                    Transform resolvedBone = null;
                    foreach (var alias in aliases)
                        if (avatarBoneByName.TryGetValue(alias, out resolvedBone)) break;

                    if (resolvedBone == null) continue;

                    // 모든 alias → 같은 아바타 본으로 연결
                    foreach (var alias in aliases)
                        aliasMap.TryAdd(alias, resolvedBone);
                }
                Debug.Log($"[MeshCombiner] boneMap alias {aliasMap.Count}개 구축 (config: {avatarConfig.avatarId})");
            }

            var stats = new CombineStats();
            var boneNameSet = new HashSet<string>();

            // ── 3. 의상의 모든 SkinnedMeshRenderer 순회 ──
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
                        // ★ 1순위: 이름 완전 일치
                        newBones[i] = avatarBone;
                        remapped++;
                    }
                    else if (aliasMap.TryGetValue(boneName, out avatarBone))
                    {
                        // ★ 2순위: boneMap alias 매칭
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

                int unbound = oldBones.Length - remapped;
                Debug.Log($"[MeshCombiner] {smr.name}: {remapped}/{oldBones.Length}본 바인딩" +
                          (unbound > 0 ? $" (미매핑 {unbound}개)" : ""));

                // 발/발가락 관련 본이 미매핑이면 진단 로그
                var footKeywords = new[]{ "foot", "toe", "ankle" };
                var unmappedFoot = oldBones
                    .Where((b, idx) => b != null && newBones[idx] == oldBones[idx]
                        && System.Array.Exists(footKeywords,
                            k => b.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(b => b.name)
                    .Distinct()
                    .ToList();
                if (unmappedFoot.Count > 0)
                    Debug.LogWarning($"[MeshCombiner] {smr.name} 발 관련 미매핑 본: {string.Join(", ", unmappedFoot)}" +
                                    $"\n  아바타 본 예시(발): {string.Join(", ", avatarBoneByName.Keys.Where(k => System.Array.Exists(footKeywords, f => k.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)).Take(6))}");
            }

            stats.TotalBoneCount = boneNameSet.Count;

            // ── 의상 고유 본(PhysBone 체인, 리본 등) → 아바타 계층으로 리패런팅 ──
            // SMR.bones만 교체하면 의상 고유 본은 의상 자체 골격에 묶여 T-포즈로 떠있음.
            // 의상 본 중 아바타 본에 대응되는 것의 자식 중 매핑 안 된 본을
            // 대응 아바타 본 아래로 옮겨 PhysBone/리본이 아바타를 따르도록 한다.
            int reparentedCount = ReparentOrphanBones(avatarBoneByName, aliasMap, clothingGo);
            if (reparentedCount > 0)
                Debug.Log($"[MeshCombiner] 의상 고유 본 리패런팅: {reparentedCount}개 → 아바타 계층");

            // 아바타 메쉬 수 집계
            stats.AvatarMeshCount = avatarRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;

            Debug.Log($"[MeshCombiner] 바인딩 완료 — {stats}");
            return stats;
        }

        /// <summary>
        /// 의상 고유 본(리본, PhysBone 체인 등)을 아바타 계층으로 리패런팅.
        ///
        /// 문제 상황:
        ///   의상 FBX에는 아바타 공유 본(Chest 등) + 의상 전용 본(Ribbon_root 등)이 혼재.
        ///   BindClothingToAvatar는 SMR.bones만 교체 → 의상 전용 본은 의상 자체 Chest 아래에 남음.
        ///   의상 Chest는 아바타를 따르지 않으므로 의상 전용 본이 T-포즈 위치에 떠있게 됨.
        ///
        /// 해결:
        ///   의상 본 중 아바타 본으로 매핑되는 것(예: 의상 Chest)의 자식 중
        ///   자신은 아바타 본이 아닌 것(의상 전용 본)을 대응 아바타 본 아래로 SetParent.
        ///   재귀적으로 처리 → 중간 계층(예: Accessory_Root → Ribbon_root)도 처리됨.
        ///   worldPositionStays=true → 현재 월드 위치/회전 보존.
        /// </summary>
        private static int ReparentOrphanBones(
            Dictionary<string, Transform> avatarBoneByName,
            Dictionary<string, Transform> aliasMap,
            GameObject clothingGo)
        {
            int count = 0;
            // 의상의 모든 Transform 수집 (처리 전 스냅샷)
            var outfitTransforms = clothingGo.GetComponentsInChildren<Transform>(true).ToList();

            // 이미 아바타 계층으로 이동된 본 추적 (중복 처리 방지)
            var alreadyMoved = new HashSet<Transform>();

            foreach (var outfitBone in outfitTransforms)
            {
                if (alreadyMoved.Contains(outfitBone)) continue;

                // 이 의상 본이 아바타 본으로 매핑되는가?
                Transform avatarBone;
                if (!avatarBoneByName.TryGetValue(outfitBone.name, out avatarBone))
                    aliasMap.TryGetValue(outfitBone.name, out avatarBone);
                if (avatarBone == null) continue;

                // 매핑된 의상 본의 직계 자식 순회 (스냅샷)
                var children = new List<Transform>();
                foreach (Transform c in outfitBone) children.Add(c);

                foreach (var child in children)
                {
                    if (alreadyMoved.Contains(child)) continue;

                    // 자식 자신이 아바타 본에 매핑되는 경우 → 리패런팅 불필요
                    bool childIsAvatarBone = avatarBoneByName.ContainsKey(child.name)
                                         || aliasMap.ContainsKey(child.name);
                    if (childIsAvatarBone) continue;

                    // 이미 아바타 본의 자식이면 스킵
                    if (child.parent == avatarBone) continue;

                    child.SetParent(avatarBone, worldPositionStays: true);
                    alreadyMoved.Add(child);
                    count++;
                    Debug.Log($"[MeshCombiner] 리패런팅: '{child.name}' → 아바타 '{avatarBone.name}' 아래");
                }
            }
            return count;
        }

        /// <summary>
        /// 의상 FBX의 본 localRotation을 아바타의 대응 본에 복사.
        ///
        /// 용도: 힐 부츠 등 발 각도가 T-포즈와 다른 의상을 착용할 때
        ///   의상 Foot.L 본의 실제 로컬 회전을 아바타 Foot.L에 적용.
        ///
        /// 적용 기준: 의상 본과 아바타 본의 회전 차이가 threshold 이상인 경우만 덮어씀.
        ///   (불필요한 Spine/Chest 회전 변경 방지)
        /// </summary>
        public static int CopyOutfitBoneRotations(
            Transform avatarRoot,
            GameObject clothingGo,
            AvatarConfig avatarConfig = null,
            float thresholdDegrees = 3f)
        {
            // 아바타 본 인덱스
            var avatarBoneByName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            CollectBones(avatarRoot, avatarBoneByName);

            // alias 맵
            var aliasMap = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            if (avatarConfig?.boneMap != null)
            {
                foreach (var (humanoidKey, aliases) in avatarConfig.boneMap)
                {
                    if (humanoidKey.StartsWith("_comment") || aliases == null) continue;
                    Transform resolved = null;
                    foreach (var alias in aliases)
                        if (avatarBoneByName.TryGetValue(alias, out resolved)) break;
                    if (resolved == null) continue;
                    foreach (var alias in aliases)
                        aliasMap.TryAdd(alias, resolved);
                }
            }

            int applied = 0;
            foreach (var outfitBone in clothingGo.GetComponentsInChildren<Transform>(true))
            {
                Transform avatarBone;
                if (!avatarBoneByName.TryGetValue(outfitBone.name, out avatarBone))
                    aliasMap.TryGetValue(outfitBone.name, out avatarBone);
                if (avatarBone == null) continue;

                float angle = Quaternion.Angle(avatarBone.localRotation, outfitBone.localRotation);
                if (angle < thresholdDegrees) continue;

                Debug.Log($"[MeshCombiner] 본 회전 이식: '{outfitBone.name}' 차이={angle:F1}° → 아바타에 적용");
                avatarBone.localRotation = outfitBone.localRotation;
                applied++;
            }

            if (applied > 0)
                Debug.Log($"[MeshCombiner] 본 회전 이식 완료: {applied}개");
            else
                Debug.Log($"[MeshCombiner] 본 회전 이식: 차이 없음 (의상 본과 아바타 본 회전 동일)");

            return applied;
        }

        // ─── 의상 착용 시 아바타 메시 자동 숨김 ───

        // 의상 키워드 → 숨길 아바타 메시 키워드 매핑
        private static readonly (string[] clothingKeys, string[] hideKeys)[] AutoHideRules =
        {
            // 신발/부츠 → 발톱, 발가락 메시 숨김
            (new[]{ "shoe", "boot", "heel", "sandal", "socks", "sock", "stocking" },
             new[]{ "nail_foot", "toe_", "toe " }),

            // 장갑 → 손톱 메시 숨김
            (new[]{ "glove", "gauntlet" },
             new[]{ "nail_hand" }),
        };

        /// <summary>
        /// 의상 메시 이름 기반으로 아바타의 겹치는 별도 메시 자동 숨김.
        /// (예: 신발 착용 시 Nail_foot_*, Toe_* 자동 숨김)
        /// 단일 바디 메시(Shinano_body 등)는 숨기지 않음 → UI 힌트로 안내.
        /// </summary>
        /// <returns>숨긴 메시 이름 목록 (UI 힌트용)</returns>
        public static List<string> AutoHideOverlappingMeshes(
            Transform avatarRoot, GameObject clothingGo)
        {
            var hidden = new List<string>();

            // 의상 메시 이름 수집
            var clothingNames = new List<string>();
            foreach (var smr in clothingGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                clothingNames.Add(smr.name.ToLowerInvariant());

            // 각 룰 적용
            foreach (var (clothingKeys, hideKeys) in AutoHideRules)
            {
                // 이 룰의 의상 키워드가 하나라도 매칭되는지
                bool clothingMatched = clothingNames.Exists(
                    n => System.Array.Exists(clothingKeys, k => n.Contains(k)));
                if (!clothingMatched) continue;

                // 숨길 아바타 메시 탐색
                foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var lower = smr.name.ToLowerInvariant();
                    if (System.Array.Exists(hideKeys, k => lower.Contains(k)))
                    {
                        smr.gameObject.SetActive(false);
                        hidden.Add(smr.name);
                        Debug.Log($"[MeshCombiner] 자동 숨김: {smr.name}");
                    }
                }
            }

            return hidden;
        }

        /// <summary>
        /// 신발 착용 시 단일 바디 메시 여부 감지 → UI 힌트 문자열 반환.
        /// 단일 바디 메시(whole-body)가 있으면 힌트 반환, 없으면 null.
        /// </summary>
        public static string GetBodyMeshHint(Transform avatarRoot, GameObject clothingGo)
        {
            var shoeKeywords = new[] { "shoe", "boot", "heel", "sandal", "socks", "sock" };
            bool hasShoe = false;
            foreach (var smr in clothingGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (System.Array.Exists(shoeKeywords, k => smr.name.ToLowerInvariant().Contains(k)))
                { hasShoe = true; break; }

            if (!hasShoe) return null;

            // 전신 바디 메시 탐색 (body 키워드 + 정점 수 많음)
            foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var lower = smr.name.ToLowerInvariant();
                if ((lower.Contains("body") || lower.Contains("skin")) && smr.gameObject.activeSelf
                    && smr.sharedMesh != null && smr.sharedMesh.vertexCount > 5000)
                    return $"발이 신발을 뚫고 보이면 메시 패널에서 '{smr.name}'을 숨겨주세요.";
            }
            return null;
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
