using AccessibilityMap.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AccessibilityMap.Server.Data;

/// <summary>
/// Заполняет БД демо-данными (объекты Воронежа с разным уровнем доступности),
/// если та пуста. Идемпотентно.
/// </summary>
public static class DbInitializer
{
    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Placemarks.AnyAsync())
        {
            return;
        }

        var samples = new List<PlacemarkModel>
        {
            new()
            {
                Name = "Поклонная гора", Address = "Воронеж, Поклонная гора", Category = "Культура",
                Latitude = 51.6784, Longitude = 39.2105,
                ScoreEntrance = 3, ScoreDoorWidth = 3, ScoreInternalPath = 3, ScoreSanitary = 3,
                ScoreInfo = 3, ScoreParking = 2, ScoreStaff = 3,
                Notes = "Смотровая площадка с пандусом и тактильными указателями."
            },
            new()
            {
                Name = "Центральный парк", Address = "Воронеж, ул. Плехановская, 1", Category = "Культура",
                Latitude = 51.6601, Longitude = 39.2003,
                ScoreEntrance = 2, ScoreDoorWidth = 2, ScoreInternalPath = 2, ScoreSanitary = 2,
                ScoreInfo = 1, ScoreParking = 2, ScoreStaff = 2,
                Notes = "Есть тропинки, но мало указателей и озвучки."
            },
            new()
            {
                Name = "Петровский сквер", Address = "Воронеж, Петровский сквер", Category = "Культура",
                Latitude = 51.6572, Longitude = 39.2151,
                ScoreEntrance = 1, ScoreDoorWidth = 1, ScoreInternalPath = 1, ScoreSanitary = 0,
                ScoreInfo = 1, ScoreParking = 0, ScoreStaff = 1,
                Notes = "Бордюры без съездов, туалет недоступен."
            },
            new()
            {
                Name = "Кафе «Воронеж»", Address = "Воронеж, ул. Кирова, 5", Category = "Магазин",
                Latitude = 51.6705, Longitude = 39.1902,
                ScoreEntrance = 2, ScoreDoorWidth = 2, ScoreInternalPath = 1, ScoreSanitary = 2,
                ScoreInfo = 2, ScoreParking = 1, ScoreStaff = 2,
                Notes = "Вход со ступеньками, персонал помогает."
            },
            new()
            {
                Name = "Ж/д вокзал Воронеж-1", Address = "Воронеж, Привокзальная пл., 1", Category = "Администрация",
                Latitude = 51.6720, Longitude = 39.2070,
                ScoreEntrance = 1, ScoreDoorWidth = 1, ScoreInternalPath = 0, ScoreSanitary = 1,
                ScoreInfo = 1, ScoreParking = 1, ScoreStaff = 0,
                Notes = "Перрон недоступен без посторонней помощи."
            }
        };

        db.Placemarks.AddRange(samples);
        await db.SaveChangesAsync();
    }
}
