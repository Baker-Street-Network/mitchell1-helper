namespace Mitchell1Helper.Controllers.Models;

public class ConnectivityStatusResponse
{
    public bool IsHealthy { get; set; }
    public DateTime? LastSuccessfulPollUtc { get; set; }
    public DateTime? LastProcessedChangeDateUtc { get; set; }
    public long? LastProcessedCustomerId { get; set; }
    public int LastBatchProcessedCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>Records that exhausted their retries and were skipped so the queue could continue.</summary>
    public int QuarantinedCount { get; set; }

    public List<QuarantinedRecord> QuarantinedRecords { get; set; } = [];
}

public class QuarantinedRecord
{
    /// <summary>Entity and id, e.g. "RepairOrder:778".</summary>
    public string Record { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime FirstFailedUtc { get; set; }
    public DateTime LastFailedUtc { get; set; }
    public string? LastError { get; set; }
}
