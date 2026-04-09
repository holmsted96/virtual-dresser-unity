# deploy.ps1 — VirtualDresser 빌드 + 배포 스크립트
#
# 사용법:
#   .\deploy.ps1
#
# 처리 순서:
#   1. Unity 헤들리스로 메인 앱 빌드 (c:/vd/build/)
#   2. vd-warudo-converter 프로젝트를 빌드 폴더 옆에 복사
#   3. converter 프로젝트 Unity 워밍업 (패키지 다운로드 + 스크립트 컴파일)
#      → 이 단계 없으면 첫 .warudo 빌드 시 Warudo SDK 다운로드 실패 가능
#
# 결과 폴더 구조:
#   c:/vd/build/
#     VirtualDresser.exe
#     VirtualDresser_Data/
#     vd-warudo-converter/          ← .warudo 헤들리스 빌드용 (SDK 포함)
#       Assets/
#       Library/                    ← 워밍업 후 생성 (재컴파일 불필요)
#       Packages/
#       ProjectSettings/

param(
    [string]$BuildDir     = "c:/vd/build",
    [string]$UnityExe     = "",
    [string]$ProjectPath  = "c:/vd/virtual-dresser-app/Dresser",
    [string]$RepoRoot     = $PSScriptRoot,
    [switch]$SkipWarmup   = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Unity.exe 탐색 ──
if (-not $UnityExe) {
    $hubEditorRoot = "C:\Program Files\Unity\Hub\Editor"
    if (Test-Path $hubEditorRoot) {
        $found = Get-ChildItem $hubEditorRoot -Directory |
                 Where-Object { $_.Name -like "2021.3*" } |
                 Select-Object -First 1
        if ($found) {
            $UnityExe = Join-Path $found.FullName "Editor\Unity.exe"
        }
    }
}

if (-not (Test-Path $UnityExe)) {
    Write-Error "Unity 2021.3.x를 찾을 수 없습니다. -UnityExe 파라미터로 경로를 지정하세요."
    exit 1
}

# ─────────────────────────────────────────
# [1/4] 메인 앱 빌드
# ─────────────────────────────────────────
Write-Host "[1/4] 메인 앱 빌드 시작..." -ForegroundColor Cyan
$buildLog = Join-Path $env:TEMP "vd-build.log"

$buildArgs = @(
    "-batchmode", "-nographics",
    "-projectPath", $ProjectPath,
    "-executeMethod", "VirtualDresser.Editor.BuildScript.BuildWindows",
    "-logFile", $buildLog,
    "-quit"
)

$proc = Start-Process -FilePath $UnityExe -ArgumentList $buildArgs -Wait -PassThru -NoNewWindow
if ($proc.ExitCode -ne 0) {
    Write-Host "--- Unity 빌드 로그 (마지막 50줄) ---" -ForegroundColor Yellow
    if (Test-Path $buildLog) { Get-Content $buildLog -Tail 50 }
    Write-Error "메인 앱 빌드 실패 (코드: $($proc.ExitCode))"
    exit 1
}
Write-Host "[1/4] 빌드 완료: $BuildDir\VirtualDresser.exe" -ForegroundColor Green

# ─────────────────────────────────────────
# [2/4] vd-warudo-converter 배포
# ─────────────────────────────────────────
$converterSrc  = Join-Path $RepoRoot "vd-warudo-converter"
$converterDest = Join-Path $BuildDir "vd-warudo-converter"

Write-Host "[2/4] vd-warudo-converter 배포: $converterDest" -ForegroundColor Cyan

$excludes = @("Library", "Temp", "obj", "Logs", ".vs")

if (Test-Path $converterDest) {
    # 이미 배포된 경우: Library/ 는 건드리지 않고 소스만 업데이트
    # ⚠️ Copy-Item folder $existingDest -Recurse 하면 $existingDest/folder/ 로 중첩됨
    #    → 폴더는 내용물(*) 을 복사해야 Assets/Assets/ 같은 중첩을 방지할 수 있음
    Get-ChildItem $converterSrc | Where-Object { $_.Name -notin $excludes } | ForEach-Object {
        $dest = Join-Path $converterDest $_.Name
        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            Copy-Item "$($_.FullName)\*" $dest -Recurse -Force
        } else {
            Copy-Item $_.FullName $dest -Force
        }
    }
} else {
    # 첫 배포: Library/ 없이 전체 복사
    New-Item -ItemType Directory -Path $converterDest -Force | Out-Null
    Get-ChildItem $converterSrc | Where-Object { $_.Name -notin $excludes } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $converterDest $_.Name) -Recurse -Force
    }
}
Write-Host "[2/4] 배포 완료" -ForegroundColor Green

# ─────────────────────────────────────────
# [3/4] converter 프로젝트 워밍업
#   - Warudo SDK(git URL) 다운로드
#   - 스크립트 컴파일
#   → 이 단계 완료 후 Library/ 캐시가 생성되어 헤들리스 빌드가 빠르게 실행됨
# ─────────────────────────────────────────
$libraryPath     = Join-Path $converterDest "Library"
$embeddedPkgDest = Join-Path $converterDest "Packages\app.warudo.modtool"

if ($SkipWarmup) {
    Write-Host "[3/4] 워밍업 스킵 (-SkipWarmup)" -ForegroundColor Yellow
} elseif (Test-Path $libraryPath) {
    Write-Host "[3/4] Library 캐시 존재 — 워밍업 스킵" -ForegroundColor Green
} else {
    Write-Host "[3/4] converter 워밍업 시작 (Warudo SDK 다운로드 + 컴파일, 3~8분 소요)..." -ForegroundColor Cyan
    Write-Host "      인터넷 연결이 필요합니다 (github.com)" -ForegroundColor Yellow

    $warmupLog = Join-Path $env:TEMP "vd-converter-warmup.log"

    # -batchmode + -quit 만으로 Unity를 열었다 닫으면 패키지 설치 + 스크립트 컴파일 완료
    $warmupArgs = @(
        "-batchmode", "-nographics",
        "-projectPath", $converterDest,
        "-logFile", $warmupLog,
        "-quit"
    )

    $warmupProc = Start-Process -FilePath $UnityExe -ArgumentList $warmupArgs -Wait -PassThru -NoNewWindow

    if ($warmupProc.ExitCode -ne 0) {
        Write-Host "--- 워밍업 로그 (마지막 30줄) ---" -ForegroundColor Yellow
        if (Test-Path $warmupLog) { Get-Content $warmupLog -Tail 30 }
        Write-Warning "워밍업 종료 코드 $($warmupProc.ExitCode) — 컴파일 오류일 수 있으나 빌드는 계속 진행합니다."
    } else {
        Write-Host "[3/4] 워밍업 완료 (Warudo SDK 설치됨)" -ForegroundColor Green
    }
}

# ─────────────────────────────────────────
# [3.5/4] Warudo SDK → embedded package 변환
#
#   목적: 헤들리스 빌드마다 Unity가 git fetch(~90초)를 하는 문제 제거.
#   방법: Library/PackageCache/app.warudo.modtool@.../  를
#         Packages/app.warudo.modtool/ 으로 복사 (embedded).
#         embedded package는 git URL 없이 로컬 디스크에서 바로 로드됨.
# ─────────────────────────────────────────
if (-not (Test-Path $embeddedPkgDest)) {
    $pkgCacheRoot = Join-Path $converterDest "Library\PackageCache"
    $cachedPkg    = Get-ChildItem $pkgCacheRoot -Directory -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like "app.warudo.modtool@*" } |
                    Select-Object -First 1

    if ($cachedPkg) {
        Write-Host "[3.5/4] Warudo SDK → embedded package 변환 (git fetch 90초 제거)..." -ForegroundColor Cyan
        $embeddedParent = Split-Path $embeddedPkgDest -Parent
        if (-not (Test-Path $embeddedParent)) { New-Item -ItemType Directory -Path $embeddedParent -Force | Out-Null }
        Copy-Item $cachedPkg.FullName $embeddedPkgDest -Recurse -Force
        Write-Host "[3.5/4] embedded package 완료: $embeddedPkgDest" -ForegroundColor Green
        Write-Host "        다음 빌드부터 Package Manager 해석 시간 ~90초 절약됩니다." -ForegroundColor Green
    } else {
        Write-Host "[3.5/4] PackageCache에 app.warudo.modtool 없음 — 스킵 (워밍업 후 재시도)" -ForegroundColor Yellow
    }
} else {
    Write-Host "[3.5/4] Embedded package 이미 존재 — 스킵" -ForegroundColor Green
}

# ─────────────────────────────────────────
# [4/4] 완료 요약
# ─────────────────────────────────────────
Write-Host ""
Write-Host "====== 배포 완료 ======" -ForegroundColor Green
Write-Host "  실행 파일  : $BuildDir\VirtualDresser.exe"
Write-Host "  Converter  : $converterDest"
Write-Host ""
if (-not (Test-Path $libraryPath)) {
    Write-Host "NOTE: 워밍업이 완료되지 않았습니다." -ForegroundColor Yellow
    Write-Host "      첫 .warudo 빌드 시 3~8분 소요될 수 있습니다." -ForegroundColor Yellow
} elseif (Test-Path $embeddedPkgDest) {
    Write-Host "NOTE: .warudo 빌드 준비 완료. Package Manager 캐시 적용됨." -ForegroundColor Cyan
    Write-Host "      예상 빌드 시간: ~1분 (기존 ~3분에서 단축)." -ForegroundColor Cyan
} else {
    Write-Host "NOTE: .warudo 빌드 준비 완료. 빌드 시간 약 1~2분." -ForegroundColor Cyan
}
