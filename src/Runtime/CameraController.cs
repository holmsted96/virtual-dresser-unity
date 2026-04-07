// CameraController.cs
// 뷰포트 마우스 조작: 아바타 주위 360도 오빗 + 줌
//
// 사용법: Main Camera GameObject에 이 컴포넌트 추가
//   - 좌클릭 드래그: 아바타 주위 오빗
//   - 스크롤: 줌 인/아웃
//   - 우클릭 드래그: 카메라 상하좌우 패닝

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

        // 오빗 타겟 (아바타 로드 시 외부에서 설정)
        public Vector3 Target = new Vector3(0f, 0.8f, 0f);

        private float _yaw;
        private float _pitch = 15f;
        private float _distance = 2.5f;
        private bool  _uiBlocking = false; // UI 위에서 클릭 시 무시

        private void Start()
        {
            // 현재 카메라 위치에서 초기 오빗 파라미터 역산
            var offset   = transform.position - Target;
            _distance    = offset.magnitude;
            _yaw         = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            _pitch       = Mathf.Asin(offset.y / Mathf.Max(_distance, 0.01f)) * Mathf.Rad2Deg;
        }

        private void LateUpdate()
        {
            // ── 좌클릭 드래그: 오빗 ──
            if (Input.GetMouseButton(0))
            {
                _yaw   += Input.GetAxis("Mouse X") * orbitSpeed * Time.deltaTime;
                _pitch -= Input.GetAxis("Mouse Y") * orbitSpeed * Time.deltaTime;
                _pitch  = Mathf.Clamp(_pitch, -60f, 80f);
            }

            // ── 우클릭 드래그: 패닝 ──
            if (Input.GetMouseButton(1))
            {
                var right = transform.right;
                var up    = transform.up;
                Target -= right * Input.GetAxis("Mouse X") * panSpeed;
                Target -= up    * Input.GetAxis("Mouse Y") * panSpeed;
            }

            // ── 스크롤: 줌 ──
            _distance -= Input.GetAxis("Mouse ScrollWheel") * zoomSpeed;
            _distance  = Mathf.Clamp(_distance, minDistance, maxDistance);

            // ── 카메라 위치/방향 적용 ──
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.position = Target + rot * new Vector3(0f, 0f, _distance);
            transform.LookAt(Target);
        }

        /// <summary>
        /// 아바타 로드 후 DresserUI에서 호출 — 타겟과 거리 자동 설정.
        /// </summary>
        public void FocusOnBounds(Bounds bounds)
        {
            Target    = bounds.center;
            _distance = bounds.size.magnitude * 1.2f;
            _distance = Mathf.Clamp(_distance, minDistance, maxDistance);
            _pitch    = 10f;
            _yaw      = 0f;
        }
    }
}
