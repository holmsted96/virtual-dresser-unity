// PoseController.cs
// 아바타 본 포즈 테스트 컨트롤러
//
// 로컬 Euler 각도 대신 월드 스페이스 방향 기반으로 팔을 회전하므로
// Blender/VRM 아바타의 본 방향이 달라도 일관되게 동작함.
//
// 지원 포즈:
//   T-Pose  — 로드 시 저장한 rest 포즈 복원
//   A-Pose  — 팔을 대각선 아래(~45도) 방향으로
//   Arms Up — 팔을 대각선 위(~70도) 방향으로

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public class PoseController : MonoBehaviour
    {
        private Dictionary<string, Transform> _boneMap;   // humanoidKey → Transform
        private Dictionary<string, Quaternion> _restPose; // humanoidKey → 원래 localRotation
        private Coroutine _poseCoroutine;

        // ── 공개 API ──

        /// <summary>아바타 로드 직후 호출. 본 매핑 + rest 포즈 저장.</summary>
        public bool SetAvatar(GameObject avatarRoot, AvatarConfig config = null)
        {
            _boneMap  = null;
            _restPose = null;

            if (avatarRoot == null) return false;

            _boneMap = BoneMapper.MapToHumanoid(avatarRoot.transform, config);
            if (_boneMap == null)
            {
                Debug.LogWarning("[PoseController] 본 매핑 실패 — 포즈 기능 비활성");
                return false;
            }

            _restPose = new Dictionary<string, Quaternion>();
            foreach (var (key, bone) in _boneMap)
                _restPose[key] = bone.localRotation;

            Debug.Log($"[PoseController] ✅ 준비: {_boneMap.Count}개 본 매핑됨 " +
                      $"(leftUpperArm={_boneMap.ContainsKey("leftUpperArm")}, " +
                      $"rightUpperArm={_boneMap.ContainsKey("rightUpperArm")})");
            return true;
        }

        public bool IsReady => _boneMap != null && _restPose != null;

        public void ApplyTPose()  => StartPoseLerp(BuildTPoseTargets());
        public void ApplyAPose()  => StartPoseLerp(BuildArmTargets(downAngle: -45f));
        public void ApplyArmsUp() => StartPoseLerp(BuildArmTargets(downAngle:  70f));

        // ── 포즈 타겟 계산 ──

        /// T-Pose: 저장된 rest 포즈 그대로 반환
        private Dictionary<string, Quaternion> BuildTPoseTargets()
        {
            var targets = new Dictionary<string, Quaternion>();
            foreach (var (key, _) in _boneMap)
                targets[key] = _restPose[key];
            return targets;
        }

        /// 팔 포즈: 월드 스페이스에서 팔 방향을 downAngle만큼 회전 (양수=아래, 음수=위)
        private Dictionary<string, Quaternion> BuildArmTargets(float downAngle)
        {
            // 먼저 rest 포즈로 전부 초기화
            var targets = new Dictionary<string, Quaternion>();
            foreach (var (key, _) in _boneMap)
                targets[key] = _restPose[key];

            // rest 포즈를 실제 본에 적용해 월드 방향을 정확히 계산
            foreach (var (key, bone) in _boneMap)
                if (bone != null) bone.localRotation = _restPose[key];

            // 왼쪽 팔
            if (_boneMap.TryGetValue("leftUpperArm", out var leftUpper))
            {
                var leftLower = _boneMap.GetValueOrDefault("leftLowerArm");
                targets["leftUpperArm"] = ComputeArmRotation(leftUpper, leftLower, downAngle, isLeft: true);
            }

            // 오른쪽 팔
            if (_boneMap.TryGetValue("rightUpperArm", out var rightUpper))
            {
                var rightLower = _boneMap.GetValueOrDefault("rightLowerArm");
                targets["rightUpperArm"] = ComputeArmRotation(rightUpper, rightLower, downAngle, isLeft: false);
            }

            return targets;
        }

        /// <summary>
        /// 팔 본을 월드 스페이스에서 downAngle만큼 돌린 localRotation 반환.
        /// 본 방향을 직접 읽어 계산하므로 Blender/VRM 어느 본 방향이든 동작.
        /// </summary>
        private static Quaternion ComputeArmRotation(
            Transform upper, Transform lower, float downAngle, bool isLeft)
        {
            // 팔의 현재 월드 방향: upper→lower (없으면 좌우 방향 추정)
            Vector3 armDir;
            if (lower != null)
                armDir = (lower.position - upper.position).normalized;
            else
                armDir = isLeft ? upper.parent.right * -1f : upper.parent.right;

            // 팔이 수평이라 가정할 때, 아래로 downAngle도 회전시킬 축
            // 팔 방향과 월드 업(up)의 외적 → 팔 회전축
            Vector3 rotAxis = Vector3.Cross(armDir, Vector3.up).normalized;
            if (rotAxis.sqrMagnitude < 0.001f)
                rotAxis = isLeft ? Vector3.forward : Vector3.back;

            // 목표 방향
            Quaternion worldRot    = Quaternion.AngleAxis(downAngle, rotAxis);
            Vector3    targetDir   = worldRot * armDir;

            // 현재 월드 rotation에서 목표 방향으로 보정
            Quaternion fromToWorld = Quaternion.FromToRotation(armDir, targetDir);
            Quaternion newWorldRot = fromToWorld * upper.rotation;

            // 부모 기준 localRotation으로 변환
            if (upper.parent != null)
                return Quaternion.Inverse(upper.parent.rotation) * newWorldRot;
            return newWorldRot;
        }

        // ── 보간 ──

        private void StartPoseLerp(Dictionary<string, Quaternion> targets)
        {
            if (!IsReady)
            {
                Debug.LogWarning("[PoseController] SetAvatar가 아직 호출되지 않았습니다.");
                return;
            }
            if (_poseCoroutine != null) StopCoroutine(_poseCoroutine);
            _poseCoroutine = StartCoroutine(LerpToPose(targets));
        }

        private IEnumerator LerpToPose(Dictionary<string, Quaternion> targets)
        {
            // 현재 rotation 스냅샷
            var from = new Dictionary<string, Quaternion>();
            foreach (var (key, bone) in _boneMap)
                if (bone != null) from[key] = bone.localRotation;

            const float duration = 0.35f;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);

                foreach (var (key, bone) in _boneMap)
                    if (bone != null && from.ContainsKey(key) && targets.ContainsKey(key))
                        bone.localRotation = Quaternion.Slerp(from[key], targets[key], t);

                yield return null;
            }

            // 최종값 확정
            foreach (var (key, bone) in _boneMap)
                if (bone != null && targets.ContainsKey(key))
                    bone.localRotation = targets[key];
        }
    }
}
