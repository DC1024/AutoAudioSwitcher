// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using CoreAudio;
using Serilog;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace AutoAudioSwitcher;

/// <summary>
/// An audio session that is, or was recently, producing sound.
/// </summary>
/// <param name="ProcessId">The owning process. Zero means the system sounds session.</param>
/// <param name="ProcessName">Executable name without extension, for logging and display.</param>
/// <param name="State">Whether the session is currently active.</param>
internal sealed record AudioSession(uint ProcessId, string ProcessName, AudioSessionState State);

/// <summary>
/// Enumerates the audio sessions Windows is currently tracking and emits the set whenever it changes.
/// </summary>
/// <remarks>
/// <para>
/// Windows groups audio sessions by process, not by window. That means two browser windows playing sound from the
/// same <c>chrome.exe</c> share one session and therefore one routing decision — this is a limitation of the Core
/// Audio API, not of this application. To route two videos to two different outputs, run the second player as a
/// separate process (for Chrome, a second instance with its own <c>--user-data-dir</c>; VLC or mpv also work well).
/// </para>
/// <para>
/// There is no simple "audio session changed" event. Rather than subscribe to per-session COM callbacks (which must
/// be registered against every session as it appears and are easy to leak), we poll. Audio sessions change slowly —
/// an app opens one stream and keeps it for the duration of playback — so a poll on the order of a second is
/// responsive enough and far more robust.
/// </para>
/// </remarks>
internal sealed class AudioSessionMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly BehaviorSubject<AudioSession[]> subject = new([]);
    private readonly MMDeviceEnumerator deviceEnumerator = new();
    private readonly ILogger logger;
    private readonly IDisposable pollSubscription;
    private bool isDisposed;

    public AudioSessionMonitor(ILogger logger)
    {
        this.logger = logger.ForContext<AudioSessionMonitor>();

        // StartWith so the first poll happens immediately rather than after one interval.
        pollSubscription = Observable.Interval(PollInterval)
            .StartWith(0L)
            .Select(_ => GetSessions())
            .DistinctUntilChanged(EqualityComparer<AudioSession[]>.Create(SequenceEquals))
            .Subscribe(subject);
    }

    /// <summary>
    /// The current set of audio sessions, ordered by process ID for stable comparison. Observers receive the
    /// latest value immediately.
    /// </summary>
    public IObservable<AudioSession[]> Sessions => subject;

    /// <summary>
    /// The current set of audio sessions.
    /// </summary>
    public AudioSession[] CurrentSessions => subject.Value;

    private AudioSession[] GetSessions()
    {
        try
        {
            // The session manager hangs off a render endpoint; any active one will do, since Windows reports the
            // same session list regardless of which endpoint you ask.
            MMDevice? device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device is null)
            {
                return [];
            }

            using AudioSessionManager2? manager = device.AudioSessionManager2;
            if (manager is null)
            {
                return [];
            }

            var sessions = new List<AudioSession>();

            SessionCollection? collection = manager.Sessions;
            if (collection is null)
            {
                return [];
            }

            foreach (AudioSessionControl2 session in collection)
            {
                using (session)
                {
                    uint pid = session.ProcessID;

                    // PID 0 is the system sounds pseudo-session; it has no window and no routable identity.
                    if (pid == 0)
                    {
                        continue;
                    }

                    if (!TryGetProcessName(pid, out string processName))
                    {
                        // The process exited between enumeration and this lookup.
                        continue;
                    }

                    sessions.Add(new AudioSession(pid, processName, session.State));
                }
            }

            sessions.Sort(static (a, b) => a.ProcessId.CompareTo(b.ProcessId));
            return [.. sessions];
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Failed to enumerate audio sessions.");
            return [];
        }
    }

    private static bool TryGetProcessName(uint processId, out string processName)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            return true;
        }
        catch (ArgumentException)
        {
            // Thrown when no process with that PID is running any more.
            processName = "";
            return false;
        }
    }

    private static bool SequenceEquals(AudioSession[]? a, AudioSession[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        pollSubscription.Dispose();
        subject.Dispose();
    }
}