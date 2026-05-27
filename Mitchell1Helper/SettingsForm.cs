using Mitchell1Helper.Services;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Mitchell1Helper;

public partial class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly SyncState _syncState;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 3000 };
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _trayMenu;
    private bool _allowExit;

    // Parameterless constructor is required for Visual Studio designer support.
    public SettingsForm() : this(new AppSettings(), new SyncState())
    {
    }

    public SettingsForm(AppSettings settings, SyncState syncState)
    {
        _settings = settings;
        _syncState = syncState;

        InitializeComponent();

        if (IsInDesigner())
        {
            statusLabel.Text = "Design mode";
            textBox1.Text = "Status preview";
            return;
        }

        SetupBackgroundMode();
        LoadSettingsIntoUi();
        _statusTimer.Tick += (_, _) => UpdateSyncStatus();
        _statusTimer.Start();
        UpdateSyncStatus();
    }

    private void SetupBackgroundMode()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Show", null, (_, _) => ShowFromTray());
        _trayMenu.Items.Add("Hide", null, (_, _) => HideToTray());
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Exit", null, (_, _) => ExitFromTray());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Mitchell1 Helper",
            ContextMenuStrip = _trayMenu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();

        Resize += SettingsForm_Resize;
        FormClosing += SettingsForm_FormClosing;
        FormClosed += SettingsForm_FormClosed;
    }

    private void SettingsForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void SettingsForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowExit)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _notifyIcon?.ShowBalloonTip(2000, "Mitchell1 Helper", "Still running in background.", ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        _statusTimer.Stop();
        _notifyIcon?.Dispose();
        _trayMenu?.Dispose();
        Close();
    }

    private void SettingsForm_FormClosed(object? sender, FormClosedEventArgs e)
    {
        _statusTimer.Dispose();
        _notifyIcon?.Dispose();
        _trayMenu?.Dispose();
    }

    private void LoadSettingsIntoUi()
    {
        sqlConnectionStringTextBox.Text = _settings.Sql.ConnectionString;

        odooUrlTextBox.Text = _settings.Odoo.Url;
        odooDatabaseTextBox.Text = _settings.Odoo.Database;
        odooUsernameTextBox.Text = _settings.Odoo.Username;
        odooPasswordTextBox.Text = _settings.Odoo.ApiKey;

        odooPasswordLabel.Text = "API Key";
        odooPasswordTextBox.PasswordChar = '\0';

        statusLabel.Text = "Settings loaded.";
        statusLabel.ForeColor = Color.DarkSlateGray;
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Sql.ConnectionString = sqlConnectionStringTextBox.Text.Trim();

            _settings.Odoo.Url = odooUrlTextBox.Text.Trim();
            _settings.Odoo.Database = odooDatabaseTextBox.Text.Trim();
            _settings.Odoo.Username = odooUsernameTextBox.Text.Trim();
            _settings.Odoo.ApiKey = odooPasswordTextBox.Text.Trim();

            _settings.Save();

            statusLabel.Text = $"Settings saved at {DateTime.Now:T}";
            statusLabel.ForeColor = Color.DarkGreen;
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Save failed: " + ex.Message;
            statusLabel.ForeColor = Color.DarkRed;
        }
    }

    private void UpdateSyncStatus()
    {
        string lastPoll = _syncState.LastSuccessfulPollUtc?.ToLocalTime().ToString("g") ?? "never";
        string lastHeartbeat = _syncState.LastHeartbeatSentUtc?.ToLocalTime().ToString("g") ?? "never";
        string lastChange = _syncState.LastProcessedChangeDateUtc?.ToString("u") ?? "none";
        string lastId = _syncState.LastProcessedCustomerId?.ToString() ?? "none";
        string err = string.IsNullOrWhiteSpace(_syncState.LastError) ? "none" : _syncState.LastError;

        textBox1.Text =
            $"Last heartbeat push: {lastHeartbeat}" + Environment.NewLine +
            $"Last poll: {lastPoll}" + Environment.NewLine +
            $"Last change processed (UTC): {lastChange}" + Environment.NewLine +
            $"Last record ID: {lastId}" + Environment.NewLine +
            $"Last batch count: {_syncState.LastBatchProcessedCount}" + Environment.NewLine +
            $"Last error: {err}" + Environment.NewLine;
    }

    private void saveButton_Click(object sender, EventArgs e)
    {
        SaveSettings();
    }

    private void reloadButton_Click(object sender, EventArgs e)
    {
        LoadSettingsIntoUi();
    }

    private static bool IsInDesigner()
    {
        return LicenseManager.UsageMode == LicenseUsageMode.Designtime;
    }
}
