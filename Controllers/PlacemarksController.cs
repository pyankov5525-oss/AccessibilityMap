using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AccessibilityMap.Server.Data;
using AccessibilityMap.Server.Models;

namespace AccessibilityMap.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PlacemarksController : ControllerBase
{
    private readonly AppDbContext _db;

    public PlacemarksController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var result = await _db.Placemarks.Select(p => new
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
        }).ToListAsync();

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.Placemarks.FindAsync(id);
        if (p == null) return NotFound();
        return Ok(new
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
        });
    }

    [HttpGet("nearest")]
    public async Task<IActionResult> GetNearest(
        [FromQuery] string lat,
        [FromQuery] string lon)
    {
        if (!double.TryParse(lat, System.Globalization.CultureInfo.InvariantCulture, out double latitude) ||
            !double.TryParse(lon, System.Globalization.CultureInfo.InvariantCulture, out double longitude))
            return BadRequest("Invalid coordinates");

        var placemarks = await _db.Placemarks.ToListAsync();

        var nearest = placemarks
            .Select(p => new
            {
                Placemark = p,
                Distance = Math.Sqrt(Math.Pow(p.Latitude - latitude, 2) + Math.Pow(p.Longitude - longitude, 2))
            })
            .Where(x => x.Distance < 0.00003)
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        if (nearest == null)
            return NotFound();

        var p = nearest.Placemark;
        return Ok(new
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
        });
    }

    [HttpPost]
    public async Task<IActionResult> Add(PlacemarkModel placemark)
    {
        placemark.CreatedAt = DateTime.Now;
        _db.Placemarks.Add(placemark);
        await _db.SaveChangesAsync();
        return Ok(placemark);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, PlacemarkModel updated)
    {
        var placemark = await _db.Placemarks.FindAsync(id);
        if (placemark == null)
            return NotFound();

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

        await _db.SaveChangesAsync();
        return Ok(placemark);
    }

    [HttpGet("geocode")]
    public async Task<IActionResult> Geocode([FromQuery] string address)
    {
        var url = $"https://geocode-maps.yandex.ru/1.x/?apikey=36a55651-ec1b-4152-bbf5-1875ec574586&geocode={Uri.EscapeDataString(address)}&format=json";

        using var client = new HttpClient();
        var response = await client.GetStringAsync(url);
        return Content(response, "application/json");
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var placemark = await _db.Placemarks.FindAsync(id);
        if (placemark == null)
            return NotFound();

        _db.Placemarks.Remove(placemark);
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("reverse-geocode")]
    public async Task<IActionResult> ReverseGeocode([FromQuery] double lat, [FromQuery] double lon)
    {
        var url = $"https://geocode-maps.yandex.ru/1.x/?apikey=36a55651-ec1b-4152-bbf5-1875ec574586&geocode={lon},{lat}&format=json";

        using var client = new HttpClient();
        var json = await client.GetStringAsync(url);

        try
        {
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
        catch { }

        return Ok(new { address = "Адрес не найден" });
    }
}