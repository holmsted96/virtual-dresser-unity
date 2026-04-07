
// WindowsFilePicker.cs
// 파일 열기 다이얼로그
//
// Editor:     UnityEditor.EditorUtility.OpenFilePanel 사용
// Standalone: Windows 네이티브 GetOpenFileName P/Invoke 사용
//
// ★ Editor에서 GetOpenFileName 직접 호출 시 Unity 메시지 루프 충돌로 Fatal Error 발생

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public static class WindowsFilePicker
    {
        /// <summary>
        /// 파일 열기 다이얼로그.
        /// filter 형식: "설명|*.ext;*.ext2|설명2|*.*"
        /// 선택한 경로 반환, 취소하면 null.
        /// </summary>
        public static string OpenFile(
            string title  = "파일 선택",
            string filter = "Unity Package|*.unitypackage;*.zip|모든 파일|*.*")
        {
#if UNITY_EDITOR
            // Editor: EditorUtility 사용 (P/Invoke는 Editor에서 Fatal Error 유발)
            return OpenFileEditor(title, filter);
#elif UNITY_STANDALONE_WIN
            // Standalone Build: Windows 네이티브 다이얼로그
            return OpenFileNative(title, filter);
#else
            Debug.LogWarning("[WindowsFilePicker] 지원하지 않는 플랫폼입니다.");
            return null;
#endif
        }

#if UNITY_EDITOR
        private static string OpenFileEditor(string title, string filter)
        {
            // "Unity Package|*.unitypackage;*.zip" → EditorUtility 확장자 형식 변환
            // EditorUtility.OpenFilePanel은 "ext1,ext2" 형식 사용
            var extensions = ExtractExtensions(filter);
            var path = UnityEditor.EditorUtility.OpenFilePanel(title, "", extensions);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        private static string ExtractExtensions(string filter)
        {
            // "Unity Package|*.unitypackage;*.zip|모든 파일|*.*"
            // → "unitypackage,zip"
            var exts = new System.Collections.Generic.List<string>();
            var parts = filter.Split('|');
            for (int i = 1; i < parts.Length; i += 2)
            {
                foreach (var p in parts[i].Split(';'))
                {
                    var ext = p.Trim().TrimStart('*', '.');
                    if (ext != "*" && !string.IsNullOrEmpty(ext))
                        exts.Add(ext);
                }
            }
            return string.Join(",", exts);
        }
#endif

#if UNITY_STANDALONE_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class OpenFileName
        {
            public int    structSize     = Marshal.SizeOf(typeof(OpenFileName));
            public IntPtr dlgOwner      = IntPtr.Zero;
            public IntPtr instance      = IntPtr.Zero;
            public IntPtr filter        = IntPtr.Zero;
            public IntPtr customFilter  = IntPtr.Zero;
            public int    maxCustFilter = 0;
            public int    filterIndex   = 1;
            public string file          = new string('\0', 4096);
            public int    maxFile       = 4096;
            public string fileTitle     = new string('\0', 256);
            public int    maxFileTitle  = 256;
            public string initialDir    = null;
            public string title         = null;
            public int    flags         = 0x00080000   // OFN_EXPLORER
                                        | 0x00001000   // OFN_FILEMUSTEXIST
                                        | 0x00000800   // OFN_PATHMUSTEXIST
                                        | 0x00000008;  // OFN_NOCHANGEDIR
            public short  fileOffset    = 0;
            public short  fileExtension = 0;
            public string defExt        = null;
            public IntPtr custData      = IntPtr.Zero;
            public IntPtr hook          = IntPtr.Zero;
            public string templateName  = null;
            public IntPtr reservedPtr   = IntPtr.Zero;
            public int    reservedInt   = 0;
            public int    flagsEx       = 0;
        }

        [DllImport("Comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        private static string OpenFileNative(string title, string filter)
        {
            var filterStr = filter.Replace("|", "\0") + "\0\0";
            var filterPtr = Marshal.StringToHGlobalAuto(filterStr);
            var savedDir  = Directory.GetCurrentDirectory();
            try
            {
                var ofn = new OpenFileName { title = title, filter = filterPtr };
                if (GetOpenFileName(ofn))
                    return ofn.file.TrimEnd('\0');
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(filterPtr);
                try { Directory.SetCurrentDirectory(savedDir); } catch { }
            }
        }
#endif
    }
}
