using Microsoft.Win32;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 深浅色判断与配色。
///
/// 不能用 <c>Application.Current.RequestedTheme</c>：WinUI3 里它默认恒为 Light，
/// 除非应用显式设置过，完全不跟随系统设置——于是深色系统上窗口依然刷成白色。
/// 这里直接读注册表里「应用使用浅色主题」这一项，和系统设置面板保持一致。
/// </summary>
internal static class AppTheme
{
    private static readonly Lazy<bool> LazyDark = new(ReadSystemDark);

    public static bool IsDark => LazyDark.Value;

    /// <summary>标题栏背景：深色 #202020 / 浅色 #FFFFFF。</summary>
    public static Windows.UI.Color TitleBarBackground => IsDark
        ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
        : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);

    /// <summary>标题栏文字与图标：深色用浅字，浅色用深字。</summary>
    public static Windows.UI.Color TitleBarForeground => IsDark
        ? Windows.UI.Color.FromArgb(255, 0xE8, 0xE8, 0xE8)
        : Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);

    /// <summary>内容区底色。</summary>
    public static Windows.UI.Color ContentBackground => IsDark
        ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
        : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);

    /// <summary>外框：深色下 25% 灰几乎看不见，提到 75%。</summary>
    public static Windows.UI.Color FrameBorder => IsDark
        ? Windows.UI.Color.FromArgb(191, 0x80, 0x80, 0x80)
        : Windows.UI.Color.FromArgb(64, 0x80, 0x80, 0x80);

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // AppsUseLightTheme: 1 = 浅色, 0 = 深色
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
