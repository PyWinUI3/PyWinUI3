using System.Runtime.InteropServices;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// IFileDialog 的 COM 定义。
///
/// WinRT 的 FileOpenPicker 不支持设置标题（SetTitle），而系统 IFileDialog 支持。
/// 这里直接用 COM 调系统对话框：能自定义标题、初始目录、按钮文字，
/// 外观与 WinRT picker 一致（本来就是同一个对话框）。
/// </summary>
internal static class NativeFileDialog
{
    private const int SIGDN_FILESYSPATH = unchecked((int)0x80058000);
    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_ALLOWMULTISELECT = 0x00000200;

    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid CLSID_FileSaveDialog = new("C0B4B2E6-3F02-4D65-9C80-FD295E0A41D0");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ComdlgFilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Spec;
    }

    [ComImport]
    [Guid("B4DB1657-70D7-485E-8E3E-6FCB5A5C1802")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IModalWindow
    {
        [PreserveSig]
        int Show(IntPtr hwnd);
    }

    [ComImport]
    [Guid("42F85169-0F66-467F-BF21-3269C7E1C419")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog : IModalWindow
    {
        void SetFileTypes(uint count, [In] ComdlgFilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IntPtr shellItem);
        void SetFolder(IntPtr shellItem);
        void GetFolder(out IntPtr shellItem);
        void GetCurrentSelection(out IntPtr shellItem);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IntPtr shellItem);
        void AddPlace(IntPtr shellItem, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        void GetResults(out IntPtr array);
        void GetSelectedFiles(out IntPtr array);
    }

    [ComImport]
    [Guid("84BCCB23-389F-4594-A167-B21DA307B2AD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog : IFileDialog
    {
        void SetSaveAsItem(IntPtr shellItem);
        void SetProperties(IntPtr store);
        void SetCollectedProperties(IntPtr list, int appendDefault);
        void GetProperties(out IntPtr store);
        void ApplyProperties(IntPtr item, IntPtr store, IntPtr hwnd, IntPtr sink);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindCtx, Guid bhid, Guid riid, out IntPtr result);
        void GetParent(out IntPtr item);
        void GetDisplayName(int sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IntPtr item, int hint, out int order);
    }

    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr bindCtx, Guid bhid, Guid riid, out IntPtr result);
        void GetPropertyStore(int flags, Guid riid, out IntPtr result);
        void GetPropertyDescriptionList(Guid key, Guid riid, out IntPtr result);
        void GetAttributes(uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IntPtr item);
        void EnumItems(out IntPtr enumerator);
    }

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindCtx,
        Guid riid,
        out IntPtr item);

    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>
    /// 用系统 IFileDialog 显示对话框，支持自定义标题。
    /// 任何一步失败都抛异常，由调用方决定要不要回退到 WinRT picker。
    /// </summary>
    public static FileDialogOutcome Show(
        IntPtr hwnd,
        string mode,
        string? title,
        string? initialDir,
        string? filter,
        string? defaultName)
    {
        var isSave = mode == "save";
        var clsid = isSave ? CLSID_FileSaveDialog : CLSID_FileOpenDialog;
        var comType = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        var instance = Activator.CreateInstance(comType)!;

        try
        {
            var dialog = (IFileDialog)instance;

            if (!string.IsNullOrWhiteSpace(title))
            {
                dialog.SetTitle(title!);
            }

            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
            {
                var hr = SHCreateItemFromParsingName(initialDir!, IntPtr.Zero, IID_IShellItem, out var folder);
                if (hr >= 0 && folder != IntPtr.Zero)
                {
                    try
                    {
                        dialog.SetFolder(folder);
                    }
                    finally
                    {
                        Marshal.Release(folder);
                    }
                }
            }

            // 文件类型过滤
            var filters = ParseFilter(filter);
            if (filters.Length > 0)
            {
                dialog.SetFileTypes((uint)filters.Length, filters);
            }

            uint options = FOS_FORCEFILESYSTEM;
            if (mode == "folder")
            {
                options |= FOS_PICKFOLDERS;
            }

            if (mode == "openMultiple")
            {
                options |= FOS_ALLOWMULTISELECT;
            }

            if (isSave)
            {
                options |= FOS_OVERWRITEPROMPT;
            }

            dialog.SetOptions(options);

            if (isSave && !string.IsNullOrWhiteSpace(defaultName))
            {
                dialog.SetFileName(defaultName!);
            }

            var showResult = dialog.Show(hwnd);
            // HRESULT_FROM_WIN32(ERROR_CANCELLED) = 0x800704C7
            if (showResult == unchecked((int)0x800704C7))
            {
                return FileDialogOutcome.Cancelled;
            }

            if (showResult < 0)
            {
                Marshal.ThrowExceptionForHR(showResult);
            }

            if (mode == "openMultiple")
            {
                var open = (IFileOpenDialog)instance;
                open.GetResults(out var arrayPtr);
                try
                {
                    var array = (IShellItemArray)Marshal.GetObjectForIUnknown(arrayPtr);
                    array.GetCount(out var count);
                    var paths = new List<string>();
                    for (uint i = 0; i < count; i++)
                    {
                        array.GetItemAt(i, out var itemPtr);
                        try
                        {
                            paths.Add(GetPath(itemPtr));
                        }
                        finally
                        {
                            Marshal.Release(itemPtr);
                        }
                    }

                    return paths.Count > 0
                        ? new FileDialogOutcome(true, paths)
                        : FileDialogOutcome.Cancelled;
                }
                finally
                {
                    Marshal.Release(arrayPtr);
                }
            }

            dialog.GetResult(out var resultPtr);
            try
            {
                return FileDialogOutcome.Selected(GetPath(resultPtr));
            }
            finally
            {
                Marshal.Release(resultPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    private static string GetPath(IntPtr shellItemPtr)
    {
        var item = (IShellItem)Marshal.GetObjectForIUnknown(shellItemPtr);
        item.GetDisplayName(SIGDN_FILESYSPATH, out var namePtr);
        try
        {
            return Marshal.PtrToStringUni(namePtr) ?? "";
        }
        finally
        {
            if (namePtr != IntPtr.Zero)
            {
                CoTaskMemFree(namePtr);
            }
        }
    }

    /// <summary>把 "图片|*.png;*.jpg" 转成 COMDLG_FILTERSPEC 数组。</summary>
    private static ComdlgFilterSpec[] ParseFilter(string? filter)
    {
        var result = new List<ComdlgFilterSpec>();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return Array.Empty<ComdlgFilterSpec>();
        }

        foreach (var segment in filter!.Split('|'))
        {
            var parts = segment.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var name = parts[0].Trim();
            var spec = string.Join(";", parts.Select(p =>
            {
                var token = p.Trim();
                return token.StartsWith('*') ? token : "*." + token.TrimStart('.');
            }));

            result.Add(new ComdlgFilterSpec { Name = name, Spec = spec });
        }

        return result.ToArray();
    }
}
