namespace Mitchell1Helper.Services;

public class SyncState
{
    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mitchell1Helper",
        "sync-state.json"
    );

    private readonly object _lock = new();

    public DateTime? LastSuccessfulPollUtc { get; private set; }
    public DateTime? LastHeartbeatSentUtc { get; private set; }
    public DateTime? LastProcessedChangeDateUtc { get; private set; }
    public long? LastProcessedCustomerId { get; private set; }
    public string? LastError { get; private set; }
    public int LastBatchProcessedCount { get; private set; }

    // PO sync state
    public DateTime? LastSuccessfulPOPollUtc { get; private set; }
    public DateTime? LastProcessedPOChangeDateUtc { get; private set; }
    public long? LastProcessedPOMasterId { get; private set; }
    public DateTime? LastProcessedPODetailChangeDateUtc { get; private set; }
    public long? LastProcessedPODetailId { get; private set; }
    public string? LastPOError { get; private set; }
    public int LastPOHeadersProcessedCount { get; private set; }
    public int LastPODetailProcessedCount { get; private set; }

    public SyncState()
    {
        Load();
    }

    public void MarkSuccess(DateTime? lastChangeDateUtc, long? lastCustomerId, int batchCount)
    {
        lock (_lock)
        {
            LastSuccessfulPollUtc = DateTime.UtcNow;
            LastProcessedChangeDateUtc = lastChangeDateUtc;
            LastProcessedCustomerId = lastCustomerId;
            LastBatchProcessedCount = batchCount;
            LastError = null;
            Save();
        }
    }

    public void MarkPOSuccess(DateTime? lastChangeDateUtc, long? lastPOMasterId, int headersCount, int detailsCount)
    {
        lock (_lock)
        {
            LastSuccessfulPOPollUtc = DateTime.UtcNow;
            LastProcessedPOChangeDateUtc = lastChangeDateUtc;
            LastProcessedPOMasterId = lastPOMasterId;
            LastPOHeadersProcessedCount = headersCount;
            LastPODetailProcessedCount = detailsCount;
            LastPOError = null;
            Save();
        }
    }

    public void MarkPODetailSuccess(DateTime? lastChangeDateUtc, long? lastPODetailId)
    {
        lock (_lock)
        {
            LastProcessedPODetailChangeDateUtc = lastChangeDateUtc;
            LastProcessedPODetailId = lastPODetailId;
            Save();
        }
    }

    public void MarkError(string error)
    {
        lock (_lock)
        {
            LastError = error;
            Save();
        }
    }

    public void MarkPOError(string error)
    {
        lock (_lock)
        {
            LastPOError = error;
            Save();
        }
    }

    public void MarkHeartbeatSent()
    {
        lock (_lock)
        {
            LastHeartbeatSentUtc = DateTime.UtcNow;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StateFilePath))
            {
                return;
            }

            string json = File.ReadAllText(StateFilePath);
            var state = System.Text.Json.JsonSerializer.Deserialize<SyncStateData>(json);
            if (state != null)
            {
                LastSuccessfulPollUtc = state.LastSuccessfulPollUtc;
                LastHeartbeatSentUtc = state.LastHeartbeatSentUtc;
                LastProcessedChangeDateUtc = state.LastProcessedChangeDateUtc;
                LastProcessedCustomerId = state.LastProcessedCustomerId;
                LastError = state.LastError;
                LastBatchProcessedCount = state.LastBatchProcessedCount;

                LastSuccessfulPOPollUtc = state.LastSuccessfulPOPollUtc;
                LastProcessedPOChangeDateUtc = state.LastProcessedPOChangeDateUtc;
                LastProcessedPOMasterId = state.LastProcessedPOMasterId;
                LastProcessedPODetailChangeDateUtc = state.LastProcessedPODetailChangeDateUtc;
                LastProcessedPODetailId = state.LastProcessedPODetailId;
                LastPOError = state.LastPOError;
                LastPOHeadersProcessedCount = state.LastPOHeadersProcessedCount;
                LastPODetailProcessedCount = state.LastPODetailProcessedCount;
            }
        }
        catch
        {
            // If load fails, start fresh
        }
    }

    private void Save()
    {
        try
        {
            var state = new SyncStateData
            {
                LastSuccessfulPollUtc = LastSuccessfulPollUtc,
                LastHeartbeatSentUtc = LastHeartbeatSentUtc,
                LastProcessedChangeDateUtc = LastProcessedChangeDateUtc,
                LastProcessedCustomerId = LastProcessedCustomerId,
                LastError = LastError,
                LastBatchProcessedCount = LastBatchProcessedCount,

                LastSuccessfulPOPollUtc = LastSuccessfulPOPollUtc,
                LastProcessedPOChangeDateUtc = LastProcessedPOChangeDateUtc,
                LastProcessedPOMasterId = LastProcessedPOMasterId,
                LastProcessedPODetailChangeDateUtc = LastProcessedPODetailChangeDateUtc,
                LastProcessedPODetailId = LastProcessedPODetailId,
                LastPOError = LastPOError,
                LastPOHeadersProcessedCount = LastPOHeadersProcessedCount,
                LastPODetailProcessedCount = LastPODetailProcessedCount
            };

            string directory = Path.GetDirectoryName(StateFilePath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(StateFilePath, json);
        }
        catch
        {
            // If save fails, continue (state will be lost on restart)
        }
    }

    private class SyncStateData
    {
        public DateTime? LastSuccessfulPollUtc { get; set; }
        public DateTime? LastHeartbeatSentUtc { get; set; }
        public DateTime? LastProcessedChangeDateUtc { get; set; }
        public long? LastProcessedCustomerId { get; set; }
        public string? LastError { get; set; }
        public int LastBatchProcessedCount { get; set; }

        public DateTime? LastSuccessfulPOPollUtc { get; set; }
        public DateTime? LastProcessedPOChangeDateUtc { get; set; }
        public long? LastProcessedPOMasterId { get; set; }
        public DateTime? LastProcessedPODetailChangeDateUtc { get; set; }
        public long? LastProcessedPODetailId { get; set; }
        public string? LastPOError { get; set; }
        public int LastPOHeadersProcessedCount { get; set; }
        public int LastPODetailProcessedCount { get; set; }
    }
}
