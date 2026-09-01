using System.Globalization;
using System.Text.Json;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Compositions;

internal static class WorkingCopySnapshotCodec
{
    private static readonly string[] RequiredRootProperties =
    [
        "id",
        "projectId",
        "createdAt",
        "updatedAt",
        "elements",
    ];

    private static readonly string[] OptionalRootProperties = ["baseVersionId"];
    private static readonly string[] RequiredElementProperties =
    [
        "id",
        "outputRange",
        "origin",
    ];

    private static readonly string[] OptionalElementProperties =
    [
        "sourceRange",
        "adoptedSuggestion",
    ];

    internal static WorkingCopy Read(StoredWorkingCopy stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        try
        {
            using JsonDocument document = JsonDocument.Parse(stored.CanonicalJson);
            JsonElement root = document.RootElement;
            RequireProperties(
                root,
                RequiredRootProperties,
                OptionalRootProperties);

            EntityId<WorkingCopyIdTag> id = ReadEntityId<WorkingCopyIdTag>(
                root.GetProperty("id"));
            EntityId<ProjectIdTag> projectId = ReadEntityId<ProjectIdTag>(
                root.GetProperty("projectId"));
            EntityId<CompositionVersionIdTag>? baseVersionId =
                root.TryGetProperty("baseVersionId", out JsonElement baseVersion)
                    ? ReadEntityId<CompositionVersionIdTag>(baseVersion)
                    : null;
            DateTimeOffset createdAt = ReadTimestamp(root.GetProperty("createdAt"));
            DateTimeOffset updatedAt = ReadTimestamp(root.GetProperty("updatedAt"));
            CompositionElement[] elements = ReadElements(
                root.GetProperty("elements"));

            var workingCopy = new WorkingCopy(
                id,
                projectId,
                baseVersionId,
                createdAt,
                updatedAt,
                elements);
            ValidateCanonicalSequence(workingCopy.Elements);

            if (workingCopy.Id != stored.Id ||
                workingCopy.ProjectId != stored.ProjectId ||
                workingCopy.BaseVersionId != stored.BaseVersionId ||
                workingCopy.UpdatedAt != stored.UpdatedAt ||
                !string.Equals(
                    CompositionSnapshotJson.WriteWorkingCopy(workingCopy),
                    stored.CanonicalJson,
                    StringComparison.Ordinal))
            {
                throw InvalidStoredWorkingCopy();
            }

            return workingCopy;
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or
                InvalidOperationException or JsonException or OverflowException)
        {
            throw InvalidStoredWorkingCopy(exception);
        }
    }

    private static CompositionElement[] ReadElements(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidStoredWorkingCopy();
        }

        var elements = new List<CompositionElement>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            elements.Add(ReadElement(item));
        }

        return elements.ToArray();
    }

    private static CompositionElement ReadElement(JsonElement value)
    {
        RequireProperties(
            value,
            RequiredElementProperties,
            OptionalElementProperties);
        EntityId<CompositionElementIdTag> id =
            ReadEntityId<CompositionElementIdTag>(value.GetProperty("id"));
        TimeRange outputRange = ReadTimeRange(
            value.GetProperty("outputRange"),
            requireNonnegativeStart: true);
        OriginRef origin = ReadOrigin(value.GetProperty("origin"));
        TimeRange? sourceRange =
            value.TryGetProperty("sourceRange", out JsonElement source)
                ? ReadTimeRange(source, requireNonnegativeStart: false)
                : null;
        SuggestionReference? adoptedSuggestion =
            value.TryGetProperty(
                "adoptedSuggestion",
                out JsonElement suggestion)
                ? ReadSuggestionReference(suggestion)
                : null;

        return new CompositionElement(
            id,
            outputRange,
            origin,
            adoptedSuggestion,
            sourceRange);
    }

    private static SuggestionReference ReadSuggestionReference(
        JsonElement value)
    {
        RequireProperties(value, ["suggestionId", "origin"], []);
        EntityId<SuggestionIdTag> suggestionId =
            ReadEntityId<SuggestionIdTag>(value.GetProperty("suggestionId"));
        OriginRef origin = ReadOrigin(value.GetProperty("origin"));
        return SuggestionReference.RestoreFromCanonicalSnapshot(
            suggestionId,
            origin);
    }

    private static OriginRef ReadOrigin(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("kind", out JsonElement kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            throw InvalidStoredWorkingCopy();
        }

        return kindElement.GetString() switch
        {
            "sourceSpan" => CreateSourceSpanOrigin(value),
            "asset" => CreateAssetOrigin(value),
            "editorialRecord" => CreateEditorialRecordOrigin(value),
            _ => throw InvalidStoredWorkingCopy(),
        };
    }

    private static OriginRef CreateSourceSpanOrigin(JsonElement value)
    {
        RequireProperties(value, ["kind", "sourceSpanId"], []);
        return OriginRef.FromSourceSpan(
            ReadEntityId<SourceSpanIdTag>(value.GetProperty("sourceSpanId")));
    }

    private static OriginRef CreateAssetOrigin(JsonElement value)
    {
        RequireProperties(value, ["kind", "assetId"], []);
        return OriginRef.FromAsset(
            ReadEntityId<AssetIdTag>(value.GetProperty("assetId")));
    }

    private static OriginRef CreateEditorialRecordOrigin(JsonElement value)
    {
        RequireProperties(value, ["kind", "editorialRecordId"], []);
        return OriginRef.FromEditorialRecord(
            ReadEntityId<EditorialRecordIdTag>(
                value.GetProperty("editorialRecordId")));
    }

    private static TimeRange ReadTimeRange(
        JsonElement value,
        bool requireNonnegativeStart)
    {
        RequireProperties(value, ["start", "duration"], []);
        RationalTime start = ReadRationalTime(value.GetProperty("start"));
        RationalTime duration = ReadRationalTime(
            value.GetProperty("duration"));
        if ((requireNonnegativeStart && start < default(RationalTime)) ||
            duration <= default(RationalTime))
        {
            throw InvalidStoredWorkingCopy();
        }

        return new TimeRange(start, duration);
    }

    private static RationalTime ReadRationalTime(JsonElement value)
    {
        RequireProperties(value, ["value", "rate"], []);
        long numerator = ReadMathematicalInt64(value.GetProperty("value"));
        long denominator = ReadMathematicalInt64(value.GetProperty("rate"));
        if (denominator <= 0)
        {
            throw InvalidStoredWorkingCopy();
        }

        var time = new RationalTime(numerator, denominator);
        if (time.Value != numerator || time.Rate != denominator)
        {
            throw InvalidStoredWorkingCopy();
        }

        return time;
    }

    private static long ReadMathematicalInt64(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !decimal.TryParse(
                value.GetRawText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed < long.MinValue ||
            parsed > long.MaxValue)
        {
            throw InvalidStoredWorkingCopy();
        }

        return decimal.ToInt64(parsed);
    }

    private static EntityId<TTag> ReadEntityId<TTag>(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string text ||
            !EntityId<TTag>.TryParse(text, out EntityId<TTag>? id) ||
            !string.Equals(text, id.ToString(), StringComparison.Ordinal))
        {
            throw InvalidStoredWorkingCopy();
        }

        return id;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string text ||
            !DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp) ||
            timestamp.Offset != TimeSpan.Zero ||
            !string.Equals(
                text,
                timestamp.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw InvalidStoredWorkingCopy();
        }

        return timestamp;
    }

    private static void ValidateCanonicalSequence(
        IReadOnlyList<CompositionElement> elements)
    {
        RationalTime expectedStart = default;
        foreach (CompositionElement element in elements)
        {
            if (element.OutputRange.Start != expectedStart)
            {
                throw InvalidStoredWorkingCopy();
            }

            expectedStart = element.OutputRange.End;
        }
    }

    private static void RequireProperties(
        JsonElement value,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidStoredWorkingCopy();
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if ((!required.Contains(property.Name, StringComparer.Ordinal) &&
                    !optional.Contains(property.Name, StringComparer.Ordinal)) ||
                !found.Add(property.Name))
            {
                throw InvalidStoredWorkingCopy();
            }
        }

        if (required.Any(property => !found.Contains(property)))
        {
            throw InvalidStoredWorkingCopy();
        }
    }

    private static InvalidOperationException InvalidStoredWorkingCopy(
        Exception? innerException = null) =>
        new("Stored working copy data is invalid.", innerException);
}