using System.Text.Json;

namespace Mitchell1Helper;

public class AppSettings
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mitchell1Helper",
        "settings.json"
    );

    public SqlConfig Sql { get; set; } = new();
    public OdooConfig Odoo { get; set; } = new();
    public SyncConfig Sync { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();

    public class SqlConfig
    {
        public string ConnectionString { get; set; } = "Server=localhost;Database=ShopMgt;Integrated Security=True;";
    }

    public class OdooConfig
    {
        public string Url { get; set; } = "http://localhost:8069";
        public string Database { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string CustomerModel { get; set; } = "res.partner";
        public string VendorModel { get; set; } = "res.partner";
        public string PartModel { get; set; } = "mitchell.part";
        public string RepairOrderModel { get; set; } = "mitchell.repair";
        public string RepairOrderLineItemModel { get; set; } = "mitchell.repair.line";
        public string POHeaderModel { get; set; } = "mitchell.po";
        public string PODetailModel { get; set; } = "mitchell.po.detail";
        public string ExternalRefPrefix { get; set; } = "MITCHELL1-";
    }

    public class SyncConfig
    {
        public int PollIntervalSeconds { get; set; } = 30;
        public int BatchSize { get; set; } = 200;
    }

    public class UpdateConfig
    {
        public bool Enabled { get; set; } = true;
        public string GithubRepoUrl { get; set; } = "https://github.com/Baker-Street-Network/mitchell1-helper";
        public int CheckIntervalHours { get; set; } = 4;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                return settings ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(SettingsFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Unable to determine settings directory.");
        }

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(SettingsFilePath, json);
    }
}
