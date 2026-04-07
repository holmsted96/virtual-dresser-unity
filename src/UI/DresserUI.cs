// DresserUI.cs
// Unity UI Toolkit 기반 메인 UI
// 현재 React 컴포넌트(LayerPanel, AvatarSelector)의 Unity 버전
//
// 의존성:
//   Unity 2022.3+ (UI Toolkit 내장)
//   Assets/UI/dresser.uxml  — 레이아웃
//   Assets/UI/dresser.uss   — 스타일

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using VirtualDresser.Runtime;

namespace VirtualDresser.UI
{
    /// <summary>
    /// 메인 Dresser UI 컨트롤러
    /// LayerPanel + AvatarSelector + MaterialTab의 Unity 버전
    /// </summary>
    public class DresserUI : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private Transform avatarRoot;   // 3D 씬의 아바타 루트

        // ─── UI 요소 참조 ───
        private VisualElement _root;
        private ListView _layerList;
        private ListView _meshList;
        private ListView _materialList;
        private TabView _tabView;
        private Label _parseStatusLabel;
        private Button _qualityToggleBtn;

        // ─── 앱 상태 ───
        private ParseResult _avatarParse;
        private ParseResult _clothingParse;
        private readonly Dictionary<string, bool> _meshVisibility = new();
        private readonly List<string> _sceneMaterials = new();
        private bool _highQualityMode = false;

        // ─── 레이어 슬롯 ───
        private static readonly string[] SlotTypes = { "avatar", "clothing", "hair", "material" };
        private readonly Dictionary<string, string> _slotNames = new();

        private void OnEnable()
        {
            _root = uiDocument.rootVisualElement;
            BindElements();
            SetupDragDrop();
            RenderAvatarSelector();
        }

        // ─── UI 요소 바인딩 ───

        private void BindElements()
        {
            _tabView    = _root.Q<TabView>("main-tabs");
            _layerList  = _root.Q<ListView>("layer-list");
            _meshList   = _root.Q<ListView>("mesh-list");
            _materialList = _root.Q<ListView>("material-list");
            _parseStatusLabel = _root.Q<Label>("parse-status");
            _qualityToggleBtn = _root.Q<Button>("quality-toggle");

            _qualityToggleBtn?.RegisterCallback<ClickEvent>(_ => ToggleQualityMode());

            // 아바타 선택 카드 버튼들
            var avatarIds = new[] { "manuka", "moe", "shinano", "shio", "mao", "lumina", "shinra" };
            foreach (var id in avatarIds)
            {
                var btn = _root.Q<Button>($"avatar-card-{id}");
                var capturedId = id;
                btn?.RegisterCallback<ClickEvent>(_ => SelectAvatar(capturedId));
            }
        }

        // ─── 드래그앤드롭 ───

        private void SetupDragDrop()
        {
            // Unity 런타임 D&D: OS 레벨 파일 드롭 이벤트
            // Unity 2022.3+에서 Windows 드래그앤드롭 지원
            _root.RegisterCallback<DragEnterEvent>(e => {
                _root.AddToClassList("drag-over");
                e.StopPropagation();
            });

            _root.RegisterCallback<DragLeaveEvent>(e => {
                _root.RemoveFromClassList("drag-over");
            });

            _root.RegisterCallback<DragPerformEvent>(e => {
                _root.RemoveFromClassList("drag-over");
                var paths = UnityEngine.Windows.File.GetDroppedFiles();
                if (paths?.Length > 0)
                    HandleFileDrop(paths[0]);
                e.StopPropagation();
            });
        }

        // ─── 파일 드롭 핸들러 ───

        private async void HandleFileDrop(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".unitypackage" && ext != ".zip") return;

            SetParseStatus($"파싱 중: {Path.GetFileName(filePath)}...");

            try
            {
                ParseResult result;
                if (ext == ".zip")
                    result = await UnitypackageParser.ParseZipAsync(filePath);
                else
                    result = await UnitypackageParser.ParseUnitypackageAsync(filePath);

                OnParseComplete(result, filePath);
            }
            catch (Exception e)
            {
                SetParseStatus($"오류: {e.Message}");
                Debug.LogError($"[DresserUI] 파싱 실패: {e}");
            }
        }

        private void OnParseComplete(ParseResult result, string filePath)
        {
            SetParseStatus($"완료: {result.DetectedType} ({result.Confidence:P0})");

            if (result.DetectedType == "avatar" || result.DetectedType == "unknown")
            {
                _avatarParse = result;
                _slotNames["avatar"] = result.DetectedName ?? Path.GetFileNameWithoutExtension(filePath);
                LoadAvatarFbx(result);
            }
            else if (result.DetectedType == "clothing")
            {
                _clothingParse = result;
                _slotNames["clothing"] = result.DetectedName ?? Path.GetFileNameWithoutExtension(filePath);
                LoadClothingFbx(result);
            }

            RefreshLayerList();
        }

        // ─── FBX 로드 ───

        private void LoadAvatarFbx(ParseResult result)
        {
            if (result.ExtractedFbxPaths.Count == 0) return;

            // Phase 1: TriLib으로 즉시 미리보기
            // TriLib.AssetLoaderOptions 설정 후 로드
            // var options = AssetLoader.CreateDefaultLoaderOptions();
            // AssetLoader.LoadModelFromFile(result.ExtractedFbxPaths[0], OnAvatarLoaded, null, null, options);

            Debug.Log($"[DresserUI] 아바타 FBX 로드: {result.ExtractedFbxPaths[0]}");

            // 텍스처 매칭은 현재 material-manager.ts 로직과 동일하게 C#으로 포팅
            // MaterialManager.ApplyTextures(avatarGameObject, result);

            // 메쉬 목록 → meshVisibility 초기화
            // avatarGameObject의 모든 SkinnedMeshRenderer 이름 수집
        }

        private void LoadClothingFbx(ParseResult result)
        {
            if (result.ExtractedFbxPaths.Count == 0) return;

            // BoneMapper로 아바타에 바인딩
            // var go = 로드된 의상 GameObject
            // BoneMapper.BindClothingToAvatar(skinnedMesh, avatarRoot);

            Debug.Log($"[DresserUI] 의상 FBX 로드: {result.ExtractedFbxPaths[0]}");
        }

        // ─── 고품질 모드 토글 ───

        private void ToggleQualityMode()
        {
            _highQualityMode = !_highQualityMode;
            _qualityToggleBtn.text = _highQualityMode ? "🔬 고품질 ON" : "⚡ 즉시 미리보기";

            if (_highQualityMode && _avatarParse != null)
            {
                // Unity Headless 실행
                var packagePath = _avatarParse.TempDirPath; // 원본 경로 별도 저장 필요
                var outputPath = GetCachePath(_avatarParse.Filename);
                HeadlessLauncher.RunImport(packagePath, outputPath, OnHighQualityReady);
            }
        }

        private void OnHighQualityReady(string bundlePath)
        {
            // AssetBundle 로드 → 고품질 렌더
            Debug.Log($"[DresserUI] 고품질 번들 로드: {bundlePath}");
        }

        // ─── UI 렌더 ───

        private void RenderAvatarSelector()
        {
            // TODO: UXML에서 아바타 카드 7종 동적 생성
        }

        private void RefreshLayerList()
        {
            // LayerPanel 상당 — 레이어 탭 갱신
            // _layerList.itemsSource = 슬롯 데이터;
        }

        public void RefreshMeshList(List<string> meshNames)
        {
            // 메쉬 탭 갱신 (onMeshesFound 상당)
            foreach (var name in meshNames)
                _meshVisibility.TryAdd(name, true);

            _meshList.itemsSource = new List<string>(meshNames);
            _meshList.makeItem = () => {
                var row = new VisualElement();
                row.AddToClassList("mesh-row");
                row.Add(new Toggle());
                row.Add(new Label());
                row.Add(new Button(() => { }) { text = "🗑" });
                return row;
            };
            _meshList.bindItem = (elem, i) => {
                var meshName = (string)_meshList.itemsSource[i];
                var toggle = elem.Q<Toggle>();
                var label = elem.Q<Label>();
                var deleteBtn = elem.Q<Button>();

                toggle.value = _meshVisibility.GetValueOrDefault(meshName, true);
                label.text = meshName;
                toggle.RegisterValueChangedCallback(e => {
                    _meshVisibility[meshName] = e.newValue;
                    SetMeshVisible(meshName, e.newValue);
                });
                deleteBtn.clicked += () => DeleteMesh(meshName);
            };
        }

        public void RefreshMaterialList(List<string> materialNames)
        {
            // 머티리얼 탭 갱신 (onMaterialsFound / onMaterialsAppend 상당)
            _sceneMaterials.Clear();
            _sceneMaterials.AddRange(materialNames);

            _materialList.itemsSource = _sceneMaterials;
            _materialList.makeItem = () => {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                // Unity UI Toolkit에 기본 Color Picker 없음
                // → 커스텀 컬러 피커 구현 필요 (또는 패키지 사용)
                row.Add(new Button(() => { }) { text = "🎨" }); // 임시
                row.Add(new Label());
                return row;
            };
        }

        // ─── 씬 조작 ───

        private void SetMeshVisible(string meshName, bool visible)
        {
            if (avatarRoot == null) return;
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
                if (renderer.name == meshName)
                    renderer.gameObject.SetActive(visible);
        }

        private void DeleteMesh(string meshName)
        {
            // 메쉬를 씬에서 영구 제거 (ThreePreview deleteMeshRef 상당)
            if (avatarRoot == null) return;
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.name == meshName)
                {
                    Destroy(renderer.gameObject);
                    _meshVisibility.Remove(meshName);
                    break;
                }
            }
        }

        private void SelectAvatar(string avatarId)
        {
            Debug.Log($"[DresserUI] 아바타 선택: {avatarId}");
            var config = AvatarConfigLoader.Get(avatarId);
            if (config != null)
                Debug.Log($"  → {config.displayNameKo} 설정 로드됨");
        }

        private void SetParseStatus(string msg)
        {
            if (_parseStatusLabel != null)
                _parseStatusLabel.text = msg;
        }

        private static string GetCachePath(string filename)
        {
            var hash = filename.GetHashCode().ToString("x8");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VirtualDresser", "cache", hash
            );
        }
    }

    // ─── Unity Headless 런처 ───

    public static class HeadlessLauncher
    {
        private static string _unityEditorPath;

        public static void RunImport(string packagePath, string outputPath, Action<string> onComplete)
        {
            _unityEditorPath = FindUnityEditor();
            if (string.IsNullOrEmpty(_unityEditorPath))
            {
                Debug.LogWarning("[HeadlessLauncher] Unity Editor 미발견 — 고품질 모드 불가");
                return;
            }

            var projectPath = GetUnityProjectPath();
            var args = $"-batchmode -nographics " +
                       $"-projectPath \"{projectPath}\" " +
                       $"-executeMethod VirtualDresser.Editor.BatchImporter.ImportFromArgs " +
                       $"-packagePath \"{packagePath}\" " +
                       $"-outputPath \"{outputPath}\" " +
                       $"-quit";

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _unityEditorPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            proc.Exited += (_, __) => {
                if (proc.ExitCode == 0)
                    onComplete?.Invoke(outputPath);
                else
                    Debug.LogError($"[HeadlessLauncher] Unity 변환 실패 (코드 {proc.ExitCode})");
            };

            proc.Start();
            Debug.Log($"[HeadlessLauncher] Unity Headless 실행: {packagePath}");
        }

        private static string FindUnityEditor()
        {
            // Windows Unity Hub 기본 경로 탐색
            var hubPath = @"C:\Program Files\Unity\Hub\Editor";
            if (!Directory.Exists(hubPath)) return null;

            foreach (var dir in Directory.GetDirectories(hubPath))
            {
                var exe = Path.Combine(dir, "Editor", "Unity.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        private static string GetUnityProjectPath()
        {
            // 앱과 함께 배포되는 Unity 프로젝트 경로
            return Path.Combine(Application.dataPath, "..", "vd-converter-project");
        }
    }
}
