using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mitchell1Helper.Services;

public class WebServerHostedService(
    ILogger<WebServerHostedService> logger,
    IServiceProvider serviceProvider) : BackgroundService
{
    private IWebHost? _webHost;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Starting web server on http://localhost:5010");

            _webHost = new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://localhost:5010")
                .ConfigureServices(services =>
                {
                    services.AddCors(options =>
                    {
                        options.AddPolicy("AllowAnyOrigin",
                            builder => builder.AllowAnyOrigin()
                                              .AllowAnyHeader()
                                              .AllowAnyMethod());
                    });
                    services.AddControllers();
                    services.AddSingleton(serviceProvider.GetRequiredService<SyncState>());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseCors("AllowAnyOrigin");
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                    });
                })
                .Build();

            await _webHost.StartAsync(stoppingToken);
            logger.LogInformation("Web server started successfully");

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Web server is stopping");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in web server");
        }
        finally
        {
            if (_webHost != null)
            {
                await _webHost.StopAsync();
                _webHost.Dispose();
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping web server");
        if (_webHost != null)
        {
            await _webHost.StopAsync(cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }
}
