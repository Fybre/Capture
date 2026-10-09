using System.Runtime.InteropServices;

namespace Capture.Core.Paths;

/// <summary>Whether Capture is running as an installed MSIX package (the Microsoft Store build) rather
/// than from the plain installer, a dev build, or another OS. A packaged build stores its data in the
/// package's own LocalState folder and leaves updates to the Store.</summary>
public static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The package family name (e.g. "Fybre.Capture_abc123xyz"), or null when not packaged.</summary>
    public static string? FamilyName { get; } = Detect();

    public static bool IsPackaged => FamilyName is not null;

    private static string? Detect()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return null;

        try
        {
            uint length = 0;
            // Not packaged → APPMODEL_ERROR_NO_PACKAGE (15700); packaged → asks for a bigger buffer.
            if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer || length == 0)
                return null;

            var buffer = new char[length];
            return GetCurrentPackageFamilyName(ref length, buffer) == 0
                ? new string(buffer, 0, (int)length - 1)
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);
}
