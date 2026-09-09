using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Capture.Scripting;

/// <summary>Caches compiled <see cref="Script{T}"/> instances keyed by (script id, source hash), so a
/// batch import of many documents against the same profile only pays Roslyn's real first-compile cost
/// (~1s+) once per script, not once per document. Re-hashes on every call, so an edited-and-saved
/// script (same id, new source) is never run stale — "Run test" after an edit always compiles the
/// current text.</summary>
internal sealed class CompiledScriptCache
{
    private readonly ConcurrentDictionary<(Guid Id, string Hash), Script<object>> _cache = new();

    public Script<object> GetOrCompile(Guid id, string source, ScriptOptions options, Type globalsType)
    {
        var hash = Hash(source);
        return _cache.GetOrAdd((id, hash), _ => CSharpScript.Create<object>(source, options, globalsType));
    }

    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
