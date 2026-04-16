using System.Reflection;
using System.Text;
using Valve.VR;
using VRRealityWindow.OpenVr;

namespace BatteryDotOverlay;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var version = GetAppVersion();
        CommandLineOptions options;

        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[error] {ex.Message}");
            Console.WriteLine(CommandLineOptions.GetUsage());
            return 1;
        }

        if (options.Help)
        {
            Console.WriteLine(CommandLineOptions.GetUsage());
            return 0;
        }

        var loadResult = BatteryDotSettingsStore.LoadOrCreate(options.SettingsPath);
        var settings = loadResult.Settings ?? BatteryDotSettings.CreateDefault();
        var validation = loadResult.IsSuccess
            ? BatteryDotSettingsValidator.ValidateAndNormalize(settings)
            : new SettingsValidationResult(Array.Empty<string>(), Array.Empty<string>());

        using var logger = AppLogger.Initialize(settings, version);

        logger.Info($"[settings] Using {options.SettingsPath}");
        logger.Info("[startup] Minimal head-locked battery dot overlay");
        logger.Info($"[startup] Version {version}");
        logger.Info("[startup] Ctrl+C to stop.");

        if (!string.IsNullOrWhiteSpace(logger.LogFilePath))
        {
            logger.Info($"[logging] File {logger.LogFilePath}");
        }

        if (!string.IsNullOrWhiteSpace(logger.InitializationWarning))
        {
            logger.Warn($"[logging] {logger.InitializationWarning}");
        }

        if (!loadResult.IsSuccess)
        {
            logger.Error($"[error] {loadResult.Message}");
            return 1;
        }

        if (loadResult.CreatedDefaultFile && !string.IsNullOrWhiteSpace(loadResult.Message))
        {
            logger.Warn($"[settings] {loadResult.Message}");
        }

        ReportSettingsValidation(validation, logger);

        if (options.ShowLogPath)
        {
            logger.Info(!string.IsNullOrWhiteSpace(logger.LogFilePath)
                ? $"[logging] Active log file: {logger.LogFilePath}"
                : "[logging] File logging is disabled or unavailable.");
            return validation.IsValid ? 0 : 1;
        }

        if (options.ValidateConfig)
        {
            RunConfigValidation(settings, validation, logger);
            return validation.IsValid ? 0 : 1;
        }

        if (options.DiagnoseBattery)
        {
            RunBatteryDiagnostics(settings, logger);
            return validation.IsValid ? 0 : 1;
        }

        AppInstanceGuard? instanceGuard = null;
        try
        {
            if (settings.Runtime.EnforceSingleInstance)
            {
                var instanceLock = AppInstanceGuard.TryAcquire(settings.Overlay.OverlayKey);
                if (!instanceLock.IsAcquired)
                {
                    logger.Error($"[error] Another BatteryDotOverlay instance is already running for overlay key '{settings.Overlay.OverlayKey}'.");
                    foreach (var runningInstance in instanceLock.RunningInstances)
                    {
                        logger.Error($"[error] Existing instance pid={runningInstance.ProcessId} started={FormatDateTime(runningInstance.StartTime)} path={Fallback(runningInstance.ExecutablePath)}");
                    }

                    logger.Error("[error] Close the previous instance or change overlay.overlay_key in the config.");
                    return 2;
                }

                instanceGuard = instanceLock.Guard;
            }

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
                logger.Error($"[error] Failed to initialize OpenVR overlay: {BuildOverlayInitializationMessage(ex, settings)}");
                return 1;
            }

            if (settings.Battery.LogDiagnosticsOnStartup)
            {
                var startupScan = BatteryTelemetry.ScanConnectedDevices(runtime);
                logger.Info(BatteryTelemetry.FormatDiagnostics(startupScan));
                logger.Info(BatteryTelemetry.FormatFallbackDiagnostics(settings));
            }

            var pollInterval = TimeSpan.FromMilliseconds(settings.Behavior.BatteryPollIntervalMs);
            var loopDelay = TimeSpan.FromMilliseconds(settings.Runtime.LoopDelayMs);
            var logInterval = TimeSpan.FromSeconds(settings.Behavior.StatusLogIntervalSeconds);

            var nextBatteryPollUtc = DateTime.MinValue;
            var nextLogUtc = DateTime.MinValue;
            var lastShouldWarn = false;
            var blinkOriginUtc = DateTime.UtcNow;
            var didLogUnavailableDiagnostics = false;
            var lastSourceSignature = string.Empty;
            BatteryStateSnapshot batteryState = BatteryStateSnapshot.Unavailable(Array.Empty<TrackedDeviceBatterySample>());

            while (!cancellation.IsCancellationRequested)
            {
                var nowUtc = DateTime.UtcNow;

                if (nowUtc >= nextBatteryPollUtc)
                {
                    batteryState = ReadBatteryState(runtime, settings);
                    nextBatteryPollUtc = nowUtc + pollInterval;

                    var sourceSignature = CreateSourceSignature(batteryState);
                    if (!string.Equals(lastSourceSignature, sourceSignature, StringComparison.Ordinal))
                    {
                        logger.Info($"[source] {DescribeBatteryStateSource(batteryState)}");
                        lastSourceSignature = sourceSignature;
                    }

                    if (!batteryState.IsAvailable && settings.Battery.LogDiagnosticsWhenUnavailable && !didLogUnavailableDiagnostics)
                    {
                        logger.Info(BatteryTelemetry.FormatDiagnostics(batteryState.Samples));
                        logger.Info(BatteryTelemetry.FormatFallbackDiagnostics(settings));
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
                        logger.Info(batteryState.ShouldWarn
                            ? $"[battery] Battery {batteryState.Percent:0.0}% from {batteryState.Source!.Description} | {DescribeChargingState(batteryState)}: indicator enabled."
                            : batteryState.IsAvailable
                                ? $"[battery] Battery {batteryState.Percent:0.0}% from {batteryState.Source!.Description} | {DescribeChargingState(batteryState)}: indicator hidden."
                                : "[battery] Battery value unavailable from OpenVR/ADB/PICO Connect: indicator hidden.");
                    }
                }

                if (nowUtc >= nextLogUtc)
                {
                    logger.Info(batteryState.IsAvailable
                        ? $"[status] Battery {batteryState.Percent:0.0}% from {batteryState.Source!.Description} | {DescribeChargingState(batteryState)} | indicator {(batteryState.ShouldWarn ? "active" : "hidden")}"
                        : "[status] Battery value unavailable from OpenVR/ADB/PICO Connect | indicator hidden");
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
        finally
        {
            instanceGuard?.Dispose();
        }
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

    private static void RunBatteryDiagnostics(BatteryDotSettings settings, AppLogger logger)
    {
        try
        {
            using var runtime = new OpenVrRuntime();
            var startupScan = BatteryTelemetry.ScanConnectedDevices(runtime);
            logger.Info(BatteryTelemetry.FormatDiagnostics(startupScan));
        }
        catch (Exception ex)
        {
            logger.Info($"[battery-diag] OpenVR scan unavailable: {ex.Message}");
        }

        logger.Info(BatteryTelemetry.FormatFallbackDiagnostics(settings));

        var snapshot = BatteryTelemetry.Read(settings);
        logger.Info(snapshot.IsAvailable
            ? $"[battery-diag] Selected battery source: {snapshot.Source!.Description} ({snapshot.Percent:0.0}%)"
            : "[battery-diag] Selected battery source: unavailable");
        logger.Info(snapshot.ChargingSource is not null
            ? $"[battery-diag] Selected charging source: {snapshot.ChargingSource.Description} ({snapshot.ChargingState})"
            : "[battery-diag] Selected charging source: unavailable");
    }

    private static void RunConfigValidation(
        BatteryDotSettings settings,
        SettingsValidationResult validation,
        AppLogger logger)
    {
        logger.Info(validation.IsValid
            ? "[validate] Config validation completed successfully."
            : "[validate] Config validation found blocking errors.");
        logger.Info($"[validate] Resolved logs directory: {AppPaths.ResolveLogsDirectory(settings.Runtime)}");

        var resolvedAdbPath = AdbBatteryServiceReader.ResolveAdbExecutablePath(settings.Battery);
        logger.Info(resolvedAdbPath is not null
            ? $"[validate] Resolved adb executable: {resolvedAdbPath}"
            : "[validate] Resolved adb executable: unavailable");

        if (validation.Warnings.Count == 0 && validation.Errors.Count == 0)
        {
            logger.Info("[validate] No config warnings.");
        }
    }

    private static void ReportSettingsValidation(SettingsValidationResult validation, AppLogger logger)
    {
        foreach (var warning in validation.Warnings)
        {
            logger.Warn($"[settings] {warning}");
        }

        foreach (var error in validation.Errors)
        {
            logger.Error($"[settings] {error}");
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

    private static string DescribeChargingState(BatteryStateSnapshot batteryState)
    {
        return batteryState.ChargingState switch
        {
            BatteryChargingState.Charging when batteryState.ChargingSource is not null
                => $"charging via {batteryState.ChargingSource.Description}",
            BatteryChargingState.NotCharging when batteryState.ChargingSource is not null
                => $"not charging via {batteryState.ChargingSource.Description}",
            BatteryChargingState.Unknown when batteryState.ChargingSource is not null
                => $"charging state unavailable via {batteryState.ChargingSource.Description}",
            _ => "charging state unavailable",
        };
    }

    private static string DescribeBatteryStateSource(BatteryStateSnapshot batteryState)
    {
        if (!batteryState.IsAvailable)
        {
            return "Battery source unavailable.";
        }

        var chargingDescription = batteryState.ChargingSource is not null
            ? $"{batteryState.ChargingSource.Provider} ({batteryState.ChargingState})"
            : "unavailable";

        return $"Battery source={batteryState.Source!.Provider} ({batteryState.Source.Description}) | charging source={chargingDescription}";
    }

    private static string CreateSourceSignature(BatteryStateSnapshot batteryState)
    {
        return string.Join(
            "|",
            batteryState.IsAvailable,
            batteryState.Source?.Provider ?? string.Empty,
            batteryState.Source?.Description ?? string.Empty,
            batteryState.ChargingSource?.Provider ?? string.Empty,
            batteryState.ChargingSource?.Description ?? string.Empty,
            batteryState.ChargingState);
    }

    private static string BuildOverlayInitializationMessage(Exception exception, BatteryDotSettings settings)
    {
        if (!exception.Message.Contains("KeyInUse", StringComparison.OrdinalIgnoreCase))
        {
            return exception.Message;
        }

        var builder = new StringBuilder();
        builder.Append(exception.Message);
        builder.Append($" Another overlay with key '{settings.Overlay.OverlayKey}' is already active.");

        var runningInstances = AppInstanceGuard.EnumerateRunningInstances();
        if (runningInstances.Count > 0)
        {
            builder.Append(" Running BatteryDotOverlay instances:");
            foreach (var runningInstance in runningInstances)
            {
                builder.Append($" pid={runningInstance.ProcessId} path={Fallback(runningInstance.ExecutablePath)};");
            }
        }

        builder.Append(" Close the previous instance or change overlay.overlay_key.");
        return builder.ToString();
    }

    private static string FormatDateTime(DateTime? value)
    {
        return value?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown";
    }

    private static string Fallback(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
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
    public bool ValidateConfig { get; init; }
    public bool ShowLogPath { get; init; }
    public bool Help { get; init; }

    public static CommandLineOptions Parse(string[] args)
    {
        var settingsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "config", "indicator.settings.json"));
        int? durationSeconds = null;
        var diagnoseBattery = false;
        var validateConfig = false;
        var showLogPath = false;
        var help = false;

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

                case "--validate-config":
                    validateConfig = true;
                    break;

                case "--show-log-path":
                    showLogPath = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    help = true;
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
            ValidateConfig = validateConfig,
            ShowLogPath = showLogPath,
            Help = help,
        };
    }

    public static string GetUsage()
    {
        return string.Join(Environment.NewLine,
        [
            "Usage:",
            "  BatteryDotOverlay.exe [options]",
            "",
            "Options:",
            "  --settings <path>         Use a specific config file.",
            "  --duration-seconds <n>    Stop automatically after n seconds.",
            "  --diagnose-battery        Print battery/charging diagnostics and exit.",
            "  --validate-config         Validate config values and print resolved runtime paths.",
            "  --show-log-path           Print the active log file path and exit.",
            "  --help                    Show this help.",
        ]);
    }
}
