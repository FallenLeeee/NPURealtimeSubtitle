using System.Windows;
using System.Windows.Threading;
using RealtimeSubtitle.Core.Configuration;
using RealtimeSubtitle.Core.Subtitle;

namespace RealtimeSubtitle.Overlay.Wpf;

/// <summary>
/// Hosts the WPF subtitle overlay on a dedicated STA thread inside the WinUI 3 process.
/// WPF's AllowsTransparency renders per-pixel alpha via the layered-window API
/// (UpdateLayeredWindow), which is the reliable see-through mechanism for overlays — the
/// WinUI 3 window itself cannot do per-pixel alpha (decisions P4-1), and GDI color-keying
/// does not affect its DirectComposition content (P5-3).
///
/// No <see cref="Application"/> is created: an AppDomain may hold only one System.Windows
/// Application, and the VS debugger's WPF tooling (WpfTap) can already own one — creating
/// another throws "不能在同一 AppDomain 中创建多个 System.Windows.Application 实例".
/// A bare <see cref="Dispatcher"/> message pump is all a standalone WPF window needs.
/// </summary>
public static class SubtitleOverlayHost
{
    private static readonly object Sync = new();
    private static Thread? _thread;
    private static SubtitleWindow? _window;
    private static SubtitleSnapshot _last;

    public static bool IsRunning
    {
        get { lock (Sync) return _window is not null; }
    }

    public static IntPtr Hwnd
    {
        get
        {
            SubtitleWindow? window;
            lock (Sync) { window = _window; }
            if (window is null) return IntPtr.Zero;
            // WindowInteropHelper.Handle must be touched on the WPF dispatcher thread.
            return window.Dispatcher.Invoke(() => window.GetHwnd());
        }
    }

    public static SubtitleSnapshot? CurrentSnapshot
    {
        get { lock (Sync) return _window is null ? null : _last; }
    }

    /// <summary>Starts the overlay thread and shows the window (idempotent).</summary>
    public static void Start(SubtitleConfig config)
    {
        lock (Sync)
        {
            if (_window is not null) return;

            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                _window = new SubtitleWindow(config);
                _window.Show();
                ready.Set();
                Dispatcher.Run(); // message pump; no Application instance needed
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Name = "OverlayWpf";
            _thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException("WPF overlay failed to start within 10s.");
            }
        }
    }

    /// <summary>Thread-safe: pushes a snapshot to the overlay's dispatcher.</summary>
    public static void PushSnapshot(SubtitleSnapshot snapshot)
    {
        SubtitleWindow? window;
        lock (Sync)
        {
            _last = snapshot;
            window = _window;
        }

        window?.Dispatcher.BeginInvoke(() => window.SetSnapshot(snapshot));
    }

    public static void Stop()
    {
        SubtitleWindow? window;
        lock (Sync)
        {
            window = _window;
            _window = null;
        }

        if (window is null) return;

        try
        {
            window.Dispatcher.Invoke(() =>
            {
                window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown(); // end Dispatcher.Run()
            });
        }
        catch
        {
            // overlay thread already gone; ignore
        }
    }
}
