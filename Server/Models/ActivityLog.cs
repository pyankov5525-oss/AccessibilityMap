namespace AccessibilityMap.Server.Models;

/// <summary>
/// Журнал действий для разработчика: входы, постановка меток, прочие действия.
/// Type: "login" | "placemark" | "action"
/// </summary>
public class ActivityLog
{
    public int Id { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Type { get; set; } = "action";

    public string? UserName { get; set; }

    public string Description { get; set; } = "";

    public string? IpAddress { get; set; }
}
