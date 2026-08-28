using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;

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
            expectedRevision,
            newVersionId,
            actorId,
            createdAt,
            changeReason,
            expectedContentDigest: null);
    }

    public CheckpointResult RollbackAsChild(
        WorkingCopy currentDraft,
        CompositionVersion targetVersion,
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason)
    {
        ArgumentNullException.ThrowIfNull(currentDraft);
        ArgumentNullException.ThrowIfNull(targetVersion);

        if (targetVersion.ProjectId != currentDraft.ProjectId)
        {
            throw new ArgumentException(
                "The rollback target and current working copy must belong to the same project.",
                nameof(targetVersion));
        }

        EnsureTimestampDoesNotMoveBackwards(currentDraft, createdAt);

        return CreateAndStoreCheckpoint(
            currentDraft,
            targetVersion.Elements,
            expectedRevision,
            newVersionId,
            actorId,
            createdAt,
            changeReason,
            targetVersion.ContentDigest);
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
        int expectedRevision,
        EntityId<CompositionVersionIdTag> newVersionId,
        string actorId,
        DateTimeOffset createdAt,
        string changeReason,
        ContentDigest? expectedContentDigest)
    {
        byte[] canonicalElementsJson =
            CompositionSnapshotJson.WriteElementsObjectToUtf8(elements);
        ContentDigest contentDigest = ContentDigest.Compute(canonicalElementsJson);
        if (expectedContentDigest is not null && contentDigest != expectedContentDigest)
        {
            throw new InvalidOperationException(
                "The rollback target content digest does not match its canonical element snapshot.");
        }

        var version = new CompositionVersion(
            newVersionId,
            draft.ProjectId,
            draft.BaseVersionId,
            actorId,
            createdAt,
            changeReason,
            contentDigest,
            elements);
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
        int workingCopyRevision = _projectStore.CheckpointWorkingCopy(
            version,
            canonicalVersionJson,
            updatedWorkingCopy,
            canonicalWorkingCopyJson,
            expectedRevision);

        return new CheckpointResult(
            version,
            updatedWorkingCopy,
            workingCopyRevision,
            canonicalVersionJson,
            canonicalWorkingCopyJson);
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
            writer.WriteEndObject();
        }));
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
