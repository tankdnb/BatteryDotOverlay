using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatteryDotOverlay;

public sealed class BatteryDotSettings
{
    public OverlayConfig Overlay { get; set; } = new();
    public VisualConfig Visual { get; set; } = new();
    public BehaviorConfig Behavior { get; set; } = new();
    public BatteryConfig Battery { get; set; } = new();
    public RuntimeConfig Runtime { get; set; } = new();

    public static BatteryDotSettings CreateDefault() => new();

    public static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
    }
}

public sealed class OverlayConfig
{
    public string OverlayKey { get; set; } = "com.vrapp.utility.batterydot";
    public string OverlayName { get; set; } = "Battery Dot Overlay";
    public float WidthInMeters { get; set; } = 0.028f;
    public float DistanceMeters { get; set; } = 0.24f;
    public float HorizontalOffsetMeters { get; set; } = 0.095f;
    public float VerticalOffsetMeters { get; set; } = 0.065f;
    public uint SortOrder { get; set; } = 120;
}

public sealed class VisualConfig
{
    public string ColorHex { get; set; } = "#FFD54A";
    public float Opacity { get; set; } = 0.55f;
    public int TextureSizePixels { get; set; } = 128;
    public float CircleFillRatio { get; set; } = 0.62f;
    public float EdgeFeatherPixels { get; set; } = 2.0f;
}

public sealed class BehaviorConfig
{
    public float BlinkFrequencyHz { get; set; } = 1.0f;
    public float? BlinkBelowPercent { get; set; }
    public bool BlinkOnlyWhenNotCharging { get; set; } = true;
    [JsonPropertyName("battery_visible_below_percent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? BatteryVisibleBelowPercent { get; set; }
    public int BatteryPollIntervalMs { get; set; } = 1000;
    public int StatusLogIntervalSeconds { get; set; } = 15;

    public float GetEffectiveBlinkBelowPercent()
    {
        return Math.Clamp(BlinkBelowPercent ?? BatteryVisibleBelowPercent ?? 100.0f, 0f, 100f);
    }
}

public sealed class RuntimeConfig
{
    public int LoopDelayMs { get; set; } = 50;
    public bool EnableFileLogging { get; set; } = true;
    public string LogsDirectory { get; set; } = "logs";
    public int MaxLogFiles { get; set; } = 10;
    public bool EnforceSingleInstance { get; set; } = true;
}

public sealed class BatteryConfig
{
    public int PreferredDeviceIndex { get; set; } = -1;
    public bool LogDiagnosticsOnStartup { get; set; } = true;
    public bool LogDiagnosticsWhenUnavailable { get; set; } = true;
    public bool EnableAdbFallback { get; set; } = true;
    public string AdbPath { get; set; } = string.Empty;
    public string AdbDeviceSerial { get; set; } = string.Empty;
    public int AdbCommandTimeoutMs { get; set; } = 2000;
    public bool EnablePicoConnectLogFallback { get; set; } = true;
    public string PicoConnectLogsPath { get; set; } = string.Empty;
    public int PicoConnectEventMaxAgeSeconds { get; set; } = 120;
    public int PicoConnectLogTailBytes { get; set; } = 262144;
}

public static class BatteryDotSettingsStore
{
    public static SettingsLoadResult LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<BatteryDotSettings>(json, BatteryDotSettings.CreateJsonOptions());
                if (loaded is not null)
                {
                    return SettingsLoadResult.Success(Normalize(loaded));
                }

                return SettingsLoadResult.Failure("Settings file could not be read because it did not contain a valid JSON object.");
            }
            catch (Exception ex)
            {
                return SettingsLoadResult.Failure($"Failed to load settings from '{path}': {ex.Message}");
            }
        }

        try
        {
            var defaults = Normalize(BatteryDotSettings.CreateDefault());
            Save(path, defaults);
            return SettingsLoadResult.Success(defaults, createdDefaultFile: true, message: "Default settings file was created because no config file was found.");
        }
        catch (Exception ex)
        {
            return SettingsLoadResult.Failure($"Failed to create default settings file at '{path}': {ex.Message}");
        }
    }

    public static void Save(string path, BatteryDotSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(settings, BatteryDotSettings.CreateJsonOptions());
        File.WriteAllText(path, json);
    }

    private static BatteryDotSettings Normalize(BatteryDotSettings settings)
    {
        settings.Behavior ??= new BehaviorConfig();
        settings.Battery ??= new BatteryConfig();
        settings.Runtime ??= new RuntimeConfig();

        if (settings.Behavior.BlinkBelowPercent is null)
        {
            settings.Behavior.BlinkBelowPercent = settings.Behavior.BatteryVisibleBelowPercent ?? 100.0f;
        }

        settings.Behavior.BatteryVisibleBelowPercent = null;
        settings.Battery.AdbCommandTimeoutMs = Math.Clamp(settings.Battery.AdbCommandTimeoutMs, 500, 30000);

        return settings;
    }
}

public sealed record SettingsLoadResult(
    bool IsSuccess,
    BatteryDotSettings? Settings,
    bool CreatedDefaultFile,
    string Message)
{
    public static SettingsLoadResult Success(BatteryDotSettings settings, bool createdDefaultFile = false, string message = "")
    {
        return new SettingsLoadResult(
            true,
            settings,
            createdDefaultFile,
            message);
    }

    public static SettingsLoadResult Failure(string message)
    {
        return new SettingsLoadResult(
            false,
            null,
            false,
            message);
    }
}
