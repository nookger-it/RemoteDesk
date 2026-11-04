using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RemoteDesk.Agent.Interactive;

/// <summary>
/// Native Win32 Clipboard helper (CF_UNICODETEXT and CF_HDROP) for Agent.Interactive
/// running in the interactive user desktop session (WinSta0\Default).
/// Supports full Unicode (Thai, English, multiline, symbols) and file-drop clipboard entries.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Win32ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    public static string? TryGetText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        AttachToInputDesktop();
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                return null;
            }

            IntPtr hData = GetClipboardData(CF_UNICODETEXT);
            if (hData == IntPtr.Zero)
            {
                return null;
            }

            IntPtr ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                GlobalUnlock(hData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TrySetText(string text)
    {
        if (!OperatingSystem.IsWindows() || text is null)
        {
            return false;
        }

        AttachToInputDesktop();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    int byteCount = (text.Length + 1) * 2;
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)byteCount);
                    if (hGlobal == IntPtr.Zero)
                    {
                        return false;
                    }

                    IntPtr target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(15);
        }

        return false;
    }

    /// <summary>
    /// Places a received file into the Windows Clipboard as CF_HDROP so the user can also press Ctrl+V
    /// in any Windows Explorer folder on the remote machine to paste the transferred file.
    /// </summary>
    public static bool TrySetFileDropList(string fullFilePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fullFilePath) || !File.Exists(fullFilePath))
        {
            return false;
        }

        AttachToInputDesktop();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    // DROPFILES struct is 20 bytes: pFiles=20, pt=(0,0), fNC=0, fWide=1 (Unicode)
                    byte[] pathBytes = Encoding.Unicode.GetBytes(fullFilePath + "\0\0");
                    int totalBytes = 20 + pathBytes.Length;

                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)totalBytes);
                    if (hGlobal == IntPtr.Zero)
                    {
                        return false;
                    }

                    IntPtr ptr = GlobalLock(hGlobal);
                    if (ptr == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    try
                    {
                        Marshal.WriteInt32(ptr, 0, 20); // pFiles offset = 20 bytes
                        Marshal.WriteInt32(ptr, 4, 0);  // pt.x
                        Marshal.WriteInt32(ptr, 8, 0);  // pt.y
                        Marshal.WriteInt32(ptr, 12, 0); // fNC
                        Marshal.WriteInt32(ptr, 16, 1); // fWide = TRUE (Unicode)
                        Marshal.Copy(pathBytes, 0, ptr + 20, pathBytes.Length);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    if (SetClipboardData(CF_HDROP, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(15);
        }

        return false;
    }

    private static void AttachToInputDesktop()
    {
        IntPtr hDesk = OpenInputDesktop(0, false, 0x10000000);
        if (hDesk != IntPtr.Zero)
        {
            _ = SetThreadDesktop(hDesk);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);
}

