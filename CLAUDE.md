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
    Editor/                     ← BatchImporter.cs (Editor 전용)
    Resources/avatar-configs/   ← shinano.json 등 7종 (git에 포함)
  Packages/
    manifest.json               ← Unity 패키지 목록
  vd-warudo-converter/          ← .warudo 빌드용 별도 Unity 2021.3 프로젝트
    Assets/WarudoConverter/Editor/WarudoBuildScript.cs

실제 Unity 프로젝트 경로 (로컬 전용, git에 없음):
  c:/vd/virtual-dresser-app/Dresser/    ← Unity 2021.3.45f2 프로젝트 (메인 + warudo 동일 버전)
    Assets/VirtualDresser/      ← src/ 파일들이 복사되는 위치
    Assets/UI/dresser.uxml
    Assets/Resources/avatar-configs/  ← src/Resources/avatar-configs/ 에서 복사
    Assets/TriLib/              ← TriLib 2.6.1 (에셋스토어 구매)
    Packages/manifest.json
```

> ⚠️ **중요**: 코드 편집은 `c:/vd/virtual-dresser-app/Dresser/Assets/VirtualDresser/` 에서 하고,
> git push 전에 `src/`로 수동 동기화 필요. (아래 동기화 명령 참고)

## 완료된 작업

### Phase 1 — 파싱 + FBX 로드
- ✅ `UnitypackageParser.cs` — tar.gz 스트리밍 파싱 (OOM 방지), .mat 파싱
- ✅ `WindowsFilePicker.cs` — Editor: EditorUtility, Standalone: P/Invoke GetOpenFileName
- ✅ `FbxConverter.cs` — TriLib 2 기반 FBX 런타임 로드 (AnimationType.Legacy)
- ✅ `MeshCombiner.cs` — 의상 골격 바인딩 (bindposes 불변 원칙)

### Phase 2 — UI + 뷰포트
- ✅ `DresserUI.cs` — UI Toolkit 기반 메인 UI
- ✅ `dresser.uxml` — 레이아웃 (오른쪽 패널 300px, 왼쪽 투명 뷰포트)
- ✅ 아바타/의상/헤어/머티리얼 분리 임포트 버튼 4종
- ✅ 레이어별 메시 패널 (아바타/의상/헤어 필터)
- ✅ `CameraController.cs` — 마우스 오빗/줌/패닝

### Phase 3 — 텍스처
- ✅ `MaterialManager.cs` — 4K 텍스처 1024 다운샘플 + 기존 머티리얼에 적용
- ✅ MainTex 매칭: 머티리얼 이름 유사도 점수 기반
- ✅ 노말맵/마스크 MainTex 후보 제외 로직

### Phase 4 — 익스포트 (진행 중)
- ✅ `BatchImporter.cs` — Editor용 익스포트 + Warudo 에셋 폴더 생성
- ✅ `WarudoBuildScript.cs` — vd-warudo-converter 헤들리스 빌드 스크립트
- ✅ `WarudoHeadlessBuilder` (DresserUI 내부) — 헤들리스 Unity 실행 로직
- ✅ `UnityMainThreadDispatcher.cs` — 백그라운드 → 메인 스레드 디스패치
- ⏳ **미완**: Unity 2021.3 설치 + vd-warudo-converter 프로젝트 셋업 필요

## 남은 작업

### 즉시 필요 (다른 PC 셋업)
1. Unity Hub에서 `2021.3.45f2` 설치 (Windows Build Support 모듈 포함)
2. Unity Hub → New Project → 3D(URP) → 경로: `c:/vd/virtual-dresser-app/`
3. 소스코드 복사 (아래 "셋업 명령" 참고)
4. TriLib 2 에셋스토어에서 재임포트 (구매 완료 상태)
5. Unity Hub에 `vd-warudo-converter` 프로젝트 추가 (경로: `virtual-dresser-unity/vd-warudo-converter/`)
6. Warudo SDK 설치 (선택, 없으면 AssetBundle 폴백 모드로 동작):
   - Package Manager → Add from git URL: `https://github.com/HakuyaLabs/Warudo-Mod-Tool.git#0.14.3.10`

### 개선 TODO (MVP 이후)
- 텍스처/머티리얼 매칭 정확도 향상 — 루미나 등 일부 아바타에서 깨짐 발생
  - `.mat` GUID 역매핑은 구현됨, 아직 완전하지 않음
  - 개선 방향: 파일명 유사도 fallback 로직 강화 / 사용자 수동 매칭 UI

### 다음 스프린트: B — AvatarConfig boneMap 통합
- `AvatarConfig.cs`의 boneMap 별칭 활용해 `MeshCombiner` 매핑 성공률 향상
- `shinano.json` 등 7종 config 파일: `Assets/Resources/avatar-configs/`
- 현재 `MeshCombiner`는 이름 완전 일치만 지원 → boneMap 별칭 fallback 추가

### 다음 스프린트: C — lilToon 셰이더
- BOOTH 아바타 원본 셰이더 지원
- 현재: URP/Lit 폴백 사용 (텍스처 기본 컬러만 표시)

### 다음 스프린트: D — Windows Standalone 빌드
- 빌드 후 `.exe` 실행 테스트
- WindowsFilePicker Standalone 경로 확인

### 다음 스프린트: E — 메시 편집 기능
- 개별 메시 **삭제** 버튼 (레이어 패널)
- 메시 **표시/숨김** 토글 (SkinnedMeshRenderer.enabled)
- 메시 **이동/회전/스케일** 조정 (Transform 직접 수정, 의상 미세 위치 보정용)
- ⚠️ 스킨드 메시 특성상 큰 이동은 본 계층과 어긋날 수 있음 → 미세 보정 용도로 제한
- 구현 위치: `DresserUI.cs` 메시 패널에 수치 입력 필드 추가

## 중요 기술 결정

### FBX 로더: TriLib 2 채택 이유
- FBX2glTF 서브프로세스 크래시(0xC0000409) 해결 불가
- glTFast SkinnedMesh 스트림 경고 + OOM 문제
- TriLib = 서브프로세스 없음, 텍스처 자동 로드, BOOTH FBX 공식 지원

### Warudo 익스포트: 헤들리스 Unity 방식
- `.warudo` = UMod 2.0 AssetBundle (Unity 2021.3.45f2 전용)
- BuildPipeline = Editor 전용 → 런타임 직접 생성 불가
- 해결: Unity 2021.3 헤들리스로 별도 변환 프로젝트 실행
- VRM 경로도 가능하나 유저가 `.warudo` 원함

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
cp "$SRC/UI/DresserUI.cs"                      "$REPO/src/UI/DresserUI.cs"
cp "$SRC/Editor/BatchImporter.cs"              "$REPO/src/Editor/BatchImporter.cs"
cp "c:/vd/virtual-dresser-app/Dresser/Assets/UI/dresser.uxml" "$REPO/src/UI/dresser.uxml"
cp "c:/vd/virtual-dresser-app/Dresser/Packages/manifest.json" "$REPO/Packages/manifest.json"
```

## avatar-configs 위치
- **git 레포**: `src/Resources/avatar-configs/` (7종 포함, git 관리)
- **Unity 프로젝트**: `c:/vd/virtual-dresser-app/Dresser/Assets/Resources/avatar-configs/` (셋업 시 복사)
- 7종: manuka, moe, shinano, shio, mao, lumina, shinra
- 스키마: `src/Runtime/AvatarConfig.cs`

## 패키지 의존성
| 패키지 | 버전 | 설치 방법 |
|--------|------|-----------|
| TriLib 2 | 2.6.1 | Asset Store (구매 완료) |
| SharpZipLib | TriLib 내장 | 별도 불필요 |
| Newtonsoft.Json | 3.2.1 | manifest.json |
| URP | 12.1.13 (2021.3용) | manifest.json |
