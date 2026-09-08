using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RealtimeSubtitle.Core.Configuration;
using RealtimeSubtitle.Core.Subtitle;

namespace RealtimeSubtitle.Overlay.Wpf;

/// <summary>
/// The per-pixel transparent subtitle pill. Runs on a dedicated STA thread (see
/// <see cref="SubtitleOverlayHost"/>); per-pixel alpha via WPF AllowsTransparency
/// (UpdateLayeredWindow), click-through via WS_EX_TRANSPARENT|NOACTIVATE|TOOLWINDOW
/// (standard overlay recipe, see decisions P4-1/P5-3).
/// </summary>
public partial class SubtitleWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    private readonly SubtitleConfig _config;

    public SubtitleWindow(SubtitleConfig config)
    {
        _config = config;
        InitializeComponent();
        SetFontSize(config.FontSize);
    }

    public string SourceText => Line1.Text;
    public string TranslatedText => Line2.Text;

    public void SetFontSize(int size)
    {
        if (size <= 0) return;
        Line1.FontSize = size;
        Line2.FontSize = Math.Max(12, size * 5 / 8);
    }

    /// <summary>Applies a subtitle snapshot; hides empty lines so the pill hugs its content.</summary>
    public void SetSnapshot(SubtitleSnapshot snapshot)
    {
        Line1.Text = snapshot.SourceText;
        Line2.Text = snapshot.TranslatedText;
        Line1.Visibility = snapshot.SourceText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Line2.Visibility = snapshot.TranslatedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Root.Visibility = (snapshot.SourceText.Length + snapshot.TranslatedText.Length) > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public IntPtr GetHwnd() => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = GetHwnd();
        long style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        PositionBottomCenter();
    }

    private void PositionBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        double width = Math.Min(1200, area.Width - 40);
        double height = 170;
        double x = area.X + (area.Width - width) / 2;
        double y = area.Y + (area.Height * _config.Position.YRatio) - height;
        Width = width;
        Height = height;
        Left = x;
        Top = y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern long GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern long SetWindowLongPtrW(IntPtr hWnd, int nIndex, long dwNewLong);
}
