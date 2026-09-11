using HubRadio.App.Compositions;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;

namespace HubRadio.App.Subtitles;

// Native-only evidence: a cue revision remains unchanged along this exact path.
// The bridge must additionally establish membership in its effective head snapshot.
internal sealed class SubtitleCompositionInheritance
{
    internal const int MaxVersions = 256;
    private readonly byte[] elements;
    private readonly SubtitleCueRevision sourceRevision;

    internal SubtitleCompositionInheritance(
        SubtitleCueRevision revision,
        IReadOnlyList<CompositionVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(versions);
        int count = versions.Count;
        if (count is < 1 or > MaxVersions)
        {
            throw Conflict();
        }

        var copied = new CompositionVersion[count];
        for (int index = 0; index < count; index++)
        {
            copied[index] = versions[index];
        }

        var ids = new HashSet<EntityId<CompositionVersionIdTag>>();
        byte[]? firstElements = null;
        CompositionVersion? previous = null;
        foreach (CompositionVersion version in copied)
        {
            if (version is null
                || !ids.Add(version.Id)
                || version.ProjectId != revision.ProjectId
                || (previous is not null
                    && (version.ParentVersionId != previous.Id
                        || version.CreatedAt < previous.CreatedAt
                        || version.CreatedAt < revision.CreatedAt)))
            {
                throw Conflict();
            }

            byte[] content = ReadElements(version);
            if (version.ContentDigest != revision.CompositionVersionDigest
                || (firstElements is not null
                    && !firstElements.AsSpan().SequenceEqual(content)))
            {
                throw Conflict();
            }

            firstElements ??= content;
            previous = version;
        }

        if (copied[0].ParentVersionId is not null
            && ids.Contains(copied[0].ParentVersionId!))
        {
            throw Conflict();
        }

        RevisionId = revision.Id;
        Versions = Array.AsReadOnly(copied);
        elements = firstElements!;
        sourceRevision = revision;
        ValidateFor(revision);
    }

    internal EntityId<SubtitleCueRevisionIdTag> RevisionId { get; }

    // Ordered from the cue-current version through the effective head (inclusive).
    internal IReadOnlyList<CompositionVersion> Versions { get; }

    internal CompositionVersion Parent => Versions[^1];

    internal void ValidateFor(SubtitleCueRevision revision)
    {
        CompositionVersion first = Versions[0];
        if (revision.Id != RevisionId
            || revision.ContentDigest != sourceRevision.ContentDigest
            || revision.Snapshot.Cue.Id != sourceRevision.Snapshot.Cue.Id
            || revision.CreatedAt != sourceRevision.CreatedAt
            || revision.ParentRevisionId != sourceRevision.ParentRevisionId
            || revision.SourceCandidateId != sourceRevision.SourceCandidateId
            || revision.SourceRevisionId != sourceRevision.SourceRevisionId
            || revision.SourceRevisionDigest != sourceRevision.SourceRevisionDigest
            || revision.SourceOrigin != sourceRevision.SourceOrigin
            || !string.Equals(revision.ActorSid, sourceRevision.ActorSid, StringComparison.Ordinal)
            || !string.Equals(revision.Reason, sourceRevision.Reason, StringComparison.Ordinal)
            || revision.ProjectId != first.ProjectId
            || revision.CompositionVersionId != first.Id
            || revision.ParentCompositionVersionId != first.ParentVersionId
            || revision.CompositionVersionDigest != first.ContentDigest
            || revision.CreatedAt < first.CreatedAt
            || (Versions.Count > 1 && Versions[1].CreatedAt < revision.CreatedAt))
        {
            throw Conflict();
        }
    }

    internal void ValidateChild(CompositionVersion child)
    {
        if (child.ProjectId != Parent.ProjectId
            || child.ParentVersionId != Parent.Id
            || child.CreatedAt < Parent.CreatedAt
            || child.ContentDigest != Parent.ContentDigest
            || Versions.Any(version => version.Id == child.Id)
            || !elements.AsSpan().SequenceEqual(ReadElements(child)))
        {
            throw Conflict();
        }
    }

    private static byte[] ReadElements(CompositionVersion version)
    {
        try
        {
            byte[] content = CompositionSnapshotJson.WriteElementsObjectToUtf8(
                version.Elements);
            if (ContentDigest.Compute(content) != version.ContentDigest)
            {
                throw Conflict();
            }

            return content;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            throw Conflict();
        }
    }

    private static SubtitleServiceException Conflict() =>
        new(SubtitleServiceFailureReason.CompositionVersionConflict);
}