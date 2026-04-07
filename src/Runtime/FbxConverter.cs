// FbxConverter.cs
// TriLib 2 기반 FBX 런타임 로더
//
// FBX2glTF + glTFast 방식 대신 TriLib을 사용:
//   - 서브프로세스 없음 → 크래시 없음
//   - 텍스처 자동 로드 (tempDir에 함께 추출된 텍스처 파일 자동 인식)
//   - BOOTH 아바타(VRM/FBX) 공식 지원
//
// 의존성: TriLib 2 (Unity Asset Store)

using System;
using System.IO;
using System.Threading.Tasks;
using TriLibCore;
using TriLibCore.General;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public static class FbxConverter
    {
        /// <summary>
        /// FBX 파일을 TriLib으로 비동기 로드하여 GameObject 반환.
        /// tempDir에 텍스처가 함께 있으면 자동으로 적용됨.
        /// </summary>
        public static Task<GameObject> LoadFbxAsync(string fbxPath, string displayName = null)
        {
            if (!File.Exists(fbxPath))
            {
                Debug.LogError($"[FbxConverter] FBX 파일 없음: {fbxPath}");
                return Task.FromResult<GameObject>(null);
            }

            var tcs  = new TaskCompletionSource<GameObject>();
            var name = displayName ?? Path.GetFileNameWithoutExtension(fbxPath);

            var options = AssetLoader.CreateDefaultLoaderOptions();
            options.ImportTextures         = true;   // 텍스처 자동 로드
            options.AddAssetUnloader       = false;  // 수명 직접 관리
            options.AnimationType          = TriLibCore.General.AnimationType.Legacy; // None이면 본 생성 안 됨
            options.GenerateColliders      = false;

            Debug.Log($"[FbxConverter] TriLib 로드 시작: {name}");

            AssetLoader.LoadModelFromFile(
                fbxPath,
                onLoad: null,               // 메시 로드됨 (텍스처 미적용)
                onMaterialsLoad: ctx =>     // 텍스처까지 모두 완료
                {
                    if (ctx?.RootGameObject == null)
                    {
                        tcs.TrySetException(new Exception("TriLib: RootGameObject가 null"));
                        return;
                    }
                    ctx.RootGameObject.name = name;
                    Debug.Log($"[FbxConverter] TriLib 로드 완료: {name}");
                    tcs.TrySetResult(ctx.RootGameObject);
                },
                onProgress: null,
                onError: err =>
                {
                    var ex = err?.GetInnerException()
                             ?? new Exception($"TriLib 로드 실패: {err}");
                    Debug.LogError($"[FbxConverter] {ex.Message}");
                    tcs.TrySetException(ex);
                },
                wrapperGameObject: null,
                assetLoaderOptions: options
            );

            return tcs.Task;
        }

        // TriLib은 변환 캐시가 필요 없음
        public static bool IsCached(string fbxPath) => false;
        public static void ClearCache() { }
    }
}
