using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Thumbnails;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ThumbnailBridgeTests
{
    private static readonly Guid RequestId = Guid.Parse(
        "00112233-4455-6677-8899-aabbccddeeff");

    private static readonly Guid SecondRequestId = Guid.Parse(
        "11223344-5566-4788-99aa-bbccddeeff00");

    private static readonly Guid GenerationId = Guid.Parse(
        "22334455-6677-4889-9aab-ccddeeff0011");

    [TestMethod]
    public async Task ReadResponse_PreservesRightsApprovalAndExactNegativeSourceRange()
    {
        Fixture fixture = new();
        var authorization = new AllowAuthorization();
        var handler = fixture.CreateHandler(
            authorization: authorization,
            generateAsync: static (_, _, _, _) =>
                throw new AssertFailedException("Generation was not expected."));

        string responseJson = await handler.HandleAsync(fixture.ReadRequest());

        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxThumbnailResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(responseJson));
        using JsonDocument document = JsonDocument.Parse(responseJson);
        JsonElement root = document.RootElement;
        AssertExactKeys(root, "version", "requestId", "method", "result");
        Assert.AreEqual(
            BridgeProtocol.ThumbnailReadMethod,
            root.GetProperty("method").GetString());
        JsonElement result = root.GetProperty("result");
        AssertExactKeys(result, "status", "snapshot");
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        JsonElement snapshot = result.GetProperty("snapshot");
        AssertRational(
            snapshot.GetProperty("sourcePresentationStart"),
            Time(-1, 10));
        AssertRational(
            snapshot.GetProperty("sourcePresentationEnd"),
            Time(1, 10));
        Assert.AreEqual(
            fixture.PlayerEvidenceDigest.Hex,
            snapshot.GetProperty("playerEvidenceDigest").GetString());
        Assert.AreEqual(
            "approved",
            snapshot.GetProperty("rights").GetProperty("status").GetString());
        Assert.IsTrue(snapshot
            .GetProperty("rights")
            .GetProperty("externalPublicationAllowed")
            .GetBoolean());
        JsonElement template = snapshot.GetProperty("templates")[0];
        Assert.AreEqual("brandApprover", template
            .GetProperty("approvalRole")
            .GetString());
        Assert.AreEqual(
            fixture.Approval.ApprovalDigest.Hex,
            template.GetProperty("approvalDigest").GetString());
        Assert.AreEqual(
            fixture.TemplateVersion.Reference.VersionDigest.Hex,
            template.GetProperty("templateVersionDigest").GetString());
        Assert.AreEqual(
            fixture.RenderProfile.ProfileDigest.Hex,
            template.GetProperty("renderProfileDigest").GetString());
        Assert.AreEqual(
            fixture.LayoutSpecDigest.Hex,
            template.GetProperty("layoutSpecDigest").GetString());
        Assert.AreEqual(1, authorization.ReadCount);
        Assert.AreEqual(0, authorization.GenerateCount);
        AssertPrivateDetailsAbsent(responseJson);
        Assert.IsFalse(responseJson.Contains("score", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains("recommend", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Generate_MapsOnlyExactProxySelectionAndSerializesVerifiedBindings()
    {
        Fixture fixture = new();
        ManualThumbnailRequest? observedRequest = null;
        ManualThumbnailContext? observedContext = null;
        CancellationToken observedCancellation = default;
        var authorization = new AllowAuthorization();
        var handler = fixture.CreateHandler(
            authorization: authorization,
            generateAsync: (request, context, timeout, cancellationToken) =>
            {
                observedRequest = request;
                observedContext = context;
                observedCancellation = cancellationToken;
                Assert.AreEqual(ProcessRunRequest.MaximumTimeout, timeout);
                return Task.FromResult(fixture.CreateResult(request));
            });

        string responseJson = await handler.HandleAsync(
            fixture.GenerateRequest(proxyFrameSequence: 1));

        Assert.IsNotNull(observedRequest);
        Assert.IsNotNull(observedContext);
        Assert.AreEqual(1, observedRequest.Selection.ProxyFrameSequence);
        Assert.AreEqual(1, observedRequest.Selection.SourceFrameSequence);
        Assert.AreEqual(
            Time(-1, 20),
            observedRequest.Selection.SourcePresentationTime);
        Assert.AreEqual(
            AllowAuthorization.ActorId,
            observedRequest.Selection.SelectedByActorId);
        Assert.IsTrue(observedCancellation.CanBeCanceled);
        Assert.AreSame(fixture.SourcePtsIndex, observedContext.SourcePtsIndex);
        Assert.AreSame(fixture.ProxyFrameMap, observedContext.ProxyFrameMap);
        Assert.AreSame(fixture.Approval, observedContext.TemplateApproval);
        Assert.AreEqual(1, authorization.GenerateCount);

        using JsonDocument document = JsonDocument.Parse(responseJson);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        JsonElement output = result.GetProperty("output");
        Assert.AreEqual(
            GenerationId.ToString("D"),
            output.GetProperty("generationId").GetString());
        AssertRational(
            output.GetProperty("selection").GetProperty("sourcePresentationTime"),
            Time(-1, 20));
        Assert.AreEqual(
            1,
            output.GetProperty("selection").GetProperty("sourceFrameSequence").GetInt32());
        Assert.AreEqual(
            fixture.OutputDigest.Hex,
            output.GetProperty("contentDigest").GetString());
        Assert.AreEqual(
            fixture.BindingDigest.Hex,
            output.GetProperty("bindingDigest").GetString());
        string mediaReference = output.GetProperty("mediaReference").GetString()!;
        Assert.IsTrue(PlayerMediaAssetReference.TryParse(
            mediaReference,
            out PlayerMediaAssetReference? parsed));
        Assert.AreEqual(PlayerMediaAssetKind.GeneratedThumbnail, parsed!.Kind);
        Assert.AreEqual(fixture.OutputDigest, parsed.ContentDigest);
        AssertPrivateDetailsAbsent(responseJson);
    }

    [TestMethod]
    public async Task AuthorizationPrecedesSourceAndTemplateLookup()
    {
        var reader = new CountingSourceReader();
        var templates = new CountingTemplateProvider();
        var handler = new ThumbnailBridgeRequestHandler(
            reader,
            templates,
            DeniedThumbnailAccessAuthorization.Instance,
            static (_, _, _, _) =>
                throw new AssertFailedException("Generation was not expected."));
        Fixture fixture = new();

        string readJson = await handler.HandleAsync(fixture.ReadRequest());
        string generationJson = await handler.HandleAsync(
            fixture.GenerateRequest(proxyFrameSequence: 1));

        Assert.AreEqual(0, reader.CallCount);
        Assert.AreEqual(0, templates.CallCount);
        AssertStatusAndNull(readJson, "forbidden", "snapshot");
        AssertStatusAndNull(generationJson, "forbidden", "output");
        AssertPrivateDetailsAbsent(readJson);
        AssertPrivateDetailsAbsent(generationJson);
    }

    [TestMethod]
    public async Task MissingTrustedApprovalProviderFailsClosedAsUnavailable()
    {
        Fixture fixture = new();
        var handler = new ThumbnailBridgeRequestHandler(
            fixture.SourceReader,
            new StaticApprovedThumbnailTemplateProvider([]),
            new AllowAuthorization(),
            static (_, _, _, _) =>
                throw new AssertFailedException("Generation was not expected."));

        string response = await handler.HandleAsync(fixture.ReadRequest());

        AssertStatusAndNull(response, "unavailable", "snapshot");
    }

    [TestMethod]
    public async Task StaleEvidenceAndExclusiveEndStopBeforeRendering()
    {
        Fixture fixture = new();
        int generationCount = 0;
        var handler = fixture.CreateHandler(
            generateAsync: (_, _, _, _) =>
            {
                generationCount++;
                throw new AssertFailedException("Stale evidence reached rendering.");
            });

        string staleThumbnail = await handler.HandleAsync(
            fixture.GenerateRequest(
                proxyFrameSequence: 1,
                evidenceDigest: Digest('f')));
        string stalePlayer = await handler.HandleAsync(
            fixture.GenerateRequest(
                proxyFrameSequence: 1,
                playerEvidenceDigest: Digest('e'),
                generationId: Guid.Parse(
                    "33445566-7788-499a-8bbc-ddeeff001122")));
        string missingTemplate = await handler.HandleAsync(
            fixture.GenerateRequest(
                proxyFrameSequence: 1,
                templateId: EntityId<TemplateIdTag>.New(),
                generationId: Guid.Parse(
                    "44556677-8899-4aab-8ccd-eeff00112233")));
        string invalidTime = await handler.HandleAsync(
            fixture.GenerateRequest(
                proxyFrameSequence: int.MaxValue,
                generationId: Guid.Parse(
                    "55667788-99aa-4bbc-8dde-ff0011223344")));
        string exclusiveEnd = await handler.HandleAsync(
            fixture.GenerateRequest(
                proxyFrameSequence: fixture.ProxyFrameMap.Entries.Count,
                generationId: Guid.Parse(
                    "66778899-aabb-4ccd-8eef-001122334455")));

        AssertStatusAndNull(staleThumbnail, "stale", "output");
        AssertStatusAndNull(stalePlayer, "stale", "output");
        AssertStatusAndNull(missingTemplate, "templateUnavailable", "output");
        AssertStatusAndNull(invalidTime, "invalidTime", "output");
        AssertStatusAndNull(exclusiveEnd, "invalidTime", "output");
        Assert.AreEqual(0, generationCount);
    }

    [TestMethod]
    [DataRow(RightsStatus.Approved, false, "approved")]
    [DataRow(RightsStatus.Unconfirmed, false, "unconfirmed")]
    [DataRow(RightsStatus.Rejected, false, "rejected")]
    public async Task CanonicalRightsWarningsAreVisibleAndHeldBeforeRendering(
        RightsStatus rightsStatus,
        bool externalPublicationAllowed,
        string expectedStatus)
    {
        Fixture fixture = new(rightsStatus, externalPublicationAllowed);
        int generationCount = 0;
        var handler = fixture.CreateHandler(
            generateAsync: (_, _, _, _) =>
            {
                generationCount++;
                throw new AssertFailedException(
                    "Rights-held generation reached rendering.");
            });

        string readResponse = await handler.HandleAsync(fixture.ReadRequest());
        using (JsonDocument document = JsonDocument.Parse(readResponse))
        {
            JsonElement rights = document.RootElement
                .GetProperty("result")
                .GetProperty("snapshot")
                .GetProperty("rights");
            Assert.AreEqual(
                expectedStatus,
                rights.GetProperty("status").GetString());
            Assert.AreEqual(
                externalPublicationAllowed,
                rights.GetProperty("externalPublicationAllowed").GetBoolean());
        }

        string generateResponse = await handler.HandleAsync(
            fixture.GenerateRequest(proxyFrameSequence: 1));

        AssertStatusAndNull(
            generateResponse,
            "rightsNotApproved",
            "output");
        Assert.AreEqual(0, generationCount);
    }

    [TestMethod]
    [DataRow(
        ManualThumbnailFailureReason.SelectedFrameInvalid,
        "invalidTime")]
    [DataRow(
        ManualThumbnailFailureReason.SourceRightsNotApproved,
        "rightsNotApproved")]
    [DataRow(
        ManualThumbnailFailureReason.TemplateNotApproved,
        "templateUnavailable")]
    [DataRow(
        ManualThumbnailFailureReason.SourceBindingStale,
        "stale")]
    [DataRow(
        ManualThumbnailFailureReason.ProfileBindingStale,
        "stale")]
    [DataRow(
        ManualThumbnailFailureReason.InputBindingMismatch,
        "mismatched")]
    [DataRow(
        ManualThumbnailFailureReason.DuplicateRequest,
        "conflict")]
    [DataRow(
        ManualThumbnailFailureReason.RegisteredSourceInvalid,
        "unavailable")]
    [DataRow(
        ManualThumbnailFailureReason.ProcessFailed,
        "failed")]
    public async Task RendererFailuresMapToClosedStableStatuses(
        ManualThumbnailFailureReason reason,
        string expectedStatus)
    {
        Fixture fixture = new();
        var handler = fixture.CreateHandler(
            generateAsync: (_, _, _, _) =>
                throw new ManualThumbnailException(reason));

        string response = await handler.HandleAsync(
            fixture.GenerateRequest(proxyFrameSequence: 1));

        AssertStatusAndNull(response, expectedStatus, "output");
        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task SameGenerationIdentityCoalescesAndConflictingReuseIsRejected()
    {
        Fixture fixture = new();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;
        var handler = fixture.CreateHandler(
            generateAsync: async (request, _, _, cancellationToken) =>
            {
                Interlocked.Increment(ref executionCount);
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return fixture.CreateResult(request);
            });

        Task<string> first = handler.HandleAsync(
            fixture.GenerateRequest(RequestId, proxyFrameSequence: 1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<string> same = handler.HandleAsync(
            fixture.GenerateRequest(SecondRequestId, proxyFrameSequence: 1));
        string conflict = await handler.HandleAsync(
            fixture.GenerateRequest(
                Guid.Parse("33445566-7788-499a-8bbc-ddeeff001122"),
                proxyFrameSequence: 2));
        AssertStatusAndNull(conflict, "conflict", "output");

        release.TrySetResult();
        string firstResponse = await first;
        string sameResponse = await same;

        Assert.AreEqual(1, executionCount);
        AssertResponseCorrelation(firstResponse, RequestId);
        AssertResponseCorrelation(sameResponse, SecondRequestId);
        Assert.AreEqual("available", GetStatus(firstResponse));
        Assert.AreEqual("available", GetStatus(sameResponse));
    }

    [TestMethod]
    public async Task CompletedGenerationReusesNativeSelectionTimeForR023Replay()
    {
        Fixture fixture = new();
        DateTimeOffset firstRequestTime = new(
            2042,
            3,
            4,
            5,
            6,
            7,
            TimeSpan.Zero);
        DateTimeOffset retryTime = firstRequestTime.AddMinutes(1);
        var timeProvider = new SequenceTimeProvider(
            firstRequestTime,
            retryTime);
        var selectedTimes = new List<DateTimeOffset>();
        int executionCount = 0;
        var handler = fixture.CreateHandler(
            generateAsync: (request, _, _, _) =>
            {
                selectedTimes.Add(request.Selection.SelectedAt);
                executionCount++;
                return Task.FromResult(fixture.CreateResult(
                    request,
                    wasAlreadyRegistered: executionCount > 1));
            },
            timeProvider: timeProvider);

        string first = await handler.HandleAsync(
            fixture.GenerateRequest(RequestId, proxyFrameSequence: 1));
        string replay = await handler.HandleAsync(
            fixture.GenerateRequest(SecondRequestId, proxyFrameSequence: 1));
        string cancelAfterCompletion = await handler.HandleAsync(
            fixture.CancelRequest(
                Guid.Parse("33445566-7788-499a-8bbc-ddeeff001122")));

        Assert.AreEqual(2, executionCount);
        CollectionAssert.AreEqual(
            new[] { firstRequestTime, firstRequestTime },
            selectedTimes);
        Assert.AreEqual("available", GetStatus(first));
        Assert.AreEqual("available", GetStatus(replay));
        using JsonDocument replayDocument = JsonDocument.Parse(replay);
        Assert.IsTrue(replayDocument.RootElement
            .GetProperty("result")
            .GetProperty("output")
            .GetProperty("wasAlreadyRegistered")
            .GetBoolean());
        AssertCancelOutcome(cancelAfterCompletion, "notRunning");
    }

    [TestMethod]
    public async Task DistinctGenerationIdentitiesHaveNoInventedSessionQuota()
    {
        Fixture fixture = new();
        int executionCount = 0;
        var handler = fixture.CreateHandler(
            generateAsync: (request, _, _, _) =>
            {
                executionCount++;
                return Task.FromResult(fixture.CreateResult(request));
            });

        const int generationCount = 65;
        for (int index = 1; index <= generationCount; index++)
        {
            Guid requestId = Guid.Parse(string.Create(
                CultureInfo.InvariantCulture,
                $"33445566-7788-499a-8bbc-{index:000000000000}"));
            Guid generationId = Guid.Parse(string.Create(
                CultureInfo.InvariantCulture,
                $"44556677-8899-4aab-8ccd-{index:000000000000}"));
            string response = await handler.HandleAsync(
                fixture.GenerateRequest(
                    requestId,
                    proxyFrameSequence: 1,
                    generationId: generationId));

            Assert.AreEqual("available", GetStatus(response));
        }

        Assert.AreEqual(generationCount, executionCount);
    }

    [TestMethod]
    public async Task CancellationPropagatesToRendererAndIsRaceSafe()
    {
        Fixture fixture = new();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var handler = fixture.CreateHandler(
            generateAsync: async (_, _, _, cancellationToken) =>
            {
                observedToken = cancellationToken;
                started.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await finishCancellation.Task;
                    throw;
                }

                throw new AssertFailedException(
                    "Cancelled generation continued.");
            });

        Task<string> generation = handler.HandleAsync(
            fixture.GenerateRequest(proxyFrameSequence: 1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(observedToken.CanBeCanceled);

        string firstCancel = await handler.HandleAsync(
            fixture.CancelRequest(SecondRequestId));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string secondCancel = await handler.HandleAsync(
            fixture.CancelRequest(
                Guid.Parse("33445566-7788-499a-8bbc-ddeeff001122")));
        finishCancellation.TrySetResult();
        string generationResponse = await generation;
        string afterCompletion = await handler.HandleAsync(
            fixture.CancelRequest(
                Guid.Parse("44556677-8899-4aab-8ccd-eeff00112233")));
        string missing = await handler.HandleAsync(
            fixture.CancelRequest(
                Guid.Parse("55667788-99aa-4bbc-8dde-ff0011223344"),
                generationId: Guid.Parse(
                    "66778899-aabb-4ccd-8eef-001122334455")));

        AssertCancelOutcome(firstCancel, "requested");
        AssertCancelOutcome(secondCancel, "alreadyRequested");
        AssertStatusAndNull(generationResponse, "cancelled", "output");
        AssertCancelOutcome(afterCompletion, "notRunning");
        AssertCancelOutcome(missing, "notFound");
        Assert.IsTrue(observedToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task CancelledLifetimeBeforeGenerationStartsDoesNotReadOrRender()
    {
        var reader = new CountingSourceReader();
        var templates = new CountingTemplateProvider();
        int generationCount = 0;
        var handler = new ThumbnailBridgeRequestHandler(
            reader,
            templates,
            new AllowAuthorization(),
            (_, _, _, _) =>
            {
                generationCount++;
                throw new AssertFailedException(
                    "Cancelled request reached rendering.");
            });
        Fixture fixture = new();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => handler.HandleAsync(
                fixture.GenerateRequest(proxyFrameSequence: 1),
                cancellation.Token));

        Assert.AreEqual(0, reader.CallCount);
        Assert.AreEqual(0, templates.CallCount);
        Assert.AreEqual(0, generationCount);
    }

    [TestMethod]
    public async Task PrivateReaderFailureReturnsOnlyUnavailableStatus()
    {
        var handler = new ThumbnailBridgeRequestHandler(
            new ThrowingSourceReader(),
            new ThrowingTemplateProvider(),
            new AllowAuthorization(),
            static (_, _, _, _) =>
                throw new AssertFailedException("Generation was not expected."));
        Fixture fixture = new();

        string response = await handler.HandleAsync(fixture.ReadRequest());

        AssertStatusAndNull(response, "unavailable", "snapshot");
        AssertPrivateDetailsAbsent(response);
    }

    private static void AssertCancelOutcome(
        string responseJson,
        string expectedOutcome)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        JsonElement result = document.RootElement.GetProperty("result");
        AssertExactKeys(result, "status", "outcome");
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        Assert.AreEqual(
            expectedOutcome,
            result.GetProperty("outcome").GetString());
    }

    private static void AssertStatusAndNull(
        string responseJson,
        string expectedStatus,
        string payloadName)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        JsonElement result = document.RootElement.GetProperty("result");
        AssertExactKeys(result, "status", payloadName);
        Assert.AreEqual(
            expectedStatus,
            result.GetProperty("status").GetString());
        Assert.AreEqual(
            JsonValueKind.Null,
            result.GetProperty(payloadName).ValueKind);
    }

    private static string GetStatus(string responseJson)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        return document.RootElement
            .GetProperty("result")
            .GetProperty("status")
            .GetString()!;
    }

    private static void AssertResponseCorrelation(
        string responseJson,
        Guid requestId)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        Assert.AreEqual(
            requestId.ToString("D"),
            document.RootElement.GetProperty("requestId").GetString());
    }

    private static void AssertPrivateDetailsAbsent(string responseJson)
    {
        Assert.IsFalse(responseJson.Contains(
            @"C:\private",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains(
            "file://",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains(
            AllowAuthorization.ActorId,
            StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains(
            "Exception",
            StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRational(
        JsonElement element,
        RationalTime expected)
    {
        AssertExactKeys(element, "value", "rate");
        Assert.AreEqual(
            expected.Value.ToString(CultureInfo.InvariantCulture),
            element.GetProperty("value").GetString());
        Assert.AreEqual(
            expected.Rate.ToString(CultureInfo.InvariantCulture),
            element.GetProperty("rate").GetString());
    }

    private static void AssertExactKeys(
        JsonElement element,
        params string[] expected)
    {
        CollectionAssert.AreEquivalent(
            expected,
            element.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
    }

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));

    private sealed class Fixture
    {
        private static readonly DateTimeOffset CreatedAt =
            new(2041, 2, 3, 4, 5, 6, TimeSpan.Zero);

        internal Fixture(
            RightsStatus rightsStatus = RightsStatus.Approved,
            bool externalPublicationAllowed = true)
        {
            ProjectId = EntityId<ProjectIdTag>.Parse(
                "77777777-7777-4777-8777-777777777777");
            JobId = EntityId<JobIdTag>.Parse(
                "88888888-8888-4888-8888-888888888888");
            SourceArtifactId = EntityId<ArtifactIdTag>.Parse(
                "99999999-9999-4999-8999-999999999999");
            SourceAssetId = EntityId<AssetIdTag>.Parse(
                "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
            SourceDigest = Digest('1');
            PlayerEvidenceDigest = Digest('2');
            SourceAsset = new Asset(
                SourceAssetId,
                "source.mov",
                "video/quicktime",
                SourceDigest,
                byteSize: 4096,
                new RightsRecord(
                    rightsStatus,
                    "evidence://source-rights/approved",
                    "Recorded source rights for thumbnail publication.",
                    externalPublicationAllowed));
            Project = new Project(
                ProjectId,
                "Manual thumbnail bridge",
                "Verify exact frame selection.",
                "External audience",
                PublicationScope.ExternalPublicationApproved,
                deadline: null,
                assets: [SourceAsset],
                performers: []);
            SourceArtifact = new StoredArtifact(
                SourceArtifactId,
                ProjectId,
                ArtifactKind.Original,
                string.Concat(
                    "projects/",
                    ProjectId.ToString(),
                    "/original/source.mov"),
                SourceDigest,
                SourceAsset.ByteSize,
                CreatedAt);
            SourcePtsIndex = new PtsIndex(
                SourceAssetId,
                ProjectId,
                JobId,
                SourceAsset.FileName,
                SourceDigest,
                SourceAsset.ByteSize,
                streamIndex: 2,
                Time(1, 100),
                [
                    new PtsIndexEntry(0, Time(-1, 10), Time(1, 20), keyFrame: true),
                    new PtsIndexEntry(1, Time(-1, 20), Time(1, 20), keyFrame: false),
                    new PtsIndexEntry(2, Time(0), Time(1, 10), keyFrame: true),
                ]);
            SourcePtsDigest = ManualThumbnailHash.ComputeCanonicalJsonDigest(
                SourcePtsIndex.CanonicalJson);
            ProxyFrameMap = ProxyFrameMap.Create(
                SourceAssetId,
                ProjectId,
                SourceDigest,
                SourcePtsDigest,
                Digest('3'),
                Digest('4'),
                SourcePtsIndex.Entries
                    .Select(static entry => entry.PresentationTime)
                    .ToArray(),
                SourcePtsIndex.PresentationEnd,
                SourcePtsIndex.Entries
                    .Select(static entry => entry.PresentationTime)
                    .ToArray());
            SourceContext = new ThumbnailSourceContext(
                Project,
                SourceArtifact,
                SourceAsset,
                SourcePtsIndex,
                ProxyFrameMap,
                PlayerEvidenceDigest);
            SourceReader = new StubSourceReader(SourceContext);

            LayoutSpec = new LayoutSpec(
                EntityId<LayoutSpecIdTag>.Parse(
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
                16,
                9,
                [
                    new LayoutRegion(
                        "video",
                        new NormalizedRect(
                            new RationalNumber(0, 1),
                            new RationalNumber(0, 1),
                            new RationalNumber(1, 1),
                            new RationalNumber(1, 1)),
                        LayoutRegionRole.Video),
                ],
                safeZones: []);
            LayoutSpecDigest = Digest('5');
            TemplateVersion = new BrandTemplateVersion(
                EntityId<SeriesIdTag>.Parse(
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
                new TemplateReference(
                    EntityId<TemplateIdTag>.Parse(
                        "dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
                    "Approved exact thumbnail",
                    Digest('6')),
                BrandTemplateKind.Thumbnail,
                parameterDefinitions: []);
            AppliedTemplate = TemplateVersion.ApplyEditorOverrides([]);
            Approval = new ApprovedThumbnailTemplateSnapshot(
                EntityId<ThumbnailTemplateApprovalIdTag>.Parse(
                    "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"),
                TemplateVersion,
                "brand-approver",
                CreatedAt.AddMinutes(10),
                "Approve this exact thumbnail template.",
                "evidence://thumbnail-template/approved");
            RenderProfile = new RenderProfile(
                EntityId<RenderProfileIdTag>.Parse(
                    "ffffffff-ffff-4fff-8fff-ffffffffffff"),
                "Verified PNG",
                Digest('7'),
                width: 16,
                height: 9,
                frameRateNumerator: 30000,
                frameRateDenominator: 1001,
                LayoutSpec.Id,
                TemplateVersion.Reference.Id,
                TemplateVersion.Reference.VersionDigest,
                "png",
                "none",
                subtitleBurnIn: false);
            TemplateContext = new ApprovedThumbnailTemplateContext(
                Approval,
                AppliedTemplate,
                RenderProfile,
                LayoutSpec,
                LayoutSpecDigest);
            Snapshot = ThumbnailBridgeSnapshot.Create(
                SourceContext,
                [TemplateContext]);
            OutputDigest = Digest('8');
            BindingDigest = Digest('9');
        }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal EntityId<JobIdTag> JobId { get; }

        internal EntityId<ArtifactIdTag> SourceArtifactId { get; }

        internal EntityId<AssetIdTag> SourceAssetId { get; }

        internal ContentDigest SourceDigest { get; }

        internal ContentDigest PlayerEvidenceDigest { get; }

        internal Asset SourceAsset { get; }

        internal Project Project { get; }

        internal StoredArtifact SourceArtifact { get; }

        internal PtsIndex SourcePtsIndex { get; }

        internal ContentDigest SourcePtsDigest { get; }

        internal ProxyFrameMap ProxyFrameMap { get; }

        internal ThumbnailSourceContext SourceContext { get; }

        internal StubSourceReader SourceReader { get; }

        internal LayoutSpec LayoutSpec { get; }

        internal ContentDigest LayoutSpecDigest { get; }

        internal BrandTemplateVersion TemplateVersion { get; }

        internal AppliedBrandTemplate AppliedTemplate { get; }

        internal ApprovedThumbnailTemplateSnapshot Approval { get; }

        internal RenderProfile RenderProfile { get; }

        internal ApprovedThumbnailTemplateContext TemplateContext { get; }

        internal ThumbnailBridgeSnapshot Snapshot { get; }

        internal ContentDigest OutputDigest { get; }

        internal ContentDigest BindingDigest { get; }

        internal ThumbnailBridgeRequestHandler CreateHandler(
            IThumbnailAccessAuthorization? authorization = null,
            Func<
                ManualThumbnailRequest,
                ManualThumbnailContext,
                TimeSpan,
                CancellationToken,
                Task<ManualThumbnailResult>>? generateAsync = null,
            TimeProvider? timeProvider = null) =>
            new(
                SourceReader,
                new StaticApprovedThumbnailTemplateProvider([TemplateContext]),
                authorization ?? new AllowAuthorization(),
                generateAsync ?? ((request, _, _, _) =>
                    Task.FromResult(CreateResult(request))),
                timeProvider: timeProvider ??
                    new FixedTimeProvider(CreatedAt.AddMinutes(20)));

        internal BridgeRequest ReadRequest(Guid? requestId = null) =>
            BridgeRequest.Create(
                requestId ?? RequestId,
                BridgeMethod.ThumbnailRead,
                JsonSerializer.SerializeToElement(new { }),
                JobId);

        internal BridgeRequest GenerateRequest(
            int proxyFrameSequence,
            ContentDigest? evidenceDigest = null,
            ContentDigest? playerEvidenceDigest = null,
            EntityId<TemplateIdTag>? templateId = null,
            Guid? generationId = null) =>
            GenerateRequest(
                RequestId,
                proxyFrameSequence,
                evidenceDigest,
                playerEvidenceDigest,
                templateId,
                generationId);

        internal BridgeRequest GenerateRequest(
            Guid requestId,
            int proxyFrameSequence,
            ContentDigest? evidenceDigest = null,
            ContentDigest? playerEvidenceDigest = null,
            EntityId<TemplateIdTag>? templateId = null,
            Guid? generationId = null) =>
            BridgeRequest.Create(
                requestId,
                BridgeMethod.ThumbnailGenerate,
                JsonSerializer.SerializeToElement(new { }),
                JobId,
                evidenceDigest: evidenceDigest ?? Snapshot.EvidenceDigest,
                proxyFrameSequence: proxyFrameSequence,
                generationId: generationId ?? GenerationId,
                playerEvidenceDigest:
                    playerEvidenceDigest ?? PlayerEvidenceDigest,
                templateId: templateId ?? TemplateVersion.Reference.Id);

        internal BridgeRequest CancelRequest(
            Guid requestId,
            Guid? generationId = null) =>
            BridgeRequest.Create(
                requestId,
                BridgeMethod.ThumbnailCancel,
                JsonSerializer.SerializeToElement(new { }),
                generationId: generationId ?? GenerationId);

        internal ManualThumbnailResult CreateResult(
            ManualThumbnailRequest request,
            bool wasAlreadyRegistered = false)
        {
            var artifact = new StoredArtifact(
                request.OutputArtifactId,
                request.Source.ProjectId,
                ArtifactKind.Render,
                string.Concat(
                    "projects/",
                    request.Source.ProjectId.ToString(),
                    "/renders/thumbnails/",
                    request.OutputArtifactId.ToString(),
                    "/",
                    BindingDigest.Hex,
                    ".png"),
                OutputDigest,
                byteSize: 2048,
                CreatedAt.AddHours(1));
            return new ManualThumbnailResult(
                request,
                artifact,
                SourceAsset,
                SourcePtsIndex.StreamIndex,
                Approval,
                OriginRef.FromAsset(SourceAsset.Id),
                BindingDigest,
                RenderProfile.Width,
                RenderProfile.Height,
                wasAlreadyRegistered);
        }
    }

    private sealed class StubSourceReader(ThumbnailSourceContext context) :
        IThumbnailSourceReader
    {
        public Task<ThumbnailSourceReadResult> ReadAsync(
            EntityId<JobIdTag> jobId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                jobId == context.SourcePtsIndex.JobId
                    ? ThumbnailSourceReadResult.Available(context)
                    : ThumbnailSourceReadResult.Unavailable());
        }
    }

    private sealed class CountingSourceReader : IThumbnailSourceReader
    {
        internal int CallCount { get; private set; }

        public Task<ThumbnailSourceReadResult> ReadAsync(
            EntityId<JobIdTag> jobId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(ThumbnailSourceReadResult.Unavailable());
        }
    }

    private sealed class ThrowingSourceReader : IThumbnailSourceReader
    {
        public Task<ThumbnailSourceReadResult> ReadAsync(
            EntityId<JobIdTag> jobId,
            CancellationToken cancellationToken) =>
            throw new IOException(@"C:\private\source.mov");
    }

    private sealed class CountingTemplateProvider :
        IApprovedThumbnailTemplateProvider
    {
        internal int CallCount { get; private set; }

        public Task<IReadOnlyList<ApprovedThumbnailTemplateContext>> ReadAsync(
            Project project,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<ApprovedThumbnailTemplateContext>>(
                []);
        }
    }

    private sealed class ThrowingTemplateProvider :
        IApprovedThumbnailTemplateProvider
    {
        public Task<IReadOnlyList<ApprovedThumbnailTemplateContext>> ReadAsync(
            Project project,
            CancellationToken cancellationToken) =>
            throw new IOException(@"C:\private\templates\approval.json");
    }

    private sealed class AllowAuthorization : IThumbnailAccessAuthorization
    {
        internal const string ActorId =
            "S-1-5-21-1111111111-2222222222-3333333333-1001";

        internal int ReadCount { get; private set; }

        internal int GenerateCount { get; private set; }

        public void DemandRead()
        {
            ReadCount++;
        }

        public string DemandGenerate()
        {
            GenerateCount++;
            return ActorId;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class SequenceTimeProvider(
        params DateTimeOffset[] values) : TimeProvider
    {
        private int nextIndex;

        public override DateTimeOffset GetUtcNow()
        {
            int index = Interlocked.Increment(ref nextIndex) - 1;
            return values[Math.Min(index, values.Length - 1)];
        }
    }
}
