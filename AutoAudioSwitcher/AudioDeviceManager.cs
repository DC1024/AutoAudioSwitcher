// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using CoreAudio;
using Serilog;
using Serilog.Events;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace AutoAudioSwitcher;

internal sealed class AudioDeviceManager : IDisposable
{
    /// <summary>
    /// How often the convergence loop re-checks that the requested device actually became the default.
    /// </summary>
    /// <remarks>
    /// Windows applies default-device changes asynchronously, with an observed lag around one second, so this sits
    /// comfortably past it while still feeling immediate to a user who is dragging a window between displays. The
    /// loop only runs while a switch is unconfirmed. See <see cref="EnsureDefaultPlaybackDevice"/>.
    /// </remarks>
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// How many consecutive ticks must agree before the convergence loop declares victory.
    /// </summary>
    /// <remarks>
    /// One match is not enough. A change we issued earlier can still be in flight and land a moment after the
    /// default happens to read as the target, which would end the loop with the audio on the wrong device — exactly
    /// the symptom this loop exists to fix. Requiring a second match costs one extra interval (~750 ms) and closes
    /// that window.
    /// </remarks>
    private const int RequiredConsecutiveMatches = 2;

    private readonly MMDeviceEnumerator deviceEnumerator;
    private readonly MMNotificationClient notificationClient;
    private readonly BehaviorSubject<DefaultAudioDevice> defaultPlaybackDevice;
    private readonly CompositeDisposable subscriptions = [];
    private readonly ILogger logger;

    /// <summary>
    /// The device name the convergence loop is currently chasing, or null when nothing is pending.
    /// </summary>
    private string? pendingVerification;

    /// <summary>
    /// How many consecutive ticks have seen <see cref="pendingVerification"/> as the default. The loop only stops
    /// after <see cref="RequiredConsecutiveMatches"/>, so a stale in-flight change landing just after a lucky match
    /// still gets corrected.
    /// </summary>
    private int pendingVerificationMatches;

    /// <summary>
    /// The interval subscription driving the convergence loop. Lazily created and disposed whenever the loop is
    /// idle, so an idle app holds no timer.
    /// </summary>
    private IDisposable? verificationSubscription;

    public AudioDeviceManager(ILogger logger)
    {
        this.logger = logger = logger.ForContext<AudioDeviceManager>();

        deviceEnumerator = new();
        notificationClient = new(deviceEnumerator);

        var deviceAdded = Observable.FromEventPattern<DeviceNotificationEventArgs>(
            handler => notificationClient.DeviceAdded += handler,
            handler => notificationClient.DeviceAdded -= handler)
            .Do(e => LogDeviceEvent("DeviceAdded", e.EventArgs))
            .Select(_ => Unit.Default);

        var deviceRemoved = Observable.FromEventPattern<DeviceNotificationEventArgs>(
            handler => notificationClient.DeviceRemoved += handler,
            handler => notificationClient.DeviceRemoved -= handler)
            .Do(e => LogDeviceEvent("DeviceRemoved", e.EventArgs))
            .Select(_ => Unit.Default);

        var deviceStateChanged = Observable.FromEventPattern<DeviceStateChangedEventArgs>( // Active, disabled, unplugged
            handler => notificationClient.DeviceStateChanged += handler,
            handler => notificationClient.DeviceStateChanged -= handler)
            .Do(e => LogDeviceEvent($"DeviceStateChanged ({e.EventArgs.DeviceState})", e.EventArgs))
            .Select(_ => Unit.Default);

        var deviceDescriptionChanged = Observable.FromEventPattern<DevicePropertyChangedEventArgs>( // Device name, etc.
            handler => notificationClient.DevicePropertyChanged += handler,
            handler => notificationClient.DevicePropertyChanged -= handler)
            .Where(e => e.EventArgs.PropertyKey == PKey.DeviceDescription)
            .Do(e => LogDeviceEvent("DevicePropertyChanged (DeviceDescription)", e.EventArgs))
            .Select(_ => Unit.Default);

        var playbackDevices = Observable.Merge(deviceAdded, deviceRemoved, deviceStateChanged, deviceDescriptionChanged)
            .StartWith(Unit.Default)
            .Select(_ => EnumeratePlaybackDevices()
                .Select(d => GetDeviceName(d))
                .Distinct()
                .Order()
                .ToArray())
            .DistinctUntilChanged(EqualityComparer<IEnumerable<string>>.Create(
                (a, b) => a is null ? b is null : b is not null && a.SequenceEqual(b)))
            .Do(devices => logger.Information("Playback devices: {Devices}", devices))
            .Replay(1);

        playbackDevices.Connect().DisposeWith(subscriptions);
        PlaybackDevices = playbackDevices;

        // There is also a "Console" role for system sounds and (oddly) games that's distinct from Multimedia, but it's
        // not exposed in the Windows UI, and in fact when you change one the OS automatically sets the other.
        var initialDefaultMultimediaDevice = GetDeviceName(deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        var initialDefaultCommunicationsDevice = GetDeviceName(deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications));

        defaultPlaybackDevice = new(new(initialDefaultMultimediaDevice, initialDefaultCommunicationsDevice));

        Observable.FromEventPattern<DefaultDeviceChangedEventArgs>(
            handler => notificationClient.DefaultDeviceChanged += handler,
            handler => notificationClient.DefaultDeviceChanged -= handler)
            .Where(e => e.EventArgs.DataFlow is DataFlow.Render &&
                        e.EventArgs.Role is Role.Multimedia or Role.Communications)
            .Scan(defaultPlaybackDevice.Value, (DefaultAudioDevice d, EventPattern<DefaultDeviceChangedEventArgs> e) =>
                e.EventArgs.Role is Role.Multimedia ?
                    d with { Multimedia = GetDeviceName(e.EventArgs) } :
                    d with { Communications = GetDeviceName(e.EventArgs) })
            .Throttle(TimeSpan.FromMilliseconds(20))
            .DistinctUntilChanged()
            .Do(d => logger.Information("Default playback device changed to {DefaultDevice}", d))
            .Subscribe(defaultPlaybackDevice)
            .DisposeWith(subscriptions);
    }

    /// <summary>
    /// The names of the active playback devices, sorted. Observers will receive the latest value immediately.
    /// </summary>
    public IObservable<IEnumerable<string>> PlaybackDevices { get; }

    /// <summary>
    /// The names of the active playback devices as of right now, sorted. A synchronous convenience for UI code,
    /// which needs a value to populate a control rather than a stream.
    /// </summary>
    public IReadOnlyList<string> CurrentPlaybackDeviceNames =>
        [.. EnumeratePlaybackDevices()
            .Select(GetDeviceName)
            .Distinct()
            .Order()];

    /// <summary>
    /// The names of the default multimedia and communications playback devices. Observers will receive the latest value
    /// immediately.
    /// </summary>
    public IObservable<DefaultAudioDevice> DefaultPlaybackDevice => defaultPlaybackDevice;

    /// <summary>
    /// The names of the default multimedia and communications playback devices.
    /// </summary>
    public DefaultAudioDevice CurrentDefaultPlaybackDevice => defaultPlaybackDevice.Value;

    public void SetDefaultPlaybackDevice(string name)
    {
        try
        {
            if (CurrentDefaultPlaybackDevice == name)
            {
                logger.Information("Default playback device is already \"{Name}\"", name);
                return;
            }

            MMDevice? device = EnumeratePlaybackDevices().FirstOrDefault(d => GetDeviceName(d) == name);

            if (device is null)
            {
                logger.Error("No device with name \"{Name}\"", name);
                return;
            }

            logger.Information("Switching to \"{Name}\"", name);
            device.Selected = true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to set default playback device");
        }
    }

    /// <summary>
    /// Makes <paramref name="name"/> the default playback device, then keeps checking until it sticks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MMDevice.Selected</c> is fire-and-forget: Windows applies the change asynchronously, and the notification
    /// has been observed landing anywhere from a few milliseconds to over a second later. That lag causes two
    /// distinct failures, both of which look like "the audio did not follow the window":
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// A switch that has not landed yet is indistinguishable from the steady state, so the shortcut in
    /// <see cref="SetDefaultPlaybackDevice"/> returns without doing anything, and when the in-flight change finally
    /// lands it moves the audio to the monitor the user just left.
    /// </item>
    /// <item>
    /// Something else (the Windows UI, another audio tool) changes the default after we set it.
    /// </item>
    /// </list>
    /// <para>
    /// Re-checking on a timer converges regardless of which happened, and stops once the device has held for
    /// <see cref="RequiredConsecutiveMatches"/> ticks, so a user who is not switching anything pays nothing.
    /// </para>
    /// </remarks>
    public void EnsureDefaultPlaybackDevice(string name)
    {
        if (string.Equals(pendingVerification, name, StringComparison.Ordinal))
        {
            // Already converging on this device; the running loop will get there.
            return;
        }

        SetDefaultPlaybackDevice(name);
        StartVerificationLoop(name);
    }

    /// <summary>
    /// Points the convergence loop at <paramref name="expectedName"/>, creating the interval subscription on first
    /// use.
    /// </summary>
    private void StartVerificationLoop(string expectedName)
    {
        pendingVerification = expectedName;
        pendingVerificationMatches = 0;

        verificationSubscription ??= Observable
            .Interval(VerificationInterval)
            .Subscribe(_ => VerifyPendingDevice());
    }

    /// <summary>
    /// Stops the convergence loop. Callers must hold <see cref="pendingVerification"/> consistent.
    /// </summary>
    private void StopVerificationLoop()
    {
        pendingVerification = null;
        pendingVerificationMatches = 0;
        verificationSubscription?.Dispose();
        verificationSubscription = null;
    }

    /// <summary>
    /// One tick of the convergence loop: re-apply the expected device if the default has drifted away from it, and
    /// stop once it has held for <see cref="RequiredConsecutiveMatches"/> ticks.
    /// </summary>
    private void VerifyPendingDevice()
    {
        string? expected = pendingVerification;
        if (expected is null)
        {
            return;
        }

        try
        {
            if (CurrentDefaultPlaybackDevice == expected)
            {
                if (++pendingVerificationMatches >= RequiredConsecutiveMatches)
                {
                    // Converged and holding.
                    StopVerificationLoop();
                }
                return;
            }

            // Drifted (or not landed yet); start the count over.
            pendingVerificationMatches = 0;

            MMDevice? device = EnumeratePlaybackDevices().FirstOrDefault(d => GetDeviceName(d) == expected);

            if (device is null)
            {
                // Unplugged or renamed; stop chasing it.
                StopVerificationLoop();
                return;
            }

            logger.Information(
                "Expected \"{Expected}\" but the default is {Actual}; re-applying (Windows applies device changes asynchronously).",
                expected, CurrentDefaultPlaybackDevice);

            device.Selected = true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to re-apply the default playback device");
        }
    }

    private MMDeviceCollection EnumeratePlaybackDevices() =>
        deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

    /// <summary>
    /// The active render endpoints, for callers that need to resolve a device name to its endpoint ID (per-app
    /// routing addresses devices by ID rather than by name).
    /// </summary>
    public MMDeviceCollection EnumerateActivePlaybackDevices() => EnumeratePlaybackDevices();

    private static string GetDeviceName(MMDevice device) =>
        device.Properties?[PKey.DeviceDescription]?.Value.ToString() ?? "<Unknown>";

    private static string GetDeviceName(DeviceNotificationEventArgs e) =>
        e.TryGetDevice(out MMDevice? device) ? GetDeviceName(device!) : "<Unknown>";

    private void LogDeviceEvent(string eventName, DeviceNotificationEventArgs e)
    {
        if (logger.IsEnabled(LogEventLevel.Debug))
        {
            logger.Debug("{Event}: \"{Device}\"", eventName, GetDeviceName(e));
        }
    }

    public void Dispose()
    {
        verificationSubscription?.Dispose();
        subscriptions.Dispose();
        defaultPlaybackDevice.Dispose();
    }
}
