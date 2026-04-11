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
        [SerializeField] private int marginTop = 0;     // 상단 여백 (px) — 필요시 조정
        [SerializeField] private int uiHeight  = 160;   // WebView 높이 (px)

        private WebViewObject _webView;
        private bool _ready;

        // ─── React 앱 URL ───
        // 빌드 시: StreamingAssets/ui/index.html (번들된 정적 파일)
        // 개발 시: http://127.0.0.1:7000 (Vite dev server)
        private string GetUiUrl()
        {
#if UNITY_EDITOR
            // 개발 중 Vite dev server 사용
            return "http://127.0.0.1:7000";
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
                    // 로드 완료 알림
                    SendToJs("{\"type\":\"unityReady\"}");
                },
                transparent: true,         // Unity 씬이 뒤에 비치게
                zoom: true,
                ua: null,
                radius: 0,
#if UNITY_EDITOR
                separated: false
#endif
            );

            // ── 창 크기에 맞게 WebView 배치 (상단 고정) ──
            yield return new WaitForEndOfFrame();
            UpdateLayout();

            var url = GetUiUrl();
            Debug.Log($"[WebViewBridge] loading: {url}");
            _webView.LoadURL(url);
        }

        private void UpdateLayout()
        {
            if (_webView == null) return;

            int screenW = Screen.width;
            int screenH = Screen.height;

            // 상단 전체 너비, 고정 높이
            _webView.SetMargins(
                left:   0,
                top:    marginTop,
                right:  0,
                bottom: screenH - marginTop - uiHeight
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

        public void SendImportProgress(float progress)
        {
            var p = progress.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            SendToJs($"{{\"type\":\"importProgress\",\"progress\":{p}}}");
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
