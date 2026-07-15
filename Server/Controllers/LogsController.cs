using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AccessibilityMap.Server.Data;
using AccessibilityMap.Server.Models;

namespace AccessibilityMap.Server.Controllers;

[ApiController]
[Route("api/logs")]
[Authorize(Roles = "Developer")]
public class LogsController : ControllerBase
{
    private readonly AppDbContext _db;

    public LogsController(AppDbContext db)
    {
        _db = db;
    }

    // type: login | placemark | action (необязательно — без фильтра отдаёт всё)
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? type)
    {
        try
        {
            var query = _db.ActivityLogs.AsQueryable();
            if (!string.IsNullOrEmpty(type))
            {
                query = query.Where(l => l.Type == type);
            }

            var list = await query
                .OrderByDescending(l => l.Timestamp)
                .Take(500)
                .Select(l => new
                {
                    l.Id,
                    l.Timestamp,
                    l.Type,
                    l.UserName,
                    l.Description,
                    l.IpAddress
                })
                .ToListAsync();

            return Ok(list);
        }
        catch
        {
            return Ok(new List<object>());
        }
    }
}
