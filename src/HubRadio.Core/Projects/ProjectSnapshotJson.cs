using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Serialization;

namespace HubRadio.Core.Projects;

internal static class ProjectSnapshotJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string WriteProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            WriteString(writer, "id", project.Id.ToString());
            WriteString(writer, "contentName", project.ContentName);
            WriteString(writer, "purpose", project.Purpose);
            WriteString(writer, "audience", project.Audience);
            WriteString(
                writer,
                "publicationScope",
                GetPublicationScopeText(project.PublicationScope));
            if (project.Deadline is DateTimeOffset deadline)
            {
                WriteTimestamp(writer, "deadline", deadline);
            }

            writer.WritePropertyName("assets");
            writer.WriteStartArray();
            foreach (Asset asset in project.Assets.OrderBy(
                static item => item.Id.ToString(),
                StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                WriteString(writer, "id", asset.Id.ToString());
                WriteString(writer, "fileName", asset.FileName);
                WriteString(writer, "mediaType", asset.MediaType);
                WriteString(writer, "contentDigest", asset.ContentDigest.Hex);
                writer.WriteNumber("byteSize", asset.ByteSize);
                writer.WritePropertyName("rights");
                writer.WriteStartObject();
                WriteString(
                    writer,
                    "status",
                    GetRightsStatusText(asset.Rights.Status));
                if (asset.Rights.EvidenceReference is not null)
                {
                    WriteString(
                        writer,
                        "evidenceReference",
                        asset.Rights.EvidenceReference);
                }

                WriteString(writer, "allowedUse", asset.Rights.AllowedUse);
                writer.WriteBoolean(
                    "externalPublicationAllowed",
                    asset.Rights.ExternalPublicationAllowed);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("performers");
            writer.WriteStartArray();
            foreach (Performer performer in project.Performers.OrderBy(
                static item => item.Id.ToString(),
                StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                WriteString(writer, "id", performer.Id.ToString());
                WriteString(writer, "displayName", performer.DisplayName);
                WriteString(writer, "role", performer.Role);
                if (performer.TitleConfirmedAt is DateTimeOffset confirmedAt)
                {
                    WriteTimestamp(writer, "titleConfirmedAt", confirmedAt);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return StrictUtf8.GetString(
            CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
    }

    private static string GetPublicationScopeText(PublicationScope scope) =>
        scope switch
        {
            PublicationScope.InternalOnly => "internalOnly",
            PublicationScope.SpecificGroupOnly => "specificGroupOnly",
            PublicationScope.ExternalCandidate => "externalCandidate",
            PublicationScope.ExternalPublicationApproved =>
                "externalPublicationApproved",
            PublicationScope.PublicationForbidden => "publicationForbidden",
            PublicationScope.PendingDecision => "pendingDecision",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    private static string GetRightsStatusText(RightsStatus status) =>
        status switch
        {
            RightsStatus.Unconfirmed => "unconfirmed",
            RightsStatus.Approved => "approved",
            RightsStatus.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static void WriteTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value) =>
        WriteString(
            writer,
            propertyName,
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

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
                "Project snapshot text must be valid Unicode.",
                exception);
        }

        writer.WriteString(propertyName, value);
    }
}
