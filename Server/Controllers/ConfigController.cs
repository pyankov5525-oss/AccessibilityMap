using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccessibilityMap.Server.Controllers;

[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<ConfigController> logger)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("yandex-maps-key")]
    [AllowAnonymous]
    public IActionResult GetYandexMapsKey()
    {
        var key = Environment.GetEnvironmentVariable("YANDEX_MAPS_API_KEY")
                  ?? _config["Yandex:MapsApiKey"]
                  ?? string.Empty;
        return Ok(new { apiKey = key });
    }

    [HttpGet("yandex-maps-js")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> GetYandexMapsScript()
    {
        var key = Environment.GetEnvironmentVariable("YANDEX_MAPS_API_KEY")
                  ?? _config["Yandex:MapsApiKey"]
                  ?? string.Empty;

        var url = "https://api-maps.yandex.ru/2.1/?" +
                  (string.IsNullOrWhiteSpace(key) ? string.Empty : $"apikey={Uri.EscapeDataString(key)}&") +
                  "lang=ru_RU&load=package.full";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var js = await client.GetStringAsync(url);
            return Content(js, "application/javascript; charset=utf-8");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy Yandex Maps script");
            return StatusCode(502, "console.error('Не удалось загрузить Яндекс.Карты через серверный прокси');");
        }
    }
}
