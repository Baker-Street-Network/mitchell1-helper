using System.Text.Json;

namespace Mitchell1Helper.Services;

/// <summary>
/// Tracks consecutive per-record sync failures so a record that can never succeed
/// stops blocking the cursor.
/// <para>
/// A failed record holds the cursor (so it is retried rather than silently lost), but
/// only for a bounded number of attempts. Once it exhausts them it is quarantined: the
/// cursor is allowed past it and it is reported through sync state, so one bad row cannot
/// dam the whole queue.
/// </para>
/// <para>
/// A quarantined record is still attempted whenever it comes back through the cursor
/// (Mitchell1 bumps LastChangeDate when it is edited), so fixing the underlying data is
/// enough to get it syncing again — the counter only decides whether to hold the cursor.
/// </para>
/// </summary>
public class SyncFailureTracker
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mitchell1Helper",
        "quarantine.json"
    );

    private readonly object _lock = new();
    private Dictionary<string, FailureEntry> _failures = new(StringComparer.Ordinal);

    public SyncFailureTracker()
    {
        Load();
    }

    public class FailureEntry
    {
        public int Attempts { get; set; }
        public DateTime FirstFailedUtc { get; set; }
        public DateTime LastFailedUtc { get; set; }
        public string? LastError { get; set; }
    }

    private static string Key(string entityName, long id) => $"{entityName}:{id}";

    /// <summary>
    /// Records a failure and returns the new consecutive attempt count.
    /// </summary>
    public int RecordFailure(string entityName, long id, string error)
    {
        string key = Key(entityName, id);

        lock (_lock)
        {
            if (!_failures.TryGetValue(key, out FailureEntry? entry))
            {
                entry = new FailureEntry { FirstFailedUtc = DateTime.UtcNow };
                _failures[key] = entry;
            }

            entry.Attempts++;
            entry.LastFailedUtc = DateTime.UtcNow;
            entry.LastError = Truncate(error, 1000);
            Save();
            return entry.Attempts;
        }
    }

    /// <summary>
    /// Clears a record's failure history after it syncs successfully.
    /// </summary>
    public void Clear(string entityName, long id)
    {
        string key = Key(entityName, id);

        lock (_lock)
        {
            if (_failures.Remove(key)) Save();
        }
    }

    /// <summary>
    /// Records that have exhausted their retries, newest failure first. Restricted to the given
    /// entities when any are named; pass none for the overall total.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, FailureEntry>> GetQuarantined(int maxAttempts, params string[] entityNames)
    {
        lock (_lock)
        {
            return _failures
                .Where(kv => kv.Value.Attempts >= maxAttempts && MatchesEntity(kv.Key, entityNames))
                .OrderByDescending(kv => kv.Value.LastFailedUtc)
                .Select(kv => new KeyValuePair<string, FailureEntry>(kv.Key, kv.Value))
                .ToList();
        }
    }

    public int QuarantinedCount(int maxAttempts, params string[] entityNames)
    {
        lock (_lock)
        {
            return _failures.Count(kv => kv.Value.Attempts >= maxAttempts && MatchesEntity(kv.Key, entityNames));
        }
    }

    /// <summary>
    /// One-line summary for sync state / the status endpoint, or null when nothing is quarantined.
    /// A caller reporting one entity's health must name that entity, otherwise it reports every
    /// other entity's failures as its own.
    /// </summary>
    public string? Describe(int maxAttempts, params string[] entityNames)
    {
        const int sampleSize = 5;

        var quarantined = GetQuarantined(maxAttempts, entityNames);
        if (quarantined.Count == 0) return null;

        var sample = quarantined.Take(sampleSize).Select(kv => kv.Key);
        string suffix = quarantined.Count > sampleSize ? $", +{quarantined.Count - sampleSize} more" : "";
        return $"{quarantined.Count} record(s) quarantined after {maxAttempts} failed attempts: " +
               $"{string.Join(", ", sample)}{suffix}. See log for details.";
    }

    /// <summary>
    /// Whether a <c>"{entity}:{id}"</c> key belongs to one of the named entities.
    /// An empty filter matches everything, so callers wanting the overall total pass none.
    /// </summary>
    private static bool MatchesEntity(string key, string[] entityNames)
    {
        if (entityNames.Length == 0) return true;

        foreach (string name in entityNames)
        {
            if (key.Length > name.Length
                && key[name.Length] == ':'
                && key.StartsWith(name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            string json = File.ReadAllText(FilePath);
            _failures = JsonSerializer.Deserialize<Dictionary<string, FailureEntry>>(json)
                ?? new Dictionary<string, FailureEntry>(StringComparer.Ordinal);
        }
        catch
        {
            _failures = new Dictionary<string, FailureEntry>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            string directory = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_failures, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // Non-fatal: worst case the counts reset on restart.
        }
    }
}
