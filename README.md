# Virtual Dresser — Unity Standalone 전환 계획

## 개요

현재 구조(Tauri + Three.js)의 미리보기 한계를 극복하기 위한
Unity Standalone + Headless 하이브리드 아키텍처 설계 문서.

```
현재:  .unitypackage → Rust(tar.gz 파싱) → Three.js 렌더링
목표:  .unitypackage → Unity Headless(에셋 변환) → Unity 런타임(정확 렌더)
```

## 폴더 구조

```
unity-standalone-plan/
├── README.md          ← 이 파일
├── PRD.md             ← 제품 요구사항 (Unity 버전)
├── ARCHITECTURE.md    ← 기술 아키텍처 + 파이프라인
├── REUSABLE.md        ← 현재 코드베이스에서 재활용 가능한 목록
└── src/
    ├── Runtime/       ← Unity 런타임 C# (빌드 앱에서 실행)
    │   ├── UnitypackageParser.cs   ← Rust lib.rs 로직 포팅
    │   ├── BoneMapper.cs           ← bone-mapper.ts 포팅
    │   ├── AvatarConfig.cs         ← avatar-configs/*.json 타입
    │   └── MaterialManager.cs      ← material-manager.ts 포팅
    ├── Editor/        ← Unity Editor C# (Headless 배치 모드)
    │   └── BatchImporter.cs        ← .unitypackage → AssetBundle 변환
    └── UI/            ← Unity UI Toolkit
        └── DresserUI.cs            ← LayerPanel / 머티리얼 탭 UI
```

## 핵심 전제

- **재활용**: `avatar-configs/*.json`, 본 매핑 로직, 파일 파싱 전략
- **신규**: Unity 렌더러, PhysBone, lilToon, UI Toolkit
- **버리는 것**: Three.js, React, Tauri WebView (Tauri IPC는 일부 유지 가능)
