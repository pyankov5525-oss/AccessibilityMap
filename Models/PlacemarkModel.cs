namespace AccessibilityMap.Server.Models;

public class PlacemarkModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Category { get; set; } = string.Empty;

    // 7 критериев оценки
    public int ScoreEntrance { get; set; }
    public int ScoreDoorWidth { get; set; }
    public int ScoreInternalPath { get; set; }
    public int ScoreSanitary { get; set; }
    public int ScoreInfo { get; set; }
    public int ScoreParking { get; set; }
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
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}