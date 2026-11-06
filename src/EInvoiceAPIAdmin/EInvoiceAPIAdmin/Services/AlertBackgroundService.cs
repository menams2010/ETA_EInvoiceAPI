using InvoiceWebApp.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InvoiceWebApp.Services;

public class AlertBackgroundService(IServiceProvider sp) : BackgroundService
{
    private readonly IServiceProvider _sp = sp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run shortly after startup, then every 24h.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<AlertRunner>();

                await runner.RunOnceAsync(stoppingToken);
            }
            catch
            {
                // swallow to keep service alive; consider adding logging if app has logging conventions
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
