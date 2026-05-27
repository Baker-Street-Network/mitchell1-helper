using Mitchell1Helper.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows.Forms;

namespace Mitchell1Helper;

internal static class Program
{
	[STAThread]
	private static async Task Main(string[] args)
	{
		var settings = AppSettings.Load();
		settings.Save();

		var host = Host.CreateDefaultBuilder(args)
			.ConfigureServices((context, services) =>
			{
				services.AddSingleton(settings);
				services.AddSingleton<SyncState>();
				services.AddSingleton<MitchellCustomerSyncService>();
				services.AddHttpClient<OdooRpcClient>()
					.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
					{
						ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
					});

				services.AddHostedService<Mitchell1BackgroundService>();
				services.AddHostedService<OdooHeartbeatWebSocketService>();
				services.AddHostedService<OdooCustomerWebSocketListenerService>();
				services.AddHostedService<WebServerHostedService>();

				services.AddLogging(builder =>
				{
					builder.AddConsole();
					builder.AddDebug();
				});
			})
			.Build();

		_ = Task.Run(async () => await host.RunAsync());

		Application.EnableVisualStyles();

		using (var scope = host.Services.CreateScope())
		{
			var form = new SettingsForm(
				scope.ServiceProvider.GetRequiredService<AppSettings>(),
				scope.ServiceProvider.GetRequiredService<SyncState>());
			Application.Run(form);
		}

		await host.StopAsync();
		host.Dispose();
	}
}
