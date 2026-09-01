using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ReviewBridgeHandlerTests
{
    private const string RequestId = "00112233-4455-6677-8899-aabbccddeeff";
    private const string AdministratorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-6101";
    private const string ReviewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-6102";
    private const string UnassignedSid =
        "S-1-5-21-1111111111-2222222222-3333333333-6103";
    private static readonly DateTimeOffset InitialTime =
        new(2036, 7, 8, 9, 10, 11, TimeSpan.Zero);

    [TestMethod]
    public async Task SnapshotResponseIsClosedCompleteAndOmitsNativeIdentity()
    {
        using ReviewBridgeFixture fixture = new();
        BridgeRequest request = ParseRequest(
            BridgeProtocol.ReviewGetSnapshotMethod,
            new { versionId = fixture.CurrentVersion.Id.ToString() });

        string responseJson = await fixture.Handler.HandleAsync(
            request,
            CancellationToken.None);

        using JsonDocument response = JsonDocument.Parse(responseJson);
        JsonElement root = response.RootElement;
        AssertExactProperties(root, "version", "requestId", "method", "result");
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(RequestId, root.GetProperty("requestId").GetString());
        Assert.AreEqual(
            BridgeProtocol.ReviewGetSnapshotMethod,
            root.GetProperty("method").GetString());

        JsonElement result = root.GetProperty("result");
        AssertExactProperties(
            result,
            "versionId",
            "versionDigest",
            "createdAt",
            "parentVersionId",
            "isCurrentTarget",
            "reviewState",
            "reviewStateReason",
            "reviewStateRecordedAt",
            "reviewStateActorRole",
            "comments",
            "approvals",
            "directParentDiff",
            "actions");
        Assert.AreEqual(
            fixture.CurrentVersion.Id.ToString(),
            result.GetProperty("versionId").GetString());
        Assert.AreEqual(
            fixture.CurrentVersion.ContentDigest.Hex,
            result.GetProperty("versionDigest").GetString());
        Assert.AreEqual(64, result.GetProperty("versionDigest").GetString()!.Length);
        Assert.IsTrue(result.GetProperty("isCurrentTarget").GetBoolean());
        Assert.AreEqual("contentReview", result.GetProperty("reviewState").GetString());
        Assert.AreEqual(
            "contentApprover",
            result.GetProperty("reviewStateActorRole").GetString());

        JsonElement comment = result.GetProperty("comments")[0];
        AssertExactProperties(
            comment,
            "id",
            "outputTime",
            "sourceSpanId",
            "actorRole",
            "createdAt",
            "text");
        Assert.AreEqual(
            "1",
            comment.GetProperty("outputTime").GetProperty("value").GetString());
        Assert.AreEqual(
            "2",
            comment.GetProperty("outputTime").GetProperty("rate").GetString());
        Assert.AreEqual(
            fixture.SourceSpanId.ToString(),
            comment.GetProperty("sourceSpanId").GetString());

        JsonElement[] approvals = result.GetProperty("approvals").EnumerateArray().ToArray();
        Assert.AreEqual(2, approvals.Length);
        CollectionAssert.AreEqual(
            new[] { "valid", "stale" },
            approvals.Select(static item => item.GetProperty("validity").GetString()).ToArray());
        Assert.AreEqual(
            fixture.CurrentVersion.Id.ToString(),
            approvals[0].GetProperty("versionId").GetString());
        Assert.AreEqual(
            fixture.ParentVersion.Id.ToString(),
            approvals[1].GetProperty("versionId").GetString());

        JsonElement diff = result.GetProperty("directParentDiff");
        Assert.AreEqual(
            fixture.ParentVersion.Id.ToString(),
            diff.GetProperty("fromVersionId").GetString());
        Assert.AreEqual(
            fixture.CurrentVersion.Id.ToString(),
            diff.GetProperty("toVersionId").GetString());
        Assert.AreEqual(
            "modified",
            diff.GetProperty("changes")[0].GetProperty("kind").GetString());

        CollectionAssert.AreEqual(
            new[] { "addComment", "requestChanges", "recordApproval" },
            result.GetProperty("actions")
                .EnumerateArray()
                .Select(static item => item.GetProperty("kind").GetString())
                .ToArray());
        Assert.IsTrue(result.GetProperty("actions").EnumerateArray().All(
            static item => item.GetProperty("status").GetString() == "authorized"));
        CollectionAssert.AreEqual(
            new[] { "contentApprover" },
            result.GetProperty("actions")[2].GetProperty("actorRoles")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());

        Assert.IsFalse(responseJson.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains(AdministratorSid, StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains(ReviewerSid, StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains("C:\\", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MutationsReturnUpdatedSnapshotAndStableClosedErrors()
    {
        using ReviewBridgeFixture fixture = new();
        BridgeRequest commentRequest = ParseRequest(
            BridgeProtocol.ReviewAddCommentMethod,
            new
            {
                versionId = fixture.CurrentVersion.Id.ToString(),
                versionDigest = fixture.CurrentVersion.ContentDigest.Hex,
                actorRole = "contentApprover",
                outputTime = new { value = "1", rate = "1" },
                text = "01 秒の表記を確認してください。",
            });

        string commentJson = await fixture.Handler.HandleAsync(
            commentRequest,
            CancellationToken.None);
        using (JsonDocument commentResponse = JsonDocument.Parse(commentJson))
        {
            Assert.AreEqual(
                2,
                commentResponse.RootElement
                    .GetProperty("result")
                    .GetProperty("comments")
                    .GetArrayLength());
        }

        BridgeRequest staleRequest = ParseRequest(
            BridgeProtocol.ReviewAddCommentMethod,
            new
            {
                versionId = fixture.CurrentVersion.Id.ToString(),
                versionDigest = new string('b', 64),
                actorRole = "contentApprover",
                outputTime = new { value = "1", rate = "1" },
                text = "古い digest では記録しません。",
            });
        string staleJson = await fixture.Handler.HandleAsync(
            staleRequest,
            CancellationToken.None);
        AssertClosedError(staleJson, "review.stale_version");

        BridgeRequest wrongRoleRequest = ParseRequest(
            BridgeProtocol.ReviewAddCommentMethod,
            new
            {
                versionId = fixture.CurrentVersion.Id.ToString(),
                versionDigest = fixture.CurrentVersion.ContentDigest.Hex,
                actorRole = "rightsApprover",
                outputTime = new { value = "1", rate = "1" },
                text = "未割当の役割では記録しません。",
            });
        string wrongRoleJson = await fixture.Handler.HandleAsync(
            wrongRoleRequest,
            CancellationToken.None);
        AssertClosedError(wrongRoleJson, "review.not_authorized");

        fixture.CurrentActorSid = UnassignedSid;
        BridgeRequest unauthorizedRequest = ParseRequest(
            BridgeProtocol.ReviewGetSnapshotMethod,
            new { versionId = fixture.CurrentVersion.Id.ToString() });
        string unauthorizedJson = await fixture.Handler.HandleAsync(
            unauthorizedRequest,
            CancellationToken.None);
        AssertClosedError(unauthorizedJson, "review.not_authorized");
    }

    [TestMethod]
    public async Task CancellationIsObservedBeforeReviewStorageAccess()
    {
        using ReviewBridgeFixture fixture = new();
        BridgeRequest request = ParseRequest(
            BridgeProtocol.ReviewGetSnapshotMethod,
            new { versionId = fixture.CurrentVersion.Id.ToString() });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => fixture.Handler.HandleAsync(request, cancellation.Token));
    }

    private static BridgeRequest ParseRequest(string method, object parameters)
    {
        string message = JsonSerializer.Serialize(new
        {
            version = 1,
            requestId = RequestId,
            method,
            @params = parameters,
        });
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection),
            rejection.ToString());
        return request ?? throw new AssertFailedException("Expected a review bridge request.");
    }

    private static void AssertClosedError(string responseJson, string code)
    {
        using JsonDocument response = JsonDocument.Parse(responseJson);
        JsonElement root = response.RootElement;
        AssertExactProperties(root, "version", "requestId", "method", "error");
        JsonElement error = root.GetProperty("error");
        AssertExactProperties(error, "code", "message");
        Assert.AreEqual(code, error.GetProperty("code").GetString());
        Assert.IsFalse(responseJson.Contains("SID", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(responseJson.Contains("C:\\", StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains("Exception", StringComparison.Ordinal));
    }

    private static void AssertExactProperties(JsonElement value, params string[] names)
    {
        CollectionAssert.AreEquivalent(
            names,
            value.EnumerateObject().Select(static property => property.Name).ToArray());
    }

    private sealed class ReviewBridgeFixture : IDisposable
    {
        private readonly string rootPath;

        internal ReviewBridgeFixture()
        {
            rootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ReviewBridgeHandlerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            string databasePath = Path.Combine(rootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(rootPath, "backups"));

            var projectStore = new ProjectStore(databasePath);
            var project = new Project(
                EntityId<ProjectIdTag>.New(),
                "Review bridge project",
                "Verify the closed U-012 bridge.",
                "Reviewers",
                PublicationScope.ExternalCandidate,
                deadline: null,
                assets: [],
                performers: []);
            projectStore.CreateProject(project, "{\"project\":true}", InitialTime);
            SourceSpanId = EntityId<SourceSpanIdTag>.New();
            CompositionElement original = CreateSourceElement(
                SourceSpanId,
                new RationalTime(2, 1));
            var workingCopy = new WorkingCopy(
                EntityId<WorkingCopyIdTag>.New(),
                project.Id,
                baseVersionId: null,
                InitialTime,
                InitialTime,
                [original]);
            projectStore.CreateWorkingCopy(
                workingCopy,
                CompositionSnapshotJson.WriteWorkingCopy(workingCopy));
            var versionService = new VersionService(projectStore);
            CheckpointResult parent = versionService.Checkpoint(
                workingCopy,
                0,
                EntityId<CompositionVersionIdTag>.New(),
                AdministratorSid,
                InitialTime.AddMinutes(1),
                "Create parent bridge version.");
            ParentVersion = parent.Version;

            var reviewStore = new ReviewStore(databasePath);
            reviewStore.AppendApproval(new Approval(
                EntityId<ApprovalIdTag>.New(),
                ParentVersion,
                "S-1-5-21-parent-approver",
                ReviewActorRole.ContentApprover,
                InitialTime.AddMinutes(2),
                "Approve the parent version."));

            CompositionElement changed = CreateSourceElement(
                SourceSpanId,
                new RationalTime(3, 1),
                original.Id);
            var changedWorkingCopy = new WorkingCopy(
                parent.WorkingCopy.Id,
                project.Id,
                ParentVersion.Id,
                parent.WorkingCopy.CreatedAt,
                InitialTime.AddMinutes(3),
                [changed]);
            CheckpointResult current = versionService.Checkpoint(
                changedWorkingCopy,
                1,
                EntityId<CompositionVersionIdTag>.New(),
                AdministratorSid,
                InitialTime.AddMinutes(3),
                "Change source-backed content.");
            CurrentVersion = current.Version;
            reviewStore.AppendReviewRecord(new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                CurrentVersion,
                ReviewState.ContentReview,
                ReviewerSid,
                ReviewActorRole.ContentApprover,
                InitialTime.AddMinutes(4),
                "Start content review."));
            reviewStore.AppendReviewComment(new ReviewComment(
                EntityId<ReviewCommentIdTag>.New(),
                CurrentVersion,
                new RationalTime(1, 2),
                SourceSpanId,
                ReviewerSid,
                ReviewActorRole.ContentApprover,
                InitialTime.AddMinutes(5),
                "字幕の語尾を確認してください。"));
            reviewStore.AppendApproval(new Approval(
                EntityId<ApprovalIdTag>.New(),
                CurrentVersion,
                "S-1-5-21-current-rights-approver",
                ReviewActorRole.RightsApprover,
                InitialTime.AddMinutes(6),
                "Current-version rights evidence."));

            var authorization = new AuthorizationService(
                new RoleAssignmentStore(databasePath));
            Assert.IsTrue(authorization.BootstrapInitialRoles(AdministratorSid));
            authorization.AssignRole(
                ReviewerSid,
                AuthorizationRole.ContentApprover,
                AdministratorSid,
                "Assign content review access.");
            CurrentActorSid = ReviewerSid;
            var service = new ReviewWorkspaceService(
                projectStore,
                reviewStore,
                authorization,
                () => CurrentActorSid,
                new ManualTimeProvider(InitialTime.AddHours(1)));
            Handler = new ReviewBridgeHandler(service);
        }

        internal string CurrentActorSid { get; set; }

        internal EntityId<SourceSpanIdTag> SourceSpanId { get; }

        internal CompositionVersion ParentVersion { get; }

        internal CompositionVersion CurrentVersion { get; }

        internal ReviewBridgeHandler Handler { get; }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        private static CompositionElement CreateSourceElement(
            EntityId<SourceSpanIdTag> sourceSpanId,
            RationalTime duration,
            EntityId<CompositionElementIdTag>? elementId = null) =>
            new(
                elementId ?? EntityId<CompositionElementIdTag>.New(),
                new TimeRange(default, duration),
                OriginRef.FromSourceSpan(sourceSpanId),
                adoptedSuggestion: null,
                new TimeRange(new RationalTime(10, 1), duration));
    }

    private sealed class ManualTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
