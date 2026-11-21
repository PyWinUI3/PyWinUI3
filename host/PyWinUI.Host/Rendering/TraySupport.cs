using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 系统托盘图标（Shell_NotifyIcon）。
///
/// 托盘回调是 Win32 消息，必须有一个 HWND 来接收。这里复用宿主主窗口的句柄
/// 挂一个 WndProc 子类（SetWindowSubclass），而不是另建隐藏窗口——
/// 少一个窗口，也避免额外的消息循环。
///
/// 一个应用可以有多个托盘图标（不同 id），每个回调都带自己的 id 回 Python。
/// </summary>
internal static class TraySupport
{
    private const uint WM_TRAYICON = 0x800;   // WM_APP + 0
    private const uint WM_LBUTTONUP = 0x202;
    private const uint WM_RBUTTONUP = 0x205;
    private const uint WM_MBUTTONUP = 0x207;
    private const uint WM_LBUTTONDBLCLK = 0x203;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("comctl32.dll", EntryPoint = "#410")]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc proc,
        UIntPtr id,
        UIntPtr data);

    [DllImport("comctl32.dll", EntryPoint = "#413")]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc proc,
        UIntPtr id);

    [DllImport("comctl32.dll", EntryPoint = "#412")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr data);

    private sealed class IconEntry
    {
        public required string Id { get; init; }
        public IntPtr IconHandle;
        public string Tip = "";
    }

    private static readonly Dictionary<string, IconEntry> Icons = new();
    private static readonly SubclassProc Subclass = OnSubclass;
    private static bool _hooked;
    private static IntPtr _hostHwnd;
    private static Action<string, string>? _emit;

    /// <summary>
    /// 初始化 WndProc 子类。必须传主窗口——托盘消息发到这个 HWND。
    /// </summary>
    public static void Initialize(Window window, Action<string, string> emit)
    {
        _emit = emit;
        _hostHwnd = WindowNative.GetWindowHandle(window);

        if (_hooked)
        {
            return;
        }

        SetWindowSubclass(_hostHwnd, Subclass, UIntPtr.Zero, UIntPtr.Zero);
        _hooked = true;
    }

    /// <summary>列出所有图标 id，供退出时统一清理。</summary>
    public static IReadOnlyList<string> AllIds() => Icons.Keys.ToList();

    public static void Create(string id, JsonObject? spec)
    {
        if (_hostHwnd == IntPtr.Zero)
        {
            HostLog.Write("[tray] 未初始化，忽略 create");
            return;
        }

        var iconPath = ValueParsers.AsString(spec?["icon"]);
        var tip = ValueParsers.AsString(spec?["tooltip"]) ?? "PyWinUI3";

        var iconHandle = LoadIconFromPath(iconPath);

        var entry = new IconEntry
        {
            Id = id,
            IconHandle = iconHandle,
            Tip = tip,
        };
        Icons[id] = entry;

        var data = BuildData(entry, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            HostLog.Write($"[tray] 创建图标失败: {id}");
            Icons.Remove(id);
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }
        }
    }

    public static void Update(string id, JsonObject? spec)
    {
        if (!Icons.TryGetValue(id, out var entry))
        {
            return;
        }

        if (ValueParsers.AsString(spec?["tooltip"]) is { } tip)
        {
            entry.Tip = tip;
        }

        if (ValueParsers.AsString(spec?["icon"]) is { } iconPath)
        {
            var newHandle = LoadIconFromPath(iconPath);
            if (entry.IconHandle != IntPtr.Zero)
            {
                DestroyIcon(entry.IconHandle);
            }

            entry.IconHandle = newHandle;
        }

        var data = BuildData(entry, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public static void Destroy(string id)
    {
        if (!Icons.Remove(id, out var entry))
        {
            return;
        }

        var data = BuildData(entry, 0);
        Shell_NotifyIcon(NIM_DELETE, ref data);

        if (entry.IconHandle != IntPtr.Zero)
        {
            DestroyIcon(entry.IconHandle);
        }
    }

    private static NotifyIconData BuildData(IconEntry entry, uint flags)
    {
        // uID 用 id 的哈希，保证同一窗口下多个图标不冲突
        var uid = (uint)entry.Id.GetHashCode();

        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hostHwnd,
            uID = uid,
            uFlags = flags,
            uCallbackMessage = WM_TRAYICON,
            hIcon = entry.IconHandle,
            szTip = entry.Tip,
        };
    }

    private static IntPtr LoadIconFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return IntPtr.Zero;
        }

        // LR_LOADFROMFILE = 0x10, LR_DEFAULTSIZE = 0x40
        return LoadImage(IntPtr.Zero, path!, 1, 16, 16, 0x10 | 0x40);
    }

    private static IntPtr OnSubclass(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr data)
    {
        if (msg == WM_TRAYICON)
        {
            var uid = (uint)lParam.ToInt64();
            var entry = Icons.Values.FirstOrDefault(e => (uint)e.Id.GetHashCode() == uid);
            if (entry is not null)
            {
                var mouseMsg = (uint)wParam.ToInt64();
                var what = mouseMsg switch
                {
                    WM_LBUTTONUP => "click",
                    WM_LBUTTONDBLCLK => "doubleClick",
                    WM_RBUTTONUP => "rightClick",
                    WM_MBUTTONUP => "middleClick",
                    _ => null,
                };

                if (what is not null)
                {
                    _emit?.Invoke(entry.Id, what);
                }
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
