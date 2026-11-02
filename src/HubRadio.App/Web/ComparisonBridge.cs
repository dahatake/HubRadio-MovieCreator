using System.Globalization;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Security;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
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
    private readonly Func<EntityId<ProjectIdTag>, PublicationScope?>?
        getPublicationScope;

    internal ComparisonBridgeRequestHandler(
        ProjectStore store,
        IComparisonBridgeAuthorization authorization)
        : this(
            GetReader(store),
            authorization,
            projectId => store.GetProject(projectId)?.PublicationScope)
    {
    }

    internal ComparisonBridgeRequestHandler(
        Func<EntityId<CompositionVersionIdTag>, StoredCompositionVersion?>
            getCompositionVersion,
        IComparisonBridgeAuthorization authorization,
        Func<EntityId<ProjectIdTag>, PublicationScope?>? getPublicationScope = null)
    {
        ArgumentNullException.ThrowIfNull(getCompositionVersion);
        ArgumentNullException.ThrowIfNull(authorization);
        this.getCompositionVersion = getCompositionVersion;
        this.authorization = authorization;
        this.getPublicationScope = getPublicationScope;
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
            PublicationScope? scope = getPublicationScope?.Invoke(left.ProjectId);
            return ComparisonBridgeJson.WriteResponse(
                request,
                ComparisonBridgeStatus.Available,
                left,
                right,
                diff,
                scope);
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
    internal static string WriteResponse(
        BridgeRequest request,
        ComparisonBridgeStatus status,
        CompositionVersion? left = null,
        CompositionVersion? right = null,
        CompositionDiff? diff = null,
        PublicationScope? publicationScope = null)
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
                    WriteComparison(writer, left!, right!, diff!, publicationScope);
                }

                writer.WriteEndObject();
            });
    }

    private static void WriteComparison(
        Utf8JsonWriter writer,
        CompositionVersion left,
        CompositionVersion right,
        CompositionDiff diff,
        PublicationScope? publicationScope)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("left");
        WriteVersion(writer, left, publicationScope);
        writer.WritePropertyName("right");
        WriteVersion(writer, right, publicationScope);
        WriteElementChanges(writer, diff.Changes);
        WriteAdoptionChanges(writer, diff.Changes);
        writer.WriteEndObject();
    }

    private static void WriteVersion(
        Utf8JsonWriter writer,
        CompositionVersion version,
        PublicationScope? publicationScope)
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
        WriteDerivedFeatures(writer, version, publicationScope);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // FR-028 AC-076: 版が実際に持つ事実から導ける項目（冒頭フック・トランジション）を
    // 記録済みとして出し、版に記録のない項目だけを "unavailable" のままにする。
    private static void WriteDerivedFeatures(
        Utf8JsonWriter writer,
        CompositionVersion version,
        PublicationScope? publicationScope)
    {
        CompositionElement[] ordered = version.Elements
            .OrderBy(static element => element.OutputRange.Start)
            .ToArray();

        if (ordered.Length == 0)
        {
            foreach (string name in new[]
            {
                "openingHook", "subtitleStyle", "telopCount", "transitions",
                "effects", "callToAction",
            })
            {
                writer.WriteString(name, "unavailable");
            }

            WriteDestination(writer, publicationScope);
            writer.WriteString("warnings", "unavailable");
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

        WriteDerivedSummaries(writer, ordered, publicationScope);
    }

    // FR-028 AC-076: 字幕スタイル・テロップ・効果・CTA・想定公開先・要確認事項を、
    // 版の要素の出自・採用提案・終端要素と、プロジェクトの公開範囲から導く。
    private static void WriteDerivedSummaries(
        Utf8JsonWriter writer,
        CompositionElement[] ordered,
        PublicationScope? publicationScope)
    {
        int subtitled = ordered.Count(static e => e.Origin.Kind == OriginKind.SourceSpan);
        int telops = ordered.Count(static e => e.Origin.Kind == OriginKind.EditorialRecord);
        int assets = ordered.Count(static e => e.Origin.Kind == OriginKind.Asset);
        int adopted = ordered.Count(static e => e.AdoptedSuggestion is not null);
        CompositionElement last = ordered[^1];

        WriteSummary(
            writer,
            "subtitleStyle",
            subtitled == 0
                ? "字幕の対象区間なし"
                : $"字幕の対象 {Num(subtitled)} 区間（既定の字幕設定）");
        WriteSummary(
            writer,
            "telopCount",
            $"テロップ候補 {Num(telops)} 件（編集記録由来の要素）");
        WriteSummary(
            writer,
            "effects",
            $"提案の採用 {Num(adopted)} 件、カット {Num(ordered.Length - 1)} 箇所");
        WriteSummary(
            writer,
            "callToAction",
            last.Origin.Kind == OriginKind.SourceSpan
                ? "終端は元動画の区間（CTA の記録なし）"
                : $"終端に{(last.Origin.Kind == OriginKind.Asset ? "素材" : "編集記録")}の要素あり（CTA の候補）");
        WriteDestination(writer, publicationScope);

        var warnings = new List<string>();
        if (assets > 0)
        {
            warnings.Add($"素材 {Num(assets)} 件の権利確認");
        }

        if (adopted > 0)
        {
            warnings.Add($"採用した提案 {Num(adopted)} 件の人による確認");
        }

        if (publicationScope is PublicationScope s &&
            s is not (PublicationScope.InternalOnly or PublicationScope.ExternalPublicationApproved))
        {
            warnings.Add("公開先が未承認または未決定");
        }

        WriteSummary(
            writer,
            "warnings",
            warnings.Count == 0 ? "要確認事項なし" : string.Join("、", warnings));
    }

    private static string Num(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static void WriteDestination(
        Utf8JsonWriter writer,
        PublicationScope? publicationScope)
    {
        if (publicationScope is PublicationScope scope)
        {
            WriteSummary(writer, "destination", FormatScope(scope));
        }
        else
        {
            writer.WriteString("destination", "unavailable");
        }
    }

    private static void WriteSummary(
        Utf8JsonWriter writer,
        string name,
        string summary)
    {
        writer.WriteStartObject(name);
        writer.WriteString("status", "recorded");
        writer.WriteString("summary", summary);
        writer.WriteEndObject();
    }

    private static string FormatScope(PublicationScope scope) =>
        scope switch
        {
            PublicationScope.InternalOnly => "社内限定",
            PublicationScope.SpecificGroupOnly => "特定グループ限定",
            PublicationScope.ExternalCandidate => "社外公開の候補（未承認）",
            PublicationScope.ExternalPublicationApproved => "社外公開（承認済み）",
            PublicationScope.PublicationForbidden => "公開禁止",
            PublicationScope.PendingDecision => "公開範囲は未決定",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

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