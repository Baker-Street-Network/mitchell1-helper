using Microsoft.AspNetCore.Mvc;
using Mitchell1Helper.Controllers.Models;
using Mitchell1Helper.Services;

namespace Mitchell1Helper.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConnectivityController(SyncState syncState) : ControllerBase
{
    [HttpGet("status")]
    public ActionResult<ConnectivityStatusResponse> GetStatus()
    {
        var response = new ConnectivityStatusResponse
        {
            IsHealthy = string.IsNullOrWhiteSpace(syncState.LastError),
            LastSuccessfulPollUtc = syncState.LastSuccessfulPollUtc,
            LastProcessedChangeDateUtc = syncState.LastProcessedChangeDateUtc,
            LastProcessedCustomerId = syncState.LastProcessedCustomerId,
            LastBatchProcessedCount = syncState.LastBatchProcessedCount,
            LastError = syncState.LastError
        };

        return Ok(response);
    }
}
