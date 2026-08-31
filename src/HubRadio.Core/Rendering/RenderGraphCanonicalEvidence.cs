using System.Buffers;
using System.Text.Json;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;

namespace HubRadio.Core.Rendering;

public static class RenderGraphCanonicalEvidence
{
    public static ContentDigest ComputeCompositionDigest(
        IReadOnlyList<CompositionElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return ContentDigest.Compute(WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("elements");
            writer.WriteStartArray();
            foreach (CompositionElement element in elements)
            {
                ArgumentNullException.ThrowIfNull(element);
                writer.WriteStartObject();
                writer.WriteString("id", element.Id.ToString());
                writer.WritePropertyName("outputRange");
                WriteRange(writer, element.OutputRange);
                writer.WritePropertyName("origin");
                WriteOrigin(writer, element.Origin);
                if (element.SourceRange is TimeRange sourceRange)
                {
                    writer.WritePropertyName("sourceRange");
                    WriteRange(writer, sourceRange);
                }

                if (element.AdoptedSuggestion is not null)
                {
                    writer.WritePropertyName("adoptedSuggestion");
                    writer.WriteStartObject();
                    writer.WriteString(
                        "suggestionId",
                        element.AdoptedSuggestion.SuggestionId.ToString());
                    writer.WritePropertyName("origin");
                    WriteOrigin(writer, element.AdoptedSuggestion.Origin);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }));
    }

    public static ContentDigest ComputeTimelineMapDigest(TimelineMap timelineMap)
    {
        ArgumentNullException.ThrowIfNull(timelineMap);
        return ContentDigest.Compute(WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", timelineMap.Id.ToString());
            writer.WriteString(
                "compositionVersionId",
                timelineMap.CompositionVersionId.ToString());
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (TimelineMapEntry entry in timelineMap.Entries)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("outputRange");
                WriteRange(writer, entry.OutputRange);
                writer.WritePropertyName("origin");
                WriteOrigin(writer, entry.Origin);
                if (entry.SourceRange is TimeRange sourceRange)
                {
                    writer.WritePropertyName("sourceRange");
                    WriteRange(writer, sourceRange);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }));
    }

    private static byte[] WriteCanonical(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }

        return CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
    }

    private static void WriteRange(Utf8JsonWriter writer, TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter writer, RationalTime time)
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
                writer.WriteString("kind", "sourceSpan");
                writer.WriteString("sourceSpanId", origin.SourceSpanId.ToString());
                break;
            case OriginKind.Asset when origin.AssetId is not null:
                writer.WriteString("kind", "asset");
                writer.WriteString("assetId", origin.AssetId.ToString());
                break;
            case OriginKind.EditorialRecord
                when origin.EditorialRecordId is not null:
                writer.WriteString("kind", "editorialRecord");
                writer.WriteString(
                    "editorialRecordId",
                    origin.EditorialRecordId.ToString());
                break;
            default:
                throw new InvalidOperationException(
                    "The render evidence origin is invalid.");
        }

        writer.WriteEndObject();
    }
}