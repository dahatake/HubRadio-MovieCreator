using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Transcripts;

public sealed class TranscriptSentenceIdTag
{
}

public sealed class Sentence
{
    public Sentence(
        EntityId<TranscriptSentenceIdTag> id,
        string text,
        IReadOnlyList<Token> tokens)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tokens);

        TranscriptValidation.ValidateRequiredText(text, nameof(text), "Sentence text");

        if (tokens.Count == 0)
        {
            throw new ArgumentException(
                "A sentence must contain at least one token.",
                nameof(tokens));
        }

        Token[] copiedTokens = new Token[tokens.Count];
        HashSet<EntityId<TranscriptTokenIdTag>> tokenIds = [];
        TimeRange? previousTimedRange = null;

        for (int index = 0; index < copiedTokens.Length; index++)
        {
            Token? token = tokens[index];
            if (token is null)
            {
                throw new ArgumentException(
                    "Tokens must not contain null elements.",
                    nameof(tokens));
            }

            if (!tokenIds.Add(token.Id))
            {
                throw new ArgumentException(
                    "Tokens must not contain duplicate IDs.",
                    nameof(tokens));
            }

            if (token.Range is TimeRange tokenRange)
            {
                if (previousTimedRange is TimeRange previous
                    && tokenRange.Start < previous.End)
                {
                    throw new ArgumentException(
                        "Timed tokens must be ordered and must not overlap.",
                        nameof(tokens));
                }

                previousTimedRange = tokenRange;
            }

            copiedTokens[index] = token;
        }

        Id = id;
        Text = text;
        Tokens = Array.AsReadOnly(copiedTokens);
    }

    public EntityId<TranscriptSentenceIdTag> Id { get; }

    public string Text { get; }

    public IReadOnlyList<Token> Tokens { get; }
}