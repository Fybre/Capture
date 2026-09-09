using Capture.Core.CaptureProfiles;
using Capture.Core.Profiles;

namespace Capture.App.Services;

/// <summary>Reassigns every id inside an imported <see cref="CaptureProfile"/> — the profile itself, its
/// document types, rules, fields, scripts, and exports — so an imported profile never collides with one
/// already on disk, while keeping every internal cross-reference (a field's <c>BoundaryRuleId</c>, an
/// export's <c>FieldIds</c>/<c>ThereforeFieldMappings</c>, the profile's <c>DefaultDocumentTypeId</c>)
/// pointing at the right, newly-assigned id. Mirrors <c>CaptureProfileDesignerViewModel.AssignNewIds</c>
/// (used for in-profile document type duplication), but scoped to the whole profile, since import brings
/// in a batch and every document type at once rather than a single copied type.</summary>
public static class CaptureProfileImportIds
{
    public static void Reassign(CaptureProfile profile)
    {
        profile.Id = Guid.NewGuid();

        // A field's BoundaryRuleId can reference a start rule from either the batch or its own document
        // type (see ProfileApplicator/CapturePlanner), so every start/recognition rule id is remapped
        // together in one dictionary rather than per-scope.
        var ruleIds = profile.Batch.StartRules.Rules
            .Concat(profile.DocumentTypes.SelectMany(type => type.RecognitionRules.Rules.Concat(type.StartRules.Rules)))
            .ToDictionary(rule => rule.Id, _ => Guid.NewGuid());
        foreach (var rule in profile.Batch.StartRules.Rules)
            rule.Id = ruleIds[rule.Id];
        foreach (var type in profile.DocumentTypes)
            foreach (var rule in type.RecognitionRules.Rules.Concat(type.StartRules.Rules))
                rule.Id = ruleIds[rule.Id];

        var batchFieldIds = ReassignFields(profile.Batch.Fields, ruleIds);
        foreach (var script in profile.Batch.Scripts)
            script.Id = Guid.NewGuid();

        var typeIds = profile.DocumentTypes.ToDictionary(type => type.Id, _ => Guid.NewGuid());
        if (profile.DefaultDocumentTypeId is { } defaultId && typeIds.TryGetValue(defaultId, out var newDefaultId))
            profile.DefaultDocumentTypeId = newDefaultId;

        foreach (var type in profile.DocumentTypes)
        {
            type.Id = typeIds[type.Id];
            var typeFieldIds = ReassignFields(type.Fields, ruleIds);
            foreach (var script in type.Scripts)
                script.Id = Guid.NewGuid();

            // An export's field selection can reference both this document type's own fields and the
            // profile's shared batch fields (see ExportDefinitionRow), so both maps apply when remapping it.
            foreach (var export in type.Exports)
            {
                export.Id = Guid.NewGuid();
                export.FieldIds = export.FieldIds
                    .Select(fieldId => typeFieldIds.TryGetValue(fieldId, out var newId) ? newId
                        : batchFieldIds.TryGetValue(fieldId, out var newBatchId) ? newBatchId
                        : (Guid?)null)
                    .Where(id => id is not null)
                    .Select(id => id!.Value)
                    .ToList();
                foreach (var mapping in export.ThereforeFieldMappings)
                    if (mapping.IndexFieldId is { } fieldId)
                        mapping.IndexFieldId = typeFieldIds.TryGetValue(fieldId, out var newId) ? newId
                            : batchFieldIds.TryGetValue(fieldId, out var newBatchId) ? newBatchId
                            : null;
            }
        }
    }

    private static Dictionary<Guid, Guid> ReassignFields(List<IndexField> fields, IReadOnlyDictionary<Guid, Guid> ruleIds)
    {
        var fieldIds = fields.ToDictionary(field => field.Id, _ => Guid.NewGuid());
        foreach (var field in fields)
        {
            field.Id = fieldIds[field.Id];
            if (field.BoundaryRuleId is { } boundaryId && ruleIds.TryGetValue(boundaryId, out var newBoundaryId))
                field.BoundaryRuleId = newBoundaryId;
        }
        return fieldIds;
    }
}
