using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using CoreProjectSnapshotJson = HubRadio.Core.Projects.ProjectSnapshotJson;

namespace HubRadio.App.Compositions;

public enum CompositionChangeKind
{
    Added,
    Removed,
    Moved,
    Modified,
}

public sealed class CompositionChange
{
    internal CompositionChange(
        CompositionChangeKind kind,
        EntityId<CompositionElementIdTag> elementId,
        int? previousIndex,
        int? currentIndex,
        CompositionElement? previousElement,
        CompositionElement? currentElement)
    {
        ArgumentNullException.ThrowIfNull(elementId);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown composition change kind.");
        }

        bool isValid = kind switch
        {
            CompositionChangeKind.Added =>
                previousIndex is null &&
                currentIndex is >= 0 &&
                previousElement is null &&
                currentElement is not null &&
                currentElement.Id == elementId,
            CompositionChangeKind.Removed =>
                previousIndex is >= 0 &&
                currentIndex is null &&
                previousElement is not null &&
                currentElement is null &&
                previousElement.Id == elementId,
            CompositionChangeKind.Moved =>
                previousIndex is >= 0 &&
                currentIndex is >= 0 &&
                previousIndex != currentIndex &&
                previousElement is not null &&
                currentElement is not null &&
                previousElement.Id == elementId &&
                currentElement.Id == elementId &&
                CompositionStableContent.Equals(previousElement, currentElement),
            CompositionChangeKind.Modified =>
                previousIndex is >= 0 &&
                currentIndex is >= 0 &&
                previousElement is not null &&
                currentElement is not null &&
                previousElement.Id == elementId &&
                currentElement.Id == elementId &&
                !CompositionStableContent.Equals(previousElement, currentElement),
            _ => false,
        };

        if (!isValid)
        {
            throw new ArgumentException(
                "The supplied composition change does not match its typed change kind.",
                nameof(kind));
        }

        Kind = kind;
        ElementId = elementId;
        PreviousIndex = previousIndex;
        CurrentIndex = currentIndex;
        PreviousElement = previousElement;
        CurrentElement = currentElement;
    }

    public CompositionChangeKind Kind { get; }

    public EntityId<CompositionElementIdTag> ElementId { get; }

    public int? PreviousIndex { get; }

    public int? CurrentIndex { get; }

    public CompositionElement? PreviousElement { get; }

    public CompositionElement? CurrentElement { get; }
}

public sealed class CompositionDiff
{
    internal CompositionDiff(
        EntityId<CompositionVersionIdTag> fromVersionId,
        EntityId<CompositionVersionIdTag> toVersionId,
        IReadOnlyList<CompositionChange> changes)
    {
        ArgumentNullException.ThrowIfNull(fromVersionId);
        ArgumentNullException.ThrowIfNull(toVersionId);
        ArgumentNullException.ThrowIfNull(changes);

        CompositionChange[] copiedChanges = new CompositionChange[changes.Count];
        for (int index = 0; index < copiedChanges.Length; index++)
        {
            copiedChanges[index] = changes[index]
                ?? throw new ArgumentException(
                    "Composition changes must not contain null elements.",
                    nameof(changes));
        }

        FromVersionId = fromVersionId;
        ToVersionId = toVersionId;
        Changes = Array.AsReadOnly(copiedChanges);
    }

    public EntityId<CompositionVersionIdTag> FromVersionId { get; }

    public EntityId<CompositionVersionIdTag> ToVersionId { get; }

    public IReadOnlyList<CompositionChange> Changes { get; }
}

public sealed class CheckpointResult
{
    internal CheckpointResult(
        CompositionVersion version,
        WorkingCopy workingCopy,
        int workingCopyRevision,
        string canonicalVersionJson,
        string canonicalWorkingCopyJson)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(workingCopy);
        ArgumentNullException.ThrowIfNull(canonicalVersionJson);
        ArgumentNullException.ThrowIfNull(canonicalWorkingCopyJson);

        if (workingCopyRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingCopyRevision),
                workingCopyRevision,
                "A working-copy revision must not be negative.");
        }

        Version = version;
        WorkingCopy = workingCopy;
        WorkingCopyRevision = workingCopyRevision;
        CanonicalVersionJson = canonicalVersionJson;
        CanonicalWorkingCopyJson = canonicalWorkingCopyJson;
    }

    public CompositionVersion Version { get; }

    public WorkingCopy WorkingCopy { get; }

    public int WorkingCopyRevision { get; }

    public string CanonicalVersionJson { get; }

    public string CanonicalWorkingCopyJson { get; }
}

public sealed class WorkingCopyState
{
    internal WorkingCopyState(WorkingCopy workingCopy, int revision)
    {
        WorkingCopy = workingCopy;
        Revision = revision;
    }

    public WorkingCopy WorkingCopy { get; }

    public int Revision { get; }
}

public sealed class VersionService
{
    private readonly ProjectStore _projectStore;

    public VersionService(ProjectStore projectStore)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        _projectStore = projectStore;
    }

    public CheckpointResult Checkpoint(
        WorkingCopy draft,
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason)
    {
        ArgumentNullException.ThrowIfNull(draft);
        EnsureTimestampDoesNotMoveBackwards(draft, createdAt);

        return CreateAndStoreCheckpoint(
            draft,
            draft.Elements,
            expectedParent: null,
            expectedProject: null,
            expectedProjectState: null,
            expectedRevision,
            newVersionId,
            actorId,
            createdAt,
            changeReason,
            expectedContentDigest: null,
            evidence: null,
            requireExactDraftSnapshot: false,
            CancellationToken.None);
    }

    internal CheckpointResult CheckpointAutoDecision(
        WorkingCopy draft,
        CompositionVersion expectedParent,
        IReadOnlyList<CompositionElement> elements,
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason,
        ContentDigest expectedContentDigest,
        ValidatedAutoDecisionEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(expectedParent);
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(expectedContentDigest);
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTimestampDoesNotMoveBackwards(draft, createdAt);
        return CreateAndStoreCheckpoint(
            draft,
            elements,
            expectedParent,
            expectedProject: null,
            expectedProjectState: null,
            expectedRevision,
            newVersionId,
            actorId,
            createdAt,
            changeReason,
            expectedContentDigest,
            evidence.Envelope,
            requireExactDraftSnapshot: true,
            cancellationToken);
    }

    internal CheckpointResult CheckpointStandardComposition(
        WorkingCopy draft,
        CompositionVersion expectedParent,
        Project expectedProject,
        StandardProjectStoreState expectedProjectState,
        IReadOnlyList<CompositionElement> elements,
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason,
        ContentDigest expectedContentDigest,
        ValidatedStandardCompositionEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(expectedParent);
        ArgumentNullException.ThrowIfNull(expectedProject);
        ArgumentNullException.ThrowIfNull(expectedProjectState);
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(expectedContentDigest);
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTimestampDoesNotMoveBackwards(draft, createdAt);
        return CreateAndStoreCheckpoint(
            draft,
            elements,
            expectedParent,
            expectedProject,
            expectedProjectState,
            expectedRevision,
            newVersionId,
            actorId,
            createdAt,
            changeReason,
            expectedContentDigest,
            evidence.Envelope,
            requireExactDraftSnapshot: true,
            cancellationToken);
    }

    public CompositionVersion? GetVersion(
        EntityId<CompositionVersionIdTag> versionId) =>
        LoadVersion(versionId)?.Version;

    internal void ValidateStoredEvidenceParent(CompositionVersion parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.Evidence is null)
        {
            return;
        }

        CompositionVersion stored = GetVersion(parent.Id)
            ?? throw new InvalidOperationException(
                "The evidence-bearing parent version is unavailable.");
        if (!string.Equals(
                CompositionSnapshotJson.WriteVersion(stored),
                CompositionSnapshotJson.WriteVersion(parent),
                StringComparison.Ordinal))
        {
            throw new StoreConcurrencyException(
                "composition parent",
                parent.Id.ToString(),
                expectedRevision: 0,
                actualRevision: 0);
        }
    }

    public StandardCompositionPreparation? GetStandardComposition(
        EntityId<CompositionVersionIdTag> versionId)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        LoadedVersion? loaded = LoadVersion(versionId);
        if (loaded is null)
        {
            return null;
        }

        if (!string.Equals(
                loaded.Version.Evidence?.Kind,
                CompositionEvidenceEnvelope.StandardCompositionPlanKind,
                StringComparison.Ordinal)
            || loaded.StandardComposition is null)
        {
            throw new InvalidOperationException(
                "The requested composition version is not a validated standard composition.");
        }

        return loaded.StandardComposition.Preparation;
    }

    private LoadedVersion? LoadVersion(
        EntityId<CompositionVersionIdTag> versionId)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        var lineage = new List<CompositionVersion>();
        var visited = new HashSet<EntityId<CompositionVersionIdTag>>();
        EntityId<CompositionVersionIdTag> currentId = versionId;
        CompositionVersion? parent = null;
        ValidatedStandardCompositionEvidence? standardComposition = null;
        while (true)
        {
            if (!visited.Add(currentId))
            {
                throw new FormatException(
                    "The composition-version evidence lineage contains a cycle.");
            }

            StoredCompositionVersion? stored =
                _projectStore.GetCompositionVersion(currentId);
            if (stored is null)
            {
                if (lineage.Count == 0)
                {
                    return null;
                }

                throw new InvalidOperationException(
                    "The evidence-bearing parent version is unavailable.");
            }

            CompositionVersion version = ReadVersionFromStored(stored);
            if (version.Evidence is null)
            {
                parent = version;
                break;
            }

            if (version.ParentVersionId is null)
            {
                throw new InvalidOperationException(
                    "An evidence-bearing version must have a stored parent.");
            }

            lineage.Add(version);
            currentId = version.ParentVersionId;
        }

        for (int index = lineage.Count - 1; index >= 0; index--)
        {
            CompositionVersion child = lineage[index];
            ValidatedStandardCompositionEvidence? validated =
                ValidateEvidence(child, parent);
            if (child.Id == versionId)
            {
                standardComposition = validated;
            }

            parent = child;
        }

        return new LoadedVersion(
            parent ?? throw new InvalidOperationException(
                "The requested composition version could not be reconstructed."),
            standardComposition);
    }

    public WorkingCopyState? GetCurrentState(EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        StoredWorkingCopy? stored = _projectStore.GetWorkingCopy(projectId);
        if (stored is null)
        {
            return null;
        }

        WorkingCopy workingCopy = CompositionSnapshotJson.ReadWorkingCopy(
            stored.CanonicalJson);
        if (workingCopy.Id != stored.Id
            || workingCopy.ProjectId != stored.ProjectId
            || workingCopy.BaseVersionId != stored.BaseVersionId
            || workingCopy.UpdatedAt != stored.UpdatedAt)
        {
            throw new InvalidOperationException(
                "The stored working-copy columns do not match its canonical snapshot.");
        }

        return new WorkingCopyState(workingCopy, stored.Revision);
    }

    public bool TryResolveExactCheckpoint(
        CompositionVersion expectedVersion,
        WorkingCopy expectedWorkingCopy,
        int expectedPreviousRevision,
        out CheckpointResult? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        ArgumentNullException.ThrowIfNull(expectedWorkingCopy);
        if (expectedPreviousRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPreviousRevision));
        }

        StoredCompositionVersion? storedVersion =
            _projectStore.GetCompositionVersion(expectedVersion.Id);
        StoredWorkingCopy? storedWorkingCopy =
            _projectStore.GetWorkingCopy(expectedVersion.ProjectId);
        int expectedCurrentRevision = checked(expectedPreviousRevision + 1);
        string expectedVersionJson = CompositionSnapshotJson.WriteVersion(expectedVersion);
        string expectedWorkingCopyJson =
            CompositionSnapshotJson.WriteWorkingCopy(expectedWorkingCopy);
        if (storedVersion is null
            || storedWorkingCopy is null
            || storedWorkingCopy.Revision != expectedCurrentRevision
            || storedVersion.Id != expectedVersion.Id
            || storedVersion.ProjectId != expectedVersion.ProjectId
            || storedVersion.ParentVersionId != expectedVersion.ParentVersionId
            || storedVersion.ContentDigest != expectedVersion.ContentDigest
            || storedVersion.CreatedAt != expectedVersion.CreatedAt
            || storedWorkingCopy.Id != expectedWorkingCopy.Id
            || storedWorkingCopy.ProjectId != expectedWorkingCopy.ProjectId
            || storedWorkingCopy.BaseVersionId != expectedVersion.Id
            || storedWorkingCopy.UpdatedAt != expectedWorkingCopy.UpdatedAt
            || !string.Equals(
                storedVersion.CanonicalJson,
                expectedVersionJson,
                StringComparison.Ordinal)
            || !string.Equals(
                storedWorkingCopy.CanonicalJson,
                expectedWorkingCopyJson,
                StringComparison.Ordinal))
        {
            checkpoint = null;
            return false;
        }

        CompositionVersion version = CompositionSnapshotJson.ReadVersion(
            storedVersion.CanonicalJson);
        WorkingCopy workingCopy = CompositionSnapshotJson.ReadWorkingCopy(
            storedWorkingCopy.CanonicalJson);
        checkpoint = new CheckpointResult(
            version,
            workingCopy,
            storedWorkingCopy.Revision,
            storedVersion.CanonicalJson,
            storedWorkingCopy.CanonicalJson);
        return true;
    }

    public CheckpointResult RollbackAsChild(
        WorkingCopy currentDraft,
        CompositionVersion targetVersion,
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason)
        => RollbackAsChildCore(
            currentDraft,
            targetVersion,
            expectedRevision,
            newVersionId,
            actorId,
            createdAt,
            changeReason,
            CancellationToken.None);

    internal CheckpointResult RollbackAutoDecisionAsChild(
        WorkingCopy currentDraft,
        CompositionVersion targetVersion,
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason,
        CancellationToken cancellationToken)
        => RollbackAsChildCore(
            currentDraft,
            targetVersion,
            expectedRevision,
            newVersionId,
            actorId,
            createdAt,
            changeReason,
            cancellationToken);

    private CheckpointResult RollbackAsChildCore(
        WorkingCopy currentDraft,
        CompositionVersion targetVersion,
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentDraft);
        ArgumentNullException.ThrowIfNull(targetVersion);
        cancellationToken.ThrowIfCancellationRequested();

        if (targetVersion.ProjectId != currentDraft.ProjectId)
        {
            throw new ArgumentException(
                "The rollback target and current working copy must belong to the same project.",
                nameof(targetVersion));
        }

        WorkingCopyState current = GetCurrentState(currentDraft.ProjectId)
            ?? throw new InvalidOperationException("The working copy does not exist.");
        if (current.Revision != expectedRevision
            || !string.Equals(
                CompositionSnapshotJson.WriteWorkingCopy(current.WorkingCopy),
                CompositionSnapshotJson.WriteWorkingCopy(currentDraft),
                StringComparison.Ordinal))
        {
            throw new StoreConcurrencyException(
                "working copy",
                currentDraft.Id.ToString(),
                expectedRevision,
                current.Revision);
        }

        CompositionVersion storedTarget = GetVersion(targetVersion.Id)
            ?? throw new ArgumentException(
                "The rollback target is not a stored composition version.",
                nameof(targetVersion));
        if (storedTarget.ProjectId != currentDraft.ProjectId
            || !string.Equals(
                CompositionSnapshotJson.WriteVersion(storedTarget),
                CompositionSnapshotJson.WriteVersion(targetVersion),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The rollback target does not exactly match its stored same-project version.",
                nameof(targetVersion));
        }

        if (!IsStrictAncestor(currentDraft.BaseVersionId, storedTarget.Id))
        {
            throw new ArgumentException(
                "The rollback target must be a strict ancestor of the current working-copy head.",
                nameof(targetVersion));
        }

        EnsureTimestampDoesNotMoveBackwards(currentDraft, createdAt);

        return CreateAndStoreCheckpoint(
            currentDraft,
            targetVersion.Elements,
            expectedParent: null,
            expectedProject: null,
            expectedProjectState: null,
            expectedRevision,
            newVersionId,
            actorId,
            createdAt,
            changeReason,
            targetVersion.ContentDigest,
            evidence: null,
            requireExactDraftSnapshot: true,
            cancellationToken);
    }

    public static CompositionDiff Compare(CompositionVersion from, CompositionVersion to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.ProjectId != to.ProjectId)
        {
            throw new ArgumentException(
                "Composition versions from different projects cannot be compared.",
                nameof(to));
        }

        Dictionary<EntityId<CompositionElementIdTag>, (CompositionElement Element, int Index)>
            previousById = IndexElements(from.Elements);
        Dictionary<EntityId<CompositionElementIdTag>, (CompositionElement Element, int Index)>
            currentById = IndexElements(to.Elements);
        var changes = new List<CompositionChange>();

        for (int previousIndex = 0; previousIndex < from.Elements.Count; previousIndex++)
        {
            CompositionElement previousElement = from.Elements[previousIndex];
            if (!currentById.ContainsKey(previousElement.Id))
            {
                changes.Add(new CompositionChange(
                    CompositionChangeKind.Removed,
                    previousElement.Id,
                    previousIndex,
                    currentIndex: null,
                    previousElement,
                    currentElement: null));
            }
        }

        for (int currentIndex = 0; currentIndex < to.Elements.Count; currentIndex++)
        {
            CompositionElement currentElement = to.Elements[currentIndex];
            if (!previousById.ContainsKey(currentElement.Id))
            {
                changes.Add(new CompositionChange(
                    CompositionChangeKind.Added,
                    currentElement.Id,
                    previousIndex: null,
                    currentIndex,
                    previousElement: null,
                    currentElement));
            }
        }

        for (int currentIndex = 0; currentIndex < to.Elements.Count; currentIndex++)
        {
            CompositionElement currentElement = to.Elements[currentIndex];
            if (!previousById.TryGetValue(
                    currentElement.Id,
                    out (CompositionElement Element, int Index) previous))
            {
                continue;
            }

            if (!CompositionStableContent.Equals(previous.Element, currentElement))
            {
                changes.Add(new CompositionChange(
                    CompositionChangeKind.Modified,
                    currentElement.Id,
                    previous.Index,
                    currentIndex,
                    previous.Element,
                    currentElement));
            }
            else if (previous.Index != currentIndex)
            {
                changes.Add(new CompositionChange(
                    CompositionChangeKind.Moved,
                    currentElement.Id,
                    previous.Index,
                    currentIndex,
                    previous.Element,
                    currentElement));
            }
        }

        return new CompositionDiff(from.Id, to.Id, changes);
    }

    private CheckpointResult CreateAndStoreCheckpoint(
        WorkingCopy draft,
        IReadOnlyList<CompositionElement> elements,
        CompositionVersion? expectedParent,
        Project? expectedProject,
        StandardProjectStoreState? expectedProjectState,
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason,
        ContentDigest? expectedContentDigest,
        CompositionEvidenceEnvelope? evidence,
        bool requireExactDraftSnapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] canonicalElementsJson =
            CompositionSnapshotJson.WriteElementsObjectToUtf8(elements);
        ContentDigest contentDigest = ContentDigest.Compute(canonicalElementsJson);
        if (expectedContentDigest is not null && contentDigest != expectedContentDigest)
        {
            throw new InvalidOperationException(
                "The expected content digest does not match the canonical element snapshot.");
        }

        CompositionVersion version = evidence is null
            ? new CompositionVersion(
                newVersionId,
                draft.ProjectId,
                draft.BaseVersionId,
                actorId,
                createdAt,
                changeReason,
                contentDigest,
                elements)
            : new CompositionVersion(
                newVersionId,
                draft.ProjectId,
                draft.BaseVersionId,
                actorId,
                createdAt,
                changeReason,
                contentDigest,
                elements,
                evidence);
        var updatedWorkingCopy = new WorkingCopy(
            draft.Id,
            draft.ProjectId,
            version.Id,
            draft.CreatedAt,
            createdAt,
            elements);
        string canonicalVersionJson = CompositionSnapshotJson.WriteVersion(version);
        string canonicalWorkingCopyJson =
            CompositionSnapshotJson.WriteWorkingCopy(updatedWorkingCopy);
        int workingCopyRevision = requireExactDraftSnapshot
            ? _projectStore.CheckpointWorkingCopyExact(
                version,
                canonicalVersionJson,
                draft,
                CompositionSnapshotJson.WriteWorkingCopy(draft),
                expectedParent,
                expectedParent is null
                    ? null
                    : CompositionSnapshotJson.WriteVersion(expectedParent),
                expectedProject,
                expectedProjectState?.Revision,
                expectedProjectState?.PayloadDigest,
                expectedProjectState?.UpdatedAt,
                expectedProject is null
                    ? null
                    : CoreProjectSnapshotJson.WriteProject(expectedProject),
                updatedWorkingCopy,
                canonicalWorkingCopyJson,
                expectedRevision,
                cancellationToken)
            : _projectStore.CheckpointWorkingCopy(
                version,
                canonicalVersionJson,
                draft,
                updatedWorkingCopy,
                canonicalWorkingCopyJson,
                expectedRevision,
                cancellationToken);

        return new CheckpointResult(
            version,
            updatedWorkingCopy,
            workingCopyRevision,
            canonicalVersionJson,
            canonicalWorkingCopyJson);
    }

    private static CompositionVersion ReadVersionFromStored(
        StoredCompositionVersion stored)
    {
        CompositionVersion version = CompositionSnapshotJson.ReadVersion(
            stored.CanonicalJson);
        if (version.Id != stored.Id
            || version.ProjectId != stored.ProjectId
            || version.ParentVersionId != stored.ParentVersionId
            || version.ContentDigest != stored.ContentDigest
            || version.CreatedAt != stored.CreatedAt)
        {
            throw new InvalidOperationException(
                "The stored composition-version columns do not match its canonical snapshot.");
        }

        return version;
    }

    private static ValidatedStandardCompositionEvidence? ValidateEvidence(
        CompositionVersion child,
        CompositionVersion parent)
    {
        CompositionEvidenceEnvelope evidence = child.Evidence
            ?? throw new InvalidOperationException(
                "The composition-version evidence is unavailable.");
        switch (evidence.Kind)
        {
            case CompositionEvidenceEnvelope.AutoDecisionKind:
                AutoDecisionEvidenceCodec.ValidateLoaded(child, parent);
                return null;
            case CompositionEvidenceEnvelope.StandardCompositionPlanKind:
                return StandardCompositionEvidenceCodec.ValidateLoaded(
                    child,
                    parent);
            default:
                throw new InvalidOperationException(
                    "The stored composition evidence kind is unsupported.");
        }
    }

    private sealed record LoadedVersion(
        CompositionVersion Version,
        ValidatedStandardCompositionEvidence? StandardComposition);

    private bool IsStrictAncestor(
        EntityId<CompositionVersionIdTag>? currentHeadId,
        EntityId<CompositionVersionIdTag> targetId)
    {
        if (currentHeadId is null)
        {
            return false;
        }

        CompositionVersion current = GetVersion(currentHeadId)
            ?? throw new InvalidOperationException(
                "The current working-copy head is not a stored composition version.");
        EntityId<CompositionVersionIdTag>? cursor = current.ParentVersionId;
        var visited = new HashSet<EntityId<CompositionVersionIdTag>> { current.Id };
        while (cursor is not null)
        {
            if (!visited.Add(cursor))
            {
                throw new InvalidOperationException(
                    "The stored composition-version lineage contains a cycle.");
            }

            CompositionVersion ancestor = GetVersion(cursor)
                ?? throw new InvalidOperationException(
                    "The stored composition-version lineage is incomplete.");
            if (ancestor.ProjectId != current.ProjectId)
            {
                throw new InvalidOperationException(
                    "The stored composition-version lineage crosses projects.");
            }

            if (ancestor.Id == targetId)
            {
                return true;
            }

            cursor = ancestor.ParentVersionId;
        }

        return false;
    }

    private static Dictionary<
        EntityId<CompositionElementIdTag>,
        (CompositionElement Element, int Index)> IndexElements(
            IReadOnlyList<CompositionElement> elements)
    {
        var indexed = new Dictionary<
            EntityId<CompositionElementIdTag>,
            (CompositionElement Element, int Index)>(elements.Count);
        for (int index = 0; index < elements.Count; index++)
        {
            indexed.Add(elements[index].Id, (elements[index], index));
        }

        return indexed;
    }

    private static void EnsureTimestampDoesNotMoveBackwards(
        WorkingCopy draft,
        DateTimeOffset createdAt)
    {
        if (createdAt < draft.UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                createdAt,
                "A checkpoint timestamp must not precede the working-copy update timestamp.");
        }
    }
}

internal static class CompositionSnapshotJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static CompositionVersion ReadVersion(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);

        try
        {
            byte[] utf8 = StrictUtf8.GetBytes(canonicalJson);
            byte[] canonicalUtf8 = CanonicalJson.CanonicalizeToUtf8(utf8);
            if (!utf8.AsSpan().SequenceEqual(canonicalUtf8))
            {
                throw new FormatException(
                    "A stored composition version must use its canonical JSON representation.");
            }

            using JsonDocument document = JsonDocument.Parse(canonicalUtf8);
            JsonElement root = document.RootElement;
            bool hasParent = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("parentVersionId", out _);
            bool hasEvidence = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("evidence", out _);
            var expectedProperties = new List<string>
            {
                "id",
                "projectId",
                "createdByActorId",
                "createdAt",
                "changeReason",
                "contentDigest",
                "elements",
            };
            if (hasParent)
            {
                expectedProperties.Add("parentVersionId");
            }

            if (hasEvidence)
            {
                expectedProperties.Add("evidence");
            }

            EnsureExactProperties(root, expectedProperties);

            EntityId<CompositionVersionIdTag> id =
                ReadEntityId<CompositionVersionIdTag>(root, "id");
            EntityId<ProjectIdTag> projectId =
                ReadEntityId<ProjectIdTag>(root, "projectId");
            EntityId<CompositionVersionIdTag>? parentVersionId = hasParent
                ? ReadEntityId<CompositionVersionIdTag>(root, "parentVersionId")
                : null;
            string createdByActorId = ReadString(root, "createdByActorId");
            DateTimeOffset createdAt = ReadTimestamp(root, "createdAt");
            string changeReason = ReadString(root, "changeReason");
            ContentDigest contentDigest = ReadContentDigest(root, "contentDigest");
            IReadOnlyList<CompositionElement> elements = ReadElements(
                root.GetProperty("elements"));
            CompositionEvidenceEnvelope? evidence = hasEvidence
                ? ReadEvidence(root.GetProperty("evidence"))
                : null;
            ContentDigest computedDigest = ContentDigest.Compute(
                WriteElementsObjectToUtf8(elements));
            if (computedDigest != contentDigest)
            {
                throw new FormatException(
                    "The stored composition-version digest does not match its elements.");
            }

            var version = new CompositionVersion(
                id,
                projectId,
                parentVersionId,
                createdByActorId,
                createdAt,
                changeReason,
                contentDigest,
                elements,
                evidence);
            if (!string.Equals(
                    canonicalJson,
                    WriteVersion(version),
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The stored composition version does not match its canonical domain representation.");
            }

            return version;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or
                EncoderFallbackException or
                ArgumentException or
                InvalidOperationException or
                OverflowException)
        {
            throw new FormatException(
                "The stored composition version is invalid.",
                exception);
        }
    }

    internal static WorkingCopy ReadWorkingCopy(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);

        try
        {
            byte[] utf8 = StrictUtf8.GetBytes(canonicalJson);
            byte[] canonicalUtf8 = CanonicalJson.CanonicalizeToUtf8(utf8);
            if (!utf8.AsSpan().SequenceEqual(canonicalUtf8))
            {
                throw new FormatException(
                    "A stored working copy must use its canonical JSON representation.");
            }

            using JsonDocument document = JsonDocument.Parse(canonicalUtf8);
            JsonElement root = document.RootElement;
            bool hasBaseVersion = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("baseVersionId", out _);
            EnsureExactProperties(
                root,
                hasBaseVersion
                    ? ["id", "projectId", "baseVersionId", "createdAt", "updatedAt", "elements"]
                    : ["id", "projectId", "createdAt", "updatedAt", "elements"]);
            var workingCopy = new WorkingCopy(
                ReadEntityId<WorkingCopyIdTag>(root, "id"),
                ReadEntityId<ProjectIdTag>(root, "projectId"),
                hasBaseVersion
                    ? ReadEntityId<CompositionVersionIdTag>(root, "baseVersionId")
                    : null,
                ReadTimestamp(root, "createdAt"),
                ReadTimestamp(root, "updatedAt"),
                ReadElements(root.GetProperty("elements")));
            if (!string.Equals(
                    canonicalJson,
                    WriteWorkingCopy(workingCopy),
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The stored working copy does not match its canonical domain representation.");
            }

            return workingCopy;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or
                EncoderFallbackException or
                ArgumentException or
                InvalidOperationException or
                OverflowException)
        {
            throw new FormatException("The stored working copy is invalid.", exception);
        }
    }

    internal static byte[] WriteElementsObjectToUtf8(
        IReadOnlyList<CompositionElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("elements");
            WriteElements(writer, elements);
            writer.WriteEndObject();
        });
    }

    internal static string WriteVersion(CompositionVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return StrictUtf8.GetString(WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteString(writer, "id", version.Id.ToString());
            WriteString(writer, "projectId", version.ProjectId.ToString());
            if (version.ParentVersionId is not null)
            {
                WriteString(writer, "parentVersionId", version.ParentVersionId.ToString());
            }

            WriteString(writer, "createdByActorId", version.CreatedByActorId);
            WriteTimestamp(writer, "createdAt", version.CreatedAt);
            WriteString(writer, "changeReason", version.ChangeReason);
            WriteString(writer, "contentDigest", version.ContentDigest.Hex);
            writer.WritePropertyName("elements");
            WriteElements(writer, version.Elements);
            if (version.Evidence is not null)
            {
                writer.WritePropertyName("evidence");
                WriteEvidence(writer, version.Evidence);
            }

            writer.WriteEndObject();
        }));
    }

    private static CompositionEvidenceEnvelope ReadEvidence(JsonElement value)
    {
        EnsureExactProperties(value, ["kind", "contentDigest", "canonicalJson"]);
        return CompositionEvidenceEnvelope.Restore(
            ReadString(value, "kind"),
            ReadContentDigest(value, "contentDigest"),
            ReadString(value, "canonicalJson"));
    }

    private static void WriteEvidence(
        Utf8JsonWriter writer,
        CompositionEvidenceEnvelope evidence)
    {
        writer.WriteStartObject();
        WriteString(writer, "kind", evidence.Kind);
        WriteString(writer, "contentDigest", evidence.ContentDigest.Hex);
        WriteString(writer, "canonicalJson", evidence.CanonicalJson);
        writer.WriteEndObject();
    }

    internal static string WriteWorkingCopy(WorkingCopy workingCopy)
    {
        ArgumentNullException.ThrowIfNull(workingCopy);
        return StrictUtf8.GetString(WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            WriteString(writer, "id", workingCopy.Id.ToString());
            WriteString(writer, "projectId", workingCopy.ProjectId.ToString());
            if (workingCopy.BaseVersionId is not null)
            {
                WriteString(writer, "baseVersionId", workingCopy.BaseVersionId.ToString());
            }

            WriteTimestamp(writer, "createdAt", workingCopy.CreatedAt);
            WriteTimestamp(writer, "updatedAt", workingCopy.UpdatedAt);
            writer.WritePropertyName("elements");
            WriteElements(writer, workingCopy.Elements);
            writer.WriteEndObject();
        }));
    }

    private static IReadOnlyList<CompositionElement> ReadElements(
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Composition elements must be an array.");
        }

        var elements = new List<CompositionElement>(value.GetArrayLength());
        foreach (JsonElement element in value.EnumerateArray())
        {
            elements.Add(ReadElement(element));
        }

        return elements;
    }

    private static CompositionElement ReadElement(JsonElement value)
    {
        bool hasSourceRange = value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("sourceRange", out _);
        bool hasAdoptedSuggestion = value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("adoptedSuggestion", out _);
        var expectedProperties = new List<string>
        {
            "id",
            "outputRange",
            "origin",
        };
        if (hasSourceRange)
        {
            expectedProperties.Add("sourceRange");
        }

        if (hasAdoptedSuggestion)
        {
            expectedProperties.Add("adoptedSuggestion");
        }

        EnsureExactProperties(value, expectedProperties);
        EntityId<CompositionElementIdTag> id =
            ReadEntityId<CompositionElementIdTag>(value, "id");
        TimeRange outputRange = ReadTimeRange(
            value.GetProperty("outputRange"),
            allowNegativeStart: false);
        OriginRef origin = ReadOrigin(value.GetProperty("origin"));
        if ((origin.Kind == OriginKind.SourceSpan) != hasSourceRange)
        {
            throw new FormatException(
                "Only SourceSpan composition elements require a source range.");
        }

        TimeRange? sourceRange = hasSourceRange
            ? ReadTimeRange(
                value.GetProperty("sourceRange"),
                allowNegativeStart: true)
            : null;
        SuggestionReference? adoptedSuggestion = hasAdoptedSuggestion
            ? ReadSuggestionReference(value.GetProperty("adoptedSuggestion"))
            : null;
        return new CompositionElement(
            id,
            outputRange,
            origin,
            adoptedSuggestion,
            sourceRange);
    }

    private static TimeRange ReadTimeRange(
        JsonElement value,
        bool allowNegativeStart)
    {
        EnsureExactProperties(value, ["start", "duration"]);
        RationalTime start = ReadRationalTime(value.GetProperty("start"));
        RationalTime duration = ReadRationalTime(value.GetProperty("duration"));
        if ((!allowNegativeStart && start < default(RationalTime)) ||
            duration <= default(RationalTime))
        {
            throw new FormatException("The stored time range is outside its valid bounds.");
        }

        return new TimeRange(start, duration);
    }

    private static RationalTime ReadRationalTime(JsonElement value)
    {
        EnsureExactProperties(value, ["value", "rate"]);
        JsonElement numeratorValue = value.GetProperty("value");
        JsonElement rateValue = value.GetProperty("rate");
        if (numeratorValue.ValueKind != JsonValueKind.Number ||
            !TryReadMathematicalInt64(numeratorValue, out long numerator) ||
            rateValue.ValueKind != JsonValueKind.Number ||
            !TryReadMathematicalInt64(rateValue, out long rate) ||
            rate <= 0)
        {
            throw new FormatException(
                "A stored rational time must contain signed 64-bit value and positive rate integers.");
        }

        var time = new RationalTime(numerator, rate);
        if (time.Value != numerator || time.Rate != rate)
        {
            throw new FormatException("A stored rational time must be reduced.");
        }

        return time;
    }

    private static bool TryReadMathematicalInt64(
        JsonElement value,
        out long result)
    {
        if (value.TryGetDecimal(out decimal number) &&
            number == decimal.Truncate(number) &&
            number is >= long.MinValue and <= long.MaxValue)
        {
            result = decimal.ToInt64(number);
            return true;
        }

        result = default;
        return false;
    }

    private static OriginRef ReadOrigin(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("kind", out JsonElement kindValue) ||
            kindValue.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("A stored composition origin is invalid.");
        }

        return kindValue.GetString() switch
        {
            "sourceSpan" => ReadSourceSpanOrigin(value),
            "asset" => ReadAssetOrigin(value),
            "editorialRecord" => ReadEditorialRecordOrigin(value),
            _ => throw new FormatException("A stored composition origin kind is invalid."),
        };
    }

    private static OriginRef ReadSourceSpanOrigin(JsonElement value)
    {
        EnsureExactProperties(value, ["kind", "sourceSpanId"]);
        return OriginRef.FromSourceSpan(
            ReadEntityId<SourceSpanIdTag>(value, "sourceSpanId"));
    }

    private static OriginRef ReadAssetOrigin(JsonElement value)
    {
        EnsureExactProperties(value, ["kind", "assetId"]);
        return OriginRef.FromAsset(ReadEntityId<AssetIdTag>(value, "assetId"));
    }

    private static OriginRef ReadEditorialRecordOrigin(JsonElement value)
    {
        EnsureExactProperties(value, ["kind", "editorialRecordId"]);
        return OriginRef.FromEditorialRecord(
            ReadEntityId<EditorialRecordIdTag>(value, "editorialRecordId"));
    }

    private static SuggestionReference ReadSuggestionReference(JsonElement value)
    {
        EnsureExactProperties(value, ["suggestionId", "origin"]);
        EntityId<SuggestionIdTag> suggestionId =
            ReadEntityId<SuggestionIdTag>(value, "suggestionId");
        OriginRef origin = ReadOrigin(value.GetProperty("origin"));
        return SuggestionReference.FromPersistedReference(suggestionId, origin);
    }

    private static EntityId<TTag> ReadEntityId<TTag>(
        JsonElement value,
        string propertyName)
    {
        string text = ReadString(value, propertyName);
        if (!EntityId<TTag>.TryParse(text, out EntityId<TTag>? id) ||
            !string.Equals(text, id.ToString(), StringComparison.Ordinal))
        {
            throw new FormatException("A stored entity ID is not canonical.");
        }

        return id;
    }

    private static ContentDigest ReadContentDigest(
        JsonElement value,
        string propertyName)
    {
        string text = ReadString(value, propertyName);
        if (!ContentDigest.TryParse(text, out ContentDigest? digest) ||
            !string.Equals(text, digest.Hex, StringComparison.Ordinal))
        {
            throw new FormatException("A stored content digest is not canonical.");
        }

        return digest;
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement value,
        string propertyName)
    {
        string text = ReadString(value, propertyName);
        if (!DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset timestamp) ||
            timestamp.Offset != TimeSpan.Zero ||
            !string.Equals(
                text,
                timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new FormatException("A stored composition timestamp is not canonical UTC.");
        }

        return timestamp;
    }

    private static string ReadString(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.String ||
            property.GetString() is not string text)
        {
            throw new FormatException("A required stored composition string is invalid.");
        }

        return text;
    }

    private static void EnsureExactProperties(
        JsonElement value,
        IReadOnlyList<string> expectedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("A stored composition object is invalid.");
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expectedProperties.Contains(property.Name, StringComparer.Ordinal) ||
                !found.Add(property.Name))
            {
                throw new FormatException(
                    "A stored composition object contains an unexpected property.");
            }
        }

        if (found.Count != expectedProperties.Count)
        {
            throw new FormatException(
                "A stored composition object is missing a required property.");
        }
    }

    private static byte[] WriteCanonical(Action<Utf8JsonWriter> writeValue)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writeValue(writer);
            writer.Flush();
        }

        return CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
    }

    private static void WriteElements(
        Utf8JsonWriter writer,
        IReadOnlyList<CompositionElement> elements)
    {
        writer.WriteStartArray();
        for (int index = 0; index < elements.Count; index++)
        {
            CompositionElement element = elements[index]
                ?? throw new ArgumentException(
                    "Composition elements must not contain null elements.",
                    nameof(elements));
            WriteElement(writer, element);
        }

        writer.WriteEndArray();
    }

    private static void WriteElement(Utf8JsonWriter writer, CompositionElement element)
    {
        writer.WriteStartObject();
        WriteString(writer, "id", element.Id.ToString());
        writer.WritePropertyName("outputRange");
        WriteTimeRange(writer, element.OutputRange);
        writer.WritePropertyName("origin");
        WriteOrigin(writer, element.Origin);

        if (element.Origin.Kind == OriginKind.SourceSpan)
        {
            if (element.SourceRange is not TimeRange sourceRange)
            {
                throw new InvalidOperationException(
                    "A SourceSpan composition element must include a source range.");
            }

            writer.WritePropertyName("sourceRange");
            WriteTimeRange(writer, sourceRange);
        }
        else if (element.SourceRange is not null)
        {
            throw new InvalidOperationException(
                "Only a SourceSpan composition element can include a source range.");
        }

        if (element.AdoptedSuggestion is not null)
        {
            writer.WritePropertyName("adoptedSuggestion");
            WriteSuggestionReference(writer, element.AdoptedSuggestion);
        }

        writer.WriteEndObject();
    }

    private static void WriteTimeRange(Utf8JsonWriter writer, TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteRationalTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteRationalTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteRationalTime(Utf8JsonWriter writer, RationalTime time)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", time.Value);
        writer.WriteNumber("rate", time.Rate);
        writer.WriteEndObject();
    }

    private static void WriteOrigin(Utf8JsonWriter writer, OriginRef origin)
    {
        writer.WriteStartObject();
        switch (origin.Kind)
        {
            case OriginKind.SourceSpan when origin.SourceSpanId is not null:
                WriteString(writer, "kind", "sourceSpan");
                WriteString(writer, "sourceSpanId", origin.SourceSpanId.ToString());
                break;
            case OriginKind.Asset when origin.AssetId is not null:
                WriteString(writer, "kind", "asset");
                WriteString(writer, "assetId", origin.AssetId.ToString());
                break;
            case OriginKind.EditorialRecord when origin.EditorialRecordId is not null:
                WriteString(writer, "kind", "editorialRecord");
                WriteString(
                    writer,
                    "editorialRecordId",
                    origin.EditorialRecordId.ToString());
                break;
            default:
                throw new InvalidOperationException("The composition origin is invalid.");
        }

        writer.WriteEndObject();
    }

    private static void WriteSuggestionReference(
        Utf8JsonWriter writer,
        SuggestionReference suggestion)
    {
        writer.WriteStartObject();
        WriteString(writer, "suggestionId", suggestion.SuggestionId.ToString());
        writer.WritePropertyName("origin");
        WriteOrigin(writer, suggestion.Origin);
        writer.WriteEndObject();
    }

    private static void WriteTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value)
    {
        WriteString(
            writer,
            propertyName,
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static void WriteString(
        Utf8JsonWriter writer,
        string propertyName,
        string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Composition snapshot text must be valid Unicode.",
                exception);
        }

        writer.WriteString(propertyName, value);
    }
}

internal static class CompositionStableContent
{
    internal static bool Equals(CompositionElement left, CompositionElement right)
    {
        return left.OutputRange.Duration == right.OutputRange.Duration &&
            left.Origin == right.Origin &&
            left.SourceRange == right.SourceRange &&
            SuggestionEquals(left.AdoptedSuggestion, right.AdoptedSuggestion);
    }

    private static bool SuggestionEquals(
        SuggestionReference? left,
        SuggestionReference? right)
    {
        return ReferenceEquals(left, right) ||
            (left is not null &&
                right is not null &&
                left.SuggestionId == right.SuggestionId &&
                left.Origin == right.Origin);
    }
}
