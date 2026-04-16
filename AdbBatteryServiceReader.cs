using System.Diagnostics;

namespace BatteryDotOverlay;

internal static class AdbBatteryServiceReader
{
    public static AdbBatteryProbeResult Read(BatteryConfig settings)
    {
        if (!settings.EnableAdbFallback)
        {
            return AdbBatteryProbeResult.Unavailable("ADB fallback is disabled in settings.");
        }

        var adbExecutablePath = ResolveAdbExecutablePath(settings);
        if (adbExecutablePath is null)
        {
            return AdbBatteryProbeResult.Unavailable("ADB executable was not found. Configure battery.adb_path or install Android platform-tools.");
        }

        var timeoutMs = Math.Clamp(settings.AdbCommandTimeoutMs, 500, 30000);
        var startInfo = new ProcessStartInfo
        {
            FileName = adbExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(settings.AdbDeviceSerial))
        {
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(settings.AdbDeviceSerial.Trim());
        }

        startInfo.ArgumentList.Add("shell");
        startInfo.ArgumentList.Add("dumpsys");
        startInfo.ArgumentList.Add("battery");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                return AdbBatteryProbeResult.Unavailable($"ADB battery query timed out after {timeoutMs} ms.");
            }

            Task.WaitAll(stdoutTask, stderrTask);

            var stdout = stdoutTask.Result?.Trim() ?? string.Empty;
            var stderr = stderrTask.Result?.Trim() ?? string.Empty;

            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(stderr)
                    ? $"ADB exited with code {process.ExitCode}."
                    : stderr;

                return AdbBatteryProbeResult.Unavailable(message);
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                return AdbBatteryProbeResult.Unavailable("ADB returned an empty battery response.");
            }

            return TryParse(stdout, adbExecutablePath, settings.AdbDeviceSerial);
        }
        catch (Exception ex)
        {
            return AdbBatteryProbeResult.Unavailable(ex.Message);
        }
    }

    private static AdbBatteryProbeResult TryParse(string stdout, string adbExecutablePath, string configuredSerial)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            values[key] = value;
        }

        if (!TryGetInt(values, "level", out var level) || !TryGetInt(values, "scale", out var scale) || scale <= 0)
        {
            return AdbBatteryProbeResult.Unavailable("ADB battery response did not include level/scale.");
        }

        var percent = Math.Clamp(level * 100f / scale, 0f, 100f);
        var acPowered = TryGetBool(values, "AC powered");
        var usbPowered = TryGetBool(values, "USB powered");
        var wirelessPowered = TryGetBool(values, "Wireless powered");
        var status = TryGetInt(values, "status", out var parsedStatus) ? (int?)parsedStatus : null;
        var chargingState = DetermineChargingState(acPowered, usbPowered, wirelessPowered, status);

        var sample = new AdbBatterySample(
            percent,
            chargingState,
            acPowered,
            usbPowered,
            wirelessPowered,
            status,
            adbExecutablePath,
            string.IsNullOrWhiteSpace(configuredSerial) ? string.Empty : configuredSerial.Trim());

        return AdbBatteryProbeResult.Available(sample);
    }

    public static string? ResolveAdbExecutablePath(BatteryConfig settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AdbPath))
        {
            var configuredPath = Environment.ExpandEnvironmentVariables(settings.AdbPath.Trim());
            if (Directory.Exists(configuredPath))
            {
                configuredPath = Path.Combine(configuredPath, "adb.exe");
            }

            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        foreach (var candidate in EnumerateDefaultAdbCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindExecutableInPath("adb.exe");
    }

    private static IEnumerable<string> EnumerateDefaultAdbCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        }

        var sdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            yield return Path.Combine(sdkRoot, "platform-tools", "adb.exe");
        }

        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrWhiteSpace(androidHome))
        {
            yield return Path.Combine(androidHome, "platform-tools", "adb.exe");
        }
    }

    private static string? FindExecutableInPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid PATH entries.
            }
        }

        return null;
    }

    private static BatteryChargingState DetermineChargingState(
        bool? acPowered,
        bool? usbPowered,
        bool? wirelessPowered,
        int? status)
    {
        if (acPowered == true || usbPowered == true || wirelessPowered == true)
        {
            return BatteryChargingState.Charging;
        }

        if (acPowered == false && usbPowered == false && wirelessPowered == false)
        {
            return BatteryChargingState.NotCharging;
        }

        return status switch
        {
            2 or 5 => BatteryChargingState.Charging,
            3 or 4 => BatteryChargingState.NotCharging,
            _ => BatteryChargingState.Unknown,
        };
    }

    private static bool? TryGetBool(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var rawValue) && bool.TryParse(rawValue, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        value = default;
        return values.TryGetValue(key, out var rawValue) && int.TryParse(rawValue, out value);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch
        {
            // Ignore kill failures during timeout cleanup.
        }
    }
}

internal sealed record AdbBatteryProbeResult(
    bool IsAvailable,
    string Message,
    AdbBatterySample? Sample)
{
    public static AdbBatteryProbeResult Available(AdbBatterySample sample)
    {
        return new AdbBatteryProbeResult(
            true,
            sample.GetDescription(),
            sample);
    }

    public static AdbBatteryProbeResult Unavailable(string message)
    {
        return new AdbBatteryProbeResult(
            false,
            message,
            null);
    }
}

internal sealed record AdbBatterySample(
    float Percent,
    BatteryChargingState ChargingState,
    bool? AcPowered,
    bool? UsbPowered,
    bool? WirelessPowered,
    int? Status,
    string AdbExecutablePath,
    string DeviceSerial)
{
    public string GetDescription()
    {
        var serialPart = string.IsNullOrWhiteSpace(DeviceSerial) ? string.Empty : $" serial={DeviceSerial}";
        return $"ADB battery service{serialPart}";
    }

    public string GetPowerDescription()
    {
        return $"AC={FormatBool(AcPowered)}, USB={FormatBool(UsbPowered)}, Wireless={FormatBool(WirelessPowered)}, status={Status?.ToString() ?? "?"}";
    }

    private static string FormatBool(bool? value)
    {
        return value switch
        {
            true => "true",
            false => "false",
            _ => "?",
        };
    }
}
