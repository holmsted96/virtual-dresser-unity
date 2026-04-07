# Virtual Dresser Unity — Claude Code 작업 컨텍스트

## 프로젝트 개요
BOOTH 아바타(.unitypackage)를 로드해 의상을 입혀 Warudo용 `.warudo` 파일로 익스포트하는 Unity 앱.

## 핵심 아키텍처

### 디렉토리 구조
```
virtual-dresser-unity/          ← git 레포 (이 폴더)
  src/
    Runtime/                    ← C# 런타임 스크립트
    UI/                         ← DresserUI.cs + dresser.uxml
    Editor/                     ← BatchImporter.cs, BuildScript.cs, SceneSetup.cs
    Resources/avatar-configs/   ← shinano.json 등 7종 (git에 포함)
  Packages/
    manifest.json               ← Unity 패키지 목록
  vd-warudo-converter/          ← .warudo 헤들리스 빌드용 별도 Unity 2021.3 프로젝트
    Assets/WarudoConverter/Editor/WarudoBuildScript.cs
    Packages/manifest.json      ← Warudo SDK(com.warudo.mod-tool) 포함
  deploy.ps1                    ← 빌드 + 배포 자동화 스크립트

실제 Unity 프로젝트 경로 (로컬 전용, git에 없음):
  c:/vd/virtual-dresser-app/Dresser/    ← Unity 2021.3.45f2 메인 프로젝트
    Assets/VirtualDresser/      ← src/ 파일들이 복사되는 위치
    Assets/UI/dresser.uxml
    Assets/Resources/avatar-configs/
    Assets/TriLib/              ← TriLib 2.6.1 (에셋스토어 구매)
    Packages/manifest.json

빌드 출력:
  c:/vd/build/
    VirtualDresser.exe
    VirtualDresser_Data/
    vd-warudo-converter/        ← deploy.ps1이 여기 복사 (Warudo SDK 포함)
```

> ⚠️ **중요**: 코드 편집은 `c:/vd/virtual-dresser-app/Dresser/Assets/VirtualDresser/` 에서 하고,
> git push 전에 `src/`로 수동 동기화 필요. (아래 동기화 명령 참고)

## 완료된 작업

### Phase 1 — 파싱 + FBX 로드 ✅
- `UnitypackageParser.cs` — tar.gz 스트리밍 파싱 (OOM 방지), .mat 2패스 파싱 (GUID 역매핑)
- `WindowsFilePicker.cs` — Editor: EditorUtility, Standalone: P/Invoke GetOpenFileName
- `FbxConverter.cs` — TriLib 2 기반 FBX 런타임 로드 (AnimationType.Legacy)
- `MeshCombiner.cs` — 의상 골격 바인딩 (bindposes 불변 원칙)

### Phase 2 — UI + 뷰포트 ✅
- `DresserUI.cs` — UI Toolkit 기반 메인 UI
- `dresser.uxml` — 레이아웃 (오른쪽 패널 300px, 왼쪽 투명 뷰포트), 전체 영문 텍스트
- 아바타/의상/헤어/머티리얼 분리 임포트 버튼 4종
- 레이어별 메시 패널 (아바타/의상/헤어 필터)
- `CameraController.cs` — 마우스 오빗/줌/패닝
- `SceneSetup.cs` — Editor 메뉴로 씬 자동 구성 (VirtualDresser > Setup Scene)

### Phase 3 — 텍스처 + lilToon ✅
- `MaterialManager.cs` — 4K 텍스처 1024 다운샘플 + lilToon 셰이더 자동 적용
- lilToon 셰이더 버전별 다중 탐색 (v1/v2/`_lil` 접두사)
- 셰이더 교체 후 `_Color=white` 초기화 (미설정 시 검정 렌더링 방지)
- Cutout/Transparent 변형 keyword + renderQueue 자동 설정
- `_LightMinLimit=0.05` 기본값 (그림자 완전 검정 방지)
- MainTex 매칭: 머티리얼 이름 유사도 점수 기반
- 노말맵/마스크 MainTex 후보 제외 로직

### Phase 4 — 익스포트 ✅
- `WarudoBuildScript.cs` — vd-warudo-converter 헤들리스 빌드 스크립트
  - Warudo SDK 있으면 UMod 빌더 사용, 없으면 AssetBundle 폴백
- `WarudoHeadlessBuilder` (DresserUI 내부) — 헤들리스 Unity 실행 로직
  - ConverterProjectPath: Standalone = `../vd-warudo-converter`, Editor = git repo 경로
  - 첫 실행 감지 (Library 폴더 유무) → 대기 안내 메시지
- `deploy.ps1` — 빌드 + converter 배포 + 워밍업 자동화 (4단계)
- `UnitySetupManager.cs` — 앱 최초 실행 시 Unity Hub + 2021.3.45f2 자동 설치
- `UnityMainThreadDispatcher.cs` — 백그라운드 → 메인 스레드 디스패치
- Warudo SDK `com.warudo.mod-tool 0.14.3.10` — vd-warudo-converter manifest에 포함

### Sprint B — boneMap 통합 ✅
- `MeshCombiner.cs` — AvatarConfig boneMap alias 역방향 맵 구축
- 본 매핑 2단계: 1) 이름 완전 일치, 2) alias 폴백
- `AutoHideOverlappingMeshes()` — 신발 착용 시 발톱/발가락 메시 자동 숨김
- `GetBodyMeshHint()` — 단일 바디 메시 감지 → UI 힌트 반환

### Sprint C — lilToon 셰이더 ✅
- 위 Phase 3에 통합 완료

### Sprint D — Windows Standalone 빌드 ✅
- `BuildScript.cs` — VirtualDresser > Build Windows 메뉴 (`c:/vd/build/` 출력)
- dresser.uxml 전체 영문화 (Standalone에서 CJK 폰트 없어 한국어/일본어 미표시 문제 해결)
- DragEnterEvent 등 Editor 전용 이벤트 `#if UNITY_EDITOR` 처리

## 남은 작업

### 즉시 해야 할 것 — e2e 테스트
1. `deploy.ps1` 실행 → `c:/vd/build/` 생성 확인
2. `VirtualDresser.exe` 실행 → 아바타 임포트 → 의상 임포트
3. "Build / Export" 버튼 → `.warudo` 파일 Desktop에 생성 확인
4. Warudo에서 `.warudo` 로드 테스트

### Sprint E — 메시 편집 기능 (다음 스프린트)
- 개별 메시 **삭제** 버튼 (레이어 패널)
- 메시 **표시/숨김** 토글 (SkinnedMeshRenderer.enabled)
- 메시 **이동/회전/스케일** 조정 (Transform 직접 수정, 의상 미세 위치 보정용)
- ⚠️ 스킨드 메시 특성상 큰 이동은 본 계층과 어긋날 수 있음 → 미세 보정 용도로 제한
- 구현 위치: `DresserUI.cs` 메시 패널에 수치 입력 필드 추가

### 개선 TODO (MVP 이후)
- **텍스처/머티리얼 매칭 정확도 향상** — 루미나 등 일부 아바타에서 깨짐 발생
  - 개선 방향: 파일명 유사도 fallback 강화 / 사용자 수동 매칭 UI
- **UI 전반 고급화** — 레이아웃, 컬러, 아이콘, 전체적인 완성도
- **카메라 UX 개선** — 오빗 속도, 줌 감도, 기본 시점 조정
- **Import 처리 속도 개선** — 프로그레스바, 병렬처리 검토
- **시나노 발 클리핑** — 단일 바디 메시라 자동 숨김 불가 → Sprint E 메시 편집으로 해결 예정

## 중요 기술 결정

### FBX 로더: TriLib 2 채택 이유
- FBX2glTF 서브프로세스 크래시(0xC0000409) 해결 불가
- glTFast SkinnedMesh 스트림 경고 + OOM 문제
- TriLib = 서브프로세스 없음, 텍스처 자동 로드, BOOTH FBX 공식 지원

### Warudo 익스포트: 헤들리스 Unity 방식
- `.warudo` = UMod 2.0 AssetBundle (Unity 2021.3.45f2 전용)
- BuildPipeline = Editor 전용 → 런타임 직접 생성 불가
- 해결: Unity 2021.3 헤들리스로 별도 변환 프로젝트(vd-warudo-converter) 실행
- Warudo SDK 있으면 UMod 정식 빌드, 없으면 AssetBundle 폴백 (Warudo 호환)
- 배포 구조: exe 옆에 `vd-warudo-converter/` 폴더 포함 (`deploy.ps1`이 복사)

### lilToon 렌더링
- 셰이더 교체 후 `_Color` 반드시 white 초기화 (미설정 = 검정 렌더링)
- 셰이더 이름 다중 탐색: `"lilToon"` → `"lilToon/lilToon"` → `"_lil/lilToon"`
- Cutout: `_ALPHATEST_ON` keyword + renderQueue=2450
- Transparent: `_ALPHABLEND_ON` + SrcBlend/DstBlend + renderQueue=3000

### 의상 바인딩 원칙
- `smr.bones` 교체만 수행
- `bindposes` (boneInverses) 절대 재계산 금지
- 재계산 시 rest pose 뒤틀림 발생

### 텍스처 OOM 방지
- 4K 텍스처 → 1024x1024 다운샘플 (RenderTexture 방식)
- 순차 로드 (병렬 X)
- `bytes = null` GC 해제

### UI 뷰포트 표시
- UIDocument가 3D씬을 덮는 문제
- 해결: `viewer-area`와 `root` 의 `background-color` 제거 (투명)
- PanelSettings: `clearDepthStencil=false`, `colorClearValue=transparent`

### Standalone 텍스트 렌더링
- Unity Standalone 기본 폰트에 CJK 글리프 없음 → 한국어/일본어 버튼 텍스트 미표시
- 해결: dresser.uxml 모든 텍스트 영문화

## 빌드 및 배포

### deploy.ps1 사용법
```powershell
# 전체 빌드 + 배포 (처음 실행 시 3~8분 소요)
.\deploy.ps1

# 이미 워밍업된 경우 워밍업 스킵
.\deploy.ps1 -SkipWarmup
```

### 배포 단계
1. Unity 헤들리스로 메인 앱 빌드 → `c:/vd/build/VirtualDresser.exe`
2. `vd-warudo-converter/` 를 `c:/vd/build/vd-warudo-converter/` 에 복사
3. converter 프로젝트 워밍업 (Warudo SDK 다운로드 + 스크립트 컴파일)
4. 완료 → `c:/vd/build/` 폴더가 배포 패키지

## 셋업 명령 (새 PC에서 Unity 프로젝트 초기 구성)
```bash
REPO="c:/Users/linix/OneDrive/업무용PC/vibe coding/virtual Dresser-Unity/virtual-dresser-unity"
SRC="c:/vd/virtual-dresser-app/Dresser/Assets/VirtualDresser"

mkdir -p "$SRC/Runtime" "$SRC/UI" "$SRC/Editor"
mkdir -p "c:/vd/virtual-dresser-app/Dresser/Assets/UI"
mkdir -p "c:/vd/virtual-dresser-app/Dresser/Assets/Resources/avatar-configs"

cp "$REPO/src/Runtime/"*.cs "$SRC/Runtime/"
cp "$REPO/src/UI/DresserUI.cs" "$SRC/UI/"
cp "$REPO/src/UI/dresser.uxml" "c:/vd/virtual-dresser-app/Dresser/Assets/UI/"
cp "$REPO/src/Editor/BatchImporter.cs" "$SRC/Editor/"
cp "$REPO/src/Editor/BuildScript.cs" "$SRC/Editor/"
cp "$REPO/src/Editor/SceneSetup.cs" "$SRC/Editor/"
cp "$REPO/src/Resources/avatar-configs/"*.json "c:/vd/virtual-dresser-app/Dresser/Assets/Resources/avatar-configs/"
cp "$REPO/Packages/manifest.json" "c:/vd/virtual-dresser-app/Dresser/Packages/manifest.json"
```

## 동기화 명령 (git push 전 실행)
```bash
REPO="c:/Users/linix/OneDrive/업무용PC/vibe coding/virtual Dresser-Unity/virtual-dresser-unity"
SRC="c:/vd/virtual-dresser-app/Dresser/Assets/VirtualDresser"

cp "$SRC/Runtime/FbxConverter.cs"             "$REPO/src/Runtime/FbxConverter.cs"
cp "$SRC/Runtime/MaterialManager.cs"           "$REPO/src/Runtime/MaterialManager.cs"
cp "$SRC/Runtime/MeshCombiner.cs"              "$REPO/src/Runtime/MeshCombiner.cs"
cp "$SRC/Runtime/WindowsFilePicker.cs"         "$REPO/src/Runtime/WindowsFilePicker.cs"
cp "$SRC/Runtime/UnitypackageParser.cs"        "$REPO/src/Runtime/UnitypackageParser.cs"
cp "$SRC/Runtime/AvatarConfig.cs"              "$REPO/src/Runtime/AvatarConfig.cs"
cp "$SRC/Runtime/CameraController.cs"          "$REPO/src/Runtime/CameraController.cs"
cp "$SRC/Runtime/UnityMainThreadDispatcher.cs" "$REPO/src/Runtime/UnityMainThreadDispatcher.cs"
cp "$SRC/Runtime/BoneMapper.cs"                "$REPO/src/Runtime/BoneMapper.cs"
cp "$SRC/Runtime/HeadlessLauncher.cs"          "$REPO/src/Runtime/HeadlessLauncher.cs"
cp "$SRC/Runtime/UnitySetupManager.cs"         "$REPO/src/Runtime/UnitySetupManager.cs"
cp "$SRC/UI/DresserUI.cs"                      "$REPO/src/UI/DresserUI.cs"
cp "$SRC/Editor/BatchImporter.cs"              "$REPO/src/Editor/BatchImporter.cs"
cp "$SRC/Editor/BuildScript.cs"                "$REPO/src/Editor/BuildScript.cs"
cp "$SRC/Editor/SceneSetup.cs"                 "$REPO/src/Editor/SceneSetup.cs"
cp "c:/vd/virtual-dresser-app/Dresser/Assets/UI/dresser.uxml" "$REPO/src/UI/dresser.uxml"
cp "c:/vd/virtual-dresser-app/Dresser/Packages/manifest.json" "$REPO/Packages/manifest.json"
```

## avatar-configs 위치
- **git 레포**: `src/Resources/avatar-configs/` (7종 포함, git 관리)
- **Unity 프로젝트**: `c:/vd/virtual-dresser-app/Dresser/Assets/Resources/avatar-configs/`
- 7종: manuka, moe, shinano, sio, mao, lumina, shinra
  - ⚠️ 표시명: Sio (내부 키: `shio` — config 파일명 및 DresserUI avatarIds 배열과 일치)
- 스키마: `src/Runtime/AvatarConfig.cs`

## 패키지 의존성

### 메인 앱 (c:/vd/virtual-dresser-app/Dresser/)
| 패키지 | 버전 | 설치 방법 |
|--------|------|-----------|
| TriLib 2 | 2.6.1 | Asset Store (구매 완료) |
| SharpZipLib | TriLib 내장 | 별도 불필요 |
| Newtonsoft.Json | 3.2.1 | manifest.json |
| URP | 12.1.13 (2021.3용) | manifest.json |

### vd-warudo-converter
| 패키지 | 버전 | 설치 방법 |
|--------|------|-----------|
| Warudo Mod Tool | 0.14.3.10 | manifest.json (git URL, 자동) |
| Newtonsoft.Json | 3.2.1 | manifest.json |
| URP | 12.1.13 | manifest.json |
