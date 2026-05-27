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

    public class SqlConfig
    {
        public string ConnectionString { get; set; } = "";
    }

    public class OdooConfig
    {
        public string Url { get; set; } = "http://localhost:8069";
        public string Database { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string CustomerModel { get; set; } = "res.partner";
        public string ExternalRefPrefix { get; set; } = "MITCHELL1-";
        public string WebsocketSecret { get; set; } = "";
        public string WebsocketChannel { get; set; } = "record_updated";
    }

    public class SyncConfig
    {
        public int PollIntervalSeconds { get; set; } = 30;
        public int BatchSize { get; set; } = 200;
        public int HeartbeatIntervalSeconds { get; set; } = 15;
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
