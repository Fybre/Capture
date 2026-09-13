namespace Capture.Core.CaptureProfiles;

/// <summary>The profile used when the user has no Capture Profile selected — shown as the always-first
/// "None" entry in the picker. Its defaults (no document types, no batch-start rules) already make every
/// captured page fall through to CapturePlanner's null-type fallback and CapturePlanMaterializer's
/// generic-batch join — so ad hoc imports get no fields, land in the existing "No profile applied" group,
/// and join the currently open batch exactly like a real profile would. This single static instance is a
/// real, permanent, non-removable entry in MainViewModel.CaptureProfilePickerItems alongside the user's
/// own profiles — never persisted to the capture-profile store itself (so it can't be edited or deleted
/// via the Capture Profiles dialog), but its fixed <see cref="UnsortedId"/> is what
/// WatchSettings.LastCaptureProfileId stores when it's the active choice, exactly like any real profile's
/// Id. See MainViewModel.SelectedCaptureProfileIdOrNone for the picker-side lookup.</summary>
public static class BuiltInCaptureProfiles
{
    public static readonly Guid UnsortedId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static CaptureProfile Unsorted { get; } = new() { Id = UnsortedId, Name = "None" };
}
