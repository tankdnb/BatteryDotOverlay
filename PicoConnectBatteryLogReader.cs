using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BatteryDotOverlay;

internal static class PicoConnectBatteryLogReader
{
    private static readonly string[] TimestampFormats =
    [
        "MMM-dd-yyyy HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.fff",
    ];

    public static PicoConnectBatterySample? TryReadLatest(BatteryConfig settings)
    {
        var logsPath = ResolveLogsPath(settings);
        if (!Directory.Exists(logsPath))
        {
            return null;
        }

        var maxTailBytes = Math.Max(4096, settings.PicoConnectLogTailBytes);
        PicoConnectBatterySample? latest = null;

        foreach (var filePath in Directory
                     .EnumerateFiles(logsPath, "pico_connect*.log", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                     .Take(5))
        {
            var candidate = TryReadLatestFromFile(filePath, maxTailBytes);
            if (candidate is null)
            {
                continue;
            }

            if (latest is null || candidate.TimestampUtc > latest.TimestampUtc)
            {
                latest = candidate;
            }
        }

        if (latest is null)
        {
            return null;
        }

        var maxAge = TimeSpan.FromSeconds(Math.Max(30, settings.PicoConnectEventMaxAgeSeconds));
        return DateTime.UtcNow - latest.TimestampUtc <= maxAge ? latest : null;
    }

    public static PicoConnectPowerCableSample? TryReadLatestPowerCableState(BatteryConfig settings)
    {
        var logsPath = ResolveLogsPath(settings);
        if (!Directory.Exists(logsPath))
        {
            return null;
        }

        var maxTailBytes = Math.Max(4096, settings.PicoConnectLogTailBytes);
        PicoConnectPowerCableSample? latest = null;

        foreach (var filePath in Directory
                     .EnumerateFiles(logsPath, "pico_connect_sdk*.log", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                     .Take(5))
        {
            var candidate = TryReadLatestPowerCableStateFromFile(filePath, maxTailBytes);
            if (candidate is null)
            {
                continue;
            }

            if (latest is null || candidate.TimestampUtc > latest.TimestampUtc)
            {
                latest = candidate;
            }
        }

        if (latest is null)
        {
            return null;
        }

        var maxAge = TimeSpan.FromSeconds(Math.Max(30, settings.PicoConnectEventMaxAgeSeconds));
        return DateTime.UtcNow - latest.TimestampUtc <= maxAge ? latest : null;
    }

    public static IReadOnlyList<PicoConnectBatterySample> TryReadRecentBatterySamples(BatteryConfig settings, int maxSamples = 8)
    {
        var logsPath = ResolveLogsPath(settings);
        if (!Directory.Exists(logsPath))
        {
            return Array.Empty<PicoConnectBatterySample>();
        }

        var maxTailBytes = Math.Max(4096, settings.PicoConnectLogTailBytes);
        var candidates = new List<PicoConnectBatterySample>();

        foreach (var filePath in Directory
                     .EnumerateFiles(logsPath, "pico_connect*.log", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                     .Take(5))
        {
            candidates.AddRange(ReadBatterySamplesFromFile(filePath, maxTailBytes, maxSamples));
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<PicoConnectBatterySample>();
        }

        var maxAge = TimeSpan.FromSeconds(Math.Max(30, settings.PicoConnectEventMaxAgeSeconds));
        return candidates
            .Where(sample => DateTime.UtcNow - sample.TimestampUtc <= maxAge)
            .OrderByDescending(sample => sample.TimestampUtc)
            .GroupBy(sample => sample.TimestampUtc)
            .Select(group => group.First())
            .Take(Math.Max(2, maxSamples))
            .ToArray();
    }

    public static string ResolveLogsPath(BatteryConfig settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PicoConnectLogsPath))
        {
            return Environment.ExpandEnvironmentVariables(settings.PicoConnectLogsPath.Trim());
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PICO Connect", "logs");
    }

    private static PicoConnectBatterySample? TryReadLatestFromFile(string filePath, int maxTailBytes)
    {
        string text;
        try
        {
            text = ReadTailText(filePath, maxTailBytes);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var candidate = TryParseLine(lines[index], filePath);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<PicoConnectBatterySample> ReadBatterySamplesFromFile(string filePath, int maxTailBytes, int maxSamples)
    {
        string text;
        try
        {
            text = ReadTailText(filePath, maxTailBytes);
        }
        catch
        {
            return Array.Empty<PicoConnectBatterySample>();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<PicoConnectBatterySample>();
        }

        var samples = new List<PicoConnectBatterySample>();
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var candidate = TryParseLine(lines[index], filePath);
            if (candidate is null)
            {
                continue;
            }

            samples.Add(candidate);
            if (samples.Count >= Math.Max(2, maxSamples))
            {
                break;
            }
        }

        return samples;
    }

    private static PicoConnectPowerCableSample? TryReadLatestPowerCableStateFromFile(string filePath, int maxTailBytes)
    {
        string text;
        try
        {
            text = ReadTailText(filePath, maxTailBytes);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var candidate = TryParsePowerCableLine(lines[index], filePath);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static PicoConnectBatterySample? TryParseLine(string line, string filePath)
    {
        if (!line.Contains("device_battery_volume", StringComparison.OrdinalIgnoreCase) ||
            !line.Contains("kDeviceTypeHead", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var jsonStart = line.IndexOf('{');
        var jsonEnd = line.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            return null;
        }

        var json = line.Substring(jsonStart, jsonEnd - jsonStart + 1);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("device_battery_volume", out var devices) ||
                devices.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var device in devices.EnumerateArray())
            {
                if (!device.TryGetProperty("device_type", out var deviceTypeElement) ||
                    !string.Equals(deviceTypeElement.GetString(), "kDeviceTypeHead", StringComparison.Ordinal))
                {
                    continue;
                }

                if (device.TryGetProperty("active", out var activeElement) &&
                    activeElement.ValueKind is JsonValueKind.False)
                {
                    return null;
                }

                if (!device.TryGetProperty("battery_percent", out var batteryPercentElement) ||
                    !batteryPercentElement.TryGetInt32(out var batteryPercent))
                {
                    return null;
                }

                var timestampUtc = TryParseTimestampUtc(line) ?? File.GetLastWriteTimeUtc(filePath);
                return new PicoConnectBatterySample(
                    Math.Clamp(batteryPercent, 0, 100),
                    timestampUtc,
                    filePath);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static PicoConnectPowerCableSample? TryParsePowerCableLine(string line, string filePath)
    {
        if (!line.Contains("power_cable_plug_flag", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var jsonStart = line.IndexOf('{');
        var jsonEnd = line.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            return null;
        }

        var json = line.Substring(jsonStart, jsonEnd - jsonStart + 1);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("power_cable_plug_flag", out var powerCableElement) ||
                powerCableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var timestampUtc = TryParseTimestampUtc(line) ?? File.GetLastWriteTimeUtc(filePath);
            return new PicoConnectPowerCableSample(
                powerCableElement.GetBoolean(),
                timestampUtc,
                filePath);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime? TryParseTimestampUtc(string line)
    {
        if (!line.StartsWith("[", StringComparison.Ordinal))
        {
            return null;
        }

        var closingBracketIndex = line.IndexOf(']');
        if (closingBracketIndex <= 1)
        {
            return null;
        }

        var timestampText = line.Substring(1, closingBracketIndex - 1);
        return DateTime.TryParseExact(
            timestampText,
            TimestampFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string ReadTailText(string filePath, int maxTailBytes)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytesToRead = (int)Math.Min(stream.Length, maxTailBytes);
        if (bytesToRead <= 0)
        {
            return string.Empty;
        }

        var startedInMiddle = stream.Length > bytesToRead;
        stream.Seek(-bytesToRead, SeekOrigin.End);

        var buffer = new byte[bytesToRead];
        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = stream.Read(buffer, totalRead, bytesToRead - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var start = 0;
        if (startedInMiddle)
        {
            while (start < totalRead && buffer[start] != (byte)'\n')
            {
                start++;
            }

            if (start < totalRead)
            {
                start++;
            }
        }

        return Encoding.UTF8.GetString(buffer, start, totalRead - start);
    }
}

internal sealed record PicoConnectBatterySample(
    int Percent,
    DateTime TimestampUtc,
    string FilePath);

internal sealed record PicoConnectPowerCableSample(
    bool IsExternalPowerConnected,
    DateTime TimestampUtc,
    string FilePath);
