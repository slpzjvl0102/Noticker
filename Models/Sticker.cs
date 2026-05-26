namespace Noticker.Models;

public class Sticker
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? NotionPageId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Category { get; set; }
    public string MonitorDeviceName { get; set; } = "";
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int Width { get; set; } = 250;
    public int Height { get; set; } = 300;
    public string SyncState { get; set; } = "pending";
    public int RetryCount { get; set; }
    public string? LastSyncedAt { get; set; }
    public string? BodyRtf { get; set; }
    public string FontFamily { get; set; } = "";
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("O");
}
