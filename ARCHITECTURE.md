# 기술 아키텍처

## 전체 파이프라인

```
[사용자 드래그앤드롭]
        │
        ▼
┌──────────────────────────────────────────┐
│  STAGE 1: 파일 파싱 (항상 실행)           │
│                                          │
│  UnitypackageParser.cs                   │
│  - .zip 내 .unitypackage 탐색            │
│  - tar.gz 해제 → FBX + 텍스처 추출       │
│  - .mat 파일 → 머티리얼-텍스처 매핑      │
│  - 임시 디렉토리에 저장                   │
└──────────────────────────────────────────┘
        │
        ├─────────────────────────┐
        ▼                         ▼
┌────────────────┐     ┌──────────────────────────────┐
│  STAGE 2A:     │     │  STAGE 2B:                   │
│  직접 로드     │     │  Unity Headless 변환 (선택)   │
│  (빠름 ~3초)   │     │  (느림 ~60초, 1회 캐시)       │
│                │     │                              │
│  TriLib로 FBX  │     │  BatchImporter.cs            │
│  런타임 로드   │     │  - AssetDatabase.Import       │
│                │     │  - 머티리얼 정확 연결         │
│  lilToon 셰이더│     │  - PhysBone 설정 유지         │
│  (근사치)      │     │  - → AssetBundle 직렬화      │
└────────────────┘     └──────────────────────────────┘
        │                         │
        └─────────────┬───────────┘
                      ▼
┌──────────────────────────────────────────┐
│  STAGE 3: Unity 런타임 뷰어              │
│                                          │
│  - 아바타 로드 + 의상 오버레이            │
│  - BoneMapper.cs로 스켈레톤 바인딩       │
│  - lilToon 셰이더 정확 렌더링            │
│  - PhysBone 실시간 시뮬레이션            │
│  - 메쉬 ON/OFF / 머티리얼 편집           │
└──────────────────────────────────────────┘
        │
        ▼
┌──────────────────────────────────────────┐
│  STAGE 4: 최종 빌드 출력                  │
│                                          │
│  - .warudo 포맷 export                   │
│  - VRChat SDK 업로드                     │
│  - 수정된 .unitypackage 재패키징         │
└──────────────────────────────────────────┘
```

---

## 프로세스 분리 전략

### 런타임 앱 (virtual-dresser.exe)
- Unity Standalone Player로 빌드
- 사용자가 직접 실행하는 메인 앱
- **항상 실행됨**

### Headless 프로세스 (vd-converter.exe)
- Unity Editor `-batchmode`로 실행
- 런타임 앱이 필요 시 `Process.Start()`로 호출
- 변환 결과를 AssetBundle로 저장 → 캐시 활용
- **선택적 실행 (고품질 필요 시)**

```csharp
// 런타임 앱에서 headless 호출 예시
var proc = Process.Start("Unity.exe",
    $"-batchmode -projectPath {projectPath} " +
    $"-executeMethod VD.BatchImporter.Import " +
    $"-packagePath {packagePath} " +
    $"-outputPath {outputPath}");
```

---

## 렌더링 전략

### Stage 2A (빠른 미리보기)
```
FBX 로드 (TriLib) → 기본 lilToon 셰이더 적용
- 텍스처 휴리스틱 매칭 (현재 material-manager.ts 로직 포팅)
- PhysBone 없음 (정적)
- 2~5초 내 렌더
```

### Stage 2B (정밀 렌더)
```
AssetBundle 로드 → Unity가 생성한 prefab 로드
- 머티리얼 100% 정확
- PhysBone 컴포넌트 살아있음
- 셰이더 파라미터 보존
- 최초 1회 60초 후 캐시 사용 → 이후 1~2초
```

---

## 데이터 흐름

```
avatar-configs/shinano.json
        │
        ▼
AvatarConfig.cs (JsonUtility.FromJson)
        │
        ▼
BoneMapper.cs.MapBones(fbxBones, config)
        │
        ▼
SkinnedMesh.bones 재할당 → 의상이 아바타 골격 따라감
```

---

## 캐시 구조

```
%APPDATA%/VirtualDresser/cache/
├── {packageHash}/
│   ├── manifest.json     ← 파싱 결과 메타데이터
│   ├── avatar.bundle     ← Unity AssetBundle
│   └── textures/         ← 추출된 텍스처
```

패키지 파일의 SHA256 해시를 키로 사용 → 동일 파일 재변환 방지.
