using System.Globalization;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.App.Web;

internal enum TimelineBridgeStatus
{
    Available,
    Forbidden,
    NotFound,
    Conflict,
    InvalidOperation,
    Unavailable,
}

internal static class TimelineBridgeProtocol
{
    internal static bool HasValidParameters(
        BridgeMethod method,
        JsonElement parameters)
    {
        if (method == BridgeMethod.TimelineRead)
        {
            return HasExactProperties(parameters, ["projectId"]) &&
                TryReadCanonicalId(
                    parameters.GetProperty("projectId"),
                    out _);
        }

        return method == BridgeMethod.TimelineApply &&
            HasExactProperties(
                parameters,
                ["projectId", "expectedRevision", "operations"]) &&
            TryReadCanonicalId(
                parameters.GetProperty("projectId"),
                out _) &&
            IsNonnegativeInt32(parameters.GetProperty("expectedRevision")) &&
            IsOperationBatch(parameters.GetProperty("operations"));
    }

    internal static EntityId<ProjectIdTag> ReadProjectId(BridgeRequest request)
    {
        return EntityId<ProjectIdTag>.Parse(
            request.Parameters.GetProperty("projectId").GetString()!);
    }

    internal static int ReadExpectedRevision(BridgeRequest request)
    {
        JsonElement value = request.Parameters.GetProperty("expectedRevision");
        _ = value.TryGetDecimal(out decimal parsed);
        return decimal.ToInt32(parsed);
    }

    internal static IReadOnlyList<EditOperation> ReadOperations(
        BridgeRequest request)
    {
        return request.Parameters
            .GetProperty("operations")
            .EnumerateArray()
            .Select(ReadOperation)
            .ToArray();
    }

    private static EditOperation ReadOperation(JsonElement value)
    {
        string kind = value.GetProperty("kind").GetString()!;
        EntityId<CompositionElementIdTag> elementId =
            EntityId<CompositionElementIdTag>.Parse(
                value.GetProperty("elementId").GetString()!);
        return kind switch
        {
            "keep" => EditOperation.Keep(elementId),
            "delete" => EditOperation.Delete(elementId),
            "split" => EditOperation.Split(
                elementId,
                ReadRationalTime(value.GetProperty("splitOffset")),
                EntityId<CompositionElementIdTag>.Parse(
                    value.GetProperty("rightElementId").GetString()!)),
            "move" => EditOperation.Move(
                elementId,
                ReadNonnegativeInt32(value.GetProperty("targetIndex"))),
            _ => throw new InvalidOperationException(
                "The parsed timeline operation kind is unsupported."),
        };
    }

    private static RationalTime ReadRationalTime(JsonElement value)
    {
        _ = long.TryParse(
            value.GetProperty("value").GetString(),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out long numerator);
        _ = long.TryParse(
            value.GetProperty("rate").GetString(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long denominator);
        return new RationalTime(numerator, denominator);
    }

    private static int ReadNonnegativeInt32(JsonElement value)
    {
        _ = value.TryGetDecimal(out decimal parsed);
        return decimal.ToInt32(parsed);
    }

    private static bool IsOperationBatch(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (JsonElement operation in value.EnumerateArray())
        {
            if (!IsOperation(operation))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOperation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("kind", out JsonElement kindElement) ||
            kindElement.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty(
                "elementId",
                out JsonElement elementIdElement) ||
            !TryReadCanonicalId(elementIdElement, out Guid elementId))
        {
            return false;
        }

        return kindElement.GetString() switch
        {
            "keep" or "delete" => HasExactProperties(
                value,
                ["kind", "elementId"]),
            "split" => HasExactProperties(
                    value,
                    ["kind", "elementId", "splitOffset", "rightElementId"]) &&
                IsPositiveCanonicalRational(value.GetProperty("splitOffset")) &&
                TryReadCanonicalId(
                    value.GetProperty("rightElementId"),
                    out Guid rightElementId) &&
                rightElementId != elementId,
            "move" => HasExactProperties(
                    value,
                    ["kind", "elementId", "targetIndex"]) &&
                IsNonnegativeInt32(value.GetProperty("targetIndex")),
            _ => false,
        };
    }

    private static bool IsPositiveCanonicalRational(JsonElement value)
    {
        if (!HasExactProperties(value, ["value", "rate"]) ||
            value.GetProperty("value").ValueKind != JsonValueKind.String ||
            value.GetProperty("rate").ValueKind != JsonValueKind.String ||
            !TryReadCanonicalInt64(
                value.GetProperty("value").GetString(),
                allowNegative: false,
                out long numerator) ||
            numerator <= 0 ||
            !TryReadCanonicalInt64(
                value.GetProperty("rate").GetString(),
                allowNegative: false,
                out long denominator) ||
            denominator <= 0)
        {
            return false;
        }

        var rational = new RationalTime(numerator, denominator);
        return rational.Value == numerator && rational.Rate == denominator;
    }

    private static bool TryReadCanonicalInt64(
        string? value,
        bool allowNegative,
        out long parsed)
    {
        NumberStyles style = allowNegative
            ? NumberStyles.AllowLeadingSign
            : NumberStyles.None;
        if (value is not null &&
            long.TryParse(
                value,
                style,
                CultureInfo.InvariantCulture,
                out parsed) &&
            string.Equals(
                value,
                parsed.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return true;
        }

        parsed = default;
        return false;
    }

    private static bool TryReadCanonicalId(
        JsonElement value,
        out Guid id)
    {
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString() is string text &&
            Guid.TryParseExact(text, "D", out id) &&
            id != Guid.Empty &&
            string.Equals(text, id.ToString("D"), StringComparison.Ordinal))
        {
            return true;
        }

        id = Guid.Empty;
        return false;
    }

    private static bool IsNonnegativeInt32(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out decimal parsed) &&
            parsed == decimal.Truncate(parsed) &&
            parsed is >= 0 and <= int.MaxValue;
    }

    private static bool HasExactProperties(
        JsonElement value,
        IReadOnlyList<string> expectedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expectedProperties.Contains(
                    property.Name,
                    StringComparer.Ordinal) ||
                !found.Add(property.Name))
            {
                return false;
            }
        }

        return found.Count == expectedProperties.Count;
    }
}

internal interface ITimelineBridgeAuthorization
{
    void DemandRead();

    string DemandEdit();
}

internal sealed class TimelineBridgeAuthorization :
    ITimelineBridgeAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal TimelineBridgeAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(authorizationService, GetActorSidProvider(actorProvider))
    {
    }

    internal TimelineBridgeAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
    }

    public void DemandRead()
    {
        authorizationService.Demand(
            getCurrentSid(),
            AuthorizationOperation.ViewProject);
    }

    public string DemandEdit()
    {
        string actorSid = getCurrentSid();
        authorizationService.DemandAs(
            actorSid,
            AuthorizationRole.Editor,
            AuthorizationOperation.CreateCompositionVersion);
        return actorSid;
    }

    private static Func<string> GetActorSidProvider(
        WindowsActorProvider actorProvider)
    {
        ArgumentNullException.ThrowIfNull(actorProvider);
        return actorProvider.GetCurrentSid;
    }
}

internal sealed class TimelineBridgeRequestHandler
{
    private const string ChangeReason = "Apply typed timeline edit operations.";

    private readonly ProjectStore store;
    private readonly VersionService versionService;
    private readonly ITimelineBridgeAuthorization authorization;
    private readonly Func<DateTimeOffset> getUtcNow;
    private readonly Func<EntityId<CompositionVersionIdTag>> createVersionId;
    private readonly Func<EntityId<TimelineMapIdTag>> createTimelineMapId;

    internal TimelineBridgeRequestHandler(
        ProjectStore store,
        ITimelineBridgeAuthorization authorization)
        : this(
            store,
            authorization,
            static () => DateTimeOffset.UtcNow,
            EntityId<CompositionVersionIdTag>.New,
            EntityId<TimelineMapIdTag>.New)
    {
    }

    internal TimelineBridgeRequestHandler(
        ProjectStore store,
        ITimelineBridgeAuthorization authorization,
        Func<DateTimeOffset> getUtcNow)
        : this(
            store,
            authorization,
            getUtcNow,
            EntityId<CompositionVersionIdTag>.New,
            EntityId<TimelineMapIdTag>.New)
    {
    }

    internal TimelineBridgeRequestHandler(
        ProjectStore store,
        ITimelineBridgeAuthorization authorization,
        Func<DateTimeOffset> getUtcNow,
        Func<EntityId<CompositionVersionIdTag>> createVersionId,
        Func<EntityId<TimelineMapIdTag>> createTimelineMapId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(getUtcNow);
        ArgumentNullException.ThrowIfNull(createVersionId);
        ArgumentNullException.ThrowIfNull(createTimelineMapId);
        this.store = store;
        versionService = new VersionService(store);
        this.authorization = authorization;
        this.getUtcNow = getUtcNow;
        this.createVersionId = createVersionId;
        this.createTimelineMapId = createTimelineMapId;
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.Method switch
            {
                BridgeMethod.TimelineRead => HandleRead(request),
                BridgeMethod.TimelineApply => HandleApply(request),
                _ => throw new ArgumentException(
                    "A timeline bridge handler requires a timeline request.",
                    nameof(request)),
            };
        }
        catch (AuthorizationException)
        {
            return TimelineBridgeJson.WriteResponse(
                request,
                TimelineBridgeStatus.Forbidden,
                snapshot: null,
                revision: null);
        }
        catch (Exception)
        {
            return TimelineBridgeJson.WriteResponse(
                request,
                TimelineBridgeStatus.Unavailable,
                snapshot: null,
                revision: null);
        }
    }

    private string HandleRead(BridgeRequest request)
    {
        authorization.DemandRead();
        EntityId<ProjectIdTag> projectId =
            TimelineBridgeProtocol.ReadProjectId(request);
        StoredWorkingCopy? stored = store.GetWorkingCopy(projectId);
        if (stored is null)
        {
            return TimelineBridgeJson.WriteResponse(
                request,
                TimelineBridgeStatus.NotFound,
                snapshot: null,
                revision: null);
        }

        WorkingCopy workingCopy = WorkingCopySnapshotCodec.Read(stored);
        return TimelineBridgeJson.WriteResponse(
            request,
            TimelineBridgeStatus.Available,
            workingCopy,
            stored.Revision);
    }

    private string HandleApply(BridgeRequest request)
    {
        string actorId = authorization.DemandEdit();
        EntityId<ProjectIdTag> projectId =
            TimelineBridgeProtocol.ReadProjectId(request);
        StoredWorkingCopy? stored = store.GetWorkingCopy(projectId);
        if (stored is null)
        {
            return TimelineBridgeJson.WriteResponse(
                request,
                TimelineBridgeStatus.NotFound,
                snapshot: null,
                revision: null);
        }

        int expectedRevision =
            TimelineBridgeProtocol.ReadExpectedRevision(request);
        if (stored.Revision != expectedRevision)
        {
            return TimelineBridgeJson.WriteResponse(
                request,
                TimelineBridgeStatus.Conflict,
                snapshot: null,
                revision: null);
        }

        WorkingCopy workingCopy = WorkingCopySnapshotCodec.Read(stored);
        DateTimeOffset updatedAt = getUtcNow();
        if (updatedAt < workingCopy.UpdatedAt)
        {
            updatedAt = workingCopy.UpdatedAt;
        }

        try
        {
            var reservedElementIds = new HashSet<
                EntityId<CompositionElementIdTag>>(
                workingCopy.Elements.Select(static element => element.Id));
            foreach (EditOperation operation in
                TimelineBridgeProtocol.ReadOperations(request))
            {
                if (operation.Kind == EditOperationKind.Split &&
                    (operation.RightElementId is not
                        EntityId<CompositionElementIdTag> rightElementId ||
                    !reservedElementIds.Add(rightElementId)))
                {
                    throw new ArgumentException(
                        "A split right element ID must be new within the edit batch.",
                        nameof(request));
                }

                CompositionEditor.Apply(workingCopy, operation, updatedAt);
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or
                OverflowException)
        {
            return TimelineBridgeJson.WriteResponse(
                request,
                TimelineBridgeStatus.InvalidOperation,
                snapshot: null,
                revision: null);
        }

        EntityId<CompositionVersionIdTag> versionId = createVersionId();
        EntityId<TimelineMapIdTag> timelineMapId = createTimelineMapId();
        CompositionVersion previewVersion = CreatePreviewVersion(
            workingCopy,
            versionId,
            actorId,
            updatedAt);
        TimelineMap timelineMap = TimelineMapBuilder.Build(
            timelineMapId,
            previewVersion);
        ValidateTimelineMap(timelineMap, workingCopy.Elements);
        var checkpointSnapshot = new WorkingCopy(
            workingCopy.Id,
            workingCopy.ProjectId,
            versionId,
            workingCopy.CreatedAt,
            updatedAt,
            workingCopy.Elements);
        int nextRevision = checked(expectedRevision + 1);
        string successResponse = TimelineBridgeJson.WriteResponse(
            request,
            TimelineBridgeStatus.Available,
            checkpointSnapshot,
            nextRevision);
        try
        {
            CheckpointResult checkpoint = versionService.Checkpoint(
                workingCopy,
                expectedRevision,
                versionId,
                actorId,
                updatedAt,
                ChangeReason);
            if (checkpoint.WorkingCopyRevision != nextRevision ||
                checkpoint.Version.Id != versionId ||
                checkpoint.WorkingCopy.BaseVersionId != versionId)
            {
                throw new InvalidOperationException(
                    "The timeline checkpoint result is inconsistent.");
            }
        }
        catch (StoreConcurrencyException)
        {
            return TimelineBridgeJson.WriteResponse(
                request,
                TimelineBridgeStatus.Conflict,
                snapshot: null,
                revision: null);
        }

        return successResponse;
    }

    private static CompositionVersion CreatePreviewVersion(
        WorkingCopy workingCopy,
        EntityId<CompositionVersionIdTag> versionId,
        string actorId,
        DateTimeOffset createdAt)
    {
        byte[] canonicalElements =
            CompositionSnapshotJson.WriteElementsObjectToUtf8(
                workingCopy.Elements);
        return new CompositionVersion(
            versionId,
            workingCopy.ProjectId,
            workingCopy.BaseVersionId,
            actorId,
            createdAt,
            ChangeReason,
            ContentDigest.Compute(canonicalElements),
            workingCopy.Elements);
    }

    private static void ValidateTimelineMap(
        TimelineMap timelineMap,
        IReadOnlyList<CompositionElement> elements)
    {
        if (timelineMap.Entries.Count != elements.Count)
        {
            throw new InvalidOperationException(
                "The timeline map does not cover the checkpoint elements.");
        }

        for (int index = 0; index < elements.Count; index++)
        {
            CompositionElement element = elements[index];
            TimelineMapEntry entry = timelineMap.Entries[index];
            if (entry.OutputRange != element.OutputRange ||
                entry.Origin != element.Origin ||
                entry.SourceRange != element.SourceRange)
            {
                throw new InvalidOperationException(
                    "The timeline map does not match the checkpoint elements.");
            }
        }
    }
}

internal static class TimelineBridgeJson
{
    internal static string WriteResponse(
        BridgeRequest request,
        TimelineBridgeStatus status,
        WorkingCopy? snapshot,
        int? revision)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((status == TimelineBridgeStatus.Available) !=
                (snapshot is not null && revision is >= 0) ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentException(
                "The timeline bridge result is inconsistent.",
                nameof(status));
        }

        return BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", FormatStatus(status));
                writer.WritePropertyName("snapshot");
                if (snapshot is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteSnapshot(writer, snapshot, revision!.Value);
                }

                writer.WriteEndObject();
            });
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        WorkingCopy snapshot,
        int revision)
    {
        writer.WriteStartObject();
        writer.WriteString("workingCopyId", snapshot.Id.ToString());
        writer.WriteString("projectId", snapshot.ProjectId.ToString());
        if (snapshot.BaseVersionId is null)
        {
            writer.WriteNull("baseVersionId");
        }
        else
        {
            writer.WriteString(
                "baseVersionId",
                snapshot.BaseVersionId.ToString());
        }

        writer.WriteNumber("revision", revision);
        writer.WriteString("createdAt", FormatTimestamp(snapshot.CreatedAt));
        writer.WriteString("updatedAt", FormatTimestamp(snapshot.UpdatedAt));
        writer.WriteStartArray("elements");
        foreach (CompositionElement element in snapshot.Elements)
        {
            WriteElement(writer, element);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteElement(
        Utf8JsonWriter writer,
        CompositionElement element)
    {
        writer.WriteStartObject();
        writer.WriteString("id", element.Id.ToString());
        writer.WritePropertyName("outputRange");
        WriteTimeRange(writer, element.OutputRange);
        writer.WritePropertyName("origin");
        WriteOrigin(writer, element.Origin);
        writer.WritePropertyName("sourceRange");
        if (element.SourceRange is TimeRange sourceRange)
        {
            WriteTimeRange(writer, sourceRange);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();
    }

    private static void WriteTimeRange(
        Utf8JsonWriter writer,
        TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteRationalTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteRationalTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "value",
            value.Value.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "rate",
            value.Rate.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteOrigin(
        Utf8JsonWriter writer,
        OriginRef origin)
    {
        writer.WriteStartObject();
        switch (origin.Kind)
        {
            case OriginKind.SourceSpan when origin.SourceSpanId is not null:
                writer.WriteString("kind", "sourceSpan");
                writer.WriteString(
                    "sourceSpanId",
                    origin.SourceSpanId.ToString());
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
                    "The timeline element origin is invalid.");
        }

        writer.WriteEndObject();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatStatus(TimelineBridgeStatus status) =>
        status switch
        {
            TimelineBridgeStatus.Available => "available",
            TimelineBridgeStatus.Forbidden => "forbidden",
            TimelineBridgeStatus.NotFound => "notFound",
            TimelineBridgeStatus.Conflict => "conflict",
            TimelineBridgeStatus.InvalidOperation => "invalidOperation",
            TimelineBridgeStatus.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown timeline bridge status."),
        };
}