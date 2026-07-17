using System;

namespace AccessibilityMap.Server.Models;

public class PlacemarkVoteModel
{
    public int Id { get; set; }
    public int PlacemarkId { get; set; }
    public string VoterKey { get; set; } = string.Empty;
    public int Value { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
