using System.Runtime.InteropServices;
using RealtimeSubtitle.Core.Audio;
using RealtimeSubtitle.Core.Diagnostics;

namespace RealtimeSubtitle.Windows;

/// <summary>
/// 使用原始 COM 互操作实现的 WASAPI 回环捕获器（无 NAudio / 无 WinRT AudioGraph，
/// 它们不暴露回环）。捕获当前默认渲染（eConsole）端点的混合格式，
/// 并在专用线程上将原始交错浮点数追加到环形缓冲区。
///
/// 流程（根据 v3.0 计划，F5/F6）：
///   CoCreateInstance(MMDeviceEnumerator) → GetDefaultAudioEndpoint(eRender, eConsole)
///     → Activate(IAudioClient) → GetMixFormat → Initialize(SHARED, LOOPBACK|EVENTCALLBACK)
///     → GetService(IAudioCaptureClient) → SetEventHandle → Start → 事件驱动的捕获循环。
/// </summary>
public sealed class WasapiLoopbackCapturer : IAudioCapturer
{
    // --- Core Audio GUIDs / CLSIDs ---
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    // Constants from audioclient.h / mmdeviceapi.h
    private const int EDataFlowRender = 0;
    private const int ERoleConsole = 0;
    private const int CLSCTX_ALL = 23;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    private readonly LogSink _log;
    private readonly RingBuffer<float> _buffer = new(192_000); // ~2 s @ 48 kHz stereo

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private AudioFormat _format;
    private bool _formatValid;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private AutoResetEvent? _dataEvent;
    private AutoResetEvent? _stopEvent;

    public WasapiLoopbackCapturer(LogSink? log = null)
    {
        _log = log ?? LogSink.Default;
    }

    public AudioFormat Format => _formatValid
        ? _format
        : throw new InvalidOperationException("Capture format is not available before Start().");

    public RingBuffer<float> Buffer => _buffer;

    public event Action<Exception>? Error;

    public void Start(CancellationToken token)
    {
        if (_thread is { IsAlive: true })
        {
            throw new InvalidOperationException("Capture is already running.");
        }

        _buffer.Clear();
        OpenAudioEndpoint();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _stopEvent = new AutoResetEvent(false);
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WasapiLoopbackCapture" };
        _thread.Start();
        _log.Info("Loopback capture started: {0} Hz, {1} ch, {2}-bit (tag {3})",
            _format.SampleRate, _format.Channels, _format.BitsPerSample, _format.FormatTag);
    }

    public void Stop()
    {
        if (_thread is null) return;

        _log.Debug("Stopping loopback capture...");
        try { _cts?.Cancel(); } catch { /* already cancelled */ }
        _stopEvent?.Set();
        _thread.Join(1500);
        _thread = null;

        try { _client?.Stop(); } catch { /* ignore */ }

        ReleaseEndpoint();
        _cts?.Dispose();
        _cts = null;
        _stopEvent?.Dispose();
        _stopEvent = null;
        _dataEvent?.Dispose();
        _dataEvent = null;
        _log.Info("Loopback capture stopped.");
    }

    public void Dispose()
    {
        Stop();
        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    // ------------------------------------------------------------------ setup

    private void OpenAudioEndpoint()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator)!)!;

            ThrowForHr(_enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out IMMDevice device));
            _device = device;

            Guid clientIid = IidIAudioClient;
            ThrowForHr(_device.Activate(ref clientIid, CLSCTX_ALL, IntPtr.Zero, out IntPtr clientPtr));
            _client = (IAudioClient)Marshal.GetObjectForIUnknown(clientPtr);
            Marshal.Release(clientPtr);

            ThrowForHr(_client.GetMixFormat(out IntPtr mixFormatPtr));
            _format = ParseFormat(mixFormatPtr);
            _formatValid = true;

            ThrowForHr(_client.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                0, 0, mixFormatPtr, IntPtr.Zero));
            Marshal.FreeCoTaskMem(mixFormatPtr);

            _dataEvent = new AutoResetEvent(false);
            ThrowForHr(_client.SetEventHandle(_dataEvent.SafeWaitHandle.DangerousGetHandle()));

            Guid captureIid = IidIAudioCaptureClient;
            ThrowForHr(_client.GetService(ref captureIid, out IntPtr capturePtr));
            _capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
            Marshal.Release(capturePtr);

            ThrowForHr(_client.Start());
        }
        catch
        {
            ReleaseEndpoint();
            throw;
        }
    }

    private void ReleaseEndpoint()
    {
        foreach (object? com in new object?[] { _capture, _client, _device })
        {
            try { if (com is not null) Marshal.ReleaseComObject(com); } catch { /* already released */ }
        }

        _capture = null;
        _client = null;
        _device = null;
        _formatValid = false;
    }

    // ------------------------------------------------------------------ capture loop

    private void CaptureLoop()
    {
        if (_cts is null || _dataEvent is null || _stopEvent is null) return;

        var token = _cts.Token;
        WaitHandle[] wait = { _stopEvent, _dataEvent };
        float[] chunk = new float[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int signalled = WaitHandle.WaitAny(wait, 200);
                if (signalled == 0) break;                        // stop requested
                if (signalled == WaitHandle.WaitTimeout) continue; // periodic token check

                DrainPackets(ref chunk);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Capture loop crashed: {0}", ex);
            Error?.Invoke(ex);
        }
    }

    private void DrainPackets(ref float[] chunk)
    {
        while (true)
        {
            ThrowForHr(_capture!.GetNextPacketSize(out uint frames));
            if (frames == 0) break;

            ThrowForHr(_capture.GetBuffer(
                out IntPtr data, out frames, out uint flags, out _, out _));

            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero && frames > 0)
            {
                int count = (int)frames * _format.Channels;
                if (count > chunk.Length) chunk = new float[count];

                if (_format.IsFloat32)
                {
                    Marshal.Copy(data, chunk, 0, count);
                    _buffer.TryWrite(chunk.AsSpan(0, count));
                }
                else if (_format.IsPcm16)
                {
                    short[] pcm = new short[count]; // Phase 5: pool these
                    Marshal.Copy(data, pcm, 0, count);
                    for (int i = 0; i < count; i++) chunk[i] = pcm[i] / 32768f;
                    _buffer.TryWrite(chunk.AsSpan(0, count));
                }
                else
                {
                    _log.Error("Unsupported mix format: tag={0}, bits={1}", _format.FormatTag, _format.BitsPerSample);
                }
            }

            ThrowForHr(_capture.ReleaseBuffer(frames));
        }
    }

    private void ThrowForHr(int hr)
    {
        if (hr < 0)
        {
            if (hr == AUDCLNT_E_DEVICE_INVALIDATED)
            {
                _log.Warn("Audio device invalidated (lost).");
                Error?.Invoke(new AudioDeviceLostException());
            }

            throw Marshal.GetExceptionForHR(hr) ?? new COMException($"WASAPI failed with HRESULT 0x{hr:X8}", hr);
        }
    }

    // ------------------------------------------------------------------ format parsing

    private static AudioFormat ParseFormat(IntPtr mixFormatPtr)
    {
        var ex = Marshal.PtrToStructure<WaveFormatEx>(mixFormatPtr);

        if (ex.FormatTag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE
        {
            var subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(mixFormatPtr, 24));
            if (subFormat == KsDataFormatSubtypeIeeeFloat)
            {
                return new AudioFormat((int)ex.SampleRate, ex.Channels, 32, 3);
            }

            if (subFormat == KsDataFormatSubtypePcm)
            {
                return new AudioFormat((int)ex.SampleRate, ex.Channels, ex.BitsPerSample, 1);
            }

            throw new NotSupportedException($"Unsupported WAVE_FORMAT_EXTENSIBLE sub-format {subFormat}.");
        }

        return new AudioFormat((int)ex.SampleRate, ex.Channels, ex.BitsPerSample, ex.FormatTag);
    }

    // ------------------------------------------------------------------ COM definitions

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr interfacePtr);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr idPtr);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration,
            long hnsPeriodicity, IntPtr waveFormat, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long hnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr waveFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr waveFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, out IntPtr service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint dwFlags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SampleRate;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }
}