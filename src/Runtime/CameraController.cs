// CameraController.cs
// 뷰포트 마우스 조작: 아바타 주위 360도 오빗 + 줌
//
//   - 좌클릭 드래그: 아바타 주위 오빗
//   - 스크롤: 줌 인/아웃
//   - 우클릭 드래그: 카메라 상하좌우 패닝
//   - 좌클릭 (드래그 아님): 메쉬 선택 레이캐스트 → OnMeshClicked 이벤트

using System.Collections.Generic;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public class CameraController : MonoBehaviour
    {
        [Header("오빗 설정")]
        [SerializeField] private float orbitSpeed  = 200f;
        [SerializeField] private float zoomSpeed   = 5f;
        [SerializeField] private float panSpeed    = 0.3f;
        [SerializeField] private float minDistance = 0.5f;
        [SerializeField] private float maxDistance = 10f;

        [Header("메쉬 선택")]
        [Tooltip("클릭으로 인식할 최대 드래그 거리 (픽셀)")]
        [SerializeField] private float clickDragThreshold = 8f;

        [Tooltip("우측 컨트롤 패널 너비 (픽셀). 이 영역 안쪽 클릭은 메쉬 선택 무시.")]
        [SerializeField] private float rightPanelWidth = 330f;

        // 오빗 타겟 (아바타 로드 시 외부에서 설정)
        public Vector3 Target = new Vector3(0f, 0.8f, 0f);

        /// <summary>
        /// 메쉬 클릭 이벤트. 클릭된 SMR(null=빈 공간 클릭)을 인자로 전달.
        /// DresserUI가 구독해서 선택 처리.
        /// </summary>
        public event System.Action<SkinnedMeshRenderer> OnMeshClicked;

        private float _yaw;
        private float _pitch    = 15f;
        private float _distance = 2.5f;

        // ─── 클릭 감지 ───
        private Vector2 _mouseDownPos;
        private bool    _leftButtonDown;
        private bool    _isDragging;      // 임계값 초과 → 드래그 상태

        // ResetView용 저장 상태
        private Bounds _savedBounds;
        private bool   _hasSavedBounds;

        private void Start()
        {
            var offset = transform.position - Target;
            _distance  = offset.magnitude;
            _yaw       = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            _pitch     = Mathf.Asin(offset.y / Mathf.Max(_distance, 0.01f)) * Mathf.Rad2Deg;
        }

        private void Update()
        {
            // ── 좌버튼 다운 ──
            if (Input.GetMouseButtonDown(0))
            {
                _mouseDownPos   = Input.mousePosition;
                _leftButtonDown = true;
                _isDragging     = false;
            }

            // ── 드래그 여부 판단 ──
            if (_leftButtonDown && !_isDragging)
            {
                float moved = Vector2.Distance(Input.mousePosition, _mouseDownPos);
                if (moved >= clickDragThreshold)
                    _isDragging = true;
            }

            // ── 좌버튼 업 → 클릭 판정 ──
            if (Input.GetMouseButtonUp(0))
            {
                if (_leftButtonDown && !_isDragging)
                {
                    // UI 패널 영역 클릭 제외 (우측 rightPanelWidth px)
                    if (_mouseDownPos.x < Screen.width - rightPanelWidth)
                        HandleMeshClickRaycast(_mouseDownPos);
                }
                _leftButtonDown = false;
                _isDragging     = false;
            }
        }

        private void LateUpdate()
        {
            // ── 좌클릭 드래그: 오빗 ──
            if (_leftButtonDown && _isDragging)
            {
                _yaw   += Input.GetAxis("Mouse X") * orbitSpeed * Time.deltaTime;
                _pitch -= Input.GetAxis("Mouse Y") * orbitSpeed * Time.deltaTime;
                _pitch  = Mathf.Clamp(_pitch, -60f, 80f);
            }

            // ── 우클릭 드래그: 패닝 ──
            if (Input.GetMouseButton(1))
            {
                Target -= transform.right * Input.GetAxis("Mouse X") * panSpeed;
                Target -= transform.up    * Input.GetAxis("Mouse Y") * panSpeed;
            }

            // ── 스크롤: 줌 ──
            _distance -= Input.GetAxis("Mouse ScrollWheel") * zoomSpeed;
            _distance  = Mathf.Clamp(_distance, minDistance, maxDistance);

            // ── 카메라 위치/방향 적용 ──
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.position = Target + rot * new Vector3(0f, 0f, _distance);
            transform.LookAt(Target);
        }

        // ─── 메쉬 레이캐스트 ───

        private void HandleMeshClickRaycast(Vector2 screenPos)
        {
            var cam = GetComponent<Camera>();
            if (cam == null) cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[CameraController] 카메라 컴포넌트 없음 — 레이캐스트 불가");
                return;
            }

            var ray = cam.ScreenPointToRay(screenPos);
            var allSmrs = FindObjectsOfType<SkinnedMeshRenderer>();

            // ── 1단계: ray-bounds 교차 후보 수집 (확장 최소화로 정확도 향상)
            var candidates = new List<(SkinnedMeshRenderer smr, float worldDist, float screenDist)>();

            foreach (var smr in allSmrs)
            {
                if (smr.gameObject.name == "__outline__") continue;
                if (!smr.gameObject.activeInHierarchy)    continue;
                if (smr.sharedMesh == null)               continue;

                var b = smr.bounds;
                b.Expand(0.03f);   // 확장 최소화 (0.08 → 0.03)

                if (!b.IntersectRay(ray, out float worldDist)) continue;

                // ── 2단계: 스크린 스페이스 거리 계산 (bounds 중심 기준)
                // 카메라에 보이는 위치가 클릭 지점에 얼마나 가까운지 평가
                var screenCenter = cam.WorldToScreenPoint(b.center);
                if (screenCenter.z < 0) continue;  // 카메라 뒤
                float screenDist = Vector2.Distance(screenPos, new Vector2(screenCenter.x, screenCenter.y));

                candidates.Add((smr, worldDist, screenDist));
            }

            if (candidates.Count == 0)
            {
                OnMeshClicked?.Invoke(null);
                return;
            }

            // ── 3단계: 스크린 거리가 가장 가까운 것 선택
            // (world distance 만 보면 겹친 메시에서 뒤쪽이 선택될 수 있음)
            candidates.Sort((a, b) =>
            {
                // 스크린 거리 200px 이내면 world distance 우선 (앞뒤 구분)
                bool aClose = a.screenDist < 200f;
                bool bClose = b.screenDist < 200f;
                if (aClose && bClose)
                    return a.worldDist.CompareTo(b.worldDist);
                return a.screenDist.CompareTo(b.screenDist);
            });

            var picked = candidates[0].smr;
            Debug.Log($"[CameraController] 선택됨: {picked.name} (screen={candidates[0].screenDist:F0}px)");
            OnMeshClicked?.Invoke(picked);
        }

        // ─── 공개 API ───

        public void FocusOnBounds(Bounds bounds)
        {
            _savedBounds    = bounds;
            _hasSavedBounds = true;

            Target    = bounds.center;
            _distance = bounds.size.magnitude * 1.2f;
            _distance = Mathf.Clamp(_distance, minDistance, maxDistance);
            _pitch    = 10f;
            _yaw      = 0f;
        }

        public void ResetView()
        {
            if (_hasSavedBounds) FocusOnBounds(_savedBounds);
        }
    }
}
