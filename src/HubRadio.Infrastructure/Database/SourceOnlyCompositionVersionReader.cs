using System.Globalization;
using System.Text.Json;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;

namespace HubRadio.Infrastructure.Database;

internal readonly record struct SourceOnlyVersionLineageNode(
    EntityId<CompositionVersionIdTag> Id,
    EntityId<CompositionVersionIdTag>? ParentVersionId,
    ContentDigest ContentDigest,
    DateTimeOffset CreatedAt);

/// <summary>
/// Reads the persisted CompositionSnapshotJson version shape without depending on App.
/// Validates domain elements and envelope integrity, not App's semantic evidence lineage
/// or proof that a coordinator actually ran the pipeline. The store calls this for EVERY
/// ancestor and retains only the validated metadata, not all ancestors' element arrays.
/// </summary>
internal static class SourceOnlyCompositionVersionReader
{
    internal static SourceOnlyVersionLineageNode Validate(string canonicalJson, EntityId<CompositionVersionIdTag> id,
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag>? parentId,
        ContentDigest digest, DateTimeOffset createdAt)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(canonicalJson);
            JsonElement root = document.RootElement;
            Properties(root, ["id", "projectId", "createdByActorId", "createdAt", "changeReason", "contentDigest", "elements"],
                ["parentVersionId", "evidence"]);
            JsonElement items = root.GetProperty("elements");
            if (items.ValueKind != JsonValueKind.Array) { throw Invalid(); }
            CompositionElement[] elements = items.EnumerateArray().Select(ReadElement).ToArray();
            var version = new CompositionVersion(
                Id<CompositionVersionIdTag>(root, "id"), Id<ProjectIdTag>(root, "projectId"),
                root.TryGetProperty("parentVersionId", out _) ? Id<CompositionVersionIdTag>(root, "parentVersionId") : null,
                Text(root, "createdByActorId"), Timestamp(root), Text(root, "changeReason"),
                Digest(root, "contentDigest"), elements);
            if (version.Id != id || version.ProjectId != projectId || version.ParentVersionId != parentId ||
                version.CreatedAt != createdAt || version.ContentDigest != digest || ComputeDigest(elements) != digest)
            { throw Invalid(); }
            if (root.TryGetProperty("evidence", out JsonElement evidence))
            {
                Properties(evidence, ["kind", "contentDigest", "canonicalJson"]);
                _ = CompositionEvidenceEnvelope.Restore(Text(evidence, "kind"),
                    Digest(evidence, "contentDigest"), Text(evidence, "canonicalJson"));
            }
            return new(version.Id, version.ParentVersionId, version.ContentDigest, version.CreatedAt);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
            InvalidOperationException or JsonException or OverflowException or KeyNotFoundException)
        {
            // No raw values or nested parser diagnostics leave the native boundary.
            throw Invalid();
        }
    }

    private static CompositionElement ReadElement(JsonElement value)
    {
        Properties(value, ["id", "outputRange", "origin"], ["sourceRange", "adoptedSuggestion"]);
        SuggestionReference? suggestion = null;
        if (value.TryGetProperty("adoptedSuggestion", out JsonElement adopted))
        {
            Properties(adopted, ["suggestionId", "origin"]);
            suggestion = SuggestionReference.FromPersistedReference(Id<SuggestionIdTag>(adopted, "suggestionId"),
                Origin(adopted.GetProperty("origin")));
        }
        return new CompositionElement(Id<CompositionElementIdTag>(value, "id"),
            Range(value.GetProperty("outputRange")), Origin(value.GetProperty("origin")), suggestion,
            value.TryGetProperty("sourceRange", out JsonElement source) ? Range(source) : null);
    }

    private static OriginRef Origin(JsonElement value)
    {
        switch (Text(value, "kind"))
        {
            case "sourceSpan":
                Properties(value, ["kind", "sourceSpanId"]);
                return OriginRef.FromSourceSpan(Id<SourceSpanIdTag>(value, "sourceSpanId"));
            case "asset":
                Properties(value, ["kind", "assetId"]);
                return OriginRef.FromAsset(Id<AssetIdTag>(value, "assetId"));
            case "editorialRecord":
                Properties(value, ["kind", "editorialRecordId"]);
                return OriginRef.FromEditorialRecord(Id<EditorialRecordIdTag>(value, "editorialRecordId"));
            default: throw Invalid();
        }
    }

    private static TimeRange Range(JsonElement value)
    {
        Properties(value, ["start", "duration"]);
        return new TimeRange(Time(value.GetProperty("start")), Time(value.GetProperty("duration")));
    }

    private static RationalTime Time(JsonElement value)
    {
        Properties(value, ["value", "rate"]);
        long numerator = Integer(value.GetProperty("value"));
        long rate = Integer(value.GetProperty("rate"));
        if (rate <= 0) { throw Invalid(); }
        var time = new RationalTime(numerator, rate);
        if (time.Value != numerator || time.Rate != rate) { throw Invalid(); }
        return time;
    }

    private static long Integer(JsonElement value)
    {
        // Parse raw tokens exactly: decimal can round a fractional tail into an integer.
        // Canonical integers may use exponent notation (for example, 10 becomes 1e1).
        if (value.ValueKind != JsonValueKind.Number || !long.TryParse(value.GetRawText(),
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out long number))
        { throw Invalid(); }
        return number;
    }

    private static EntityId<T> Id<T>(JsonElement value, string name)
    {
        string text = Text(value, name);
        return EntityId<T>.TryParse(text, out EntityId<T>? id) && id.ToString() == text ? id : throw Invalid();
    }

    private static ContentDigest Digest(JsonElement value, string name)
    {
        string text = Text(value, name);
        return ContentDigest.TryParse(text, out ContentDigest? digest) && digest.Hex == text ? digest : throw Invalid();
    }

    private static DateTimeOffset Timestamp(JsonElement root)
    {
        string text = Text(root, "createdAt");
        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out DateTimeOffset value) || value.Offset != TimeSpan.Zero ||
            value.ToString("O", CultureInfo.InvariantCulture) != text) { throw Invalid(); }
        return value;
    }

    private static string Text(JsonElement value, string name)
    {
        JsonElement property = value.GetProperty(name);
        return property.ValueKind == JsonValueKind.String ? property.GetString()! : throw Invalid();
    }

    private static void Properties(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw Invalid(); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!names.Add(property.Name) || (!required.Contains(property.Name, StringComparer.Ordinal) &&
                !(optional?.Contains(property.Name, StringComparer.Ordinal) ?? false))) { throw Invalid(); }
        }
        if (required.Any(name => !names.Contains(name))) { throw Invalid(); }
    }

    // Re-encode typed, validated elements, not the full stored payload or claimed digest.
    // This is the same digest domain as App's CompositionSnapshotJson.
    private static ContentDigest ComputeDigest(IReadOnlyList<CompositionElement> elements)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("elements");
            foreach (CompositionElement element in elements)
            {
                writer.WriteStartObject();
                writer.WriteString("id", element.Id.ToString());
                WriteRange(writer, "outputRange", element.OutputRange);
                WriteOrigin(writer, element.Origin);
                if (element.SourceRange is TimeRange source) { WriteRange(writer, "sourceRange", source); }
                if (element.AdoptedSuggestion is SuggestionReference adopted)
                {
                    writer.WriteStartObject("adoptedSuggestion");
                    writer.WriteString("suggestionId", adopted.SuggestionId.ToString());
                    WriteOrigin(writer, adopted.Origin);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return ContentDigest.Compute(CanonicalJson.CanonicalizeToUtf8(stream.ToArray()));
    }

    private static void WriteRange(Utf8JsonWriter writer, string name, TimeRange range)
    {
        writer.WriteStartObject(name);
        WriteTime(writer, "start", range.Start);
        WriteTime(writer, "duration", range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter writer, string name, RationalTime time)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("value", time.Value);
        writer.WriteNumber("rate", time.Rate);
        writer.WriteEndObject();
    }

    private static void WriteOrigin(Utf8JsonWriter writer, OriginRef origin)
    {
        writer.WriteStartObject("origin");
        switch (origin.Kind)
        {
            case OriginKind.SourceSpan:
                writer.WriteString("kind", "sourceSpan");
                writer.WriteString("sourceSpanId", origin.SourceSpanId!.ToString());
                break;
            case OriginKind.Asset:
                writer.WriteString("kind", "asset");
                writer.WriteString("assetId", origin.AssetId!.ToString());
                break;
            case OriginKind.EditorialRecord:
                writer.WriteString("kind", "editorialRecord");
                writer.WriteString("editorialRecordId", origin.EditorialRecordId!.ToString());
                break;
            default: throw Invalid();
        }
        writer.WriteEndObject();
    }

    private static InvalidOperationException Invalid() => SqliteStoreSupport.InvalidStoredData("source-only composition version");
}