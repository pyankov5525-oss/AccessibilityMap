using AccessibilityMap.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AccessibilityMap.Server.Data;

/// <summary>
/// Раньше здесь создавались демонстрационные метки. Теперь демо-метки удаляются,
/// чтобы в рабочей базе оставались только реальные пользовательские данные.
/// </summary>
public static class DbInitializer
{
    private static readonly string[] DemoNames =
    {
        "Поклонная гора",
        "Центральный парк",
        "Петровский сквер",
        "Кафе «Воронеж»",
        "Ж/д вокзал Воронеж-1"
    };

    public static async Task SeedAsync(AppDbContext db)
    {
        var demo = await db.Placemarks
            .Where(p => DemoNames.Contains(p.Name))
            .ToListAsync();

        if (demo.Count > 0)
        {
            db.Placemarks.RemoveRange(demo);
            await db.SaveChangesAsync();
        }
    }
}
