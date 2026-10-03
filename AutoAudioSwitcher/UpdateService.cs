// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using Serilog;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoAudioSwitcher;

/// <summary>
/// The result of looking for a newer release.
/// </summary>
/// <param name="Status">What was found.</param>
/// <param name="Version">The newer version, when one exists.</param>
/// <param name="DownloadUrl">Direct link to the asset to install.</param>
/// <param name="AssetName">File name of the asset, used for the temporary download path.</param>
/// <param name="ReleaseNotes">Release body text, shown to the user.</param>
/// <param name="ReleasePageUrl">HTML page to open for full details.</param>
/// <param name="Error">Human-readable failure reason when <see cref="UpdateStatus.Failed"/>.</param>
internal sealed record UpdateCheckResult(
    UpdateStatus Status,
    Version? Version = null,
    string? DownloadUrl = null,
    string? AssetName = null,
    string? ReleaseNotes = null,
    string? ReleasePageUrl = null,
    string? Error = null);

internal enum UpdateStatus
{
    /// <summary>A newer release is available.</summary>
    Available,

    /// <summary>Running the latest release.</summary>
    UpToDate,

    /// <summary>The running build can't replace itself (e.g. launched from a dev path).</summary>
    NotSupported,

    /// <summary>Network or API error.</summary>
    Failed,
}

/// <summary>
/// Checks GitHub Releases for a newer build and, on request, downloads and installs it.
/// </summary>
/// <remarks>
/// <para>
/// A running executable cannot overwrite itself on Windows — the file is locked for the lifetime of the process.
/// The standard workaround, and what this class does, is to hand the swap off to a detached <c>cmd.exe</c> that
/// waits for this process to exit, moves the new file into place, and relaunches. That script must live outside
/// the install directory so replacing the install directory's contents can't clobber it mid-run.
/// </para>
/// <para>
/// Replacing the portable build "in place" only works when this is a single-file publish, because that is the
/// only case where the running image is just <c>AutoAudioSwitcher.exe</c> with no dependencies beside it. When
/// running from a framework-dependent build (as during development), the check is still performed but the
/// install step is refused with <see cref="UpdateStatus.NotSupported"/>.
/// </para>
/// </remarks>
internal sealed class UpdateService : IDisposable
{
    /// <summary>
    /// Where releases are published. Overridable at build time via <c>UpdateRepository</c> so a fork can point
    /// at its own repository without editing code.
    /// </summary>
    private const string DefaultRepository = "maxkagamine/AutoAudioSwitcher";

    /// <summary>Name of the portable single-file asset attached to each release.</summary>
    private const string PortableAssetName = "AutoAudioSwitcher.exe";

    /// <summary>GitHub requires a User-Agent on API requests and rejects requests without one.</summary>
    private const string UserAgent = "AutoAudioSwitcher";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;
    private readonly ILogger logger;

    public UpdateService(ILogger logger)
    {
        this.logger = logger.ForContext<UpdateService>();

        http = new HttpClient { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, GetCurrentVersion().ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// The repository releases are fetched from.
    /// </summary>
    public static string Repository =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value is { Length: > 0 } configured
            ? configured
            : DefaultRepository;

    /// <summary>
    /// The informational version of the running build, with any build metadata (+sha) stripped.
    /// </summary>
    public static Version GetCurrentVersion()
    {
        string? informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            int plusIndex = informational.IndexOf('+');
            if (plusIndex >= 0)
            {
                informational = informational[..plusIndex];
            }

            if (Version.TryParse(informational, out Version? parsed))
            {
                return parsed;
            }
        }

        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    }

    /// <summary>
    /// Whether this build can replace itself. True only for single-file publishes, where the running image is a
    /// lone <c>.exe</c>; a framework-dependent build has <c>.dll</c> and <c>.deps.json</c> siblings that would be
    /// left behind and then shadow the new executable.
    /// </summary>
    public static bool CanSelfUpdate
    {
        get
        {
            string executable = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(executable) || !Path.GetFileName(executable).Equals("AutoAudioSwitcher.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string directory = Path.GetDirectoryName(executable) ?? "";
            return !File.Exists(Path.Combine(directory, "AutoAudioSwitcher.dll"));
        }
    }

    /// <summary>
    /// Fetches the latest release and compares it with the running version.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        Version current = GetCurrentVersion();

        try
        {
            using HttpResponseMessage response = await http.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.Warning("Update check returned HTTP {Status}.", (int)response.StatusCode);
                return new(UpdateStatus.Failed, Error: $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            GitHubRelease? release = await JsonSerializer.DeserializeAsync(
                stream, UpdateJsonContext.Default.GitHubRelease, cancellationToken);

            if (release is null)
            {
                return new(UpdateStatus.Failed, Error: "Empty response from GitHub.");
            }

            if (!TryParseTag(release.TagName, out Version? latest))
            {
                logger.Warning("Could not parse release tag {Tag}.", release.TagName);
                return new(UpdateStatus.Failed, Error: $"Unrecognized release tag \"{release.TagName}\".");
            }

            logger.Information("Latest release is {Latest}; running {Current}.", latest, current);

            if (latest <= current)
            {
                return new(UpdateStatus.UpToDate, latest, ReleasePageUrl: release.HtmlUrl);
            }

            GitHubAsset? asset = release.Assets?.FirstOrDefault(a =>
                a.Name.Equals(PortableAssetName, StringComparison.OrdinalIgnoreCase));

            if (asset is null)
            {
                // The release exists but has no portable build attached; point the user at the page instead.
                logger.Warning("Release {Tag} has no {Asset} asset.", release.TagName, PortableAssetName);
                return new(UpdateStatus.NotSupported, latest, ReleasePageUrl: release.HtmlUrl,
                    ReleaseNotes: release.Body, Error: $"Release {latest} has no {PortableAssetName} asset.");
            }

            return new(
                UpdateStatus.Available,
                latest,
                asset.BrowserDownloadUrl,
                asset.Name,
                release.Body,
                release.HtmlUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Update check failed.");
            return new(UpdateStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Downloads <paramref name="result"/>'s asset and schedules the swap-and-restart.
    /// </summary>
    /// <param name="result">A result previously returned by <see cref="CheckAsync"/>.</param>
    /// <param name="progress">Optional progress callback (bytes received, total bytes or -1).</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The path of the downloaded file, or null on failure.</returns>
    public async Task<string?> DownloadAsync(
        UpdateCheckResult result,
        IProgress<(long Received, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (result.Status is not UpdateStatus.Available || result.DownloadUrl is null)
        {
            return null;
        }

        string target = Path.Combine(Path.GetTempPath(), result.AssetName ?? PortableAssetName);

        try
        {
            using HttpResponseMessage response = await http.GetAsync(
                result.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            long received = 0;

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream destination = File.Create(target))
            {
                byte[] buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report((received, total));
                }
            }

            logger.Information("Downloaded {Version} to {Path} ({Bytes} bytes).", result.Version, target, received);
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error(ex, "Failed to download the update.");
            TryDelete(target);
            return null;
        }
    }

    /// <summary>
    /// Launches a detached helper that waits for this process to exit, swaps in <paramref name="downloadedFile"/>,
    /// and restarts the application. The caller should exit immediately afterwards.
    /// </summary>
    /// <returns>True if the helper was started.</returns>
    public bool ScheduleInstallAndRestart(string downloadedFile)
    {
        string executable = Environment.ProcessPath ?? "";

        if (!CanSelfUpdate)
        {
            logger.Warning("Refusing to self-update: not a single-file publish ({Path}).", executable);
            return false;
        }

        try
        {
            // The script must not live in the install directory: the swap replaces that directory's contents, and
            // a script running from inside it could be deleted out from under cmd.exe mid-execution.
            string scriptPath = Path.Combine(Path.GetTempPath(), $"aas-update-{Guid.NewGuid():N}.cmd");
            int processId = Environment.ProcessId;

            // Ping localhost with a timeout as a sleep that doesn't need another process; repeat until this PID is
            // gone or we give up (~60s), so a slow shutdown can't leave the file swap racing the old process.
            string script = $"""
                @echo off
                setlocal
                set "TARGET={executable}"
                set "SOURCE={downloadedFile}"
                set /a TRIES=0
                :waitloop
                tasklist /FI "PID eq {processId}" 2>nul | find "{processId}" >nul
                if errorlevel 1 goto swap
                set /a TRIES+=1
                if %TRIES% GEQ 240 goto swap
                ping -n 2 127.0.0.1 >nul
                goto waitloop
                :swap
                move /Y "%SOURCE%" "%TARGET%" >nul 2>&1
                if errorlevel 1 (
                    echo Update failed: could not replace "%TARGET%".
                    timeout /t 10 >nul
                    exit /b 1
                )
                start "" "%TARGET%"
                (goto) 2>nul & del "%~f0"
                """;

            File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(false));

            ProcessStartInfo startInfo = new("cmd.exe")
            {
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using Process? helper = Process.Start(startInfo);
            if (helper is null)
            {
                logger.Error("Could not start the update helper process.");
                TryDelete(scriptPath);
                return false;
            }

            logger.Information("Update helper started; restarting to install {File}.", downloadedFile);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to schedule the update.");
            return false;
        }
    }

    /// <summary>
    /// Extracts a version from a release tag such as <c>v1.2.3</c>.
    /// </summary>
    private static bool TryParseTag(string? tag, out Version? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        string trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        // Tolerate a pre-release suffix ("1.2.3-beta.1") by keeping only the numeric prefix.
        int dashIndex = trimmed.IndexOf('-');
        if (dashIndex > 0)
        {
            trimmed = trimmed[..dashIndex];
        }

        if (!Version.TryParse(trimmed, out Version? parsed))
        {
            return false;
        }

        // Normalize 1.2 to 1.2.0 so comparisons against a three-part assembly version behave.
        version = parsed.Revision < 0
            ? (parsed.Build < 0 ? new Version(parsed.Major, parsed.Minor, 0) : new Version(parsed.Major, parsed.Minor, parsed.Build))
            : parsed;

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }

    public void Dispose() => http.Dispose();
}

/// <summary>
/// The subset of the GitHub releases payload we need.
/// </summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
