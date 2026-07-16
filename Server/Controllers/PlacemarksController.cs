using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AccessibilityMap.Server.Data;
using AccessibilityMap.Server.Models;
using Microsoft.Extensions.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using System.IO;

namespace AccessibilityMap.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PlacemarksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlacemarksController> _logger;
    private readonly IWebHostEnvironment _env;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const string GeocoderApiKey = "36a55651-ec1b-4152-bbf5-1875ec574586";

    public PlacemarksController(AppDbContext db, IHttpClientFactory httpClientFactory, ILogger<PlacemarksController> logger, IWebHostEnvironment env)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _env = env;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        // Публичным (анонимным) пользователям отдаём только одобренные метки
        // (обязательная модерация — новые видны на карте лишь после апрува).
        // Авторизованным (управляющий/разработчик в режиме проверки) — все,
        // чтобы можно было одобрить/вернуть.
        IQueryable<PlacemarkModel> query = _db.Placemarks;
        if (!User.Identity!.IsAuthenticated)
            query = query.Where(p => p.VerificationStatus == "approved");
        var placemarks = await query.ToListAsync();
        return Ok(placemarks.Select(ToDto).ToList());
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.Placemarks.FindAsync(id);
        if (p == null) return NotFound();
        return Ok(ToDto(p));
    }

    [HttpGet("nearest")]
    [AllowAnonymous]
    public async Task<IActionResult> GetNearest([FromQuery] string lat, [FromQuery] string lon)
    {
        if (!double.TryParse(lat, Invariant, out double latitude) ||
            !double.TryParse(lon, Invariant, out double longitude))
        {
            return BadRequest("Invalid coordinates");
        }

        var placemarks = await _db.Placemarks.ToListAsync();

        // Хаверсин (метры) вместо евклидова расстояния по сырым градусам.
        var nearest = placemarks
            .Select(p => new { Placemark = p, Distance = Haversine(latitude, longitude, p.Latitude, p.Longitude) })
            .Where(x => x.Distance < 50) // в радиусе 50 м считаем дубликатом
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        if (nearest == null)
        {
            // Нет ближайшей метки в радиусе 50 м — это нормально (не дубль).
            // Возвращаем 200 с id=0, чтобы в консоли браузера не было шума 404.
            return Ok(new { id = 0 });
        }

        return Ok(ToDto(nearest.Placemark));
    }

    // Метки «на удержании»: неодобренные (pending/rejected), ещё не удалённые по сроку.
    // Доступно управляющим/разработчикам, чтобы «вернуть» при ошибочном нажатии.
    [HttpGet("holds")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> GetHolds()
    {
        try
        {
            var now = DateTime.UtcNow;
            var holds = await _db.Placemarks
                .Where(p => p.VerificationStatus != "approved" && p.ExpiresAt != null && p.ExpiresAt > now)
                .ToListAsync();
            return Ok(holds.Select(ToDto).ToList());
        }
        catch
        {
            return Ok(new List<object>());
        }
    }

    [HttpPost]
    public async Task<IActionResult> Add(PlacemarkModel placemark)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        placemark.CreatedAt = DateTime.UtcNow;
        // Обязательная модерация: новые метки появляются на публичной карте
        // только после одобрения управляющим/разработчиком (verificationStatus=approved).
        placemark.VerificationStatus = "pending";
        // Неодобренные метки живут в БД ~сутки, затем авто-удаляются фоновой службой.
        placemark.ExpiresAt = DateTime.UtcNow.AddHours(24);
        _db.Placemarks.Add(placemark);
        await _db.SaveChangesAsync();
        // Лог — «лучшее усилие»: если таблица ещё не создана (старый файл БД), не падаем.
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "placemark",
                UserName = User.Identity?.Name,
                Description = $"Добавлена метка «{placemark.Name}» ({placemark.Address})",
                IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(ToDto(placemark));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, PlacemarkModel updated)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var placemark = await _db.Placemarks.FindAsync(id);
        if (placemark == null)
        {
            return NotFound();
        }

        placemark.Name = updated.Name;
        placemark.Address = updated.Address;
        placemark.Category = updated.Category;
        placemark.ScoreEntrance = updated.ScoreEntrance;
        placemark.ScoreDoorWidth = updated.ScoreDoorWidth;
        placemark.ScoreInternalPath = updated.ScoreInternalPath;
        placemark.ScoreSanitary = updated.ScoreSanitary;
        placemark.ScoreInfo = updated.ScoreInfo;
        placemark.ScoreParking = updated.ScoreParking;
        placemark.ScoreStaff = updated.ScoreStaff;
        placemark.Notes = updated.Notes;
        placemark.PhotoPath = updated.PhotoPath;

        await _db.SaveChangesAsync();
        return Ok(ToDto(placemark));
    }

    [HttpGet("geocode")]
    [AllowAnonymous]
    public async Task<IActionResult> Geocode([FromQuery] string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return BadRequest("Address required");
        }

        try
        {
            var url = $"https://geocode-maps.yandex.ru/1.x/?apikey={GeocoderApiKey}&geocode={Uri.EscapeDataString(address)}&format=json";
            var client = _httpClientFactory.CreateClient();
            var json = await client.GetStringAsync(url);

            // Возвращаем готовую структуру, чтобы клиенту не пришлось парсить
            // «сырой» ответ Яндекса. pos имеет вид "долгота широта".
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var members = doc.RootElement
                .GetProperty("response")
                .GetProperty("GeoObjectCollection")
                .GetProperty("featureMember");

            if (members.GetArrayLength() > 0)
            {
                var go = members[0].GetProperty("GeoObject");
                var pos = go.GetProperty("Point").GetProperty("pos").GetString() ?? "";
                var text = go.GetProperty("metaDataProperty")
                               .GetProperty("GeocoderMetaData")
                               .GetProperty("text").GetString() ?? "";
                var parts = pos.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], Invariant, out double lon) &&
                    double.TryParse(parts[1], Invariant, out double lat))
                {
                    return Ok(new { lat, lon, address = text });
                }
            }
            return Ok(new { lat = 0.0, lon = 0.0, address = "" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Geocode failed for {Address}", address);
            return StatusCode(502, "Ошибка геокодера");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var placemark = await _db.Placemarks.FindAsync(id);
        if (placemark == null)
        {
            return NotFound();
        }

        _db.ActivityLogs.Add(new ActivityLog
        {
            Type = "action",
            UserName = User.Identity?.Name,
            Description = $"Удалена метка «{placemark.Name}»",
            IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString()
        });
        _db.Placemarks.Remove(placemark);
        await _db.SaveChangesAsync();
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "action",
                UserName = User.Identity?.Name,
                Description = $"Удалена метка «{placemark.Name}»",
                IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok();
    }

    // Подсказки при поиске (несколько вариантов адреса, как в поиске Яндекса)
    [HttpGet("suggest")]
    [AllowAnonymous]
    public async Task<IActionResult> Suggest([FromQuery] string q, [FromQuery] int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(new { items = new List<object>() });
        try
        {
            var url = $"https://geocode-maps.yandex.ru/1.x/?apikey={GeocoderApiKey}&geocode={Uri.EscapeDataString(q)}&format=json&results={limit}";
            var client = _httpClientFactory.CreateClient();
            var json = await client.GetStringAsync(url);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var members = doc.RootElement
                .GetProperty("response")
                .GetProperty("GeoObjectCollection")
                .GetProperty("featureMember");
            var items = new List<object>();
            foreach (var m in members.EnumerateArray())
            {
                var go = m.GetProperty("GeoObject");
                var pos = go.GetProperty("Point").GetProperty("pos").GetString() ?? "";
                var text = go.GetProperty("metaDataProperty").GetProperty("GeocoderMetaData").GetProperty("text").GetString() ?? "";
                var parts = pos.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], Invariant, out double lon) &&
                    double.TryParse(parts[1], Invariant, out double lat))
                {
                    items.Add(new { lat, lon, address = text });
                }
            }
            return Ok(new { items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Suggest failed for {Q}", q);
            return Ok(new { items = new List<object>() });
        }
    }

    [HttpGet("reverse-geocode")]
    [AllowAnonymous]
    public async Task<IActionResult> ReverseGeocode([FromQuery] double lat, [FromQuery] double lon)
    {
        try
        {
            var url = $"https://geocode-maps.yandex.ru/1.x/?apikey={GeocoderApiKey}&geocode={lon},{lat}&format=json";
            var client = _httpClientFactory.CreateClient();
            var json = await client.GetStringAsync(url);

            var doc = System.Text.Json.JsonDocument.Parse(json);
            var members = doc.RootElement
                .GetProperty("response")
                .GetProperty("GeoObjectCollection")
                .GetProperty("featureMember");

            if (members.GetArrayLength() > 0)
            {
                var address = members[0]
                    .GetProperty("GeoObject")
                    .GetProperty("metaDataProperty")
                    .GetProperty("GeocoderMetaData")
                    .GetProperty("text")
                    .GetString();
                return Ok(new { address });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reverse geocode failed for {Lat},{Lon}", lat, lon);
        }

        return Ok(new { address = "Адрес не найден" });
    }

    [HttpPost("/api/photos")]
    public async Task<IActionResult> UploadPhoto(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "Файл не выбран" });
        }

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var ext = Path.GetExtension(file.FileName);
        if (!allowed.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Недопустимый тип файла (только изображения)" });
        }

        var uploads = Path.Combine(_env.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploads);

        var fileName = Guid.NewGuid().ToString("N") + ext;
        var fullPath = Path.Combine(uploads, fileName);
        await using var stream = System.IO.File.Create(fullPath);
        await file.CopyToAsync(stream);

        return Ok(new { fileName });
    }

    [HttpGet("/api/photos/{fileName}")]
    [AllowAnonymous]
    public IActionResult GetPhoto(string fileName)
    {
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { error = "Некорректное имя файла" });
        }

        var uploads = Path.Combine(_env.ContentRootPath, "uploads");
        var fullPath = Path.Combine(uploads, fileName);
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        return PhysicalFile(fullPath, contentType);
    }

    [HttpPost("{id}/verify")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> Verify(int id, [FromBody] VerifyModel model)
    {
        var p = await _db.Placemarks.FindAsync(id);
        if (p == null) return NotFound();
        if (model.Status != "approved" && model.Status != "rejected")
            return BadRequest(new { error = "Статус должен быть approved или rejected" });

        p.VerificationStatus = model.Status;
        await _db.SaveChangesAsync();
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "action",
                UserName = User.Identity?.Name,
                Description = $"Проверка метки «{p.Name}»: {model.Status}",
                IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(ToDto(p));
    }

    public class VerifyModel
    {
        public string Status { get; set; } = "";
    }

    private static object ToDto(PlacemarkModel p) => new
    {
        p.Id,
        p.Latitude,
        p.Longitude,
        p.Name,
        p.Address,
        p.Category,
        p.LevelText,
        p.Level,
        p.TotalScore,
        p.Notes,
        PhotoUrl = string.IsNullOrEmpty(p.PhotoPath) ? null : "/api/photos/" + p.PhotoPath,
        PhotoPath = p.PhotoPath,
        VerificationStatus = p.VerificationStatus,
        ExpiresAt = p.ExpiresAt,
        Scores = new
        {
            Entrance = p.ScoreEntrance,
            DoorWidth = p.ScoreDoorWidth,
            InternalPath = p.ScoreInternalPath,
            Sanitary = p.ScoreSanitary,
            Info = p.ScoreInfo,
            Parking = p.ScoreParking,
            Staff = p.ScoreStaff
        }
    };

    /// <summary>Расстояние между точками в метрах (формула Хаверсина).</summary>
    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000; // радиус Земли, м
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }
}
