namespace Mitchell1Helper.Controllers.Models;

public class ConnectivityStatusResponse
{
    public bool IsHealthy { get; set; }
    public DateTime? LastSuccessfulPollUtc { get; set; }
    public DateTime? LastProcessedChangeDateUtc { get; set; }
    public long? LastProcessedCustomerId { get; set; }
    public int LastBatchProcessedCount { get; set; }
    public string? LastError { get; set; }
}
