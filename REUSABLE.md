# 재활용 가능한 코드 목록

현재 코드베이스에서 Unity 버전으로 직접 포팅/재사용 가능한 항목.

---

## ✅ 그대로 재사용 (수정 없음)

### avatar-configs/*.json
- 7개 아바타(마누카/모에/시나노/시오/마오/루미나/신라)의 본 매핑 데이터
- Unity C#에서 `JsonUtility.FromJson<AvatarConfig>()` 로 바로 로드 가능
- **위치**: `avatar-configs/shinano.json` 등

---

## 🔄 로직 재사용 (C# 포팅 필요)

### src-tauri/src/lib.rs → Runtime/UnitypackageParser.cs
- `.unitypackage` = tar.gz 압축 파일 파싱 로직
- `.zip` 내부 `.unitypackage` 탐색 로직
- FBX / 텍스처 / `.mat` 파일 추출
- GUID → pathname 매핑
- 재활용률: **약 80%** (알고리즘 동일, C#으로 문법만 변환)

```
Rust: flate2 + tar 크레이트
C#:  SharpZipLib 또는 System.IO.Compression
```

### src/engine/bone-mapper.ts → Runtime/BoneMapper.cs
- `mapUnityBonesToHumanoid()` — 본 이름 패턴 매핑 테이블
- `normalizeBoneName()`, `extractSide()`, `stringSimilarity()`
- `HUMANOID_BONE_CANDIDATES` 딕셔너리
- 재활용률: **약 90%** (순수 로직, UI 의존성 없음)

### src/engine/material-manager.ts → Runtime/MaterialManager.cs
- 머티리얼 이름 → 텍스처 파일 매칭 전략 (우선순위 6단계)
- 보조 텍스처 키워드 필터 (`mask`, `normal`, `shadow` 등)
- `.mat` YAML 파싱 결과 적용 로직
- 재활용률: **약 70%** (Three.js Material → Unity Material로 교체 필요)

### src/engine/mesh-combiner.ts → Runtime/MeshCombiner.cs
- 의상 SkinnedMesh → 아바타 Skeleton 바인딩 전략
- bone-inverse 보존 로직 (boneInverses 클론)
- nearest-bone fallback 알고리즘
- 재활용률: **약 75%**

---

## 🗑 버리는 것 (Unity에서 완전 대체)

| 현재 | 대체 |
|------|------|
| Three.js FBXLoader | Unity FBX Importer (Editor) / TriLib (Runtime) |
| React 컴포넌트 | Unity UI Toolkit (UXML/USS) |
| Tauri WebView | Unity 빌드 앱 |
| Three.js 머티리얼 | Unity MeshRenderer + lilToon |
| 프로시저럴 애니메이션 | Unity Animator + AnimationClip |
| PhysBone 시뮬 없음 | Unity VRC PhysBone 컴포넌트 |

---

## 📦 의존성 비교

### 현재
```
Rust: flate2, tar, zip, serde, tauri
JS:   three.js, @pixiv/three-vrm (미사용), react
```

### Unity 버전
```
Unity 패키지:
  - com.unity.render-pipelines.universal (URP) — 선택
  - lilToon (BOOTH 아바타 표준 셰이더)
  - VRC SDK (PhysBone 컴포넌트)
  - TriLib 2 (런타임 FBX 로드, 유료 $60) — 또는 GLB 변환 파이프라인
  - SharpZipLib (C# tar.gz 파싱)
  - Newtonsoft.Json (avatar-config.json 로드)
```
