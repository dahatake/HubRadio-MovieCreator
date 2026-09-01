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
            EnsureExactProperties(
                root,
                hasParent
                    ? [
                        "id",
                        "projectId",
                        "parentVersionId",
                        "createdByActorId",
                        "createdAt",
                        "changeReason",
                        "contentDigest",
                        "elements",
                    ]
                    : [
                        "id",
                        "projectId",
                        "createdByActorId",
                        "createdAt",
                        "changeReason",
                        "contentDigest",
                        "elements",
                    ]);

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
                elements);
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
