using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FrostLock.Services;

/// <summary>A mounted removable volume that can be selected as the convenience trigger.</summary>
public sealed record UsbDriveOption(string RootPath, string VolumeLabel, string VolumeSerial)
{
    public string DisplayName =>
        $"{RootPath.TrimEnd('\\')} {VolumeLabel} (serial {VolumeSerial})";
}

/// <summary>
/// Watches volume arrival/removal broadcasts on FrostLock's main HWND. It identifies a
/// selected volume by its filesystem serial, not its drive letter. This is a convenience
/// trigger only: volume serials are not cryptographic credentials and can be copied.
/// </summary>
public sealed class UsbDriveService : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x00000002;

    private HwndSource? _source;
    private string _targetSerial = string.Empty;
    private bool _enabled;
    private bool _targetPresent;
    private readonly Dictionary<char, UsbDriveOption> _mounted = new();

    public event Action<bool>? TargetPresenceChanged;

    public bool TargetPresent => _targetPresent;

    public static IReadOnlyList<UsbDriveOption> GetAvailableDrives()
    {
        var drives = new List<UsbDriveOption>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Removable) continue;
                if (!TryReadVolume(drive.RootDirectory.FullName, out var option)) continue;
                drives.Add(option);
            }
            catch
            {
                // A removable drive can disappear between enumeration and inspection.
            }
        }

        return drives.OrderBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>Updates the configured trigger. Existing presence only seeds state; it does
    /// not immediately start a freeze when settings are saved or FrostLock launches.</summary>
    public void Configure(bool enabled, string? serial)
    {
        _enabled = enabled;
        _targetSerial = (serial ?? string.Empty).Trim().ToUpperInvariant();
        _mounted.Clear();
        _targetPresent = false;

        if (!_enabled || _targetSerial.Length != 8) return;

        foreach (var drive in GetAvailableDrives())
        {
            Cache(drive);
            if (string.Equals(drive.VolumeSerial, _targetSerial, StringComparison.OrdinalIgnoreCase))
                _targetPresent = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DEVICECHANGE || !_enabled || lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var eventCode = wParam.ToInt32();
        if (eventCode is not (DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE))
            return IntPtr.Zero;

        try
        {
            var header = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
            if (header.DeviceType != DBT_DEVTYP_VOLUME || header.Size < Marshal.SizeOf<DEV_BROADCAST_VOLUME>())
                return IntPtr.Zero;

            var volume = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
            if (eventCode == DBT_DEVICEARRIVAL)
                ProcessArrival(volume.UnitMask);
            else
                ProcessRemoval(volume.UnitMask);
        }
        catch
        {
            // Ignore malformed or unsupported device notifications.
        }

        return IntPtr.Zero;
    }

    private void ProcessArrival(uint unitMask)
    {
        for (var index = 0; index < 26; index++)
        {
            if ((unitMask & (1u << index)) == 0) continue;

            var letter = (char)('A' + index);
            var root = $"{letter}:\\";

            // Windows may broadcast arrival just before the mount is queryable.
            if (!TryReadVolume(root, out var option)) continue;
            Cache(option);

            if (_targetPresent || !string.Equals(option.VolumeSerial, _targetSerial,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            _targetPresent = true;
            TargetPresenceChanged?.Invoke(true);
        }
    }

    private void ProcessRemoval(uint unitMask)
    {
        for (var index = 0; index < 26; index++)
        {
            if ((unitMask & (1u << index)) == 0) continue;

            var letter = (char)('A' + index);
            if (!_mounted.Remove(letter, out var option)) continue;

            if (!_targetPresent || !string.Equals(option.VolumeSerial, _targetSerial,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            // If another copy of the same volume serial remains mounted, keep the trigger
            // present instead of releasing on removal of just one copy.
            _targetPresent = _mounted.Values.Any(d => string.Equals(
                d.VolumeSerial, _targetSerial, StringComparison.OrdinalIgnoreCase));

            if (!_targetPresent)
                TargetPresenceChanged?.Invoke(false);
        }
    }

    private void Cache(UsbDriveOption option)
    {
        if (!string.IsNullOrEmpty(option.RootPath))
            _mounted[char.ToUpperInvariant(option.RootPath[0])] = option;
    }

    private static bool TryReadVolume(string root, out UsbDriveOption option)
    {
        option = null!;

        var label = new char[261];
        var fileSystem = new char[261];
        if (!GetVolumeInformation(root, label, label.Length, out var serial,
                out _, out _, fileSystem, fileSystem.Length))
            return false;

        option = new UsbDriveOption(root, new string(label).TrimEnd('\0'), serial.ToString("X8"));
        return true;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _mounted.Clear();
        TargetPresenceChanged = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public uint UnitMask;
        public short Flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        [Out] char[]? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out int maximumComponentLength,
        out int fileSystemFlags,
        [Out] char[]? fileSystemNameBuffer,
        int fileSystemNameSize);
}
