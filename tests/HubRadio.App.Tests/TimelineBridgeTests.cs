using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TimelineBridgeTests
{
    private const string RequestId =
        "00112233-4455-6677-8899-aabbccddeeff";
    private const string ActorId = "S-1-5-21-1000";
    private const string ViewerId = "S-1-5-21-1001";
    private static readonly DateTimeOffset InitialTime = new(
        2026,
        9,
        1,
        2,
        3,
        4,
        TimeSpan.Zero);

    [TestMethod]
    public void ProtocolAcceptsOnlyClosedTypedTimelineRequests()
    {
        string projectId = EntityId<ProjectIdTag>.New().ToString();
        string elementId = EntityId<CompositionElementIdTag>.New().ToString();
        string rightId = EntityId<CompositionElementIdTag>.New().ToString();
        string operations = $$"""
            [
              {"kind":"keep","elementId":"{{elementId}}"},
              {"kind":"delete","elementId":"{{elementId}}"},
              {"kind":"split","elementId":"{{elementId}}","splitOffset":{"value":"1001","rate":"24000"},"rightElementId":"{{rightId}}"},
              {"kind":"move","elementId":"{{elementId}}","targetIndex":0}
            ]
            """;

        BridgeRequest read = ParseRequest(
            BridgeProtocol.TimelineReadMethod,
            $$"""{"projectId":"{{projectId}}"}""");
        BridgeRequest apply = ParseRequest(
            BridgeProtocol.TimelineApplyMethod,
            $$"""{"projectId":"{{projectId}}","expectedRevision":0,"operations":{{operations}}}""");

        Assert.AreEqual(BridgeMethod.TimelineRead, read.Method);
        Assert.AreEqual(BridgeMethod.TimelineApply, apply.Method);
        Assert.AreEqual(
            BridgeProtocol.MaxProjectResponseUtf8Bytes,
            BridgeProtocol.GetMaximumResponseUtf8Bytes(apply.Method));

        string[] invalidParameters =
        [
            $$"""{"projectId":"{{projectId}}","command":"ffmpeg -i private.mov"}""",
            $$"""{"projectId":"{{projectId.ToUpperInvariant()}}"}""",
            $$"""{"projectId":"{{projectId}}","expectedRevision":0,"operations":[]}""",
            $$"""{"projectId":"{{projectId}}","expectedRevision":0,"operations":[{"kind":"split","elementId":"{{elementId}}","splitOffset":{"value":"2","rate":"2"},"rightElementId":"{{rightId}}"}]}""",
            $$"""{"projectId":"{{projectId}}","expectedRevision":0,"operations":[{"kind":"move","elementId":"{{elementId}}","targetIndex":-1}]}""",
            $$"""{"projectId":"{{projectId}}","expectedRevision":0,"operations":[{"kind":"delete","elementId":"{{elementId}}","path":"C:\\private.mov"}]}""",
        ];

        foreach (string parameters in invalidParameters)
        {
            string method = parameters.Contains("expectedRevision", StringComparison.Ordinal)
                ? BridgeProtocol.TimelineApplyMethod
                : BridgeProtocol.TimelineReadMethod;
            Assert.IsFalse(BridgeProtocol.TryParseRequest(
                WebViewHost.EntryPoint,
                CreateRequest(method, parameters),
                out BridgeRequest? rejected,
                out BridgeRequestRejection rejection));
            Assert.IsNull(rejected);
            Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
        }
    }

    [TestMethod]
    public void TimelineMethodsAloneReceiveTheLargeRequestBudget()
    {
        string projectId = EntityId<ProjectIdTag>.New().ToString();
        string valid = CreateRequest(
            BridgeProtocol.TimelineReadMethod,
            $$"""{"projectId":"{{projectId}}"}""");
        string exactLimit =
            new string(
                ' ',
                BridgeProtocol.MaxProjectRequestUtf8Bytes -
                Encoding.UTF8.GetByteCount(valid)) +
            valid;

        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit,
            out _,
            out BridgeRequestRejection accepted));
        Assert.AreEqual(BridgeRequestRejection.None, accepted);
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            exactLimit + " ",
            out _,
            out BridgeRequestRejection rejected));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejected);
    }

    [TestMethod]
    public void ReadAuthorizesBeforeExistenceAndReturnsNoPrivateDetail()
    {
        using TimelineTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        var denied = new RecordingAuthorization(denyRead: true);
        var handler = new TimelineBridgeRequestHandler(store, denied);
        EntityId<ProjectIdTag> missingProjectId = EntityId<ProjectIdTag>.New();

        string response = handler.Handle(ParseRead(missingProjectId));

        Assert.AreEqual("forbidden", ReadStatus(response));
        Assert.AreEqual(1, denied.ReadDemandCount);
        Assert.AreEqual(0, denied.EditDemandCount);
        AssertPrivateDetailAbsent(response);
    }

    [TestMethod]
    public void ApplyAuthorizesBeforeExistenceAndReturnsNoPrivateDetail()
    {
        using TimelineTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        var denied = new RecordingAuthorization(denyEdit: true);
        var handler = new TimelineBridgeRequestHandler(store, denied);
        EntityId<ProjectIdTag> missingProjectId = EntityId<ProjectIdTag>.New();
        string operations =
            $$"""[{"kind":"keep","elementId":"{{EntityId<CompositionElementIdTag>.New()}}"}]""";

        string response = handler.Handle(ParseApply(
            missingProjectId,
            expectedRevision: 0,
            operations));

        Assert.AreEqual("forbidden", ReadStatus(response));
        Assert.AreEqual(0, denied.ReadDemandCount);
        Assert.AreEqual(1, denied.EditDemandCount);
        AssertPrivateDetailAbsent(response);
    }

    [TestMethod]
    public void AuthorizationUsesTheCurrentNativeActorAndExactExistingRoles()
    {
        using TimelineTestDatabase database = new();
        var authorizationService = new AuthorizationService(
            new RoleAssignmentStore(database.DatabasePath));
        Assert.IsTrue(authorizationService.BootstrapInitialRoles(ActorId));
        authorizationService.AssignRole(
            ViewerId,
            AuthorizationRole.Viewer,
            ActorId,
            "Allow timeline read verification.");
        string currentActorId = ViewerId;
        var authorization = new TimelineBridgeAuthorization(
            authorizationService,
            () => currentActorId);

        authorization.DemandRead();
        AuthorizationException denied =
            Assert.ThrowsExactly<AuthorizationException>(
                () => authorization.DemandEdit());
        Assert.AreEqual(
            AuthorizationOperation.CreateCompositionVersion,
            denied.Operation);
        Assert.AreEqual(AuthorizationRole.Editor, denied.ActingRole);

        currentActorId = ActorId;
        Assert.AreEqual(ActorId, authorization.DemandEdit());
    }

    [TestMethod]
    public void ReadRestoresCanonicalSnapshotIncludingExponentNumbersAndOrigins()
    {
        using TimelineTestDatabase database = new();
        EntityId<SourceSpanIdTag> sourceSpanId =
            EntityId<SourceSpanIdTag>.New();
        OriginRef sourceOrigin = OriginRef.FromSourceSpan(sourceSpanId);
        SuggestionReference suggestion =
            CreateAdoptedSuggestionReference(
                EntityId<SuggestionIdTag>.New(),
                sourceOrigin);
        CompositionElement source = new(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(default, new RationalTime(30000, 1001)),
            sourceOrigin,
            suggestion,
            new TimeRange(
                new RationalTime(-1001, 24000),
                new RationalTime(30000, 1001)));
        CompositionElement asset = new(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(source.OutputRange.End, new RationalTime(1, 1)),
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()));
        CompositionElement editorial = new(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(asset.OutputRange.End, new RationalTime(1, 2)),
            OriginRef.FromEditorialRecord(
                EntityId<EditorialRecordIdTag>.New()));
        StoredTimeline stored = CreateStoredTimeline(
            database,
            [source, asset, editorial]);
        Assert.IsTrue(
            stored.Stored.CanonicalJson.Contains("3e4", StringComparison.Ordinal),
            stored.Stored.CanonicalJson);
        var handler = new TimelineBridgeRequestHandler(
            stored.Store,
            new RecordingAuthorization());

        string response = handler.Handle(ParseRead(stored.Project.Id));

        Assert.AreEqual("available", ReadStatus(response));
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement snapshot = document.RootElement
            .GetProperty("result")
            .GetProperty("snapshot");
        Assert.AreEqual(stored.Project.Id.ToString(), snapshot.GetProperty("projectId").GetString());
        Assert.AreEqual(stored.Draft.Id.ToString(), snapshot.GetProperty("workingCopyId").GetString());
        Assert.AreEqual(0, snapshot.GetProperty("revision").GetInt32());
        JsonElement[] elements = snapshot.GetProperty("elements").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            new[] { source.Id.ToString(), asset.Id.ToString(), editorial.Id.ToString() },
            elements.Select(static item => item.GetProperty("id").GetString()).ToArray());
        Assert.AreEqual(
            "-1001",
            elements[0].GetProperty("sourceRange").GetProperty("start").GetProperty("value").GetString());
        Assert.AreEqual("asset", elements[1].GetProperty("origin").GetProperty("kind").GetString());
        Assert.AreEqual(JsonValueKind.Null, elements[1].GetProperty("sourceRange").ValueKind);
        Assert.AreEqual("editorialRecord", elements[2].GetProperty("origin").GetProperty("kind").GetString());
        AssertPrivateDetailAbsent(response);
    }

    [TestMethod]
    // FR-027 AC-075: 画面操作（bridge）からの編集指示がまとめて適用される。
    // FR-032 AC-005: 適用後も安定 ID と元動画区間への対応が保たれる。
    public void ApplyBatchIsAtomicPreservesStableIdsAndExactSourceMapping()
    {
        using TimelineTestDatabase database = new();
        EntityId<CompositionElementIdTag> sourceId =
            EntityId<CompositionElementIdTag>.New();
        EntityId<CompositionElementIdTag> assetId =
            EntityId<CompositionElementIdTag>.New();
        EntityId<CompositionElementIdTag> rightId =
            EntityId<CompositionElementIdTag>.New();
        RationalTime duration = new(1001, 2400);
        RationalTime splitOffset = new(1001, 4800);
        TimeRange sourceRange = new(new RationalTime(1001, 24000), duration);
        OriginRef sourceOrigin = OriginRef.FromSourceSpan(
            EntityId<SourceSpanIdTag>.New());
        SuggestionReference suggestion =
            CreateAdoptedSuggestionReference(
                EntityId<SuggestionIdTag>.New(),
                sourceOrigin);
        CompositionElement source = new(
            sourceId,
            new TimeRange(default, duration),
            sourceOrigin,
            suggestion,
            sourceRange);
        CompositionElement asset = new(
            assetId,
            new TimeRange(duration, new RationalTime(1, 1)),
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()));
        StoredTimeline stored = CreateStoredTimeline(database, [source, asset]);
        EntityId<CompositionVersionIdTag> versionId =
            EntityId<CompositionVersionIdTag>.New();
        EntityId<TimelineMapIdTag> timelineMapId =
            EntityId<TimelineMapIdTag>.New();
        var handler = new TimelineBridgeRequestHandler(
            stored.Store,
            new RecordingAuthorization(),
            () => InitialTime.AddMinutes(1),
            () => versionId,
            () => timelineMapId);
        string operations = $$"""
            [
              {"kind":"split","elementId":"{{sourceId}}","splitOffset":{"value":"{{splitOffset.Value}}","rate":"{{splitOffset.Rate}}"},"rightElementId":"{{rightId}}"},
              {"kind":"move","elementId":"{{assetId}}","targetIndex":0},
              {"kind":"delete","elementId":"{{sourceId}}"}
            ]
            """;

        string response = handler.Handle(ParseApply(
            stored.Project.Id,
            expectedRevision: 0,
            operations));

        Assert.AreEqual("available", ReadStatus(response));
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement snapshot = document.RootElement
            .GetProperty("result")
            .GetProperty("snapshot");
        Assert.AreEqual(1, snapshot.GetProperty("revision").GetInt32());
        JsonElement[] elements = snapshot.GetProperty("elements").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            new[] { assetId.ToString(), rightId.ToString() },
            elements.Select(static item => item.GetProperty("id").GetString()).ToArray());
        AssertRational(elements[0].GetProperty("outputRange").GetProperty("start"), 0, 1);
        AssertRational(elements[1].GetProperty("outputRange").GetProperty("start"), 1, 1);
        RationalTime expectedSourceStart = sourceRange.Start + splitOffset;
        AssertRational(
            elements[1].GetProperty("sourceRange").GetProperty("start"),
            expectedSourceStart.Value,
            expectedSourceStart.Rate);

        StoredWorkingCopy persisted = stored.Store.GetWorkingCopy(stored.Project.Id)!;
        Assert.AreEqual(1, persisted.Revision);
        Assert.AreEqual(versionId, persisted.BaseVersionId);
        WorkingCopy restored = WorkingCopySnapshotCodec.Read(persisted);
        CollectionAssert.AreEqual(
            new[] { assetId, rightId },
            restored.Elements.Select(static element => element.Id).ToArray());
        Assert.AreEqual(suggestion.SuggestionId, restored.Elements[1].AdoptedSuggestion!.SuggestionId);
        Assert.AreEqual(expectedSourceStart, restored.Elements[1].SourceRange!.Value.Start);

        StoredCompositionVersion persistedVersion =
            stored.Store.GetCompositionVersion(versionId)!;
        Assert.AreEqual(stored.Project.Id, persistedVersion.ProjectId);
        Assert.IsNull(persistedVersion.ParentVersionId);
        using JsonDocument versionDocument = JsonDocument.Parse(
            persistedVersion.CanonicalJson);
        JsonElement version = versionDocument.RootElement;
        Assert.AreEqual(ActorId, version.GetProperty("createdByActorId").GetString());
        Assert.AreEqual(
            "Apply typed timeline edit operations.",
            version.GetProperty("changeReason").GetString());
        CollectionAssert.AreEqual(
            new[] { assetId.ToString(), rightId.ToString() },
            version.GetProperty("elements")
                .EnumerateArray()
                .Select(static element => element.GetProperty("id").GetString())
                .ToArray());
    }

    [TestMethod]
    public void KeepCreatesAuditableCheckpointWithoutChangingStableElement()
    {
        using TimelineTestDatabase database = new();
        CompositionElement element = CreateAssetElement(
            EntityId<CompositionElementIdTag>.New(),
            default);
        StoredTimeline stored = CreateStoredTimeline(database, [element]);
        EntityId<CompositionVersionIdTag> versionId =
            EntityId<CompositionVersionIdTag>.New();
        var handler = new TimelineBridgeRequestHandler(
            stored.Store,
            new RecordingAuthorization(),
            () => InitialTime.AddMinutes(1),
            () => versionId,
            EntityId<TimelineMapIdTag>.New);
        string operations =
            $$"""[{"kind":"keep","elementId":"{{element.Id}}"}]""";

        string response = handler.Handle(ParseApply(
            stored.Project.Id,
            expectedRevision: 0,
            operations));

        Assert.AreEqual("available", ReadStatus(response));
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement snapshot = document.RootElement
            .GetProperty("result")
            .GetProperty("snapshot");
        Assert.AreEqual(1, snapshot.GetProperty("revision").GetInt32());
        Assert.AreEqual(versionId.ToString(), snapshot.GetProperty("baseVersionId").GetString());
        Assert.AreEqual(
            element.Id.ToString(),
            snapshot.GetProperty("elements")[0].GetProperty("id").GetString());

        StoredWorkingCopy persisted = stored.Store.GetWorkingCopy(stored.Project.Id)!;
        Assert.AreEqual(versionId, persisted.BaseVersionId);
        Assert.IsNotNull(stored.Store.GetCompositionVersion(versionId));
    }

    [TestMethod]
    public void InvalidSecondOperationDoesNotPersistTheFirstOperation()
    {
        using TimelineTestDatabase database = new();
        CompositionElement first = CreateAssetElement(
            EntityId<CompositionElementIdTag>.New(),
            default);
        CompositionElement second = CreateAssetElement(
            EntityId<CompositionElementIdTag>.New(),
            first.OutputRange.End);
        StoredTimeline stored = CreateStoredTimeline(database, [first, second]);
        var handler = new TimelineBridgeRequestHandler(
            stored.Store,
            new RecordingAuthorization(),
            () => InitialTime.AddMinutes(1));
        string operations = $$"""
            [
              {"kind":"delete","elementId":"{{first.Id}}"},
              {"kind":"keep","elementId":"{{first.Id}}"}
            ]
            """;

        string response = handler.Handle(ParseApply(
            stored.Project.Id,
            expectedRevision: 0,
            operations));

        Assert.AreEqual("invalidOperation", ReadStatus(response));
        StoredWorkingCopy persisted = stored.Store.GetWorkingCopy(stored.Project.Id)!;
        Assert.AreEqual(0, persisted.Revision);
        Assert.IsNull(persisted.BaseVersionId);
        WorkingCopy restored = WorkingCopySnapshotCodec.Read(persisted);
        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id },
            restored.Elements.Select(static element => element.Id).ToArray());
    }

    [TestMethod]
    public void SplitCannotReuseAnIdDeletedEarlierInTheSameBatch()
    {
        using TimelineTestDatabase database = new();
        CompositionElement first = CreateAssetElement(
            EntityId<CompositionElementIdTag>.New(),
            default);
        CompositionElement second = CreateAssetElement(
            EntityId<CompositionElementIdTag>.New(),
            first.OutputRange.End);
        StoredTimeline stored = CreateStoredTimeline(database, [first, second]);
        var handler = new TimelineBridgeRequestHandler(
            stored.Store,
            new RecordingAuthorization(),
            () => InitialTime.AddMinutes(1));
        string operations = $$"""
            [
              {"kind":"delete","elementId":"{{first.Id}}"},
              {"kind":"split","elementId":"{{second.Id}}","splitOffset":{"value":"1","rate":"2"},"rightElementId":"{{first.Id}}"}
            ]
            """;

        string response = handler.Handle(ParseApply(
            stored.Project.Id,
            expectedRevision: 0,
            operations));

        Assert.AreEqual("invalidOperation", ReadStatus(response));
        StoredWorkingCopy persisted = stored.Store.GetWorkingCopy(stored.Project.Id)!;
        Assert.AreEqual(0, persisted.Revision);
        Assert.AreEqual(stored.Stored.CanonicalJson, persisted.CanonicalJson);
    }

    [TestMethod]
    public void StaleRevisionReturnsConflictWithoutPretendingToUndoServerState()
    {
        using TimelineTestDatabase database = new();
        CompositionElement element = CreateAssetElement(
            EntityId<CompositionElementIdTag>.New(),
            default);
        StoredTimeline stored = CreateStoredTimeline(database, [element]);
        var handler = new TimelineBridgeRequestHandler(
            stored.Store,
            new RecordingAuthorization(),
            () => InitialTime.AddMinutes(1));
        string operations =
            $$"""[{"kind":"delete","elementId":"{{EntityId<CompositionElementIdTag>.New()}}"}]""";

        string response = handler.Handle(ParseApply(
            stored.Project.Id,
            expectedRevision: 1,
            operations));

        Assert.AreEqual("conflict", ReadStatus(response));
        StoredWorkingCopy persisted = stored.Store.GetWorkingCopy(stored.Project.Id)!;
        Assert.AreEqual(0, persisted.Revision);
        Assert.AreEqual(stored.Stored.CanonicalJson, persisted.CanonicalJson);
    }

    [TestMethod]
    public void OversizedSuccessSnapshotDoesNotCommitAHiddenMutation()
    {
        using TimelineTestDatabase database = new();
        OriginRef origin = OriginRef.FromAsset(EntityId<AssetIdTag>.New());
        var elements = new CompositionElement[6_500];
        for (int index = 0; index < elements.Length; index++)
        {
            elements[index] = new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                new TimeRange(
                    new RationalTime(index, 1),
                    new RationalTime(1, 1)),
                origin);
        }

        StoredTimeline stored = CreateStoredTimeline(database, elements);
        EntityId<CompositionVersionIdTag> versionId =
            EntityId<CompositionVersionIdTag>.New();
        var handler = new TimelineBridgeRequestHandler(
            stored.Store,
            new RecordingAuthorization(),
            () => InitialTime.AddMinutes(1),
            () => versionId,
            EntityId<TimelineMapIdTag>.New);
        string operations =
            $$"""[{"kind":"keep","elementId":"{{elements[0].Id}}"}]""";

        string response = handler.Handle(ParseApply(
            stored.Project.Id,
            expectedRevision: 0,
            operations));

        Assert.AreEqual("unavailable", ReadStatus(response));
        StoredWorkingCopy persisted = stored.Store.GetWorkingCopy(stored.Project.Id)!;
        Assert.AreEqual(0, persisted.Revision);
        Assert.AreEqual(stored.Stored.CanonicalJson, persisted.CanonicalJson);
        Assert.IsNull(stored.Store.GetCompositionVersion(versionId));
        AssertPrivateDetailAbsent(response);
    }

    [TestMethod]
    public void CorruptPayloadBindingFailsClosedAsUnavailable()
    {
        using TimelineTestDatabase database = new();
        StoredTimeline stored = CreateStoredTimeline(
            database,
            [CreateAssetElement(EntityId<CompositionElementIdTag>.New(), default)]);
        EntityId<ProjectIdTag> otherProjectId = EntityId<ProjectIdTag>.New();
        string corrupted = stored.Stored.CanonicalJson.Replace(
            stored.Project.Id.ToString(),
            otherProjectId.ToString(),
            StringComparison.Ordinal);
        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(
            Encoding.UTF8.GetBytes(corrupted));
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE working_copies SET payload = $payload WHERE id = $id;";
            command.Parameters.Add("$payload", SqliteType.Blob).Value = canonical;
            command.Parameters.Add("$id", SqliteType.Text).Value = stored.Draft.Id.ToString();
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        var handler = new TimelineBridgeRequestHandler(
            stored.Store,
            new RecordingAuthorization());

        string response = handler.Handle(ParseRead(stored.Project.Id));

        Assert.AreEqual("unavailable", ReadStatus(response));
        AssertPrivateDetailAbsent(response);
    }

    private static StoredTimeline CreateStoredTimeline(
        TimelineTestDatabase database,
        IReadOnlyList<CompositionElement> elements)
    {
        var store = new ProjectStore(database.DatabasePath);
        var project = new Project(
            EntityId<ProjectIdTag>.New(),
            "Timeline test",
            "Verify typed timeline editing.",
            "Editors",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        store.CreateProject(project, "{\"project\":true}", InitialTime);
        var draft = new WorkingCopy(
            EntityId<WorkingCopyIdTag>.New(),
            project.Id,
            baseVersionId: null,
            InitialTime,
            InitialTime,
            elements);
        store.CreateWorkingCopy(
            draft,
            CompositionSnapshotJson.WriteWorkingCopy(draft));
        return new StoredTimeline(
            store,
            project,
            draft,
            store.GetWorkingCopy(project.Id)!);
    }

    private static CompositionElement CreateAssetElement(
        EntityId<CompositionElementIdTag> id,
        RationalTime start)
    {
        return new CompositionElement(
            id,
            new TimeRange(start, new RationalTime(1, 1)),
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()));
    }

    private static SuggestionReference CreateAdoptedSuggestionReference(
        EntityId<SuggestionIdTag> suggestionId,
        OriginRef origin)
    {
        var suggestion = new Suggestion(
            suggestionId,
            EntityId<ModelRunIdTag>.New(),
            SuggestionKind.Highlight,
            SuggestionStatus.Adopted,
            origin,
            "Timeline test suggestion.",
            confidence: null,
            missingContext: null,
            warning: null);
        return SuggestionReference.FromAdoptedSuggestion(suggestion);
    }

    private static BridgeRequest ParseRead(EntityId<ProjectIdTag> projectId)
    {
        return ParseRequest(
            BridgeProtocol.TimelineReadMethod,
            $$"""{"projectId":"{{projectId}}"}""");
    }

    private static BridgeRequest ParseApply(
        EntityId<ProjectIdTag> projectId,
        int expectedRevision,
        string operations)
    {
        return ParseRequest(
            BridgeProtocol.TimelineApplyMethod,
            $$"""{"projectId":"{{projectId}}","expectedRevision":{{expectedRevision}},"operations":{{operations}}}""");
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

    private static string CreateRequest(string method, string parameters)
    {
        return $$"""{"version":1,"requestId":"{{RequestId}}","method":"{{method}}","params":{{parameters}}}""";
    }

    private static string ReadStatus(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(RequestId, root.GetProperty("requestId").GetString());
        JsonElement result = root.GetProperty("result");
        CollectionAssert.AreEquivalent(
            new[] { "status", "snapshot" },
            result.EnumerateObject().Select(static property => property.Name).ToArray());
        return result.GetProperty("status").GetString()!;
    }

    private static void AssertRational(
        JsonElement value,
        long numerator,
        long denominator)
    {
        Assert.AreEqual(
            numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.GetProperty("value").GetString());
        Assert.AreEqual(
            denominator.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.GetProperty("rate").GetString());
    }

    private static void AssertPrivateDetailAbsent(string response)
    {
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxProjectResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(response));
        Assert.IsFalse(response.Contains("C:\\", StringComparison.Ordinal));
        Assert.IsFalse(response.Contains("Exception", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains("command", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains("transcript", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record StoredTimeline(
        ProjectStore Store,
        Project Project,
        WorkingCopy Draft,
        StoredWorkingCopy Stored);

    private sealed class RecordingAuthorization : ITimelineBridgeAuthorization
    {
        private readonly bool denyRead;
        private readonly bool denyEdit;

        internal RecordingAuthorization(
            bool denyRead = false,
            bool denyEdit = false)
        {
            this.denyRead = denyRead;
            this.denyEdit = denyEdit;
        }

        internal int ReadDemandCount { get; private set; }

        internal int EditDemandCount { get; private set; }

        public void DemandRead()
        {
            ReadDemandCount++;
            if (denyRead)
            {
                throw new AuthorizationException(
                    AuthorizationOperation.ViewProject,
                    actingRole: null);
            }
        }

        public string DemandEdit()
        {
            EditDemandCount++;
            if (denyEdit)
            {
                throw new AuthorizationException(
                    AuthorizationOperation.CreateCompositionVersion,
                    AuthorizationRole.Editor);
            }

            return ActorId;
        }
    }

    private sealed class TimelineTestDatabase : IDisposable
    {
        internal TimelineTestDatabase()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.TimelineBridgeTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
        }

        internal string RootPath { get; }

        internal string DatabasePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}