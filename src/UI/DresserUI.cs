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

        // ─── 앱 상태 ───
        private ParseResult _avatarParse;
        private ParseResult _clothingParse;
        private ParseResult _hairParse;
        private readonly List<string> _sceneMaterials = new();
        private bool _highQualityMode = false;
        private AvatarConfig _currentAvatarConfig;

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

            var title = new Label("Warudo 익스포트 설정 필요");
            title.style.fontSize   = 18;
            title.style.color      = Color.white;
            title.style.marginBottom = 12;

            var desc = new Label(
                $"아바타를 Warudo로 익스포트하려면\n" +
                $"Unity {UnitySetupManager.UnityVersion}이 필요합니다.\n\n" +
                $"설치 버튼을 누르면 자동으로 설치됩니다.\n" +
                $"(약 3~5GB, 10~20분 소요)");
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
            installBtn.text = "Unity 자동 설치 시작";
            installBtn.style.width  = 220;
            installBtn.style.height = 40;
            installBtn.style.fontSize = 14;
            installBtn.style.backgroundColor = new Color(0.2f, 0.6f, 0.2f);
            installBtn.style.marginBottom = 8;

            var skipBtn = new Button();
            skipBtn.text = "나중에 (익스포트 기능 비활성)";
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
                installBtn.text = "설치 중...";
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
                                SetParseStatus("✅ Unity 설치 완료! 빌드/익스포트 기능을 사용할 수 있습니다.");
                            }
                            else
                            {
                                progressLabel.text = $"❌ {error}";
                                installBtn.SetEnabled(true);
                                installBtn.text = "다시 시도";
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
                title: "패키지 선택",
                filter: "Unity Package|*.unitypackage;*.zip|모든 파일|*.*"
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

            SetParseStatus($"파싱 중: {Path.GetFileName(filePath)}...");

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
            var displayName = result.DetectedName ?? Path.GetFileNameWithoutExtension(filePath);
            SetParseStatus($"감지: {result.DetectedType} ({result.Confidence:P0}) — {displayName}");

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
                // 메시 없이 머티리얼/텍스처만 기존 의상에 적용
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
            if (result.ExtractedFbxPaths.Count == 0) return;

            SetParseStatus($"FBX 변환 중: {displayName}...");
            try
            {
                // 기존 아바타 제거
                if (avatarRoot != null)
                    foreach (Transform child in avatarRoot) Destroy(child.gameObject);

                var go = await FbxConverter.LoadFbxAsync(result.ExtractedFbxPaths[0], displayName);
                if (go == null) { SetParseStatus("아바타 FBX 로드 실패"); return; }
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

                await MaterialManager.ApplyTexturesAsync(go, result);
                RegisterMeshGroup("avatar", displayName, go);
                FocusCameraOnAvatar(go);
                SetParseStatus($"아바타 로드 완료: {displayName}");
            }
            catch (Exception e)
            {
                SetParseStatus($"아바타 로드 실패: {e.Message}");
                Debug.LogError($"[DresserUI] 아바타 로드 실패: {e}");
            }
        }

        private async void LoadClothingFbx(ParseResult result, string displayName)
        {
            if (result.ExtractedFbxPaths.Count == 0) return;

            SetParseStatus($"의상 변환 중: {displayName}...");
            try
            {
                var go = await FbxConverter.LoadFbxAsync(result.ExtractedFbxPaths[0], displayName);
                if (go == null) { SetParseStatus("의상 FBX 로드 실패"); return; }
                go.transform.SetParent(avatarRoot, false);
                _clothingGo    = go;
                _clothingParse = result;

                if (avatarRoot != null)
                {
                    var stats = MeshCombiner.BindClothingToAvatar(avatarRoot, go, _currentAvatarConfig);
                    Debug.Log($"[DresserUI] 의상 바인딩: {stats}");

                    // 겹치는 별도 메시 자동 숨김 (Nail_foot_*, Toe_* 등)
                    var hidden = MeshCombiner.AutoHideOverlappingMeshes(avatarRoot, go);
                    if (hidden.Count > 0)
                        Debug.Log($"[DresserUI] 자동 숨김: {string.Join(", ", hidden)}");

                    // 단일 바디 메시 힌트
                    var hint = MeshCombiner.GetBodyMeshHint(avatarRoot, go);
                    if (hint != null)
                        SetParseStatus($"의상 로드 완료: {displayName}\n💡 {hint}");
                }

                await MaterialManager.ApplyTexturesAsync(go, result);
                RegisterMeshGroup("clothing", displayName, go);
                SetParseStatus($"의상 로드 완료: {displayName}");
            }
            catch (Exception e)
            {
                SetParseStatus($"의상 로드 실패: {e.Message}");
                Debug.LogError($"[DresserUI] 의상 로드 실패: {e}");
            }
        }

        private async void LoadHairFbx(ParseResult result, string displayName)
        {
            if (result.ExtractedFbxPaths.Count == 0) return;

            SetParseStatus($"헤어 변환 중: {displayName}...");
            try
            {
                var go = await FbxConverter.LoadFbxAsync(result.ExtractedFbxPaths[0], displayName);
                if (go == null) { SetParseStatus("헤어 FBX 로드 실패"); return; }
                go.transform.SetParent(avatarRoot, false);
                _hairGo    = go;
                _hairParse = result;

                if (avatarRoot != null)
                {
                    var stats = MeshCombiner.BindClothingToAvatar(avatarRoot, go, _currentAvatarConfig);
                    Debug.Log($"[DresserUI] 헤어 바인딩: {stats}");
                }

                await MaterialManager.ApplyTexturesAsync(go, result);
                SuggestHideAvatarHair();
                RegisterMeshGroup("hair", displayName, go);
                SetParseStatus($"헤어 로드 완료: {displayName}");
            }
            catch (Exception e)
            {
                SetParseStatus($"헤어 로드 실패: {e.Message}");
                Debug.LogError($"[DresserUI] 헤어 로드 실패: {e}");
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
                SetParseStatus("먼저 의상 메시를 임포트하세요");
                return;
            }

            SetParseStatus($"의상 머티리얼 적용 중: {displayName}...");
            try
            {
                await MaterialManager.ApplyTexturesAsync(_clothingGo, result);
                SetParseStatus($"의상 머티리얼 적용 완료: {displayName}");
            }
            catch (Exception e)
            {
                SetParseStatus($"머티리얼 적용 실패: {e.Message}");
                Debug.LogError($"[DresserUI] 머티리얼 적용 실패: {e}");
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
                var nameLabel = new Label(loaded ? group.DisplayName : "— 비어있음");
                nameLabel.style.flexGrow  = 1;
                nameLabel.style.fontSize  = 12;
                nameLabel.style.color     = loaded
                    ? new StyleColor(Color.white)
                    : new StyleColor(new Color(0.4f, 0.4f, 0.4f));

                row.Add(slotLabel);
                row.Add(nameLabel);

                // 로드된 경우 메시 수 배지
                if (loaded)
                {
                    var badge = new Label($"{group.Entries.Count}메시");
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
            var row = new VisualElement();
            row.AddToClassList("mesh-row");

            // 가시성 토글
            var toggle = new Toggle { value = entry.IsVisible };
            toggle.RegisterValueChangedCallback(e => SetMeshVisible(entry, e.newValue));

            // 메쉬 이름
            var label = new Label(entry.MeshName);
            label.AddToClassList("mesh-name");

            // 숨김 버튼 (토글과 동일하지만 명시적 버튼)
            var hideBtn = new Button(() => SetMeshVisible(entry, !entry.IsVisible))
            {
                text = entry.IsVisible ? "숨김" : "표시"
            };
            hideBtn.AddToClassList("mesh-hide-btn");

            // 삭제 버튼
            var deleteBtn = new Button(() => DeleteMesh(entry)) { text = "🗑" };
            deleteBtn.AddToClassList("mesh-delete-btn");

            row.Add(toggle);
            row.Add(label);
            row.Add(hideBtn);
            row.Add(deleteBtn);
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

            Debug.Log($"[DresserUI] 헤어 에셋 드롭 감지 → 아바타 헤어 {avatarHairMeshes.Count}개 자동 숨김");
            SetParseStatus($"아바타 헤어 {avatarHairMeshes.Count}개를 숨겼습니다. 필요하면 메쉬 탭에서 다시 켜세요.");
        }

        // ─── 빌드 / 익스포트 ───

        private void OnBuildButtonClicked()
        {
            if (_avatarGo == null)
            {
                SetParseStatus("먼저 아바타를 임포트하세요");
                return;
            }

            // ── 준비 상태 확인 ──
            if (!WarudoHeadlessBuilder.IsReady(out var reason))
            {
                SetParseStatus($"⚠️ {reason}\n설정 가이드를 확인하세요.");
                Debug.LogWarning($"[DresserUI] Warudo 빌드 불가: {reason}");
                return;
            }

            var avatarName = _slotNames.TryGetValue("avatar", out var n) ? n : "VDExport";

            // ── 출력 경로 선택 ──
#if UNITY_EDITOR
            var outputPath = UnityEditor.EditorUtility.SaveFilePanel(
                ".warudo 저장 위치",
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

            // ── 헤들리스 빌드 실행 ──
            // 첫 실행 시 Unity 스크립트 컴파일로 3~5분 소요됨
            bool isFirstRun = !Directory.Exists(
                Path.Combine(WarudoHeadlessBuilder.ConverterProjectPath, "Library"));
            var waitMsg = isFirstRun
                ? "⏳ .warudo 빌드 중...\n(첫 실행 시 Unity 컴파일로 3~5분 소요됩니다)"
                : "⏳ .warudo 빌드 중... (1~2분 소요)";
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
                            SetParseStatus($"✅ .warudo 생성 완료!\n저장 위치: {outputPath}");
                            Debug.Log($"[DresserUI] 빌드 완료: {result}");
                        }
                        else
                        {
                            SetParseStatus($"❌ 빌드 실패\n{error}");
                            Debug.LogError($"[DresserUI] 빌드 실패: {error}");
                        }
                    });
                });
        }

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
            _qualityToggleBtn.text = _highQualityMode ? "🔬 고품질 ON" : "⚡ 즉시 미리보기";

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
            var config = AvatarConfigLoader.Get(avatarId);
            if (config != null)
                Debug.Log($"  → {config.displayNameKo} 설정 로드됨");
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
            Debug.Log($"[DresserUI] GO 계층: Transform {allTransforms.Length}개, " +
                      $"Renderer {allRenderers.Length}개, SMR {allSMR.Length}개");
            Debug.Log($"[DresserUI] GO 위치: {go.transform.position}, " +
                      $"스케일: {go.transform.localScale}, 활성: {go.activeInHierarchy}");

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

        private static string LayerDisplayName(string layerKey) => layerKey switch
        {
            "avatar"   => "아바타",
            "clothing" => "의상",
            "hair"     => "헤어",
            "material" => "머티리얼",
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
        public static string ConverterProjectPath =>
#if UNITY_EDITOR
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "virtual-dresser-unity", "vd-warudo-converter"));
#else
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "vd-warudo-converter"));
#endif

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
                    "Unity 2021.3.45f2를 찾을 수 없습니다.\n" +
                    "Unity Hub에서 2021.3.45f2를 설치해주세요.");
                return;
            }

            if (!Directory.Exists(ConverterProjectPath))
            {
                onComplete?.Invoke(null,
                    $"vd-warudo-converter 프로젝트 없음:\n{ConverterProjectPath}");
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
                        : "로그 없음";
                    onComplete?.Invoke(null, $"빌드 실패 (코드 {proc.ExitCode})\n{log}");
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
                reason = "Unity 2021.3.45f2 미설치";
                return false;
            }
            if (!Directory.Exists(ConverterProjectPath))
            {
                reason = "vd-warudo-converter 프로젝트 없음";
                return false;
            }
            reason = null;
            return true;
        }
    }
}
