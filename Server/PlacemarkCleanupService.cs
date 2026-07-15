using AccessibilityMap.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccessibilityMap.Server;

/// <summary>
/// Фоновая служба: раз в 10 минут удаляет неодобренные метки, у которых истёк
/// срок «удержания» (ExpiresAt). Одобренные метки не трогает.
/// </summary>
public class PlacemarkCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlacemarkCleanupService> _logger;

    public PlacemarkCleanupService(IServiceScopeFactory scopeFactory, ILogger<PlacemarkCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTime.UtcNow;

                var expired = await db.Placemarks
                    .Where(p => p.VerificationStatus != "approved" && p.ExpiresAt != null && p.ExpiresAt < cutoff)
                    .ToListAsync(stoppingToken);

                if (expired.Count > 0)
                {
                    db.Placemarks.RemoveRange(expired);
                    await db.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation("PlacemarkCleanupService: удалено неодобренных меток — {Count}", expired.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PlacemarkCleanupService: ошибка очистки");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
}
