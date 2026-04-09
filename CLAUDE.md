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
    Packages/manifest.json      ← Warudo SDK(app.warudo.modtool) 포함
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

---

## 완료된 작업

### Phase 1 — 파싱 + FBX 로드 ✅
- `UnitypackageParser.cs` — tar.gz 스트리밍 파싱 (OOM 방지)
- `.mat` 2패스 파싱: GUID 역매핑으로 정확한 텍스처-머티리얼 연결
  - ⚠️ YAML 리스트 항목 형식 `- _MainTex:` → `TrimStart('-', ' ')` 로 처리 (단순 StartsWith 안 됨)
- `MaterialTextureMap` — OrdinalIgnoreCase 딕셔너리 (TriLib 머티리얼명 대소문자 불일치 대응)
- `WindowsFilePicker.cs` — Editor: EditorUtility, Standalone: P/Invoke GetOpenFileName
- `FbxConverter.cs` — TriLib 2 기반 FBX 런타임 로드 (AnimationType.Legacy)
- `MeshCombiner.cs` — 의상 골격 바인딩 (bindposes 불변 원칙)

### Phase 2 — UI + 뷰포트 ✅
- `DresserUI.cs` — UI Toolkit 기반 메인 UI (Unity 2021.3 런타임)
- `dresser.uxml` — 레이아웃 (오른쪽 패널 300px, 왼쪽 투명 뷰포트), 전체 영문 텍스트
  - viewer-area에 Reset View 버튼 포함 (absolute 포지셔닝)
- 아바타/의상/헤어/머티리얼 분리 임포트 버튼 4종
- `CameraController.cs` — 마우스 오빗/줌/패닝
- `SceneSetup.cs` — Editor 메뉴로 씬 자동 구성 (VirtualDresser > Setup Scene)

### Phase 3 — 텍스처 + lilToon ✅
- `MaterialManager.cs` — 4K 텍스처 1024 다운샘플 + lilToon 셰이더 자동 적용
- lilToon 셰이더 버전별 다중 탐색 (v1/v2/`_lil` 접두사)
- 셰이더 교체 후 `_Color=white` 초기화 (미설정 시 검정 렌더링 방지)
- Cutout/Transparent 변형 keyword + renderQueue 자동 설정
- `_LightMinLimit=0.05` 기본값 (그림자 완전 검정 방지)
- 텍스처 매칭: GUID 직접 매핑 우선, 실패 시 유사도 점수 fallback
  - `FindMatInfo()` — 3단계 fallback: 직접일치 → prefix제거 → 부분문자열 포함
  - `SimilarityScore()` — tex_/t_ prefix, _d/_col/_mat suffix 제거 후 비교
  - 매칭 실패 시 콘솔에 상세 진단 로그 출력

### Phase 4 — 익스포트 ✅
- `WarudoBuildScript.cs` — vd-warudo-converter 헤들리스 빌드 스크립트
  - Warudo SDK 있으면 UMod 빌더 사용, 없으면 AssetBundle 폴백
- `WarudoHeadlessBuilder` (DresserUI 내부) — 헤들리스 Unity 실행 로직
  - ConverterProjectPath: Standalone = `../vd-warudo-converter`, Editor = git repo 경로 (`#if UNITY_EDITOR`)
  - 첫 실행 감지 (Library 폴더 유무) → 대기 안내 메시지 (3~5분)
- `deploy.ps1` — 빌드 + converter 배포 + 워밍업 자동화 (4단계)
- `UnitySetupManager.cs` — 앱 최초 실행 시 Unity Hub + 2021.3.45f2 자동 설치
- `UnityMainThreadDispatcher.cs` — 백그라운드 → 메인 스레드 디스패치
- Warudo SDK `app.warudo.modtool 0.14.3.10` — vd-warudo-converter manifest에 포함 (자동 다운로드)
  - ⚠️ 패키지명 주의: `app.warudo.modtool` (이전에 com.warudo.mod-tool 로 잘못 기재된 적 있음)

### Sprint B — boneMap 통합 ✅
- `MeshCombiner.cs` — AvatarConfig boneMap alias 역방향 맵 구축
- 본 매핑 2단계: 1) 이름 완전 일치, 2) alias 폴백
- `AutoHideOverlappingMeshes()` — 신발 착용 시 발톱/발가락 메시 자동 숨김
- `GetBodyMeshHint()` — 단일 바디 메시 감지 → UI 힌트 반환

### Sprint D — Windows Standalone 빌드 ✅
- `BuildScript.cs` — VirtualDresser > Build Windows 메뉴 (`c:/vd/build/` 출력)
- 창모드(Windowed) 빌드, 1280×800 기본 해상도, resizableWindow
- dresser.uxml 전체 영문화 (Standalone에서 CJK 폰트 없어 한국어/일본어 미표시 문제 해결)
- DragEnterEvent 등 Editor 전용 이벤트 `#if UNITY_EDITOR` 처리
- A-Pose / Arms Up 포즈 테스트 기능 추가 (`CameraController.cs`)
- 카메라 Reset View 버튼 (뷰포트 우상단 absolute 포지셔닝)

### Sprint E — 메시 편집 기능 ✅
- 메시 패널 각 항목: `[Hide/Show]` `[Del]` `[...]` 버튼 (영문, Standalone 호환)
- `[...]` 클릭 시 트랜스폼 인라인 편집 패널 토글
  - Pos / Rot / Sca 각 X/Y/Z 수치 입력 (TextField + float.TryParse, InvariantCulture)
  - X(빨강) / Y(초록) / Z(파랑) 색상 구분
  - Reset Transform 버튼
- ⚠️ `FloatField` 는 Unity 2021.3 런타임에 없음 → `TextField` + `float.TryParse` 사용
- ⚠️ `unityTextOverflow` 는 Unity 2022.2+ 전용 → 제거

### Sprint F — 임포트 UX + 빌드 최적화 ✅
- 임포트 로딩 오버레이: 진행률 게이지 표시 (파싱 중 UI 피드백)
- 아바타 완전 비가시 버그 수정: lilToon null 시 TriLib 기본 셰이더 유지 (Standard로 교체하면 Standalone strip)
- **lilToon 빌드 시간 최적화**:
  - Always Included Shaders에서 lilToon + URP/Lit 제거 (수천 변형 → 컴파일 1~3시간)
  - `ShaderVariantCollection` 방식으로 교체: 실제 사용 변형 6개만 컴파일 (수십 분)
  - `Assets/Resources/LilToonVariants.shadervariants` — 빌드 시 자동 생성
  - Graphics Settings Preloaded Shaders에 자동 등록

---

## 남은 작업

### 🔴 즉시 — e2e 테스트 (미완)
1. `VirtualDresser > Build Windows` → `c:/vd/build/VirtualDresser.exe` 생성 확인
2. `VirtualDresser.exe` 실행 → 아바타 임포트 → 텍스처 정상 렌더링 확인
3. 의상 임포트 → 본 바인딩 확인
4. "Export" 버튼 → `.warudo` 파일 생성 확인
5. Warudo에서 `.warudo` 로드 테스트

### 🔴 즉시 — 모에 텍스처 매칭 최종 확인
- 빌드 후 콘솔에서 `[MatMgr]` 로그 확인
- "GUID매핑 없음 → 유사도 매칭: 'XXX'" 로그가 나오면 matchKey와 맵 키 비교해서 추가 대응

---

## UX 개선 TODO (우선순위별)

### 🟡 MVP 바로 다음 — 핵심 UX

| 항목 | 설명 | 난이도 |
|------|------|--------|
| **Drag & Drop 파일 열기** | .unitypackage를 앱 창에 드래그해서 바로 열기. 현재 버튼 클릭만 가능. Standalone에서는 WinAPI로 구현 필요 | 중 |
| **카메라 뷰 프리셋** | Front / Side / Top 버튼으로 즉시 시점 전환. 현재는 마우스로만 조작 | 하 |
| **머티리얼 컬러 오버라이드** | 메시 패널에서 색상 피커로 특정 머티리얼 색상 변경. 아바타 커스터마이징 핵심 기능 | 중 |
| **메시 목록 검색/필터** | 의상이 많을 때 이름으로 검색. 현재는 스크롤만 가능 | 하 |

### 🟢 그 다음 — 완성도

| 항목 | 설명 | 난이도 |
|------|------|--------|
| **세션 저장/복원** | 마지막으로 로드한 아바타+의상 조합을 앱 재시작 후에도 복원 | 중 |
| **트랜스폼 Undo/Redo** | `[...]` 패널에서 수치 변경 후 Ctrl+Z 되돌리기 | 중 |
| **아웃핏 프리셋 저장** | 현재 의상 조합을 이름 붙여 저장하고 불러오기 | 중 |
| **뷰포트 전체화면 토글** | 오른쪽 패널 숨기고 캐릭터만 전체 화면으로 보기 | 하 |
| **메시 다중 선택** | 체크박스로 여러 메시 선택 후 일괄 Hide/Delete | 중 |

### 🔵 MVP 이후 — 품질

| 항목 | 설명 | 난이도 |
|------|------|--------|
| **UI 전반 디자인 고급화** | 컬러 시스템, 아이콘, 패딩, 폰트 크기 등 전반적 완성도 | 상 |
| **Warudo 빌드 진행 상태 UI** | Export 버튼 누른 후 백그라운드에서 돌아가는 vd-warudo-converter 진행상황을 실시간으로 표시. 현재는 아무 피드백 없음. 구현 방향: 헤들리스 Unity 프로세스의 stdout을 읽어 단계별 상태 표시 (SDK 다운로드 중 / 컴파일 중 / 빌드 중 / 완료), 예상 소요 시간 안내, 완료 시 토스트 알림 | 중 |
| **임포트 속도 개선** | 병렬 처리 검토, 텍스처 캐시 | 상 |
| **시나노 발 클리핑** | 단일 바디 메시라 자동 숨김 불가 → 메시 편집(Sprint E)으로 사용자가 직접 조정하도록 안내 추가 | 하 |
| **아바타 썸네일** | 메시 패널 각 항목에 작은 미리보기 이미지 | 상 |

---

## 중요 기술 결정 & 트러블슈팅

### FBX 로더: TriLib 2 채택 이유
- FBX2glTF 서브프로세스 크래시(0xC0000409) 해결 불가
- glTFast SkinnedMesh 스트림 경고 + OOM 문제
- TriLib = 서브프로세스 없음, 텍스처 자동 로드, BOOTH FBX 공식 지원

### Warudo 익스포트: 헤들리스 Unity 방식
- `.warudo` = UMod 2.0 AssetBundle (Unity 2021.3.45f2 전용)
- BuildPipeline = Editor 전용 → 런타임 직접 생성 불가
- 해결: Unity 2021.3 헤들리스로 별도 변환 프로젝트(vd-warudo-converter) 실행
- Warudo SDK 있으면 UMod 정식 빌드, 없으면 AssetBundle 폴백 (Warudo 호환)
- 배포 구조: exe 옆에 `vd-warudo-converter/` 포함 (`deploy.ps1`이 자동 복사)
- 첫 실행 시 SDK 다운로드 + 컴파일 3~8분 소요 (Library 폴더 없으면 자동 안내)

### lilToon Standalone 빌드 전략
- `Shader.Find("lilToon")` 은 런타임에 호출 → 빌드에 셰이더가 포함되어야 함
- Always Included Shaders 방식: 전체 변형 컴파일 → 첫 빌드 1~3시간 (❌ 비실용)
- **ShaderVariantCollection 방식**: 실제 사용 변형만 6개 등록 → 빌드 수십 분 (✅ 채택)
  - `Assets/Resources/LilToonVariants.shadervariants`
  - `BuildScript.cs > CreateLilToonVariantCollection()` 에서 빌드 전 자동 생성
  - Graphics Settings > Preloaded Shaders에 등록 → 빌드에 포함 보장
- lilToon이 null이면 셰이더 교체 금지 (TriLib 기본 셰이더 유지), `_Color=white` 만 설정

### lilToon 렌더링
- 셰이더 교체 후 `_Color` 반드시 white 초기화 (미설정 = 검정 렌더링)
- 셰이더 이름 다중 탐색: `"lilToon"` → `"lilToon/lilToon"` → `"_lil/lilToon"`
- Cutout: `_ALPHATEST_ON` keyword + renderQueue=2450
- Transparent: `_ALPHABLEND_ON` + SrcBlend/DstBlend + renderQueue=3000
- `_LightMinLimit=0.05` — 그림자 완전 검정 방지

### 텍스처 매칭 구조
1. `.mat` YAML에서 GUID 추출 → guidToPathname으로 실제 파일명 역매핑
   - YAML 구조: `- _MainTex:` → `TrimStart('-', ' ')` 후 StartsWith 검사 필수
2. `MaterialTextureMap[matName]` 직접 조회 (OrdinalIgnoreCase)
3. 실패 시 `FindMatInfo()` — prefix(FBX_/MTL_) 제거 → 부분문자열 포함 순 fallback
4. 최종 실패 시 `SimilarityScore()` 유사도 매칭 (prefix/suffix 제거 후 토큰 비교)
- 매칭 실패 시 콘솔 `[MatMgr]` 로그로 맵 키/텍스처 목록 출력

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

### Unity 2021.3 런타임 UI Toolkit 제약
- `FloatField` 없음 → `TextField` + `float.TryParse(InvariantCulture)` 로 대체
- `unityTextOverflow` 없음 (2022.2+ 전용) → 생략
- DragEnterEvent / DragLeaveEvent / DragPerformEvent 없음 → `#if UNITY_EDITOR` 처리
- Standalone 기본 폰트에 CJK 글리프 없음 → UI 텍스트 전체 영문화 필수

---

## 빌드 및 배포

### 빌드 방법 (Unity Editor에서)
```
VirtualDresser > Build Windows
```
→ `c:/vd/build/VirtualDresser.exe` 생성  
→ 첫 빌드: 10~20분 / 이후 빌드: 5~10분 (ShaderVariantCollection 덕분에 빠름)

### deploy.ps1 사용법 (헤들리스 자동화)
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

---

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

---

## avatar-configs 위치
- **git 레포**: `src/Resources/avatar-configs/` (7종 포함, git 관리)
- **Unity 프로젝트**: `c:/vd/virtual-dresser-app/Dresser/Assets/Resources/avatar-configs/`
- 7종: manuka, moe, shinano, sio, mao, lumina, shinra
  - ⚠️ 표시명: Sio (내부 키: `shio` — config 파일명 및 DresserUI avatarIds 배열과 일치)
- 스키마: `src/Runtime/AvatarConfig.cs`

---

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
| Warudo Mod Tool | 0.14.3.10 | manifest.json (git URL, 자동 다운로드) |
| Newtonsoft.Json | 3.2.1 | manifest.json |
| URP | 12.1.13 | manifest.json |
