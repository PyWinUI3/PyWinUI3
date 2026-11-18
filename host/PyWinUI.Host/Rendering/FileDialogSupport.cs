using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PyWinUI.Host.Rendering;

/// <summary>文件对话框的结果：ok=false 表示用户取消。</summary>
internal sealed record FileDialogOutcome(bool Ok, List<string> Paths);

/// <summary>列表里的一项，Tag 上挂着真实路径。</summary>
internal sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
}

/// <summary>
/// 自绘文件对话框：不用 WinUI 原生 FilePicker / FolderPicker，
/// 用 ListView + TextBox + Button 组装一个可浏览目录的模态弹窗。
///
/// 四种模式：
/// - open        选单个已存在文件
/// - openMultiple 多选已存在文件
/// - save        浏览目录 + 输入文件名（文件可以不存在）
/// - folder      选一个目录
///
/// 之所以自绘而不用原生 Picker：原生 Picker 是系统级窗口，会脱离主窗口、
/// 在自动化/多屏/无头会话里行为不一致，且无法用协议统一驱动。
/// </summary>
internal static class FileDialogSupport
{
    private static readonly SolidColorBrush FolderBrush =
        new(Microsoft.UI.Colors.Goldenrod);
    private static readonly SolidColorBrush FileBrush =
        new(Microsoft.UI.Colors.DodgerBlue);

    /// <summary>在 UI 线程调用；弹窗并等待用户操作完成。</summary>
    public static async Task<FileDialogOutcome> ShowAsync(
        XamlRoot xamlRoot,
        string mode,
        string title,
        string? initialDir,
        string? filter,
        string? defaultName)
    {
        var saveMode = mode == "save";
        var folderMode = mode == "folder";
        var multiSelect = mode == "openMultiple";
        var patterns = ParsePatterns(filter);

        var current = NormalizeDirectory(initialDir);
        var nameBox = new TextBox
        {
            Text = defaultName ?? "",
            PlaceholderText = folderMode ? "文件夹名（可选）" : "文件名",
            Margin = new Thickness(0, 4, 0, 0),
        };
        var pathBox = new TextBox { Text = current, Margin = new Thickness(0, 0, 8, 0) };
        var upButton = new Button { Content = "\uE197 上级" }; // 向上箭头 Segoe 字形
        var upRow = new StackPanel { Orientation = Orientation.Horizontal };
        upRow.Children.Add(pathBox);
        upRow.Children.Add(upButton);

        var list = new ListView
        {
            Height = 320,
            Margin = new Thickness(0, 8, 0, 0),
            SelectionMode = multiSelect
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Single,
        };

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(upRow);
        root.Children.Add(list);
        if (saveMode || folderMode)
        {
            root.Children.Add(nameBox);
        }
        else
        {
            // open 模式也放一个只读提示，说明可多选/过滤规则
            var hint = new TextBlock
            {
                Text = multiSelect ? "按住 Ctrl 可多选" : "双击文件夹进入，选中文件后打开",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
            };
            root.Children.Add(hint);
        }

        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(mode) : title,
            Content = root,
            PrimaryButtonText = saveMode ? "保存" : folderMode ? "选择文件夹" : "打开",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        void Refresh(string dir)
        {
            current = dir;
            pathBox.Text = dir;
            list.Items.Clear();

            if (!Directory.Exists(dir))
            {
                return;
            }

            // 目录在前、文件在后；folder 模式只列目录
            foreach (var path in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                list.Items.Add(MakeItem(Path.GetFileName(path), path, isDirectory: true));
            }

            if (!folderMode)
            {
                foreach (var path in Directory.EnumerateFiles(dir)
                             .Where(p => Matches(p, patterns))
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    list.Items.Add(MakeItem(Path.GetFileName(path), path, isDirectory: false));
                }
            }
        }

        upButton.Click += (_, _) =>
        {
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is not null)
            {
                Refresh(parent);
            }
        };

        pathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && Directory.Exists(pathBox.Text.Trim()))
            {
                Refresh(pathBox.Text.Trim());
            }
        };

        // 双击目录进入。双击文件只保持选中，不直接关窗——
        // ContentDialog.Hide() 返回的是 None，会被当成"取消"，
        // 确认必须走 PrimaryButton，结果语义才一致。
        list.DoubleTapped += (_, e) =>
        {
            if (list.SelectedItem is not ListViewItem item
                || item.Tag is not FileEntry { IsDirectory: true } entry)
            {
                return;
            }

            Refresh(entry.FullPath);
        };

        // save 模式：单击文件把名字填进输入框
        if (saveMode)
        {
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is ListViewItem item
                    && item.Tag is FileEntry { IsDirectory: false } entry)
                {
                    nameBox.Text = entry.Name;
                }
            };
        }

        Refresh(current);
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return new FileDialogOutcome(false, new List<string>());
        }

        var selected = list.SelectedItems
            .OfType<ListViewItem>()
            .Select(i => i.Tag as FileEntry)
            .Where(e => e is not null)
            .Cast<FileEntry>()
            .ToList();

        if (folderMode)
        {
            // 选中的目录，或双击进入后没选任何项时用当前目录
            var folder = selected.FirstOrDefault(e => e.IsDirectory)?.FullPath ?? current;
            return new FileDialogOutcome(true, new List<string> { folder });
        }

        if (saveMode)
        {
            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                return new FileDialogOutcome(false, new List<string>());
            }

            var full = Path.IsPathRooted(name) ? name : Path.Combine(current, name);
            return new FileDialogOutcome(true, new List<string> { full });
        }

        var files = selected
            .Where(e => !e.IsDirectory)
            .Select(e => e.FullPath)
            .ToList();
        return new FileDialogOutcome(true, files);
    }

    private static ListViewItem MakeItem(string name, string fullPath, bool isDirectory)
    {
        var glyph = isDirectory ? "\uE8B7" : "\uE8A5"; // 文件夹 / 文档 Segoe 字形
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon
        {
            Glyph = glyph,
            Foreground = isDirectory ? FolderBrush : FileBrush,
        });
        panel.Children.Add(new TextBlock { Text = name });
        return new ListViewItem { Content = panel, Tag = new FileEntry
        {
            Name = name,
            FullPath = fullPath,
            IsDirectory = isDirectory,
        } };
    }

    private static string NormalizeDirectory(string? dir)
    {
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            return Path.GetFullPath(dir);
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string DefaultTitle(string mode) => mode switch
    {
        "save" => "保存文件",
        "folder" => "选择文件夹",
        "openMultiple" => "选择多个文件",
        _ => "打开文件",
    };

    /// <summary>
    /// 解析扩展名过滤串。接受 "*.txt;*.py" 或 "*.txt,*.py" 或 "图片|*.png;*.jpg"；
    /// 带标签的写法只取 '|' 后面的 pattern 部分。空串表示不过滤。
    /// </summary>
    private static List<string> ParsePatterns(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return new List<string>();
        }

        var text = filter.Contains('|') ? filter[(filter.LastIndexOf('|') + 1)..] : filter;
        return text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    private static bool Matches(string path, List<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return patterns.Any(p =>
            p == "*" || name.EndsWith(p.TrimStart('*'), StringComparison.OrdinalIgnoreCase));
    }
}
