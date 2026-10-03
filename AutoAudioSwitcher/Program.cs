// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using AutoAudioSwitcher.Properties;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Templates;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;

namespace AutoAudioSwitcher;

internal sealed class Program
{
    private static readonly TimeSpan WindowsAutomaticDefaultDeviceChangeThreshold = TimeSpan.FromSeconds(2);

    private const string LogsDirectoryName = "logs";
    public const string SettingsFile = "appsettings.json";

    /// <summary>
    /// Directory the rolling log files are written to, relative to the working directory.
    /// </summary>
    public static string LogsDirectory => LogsDirectoryName;

    private static readonly LoggingLevelSwitch levelSwitch = new(LogEventLevel.Error);
    private static ServiceProvider? provider;
    private static ILogger? logger;
    private static Mutex? singleInstanceMutex;

    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();

        if (!File.Exists(SettingsFile))
        {
            new Settings().Save();
        }

        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(SettingsFile, optional: false, reloadOnChange: true)
            .SetFileLoadExceptionHandler(e =>
            {
                // Happens if the file is empty or not a JSON object. This will revert the settings to default; no point
                // showing an error here since IConfiguration normally ignores invalid property values anyway.
                e.Ignore = e.Exception.GetBaseException() is JsonException;
            })
            .Build();

        services.ConfigureObservable<Settings>(config);

        levelSwitch.MinimumLevel = config.GetValue<LogEventLevel>(nameof(Settings.LogLevel));

        services.AddSingleton<ILogger>(_ => new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(
                path: Path.Combine(LogsDirectory, ".log"),
                formatter: new ExpressionTemplate(
                    "{@t:yyyy-MM-dd HH:mm:ss.fff zzz} [{@l:u3}] {#if SourceContext is not null}[{Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1)}] {#end}{@m}\n{@x}"),
                levelSwitch: levelSwitch,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 5,
                fileSizeLimitBytes: 10485760 /* 10 MiB */)
            .CreateLogger());

        services.AddSingleton<AudioDeviceManager>();
        services.AddSingleton<ConnectedMonitorsMonitor>();
        services.AddSingleton<CurrentMonitorMonitor>();
        services.AddSingleton<TrayIcon>();
        services.AddSingleton<WindowMessageListener>();
        services.AddSingleton<UpdateService>();

        // Per-app routing (mode B). ProcessAudioPolicyConfig is constructed eagerly because probing for the
        // undocumented interface is the one part that can legitimately fail, and we want to know at startup.
        services.AddSingleton<AudioSessionMonitor>();
        services.AddSingleton<WindowPositionTracker>();
        services.AddSingleton(sp => new ProcessAudioPolicyConfig(sp.GetRequiredService<ILogger>()));
        services.AddSingleton<PerAppAudioRouter>();

        return services.BuildServiceProvider();
    }

    [STAThread]
    public static void Main()
    {
        singleInstanceMutex = new(true, "f09f929b-e98f-a1e9-9fb3-e383aae383b3" /* This is my favorite GUID */, out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        Environment.CurrentDirectory = AppContext.BaseDirectory;

        // Must happen before any control or resource string is read: WinForms captures a control's text when it is
        // constructed, so applying the language after the tray icon exists would only half-work.
        AppEnvironment.ApplyLanguage(ReadConfiguredLanguage());

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (sender, e) => HandleCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (sender, e) => HandleCrash((Exception)e.ExceptionObject);

        ApplicationConfiguration.Initialize();

        // Automatic dark mode is restricted to Win11+ for no good reason. It uses the "AppsUseLightTheme" setting
        // rather than "SystemUsesLightTheme" anyway, so to be consistent with the taskbar and system icons' context
        // menus, we'll manage it ourselves instead.
        Application.SetColorMode(IsSystemDarkModeEnabled() ? SystemColorMode.Dark : SystemColorMode.Classic);
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            Application.SetColorMode(IsSystemDarkModeEnabled() ? SystemColorMode.Dark : SystemColorMode.Classic);
        };

        provider = ConfigureServices();
        logger = provider.GetRequiredService<ILogger>();
        logger.Information("Application is starting.");

        Application.ApplicationExit += (_, _) =>
        {
            logger.Information("Application is exiting.");
            provider.Dispose();
        };

        var settings = provider.GetRequiredService<IBehaviorObservable<Settings>>();
        settings.Subscribe(currentSettings =>
        {
            levelSwitch.MinimumLevel = currentSettings.LogLevel;

            logger.Information("Loaded settings: {@Settings}", currentSettings);
        });

        var connectedMonitorsMonitor = provider.GetRequiredService<ConnectedMonitorsMonitor>();
        connectedMonitorsMonitor.ConnectedMonitors.Subscribe(currentMonitors =>
        {
            logger.Information("Connected monitors: {Monitors}", currentMonitors.Select(m => m.FriendlyName));

            AddNewMonitorsToSettings(settings, currentMonitors, logger);
        });

        var currentMonitorMonitor = provider.GetRequiredService<CurrentMonitorMonitor>();
        var audioDeviceManager = provider.GetRequiredService<AudioDeviceManager>();

        // See https://github.com/maxkagamine/AutoAudioSwitcher/issues/11
        IObservable<Monitor> currentMonitorWhenWindowsChangesDefaultDeviceAutomatically =
            audioDeviceManager.PlaybackDevices.Skip(1)
                .Join(
                    right: audioDeviceManager.DefaultPlaybackDevice.Skip(1),
                    leftDurationSelector: _ => Observable.Timer(WindowsAutomaticDefaultDeviceChangeThreshold),
                    rightDurationSelector: _ => Observable.Empty<Unit>(),
                    resultSelector: (_, _) => currentMonitorMonitor.CurrentCurrentMonitor /* lol */)
                .Where(x => x is not null && settings.Value.Enabled)
                .Do(_ => logger.Information("Detected Windows automatically changing the default audio device. Rechecking the current monitor..."))!;

        // Mode A (Global): follow the foreground window. This is the original behaviour — whichever window has focus
        // decides the system default playback device, and therefore where all audio goes.
        currentMonitorMonitor.CurrentMonitor
            .Merge(currentMonitorWhenWindowsChangesDefaultDeviceAutomatically)
            .Where(_ => settings.Value.Mode is AudioRoutingMode.Global)
            .Subscribe(currentMonitor =>
            {
                if (!settings.Value.Enabled)
                {
                    return;
                }

                logger.Information("Current monitor is \"{CurrentMonitor}\"", currentMonitor.FriendlyName);

                if (settings.Value.Monitors.TryGetValue(currentMonitor.FriendlyName, out string? playbackDevice) &&
                    !string.IsNullOrEmpty(playbackDevice))
                {
                    audioDeviceManager.SetDefaultPlaybackDevice(playbackDevice);
                }
                else
                {
                    // Warning rather than Information: an unconfigured monitor means mode A does nothing at all for it,
                    // which is indistinguishable from "the feature is broken" unless the log says so.
                    logger.Warning("No playback device set for \"{CurrentMonitor}\"; not switching. Configure it in the settings window (tray icon > Settings...).",
                        currentMonitor.FriendlyName);
                }
            });

        WarnAboutUnconfiguredMonitors(settings, connectedMonitorsMonitor, logger);

        // Also re-check whenever the monitor set changes, so plugging in a new display surfaces it immediately.
        connectedMonitorsMonitor.ConnectedMonitors
            .Skip(1)
            .Subscribe(_ => WarnAboutUnconfiguredMonitors(settings, connectedMonitorsMonitor, logger));


        // Mode B (PerApp): follow each application's own window. The router owns its own subscriptions and decides
        // internally whether the current mode applies, so simply resolving it starts it.
        provider.GetRequiredService<PerAppAudioRouter>();

        provider.GetRequiredService<WindowMessageListener>();
        provider.GetRequiredService<TrayIcon>().Show();

        Application.Run();
    }

    /// <summary>
    /// Logs a warning for every connected monitor that has no playback device assigned.
    /// </summary>
    /// <remarks>
    /// A monitor whose mapping is the empty string is skipped by the routing logic entirely, so mode A appears to
    /// "work once and then stop" when in fact it never ran for that display. Saying so up front turns a confusing
    /// bug report into a one-line fix.
    /// </remarks>
    private static void WarnAboutUnconfiguredMonitors(
        IBehaviorObservable<Settings> settings,
        ConnectedMonitorsMonitor connectedMonitorsMonitor,
        ILogger logger)
    {
        try
        {
            if (settings.Value.Mode is not AudioRoutingMode.Global)
            {
                // Mode B decides per application, and its own router reports what it could not resolve.
                return;
            }

            string[] unconfigured = [.. connectedMonitorsMonitor.CurrentConnectedMonitors
                .Select(m => m.FriendlyName)
                .Where(name => !settings.Value.Monitors.TryGetValue(name, out string? device) || string.IsNullOrEmpty(device))];

            if (unconfigured.Length > 0)
            {
                logger.Warning(
                    "{Count} connected monitor(s) have no playback device set ({Monitors}). Global mode will not switch audio for them. Open the settings window (tray icon > Settings...) to assign one.",
                    unconfigured.Length, unconfigured);
            }
        }
        catch (Exception ex)
        {
            // Best-effort diagnostics only; never let this keep the app from starting.
            logger.Debug(ex, "Could not check for unconfigured monitors");
        }
    }

    private static void AddNewMonitorsToSettings(
        IBehaviorObservable<Settings> settings, IEnumerable<Monitor> currentMonitors, ILogger logger)
    {
        try
        {
            string[] newMonitors = currentMonitors
                .Select(m => m.FriendlyName)
                .Except(settings.Value.Monitors.Keys)
                .Distinct()
                .ToArray();

            if (newMonitors.Length == 0)
            {
                return;
            }

            logger.Information("Adding new monitors to appsettings.json: {Monitors}", newMonitors);

            var newSettings = settings.Value with
            {
                Monitors = new Dictionary<string, string>([
                    .. settings.Value.Monitors,
                    .. newMonitors.Select(m => new KeyValuePair<string, string>(m, ""))])
            };

            newSettings.Save();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to add new monitors to appsettings.json");
        }
    }

    /// <summary>
    /// Reads the language tag out of the settings file before the DI container exists, so the language can be
    /// applied ahead of any localized text.
    /// </summary>
    private static string ReadConfiguredLanguage()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return "";
            }

            using FileStream file = File.OpenRead(SettingsFile);
            Settings? settings = JsonSerializer.Deserialize(file, SettingsSerializerContext.Default.Settings);
            return settings?.Language ?? "";
        }
        catch
        {
            // A corrupt settings file must not prevent startup; ConfigureServices will handle it properly.
            return "";
        }
    }

    public static bool IsSystemDarkModeEnabled()
    {
        // https://github.com/maxkagamine/AutoAudioSwitcher/issues/9
        int? systemUsesLightTheme = null;

        try
        {
            systemUsesLightTheme = Registry.GetValue(
                keyName: @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                valueName: "SystemUsesLightTheme",
                defaultValue: 1) as int?;
        }
        catch { }

        return systemUsesLightTheme == 0;
    }

    private static void HandleCrash(Exception ex)
    {
        try
        {
            logger?.Fatal(ex, "Unhandled exception.");
            provider?.Dispose();
        }
        catch { }

        try
        {
            TaskDialogCommandLinkButton restartButton = new(Resources.Restart);
            TaskDialogCommandLinkButton exitButton = new(Resources.Exit);
            TaskDialogCommandLinkButton logsButton = new(Resources.OpenLogDirectory, allowCloseDialog: false);

            string str = ex.ToString();
            var stackTraceIndex = str.IndexOf("   at ", StringComparison.OrdinalIgnoreCase);
            string text = stackTraceIndex > 0 ? str[..stackTraceIndex].TrimEnd() : str;
            TaskDialogExpander? stackTrace = stackTraceIndex > 0 ? new(str[stackTraceIndex..]) : null;

            text = text.Replace("\\", "\\\u200B"); // Zero width space to allow paths to wrap instead of getting shortened with an ellipsis

            TaskDialogPage taskDialog = new()
            {
                Heading = Resources.UnhandledException,
                Text = text,
                Expander = stackTrace,
                SizeToContent = true,
                Caption = Resources.ProgramName,
                Icon = TaskDialogIcon.Error,
                Buttons = logger is null ? // Don't show logs button if program crashed during init before logger setup
                    [restartButton, exitButton] :
                    [restartButton, exitButton, logsButton],
            };

            logsButton.Click += (_, _) =>
            {
                Process.Start(new ProcessStartInfo(LogsDirectory) { UseShellExecute = true });
            };

            if (TaskDialog.ShowDialog(taskDialog) == restartButton)
            {
                singleInstanceMutex?.Dispose();
                Application.Restart();
                return;
            }
        }
        catch { }

        Application.Exit();
    }
}
