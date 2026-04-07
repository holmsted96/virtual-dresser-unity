// AvatarConfig.cs
// avatar-configs/*.json 스키마 C# 버전
// 현재 TypeScript bone-mapper.ts의 AvatarConfig 인터페이스 포팅
//
// 사용법:
//   var json = File.ReadAllText("avatar-configs/shinano.json");
//   var config = JsonUtility.FromJson<AvatarConfig>(json);
//   // 또는 Newtonsoft.Json 사용 (딕셔너리 지원)
//   var config = JsonConvert.DeserializeObject<AvatarConfig>(json);

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    /// <summary>
    /// avatar-configs/*.json 스키마
    /// 현재 7종 아바타 설정 파일과 100% 호환
    /// </summary>
    [Serializable]
    public class AvatarConfig
    {
        public string avatarId;
        public string displayName;
        public string displayNameKo;
        public string boothUrl;
        public string version;
        public string creator;
        public string armatureRoot;

        // NOTE: Unity JsonUtility는 Dictionary 미지원 → Newtonsoft.Json 필요
        // boneMap: { "Hips": ["Hips", "J_Bip_C_Hips", ...], ... }
        public Dictionary<string, List<string>> boneMap;

        public List<string> knownClothingPrefixes;
    }

    /// <summary>
    /// 모든 아바타 config를 로드하는 유틸리티
    /// 현재 bone-mapper.ts의 loadAllAvatarConfigs() 포팅
    /// </summary>
    public static class AvatarConfigLoader
    {
        private static Dictionary<string, AvatarConfig> _cache;

        public static Dictionary<string, AvatarConfig> LoadAll()
        {
            if (_cache != null) return _cache;

            _cache = new Dictionary<string, AvatarConfig>();

            // Resources 폴더에 avatar-configs/*.json 배치
            var configs = Resources.LoadAll<TextAsset>("avatar-configs");
            foreach (var textAsset in configs)
            {
                try
                {
                    // Newtonsoft.Json 필요 (JsonUtility는 Dictionary 미지원)
                    var config = Newtonsoft.Json.JsonConvert.DeserializeObject<AvatarConfig>(textAsset.text);
                    if (config?.avatarId != null)
                    {
                        _cache[config.avatarId] = config;
                        Debug.Log($"[AvatarConfig] 로드: {config.avatarId} ({config.displayNameKo})");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AvatarConfig] 파싱 실패: {textAsset.name} — {e.Message}");
                }
            }

            Debug.Log($"[AvatarConfig] 총 {_cache.Count}개 아바타 config 로드 완료");
            return _cache;
        }

        public static AvatarConfig Get(string avatarId)
        {
            var all = LoadAll();
            return all.TryGetValue(avatarId, out var cfg) ? cfg : null;
        }
    }
}
