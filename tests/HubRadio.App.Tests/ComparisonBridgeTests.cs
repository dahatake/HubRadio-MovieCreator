using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Web;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ComparisonBridgeTests
{
    private const string RequestId =
        "00112233-4455-6677-8899-aabbccddeeff";
    private const string PrivateActorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-7701";
    private static readonly DateTimeOffset CreatedAt =
        new(2036, 8, 9, 10, 11, 12, TimeSpan.Zero);

    [TestMethod]
    public void ProtocolAcceptsOnlyExactDigestBoundComparisonRequests()
    {
        EntityId<CompositionVersionIdTag> leftId =
            EntityId<CompositionVersionIdTag>.New();
        EntityId<CompositionVersionIdTag> rightId =
            EntityId<CompositionVersionIdTag>.New();
        string digest = new('a', 64);
        string valid = CreateRequest(leftId, digest, rightId, digest);

        Assert.IsTrue(
            Encoding.UTF8.GetByteCount(valid) > BridgeProtocol.MaxRequestUtf8Bytes);
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            valid,
            out BridgeRequest? parsed,
            out BridgeRequestRejection accepted),
            accepted.ToString());
        Assert.AreEqual(BridgeMethod.ComparisonRead, parsed!.Method);
        Assert.AreEqual(
            BridgeProtocol.MaxComparisonResponseUtf8Bytes,
            BridgeProtocol.GetMaximumResponseUtf8Bytes(parsed.Method));

        string exactLimit = new string(
            ' ',
            BridgeProtocol.MaxComparisonRequestUtf8Bytes -
                Encoding.UTF8.GetByteCount(valid)) + valid;
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit,
            out _,
            out BridgeRequestRejection exactAccepted),
            exactAccepted.ToString());
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit + " ",
            out _,
            out BridgeRequestRejection oversized));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, oversized);

        string[] invalid =
        [
            CreateRequest(
                leftId,
                digest,
                rightId,
                digest,
                "\"path\":\"C:\\\\private.mov\","),
            CreateRequestText(
                "A1112222-3333-4444-8555-666677778888",
                digest,
                rightId.ToString(),
                digest),
            CreateRequestText(
                leftId.ToString(),
                digest.ToUpperInvariant(),
                rightId.ToString(),
                digest),
            CreateRequestText(
                leftId.ToString(),
                digest[..63],
                rightId.ToString(),
                digest),
        ];

        foreach (string message in invalid)
        {
            Assert.IsFalse(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                message,
                out BridgeRequest? rejected,
                out BridgeRequestRejection rejection));
            Assert.IsNull(rejected);
            Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
        }
    }

    [TestMethod]
    public void ReadAuthorizesBeforeVersionLookupAndReturnsNoPrivateDetail()
    {
        int lookupCount = 0;
        var authorization = new RecordingAuthorization(deny: true);
        var handler = new ComparisonBridgeRequestHandler(
            _ =>
            {
                lookupCount++;
                throw new InvalidOperationException("C:\\private\\versions.db");
            },
            authorization);
        EntityId<CompositionVersionIdTag> leftId =
            EntityId<CompositionVersionIdTag>.New();
        EntityId<CompositionVersionIdTag> rightId =
            EntityId<CompositionVersionIdTag>.New();

        string response = handler.Handle(ParseRequest(
            leftId,
            new string('a', 64),
            rightId,
            new string('b', 64)));

        Assert.AreEqual("forbidden", ReadStatus(response));
        Assert.AreEqual(1, authorization.DemandCount);
        Assert.AreEqual(0, lookupCount);
        AssertPrivateDetailAbsent(response);
    }

    [TestMethod]
    public void ReadConvertsPrivateLookupFailureToClosedUnavailable()
    {
        int lookupCount = 0;
        var authorization = new RecordingAuthorization();
        var handler = new ComparisonBridgeRequestHandler(
            _ =>
            {
                lookupCount++;
                throw new InvalidOperationException(
                    "C:\\private\\versions.db for S-1-5-21-private");
            },
            authorization);

        string response = handler.Handle(ParseRequest(
            EntityId<CompositionVersionIdTag>.New(),
            new string('a', 64),
            EntityId<CompositionVersionIdTag>.New(),
            new string('b', 64)));

        AssertClosedFailure(response, "unavailable");
        Assert.AreEqual(1, authorization.DemandCount);
        Assert.AreEqual(1, lookupCount);
    }

    [TestMethod]
    public void ReadConvertsOversizedAvailableResponseToClosedUnavailable()
    {
        CompositionVersion version = CreateVersion(
            EntityId<CompositionVersionIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            parentVersionId: null,
            new string('x', BridgeProtocol.MaxComparisonResponseUtf8Bytes),
            [],
            CreatedAt);
        StoredCompositionVersion stored = Store(version);
        int lookupCount = 0;
        var handler = new ComparisonBridgeRequestHandler(
            _ =>
            {
                lookupCount++;
                return stored;
            },
            new RecordingAuthorization());

        string response = handler.Handle(ParseRequest(
            version.Id,
            version.ContentDigest.Hex,
            version.Id,
            version.ContentDigest.Hex));

        AssertClosedFailure(response, "unavailable");
        Assert.IsTrue(
            Encoding.UTF8.GetByteCount(response) <=
                BridgeProtocol.MaxComparisonResponseUtf8Bytes);
        Assert.AreEqual(1, lookupCount);
    }

    [TestMethod]
    // FR-027 AC-075: 版どうしの適用前後の差分（変更・採用の変更）が表示用に射影される。
    public void ReadProjectsExactEvidenceOrderedChangesAndAdoptionChanges()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<CompositionVersionIdTag> leftId =
            EntityId<CompositionVersionIdTag>.New();
        EntityId<CompositionVersionIdTag> rightId =
            EntityId<CompositionVersionIdTag>.New();
        EntityId<CompositionElementIdTag> sourceElementId =
            EntityId<CompositionElementIdTag>.New();
        EntityId<CompositionElementIdTag> movedElementId =
            EntityId<CompositionElementIdTag>.New();
        EntityId<CompositionElementIdTag> removedElementId =
            EntityId<CompositionElementIdTag>.New();
        EntityId<CompositionElementIdTag> addedElementId =
            EntityId<CompositionElementIdTag>.New();
        EntityId<SourceSpanIdTag> sourceSpanId =
            EntityId<SourceSpanIdTag>.New();
        OriginRef sourceOrigin = OriginRef.FromSourceSpan(sourceSpanId);
        SuggestionReference previousSuggestion =
            SuggestionReference.FromPersistedReference(
                EntityId<SuggestionIdTag>.New(),
                sourceOrigin);
        SuggestionReference currentSuggestion =
            SuggestionReference.FromPersistedReference(
                EntityId<SuggestionIdTag>.New(),
                sourceOrigin);
        OriginRef addedOrigin = OriginRef.FromSourceSpan(
            EntityId<SourceSpanIdTag>.New());
        SuggestionReference addedSuggestion =
            SuggestionReference.FromPersistedReference(
                EntityId<SuggestionIdTag>.New(),
                addedOrigin);

        CompositionElement leftSource = new(
            sourceElementId,
            new TimeRange(default, new RationalTime(2, 1)),
            sourceOrigin,
            previousSuggestion,
            new TimeRange(new RationalTime(21, 2), new RationalTime(2, 1)));
        CompositionElement moved = new(
            movedElementId,
            new TimeRange(new RationalTime(2, 1), new RationalTime(1, 1)),
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()));
        CompositionElement removed = new(
            removedElementId,
            new TimeRange(new RationalTime(3, 1), new RationalTime(3, 2)),
            OriginRef.FromEditorialRecord(
                EntityId<EditorialRecordIdTag>.New()));
        CompositionElement rightSource = new(
            sourceElementId,
            leftSource.OutputRange,
            sourceOrigin,
            currentSuggestion,
            leftSource.SourceRange);
        CompositionElement added = new(
            addedElementId,
            new TimeRange(new RationalTime(3, 1), new RationalTime(4, 3)),
            addedOrigin,
            addedSuggestion,
            new TimeRange(new RationalTime(20, 1), new RationalTime(4, 3)));

        CompositionVersion left = CreateVersion(
            leftId,
            projectId,
            parentVersionId: null,
            "left version",
            [leftSource, moved, removed],
            CreatedAt);
        const string HostileReason =
            "<img src=x onerror=alert('comparison')> & exact reason";
        CompositionVersion right = CreateVersion(
            rightId,
            projectId,
            left.Id,
            HostileReason,
            [moved, rightSource, added],
            CreatedAt.AddMinutes(1));
        var versions = new Dictionary<
            EntityId<CompositionVersionIdTag>,
            StoredCompositionVersion>
        {
            [left.Id] = Store(left),
            [right.Id] = Store(right),
        };
        var authorization = new RecordingAuthorization();
        var handler = new ComparisonBridgeRequestHandler(
            id => versions.GetValueOrDefault(id),
            authorization);

        string responseJson = handler.Handle(ParseRequest(
            left.Id,
            left.ContentDigest.Hex,
            right.Id,
            right.ContentDigest.Hex));

        using JsonDocument response = JsonDocument.Parse(responseJson);
        JsonElement root = response.RootElement;
        AssertExactProperties(root, "version", "requestId", "method", "result");
        Assert.AreEqual(
            BridgeProtocol.ComparisonReadMethod,
            root.GetProperty("method").GetString());
        JsonElement result = root.GetProperty("result");
        AssertExactProperties(result, "status", "comparison");
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        JsonElement comparison = result.GetProperty("comparison");
        AssertExactProperties(
            comparison,
            "left",
            "right",
            "elementChanges",
            "adoptionChanges");

        JsonElement leftSnapshot = comparison.GetProperty("left");
        JsonElement rightSnapshot = comparison.GetProperty("right");
        AssertVersionSnapshot(leftSnapshot, left);
        AssertVersionSnapshot(rightSnapshot, right);
        Assert.AreEqual(
            HostileReason,
            rightSnapshot.GetProperty("changeReason").GetString());
        AssertRationalTime(
            leftSnapshot.GetProperty("playbackDuration"),
            "9",
            "2");
        AssertRationalTime(
            rightSnapshot.GetProperty("playbackDuration"),
            "13",
            "3");

        JsonElement sourceRange = leftSnapshot
            .GetProperty("usedSourceRanges")[0];
        AssertExactProperties(
            sourceRange,
            "elementId",
            "sourceSpanId",
            "sourceRange",
            "outputRange");
        Assert.AreEqual(sourceElementId.ToString(), sourceRange
            .GetProperty("elementId").GetString());
        Assert.AreEqual(sourceSpanId.ToString(), sourceRange
            .GetProperty("sourceSpanId").GetString());
        AssertRationalTime(
            sourceRange.GetProperty("sourceRange").GetProperty("start"),
            "21",
            "2");

        JsonElement[] changes = comparison.GetProperty("elementChanges")
            .EnumerateArray()
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "removed", "added", "moved", "modified" },
            changes.Select(static item => item.GetProperty("kind").GetString())
                .ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                removedElementId.ToString(),
                addedElementId.ToString(),
                movedElementId.ToString(),
                sourceElementId.ToString(),
            },
            changes.Select(static item =>
                item.GetProperty("elementId").GetString()).ToArray());

        JsonElement[] adoptions = comparison.GetProperty("adoptionChanges")
            .EnumerateArray()
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "adopted", "replaced" },
            adoptions.Select(static item => item.GetProperty("kind").GetString())
                .ToArray());
        Assert.AreEqual(
            addedSuggestion.SuggestionId.ToString(),
            adoptions[0].GetProperty("currentSuggestionId").GetString());
        Assert.AreEqual(
            previousSuggestion.SuggestionId.ToString(),
            adoptions[1].GetProperty("previousSuggestionId").GetString());
        Assert.AreEqual(
            currentSuggestion.SuggestionId.ToString(),
            adoptions[1].GetProperty("currentSuggestionId").GetString());

        Assert.AreEqual(1, authorization.DemandCount);
        Assert.IsFalse(responseJson.Contains(
            PrivateActorSid,
            StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains(
            "createdByActorId",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains(
            "actorSid",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains(
            "projectId",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains(
            "command",
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SameVersionIsAvailableWithoutInventedChanges()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        CompositionVersion version = CreateVersion(
            EntityId<CompositionVersionIdTag>.New(),
            projectId,
            parentVersionId: null,
            "same version",
            [],
            CreatedAt);
        StoredCompositionVersion stored = Store(version);
        int lookupCount = 0;
        var handler = new ComparisonBridgeRequestHandler(
            _ =>
            {
                lookupCount++;
                return stored;
            },
            new RecordingAuthorization());

        string responseJson = handler.Handle(ParseRequest(
            version.Id,
            version.ContentDigest.Hex,
            version.Id,
            version.ContentDigest.Hex));

        using JsonDocument response = JsonDocument.Parse(responseJson);
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        JsonElement comparison = result.GetProperty("comparison");
        Assert.AreEqual(0, comparison.GetProperty("elementChanges").GetArrayLength());
        Assert.AreEqual(0, comparison.GetProperty("adoptionChanges").GetArrayLength());
        AssertRationalTime(
            comparison.GetProperty("left").GetProperty("playbackDuration"),
            "0",
            "1");
        Assert.AreEqual(1, lookupCount);
    }

    [TestMethod]
    public void AdoptionChangesUseSuggestionIdentityAndRepresentRemoval()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<CompositionVersionIdTag> leftId =
            EntityId<CompositionVersionIdTag>.New();
        EntityId<CompositionVersionIdTag> rightId =
            EntityId<CompositionVersionIdTag>.New();
        EntityId<SuggestionIdTag> retainedSuggestionId =
            EntityId<SuggestionIdTag>.New();
        EntityId<CompositionElementIdTag> provenanceElementId =
            EntityId<CompositionElementIdTag>.New();
        EntityId<CompositionElementIdTag> removalElementId =
            EntityId<CompositionElementIdTag>.New();
        OriginRef leftOrigin = OriginRef.FromSourceSpan(
            EntityId<SourceSpanIdTag>.New());
        OriginRef rightOrigin = OriginRef.FromSourceSpan(
            EntityId<SourceSpanIdTag>.New());
        OriginRef removalOrigin = OriginRef.FromSourceSpan(
            EntityId<SourceSpanIdTag>.New());
        TimeRange oneSecond = new(default, new RationalTime(1, 1));
        TimeRange secondOutput = new(
            new RationalTime(1, 1),
            new RationalTime(1, 1));
        SuggestionReference leftRetained =
            SuggestionReference.FromPersistedReference(
                retainedSuggestionId,
                leftOrigin);
        SuggestionReference rightRetained =
            SuggestionReference.FromPersistedReference(
                retainedSuggestionId,
                rightOrigin);
        SuggestionReference removed =
            SuggestionReference.FromPersistedReference(
                EntityId<SuggestionIdTag>.New(),
                removalOrigin);

        CompositionVersion left = CreateVersion(
            leftId,
            projectId,
            parentVersionId: null,
            "left adoption",
            [
                new CompositionElement(
                    provenanceElementId,
                    oneSecond,
                    leftOrigin,
                    leftRetained,
                    oneSecond),
                new CompositionElement(
                    removalElementId,
                    secondOutput,
                    removalOrigin,
                    removed,
                    oneSecond),
            ],
            CreatedAt);
        CompositionVersion right = CreateVersion(
            rightId,
            projectId,
            leftId,
            "right adoption",
            [
                new CompositionElement(
                    provenanceElementId,
                    oneSecond,
                    rightOrigin,
                    rightRetained,
                    oneSecond),
                new CompositionElement(
                    removalElementId,
                    secondOutput,
                    removalOrigin,
                    adoptedSuggestion: null,
                    oneSecond),
            ],
            CreatedAt.AddMinutes(1));
        var versions = new Dictionary<
            EntityId<CompositionVersionIdTag>,
            StoredCompositionVersion>
        {
            [left.Id] = Store(left),
            [right.Id] = Store(right),
        };
        var handler = new ComparisonBridgeRequestHandler(
            id => versions.GetValueOrDefault(id),
            new RecordingAuthorization());

        string responseJson = handler.Handle(ParseRequest(
            left.Id,
            left.ContentDigest.Hex,
            right.Id,
            right.ContentDigest.Hex));

        using JsonDocument response = JsonDocument.Parse(responseJson);
        JsonElement comparison = response.RootElement.GetProperty("result")
            .GetProperty("comparison");
        JsonElement[] elementChanges = comparison.GetProperty("elementChanges")
            .EnumerateArray()
            .ToArray();
        Assert.AreEqual(2, elementChanges.Length);
        Assert.IsTrue(elementChanges.All(static change =>
            change.GetProperty("kind").GetString() == "modified"));
        JsonElement[] adoptionChanges = comparison.GetProperty("adoptionChanges")
            .EnumerateArray()
            .ToArray();
        Assert.AreEqual(1, adoptionChanges.Length);
        Assert.AreEqual(
            "removed",
            adoptionChanges[0].GetProperty("kind").GetString());
        Assert.AreEqual(
            removalElementId.ToString(),
            adoptionChanges[0].GetProperty("elementId").GetString());
        Assert.AreEqual(
            removed.SuggestionId.ToString(),
            adoptionChanges[0].GetProperty("previousSuggestionId").GetString());
        Assert.AreEqual(
            JsonValueKind.Null,
            adoptionChanges[0].GetProperty("currentSuggestionId").ValueKind);
    }

    [TestMethod]
    public void ReadReturnsClosedStatusesForStaleMissingAndDifferentProjects()
    {
        CompositionVersion left = CreateVersion(
            EntityId<CompositionVersionIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            parentVersionId: null,
            "left",
            [],
            CreatedAt);
        CompositionVersion right = CreateVersion(
            EntityId<CompositionVersionIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            parentVersionId: null,
            "right",
            [],
            CreatedAt);
        var versions = new Dictionary<
            EntityId<CompositionVersionIdTag>,
            StoredCompositionVersion>
        {
            [left.Id] = Store(left),
            [right.Id] = Store(right),
        };
        var handler = new ComparisonBridgeRequestHandler(
            id => versions.GetValueOrDefault(id),
            new RecordingAuthorization());

        AssertClosedFailure(
            handler.Handle(ParseRequest(
                left.Id,
                new string('f', 64),
                right.Id,
                right.ContentDigest.Hex)),
            "staleVersion");
        AssertClosedFailure(
            handler.Handle(ParseRequest(
                left.Id,
                left.ContentDigest.Hex,
                right.Id,
                right.ContentDigest.Hex)),
            "invalidComparison");
        AssertClosedFailure(
            handler.Handle(ParseRequest(
                left.Id,
                left.ContentDigest.Hex,
                EntityId<CompositionVersionIdTag>.New(),
                right.ContentDigest.Hex)),
            "notFound");
    }

    private static void AssertVersionSnapshot(
        JsonElement snapshot,
        CompositionVersion version)
    {
        AssertExactProperties(
            snapshot,
            "versionId",
            "versionDigest",
            "createdAt",
            "changeReason",
            "playbackDuration",
            "usedSourceRanges",
            "features");
        Assert.AreEqual(
            version.Id.ToString(),
            snapshot.GetProperty("versionId").GetString());
        Assert.AreEqual(
            version.ContentDigest.Hex,
            snapshot.GetProperty("versionDigest").GetString());
        Assert.AreEqual(
            version.CreatedAt.ToString("O"),
            snapshot.GetProperty("createdAt").GetString());
        JsonElement features = snapshot.GetProperty("features");
        AssertExactProperties(
            features,
            "openingHook",
            "subtitleStyle",
            "telopCount",
            "transitions",
            "effects",
            "callToAction",
            "destination",
            "warnings");
        Assert.IsTrue(features.EnumerateObject().All(static property =>
            property.Value.GetString() == "unavailable"));
    }

    private static void AssertClosedFailure(
        string responseJson,
        string expectedStatus)
    {
        using JsonDocument response = JsonDocument.Parse(responseJson);
        JsonElement result = response.RootElement.GetProperty("result");
        AssertExactProperties(result, "status", "comparison");
        Assert.AreEqual(expectedStatus, result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("comparison").ValueKind);
        AssertPrivateDetailAbsent(responseJson);
    }

    private static void AssertPrivateDetailAbsent(string responseJson)
    {
        Assert.IsFalse(responseJson.Contains("C:\\", StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains(
            "private",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains(
            "exception",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains(
            "stack",
            StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRationalTime(
        JsonElement value,
        string expectedValue,
        string expectedRate)
    {
        AssertExactProperties(value, "value", "rate");
        Assert.AreEqual(expectedValue, value.GetProperty("value").GetString());
        Assert.AreEqual(expectedRate, value.GetProperty("rate").GetString());
    }

    private static string ReadStatus(string responseJson)
    {
        using JsonDocument response = JsonDocument.Parse(responseJson);
        return response.RootElement.GetProperty("result")
            .GetProperty("status").GetString()!;
    }

    private static BridgeRequest ParseRequest(
        EntityId<CompositionVersionIdTag> leftId,
        string leftDigest,
        EntityId<CompositionVersionIdTag> rightId,
        string rightDigest)
    {
        string message = CreateRequest(leftId, leftDigest, rightId, rightDigest);
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection),
            rejection.ToString());
        return request ?? throw new AssertFailedException(
            "Expected a comparison bridge request.");
    }

    private static string CreateRequest(
        EntityId<CompositionVersionIdTag> leftId,
        string leftDigest,
        EntityId<CompositionVersionIdTag> rightId,
        string rightDigest,
        string extraParameterPrefix = "") =>
        $$$"""
        {"version":1,"requestId":"{{{RequestId}}}","method":"compare.read","params":{ {{{extraParameterPrefix}}}"leftVersionId":"{{{leftId}}}","leftVersionDigest":"{{{leftDigest}}}","rightVersionId":"{{{rightId}}}","rightVersionDigest":"{{{rightDigest}}}"}}
        """;

    private static string CreateRequestText(
        string leftId,
        string leftDigest,
        string rightId,
        string rightDigest) =>
        $$$"""
        {"version":1,"requestId":"{{{RequestId}}}","method":"compare.read","params":{"leftVersionId":"{{{leftId}}}","leftVersionDigest":"{{{leftDigest}}}","rightVersionId":"{{{rightId}}}","rightVersionDigest":"{{{rightDigest}}}"}}
        """;

    private static CompositionVersion CreateVersion(
        EntityId<CompositionVersionIdTag> versionId,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag>? parentVersionId,
        string reason,
        IReadOnlyList<CompositionElement> elements,
        DateTimeOffset createdAt)
    {
        ContentDigest digest = ContentDigest.Compute(
            CompositionSnapshotJson.WriteElementsObjectToUtf8(elements));
        return new CompositionVersion(
            versionId,
            projectId,
            parentVersionId,
            PrivateActorSid,
            createdAt,
            reason,
            digest,
            elements);
    }

    private static StoredCompositionVersion Store(CompositionVersion version) =>
        new(
            version.Id,
            version.ProjectId,
            version.ParentVersionId,
            version.ContentDigest,
            version.CreatedAt,
            CompositionSnapshotJson.WriteVersion(version));

    private static void AssertExactProperties(
        JsonElement value,
        params string[] names)
    {
        CollectionAssert.AreEquivalent(
            names,
            value.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(names.Length, value.EnumerateObject().Count());
    }

    private sealed class RecordingAuthorization(bool deny = false) :
        IComparisonBridgeAuthorization
    {
        internal int DemandCount { get; private set; }

        public void DemandRead()
        {
            DemandCount++;
            if (deny)
            {
                throw new UnauthorizedAccessException(
                    "Denied by C:\\private\\role-store.db");
            }
        }
    }
}