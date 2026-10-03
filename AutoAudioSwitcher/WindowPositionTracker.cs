// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using Serilog;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using static Windows.Win32.PInvoke;
using static Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS;
using static Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD;
using static Windows.Win32.Graphics.Gdi.MONITOR_FROM_FLAGS;

namespace AutoAudioSwitcher;

/// <summary>
/// Works out which monitor a given process's window is on, remembering where a window was last seen so that a
/// minimized or hidden window still resolves to the monitor the user put it on.
/// </summary>
/// <remarks>
/// <para>
/// The reason this needs a memory at all: when a window is minimized or hidden, <c>GetWindowRect</c> returns a
/// degenerate rectangle (classically <c>-32000, -32000</c>) that belongs to no monitor. Without remembering the
/// last real position, audio would jump back to the primary device the moment the user minimized their player —
/// exactly the behaviour this feature exists to avoid.
/// </para>
/// <para>
/// Remembered positions are persisted to <c>positions.json</c> next to the executable so the mapping survives a
/// restart, and are keyed by <c>process name + window title</c> rather than PID, since PIDs are recycled every time
/// an application is relaunched.
/// </para>
/// </remarks>
internal sealed class WindowPositionTracker : IDisposable
{
    public const string PositionsFile = "positions.json";

    /// <summary>
    /// A window rectangle is considered "real" only if it is at least this large. Minimized windows report a
    /// negative-size or off-screen rectangle, and a stray 1x1 window is never a meaningful audio target.
    /// </summary>
    private const int MinimumWindowDimension = 32;

    private readonly Dictionary<string, string> lastKnownMonitor;
    private readonly ILogger logger;
    private readonly object sync = new();

    public WindowPositionTracker(ILogger logger)
    {
        this.logger = logger.ForContext<WindowPositionTracker>();
        lastKnownMonitor = Load();
    }

    /// <summary>
    /// Returns the GDI device name of the monitor that <paramref name="processId"/>'s window is on, falling back to
    /// the last monitor the window was seen on. Returns null when the process has no top-level window at all.
    /// </summary>
    public unsafe string? GetMonitorDeviceName(uint processId, string processName)
    {
        HWND window = FindMainWindow(processId);
        if (window.IsNull)
        {
            logger.Verbose("Process {ProcessId} ({ProcessName}) has no top-level window.", processId, processName);
            return null;
        }

        // A minimized window's rectangle is not usable, so remember-and-recall instead of trusting it.
        RECT rect = default;
        bool haveUsableRect = !IsIconic(window)
                              && GetWindowRect(window, out rect)
                              && IsUsableRect(rect);

        if (!haveUsableRect)
        {
            string key = GetCacheKey(window, processName);

            lock (sync)
            {
                if (lastKnownMonitor.TryGetValue(key, out string? remembered))
                {
                    logger.Verbose("Window {Window} of {ProcessName} is not visible; using remembered monitor {Monitor}.",
                        window, processName, remembered);
                    return remembered;
                }
            }

            logger.Verbose("Window {Window} of {ProcessName} is not visible and has no remembered monitor.",
                window, processName);
            return null;
        }

        // MonitorFromPoint needs a point; the window's top-left corner is a reasonable representative and is what
        // MonitorFromWindow effectively uses when a window straddles two displays. CsWin32 exposes a friendly
        // overload taking System.Drawing.Point.
        HMONITOR monitorHandle = MonitorFromPoint(
            new System.Drawing.Point(rect.left, rect.top),
            MONITOR_DEFAULTTONULL);

        if (monitorHandle == 0)
        {
            logger.Verbose("No monitor contains the top-left corner of window {Window}.", window);
            return null;
        }

        MONITORINFOEXW monitorInfo = new()
        {
            monitorInfo = new() { cbSize = (uint)sizeof(MONITORINFOEXW) }
        };

        if (!GetMonitorInfo(monitorHandle, (MONITORINFO*)&monitorInfo))
        {
            logger.Warning("GetMonitorInfo failed: {Message}", Marshal.GetLastPInvokeErrorMessage());
            return null;
        }

        string gdiDeviceName = monitorInfo.szDevice.ToString();
        Remember(GetCacheKey(window, processName), gdiDeviceName);
        return gdiDeviceName;
    }

    private static bool IsUsableRect(RECT rect) =>
        rect.right - rect.left >= MinimumWindowDimension &&
        rect.bottom - rect.top >= MinimumWindowDimension;

    /// <summary>
    /// Finds the process's main window. A process can own any number of top-level windows, so we track the best
    /// candidates as we walk the whole desktop: an ordinary usable window wins, then any titled window, then
    /// anything at all.
    /// </summary>
    private static unsafe HWND FindMainWindow(uint processId)
    {
        HWND bestUsable = HWND.Null;
        HWND bestTitled = HWND.Null;
        HWND bestAny = HWND.Null;

        // EnumWindows has no early-exit that cooperates with a lambda, so we scan the whole desktop and pick
        // afterwards. This runs once per routing decision (a handful of windows), so the cost is irrelevant.
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint windowProcessId);
            if (windowProcessId != processId)
            {
                return true;
            }

            // Owned popups and tool windows are not what the user dragged; prefer the root owner.
            HWND root = GetAncestor(hwnd, GA_ROOTOWNER);
            if (!root.IsNull)
            {
                hwnd = root;
            }

            if (bestAny.IsNull)
            {
                bestAny = hwnd;
            }

            bool isTitled = GetWindowTextLength(hwnd) > 0;

            if (bestTitled.IsNull && isTitled)
            {
                bestTitled = hwnd;
            }

            if (bestUsable.IsNull && isTitled && IsWindowVisible(hwnd) && !IsIconic(hwnd))
            {
                bestUsable = hwnd;
            }

            return true;
        }, 0);

        if (!bestUsable.IsNull) return bestUsable;
        if (!bestTitled.IsNull) return bestTitled;
        return bestAny;
    }

    private static string GetCacheKey(HWND window, string processName)
    {
        // Title disambiguates multiple windows of one process (e.g. two browser windows). It can change as the user
        // browses, so the process name alone is the fallback identity.
        string title = GetWindowTextSafe(window);
        return string.IsNullOrEmpty(title) ? processName : $"{processName}\u0000{title}";
    }

    private static unsafe string GetWindowTextSafe(HWND window)
    {
        try
        {
            int length = GetWindowTextLength(window);
            if (length <= 0)
            {
                return "";
            }

            // Cap the buffer: some windows (and some misbehaving apps) report absurd title lengths.
            int capacity = Math.Min(length, 512) + 1;
            Span<char> buffer = capacity <= 256 ? stackalloc char[capacity] : new char[capacity];

            fixed (char* p = buffer)
            {
                int copied = GetWindowText(window, p, buffer.Length);
                return copied <= 0 ? "" : new string(p, 0, copied);
            }
        }
        catch
        {
            return "";
        }
    }

    private void Remember(string key, string gdiDeviceName)
    {
        lock (sync)
        {
            if (lastKnownMonitor.TryGetValue(key, out string? existing) && existing == gdiDeviceName)
            {
                return;
            }

            lastKnownMonitor[key] = gdiDeviceName;
        }

        Save();
    }

    /// <summary>
    /// Drops remembered positions for windows that no longer exist, so the file does not grow without bound.
    /// </summary>
    public void Prune(IEnumerable<(string ProcessName, string Title)> liveWindows)
    {
        var live = new HashSet<string>(
            liveWindows.Select(w => string.IsNullOrEmpty(w.Title)
                ? w.ProcessName
                : $"{w.ProcessName}\u0000{w.Title}"));

        lock (sync)
        {
            string[] stale = [.. lastKnownMonitor.Keys.Where(k => !live.Contains(k))];
            if (stale.Length == 0)
            {
                return;
            }

            foreach (string key in stale)
            {
                lastKnownMonitor.Remove(key);
            }

            logger.Debug("Pruned {Count} stale window position entries.", stale.Length);
        }

        Save();
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(PositionsFile))
            {
                return [];
            }

            using FileStream file = File.OpenRead(PositionsFile);
            return JsonSerializer.Deserialize(file, WindowPositionContext.Default.DictionaryStringString) ?? [];
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not read {File}; starting with no remembered window positions.", PositionsFile);
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Dictionary<string, string> snapshot;
            lock (sync)
            {
                snapshot = new(lastKnownMonitor);
            }

            string tempFile = $"{PositionsFile}.tmp";
            using (FileStream file = File.Open(tempFile, FileMode.Create, FileAccess.Write))
            {
                JsonSerializer.Serialize(file, snapshot, WindowPositionContext.Default.DictionaryStringString);
            }

            File.Move(tempFile, PositionsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not save {File}.", PositionsFile);
        }
    }

    /// <summary>
    /// Flushes any pending positions. Nothing is held open between calls, so this exists mainly to make the
    /// lifetime explicit to the DI container.
    /// </summary>
    public void Dispose() => Save();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class WindowPositionContext : JsonSerializerContext;
