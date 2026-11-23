namespace PyWinUI.Host.Rendering;

/// <summary>
/// 窗口外观的几个尺寸常量。
///
/// 这几个数字在好几个地方都要用（渲染层画外壳、Win32 层裁边、拖动还原时
/// 对齐光标），各写各的必然会对不上——之前标题栏高度就写错过一次。
/// </summary>
internal static class WindowChrome
{
    /// <summary>标题栏高度（DIP）。</summary>
    public const int TitleBarHeight = 32;

    /// <summary>
    /// 窗口内容区四周留的透明边宽多少（DIP）。
    ///
    /// 这圈透明边是给**外框线**留的位置：外框线画在内容区的边界上，
    /// 而窗口最外 <see cref="OuterTrim"/> 像素会被
    /// <c>SetWindowRgn</c> 裁掉（用来遮 DWM 残留的半透明脏边）。
    ///
    /// 两者取同一个值，外框线就正好贴在**可见窗口的边缘**上——
    /// 留得比它大，线就会凹进窗口里，看上去像画错了地方。
    /// </summary>
    public const int TransparentMargin = OuterTrim;

    /// <summary>
    /// 窗口最外圈要裁掉的像素。
    ///
    /// 用来遮 DWM 合成缓存没及时更新留下的那条约 8px 半透明脏边。
    /// </summary>
    public const int OuterTrim = 10;
}
