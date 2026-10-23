using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetLedger.Service.Collection;

/// <summary>How an app is stored: <see cref="Key"/> groups traffic, <see cref="Name"/> is shown.</summary>
internal sealed record AppIdentity(string Key, string Name, string? Path);

/// <summary>
/// How traffic is grouped into apps. Programs are keyed by file name and description, not full path, so an app
/// that installs every update into a new version folder (Discord's app-1.0.x, Store apps, ...) stays one app.
/// </summary>
internal static class AppKeys
{
    public const string ServicePrefix = "svc:";

    public static string ForExecutable(string path, string name) =>
        $"exe:{Path.GetFileName(path).ToLowerInvariant()}|{name}";

    /// <summary>Key for an svchost.exe instance. Per-user services lose their sign-in-specific suffix.</summary>
    public static string ForServices(IEnumerable<string> serviceNames) =>
        ServicePrefix + string.Join('+', serviceNames
            .Select(n => ServiceNames.BaseName(n).ToLowerInvariant())
            .Distinct()
            .Order(StringComparer.Ordinal));

    /// <summary>The key a stored app would get today (used to bring older records up to date).</summary>
    public static string Current(string key, string name, string? path)
    {
        if (key.StartsWith(ServicePrefix, StringComparison.Ordinal))
        {
            return ForServices(key[ServicePrefix.Length..].Split('+'));
        }

        return path is null ? key : ForExecutable(path, name);
    }
}

/// <summary>
/// Maps process ids to apps. Ids are reused by Windows, so every lookup checks the process creation time.
/// svchost.exe instances are split by the services they host.
/// </summary>
internal sealed unsafe partial class ProcessResolver
{
    public static readonly AppIdentity System = new("system", "System (kernel)", null);
    public static readonly AppIdentity Unknown = new("unknown", "Exited processes", null);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly TimeSpan CacheIdle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ServiceMapMaxAge = TimeSpan.FromSeconds(5);

    private readonly Dictionary<uint, Entry> _byPid = new();
    private readonly Dictionary<string, string> _nameByPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, List<(string Name, string Display)>> _servicesByPid = new();
    private long _servicesLoadedAt;

    private sealed class Entry(long created, AppIdentity app)
    {
        public long Created { get; } = created;
        public AppIdentity App { get; } = app;
        public long LastUsed { get; set; }
    }

    public AppIdentity Resolve(uint pid)
    {
        if (pid is 0 or 4)
        {
            return System;
        }

        long now = Stopwatch.GetTimestamp();
        using SafeProcessHandle handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
        {
            // Already gone: the last thing we knew this id to be is the best guess.
            if (_byPid.TryGetValue(pid, out Entry? stale))
            {
                stale.LastUsed = now;
                return stale.App;
            }

            return Unknown;
        }

        long created = GetProcessTimes(handle, out long creation, out _, out _, out _) ? creation : 0;
        if (_byPid.TryGetValue(pid, out Entry? entry) && entry.Created == created)
        {
            entry.LastUsed = now;
            return entry.App;
        }

        string? path = QueryImagePath(handle);
        AppIdentity app = path is null ? new AppIdentity("pid-unnamed", "Unnamed system processes", null) : Identify(path, pid);
        _byPid[pid] = new Entry(created, app) { LastUsed = now };
        return app;
    }

    public void Prune()
    {
        long cutoff = Stopwatch.GetTimestamp() - (long)(CacheIdle.TotalSeconds * Stopwatch.Frequency);
        foreach (uint pid in _byPid.Where(p => p.Value.LastUsed < cutoff).Select(p => p.Key).ToList())
        {
            _byPid.Remove(pid);
        }

        if (_nameByPath.Count > 2000)
        {
            _nameByPath.Clear();
        }
    }

    private AppIdentity Identify(string path, uint pid)
    {
        if (string.Equals(Path.GetFileName(path), "svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            List<(string Name, string Display)>? services = ServicesFor(pid);
            if (services is { Count: > 0 })
            {
                return new AppIdentity(AppKeys.ForServices(services.Select(s => s.Name)), ServiceNames.Describe(services), path);
            }
        }

        if (!_nameByPath.TryGetValue(path, out string? display))
        {
            display = DescribeFile(path);
            _nameByPath[path] = display;
        }

        return new AppIdentity(AppKeys.ForExecutable(path, display), display, path);
    }

    private static string DescribeFile(string path)
    {
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description))
            {
                return description;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private List<(string Name, string Display)>? ServicesFor(uint pid)
    {
        if (!_servicesByPid.ContainsKey(pid)
            && (_servicesLoadedAt == 0 || Stopwatch.GetElapsedTime(_servicesLoadedAt) > ServiceMapMaxAge))
        {
            _servicesByPid = LoadServiceMap();
            _servicesLoadedAt = Stopwatch.GetTimestamp();
        }

        return _servicesByPid.TryGetValue(pid, out var list) ? list : null;
    }

    private static string? QueryImagePath(SafeProcessHandle handle)
    {
        char* buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    // ENUM_SERVICE_STATUS_PROCESSW, 64-bit: two string pointers then SERVICE_STATUS_PROCESS (pid at +28).
    private const int ServiceEntrySize = 56;
    private const int ServiceEntryPidOffset = 16 + 28;

    private static Dictionary<uint, List<(string Name, string Display)>> LoadServiceMap()
    {
        var map = new Dictionary<uint, List<(string, string)>>();
        nint scm = OpenSCManager(null, null, 0x0001 | 0x0004); // CONNECT | ENUMERATE_SERVICE
        if (scm == 0)
        {
            return map;
        }

        try
        {
            uint resume = 0;
            uint bufferSize = 64 * 1024;
            while (true)
            {
                byte* buffer = (byte*)NativeMemory.Alloc(bufferSize);
                try
                {
                    bool ok = EnumServicesStatusEx(scm, 0, 0x30, 1, buffer, bufferSize, out uint needed, out uint count, ref resume, 0);
                    int error = ok ? 0 : Marshal.GetLastPInvokeError();
                    if (!ok && error != 234) // ERROR_MORE_DATA
                    {
                        return map;
                    }

                    for (uint i = 0; i < count; i++)
                    {
                        byte* entry = buffer + i * ServiceEntrySize;
                        uint pid = *(uint*)(entry + ServiceEntryPidOffset);
                        if (pid == 0)
                        {
                            continue;
                        }

                        string name = new((char*)*(nint*)entry);
                        string display = new((char*)*(nint*)(entry + 8));
                        if (!map.TryGetValue(pid, out var list))
                        {
                            map[pid] = list = new List<(string, string)>();
                        }

                        list.Add((name, display));
                    }

                    if (ok)
                    {
                        return map;
                    }

                    bufferSize = Math.Max(bufferSize, needed + 4096);
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusEx(
        nint scm, int infoLevel, uint serviceType, uint serviceState, byte* buffer, uint bufferSize,
        out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, nint groupName);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
