// SceneSetup.cs
// Unity 메뉴에서 씬을 자동으로 셋업하는 Editor 유틸리티
// 메뉴: VirtualDresser > Setup Scene

using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using VirtualDresser.UI;
using VirtualDresser.Runtime;

namespace VirtualDresser.Editor
{
    public static class SceneSetup
    {
        [MenuItem("VirtualDresser/Setup Scene")]
        public static void SetupScene()
        {
            // ── 1. AvatarRoot ──
            var avatarRoot = GameObject.Find("AvatarRoot");
            if (avatarRoot == null)
            {
                avatarRoot = new GameObject("AvatarRoot");
                avatarRoot.transform.position = Vector3.zero;
            }

            // ── 2. DresserManager ──
            var manager = GameObject.Find("DresserManager");
            if (manager == null)
                manager = new GameObject("DresserManager");

            // UIDocument
            var uiDoc = manager.GetComponent<UIDocument>();
            if (uiDoc == null)
                uiDoc = manager.AddComponent<UIDocument>();

            // dresser.uxml 자동 연결
            var uxmlAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/dresser.uxml");
            if (uxmlAsset != null)
                uiDoc.visualTreeAsset = uxmlAsset;
            else
                Debug.LogWarning("[SceneSetup] Assets/UI/dresser.uxml 을 찾을 수 없습니다. 수동 연결 필요.");

            // PanelSettings — 없으면 기본 생성
            if (uiDoc.panelSettings == null)
            {
                var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UI/DresserPanelSettings.asset");
                if (ps == null)
                {
                    ps = ScriptableObject.CreateInstance<PanelSettings>();
                    ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                    ps.referenceResolution = new Vector2Int(1920, 1080);
                    ps.clearDepthStencil = false;
                    ps.colorClearValue = new Color(0f, 0f, 0f, 0f); // 완전 투명
                    AssetDatabase.CreateAsset(ps, "Assets/UI/DresserPanelSettings.asset");
                    AssetDatabase.SaveAssets();
                }
                else
                {
                    // 기존 에셋도 투명으로 업데이트
                    ps.clearDepthStencil = false;
                    ps.colorClearValue = new Color(0f, 0f, 0f, 0f);
                    EditorUtility.SetDirty(ps);
                    AssetDatabase.SaveAssets();
                }
                uiDoc.panelSettings = ps;
            }

            // DresserUI
            var dresserUI = manager.GetComponent<DresserUI>();
            if (dresserUI == null)
                dresserUI = manager.AddComponent<DresserUI>();

            // SerializedObject로 private SerializeField 참조 연결
            var so = new SerializedObject(dresserUI);
            so.FindProperty("uiDocument").objectReferenceValue = uiDoc;
            so.FindProperty("avatarRoot").objectReferenceValue = avatarRoot.transform;
            so.ApplyModifiedProperties();

            // UnityMainThreadDispatcher
            if (manager.GetComponent<UnityMainThreadDispatcher>() == null)
                manager.AddComponent<UnityMainThreadDispatcher>();

            // ElectronBridge (Electron React UI 연동)
            if (manager.GetComponent<ElectronBridge>() == null)
                manager.AddComponent<ElectronBridge>();

            // WebViewBridge (gree/unity-webview React UI 연동)
            if (manager.GetComponent<WebViewBridge>() == null)
                manager.AddComponent<WebViewBridge>();

            // ── 3. Main Camera ──
            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }

            cam.transform.position = new Vector3(0f, 0.9f, 2.5f);
            cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.18f, 0.18f, 0.18f);

            if (cam.GetComponent<CameraController>() == null)
                cam.gameObject.AddComponent<CameraController>();

            // ── 4. Directional Light ──
            var light = GameObject.FindObjectOfType<Light>();
            if (light == null)
            {
                var lightGo = new GameObject("Directional Light");
                light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                light.intensity = 1.2f;
            }

            // ── 완료 ──
            EditorUtility.SetDirty(manager);
            Debug.Log("[SceneSetup] 씬 셋업 완료!\n" +
                      "- DresserManager (UIDocument + DresserUI + UnityMainThreadDispatcher + ElectronBridge)\n" +
                      "- AvatarRoot\n" +
                      "- Main Camera (CameraController)\n" +
                      "- Directional Light");

            Selection.activeGameObject = manager;
        }
    }
}
