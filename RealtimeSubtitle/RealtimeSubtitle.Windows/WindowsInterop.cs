using System.Runtime.InteropServices;
using RealtimeSubtitle.Core.Diagnostics;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// Win32 helpers for the overlay window (plan F11): click-through, no-activate, tool-window,
/// always-on-top without alt-tab, square corners. Applied to a WinUI 3 AppWindow HWND.
/// </summary>
public static class WindowsInterop
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_LAYERED = 0x00080000;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const uint LWA_ALPHA = 0x2;
    private const uint LWA_COLORKEY = 0x1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtrW(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public static long GetExStyle(IntPtr hwnd) => GetWindowLongPtrW(hwnd, GWL_EXSTYLE);

    /// <summary>Makes the window click-through, focus-stealing-immune and hidden from the taskbar.</summary>
    public static void MakeOverlayStyle(IntPtr hwnd)
    {
        long style = GetExStyle(hwnd);
        long updated = style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, updated);
    }

    /// <summary>Whole-window alpha in [0..255] (255 = opaque), requires WS_EX_LAYERED.</summary>
    public static void SetAlpha(IntPtr hwnd, byte alpha)
    {
        long style = GetExStyle(hwnd) | WS_EX_LAYERED;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>
    /// Color-key transparency: pixels exactly equal to <paramref name="colorKeyBgr"/>
    /// (0x00BBGGRR) become fully transparent. Combined with a solid window background this
    /// gives a WinUI 3 window an effective see-through overlay (WinUI has no per-pixel alpha,
    /// see decisions F11/P4-1).
    /// </summary>
    public static void SetColorKey(IntPtr hwnd, uint colorKeyBgr = 0x000000, byte alpha = 255)
    {
        long style = GetExStyle(hwnd) | WS_EX_LAYERED;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style);
        SetLayeredWindowAttributes(hwnd, colorKeyBgr, alpha, LWA_COLORKEY | LWA_ALPHA);
    }

    public static void RemoveRoundedCorners(IntPtr hwnd)
    {
        int pref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>
    /// Windows 11 "transient window" backdrop (DWMSBT_TRANSIENTWINDOW): DWM paints no backdrop
    /// for the window at all, so areas whose XAML background is Transparent show the desktop /
    /// game behind the window. This is the WinUI 3-compatible replacement for LWA_COLORKEY —
    /// color-keying only applies to the GDI surface and cannot affect DirectComposition-rendered
    /// content, which is why the overlay previously stayed black (decisions P4-1: WinUI 3 has no
    /// per-pixel alpha; transient backdrop + transparent root is the supported overlay recipe).
    /// </summary>
    public static void SetTransientBackdrop(IntPtr hwnd)
    {
        int type = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
    }

    /// <summary>Diagnostics snapshot used by the Phase 4 acceptance probe.</summary>
    public static string DescribeStyle(IntPtr hwnd)
    {
        long style = GetExStyle(hwnd);
        var flags = new List<string>();
        if ((style & WS_EX_TRANSPARENT) != 0) flags.Add("TRANSPARENT");
        if ((style & WS_EX_NOACTIVATE) != 0) flags.Add("NOACTIVATE");
        if ((style & WS_EX_TOOLWINDOW) != 0) flags.Add("TOOLWINDOW");
        if ((style & WS_EX_LAYERED) != 0) flags.Add("LAYERED");
        return string.Join("|", flags);
    }

    /// <summary>Window rect as "left,top,right,bottom" (physical pixels), or "n/a".</summary>
    public static string DescribeRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return "n/a";
        return $"{r.Left},{r.Top},{r.Right},{r.Bottom}";
    }
}

/// <summary>Converts a WinUI 3 Window to its native HWND (WinRT.Interop).</summary>
public static class WindowHandle
{
    public static IntPtr Get(Microsoft.UI.Xaml.Window window) =>
        WinRT.Interop.WindowNative.GetWindowHandle(window);
}