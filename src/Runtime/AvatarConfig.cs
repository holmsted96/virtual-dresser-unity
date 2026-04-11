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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    /// <summary>
    /// boneMap의 "_comment_*" 키(string 값)를 무시하고 배열 값만 역직렬화.
    /// </summary>
    public class BoneMapConverter : JsonConverter<Dictionary<string, List<string>>>
    {
        public override Dictionary<string, List<string>> ReadJson(
            JsonReader reader, Type objectType,
            Dictionary<string, List<string>> existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            var dict = new Dictionary<string, List<string>>();
            var obj  = JObject.Load(reader);
            foreach (var prop in obj.Properties())
            {
                if (prop.Name.StartsWith("_comment", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.Type == JTokenType.Array)
                    dict[prop.Name] = prop.Value.ToObject<List<string>>();
            }
            return dict;
        }

        public override void WriteJson(JsonWriter writer,
            Dictionary<string, List<string>> value, JsonSerializer serializer)
            => serializer.Serialize(writer, value);
    }


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
        // BoneMapConverter가 _comment_* 키를 자동으로 무시
        [JsonConverter(typeof(BoneMapConverter))]
        public Dictionary<string, List<string>> boneMap;

        public List<string> knownClothingPrefixes;

        /// <summary>
        /// 아바타별 머티리얼 매핑 설정
        /// smrToMat: SMR 이름 → .mat 파일명
        /// matNameToMat: FBX 내부 mat 이름 → .mat 파일명
        /// </summary>
        public MaterialConfig materialConfig;
    }

    [Serializable]
    public class MaterialConfig
    {
        /// <summary>SMR 이름 → .mat 파일명 (파일명 기준 매칭용)</summary>
        public Dictionary<string, string> smrToMat;
        /// <summary>FBX 내부 mat 이름 → .mat 파일명 (TriLib mat.name 변환용)</summary>
        public Dictionary<string, string> matNameToMat;
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

        /// <summary>
        /// 특정 아바타 config만 로드. 캐시에 없으면 해당 파일만 읽음.
        /// LoadAll()과 달리 첫 호출 시 지정된 아바타만 파싱하므로 빠름.
        /// </summary>
        public static AvatarConfig Get(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId)) return null;

            // 이미 전체 캐시가 있으면 그대로 사용
            if (_cache != null)
                return _cache.TryGetValue(avatarId, out var cached) ? cached : null;

            // 전체 캐시 없으면 해당 아바타 JSON만 먼저 시도
            var asset = Resources.Load<TextAsset>($"avatar-configs/{avatarId}");
            if (asset != null)
            {
                try
                {
                    var config = Newtonsoft.Json.JsonConvert.DeserializeObject<AvatarConfig>(asset.text);
                    if (config?.avatarId != null)
                    {
                        // 부분 캐시 초기화 (이후 LoadAll()과 공유)
                        if (_cache == null) _cache = new Dictionary<string, AvatarConfig>();
                        _cache[config.avatarId] = config;
                        Debug.Log($"[AvatarConfig] 개별 로드: {config.avatarId} ({config.displayNameKo})");
                        return config;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AvatarConfig] 파싱 실패: {avatarId} — {e.Message}");
                }
            }

            // 개별 로드 실패 시 전체 로드로 폴백
            var all = LoadAll();
            return all.TryGetValue(avatarId, out var cfg) ? cfg : null;
        }
    }
}
