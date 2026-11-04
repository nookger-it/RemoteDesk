using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesk.Contracts.Sessions;

namespace RemoteDesk.Agent.Interactive;

/// <summary>
/// Captures the Windows desktop in the user session on a dedicated thread bound to the active
/// interactive input desktop (WinSta0\Default via OpenInputDesktop + SetThreadDesktop),
/// optionally scales it, draws the real mouse cursor, and encodes as JPEG.
/// Performance notes:
///  - Capture/encode bitmaps are reused between frames (no per-frame 8 MB allocations).
///  - Width/Height = 0 captures at native resolution with no rescale (sharpest text).
///  - When <c>skipUnchangedFrames</c> is enabled, identical frames return an empty payload so the
///    transport can skip sending them (a keyframe is still forced every second).
/// Strictly runs in Agent.Interactive (User Session), never in Agent.Service (Session 0).
/// </summary>
public sealed class ScreenCaptureEngine : IDisposable
{
    private readonly bool _captureAllMonitors;
    private readonly bool _skipUnchangedFrames;
    private readonly BlockingCollection<(long Sequence, TaskCompletionSource<byte[]> Tcs)>? _captureQueue;
    private readonly Thread? _captureThread;

    // Settings can be changed live (read by the capture thread on every frame)
    private volatile int _requestedWidth;
    private volatile int _requestedHeight;
    private long _jpegQuality;

    // Last produced frame size (reported in the ScreenFramePacket)
    private volatile int _lastWidth;
    private volatile int _lastHeight;

    // Capture-thread-only state (reused buffers)
    private Bitmap? _screenBitmap;
    private Graphics? _screenGraphics;
    private Bitmap? _scaledBitmap;
    private Graphics? _scaledGraphics;
    private ImageCodecInfo? _jpegCodec;
    private EncoderParameters? _cachedEncoderParams;
    private long _cachedEncoderQuality = -1;
    private readonly MemoryStream _encodeStream = new(512 * 1024);
    private ulong _lastFrameHash;
    private long _lastSentTicks;
    private bool _forceKeyframe = true;

    public ScreenCaptureEngine(
        int targetWidth = 1280,
        int targetHeight = 720,
        bool captureAllMonitors = false,
        long jpegQuality = 85L,
        bool skipUnchangedFrames = false)
    {
        _requestedWidth = Math.Max(0, targetWidth);
        _requestedHeight = Math.Max(0, targetHeight);
        _lastWidth = targetWidth > 0 ? targetWidth : 1280;
        _lastHeight = targetHeight > 0 ? targetHeight : 720;
        _captureAllMonitors = captureAllMonitors;
        _jpegQuality = Math.Clamp(jpegQuality, 40L, 95L);
        _skipUnchangedFrames = skipUnchangedFrames;

        if (OperatingSystem.IsWindows())
        {
            _captureQueue = new BlockingCollection<(long, TaskCompletionSource<byte[]>)>(boundedCapacity: 4);
            _captureThread = new Thread(WindowsInteractiveCaptureThreadLoop)
            {
                IsBackground = true,
                Name = "RemoteDesk-InteractiveDesktopCapture",
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();
        }
    }

    /// <summary>
    /// Applies new resolution / JPEG quality live. Width/Height = 0 means native resolution.
    /// </summary>
    public void ApplySettings(int targetWidth, int targetHeight, long jpegQuality)
    {
        _requestedWidth = Math.Max(0, targetWidth);
        _requestedHeight = Math.Max(0, targetHeight);
        Interlocked.Exchange(ref _jpegQuality, Math.Clamp(jpegQuality, 40L, 95L));
        _forceKeyframe = true;
    }

    /// <summary>Forces the next frame to be sent even if the screen has not changed (e.g. new viewer).</summary>
    public void RequestKeyframe() => _forceKeyframe = true;

    public async Task<ScreenFramePacket> CaptureFrameAsync(
        Guid sessionId,
        long sequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] payload;
        if (OperatingSystem.IsWindows() && _captureQueue is not null)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_captureQueue.TryAdd((sequence, tcs), millisecondsTimeout: 500, cancellationToken))
            {
                payload = await tcs.Task.WaitAsync(cancellationToken);
            }
            else
            {
                payload = GenerateSyntheticTestFrame(sequence);
            }
        }
        else
        {
            payload = GenerateSyntheticTestFrame(sequence);
        }

        return new ScreenFramePacket(
            SessionId: sessionId,
            FrameSequence: sequence,
            CapturedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Width: _lastWidth,
            Height: _lastHeight,
            Encoding: "jpeg",
            Payload: payload);
    }

    [SupportedOSPlatform("windows")]
    private void WindowsInteractiveCaptureThreadLoop()
    {
        // Attach this fresh worker thread directly to the active interactive input desktop (WinSta0\Default)
        // BEFORE creating any GDI/USER handles on this thread.
        IntPtr hDesk = OpenInputDesktop(0, false, 0x10000000);
        if (hDesk == IntPtr.Zero)
        {
            hDesk = OpenInputDesktop(0, false, 0x01FF);
        }

        if (hDesk != IntPtr.Zero)
        {
            _ = SetThreadDesktop(hDesk);
        }

        _jpegCodec = GetJpegEncoder();

        foreach (var (sequence, tcs) in _captureQueue!.GetConsumingEnumerable())
        {
            try
            {
                tcs.TrySetResult(CaptureWindowsScreenAsJpeg(sequence));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        DisposeCaptureBuffers();
    }

    [SupportedOSPlatform("windows")]
    private byte[] CaptureWindowsScreenAsJpeg(long sequence)
    {
        try
        {
            int srcX = _captureAllMonitors ? GetSystemMetrics(SM_XVIRTUALSCREEN) : 0;
            int srcY = _captureAllMonitors ? GetSystemMetrics(SM_YVIRTUALSCREEN) : 0;
            int screenWidth = Math.Max(640, _captureAllMonitors ? GetSystemMetrics(SM_CXVIRTUALSCREEN) : GetSystemMetrics(SM_CXSCREEN));
            int screenHeight = Math.Max(480, _captureAllMonitors ? GetSystemMetrics(SM_CYVIRTUALSCREEN) : GetSystemMetrics(SM_CYSCREEN));

            EnsureBitmap(ref _screenBitmap, ref _screenGraphics, screenWidth, screenHeight);

            bool captured;
            int cursorX = 0, cursorY = 0;
            try
            {
                _screenGraphics!.CopyFromScreen(srcX, srcY, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);
                DrawRealCursor(_screenGraphics, srcX, srcY, out cursorX, out cursorY);
                captured = true;
            }
            catch
            {
                // Fallback when running in headless CI runner without interactive WinSta0
                _screenGraphics!.Clear(Color.FromArgb(15, 23, 42));
                captured = false;
            }

            // Resolve output size (0 = native). Keep the screen aspect ratio.
            int reqW = _requestedWidth;
            int reqH = _requestedHeight;
            int outW = screenWidth;
            int outH = screenHeight;
            if (reqW > 0 && reqH > 0 && (reqW < screenWidth || reqH < screenHeight))
            {
                double scale = Math.Min((double)reqW / screenWidth, (double)reqH / screenHeight);
                outW = Math.Max(320, (int)Math.Round(screenWidth * scale) & ~1);
                outH = Math.Max(240, (int)Math.Round(screenHeight * scale) & ~1);
            }

            Bitmap frameBitmap;
            if (outW == screenWidth && outH == screenHeight)
            {
                frameBitmap = _screenBitmap!;
            }
            else
            {
                EnsureBitmap(ref _scaledBitmap, ref _scaledGraphics, outW, outH);
                _scaledGraphics!.InterpolationMode = InterpolationMode.Bilinear;
                _scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                _scaledGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                _scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
                _scaledGraphics.DrawImage(
                    _screenBitmap!,
                    new Rectangle(0, 0, outW, outH),
                    new Rectangle(0, 0, screenWidth, screenHeight),
                    GraphicsUnit.Pixel);
                frameBitmap = _scaledBitmap!;
            }

            _lastWidth = outW;
            _lastHeight = outH;

            // Fast sparse hash + cursor coordinates: skips unchanged frames while keeping mouse movement instant
            if (_skipUnchangedFrames && captured)
            {
                var hash = ComputeFastFrameHash(frameBitmap, cursorX, cursorY);
                var nowTicks = Environment.TickCount64;
                bool unchanged = hash == _lastFrameHash && !_forceKeyframe && (nowTicks - _lastSentTicks) < 1000;
                if (unchanged)
                {
                    return [];
                }

                _lastFrameHash = hash;
                _lastSentTicks = nowTicks;
                _forceKeyframe = false;
            }

            _encodeStream.Position = 0;
            _encodeStream.SetLength(0);
            if (_jpegCodec is not null)
            {
                var q = Interlocked.Read(ref _jpegQuality);
                if (_cachedEncoderParams is null || _cachedEncoderQuality != q)
                {
                    _cachedEncoderParams?.Dispose();
                    _cachedEncoderParams = new EncoderParameters(1);
                    _cachedEncoderParams.Param[0] = new EncoderParameter(Encoder.Quality, q);
                    _cachedEncoderQuality = q;
                }

                frameBitmap.Save(_encodeStream, _jpegCodec, _cachedEncoderParams);
            }
            else
            {
                frameBitmap.Save(_encodeStream, ImageFormat.Jpeg);
            }

            return _encodeStream.ToArray();
        }
        catch
        {
            return GenerateSyntheticTestFrame(sequence);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureBitmap(ref Bitmap? bitmap, ref Graphics? graphics, int width, int height)
    {
        if (bitmap is not null && bitmap.Width == width && bitmap.Height == height)
        {
            return;
        }

        graphics?.Dispose();
        bitmap?.Dispose();
        // 32bpp RGB matches the desktop surface => fastest BitBlt (no pixel format conversion)
        bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        graphics = Graphics.FromImage(bitmap);
    }

    [SupportedOSPlatform("windows")]
    private static void DrawRealCursor(Graphics g, int offsetX, int offsetY, out int cursorX, out int cursorY)
    {
        cursorX = 0;
        cursorY = 0;
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
        {
            return;
        }

        cursorX = ci.ptScreenPos.X;
        cursorY = ci.ptScreenPos.Y;

        int hotX = 0, hotY = 0;
        if (GetIconInfo(ci.hCursor, out var iconInfo))
        {
            hotX = iconInfo.xHotspot;
            hotY = iconInfo.yHotspot;
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
        }

        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc, ci.ptScreenPos.X - offsetX - hotX, ci.ptScreenPos.Y - offsetY - hotY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe ulong ComputeFastFrameHash(Bitmap bitmap, int cursorX, int cursorY)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            ulong hash = 14695981039346656037UL ^ (ulong)((long)cursorX << 32 | (uint)cursorY);
            int rowBytes = bitmap.Width * 4;
            int words = rowBytes / 8;
            // Sample every 4th scanline and every 4th 64-bit word (16x faster, < 0.15ms per 1080p frame)
            for (int y = 0; y < bitmap.Height; y += 4)
            {
                var row = (ulong*)((byte*)data.Scan0 + ((long)y * data.Stride));
                for (int i = 0; i < words; i += 4)
                {
                    hash = (hash ^ row[i]) * 1099511628211UL;
                }
            }

            return hash;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [SupportedOSPlatform("windows")]
    private static ImageCodecInfo? GetJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                return codec;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private void DisposeCaptureBuffers()
    {
        _cachedEncoderParams?.Dispose();
        _encodeStream.Dispose();
        _scaledGraphics?.Dispose();
        _scaledBitmap?.Dispose();
        _screenGraphics?.Dispose();
        _screenBitmap?.Dispose();
    }

    public void Dispose()
    {
        _captureQueue?.CompleteAdding();
    }

    private static byte[] GenerateSyntheticTestFrame(long sequence)
    {
        var data = new byte[256];
        data[0] = 0xFF;
        data[1] = 0xD8;
        BitConverter.TryWriteBytes(data.AsSpan(2, 8), sequence);
        data[^2] = 0xFF;
        data[^1] = 0xD9;
        return data;
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);
}
