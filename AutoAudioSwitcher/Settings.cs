// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using Serilog.Events;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoAudioSwitcher;

/// <summary>
/// How the application decides which playback device to use.
/// </summary>
internal enum AudioRoutingMode
{
    /// <summary>
    /// Follows the window the user is currently focused on: audio moves to whichever monitor holds the foreground
    /// window. Simple and predictable, and matches the original AutoAudioSwitcher behaviour.
    /// </summary>
    Global,

    /// <summary>
    /// Follows each application's own window: a process playing audio is routed to the device assigned to the
    /// monitor its window is on, regardless of what else has focus.
    /// </summary>
    PerApp,
}

internal sealed record Settings : IEquatable<Settings?>
{
    /// <summary>
    /// Which strategy to use for choosing a playback device.
    /// </summary>
    public AudioRoutingMode Mode { get; init; } = AudioRoutingMode.Global;

    /// <summary>
    /// Map of display names (as shown in the Settings app under "Advanced display settings" or similar) to playback
    /// device names (as set in the Sound control panel).
    /// </summary>
    public IReadOnlyDictionary<string, string> Monitors { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// If false, the application will remain running in the tray but auto-switching will be temporarily disabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Log level for the error log. Can be set to "Debug" to enable debug logging.
    /// </summary>
    public LogEventLevel LogLevel { get; init; } = LogEventLevel.Error;

    /// <summary>
    /// UI language as a BCP-47 tag (e.g. "zh-CN", "ja"), or an empty string to follow the operating system.
    /// Applied at startup; a change requires a restart because WinForms caches localized control text.
    /// </summary>
    public string Language { get; init; } = "";

    /// <summary>
    /// Whether to look for a newer release when the application starts. Checking is silent: the only visible
    /// effect is a tray notification when an update actually exists.
    /// </summary>
    public bool CheckForUpdatesOnStartup { get; init; } = true;

    /// <summary>
    /// Writes the current <see cref="Settings"/> to the settings file.
    /// </summary>
    public void Save()
    {
        try
        {
            string tempFile = $"{Program.SettingsFile}.tmp";

            using (FileStream file = File.Open(tempFile, FileMode.Create, FileAccess.Write))
            {
                JsonSerializer.Serialize(file, this, SettingsSerializerContext.Default.Options);
            }

            File.Move(tempFile, Program.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to save settings file \"{Path.GetFullPath(Program.SettingsFile)}\".", ex);
        }
    }

    public bool Equals(Settings? other)
    {
        return other is not null &&
               Mode == other.Mode &&
               Monitors.Count == other.Monitors.Count &&
               Monitors.All(x => other.Monitors.TryGetValue(x.Key, out var value) && value.Equals(x.Value, StringComparison.Ordinal)) &&
               Enabled == other.Enabled &&
               LogLevel == other.LogLevel &&
               Language == other.Language &&
               CheckForUpdatesOnStartup == other.CheckForUpdatesOnStartup;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Mode,
            Monitors.Count,
            Enabled,
            LogLevel,
            Language,
            CheckForUpdatesOnStartup);
    }
}

[JsonSerializable(typeof(Settings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal sealed partial class SettingsSerializerContext : JsonSerializerContext;
