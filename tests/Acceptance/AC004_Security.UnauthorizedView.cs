using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AC004_Security_UnauthorizedView
{
    private const string RequestId =
        "00400000-0000-4000-8000-000000000001";
    private const string AdministratorSid =
        "S-1-5-21-4004-1000-2000-3000";
    private const string ViewerSid =
        "S-1-5-21-4004-1000-2000-3001";
    private const string UnassignedSid =
        "S-1-5-21-4004-1000-2000-3999";

    [TestMethod]
    public async Task UnassignedActorAndUnauthorizedRoleAreForbiddenBeforeDataRead()
    {
        using var fixture = new AuthorizationFixture();

        var unassignedRuntime = new CountingRuntime();
        var unassignedHandler = new AnalysisBridgeRequestHandler(
            unassignedRuntime,
            new AnalysisBridgeAuthorization(fixture.Authorization, () => UnassignedSid));
        using JsonDocument forbiddenRead = JsonDocument.Parse(
            await unassignedHandler.HandleAsync(ParseRequest(
                BridgeProtocol.AnalysisReadMethod,
                $$"""{"projectId":"{{Guid.NewGuid():D}}"}""")));

        AssertResult(forbiddenRead, "forbidden");
        Assert.AreEqual(0, unassignedRuntime.CallCount);
        Assert.IsFalse(fixture.Authorization.CanPerform(
            UnassignedSid,
            AuthorizationOperation.ViewProject));
        AuthorizationException unassignedFailure =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Authorization.Demand(
                    UnassignedSid,
                    AuthorizationOperation.ViewProject));
        Assert.AreEqual(
            AuthorizationOperation.ViewProject,
            unassignedFailure.Operation);

        var viewerRuntime = new CountingRuntime();
        var viewerHandler = new AnalysisBridgeRequestHandler(
            viewerRuntime,
            new AnalysisBridgeAuthorization(fixture.Authorization, () => ViewerSid));
        using JsonDocument forbiddenMutation = JsonDocument.Parse(
            await viewerHandler.HandleAsync(ParseRequest(
                BridgeProtocol.AnalysisAdoptMethod,
                $$"""{"projectId":"{{Guid.NewGuid():D}}","revisionId":"10000000-0000-4000-8000-000000000099","digest":"{{new string('a', 64)}}","candidateId":"10000000-0000-4000-8000-000000000098","reason":"AC004 negative control"}""")));

        AssertResult(forbiddenMutation, "forbidden");
        Assert.AreEqual(0, viewerRuntime.CallCount);
        AuthorizationException roleFailure =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Authorization.DemandAs(
                    ViewerSid,
                    AuthorizationRole.Viewer,
                    AuthorizationOperation.DecideAnalysisSuggestion));
        Assert.AreEqual(
            AuthorizationOperation.DecideAnalysisSuggestion,
            roleFailure.Operation);
        Assert.AreEqual(AuthorizationRole.Viewer, roleFailure.ActingRole);

        var evidence = new AcceptanceEvidence(
            "Q-004",
            new ScopeEvidence(
                UnassignedActorReadForbidden: true,
                RoleWithoutMutationPermissionForbidden: true,
                DataReadSideEffectCount: unassignedRuntime.CallCount
                    + viewerRuntime.CallCount));
        Assert.AreEqual("Q-004", evidence.Requirement);
        Assert.IsTrue(evidence.Scope.UnassignedActorReadForbidden);
        Assert.IsTrue(evidence.Scope.RoleWithoutMutationPermissionForbidden);
        Assert.AreEqual(0, evidence.Scope.DataReadSideEffectCount);
    }

    private static BridgeRequest ParseRequest(string method, string parameters)
    {
        bool accepted = BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            CreateRequest(method, parameters),
            out BridgeRequest? request,
            out BridgeRequestRejection rejection);
        Assert.IsTrue(accepted, rejection.ToString());
        Assert.IsNotNull(request);
        return request;
    }

    private static string CreateRequest(string method, string parameters) =>
        $$"""{"version":1,"requestId":"{{RequestId}}","method":"{{method}}","params":{{parameters}}}""";

    private static void AssertResult(JsonDocument response, string status)
    {
        JsonElement root = response.RootElement;
        Assert.AreEqual(RequestId, root.GetProperty("requestId").GetString());
        JsonElement result = root.GetProperty("result");
        Assert.AreEqual(status, result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
    }

    private sealed class CountingRuntime : IAnalysisBridgeRuntime
    {
        internal int CallCount { get; private set; }

        public AnalysisBridgeOperationResult Execute(
            BridgeRequest request,
            string actorSid,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return AnalysisBridgeOperationResult.Unavailable();
        }
    }

    private sealed class AuthorizationFixture : IDisposable
    {
        internal AuthorizationFixture()
        {
            RootPath = CreateIsolatedRoot(nameof(AC004_Security_UnauthorizedView));
            string databasePath = Path.Combine(RootPath, "store.db");
            string backupDirectory = Path.Combine(RootPath, "backups");
            Directory.CreateDirectory(backupDirectory);
            _ = DatabaseMigrator.Migrate(
                databasePath,
                backupDirectory);
            Authorization = new AuthorizationService(
                new RoleAssignmentStore(databasePath));
            Assert.IsTrue(Authorization.BootstrapInitialRoles(AdministratorSid));
            Authorization.AssignRole(
                ViewerSid,
                AuthorizationRole.Viewer,
                AdministratorSid,
                "Assign viewer for AC004 negative authorization coverage.");
        }

        internal string RootPath { get; }

        internal AuthorizationService Authorization { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private static string CreateIsolatedRoot(string testName)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "HubRadio.Q004.Acceptance",
            testName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record AcceptanceEvidence(
        string Requirement,
        ScopeEvidence Scope);

    private sealed record ScopeEvidence(
        bool UnassignedActorReadForbidden,
        bool RoleWithoutMutationPermissionForbidden,
        int DataReadSideEffectCount);
}
