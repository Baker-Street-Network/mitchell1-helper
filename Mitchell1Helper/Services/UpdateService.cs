using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Mitchell1Helper.Services;

public class UpdateService(
    AppSettings appSettings,
    ILogger<UpdateService> logger) : BackgroundService
{
    private UpdateManager? _updateManager;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!appSettings.Update.Enabled)
        {
            logger.LogInformation("Auto-update is disabled in settings.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForUpdatesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking for updates");
            }

            int hours = Math.Max(1, appSettings.Update.CheckIntervalHours);
            await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
        }
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        string repoUrl = appSettings.Update.GithubRepoUrl;
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            logger.LogWarning("Update repository URL is empty; skipping update check.");
            return;
        }

        logger.LogInformation("Checking for updates from {RepoUrl}", repoUrl);

        _updateManager ??= new UpdateManager(new GithubSource(repoUrl, null, false));

        var newVersion = await _updateManager.CheckForUpdatesAsync();
        if (newVersion is null)
        {
            logger.LogInformation("No updates available.");
            return;
        }

        logger.LogInformation("Update available: {Version}. Downloading...", newVersion.TargetFullRelease.Version);
        await _updateManager.DownloadUpdatesAsync(newVersion);
        logger.LogInformation("Update downloaded. Applying and restarting...");
        _updateManager.ApplyUpdatesAndRestart(newVersion);
    }
}
