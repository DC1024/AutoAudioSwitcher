// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using AutoAudioSwitcher.Properties;
using Serilog;
using Serilog.Events;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text;

namespace AutoAudioSwitcher;

/// <summary>
/// The graphical settings window, opened from the tray menu.
/// </summary>
/// <remarks>
/// <para>
/// The window is built entirely in code rather than with the WinForms designer. Two reasons: the rest of this
/// project has no designer files, so keeping one code path is simpler; and localized text is assigned as each
/// control is constructed, which would otherwise fight the designer's resource machinery.
/// </para>
/// <para>
/// Nothing is written to disk until the user presses OK or Apply. Checkboxes and combo boxes only mutate the
/// pending <see cref="Settings"/> record, so Cancel is a genuine no-op.
/// </para>
/// </remarks>
[SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "Every control field is added to a parent container's Controls collection, which disposes it " +
                    "when the form is disposed. The analyzer cannot see through Controls.Add.")]
internal sealed class SettingsForm : Form
{
    /// <summary>
    /// Placeholders are formatted with <see cref="CultureInfo.InvariantCulture"/>: the substituted values are a
    /// version number, a device name and an error string, none of which should follow the UI locale.
    /// </summary>
    private static readonly CompositeFormat CurrentVersionFormat = CompositeFormat.Parse(Resources.CurrentVersion);
    private static readonly CompositeFormat UpdateAvailableFormat = CompositeFormat.Parse(Resources.UpdateAvailable);
    private static readonly CompositeFormat UpdateDownloadingFormat = CompositeFormat.Parse(Resources.UpdateDownloading);
    private static readonly CompositeFormat UpdateFailedFormat = CompositeFormat.Parse(Resources.UpdateFailed);
    private static readonly CompositeFormat PlaybackDeviceNotFoundFormat = CompositeFormat.Parse(Resources.PlaybackDeviceNotFound);

    private readonly IBehaviorObservable<Settings> settings;
    private readonly ConnectedMonitorsMonitor connectedMonitorsMonitor;
    private readonly AudioDeviceManager audioDeviceManager;
    private readonly ProcessAudioPolicyConfig policyConfig;
    private readonly UpdateService updateService;
    private readonly ILogger logger;

    private Settings pending;

    // General page
    private RadioButton globalModeRadio = null!;
    private RadioButton perAppModeRadio = null!;
    private Label perAppHint = null!;
    private Label perAppUnavailableLabel = null!;
    private CheckBox enabledCheckBox = null!;
    private CheckBox startWithWindowsCheckBox = null!;
    private ComboBox languageComboBox = null!;
    private ComboBox logLevelComboBox = null!;

    // Monitors page
    private DataGridView monitorGrid = null!;

    // Updates page
    private CheckBox checkOnStartupCheckBox = null!;
    private Button checkNowButton = null!;
    private Label currentVersionLabel = null!;
    private Label updateStatusLabel = null!;
    private ProgressBar updateProgressBar = null!;
    private TextBox releaseNotesTextBox = null!;
    private Label releaseNotesLabel = null!;
    private Button openReleasesButton = null!;

    private Button okButton = null!;
    private Button cancelButton = null!;
    private Button applyButton = null!;

    private UpdateCheckResult? lastCheckResult;
    private CancellationTokenSource? checkCancellation;

    public SettingsForm(
        IBehaviorObservable<Settings> settings,
        ConnectedMonitorsMonitor connectedMonitorsMonitor,
        AudioDeviceManager audioDeviceManager,
        ProcessAudioPolicyConfig policyConfig,
        UpdateService updateService,
        ILogger logger)
    {
        this.settings = settings;
        this.connectedMonitorsMonitor = connectedMonitorsMonitor;
        this.audioDeviceManager = audioDeviceManager;
        this.policyConfig = policyConfig;
        this.updateService = updateService;
        this.logger = logger.ForContext<SettingsForm>();

        pending = settings.Value;

        BuildWindow();

        LoadFromSettings(pending);

        // A monitor can be plugged in or unplugged while the window is open; keep the grid honest.
        connectedMonitorsMonitor.ConnectedMonitors
            .Skip(1)
            .Subscribe(_ => PopulateMonitorGrid())
            .DisposeWith(formSubscriptions);
    }

    private readonly CompositeDisposable formSubscriptions = [];

    private void BuildWindow()
    {
        Text = Resources.SettingsTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 440);
        Icon = Program.IsSystemDarkModeEnabled() ? Resources.TrayIconLight : Resources.TrayIconDark;

        TabControl tabs = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 8),
        };

        tabs.TabPages.Add(BuildGeneralPage());
        tabs.TabPages.Add(BuildMonitorsPage());
        tabs.TabPages.Add(BuildUpdatesPage());
        tabs.TabPages.Add(BuildAboutPage());

        okButton = new Button
        {
            Text = Resources.Ok,
            DialogResult = DialogResult.None,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(96, 30),
            Margin = new Padding(6, 0, 0, 0),
        };
        okButton.Click += OnOkClicked;

        cancelButton = new Button
        {
            Text = Resources.Cancel,
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(96, 30),
            Margin = new Padding(6, 0, 0, 0),
        };

        applyButton = new Button
        {
            Text = Resources.Apply,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(96, 30),
            Margin = new Padding(6, 0, 0, 0),
        };
        applyButton.Click += OnApplyClicked;

        FlowLayoutPanel buttonBar = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
        };
        buttonBar.Controls.AddRange([okButton, cancelButton, applyButton]);

        Controls.Add(tabs);
        Controls.Add(buttonBar);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private TabPage BuildGeneralPage()
    {
        TabPage page = new(Resources.General) { Padding = new Padding(14), UseVisualStyleBackColor = true };

        GroupBox modeGroup = new()
        {
            Text = Resources.RoutingModeGroup,
            Location = new Point(14, 14),
            Size = new Size(500, 176),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        globalModeRadio = new RadioButton
        {
            Text = Resources.RoutingModeGlobal,
            Location = new Point(14, 26),
            AutoSize = true,
        };
        globalModeRadio.CheckedChanged += (_, _) => SyncModeFromRadios();

        Label globalHint = new()
        {
            Text = Resources.RoutingModeGlobalDescription,
            Location = new Point(34, 48),
            Size = new Size(452, 34),
            ForeColor = SystemColors.GrayText,
        };

        perAppModeRadio = new RadioButton
        {
            Text = Resources.RoutingModePerApp,
            Location = new Point(14, 90),
            AutoSize = true,
        };
        perAppModeRadio.CheckedChanged += (_, _) => SyncModeFromRadios();

        perAppHint = new Label
        {
            Text = Resources.RoutingModePerAppDescription,
            Location = new Point(34, 112),
            AutoSize = true,
            MaximumSize = new Size(452, 0),
            ForeColor = SystemColors.GrayText,
        };

        // Mutually exclusive with perAppHint: exactly one of the two is ever visible, so sharing an origin is
        // intentional, not an overlap. AutoSize + MaximumSize keeps long zh-CN/ja strings from being clipped.
        perAppUnavailableLabel = new Label
        {
            Text = Resources.RoutingModePerAppUnavailable,
            Location = new Point(34, 112),
            AutoSize = true,
            MaximumSize = new Size(452, 0),
            ForeColor = Color.Firebrick,
            Visible = false,
        };

        modeGroup.Controls.AddRange([globalModeRadio, globalHint, perAppModeRadio, perAppHint, perAppUnavailableLabel]);

        enabledCheckBox = new CheckBox
        {
            Text = Resources.EnableSwitching,
            Location = new Point(18, 204),
            AutoSize = true,
        };
        enabledCheckBox.CheckedChanged += (_, _) => pending = pending with { Enabled = enabledCheckBox.Checked };

        startWithWindowsCheckBox = new CheckBox
        {
            Text = Resources.StartWithWindows,
            Location = new Point(18, 230),
            AutoSize = true,
        };

        Label languageLabel = new()
        {
            Text = Resources.Language,
            Location = new Point(18, 262),
            AutoSize = true,
        };

        languageComboBox = new ComboBox
        {
            Location = new Point(140, 258),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        languageComboBox.Items.AddRange([.. AppEnvironment.Languages.Select(l => (object)l.DisplayName)]);
        languageComboBox.SelectedIndexChanged += (_, _) =>
        {
            if (languageComboBox.SelectedIndex >= 0)
            {
                pending = pending with { Language = AppEnvironment.Languages[languageComboBox.SelectedIndex].Tag };
            }
        };

        Label restartHint = new()
        {
            Text = Resources.LanguageRestartRequired,
            Location = new Point(348, 262),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        Label logLevelLabel = new()
        {
            Text = Resources.LogLevel,
            Location = new Point(18, 296),
            AutoSize = true,
        };

        logLevelComboBox = new ComboBox
        {
            Location = new Point(140, 292),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        logLevelComboBox.Items.AddRange(
        [
            (object)Resources.LogLevelDebug,
            Resources.LogLevelInformation,
            Resources.LogLevelWarning,
            Resources.LogLevelError,
        ]);
        logLevelComboBox.SelectedIndexChanged += (_, _) =>
        {
            if (logLevelComboBox.SelectedIndex >= 0)
            {
                pending = pending with { LogLevel = LogLevels[logLevelComboBox.SelectedIndex] };
            }
        };

        Button openLogsButton = new()
        {
            Text = Resources.OpenLogDirectoryButton,
            Location = new Point(352, 290),
            AutoSize = true,
        };
        openLogsButton.Click += (_, _) => AppEnvironment.OpenExternally(Program.LogsDirectory, logger);

        page.Controls.AddRange(
        [
            modeGroup,
            enabledCheckBox,
            startWithWindowsCheckBox,
            languageLabel,
            languageComboBox,
            restartHint,
            logLevelLabel,
            logLevelComboBox,
            openLogsButton,
        ]);

        return page;
    }

    /// <summary>Maps the log level combo box index to a Serilog level. Order must match the item order above.</summary>
    private static readonly LogEventLevel[] LogLevels =
    [
        LogEventLevel.Debug,
        LogEventLevel.Information,
        LogEventLevel.Warning,
        LogEventLevel.Error,
    ];

    private TabPage BuildMonitorsPage()
    {
        TabPage page = new(Resources.Monitors) { Padding = new Padding(14), UseVisualStyleBackColor = true };

        Label description = new()
        {
            Text = Resources.MonitorMappingDescription,
            Location = new Point(14, 14),
            Size = new Size(500, 34),
            ForeColor = SystemColors.GrayText,
        };

        monitorGrid = new DataGridView
        {
            Location = new Point(14, 54),
            Size = new Size(500, 300),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditOnEnter,
            BackgroundColor = SystemColors.Window,
        };

        monitorGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Monitor",
            HeaderText = Resources.ColumnMonitor,
            ReadOnly = true,
            FillWeight = 38,
        });

        DataGridViewComboBoxColumn deviceColumn = new()
        {
            Name = "Device",
            HeaderText = Resources.ColumnPlaybackDevice,
            FlatStyle = FlatStyle.Flat,
            FillWeight = 62,
        };
        monitorGrid.Columns.Add(deviceColumn);

        // A combo box cell commits its value only when the edit is pushed; without this, clicking straight to OK
        // would silently drop an in-progress selection.
        monitorGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (monitorGrid.IsCurrentCellDirty)
            {
                monitorGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        monitorGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == deviceColumn.Index)
            {
                CommitMonitorRow(e.RowIndex);
            }
        };

        page.Controls.AddRange([description, monitorGrid]);

        PopulateMonitorGrid();

        return page;
    }

    private TabPage BuildUpdatesPage()
    {
        TabPage page = new(Resources.Updates) { Padding = new Padding(14), UseVisualStyleBackColor = true };

        checkOnStartupCheckBox = new CheckBox
        {
            Text = Resources.CheckForUpdatesOnStartup,
            Location = new Point(18, 18),
            AutoSize = true,
        };
        checkOnStartupCheckBox.CheckedChanged += (_, _) =>
            pending = pending with { CheckForUpdatesOnStartup = checkOnStartupCheckBox.Checked };

        currentVersionLabel = new Label
        {
            Location = new Point(18, 48),
            AutoSize = true,
        };

        checkNowButton = new Button
        {
            Text = Resources.CheckNow,
            Location = new Point(18, 76),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(110, 30),
        };
        checkNowButton.Click += async (_, _) => await CheckForUpdatesAsync(userInitiated: true);

        openReleasesButton = new Button
        {
            Text = Resources.OpenReleasesPage,
            Location = new Point(140, 76),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(140, 30),
            Enabled = false,
        };
        openReleasesButton.Click += (_, _) => OnOpenReleasesClicked();

        updateStatusLabel = new Label
        {
            Location = new Point(18, 118),
            Size = new Size(500, 36),
        };

        updateProgressBar = new ProgressBar
        {
            Location = new Point(18, 158),
            Size = new Size(500, 22),
            Style = ProgressBarStyle.Continuous,
            Visible = false,
        };

        releaseNotesLabel = new Label
        {
            Text = Resources.UpdateReleaseNotes,
            Location = new Point(18, 192),
            AutoSize = true,
            Visible = false,
        };

        releaseNotesTextBox = new TextBox
        {
            Location = new Point(18, 214),
            Size = new Size(500, 150),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Visible = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = SystemColors.Window,
        };

        page.Controls.AddRange(
        [
            checkOnStartupCheckBox,
            currentVersionLabel,
            checkNowButton,
            openReleasesButton,
            updateStatusLabel,
            updateProgressBar,
            releaseNotesLabel,
            releaseNotesTextBox,
        ]);

        return page;
    }

    private TabPage BuildAboutPage()
    {
        TabPage page = new(Resources.About) { Padding = new Padding(14), UseVisualStyleBackColor = true };

        Label name = new()
        {
            Text = Resources.ProgramName,
            Location = new Point(18, 20),
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 14f, FontStyle.Bold),
        };

        Label description = new()
        {
            Text = Resources.AboutDescription,
            Location = new Point(18, 56),
            AutoSize = true,
            MaximumSize = new Size(500, 0),
        };

        Label basedOn = new()
        {
            Text = Resources.AboutBasedOn,
            Location = new Point(18, 100),
            AutoSize = true,
            MaximumSize = new Size(420, 0),
        };

        Label license = new()
        {
            Text = Resources.AboutLicense,
            Location = new Point(18, 124),
            AutoSize = true,
            MaximumSize = new Size(420, 0),
        };

        Button sourceButton = new()
        {
            Text = Resources.OpenSourcePage,
            Location = new Point(18, 160),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(140, 30),
        };
        sourceButton.Click += (_, _) => AppEnvironment.OpenExternally(
            $"https://github.com/{UpdateService.Repository}", logger);

        page.Controls.AddRange([name, description, basedOn, license, sourceButton]);

        return page;
    }

    private void LoadFromSettings(Settings current)
    {
        globalModeRadio.Checked = current.Mode is AudioRoutingMode.Global;
        perAppModeRadio.Checked = current.Mode is AudioRoutingMode.PerApp;

        // If the undocumented interface could not be activated, offering per-app mode would be a lie.
        bool perAppUsable = policyConfig.IsUsable;
        perAppModeRadio.Enabled = perAppUsable;
        perAppHint.Visible = perAppUsable;
        perAppUnavailableLabel.Visible = !perAppUsable;

        if (!perAppUsable && current.Mode is AudioRoutingMode.PerApp)
        {
            globalModeRadio.Checked = true;
        }

        enabledCheckBox.Checked = current.Enabled;
        startWithWindowsCheckBox.Checked = AppEnvironment.IsStartWithWindowsEnabled();

        int languageIndex = AppEnvironment.Languages
            .Select((l, i) => (l, i))
            .FirstOrDefault(x => x.l.Tag.Equals(current.Language, StringComparison.OrdinalIgnoreCase)).i;
        languageComboBox.SelectedIndex = languageIndex;

        int logLevelIndex = Array.IndexOf(LogLevels, current.LogLevel);
        logLevelComboBox.SelectedIndex = logLevelIndex >= 0 ? logLevelIndex : LogLevels.Length - 1;

        checkOnStartupCheckBox.Checked = current.CheckForUpdatesOnStartup;

        currentVersionLabel.Text = string.Format(CultureInfo.InvariantCulture, CurrentVersionFormat, UpdateService.GetCurrentVersion());

        if (!UpdateService.CanSelfUpdate)
        {
            SetUpdateStatus(Resources.UpdateNotSupported, Color.DimGray);
        }
    }

    private void SyncModeFromRadios()
    {
        // Both radios fire this; only the one that just became checked should drive the pending value.
        if (globalModeRadio.Checked)
        {
            pending = pending with { Mode = AudioRoutingMode.Global };
        }
        else if (perAppModeRadio.Checked)
        {
            pending = pending with { Mode = AudioRoutingMode.PerApp };
        }
    }

    private void PopulateMonitorGrid()
    {
        // Preserve any choices the user already made, since repopulating on a display change would otherwise
        // discard them.
        Dictionary<string, string> selections = [];
        for (int row = 0; row < monitorGrid.Rows.Count; row++)
        {
            string? monitorName = monitorGrid.Rows[row].Cells[0].Value as string;
            if (!string.IsNullOrEmpty(monitorName))
            {
                selections[monitorName] = monitorGrid.Rows[row].Cells[1].Value as string ?? "";
            }
        }

        foreach (var (key, value) in pending.Monitors)
        {
            selections.TryAdd(key, value);
        }

        string[] connectedNames = [.. connectedMonitorsMonitor.CurrentConnectedMonitors.Select(m => m.FriendlyName)];
        string[] knownDevices = [.. audioDeviceManager.CurrentPlaybackDeviceNames];

        monitorGrid.Rows.Clear();

        foreach (string monitorName in connectedNames.Order(StringComparer.CurrentCulture))
        {
            int index = monitorGrid.Rows.Add();
            monitorGrid.Rows[index].Cells[0].Value = monitorName;

            selections.TryGetValue(monitorName, out string? configured);
            configured ??= "";

            monitorGrid.Rows[index].Cells[1].Value = BuildDeviceList(knownDevices, configured);

            // The combo column can't be set per cell via the column's Items, so stash the list and set the value.
            if (monitorGrid.Rows[index].Cells[1] is DataGridViewComboBoxCell cell)
            {
                ConfigureComboCell(cell, knownDevices, configured);
            }
        }

        if (monitorGrid.Rows.Count == 0)
        {
            int index = monitorGrid.Rows.Add();
            monitorGrid.Rows[index].Cells[0].Value = Resources.NoMonitorsDetected;
        }
    }

    private static string[] BuildDeviceList(string[] knownDevices, string configured) =>
        string.IsNullOrEmpty(configured) || knownDevices.Contains(configured, StringComparer.Ordinal)
            ? knownDevices
            : [.. knownDevices, configured];

    private static void ConfigureComboCell(DataGridViewComboBoxCell cell, string[] knownDevices, string configured)
    {
        cell.Items.Clear();

        // The first entry is an explicit "don't switch" so users can opt a monitor out instead of being forced to
        // pick a device (which is what an empty first item would otherwise appear to be).
        cell.Items.Add(Resources.DontSwitch);
        cell.Items.AddRange(knownDevices);

        if (!string.IsNullOrEmpty(configured) && !knownDevices.Contains(configured, StringComparer.Ordinal))
        {
            // The device was unplugged or renamed; show it so the mapping isn't silently lost.
            cell.Items.Add(string.Format(CultureInfo.InvariantCulture, PlaybackDeviceNotFoundFormat, configured));
            cell.Value = cell.Items[^1];
            return;
        }

        cell.Value = string.IsNullOrEmpty(configured) ? cell.Items[0] : configured;
    }
    private void CommitMonitorRow(int rowIndex)
    {
        string? monitorName = monitorGrid.Rows[rowIndex].Cells[0].Value as string;
        if (string.IsNullOrEmpty(monitorName))
        {
            return;
        }

        string? raw = monitorGrid.Rows[rowIndex].Cells[1].Value as string;

        // Translate the "don't switch" placeholder and any "device not connected" entry back to an empty string.
        string device = raw is null || raw == Resources.DontSwitch || raw.StartsWith('(') ? "" : raw;

        var monitors = new Dictionary<string, string>(pending.Monitors)
        {
            [monitorName] = device
        };

        pending = pending with { Monitors = monitors };

        logger.Debug("Monitor \"{Monitor}\" set to \"{Device}\" in the settings window.", monitorName, device);
    }

    private async void OnOkClicked(object? sender, EventArgs e)
    {
        if (ApplyPendingSettings())
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void OnApplyClicked(object? sender, EventArgs e)
    {
        ApplyPendingSettings();
    }

    private bool ApplyPendingSettings()
    {
        try
        {
            if (startWithWindowsCheckBox.Checked != AppEnvironment.IsStartWithWindowsEnabled() &&
                !AppEnvironment.SetStartWithWindows(startWithWindowsCheckBox.Checked, logger))
            {
                ShowError(Resources.ErrorSavingSettings);
                return false;
            }

            pending.Save();
            return true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to save settings from the settings window.");
            ShowError($"{Resources.ErrorSavingSettings}\n\n{ex.Message}");
            return false;
        }
    }

    private static void ShowError(string message) =>
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = Resources.ProgramName,
            Heading = Resources.ErrorSavingSettings,
            Text = message,
            Icon = TaskDialogIcon.Error,
            SizeToContent = true,
            Buttons = [new TaskDialogButton(Resources.Ok)],
        });

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (checkCancellation is not null)
        {
            return; // A check is already in flight.
        }

        checkCancellation = new CancellationTokenSource();

        checkNowButton.Enabled = false;
        updateProgressBar.Visible = false;
        SetUpdateStatus(Resources.UpdateChecking, SystemColors.ControlText);

        try
        {
            UpdateCheckResult result = await updateService.CheckAsync(checkCancellation.Token);
            lastCheckResult = result;

            switch (result.Status)
            {
                case UpdateStatus.UpToDate:
                    SetUpdateStatus(Resources.UpdateUpToDate, Color.ForestGreen);
                    openReleasesButton.Enabled = true;
                    break;

                case UpdateStatus.Available:
                    SetUpdateStatus(
                        string.Format(CultureInfo.InvariantCulture, UpdateAvailableFormat, result.Version, UpdateService.GetCurrentVersion()),
                        Color.RoyalBlue);
                    openReleasesButton.Enabled = true;
                    ShowReleaseNotes(result.ReleaseNotes);
                    await PromptToInstallAsync(result);
                    break;

                case UpdateStatus.NotSupported:
                    SetUpdateStatus(Resources.UpdateNotSupported, Color.DimGray);
                    openReleasesButton.Enabled = result.ReleasePageUrl is not null;
                    break;

                default:
                    SetUpdateStatus(string.Format(CultureInfo.InvariantCulture, UpdateFailedFormat, result.Error), Color.Firebrick);
                    break;
            }
        }
        finally
        {
            checkNowButton.Enabled = true;
            checkCancellation?.Dispose();
            checkCancellation = null;
        }
    }

    private async Task PromptToInstallAsync(UpdateCheckResult result)
    {
        if (!UpdateService.CanSelfUpdate)
        {
            return; // The status line already explains; no point offering a button that can't work.
        }

        TaskDialogCommandLinkButton installButton = new(Resources.UpdateInstallNow);
        TaskDialogCommandLinkButton laterButton = new(Resources.UpdateInstallLater);

        TaskDialogPage page = new()
        {
            Caption = Resources.ProgramName,
            Heading = string.Format(CultureInfo.InvariantCulture, UpdateAvailableFormat, result.Version, UpdateService.GetCurrentVersion()),
            Text = result.ReleaseNotes is { Length: > 0 } notes ? notes : Resources.AboutDescription,
            Icon = TaskDialogIcon.Information,
            SizeToContent = true,
            AllowCancel = true,
            Buttons = [installButton, laterButton],
        };

        if (TaskDialog.ShowDialog(this, page) != installButton)
        {
            return;
        }

        string? downloaded = await DownloadUpdateAsync(result);
        if (downloaded is null)
        {
            SetUpdateStatus(string.Format(CultureInfo.InvariantCulture, UpdateFailedFormat, "download failed"), Color.Firebrick);
            return;
        }

        if (updateService.ScheduleInstallAndRestart(downloaded))
        {
            SetUpdateStatus(Resources.UpdateReadyToInstall, Color.ForestGreen);
            Application.Exit();
        }
        else
        {
            SetUpdateStatus(Resources.UpdateNotSupported, Color.DimGray);
        }
    }

    private async Task<string?> DownloadUpdateAsync(UpdateCheckResult result)
    {
        updateProgressBar.Visible = true;
        updateProgressBar.Style = ProgressBarStyle.Continuous;
        updateProgressBar.Value = 0;
        SetUpdateStatus(string.Format(CultureInfo.InvariantCulture, UpdateDownloadingFormat, result.Version), SystemColors.ControlText);

        Progress<(long Received, long Total)> progress = new(p =>
        {
            if (p.Total > 0 && p.Total <= int.MaxValue)
            {
                int percent = (int)Math.Clamp(p.Received * 100 / p.Total, 0, 100);
                updateProgressBar.Value = percent;
            }
            else
            {
                updateProgressBar.Style = ProgressBarStyle.Marquee;
            }
        });

        string? path = await updateService.DownloadAsync(result, progress);

        updateProgressBar.Visible = false;
        return path;
    }

    private void SetUpdateStatus(string text, Color color)
    {
        updateStatusLabel.Text = text;
        updateStatusLabel.ForeColor = color;
    }

    private void ShowReleaseNotes(string? notes)
    {
        bool hasNotes = !string.IsNullOrWhiteSpace(notes);
        releaseNotesTextBox.Visible = hasNotes;
        releaseNotesTextBox.Text = notes ?? "";
        releaseNotesLabel.Visible = hasNotes;
    }

    private void OnOpenReleasesClicked()
    {
        string url = lastCheckResult?.ReleasePageUrl
            ?? $"https://github.com/{UpdateService.Repository}/releases";

        AppEnvironment.OpenExternally(url, logger);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            formSubscriptions.Dispose();
            checkCancellation?.Cancel();
            checkCancellation?.Dispose();
        }

        base.Dispose(disposing);
    }
}
