using System.Text;
using Valve.VR;
using VRRealityWindow.OpenVr;

namespace BatteryDotOverlay;

internal static class BatteryTelemetry
{
    public static BatteryStateSnapshot Read(OpenVrRuntime runtime, BatteryDotSettings settings)
    {
        IReadOnlyList<TrackedDeviceBatterySample> samples = Array.Empty<TrackedDeviceBatterySample>();
        TrackedDeviceBatterySample? openVrBatteryDevice = null;
        TrackedDeviceBatterySample? openVrChargingDevice = null;

        try
        {
            runtime.EnsureInitialized();
            samples = ScanConnectedDevices(runtime);
            openVrBatteryDevice = SelectOpenVrBatteryDevice(samples, settings);
            openVrChargingDevice = SelectOpenVrChargingDevice(samples, settings, openVrBatteryDevice?.DeviceIndex);
        }
        catch
        {
            // OpenVR remains the primary source when available, but charge/battery fallbacks
            // should still work for diagnostics and unsupported runtimes.
        }

        var adbProbe = AdbBatteryServiceReader.Read(settings.Battery);

        var batterySource = openVrBatteryDevice is not null
            ? CreateOpenVrBatterySource(openVrBatteryDevice)
            : TryCreateAdbBatterySource(adbProbe) ?? TryReadPicoConnectBatteryFallback(settings);

        var chargingSource = TryCreateAdbChargingSource(adbProbe) ?? (openVrChargingDevice is not null
            ? CreateOpenVrChargingSource(openVrChargingDevice)
            : TryReadPicoConnectChargingFallback(settings));

        return batterySource is not null
            ? CreateSnapshot(batterySource, chargingSource, settings, samples)
            : BatteryStateSnapshot.Unavailable(samples, chargingSource);
    }

    public static BatteryStateSnapshot Read(BatteryDotSettings settings)
    {
        var adbProbe = AdbBatteryServiceReader.Read(settings.Battery);
        var batterySource = TryCreateAdbBatterySource(adbProbe) ?? TryReadPicoConnectBatteryFallback(settings);
        var chargingSource = TryCreateAdbChargingSource(adbProbe) ?? TryReadPicoConnectChargingFallback(settings);

        return batterySource is not null
            ? CreateSnapshot(batterySource, chargingSource, settings, Array.Empty<TrackedDeviceBatterySample>())
            : BatteryStateSnapshot.Unavailable(Array.Empty<TrackedDeviceBatterySample>(), chargingSource);
    }

    public static IReadOnlyList<TrackedDeviceBatterySample> ScanConnectedDevices(OpenVrRuntime runtime)
    {
        runtime.EnsureInitialized();
        var samples = new List<TrackedDeviceBatterySample>();

        for (uint deviceIndex = 0; deviceIndex < OpenVR.k_unMaxTrackedDeviceCount; deviceIndex++)
        {
            var isConnected = runtime.System.IsTrackedDeviceConnected(deviceIndex);
            var deviceClass = runtime.System.GetTrackedDeviceClass(deviceIndex);
            var trackingSystemName = GetStringProperty(runtime, deviceIndex, ETrackedDeviceProperty.Prop_TrackingSystemName_String);
            var modelNumber = GetStringProperty(runtime, deviceIndex, ETrackedDeviceProperty.Prop_ModelNumber_String);
            var renderModelName = GetStringProperty(runtime, deviceIndex, ETrackedDeviceProperty.Prop_RenderModelName_String);
            var serialNumber = GetStringProperty(runtime, deviceIndex, ETrackedDeviceProperty.Prop_SerialNumber_String);
            var batteryError = ETrackedPropertyError.TrackedProp_Success;
            var rawBattery = runtime.System.GetFloatTrackedDeviceProperty(
                deviceIndex,
                ETrackedDeviceProperty.Prop_DeviceBatteryPercentage_Float,
                ref batteryError);

            var chargingError = ETrackedPropertyError.TrackedProp_Success;
            var isCharging = runtime.System.GetBoolTrackedDeviceProperty(
                deviceIndex,
                ETrackedDeviceProperty.Prop_DeviceIsCharging_Bool,
                ref chargingError);

            var shouldInclude =
                deviceIndex == OpenVR.k_unTrackedDeviceIndex_Hmd ||
                isConnected ||
                deviceClass != ETrackedDeviceClass.Invalid ||
                batteryError == ETrackedPropertyError.TrackedProp_Success ||
                chargingError == ETrackedPropertyError.TrackedProp_Success ||
                !string.IsNullOrWhiteSpace(trackingSystemName) ||
                !string.IsNullOrWhiteSpace(modelNumber) ||
                !string.IsNullOrWhiteSpace(renderModelName) ||
                !string.IsNullOrWhiteSpace(serialNumber);

            if (!shouldInclude)
            {
                continue;
            }

            samples.Add(new TrackedDeviceBatterySample(
                deviceIndex,
                isConnected,
                deviceClass,
                trackingSystemName,
                modelNumber,
                renderModelName,
                serialNumber,
                batteryError == ETrackedPropertyError.TrackedProp_Success,
                Math.Clamp(rawBattery * 100f, 0f, 100f),
                batteryError,
                chargingError == ETrackedPropertyError.TrackedProp_Success,
                isCharging,
                chargingError));
        }

        return samples;
    }

    public static string FormatDiagnostics(IReadOnlyList<TrackedDeviceBatterySample> samples)
    {
        if (samples.Count == 0)
        {
            return "[battery-diag] No connected tracked devices were reported by OpenVR.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("[battery-diag] Connected tracked devices:");

        foreach (var sample in samples.OrderBy(sample => sample.DeviceIndex))
        {
            var batteryText = sample.HasBatteryValue
                ? $"{sample.BatteryPercent:0.0}%"
                : $"unavailable ({sample.BatteryError})";
            var chargingText = sample.HasChargingValue
                ? (sample.IsCharging ? "charging" : "not-charging")
                : $"unknown ({sample.ChargingError})";

            builder.AppendLine(
                $"[battery-diag] idx={sample.DeviceIndex} connected={sample.IsConnected} class={sample.DeviceClass} tracking={Fallback(sample.TrackingSystemName)} model={Fallback(sample.ModelNumber)} render={Fallback(sample.RenderModelName)} serial={Fallback(sample.SerialNumber)} battery={batteryText} charging={chargingText}");
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatFallbackDiagnostics(BatteryDotSettings settings)
    {
        var lines = new List<string>();
        var adbProbe = AdbBatteryServiceReader.Read(settings.Battery);

        lines.Add(adbProbe.IsAvailable && adbProbe.Sample is not null
            ? $"[battery-diag] ADB battery: {adbProbe.Sample.Percent:0.0}% from {adbProbe.Sample.GetDescription()}"
            : $"[battery-diag] ADB fallback: {adbProbe.Message}");

        lines.Add(adbProbe.IsAvailable && adbProbe.Sample is not null
            ? $"[battery-diag] ADB power: {DescribeChargingState(adbProbe.Sample.ChargingState)} from {adbProbe.Sample.GetDescription()} ({adbProbe.Sample.GetPowerDescription()})"
            : "[battery-diag] ADB power: unavailable");

        if (!settings.Battery.EnablePicoConnectLogFallback)
        {
            lines.Add("[battery-diag] PICO Connect fallback is disabled in settings.");
            return string.Join(Environment.NewLine, lines);
        }

        var logsPath = PicoConnectBatteryLogReader.ResolveLogsPath(settings.Battery);

        var batteryFallback = TryReadPicoConnectBatteryFallback(settings);
        lines.Add(batteryFallback is not null
            ? $"[battery-diag] PICO Connect headset battery: {batteryFallback.Percent:0.0}% from {batteryFallback.Description}"
            : $"[battery-diag] PICO Connect headset battery: no fresh headset battery event found in {logsPath}");

        var chargingFallback = TryReadPicoConnectChargingFallback(settings);
        lines.Add(chargingFallback is not null
            ? $"[battery-diag] PICO Connect headset power: {DescribeChargingState(chargingFallback.State)} from {chargingFallback.Description}"
            : $"[battery-diag] PICO Connect headset power: no fresh power-cable event found in {logsPath}");

        var batteryTrend = AssessPicoConnectBatteryTrend(settings);
        if (batteryTrend != PicoBatteryTrend.Unknown)
        {
            lines.Add($"[battery-diag] PICO Connect battery trend: {DescribeBatteryTrend(batteryTrend)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static TrackedDeviceBatterySample? SelectOpenVrBatteryDevice(
        IReadOnlyList<TrackedDeviceBatterySample> samples,
        BatteryDotSettings settings)
    {
        if (settings.Battery.PreferredDeviceIndex >= 0)
        {
            var preferred = samples.FirstOrDefault(sample =>
                sample.DeviceIndex == settings.Battery.PreferredDeviceIndex &&
                sample.HasBatteryValue);

            if (preferred is not null)
            {
                return preferred;
            }
        }

        var hmdBattery = samples.FirstOrDefault(sample =>
            sample.DeviceClass == ETrackedDeviceClass.HMD &&
            sample.HasBatteryValue);
        if (hmdBattery is not null)
        {
            return hmdBattery;
        }

        return null;
    }

    private static TrackedDeviceBatterySample? SelectOpenVrChargingDevice(
        IReadOnlyList<TrackedDeviceBatterySample> samples,
        BatteryDotSettings settings,
        uint? preferredBatteryDeviceIndex)
    {
        if (settings.Battery.PreferredDeviceIndex >= 0)
        {
            var preferred = samples.FirstOrDefault(sample =>
                sample.DeviceIndex == settings.Battery.PreferredDeviceIndex &&
                sample.HasChargingValue);

            if (preferred is not null)
            {
                return preferred;
            }
        }

        if (preferredBatteryDeviceIndex is not null)
        {
            var sameDevice = samples.FirstOrDefault(sample =>
                sample.DeviceIndex == preferredBatteryDeviceIndex.Value &&
                sample.HasChargingValue);

            if (sameDevice is not null)
            {
                return sameDevice;
            }
        }

        var hmdCharging = samples.FirstOrDefault(sample =>
            sample.DeviceClass == ETrackedDeviceClass.HMD &&
            sample.HasChargingValue);
        if (hmdCharging is not null)
        {
            return hmdCharging;
        }

        return null;
    }

    private static BatteryReadingSource? TryReadPicoConnectBatteryFallback(BatteryDotSettings settings)
    {
        if (!settings.Battery.EnablePicoConnectLogFallback)
        {
            return null;
        }

        var sample = PicoConnectBatteryLogReader.TryReadLatest(settings.Battery);
        return sample is null
            ? null
            : new BatteryReadingSource(
                "pico-connect-log",
                $"PICO Connect log ({Path.GetFileName(sample.FilePath)})",
                sample.Percent);
    }

    private static ChargingStateSource? TryReadPicoConnectChargingFallback(BatteryDotSettings settings)
    {
        if (!settings.Battery.EnablePicoConnectLogFallback)
        {
            return null;
        }

        var sample = PicoConnectBatteryLogReader.TryReadLatestPowerCableState(settings.Battery);
        if (sample is null)
        {
            return null;
        }

        var description = $"PICO Connect SDK log ({Path.GetFileName(sample.FilePath)})";
        if (!sample.IsExternalPowerConnected)
        {
            return new ChargingStateSource(
                "pico-connect-sdk-log",
                description,
                BatteryChargingState.NotCharging);
        }

        var batteryTrend = AssessPicoConnectBatteryTrend(settings);
        return batteryTrend switch
        {
            PicoBatteryTrend.Decreasing => new ChargingStateSource(
                "pico-connect-sdk-log",
                $"{description}; battery trend is decreasing",
                BatteryChargingState.NotCharging),
            PicoBatteryTrend.Increasing => new ChargingStateSource(
                "pico-connect-sdk-log",
                $"{description}; battery trend is increasing",
                BatteryChargingState.Charging),
            _ => new ChargingStateSource(
                "pico-connect-sdk-log",
                $"{description}; charging flag is not confirmed by battery trend",
                BatteryChargingState.Unknown),
        };
    }

    private static BatteryReadingSource? TryCreateAdbBatterySource(AdbBatteryProbeResult adbProbe)
    {
        return adbProbe.IsAvailable && adbProbe.Sample is not null
            ? new BatteryReadingSource(
                "adb-battery-service",
                adbProbe.Sample.GetDescription(),
                adbProbe.Sample.Percent)
            : null;
    }

    private static ChargingStateSource? TryCreateAdbChargingSource(AdbBatteryProbeResult adbProbe)
    {
        return adbProbe.IsAvailable && adbProbe.Sample is not null
            ? new ChargingStateSource(
                "adb-battery-service",
                adbProbe.Sample.GetDescription(),
                adbProbe.Sample.ChargingState)
            : null;
    }

    private static BatteryReadingSource CreateOpenVrBatterySource(TrackedDeviceBatterySample sample)
    {
        return new BatteryReadingSource(
            "openvr",
            $"OpenVR device #{sample.DeviceIndex} ({sample.DeviceClass})",
            sample.BatteryPercent);
    }

    private static ChargingStateSource CreateOpenVrChargingSource(TrackedDeviceBatterySample sample)
    {
        return new ChargingStateSource(
            "openvr",
            $"OpenVR device #{sample.DeviceIndex} ({sample.DeviceClass})",
            sample.IsCharging ? BatteryChargingState.Charging : BatteryChargingState.NotCharging);
    }

    private static BatteryStateSnapshot CreateSnapshot(
        BatteryReadingSource batterySource,
        ChargingStateSource? chargingSource,
        BatteryDotSettings settings,
        IReadOnlyList<TrackedDeviceBatterySample> samples)
    {
        var percent = Math.Clamp(batterySource.Percent, 0f, 100f);
        var effectiveBatterySource = batterySource with { Percent = percent };
        var isBelowThreshold = percent < settings.Behavior.GetEffectiveBlinkBelowPercent();
        var shouldWarn = isBelowThreshold && ShouldWarnForChargingState(chargingSource?.State ?? BatteryChargingState.Unknown, settings);
        return BatteryStateSnapshot.Available(
            effectiveBatterySource,
            chargingSource,
            shouldWarn,
            samples);
    }

    private static string GetStringProperty(OpenVrRuntime runtime, uint deviceIndex, ETrackedDeviceProperty property)
    {
        var value = runtime.GetTrackedDeviceString(deviceIndex, property);
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static bool ShouldWarnForChargingState(BatteryChargingState chargingState, BatteryDotSettings settings)
    {
        if (!settings.Behavior.BlinkOnlyWhenNotCharging)
        {
            return true;
        }

        // Preserve the previous warning behavior if charging state is unavailable.
        return chargingState != BatteryChargingState.Charging;
    }

    private static string DescribeChargingState(BatteryChargingState chargingState)
    {
        return chargingState switch
        {
            BatteryChargingState.Charging => "external power connected",
            BatteryChargingState.NotCharging => "external power not connected",
            _ => "unknown",
        };
    }

    private static PicoBatteryTrend AssessPicoConnectBatteryTrend(BatteryDotSettings settings)
    {
        var samples = PicoConnectBatteryLogReader.TryReadRecentBatterySamples(settings.Battery);
        if (samples.Count < 2)
        {
            return PicoBatteryTrend.Unknown;
        }

        var ordered = samples
            .OrderBy(sample => sample.TimestampUtc)
            .ToArray();

        var oldest = ordered.First();
        var newest = ordered.Last();
        if (newest.Percent > oldest.Percent)
        {
            return PicoBatteryTrend.Increasing;
        }

        if (newest.Percent < oldest.Percent)
        {
            return PicoBatteryTrend.Decreasing;
        }

        return PicoBatteryTrend.Flat;
    }

    private static string DescribeBatteryTrend(PicoBatteryTrend trend)
    {
        return trend switch
        {
            PicoBatteryTrend.Increasing => "increasing",
            PicoBatteryTrend.Decreasing => "decreasing",
            PicoBatteryTrend.Flat => "flat",
            _ => "unknown",
        };
    }

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}

internal enum PicoBatteryTrend
{
    Unknown,
    Flat,
    Increasing,
    Decreasing,
}

internal enum BatteryChargingState
{
    Unknown,
    Charging,
    NotCharging,
}

internal sealed record TrackedDeviceBatterySample(
    uint DeviceIndex,
    bool IsConnected,
    ETrackedDeviceClass DeviceClass,
    string TrackingSystemName,
    string ModelNumber,
    string RenderModelName,
    string SerialNumber,
    bool HasBatteryValue,
    float BatteryPercent,
    ETrackedPropertyError BatteryError,
    bool HasChargingValue,
    bool IsCharging,
    ETrackedPropertyError ChargingError);

internal sealed record BatteryReadingSource(
    string Provider,
    string Description,
    float Percent);

internal sealed record ChargingStateSource(
    string Provider,
    string Description,
    BatteryChargingState State);

internal sealed record BatteryStateSnapshot(
    bool IsAvailable,
    float Percent,
    bool ShouldWarn,
    BatteryChargingState ChargingState,
    BatteryReadingSource? Source,
    ChargingStateSource? ChargingSource,
    IReadOnlyList<TrackedDeviceBatterySample> Samples)
{
    public static BatteryStateSnapshot Available(
        BatteryReadingSource source,
        ChargingStateSource? chargingSource,
        bool shouldWarn,
        IReadOnlyList<TrackedDeviceBatterySample> samples)
    {
        return new BatteryStateSnapshot(
            true,
            source.Percent,
            shouldWarn,
            chargingSource?.State ?? BatteryChargingState.Unknown,
            source,
            chargingSource,
            samples);
    }

    public static BatteryStateSnapshot Unavailable(
        IReadOnlyList<TrackedDeviceBatterySample> samples,
        ChargingStateSource? chargingSource = null)
    {
        return new BatteryStateSnapshot(
            false,
            0f,
            false,
            chargingSource?.State ?? BatteryChargingState.Unknown,
            null,
            chargingSource,
            samples);
    }
}
