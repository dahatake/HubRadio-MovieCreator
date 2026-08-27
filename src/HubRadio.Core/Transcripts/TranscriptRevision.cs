using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Transcripts;

public enum TranscriptChangeSource
{
    Machine,
    Human,
}

public sealed class TranscriptRevision
{
    public TranscriptRevision(
        EntityId<TranscriptRevisionIdTag> id,
        EntityId<TranscriptRevisionIdTag>? parentRevisionId,
        TranscriptChangeSource changeSource,
        DateTimeOffset createdAt,
        IReadOnlyList<Utterance> utterances)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(utterances);

        if (parentRevisionId == id)
        {
            throw new ArgumentException(
                "A transcript revision cannot be its own parent.",
                nameof(parentRevisionId));
        }

        if (!Enum.IsDefined(changeSource))
        {
            throw new ArgumentOutOfRangeException(
                nameof(changeSource),
                changeSource,
                "Unknown transcript change source.");
        }

        Utterance[] copiedUtterances = new Utterance[utterances.Count];
        HashSet<EntityId<TranscriptUtteranceIdTag>> utteranceIds = [];
        HashSet<EntityId<TranscriptSentenceIdTag>> sentenceIds = [];
        HashSet<EntityId<TranscriptTokenIdTag>> tokenIds = [];
        Dictionary<EntityId<TranscriptSpeakerIdTag>, string> speakerLabels = [];
        RationalTime? previousStart = null;

        for (int index = 0; index < copiedUtterances.Length; index++)
        {
            Utterance? utterance = utterances[index];
            if (utterance is null)
            {
                throw new ArgumentException(
                    "Utterances must not contain null elements.",
                    nameof(utterances));
            }

            if (!utteranceIds.Add(utterance.Id))
            {
                throw new ArgumentException(
                    "Utterances must not contain duplicate IDs.",
                    nameof(utterances));
            }

            if (previousStart is RationalTime priorStart
                && utterance.Range.Start < priorStart)
            {
                throw new ArgumentException(
                    "Utterances must be ordered by start time.",
                    nameof(utterances));
            }

            previousStart = utterance.Range.Start;

            if (speakerLabels.TryGetValue(utterance.SpeakerId, out string? existingLabel))
            {
                if (!string.Equals(
                    existingLabel,
                    utterance.SpeakerLabel,
                    StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "A speaker ID must use one label within a transcript revision.",
                        nameof(utterances));
                }
            }
            else
            {
                speakerLabels.Add(utterance.SpeakerId, utterance.SpeakerLabel);
            }

            foreach (Sentence sentence in utterance.Sentences)
            {
                if (!sentenceIds.Add(sentence.Id))
                {
                    throw new ArgumentException(
                        "Sentence IDs must be unique within a transcript revision.",
                        nameof(utterances));
                }
            }

            foreach (Token token in utterance.Tokens)
            {
                if (!tokenIds.Add(token.Id))
                {
                    throw new ArgumentException(
                        "Token IDs must be unique within a transcript revision.",
                        nameof(utterances));
                }
            }

            copiedUtterances[index] = utterance;
        }

        Id = id;
        ParentRevisionId = parentRevisionId;
        ChangeSource = changeSource;
        CreatedAt = createdAt;
        Utterances = Array.AsReadOnly(copiedUtterances);
    }

    public EntityId<TranscriptRevisionIdTag> Id { get; }

    public EntityId<TranscriptRevisionIdTag>? ParentRevisionId { get; }

    public TranscriptChangeSource ChangeSource { get; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<Utterance> Utterances { get; }
}