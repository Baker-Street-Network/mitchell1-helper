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

        batchSizeNumericUpDown.Value = Math.Clamp(_settings.Sync.BatchSize, 1, 10000);

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

            _settings.Sync.BatchSize = (int)batchSizeNumericUpDown.Value;

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
        string lastChange = _syncState.LastProcessedChangeDateUtc?.ToString("u") ?? "none";
        string lastId = _syncState.LastProcessedCustomerId?.ToString() ?? "none";
        string err = string.IsNullOrWhiteSpace(_syncState.LastError) ? "none" : _syncState.LastError;

        string lastVendorPoll = _syncState.LastSuccessfulVendorPollUtc?.ToLocalTime().ToString("g") ?? "never";
        string lastVendorChange = _syncState.LastProcessedVendorChangeDateUtc?.ToString("u") ?? "none";
        string lastVendorId = _syncState.LastProcessedVendorId?.ToString() ?? "none";
        string vendorErr = string.IsNullOrWhiteSpace(_syncState.LastVendorError) ? "none" : _syncState.LastVendorError;

        string lastPartPoll = _syncState.LastSuccessfulPartPollUtc?.ToLocalTime().ToString("g") ?? "never";
        string lastPartChange = _syncState.LastProcessedPartChangeDateUtc?.ToString("u") ?? "none";
        string lastPartId = _syncState.LastProcessedPartId?.ToString() ?? "none";
        string partErr = string.IsNullOrWhiteSpace(_syncState.LastPartError) ? "none" : _syncState.LastPartError;

        string lastRepairOrderPoll = _syncState.LastSuccessfulRepairOrderPollUtc?.ToLocalTime().ToString("g") ?? "never";
        string lastRepairOrderChange = _syncState.LastProcessedRepairOrderChangeDateUtc?.ToString("u") ?? "none";
        string lastRepairOrderId = _syncState.LastProcessedRepairOrderId?.ToString() ?? "none";
        string repairOrderErr = string.IsNullOrWhiteSpace(_syncState.LastRepairOrderError) ? "none" : _syncState.LastRepairOrderError;

        string lastRepairOrderLineItemPoll = _syncState.LastSuccessfulRepairOrderLineItemPollUtc?.ToLocalTime().ToString("g") ?? "never";
        string lastRepairOrderLineItemChange = _syncState.LastProcessedRepairOrderLineItemChangeDateUtc?.ToString("u") ?? "none";
        string lastRepairOrderLineItemId = _syncState.LastProcessedRepairOrderLineItemId?.ToString() ?? "none";
        string repairOrderLineItemErr = string.IsNullOrWhiteSpace(_syncState.LastRepairOrderLineItemError) ? "none" : _syncState.LastRepairOrderLineItemError;

        string lastPOPoll = _syncState.LastSuccessfulPOPollUtc?.ToLocalTime().ToString("g") ?? "never";
        string lastPOChange = _syncState.LastProcessedPOChangeDateUtc?.ToString("u") ?? "none";
        string lastPOMasterId = _syncState.LastProcessedPOMasterId?.ToString() ?? "none";
        string poErr = string.IsNullOrWhiteSpace(_syncState.LastPOError) ? "none" : _syncState.LastPOError;

        string lastGLAccountPoll = _syncState.LastSuccessfulGLAccountPollUtc?.ToLocalTime().ToString("g") ?? "never";
        string glAccountCount = _syncState.LastSyncedGLAccountIds?.Length.ToString() ?? "0";
        string glAccountErr = string.IsNullOrWhiteSpace(_syncState.LastGLAccountError) ? "none" : _syncState.LastGLAccountError;

        textBox1.Text =
            $"=== GL ACCOUNTS ===" + Environment.NewLine +
            $"Last poll: {lastGLAccountPoll}" + Environment.NewLine +
            $"Last processed count: {_syncState.LastGLAccountProcessedCount}" + Environment.NewLine +
            $"Accounts currently synced: {glAccountCount}" + Environment.NewLine +
            $"Last error: {glAccountErr}" + Environment.NewLine +
            Environment.NewLine +
            $"=== CUSTOMERS ===" + Environment.NewLine +
            $"Last poll: {lastPoll}" + Environment.NewLine +
            $"Last change processed (UTC): {lastChange}" + Environment.NewLine +
            $"Last record ID: {lastId}" + Environment.NewLine +
            $"Last batch count: {_syncState.LastBatchProcessedCount}" + Environment.NewLine +
            $"Last error: {err}" + Environment.NewLine +
            Environment.NewLine +
            $"=== VENDORS ===" + Environment.NewLine +
            $"Last poll: {lastVendorPoll}" + Environment.NewLine +
            $"Last change processed (UTC): {lastVendorChange}" + Environment.NewLine +
            $"Last record ID: {lastVendorId}" + Environment.NewLine +
            $"Last batch count: {_syncState.LastVendorBatchProcessedCount}" + Environment.NewLine +
            $"Last error: {vendorErr}" + Environment.NewLine +
            Environment.NewLine +
            $"=== PARTS ===" + Environment.NewLine +
            $"Last poll: {lastPartPoll}" + Environment.NewLine +
            $"Last change processed (UTC): {lastPartChange}" + Environment.NewLine +
            $"Last record ID: {lastPartId}" + Environment.NewLine +
            $"Last batch count: {_syncState.LastPartBatchProcessedCount}" + Environment.NewLine +
            $"Last error: {partErr}" + Environment.NewLine +
            Environment.NewLine +
            $"=== REPAIR ORDERS ===" + Environment.NewLine +
            $"Last poll: {lastRepairOrderPoll}" + Environment.NewLine +
            $"Last change processed (UTC): {lastRepairOrderChange}" + Environment.NewLine +
            $"Last record ID: {lastRepairOrderId}" + Environment.NewLine +
            $"Last batch count: {_syncState.LastRepairOrderBatchProcessedCount}" + Environment.NewLine +
            $"Last error: {repairOrderErr}" + Environment.NewLine +
            Environment.NewLine +
            $"=== REPAIR ORDER LINE ITEMS ===" + Environment.NewLine +
            $"Last poll: {lastRepairOrderLineItemPoll}" + Environment.NewLine +
            $"Last change processed (UTC): {lastRepairOrderLineItemChange}" + Environment.NewLine +
            $"Last record ID: {lastRepairOrderLineItemId}" + Environment.NewLine +
            $"Last batch count: {_syncState.LastRepairOrderLineItemBatchProcessedCount}" + Environment.NewLine +
            $"Last error: {repairOrderLineItemErr}" + Environment.NewLine +
            Environment.NewLine +
            $"=== PURCHASE ORDERS ===" + Environment.NewLine +
            $"Last poll: {lastPOPoll}" + Environment.NewLine +
            $"Last change processed (UTC): {lastPOChange}" + Environment.NewLine +
            $"Last POMasterId: {lastPOMasterId}" + Environment.NewLine +
            $"Last headers count: {_syncState.LastPOHeadersProcessedCount}" + Environment.NewLine +
            $"Last details count: {_syncState.LastPODetailProcessedCount}" + Environment.NewLine +
            $"Last error: {poErr}" + Environment.NewLine;
    }

    private void saveButton_Click(object sender, EventArgs e)
    {
        SaveSettings();
    }

    private void reloadButton_Click(object sender, EventArgs e)
    {
        LoadSettingsIntoUi();
    }

    private async void testConnectionButton_Click(object sender, EventArgs e)
    {
        string cs = sqlConnectionStringTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cs))
        {
            statusLabel.Text = "Enter a connection string first.";
            statusLabel.ForeColor = Color.DarkOrange;
            return;
        }

        testConnectionButton.Enabled = false;
        statusLabel.Text = "Testing...";
        statusLabel.ForeColor = Color.DarkSlateGray;

        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs)
            {
                TrustServerCertificate = true,
                ConnectTimeout = 5
            };
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            statusLabel.Text = $"Connected to {conn.Database} on {conn.DataSource}";
            statusLabel.ForeColor = Color.DarkGreen;
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Connection failed: " + ex.Message;
            statusLabel.ForeColor = Color.DarkRed;
        }
        finally
        {
            testConnectionButton.Enabled = true;
        }
    }

    private static bool IsInDesigner()
    {
        return LicenseManager.UsageMode == LicenseUsageMode.Designtime;
    }
}
