// BoneMapper.cs
// src/engine/bone-mapper.ts의 mapUnityBonesToHumanoid() C# 포팅
//
// 현재 TypeScript 버전의 HUMANOID_BONE_CANDIDATES 테이블과
// 3단계 매칭 전략(정확 → 대소문자무시 → 정규화)을 그대로 포팅.
//
// Three.js Bone → Unity Transform 으로만 바뀜.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    /// <summary>
    /// Unity Transform(본) → HumanoidBones 자동 매핑
    /// bone-mapper.ts mapUnityBonesToHumanoid() 완전 포팅
    /// </summary>
    public static class BoneMapper
    {
        /// <summary>
        /// HumanoidBones 각 키 → 후보 본 이름 배열
        /// TypeScript HUMANOID_BONE_CANDIDATES와 동일
        /// BOOTH/VRM 아바타(마누카, 모에, 시나노 등) 실제 본 이름 기반
        /// </summary>
        private static readonly Dictionary<string, string[]> HumanoidBoneCandidates = new()
        {
            ["hips"]           = new[] { "Hips", "J_Bip_C_Hips", "mixamorig:Hips", "hip", "pelvis" },
            ["spine"]          = new[] { "Spine", "J_Bip_C_Spine", "mixamorig:Spine" },
            ["chest"]          = new[] { "Chest", "J_Bip_C_Chest", "J_Bip_C_UpperChest", "UpperChest", "Spine1" },
            ["neck"]           = new[] { "Neck", "J_Bip_C_Neck", "mixamorig:Neck" },
            ["head"]           = new[] { "Head", "J_Bip_C_Head", "mixamorig:Head" },
            // 어깨 (BOOTH: Shoulder.L/R)
            ["leftShoulder"]   = new[] { "Shoulder.L", "LeftShoulder", "J_Bip_L_Shoulder", "Shoulder_L" },
            ["rightShoulder"]  = new[] { "Shoulder.R", "RightShoulder", "J_Bip_R_Shoulder", "Shoulder_R" },
            // 팔 (BOOTH: Upper_arm.L, Lower_arm.L, Hand.L)
            ["leftUpperArm"]   = new[] { "Upper_arm.L", "LeftUpperArm", "J_Bip_L_UpperArm", "mixamorig:LeftArm", "Arm_L" },
            ["leftLowerArm"]   = new[] { "Lower_arm.L", "LeftLowerArm", "J_Bip_L_LowerArm", "mixamorig:LeftForeArm" },
            ["leftHand"]       = new[] { "Hand.L", "LeftHand", "J_Bip_L_Hand", "mixamorig:LeftHand" },
            ["rightUpperArm"]  = new[] { "Upper_arm.R", "RightUpperArm", "J_Bip_R_UpperArm", "mixamorig:RightArm", "Arm_R" },
            ["rightLowerArm"]  = new[] { "Lower_arm.R", "RightLowerArm", "J_Bip_R_LowerArm", "mixamorig:RightForeArm" },
            ["rightHand"]      = new[] { "Hand.R", "RightHand", "J_Bip_R_Hand", "mixamorig:RightHand" },
            // 다리 (BOOTH: Upper_leg.L, Lower_leg.L, Foot.L, Toe.L)
            ["leftUpperLeg"]   = new[] { "Upper_leg.L", "LeftUpperLeg", "J_Bip_L_UpperLeg", "mixamorig:LeftUpLeg", "Leg_L" },
            ["leftLowerLeg"]   = new[] { "Lower_leg.L", "LeftLowerLeg", "J_Bip_L_LowerLeg", "mixamorig:LeftLeg" },
            ["leftFoot"]       = new[] { "Foot.L", "LeftFoot", "J_Bip_L_Foot", "mixamorig:LeftFoot", "Ankle_L" },
            ["rightUpperLeg"]  = new[] { "Upper_leg.R", "RightUpperLeg", "J_Bip_R_UpperLeg", "mixamorig:RightUpLeg", "Leg_R" },
            ["rightLowerLeg"]  = new[] { "Lower_leg.R", "RightLowerLeg", "J_Bip_R_LowerLeg", "mixamorig:RightLeg" },
            ["rightFoot"]      = new[] { "Foot.R", "RightFoot", "J_Bip_R_Foot", "mixamorig:RightFoot", "Ankle_R" },
            ["leftToes"]       = new[] { "Toe.L", "LeftToes", "J_Bip_L_ToeBase", "Toes_L" },
            ["rightToes"]      = new[] { "Toe.R", "RightToes", "J_Bip_R_ToeBase", "Toes_R" },
        };

        /// <summary>
        /// FBX 루트 Transform에서 HumanoidBones 매핑 수행
        /// bone-mapper.ts mapUnityBonesToHumanoid() 포팅
        /// </summary>
        /// <returns>humanoidKey → Transform 매핑, 또는 필수 본 부족 시 null</returns>
        public static Dictionary<string, Transform> MapToHumanoid(Transform root)
        {
            // 모든 자식 본 수집
            var allBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            CollectBones(root, allBones);

            if (allBones.Count == 0)
            {
                Debug.LogWarning("[BoneMapper] 본 없음");
                return null;
            }

            var result = new Dictionary<string, Transform>();
            var usedBones = new HashSet<string>();

            foreach (var (humanoidKey, candidates) in HumanoidBoneCandidates)
            {
                // 1단계: 정확 매칭
                foreach (var candidate in candidates)
                {
                    if (allBones.TryGetValue(candidate, out var bone) && !usedBones.Contains(candidate))
                    {
                        result[humanoidKey] = bone;
                        usedBones.Add(candidate);
                        goto nextKey;
                    }
                }

                // 2단계: 대소문자 무시
                foreach (var candidate in candidates)
                {
                    foreach (var (boneName, bone) in allBones)
                    {
                        if (string.Equals(boneName, candidate, StringComparison.OrdinalIgnoreCase)
                            && !usedBones.Contains(boneName))
                        {
                            result[humanoidKey] = bone;
                            usedBones.Add(boneName);
                            goto nextKey;
                        }
                    }
                }

                // 3단계: 정규화 매칭 (구분자/접두사 제거)
                foreach (var candidate in candidates)
                {
                    var normCandidate = NormalizeBoneName(candidate);
                    foreach (var (boneName, bone) in allBones)
                    {
                        if (NormalizeBoneName(boneName) == normCandidate && !usedBones.Contains(boneName))
                        {
                            result[humanoidKey] = bone;
                            usedBones.Add(boneName);
                            goto nextKey;
                        }
                    }
                }

                nextKey:;
            }

            // 필수 본 검증
            var required = new[] { "hips", "spine", "leftUpperArm", "rightUpperArm", "leftUpperLeg", "rightUpperLeg" };
            var mappedRequired = required.Count(k => result.ContainsKey(k));

            if (mappedRequired < 4)
            {
                Debug.LogWarning($"[BoneMapper] 필수 본 부족: {mappedRequired}/6 → 매핑 실패");
                return null;
            }

            Debug.Log($"[BoneMapper] ✅ 매핑 완료: {result.Count}/{HumanoidBoneCandidates.Count}개");
            return result;
        }

        /// <summary>
        /// 의상 SkinnedMeshRenderer의 본을 아바타 본으로 교체
        /// ThreePreview.tsx 의상 바인딩 로직 포팅
        /// </summary>
        public static void BindClothingToAvatar(SkinnedMeshRenderer clothingMesh, Transform avatarRoot)
        {
            var avatarBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            CollectBones(avatarRoot, avatarBones);

            var oldBones = clothingMesh.bones;
            var newBones = new Transform[oldBones.Length];

            int bound = 0;
            for (int i = 0; i < oldBones.Length; i++)
            {
                if (oldBones[i] == null) continue;

                var boneName = oldBones[i].name;
                if (avatarBones.TryGetValue(boneName, out var avatarBone))
                {
                    newBones[i] = avatarBone;
                    bound++;
                }
                else
                {
                    // nearest-bone fallback: 부모 계층 탐색
                    newBones[i] = FindNearestAvatarBone(oldBones[i], avatarBones)
                                  ?? avatarBones.GetValueOrDefault("Hips")
                                  ?? oldBones[i];
                }
            }

            // boneInverses 원본 보존 (현재 JS 코드의 전략과 동일)
            // bind() 호출 없이 직접 교체 → 뒤틀림 방지
            clothingMesh.bones = newBones;

            Debug.Log($"[BoneMapper] 의상 바인딩: {bound}/{oldBones.Length}개 본 매핑됨");
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

        private static Transform FindNearestAvatarBone(Transform clothingBone,
            Dictionary<string, Transform> avatarBones)
        {
            var current = clothingBone.parent;
            while (current != null)
            {
                if (avatarBones.TryGetValue(current.name, out var found)) return found;
                current = current.parent;
            }
            return null;
        }

        /// <summary>
        /// 본 이름 정규화 — bone-mapper.ts normalizeBoneName() 포팅
        /// "J_Bip_L_UpperArm" → "lupperarm"
        /// </summary>
        private static string NormalizeBoneName(string name)
        {
            return Regex.Replace(
                Regex.Replace(name.ToLowerInvariant(), @"^j_bip_[clr]_", ""),
                @"[_\-.\s]", ""
            );
        }
    }
}
