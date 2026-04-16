using System.Text;

namespace BatteryDotOverlay;

internal sealed class AppLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter? _writer;
    private readonly UnhandledExceptionEventHandler _unhandledExceptionHandler;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _unobservedTaskExceptionHandler;

    private AppLogger(StreamWriter? writer, string? logFilePath, string? initializationWarning)
    {
        _writer = writer;
        LogFilePath = logFilePath ?? string.Empty;
        InitializationWarning = initializationWarning ?? string.Empty;

        _unhandledExceptionHandler = (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            Error(exception is not null
                ? $"[fatal] Unhandled exception: {exception}"
                : $"[fatal] Unhandled exception object: {args.ExceptionObject}");
        };

        _unobservedTaskExceptionHandler = (_, args) =>
        {
            Error($"[error] Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
        TaskScheduler.UnobservedTaskException += _unobservedTaskExceptionHandler;
    }

    public string LogFilePath { get; }

    public string InitializationWarning { get; }

    public static AppLogger Initialize(BatteryDotSettings settings, string version)
    {
        if (!settings.Runtime.EnableFileLogging)
        {
            return new AppLogger(null, null, null);
        }

        try
        {
            var logsDirectory = AppPaths.ResolveLogsDirectory(settings.Runtime);
            RotateLogs(logsDirectory, settings.Runtime.MaxLogFiles);

            var fileName = $"BatteryDotOverlay-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.log";
            var logFilePath = Path.Combine(logsDirectory, fileName);
            var stream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };

            var logger = new AppLogger(writer, logFilePath, null);
            logger.WriteToFile($"[logging] Battery Dot Overlay {version}");
            logger.WriteToFile($"[logging] Process {Environment.ProcessId}");
            return logger;
        }
        catch (Exception ex)
        {
            return new AppLogger(null, null, $"File logging is unavailable: {ex.Message}");
        }
    }

    public void Info(string message) => Write(message, isError: false);

    public void Warn(string message) => Write(message, isError: false);

    public void Error(string message) => Write(message, isError: true);

    public void LogException(string prefix, Exception exception)
    {
        Error($"{prefix}: {exception}");
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;
        TaskScheduler.UnobservedTaskException -= _unobservedTaskExceptionHandler;

        lock (_sync)
        {
            _writer?.Dispose();
        }
    }

    private void Write(string message, bool isError)
    {
        if (isError)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            Console.WriteLine(message);
        }

        WriteToFile(message);
    }

    private void WriteToFile(string message)
    {
        if (_writer is null)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (_sync)
        {
            _writer.WriteLine(line);
        }
    }

    private static void RotateLogs(string logsDirectory, int maxLogFiles)
    {
        var keepCount = Math.Clamp(maxLogFiles, 1, 100);
        var filesToDelete = Directory
            .EnumerateFiles(logsDirectory, "BatteryDotOverlay-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .Skip(keepCount - 1)
            .ToArray();

        foreach (var filePath in filesToDelete)
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // Ignore log cleanup failures.
            }
        }
    }
}
