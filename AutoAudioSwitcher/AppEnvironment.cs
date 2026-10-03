// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using Microsoft.Win32;
using Serilog;
using System.Diagnostics;
using System.Globalization;

namespace AutoAudioSwitcher;

/// <summary>
/// The set of UI languages the application ships translations for.
/// </summary>
/// <param name="Tag">BCP-47 tag, or an empty string meaning "follow the operating system".</param>
/// <param name="DisplayName">Name shown in the settings window.</param>
internal sealed record SupportedLanguage(string Tag, string DisplayName);

/// <summary>
/// Applies the configured UI language, and manages the "start with Windows" registry entry.
/// </summary>
internal static class AppEnvironment
{
    private const string RunKeyPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AutoAudioSwitcher";

    /// <summary>
    /// The languages offered in the settings window. The empty tag is "follow system", which lets the
    /// ResourceManager do its normal parent-culture fallback.
    /// </summary>
    public static IReadOnlyList<SupportedLanguage> Languages { get; } =
    [
        new("", "System"),
        new("en", "English"),
        new("zh-CN", "简体中文"),
        new("ja", "日本語"),
    ];

    /// <summary>
    /// Sets <see cref="CultureInfo"/> for the process from the configured tag.
    /// </summary>
    /// <remarks>
    /// This must run before any localized text is read. WinForms caches the text of a control at the moment it is
    /// created, so changing the culture later would leave already-constructed forms in the old language — which is
    /// why the settings window tells the user a restart is needed.
    /// </remarks>
    public static void ApplyLanguage(string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return; // Follow the OS: leave the default culture resolution alone.
        }

        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(tag);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // A stale or hand-edited tag; fall back to the OS language rather than crashing at startup.
        }
    }

    /// <summary>
    /// Whether the application is currently registered to start with Windows.
    /// </summary>
    public static bool IsStartWithWindowsEnabled()
    {
        try
        {
            return Registry.GetValue(RunKeyPath, RunValueName, null) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds or removes the "start with Windows" registry entry for the current executable.
    /// </summary>
    /// <returns>True on success.</returns>
    public static bool SetStartWithWindows(bool enabled, ILogger logger)
    {
        try
        {
            if (!enabled)
            {
                Registry.CurrentUser.DeleteValue($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run\{RunValueName}", throwOnMissingValue: false);
                return true;
            }

            // Quoted, because the install path may contain spaces. HKCU\...\Run needs no elevation, which is why
            // the installer is also per-user (PrivilegesRequired=lowest).
            string executable = Environment.ProcessPath ?? Application.ExecutablePath;
            Registry.SetValue(RunKeyPath, RunValueName, $"\"{executable}\"");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to set the start-with-Windows entry.");
            return false;
        }
    }

    /// <summary>
    /// Opens a URL or a directory in the user's default handler.
    /// </summary>
    public static void OpenExternally(string target, ILogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to open {Target}.", target);
        }
    }
}
