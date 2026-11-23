using Windows.UI.Composition;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 只做一件事：把 Window 根 Visual 的默认底色换成**透明**。
///
/// WinUI 的 Window 根 Visual 默认带一层不透明的底色（浅色主题下是白色）。
/// 顶层窗口有 WinUIEx 的 TransparentTintBackdrop 盖住它，看不出来；
/// 但**嵌入子窗口**没有 backdrop，于是整块客户区糊着一个矩形白底——
/// 哪怕里面的内容已经是圆角裁剪过的，矩形底色仍在四周露出来。
///
/// 为什么继承 WinUIEx 的 CompositionBrushBackdrop，而不是自己写
/// SystemBackdrop 子类：
///
/// - SystemBackdrop.SystemBackdrop 的类型是
///   **Windows.UI.Composition.CompositionBrush**，画刷必须由系统合成器创建。
///   而系统合成器在 WinAppSDK 里没法自己建——它需要
///   Windows.System.DispatcherQueue，可
///   Windows.System.DispatcherQueueController.CreateOnCurrentThread
///   并不在 Windows.Win32 元数据里。
/// - 继承 WinUIEx 的基类正好绕开这个问题：**框架在调用 CreateBrush 时
///   会把系统合成器传进来**，我们拿它建个透明画刷就行。
///
/// 和 WinUIEx 那个 TransparentTintBackdrop 的区别：
/// 它额外做了 DWM 扩展帧、blur behind、背景擦除——那是**顶层窗口**才需要的。
/// 子窗口是 WS_CHILD，DWM 那套对它无效，而且它的背景擦除会把子窗口客户区
/// 涂成黑色，反而多出一块黑矩形。
/// </summary>
internal sealed class TransparentBackdrop : WinUIEx.CompositionBrushBackdrop
{
    protected override CompositionBrush CreateBrush(Compositor compositor)
    {
        // RGB 取白、alpha 取 0：alpha=0 才是「不画」。
        return compositor.CreateColorBrush(
            Windows.UI.Color.FromArgb(0, 255, 255, 255));
    }
}
