// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using AutoAudioSwitcher.Properties;
using Microsoft.Win32;
using Serilog;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;

namespace AutoAudioSwitcher;

internal sealed class TrayIcon : IDisposable
{
    private static readonly CompositeFormat UpdateAvailableFormat =
        CompositeFormat.Parse(Resources.BalloonUpdateAvailableText);

    private readonly ILogger logger;
    private readonly NotifyIcon notifyIcon;
    private readonly IBehaviorObservable<Settings> settings;
    private readonly IObservable<ContextMenuStrip> menu;
    private readonly CompositeDisposable subscriptions = [];

    private readonly ConnectedMonitorsMonitor connectedMonitorsMonitor;
    private readonly AudioDeviceManager audioDeviceManager;
    private readonly ProcessAudioPolicyConfig policyConfig;
    private readonly UpdateService updateService;

    /// <summary>
    /// Kept alive between openings so the window reuses its layout instead of rebuilding every time. Nulled by
    /// the form's own dispose so a later click creates a fresh one.
    /// </summary>
    private SettingsForm? settingsForm;

    public TrayIcon(
        ConnectedMonitorsMonitor connectedMonitorsMonitor,
        AudioDeviceManager audioDeviceManager,
        ProcessAudioPolicyConfig policyConfig,
        UpdateService updateService,
        IBehaviorObservable<Settings> settings,
        ILogger logger)
    {
        this.logger = logger = logger.ForContext<TrayIcon>();
        this.settings = settings;
        this.connectedMonitorsMonitor = connectedMonitorsMonitor;
        this.audioDeviceManager = audioDeviceManager;
        this.policyConfig = policyConfig;
        this.updateService = updateService;

        notifyIcon = new()
        {
            Text = Resources.ProgramName
        };

        UpdateIcon();
        subscriptions.Add(settings.Subscribe(_ => UpdateIcon()));
        subscriptions.Add(Observable.FromEventPattern<UserPreferenceChangedEventHandler, UserPreferenceChangedEventArgs>(
            handler => SystemEvents.UserPreferenceChanged += handler,
            handler => SystemEvents.UserPreferenceChanged -= handler)
            .Subscribe(_ => UpdateIcon()));

        notifyIcon.Click += (object? sender, EventArgs e) =>
        {
            if (e is MouseEventArgs { Button: MouseButtons.Left })
            {
                OnEnabledClicked(null, EventArgs.Empty);
            }
        };

        menu = Observable.CombineLatest(
            connectedMonitorsMonitor.ConnectedMonitors,
            audioDeviceManager.PlaybackDevices,
            settings,
            (connectedMonitors, playbackDevices, settings) =>
            {
                logger.Debug("Rebuilding tray menu");

                ContextMenuStrip menu = new();

                foreach (var monitorName in connectedMonitors.Select(m => m.FriendlyName).Order())
                {
                    string monitorPlaybackDevice = settings.Monitors.GetValueOrDefault(monitorName) ?? "";

                    ToolStripMenuItem monitorItem = new(
                        text: monitorName.Replace("&", "&&"),
                        image: null,
                        dropDownItems: [
                            .. playbackDevices.Select(deviceName =>
                                new PlaybackDeviceMenuItem(this, monitorName, deviceName, monitorPlaybackDevice)),
                            new ToolStripSeparator(),
                            new PlaybackDeviceMenuItem(this, monitorName, "", monitorPlaybackDevice)
                        ]);

                    menu.Items.Add(monitorItem);
                }

                menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem enabledItem = new(Resources.Enabled) { Checked = settings.Enabled };
                enabledItem.Click += OnEnabledClicked;
                menu.Items.Add(enabledItem);

                ToolStripMenuItem settingsItem = new(Resources.Settings);
                settingsItem.Click += OnSettingsClicked;
                menu.Items.Add(settingsItem);

                menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem exitItem = new(Resources.Exit);
                exitItem.Click += (_, _) => Application.Exit();
                menu.Items.Add(exitItem);

                return menu;
            });
    }

    public void Show()
    {
        subscriptions.Add(menu.Subscribe(menu =>
        {
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.Visible = true;
        }));

        notifyIcon.BalloonTipClicked += (_, _) => OnSettingsClicked(null, EventArgs.Empty);

        if (settings.Value.CheckForUpdatesOnStartup)
        {
            // Fire and forget: a startup update check must never delay the tray icon appearing, and any failure
            // is already logged and swallowed by the service.
            _ = CheckForUpdatesOnStartupAsync();
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            UpdateCheckResult result = await updateService.CheckAsync();

            if (result.Status is UpdateStatus.Available && result.Version is not null)
            {
                logger.Information("Update {Version} is available.", result.Version);
                ShowUpdateAvailableBalloon(result.Version);
            }
        }
        catch (Exception ex)
        {
            // Never let the updater break startup.
            logger.Warning(ex, "Startup update check failed.");
        }
    }

    private void UpdateIcon(Settings? settings = null)
    {
        settings ??= this.settings.Value;

        notifyIcon.Icon = (Program.IsSystemDarkModeEnabled(), settings.Enabled) switch
        {
            (true, true) => Resources.TrayIconLight,
            (true, false) => Resources.TrayIconLightDisabled,
            (false, true) => Resources.TrayIconDark,
            (false, false) => Resources.TrayIconDarkDisabled
        };
    }

    private void OnPlaybackDeviceMenuItemClicked(object? sender, EventArgs e)
    {
        var item = (PlaybackDeviceMenuItem)sender!;

        logger.Debug("Setting playback device for \"{MonitorName}\" to \"{DeviceName}\"",
            item.MonitorName, item.DeviceName);

        var newSettings = settings.Value with
        {
            Monitors = new Dictionary<string, string>(settings.Value.Monitors)
            {
                [item.MonitorName] = item.DeviceName
            }
        };

        newSettings.Save();
    }

    private void OnEnabledClicked(object? sender, EventArgs e)
    {
        var newSettings = settings.Value with { Enabled = !settings.Value.Enabled };

        logger.Debug("Changing Enabled to {Enabled}", newSettings.Enabled);

        UpdateIcon(newSettings); // Update icon with no delay
        newSettings.Save();
    }

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        // A modal dialog with no owner has no window to stay in front of, so it can end up behind the app the
        // user was just looking at. Reuse a single instance rather than accumulating one per click.
        if (settingsForm is { IsDisposed: false })
        {
            if (settingsForm.WindowState is FormWindowState.Minimized)
            {
                settingsForm.WindowState = FormWindowState.Normal;
            }

            settingsForm.Activate();
            return;
        }

        settingsForm = new SettingsForm(
            settings,
            connectedMonitorsMonitor,
            audioDeviceManager,
            policyConfig,
            updateService,
            logger);

        settingsForm.FormClosed += (_, _) => settingsForm = null;
        settingsForm.Show();

        // The tray icon is the only UI, so a plain Show() would leave the window behind whatever is focused.
        settingsForm.Activate();
        settingsForm.BringToFront();
    }

    /// <summary>
    /// Tells the user an update exists. Clicking the balloon opens the settings window on the Updates tab.
    /// </summary>
    private void ShowUpdateAvailableBalloon(Version version)
    {
        notifyIcon.BalloonTipTitle = Resources.BalloonUpdateAvailableTitle;
        notifyIcon.BalloonTipText = string.Format(CultureInfo.InvariantCulture, UpdateAvailableFormat, version);
        notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        notifyIcon.ShowBalloonTip(10000);
    }

    public void Dispose()
    {
        subscriptions.Dispose();

        settingsForm?.Dispose();
        settingsForm = null;

        notifyIcon.Visible = false;
        notifyIcon.Dispose();
    }

    private sealed class PlaybackDeviceMenuItem : ToolStripMenuItem
    {
        public PlaybackDeviceMenuItem(
            TrayIcon trayIcon,
            string monitorName,
            string deviceName,
            string? deviceNameSetForMonitor)
            : base(deviceName == "" ? Resources.EmptyStringMenuItem : deviceName.Replace("&", "&&"))
        {
            MonitorName = monitorName;
            DeviceName = deviceName;
            Checked = deviceName == (deviceNameSetForMonitor ?? "");
            Click += trayIcon.OnPlaybackDeviceMenuItemClicked;
        }

        public string MonitorName { get; }

        public string DeviceName { get; }
    }
}
