using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mitchell1Helper.Services;

/// <summary>
/// Persists a SHA256 hash of each synced record's values so we can skip
/// Odoo writes when nothing has actually changed, without any fragile
/// JSON comparison against the Odoo response.
/// </summary>
public class RecordHashCache
{
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mitchell1Helper",
        "record-hashes.json"
    );

    private readonly object _lock = new();
    private Dictionary<string, string> _hashes = new(StringComparer.Ordinal);

    public RecordHashCache()
    {
        Load();
    }

    /// <summary>
    /// Returns true (and updates the cache) if the values have changed since the last sync.
    /// Returns false if the hash matches — caller should skip the write.
    /// </summary>
    public bool HasChangedAndUpdate(string recordKey, Dictionary<string, object> values)
    {
        string hash = ComputeHash(values);

        lock (_lock)
        {
            if (_hashes.TryGetValue(recordKey, out string? existing) && existing == hash)
            {
                return false;
            }

            _hashes[recordKey] = hash;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Removes a record from the cache (e.g. after archive) so it gets re-evaluated next time.
    /// </summary>
    public void Remove(string recordKey)
    {
        lock (_lock)
        {
            if (_hashes.Remove(recordKey))
            {
                Save();
            }
        }
    }

    private static string ComputeHash(Dictionary<string, object> values)
    {
        // Sort keys for a deterministic representation
        var ordered = values
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { kv.Key, Value = kv.Value?.ToString() ?? "" });

        string json = JsonSerializer.Serialize(ordered);
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(CacheFilePath))
            {
                string json = File.ReadAllText(CacheFilePath);
                _hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
        catch
        {
            _hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            string directory = Path.GetDirectoryName(CacheFilePath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(_hashes, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
        }
        catch
        {
            // Non-fatal: worst case we re-send an unchanged record next restart
        }
    }
}
