using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AccessibilityMap.Server.Data;
using AccessibilityMap.Server.Models;
using AccessibilityMap.Server.Services;
using Microsoft.Extensions.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
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
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _config;
    private readonly PhotoObjectStorage _photoStorage;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private string GeocoderApiKey => Environment.GetEnvironmentVariable("YANDEX_GEOCODER_API_KEY")
                                     ?? _config["Yandex:GeocoderApiKey"]
                                     ?? string.Empty;

    public PlacemarksController(AppDbContext db, IHttpClientFactory httpClientFactory, ILogger<PlacemarksController> logger, IWebHostEnvironment env, UserManager<ApplicationUser> userManager, IConfiguration config, PhotoObjectStorage photoStorage)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _env = env;
        _userManager = userManager;
        _config = config;
        _photoStorage = photoStorage;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] double? minLat = null, [FromQuery] double? minLon = null,
        [FromQuery] double? maxLat = null, [FromQuery] double? maxLon = null)
    {
        IQueryable<PlacemarkModel> query = _db.Placemarks.AsNoTracking();
        // Гость видит только approved. Любой вошедший пользователь видит также pending,
        // чтобы волонтёры не ставили дубли и понимали, что объект уже на проверке.
        if (User.Identity?.IsAuthenticated != true)
            query = query.Where(p => p.VerificationStatus == "approved");

        // Необязательная выборка по видимой области уже готова для следующего этапа
        // масштабирования; старый клиент без параметров остаётся совместимым.
        if (minLat.HasValue && minLon.HasValue && maxLat.HasValue && maxLon.HasValue)
        {
            if (minLat > maxLat || minLon > maxLon) return BadRequest(new { error = "Некорректные границы карты" });
            var south = minLat.Value;
            var west = minLon.Value;
            var north = maxLat.Value;
            var east = maxLon.Value;
            query = query.Where(p => p.Latitude >= south && p.Latitude <= north && p.Longitude >= west && p.Longitude <= east);
        }

        var placemarks = await query.OrderByDescending(p => p.CreatedAt).Take(10_000).ToListAsync();
        return Ok(placemarks.Select(ToDto).ToList());
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.Placemarks.FindAsync(id);
        if (p == null) return NotFound();
        if (User.Identity?.IsAuthenticated != true && p.VerificationStatus != "approved") return NotFound();
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

        var nearestQuery = _db.Placemarks.AsQueryable();
        if (User.Identity?.IsAuthenticated != true)
            nearestQuery = nearestQuery.Where(p => p.VerificationStatus == "approved");
        var placemarks = await nearestQuery.ToListAsync();

        // Хаверсин (метры) вместо евклидова расстояния по сырым градусам.
        var nearest = placemarks
            .Select(p => new { Placemark = p, Distance = Haversine(latitude, longitude, p.Latitude, p.Longitude) })
            .Where(x => x.Distance < 0.5) // проверка дубля отключена практически полностью: только точное попадание
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

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(user.FullName) || string.IsNullOrWhiteSpace(user.DateOfBirth))
            return BadRequest(new { error = "Заполните ФИО и дату рождения в профиле, чтобы добавлять метки" });

        var allowedCategories = new[] { "Поликлиника", "Аптека", "Магазин", "Администрация", "Культура", "Образование", "Другое" };
        if (!allowedCategories.Contains(placemark.Category))
            return BadRequest(new { error = "Недопустимая категория" });
        if (placemark.Latitude == 0 && placemark.Longitude == 0)
            return BadRequest(new { error = "Не указано положение объекта на карте" });

        placemark.Name = placemark.Name.Trim();
        placemark.Address = placemark.Address.Trim();
        placemark.Notes = placemark.Notes?.Trim() ?? string.Empty;
        placemark.Likes = 0;
        placemark.Dislikes = 0;
        placemark.CreatedAt = DateTime.UtcNow;
        // Обязательная модерация: новые метки появляются на публичной карте
        // только после одобрения управляющим/разработчиком (verificationStatus=approved).
        placemark.VerificationStatus = "pending";
        // Неодобренные метки живут в БД ~сутки, затем авто-удаляются фоновой службой.
        placemark.ExpiresAt = DateTime.UtcNow.AddHours(24);
        placemark.CreatedByUserId = user.Id;
        placemark.CreatedByFullName = user.FullName;
        placemark.PhotoPaths = NormalizePhotoPaths(placemark.PhotoPaths ?? placemark.PhotoPath);
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
    [Authorize(Roles = "Manager,Developer")]
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
        placemark.PhotoPaths = NormalizePhotoPaths(updated.PhotoPaths ?? updated.PhotoPath);

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
            if (string.IsNullOrWhiteSpace(GeocoderApiKey)) return StatusCode(503, "YANDEX_GEOCODER_API_KEY не задан");
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
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> Delete(int id)
    {
        var placemark = await _db.Placemarks.FindAsync(id);
        if (placemark == null)
        {
            return NotFound();
        }

        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "action",
                UserName = User.Identity?.Name,
                Description = $"Удалена метка «{placemark.Name}»",
                IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString()
            });
        }
        catch { }
        _db.Placemarks.Remove(placemark);
        await _db.SaveChangesAsync();
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
            if (string.IsNullOrWhiteSpace(GeocoderApiKey)) return Ok(new { items = new List<object>() });
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
            if (string.IsNullOrWhiteSpace(GeocoderApiKey)) return Ok(new { address = "Адрес не найден" });
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

        if (file.Length > 5 * 1024 * 1024)
        {
            return BadRequest(new { error = "Фото слишком большое (максимум 5 МБ)" });
        }

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var detected = DetectImageType(bytes);
        if (detected is null)
        {
            return BadRequest(new { error = "Содержимое файла не является поддерживаемым изображением (JPEG, PNG, GIF или WebP)" });
        }

        // Имя и MIME формируются по сигнатуре содержимого, а не по присланному имени.
        var fileName = Guid.NewGuid().ToString("N") + detected.Value.Extension;

        try
        {
            var photo = new PhotoModel
            {
                FileName = fileName,
                ContentType = detected.Value.ContentType,
                Data = bytes,
                UploadedAt = DateTime.UtcNow
            };
            _db.Photos.Add(photo);
            await _db.SaveChangesAsync();

            // New photos go to Object Storage when configured. Keep the database
            // bytes only if the upload fails, so an S3 incident cannot lose data.
            if (_photoStorage.IsConfigured)
            {
                try
                {
                    await _photoStorage.PutAsync(fileName, photo.ContentType, bytes, HttpContext.RequestAborted);
                    photo.Data = Array.Empty<byte>();
                    try
                    {
                        await _db.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        // The initial DB copy is already durable. Duplicate storage is
                        // safe and can be cleaned only after a later checksum audit.
                        _logger.LogWarning(ex, "S3 contains {FileName}, but its database BLOB could not be cleared", fileName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "S3 upload failed for {FileName}; the database copy was retained", fileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Photo upload failed");
            return StatusCode(500, new { error = "Не удалось сохранить фото в базу данных" });
        }

        return Ok(new { fileName });
    }

    [HttpGet("/api/photos/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPhoto(string fileName)
    {
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { error = "Некорректное имя файла" });

        // Неодобренные вложения доступны только вошедшим участникам. Гость не может
        // получить скрытую фотографию, просто угадав URL из истории или логов.
        var isAuthenticated = User.Identity?.IsAuthenticated == true;
        if (!isAuthenticated)
        {
            var isPublic = await _db.Placemarks.AnyAsync(p =>
                p.VerificationStatus == "approved" &&
                (p.PhotoPath == fileName || (p.PhotoPaths != null && p.PhotoPaths.Contains(fileName))));
            if (!isPublic) return NotFound();
            Response.Headers["Cache-Control"] = "public,max-age=86400";
        }
        else
        {
            Response.Headers["Cache-Control"] = "private,max-age=300";
        }

        if (_photoStorage.IsConfigured)
        {
            var storedPhoto = await _photoStorage.GetAsync(fileName, HttpContext.RequestAborted);
            if (storedPhoto is not null)
                return File(storedPhoto.Data, storedPhoto.ContentType);
        }

        // Read fallback for photos not migrated yet or retained after a failed S3 upload.
        var dbPhoto = await _db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.FileName == fileName);
        if (dbPhoto is { Data.Length: > 0 })
            return File(dbPhoto.Data, dbPhoto.ContentType);

        // fallback для старых локальных файлов, если они ещё есть на диске
        var uploads = Path.Combine(_env.ContentRootPath, "uploads");
        var fullPath = Path.Combine(uploads, fileName);
        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        return PhysicalFile(fullPath, GetContentType(fileName));
    }

    private static (string Extension, string ContentType)? DetectImageType(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return (".jpg", "image/jpeg");
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return (".png", "image/png");
        if (data.Length >= 6 && (System.Text.Encoding.ASCII.GetString(data, 0, 6) is "GIF87a" or "GIF89a"))
            return (".gif", "image/gif");
        if (data.Length >= 12 && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP")
            return (".webp", "image/webp");
        return null;
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
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

    [HttpPost("batch-verify")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> BatchVerify([FromBody] BatchVerifyModel model)
    {
        if (model.Ids == null || model.Ids.Count == 0) return BadRequest(new { error = "Не выбраны метки" });
        if (model.Status != "approved" && model.Status != "rejected") return BadRequest(new { error = "Недопустимый статус" });
        var list = await _db.Placemarks.Where(p => model.Ids.Contains(p.Id)).ToListAsync();
        foreach (var p in list) p.VerificationStatus = model.Status;
        await _db.SaveChangesAsync();
        return Ok(new { count = list.Count });
    }

    [HttpPost("batch-delete")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> BatchDelete([FromBody] BatchIdsModel model)
    {
        if (model.Ids == null || model.Ids.Count == 0) return BadRequest(new { error = "Не выбраны метки" });
        var list = await _db.Placemarks.Where(p => model.Ids.Contains(p.Id)).ToListAsync();
        _db.Placemarks.RemoveRange(list);
        await _db.SaveChangesAsync();
        return Ok(new { count = list.Count });
    }

    [HttpPost("{id}/vote")]
    [AllowAnonymous]
    public IActionResult Vote(int id, [FromBody] VoteModel model)
    {
        // Лайки/дизлайки отключены по требованию: не оставляем открытый публичный endpoint для накрутки.
        return StatusCode(410, new { error = "Голосование отключено" });
    }

    [HttpGet("attachments")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> Attachments()
    {
        var list = await _db.Placemarks.ToListAsync();
        return Ok(list.Where(p => GetPhotos(p).Any()).Select(p => new
        {
            p.Id,
            p.Name,
            p.Address,
            p.VerificationStatus,
            Photos = GetPhotos(p).Select(x => "/api/photos/" + x).ToList()
        }).ToList());
    }


    private string GetVoterKey()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            var id = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(id)) return "user:" + id;
        }

        var ip = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = Request.Headers.UserAgent.ToString();
        return "anon:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ip + "|" + ua)));
    }

    public class VerifyModel
    {
        public string Status { get; set; } = "";
    }
    public class BatchVerifyModel { public List<int> Ids { get; set; } = new(); public string Status { get; set; } = ""; }
    public class BatchIdsModel { public List<int> Ids { get; set; } = new(); }
    public class VoteModel { public int Value { get; set; } }

    private static string NormalizePhotoPaths(string? raw)
    {
        var items = (raw ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(10);
        return string.Join(';', items);
    }

    private static List<string> GetPhotos(PlacemarkModel p)
    {
        var raw = string.IsNullOrWhiteSpace(p.PhotoPaths) ? p.PhotoPath : p.PhotoPaths;
        return NormalizePhotoPaths(raw).Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
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
        PhotoUrl = GetPhotos(p).FirstOrDefault() is string first ? "/api/photos/" + first : null,
        PhotoPath = p.PhotoPath,
        PhotoPaths = p.PhotoPaths,
        Photos = GetPhotos(p).Select(x => "/api/photos/" + x).ToList(),
        VerificationStatus = p.VerificationStatus,
        CreatedByFullName = p.CreatedByFullName,
        Likes = p.Likes,
        Dislikes = p.Dislikes,
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
