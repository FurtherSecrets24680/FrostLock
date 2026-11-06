using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FrostLock.Services;

public sealed class TaskControlException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record TaskProcess(
    int Pid,
    string Name,
    string DisplayName,
    string? Publisher,
    string? Version,
    string? Path,
    string? IconKey,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    long DiskReadBytes,
    long DiskWriteBytes,
    double DiskReadBytesPerSec,
    double DiskWriteBytesPerSec,
    int ConnectionCount,
    IReadOnlyList<string> RemoteEndpoints,
    int ThreadCount,
    int SessionId,
    long StartedAtMs,
    string? MainWindowTitle,
    bool HasWindow,
    bool? Elevated,
    bool Protected,
    string? ProtectedReason);

public sealed record WindowsTaskSnapshot(
    long SampledAtMs,
    int SampleMilliseconds,
    string MachineName,
    string UserName,
    bool ControllerElevated,
    double TotalCpuPercent,
    long TotalMemoryBytes,
    long UsedMemoryBytes,
    long NetworkReceiveBytesPerSec,
    long NetworkSendBytesPerSec,
    int ProcessCount,
    int AppCount,
    int ConnectionCount,
    IReadOnlyList<TaskProcess> Processes);

public sealed record StartedTask(int Pid, string Name, string Path, long StartedAtMs);

public sealed record EndedTask(int Pid, string Name, bool Graceful);

public sealed record TaskIcon(string Key, string PngBase64);

public sealed class TaskManagerService
{
    private const int SampleMilliseconds = 350;
    private const int MaxIconsPerRequest = 48;
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "memory compression", "secure system", "idle",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "fontdrvhost", "dwm", "wudfhost", "audiodg", "frostlock"
    };
    private static readonly HashSet<string> StartableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com"
    };

    private readonly object _snapshotGate = new();
    private readonly ConcurrentDictionary<string, byte[]> _iconCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _iconSources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileMetadata> _fileMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long Ticks, string Key)> _iconKeys = new(StringComparer.OrdinalIgnoreCase);

    public WindowsTaskSnapshot GetSnapshot()
    {
        lock (_snapshotGate)
        {
            var rows = new List<Row>();
            var processList = Process.GetProcesses();
            try
            {
                foreach (var process in processList)
                {
                    try
                    {
                        if (process.Id == 0) continue;
                        rows.Add(BuildRow(process));
                    }
                    catch
                    {
                    }
                }

                var cpuStart = ReadSystemTimes();
                var networkStart = ReadNetworkBytes();
                foreach (var row in rows) row.First = Capture(row.Process);

                var started = Stopwatch.GetTimestamp();
                Thread.Sleep(SampleMilliseconds);

                foreach (var row in rows) row.Second = Capture(row.Process);
                var elapsed = Math.Max(0.05, Stopwatch.GetElapsedTime(started).TotalSeconds);
                var cpuEnd = ReadSystemTimes();
                var networkEnd = ReadNetworkBytes();

                var connections = ReadConnections();
                var memory = ReadMemory();
                var totalCpu = ComputeSystemCpu(cpuStart, cpuEnd, rows, elapsed);
                var processCount = rows.Count;
                var appCount = rows.Count(row => row.HasWindow);
                var connectionCount = connections.Values.Sum(item => item.Count);

                var processes = rows.Select(row => FinishRow(row, connections, elapsed)).ToList();
                processes.Sort((left, right) =>
                {
                    var cpu = right.CpuPercent.CompareTo(left.CpuPercent);
                    return cpu != 0 ? cpu : right.WorkingSetBytes.CompareTo(left.WorkingSetBytes);
                });

                var networkDeltaRx = Math.Max(0, networkEnd.Received - networkStart.Received);
                var networkDeltaTx = Math.Max(0, networkEnd.Sent - networkStart.Sent);

                return new WindowsTaskSnapshot(
                    DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    SampleMilliseconds,
                    Environment.MachineName,
                    Environment.UserName,
                    ReadElevation(Environment.ProcessId) == true,
                    Math.Round(totalCpu, 1),
                    memory.Total,
                    memory.Used,
                    (long)(networkDeltaRx / elapsed),
                    (long)(networkDeltaTx / elapsed),
                    processCount,
                    appCount,
                    connectionCount,
                    processes);
            }
            finally
            {
                foreach (var process in processList) process.Dispose();
            }
        }
    }

    public IReadOnlyList<TaskIcon> GetIcons(IEnumerable<string> keys)
    {
        var results = new List<TaskIcon>();
        foreach (var key in keys.Where(IsValidIconKey).Distinct(StringComparer.Ordinal).Take(MaxIconsPerRequest))
        {
            var bytes = _iconCache.GetOrAdd(key, value =>
                _iconSources.TryGetValue(value, out var path) ? ExtractIcon(path) : Array.Empty<byte>());
            if (bytes.Length > 0)
                results.Add(new TaskIcon(key, Convert.ToBase64String(bytes)));
        }
        return results;
    }

    public EndedTask EndTask(int pid, bool force)
    {
        if (pid <= 0) throw new TaskControlException("invalid_pid", "That process ID is not valid.");
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { throw new TaskControlException("not_found", "That process has already ended.", 404); }

        using (process)
        {
            var name = SafeProcessName(process);
            var reason = ProtectedReason(pid, name);
            if (reason is not null)
                throw new TaskControlException("protected_process", $"FrostLock will not end {name}. {reason}", 403);

            var graceful = false;
            try
            {
                if (!force && process.MainWindowHandle != IntPtr.Zero)
                {
                    graceful = process.CloseMainWindow();
                    if (graceful) graceful = process.WaitForExit(2500);
                }
            }
            catch
            {
                graceful = false;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(4000);
                    graceful = false;
                }
            }
            catch (InvalidOperationException)
            {
                throw new TaskControlException("not_found", "That process has already ended.", 404);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                throw new TaskControlException(
                    "access_denied",
                    $"Windows denied access to {name}. Run FrostLock as administrator to end elevated or protected tasks.",
                    403);
            }
            catch (Exception ex)
            {
                throw new TaskControlException("kill_failed", $"Windows could not end {name}: {ex.Message}", 409);
            }

            return new EndedTask(pid, name, graceful);
        }
    }

    public StartedTask StartTask(string path, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var requested = (path ?? string.Empty).Trim();
        if (requested.Length is 0 or > 4096 || requested.Contains('\0'))
            throw new TaskControlException("invalid_path", "Enter a full path to a program on this PC.");

        string fullPath;
        try { fullPath = Path.GetFullPath(requested); }
        catch { throw new TaskControlException("invalid_path", "That program path is not valid."); }

        if (!File.Exists(fullPath))
            throw new TaskControlException("not_found", "That program path does not exist on this PC.", 404);

        var extension = Path.GetExtension(fullPath);
        if (!StartableExtensions.Contains(extension))
            throw new TaskControlException("invalid_path", "Phones may start .exe, .bat, .cmd, and .com programs only.");

        if (arguments.Count > 32 || arguments.Any(value => value.Length > 512 || value.Contains('\0')))
            throw new TaskControlException("invalid_arguments", "Use at most 32 arguments of 512 characters each.");

        var directory = workingDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new TaskControlException("invalid_working_directory", "That working directory does not exist on this PC.");

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = extension is ".bat" or ".cmd"
                    ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                    : fullPath,
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = extension is ".bat" or ".cmd"
            };
            if (extension is ".bat" or ".cmd")
            {
                info.ArgumentList.Add("/c");
                info.ArgumentList.Add(fullPath);
            }
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var started = Process.Start(info)
                ?? throw new TaskControlException("start_failed", "Windows did not start that program.", 409);
            return new StartedTask(
                started.Id,
                SafeProcessName(started),
                fullPath,
                DateTimeOffset.Now.ToUnixTimeMilliseconds());
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            throw new TaskControlException(
                "access_denied",
                "Windows denied access. Run FrostLock as administrator to start this program.",
                403);
        }
        catch (TaskControlException) { throw; }
        catch (Exception ex)
        {
            throw new TaskControlException("start_failed", $"Windows could not start that program: {ex.Message}", 409);
        }
    }

    private Row BuildRow(Process process)
    {
        var path = SafePath(process);
        var metadata = path is null ? null : ReadFileMetadata(path);
        var iconKey = path is null ? null : IconKeyFor(path);
        if (iconKey is not null) _iconSources[iconKey] = path!;
        var name = SafeProcessName(process);
        var reason = ProtectedReason(process.Id, name);
        return new Row
        {
            Process = process,
            Pid = process.Id,
            Name = name,
            DisplayName = metadata?.Description ?? FriendlyName(name),
            Publisher = metadata?.Company,
            Version = metadata?.Version,
            Path = path,
            IconKey = iconKey,
            ThreadCount = SafeThreadCount(process),
            SessionId = SafeSessionId(process),
            StartedAtMs = SafeStartTimeMs(process),
            MainWindowTitle = SafeWindowTitle(process),
            HasWindow = SafeHasWindow(process),
            Elevated = ReadElevation(process.Id),
            ProtectedReason = reason,
        };
    }

    private static TaskProcess FinishRow(Row row, IReadOnlyDictionary<int, ConnectionSummary> connections, double elapsed)
    {
        var cpuDelta = DeltaTime(row.First?.Cpu, row.Second?.Cpu);
        var readDelta = Delta(row.First?.ReadBytes, row.Second?.ReadBytes);
        var writeDelta = Delta(row.First?.WriteBytes, row.Second?.WriteBytes);
        var summary = connections.TryGetValue(row.Pid, out var value) ? value : ConnectionSummary.Empty;

        return new TaskProcess(
            row.Pid,
            row.Name,
            row.DisplayName,
            row.Publisher,
            row.Version,
            row.Path,
            row.IconKey,
            Math.Round(Math.Clamp(cpuDelta.TotalSeconds / elapsed / Environment.ProcessorCount * 100, 0, 100), 1),
            (long)(row.Second?.WorkingSet ?? row.First?.WorkingSet ?? 0),
            (long)(row.Second?.PrivateBytes ?? row.First?.PrivateBytes ?? 0),
            (long)(row.Second?.ReadBytes ?? row.First?.ReadBytes ?? 0),
            (long)(row.Second?.WriteBytes ?? row.First?.WriteBytes ?? 0),
            Math.Round(readDelta / elapsed, 0),
            Math.Round(writeDelta / elapsed, 0),
            summary.Count,
            summary.Endpoints,
            row.ThreadCount,
            row.SessionId,
            row.StartedAtMs,
            row.MainWindowTitle,
            row.HasWindow,
            row.Elevated,
            row.ProtectedReason is not null,
            row.ProtectedReason);
    }

    private static Sample Capture(Process process)
    {
        TimeSpan? cpu = null;
        try { cpu = process.TotalProcessorTime; } catch { }

        ulong? read = null;
        ulong? write = null;
        ulong? workingSet = null;
        ulong? privateBytes = null;
        try { workingSet = (ulong)process.WorkingSet64; } catch { }
        try { privateBytes = (ulong)process.PrivateMemorySize64; } catch { }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle != IntPtr.Zero)
        {
            try
            {
                if (GetProcessIoCounters(handle, out var counters))
                {
                    read = counters.ReadTransferCount;
                    write = counters.WriteTransferCount;
                }
            }
            finally { CloseHandle(handle); }
        }

        return new Sample(cpu, workingSet, privateBytes, read, write);
    }

    private static double ComputeSystemCpu(
        SystemTimes? start,
        SystemTimes? end,
        IReadOnlyList<Row> rows,
        double elapsed)
    {
        if (start is { } first && end is { } second)
        {
            var idle = DeltaTime(first.Idle, second.Idle).TotalSeconds;
            var kernel = DeltaTime(first.Kernel, second.Kernel).TotalSeconds;
            var user = DeltaTime(first.User, second.User).TotalSeconds;
            var total = kernel + user;
            var busy = total - idle;
            if (total > 0) return Math.Clamp(busy / total * 100, 0, 100);
        }

        var processTime = rows.Sum(row => DeltaTime(row.First?.Cpu, row.Second?.Cpu).TotalSeconds);
        return Math.Clamp(processTime / elapsed / Environment.ProcessorCount * 100, 0, 100);
    }

    private static string? ProtectedReason(int pid, string name)
    {
        if (pid <= 4) return "Windows core processes cannot be ended.";
        if (pid == Environment.ProcessId || name.Equals("FrostLock", StringComparison.OrdinalIgnoreCase))
            return "FrostLock protects itself so phones cannot disable the lock.";
        if (ProtectedNames.Contains(name))
            return $"{name} is required by Windows and cannot be ended from a phone.";
        return null;
    }

    private static bool IsValidIconKey(string key) =>
        key.Length is > 7 and <= 32 && key.All(character => char.IsAsciiLetterOrDigit(character));

    private string? IconKeyFor(string path)
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            var cached = _iconKeys.TryGetValue(path, out var entry) && entry.Ticks == ticks ? entry.Key : null;
            if (cached is not null) return cached;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{path.ToLowerInvariant()}|{ticks}")))
                .ToLowerInvariant()[..16];
            _iconKeys[path] = (ticks, key);
            return key;
        }
        catch { return null; }
    }

    private static byte[] ExtractIcon(string path)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return Array.Empty<byte>();
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private FileMetadata? ReadFileMetadata(string path)
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            var cached = _fileMetadata.TryGetValue(path, out var entry) && entry.Ticks == ticks ? entry : null;
            if (cached is not null) return cached;

            var info = FileVersionInfo.GetVersionInfo(path);
            var metadata = new FileMetadata(
                ticks,
                NullIfBlank(info.FileDescription),
                NullIfBlank(info.CompanyName),
                NullIfBlank(info.FileVersion) ?? NullIfBlank(info.ProductVersion));
            _fileMetadata[path] = metadata;
            return metadata;
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FriendlyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown process";
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string SafeProcessName(Process process)
    {
        try { return process.ProcessName; } catch { return "process"; }
    }

    private static string? SafePath(Process process)
    {
        try { return process.MainModule?.FileName; } catch { return null; }
    }

    private static int SafeThreadCount(Process process)
    {
        try { return process.Threads.Count; } catch { return 0; }
    }

    private static int SafeSessionId(Process process)
    {
        try { return process.SessionId; } catch { return -1; }
    }

    private static long SafeStartTimeMs(Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(); }
        catch { return 0; }
    }

    private static string? SafeWindowTitle(Process process)
    {
        try
        {
            var title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }
        catch { return null; }
    }

    private static bool SafeHasWindow(Process process)
    {
        try { return process.MainWindowHandle != IntPtr.Zero; } catch { return false; }
    }

    private static bool? ReadElevation(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(handle, TokenQuery, out token)) return null;
            if (!GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(int), out _)) return null;
            return elevated != 0;
        }
        catch { return null; }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(handle);
        }
    }

    private static SystemTimes? ReadSystemTimes()
    {
        try
        {
            return GetSystemTimes(out var idle, out var kernel, out var user)
                ? new SystemTimes(
                    TimeSpan.FromTicks(FileTime(idle)),
                    TimeSpan.FromTicks(FileTime(kernel)),
                    TimeSpan.FromTicks(FileTime(user)))
                : null;
        }
        catch { return null; }
    }

    private static long FileTime(FILETIME value) => ((long)value.High << 32) | value.Low;

    private static (long Total, long Used) ReadMemory()
    {
        try
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
            var used = (long)status.TotalPhysical - (long)status.AvailablePhysical;
            return ((long)status.TotalPhysical, Math.Max(0, used));
        }
        catch { return (0, 0); }
    }

    private static (long Received, long Sent) ReadNetworkBytes()
    {
        long received = 0;
        long sent = 0;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    var statistics = adapter.GetIPv4Statistics();
                    received += statistics.BytesReceived;
                    sent += statistics.BytesSent;
                }
                catch { }
            }
        }
        catch { }
        return (received, sent);
    }

    private static Dictionary<int, ConnectionSummary> ReadConnections()
    {
        var counts = new Dictionary<int, int>();
        var endpoints = new Dictionary<int, List<string>>();

        ReadTcpRows(counts, endpoints);
        ReadUdpRows(counts);
        ReadTcp6Rows(counts, endpoints);
        ReadUdp6Rows(counts);

        return counts.ToDictionary(
            pair => pair.Key,
            pair => new ConnectionSummary(
                pair.Value,
                endpoints.TryGetValue(pair.Key, out var list) ? list.Distinct(StringComparer.Ordinal).Take(6).ToList() : new List<string>()));
    }

    private static void ReadTcpRows(Dictionary<int, int> counts, Dictionary<int, List<string>> endpoints)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableClass.OwnerPidAll, 0);
        if (result != ErrorInsufficientBuffer && result != 0) return;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var memory = Marshal.AllocHGlobal(size);
            var requested = size;
            result = GetExtendedTcpTable(memory, ref requested, false, AfInet, TcpTableClass.OwnerPidAll, 0);
            if (result != 0)
            {
                Marshal.FreeHGlobal(memory);
                if (result != ErrorInsufficientBuffer) return;
                size = Math.Max(requested, size * 2);
                continue;
            }

            try
            {
                var count = Marshal.ReadInt32(memory);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                var offset = IntPtr.Add(memory, 4);
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(offset);
                    offset = IntPtr.Add(offset, rowSize);
                    if (row.OwningPid == 0 || row.State == MibTcpState.Listen) continue;

                    counts[row.OwningPid] = counts.GetValueOrDefault(row.OwningPid) + 1;
                    if (row.RemoteAddress == 0) continue;
                    var endpoint = $"{new IPAddress(BitConverter.GetBytes(row.RemoteAddress))}:{NetworkPort(row.RemotePort)}";
                    var list = endpoints.TryGetValue(row.OwningPid, out var existing) ? existing : endpoints[row.OwningPid] = new List<string>();
                    if (list.Count < 6) list.Add(endpoint);
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
            return;
        }
    }

    private static void ReadUdpRows(Dictionary<int, int> counts)
    {
        var size = 0;
        var result = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, UdpTableClass.OwnerPid, 0);
        if (result != ErrorInsufficientBuffer && result != 0) return;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var memory = Marshal.AllocHGlobal(size);
            var requested = size;
            result = GetExtendedUdpTable(memory, ref requested, false, AfInet, UdpTableClass.OwnerPid, 0);
            if (result != 0)
            {
                Marshal.FreeHGlobal(memory);
                if (result != ErrorInsufficientBuffer) return;
                size = Math.Max(requested, size * 2);
                continue;
            }

            try
            {
                var count = Marshal.ReadInt32(memory);
                var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
                var offset = IntPtr.Add(memory, 4);
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(offset);
                    offset = IntPtr.Add(offset, rowSize);
                    if (row.OwningPid == 0) continue;
                    counts[row.OwningPid] = counts.GetValueOrDefault(row.OwningPid) + 1;
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
            return;
        }
    }

    private static void ReadTcp6Rows(Dictionary<int, int> counts, Dictionary<int, List<string>> endpoints)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet6, TcpTableClass.OwnerPidAll, 0);
        if (result != ErrorInsufficientBuffer && result != 0) return;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var memory = Marshal.AllocHGlobal(size);
            var requested = size;
            result = GetExtendedTcpTable(memory, ref requested, false, AfInet6, TcpTableClass.OwnerPidAll, 0);
            if (result != 0)
            {
                Marshal.FreeHGlobal(memory);
                if (result != ErrorInsufficientBuffer) return;
                size = Math.Max(requested, size * 2);
                continue;
            }

            try
            {
                var count = Marshal.ReadInt32(memory);
                var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                var offset = IntPtr.Add(memory, 4);
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(offset);
                    offset = IntPtr.Add(offset, rowSize);
                    if (row.OwningPid == 0 || row.State == MibTcpState.Listen) continue;

                    counts[row.OwningPid] = counts.GetValueOrDefault(row.OwningPid) + 1;
                    if (row.RemoteAddress is null || row.RemoteAddress.All(value => value == 0)) continue;
                    var endpoint = $"[{new IPAddress(row.RemoteAddress)}]:{NetworkPort(row.RemotePort)}";
                    var list = endpoints.TryGetValue(row.OwningPid, out var existing) ? existing : endpoints[row.OwningPid] = new List<string>();
                    if (list.Count < 6) list.Add(endpoint);
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
            return;
        }
    }

    private static void ReadUdp6Rows(Dictionary<int, int> counts)
    {
        var size = 0;
        var result = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet6, UdpTableClass.OwnerPid, 0);
        if (result != ErrorInsufficientBuffer && result != 0) return;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var memory = Marshal.AllocHGlobal(size);
            var requested = size;
            result = GetExtendedUdpTable(memory, ref requested, false, AfInet6, UdpTableClass.OwnerPid, 0);
            if (result != 0)
            {
                Marshal.FreeHGlobal(memory);
                if (result != ErrorInsufficientBuffer) return;
                size = Math.Max(requested, size * 2);
                continue;
            }

            try
            {
                var count = Marshal.ReadInt32(memory);
                var rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
                var offset = IntPtr.Add(memory, 4);
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(offset);
                    offset = IntPtr.Add(offset, rowSize);
                    if (row.OwningPid == 0) continue;
                    counts[row.OwningPid] = counts.GetValueOrDefault(row.OwningPid) + 1;
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
            return;
        }
    }

    private static ushort NetworkPort(uint raw) =>
        (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static TimeSpan DeltaTime(TimeSpan? first, TimeSpan? second) =>
        first is null || second is null ? TimeSpan.Zero : second.Value - first.Value;

    private static long Delta(ulong? first, ulong? second) =>
        first is null || second is null ? 0L : (long)Math.Max(0, second.Value - first.Value);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int informationClass, out int information, int informationLength, out int returnLength);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, TcpTableClass tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int addressFamily, UdpTableClass tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherOperationCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public MibTcpState State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public int OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddress;
        public uint LocalPort;
        public int OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public MibTcpState State;
        public int OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        public int OwningPid;
    }

    private enum MibTcpState : uint
    {
        Closed = 1,
        Listen = 2,
        SynSent = 3,
        SynReceived = 4,
        Established = 5,
        FinWait1 = 6,
        FinWait2 = 7,
        CloseWait = 8,
        Closing = 9,
        LastAck = 10,
        TimeWait = 11,
        DeleteTcb = 12
    }

    private enum TcpTableClass
    {
        OwnerPidAll = 5
    }

    private enum UdpTableClass
    {
        OwnerPid = 1
    }

    private sealed record SystemTimes(TimeSpan Idle, TimeSpan Kernel, TimeSpan User);
    private sealed record Sample(TimeSpan? Cpu, ulong? WorkingSet, ulong? PrivateBytes, ulong? ReadBytes, ulong? WriteBytes);
    private sealed record FileMetadata(long Ticks, string? Description, string? Company, string? Version);
    private sealed record ConnectionSummary(int Count, IReadOnlyList<string> Endpoints)
    {
        public static readonly ConnectionSummary Empty = new(0, Array.Empty<string>());
    }

    private sealed class Row
    {
        public required Process Process { get; init; }
        public int Pid { get; init; }
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? Publisher { get; init; }
        public string? Version { get; init; }
        public string? Path { get; init; }
        public string? IconKey { get; init; }
        public int ThreadCount { get; init; }
        public int SessionId { get; init; }
        public long StartedAtMs { get; init; }
        public string? MainWindowTitle { get; init; }
        public bool HasWindow { get; init; }
        public bool? Elevated { get; init; }
        public string? ProtectedReason { get; init; }
        public Sample? First { get; set; }
        public Sample? Second { get; set; }
    }
}
