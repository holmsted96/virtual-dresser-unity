// MeshSelector.cs
// 뷰포트 메쉬 클릭 선택 + 아웃라인 렌더링
//
// 동작:
//   1) Select(smr) 호출 → 해당 SMR과 동일한 본 바인딩을 가진 아웃라인 SMR을 자식 GO로 생성
//   2) 아웃라인 SMR은 VirtualDresser/Outline 셰이더 사용 (back-face scaled)
//   3) Deselect() → 아웃라인 GO 파괴

using System.Collections.Generic;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public class MeshSelector : MonoBehaviour
    {
        // ─── 공개 API ───

        /// <summary>현재 선택된 SMR. null이면 선택 없음.</summary>
        public SkinnedMeshRenderer Selected => _selected;

        /// <summary>선택 변경 이벤트. 새 SMR(null=해제)을 인자로 전달.</summary>
        public event System.Action<SkinnedMeshRenderer> OnSelectionChanged;

        [Header("아웃라인")]
        [SerializeField] private Color outlineColor = new Color(0.2f, 0.7f, 1f, 1f);
        [SerializeField] [Range(0f, 0.05f)] private float outlineWidth = 0.007f;

        // ─── 내부 상태 ───
        private SkinnedMeshRenderer _selected;
        private GameObject          _outlineGo;
        private Material            _outlineMat;

        // MaterialPropertyBlock으로 선택된 SMR에 파란 테두리 느낌의 rim 효과 추가
        // (아웃라인과 함께 이중 피드백)
        private MaterialPropertyBlock _mpb;
        private readonly Dictionary<int, bool> _savedEmissions = new();

        // ─── 초기화 ───

        private void Awake()
        {
            _mpb = new MaterialPropertyBlock();

            // 아웃라인 머티리얼 동적 생성
            var shader = Shader.Find("VirtualDresser/Outline");
            if (shader == null)
            {
                // 빌드에 셰이더가 없을 경우 폴백 — 아웃라인 없이 동작
                Debug.LogWarning("[MeshSelector] VirtualDresser/Outline 셰이더를 찾을 수 없음 — 아웃라인 비활성화");
                return;
            }
            _outlineMat = new Material(shader);
            _outlineMat.SetColor("_OutlineColor", outlineColor);
            _outlineMat.SetFloat("_OutlineWidth", outlineWidth);
        }

        // ─── 공개 메서드 ───

        /// <summary>SMR 선택. null이면 해제. 이미 같은 SMR이면 토글(해제).</summary>
        public void Select(SkinnedMeshRenderer smr)
        {
            if (smr == null) { Deselect(); return; }

            if (smr == _selected)
            {
                Deselect();
                return;
            }

            Deselect(); // 이전 선택 해제

            _selected = smr;
            CreateOutline(smr);
            ApplyRimHighlight(smr);
            OnSelectionChanged?.Invoke(_selected);
        }

        /// <summary>선택 해제.</summary>
        public void Deselect()
        {
            if (_selected == null) return;

            RemoveOutline();
            RemoveRimHighlight(_selected);
            _selected = null;
            OnSelectionChanged?.Invoke(null);
        }

        // ─── 아웃라인 (별도 SMR 복제) ───

        private void CreateOutline(SkinnedMeshRenderer source)
        {
            if (_outlineMat == null) return;
            if (source.sharedMesh == null) return;

            _outlineGo = new GameObject("__outline__");
            _outlineGo.hideFlags = HideFlags.HideAndDontSave;
            _outlineGo.transform.SetParent(source.transform, false);
            _outlineGo.transform.localPosition = Vector3.zero;
            _outlineGo.transform.localRotation = Quaternion.identity;
            _outlineGo.transform.localScale    = Vector3.one;

            var outlineSmr = _outlineGo.AddComponent<SkinnedMeshRenderer>();
            outlineSmr.sharedMesh     = source.sharedMesh;
            outlineSmr.bones          = source.bones;
            outlineSmr.rootBone       = source.rootBone;
            outlineSmr.localBounds    = source.localBounds;

            // 머티리얼 슬롯 수를 submesh 수에 맞춰 전부 아웃라인 머티리얼로
            var mats = new Material[source.sharedMesh.subMeshCount];
            for (int i = 0; i < mats.Length; i++) mats[i] = _outlineMat;
            outlineSmr.sharedMaterials = mats;

            // 아웃라인은 그림자 드리우지 않음
            outlineSmr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            outlineSmr.receiveShadows     = false;
            // sortingOrder 기본값(0) 유지 — -1로 내리면 메인 메쉬에 가려짐
        }

        private void RemoveOutline()
        {
            if (_outlineGo != null)
            {
                Destroy(_outlineGo);
                _outlineGo = null;
            }
        }

        // ─── Rim 하이라이트 (MaterialPropertyBlock) ───
        // lilToon은 _RimColor / _RimLightingMix 프로퍼티가 있음

        private void ApplyRimHighlight(SkinnedMeshRenderer smr)
        {
            for (int mi = 0; mi < smr.sharedMaterials.Length; mi++)
            {
                var mat = smr.sharedMaterials[mi];
                if (mat == null) continue;
                // rimLightStrength 살짝 올려서 외곽 선택 느낌 강화 (lilToon)
                // 실패해도 무방 (프로퍼티 없을 수 있음)
                smr.GetPropertyBlock(_mpb, mi);
                if (mat.HasProperty("_RimColor"))
                    _mpb.SetColor("_RimColor", new Color(0.3f, 0.7f, 1f, 1f));
                if (mat.HasProperty("_RimLightingMix"))
                    _mpb.SetFloat("_RimLightingMix", 1f);
                smr.SetPropertyBlock(_mpb, mi);
            }
        }

        private void RemoveRimHighlight(SkinnedMeshRenderer smr)
        {
            if (smr == null) return;
            for (int mi = 0; mi < smr.sharedMaterials.Length; mi++)
                smr.SetPropertyBlock(null, mi);
        }

        // ─── 아웃라인 색상/두께 런타임 변경 ───

        public void SetOutlineStyle(Color color, float width)
        {
            outlineColor = color;
            outlineWidth = width;
            if (_outlineMat != null)
            {
                _outlineMat.SetColor("_OutlineColor", color);
                _outlineMat.SetFloat("_OutlineWidth", width);
            }
        }

        private void OnDestroy()
        {
            RemoveOutline();
            if (_outlineMat != null) Destroy(_outlineMat);
        }
    }
}
