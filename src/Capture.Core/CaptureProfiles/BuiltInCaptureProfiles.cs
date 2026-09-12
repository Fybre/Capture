namespace Capture.Core.CaptureProfiles;

/// <summary>The profile used when the user has no Capture Profile selected — shown as the always-first
/// "None" entry in the picker. Its defaults (no document types, no batch-start rules) already make every
/// captured page fall through to CapturePlanner's null-type fallback and CapturePlanMaterializer's
/// generic-batch join — so ad hoc imports get no fields, land in the existing "No profile applied" group,
/// and join the currently open batch exactly like a real profile would. Never persisted: MainViewModel
/// translates between this sentinel and a null SelectedCaptureProfile at the UI boundary
/// (SelectedCaptureProfileOrNone), so "None" round-trips through settings exactly like no selection did.</summary>
public static class BuiltInCaptureProfiles
{
    public static readonly Guid UnsortedId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static CaptureProfile Unsorted { get; } = new() { Id = UnsortedId, Name = "None" };
}
