using HubRadio.Core.Identity;
using HubRadio.Core.Time;

namespace HubRadio.Core.Provenance;

public sealed class SourceSpanIdTag
{
}

public sealed class AssetIdTag
{
}

public sealed class TranscriptRevisionIdTag
{
}

public sealed class TranscriptTokenIdTag
{
}

public sealed class EditorialRecordIdTag
{
}

public sealed class SourceSpan
{
    public SourceSpan(
        EntityId<SourceSpanIdTag> id,
        EntityId<AssetIdTag> assetId,
        string streamId,
        TimeRange range,
        ContentDigest sourceDigest,
        EntityId<TranscriptRevisionIdTag>? transcriptRevisionId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> transcriptTokenIds)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(assetId);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(sourceDigest);
        ArgumentNullException.ThrowIfNull(transcriptTokenIds);

        if (string.IsNullOrWhiteSpace(streamId)
            || !string.Equals(streamId, streamId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A stream ID must not be blank or have leading or trailing whitespace.",
                nameof(streamId));
        }

        if (range.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range,
                "A source span must have a positive duration.");
        }

        EntityId<TranscriptTokenIdTag>[] copiedTokenIds =
            new EntityId<TranscriptTokenIdTag>[transcriptTokenIds.Count];
        HashSet<EntityId<TranscriptTokenIdTag>> uniqueTokenIds = [];

        for (int index = 0; index < copiedTokenIds.Length; index++)
        {
            EntityId<TranscriptTokenIdTag>? tokenId = transcriptTokenIds[index];
            if (tokenId is null)
            {
                throw new ArgumentException(
                    "Transcript token IDs must not contain null elements.",
                    nameof(transcriptTokenIds));
            }

            if (!uniqueTokenIds.Add(tokenId))
            {
                throw new ArgumentException(
                    "Transcript token IDs must not contain duplicates.",
                    nameof(transcriptTokenIds));
            }

            copiedTokenIds[index] = tokenId;
        }

        if (copiedTokenIds.Length > 0 && transcriptRevisionId is null)
        {
            throw new ArgumentException(
                "Transcript token IDs require a transcript revision ID.",
                nameof(transcriptRevisionId));
        }

        Id = id;
        AssetId = assetId;
        StreamId = streamId;
        Range = range;
        SourceDigest = sourceDigest;
        TranscriptRevisionId = transcriptRevisionId;
        TranscriptTokenIds = Array.AsReadOnly(copiedTokenIds);
    }

    public EntityId<SourceSpanIdTag> Id { get; }

    public EntityId<AssetIdTag> AssetId { get; }

    public string StreamId { get; }

    public TimeRange Range { get; }

    public ContentDigest SourceDigest { get; }

    public EntityId<TranscriptRevisionIdTag>? TranscriptRevisionId { get; }

    public IReadOnlyList<EntityId<TranscriptTokenIdTag>> TranscriptTokenIds { get; }
}
