using System;
using System.ComponentModel.DataAnnotations;

namespace AccessibilityMap.Server.Models;

public class PlacemarkModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Укажите название")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Укажите адрес")]
    public string Address { get; set; } = string.Empty;

    public double Latitude { get; set; }
    public double Longitude { get; set; }

    [Required(ErrorMessage = "Выберите категорию")]
    public string Category { get; set; } = string.Empty;

    [Range(0, 3)]
    public int ScoreEntrance { get; set; }
    [Range(0, 3)]
    public int ScoreDoorWidth { get; set; }
    [Range(0, 3)]
    public int ScoreInternalPath { get; set; }
    [Range(0, 3)]
    public int ScoreSanitary { get; set; }
    [Range(0, 3)]
    public int ScoreInfo { get; set; }
    [Range(0, 3)]
    public int ScoreParking { get; set; }
    [Range(0, 3)]
    public int ScoreStaff { get; set; }

    public int TotalScore => ScoreEntrance + ScoreDoorWidth + ScoreInternalPath + ScoreSanitary + ScoreInfo + ScoreParking + ScoreStaff;

    public string Level => TotalScore switch
    {
        >= 18 => "green",
        >= 9 => "gold",
        _ => "red"
    };

    public string LevelText => TotalScore switch
    {
        >= 18 => "Полностью доступно",
        >= 9 => "Частично доступно",
        _ => "Недоступно"
    };

    public string Notes { get; set; } = string.Empty;
    public string? PhotoPath { get; set; }
    // Статус проверки: pending (на проверке) | approved (одобрено) | rejected (отклонено)
    public string VerificationStatus { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
