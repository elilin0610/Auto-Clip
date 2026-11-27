using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ApexHighlighter.App;

// =====================================================================================
// DarkTitleBar.cs — 讓 Windows 的標題列跟 App 一樣是深色
//
// 標題列是 Windows 畫的，WPF 的 Background 管不到。用 DWM（桌面視窗管理員）的設定：
//   Windows 10 20H1+ / 11：DWMWA_USE_IMMERSIVE_DARK_MODE → 深色標題列
//   Windows 11：DWMWA_CAPTION_COLOR / TEXT_COLOR / BORDER_COLOR → 標題列直接用 App 的底色
// 舊版 Windows 不支援就維持原樣，不會出錯。
// =====================================================================================

/// <summary>Makes the Windows-drawn title bar dark and, on Windows 11, the same colour as the window background.</summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 before 20H1
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    /// <summary>Call from the window's constructor; applies as soon as the native window exists (before it is shown).</summary>
    public static void Attach(Window w)
    {
        w.SourceInitialized += (_, _) => Apply(w);
    }

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

            // Windows 11 only (ignored elsewhere): caption = window background, text = foreground, border = panel line.
            var bg = (w.Background as SolidColorBrush)?.Color ?? Color.FromRgb(0x16, 0x18, 0x1D);
            var fg = (w.Foreground as SolidColorBrush)?.Color ?? Color.FromRgb(0xE6, 0xE8, 0xEE);
            int caption = ColorRef(bg), text = ColorRef(fg), border = ColorRef(Color.FromRgb(0x2E, 0x32, 0x3B));
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // COLORREF is 0x00BBGGRR.
    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
