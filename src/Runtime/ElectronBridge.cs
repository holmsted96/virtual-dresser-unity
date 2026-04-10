// ElectronBridge.cs
// Unity → Electron React UI WebSocket 브리지
// Electron의 ws://localhost:8765 에 연결해서 메시지를 주고받음
//
// 사용법:
//   ElectronBridge.Instance.SendAvatarLoaded("Shinano", meshEntries);
//   ElectronBridge.Instance.SendImportProgress(0.5f);
//   ElectronBridge.OnMessage += HandleMessage;  // Electron → Unity 수신

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    [Serializable]
    public class MeshEntryDto
    {
        public string name;
        public bool visible;
    }

    public class ElectronBridge : MonoBehaviour
    {
        // ─── Singleton ───
        public static ElectronBridge Instance { get; private set; }

        // ─── 이벤트: Electron → Unity ───
        // 메인 스레드에서 호출됨
        public static event Action<string, object> OnMessage;

        // ─── 설정 ───
        [SerializeField] private string url = "ws://127.0.0.1:8765";
        [SerializeField] private float reconnectInterval = 3f;

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private bool _running;
        private readonly Queue<string> _sendQueue = new Queue<string>();
        private readonly object _queueLock = new object();

        // ─── 연결 상태 (UI에서 읽기 전용) ───
        public bool IsConnected => _ws?.State == WebSocketState.Open;

        // ─────────────────────────────────────────

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            _running = true;
            _cts = new CancellationTokenSource();
            StartCoroutine(ConnectLoop());
            StartCoroutine(SendLoop());
        }

        void OnDestroy()
        {
            _running = false;
            _cts?.Cancel();
            _ws?.Abort();
        }

        // ─── 연결 루프 ───

        private IEnumerator ConnectLoop()
        {
            while (_running)
            {
                if (_ws == null || _ws.State == WebSocketState.Closed || _ws.State == WebSocketState.Aborted)
                {
                    yield return ConnectAsync();
                }
                yield return new WaitForSeconds(reconnectInterval);
            }
        }

        private IEnumerator ConnectAsync()
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();

            var connectTask = _ws.ConnectAsync(new Uri(url), _cts.Token);
            yield return new WaitUntil(() => connectTask.IsCompleted);

            if (_ws.State == WebSocketState.Open)
            {
                Debug.Log("[ElectronBridge] Connected to Electron ws://127.0.0.1:8765");
                // 연결 직후 상태 전송 (Electron이 나중에 켜진 경우 대비)
                StartCoroutine(ReceiveLoop());
            }
            else
            {
                Debug.Log("[ElectronBridge] Connection failed, will retry...");
            }
        }

        // ─── 수신 루프 ───

        private IEnumerator ReceiveLoop()
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();

            while (_running && _ws.State == WebSocketState.Open)
            {
                var segment = new ArraySegment<byte>(buffer);
                var receiveTask = _ws.ReceiveAsync(segment, _cts.Token);
                yield return new WaitUntil(() => receiveTask.IsCompleted);

                if (receiveTask.IsFaulted || receiveTask.IsCanceled) break;

                var result = receiveTask.Result;
                if (result.MessageType == WebSocketMessageType.Close) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var raw = sb.ToString();
                    sb.Clear();
                    DispatchMessage(raw);
                }
            }
        }

        private void DispatchMessage(string raw)
        {
            try
            {
                // 최소 파싱: type 필드만 추출 (Newtonsoft 없이)
                var typeVal = ExtractStringField(raw, "type");
                OnMessage?.Invoke(typeVal, raw);
                Debug.Log($"[ElectronBridge] → {typeVal}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ElectronBridge] message parse error: {e.Message}");
            }
        }

        // ─── 송신 루프 ───

        private IEnumerator SendLoop()
        {
            while (_running)
            {
                string msg = null;
                lock (_queueLock)
                {
                    if (_sendQueue.Count > 0) msg = _sendQueue.Dequeue();
                }

                if (msg != null && _ws?.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    var sendTask = _ws.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        _cts.Token
                    );
                    yield return new WaitUntil(() => sendTask.IsCompleted);
                }
                else
                {
                    yield return null;
                }
            }
        }

        // ─── 공개 API ───

        public void SendAvatarLoaded(string avatarName, IEnumerable<MeshEntryDto> meshes)
        {
            var meshJson = BuildMeshArrayJson(meshes);
            Enqueue($"{{\"type\":\"avatarLoaded\",\"name\":{Q(avatarName)},\"meshes\":{meshJson}}}");
        }

        public void SendClothingLoaded(string clothingName, IEnumerable<MeshEntryDto> meshes)
        {
            var meshJson = BuildMeshArrayJson(meshes);
            Enqueue($"{{\"type\":\"clothingLoaded\",\"name\":{Q(clothingName)},\"meshes\":{meshJson}}}");
        }

        public void SendImportProgress(float progress)
        {
            // 0.0 ~ 1.0
            var p = progress.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            Enqueue($"{{\"type\":\"importProgress\",\"progress\":{p}}}");
        }

        public void SendExportStatus(string status, string log = "")
        {
            // status: "building" | "done" | "error"
            Enqueue($"{{\"type\":\"exportStatus\",\"status\":{Q(status)},\"log\":{Q(log)}}}");
        }

        public void SendMeshVisibility(string meshName, bool visible)
        {
            var v = visible ? "true" : "false";
            Enqueue($"{{\"type\":\"meshVisibility\",\"name\":{Q(meshName)},\"visible\":{v}}}");
        }

        // ─── 내부 유틸 ───

        private void Enqueue(string json)
        {
            lock (_queueLock) { _sendQueue.Enqueue(json); }
        }

        private static string Q(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string BuildMeshArrayJson(IEnumerable<MeshEntryDto> meshes)
        {
            var parts = new List<string>();
            foreach (var m in meshes)
            {
                var v = m.visible ? "true" : "false";
                parts.Add($"{{\"name\":{Q(m.name)},\"visible\":{v}}}");
            }
            return "[" + string.Join(",", parts) + "]";
        }

        private static string ExtractStringField(string json, string field)
        {
            // 간단한 정규식 없는 파싱: "field":"value"
            var key = $"\"{field}\":\"";
            var idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return "";
            var start = idx + key.Length;
            var end = json.IndexOf('"', start);
            if (end < 0) return "";
            return json.Substring(start, end - start);
        }
    }
}
