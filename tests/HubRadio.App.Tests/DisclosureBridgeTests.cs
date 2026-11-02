using System.Text.Json;
using HubRadio.App.Audit;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Analysis;
using HubRadio.Core.Audit;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Security;

namespace HubRadio.App.Tests;

/// <summary>NFR-BR-005 AC-126: AI element list and destination labels over the bridge (disclosure.list / disclosure.addLabel).</summary>
[TestClass]
public sealed class DisclosureBridgeTests
{
    private const string Actor = "S-1-5-21-2000";
    private const string Stranger = "S-1-5-21-2999";
    private static readonly DateTimeOffset At = new(2033, 4, 5, 6, 7, 8, TimeSpan.FromHours(9));

    private static BridgeRequest Request(BridgeMethod method, string parameters) =>
        BridgeRequest.Create(Guid.NewGuid(), method, JsonDocument.Parse(parameters).RootElement.Clone());

    private static bool Parse(string method, string parameters, out BridgeRequest? request) =>
        BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"{{method}}","params":{{parameters}}}""",
            out request, out _);

    private static JsonElement Result(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();

    [TestMethod]
    // NFR-BR-005 AC-126: closed parameters only.
    public void ProtocolAcceptsOnlyExactClosedParameters()
    {
        string id = Guid.NewGuid().ToString("D");
        Assert.IsTrue(Parse("disclosure.list", $$"""{"projectId":"{{id}}"}""", out BridgeRequest? list));
        Assert.AreEqual(BridgeMethod.DisclosureList, list!.Method);
        Assert.AreEqual("disclosure.addLabel", BridgeProtocol.GetMethodName(BridgeMethod.DisclosureAddLabel));
        Assert.IsTrue(Parse("disclosure.addLabel", $$"""{"projectId":"{{id}}","destination":"YouTube","labelText":"AI 字幕"}""", out _));
        Assert.IsFalse(Parse("disclosure.list", "{}", out _));
        Assert.IsFalse(Parse("disclosure.list", $$"""{"projectId":"{{id}}","x":1}""", out _));
        Assert.IsFalse(Parse("disclosure.addLabel", $$"""{"projectId":"{{id}}","destination":" ","labelText":"a"}""", out _));
        Assert.IsFalse(Parse("disclosure.addLabel", $$"""{"projectId":"{{id}}","destination":"{{new string('d', 65)}}","labelText":"a"}""", out _));
        Assert.IsFalse(Parse("disclosure.addLabel", $$"""{"projectId":"{{id}}","destination":"YouTube","labelText":"{{new string('l', 201)}}"}""", out _));
    }

    [TestMethod]
    // NFR-BR-005 AC-126: recorded AI elements are listed; an added label is listed and reflected into the artifact.
    public void ListsElementsAndAddedLabelsAndReflectsLabelIntoArtifact()
    {
        using Fixture f = new();
        _ = f.Service.RecordAiElement(
            EntityId<AuditEventIdTag>.New(), f.Project.Id, AiGeneratedElementKind.SpeakerEstimate, "speaker-1",
            ModelRun(), Actor, ReviewActorRole.Editor, At);
        string id = f.Project.Id.ToString();

        JsonElement empty = Result(f.Handler.Handle(Request(BridgeMethod.DisclosureList, $$"""{"projectId":"{{id}}"}""")));
        Assert.AreEqual("listed", empty.GetProperty("status").GetString());
        Assert.AreEqual(1, empty.GetProperty("elements").GetArrayLength());
        Assert.AreEqual("SpeakerEstimate", empty.GetProperty("elements")[0].GetProperty("kind").GetString());
        Assert.AreEqual(0, empty.GetProperty("labels").GetArrayLength());

        JsonElement added = Result(f.Handler.Handle(Request(
            BridgeMethod.DisclosureAddLabel,
            $$"""{"projectId":"{{id}}","destination":"YouTube","labelText":"この動画には AI 生成の要素が含まれます"}""")));
        Assert.AreEqual("added", added.GetProperty("status").GetString());
        Assert.AreEqual("YouTube", added.GetProperty("labels")[0].GetProperty("destination").GetString());

        AiDisclosure disclosure = f.Service.BuildDisclosure(f.Project.Id, "youtube");
        string? path = disclosure.WriteArtifact(Path.Combine(f.Root, "out"));
        Assert.IsNotNull(path);
        StringAssert.Contains(File.ReadAllText(path), "この動画には AI 生成の要素が含まれます");
    }

    [TestMethod]
    // NFR-BR-005 AC-126: unauthorized callers and invalid text change nothing.
    public void ForbiddenAndInvalidRequestsChangeNothing()
    {
        using Fixture f = new();
        string id = f.Project.Id.ToString();
        var denied = new DisclosureBridgeHandler(f.Service, f.Authorization, () => Stranger, new FixedTime(At));
        Assert.AreEqual("forbidden", Result(denied.Handle(Request(
            BridgeMethod.DisclosureAddLabel, $$"""{"projectId":"{{id}}","destination":"YouTube","labelText":"x"}"""))).GetProperty("status").GetString());
        Assert.AreEqual("forbidden", Result(denied.Handle(Request(
            BridgeMethod.DisclosureList, $$"""{"projectId":"{{id}}"}"""))).GetProperty("status").GetString());
        Assert.AreEqual(0, f.Service.ListLabels(f.Project.Id).Count);
        Assert.AreEqual("unavailable", Result(DisclosureBridgeJson.Unavailable(
            Request(BridgeMethod.DisclosureList, $$"""{"projectId":"{{id}}"}"""))).GetProperty("status").GetString());
    }

    private static ModelRun ModelRun()
    {
        ContentDigest Digest(char c) => new(new string(c, 64));
        return new ModelRun(
            EntityId<ModelRunIdTag>.New(), "local-provider", "local-model", "1.2.3",
            Digest('a'), ModelHashAvailability.Available, Digest('b'), Digest('c'),
            Digest('d'), ModelRunOutcome.Succeeded, null, ModelExecutionLocation.Local,
            null, null, null, null, At.AddMinutes(-2), At.AddMinutes(-1));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "HubRadio.DisclosureBridgeTests", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Root);
            string databasePath = Path.Combine(Root, "store.db");
            _ = DatabaseMigrator.Migrate(databasePath, Path.Combine(Root, "backups"));
            Project = new Project(
                EntityId<ProjectIdTag>.New(), "Disclosure bridge", "Verify the bridge.",
                "Test runner", PublicationScope.InternalOnly, deadline: null, assets: [], performers: []);
            new ProjectStore(databasePath).CreateProject(Project, "{\"project\":true}", At.AddMinutes(-10));
            Service = new AiGenerationDisclosureService(new ReviewStore(databasePath));
            Authorization = new AuthorizationService(new RoleAssignmentStore(databasePath));
            Assert.IsTrue(Authorization.BootstrapInitialRoles(Actor));
            Handler = new DisclosureBridgeHandler(Service, Authorization, () => Actor, new FixedTime(At));
        }

        internal string Root { get; }

        internal Project Project { get; }

        internal AiGenerationDisclosureService Service { get; }

        internal AuthorizationService Authorization { get; }

        internal DisclosureBridgeHandler Handler { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
