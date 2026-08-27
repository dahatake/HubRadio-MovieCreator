using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Transcripts;

public sealed class Token
{
    public Token(
        EntityId<TranscriptTokenIdTag> id,
        string text,
        TimeRange? range,
        double? recognitionConfidence)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(text);

        TranscriptValidation.ValidateRequiredText(text, nameof(text), "Token text");
        TranscriptValidation.ValidateConfidence(
            recognitionConfidence,
            nameof(recognitionConfidence),
            "Token recognition confidence");

        if (range is TimeRange suppliedRange
            && suppliedRange.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range,
                "A supplied token range must have a positive duration.");
        }

        Id = id;
        Text = text;
        Range = range;
        RecognitionConfidence = recognitionConfidence;
    }

    public EntityId<TranscriptTokenIdTag> Id { get; }

    public string Text { get; }

    public TimeRange? Range { get; }

    public double? RecognitionConfidence { get; }

    public bool ManualTimingAdjustmentRequired => Range is null;
}