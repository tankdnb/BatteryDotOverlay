namespace BatteryDotOverlay;

internal static class AppPaths
{
    public static string ResolveLogsDirectory(RuntimeConfig runtime)
    {
        var configured = string.IsNullOrWhiteSpace(runtime.LogsDirectory)
            ? "logs"
            : runtime.LogsDirectory.Trim();

        var expanded = Environment.ExpandEnvironmentVariables(configured);
        var candidate = Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));

        var writableCandidate = TryEnsureWritableDirectory(candidate);
        if (writableCandidate is not null)
        {
            return writableCandidate;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fallback = Path.Combine(localAppData, "BatteryDotOverlay", "logs");
        return TryEnsureWritableDirectory(fallback)
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs"));
    }

    private static string? TryEnsureWritableDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, $".write-probe-{Environment.ProcessId}.tmp");
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
