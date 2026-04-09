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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using VirtualDresser.Runtime;

namespace VirtualDresser.UI
{
    // ─── 데이터 모델 ───

    /// <summary>
    /// 메쉬 하나의 상태.
    /// 씬에서 삭제하지 않고 상태만 관리 → export 시 참조.
    /// </summary>
    public class MeshEntry
    {
        public string MeshName;
        public string LayerKey;          // "avatar" / "clothing" / "hair" / "material"
        public SkinnedMeshRenderer Renderer;
        public bool IsVisible;
        public bool IsDeleted;           // true면 export에서도 제외

        public MeshEntry(string meshName, string layerKey, SkinnedMeshRenderer renderer)
        {
            MeshName = meshName;
            LayerKey = layerKey;
            Renderer = renderer;
            IsVisible = true;
            IsDeleted = false;
        }
    }

    /// <summary>
    /// 레이어(소속 에셋) 단위 메쉬 그룹.
    /// 아바타 / 의상 / 헤어 드롭 각각이 MeshGroup 하나.
    /// </summary>
    public class MeshGroup
    {
        public string LayerKey;          // "avatar" / "clothing" / "hair"
        public string DisplayName;       // UI에 표시할 에셋 이름
        public List<MeshEntry> Entries = new();
        public bool IsExpanded = true;

        public MeshGroup(string layerKey, string displayName)
        {
            LayerKey = layerKey;
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// 메인 Dresser UI 컨트롤러
    /// LayerPanel + AvatarSelector + MaterialTab의 Unity 버전
    /// </summary>
    public class DresserUI : MonoBehaviour
    {
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private Transform avatarRoot;

        // ─── UI 요소 참조 ───
        private VisualElement _root;
        private VisualElement _meshPanelContainer;  // 메쉬 패널 전체 영역
        private ListView _materialList;
        private VisualElement _tabView;             // TabView는 2023.2+, 2022.3에서는 VisualElement로 대체
        private Label _parseStatusLabel;
        private Button _qualityToggleBtn;

        // ─── 로딩 오버레이 ───
        private VisualElement _loadingOverlay;
        private Label _loadingTitle;
        private Label _loadingStep;
        private VisualElement _progressFill;
        private Label _loadingPct;

        // ─── 앱 상태 ───
        private ParseResult _avatarParse;
        private ParseResult _clothingParse;
        private ParseResult _hairParse;
        private readonly List<string> _sceneMaterials = new();
        private bool _highQualityMode = false;
        private AvatarConfig _currentAvatarConfig;
        private PoseController _poseController;

        // ─── 레이어별 GameObject 참조 (머티리얼 격리에 사용) ───
        private GameObject _avatarGo;
        private GameObject _clothingGo;
        private GameObject _hairGo;

        // ─── 메쉬 그룹 (레이어별) ───
        // 순서 고정: avatar → clothing → hair
        private readonly List<MeshGroup> _meshGroups = new();
        private string _meshPanelFilter = "avatar";   // 메쉬 패널 현재 필터

        // ─── 레이어 슬롯 ───
        private static readonly string[] SlotTypes = { "avatar", "clothing", "hair", "material" };
        private readonly Dictionary<string, string> _slotNames = new();

        private void OnEnable()
        {
            // Inspector에서 미연결 시 같은 GameObject에서 자동 탐색
            if (uiDocument == null)
                uiDocument = GetComponent<UIDocument>();

            if (uiDocument == null)
            {
                Debug.LogError("[DresserUI] UIDocument 컴포넌트를 찾을 수 없습니다. DresserManager에 추가했는지 확인하세요.");
                return;
            }

            _root = uiDocument.rootVisualElement;

            if (_root == null)
            {
                Debug.LogError("[DresserUI] rootVisualElement가 null입니다. UIDocument의 Source Asset(dresser.uxml)이 연결됐는지 확인하세요.");
                return;
            }

            BindElements();
            CreateLoadingOverlay();
            SetupDragDrop();
            RenderAvatarSelector();

            // 첫 실행 시 Unity 설치 여부 확인 (Standalone 빌드에서만)
#if !UNITY_EDITOR
            CheckUnitySetupAsync();
#endif
        }

        // ─── Unity 자동 설치 ───

        private CancellationTokenSource _setupCts;

        private async void CheckUnitySetupAsync()
        {
            if (UnitySetupManager.IsUnityInstalled()) return;

            // 설치 안내 UI 표시
            ShowSetupOverlay();
        }

        private void ShowSetupOverlay()
        {
            // 기존 패널 위에 오버레이 동적 생성
            var overlay = new VisualElement();
            overlay.name = "setup-overlay";
            overlay.style.position   = Position.Absolute;
            overlay.style.top        = 0; overlay.style.left   = 0;
            overlay.style.right      = 0; overlay.style.bottom = 0;
            overlay.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.92f);
            overlay.style.justifyContent  = Justify.Center;
            overlay.style.alignItems      = Align.Center;

            var title = new Label("Warudo Export Setup Required");
            title.style.fontSize   = 18;
            title.style.color      = Color.white;
            title.style.marginBottom = 12;

            var desc = new Label(
                $"To export avatars to Warudo,\n" +
                $"Unity {UnitySetupManager.UnityVersion} is required.\n\n" +
                $"Click Install to set it up automatically.\n" +
                $"(~3-5GB download, 10-20 min)");
            desc.style.color      = new Color(0.8f, 0.8f, 0.8f);
            desc.style.fontSize   = 13;
            desc.style.whiteSpace = WhiteSpace.Normal;
            desc.style.unityTextAlign = TextAnchor.MiddleCenter;
            desc.style.marginBottom   = 20;

            var progressLabel = new Label("");
            progressLabel.name = "setup-progress-label";
            progressLabel.style.color      = new Color(0.6f, 1f, 0.6f);
            progressLabel.style.fontSize   = 11;
            progressLabel.style.marginBottom = 8;
            progressLabel.style.whiteSpace = WhiteSpace.Normal;
            progressLabel.style.unityTextAlign = TextAnchor.MiddleCenter;

            var progressBar = new VisualElement();
            progressBar.style.width           = 300;
            progressBar.style.height          = 6;
            progressBar.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            progressBar.style.marginBottom    = 20;
            var progressFill = new VisualElement();
            progressFill.name = "setup-progress-fill";
            progressFill.style.height          = 6;
            progressFill.style.width           = Length.Percent(0);
            progressFill.style.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            progressBar.Add(progressFill);

            var installBtn = new Button();
            installBtn.name = "setup-install-btn";
            installBtn.text = "Install Unity Automatically";
            installBtn.style.width  = 220;
            installBtn.style.height = 40;
            installBtn.style.fontSize = 14;
            installBtn.style.backgroundColor = new Color(0.2f, 0.6f, 0.2f);
            installBtn.style.marginBottom = 8;

            var skipBtn = new Button();
            skipBtn.text = "Later (export disabled)";
            skipBtn.style.fontSize = 11;
            skipBtn.style.color    = new Color(0.5f, 0.5f, 0.5f);

            overlay.Add(title);
            overlay.Add(desc);
            overlay.Add(progressLabel);
            overlay.Add(progressBar);
            overlay.Add(installBtn);
            overlay.Add(skipBtn);
            _root.Add(overlay);

            // 설치 버튼
            installBtn.RegisterCallback<ClickEvent>(_ =>
            {
                installBtn.SetEnabled(false);
                installBtn.text = "Installing...";
                skipBtn.SetEnabled(false);

                _setupCts = new CancellationTokenSource();
                UnitySetupManager.InstallAsync(
                    onProgress: (ratio, msg) =>
                    {
                        // 백그라운드 스레드 → 메인 스레드
                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            progressFill.style.width  = Length.Percent(ratio * 100f);
                            progressLabel.text = msg;
                        });
                    },
                    onComplete: (success, error) =>
                    {
                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (success)
                            {
                                _root.Remove(overlay);
                                SetParseStatus("Unity installed. Build / Export is now available.");
                            }
                            else
                            {
                                progressLabel.text = $"Error: {error}";
                                installBtn.SetEnabled(true);
                                installBtn.text = "Retry";
                                skipBtn.SetEnabled(true);
                            }
                        });
                    },
                    ct: _setupCts.Token
                );
            });

            // 건너뛰기 버튼
            skipBtn.RegisterCallback<ClickEvent>(_ => _root.Remove(overlay));
        }

        // ─── UI 요소 바인딩 ───

        private void BindElements()
        {
            _tabView            = _root.Q<VisualElement>("main-tabs");
            _meshPanelContainer = _root.Q<VisualElement>("mesh-panel");
            _materialList       = _root.Q<ListView>("material-list");
            _parseStatusLabel   = _root.Q<Label>("parse-status");
            _qualityToggleBtn   = _root.Q<Button>("quality-toggle");

            // 임포트 버튼 4종
            _root.Q<Button>("import-avatar-btn")
                ?.RegisterCallback<ClickEvent>(_ => OpenFileForType("avatar"));
            _root.Q<Button>("import-clothing-btn")
                ?.RegisterCallback<ClickEvent>(_ => OpenFileForType("clothing"));
            _root.Q<Button>("import-clothing-mat-btn")
                ?.RegisterCallback<ClickEvent>(_ => OpenFileForType("clothing-material"));
            _root.Q<Button>("import-hair-btn")
                ?.RegisterCallback<ClickEvent>(_ => OpenFileForType("hair"));

            // 품질 토글
            _qualityToggleBtn?.RegisterCallback<ClickEvent>(_ => ToggleQualityMode());

            // 카메라 리셋 버튼
            _root.Q<Button>("reset-view-btn")
                ?.RegisterCallback<ClickEvent>(_ =>
                {
                    var cam = Camera.main
                        ?? UnityEngine.Object.FindObjectsOfType<Camera>()?.FirstOrDefault();
                    cam?.GetComponent<VirtualDresser.Runtime.CameraController>()?.ResetView();
                });

            // 포즈 버튼
            _root.Q<Button>("pose-tpose-btn")
                ?.RegisterCallback<ClickEvent>(_ => _poseController?.ApplyTPose());
            _root.Q<Button>("pose-apose-btn")
                ?.RegisterCallback<ClickEvent>(_ => _poseController?.ApplyAPose());
            _root.Q<Button>("pose-armsup-btn")
                ?.RegisterCallback<ClickEvent>(_ => _poseController?.ApplyArmsUp());

            // 빌드 버튼
            _root.Q<Button>("build-btn")
                ?.RegisterCallback<ClickEvent>(_ => OnBuildButtonClicked());

            // 탭 버튼
            _root.Q<Button>("tab-btn-layer")
                ?.RegisterCallback<ClickEvent>(_ => SwitchTab("layer"));
            _root.Q<Button>("tab-btn-mesh")
                ?.RegisterCallback<ClickEvent>(_ => SwitchTab("mesh"));
            _root.Q<Button>("tab-btn-material")
                ?.RegisterCallback<ClickEvent>(_ => SwitchTab("material"));

            // 아바타 카드
            var avatarIds = new[] { "manuka", "moe", "shinano", "shio", "mao", "lumina", "shinra" };
            foreach (var id in avatarIds)
            {
                var btn = _root.Q<Button>($"avatar-card-{id}");
                var capturedId = id;
                btn?.RegisterCallback<ClickEvent>(_ => SelectAvatar(capturedId));
            }

            // 초기 탭: 레이어
            SwitchTab("layer");
        }

        // ─── 탭 전환 ───

        private void SwitchTab(string tabName)
        {
            SetPanelDisplay("tab-layer",       tabName == "layer");
            SetPanelDisplay("mesh-panel",      tabName == "mesh");
            SetPanelDisplay("material-panel",  tabName == "material");

            // 탭 버튼 활성 스타일
            var tabMap = new[] { ("tab-btn-layer", "layer"), ("tab-btn-mesh", "mesh"), ("tab-btn-material", "material") };
            foreach (var (btnName, tab) in tabMap)
            {
                var btn = _root.Q<Button>(btnName);
                if (btn == null) continue;
                if (tab == tabName) btn.AddToClassList("tab-btn--active");
                else                btn.RemoveFromClassList("tab-btn--active");
            }
        }

        private void SetPanelDisplay(string panelName, bool visible)
        {
            var panel = _root.Q<VisualElement>(panelName);
            if (panel != null)
                panel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ─── 파일 선택 ───

        /// <summary>
        /// 버튼별로 강제 타입을 지정해서 파일 열기.
        /// forcedType: "avatar" / "clothing" / "clothing-material" / "hair"
        /// </summary>
        private void OpenFileForType(string forcedType)
        {
            var path = WindowsFilePicker.OpenFile(
                title: "Select Package",
                filter: "Unity Package|*.unitypackage;*.zip|All Files|*.*"
            );
            if (!string.IsNullOrEmpty(path))
                HandleFileDrop(path, forcedType);
        }

        // ─── 드래그앤드롭 (시각 피드백만, 경로는 버튼으로 수신) ───

        private void SetupDragDrop()
        {
            // DragEnterEvent/DragLeaveEvent/DragPerformEvent는 Standalone 빌드 미지원
            // MVP: 버튼 클릭 방식으로 대체, 에디터에서만 시각 피드백 활성화
#if UNITY_EDITOR
            _root.RegisterCallback<DragEnterEvent>(e => {
                _root.AddToClassList("drag-over");
                e.StopPropagation();
            });

            _root.RegisterCallback<DragLeaveEvent>(e => {
                _root.RemoveFromClassList("drag-over");
            });

            _root.RegisterCallback<DragPerformEvent>(e => {
                _root.RemoveFromClassList("drag-over");
                e.StopPropagation();
            });
#endif
        }

        // ─── 파일 드롭 핸들러 ───

        private async void HandleFileDrop(string filePath, string forcedType = null)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".unitypackage" && ext != ".zip") return;

            var fileName = Path.GetFileName(filePath);
            SetParseStatus($"Parsing: {fileName}...");
            ShowLoading("Importing...", $"Parsing {fileName}", 0.05f);

            try
            {
                ParseResult result;
                if (ext == ".zip")
                    result = await UnitypackageParser.ParseZipAsync(filePath);
                else
                    result = await UnitypackageParser.ParseUnitypackageAsync(filePath);

                // 버튼으로 강제 타입 지정 시 감지 결과 덮어씌움
                if (!string.IsNullOrEmpty(forcedType))
                    result.DetectedType = forcedType;

                ShowLoading("Importing...", "Extracting assets...", 0.35f);
                OnParseComplete(result, filePath);
            }
            catch (Exception e)
            {
                HideLoading();
                SetParseStatus($"Error: {e.Message}");
                Debug.LogError($"[DresserUI] 파싱 실패: {e}");
            }
        }

        private void OnParseComplete(ParseResult result, string filePath)
        {
            var displayName = result.DetectedName ?? Path.GetFileNameWithoutExtension(filePath);
            SetParseStatus($"Detected: {result.DetectedType} ({result.Confidence:P0}) — {displayName}");

            if (result.DetectedType == "avatar")
            {
                _avatarParse = result;
                _slotNames["avatar"] = displayName;
                LoadAvatarFbx(result, displayName);
            }
            else if (result.DetectedType == "hair")
            {
                _slotNames["hair"] = displayName;
                LoadHairFbx(result, displayName);
            }
            else if (result.DetectedType == "clothing-material")
            {
                //  meshes 없이 머티리얼/텍스처만 기존 의상에 적용
                LoadClothingMaterialOnly(result, displayName);
            }
            else  // clothing + unknown
            {
                _clothingParse = result;
                _slotNames["clothing"] = displayName;
                LoadClothingFbx(result, displayName);
            }
        }

        // ─── FBX 로드 (FBX2glTF → glTFast 파이프라인) ───

        private async void LoadAvatarFbx(ParseResult result, string displayName)
        {
            if (result.ExtractedFbxPaths.Count == 0) { HideLoading(); return; }

            SetParseStatus($"Loading FBX: {displayName}...");
            ShowLoading("Importing Avatar", "Loading FBX model...", 0.45f);
            try
            {
                // 기존 아바타 제거
                if (avatarRoot != null)
                    foreach (Transform child in avatarRoot) Destroy(child.gameObject);

                var go = await FbxConverter.LoadFbxAsync(result.ExtractedFbxPaths[0], displayName);
                if (go == null) { HideLoading(); SetParseStatus("Avatar FBX load failed"); return; }
                go.transform.SetParent(avatarRoot, false);
                _avatarGo = go;

                // 아바타 config 로드 (boneMap alias 매칭에 사용)
                _currentAvatarConfig = result.DetectedName != null
                    ? AvatarConfigLoader.Get(result.DetectedName)
                    : null;
                if (_currentAvatarConfig != null)
                    Debug.Log($"[DresserUI] AvatarConfig 로드: {_currentAvatarConfig.avatarId}");
                else
                    Debug.LogWarning($"[DresserUI] AvatarConfig 없음 (DetectedName={result.DetectedName}) — 이름 완전 일치만 사용");

                ShowLoading("Importing Avatar", "Applying textures & materials...", 0.70f);
                await MaterialManager.ApplyTexturesAsync(go, result);

                ShowLoading("Importing Avatar", "Finalizing...", 0.92f);
                RegisterMeshGroup("avatar", displayName, go);
                FocusCameraOnAvatar(go);

                // 포즈 컨트롤러 초기화
                if (_poseController == null)
                    _poseController = gameObject.AddComponent<PoseController>();
                _poseController.SetAvatar(go, _currentAvatarConfig);

                HideLoading();
                SetParseStatus($"Avatar loaded: {displayName}");
            }
            catch (Exception e)
            {
                HideLoading();
                SetParseStatus($"Avatar load failed: {e.Message}");
                Debug.LogError($"[DresserUI] Avatar load failed: {e}");
            }
        }

        private async void LoadClothingFbx(ParseResult result, string displayName)
        {
            if (result.ExtractedFbxPaths.Count == 0) { HideLoading(); return; }

            SetParseStatus($"Loading clothing: {displayName}...");
            ShowLoading("Importing Clothing", "Loading FBX model...", 0.45f);
            try
            {
                var go = await FbxConverter.LoadFbxAsync(result.ExtractedFbxPaths[0], displayName);
                if (go == null) { HideLoading(); SetParseStatus("Clothing FBX load failed"); return; }
                go.transform.SetParent(avatarRoot, false);
                _clothingGo    = go;
                _clothingParse = result;

                ShowLoading("Importing Clothing", "Binding bones to avatar...", 0.62f);
                if (avatarRoot != null)
                {
                    var stats = MeshCombiner.BindClothingToAvatar(avatarRoot, go, _currentAvatarConfig);
                    Debug.Log($"[DresserUI] 의상 바인딩: {stats}");

                    var hidden = MeshCombiner.AutoHideOverlappingMeshes(avatarRoot, go);
                    if (hidden.Count > 0)
                        Debug.Log($"[DresserUI] 자동 숨김: {string.Join(", ", hidden)}");

                    var hint = MeshCombiner.GetBodyMeshHint(avatarRoot, go);
                    if (hint != null)
                        SetParseStatus($"Clothing loaded: {displayName}\n💡 {hint}");
                }

                ShowLoading("Importing Clothing", "Applying textures & materials...", 0.78f);
                await MaterialManager.ApplyTexturesAsync(go, result);

                ShowLoading("Importing Clothing", "Finalizing...", 0.92f);
                RegisterMeshGroup("clothing", displayName, go);

                HideLoading();
                SetParseStatus($"Clothing loaded: {displayName}");
            }
            catch (Exception e)
            {
                HideLoading();
                SetParseStatus($"Clothing load failed: {e.Message}");
                Debug.LogError($"[DresserUI] Clothing load failed: {e}");
            }
        }

        private async void LoadHairFbx(ParseResult result, string displayName)
        {
            if (result.ExtractedFbxPaths.Count == 0) { HideLoading(); return; }

            SetParseStatus($"Loading hair: {displayName}...");
            ShowLoading("Importing Hair", "Loading FBX model...", 0.45f);
            try
            {
                var go = await FbxConverter.LoadFbxAsync(result.ExtractedFbxPaths[0], displayName);
                if (go == null) { HideLoading(); SetParseStatus("Hair FBX load failed"); return; }
                go.transform.SetParent(avatarRoot, false);
                _hairGo    = go;
                _hairParse = result;

                ShowLoading("Importing Hair", "Binding bones to avatar...", 0.65f);
                if (avatarRoot != null)
                {
                    var stats = MeshCombiner.BindClothingToAvatar(avatarRoot, go, _currentAvatarConfig);
                    Debug.Log($"[DresserUI] 헤어 바인딩: {stats}");
                }

                ShowLoading("Importing Hair", "Applying textures & materials...", 0.80f);
                await MaterialManager.ApplyTexturesAsync(go, result);

                ShowLoading("Importing Hair", "Finalizing...", 0.92f);
                SuggestHideAvatarHair();
                RegisterMeshGroup("hair", displayName, go);

                HideLoading();
                SetParseStatus($"Hair loaded: {displayName}");
            }
            catch (Exception e)
            {
                HideLoading();
                SetParseStatus($"Hair load failed: {e.Message}");
                Debug.LogError($"[DresserUI] Hair load failed: {e}");
            }
        }

        /// <summary>
        /// 의상 머티리얼 전용 패키지 — FBX 없이 텍스처만 기존 의상 GO에 적용.
        /// </summary>
        private async void LoadClothingMaterialOnly(ParseResult result, string displayName)
        {
            // ★ _clothingGo만 사용 — 아바타/헤어에 영향 없음
            if (_clothingGo == null)
            {
                HideLoading();
                SetParseStatus("Import clothing mesh first");
                return;
            }

            SetParseStatus($"Applying material: {displayName}...");
            ShowLoading("Importing Material", "Applying textures & materials...", 0.70f);
            try
            {
                await MaterialManager.ApplyTexturesAsync(_clothingGo, result);
                HideLoading();
                SetParseStatus($"Material applied: {displayName}");
            }
            catch (Exception e)
            {
                HideLoading();
                SetParseStatus($"Material apply failed: {e.Message}");
                Debug.LogError($"[DresserUI] Material apply failed: {e}");
            }
        }

        // ─── 메쉬 그룹 등록 ───

        /// <summary>
        /// FBX 로드 완료 후 호출.
        /// GameObject의 모든 SkinnedMeshRenderer를 수집해서 그룹으로 등록.
        /// </summary>
        public void RegisterMeshGroup(string layerKey, string displayName, GameObject loadedGo)
        {
            // 기존 동일 레이어 그룹이 있으면 교체
            _meshGroups.RemoveAll(g => g.LayerKey == layerKey);

            var group = new MeshGroup(layerKey, displayName);

            foreach (var smr in loadedGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                group.Entries.Add(new MeshEntry(smr.name, layerKey, smr));
            }

            // 순서 보장: avatar → clothing → hair
            _meshGroups.Add(group);
            _meshGroups.Sort((a, b) =>
                Array.IndexOf(SlotTypes, a.LayerKey)
                    .CompareTo(Array.IndexOf(SlotTypes, b.LayerKey)));

            RefreshMeshPanel();
            RefreshLayerPanel();
            SwitchTab("layer");  // 로드 완료 후 레이어 탭으로 자동 전환
        }

        // ─── 레이어 패널 렌더 ───

        /// <summary>
        /// 레이어 탭의 슬롯 목록(아바타/의상/헤어)을 갱신.
        /// </summary>
        private void RefreshLayerPanel()
        {
            var layerList = _root.Q<VisualElement>("layer-list");
            if (layerList == null) return;
            layerList.Clear();

            foreach (var slot in new[] { "avatar", "clothing", "hair" })
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems    = Align.Center;
                row.style.marginBottom  = 6;
                row.style.paddingLeft   = 4;
                row.style.paddingRight  = 4;

                var group = _meshGroups.FirstOrDefault(g => g.LayerKey == slot);
                var loaded = group != null;

                // 슬롯 레이블
                var slotLabel = new Label(LayerDisplayName(slot));
                slotLabel.style.width     = 50;
                slotLabel.style.fontSize  = 11;
                slotLabel.style.color     = new StyleColor(new Color(0.6f, 0.6f, 0.6f));

                // 에셋 이름 or 빈 상태
                var nameLabel = new Label(loaded ? group.DisplayName : "— empty");
                nameLabel.style.flexGrow  = 1;
                nameLabel.style.fontSize  = 12;
                nameLabel.style.color     = loaded
                    ? new StyleColor(Color.white)
                    : new StyleColor(new Color(0.4f, 0.4f, 0.4f));

                row.Add(slotLabel);
                row.Add(nameLabel);

                // 로드된 경우  meshes 수 배지
                if (loaded)
                {
                    var badge = new Label($"{group.Entries.Count} meshes");
                    badge.style.fontSize        = 10;
                    badge.style.color           = new StyleColor(new Color(0.5f, 0.8f, 0.5f));
                    badge.style.marginLeft      = 4;
                    row.Add(badge);
                }

                layerList.Add(row);
            }
        }

        // ─── 메쉬 패널 렌더 ───

        /// <summary>
        /// 레이어별로 그루핑된 메쉬 패널 전체를 다시 그림.
        /// </summary>
        private void RefreshMeshPanel()
        {
            if (_meshPanelContainer == null) return;
            _meshPanelContainer.Clear();

            // ── 레이어 필터 버튼 ──
            var filterRow = new VisualElement();
            filterRow.style.flexDirection = FlexDirection.Row;
            filterRow.style.marginBottom  = 6;

            foreach (var slot in new[] { "avatar", "clothing", "hair" })
            {
                var s = slot;
                var hasGroup = _meshGroups.Any(g => g.LayerKey == s);
                var btn = new Button(() => { _meshPanelFilter = s; RefreshMeshPanel(); })
                {
                    text = LayerDisplayName(s)
                };
                btn.style.flexGrow   = 1;
                btn.style.height     = 24;
                btn.style.fontSize   = 10;
                btn.style.opacity    = hasGroup ? 1f : 0.4f;
                if (_meshPanelFilter == s)
                    btn.style.backgroundColor = new StyleColor(new Color(0.2f, 0.5f, 0.8f));
                filterRow.Add(btn);
            }
            _meshPanelContainer.Add(filterRow);

            // ── 현재 필터에 해당하는 그룹만 표시 ──
            var filtered = _meshGroups.Where(g => g.LayerKey == _meshPanelFilter);
            foreach (var group in filtered)
                _meshPanelContainer.Add(BuildMeshGroupElement(group));
        }

        private VisualElement BuildMeshGroupElement(MeshGroup group)
        {
            var container = new VisualElement();
            container.AddToClassList("mesh-group");

            // ── 그룹 헤더 ──
            var header = new VisualElement();
            header.AddToClassList("mesh-group-header");

            var foldout = new Label(group.IsExpanded ? "▼" : "▶");
            foldout.AddToClassList("mesh-foldout-icon");

            var groupLabel = new Label($"{LayerDisplayName(group.LayerKey)}: {group.DisplayName}");
            groupLabel.AddToClassList("mesh-group-label");

            // 그룹 전체 숨김/표시 토글
            var groupToggle = new Toggle { value = group.Entries.Any(e => e.IsVisible && !e.IsDeleted) };
            groupToggle.RegisterValueChangedCallback(e => SetGroupVisible(group, e.newValue));

            header.Add(foldout);
            header.Add(groupLabel);
            header.Add(groupToggle);

            // 헤더 클릭 → 접기/펼치기
            header.RegisterCallback<ClickEvent>(_ => {
                group.IsExpanded = !group.IsExpanded;
                RefreshMeshPanel();
            });

            container.Add(header);

            // ── 메쉬 목록 (접힌 경우 숨김) ──
            if (!group.IsExpanded) return container;

            var list = new VisualElement();
            list.AddToClassList("mesh-entry-list");

            foreach (var entry in group.Entries.Where(e => !e.IsDeleted))
            {
                list.Add(BuildMeshEntryElement(entry));
            }

            container.Add(list);
            return container;
        }

        private VisualElement BuildMeshEntryElement(MeshEntry entry)
        {
            var container = new VisualElement();
            container.style.marginBottom = 1;

            // ── 메인 행: [toggle] [name] [Hide/Show] [Del] [...] ──
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems    = Align.Center;
            row.style.paddingLeft   = 4;
            row.style.paddingRight  = 2;

            var toggle = new Toggle { value = entry.IsVisible };
            toggle.style.marginRight = 4;
            toggle.RegisterValueChangedCallback(e => SetMeshVisible(entry, e.newValue));

            var nameLabel = new Label(entry.MeshName);
            nameLabel.style.flexGrow           = 1;
            nameLabel.style.fontSize           = 11;
            nameLabel.style.overflow           = Overflow.Hidden;
            nameLabel.style.whiteSpace         = WhiteSpace.NoWrap;
            // unityTextOverflow: Unity 2022.2+ 전용 — 2021.3에서는 생략
            nameLabel.style.color              = entry.IsVisible
                ? new StyleColor(Color.white)
                : new StyleColor(new Color(0.4f, 0.4f, 0.4f));

            var hideBtn = new Button(() => SetMeshVisible(entry, !entry.IsVisible))
            {
                text = entry.IsVisible ? "Hide" : "Show"
            };
            hideBtn.style.width    = 38;
            hideBtn.style.height   = 20;
            hideBtn.style.fontSize = 10;

            var deleteBtn = new Button(() => DeleteMesh(entry)) { text = "Del" };
            deleteBtn.style.width           = 30;
            deleteBtn.style.height          = 20;
            deleteBtn.style.fontSize        = 10;
            deleteBtn.style.backgroundColor = new StyleColor(new Color(0.45f, 0.12f, 0.12f));

            // 트랜스폼 편집 패널 (기본 숨김, "..." 버튼으로 토글)
            var transformPanel = BuildTransformPanel(entry);
            transformPanel.style.display = DisplayStyle.None;

            var editBtn = new Button(() =>
            {
                var isOpen = transformPanel.style.display == DisplayStyle.Flex;
                transformPanel.style.display = isOpen ? DisplayStyle.None : DisplayStyle.Flex;
            }) { text = "..." };
            editBtn.style.width    = 24;
            editBtn.style.height   = 20;
            editBtn.style.fontSize = 10;

            row.Add(toggle);
            row.Add(nameLabel);
            row.Add(hideBtn);
            row.Add(deleteBtn);
            row.Add(editBtn);

            container.Add(row);
            container.Add(transformPanel);
            return container;
        }

        /// <summary>
        ///  meshes별 Position / Rotation / Scale 수치 편집 패널.
        /// 의상 미세 위치 보정용 (스킨드  meshes 특성상 큰 이동은 본 계층과 어긋남).
        /// </summary>
        private static VisualElement BuildTransformPanel(MeshEntry entry)
        {
            var panel = new VisualElement();
            panel.style.paddingLeft     = 22;
            panel.style.paddingRight    = 4;
            panel.style.paddingTop      = 4;
            panel.style.paddingBottom   = 4;
            panel.style.backgroundColor = new StyleColor(new Color(0.13f, 0.13f, 0.13f));
            panel.style.marginBottom    = 2;

            if (entry.Renderer == null)
            {
                panel.Add(new Label("(renderer null)") { style = { fontSize = 10 } });
                return panel;
            }

            var tf = entry.Renderer.transform;
            panel.Add(BuildVec3Row("Pos", tf.localPosition,
                (axis, v) => { var p = tf.localPosition; if (axis==0) p.x=v; else if (axis==1) p.y=v; else p.z=v; tf.localPosition = p; }));
            panel.Add(BuildVec3Row("Rot", tf.localEulerAngles,
                (axis, v) => { var r = tf.localEulerAngles; if (axis==0) r.x=v; else if (axis==1) r.y=v; else r.z=v; tf.localEulerAngles = r; }));
            panel.Add(BuildVec3Row("Sca", tf.localScale,
                (axis, v) => { var s = tf.localScale; if (axis==0) s.x=v; else if (axis==1) s.y=v; else s.z=v; tf.localScale = s; }));

            var resetBtn = new Button(() =>
            {
                tf.localPosition    = Vector3.zero;
                tf.localEulerAngles = Vector3.zero;
                tf.localScale       = Vector3.one;
            }) { text = "Reset Transform" };
            resetBtn.style.marginTop  = 4;
            resetBtn.style.height     = 20;
            resetBtn.style.fontSize   = 10;
            panel.Add(resetBtn);

            return panel;
        }

        private static VisualElement BuildVec3Row(
            string rowLabel, Vector3 initial, Action<int, float> onChange)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems    = Align.Center;
            row.style.marginBottom  = 2;

            var lbl = new Label(rowLabel);
            lbl.style.width    = 26;
            lbl.style.fontSize = 10;
            lbl.style.color    = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
            row.Add(lbl);

            var axisNames  = new[] { "X", "Y", "Z" };
            var axisValues = new[] { initial.x, initial.y, initial.z };
            var axisColors = new[] {
                new Color(0.85f, 0.3f, 0.3f),
                new Color(0.3f, 0.85f, 0.3f),
                new Color(0.3f, 0.5f, 0.9f)
            };

            for (int i = 0; i < 3; i++)
            {
                var idx = i;
                var axisLbl = new Label(axisNames[i]);
                axisLbl.style.width       = 10;
                axisLbl.style.fontSize    = 9;
                axisLbl.style.color       = new StyleColor(axisColors[i]);
                axisLbl.style.marginRight = 1;

                // FloatField는 Unity 2021.3 런타임에 없으므로 TextField + 파싱으로 대체
                var field = new TextField { value = axisValues[i].ToString("F3") };
                field.style.width    = 52;
                field.style.fontSize = 10;
                field.RegisterValueChangedCallback(e => {
                    if (float.TryParse(e.newValue,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var f))
                        onChange(idx, f);
                });

                row.Add(axisLbl);
                row.Add(field);
            }

            return row;
        }

        // ─── 메쉬 조작 ───

        /// <summary>
        /// 메쉬 가시성 토글. 씬에서 즉시 반영.
        /// </summary>
        private void SetMeshVisible(MeshEntry entry, bool visible)
        {
            entry.IsVisible = visible;
            if (entry.Renderer != null)
                entry.Renderer.gameObject.SetActive(visible);
            RefreshMeshPanel();
        }

        /// <summary>
        /// 그룹 전체 가시성 토글.
        /// </summary>
        private void SetGroupVisible(MeshGroup group, bool visible)
        {
            foreach (var entry in group.Entries.Where(e => !e.IsDeleted))
                SetMeshVisible(entry, visible);
        }

        /// <summary>
        /// 메쉬 삭제.
        /// 씬에서는 비활성화만 (Destroy 안 함) → export 시 IsDeleted 목록 참조.
        /// </summary>
        private void DeleteMesh(MeshEntry entry)
        {
            entry.IsDeleted = true;
            entry.IsVisible = false;

            // 씬에서는 비활성화만 — 실수로 삭제한 경우 재로드로 복구 가능하도록
            if (entry.Renderer != null)
                entry.Renderer.gameObject.SetActive(false);

            Debug.Log($"[DresserUI] 메쉬 삭제 마킹: {entry.MeshName} (레이어: {entry.LayerKey})");
            RefreshMeshPanel();
        }

        /// <summary>
        /// export 파이프라인에서 제외할 메쉬 이름 목록 반환.
        /// MeshCombiner, BatchImporter에서 참조.
        /// </summary>
        public IReadOnlyList<string> GetDeletedMeshNames()
        {
            return _meshGroups
                .SelectMany(g => g.Entries)
                .Where(e => e.IsDeleted)
                .Select(e => e.MeshName)
                .ToList();
        }

        // ─── 헤어 교체 감지 ───

        /// <summary>
        /// 헤어 에셋 드롭 시: 아바타 그룹에 헤어 메쉬가 있으면 숨김 제안.
        /// 헤어 관련 이름 키워드로 판별.
        /// </summary>
        private void SuggestHideAvatarHair()
        {
            var avatarGroup = _meshGroups.FirstOrDefault(g => g.LayerKey == "avatar");
            if (avatarGroup == null) return;

            var hairKeywords = new[] { "hair", "wig", "kami", "髪" };
            var avatarHairMeshes = avatarGroup.Entries
                .Where(e => !e.IsDeleted && hairKeywords.Any(k =>
                    e.MeshName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (avatarHairMeshes.Count == 0) return;

            // TODO: Unity UI Toolkit으로 확인 다이얼로그 표시
            // 지금은 자동 숨김으로 처리
            foreach (var entry in avatarHairMeshes)
                SetMeshVisible(entry, false);

            Debug.Log($"[DresserUI] 헤어 에셋 드롭 감지 → Avatar hair {avatarHairMeshes.Count}개 자동 숨김");
            SetParseStatus($"Avatar hair {avatarHairMeshes.Count} mesh(es) hidden. Re-enable in Mesh tab.");
        }

        // ─── 빌드 / 익스포트 ───

        private void OnBuildButtonClicked()
        {
            if (_avatarGo == null)
            {
                SetParseStatus("Import an avatar first");
                return;
            }

            // ── 준비 상태 확인 ──
            if (!WarudoHeadlessBuilder.IsReady(out var reason))
            {
                SetParseStatus($"⚠️ {reason}\nCheck setup guide.");
                Debug.LogWarning($"[DresserUI] Warudo 빌드 불가: {reason}");
                return;
            }

            var avatarName = _slotNames.TryGetValue("avatar", out var n) ? n : "VDExport";

            // ── 출력 경로 선택 ──
#if UNITY_EDITOR
            var outputPath = UnityEditor.EditorUtility.SaveFilePanel(
                "Save .warudo",
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                avatarName + ".warudo",
                "warudo");
#else
            var outputPath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                avatarName + ".warudo");
#endif
            if (string.IsNullOrEmpty(outputPath)) return;

            // ── 임시 에셋 폴더 준비 (기존 캐시 초기화) ──
            var inputPath = Path.Combine(Path.GetTempPath(), "vd-warudo-input", avatarName);
            if (Directory.Exists(inputPath)) Directory.Delete(inputPath, recursive: true);
            Directory.CreateDirectory(inputPath);

            // 아바타 에셋 복사
            CopyParseAssets(_avatarParse, inputPath);
            if (_clothingParse != null)
                CopyParseAssets(_clothingParse, Path.Combine(inputPath, "clothing"));
            if (_hairParse != null)
                CopyParseAssets(_hairParse, Path.Combine(inputPath, "hair"));

            // 숨김/삭제된 메시 목록을 manifest.json으로 저장
            // WarudoBuildScript가 읽어서 해당 SMR을 빌드에서 제외함
            WriteExcludeManifest(inputPath);

            // ── 헤들리스 빌드 실행 ──
            // 첫 실행 시 Unity 스크립트 컴파일로 3~5분 소요됨
            bool isFirstRun = !Directory.Exists(
                Path.Combine(WarudoHeadlessBuilder.ConverterProjectPath, "Library"));
            var waitMsg = isFirstRun
                ? "⏳ .warudo building...\n(First run: Unity compile ~3-5 min)"
                : "⏳ .warudo building... (~1-2 min)";
            SetParseStatus(waitMsg);
            Debug.Log($"[DresserUI] Warudo 헤들리스 빌드 시작: {outputPath}  (firstRun={isFirstRun})");

            WarudoHeadlessBuilder.BuildWarudo(
                inputPath, outputPath, avatarName,
                (result, error) =>
                {
                    // 콜백은 백그라운드 스레드에서 오므로 메인 스레드로 디스패치
                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        if (error == null)
                        {
                            SetParseStatus($"✅ .warudo created!\nSaved to: {outputPath}");
                            Debug.Log($"[DresserUI] 빌드 완료: {result}");
                        }
                        else
                        {
                            SetParseStatus($"❌ Build failed\n{error}");
                            Debug.LogError($"[DresserUI] 빌드 실패: {error}");
                        }
                    });
                });
        }

        /// <summary>
        /// 앱에서 숨기거나 삭제한 메시 이름 + 실제 머티리얼-텍스처 매핑을 manifest.json에 기록.
        /// WarudoBuildScript가 읽어서 정확한 텍스처를 머티리얼에 적용.
        /// </summary>
        private void WriteExcludeManifest(string inputPath)
        {
            // ── 숨김/삭제된 메시 목록 ──
            var excluded = _meshGroups
                .SelectMany(g => g.Entries)
                .Where(e => e.IsDeleted || !e.IsVisible)
                .Select(e => e.MeshName)
                .Distinct()
                .ToList();

            // ── 머티리얼 → 텍스처 매핑 ──
            var matTexMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _meshGroups.SelectMany(g => g.Entries))
            {
                if (entry.Renderer == null) continue;
                foreach (var mat in entry.Renderer.sharedMaterials)
                {
                    if (mat == null || matTexMap.ContainsKey(mat.name)) continue;
                    Texture2D tex = null;
                    if (mat.HasProperty("_MainTex"))
                        tex = mat.GetTexture("_MainTex") as Texture2D;
                    if (tex == null && mat.HasProperty("_BaseMap"))
                        tex = mat.GetTexture("_BaseMap") as Texture2D;
                    if (tex != null)
                        matTexMap[mat.name] = tex.name;
                }
            }

            // ── SMR 바인딩 정보 ──
            // 메인 앱에서 이미 올바르게 바인딩된 본 이름 순서를 기록
            // 컨버터는 이 순서대로 아바타 스켈레톤에서 본을 찾아 적용
            var smrBindings = new List<string>();
            foreach (var group in _meshGroups)
            {
                foreach (var entry in group.Entries)
                {
                    if (entry.IsDeleted || !entry.IsVisible) continue;
                    var smr = entry.Renderer;
                    if (smr == null || smr.sharedMesh == null) continue;

                    // 본 이름 배열 (null이면 "null" 로 직렬화)
                    var boneNames = smr.bones
                        .Select(b => b != null ? EscapeJson(b.name) : "null")
                        .ToArray();
                    var rootBoneName = smr.rootBone != null ? EscapeJson(smr.rootBone.name) : "null";

                    // 머티리얼 이름 배열
                    var matNames = smr.sharedMaterials
                        .Select(m => m != null ? EscapeJson(m.name) : "null")
                        .ToArray();

                    var bonesJson = "[ " + string.Join(", ", boneNames.Select(n => $"\"{n}\"")) + " ]";
                    var matsJson  = "[ " + string.Join(", ", matNames.Select(n => $"\"{n}\"")) + " ]";

                    smrBindings.Add(
                        $"    {{\n" +
                        $"      \"smrName\": \"{EscapeJson(smr.name)}\",\n" +
                        $"      \"layer\": \"{EscapeJson(group.LayerKey)}\",\n" +
                        $"      \"rootBone\": \"{rootBoneName}\",\n" +
                        $"      \"materials\": {matsJson},\n" +
                        $"      \"boneNames\": {bonesJson}\n" +
                        $"    }}");
                }
            }

            // ── JSON 조합 ──
            var excludedJson = string.Join(",\n", excluded.Select(n => $"    \"{EscapeJson(n)}\""));
            var matTexJson   = string.Join(",\n", matTexMap.Select(
                kv => $"    \"{EscapeJson(kv.Key)}\": \"{EscapeJson(kv.Value)}\""));
            var bindingsJson = string.Join(",\n", smrBindings);

            var json = "{\n" +
                       "  \"excludedMeshes\": [\n" + excludedJson + "\n  ],\n" +
                       "  \"materialTextures\": {\n" + matTexJson + "\n  },\n" +
                       "  \"smrBindings\": [\n" + bindingsJson + "\n  ]\n" +
                       "}";

            File.WriteAllText(Path.Combine(inputPath, "manifest.json"), json);
            Debug.Log($"[DresserUI] manifest: 제외 {excluded.Count}개, 머티리얼 {matTexMap.Count}개, SMR {smrBindings.Count}개");
        }

        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static void CopyParseAssets(ParseResult parse, string destDir)
        {
            if (parse == null || string.IsNullOrEmpty(parse.TempDirPath)) return;
            Directory.CreateDirectory(destDir);
            foreach (var fbx in parse.ExtractedFbxPaths)
                if (File.Exists(fbx))
                    File.Copy(fbx, Path.Combine(destDir, Path.GetFileName(fbx)), true);
            foreach (var tex in parse.ExtractedTextureNames)
            {
                var src = Path.Combine(parse.TempDirPath, tex);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(destDir, tex), true);
            }
        }

        // ─── 고품질 모드 ───

        private void ToggleQualityMode()
        {
            _highQualityMode = !_highQualityMode;
            _qualityToggleBtn.text = _highQualityMode ? "High Quality ON" : "Preview Mode";

            if (_highQualityMode && _avatarParse != null)
            {
                var packagePath = _avatarParse.TempDirPath;
                var outputPath = GetCachePath(_avatarParse.Filename);
                HeadlessLauncher.RunImport(packagePath, outputPath, OnHighQualityReady);
            }
        }

        private void OnHighQualityReady(string bundlePath)
        {
            Debug.Log($"[DresserUI] 고품질 번들 로드: {bundlePath}");
        }

        // ─── 머티리얼 패널 ───

        public void RefreshMaterialList(List<string> materialNames)
        {
            _sceneMaterials.Clear();
            _sceneMaterials.AddRange(materialNames);

            _materialList.itemsSource = _sceneMaterials;
            _materialList.makeItem = () => {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.Add(new Button(() => { }) { text = "🎨" });
                row.Add(new Label());
                return row;
            };
        }

        // ─── 아바타 선택 ───

        private void RenderAvatarSelector()
        {
            // TODO: UXML에서 아바타 카드 7종 동적 생성
        }

        private void SelectAvatar(string avatarId)
        {
            Debug.Log($"[DresserUI] 아바타 선택: {avatarId}");
            _currentAvatarConfig = AvatarConfigLoader.Get(avatarId);
            if (_currentAvatarConfig != null)
                Debug.Log($"  → {_currentAvatarConfig.displayNameKo} 설정 로드됨");

            // 이미 씬에 로드된 아바타가 있으면 PoseController 재연결
            if (_avatarGo != null)
            {
                if (_poseController == null)
                    _poseController = gameObject.AddComponent<PoseController>();
                _poseController.SetAvatar(_avatarGo, _currentAvatarConfig);
            }
        }

        // ─── 유틸리티 ───

        /// <summary>
        /// 로드된 아바타의 바운딩 박스를 계산해 카메라를 정면으로 이동.
        /// </summary>
        private static void FocusCameraOnAvatar(GameObject go)
        {
            // ── 1. 로드된 GO 상태 확인 ──
            var allTransforms = go.GetComponentsInChildren<Transform>(true);
            var allRenderers  = go.GetComponentsInChildren<Renderer>(true);
            var allSMR        = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            Debug.Log($"[DresserUI] GO hierarchy: Transform {allTransforms.Length}, " +
                      $"Renderer {allRenderers.Length}, SMR {allSMR.Length}");
            Debug.Log($"[DresserUI] GO 위치: {go.transform.position}, " +
                      $"Scale: {go.transform.localScale}, active: {go.activeInHierarchy}");

            // ── 2. 카메라 확인 ──
            var cam = Camera.main;
            if (cam == null)
            {
                var allCams = UnityEngine.Object.FindObjectsOfType<Camera>();
                Debug.LogWarning($"[DresserUI] Camera.main=null. 씬 내 카메라 수: {allCams.Length}");
                if (allCams.Length > 0) cam = allCams[0];
                else { Debug.LogError("[DresserUI] 씬에 카메라가 없음!"); return; }
            }
            Debug.Log($"[DresserUI] 사용 카메라: {cam.name}, 위치: {cam.transform.position}");

            // ── 3. 렌더러 없으면 GO 위치 기준으로 카메라 이동 ──
            if (allRenderers.Length == 0)
            {
                Debug.LogWarning("[DresserUI] 렌더러 없음 — GO 위치 기준으로 카메라 이동");
                cam.transform.position = go.transform.position + new Vector3(0f, 1f, 3f);
                cam.transform.LookAt(go.transform.position + Vector3.up);
                return;
            }

            // ── 4. 바운딩 박스 계산 ──
            var bounds = allRenderers[0].bounds;
            foreach (var r in allRenderers) bounds.Encapsulate(r.bounds);
            Debug.Log($"[DresserUI] 바운딩 박스: center={bounds.center}, size={bounds.size}");

            var center = bounds.center;
            var size   = bounds.size.magnitude;

            // CameraController가 있으면 FocusOnBounds 위임, 없으면 직접 이동
            var controller = cam.GetComponent<VirtualDresser.Runtime.CameraController>();
            if (controller != null)
            {
                controller.FocusOnBounds(bounds);
            }
            else
            {
                cam.transform.position = center + new Vector3(0f, bounds.size.y * 0.1f, size * 1.2f);
                cam.transform.LookAt(center);
            }
            Debug.Log($"[DresserUI] 카메라 포커스: center={center} size={size:F2}");
        }

        private void SetParseStatus(string msg)
        {
            if (_parseStatusLabel != null)
                _parseStatusLabel.text = msg;
        }

        // ─── 로딩 오버레이 ───

        private void CreateLoadingOverlay()
        {
            // 전체 화면 반투명 배경
            _loadingOverlay = new VisualElement();
            _loadingOverlay.style.position        = Position.Absolute;
            _loadingOverlay.style.top             = 0;
            _loadingOverlay.style.left            = 0;
            _loadingOverlay.style.width           = new StyleLength(new Length(100, LengthUnit.Percent));
            _loadingOverlay.style.height          = new StyleLength(new Length(100, LengthUnit.Percent));
            _loadingOverlay.style.backgroundColor = new Color(0.02f, 0.05f, 0.12f, 0.88f); // #0b1120 tinted
            _loadingOverlay.style.alignItems      = Align.Center;
            _loadingOverlay.style.justifyContent  = Justify.Center;
            _loadingOverlay.style.display         = DisplayStyle.None;
            _loadingOverlay.pickingMode           = PickingMode.Position; // 클릭 차단

            // 중앙 카드
            var card = new VisualElement();
            card.style.backgroundColor = new Color(0.067f, 0.122f, 0.208f, 1f); // #111f35
            card.style.borderTopLeftRadius     = 10;
            card.style.borderTopRightRadius    = 10;
            card.style.borderBottomLeftRadius  = 10;
            card.style.borderBottomRightRadius = 10;
            card.style.borderTopColor          = new Color(0.086f, 0.467f, 1f, 0.2f); // #1677ff 20%
            card.style.borderBottomColor       = new Color(0.086f, 0.467f, 1f, 0.2f);
            card.style.borderLeftColor         = new Color(0.086f, 0.467f, 1f, 0.2f);
            card.style.borderRightColor        = new Color(0.086f, 0.467f, 1f, 0.2f);
            card.style.borderTopWidth          = 1;
            card.style.borderBottomWidth       = 1;
            card.style.borderLeftWidth         = 1;
            card.style.borderRightWidth        = 1;
            card.style.paddingTop    = 30;
            card.style.paddingBottom = 30;
            card.style.paddingLeft   = 40;
            card.style.paddingRight  = 40;
            card.style.alignItems    = Align.Center;
            card.style.width         = 340;

            // 타이틀
            _loadingTitle = new Label("Importing...");
            _loadingTitle.style.fontSize   = 15;
            _loadingTitle.style.color      = new Color(1f, 1f, 1f, 0.92f);
            _loadingTitle.style.marginBottom = 5;
            _loadingTitle.style.unityFontStyleAndWeight = FontStyle.Bold;

            // 단계 설명
            _loadingStep = new Label("");
            _loadingStep.style.fontSize     = 11;
            _loadingStep.style.color        = new Color(0.086f, 0.467f, 1f, 0.65f); // blue-tinted
            _loadingStep.style.marginBottom = 20;

            // 게이지 트랙
            var track = new VisualElement();
            track.style.width           = 280;
            track.style.height          = 6;
            track.style.backgroundColor = new Color(1f, 1f, 1f, 0.07f);
            track.style.borderTopLeftRadius     = 3;
            track.style.borderTopRightRadius    = 3;
            track.style.borderBottomLeftRadius  = 3;
            track.style.borderBottomRightRadius = 3;
            track.style.marginBottom            = 10;
            track.style.overflow                = Overflow.Hidden;

            // 게이지 채움 (#1677ff)
            _progressFill = new VisualElement();
            _progressFill.style.height          = new StyleLength(new Length(100, LengthUnit.Percent));
            _progressFill.style.width           = 0;
            _progressFill.style.backgroundColor = new Color(0.086f, 0.467f, 1f, 1f); // #1677ff
            _progressFill.style.borderTopLeftRadius     = 3;
            _progressFill.style.borderTopRightRadius    = 3;
            _progressFill.style.borderBottomLeftRadius  = 3;
            _progressFill.style.borderBottomRightRadius = 3;
            track.Add(_progressFill);

            // 퍼센트 텍스트
            _loadingPct = new Label("0%");
            _loadingPct.style.fontSize = 10;
            _loadingPct.style.color    = new Color(1f, 1f, 1f, 0.3f);

            card.Add(_loadingTitle);
            card.Add(_loadingStep);
            card.Add(track);
            card.Add(_loadingPct);
            _loadingOverlay.Add(card);
            _root.Add(_loadingOverlay);
        }

        /// <summary>로딩 오버레이 표시. progress 0.0~1.0</summary>
        private void ShowLoading(string title, string step, float progress)
        {
            if (_loadingOverlay == null) return;
            _loadingOverlay.style.display = DisplayStyle.Flex;
            _loadingTitle.text = title;
            _loadingStep.text  = step;

            int pct = Mathf.RoundToInt(progress * 100f);
            _loadingPct.text   = $"{pct}%";
            _progressFill.style.width = new StyleLength(new Length(pct, LengthUnit.Percent));
        }

        private void HideLoading()
        {
            if (_loadingOverlay == null) return;
            _loadingOverlay.style.display = DisplayStyle.None;
        }

        private static string LayerDisplayName(string layerKey) => layerKey switch
        {
            "avatar"   => "Avatar",
            "clothing" => "Clothing",
            "hair"     => "Hair",
            "material" => "Material",
            _          => layerKey
        };

        /// <summary>
        /// SHA256 기반 캐시 경로 — ARCHITECTURE.md 명세 대로.
        /// </summary>
        private static string GetCachePath(string filename)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(filename));
            var hashStr = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()[..16];
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VirtualDresser", "cache", hashStr
            );
        }
    }

    // ─── Warudo 헤들리스 빌더 ───

    public static class WarudoHeadlessBuilder
    {
        // vd-warudo-converter 프로젝트 경로
        // Standalone 빌드: <exe폴더>/vd-warudo-converter/
        //   Application.dataPath = <exe폴더>/VirtualDresser_Data
        //   → ../vd-warudo-converter = <exe폴더>/vd-warudo-converter  ✓
        // Editor: <project>/Assets → ../../vd-warudo-converter 이지만 Editor에서는 직접 빌드 테스트용
        public static string ConverterProjectPath
        {
            get
            {
#if UNITY_EDITOR
                // 환경변수 VD_CONVERTER_PATH 설정 시 우선 사용
                var envPath = System.Environment.GetEnvironmentVariable("VD_CONVERTER_PATH");
                if (!string.IsNullOrEmpty(envPath)) return envPath;

                // 없으면 git 레포 안의 vd-warudo-converter 탐색
                // Application.dataPath = c:/vd/virtual-dresser-app/Dresser/Assets
                // 레포는 별도 위치이므로 상위 드라이브부터 검색
                var candidates = new[]
                {
                    // 배포된 build 경로 우선 (OneDrive 밖 → UMod 빌드 권한 문제 없음)
                    "c:/vd/build/vd-warudo-converter",
                    // git 레포가 c:/vd/ 아래 있을 경우
                    Path.GetFullPath(Path.Combine(Application.dataPath,
                        "..", "..", "virtual-dresser-unity", "vd-warudo-converter")),
                    // OneDrive 경로 (권한 문제 발생 가능 — 최후 fallback)
                    Path.Combine(System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.UserProfile),
                        "OneDrive", "업무용PC", "vibe coding",
                        "virtual Dresser-Unity", "virtual-dresser-unity",
                        "vd-warudo-converter"),
                };
                foreach (var c in candidates)
                    if (Directory.Exists(c)) return c;

                return candidates[0];
#else
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "vd-warudo-converter"));
#endif
            }
        }

        /// <summary>
        /// 헤들리스 Unity 2021.3으로 .warudo 파일 빌드.
        /// onComplete: (outputPath, errorMsg) — 성공 시 errorMsg=null
        /// </summary>
        public static void BuildWarudo(
            string inputPath,
            string outputPath,
            string avatarName,
            Action<string, string> onComplete)
        {
            var unityExe = FindUnity2021();
            if (string.IsNullOrEmpty(unityExe))
            {
                onComplete?.Invoke(null,
                    "Unity 2021.3.45f2 not found.\n" +
                    "Install Unity 2021.3.45f2 via Unity Hub.");
                return;
            }

            if (!Directory.Exists(ConverterProjectPath))
            {
                onComplete?.Invoke(null,
                    $"vd-warudo-converter not found:\n{ConverterProjectPath}");
                return;
            }

            var logPath = Path.Combine(Path.GetTempPath(), "vd-warudo-build.log");
            var args    = $"-batchmode -nographics " +
                          $"-projectPath \"{ConverterProjectPath}\" " +
                          $"-executeMethod WarudoConverter.WarudoBuildScript.Build " +
                          $"-inputPath \"{inputPath}\" " +
                          $"-outputPath \"{outputPath}\" " +
                          $"-avatarName \"{avatarName}\" " +
                          $"-logFile \"{logPath}\" " +
                          $"-quit";

            Debug.Log($"[WarudoBuilder] 헤들리스 실행: {unityExe}");
            Debug.Log($"[WarudoBuilder] 인수: {args}");

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = unityExe,
                    Arguments       = args,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                },
                EnableRaisingEvents = true,
            };

            proc.Exited += (_, __) =>
            {
                if (proc.ExitCode == 0 && File.Exists(outputPath))
                    onComplete?.Invoke(outputPath, null);
                else
                {
                    var log = File.Exists(logPath)
                        ? File.ReadAllText(logPath)[^Mathf.Min(500, File.ReadAllText(logPath).Length)..]
                        : "no log";
                    onComplete?.Invoke(null, $"Build failed (code {proc.ExitCode})\n{log}");
                }
            };

            proc.Start();
        }

        /// <summary>Unity Hub에서 2021.3.x 설치 경로 탐색</summary>
        public static string FindUnity2021()
        {
            var hubPath = @"C:\Program Files\Unity\Hub\Editor";
            if (!Directory.Exists(hubPath)) return null;

            foreach (var dir in Directory.GetDirectories(hubPath))
            {
                if (!Path.GetFileName(dir).StartsWith("2021.3")) continue;
                var exe = Path.Combine(dir, "Editor", "Unity.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        public static bool IsReady(out string reason)
        {
            if (string.IsNullOrEmpty(FindUnity2021()))
            {
                reason = "Unity 2021.3.45f2 not installed";
                return false;
            }
            if (!Directory.Exists(ConverterProjectPath))
            {
                reason = "vd-warudo-converter not found";
                return false;
            }
            reason = null;
            return true;
        }
    }
}
