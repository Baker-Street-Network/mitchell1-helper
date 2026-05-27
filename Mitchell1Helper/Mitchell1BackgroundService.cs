using Mitchell1Helper.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper;

public class Mitchell1BackgroundService(
    ILogger<Mitchell1BackgroundService> logger,
    AppSettings appSettings,
    MitchellCustomerSyncService customerSyncService,
    MitchellVendorSyncService vendorSyncService,
    MitchellPartSyncService partSyncService,
    MitchellRepairOrderSyncService repairOrderSyncService,
    MitchellPOSyncService poSyncService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Mitchell1 background service started");

        int pollSeconds = Math.Max(5, appSettings.Sync.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await customerSyncService.PollAndSyncAsync(stoppingToken);
                await vendorSyncService.PollAndSyncAsync(stoppingToken);
                await partSyncService.PollAndSyncAsync(stoppingToken);
                await repairOrderSyncService.PollAndSyncAsync(stoppingToken);
                await poSyncService.PollAndSyncAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in background loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("Mitchell1 background service stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Mitchell1 background service is stopping");
        await base.StopAsync(cancellationToken);
    }
}
