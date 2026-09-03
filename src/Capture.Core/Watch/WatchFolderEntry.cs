namespace Capture.Core.Watch;

public sealed class WatchFolderEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string? Folder { get; set; }
    public Guid? ProfileId { get; set; }
    public int SettleMilliseconds { get; set; } = 2000;
    public Guid? BatchProfileId { get; set; }
    public Guid? ImportProfileId { get; set; }
}
