// UnitySetupManager.cs
// 첫 실행 시 Unity 2021.3.45f2 자동 감지 + 설치
//
// 흐름:
//   IsUnityInstalled() → false
//   → InstallAsync(onProgress, onComplete) 호출
//   → Unity Hub 다운로드 → 무음 설치
//   → Unity Hub CLI로 Unity 2021.3.45f2 설치
//   → 완료 콜백

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public static class UnitySetupManager
    {
        // ─── 상수 ───
        public  const string UnityVersion   = "2021.3.45f2";
        private const string UnityChangeset = "9b9180224418";   // Unity 2021.3.45f2 공식 changeset

        private const string HubDownloadUrl =
            "https://public-cdn.cloud.unity3d.com/hub/prod/UnityHubSetup.exe";

        private static readonly string HubInstallerPath =
            Path.Combine(Path.GetTempPath(), "UnityHubSetup.exe");

        // Unity Hub 설치 경로 (기본값)
        private static readonly string[] HubExePaths =
        {
            @"C:\Program Files\Unity Hub\Unity Hub.exe",
            @"C:\Program Files (x86)\Unity Hub\Unity Hub.exe",
        };

        // ─── 공개 API ───

        /// <summary>
        /// Unity 2021.3.x가 설치되어 있는지 확인.
        /// </summary>
        public static bool IsUnityInstalled()
            => !string.IsNullOrEmpty(FindUnityExe());

        /// <summary>
        /// Unity Hub + Unity 2021.3.45f2 설치.
        /// onProgress: (0.0 ~ 1.0, 메시지)
        /// onComplete: (성공여부, 에러메시지)
        /// </summary>
        public static async Task InstallAsync(
            Action<float, string> onProgress,
            Action<bool, string> onComplete,
            CancellationToken ct = default)
        {
            try
            {
                // ── 1단계: Unity Hub 설치 여부 확인 ──
                var hubExe = FindHubExe();
                if (hubExe == null)
                {
                    onProgress?.Invoke(0.05f, "Unity Hub 다운로드 중...");
                    await DownloadFileAsync(HubDownloadUrl, HubInstallerPath, onProgress, 0f, 0.3f, ct);

                    onProgress?.Invoke(0.32f, "Unity Hub 설치 중... (잠시 기다려주세요)");
                    await RunProcessAsync(HubInstallerPath, "/S", ct);   // NSIS 무음 설치

                    // 설치 후 잠시 대기 (Hub 서비스 초기화)
                    await Task.Delay(3000, ct);
                    hubExe = FindHubExe();

                    if (hubExe == null)
                    {
                        onComplete?.Invoke(false, "Unity Hub 설치에 실패했습니다.\n수동으로 unity.com/download 에서 설치해주세요.");
                        return;
                    }
                }
                else
                {
                    onProgress?.Invoke(0.3f, "Unity Hub 확인 완료");
                }

                // ── 2단계: Unity 2021.3.45f2 설치 ──
                if (!IsUnityInstalled())
                {
                    onProgress?.Invoke(0.35f, $"Unity {UnityVersion} 설치 중...\n(약 2~5GB 다운로드, 시간이 걸립니다)");

                    // Unity Hub CLI: 에디터 + Windows Mono 빌드 지원 모듈만 설치
                    var hubArgs = $"-- --headless install " +
                                  $"--version {UnityVersion} " +
                                  $"--changeset {UnityChangeset} " +
                                  $"--module windows-mono";

                    // Hub CLI는 설치 완료까지 blocking (최대 20분)
                    await RunProcessAsync(hubExe, hubArgs, ct, timeoutMs: 20 * 60 * 1000,
                        onStdout: line => onProgress?.Invoke(0.6f, $"설치 중...\n{line}"));

                    if (!IsUnityInstalled())
                    {
                        onComplete?.Invoke(false,
                            $"Unity {UnityVersion} 설치에 실패했습니다.\n" +
                            "Unity Hub를 직접 열어 2021.3.45f2를 설치해주세요.");
                        return;
                    }
                }

                onProgress?.Invoke(1f, $"Unity {UnityVersion} 설치 완료!");
                onComplete?.Invoke(true, null);
            }
            catch (OperationCanceledException)
            {
                onComplete?.Invoke(false, "설치가 취소됐습니다.");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[UnitySetup] 설치 오류: {ex}");
                onComplete?.Invoke(false, $"설치 오류: {ex.Message}");
            }
            finally
            {
                // 임시 설치 파일 정리
                try { if (File.Exists(HubInstallerPath)) File.Delete(HubInstallerPath); } catch { }
            }
        }

        // ─── 내부 유틸리티 ───

        public static string FindUnityExe()
        {
            var hubEditorPath = @"C:\Program Files\Unity\Hub\Editor";
            if (Directory.Exists(hubEditorPath))
            {
                foreach (var dir in Directory.GetDirectories(hubEditorPath))
                {
                    if (!Path.GetFileName(dir).StartsWith("2021.3")) continue;
                    var exe = Path.Combine(dir, "Editor", "Unity.exe");
                    if (File.Exists(exe)) return exe;
                }
            }

            // 대체 경로: 직접 설치된 경우
            var directPath = $@"C:\Program Files\Unity\2021.3\Editor\Unity.exe";
            if (File.Exists(directPath)) return directPath;

            return null;
        }

        private static string FindHubExe()
        {
            foreach (var path in HubExePaths)
                if (File.Exists(path)) return path;
            return null;
        }

        /// <summary>파일 다운로드 (진행률 포함)</summary>
        private static async Task DownloadFileAsync(
            string url, string destPath,
            Action<float, string> onProgress,
            float progressStart, float progressEnd,
            CancellationToken ct)
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var file   = File.Create(destPath);

            var buffer    = new byte[81920];
            long downloaded = 0;
            int  read;

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await file.WriteAsync(buffer, 0, read, ct);
                downloaded += read;

                if (total > 0)
                {
                    var ratio    = (float)downloaded / total;
                    var progress = progressStart + ratio * (progressEnd - progressStart);
                    var mb       = downloaded / 1024f / 1024f;
                    onProgress?.Invoke(progress, $"다운로드 중... {mb:F1} MB");
                }
            }
        }

        /// <summary>프로세스 실행 후 완료 대기</summary>
        private static Task RunProcessAsync(
            string exe, string args,
            CancellationToken ct,
            int timeoutMs = 5 * 60 * 1000,
            Action<string> onStdout = null)
        {
            var tcs = new TaskCompletionSource<bool>();

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = exe,
                    Arguments              = args,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = onStdout != null,
                    RedirectStandardError  = onStdout != null,
                },
                EnableRaisingEvents = true,
            };

            if (onStdout != null)
            {
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) onStdout(e.Data); };
                proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) onStdout(e.Data); };
            }

            proc.Exited += (_, __) => tcs.TrySetResult(proc.ExitCode == 0);

            ct.Register(() => { try { proc.Kill(); } catch { } tcs.TrySetCanceled(); });

            proc.Start();
            if (onStdout != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }

            // 타임아웃 처리
            Task.Delay(timeoutMs, ct).ContinueWith(_ =>
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                tcs.TrySetException(new TimeoutException($"프로세스 타임아웃: {Path.GetFileName(exe)}"));
            }, ct);

            return tcs.Task;
        }
    }
}
