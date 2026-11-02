using System.Globalization;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Security;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.App.Web;

internal enum ComparisonBridgeStatus
{
    Available,
    Forbidden,
    NotFound,
    StaleVersion,
    InvalidComparison,
    Unavailable,
}

internal interface IComparisonBridgeAuthorization
{
    void DemandRead();
}

internal sealed class ComparisonBridgeAuthorization :
    IComparisonBridgeAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal ComparisonBridgeAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(authorizationService, GetActorSidProvider(actorProvider))
    {
    }

    internal ComparisonBridgeAuthorization(
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

    private static Func<string> GetActorSidProvider(
        WindowsActorProvider actorProvider)
    {
        ArgumentNullException.ThrowIfNull(actorProvider);
        return actorProvider.GetCurrentSid;
    }
}

internal sealed class ComparisonBridgeRequestHandler
{
    private readonly Func<
        EntityId<CompositionVersionIdTag>,
        StoredCompositionVersion?> getCompositionVersion;
    private readonly IComparisonBridgeAuthorization authorization;

    internal ComparisonBridgeRequestHandler(
        ProjectStore store,
        IComparisonBridgeAuthorization authorization)
        : this(GetReader(store), authorization)
    {
    }

    internal ComparisonBridgeRequestHandler(
        Func<EntityId<CompositionVersionIdTag>, StoredCompositionVersion?>
            getCompositionVersion,
        IComparisonBridgeAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(getCompositionVersion);
        ArgumentNullException.ThrowIfNull(authorization);
        this.getCompositionVersion = getCompositionVersion;
        this.authorization = authorization;
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method != BridgeMethod.ComparisonRead)
        {
            throw new ArgumentException(
                "A comparison bridge handler requires a comparison request.",
                nameof(request));
        }

        try
        {
            authorization.DemandRead();

            EntityId<CompositionVersionIdTag> leftVersionId =
                ReadVersionId(request, "leftVersionId");
            EntityId<CompositionVersionIdTag> rightVersionId =
                ReadVersionId(request, "rightVersionId");
            ContentDigest leftVersionDigest =
                ReadDigest(request, "leftVersionDigest");
            ContentDigest rightVersionDigest =
                ReadDigest(request, "rightVersionDigest");

            StoredCompositionVersion? storedLeft =
                getCompositionVersion(leftVersionId);
            StoredCompositionVersion? storedRight = leftVersionId == rightVersionId
                ? storedLeft
                : getCompositionVersion(rightVersionId);
            if (storedLeft is null || storedRight is null)
            {
                return ComparisonBridgeJson.WriteResponse(
                    request,
                    ComparisonBridgeStatus.NotFound);
            }

            CompositionVersion left = ReadAndValidate(storedLeft);
            CompositionVersion right = ReadAndValidate(storedRight);
            if (left.ContentDigest != leftVersionDigest ||
                right.ContentDigest != rightVersionDigest)
            {
                return ComparisonBridgeJson.WriteResponse(
                    request,
                    ComparisonBridgeStatus.StaleVersion);
            }

            if (left.ProjectId != right.ProjectId)
            {
                return ComparisonBridgeJson.WriteResponse(
                    request,
                    ComparisonBridgeStatus.InvalidComparison);
            }

            CompositionDiff diff = VersionService.Compare(left, right);
            return ComparisonBridgeJson.WriteResponse(
                request,
                ComparisonBridgeStatus.Available,
                left,
                right,
                diff);
        }
        catch (AuthorizationException)
        {
            return ComparisonBridgeJson.WriteResponse(
                request,
                ComparisonBridgeStatus.Forbidden);
        }
        catch (UnauthorizedAccessException)
        {
            return ComparisonBridgeJson.WriteResponse(
                request,
                ComparisonBridgeStatus.Forbidden);
        }
        catch (Exception)
        {
            return ComparisonBridgeJson.WriteResponse(
                request,
                ComparisonBridgeStatus.Unavailable);
        }
    }

    private static Func<
        EntityId<CompositionVersionIdTag>,
        StoredCompositionVersion?> GetReader(ProjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.GetCompositionVersion;
    }

    private static EntityId<CompositionVersionIdTag> ReadVersionId(
        BridgeRequest request,
        string propertyName) =>
        EntityId<CompositionVersionIdTag>.Parse(
            request.Parameters.GetProperty(propertyName).GetString()!);

    private static ContentDigest ReadDigest(
        BridgeRequest request,
        string propertyName) =>
        ContentDigest.Parse(
            request.Parameters.GetProperty(propertyName).GetString()!);

    private static CompositionVersion ReadAndValidate(
        StoredCompositionVersion stored)
    {
        CompositionVersion version = CompositionSnapshotJson.ReadVersion(
            stored.CanonicalJson);
        if (stored.Id != version.Id ||
            stored.ProjectId != version.ProjectId ||
            stored.ParentVersionId != version.ParentVersionId ||
            stored.ContentDigest != version.ContentDigest ||
            !stored.CreatedAt.EqualsExact(version.CreatedAt))
        {
            throw new FormatException(
                "The stored comparison version does not match its envelope.");
        }

        return version;
    }
}

internal static class ComparisonBridgeJson
{
    private static readonly string[] UnavailableFeatureNames =
    [
        "subtitleStyle",
        "telopCount",
        "effects",
        "callToAction",
        "destination",
        "warnings",
    ];

    internal static string WriteResponse(
        BridgeRequest request,
        ComparisonBridgeStatus status,
        CompositionVersion? left = null,
        CompositionVersion? right = null,
        CompositionDiff? diff = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool hasComparison = left is not null && right is not null && diff is not null;
        if (!Enum.IsDefined(status) ||
            (status == ComparisonBridgeStatus.Available) != hasComparison ||
            hasComparison &&
                (left!.ProjectId != right!.ProjectId ||
                    diff!.FromVersionId != left.Id ||
                    diff.ToVersionId != right.Id))
        {
            throw new ArgumentException(
                "The comparison bridge result is inconsistent.",
                nameof(status));
        }

        return BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", FormatStatus(status));
                writer.WritePropertyName("comparison");
                if (!hasComparison)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteComparison(writer, left!, right!, diff!);
                }

                writer.WriteEndObject();
            });
    }

    private static void WriteComparison(
        Utf8JsonWriter writer,
        CompositionVersion left,
        CompositionVersion right,
        CompositionDiff diff)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("left");
        WriteVersion(writer, left);
        writer.WritePropertyName("right");
        WriteVersion(writer, right);
        WriteElementChanges(writer, diff.Changes);
        WriteAdoptionChanges(writer, diff.Changes);
        writer.WriteEndObject();
    }

    private static void WriteVersion(
        Utf8JsonWriter writer,
        CompositionVersion version)
    {
        writer.WriteStartObject();
        writer.WriteString("versionId", version.Id.ToString());
        writer.WriteString("versionDigest", version.ContentDigest.Hex);
        writer.WriteString("createdAt", FormatTimestamp(version.CreatedAt));
        writer.WriteString("changeReason", version.ChangeReason);
        WriteRationalTime(writer, "playbackDuration", GetPlaybackDuration(version));
        writer.WriteStartArray("usedSourceRanges");
        foreach (CompositionElement element in version.Elements)
        {
            if (element.Origin.Kind != OriginKind.SourceSpan ||
                element.Origin.SourceSpanId is not EntityId<SourceSpanIdTag>
                    sourceSpanId ||
                element.SourceRange is not TimeRange sourceRange)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("elementId", element.Id.ToString());
            writer.WriteString("sourceSpanId", sourceSpanId.ToString());
            WriteTimeRange(writer, "sourceRange", sourceRange);
            WriteTimeRange(writer, "outputRange", element.OutputRange);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("features");
        foreach (string featureName in UnavailableFeatureNames)
        {
            writer.WriteString(featureName, "unavailable");
        }

        WriteDerivedFeatures(writer, version);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // FR-028 AC-076: 版が実際に持つ事実から導ける項目（冒頭フック・トランジション）を
    // 記録済みとして出し、版に記録のない項目だけを "unavailable" のままにする。
    private static void WriteDerivedFeatures(
        Utf8JsonWriter writer,
        CompositionVersion version)
    {
        CompositionElement[] ordered = version.Elements
            .OrderBy(static element => element.OutputRange.Start)
            .ToArray();

        if (ordered.Length == 0)
        {
            writer.WriteString("openingHook", "unavailable");
            writer.WriteString("transitions", "unavailable");
            return;
        }

        CompositionElement first = ordered[0];
        writer.WriteStartObject("openingHook");
        writer.WriteString("status", "recorded");
        writer.WriteString("elementId", first.Id.ToString());
        writer.WriteString("origin", FormatOrigin(first.Origin.Kind));
        WriteRationalTime(writer, "outputStart", first.OutputRange.Start);
        WriteRationalTime(writer, "outputDuration", first.OutputRange.Duration);
        writer.WriteBoolean("adopted", first.AdoptedSuggestion is not null);
        writer.WriteEndObject();

        writer.WriteStartObject("transitions");
        writer.WriteString("status", "recorded");
        writer.WriteNumber("cutCount", ordered.Length - 1);
        writer.WriteEndObject();
    }

    private static string FormatOrigin(OriginKind kind) =>
        kind switch
        {
            OriginKind.SourceSpan => "sourceSpan",
            OriginKind.Asset => "asset",
            OriginKind.EditorialRecord => "editorialRecord",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static RationalTime GetPlaybackDuration(
        CompositionVersion version)
    {
        RationalTime duration = default;
        foreach (CompositionElement element in version.Elements)
        {
            RationalTime end = element.OutputRange.End;
            if (end > duration)
            {
                duration = end;
            }
        }

        return duration;
    }

    private static void WriteElementChanges(
        Utf8JsonWriter writer,
        IReadOnlyList<CompositionChange> changes)
    {
        writer.WriteStartArray("elementChanges");
        foreach (CompositionChange change in changes)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", FormatChangeKind(change.Kind));
            writer.WriteString("elementId", change.ElementId.ToString());
            WriteNullableNumber(writer, "previousIndex", change.PreviousIndex);
            WriteNullableNumber(writer, "currentIndex", change.CurrentIndex);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAdoptionChanges(
        Utf8JsonWriter writer,
        IReadOnlyList<CompositionChange> changes)
    {
        writer.WriteStartArray("adoptionChanges");
        foreach (CompositionChange change in changes)
        {
            SuggestionReference? previous =
                change.PreviousElement?.AdoptedSuggestion;
            SuggestionReference? current =
                change.CurrentElement?.AdoptedSuggestion;
            if (SuggestionIdsEqual(previous, current))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString(
                "kind",
                previous is null
                    ? "adopted"
                    : current is null
                        ? "removed"
                        : "replaced");
            writer.WriteString("elementId", change.ElementId.ToString());
            WriteNullableString(
                writer,
                "previousSuggestionId",
                previous?.SuggestionId.ToString());
            WriteNullableString(
                writer,
                "currentSuggestionId",
                current?.SuggestionId.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static bool SuggestionIdsEqual(
        SuggestionReference? left,
        SuggestionReference? right) =>
        ReferenceEquals(left, right) ||
        left is not null &&
            right is not null &&
            left.SuggestionId == right.SuggestionId;

    private static void WriteTimeRange(
        Utf8JsonWriter writer,
        string propertyName,
        TimeRange range)
    {
        writer.WriteStartObject(propertyName);
        WriteRationalTime(writer, "start", range.Start);
        WriteRationalTime(writer, "duration", range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        string propertyName,
        RationalTime value)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString(
            "value",
            value.Value.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "rate",
            value.Rate.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatChangeKind(CompositionChangeKind kind) =>
        kind switch
        {
            CompositionChangeKind.Added => "added",
            CompositionChangeKind.Removed => "removed",
            CompositionChangeKind.Moved => "moved",
            CompositionChangeKind.Modified => "modified",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown composition change kind."),
        };

    private static string FormatStatus(ComparisonBridgeStatus status) =>
        status switch
        {
            ComparisonBridgeStatus.Available => "available",
            ComparisonBridgeStatus.Forbidden => "forbidden",
            ComparisonBridgeStatus.NotFound => "notFound",
            ComparisonBridgeStatus.StaleVersion => "staleVersion",
            ComparisonBridgeStatus.InvalidComparison => "invalidComparison",
            ComparisonBridgeStatus.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown comparison bridge status."),
        };
}