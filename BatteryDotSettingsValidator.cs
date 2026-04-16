using System.Globalization;

namespace BatteryDotOverlay;

internal static class BatteryDotSettingsValidator
{
    public static SettingsValidationResult ValidateAndNormalize(BatteryDotSettings settings)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        settings.Overlay ??= new OverlayConfig();
        settings.Visual ??= new VisualConfig();
        settings.Behavior ??= new BehaviorConfig();
        settings.Battery ??= new BatteryConfig();
        settings.Runtime ??= new RuntimeConfig();

        EnsureString(
            () => settings.Overlay.OverlayKey,
            value => settings.Overlay.OverlayKey = value,
            "com.vrapp.utility.batterydot",
            "overlay.overlay_key",
            warnings);
        EnsureString(
            () => settings.Overlay.OverlayName,
            value => settings.Overlay.OverlayName = value,
            "Battery Dot Overlay",
            "overlay.overlay_name",
            warnings);

        if (!IsValidColorHex(settings.Visual.ColorHex))
        {
            warnings.Add("visual.color_hex has invalid format. Using default #FFD54A.");
            settings.Visual.ColorHex = "#FFD54A";
        }

        Clamp(
            () => settings.Overlay.WidthInMeters,
            value => settings.Overlay.WidthInMeters = value,
            0.005f,
            0.25f,
            "overlay.width_in_meters",
            warnings);
        Clamp(
            () => settings.Overlay.DistanceMeters,
            value => settings.Overlay.DistanceMeters = value,
            0.05f,
            2.0f,
            "overlay.distance_meters",
            warnings);
        Clamp(
            () => settings.Overlay.HorizontalOffsetMeters,
            value => settings.Overlay.HorizontalOffsetMeters = value,
            -0.5f,
            0.5f,
            "overlay.horizontal_offset_meters",
            warnings);
        Clamp(
            () => settings.Overlay.VerticalOffsetMeters,
            value => settings.Overlay.VerticalOffsetMeters = value,
            -0.5f,
            0.5f,
            "overlay.vertical_offset_meters",
            warnings);

        Clamp(
            () => settings.Visual.Opacity,
            value => settings.Visual.Opacity = value,
            0.0f,
            1.0f,
            "visual.opacity",
            warnings);
        Clamp(
            () => settings.Visual.TextureSizePixels,
            value => settings.Visual.TextureSizePixels = value,
            32,
            1024,
            "visual.texture_size_pixels",
            warnings);
        Clamp(
            () => settings.Visual.CircleFillRatio,
            value => settings.Visual.CircleFillRatio = value,
            0.10f,
            0.95f,
            "visual.circle_fill_ratio",
            warnings);
        Clamp(
            () => settings.Visual.EdgeFeatherPixels,
            value => settings.Visual.EdgeFeatherPixels = value,
            0.5f,
            128.0f,
            "visual.edge_feather_pixels",
            warnings);

        Clamp(
            () => settings.Behavior.BlinkFrequencyHz,
            value => settings.Behavior.BlinkFrequencyHz = value,
            0.0f,
            20.0f,
            "behavior.blink_frequency_hz",
            warnings);
        Clamp(
            () => settings.Behavior.BlinkBelowPercent ?? 100.0f,
            value => settings.Behavior.BlinkBelowPercent = value,
            0.0f,
            100.0f,
            "behavior.blink_below_percent",
            warnings);
        Clamp(
            () => settings.Behavior.BatteryPollIntervalMs,
            value => settings.Behavior.BatteryPollIntervalMs = value,
            100,
            60000,
            "behavior.battery_poll_interval_ms",
            warnings);
        Clamp(
            () => settings.Behavior.StatusLogIntervalSeconds,
            value => settings.Behavior.StatusLogIntervalSeconds = value,
            1,
            3600,
            "behavior.status_log_interval_seconds",
            warnings);

        Clamp(
            () => settings.Battery.AdbCommandTimeoutMs,
            value => settings.Battery.AdbCommandTimeoutMs = value,
            500,
            30000,
            "battery.adb_command_timeout_ms",
            warnings);
        Clamp(
            () => settings.Battery.PicoConnectEventMaxAgeSeconds,
            value => settings.Battery.PicoConnectEventMaxAgeSeconds = value,
            30,
            3600,
            "battery.pico_connect_event_max_age_seconds",
            warnings);
        Clamp(
            () => settings.Battery.PicoConnectLogTailBytes,
            value => settings.Battery.PicoConnectLogTailBytes = value,
            4096,
            4 * 1024 * 1024,
            "battery.pico_connect_log_tail_bytes",
            warnings);

        Clamp(
            () => settings.Runtime.LoopDelayMs,
            value => settings.Runtime.LoopDelayMs = value,
            10,
            1000,
            "runtime.loop_delay_ms",
            warnings);
        Clamp(
            () => settings.Runtime.MaxLogFiles,
            value => settings.Runtime.MaxLogFiles = value,
            1,
            100,
            "runtime.max_log_files",
            warnings);

        EnsureString(
            () => settings.Runtime.LogsDirectory,
            value => settings.Runtime.LogsDirectory = value,
            "logs",
            "runtime.logs_directory",
            warnings);

        if (!string.IsNullOrWhiteSpace(settings.Battery.AdbPath))
        {
            var expandedAdbPath = Environment.ExpandEnvironmentVariables(settings.Battery.AdbPath.Trim());
            if (!File.Exists(expandedAdbPath) && !Directory.Exists(expandedAdbPath))
            {
                warnings.Add("battery.adb_path does not exist. Falling back to auto-discovery.");
                settings.Battery.AdbPath = string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.Battery.PicoConnectLogsPath))
        {
            var expandedLogsPath = Environment.ExpandEnvironmentVariables(settings.Battery.PicoConnectLogsPath.Trim());
            if (!Directory.Exists(expandedLogsPath))
            {
                warnings.Add("battery.pico_connect_logs_path does not exist. PICO Connect fallback may stay unavailable until the path appears.");
            }
        }

        return new SettingsValidationResult(warnings, errors);
    }

    private static void EnsureString(
        Func<string> getter,
        Action<string> setter,
        string fallbackValue,
        string settingName,
        ICollection<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(getter()))
        {
            return;
        }

        setter(fallbackValue);
        warnings.Add($"{settingName} was empty. Using '{fallbackValue}'.");
    }

    private static void Clamp(
        Func<int> getter,
        Action<int> setter,
        int min,
        int max,
        string settingName,
        ICollection<string> warnings)
    {
        var current = getter();
        var clamped = Math.Clamp(current, min, max);
        if (current == clamped)
        {
            return;
        }

        setter(clamped);
        warnings.Add($"{settingName} was outside the allowed range {min}..{max}. Using {clamped}.");
    }

    private static void Clamp(
        Func<float> getter,
        Action<float> setter,
        float min,
        float max,
        string settingName,
        ICollection<string> warnings)
    {
        var current = getter();
        var clamped = Math.Clamp(current, min, max);
        if (Math.Abs(current - clamped) < 0.0001f)
        {
            return;
        }

        setter(clamped);
        warnings.Add($"{settingName} was outside the allowed range {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}. Using {clamped.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static bool IsValidColorHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length != 6)
        {
            return false;
        }

        return byte.TryParse(normalized[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) &&
               byte.TryParse(normalized[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) &&
               byte.TryParse(normalized[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    }
}

internal sealed record SettingsValidationResult(
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
