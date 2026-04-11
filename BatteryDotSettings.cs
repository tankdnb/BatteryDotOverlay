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
}

public sealed class BatteryConfig
{
    public int PreferredDeviceIndex { get; set; } = -1;
    public bool LogDiagnosticsOnStartup { get; set; } = true;
    public bool LogDiagnosticsWhenUnavailable { get; set; } = true;
    public bool EnablePicoConnectLogFallback { get; set; } = true;
    public string PicoConnectLogsPath { get; set; } = string.Empty;
    public int PicoConnectEventMaxAgeSeconds { get; set; } = 120;
    public int PicoConnectLogTailBytes { get; set; } = 262144;
}

public static class BatteryDotSettingsStore
{
    public static BatteryDotSettings LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<BatteryDotSettings>(json, BatteryDotSettings.CreateJsonOptions());
            if (loaded is not null)
            {
                return Normalize(loaded);
            }
        }

        var defaults = Normalize(BatteryDotSettings.CreateDefault());
        Save(path, defaults);
        return defaults;
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

        if (settings.Behavior.BlinkBelowPercent is null)
        {
            settings.Behavior.BlinkBelowPercent = settings.Behavior.BatteryVisibleBelowPercent ?? 100.0f;
        }

        settings.Behavior.BatteryVisibleBelowPercent = null;

        return settings;
    }
}
