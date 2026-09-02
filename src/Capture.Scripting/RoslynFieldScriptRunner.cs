using System.Diagnostics;
using System.Reflection;
using Capture.Core.Profiles;
using Capture.Core.Scripting;
using Capture.Core.Watch;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Capture.Scripting;

public sealed class RoslynFieldScriptRunner : IFieldScriptRunner
{
    private static readonly ScriptOptions ProfileScriptOptions = BuildOptions();
    private static readonly ScriptOptions FieldExpressionOptions = BuildOptions();

    private readonly IWatchSettingsStore _settings;
    private readonly HttpClient _http;
    private readonly CompiledScriptCache _profileScriptCache = new();
    private readonly CompiledScriptCache _fieldExpressionCache = new();

    public RoslynFieldScriptRunner(IWatchSettingsStore settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
    }

    // Mirrors OpenAiExtractor.IsConfigured's existing blocking-load pattern for a synchronous
    // "is this feature turned on" check — WatchSettings.LoadAsync just reads a small local JSON file.
    public bool IsAvailable => Task.Run(() => _settings.LoadAsync()).GetAwaiter().GetResult().AllowFieldScripts;

    public async Task<ScriptRunResult> RunProfileScriptAsync(
        FieldScript script,
        ScriptExecutionContext context,
        CancellationToken cancellationToken = default,
        string sharedSource = "")
    {
        var stopwatch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, script.TimeoutSeconds)));

        try
        {
            var compiled = _profileScriptCache.GetOrCompile(script.Id, Combine(sharedSource, script.Source), ProfileScriptOptions, typeof(ScriptGlobals));
            var globals = new ScriptGlobals(context, _http, script.Name, cts.Token);
            await compiled.RunAsync(globals, cancellationToken: cts.Token).ConfigureAwait(false);
            return ScriptRunResult.Ok(null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ScriptRunResult.Failed($"Timed out after {script.TimeoutSeconds}s", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return ScriptRunResult.Failed(DescribeError(ex), stopwatch.Elapsed);
        }
    }

    public async Task<ScriptRunResult> RunFieldExpressionAsync(
        Guid scriptCacheKey,
        string expression,
        ScriptExecutionContext context,
        CancellationToken cancellationToken = default,
        string sharedSource = "")
    {
        var stopwatch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var compiled = _fieldExpressionCache.GetOrCompile(scriptCacheKey, Combine(sharedSource, expression), FieldExpressionOptions, typeof(ReadOnlyScriptGlobals));
            // scriptCacheKey doubles as the id of the field this expression belongs to (every real
            // caller passes that field's own IndexField.Id) — used here to resolve its own
            // pre-evaluation value for the Value shorthand, not just as a compile-cache key.
            var selfValue = context.Values.FirstOrDefault(v => v.FieldId == scriptCacheKey)?.Value ?? string.Empty;
            var globals = new ReadOnlyScriptGlobals(context, _http, "field expression", selfValue, cts.Token);
            var state = await compiled.RunAsync(globals, cancellationToken: cts.Token).ConfigureAwait(false);
            return ScriptRunResult.Ok(state.ReturnValue?.ToString() ?? string.Empty, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ScriptRunResult.Failed("Timed out after 10s", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return ScriptRunResult.Failed(DescribeError(ex), stopwatch.Elapsed);
        }
    }

    // Shared helper functions compile ahead of the script's own text, in the same top-level scope — a
    // script can call them directly, no namespace/class qualifier needed. Compiled cache key already
    // covers this full combined string (CompiledScriptCache hashes whatever source it's given), so
    // editing the shared source correctly invalidates every script/expression that used the old text.
    private static string Combine(string sharedSource, string source) =>
        string.IsNullOrWhiteSpace(sharedSource) ? source : sharedSource + "\n" + source;

    // A compilation error's Diagnostics collection is far more useful to a script author than the
    // generic exception message ("script returned no diagnostics" territory) — surface it directly.
    private static string DescribeError(Exception ex) => ex switch
    {
        CompilationErrorException compilation => string.Join("; ", compilation.Diagnostics),
        _ => ex.Message
    };

    private static ScriptOptions BuildOptions() => ScriptOptions.Default
        .WithReferences(
            typeof(object).Assembly,
            typeof(Uri).Assembly,
            typeof(System.Linq.Enumerable).Assembly,
            typeof(System.Text.RegularExpressions.Regex).Assembly,
            typeof(System.Net.Http.HttpClient).Assembly,
            typeof(System.Text.Json.JsonSerializer).Assembly,
            typeof(Capture.Core.Models.IndexValue).Assembly,
            Assembly.GetExecutingAssembly())
        .WithImports(
            "System",
            "System.Linq",
            "System.Net.Http",
            "System.Text",
            "System.Text.Json",
            "System.Text.RegularExpressions",
            "System.Threading.Tasks");
}
