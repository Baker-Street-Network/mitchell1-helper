using Microsoft.AspNetCore.Mvc;
using Mitchell1Helper.Controllers.Models;
using Mitchell1Helper.Services;

namespace Mitchell1Helper.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConnectivityController(
    SyncState syncState,
    SyncFailureTracker failureTracker,
    AppSettings appSettings) : ControllerBase
{
    [HttpGet("status")]
    public ActionResult<ConnectivityStatusResponse> GetStatus()
    {
        int maxAttempts = Math.Max(1, appSettings.Sync.MaxSyncAttempts);
        var quarantined = failureTracker.GetQuarantined(maxAttempts);

        var response = new ConnectivityStatusResponse
        {
            IsHealthy = string.IsNullOrWhiteSpace(syncState.LastError) && quarantined.Count == 0,
            LastSuccessfulPollUtc = syncState.LastSuccessfulPollUtc,
            LastProcessedChangeDateUtc = syncState.LastProcessedChangeDateUtc,
            LastProcessedCustomerId = syncState.LastProcessedCustomerId,
            LastBatchProcessedCount = syncState.LastBatchProcessedCount,
            LastError = syncState.LastError,
            QuarantinedCount = quarantined.Count,
            QuarantinedRecords = quarantined
                .Select(kv => new QuarantinedRecord
                {
                    Record = kv.Key,
                    Attempts = kv.Value.Attempts,
                    FirstFailedUtc = kv.Value.FirstFailedUtc,
                    LastFailedUtc = kv.Value.LastFailedUtc,
                    LastError = kv.Value.LastError
                })
                .ToList()
        };

        return Ok(response);
    }
}
