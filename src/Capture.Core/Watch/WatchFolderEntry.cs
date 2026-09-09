namespace Capture.Core.Watch;

public sealed class WatchFolderEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string? Folder { get; set; }
    public Guid? CaptureProfileId { get; set; }
    public int SettleMilliseconds { get; set; } = 2000;
}
