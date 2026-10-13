using Capture.Core.CaptureProfiles;
using Capture.Core.Models;

namespace Capture.Core.Lattice;

/// <summary>An OCR service outside the app (Azure AI Document Intelligence), reached through a connection
/// set up in Settings ▸ Connections.</summary>
public interface IExternalOcrEngine
{
    /// <summary>Reads one page image. Word positions are in the image's pixels, confidence 0–100.
    /// Throws when the service can't be reached or refuses the page.</summary>
    Task<OcrResult> RecognizeAsync(Guid? connectionId, string imagePath, CancellationToken cancellationToken = default);
}

/// <summary>Finds the OCR settings for a page of an already-captured document (a page added or rotated
/// in review): those of the capture profile its document type belongs to.</summary>
public interface IOcrOptionsResolver
{
    Task<OcrOptions> ResolveAsync(CaptureDocument document, CancellationToken cancellationToken = default);
}

public sealed class CaptureProfileOcrOptionsResolver(ICaptureProfileStore profiles) : IOcrOptionsResolver
{
    public async Task<OcrOptions> ResolveAsync(CaptureDocument document, CancellationToken cancellationToken = default)
    {
        if (document.ProfileId is not { } typeId)
            return OcrOptions.BuiltInOnly;
        var all = await profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(profile => profile.DocumentTypes.Any(type => type.Id == typeId))?.OcrOptions
            ?? OcrOptions.BuiltInOnly;
    }
}
