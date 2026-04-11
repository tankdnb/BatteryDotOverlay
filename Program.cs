using System.Reflection;
using Valve.VR;
using VRRealityWindow.OpenVr;

namespace BatteryDotOverlay;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        var settingsPath = options.SettingsPath;
        var settings = BatteryDotSettingsStore.LoadOrCreate(settingsPath);
        var version = GetAppVersion();

        Console.WriteLine($"[settings] Using {settingsPath}");
        Console.WriteLine("[startup] Minimal head-locked battery dot overlay");
        Console.WriteLine($"[startup] Version {version}");
        Console.WriteLine("[startup] Ctrl+C to stop.");

        using var runtime = new OpenVrRuntime();
        using var overlay = new BatteryIndicatorOverlay(runtime, settings);
        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        if (options.DurationSeconds is > 0)
        {
            cancellation.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds.Value));
        }

        try
        {
            overlay.Initialize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Failed to initialize OpenVR overlay: {ex.Message}");
            return 1;
        }

        if (settings.Battery.LogDiagnosticsOnStartup || options.DiagnoseBattery)
        {
            var startupScan = BatteryTelemetry.ScanConnectedDevices(runtime);
            Console.WriteLine(BatteryTelemetry.FormatDiagnostics(startupScan));
            Console.WriteLine(BatteryTelemetry.FormatFallbackDiagnostics(settings));
        }

        if (options.DiagnoseBattery)
        {
            return 0;
        }

        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(100, settings.Behavior.BatteryPollIntervalMs));
        var loopDelay = TimeSpan.FromMilliseconds(Math.Max(10, settings.Runtime.LoopDelayMs));
        var logInterval = TimeSpan.FromSeconds(Math.Max(1, settings.Behavior.StatusLogIntervalSeconds));

        var nextBatteryPollUtc = DateTime.MinValue;
        var nextLogUtc = DateTime.MinValue;
        var lastShouldWarn = false;
        var blinkOriginUtc = DateTime.UtcNow;
        BatteryStateSnapshot batteryState = BatteryStateSnapshot.Unavailable(Array.Empty<TrackedDeviceBatterySample>());
        var didLogUnavailableDiagnostics = false;

        while (!cancellation.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;

            if (nowUtc >= nextBatteryPollUtc)
            {
                batteryState = ReadBatteryState(runtime, settings);
                nextBatteryPollUtc = nowUtc + pollInterval;

                if (!batteryState.IsAvailable && settings.Battery.LogDiagnosticsWhenUnavailable && !didLogUnavailableDiagnostics)
                {
                    Console.WriteLine(BatteryTelemetry.FormatDiagnostics(batteryState.Samples));
                    Console.WriteLine(BatteryTelemetry.FormatFallbackDiagnostics(settings));
                    didLogUnavailableDiagnostics = true;
                }

                if (batteryState.IsAvailable)
                {
                    didLogUnavailableDiagnostics = false;
                }

                if (batteryState.ShouldWarn != lastShouldWarn)
                {
                    blinkOriginUtc = nowUtc;
                    lastShouldWarn = batteryState.ShouldWarn;
                    Console.WriteLine(batteryState.ShouldWarn
                        ? $"[battery] Battery {batteryState.Percent:0.0}% from {batteryState.Source!.Description}: indicator enabled."
                        : batteryState.IsAvailable
                            ? $"[battery] Battery {batteryState.Percent:0.0}% from {batteryState.Source!.Description}: indicator hidden."
                            : "[battery] Battery value unavailable from OpenVR/PICO Connect: indicator hidden.");
                }
            }

            if (nowUtc >= nextLogUtc)
            {
                Console.WriteLine(batteryState.IsAvailable
                    ? $"[status] Battery {batteryState.Percent:0.0}% from {batteryState.Source!.Description} | indicator {(batteryState.ShouldWarn ? "active" : "hidden")}"
                    : "[status] Battery value unavailable from OpenVR/PICO Connect | indicator hidden");
                nextLogUtc = nowUtc + logInterval;
            }

            overlay.SetVisible(ShouldOverlayBeVisible(batteryState, settings, blinkOriginUtc, nowUtc));

            try
            {
                await Task.Delay(loopDelay, cancellation.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        overlay.Hide();
        return 0;
    }

    private static BatteryStateSnapshot ReadBatteryState(OpenVrRuntime runtime, BatteryDotSettings settings)
    {
        try
        {
            return BatteryTelemetry.Read(runtime, settings);
        }
        catch
        {
            return BatteryStateSnapshot.Unavailable(Array.Empty<TrackedDeviceBatterySample>());
        }
    }

    private static bool ShouldOverlayBeVisible(
        BatteryStateSnapshot batteryState,
        BatteryDotSettings settings,
        DateTime blinkOriginUtc,
        DateTime nowUtc)
    {
        if (!batteryState.ShouldWarn)
        {
            return false;
        }

        var frequency = settings.Behavior.BlinkFrequencyHz;
        if (frequency <= 0f)
        {
            return true;
        }

        var phase = (nowUtc - blinkOriginUtc).TotalSeconds * frequency;
        var normalizedPhase = phase - Math.Floor(phase);
        return normalizedPhase < 0.5d;
    }

    private static string GetAppVersion()
    {
        return Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
    }
}

internal sealed class CommandLineOptions
{
    public string SettingsPath { get; init; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "config", "indicator.settings.json"));
    public int? DurationSeconds { get; init; }
    public bool DiagnoseBattery { get; init; }

    public static CommandLineOptions Parse(string[] args)
    {
        var settingsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "config", "indicator.settings.json"));
        int? durationSeconds = null;
        var diagnoseBattery = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--settings" when index + 1 < args.Length:
                    settingsPath = Path.GetFullPath(args[++index]);
                    break;

                case "--duration-seconds" when index + 1 < args.Length && int.TryParse(args[++index], out var seconds):
                    durationSeconds = seconds;
                    break;

                case "--diagnose-battery":
                    diagnoseBattery = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }

        return new CommandLineOptions
        {
            SettingsPath = settingsPath,
            DurationSeconds = durationSeconds,
            DiagnoseBattery = diagnoseBattery,
        };
    }
}
