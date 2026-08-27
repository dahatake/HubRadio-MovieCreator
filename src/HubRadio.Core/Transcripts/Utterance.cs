using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Transcripts;

public sealed class TranscriptUtteranceIdTag
{
}

public sealed class TranscriptSpeakerIdTag
{
}

public sealed class Utterance
{
    public Utterance(
        EntityId<TranscriptUtteranceIdTag> id,
        EntityId<SourceSpanIdTag> sourceSpanId,
        EntityId<TranscriptSpeakerIdTag> speakerId,
        string speakerLabel,
        string text,
        TimeRange range,
        double? recognitionConfidence,
        IReadOnlyList<Sentence> sentences)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourceSpanId);
        ArgumentNullException.ThrowIfNull(speakerId);
        ArgumentNullException.ThrowIfNull(speakerLabel);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sentences);

        TranscriptValidation.ValidateRequiredText(
            speakerLabel,
            nameof(speakerLabel),
            "A speaker label");
        TranscriptValidation.ValidateRequiredText(text, nameof(text), "Utterance text");
        TranscriptValidation.ValidateConfidence(
            recognitionConfidence,
            nameof(recognitionConfidence),
            "Utterance recognition confidence");

        if (range.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range,
                "An utterance range must have a positive duration.");
        }

        if (sentences.Count == 0)
        {
            throw new ArgumentException(
                "An utterance must contain at least one sentence.",
                nameof(sentences));
        }

        Sentence[] copiedSentences = new Sentence[sentences.Count];
        HashSet<EntityId<TranscriptSentenceIdTag>> sentenceIds = [];
        HashSet<EntityId<TranscriptTokenIdTag>> tokenIds = [];
        List<Token> flattenedTokens = [];
        TimeRange? previousTimedRange = null;

        for (int sentenceIndex = 0; sentenceIndex < copiedSentences.Length; sentenceIndex++)
        {
            Sentence? sentence = sentences[sentenceIndex];
            if (sentence is null)
            {
                throw new ArgumentException(
                    "Sentences must not contain null elements.",
                    nameof(sentences));
            }

            if (!sentenceIds.Add(sentence.Id))
            {
                throw new ArgumentException(
                    "Sentences must not contain duplicate IDs.",
                    nameof(sentences));
            }

            foreach (Token token in sentence.Tokens)
            {
                if (!tokenIds.Add(token.Id))
                {
                    throw new ArgumentException(
                        "Tokens in an utterance must not contain duplicate IDs.",
                        nameof(sentences));
                }

                if (token.Range is TimeRange tokenRange)
                {
                    if (tokenRange.Start < range.Start || tokenRange.End > range.End)
                    {
                        throw new ArgumentException(
                            "A timed token range must be contained by its utterance range.",
                            nameof(sentences));
                    }

                    if (previousTimedRange is TimeRange previous
                        && tokenRange.Start < previous.End)
                    {
                        throw new ArgumentException(
                            "Timed tokens must be ordered and must not overlap across sentences.",
                            nameof(sentences));
                    }

                    previousTimedRange = tokenRange;
                }

                flattenedTokens.Add(token);
            }

            copiedSentences[sentenceIndex] = sentence;
        }

        Id = id;
        SourceSpanId = sourceSpanId;
        SpeakerId = speakerId;
        SpeakerLabel = speakerLabel;
        Text = text;
        Range = range;
        RecognitionConfidence = recognitionConfidence;
        Sentences = Array.AsReadOnly(copiedSentences);
        Tokens = Array.AsReadOnly(flattenedTokens.ToArray());
    }

    public EntityId<TranscriptUtteranceIdTag> Id { get; }

    public EntityId<SourceSpanIdTag> SourceSpanId { get; }

    public EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

    public string SpeakerLabel { get; }

    public string Text { get; }

    public TimeRange Range { get; }

    public double? RecognitionConfidence { get; }

    public IReadOnlyList<Sentence> Sentences { get; }

    public IReadOnlyList<Token> Tokens { get; }

    public TimeRange ResolveEditingRange(EntityId<TranscriptTokenIdTag> tokenId)
    {
        ArgumentNullException.ThrowIfNull(tokenId);

        foreach (Token token in Tokens)
        {
            if (token.Id == tokenId)
            {
                return token.Range ?? Range;
            }
        }

        throw new ArgumentException(
            "The token ID does not belong to this utterance.",
            nameof(tokenId));
    }
}