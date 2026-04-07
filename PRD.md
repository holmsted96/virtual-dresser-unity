# PRD — Virtual Dresser Unity Standalone 버전

## 제품 목표

VRChat/Warudo 아바타 의상 교체 도구.
현재 Three.js 버전의 **빠른 반응성**은 유지하면서
lilToon 정확 렌더링 + PhysBone 시뮬레이션을 추가한다.

---

## 핵심 유저 플로우

```
① 앱 실행 (2~3초)
        ↓
② 아바타 선택 (7종 카드 UI — 현재와 동일)
        ↓
③ .unitypackage / .zip 드래그앤드롭
        ↓
④ 즉시 미리보기 (Stage 2A — TriLib 로드, ~3초)
        ↓
⑤ [선택] 고품질 변환 버튼 → Unity Headless 실행 (~60초, 1회)
        ↓
⑥ 의상 .unitypackage 드롭 → 아바타에 오버레이
        ↓
⑦ 레이어 패널에서 메쉬 ON/OFF / 머티리얼 편집
        ↓
⑧ "뚜따 빌드" → .warudo / VRChat 패키지 출력
```

---

## 기능 요구사항

### F1. 스마트 임포트 (현재와 동일)
- `.zip` 내 `.unitypackage` 자동 탐색
- 에셋 타입 자동 판별 (아바타/의상/헤어/머티리얼)
- 파일명 기반 + GUID 기반 판별 (현재보다 정밀)

### F2. 렌더링
- **즉시 미리보기**: TriLib FBX 로드 + 기본 lilToon
- **고품질 모드**: Unity AssetBundle → 정확한 셰이더/텍스처
- PhysBone 실시간 시뮬레이션 (고품질 모드에서만)

### F3. 레이어 시스템 (현재와 동일한 UX)
- 아바타 / 의상 / 헤어 / 머티리얼 4개 슬롯
- 레이어 탭: 드래그앤드롭 + 파일 선택
- 메쉬 탭: 메쉬별 ON/OFF + 🗑 삭제
- 머티리얼 탭: 색상 피커 + 텍스처 교체

### F4. 본 매핑 (현재 로직 포팅)
- `BoneMapper.cs` — bone-mapper.ts 알고리즘 그대로
- `avatar-configs/*.json` 그대로 사용
- 의상 SkinnedMesh → 아바타 Skeleton 자동 바인딩

### F5. 메쉬 편집 (신규)
- 메쉬 삭제: 빌드 출력에서도 제외
- 헤어 파트별 토글 (PhysBone 본 단위)
- 메쉬 병합 (의상 메쉬를 아바타 메쉬에 합치기)

### F6. 최종 빌드 출력 (현재보다 강화)
- `.warudo` 포맷 (CharacterAsset)
- VRChat SDK 업로드 준비 상태
- 수정된 아바타를 `.unitypackage`로 재패키징

---

## 비기능 요구사항

| 항목 | 목표 |
|------|------|
| 앱 시작 시간 | < 5초 |
| 즉시 미리보기 로드 | < 5초 |
| 고품질 변환 (최초) | < 90초 (이후 캐시) |
| 메모리 사용량 | < 1GB (일반 아바타 기준) |
| 배포 크기 | < 300MB (Unity 런타임 포함) |
| Unity 설치 여부 | 즉시 미리보기: 불필요 / 고품질: 필요 |

---

## 대응 아바타 (현재와 동일 7종)

마누카 / 萌 / シナノ / シオ / マオ / ルミナ / シンラ

---

## 기술 스택

```
언어:    C# (Unity 2022.3 LTS)
렌더:    Built-in RP 또는 URP + lilToon
물리:    VRC PhysBone / Dynamic Bone 호환
UI:      Unity UI Toolkit (UXML/USS)
파싱:    SharpZipLib (tar.gz), Newtonsoft.Json
FBX:     TriLib 2 (런타임) + Unity FBX Importer (에디터)
빌드:    Unity Standalone (Windows x64)
```

---

## 개발 단계

### Phase 1 — 기반 인프라 (2~3주)
- [ ] Unity 프로젝트 세팅 (URP, lilToon, TriLib)
- [ ] `UnitypackageParser.cs` — Rust lib.rs 포팅
- [ ] `AvatarConfig.cs` + JSON 로드
- [ ] `BoneMapper.cs` — bone-mapper.ts 포팅
- [ ] 기본 UI 레이아웃 (UI Toolkit)

### Phase 2 — 코어 기능 (3~4주)
- [ ] 즉시 미리보기 파이프라인 (TriLib + 텍스처 매칭)
- [ ] 의상 바인딩 (MeshCombiner.cs)
- [ ] 레이어 패널 (메쉬 ON/OFF, 머티리얼 편집)
- [ ] 아바타 선택 화면 (7종 카드)

### Phase 3 — 고품질 모드 (2~3주)
- [ ] Unity Headless 연동 (BatchImporter.cs)
- [ ] AssetBundle 로드 + 캐시
- [ ] PhysBone 실시간 시뮬

### Phase 4 — 빌드 출력 (2~3주)
- [ ] `.warudo` CharacterAsset 생성
- [ ] 메쉬 삭제 반영한 export
- [ ] VRChat SDK 패키지 준비

---

## 현재 버전과의 기능 비교

| 기능 | 현재 (Three.js) | Unity 버전 |
|------|----------------|-----------|
| .unitypackage 파싱 | ✅ Rust | ✅ C# 포팅 |
| FBX 즉시 로드 | ✅ Three.js | ✅ TriLib |
| lilToon 렌더링 | ⚠️ 근사치 | ✅ 정확 |
| PhysBone | ❌ | ✅ |
| 메쉬 ON/OFF | ✅ | ✅ |
| 메쉬 삭제 (빌드 반영) | ⚠️ 씬만 | ✅ |
| 머티리얼 색상 편집 | ✅ | ✅ |
| .warudo 빌드 | ⚠️ 미완 | ✅ |
| Unity 설치 없이 실행 | ✅ | ✅ (즉시 미리보기) |
| 앱 시작 속도 | ✅ 빠름 | ✅ 빠름 (빌드 앱) |
