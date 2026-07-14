using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AccessibilityMap.Server.Data;
using AccessibilityMap.Server.Models;
using Microsoft.Extensions.Http;
using Microsoft.AspNetCore.Hosting;
using System.Globalization;
using System.IO;

namespace AccessibilityMap.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
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
    public async Task<IActionResult> GetAll()
    {
        var placemarks = await _db.Placemarks.ToListAsync();
        return Ok(placemarks.Select(ToDto).ToList());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.Placemarks.FindAsync(id);
        if (p == null) return NotFound();
        return Ok(ToDto(p));
    }

    [HttpGet("nearest")]
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
            return NotFound();
        }

        return Ok(ToDto(nearest.Placemark));
    }

    [HttpPost]
    public async Task<IActionResult> Add(PlacemarkModel placemark)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        placemark.CreatedAt = DateTime.UtcNow;
        _db.Placemarks.Add(placemark);
        await _db.SaveChangesAsync();
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
            var response = await client.GetStringAsync(url);
            return Content(response, "application/json");
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

        _db.Placemarks.Remove(placemark);
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("reverse-geocode")]
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
