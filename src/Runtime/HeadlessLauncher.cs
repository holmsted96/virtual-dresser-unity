// HeadlessLauncher.cs
// Warudo 헤들리스 빌드 실행기 — Phase 4 구현 예정
// DresserUI의 고품질 모드에서 호출

using System;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public static class HeadlessLauncher
    {
        /// <summary>
        /// 헤들리스 Unity를 실행해 패키지를 AssetBundle로 변환.
        /// Phase 4에서 구현 예정 — 현재는 스텁.
        /// </summary>
        public static void RunImport(string packagePath, string outputPath, Action<string> onComplete)
        {
            Debug.LogWarning("[HeadlessLauncher] 아직 구현되지 않았습니다. (Phase 4 예정)");
            onComplete?.Invoke(null);
        }
    }
}
