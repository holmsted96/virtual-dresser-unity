// WebViewBridge.cs
// gree/unity-webview 기반 React UI 브리지
// WebView2(Windows) / WKWebView(Mac) 위에 React 앱을 표시하고
// JS ↔ C# 양방향 메시지를 처리한다.
//
// 메시지 프로토콜 (JSON):
//   JS → Unity : window.Unity.call(JSON.stringify({ type, ...payload }))
//   Unity → JS : webViewObject.EvaluateJS($"window.onUnityMessage({json})")

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public class WebViewBridge : MonoBehaviour
    {
        // ─── Singleton ───
        public static WebViewBridge Instance { get; private set; }

        // ─── 이벤트: JS → Unity ───
        public static event Action<string, string> OnMessage; // (type, rawJson)

        // ─── 설정 ───
        [SerializeField] private int marginTop  = 0;    // 상단 여백 (px)
        [SerializeField] private int panelWidth = 340;  // 오른쪽 패널 너비 — App.css --panel-w 와 일치

        private WebViewObject _webView;
        private bool _ready;
        private bool _suspended;

        // ─── React 앱 URL ───
        // 빌드 시: StreamingAssets/ui/index.html (번들된 정적 파일)
        // 개발 시: http://127.0.0.1:7000 (Vite dev server)
        private string GetUiUrl()
        {
#if UNITY_EDITOR
            // 개발 중 Vite dev server 사용
            return "http://localhost:7000";
#else
            var htmlPath = Path.Combine(Application.streamingAssetsPath, "ui", "index.html");
#if UNITY_STANDALONE_WIN
            return "file:///" + htmlPath.Replace('\\', '/');
#else
            return "file://" + htmlPath;
#endif
#endif
        }

        // ─────────────────────────────────────────

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            StartCoroutine(InitWebView());
        }

        void OnDestroy()
        {
            if (_webView != null) Destroy(_webView.gameObject);
        }

        // ─── 초기화 ───

        private IEnumerator InitWebView()
        {
            _webView = new GameObject("WebViewObject").AddComponent<WebViewObject>();

            _webView.Init(
                cb: OnJsMessage,           // JS → Unity
                err: (msg) => Debug.LogError($"[WebViewBridge] error: {msg}"),
                httpErr: (msg) => Debug.LogWarning($"[WebViewBridge] httpErr: {msg}"),
                started: (url) => Debug.Log($"[WebViewBridge] started: {url}"),
                hooked: null,
                ld: (url) => {
                    Debug.Log($"[WebViewBridge] loaded: {url}");
                    _ready = true;
                    // WebView 로드 완료 → 기존 UIDocument 숨김
                    HideUnityUI();
                    SendToJs("{\"type\":\"unityReady\"}");
                },
                transparent: true,         // Unity 씬이 뒤에 비치게
                zoom: true,
                ua: null,
                radius: 0,
#if UNITY_EDITOR
                separated: true   // Editor에서 별도 프로세스로 실행 → TriLib 충돌 방지
#endif
            );

            // ── 창 크기에 맞게 WebView 배치 (상단 고정) ──
            yield return new WaitForEndOfFrame();
            UpdateLayout();

            var url = GetUiUrl();
            Debug.Log($"[WebViewBridge] loading: {url}");
            _webView.LoadURL(url);
        }

        private void HideUnityUI()
        {
            // UIDocument (Unity UI Toolkit) 비활성화
            var uiDocs = FindObjectsOfType<UnityEngine.UIElements.UIDocument>();
            foreach (var doc in uiDocs)
            {
                doc.enabled = false;
                Debug.Log($"[WebViewBridge] UIDocument 비활성화: {doc.gameObject.name}");
            }
        }

        private void UpdateLayout()
        {
            if (_webView == null) return;

            int screenW = Screen.width;
            int screenH = Screen.height;

            // WebView를 오른쪽 패널 영역에만 배치
            // → 왼쪽 뷰포트를 WebView가 덮지 않으므로 투명도 문제 없음
            _webView.SetMargins(
                left:   screenW - panelWidth,
                top:    marginTop,
                right:  0,
                bottom: 0
            );
            _webView.SetVisibility(true);
        }

        // ─── JS → Unity 수신 ───

        private void OnJsMessage(string rawJson)
        {
            Debug.Log($"[WebViewBridge] JS→Unity: {rawJson}");
            var type = ExtractStringField(rawJson, "type");

            // WebViewBridge 자체 처리
            switch (type)
            {
                case "openFileDialog":
                    HandleFileDialogRequest();
                    return;
                case "openFolder":
                    var folderPath = ExtractStringField(rawJson, "path");
                    if (!string.IsNullOrEmpty(folderPath))
                        System.Diagnostics.Process.Start("explorer.exe", folderPath.Replace('/', '\\'));
                    return;
                case "quit":
                    Application.Quit();
                    return;
            }

            OnMessage?.Invoke(type, rawJson);
        }

        private void HandleFileDialogRequest()
        {
            var path = WindowsFilePicker.OpenFile(
                title: "Select Package",
                filter: "Unity Package|*.unitypackage;*.zip|All Files|*.*"
            );
            if (string.IsNullOrEmpty(path))
                SendToJs("{\"type\":\"fileDialogResult\",\"canceled\":true,\"filePaths\":[]}");
            else
            {
                var escaped = path.Replace("\\", "\\\\");
                SendToJs($"{{\"type\":\"fileDialogResult\",\"canceled\":false,\"filePaths\":[\"{escaped}\"]}}");
            }
        }

        // ─── Unity → JS 송신 ───

        public void SendToJs(string json)
        {
            if (_webView == null) return;
            // XSS 방지: JSON은 이미 안전하지만 백슬래시/따옴표 이스케이프
            var escaped = json.Replace("\\", "\\\\").Replace("'", "\\'");
            _webView.EvaluateJS($"if(window.onUnityMessage)window.onUnityMessage({json})");
        }

        // ─── 공개 API (DresserUI에서 호출) ───

        // 임포트 중 WebViewObject 완전 비활성화 (native Update 포함 차단)
        public void Suspend()
        {
            if (_suspended) return;
            _suspended = true;
            if (_webView != null)
            {
                _webView.SetVisibility(false);
                _webView.gameObject.SetActive(false);
                Debug.Log("[WebViewBridge] Suspended (import start)");
            }
        }

        public void Resume()
        {
            if (!_suspended) return;
            _suspended = false;
            if (_webView != null)
            {
                _webView.gameObject.SetActive(true);
                _webView.SetVisibility(true);
                Debug.Log("[WebViewBridge] Resumed (import done)");
            }
        }

        public void SendAvatarLoaded(string name, IEnumerable<MeshEntryDto> meshes)
        {
            var meshJson = BuildMeshArrayJson(meshes);
            SendToJs($"{{\"type\":\"avatarLoaded\",\"name\":{Q(name)},\"meshes\":{meshJson}}}");
        }

        public void SendClothingLoaded(string name, IEnumerable<MeshEntryDto> meshes)
        {
            var meshJson = BuildMeshArrayJson(meshes);
            SendToJs($"{{\"type\":\"clothingLoaded\",\"name\":{Q(name)},\"meshes\":{meshJson}}}");
        }

        public void SendImportProgress(float progress, string title = "", string step = "")
        {
            var p = progress.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            SendToJs($"{{\"type\":\"importProgress\",\"progress\":{p},\"title\":{Q(title)},\"step\":{Q(step)}}}");
        }

        public void SendExportStatus(string status, string log = "")
        {
            SendToJs($"{{\"type\":\"exportStatus\",\"status\":{Q(status)},\"log\":{Q(log)}}}");
        }

        public void SendMeshVisibility(string meshName, bool visible)
        {
            SendToJs($"{{\"type\":\"meshVisibility\",\"name\":{Q(meshName)},\"visible\":{(visible ? "true" : "false")}}}");
        }

        // ─── 유틸 ───

        private static string Q(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string BuildMeshArrayJson(IEnumerable<MeshEntryDto> meshes)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var m in meshes)
                parts.Add($"{{\"name\":{Q(m.name)},\"visible\":{(m.visible ? "true" : "false")}}}");
            return "[" + string.Join(",", parts) + "]";
        }

        private static string ExtractStringField(string json, string field)
        {
            var key = $"\"{field}\":\"";
            var idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return "";
            var start = idx + key.Length;
            var end = json.IndexOf('"', start);
            return end < 0 ? "" : json.Substring(start, end - start);
        }
    }
}
