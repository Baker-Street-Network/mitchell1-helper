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

        LoadSettingsIntoUi();
        _statusTimer.Tick += (_, _) => UpdateSyncStatus();
        _statusTimer.Start();
        UpdateSyncStatus();
    }

    private void LoadSettingsIntoUi()
    {
        sqlConnectionStringTextBox.Text = _settings.Sql.ConnectionString;

        odooUrlTextBox.Text = _settings.Odoo.Url;
        odooDatabaseTextBox.Text = _settings.Odoo.Database;
        odooUsernameTextBox.Text = _settings.Odoo.Username;
        odooPasswordTextBox.Text = _settings.Odoo.Password;

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
            _settings.Odoo.Password = odooPasswordTextBox.Text;

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
