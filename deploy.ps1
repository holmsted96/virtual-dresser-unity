# deploy.ps1 — VirtualDresser 빌드 + 배포 스크립트
#
# 사용법:
#   .\deploy.ps1
#
# 처리 순서:
#   1. Unity 헤들리스로 메인 앱 빌드 (c:/vd/build/)
#   2. vd-warudo-converter 프로젝트를 빌드 폴더 옆에 복사
#
# 결과 폴더 구조:
#   c:/vd/build/
#     VirtualDresser.exe
#     VirtualDresser_Data/
#     vd-warudo-converter/          ← .warudo 헤들리스 빌드용
#       Assets/
#       Packages/
#       ProjectSettings/

param(
    [string]$BuildDir     = "c:/vd/build",
    [string]$UnityExe     = "",
    [string]$ProjectPath  = "c:/vd/virtual-dresser-app/Dresser",
    [string]$RepoRoot     = $PSScriptRoot
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

Write-Host "[1/3] Unity 빌드 시작: $UnityExe" -ForegroundColor Cyan
$logFile = Join-Path $env:TEMP "vd-build.log"

$buildArgs = @(
    "-batchmode", "-nographics",
    "-projectPath", $ProjectPath,
    "-executeMethod", "VirtualDresser.Editor.BuildScript.BuildWindows",
    "-logFile", $logFile,
    "-quit"
)

$proc = Start-Process -FilePath $UnityExe -ArgumentList $buildArgs -Wait -PassThru -NoNewWindow
if ($proc.ExitCode -ne 0) {
    Write-Host "--- Unity 빌드 로그 (마지막 50줄) ---" -ForegroundColor Yellow
    if (Test-Path $logFile) { Get-Content $logFile -Tail 50 }
    Write-Error "빌드 실패 (종료 코드: $($proc.ExitCode))"
    exit 1
}
Write-Host "[1/3] 빌드 완료: $BuildDir\VirtualDresser.exe" -ForegroundColor Green

# ── vd-warudo-converter 복사 ──
$converterSrc  = Join-Path $RepoRoot "vd-warudo-converter"
$converterDest = Join-Path $BuildDir "vd-warudo-converter"

Write-Host "[2/3] vd-warudo-converter 배포: $converterDest" -ForegroundColor Cyan

if (Test-Path $converterDest) {
    # Library/ 캐시는 덮어쓰지 않음 (이미 컴파일된 경우)
    $excludes = @("Library", "Temp", "obj", "Logs")
    Get-ChildItem $converterSrc | Where-Object { $_.Name -notin $excludes } | ForEach-Object {
        $dest = Join-Path $converterDest $_.Name
        if ($_.PSIsContainer) {
            Copy-Item $_.FullName $dest -Recurse -Force
        } else {
            Copy-Item $_.FullName $dest -Force
        }
    }
} else {
    # 처음 배포 — Library/ 제외하고 전체 복사
    New-Item -ItemType Directory -Path $converterDest -Force | Out-Null
    Get-ChildItem $converterSrc | Where-Object { $_.Name -ne "Library" -and $_.Name -ne "Temp" } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $converterDest $_.Name) -Recurse -Force
    }
}
Write-Host "[2/3] vd-warudo-converter 배포 완료" -ForegroundColor Green

# ── 완료 요약 ──
Write-Host ""
Write-Host "[3/3] 배포 완료!" -ForegroundColor Green
Write-Host "  실행 파일 : $BuildDir\VirtualDresser.exe"
Write-Host "  Converter : $converterDest"
Write-Host ""
Write-Host "NOTE: .warudo 첫 빌드 시 Unity 스크립트 컴파일로 3~5분 소요됩니다." -ForegroundColor Yellow
