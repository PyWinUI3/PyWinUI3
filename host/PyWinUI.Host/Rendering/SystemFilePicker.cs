using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PyWinUI.Host.Rendering;

/// <summary>文件选择结果：Ok 为 false 表示用户取消。</summary>
internal sealed record FileDialogOutcome(bool Ok, List<string> Paths)
{
    public static FileDialogOutcome Cancelled => new(false, new List<string>());

    public static FileDialogOutcome Selected(params string[] paths) =>
        new(true, new List<string>(paths));
}

/// <summary>
/// 系统自带的文件选择器（FileOpenPicker / FileSavePicker / FolderPicker）。
///
/// 只做「把协议字段翻译成 picker 参数」这一件事：系统 picker 不支持自定义
/// 标题和初始目录，所以 title / initialDir 会被忽略——这是用回系统对话框的
/// 代价，换来的是一致的外观、最近位置记忆和完整的键盘/无障碍支持。
/// </summary>
internal static class SystemFilePicker
{
    public static async Task<FileDialogOutcome> ShowAsync(
        Window window,
        string mode,
        string? title,
        string? initialDir,
        string? filter,
        string? defaultName)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // 优先用系统 IFileDialog COM 接口：支持自定义标题、初始目录、按钮文字。
        // 失败（COM 不可用、沙箱限制等）时回退到 WinRT picker，保证功能不丢。
        try
        {
            // IFileDialog.Show 本身就阻塞到用户关闭，且在 UI 线程（STA）调用
            // 才符合 COM 单元要求——放到 Task.Run 的 MTA 线程会单元不匹配。
            return NativeFileDialog.Show(
                hwnd, mode, title, initialDir, filter, defaultName);
        }
        catch (Exception ex)
        {
            HostLog.Write($"[fileDialog] 系统 IFileDialog 不可用，回退 WinRT picker: {ex.Message}");
        }

        var extensions = ParseExtensions(filter);

        switch (mode)
        {
            case "save":
                return await SaveAsync(hwnd, extensions, defaultName);

            case "folder":
                return await FolderAsync(hwnd);

            case "openMultiple":
                return await OpenMultipleAsync(hwnd, extensions);

            default:
                return await OpenAsync(hwnd, extensions);
        }
    }

    private static async Task<FileDialogOutcome> OpenAsync(IntPtr hwnd, List<string> extensions)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        // FileTypeFilter 不能为空，否则 picker 直接抛异常
        foreach (var ext in extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        if (picker.FileTypeFilter.Count == 0)
        {
            picker.FileTypeFilter.Add("*");
        }

        var file = await picker.PickSingleFileAsync();
        return file is null
            ? FileDialogOutcome.Cancelled
            : FileDialogOutcome.Selected(file.Path);
    }

    private static async Task<FileDialogOutcome> OpenMultipleAsync(IntPtr hwnd, List<string> extensions)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        foreach (var ext in extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        if (picker.FileTypeFilter.Count == 0)
        {
            picker.FileTypeFilter.Add("*");
        }

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
        {
            return FileDialogOutcome.Cancelled;
        }

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            paths.Add(file.Path);
        }

        return new FileDialogOutcome(true, paths);
    }

    private static async Task<FileDialogOutcome> SaveAsync(
        IntPtr hwnd,
        List<string> extensions,
        string? defaultName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        // FileTypeChoices 也必须非空；没有扩展名时用 .txt 兜底，
        // 否则 PickSaveFileAsync 会抛 "File type choices must not be empty"。
        var choices = extensions.Count > 0 ? extensions : new List<string> { ".txt" };
        picker.FileTypeChoices.Add("文件", choices);

        var name = string.IsNullOrWhiteSpace(defaultName) ? "未命名" : defaultName!.Trim();
        picker.SuggestedFileName = name;

        var file = await picker.PickSaveFileAsync();
        return file is null
            ? FileDialogOutcome.Cancelled
            : FileDialogOutcome.Selected(file.Path);
    }

    private static async Task<FileDialogOutcome> FolderAsync(IntPtr hwnd)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        return folder is null
            ? FileDialogOutcome.Cancelled
            : FileDialogOutcome.Selected(folder.Path);
    }

    /// <summary>
    /// 把协议里的 filter 翻译成 picker 的扩展名列表。
    ///
    /// 协议支持两种写法：<c>"*.txt;*.py"</c> 和带标签的 <c>"图片|*.png;*.jpg"</c>。
    /// 只保留带点的规范形式（<c>.txt</c>），因为 picker 只认这个；
    /// <c>*</c> / <c>*.*</c> 表示不限类型，返回空列表由调用方补 <c>"*"</c>。
    /// </summary>
    private static List<string> ParseExtensions(string? filter)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return result;
        }

        foreach (var raw in filter.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();

            // 带标签的写法："图片|*.png" —— 取竖线后半截
            var bar = token.LastIndexOf('|');
            if (bar >= 0)
            {
                token = token[(bar + 1)..].Trim();
            }

            if (token.Length == 0)
            {
                continue;
            }

            // 不限类型：交给调用方补 "*"
            if (token is "*" or "*.*")
            {
                return new List<string>();
            }

            var ext = token.StartsWith("*.") ? token[1..] : token;
            if (!ext.StartsWith('.'))
            {
                ext = "." + ext;
            }

            if (ext.Length > 1 && !result.Contains(ext))
            {
                result.Add(ext);
            }
        }

        return result;
    }
}
