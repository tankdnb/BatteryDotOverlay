using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BatteryDotOverlay;

internal sealed class AppInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private AppInstanceGuard(Mutex mutex, string mutexName)
    {
        _mutex = mutex;
        MutexName = mutexName;
    }

    public string MutexName { get; }

    public static InstanceLockResult TryAcquire(string overlayKey)
    {
        var mutexName = CreateMutexName(overlayKey);
        var mutex = new Mutex(initiallyOwned: false, name: mutexName);

        try
        {
            try
            {
                if (!mutex.WaitOne(0, exitContext: false))
                {
                    mutex.Dispose();
                    return InstanceLockResult.Failed(EnumerateRunningInstances());
                }
            }
            catch (AbandonedMutexException)
            {
                // Treat an abandoned mutex as acquired so the new instance can recover.
            }

            return InstanceLockResult.Acquired(new AppInstanceGuard(mutex, mutexName));
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
        }

        _mutex.Dispose();
        _disposed = true;
    }

    public static IReadOnlyList<RunningInstanceInfo> EnumerateRunningInstances()
    {
        var currentProcessName = Process.GetCurrentProcess().ProcessName;
        var currentProcessId = Environment.ProcessId;
        var instances = new List<RunningInstanceInfo>();

        foreach (var process in Process.GetProcessesByName(currentProcessName))
        {
            try
            {
                if (process.Id == currentProcessId)
                {
                    continue;
                }

                var startTime = TryGetStartTime(process);
                var executablePath = TryGetPath(process);
                instances.Add(new RunningInstanceInfo(
                    process.Id,
                    startTime,
                    executablePath));
            }
            catch
            {
                // Skip inaccessible processes with the same name.
            }
            finally
            {
                process.Dispose();
            }
        }

        return instances
            .OrderBy(instance => instance.ProcessId)
            .ToArray();
    }

    private static string CreateMutexName(string overlayKey)
    {
        var normalized = string.IsNullOrWhiteSpace(overlayKey)
            ? "default"
            : overlayKey.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $@"Global\BatteryDotOverlay_{hash}";
    }

    private static DateTime? TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal sealed record InstanceLockResult(
    bool IsAcquired,
    AppInstanceGuard? Guard,
    IReadOnlyList<RunningInstanceInfo> RunningInstances)
{
    public static InstanceLockResult Acquired(AppInstanceGuard guard)
    {
        return new InstanceLockResult(
            true,
            guard,
            Array.Empty<RunningInstanceInfo>());
    }

    public static InstanceLockResult Failed(IReadOnlyList<RunningInstanceInfo> runningInstances)
    {
        return new InstanceLockResult(
            false,
            null,
            runningInstances);
    }
}

internal sealed record RunningInstanceInfo(
    int ProcessId,
    DateTime? StartTime,
    string ExecutablePath);
