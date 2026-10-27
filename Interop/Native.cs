using System.Runtime.InteropServices;

namespace FrostLock.Interop;

/// <summary>Win32 API declarations used by FrostLock.</summary>
internal static class Native
{
    // Input freezing
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool fBlockIt);

    // Low-level hooks
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;

    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;

    internal delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Global hotkeys
    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Window placement / focus
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    // Power / idle
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SetThreadExecutionState(uint esFlags);

    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED = 0x00000001;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    // Rounded window corners (Windows 11)
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Audio feedback
    internal const uint MB_ICONASTERISK = 0x00000040;
    internal const uint MB_ICONEXCLAMATION = 0x00000030;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MessageBeep(uint uType);

    // Session lock on release
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LockWorkStation();

    // DPAPI: keep the PIN hash unreadable in the settings file.
    internal const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, out IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    internal static byte[]? DpapiProtect(byte[] data, byte[] entropy)
        => DpapiTransform(data, entropy, protect: true);

    internal static byte[]? DpapiUnprotect(byte[] data, byte[] entropy)
        => DpapiTransform(data, entropy, protect: false);

    private static byte[]? DpapiTransform(byte[] data, byte[] entropy, bool protect)
    {
        if (data.Length == 0) return null;

        var dataHandle = Marshal.AllocHGlobal(data.Length);
        var entropyDataHandle = IntPtr.Zero;
        var entropyBlobHandle = IntPtr.Zero;

        try
        {
            Marshal.Copy(data, 0, dataHandle, data.Length);

            // pOptionalEntropy is a DATA_BLOB pointer, not a raw byte pointer. Marshalling
            // raw bytes here makes CryptProtectData fail with ERROR_INVALID_PARAMETER.
            if (entropy.Length > 0)
            {
                entropyDataHandle = Marshal.AllocHGlobal(entropy.Length);
                Marshal.Copy(entropy, 0, entropyDataHandle, entropy.Length);

                var entropyBlob = new DATA_BLOB { cbData = entropy.Length, pbData = entropyDataHandle };
                entropyBlobHandle = Marshal.AllocHGlobal(Marshal.SizeOf<DATA_BLOB>());
                Marshal.StructureToPtr(entropyBlob, entropyBlobHandle, fDeleteOld: false);
            }

            var input = new DATA_BLOB { cbData = data.Length, pbData = dataHandle };

            bool ok;
            DATA_BLOB output;
            IntPtr description = IntPtr.Zero;

            if (protect)
            {
                ok = CryptProtectData(ref input, "FrostLock PIN", entropyBlobHandle,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            }
            else
            {
                ok = CryptUnprotectData(ref input, out description, entropyBlobHandle,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            }

            try
            {
                if (!ok) return null;

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                if (description != IntPtr.Zero) LocalFree(description);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dataHandle);
            if (entropyDataHandle != IntPtr.Zero) Marshal.FreeHGlobal(entropyDataHandle);
            if (entropyBlobHandle != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlobHandle);
        }
    }
}
