namespace Capture.Core.Scripting;

/// <summary>Outcome of one script execution. Plain data — no exception ever crosses the
/// <see cref="IFieldScriptRunner"/> boundary.</summary>
public sealed record ScriptRunResult(bool Success, string? Value, string? ErrorMessage, TimeSpan Elapsed)
{
    public static ScriptRunResult Ok(string? value, TimeSpan elapsed) => new(true, value, null, elapsed);

    public static ScriptRunResult Failed(string errorMessage, TimeSpan elapsed) => new(false, null, errorMessage, elapsed);
}
