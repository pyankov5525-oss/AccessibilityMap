using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccessibilityMap.Server.Controllers;

[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly IConfiguration _config;

    public ConfigController(IConfiguration config)
    {
        _config = config;
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
}
