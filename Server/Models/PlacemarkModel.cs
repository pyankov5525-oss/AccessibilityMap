using System;
using System.ComponentModel.DataAnnotations;

namespace AccessibilityMap.Server.Models;

public class PlacemarkModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Укажите название")]
    [MaxLength(160, ErrorMessage = "Название не должно превышать 160 символов")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Укажите адрес")]
    [MaxLength(300, ErrorMessage = "Адрес не должен превышать 300 символов")]
    public string Address { get; set; } = string.Empty;

    [Range(-90, 90, ErrorMessage = "Некорректная широта")]
    public double Latitude { get; set; }
    [Range(-180, 180, ErrorMessage = "Некорректная долгота")]
    public double Longitude { get; set; }

    [Required(ErrorMessage = "Выберите категорию")]
    [MaxLength(80)]
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

    [MaxLength(2000, ErrorMessage = "Примечание не должно превышать 2000 символов")]
    public string Notes { get; set; } = string.Empty;
    [MaxLength(255)]
    public string? PhotoPath { get; set; }
    // Несколько фотографий через ; (до 10). PhotoPath оставлен для совместимости.
    [MaxLength(3000)]
    public string? PhotoPaths { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? CreatedByFullName { get; set; }
    public int Likes { get; set; } = 0;
    public int Dislikes { get; set; } = 0;
    // Статус проверки: pending (на проверке) | approved (одобрено) | rejected (отклонено)
    public string VerificationStatus { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // Для неодобренных меток — время авто-удаления (≈сутки). null у одобренных.
    public DateTime? ExpiresAt { get; set; } = null;
}
