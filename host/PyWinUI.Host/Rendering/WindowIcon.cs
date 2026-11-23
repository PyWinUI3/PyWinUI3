using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 从窗口的 Win32 属性里取**实际图标**，转成 WinUI 能用的 ImageSource。
///
/// 之前标题栏画的是硬编码的 Segoe 字形（\uE71D），跟窗口真实图标无关——
/// 用户设了 icon= 也只在系统任务栏/切换器里生效，标题栏上一个样。
///
/// 取值顺序：
///   1. WM_GETICON / ICON_SMALL — AppWindow.SetIcon 设进去的
///   2. WM_GETICON / ICON_BIG
///   3. GCLP_HICONSM / GCLP_HICON — 窗口类默认图标（exe 图标），
///      没设过 icon= 时走这条，这样标题栏至少有东西
///
/// 窗口上全拿不到时，回退到 imageres.dll 里的默认 exe 图标
///（%SystemRoot%\system32\imageres.dll，资源 ID 15 / 索引 -15）——
/// 这样标题栏永远有一个像样的图标，不会空着。
/// </summary>
internal static class WindowIcon
{
    private const uint WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL = 0;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string lpszFile, int nIconIndex,
        out IntPtr phIconLarge, out IntPtr phIconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, int istep, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // 32bpp 用不到颜色表，但结构上留 1 项，避免越界
        public uint bmiColors;
    }

    private static IntPtr GetIconHandle(IntPtr hwnd)
    {
        var icon = SendMessage(hwnd, WM_GETICON, ICON_SMALL, IntPtr.Zero);
        if (icon == IntPtr.Zero)
        {
            icon = SendMessage(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero);
        }

        if (icon == IntPtr.Zero)
        {
            icon = GetClassLongPtr(hwnd, GCLP_HICONSM);
        }

        if (icon == IntPtr.Zero)
        {
            icon = GetClassLongPtr(hwnd, GCLP_HICON);
        }

        return icon;
    }

    /// <summary>
    /// imageres.dll 里的默认 exe 图标。
    ///
    /// 这个 HICON 是 ExtractIconEx **新建**的，调用方用完要 DestroyIcon。
    /// </summary>
    private static IntPtr GetDefaultIconHandle()
    {
        try
        {
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var path = Path.Combine(systemDir, "imageres.dll");
            if (!File.Exists(path))
            {
                return IntPtr.Zero;
            }

            // 索引 -15 = 资源 ID 15，Windows 的默认可执行文件图标
            var count = ExtractIconEx(
                path, -15, out _, out var small, 1);

            return count > 0 ? small : IntPtr.Zero;
        }
        catch (Exception ex)
        {
            HostLog.Write($"[icon] 取 imageres.dll 默认图标失败: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>指定 HICON → BGRA 像素。</summary>
    private static bool TryGetPixels(IntPtr hwnd, IntPtr icon, int size, out byte[] pixels)
    {
        pixels = Array.Empty<byte>();

        if (icon == IntPtr.Zero)
        {
            return false;
        }

        var screenDc = GetDC(hwnd);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;

        try
        {
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size,   // 负数 = 自上而下的 DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };

            bitmap = CreateDIBSection(
                memDc, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);

            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return false;
            }

            oldBitmap = SelectObject(memDc, bitmap);

            // DI_IMAGE = 0x0002，不要 DI_MASK，否则透明处会变成黑白棋盘
            if (!DrawIconEx(memDc, 0, 0, icon, size, size, 0, IntPtr.Zero, 0x0002))
            {
                return false;
            }

            // DIB section 里就是自上而下的 32bpp BGRA，直接拷出来
            pixels = new byte[size * size * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return true;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memDc != IntPtr.Zero)
            {
                DeleteDC(memDc);
            }

            ReleaseDC(hwnd, screenDc);
        }
    }

    /// <summary>
    /// 从窗口属性取图标并转成 ImageSource。
    ///
    /// 取不到（窗口还没有图标、或者句柄失效）返回 null。
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(IntPtr hwnd, int size = 16)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        // 窗口上的图标归系统所有，不能 DestroyIcon；
        // 回退的那个是 ExtractIconEx 新建的，用完要销毁。
        var icon = GetIconHandle(hwnd);
        var ownsIcon = false;

        try
        {
            if (icon == IntPtr.Zero)
            {
                icon = GetDefaultIconHandle();
                ownsIcon = icon != IntPtr.Zero;
            }

            if (!TryGetPixels(hwnd, icon, size, out var pixels))
            {
                return null;
            }

            // BGRA -> PNG。走 WinRT 的 BitmapEncoder，项目里已经有
            // Windows.Storage.Streams / Graphics.Imaging 的引用。
            var height = size;
            var stride = size * 4;

            // BitmapEncoder 要的是自下而上的行序，翻转一下
            var flipped = new byte[pixels.Length];
            for (var y = 0; y < height; y++)
            {
                Array.Copy(
                    pixels, y * stride,
                    flipped, (height - 1 - y) * stride,
                    stride);
            }

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);

            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                (uint)size, (uint)height, 96, 96, flipped);

            await encoder.FlushAsync();
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            HostLog.Write($"[icon] 读取窗口图标失败: {ex.Message}");
            return null;
        }
        finally
        {
            if (ownsIcon && icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
            }
        }
    }
}
