using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Analysis;

public enum SuggestionKind
{
    DeleteRange,
    TopicBoundary,
    Highlight,
    /// <summary>
    /// A proposed dictionary correction; the status, not the kind, records adoption.
    /// </summary>
    TermCorrection,
}

public enum SuggestionStatus
{
    Proposed,
    Reviewed,
    Rejected,
    Adopted,
}

public sealed class Suggestion
{
    public Suggestion(
        EntityId<SuggestionIdTag> id,
        EntityId<ModelRunIdTag> modelRunId,
        SuggestionKind kind,
        SuggestionStatus status,
        OriginRef origin,
        string reason,
        double? confidence,
        string? missingContext,
        string? warning)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(reason);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown suggestion kind.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown suggestion status.");
        }

        AnalysisValidation.ValidateSourceSpanOrigin(origin, nameof(origin));
        AnalysisValidation.ValidateRequiredText(reason, nameof(reason), "A suggestion reason");
        AnalysisValidation.ValidateConfidence(
            confidence,
            nameof(confidence),
            "Suggestion confidence");
        AnalysisValidation.ValidateOptionalText(
            missingContext,
            nameof(missingContext),
            "Missing-context text");
        AnalysisValidation.ValidateOptionalText(warning, nameof(warning), "A suggestion warning");

        Id = id;
        ModelRunId = modelRunId;
        Kind = kind;
        Status = status;
        Origin = origin;
        Reason = reason;
        Confidence = confidence;
        MissingContext = missingContext;
        Warning = warning;
    }

    public EntityId<SuggestionIdTag> Id { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public SuggestionKind Kind { get; }

    public SuggestionStatus Status { get; }

    public OriginRef Origin { get; }

    public string Reason { get; }

    public double? Confidence { get; }

    public string? MissingContext { get; }

    public string? Warning { get; }
}

public sealed class SuggestionReference
{
    private SuggestionReference(
        EntityId<SuggestionIdTag> suggestionId,
        OriginRef origin)
    {
        SuggestionId = suggestionId;
        Origin = origin;
    }

    public EntityId<SuggestionIdTag> SuggestionId { get; }

    public OriginRef Origin { get; }

    public static SuggestionReference FromAdoptedSuggestion(Suggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        if (suggestion.Status != SuggestionStatus.Adopted
            || suggestion.Kind == SuggestionKind.TermCorrection)
        {
            throw new ArgumentException(
                "Only an adopted composition suggestion can be referenced by a composition element.",
                nameof(suggestion));
        }

        return new SuggestionReference(suggestion.Id, suggestion.Origin);
    }
}