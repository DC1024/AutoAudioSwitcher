// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using CoreAudio;
using Serilog;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;

namespace AutoAudioSwitcher;

/// <summary>
/// Per-application routing ("mode B"): each process playing audio is sent to the playback device assigned to the
/// monitor its own window is on.
/// </summary>
/// <remarks>
/// <para>
/// This differs from <see cref="CurrentMonitorMonitor"/>-driven routing (mode A) in one crucial way. Mode A follows
/// the <em>foreground window</em>: whichever window has focus decides where <em>all</em> audio goes. Mode B instead
/// looks at each individual audio session and asks "where is <em>this</em> application's window?" — so a music
/// player parked on the second display keeps playing through the second display's speakers even while you work in
/// a different window on the first.
/// </para>
/// <para>
/// Two limits are worth being explicit about, because they are properties of Windows rather than of this code:
/// </para>
/// <list type="bullet">
/// <item>
/// Windows tracks audio sessions <b>per process</b>, so two windows of the same browser share one routing decision.
/// Use a second browser instance with its own profile, or a separate player, to route them independently.
/// </item>
/// <item>
/// A binding made through the policy interface applies when the application next opens an audio stream. An app
/// already playing keeps its current stream until it restarts playback.
/// </item>
/// </list>
/// </remarks>
internal sealed class PerAppAudioRouter : IDisposable
{
    /// <summary>
    /// Audio sessions and window positions both change in bursts (drag a window and you get a stream of move
    /// events), so routing decisions are debounced.
    /// </summary>
    private static readonly TimeSpan DebounceTimeout = TimeSpan.FromMilliseconds(250);

    private readonly AudioSessionMonitor sessionMonitor;
    private readonly ConnectedMonitorsMonitor connectedMonitorsMonitor;
    private readonly WindowPositionTracker positionTracker;
    private readonly AudioDeviceManager audioDeviceManager;
    private readonly ProcessAudioPolicyConfig policyConfig;
    private readonly IBehaviorObservable<Settings> settings;
    private readonly CompositeDisposable subscriptions = [];
    private readonly ILogger logger;

    /// <summary>
    /// The last routing we <em>requested</em> per process, keyed by process ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately a record of intent rather than a claim of success. <c>SetPersistedDefaultAudioEndpoint</c>
    /// writes a persisted preference, and Windows does not reliably honour that preference when it later creates the
    /// application's audio session — the preference reads back correctly while the app keeps playing through the
    /// previous device. So a successful call is not evidence that the audio actually moved, and treating it as such
    /// would mean never trying again.
    /// </para>
    /// <para>
    /// The pair we store is therefore (device, session state at the time we asked). Re-issuing is driven by the
    /// session state changing — see <see cref="NeedsRouting"/>.
    /// </para>
    /// </remarks>
    private readonly Dictionary<uint, (string Device, AudioSessionState State)> currentRouting = [];

    public PerAppAudioRouter(
        AudioSessionMonitor sessionMonitor,
        ConnectedMonitorsMonitor connectedMonitorsMonitor,
        WindowPositionTracker positionTracker,
        AudioDeviceManager audioDeviceManager,
        ProcessAudioPolicyConfig policyConfig,
        IBehaviorObservable<Settings> settings,
        ILogger logger)
    {
        this.sessionMonitor = sessionMonitor;
        this.connectedMonitorsMonitor = connectedMonitorsMonitor;
        this.positionTracker = positionTracker;
        this.audioDeviceManager = audioDeviceManager;
        this.policyConfig = policyConfig;
        this.settings = settings;
        this.logger = logger.ForContext<PerAppAudioRouter>();

        if (!policyConfig.IsUsable)
        {
            logger.Warning("Per-app audio routing is not available on this system; mode B will not move any audio.");
        }

        // Re-evaluate whenever the set of audio sessions changes, and also on a slow heartbeat. The heartbeat is not
        // just for window moves: because Windows applies a persisted per-app preference unreliably, a periodic
        // re-issue is the only way to converge when the OS quietly declines to honour it.
        sessionMonitor.Sessions
            .Select(_ => Unit.Default)
            .Merge(Observable.Interval(TimeSpan.FromSeconds(3)).Select(_ => Unit.Default))
            .Throttle(DebounceTimeout)
            .Subscribe(_ => RouteAll())
            .DisposeWith(subscriptions);

        // A newly added monitor has no device mapping yet and a removed one still has a stale mapping, so re-run
        // routing when the display layout changes.
        connectedMonitorsMonitor.ConnectedMonitors
            .Skip(1)
            .Subscribe(monitors =>
            {
                logger.Information("Display layout changed; re-evaluating per-app routing.");
                RouteAll();
            })
            .DisposeWith(subscriptions);

        // Changing settings (assigning a device to a monitor, switching modes) should take effect immediately.
        settings.Skip(1)
            .Subscribe(_ => RouteAll())
            .DisposeWith(subscriptions);
    }

    /// <summary>
    /// Applies the current routing rules to every active audio session.
    /// </summary>
    public void RouteAll()
    {
        var currentSettings = settings.Value;

        if (!currentSettings.Enabled)
        {
            logger.Verbose("Auto-switching is disabled; skipping per-app routing.");
            return;
        }

        if (currentSettings.Mode is not AudioRoutingMode.PerApp)
        {
            // Debug, not Verbose: "I switched to per-app mode and nothing happened" is otherwise impossible to
            // tell apart from "per-app mode did nothing useful", and the two need very different answers.
            logger.Debug("Current routing mode is {Mode}; skipping per-app routing.", currentSettings.Mode);
            return;
        }

        if (!policyConfig.IsUsable)
        {
            return;
        }

        AudioSession[] sessions = sessionMonitor.CurrentSessions;
        var liveProcessIds = new HashSet<uint>();

        foreach (AudioSession session in sessions)
        {
            liveProcessIds.Add(session.ProcessId);
            RouteSession(session, currentSettings);
        }

        // Forget processes that are no longer playing anything, so a recycled PID doesn't inherit a stale entry.
        foreach (uint stalePid in currentRouting.Keys.Where(pid => !liveProcessIds.Contains(pid)).ToArray())
        {
            currentRouting.Remove(stalePid);
        }
    }

    private void RouteSession(AudioSession session, Settings currentSettings)
    {
        string? monitorDeviceName = positionTracker.GetMonitorDeviceName(session.ProcessId, session.ProcessName);
        if (monitorDeviceName is null)
        {
            logger.Verbose("{ProcessName} (pid {ProcessId}) has no resolvable monitor; leaving routing unchanged.",
                session.ProcessName, session.ProcessId);
            return;
        }

        // Map the monitor's GDI name (e.g. \\.\DISPLAY2) to its friendly name, which is what the user assigns a
        // playback device to in settings.
        Monitor? monitor = connectedMonitorsMonitor.CurrentConnectedMonitors
            .FirstOrDefault(m => m.GdiDeviceName == monitorDeviceName);

        if (monitor is null)
        {
            logger.Verbose("Monitor {GdiDeviceName} is not in the connected monitor list.", monitorDeviceName);
            return;
        }

        if (!currentSettings.Monitors.TryGetValue(monitor.FriendlyName, out string? deviceName) ||
            string.IsNullOrEmpty(deviceName))
        {
            logger.Verbose("No playback device configured for monitor {Monitor}; {ProcessName} left as-is.",
                monitor.FriendlyName, session.ProcessName);
            return;
        }

        if (!NeedsRouting(session, deviceName))
        {
            return;
        }

        string? deviceId = FindDeviceId(deviceName);
        if (deviceId is null)
        {
            logger.Warning("Configured playback device {DeviceName} was not found among active devices.", deviceName);
            return;
        }

        logger.Information("Routing {ProcessName} (pid {ProcessId}, on {Monitor}, session {State}) to {Device}.",
            session.ProcessName, session.ProcessId, monitor.FriendlyName, session.State, deviceName);

        // All three roles, so whichever one the application plays through is covered. Windows keeps console and
        // multimedia in sync for most apps, but communications is often separate.
        bool ok = policyConfig.SetProcessPlaybackEndpoint(deviceId, session.ProcessId, 0, 1, 2);

        // Record the attempt regardless of the result: the point is to remember what we last asked for and under
        // which session state, not to assert that Windows complied. If the OS ignored us, a later state change or
        // the heartbeat will bring us back here.
        currentRouting[session.ProcessId] = (deviceName, session.State);

        if (!ok)
        {
            logger.Debug("Per-app routing for {ProcessName} returned false (no audio session yet?); will retry.",
                session.ProcessName);
        }
    }

    /// <summary>
    /// Decides whether <paramref name="session"/> should be re-routed to <paramref name="deviceName"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-issue when any of these is true:
    /// </para>
    /// <list type="bullet">
    /// <item>The process has not been routed yet this run.</item>
    /// <item>The target device changed (the user moved the window to the other display, or reassigned a device).</item>
    /// <item>The session state changed since we last asked — in particular a transition into
    /// <see cref="AudioSessionState.Active"/>, which is exactly when the application opens a fresh stream and
    /// Windows re-reads the persisted preference. This is the moment our earlier request needs to be in place.</item>
    /// </list>
    /// <para>
    /// Staying silent while nothing changes keeps the log readable and avoids needless COM traffic, without the
    /// false confidence of "we succeeded once, so we are done".
    /// </para>
    /// </remarks>
    private bool NeedsRouting(AudioSession session, string deviceName)
    {
        if (!currentRouting.TryGetValue(session.ProcessId, out var last))
        {
            return true;
        }

        return last.Device != deviceName || last.State != session.State;
    }

    /// <summary>
    /// Resolves a playback device's display name to its MMDeviceAPI endpoint ID.
    /// </summary>
    private string? FindDeviceId(string displayName)
    {
        foreach (MMDevice device in audioDeviceManager.EnumerateActivePlaybackDevices())
        {
            using (device)
            {
                string name = device.Properties?[PKey.DeviceDescription]?.Value?.ToString() ?? "<Unknown>";
                if (name == displayName)
                {
                    return device.ID;
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        subscriptions.Dispose();
    }
}