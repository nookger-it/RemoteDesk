using System.Runtime.InteropServices;
using RemoteDesk.Contracts.Sessions;

namespace RemoteDesk.Agent.Interactive;

/// <summary>
/// Injects Mouse and Keyboard events in the Windows User Session via Win32 SendInput
/// after enforcing the Session Permission Gate.
/// Strictly resides in Agent.Interactive, never in Agent.Service (Session 0).
/// </summary>
public sealed class InputInjectorEngine
{
    private readonly bool _executeNativeSendInput;
    private long _processedCount;
    private long _blockedCount;

    public InputInjectorEngine(bool executeNativeSendInput = true)
    {
        _executeNativeSendInput = executeNativeSendInput;
    }

    public long ProcessedEventsCount => Interlocked.Read(ref _processedCount);
    public long BlockedEventsCount => Interlocked.Read(ref _blockedCount);
    public (int AbsX, int AbsY) LastMappedAbsolutePosition { get; private set; }
    public int LastInjectedVirtualKey { get; private set; }

    public Task<InputControlAckPacket> ProcessInputAsync(
        RemoteInputEventPacket packet,
        bool allowInputControl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!allowInputControl)
        {
            var blocked = Interlocked.Increment(ref _blockedCount);
            return Task.FromResult(new InputControlAckPacket(
                packet.SessionId,
                Accepted: false,
                Reason: "Permission denied: Session is View-Only.",
                ProcessedEventsCount: Interlocked.Read(ref _processedCount),
                BlockedEventsCount: blocked));
        }

        var clampedX = Math.Clamp(packet.NormalizedX, 0.0, 1.0);
        var clampedY = Math.Clamp(packet.NormalizedY, 0.0, 1.0);

        // Win32 SendInput absolute coordinates range from 0 to 65535
        int absX = (int)Math.Round(clampedX * 65535.0);
        int absY = (int)Math.Round(clampedY * 65535.0);

        if (packet.EventType is RemoteInputEventType.MouseMove or RemoteInputEventType.MouseDown or RemoteInputEventType.MouseUp or RemoteInputEventType.MouseWheel)
        {
            LastMappedAbsolutePosition = (absX, absY);
        }

        if (packet.EventType is RemoteInputEventType.KeyDown or RemoteInputEventType.KeyUp)
        {
            LastInjectedVirtualKey = packet.VirtualKeyCode;
        }

        if (_executeNativeSendInput && OperatingSystem.IsWindows())
        {
            ExecuteWin32SendInput(packet, absX, absY);
        }

        var processed = Interlocked.Increment(ref _processedCount);
        return Task.FromResult(new InputControlAckPacket(
            packet.SessionId,
            Accepted: true,
            Reason: null,
            ProcessedEventsCount: processed,
            BlockedEventsCount: Interlocked.Read(ref _blockedCount)));
    }

    private static void ExecuteWin32SendInput(RemoteInputEventPacket packet, int absX, int absY)
    {
        try
        {
            var input = new INPUT();
            switch (packet.EventType)
            {
                case RemoteInputEventType.MouseMove:
                    input.type = INPUT_MOUSE;
                    input.U.mi.dx = absX;
                    input.U.mi.dy = absY;
                    input.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
                    break;

                case RemoteInputEventType.MouseDown:
                    input.type = INPUT_MOUSE;
                    input.U.mi.dx = absX;
                    input.U.mi.dy = absY;
                    input.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | GetMouseDownFlag(packet.MouseButton);
                    break;

                case RemoteInputEventType.MouseUp:
                    input.type = INPUT_MOUSE;
                    input.U.mi.dx = absX;
                    input.U.mi.dy = absY;
                    input.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | GetMouseUpFlag(packet.MouseButton);
                    break;

                case RemoteInputEventType.MouseWheel:
                    input.type = INPUT_MOUSE;
                    input.U.mi.mouseData = (uint)packet.WheelDelta;
                    input.U.mi.dwFlags = MOUSEEVENTF_WHEEL;
                    break;

                case RemoteInputEventType.KeyDown:
                    input.type = INPUT_KEYBOARD;
                    input.U.ki.wVk = (ushort)Math.Clamp(packet.VirtualKeyCode, 0, 255);
                    input.U.ki.dwFlags = 0;
                    break;

                case RemoteInputEventType.KeyUp:
                    input.type = INPUT_KEYBOARD;
                    input.U.ki.wVk = (ushort)Math.Clamp(packet.VirtualKeyCode, 0, 255);
                    input.U.ki.dwFlags = KEYEVENTF_KEYUP;
                    break;

                default:
                    return;
            }

            INPUT[] inputs = [input];
            IntPtr hDesk = OpenInputDesktop(0, false, 0x10000000);
            if (hDesk != IntPtr.Zero)
            {
                _ = SetThreadDesktop(hDesk);
            }
            _ = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }
        catch
        {
            // Ignore Win32 injection failure if locked desktop / UAC secure desktop
        }
    }

    private static uint GetMouseDownFlag(MouseButtonType button) => button switch
    {
        MouseButtonType.Right => MOUSEEVENTF_RIGHTDOWN,
        MouseButtonType.Middle => MOUSEEVENTF_MIDDLEDOWN,
        _ => MOUSEEVENTF_LEFTDOWN
    };

    private static uint GetMouseUpFlag(MouseButtonType button) => button switch
    {
        MouseButtonType.Right => MOUSEEVENTF_RIGHTUP,
        MouseButtonType.Middle => MOUSEEVENTF_MIDDLEUP,
        _ => MOUSEEVENTF_LEFTUP
    };

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);
}
