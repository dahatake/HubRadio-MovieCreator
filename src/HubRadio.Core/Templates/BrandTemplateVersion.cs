using HubRadio.Core.Identity;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Templates;

public sealed class SeriesIdTag
{
}

public sealed class BrandTemplateVersion
{
    private readonly Dictionary<string, TemplateParameterDefinition> _definitionsByName;

    public BrandTemplateVersion(
        EntityId<SeriesIdTag> seriesId,
        TemplateReference reference,
        BrandTemplateKind kind,
        IReadOnlyList<TemplateParameterDefinition> parameterDefinitions)
    {
        ArgumentNullException.ThrowIfNull(seriesId);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(parameterDefinitions);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown brand-template kind.");
        }

        TemplateParameterDefinition[] copiedDefinitions =
            new TemplateParameterDefinition[parameterDefinitions.Count];
        _definitionsByName = new Dictionary<string, TemplateParameterDefinition>(
            parameterDefinitions.Count,
            StringComparer.Ordinal);

        for (int index = 0; index < copiedDefinitions.Length; index++)
        {
            TemplateParameterDefinition? definition = parameterDefinitions[index];
            if (definition is null)
            {
                throw new ArgumentException(
                    "Parameter definitions must not contain null elements.",
                    nameof(parameterDefinitions));
            }

            if (!_definitionsByName.TryAdd(definition.Name, definition))
            {
                throw new ArgumentException(
                    "Parameter definition names must be unique.",
                    nameof(parameterDefinitions));
            }

            copiedDefinitions[index] = definition;
        }

        SeriesId = seriesId;
        Reference = reference;
        Kind = kind;
        ParameterDefinitions = Array.AsReadOnly(copiedDefinitions);
    }

    public EntityId<SeriesIdTag> SeriesId { get; }

    public TemplateReference Reference { get; }

    public BrandTemplateKind Kind { get; }

    public IReadOnlyList<TemplateParameterDefinition> ParameterDefinitions { get; }

    public AppliedBrandTemplate ApplyEditorOverrides(
        IReadOnlyList<TemplateParameterAssignment> editorOverrides)
    {
        ArgumentNullException.ThrowIfNull(editorOverrides);

        Dictionary<string, TemplateParameterAssignment> overridesByName =
            new(editorOverrides.Count, StringComparer.Ordinal);

        for (int index = 0; index < editorOverrides.Count; index++)
        {
            TemplateParameterAssignment? assignment = editorOverrides[index];
            if (assignment is null)
            {
                throw new ArgumentException(
                    "Editor overrides must not contain null elements.",
                    nameof(editorOverrides));
            }

            if (!overridesByName.TryAdd(assignment.Name, assignment))
            {
                throw new ArgumentException(
                    "Editor overrides must not contain duplicate parameter names.",
                    nameof(editorOverrides));
            }

            if (!_definitionsByName.TryGetValue(assignment.Name, out TemplateParameterDefinition? definition))
            {
                throw new ArgumentException(
                    $"Template parameter '{assignment.Name}' is not defined.",
                    nameof(editorOverrides));
            }

            if (!definition.EditorOverrideAllowed)
            {
                throw new ArgumentException(
                    $"Template parameter '{assignment.Name}' is fixed and cannot be changed by an editor.",
                    nameof(editorOverrides));
            }

            if (!definition.AllowsEditorValue(assignment.Value))
            {
                throw new ArgumentException(
                    $"The value for template parameter '{assignment.Name}' is not approved for editor use.",
                    nameof(editorOverrides));
            }
        }

        TemplateParameterAssignment[] resolvedParameters =
            new TemplateParameterAssignment[ParameterDefinitions.Count];
        for (int index = 0; index < resolvedParameters.Length; index++)
        {
            TemplateParameterDefinition definition = ParameterDefinitions[index];
            TemplateParameterValue value = overridesByName.TryGetValue(
                definition.Name,
                out TemplateParameterAssignment? assignment)
                ? assignment.Value
                : definition.DefaultValue;
            resolvedParameters[index] = new TemplateParameterAssignment(definition.Name, value);
        }

        return new AppliedBrandTemplate(SeriesId, Reference, Kind, resolvedParameters);
    }
}