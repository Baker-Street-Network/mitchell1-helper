namespace Mitchell1Helper.Services;

public class SyncState
{
    private readonly object _lock = new();

    public DateTime? LastSuccessfulPollUtc { get; private set; }
    public DateTime? LastHeartbeatSentUtc { get; private set; }
    public DateTime? LastProcessedChangeDateUtc { get; private set; }
    public long? LastProcessedCustomerId { get; private set; }
    public string? LastError { get; private set; }
    public int LastBatchProcessedCount { get; private set; }

    public void MarkSuccess(DateTime? lastChangeDateUtc, long? lastCustomerId, int batchCount)
    {
        lock (_lock)
        {
            LastSuccessfulPollUtc = DateTime.UtcNow;
            LastProcessedChangeDateUtc = lastChangeDateUtc;
            LastProcessedCustomerId = lastCustomerId;
            LastBatchProcessedCount = batchCount;
            LastError = null;
        }
    }

    public void MarkError(string error)
    {
        lock (_lock)
        {
            LastError = error;
        }
    }

    public void MarkHeartbeatSent()
    {
        lock (_lock)
        {
            LastHeartbeatSentUtc = DateTime.UtcNow;
        }
    }
}
