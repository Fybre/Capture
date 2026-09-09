using System.Collections;
using Capture.Core.Models;

namespace Capture.Scripting;

/// <summary>Case-insensitive by-name lookup (matching <c>DefaultValueTemplateEvaluator</c>'s existing
/// token-matching convention) plus enumeration, wrapping every field of the current document as
/// <typeparamref name="TAccessor"/>.</summary>
public sealed class ScriptFieldCollection<TAccessor> : IEnumerable<TAccessor>
{
    private readonly List<TAccessor> _items;
    private readonly Dictionary<string, TAccessor> _byName;

    internal ScriptFieldCollection(IReadOnlyList<IndexValue> values, Func<IndexValue, TAccessor> wrap)
    {
        _items = values.Select(wrap).ToList();
        _byName = new Dictionary<string, TAccessor>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < values.Count; i++)
            _byName[values[i].FieldName] = _items[i];
    }

    /// <summary>Throws <see cref="KeyNotFoundException"/> for an unknown field name — a script typo
    /// should fail loudly during "Run test" rather than silently resolve to nothing, unlike
    /// <c>DefaultValueTemplateEvaluator</c>'s tokens, which resolve blank on purpose because a template
    /// typo must never break indexing. A script is expected to be tested before being enabled for real
    /// import, so failing loudly here is the more useful behavior.</summary>
    public TAccessor this[string name] =>
        _byName.TryGetValue(name, out var accessor)
            ? accessor
            : throw new KeyNotFoundException($"No field named \"{name}\" on this document.");

    public bool TryGet(string name, out TAccessor accessor) => _byName.TryGetValue(name, out accessor!);

    public int Count => _items.Count;

    public IEnumerator<TAccessor> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
