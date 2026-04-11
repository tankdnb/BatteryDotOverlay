using System.Text;
using Valve.VR;
using VRRealityWindow.OpenVr;

namespace BatteryDotOverlay;

internal static class BatteryTelemetry
{
    public static BatteryStateSnapshot Read(OpenVrRuntime runtime, BatteryDotSettings settings)
    {
        runtime.EnsureInitialized();

        var samples = ScanConnectedDevices(runtime);
        var openVrDevice = SelectOpenVrBatteryDevice(samples, settings);
        if (openVrDevice is not null)
        {
            return CreateSnapshot(CreateOpenVrSource(openVrDevice), settings, samples);
        }

        var picoConnectSource = TryReadPicoConnectFallback(settings);
        return picoConnectSource is not null
            ? CreateSnapshot(picoConnectSource, settings, samples)
            : BatteryStateSnapshot.Unavailable(samples);
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
        if (!settings.Battery.EnablePicoConnectLogFallback)
        {
            return "[battery-diag] PICO Connect fallback is disabled in settings.";
        }

        var fallback = TryReadPicoConnectFallback(settings);
        return fallback is not null
            ? $"[battery-diag] PICO Connect headset battery: {fallback.Percent:0.0}% from {fallback.Description}"
            : $"[battery-diag] PICO Connect fallback: no fresh headset battery event found in {PicoConnectBatteryLogReader.ResolveLogsPath(settings.Battery)}";
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

    private static BatteryReadingSource? TryReadPicoConnectFallback(BatteryDotSettings settings)
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

    private static BatteryReadingSource CreateOpenVrSource(TrackedDeviceBatterySample sample)
    {
        return new BatteryReadingSource(
            "openvr",
            $"OpenVR device #{sample.DeviceIndex} ({sample.DeviceClass})",
            sample.BatteryPercent);
    }

    private static BatteryStateSnapshot CreateSnapshot(
        BatteryReadingSource source,
        BatteryDotSettings settings,
        IReadOnlyList<TrackedDeviceBatterySample> samples)
    {
        var percent = Math.Clamp(source.Percent, 0f, 100f);
        var shouldWarn = percent < settings.Behavior.GetEffectiveBlinkBelowPercent();
        return BatteryStateSnapshot.Available(source with { Percent = percent }, shouldWarn, samples);
    }

    private static string GetStringProperty(OpenVrRuntime runtime, uint deviceIndex, ETrackedDeviceProperty property)
    {
        var value = runtime.GetTrackedDeviceString(deviceIndex, property);
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
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

internal sealed record BatteryStateSnapshot(
    bool IsAvailable,
    float Percent,
    bool ShouldWarn,
    BatteryReadingSource? Source,
    IReadOnlyList<TrackedDeviceBatterySample> Samples)
{
    public static BatteryStateSnapshot Available(
        BatteryReadingSource source,
        bool shouldWarn,
        IReadOnlyList<TrackedDeviceBatterySample> samples)
    {
        return new BatteryStateSnapshot(true, source.Percent, shouldWarn, source, samples);
    }

    public static BatteryStateSnapshot Unavailable(IReadOnlyList<TrackedDeviceBatterySample> samples)
    {
        return new BatteryStateSnapshot(false, 0f, false, null, samples);
    }
}
