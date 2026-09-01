using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Delivery;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class DeliveryBridgeTests
{
    private static readonly Guid RequestId = Guid.Parse(
        "00112233-4455-4677-8899-aabbccddeeff");

    [TestMethod]
    public async Task AuthorizationPrecedesProviderAccess()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        var provider = new CountingProvider(
            DeliveryEvidenceProviderResult.Unavailable());
        var handler = new DeliveryBridgeRequestHandler(
            provider,
            DeniedDeliveryAccessAuthorization.Instance);

        DeliveryBridgeReadResult result = await handler.ReadAsync(
            fixture.Version.Id,
            outputTime: null,
            CancellationToken.None);

        Assert.AreEqual(DeliveryBridgeStatus.Forbidden, result.Status);
        Assert.IsNull(result.Snapshot);
        Assert.IsNull(result.Trace);
        Assert.AreEqual(0, provider.CallCount);
    }

    [TestMethod]
    public async Task MissingProductionProviderFailsClosedAsUnavailable()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        var handler = new DeliveryBridgeRequestHandler(
            UnavailableDeliveryEvidenceProvider.Instance,
            new AllowAuthorization());

        DeliveryBridgeReadResult result = await handler.ReadAsync(
            fixture.Version.Id,
            outputTime: null,
            CancellationToken.None);

        Assert.AreEqual(DeliveryBridgeStatus.Unavailable, result.Status);
        Assert.IsNull(result.Snapshot);
        Assert.IsNull(result.Trace);
    }

    [TestMethod]
    public async Task QualityControlFailureIsExplicitAndNeverReturnsSnapshot()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        var provider = new CountingProvider(
            DeliveryEvidenceProviderResult.QualityControlFailed());
        var handler = new DeliveryBridgeRequestHandler(
            provider,
            new AllowAuthorization());

        string response = await handler.HandleAsync(
            CreateRequest(fixture.Version.Id, outputTime: null),
            CancellationToken.None);

        AssertFailure(response, "qualityControlFailed");
        Assert.AreEqual(1, provider.CallCount);
        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task AvailableResponseShowsCompleteEvidenceAndExactTraceWithoutPrivateData()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        Project internalProject = new(
            fixture.Project.Id,
            fixture.Project.ContentName,
            fixture.Project.Purpose,
            fixture.Project.Audience,
            PublicationScope.InternalOnly,
            fixture.Project.Deadline,
            fixture.Project.Assets,
            fixture.Project.Performers);
        RenderGraphValidationContext validation = CreateValidationContext(
            fixture,
            internalProject,
            [fixture.UnusedSourceSpan, fixture.UsedSourceSpan]);
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(
            fixture,
            validationContext: validation);
        var handler = CreateHandler(context);

        string response = await handler.HandleAsync(
            CreateRequest(fixture.Version.Id, new RationalTime(1, 2)),
            CancellationToken.None);

        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxDeliveryResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(response));
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        AssertExactKeys(root, "version", "requestId", "method", "result");
        Assert.AreEqual(
            BridgeProtocol.DeliveryReadMethod,
            root.GetProperty("method").GetString());
        JsonElement result = root.GetProperty("result");
        AssertExactKeys(result, "status", "snapshot", "trace");
        Assert.AreEqual("available", result.GetProperty("status").GetString());

        JsonElement snapshot = result.GetProperty("snapshot");
        Assert.AreEqual(
            fixture.Version.ContentDigest.Hex,
            snapshot.GetProperty("versionDigest").GetString());
        Assert.AreEqual(
            context.Delivery.EvidenceDigest.Hex,
            snapshot.GetProperty("deliveryEvidenceDigest").GetString());
        Assert.AreEqual(
            context.Delivery.UsedSourceSpansDigest.Hex,
            snapshot.GetProperty("usedSourceSpansDigest").GetString());
        Assert.AreEqual(
            context.Delivery.ApprovalEvidenceDigest.Hex,
            snapshot.GetProperty("approvalEvidenceDigest").GetString());
        Assert.AreEqual(
            "internalOnly",
            snapshot.GetProperty("publicationScope").GetString());

        JsonElement artifacts = snapshot.GetProperty("artifacts");
        Assert.AreEqual(2, artifacts.GetArrayLength());
        Assert.AreEqual(
            context.Delivery.Manifest.Artifacts[0].Sha256.Hex,
            artifacts[0].GetProperty("sha256").GetString());
        Assert.AreEqual(
            context.Delivery.Manifest.Artifacts[1].Sha256.Hex,
            artifacts[1].GetProperty("sha256").GetString());
        Assert.IsTrue(artifacts.EnumerateArray().All(artifact =>
            artifact.GetProperty("sha256").GetString()!.Length == 64));

        JsonElement review = snapshot.GetProperty("review");
        Assert.AreEqual(1, review.GetProperty("records").GetArrayLength());
        Assert.AreEqual(1, review.GetProperty("approvals").GetArrayLength());
        Assert.AreEqual(
            "contentApprover",
            review.GetProperty("approvals")[0]
                .GetProperty("actorRole")
                .GetString());

        JsonElement rights = snapshot.GetProperty("rights")[0];
        Assert.AreEqual("approved", rights.GetProperty("status").GetString());
        Assert.IsTrue(rights.GetProperty("evidenceRecorded").GetBoolean());
        Assert.IsTrue(
            rights.GetProperty("externalPublicationAllowed").GetBoolean());

        JsonElement qc = snapshot.GetProperty("qualityControl");
        Assert.IsTrue(qc.GetProperty("machineChecksPassed").GetBoolean());
        Assert.IsTrue(qc.GetProperty("humanReviewRequired").GetBoolean());
        Assert.IsFalse(
            qc.GetProperty("approvedNumericThresholdsApplied").GetBoolean());
        Assert.IsFalse(qc.GetProperty("canProceedToDelivery").GetBoolean());
        Assert.AreEqual(
            fixture.QualityControl.ContentDigest.Hex,
            qc.GetProperty("sha256").GetString());
        Assert.AreEqual(
            fixture.QualityControl.ProbeEvidenceDigest.Hex,
            qc.GetProperty("probeEvidenceDigest").GetString());

        JsonElement trace = result.GetProperty("trace");
        Assert.AreEqual("resolved", trace.GetProperty("status").GetString());
        Assert.AreEqual("sourceSpan", trace.GetProperty("originKind").GetString());
        AssertRational(
            trace.GetProperty("outputTime"),
            new RationalTime(1, 2));
        AssertRational(
            trace.GetProperty("sourceTime"),
            new RationalTime(1, 2));
        Assert.AreEqual(
            fixture.UsedSourceSpan.Id.ToString(),
            trace.GetProperty("sourceSpan")
                .GetProperty("sourceSpanId")
                .GetString());

        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task ArtifactProviderOrderDoesNotChangeCanonicalResponse()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        EntityId<ReviewRecordIdTag> reviewRecordId =
            EntityId<ReviewRecordIdTag>.New();
        DeliveryEvidenceContext declaredOrder =
            await CreateEvidenceContextAsync(
                fixture,
                reviewRecordId: reviewRecordId);
        DeliveryEvidenceContext reverseProviderOrder =
            await CreateEvidenceContextAsync(
                fixture,
                artifactEvidence: fixture.ArtifactEvidence.Reverse().ToArray(),
                reviewRecordId: reviewRecordId);

        string declaredResponse = await CreateHandler(declaredOrder).HandleAsync(
            CreateRequest(fixture.Version.Id, outputTime: null),
            CancellationToken.None);
        string reversedResponse = await CreateHandler(
            reverseProviderOrder).HandleAsync(
                CreateRequest(fixture.Version.Id, outputTime: null),
                CancellationToken.None);

        Assert.AreEqual(declaredResponse, reversedResponse);
    }

    [TestMethod]
    public async Task ExclusiveOutputEndReturnsNotFoundWithoutInventingOrigin()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(fixture);
        var handler = CreateHandler(context);

        string response = await handler.HandleAsync(
            CreateRequest(fixture.Version.Id, new RationalTime(1, 1)),
            CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement trace = document.RootElement
            .GetProperty("result")
            .GetProperty("trace");
        Assert.AreEqual("notFound", trace.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, trace.GetProperty("originKind").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, trace.GetProperty("originId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, trace.GetProperty("sourceTime").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, trace.GetProperty("sourceSpan").ValueKind);
    }

    [TestMethod]
    public async Task MissingArtifactReturnsIncompleteWithoutPartialEvidence()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(
            fixture,
            artifactEvidence: [fixture.ArtifactEvidence[0]]);
        var handler = CreateHandler(context);

        string response = await handler.HandleAsync(
            CreateRequest(fixture.Version.Id, outputTime: null),
            CancellationToken.None);

        AssertFailure(response, "incomplete");
        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task WrongArtifactDigestReturnsMismatchedWithoutPartialEvidence()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        ManifestArtifact original = fixture.VideoManifestArtifact;
        var wrongDigest = new ManifestArtifact(
            original.ArtifactType,
            original.MediaType,
            original.Language,
            original.ByteSize,
            Digest('f'),
            original.StorageReference);
        KeyValuePair<EntityId<ArtifactIdTag>, ManifestArtifact>[] bindings =
        [
            new(fixture.VideoArtifactId, wrongDigest),
            fixture.ArtifactEvidence[1],
        ];
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(
            fixture,
            artifactEvidence: bindings);
        var handler = CreateHandler(context);

        string response = await handler.HandleAsync(
            CreateRequest(fixture.Version.Id, outputTime: null),
            CancellationToken.None);

        AssertFailure(response, "mismatched");
        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task BrokenTraceEvidenceReturnsMismatchedInsteadOfFalseSuccess()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        SourceSpan original = fixture.UsedSourceSpan;
        var broken = new SourceSpan(
            original.Id,
            original.AssetId,
            original.StreamId,
            original.Range,
            Digest('f'),
            original.TranscriptRevisionId,
            original.TranscriptTokenIds);
        RenderGraphValidationContext validation = CreateValidationContext(
            fixture,
            fixture.Project,
            [fixture.UnusedSourceSpan, broken]);
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(
            fixture,
            validationContext: validation);
        var handler = CreateHandler(context);

        string response = await handler.HandleAsync(
            CreateRequest(fixture.Version.Id, new RationalTime(1, 2)),
            CancellationToken.None);

        AssertFailure(response, "mismatched");
        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task NonApproverRoleCannotCrossAsApprovalEvidence()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        var invalidApproval = new StoredApproval(
            fixture.Approval.Id,
            fixture.Approval.ProjectId,
            fixture.Approval.CompositionVersionId,
            fixture.Approval.CompositionVersionDigest,
            fixture.Approval.ActorId,
            ReviewActorRole.Editor,
            fixture.Approval.ApprovedAt,
            fixture.Approval.Reason);
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(
            fixture,
            approvals: [invalidApproval]);

        string response = await CreateHandler(context).HandleAsync(
            CreateRequest(fixture.Version.Id, outputTime: null),
            CancellationToken.None);

        AssertFailure(response, "mismatched");
        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task TamperedAggregateEvidenceDigestFailsClosed()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        DeliveryCreationResult valid = await fixture.CreateAsync();
        var tampered = new DeliveryCreationResult(
            valid.Manifest,
            valid.UsedSourceSpans,
            valid.UsedSourceSpansDigest,
            valid.ApprovalEvidenceDigest,
            Digest('f'));
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(
            fixture,
            delivery: tampered);

        string response = await CreateHandler(context).HandleAsync(
            CreateRequest(fixture.Version.Id, outputTime: null),
            CancellationToken.None);

        AssertFailure(response, "mismatched");
        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task MissingRegisteredArtifactMetadataFailsClosed()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(
            fixture,
            registeredArtifacts:
            [
                fixture.RegisteredArtifacts[fixture.VideoArtifactId],
            ]);

        string response = await CreateHandler(context).HandleAsync(
            CreateRequest(fixture.Version.Id, outputTime: null),
            CancellationToken.None);

        AssertFailure(response, "incomplete");
        AssertPrivateDetailsAbsent(response);
    }

    [TestMethod]
    public async Task OversizedAvailableResponseBecomesBoundedClosedFailure()
    {
        DeliveryServiceTests.DeliveryFixture fixture = new();
        DeliveryEvidenceContext context = await CreateEvidenceContextAsync(fixture);
        DeliveryBridgeReadResult valid = await CreateHandler(context).ReadAsync(
            fixture.Version.Id,
            outputTime: null,
            CancellationToken.None);
        Assert.IsNotNull(valid.Snapshot);
        DeliveryArtifactEvidence oversizedArtifact =
            valid.Snapshot.Artifacts[0] with
            {
                ArtifactType = new string('x', 4096),
            };
        DeliveryBridgeSnapshot oversizedSnapshot = valid.Snapshot with
        {
            Artifacts = Array.AsReadOnly(
                Enumerable.Repeat(oversizedArtifact, 1024).ToArray()),
        };

        string response = DeliveryBridgeJson.WriteResponse(
            CreateRequest(fixture.Version.Id, outputTime: null),
            DeliveryBridgeReadResult.Available(
                oversizedSnapshot,
                trace: null));

        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxDeliveryResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(response));
        AssertFailure(response, "mismatched");
        AssertPrivateDetailsAbsent(response);
    }

    private static DeliveryBridgeRequestHandler CreateHandler(
        DeliveryEvidenceContext context) =>
        new(
            new CountingProvider(
                DeliveryEvidenceProviderResult.Available(context)),
            new AllowAuthorization());

    private static async Task<DeliveryEvidenceContext> CreateEvidenceContextAsync(
        DeliveryServiceTests.DeliveryFixture fixture,
        RenderGraphValidationContext? validationContext = null,
        IReadOnlyList<KeyValuePair<
            EntityId<ArtifactIdTag>,
            ManifestArtifact>>? artifactEvidence = null,
        IReadOnlyList<StoredArtifact>? registeredArtifacts = null,
        IReadOnlyList<StoredApproval>? approvals = null,
        DeliveryCreationResult? delivery = null,
        EntityId<ReviewRecordIdTag>? reviewRecordId = null)
    {
        var reviewRecord = new StoredReviewRecord(
            reviewRecordId ?? EntityId<ReviewRecordIdTag>.New(),
            fixture.Project.Id,
            fixture.Version.Id,
            fixture.Version.ContentDigest,
            ReviewState.Approved,
            "S-1-5-21-private-reviewer",
            ReviewActorRole.ContentApprover,
            fixture.CreatedAt.AddSeconds(30),
            "Private review reason must not cross the bridge.");
        var reviewEvidence = new StoredVersionEvidence(
            fixture.Version.Id,
            fixture.Project.Id,
            fixture.Version.ContentDigest,
            [reviewRecord],
            reviewComments: [],
            approvals ?? fixture.Approvals);
        return new DeliveryEvidenceContext(
            delivery ?? await fixture.CreateAsync(),
            fixture.RenderManifest,
            fixture.Graph,
            validationContext ?? fixture.ValidationContext,
            fixture.QualityControl,
            reviewEvidence,
            artifactEvidence ?? fixture.ArtifactEvidence,
            registeredArtifacts ??
                fixture.RegisteredArtifacts.Values.ToArray());
    }

    private static RenderGraphValidationContext CreateValidationContext(
        DeliveryServiceTests.DeliveryFixture fixture,
        Project project,
        IReadOnlyList<SourceSpan> sourceSpans) =>
        new(
            project,
            fixture.Version,
            fixture.TimelineMap,
            fixture.TimelineMapDigest,
            fixture.Profile,
            fixture.Layout,
            fixture.LayoutDigest,
            fixture.Template,
            sourceSpans,
            editorialRecordIds: [],
            [
                new RenderArtifactEvidence(
                    fixture.InputId,
                    fixture.Asset.ContentDigest,
                    fixture.Asset.ByteSize),
            ],
            fonts: []);

    private static BridgeRequest CreateRequest(
        EntityId<CompositionVersionIdTag> versionId,
        RationalTime? outputTime)
    {
        JsonElement parameters = outputTime is null
            ? JsonSerializer.SerializeToElement(new
            {
                versionId = versionId.ToString(),
                outputTime = (object?)null,
            })
            : JsonSerializer.SerializeToElement(new
            {
                versionId = versionId.ToString(),
                outputTime = new
                {
                    value = outputTime.Value.Value.ToString(
                        CultureInfo.InvariantCulture),
                    rate = outputTime.Value.Rate.ToString(
                        CultureInfo.InvariantCulture),
                },
            });
        return BridgeRequest.Create(
            RequestId,
            BridgeMethod.DeliveryRead,
            parameters);
    }

    private static void AssertFailure(string response, string expectedStatus)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement result = document.RootElement.GetProperty("result");
        AssertExactKeys(result, "status", "snapshot", "trace");
        Assert.AreEqual(expectedStatus, result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("trace").ValueKind);
    }

    private static void AssertPrivateDetailsAbsent(string response)
    {
        Assert.IsFalse(response.Contains(
            "storageReference",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains(
            "projects/",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains(
            "rights-evidence",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains(
            "S-1-5-21",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains(
            "private review reason",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains(
            "download",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains(
            "file://",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains(
            "Exception",
            StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRational(
        JsonElement value,
        RationalTime expected)
    {
        AssertExactKeys(value, "value", "rate");
        Assert.AreEqual(
            expected.Value.ToString(CultureInfo.InvariantCulture),
            value.GetProperty("value").GetString());
        Assert.AreEqual(
            expected.Rate.ToString(CultureInfo.InvariantCulture),
            value.GetProperty("rate").GetString());
    }

    private static void AssertExactKeys(
        JsonElement value,
        params string[] expected)
    {
        CollectionAssert.AreEquivalent(
            expected,
            value.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
    }

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));

    private sealed class CountingProvider(
        DeliveryEvidenceProviderResult result) : IDeliveryEvidenceProvider
    {
        internal int CallCount { get; private set; }

        public ValueTask<DeliveryEvidenceProviderResult> ReadAsync(
            EntityId<CompositionVersionIdTag> versionId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AllowAuthorization : IDeliveryAccessAuthorization
    {
        public void DemandRead()
        {
        }
    }
}
