using Mitchell1Helper.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Windows.Forms;
using Velopack;

namespace Mitchell1Helper;

internal static class Program
{
	[STAThread]
	private static async Task Main(string[] args)
	{
		VelopackApp.Build().Run();
		AddToStartup();

		var settings = AppSettings.Load();
		settings.Save();

		var host = Host.CreateDefaultBuilder(args)
			.ConfigureServices((context, services) =>
			{
				services.AddSingleton(settings);
				services.AddSingleton<SyncState>();
				services.AddSingleton<MitchellCustomerSyncService>();
				services.AddSingleton<MitchellVendorSyncService>();
				services.AddSingleton<MitchellPartSyncService>();
				services.AddSingleton<MitchellRepairOrderSyncService>();
				services.AddSingleton<MitchellPOSyncService>();
				services.AddHttpClient<OdooRpcClient>()
					.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
					{
						ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
					});

				services.AddHostedService<Mitchell1BackgroundService>();
				services.AddHostedService<OdooHeartbeatWebSocketService>();
				services.AddHostedService<OdooCustomerWebSocketListenerService>();
				services.AddHostedService<UpdateService>();
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

	private static void AddToStartup()
	{
		const string appName = "Mitchell1Helper";
		string appPath = Environment.ProcessPath ?? Application.ExecutablePath;

		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
		if (key is null)
		{
			return;
		}

		string expectedValue = $"\"{appPath}\"";
		string? existingValue = key.GetValue(appName) as string;
		if (!string.Equals(existingValue, expectedValue, StringComparison.Ordinal))
		{
			key.SetValue(appName, expectedValue);
		}
	}
}
