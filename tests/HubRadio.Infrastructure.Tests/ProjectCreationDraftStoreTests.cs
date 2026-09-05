using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class ProjectCreationDraftStoreTests
{
    private const string DigestHex =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset InitialTime =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public void CreateGetAndListPreserveCanonicalDraftsInDeterministicOrder()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft first = CreateDraft();
        ProjectCreationDraft second = CreateDraft();

        StoredProjectCreationDraft firstStored = store.CreateDraft(
            first,
            " { \"z\" : 2, \"a\" : 1 } ",
            InitialTime);
        StoredProjectCreationDraft secondStored = store.CreateDraft(
            second,
            "{\"value\":2}",
            InitialTime.AddMinutes(1));

        Assert.AreEqual(first.Id, firstStored.Id);
        Assert.AreEqual(0, firstStored.Revision);
        Assert.AreEqual("{\"a\":1,\"z\":2}", firstStored.CanonicalJson);
        Assert.AreEqual(InitialTime, firstStored.UpdatedAt);
        Assert.IsFalse(firstStored.IsFinalized);
        Assert.IsNull(firstStored.FinalizedProjectId);
        Assert.IsNull(firstStored.FinalizedAt);
        Assert.IsNull(store.GetDraft(EntityId<ProjectCreationDraftIdTag>.New()));
        Assert.AreEqual(firstStored.CanonicalJson, store.GetDraft(first.Id)!.CanonicalJson);

        IReadOnlyList<StoredProjectCreationDraft> listed = store.ListDrafts();
        CollectionAssert.AreEqual(
            new[] { secondStored.Id, firstStored.Id },
            listed.Select(static draft => draft.Id).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StoredProjectCreationDraft>)listed).Clear());
    }

    [TestMethod]
    public async Task ConcurrentSavesAllowExactlyOneRevisionWinner()
    {
        using StoreTestDatabase database = new();
        var firstStore = new ProjectCreationDraftStore(database.DatabasePath);
        var secondStore = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft draft = CreateDraft();
        firstStore.CreateDraft(draft, "{\"value\":0}", InitialTime);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(() => TrySave(firstStore, draft, 1, release));
        Task<bool> second = Task.Run(() => TrySave(secondStore, draft, 2, release));
        release.Set();
        bool[] outcomes = await Task.WhenAll(first, second);

        Assert.AreEqual(1, outcomes.Count(static value => value));
        StoredProjectCreationDraft stored = firstStore.GetDraft(draft.Id)!;
        Assert.AreEqual(1, stored.Revision);
        Assert.IsTrue(stored.CanonicalJson is "{\"value\":1}" or "{\"value\":2}");
        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => firstStore.SaveDraft(
                draft,
                "{\"value\":3}",
                expectedRevision: 0,
                InitialTime.AddMinutes(2)));
        Assert.AreEqual("project creation draft", stale.EntityKind);
        Assert.AreEqual(draft.Id.ToString(), stale.EntityId);
        Assert.AreEqual(0, stale.ExpectedRevision);
        Assert.AreEqual(1, stale.ActualRevision);
    }

    [TestMethod]
    public void SaveRejectsReverseTimeAndLeavesStoredStateUntouched()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft draft = CreateDraft();
        store.CreateDraft(draft, "{\"safe\":true}", InitialTime);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.SaveDraft(
                draft,
                "{\"safe\":false}",
                expectedRevision: 0,
                InitialTime.AddTicks(-1)));

        StoredProjectCreationDraft stored = store.GetDraft(draft.Id)!;
        Assert.AreEqual(0, stored.Revision);
        Assert.AreEqual("{\"safe\":true}", stored.CanonicalJson);
        Assert.AreEqual(InitialTime, stored.UpdatedAt);
    }

    [TestMethod]
    public void FinalizeAtomicallyCreatesTruthfulProjectAndClosesDraft()
    {
        using StoreTestDatabase database = new();
        var draftStore = new ProjectCreationDraftStore(database.DatabasePath);
        var projectStore = new ProjectStore(database.DatabasePath);
        DateTimeOffset deadline = InitialTime.AddMonths(1);
        ProjectCreationDraft draft = CreateDraft(
            deadlineSelection: new ProjectDeadlineSelection(deadline));
        Project project = CreateProjectFromDraft(draft);
        draftStore.CreateDraft(draft, "{\"state\":\"draft\"}", InitialTime);

        StoredProjectCreationDraft finalized = draftStore.FinalizeDraft(
            draft,
            project,
            "{\"state\":\"complete\"}",
            expectedRevision: 0,
            InitialTime.AddMinutes(1));

        Assert.AreEqual(1, finalized.Revision);
        Assert.IsTrue(finalized.IsFinalized);
        Assert.AreEqual(project.Id, finalized.FinalizedProjectId);
        Assert.AreEqual(InitialTime.AddMinutes(1), finalized.FinalizedAt);
        Assert.AreEqual("{\"state\":\"complete\"}", finalized.CanonicalJson);
        StoredProject storedProject = projectStore.GetProject(project.Id)!;
        Assert.AreEqual(0, storedProject.Revision);
        Assert.AreEqual(draft.PublicationScope, storedProject.PublicationScope);
        Assert.AreEqual(deadline, storedProject.Deadline);
        using JsonDocument projectPayload = JsonDocument.Parse(
            storedProject.CanonicalJson);
        JsonElement projectRoot = projectPayload.RootElement;
        Assert.AreEqual(project.Id.ToString(), projectRoot.GetProperty("id").GetString());
        Assert.AreEqual(
            project.ContentName,
            projectRoot.GetProperty("contentName").GetString());
        Assert.AreEqual(
            "internalOnly",
            projectRoot.GetProperty("publicationScope").GetString());
        Assert.AreEqual(0, projectRoot.GetProperty("assets").GetArrayLength());
        Assert.AreEqual(0, projectRoot.GetProperty("performers").GetArrayLength());
        Assert.AreNotEqual(finalized.CanonicalJson, storedProject.CanonicalJson);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => draftStore.SaveDraft(
                draft,
                "{\"changed\":true}",
                expectedRevision: 1,
                InitialTime.AddMinutes(2)));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => draftStore.FinalizeDraft(
                draft,
                CreateProjectFromDraft(draft),
                "{\"changed\":true}",
                expectedRevision: 1,
                InitialTime.AddMinutes(2)));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM projects;"));
    }

    [TestMethod]
    public void FinalizeRejectsIncompleteOrMismatchedAggregateBeforeMutation()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft incomplete = CreateDraft(
            includeSourceCandidate: false,
            contentName: null,
            outputProfiles: []);
        store.CreateDraft(incomplete, "{\"draft\":true}", InitialTime);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.FinalizeDraft(
                incomplete,
                new Project(
                    EntityId<ProjectIdTag>.New(),
                    "Content",
                    "Purpose",
                    "Audience",
                    PublicationScope.InternalOnly,
                    null,
                    [],
                    []),
                "{\"attempt\":true}",
                expectedRevision: 0,
                InitialTime.AddMinutes(1)));

        ProjectCreationDraft complete = CreateDraft();
        store.CreateDraft(complete, "{\"draft\":true}", InitialTime);
        Project mismatched = new(
            EntityId<ProjectIdTag>.New(),
            "Different content",
            complete.Purpose!,
            complete.Audience!,
            complete.PublicationScope!.Value,
            complete.DeadlineSelection!.Deadline,
            [],
            []);
        AssertParamName<ArgumentException>(
            "project",
            () => store.FinalizeDraft(
                complete,
                mismatched,
                "{\"attempt\":true}",
                expectedRevision: 0,
                InitialTime.AddMinutes(1)));

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM projects;"));
        Assert.AreEqual(0, store.GetDraft(incomplete.Id)!.Revision);
        Assert.AreEqual(0, store.GetDraft(complete.Id)!.Revision);
    }

    [TestMethod]
    public void FinalizeRejectsProjectPayloadThatDoesNotMatchAggregate()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft draft = CreateDraft();
        Project project = CreateProjectFromDraft(draft);
        store.CreateDraft(draft, "{\"state\":\"draft\"}", InitialTime);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => store.FinalizeDraft(
                draft,
                project,
                "{\"state\":\"complete\"}",
                "{\"forgedProject\":true}",
                expectedRevision: 0,
                InitialTime.AddMinutes(1)));

        Assert.AreEqual("projectPayloadJson", exception.ParamName);
        Assert.IsNull(new ProjectStore(database.DatabasePath).GetProject(project.Id));
        Assert.AreEqual(0, store.GetDraft(draft.Id)!.Revision);
        Assert.IsFalse(store.GetDraft(draft.Id)!.IsFinalized);
    }

    [TestMethod]
    public void FinalizeProjectInsertOrDraftUpdateFailureRollsBackBothRows()
    {
        using StoreTestDatabase database = new();
        var projectStore = new ProjectStore(database.DatabasePath);
        var draftStore = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft duplicateDraft = CreateDraft();
        Project duplicateProject = CreateProjectFromDraft(duplicateDraft);
        projectStore.CreateProject(duplicateProject, "{\"existing\":true}", InitialTime);
        draftStore.CreateDraft(duplicateDraft, "{\"draft\":true}", InitialTime);

        Assert.ThrowsExactly<SqliteException>(
            () => draftStore.FinalizeDraft(
                duplicateDraft,
                duplicateProject,
                "{\"attempt\":true}",
                expectedRevision: 0,
                InitialTime.AddMinutes(1)));
        Assert.AreEqual(0, draftStore.GetDraft(duplicateDraft.Id)!.Revision);
        Assert.IsFalse(draftStore.GetDraft(duplicateDraft.Id)!.IsFinalized);
        Assert.AreEqual("{\"existing\":true}", projectStore.GetProject(duplicateProject.Id)!.CanonicalJson);

        ProjectCreationDraft triggeredDraft = CreateDraft();
        Project triggeredProject = CreateProjectFromDraft(triggeredDraft);
        draftStore.CreateDraft(triggeredDraft, "{\"draft\":true}", InitialTime);
        database.ExecuteNonQuery(
            """
            CREATE TRIGGER fail_project_creation_draft_finalize
            BEFORE UPDATE ON project_creation_drafts
            WHEN NEW.finalized_project_id IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'injected finalize failure');
            END;
            """);

        Assert.ThrowsExactly<SqliteException>(
            () => draftStore.FinalizeDraft(
                triggeredDraft,
                triggeredProject,
                "{\"attempt\":true}",
                expectedRevision: 0,
                InitialTime.AddMinutes(1)));
        Assert.IsNull(projectStore.GetProject(triggeredProject.Id));
        Assert.AreEqual(0, draftStore.GetDraft(triggeredDraft.Id)!.Revision);
        Assert.IsFalse(draftStore.GetDraft(triggeredDraft.Id)!.IsFinalized);
    }

    [TestMethod]
    public void FinalizeWithSettingsAtomicallyBindsInitialAndCurrentSnapshots()
    {
        using StoreTestDatabase database = new();
        var draftStore = new ProjectCreationDraftStore(database.DatabasePath);
        var settingsStore = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = CreateSettingsProfile();
        _ = settingsStore.RegisterProfile(Encoding.UTF8.GetBytes(profile.CanonicalJson));
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot();
        ProjectCreationDraft draft = CreateDraft();
        Project project = CreateProjectFromDraft(draft);
        draftStore.CreateDraft(draft, "{\"draft\":true}", InitialTime);

        StoredProjectCreationDraft finalized = draftStore.FinalizeDraft(
            draft,
            project,
            "{\"draft\":false}",
            expectedRevision: 0,
            InitialTime.AddMinutes(1),
            snapshot);

        Assert.IsTrue(finalized.IsFinalized);
        ProjectSettingsResult settings = settingsStore.GetProjectSettings(project.Id);
        Assert.IsTrue(settings.IsBound);
        Assert.AreEqual(
            snapshot.ContentDigest,
            settings.Settings!.InitialSnapshot.ContentDigest);
        Assert.AreEqual(
            snapshot.ContentDigest,
            settings.Settings.CurrentSnapshot.ContentDigest);
        Assert.AreEqual(0, settings.Settings.Revision);
    }

    [TestMethod]
    public void FinalizeSettingsOrDraftFailureRollsBackEveryRow()
    {
        using StoreTestDatabase database = new();
        var draftStore = new ProjectCreationDraftStore(database.DatabasePath);
        var settingsStore = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = CreateSettingsProfile();
        _ = settingsStore.RegisterProfile(Encoding.UTF8.GetBytes(profile.CanonicalJson));
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot();
        ProjectCreationDraft draft = CreateDraft();
        Project project = CreateProjectFromDraft(draft);
        draftStore.CreateDraft(draft, "{\"draft\":true}", InitialTime);
        database.ExecuteNonQuery(
            """
            CREATE TRIGGER fail_source_only_settings_bind
            BEFORE INSERT ON project_default_settings
            BEGIN
                SELECT RAISE(ABORT, 'injected settings failure');
            END;
            """);

        Assert.ThrowsExactly<SqliteException>(() => draftStore.FinalizeDraft(
            draft,
            project,
            "{\"draft\":false}",
            expectedRevision: 0,
            InitialTime.AddMinutes(1),
            snapshot));

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM projects;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM project_default_settings;"));
        StoredProjectCreationDraft unchanged = draftStore.GetDraft(draft.Id)!;
        Assert.AreEqual(0, unchanged.Revision);
        Assert.IsFalse(unchanged.IsFinalized);
    }

    [TestMethod]
    public void PayloadValidationIsBoundedCanonicalAndPrivateSafe()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft draft = CreateDraft();
        string oversized = "{\"value\":\"" +
            new string('x', ProjectCreationDraftStore.MaximumPayloadUtf8Bytes) +
            "\"}";
        string[] invalid =
        [
            oversized,
            "{\"key\":1,\"key\":2}",
            "{\"value\":\"\ud800\"}",
            "[]",
        ];

        foreach (string payload in invalid)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => store.CreateDraft(draft, payload, InitialTime));
            Assert.AreEqual("payloadJson", exception.ParamName);
            Assert.IsFalse(exception.Message.Contains(payload, StringComparison.Ordinal));
        }

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM project_creation_drafts;"));
    }

    [TestMethod]
    public void StoredCorruptionFailsClosed()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft draft = CreateDraft();
        store.CreateDraft(draft, "{\"safe\":true}", InitialTime);

        database.ExecuteNonQuery(
            "UPDATE project_creation_drafts SET payload = $payload WHERE id = $id;",
            ("$payload", Encoding.UTF8.GetBytes("{ \"safe\" : true }")),
            ("$id", draft.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetDraft(draft.Id));

        database.ExecuteNonQuery(
            "UPDATE project_creation_drafts SET payload = $payload, updated_at = $updated_at WHERE id = $id;",
            ("$payload", Encoding.UTF8.GetBytes("{\"safe\":true}")),
            ("$updated_at", "not-a-timestamp"),
            ("$id", draft.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetDraft(draft.Id));
    }

    [TestMethod]
    public void QuotedPayloadIsBoundAsDataWithoutChangingSchema()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectCreationDraftStore(database.DatabasePath);
        ProjectCreationDraft draft = CreateDraft();
        const string payload =
            "{\"text\":\"Robert'); DROP TABLE project_creation_drafts;--\"}";

        store.CreateDraft(draft, payload, InitialTime);

        Assert.AreEqual(
            CanonicalJson.Canonicalize(payload),
            store.GetDraft(draft.Id)!.CanonicalJson);
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'project_creation_drafts';"));
    }

    private static bool TrySave(
        ProjectCreationDraftStore store,
        ProjectCreationDraft draft,
        int value,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.SaveDraft(
                draft,
                $"{{\"value\":{value}}}",
                expectedRevision: 0,
                InitialTime.AddMinutes(1));
            return true;
        }
        catch (StoreConcurrencyException)
        {
            return false;
        }
    }

    private static ProjectCreationDraft CreateDraft(
        bool includeSourceCandidate = true,
        string? contentName = "Hub Radio Episode",
        IReadOnlyList<ProjectOutputProfileReference>? outputProfiles = null,
        ProjectDeadlineSelection? deadlineSelection = null)
    {
        return new ProjectCreationDraft(
            EntityId<ProjectCreationDraftIdTag>.New(),
            includeSourceCandidate ? EntityId<AssetIdTag>.New() : null,
            contentName,
            "Explain the release",
            "Internal engineering audience",
            PublicationScope.InternalOnly,
            outputProfiles ?? [new ProjectOutputProfileReference(
                EntityId<RenderProfileIdTag>.New(),
                "long-form-1280x720",
                new ContentDigest(DigestHex))],
            "ja-JP",
            ["Host A"],
            deadlineSelection ?? new ProjectDeadlineSelection(deadline: null),
            new TemplateReference(
                EntityId<TemplateIdTag>.New(),
                "development-neutral-long-form",
                new ContentDigest(DigestHex)));
    }

    private static Project CreateProjectFromDraft(
        ProjectCreationDraft draft,
        EntityId<ProjectIdTag>? projectId = null)
    {
        return new Project(
            projectId ?? EntityId<ProjectIdTag>.New(),
            draft.ContentName!,
            draft.Purpose!,
            draft.Audience!,
            draft.PublicationScope!.Value,
            draft.DeadlineSelection!.Deadline,
            [],
            []);
    }

    private static DefaultSettingsProfile CreateSettingsProfile()
    {
        return DefaultSettingsProfile.Create(
            EntityId<DefaultSettingsProfileIdTag>.New(),
            "source-only-test-profile",
            new SettingsProfileVersion(1, 0, 0),
            [new DefaultSettingDefinition(
                "project.publicationScope",
                DefaultSettingGroup.ProjectAndOutput,
                isAdvanced: false,
                "Publication scope",
                "Test-only internal publication scope.",
                DefaultSettingUnit.None,
                DefaultSettingValue.FromText("internalOnly"),
                minimum: null,
                maximum: null,
                allowedTextValues: ["internalOnly"],
                evidence: [new DefaultSettingEvidence(
                    DefaultSettingEvidenceKind.ProductDecision,
                    "productDecision")])]);
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}