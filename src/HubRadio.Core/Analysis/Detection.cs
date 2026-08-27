using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Analysis;

public enum DetectionKind
{
    Silence,
    Repetition,
    PreparationConversation,
    RecordingProblem,
    ContextDeviation,
    AudioEvent,
    EditorialInstruction,
    TopicBoundary,
}

public sealed class Detection
{
    public Detection(
        EntityId<DetectionIdTag> id,
        EntityId<ModelRunIdTag> modelRunId,
        DetectionKind kind,
        OriginRef origin,
        string description,
        double? confidence)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(description);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown detection kind.");
        }

        AnalysisValidation.ValidateSourceSpanOrigin(origin, nameof(origin));
        AnalysisValidation.ValidateRequiredText(
            description,
            nameof(description),
            "A detection description");
        AnalysisValidation.ValidateConfidence(
            confidence,
            nameof(confidence),
            "Detection confidence");

        Id = id;
        ModelRunId = modelRunId;
        Kind = kind;
        Origin = origin;
        Description = description;
        Confidence = confidence;
    }

    public EntityId<DetectionIdTag> Id { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public DetectionKind Kind { get; }

    public OriginRef Origin { get; }

    public string Description { get; }

    public double? Confidence { get; }
}