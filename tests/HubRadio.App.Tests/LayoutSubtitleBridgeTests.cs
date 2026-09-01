using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Graphics;
using HubRadio.App.Security;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class LayoutSubtitleBridgeTests
{
    private const string ActorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-8101";
    private static readonly Guid RequestId = Guid.Parse(
        "00112233-4455-4677-8899-aabbccddeeff");
    private static readonly DateTimeOffset CreatedAt = new(
        2038,
        4,
        5,
        6,
        7,
        8,
        TimeSpan.Zero);

    [TestMethod]
    public async Task AuthorizationPrecedesRuntimeAccessForReadAndMutation()
    {
        Fixture fixture = Fixture.Create();
        var runtime = new CountingRuntime();
        var authorization = new StubAuthorization(allow: false);
        var handler = new LayoutSubtitleBridgeRequestHandler(
            runtime,
            authorization);

        using JsonDocument read = JsonDocument.Parse(await handler.HandleAsync(
            fixture.ReadRequest()));
        using JsonDocument apply = JsonDocument.Parse(await handler.HandleAsync(
            fixture.ApplyRequest(fixture.ManualLayoutOperation())));

        AssertResult(read, "forbidden", hasSnapshot: false);
        AssertResult(apply, "forbidden", hasSnapshot: false);
        Assert.AreEqual(0, runtime.CallCount);
        Assert.AreEqual(1, authorization.ReadDemandCount);
        Assert.AreEqual(1, authorization.MutationDemandCount);
    }

    [TestMethod]
    public async Task MissingProductionRuntimeFailsClosedAsUnavailable()
    {
        Fixture fixture = Fixture.Create();
        var handler = new LayoutSubtitleBridgeRequestHandler(
            UnavailableLayoutSubtitleBridgeRuntime.Instance,
            new StubAuthorization(allow: true));

        using JsonDocument response = JsonDocument.Parse(
            await handler.HandleAsync(fixture.ReadRequest()));

        AssertResult(response, "unavailable", hasSnapshot: false);
    }

    [TestMethod]
    public void ProtocolAcceptsOnlyClosedCanonicalLayoutRequests()
    {
        Fixture fixture = Fixture.Create();

        Assert.AreEqual(BridgeMethod.LayoutRead, fixture.ReadRequest().Method);
        Assert.AreEqual(
            BridgeMethod.LayoutApply,
            fixture.ApplyRequest(fixture.ManualLayoutOperation()).Method);
        Assert.AreEqual(
            BridgeProtocol.MaxLayoutResponseUtf8Bytes,
            BridgeProtocol.GetMaximumResponseUtf8Bytes(
                BridgeMethod.LayoutRead));

        AssertRejected(
            BridgeProtocol.LayoutReadMethod,
            new
            {
                projectId = fixture.ProjectId.ToString(),
                evaluationTime = (object?)null,
                actorSid = ActorSid,
            });
        AssertRejected(
            BridgeProtocol.LayoutReadMethod,
            new
            {
                projectId = "A1000001-0000-4000-8000-000000000001",
                evaluationTime = (object?)null,
            });
        AssertRejected(
            BridgeProtocol.LayoutReadMethod,
            new
            {
                projectId = fixture.ProjectId.ToString(),
                evaluationTime = TimeDto("2", "2"),
            });
        AssertRejected(
            BridgeProtocol.LayoutApplyMethod,
            new
            {
                projectId = fixture.ProjectId.ToString(),
                expectedRevision = 0,
                operation = new
                {
                    kind = "deleteEverything",
                },
            });
        AssertRejected(
            BridgeProtocol.LayoutApplyMethod,
            new
            {
                projectId = fixture.ProjectId.ToString(),
                expectedRevision = 0,
                operation = new
                {
                    kind = "setManualLayout",
                    mode = "singleSubjectCrop",
                    staticCropDecision = "singleStaticCropSufficient",
                    sources = Array.Empty<object>(),
                    templateId = (string?)null,
                },
            });
    }

    [TestMethod]
    public void SnapshotRequiresExactNineBySixteenOutputFrameEnvelope()
    {
        Fixture fixture = Fixture.Create();
        LayoutSpec wrongSafeZone = new(
            Id<LayoutSpecIdTag>(0x81000040, 1),
            9,
            16,
            fixture.OutputEnvelope.Regions,
            [new SafeZone("guessed-safe-zone", FullRect())]);

        Assert.ThrowsExactly<ArgumentException>(() =>
            new LayoutSubtitleBridgeSnapshot(
                fixture.ProjectId,
                0,
                fixture.RootComposition,
                wrongSafeZone,
                1080,
                1920,
                fixture.SourceBindings,
                templates: [],
                fixture.SubtitleBindings,
                fixture.TeropBindings,
                fixture.SubtitleDisplays,
                compositionVersions: fixture.CompositionVersions));
    }

    [TestMethod]
    public async Task ReadReturnsNativeResolvedNineBySixteenGeometry()
    {
        Fixture fixture = Fixture.Create();

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ReadRequest());
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);

        Assert.AreEqual(1080, snapshot.GetProperty("output")
            .GetProperty("width").GetInt32());
        Assert.AreEqual(1920, snapshot.GetProperty("output")
            .GetProperty("height").GetInt32());
        Assert.AreEqual(9, snapshot.GetProperty("output")
            .GetProperty("aspectWidth").GetInt32());
        Assert.AreEqual(16, snapshot.GetProperty("output")
            .GetProperty("aspectHeight").GetInt32());
        JsonElement safeZone = snapshot.GetProperty("layout")
            .GetProperty("safeZones")[0];
        Assert.AreEqual("output-frame-bounds", safeZone
            .GetProperty("name").GetString());
        AssertPixelRect(
            safeZone.GetProperty("pixels"),
            x: 0,
            y: 0,
            width: 1080,
            height: 1920);
        Assert.AreEqual("notPersisted", snapshot
            .GetProperty("persistence").GetString());
        Assert.AreEqual(JsonValueKind.Null, snapshot
            .GetProperty("manualSelection").ValueKind);
        AssertPrivateDetailsAbsent(response.RootElement.GetRawText());
    }

    [TestMethod]
    public async Task ReadProjectsOnlyNativeValidatedSubtitleDisplayPolicies()
    {
        Fixture fixture = Fixture.Create();

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ReadRequest());
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        JsonElement displays = snapshot.GetProperty("subtitleDisplays");
        JsonElement hidden = displays.EnumerateArray().Single(option =>
            option.GetProperty("cueMode").GetString() == "full"
            && option.GetProperty("punctuationDisplay").GetString()
                == "hideJapaneseCommaAndPeriod");

        Assert.AreEqual(4, displays.GetArrayLength());
        Assert.AreEqual(1, hidden.GetProperty("sourceCueCount").GetInt32());
        Assert.AreEqual("字幕です", hidden.GetProperty("displayedCues")[0]
            .GetProperty("text").GetString());
        Assert.IsTrue(hidden.GetProperty("displayedCues")[0]
            .GetProperty("textChanged").GetBoolean());
        Assert.AreEqual("字幕、です。", snapshot.GetProperty("subtitles")[0]
            .GetProperty("text").GetString());
        Assert.AreEqual("字幕、です。", fixture.SubtitleHistory
            .CurrentRevision.Snapshot.Text);
    }

    [TestMethod]
    public void SnapshotRejectsDuplicateOrAmbiguousSubtitleDisplayEvidence()
    {
        Fixture fixture = Fixture.Create();
        var duplicateCombination = new LayoutSubtitleDisplayBinding(
            new SubtitleDisplayProfile(
                Digest('9'),
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.Preserve),
            []);
        var ambiguousFullSelection = new LayoutSubtitleDisplayBinding(
            new SubtitleDisplayProfile(
                Digest('8'),
                SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod),
            [fixture.SubtitleHistory.CueId]);

        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.CreateSnapshot([
                .. fixture.SubtitleDisplays,
                duplicateCombination,
            ]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.CreateSnapshot([ambiguousFullSelection]));
    }

    [TestMethod]
    public async Task NoFaceManualPathPreservesExactNormalizedCoordinatesAndPixels()
    {
        Fixture fixture = Fixture.Create();

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ApplyRequest(fixture.ManualLayoutOperation()));
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        JsonElement selection = snapshot.GetProperty("manualSelection");

        Assert.AreEqual("singleSubjectCrop", selection
            .GetProperty("mode").GetString());
        Assert.AreEqual("resolved", selection
            .GetProperty("status").GetString());
        JsonElement placement = selection.GetProperty("placements")[0];
        AssertRational(
            placement.GetProperty("sourceCrop")
                .GetProperty("rect").GetProperty("x"),
            "1",
            "3");
        AssertPixelRect(
            placement.GetProperty("sourceCrop").GetProperty("pixels"),
            x: 360,
            y: 0,
            width: 720,
            height: 1920);
        AssertPixelRect(
            placement.GetProperty("destination").GetProperty("pixels"),
            x: 360,
            y: 0,
            width: 720,
            height: 1920);
        Assert.AreEqual("subject", placement
            .GetProperty("semanticRole").GetString());
        Assert.AreEqual(1, snapshot.GetProperty("revision").GetInt32());
        Assert.AreEqual(0, fixture.Snapshot.Sources.Count(source =>
            source.GetType().Name.Contains("Face", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ManualKeyframesRequiredRemainsExplicitWithoutFallbackPlan()
    {
        Fixture fixture = Fixture.Create();

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ApplyRequest(fixture.ManualLayoutOperation(
                "manualKeyframesRequired")));
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);

        Assert.AreEqual("manualKeyframesRequired", snapshot
            .GetProperty("manualSelection")
            .GetProperty("status").GetString());
        Assert.AreEqual(
            fixture.OutputEnvelope.Id.ToString(),
            snapshot.GetProperty("layout")
                .GetProperty("layoutSpecId").GetString());
        Assert.AreEqual(0, snapshot.GetProperty("keyframeTracks")
            .GetArrayLength());
    }

    [TestMethod]
    public async Task KeyframesUseExactNativeEvaluationForPreviewAndRenderGeometry()
    {
        Fixture fixture = Fixture.Create();

        using JsonDocument applied = await fixture.HandleAsync(
            fixture.ApplyRequest(fixture.KeyframeOperation()));
        JsonElement firstSnapshot = AssertResult(
            applied,
            "available",
            hasSnapshot: true);
        JsonElement firstEvaluation = firstSnapshot
            .GetProperty("keyframeTracks")[0]
            .GetProperty("evaluation");
        AssertRational(
            firstEvaluation.GetProperty("destination").GetProperty("x"),
            "1",
            "4");
        AssertPixelRect(
            firstEvaluation.GetProperty("destinationPixels"),
            x: 270,
            y: 480,
            width: 540,
            height: 960);

        using JsonDocument projected = await fixture.HandleAsync(
            fixture.ReadRequest(TimeDto("3", "2")));
        JsonElement projectedSnapshot = AssertResult(
            projected,
            "available",
            hasSnapshot: true);
        JsonElement projectedEvaluation = projectedSnapshot
            .GetProperty("keyframeTracks")[0]
            .GetProperty("evaluation");
        AssertRational(
            projectedEvaluation.GetProperty("destination").GetProperty("x"),
            "3",
            "8");
        AssertPixelRect(
            projectedEvaluation.GetProperty("destinationPixels"),
            x: 405,
            y: 720,
            width: 540,
            height: 960);
        Assert.AreEqual(1, projectedSnapshot
            .GetProperty("revision").GetInt32());
    }

    [TestMethod]
    public async Task SubtitlePlacementTreatsBoundaryContactAsSafe()
    {
        Fixture fixture = Fixture.Create(withProhibitedRegion: true);
        object operation = fixture.SubtitlePlacementOperation(
            RectDto("0", "1", "3", "4", "1", "2", "1", "4"));

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ApplyRequest(operation));
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        JsonElement subtitle = snapshot.GetProperty("subtitles")[0];

        Assert.AreEqual("resolved", subtitle
            .GetProperty("layoutResolution")
            .GetProperty("status").GetString());
        Assert.AreEqual(2, subtitle.GetProperty("revisionCount").GetInt32());
    }

    [TestMethod]
    public async Task SubtitlePlacementSurfacesProhibitedOverlapAsNoSolution()
    {
        Fixture fixture = Fixture.Create(withProhibitedRegion: true);
        object operation = fixture.SubtitlePlacementOperation(
            RectDto("0", "1", "1", "2", "1", "1", "1", "4"));

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ApplyRequest(operation));
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        JsonElement resolution = snapshot.GetProperty("subtitles")[0]
            .GetProperty("layoutResolution");

        Assert.AreEqual("manualAdjustmentRequired", resolution
            .GetProperty("status").GetString());
        CollectionAssert.Contains(
            resolution.GetProperty("evaluations")[0]
                .GetProperty("hardConstraints")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray(),
            "prohibitedRegionOverlap");
        Assert.AreEqual(JsonValueKind.Null, resolution
            .GetProperty("selectedCandidateId").ValueKind);
    }

    [TestMethod]
    public async Task MissingRequiredSafeZoneIsExposedWithoutInventingFallback()
    {
        Fixture fixture = Fixture.Create(missingRequiredSafeZone: true);

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ReadRequest());
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        JsonElement resolution = snapshot.GetProperty("subtitles")[0]
            .GetProperty("layoutResolution");

        Assert.AreEqual("manualAdjustmentRequired", resolution
            .GetProperty("status").GetString());
        CollectionAssert.Contains(
            resolution.GetProperty("globalHardConstraints")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray(),
            "requiredSafeZoneMissing");
    }

    [TestMethod]
    public async Task SubtitleEditCreatesImmutableDirectChildRevision()
    {
        Fixture fixture = Fixture.Create();
        EntityId<CompositionVersionIdTag> parentId =
            fixture.RootComposition.Id;
        ContentDigest previousDigest = fixture.SubtitleHistory
            .CurrentRevision.ContentDigest;

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ApplyRequest(fixture.SubtitleTextOperation("更新、字幕。")));
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        LayoutSubtitleBridgeSnapshot installed = fixture.Runtime.GetRequired(
            fixture.ProjectId);
        SubtitleCueHistory history = installed.Subtitles[0].History;

        Assert.AreEqual(2, history.Revisions.Count);
        Assert.AreEqual("更新、字幕。", history.CurrentRevision.Snapshot.Text);
        Assert.AreEqual(parentId, history.CurrentRevision.ParentCompositionVersionId);
        Assert.AreNotEqual(previousDigest, history.CurrentRevision.ContentDigest);
        Assert.AreEqual(parentId, installed.CompositionVersion.ParentVersionId);
        Assert.AreEqual(1, installed.Revision);
        Assert.AreEqual(JsonValueKind.Null, snapshot
            .GetProperty("variantPair").ValueKind);
        JsonElement hidden = snapshot.GetProperty("subtitleDisplays")
            .EnumerateArray().Single(option =>
                option.GetProperty("cueMode").GetString() == "full"
                && option.GetProperty("punctuationDisplay").GetString()
                    == "hideJapaneseCommaAndPeriod");
        Assert.AreEqual("更新字幕", hidden.GetProperty("displayedCues")[0]
            .GetProperty("text").GetString());
        Assert.IsTrue(hidden.GetProperty("displayedCues")[0]
            .GetProperty("textChanged").GetBoolean());
    }

    [TestMethod]
    public async Task StaleAggregateAndCueEvidenceReturnConflictWithoutMutation()
    {
        Fixture fixture = Fixture.Create();
        BridgeRequest staleAggregate = fixture.ApplyRequest(
            fixture.SubtitleTextOperation("更新字幕"),
            expectedRevision: 9);
        object staleCue = fixture.SubtitleTextOperation(
            "更新字幕",
            expectedRevisionDigest: Digest('f').Hex);

        using JsonDocument first = await fixture.HandleAsync(staleAggregate);
        using JsonDocument second = await fixture.HandleAsync(
            fixture.ApplyRequest(staleCue));

        AssertResult(first, "conflict", hasSnapshot: false);
        AssertResult(second, "conflict", hasSnapshot: false);
        LayoutSubtitleBridgeSnapshot installed = fixture.Runtime.GetRequired(
            fixture.ProjectId);
        Assert.AreEqual(0, installed.Revision);
        Assert.AreEqual(1, installed.Subtitles[0].History.Revisions.Count);
    }

    [TestMethod]
    public async Task CrossCueCompositionBranchIsRejectedInsteadOfCreatingSibling()
    {
        Fixture fixture = Fixture.Create(includeTerop: true);

        using JsonDocument subtitle = await fixture.HandleAsync(
            fixture.ApplyRequest(fixture.SubtitleTextOperation("更新字幕")));
        AssertResult(subtitle, "available", hasSnapshot: true);
        using JsonDocument terop = await fixture.HandleAsync(
            fixture.ApplyRequest(
                fixture.TeropTextOperation("更新テロップ"),
                expectedRevision: 1));

        AssertResult(terop, "conflict", hasSnapshot: false);
        LayoutSubtitleBridgeSnapshot installed = fixture.Runtime.GetRequired(
            fixture.ProjectId);
        Assert.AreEqual(1, installed.Revision);
        Assert.AreEqual(1, installed.Terops[0].History.Revisions.Count);
        Assert.AreEqual(
            fixture.RootComposition.Id,
            installed.Terops[0].History.CurrentRevision.CompositionVersionId);
    }

    [TestMethod]
    public async Task TeropEditUsesIndependentTypedRevisionHistory()
    {
        Fixture fixture = Fixture.Create(includeTerop: true);

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ApplyRequest(fixture.TeropTextOperation("更新テロップ")));
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        LayoutSubtitleBridgeSnapshot installed = fixture.Runtime.GetRequired(
            fixture.ProjectId);

        Assert.AreEqual("更新テロップ", snapshot.GetProperty("terops")[0]
            .GetProperty("text").GetString());
        Assert.AreEqual(2, installed.Terops[0].History.Revisions.Count);
        Assert.AreEqual(1, installed.Subtitles[0].History.Revisions.Count);
    }

    [TestMethod]
    public async Task OversizedMutationResponseDoesNotInstallState()
    {
        Fixture fixture = Fixture.Create(oversizedTemplateCatalog: true);

        using JsonDocument response = await fixture.HandleAsync(
            fixture.ApplyRequest(fixture.ManualLayoutOperation()));

        AssertResult(response, "unavailable", hasSnapshot: false);
        LayoutSubtitleBridgeSnapshot installed = fixture.Runtime.GetRequired(
            fixture.ProjectId);
        Assert.AreEqual(0, installed.Revision);
        Assert.IsNull(installed.ManualSelection);
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxLayoutResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(response.RootElement.GetRawText()));
    }

    private static JsonElement AssertResult(
        JsonDocument response,
        string expectedStatus,
        bool hasSnapshot)
    {
        JsonElement root = response.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        JsonElement result = root.GetProperty("result");
        CollectionAssert.AreEquivalent(
            new[] { "status", "snapshot" },
            result.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(expectedStatus, result.GetProperty("status").GetString());
        Assert.AreEqual(
            hasSnapshot ? JsonValueKind.Object : JsonValueKind.Null,
            result.GetProperty("snapshot").ValueKind);
        return result.GetProperty("snapshot");
    }

    private static void AssertRejected(string method, object parameters)
    {
        string message = JsonSerializer.Serialize(new
        {
            version = BridgeProtocol.Version,
            requestId = RequestId.ToString("D"),
            method,
            @params = parameters,
        });
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }

    private static void AssertRational(
        JsonElement value,
        string numerator,
        string denominator)
    {
        Assert.AreEqual(numerator, value.GetProperty("numerator").GetString());
        Assert.AreEqual(
            denominator,
            value.GetProperty("denominator").GetString());
    }

    private static void AssertPixelRect(
        JsonElement value,
        int x,
        int y,
        int width,
        int height)
    {
        Assert.AreEqual(x, value.GetProperty("x").GetInt32());
        Assert.AreEqual(y, value.GetProperty("y").GetInt32());
        Assert.AreEqual(width, value.GetProperty("width").GetInt32());
        Assert.AreEqual(height, value.GetProperty("height").GetInt32());
    }

    private static void AssertPrivateDetailsAbsent(string response)
    {
        string[] forbidden =
        [
            "file://",
            "storageReference",
            "confidence",
            "provider",
            "modelVersion",
            ActorSid,
            "Exception",
        ];
        foreach (string value in forbidden)
        {
            Assert.IsFalse(response.Contains(
                value,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static object TimeDto(string value, string rate) =>
        new { value, rate };

    private static object RangeDto(
        string startValue,
        string startRate,
        string durationValue,
        string durationRate) =>
        new
        {
            start = TimeDto(startValue, startRate),
            duration = TimeDto(durationValue, durationRate),
        };

    private static object RationalDto(string numerator, string denominator) =>
        new { numerator, denominator };

    private static object RectDto(
        string xNumerator,
        string xDenominator,
        string yNumerator,
        string yDenominator,
        string widthNumerator,
        string widthDenominator,
        string heightNumerator,
        string heightDenominator) =>
        new
        {
            x = RationalDto(xNumerator, xDenominator),
            y = RationalDto(yNumerator, yDenominator),
            width = RationalDto(widthNumerator, widthDenominator),
            height = RationalDto(heightNumerator, heightDenominator),
        };

    private static NormalizedRect Rect(
        long xNumerator,
        long xDenominator,
        long yNumerator,
        long yDenominator,
        long widthNumerator,
        long widthDenominator,
        long heightNumerator,
        long heightDenominator) =>
        new(
            new RationalNumber(xNumerator, xDenominator),
            new RationalNumber(yNumerator, yDenominator),
            new RationalNumber(widthNumerator, widthDenominator),
            new RationalNumber(heightNumerator, heightDenominator));

    private static NormalizedRect FullRect() =>
        Rect(0, 1, 0, 1, 1, 1, 1, 1);

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static TimeRange Range(long start, long duration, long rate = 1) =>
        new(Time(start, rate), Time(duration, rate));

    private static ContentDigest Digest(char fill) =>
        new(new string(fill, 64));

    private static ContentDigest EmptyCompositionDigest() =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes("{\"elements\":[]}"));

    private static EntityId<TTag> Id<TTag>(uint prefix, long value) =>
        EntityId<TTag>.Parse($"{prefix:x8}-0000-4000-8000-{value:x12}");

    private sealed class CountingRuntime : ILayoutSubtitleBridgeRuntime
    {
        internal int CallCount { get; private set; }

        public LayoutSubtitleBridgeOperationResult Execute(
            BridgeRequest request,
            string actorSid,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return LayoutSubtitleBridgeOperationResult.Failure(
                LayoutSubtitleBridgeStatus.Unavailable);
        }
    }

    private sealed class StubAuthorization(bool allow) :
        ILayoutSubtitleBridgeAuthorization
    {
        internal int ReadDemandCount { get; private set; }

        internal int MutationDemandCount { get; private set; }

        public string DemandRead()
        {
            ReadDemandCount++;
            if (!allow)
            {
                throw new AuthorizationException(
                    AuthorizationOperation.ViewProject,
                    actingRole: null);
            }

            return ActorSid;
        }

        public string DemandMutation()
        {
            MutationDemandCount++;
            if (!allow)
            {
                throw new AuthorizationException(
                    AuthorizationOperation.CreateCompositionVersion,
                    AuthorizationRole.Editor);
            }

            return ActorSid;
        }
    }

    private sealed class Fixture
    {
        private Fixture(
            EntityId<ProjectIdTag> projectId,
            EntityId<AssetIdTag> sourceAssetId,
            LayoutSpec outputEnvelope,
            CompositionVersion rootComposition,
            SubtitleCueHistory subtitleHistory,
            IReadOnlyList<LayoutSubtitleSourceBinding> sourceBindings,
            IReadOnlyList<LayoutSubtitleCueBinding> subtitleBindings,
            IReadOnlyList<LayoutSubtitleTeropBinding> teropBindings,
            IReadOnlyList<LayoutSubtitleDisplayBinding> subtitleDisplays,
            IReadOnlyList<TemplateReference> templates)
        {
            ProjectId = projectId;
            SourceAssetId = sourceAssetId;
            OutputEnvelope = outputEnvelope;
            RootComposition = rootComposition;
            SubtitleHistory = subtitleHistory;
            SourceBindings = sourceBindings;
            SubtitleBindings = subtitleBindings;
            TeropBindings = teropBindings;
            SubtitleDisplays = subtitleDisplays;
            CompositionVersions = new Dictionary<
                EntityId<CompositionVersionIdTag>,
                CompositionVersion>
            {
                [rootComposition.Id] = rootComposition,
            };
            Snapshot = new LayoutSubtitleBridgeSnapshot(
                projectId,
                revision: 0,
                rootComposition,
                outputEnvelope,
                outputPixelWidth: 1080,
                outputPixelHeight: 1920,
                sourceBindings,
                templates,
                subtitleBindings,
                teropBindings,
                subtitleDisplays,
                compositionVersions: CompositionVersions);
            Runtime = new LayoutSubtitleBridgeRuntime(
                () => CreatedAt.AddMinutes(20),
                Guid.NewGuid);
            Runtime.Register(Snapshot);
            Handler = new LayoutSubtitleBridgeRequestHandler(
                Runtime,
                new StubAuthorization(allow: true));
        }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal EntityId<AssetIdTag> SourceAssetId { get; }

        internal LayoutSpec OutputEnvelope { get; }

        internal CompositionVersion RootComposition { get; }

        internal SubtitleCueHistory SubtitleHistory { get; }

        internal IReadOnlyList<LayoutSubtitleSourceBinding> SourceBindings { get; }

        internal IReadOnlyList<LayoutSubtitleCueBinding> SubtitleBindings { get; }

        internal IReadOnlyList<LayoutSubtitleTeropBinding> TeropBindings { get; }

        internal IReadOnlyList<LayoutSubtitleDisplayBinding> SubtitleDisplays
        { get; }

        internal IReadOnlyDictionary<
            EntityId<CompositionVersionIdTag>,
            CompositionVersion>
            CompositionVersions
        { get; }

        internal LayoutSubtitleBridgeSnapshot Snapshot { get; }

        internal LayoutSubtitleBridgeRuntime Runtime { get; }

        private LayoutSubtitleBridgeRequestHandler Handler { get; }

        internal static Fixture Create(
            bool includeTerop = false,
            bool withProhibitedRegion = false,
            bool missingRequiredSafeZone = false,
            bool oversizedTemplateCatalog = false)
        {
            EntityId<ProjectIdTag> projectId = Id<ProjectIdTag>(0x81000001, 1);
            EntityId<TranscriptRevisionIdTag> transcriptRevisionId =
                Id<TranscriptRevisionIdTag>(0x81000002, 1);
            EntityId<TranscriptTokenIdTag> tokenId =
                Id<TranscriptTokenIdTag>(0x81000003, 1);
            EntityId<SourceSpanIdTag> sourceSpanId =
                Id<SourceSpanIdTag>(0x81000004, 1);
            EntityId<AssetIdTag> assetId = Id<AssetIdTag>(0x81000005, 1);
            var token = new Token(tokenId, "字幕、です。", Range(0, 2), null);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(0x81000006, 1),
                token.Text,
                [token]);
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(0x81000007, 1),
                sourceSpanId,
                Id<TranscriptSpeakerIdTag>(0x81000008, 1),
                "Speaker",
                token.Text,
                Range(0, 2),
                null,
                [sentence]);
            var transcript = new TranscriptRevision(
                transcriptRevisionId,
                Id<TranscriptRevisionIdTag>(0x81000002, 2),
                TranscriptChangeSource.Human,
                CreatedAt,
                [utterance]);
            var sourceSpan = new SourceSpan(
                sourceSpanId,
                assetId,
                "video:0",
                Range(0, 2),
                Digest('a'),
                transcriptRevisionId,
                [tokenId]);
            ContentDigest transcriptDigest = ContentDigest.Compute(
                Encoding.UTF8.GetBytes(
                    TranscriptSnapshotJson.WriteRevision(transcript)));
            SubtitleCueCandidate candidate = SubtitleSegmenter.Segment(
                    transcript,
                    transcriptDigest,
                    [sourceSpan])
                .Candidates
                .Single();

            LayoutSpec layout = new(
                Id<LayoutSpecIdTag>(0x81000009, 1),
                9,
                16,
                [
                    new LayoutRegion(
                        "subtitle",
                        Rect(0, 1, 1, 2, 1, 1, 1, 2),
                        LayoutRegionRole.Subtitle),
                    new LayoutRegion(
                        "graphic",
                        Rect(0, 1, 0, 1, 1, 1, 1, 4),
                        LayoutRegionRole.Graphic),
                ],
                [new SafeZone("output-frame-bounds", FullRect())]);
            var profile = new SubtitleLayoutProfile(
                layout.Id,
                Digest('b'),
                missingRequiredSafeZone
                    ? ["missing-safe-zone"]
                    : ["output-frame-bounds"],
                Enum.GetValues<SubtitleSoftConstraintKind>()
                    .Select(kind => new SubtitleSoftConstraintWeight(kind, 0))
                    .ToArray());
            var placement = new SubtitlePlacementCandidate(
                Id<SubtitlePlacementCandidateIdTag>(0x8100000a, 1),
                "subtitle",
                new SubtitleLayoutRect(
                    new RationalNumber(0, 1),
                    new RationalNumber(3, 4),
                    new RationalNumber(1, 1),
                    new RationalNumber(1, 4)));
            SubtitleLayoutResolution resolution = SubtitleLayoutEngine.Resolve(
                new SubtitleLayoutRequest(
                    candidate,
                    layout,
                    profile,
                    [placement],
                    prohibitedRegions: [],
                    faceRegions: [],
                    previousPlacement: null));
            var root = new CompositionVersion(
                Id<CompositionVersionIdTag>(0x8100000b, 1),
                projectId,
                parentVersionId: null,
                ActorSid,
                CreatedAt,
                "Create root composition.",
                EmptyCompositionDigest(),
                elements: []);
            SubtitleCueHistory subtitle = SubtitleService.CreateInitial(
                root,
                candidate,
                resolution,
                Id<CueIdTag>(0x8100000c, 1),
                Id<SubtitleCueRevisionIdTag>(0x8100000d, 1),
                Range(1, 1),
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Create subtitle cue.");
            IReadOnlyList<SubtitleProhibitedRegion> prohibited =
                withProhibitedRegion
                    ? [
                        new SubtitleProhibitedRegion(
                            "terop-occupied",
                            Rect(0, 1, 0, 1, 1, 1, 3, 4)),
                    ]
                    : [];
            var subtitleBinding = new LayoutSubtitleCueBinding(
                subtitle,
                layout,
                profile,
                prohibited,
                faceRegions: []);
            IReadOnlyList<LayoutSubtitleDisplayBinding> subtitleDisplays =
            [
                new(
                    new SubtitleDisplayProfile(
                        Digest('1'),
                        SubtitleCueDisplayMode.Full,
                        SubtitlePunctuationDisplay.Preserve),
                    []),
                new(
                    new SubtitleDisplayProfile(
                        Digest('2'),
                        SubtitleCueDisplayMode.Full,
                        SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod),
                    []),
                new(
                    new SubtitleDisplayProfile(
                        Digest('3'),
                        SubtitleCueDisplayMode.Selected,
                        SubtitlePunctuationDisplay.Preserve),
                    [subtitle.CueId]),
                new(
                    new SubtitleDisplayProfile(
                        Digest('4'),
                        SubtitleCueDisplayMode.Selected,
                        SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod),
                    [subtitle.CueId]),
            ];

            IReadOnlyList<LayoutSubtitleTeropBinding> teropBindings = [];
            if (includeTerop)
            {
                TopicMetadataRevision topicMetadata = CreateTopicMetadata(
                    projectId,
                    transcript,
                    sourceSpan);
                var context = new EmphasisCuePlacementContext(
                    layout,
                    profile,
                    topicOutputRange: Range(0, 4),
                    evidenceOutputRange: null,
                    currentSourceRevisionDigest: null,
                    existingCues: [],
                    prohibitedRegions: []);
                EmphasisCueHistory terop = EmphasisCueService.CreateManual(
                    root,
                    Id<CueIdTag>(0x8100000e, 1),
                    Range(2, 1),
                    "graphic",
                    "テロップ",
                    SubtitleCueStyle.Emphasis,
                    OriginRef.FromEditorialRecord(
                        Id<EditorialRecordIdTag>(0x8100000f, 1)),
                    topicMetadata,
                    context,
                    Id<EmphasisCueRevisionIdTag>(0x81000010, 1),
                    ActorSid,
                    CreatedAt.AddMinutes(2),
                    "Create terop cue.");
                teropBindings =
                [
                    new LayoutSubtitleTeropBinding(
                        terop,
                        topicMetadata,
                        context),
                ];
            }

            IReadOnlyList<TemplateReference> templates =
                oversizedTemplateCatalog
                    ? Enumerable.Range(1, 256)
                        .Select(index => new TemplateReference(
                            Id<TemplateIdTag>(0x81000020, index),
                            new string('x', 16 * 1024),
                            Digest('c')))
                        .ToArray()
                    : [];
            return new Fixture(
                projectId,
                assetId,
                layout,
                root,
                subtitle,
                [
                    new LayoutSubtitleSourceBinding(
                        assetId,
                        OriginRef.FromAsset(assetId),
                        new SourceDisplayGeometry(1080, 1920)),
                ],
                [subtitleBinding],
                teropBindings,
                subtitleDisplays,
                templates);
        }

        internal BridgeRequest ReadRequest(object? evaluationTime = null) =>
            ParseRequest(
                BridgeProtocol.LayoutReadMethod,
                new
                {
                    projectId = ProjectId.ToString(),
                    evaluationTime,
                });

        internal LayoutSubtitleBridgeSnapshot CreateSnapshot(
            IReadOnlyList<LayoutSubtitleDisplayBinding> subtitleDisplays) =>
            new(
                ProjectId,
                revision: 0,
                RootComposition,
                OutputEnvelope,
                outputPixelWidth: 1080,
                outputPixelHeight: 1920,
                SourceBindings,
                templates: [],
                SubtitleBindings,
                TeropBindings,
                subtitleDisplays,
                compositionVersions: CompositionVersions);

        internal BridgeRequest ApplyRequest(
            object operation,
            int expectedRevision = 0) =>
            ParseRequest(
                BridgeProtocol.LayoutApplyMethod,
                new
                {
                    projectId = ProjectId.ToString(),
                    expectedRevision,
                    operation,
                });

        internal object ManualLayoutOperation(
            string staticCropDecision = "singleStaticCropSufficient") =>
            new
            {
                kind = "setManualLayout",
                mode = "singleSubjectCrop",
                staticCropDecision,
                sources = new[]
                {
                    new
                    {
                        sourceAssetId = SourceAssetId.ToString(),
                        crop = RectDto(
                            "1", "3", "0", "1", "2", "3", "1", "1"),
                        subject = RectDto(
                            "1", "3", "1", "4", "1", "3", "1", "2"),
                        destination = RectDto(
                            "1", "3", "0", "1", "2", "3", "1", "1"),
                    },
                },
                templateId = (string?)null,
            };

        internal object KeyframeOperation() =>
            new
            {
                kind = "setKeyframes",
                sourceAssetId = SourceAssetId.ToString(),
                destinationSlot = "singleSubject",
                segments = new[]
                {
                    new
                    {
                        range = RangeDto("0", "1", "2", "1"),
                        keyframes = new[]
                        {
                            new
                            {
                                time = TimeDto("0", "1"),
                                sourceCrop = RectDto(
                                    "0", "1", "0", "1", "1", "2", "1", "2"),
                                destination = RectDto(
                                    "0", "1", "0", "1", "1", "2", "1", "2"),
                                interpolation = "linear",
                            },
                            new
                            {
                                time = TimeDto("2", "1"),
                                sourceCrop = RectDto(
                                    "1", "2", "1", "2", "1", "2", "1", "2"),
                                destination = RectDto(
                                    "1", "2", "1", "2", "1", "2", "1", "2"),
                                interpolation = "hold",
                            },
                        },
                    },
                },
                evaluationTime = TimeDto("1", "1"),
            };

        internal object SubtitleTextOperation(
            string text,
            string? expectedRevisionDigest = null) =>
            new
            {
                kind = "editSubtitle",
                cueId = SubtitleHistory.CueId.ToString(),
                expectedRevisionId = SubtitleHistory.CurrentRevision.Id.ToString(),
                expectedRevisionDigest = expectedRevisionDigest ??
                    SubtitleHistory.CurrentRevision.ContentDigest.Hex,
                edit = new
                {
                    kind = "text",
                    text,
                },
                reason = "Edit subtitle text.",
            };

        internal object SubtitlePlacementOperation(object rect) =>
            new
            {
                kind = "editSubtitle",
                cueId = SubtitleHistory.CueId.ToString(),
                expectedRevisionId = SubtitleHistory.CurrentRevision.Id.ToString(),
                expectedRevisionDigest =
                    SubtitleHistory.CurrentRevision.ContentDigest.Hex,
                edit = new
                {
                    kind = "placement",
                    regionName = "subtitle",
                    rect,
                },
                reason = "Edit subtitle placement.",
            };

        internal object TeropTextOperation(string text)
        {
            EmphasisCueHistory history = TeropBindings.Single().History;
            return new
            {
                kind = "editTerop",
                cueId = history.CueId.ToString(),
                expectedRevisionId = history.CurrentRevision.Id.ToString(),
                expectedRevisionDigest = history.CurrentRevision.ContentDigest.Hex,
                edit = new
                {
                    kind = "text",
                    text,
                },
                reason = "Edit terop text.",
            };
        }

        internal async Task<JsonDocument> HandleAsync(BridgeRequest request) =>
            JsonDocument.Parse(await Handler.HandleAsync(request));

        private static BridgeRequest ParseRequest(string method, object parameters)
        {
            string message = JsonSerializer.Serialize(new
            {
                version = BridgeProtocol.Version,
                requestId = RequestId.ToString("D"),
                method,
                @params = parameters,
            });
            Assert.IsTrue(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                message,
                out BridgeRequest? request,
                out BridgeRequestRejection rejection),
                $"Request rejected as {rejection}: {message}");
            return request!;
        }

        private static TopicMetadataRevision CreateTopicMetadata(
            EntityId<ProjectIdTag> projectId,
            TranscriptRevision transcript,
            SourceSpan sourceSpan)
        {
            var project = new Project(
                projectId,
                "Layout bridge test",
                "Verify typed layout and cue editing.",
                "Reviewers",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            EntityId<ModelRunIdTag> modelRunId =
                Id<ModelRunIdTag>(0x81000030, 1);
            TopicBoundaryScanResult boundary =
                TopicBoundaryService.FindCandidates(
                    transcript,
                    [sourceSpan],
                    modelRunId);
            TopicRangeRevision topicRange =
                TopicBoundaryService.CreateTopicRange(
                    boundary,
                    Id<TopicRangeIdTag>(0x81000031, 1),
                    Id<TopicRangeRevisionIdTag>(0x81000032, 1),
                    startBoundarySuggestionId: null,
                    endBoundarySuggestionId: null,
                    Range(0, 2),
                    ActorSid,
                    CreatedAt,
                    "Create topic range.");
            return TopicMetadataService.CreateInitial(
                project,
                transcript,
                [sourceSpan],
                topicRange,
                speakerAssignments: null,
                topicOrdinal: 1,
                Id<TopicMetadataIdTag>(0x81000033, 1),
                Id<TopicMetadataRevisionIdTag>(0x81000034, 1),
                ActorSid,
                CreatedAt,
                "Create topic metadata.");
        }
    }
}
