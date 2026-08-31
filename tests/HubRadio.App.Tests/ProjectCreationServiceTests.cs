using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Profiles;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ProjectCreationServiceTests
{
    private const string AuthorizedSid =
        "S-1-5-21-1111111111-2222222222-3333333333-5101";
    private const string UnauthorizedSid =
        "S-1-5-21-1111111111-2222222222-3333333333-5102";

    [TestMethod]
    public void CatalogUsesOnlyVerifiedBundledProfileAndTemplateMetadata()
    {
        ProjectCreationCatalog catalog = LoadCatalog();

        Assert.HasCount(4, catalog.OutputProfiles);
        Assert.HasCount(4, catalog.BrandTemplates);
        Assert.IsTrue(catalog.OutputProfiles
            .Select(static option => option.Id)
            .SequenceEqual(catalog.OutputProfiles
                .Select(static option => option.Id)
                .Distinct(StringComparer.Ordinal)));
        ProjectCatalogOption firstProfile = catalog.OutputProfiles[0];
        ProjectCatalogOption firstTemplate = catalog.BrandTemplates[0];
        Assert.AreEqual(
            firstProfile.Name,
            catalog.ResolveOutputProfile(firstProfile.Id).Name);
        Assert.AreEqual(
            firstTemplate.Name,
            catalog.ResolveBrandTemplate(firstTemplate.Id).Name);

        Assert.ThrowsExactly<FormatException>(() =>
            catalog.ResolveOutputProfile(firstProfile.Id.ToUpperInvariant()));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            catalog.ResolveOutputProfile(EntityId<HubRadio.Core.Rendering.RenderProfileIdTag>
                .New()
                .ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            catalog.ResolveBrandTemplate(EntityId<HubRadio.Core.Layout.TemplateIdTag>
                .New()
                .ToString()));
    }

    [TestMethod]
    public void PayloadCodecRoundTripsIncompleteAndCompleteDraftsCanonically()
    {
        ProjectCreationCatalog catalog = LoadCatalog();
        EntityId<ProjectCreationDraftIdTag> draftId =
            EntityId<ProjectCreationDraftIdTag>.New();
        ProjectCreationDraft incomplete = ParseInput(CreateInput(catalog), draftId, catalog);

        Assert.IsFalse(incomplete.IsComplete);
        CollectionAssert.AreEqual(
            Enum.GetValues<ProjectCreationField>(),
            incomplete.MissingFields.ToArray());
        string incompletePayload = ProjectCreationPayloadCodec.Serialize(incomplete);
        Assert.AreEqual(
            incompletePayload,
            CanonicalJson.Canonicalize(incompletePayload));
        AssertDraftsEquivalent(
            incomplete,
            ProjectCreationPayloadCodec.ParseStored(incompletePayload, catalog));

        AssetCandidateRegistry registry = new();
        EntityId<AssetIdTag> candidateId = registry.RegisterSelection(@"C:\private\source.mov")!;
        ProjectCreationDraft complete = ParseInput(
            CreateCompleteInput(catalog, candidateId),
            draftId,
            catalog);
        Assert.IsTrue(complete.IsComplete);
        string completePayload = ProjectCreationPayloadCodec.Serialize(complete);
        AssertDraftsEquivalent(
            complete,
            ProjectCreationPayloadCodec.ParseStored(completePayload, catalog));
        Assert.IsFalse(completePayload.Contains(@"C:\private", StringComparison.Ordinal));
        StringAssert.Contains(completePayload, candidateId.ToString());
    }

    [TestMethod]
    public void PayloadCodecRejectsOpenWrongTypedForgedAndNonCanonicalShapes()
    {
        ProjectCreationCatalog catalog = LoadCatalog();
        EntityId<ProjectCreationDraftIdTag> draftId =
            EntityId<ProjectCreationDraftIdTag>.New();

        JsonObject extra = CreateInput(catalog);
        extra["extra"] = true;
        Assert.Throws<JsonException>(() => ParseInput(extra, draftId, catalog));

        JsonObject missing = CreateInput(catalog);
        _ = missing.Remove("language");
        Assert.Throws<JsonException>(() => ParseInput(missing, draftId, catalog));

        JsonObject wrongScope = CreateInput(catalog);
        wrongScope["publicationScope"] = "InternalOnly";
        Assert.Throws<JsonException>(() => ParseInput(wrongScope, draftId, catalog));

        JsonObject forgedProfile = CreateInput(catalog);
        forgedProfile["outputProfileIds"] = new JsonArray(
            EntityId<HubRadio.Core.Rendering.RenderProfileIdTag>.New().ToString());
        Assert.Throws<InvalidOperationException>(() =>
            ParseInput(forgedProfile, draftId, catalog));

        ProjectCreationDraft draft = ParseInput(CreateInput(catalog), draftId, catalog);
        string canonical = ProjectCreationPayloadCodec.Serialize(draft);
        Assert.Throws<JsonException>(() =>
            ProjectCreationPayloadCodec.ParseStored(" " + canonical, catalog));

        JsonObject tampered = JsonNode.Parse(canonical)!.AsObject();
        JsonArray profiles = tampered["outputProfiles"]!.AsArray();
        ProjectCatalogOption approved = catalog.OutputProfiles[0];
        profiles.Add(new JsonObject
        {
            ["id"] = approved.Id,
            ["name"] = "forged-name",
            ["profileDigest"] = new string('0', 64),
        });
        string tamperedCanonical = CanonicalJson.Canonicalize(tampered.ToJsonString());
        Assert.Throws<JsonException>(() =>
            ProjectCreationPayloadCodec.ParseStored(tamperedCanonical, catalog));
    }

    [TestMethod]
    public async Task NativeAuthorizationDeniesEveryReadAndMutationBeforeSideEffects()
    {
        using ProjectCreationFixture fixture = new(UnauthorizedSid);

        AssertAuthorizationDenied(() => fixture.Service.ListProjects());
        AssertAuthorizationDenied(() => fixture.Service.CreateDraft());
        AssertAuthorizationDenied(() => fixture.Service.LoadDraft(
            EntityId<ProjectCreationDraftIdTag>.New()));
        await Assert.ThrowsExactlyAsync<AuthorizationException>(() =>
            fixture.Service.PickSourceAsync());

        Assert.AreEqual(0, fixture.DraftStore.ListDrafts().Count);
        Assert.AreEqual(0, fixture.PickerInvocationCount);
        Assert.AreEqual(0, fixture.Registry.PendingCount);

        using ProjectCreationFixture authorizedFixture = new(AuthorizedSid);
        ProjectCreationDraftView created = authorizedFixture.Service.CreateDraft();
        EntityId<ProjectCreationDraftIdTag> draftId =
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId);
        ProjectCreationService unauthorizedService = authorizedFixture.CreateService(
            authorizedFixture.Registry,
            UnauthorizedSid);
        JsonElement input = ParseElement(CreateInput(authorizedFixture.Catalog));

        AssertAuthorizationDenied(() => unauthorizedService.SaveDraft(
            draftId,
            expectedRevision: 0,
            input));
        AssertAuthorizationDenied(() => unauthorizedService.FinalizeDraft(
            draftId,
            expectedRevision: 0,
            input));
        StoredProjectCreationDraft unchanged =
            authorizedFixture.DraftStore.GetDraft(draftId)!;
        Assert.AreEqual(0, unchanged.Revision);
        Assert.IsNull(unchanged.FinalizedProjectId);
    }

    [TestMethod]
    public async Task BridgeHandlerMapsAuthorizationAndPickerToClosedPrivateSafeResponses()
    {
        using ProjectCreationFixture deniedFixture = new(UnauthorizedSid);
        var deniedHandler = new ProjectBridgeHandler(deniedFixture.Service);
        BridgeRequest listRequest = CreateBridgeRequest(
            BridgeProtocol.ListProjectsMethod,
            new JsonObject());

        string deniedJson = await deniedHandler.HandleAsync(
            listRequest,
            CancellationToken.None);
        using (JsonDocument denied = JsonDocument.Parse(deniedJson))
        {
            JsonElement root = denied.RootElement;
            CollectionAssert.AreEquivalent(
                new[] { "version", "requestId", "method", "error" },
                root.EnumerateObject().Select(static property => property.Name).ToArray());
            Assert.AreEqual(
                "project.not_authorized",
                root.GetProperty("error").GetProperty("code").GetString());
        }

        Assert.IsFalse(deniedJson.Contains("S-1-5-", StringComparison.Ordinal));
        Assert.IsFalse(deniedJson.Contains("C:\\", StringComparison.Ordinal));
        Assert.IsFalse(deniedJson.Contains("Exception", StringComparison.Ordinal));

        using ProjectCreationFixture allowedFixture = new(AuthorizedSid);
        var allowedHandler = new ProjectBridgeHandler(allowedFixture.Service);
        string pickedJson = await allowedHandler.HandleAsync(
            CreateBridgeRequest(BridgeProtocol.PickSourceMethod, new JsonObject()),
            CancellationToken.None);
        using JsonDocument picked = JsonDocument.Parse(pickedJson);
        JsonElement result = picked.RootElement.GetProperty("result");
        CollectionAssert.AreEqual(
            new[] { "candidateId" },
            result.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.IsNotNull(result.GetProperty("candidateId").GetString());
        Assert.IsFalse(pickedJson.Contains("picked-source.mov", StringComparison.Ordinal));
        Assert.IsFalse(pickedJson.Contains("C:\\", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BridgeHandlerMapsOccAndSourceLossWithoutLeakingInternalDetails()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        var handler = new ProjectBridgeHandler(fixture.Service);
        ProjectCreationDraftView created = fixture.Service.CreateDraft();
        EntityId<ProjectCreationDraftIdTag> draftId =
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId);
        _ = fixture.Service.SaveDraft(
            draftId,
            expectedRevision: 0,
            ParseElement(CreateInput(fixture.Catalog)));

        string conflictJson = await handler.HandleAsync(
            CreateBridgeRequest(
                BridgeProtocol.SaveDraftMethod,
                CreateMutationParameters(
                    created.DraftId,
                    expectedRevision: 0,
                    CreateInput(fixture.Catalog))),
            CancellationToken.None);
        AssertBridgeError(conflictJson, "project.revision_conflict");

        EntityId<AssetIdTag> unavailableCandidate = EntityId<AssetIdTag>.New();
        string sourceLossJson = await handler.HandleAsync(
            CreateBridgeRequest(
                BridgeProtocol.FinalizeDraftMethod,
                CreateMutationParameters(
                    created.DraftId,
                    expectedRevision: 1,
                    CreateCompleteInput(fixture.Catalog, unavailableCandidate))),
            CancellationToken.None);
        AssertBridgeError(
            sourceLossJson,
            "project.source_reselection_required");
        Assert.IsNull(fixture.DraftStore.GetDraft(draftId)!.FinalizedProjectId);
    }

    [TestMethod]
    public void IncompleteDraftCanBeSavedListedLoadedAndDetectsStaleRevision()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created = fixture.Service.CreateDraft();
        Assert.AreEqual(0, created.Revision);
        Assert.HasCount(10, created.MissingFields);
        Assert.AreEqual("notSelected", created.SourceStatus);

        JsonObject input = CreateInput(fixture.Catalog);
        input["contentName"] = "<script>alert('x')</script>";
        input["publicationScope"] = "internalOnly";
        ProjectCreationDraftView saved = fixture.Service.SaveDraft(
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId),
            expectedRevision: 0,
            ParseElement(input));

        Assert.AreEqual(1, saved.Revision);
        Assert.AreEqual("<script>alert('x')</script>", saved.ContentName);
        Assert.AreEqual("internalOnly", saved.PublicationScope);
        Assert.IsFalse(saved.IsFinalized);
        Assert.HasCount(8, saved.MissingFields);
        ProjectCreationDraftView loaded = fixture.Service.LoadDraft(
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId));
        Assert.AreEqual(saved.DraftId, loaded.DraftId);
        Assert.AreEqual(saved.Revision, loaded.Revision);
        Assert.AreEqual(saved.ContentName, loaded.ContentName);
        Assert.AreEqual(saved.PublicationScope, loaded.PublicationScope);
        Assert.AreEqual(saved.SourceStatus, loaded.SourceStatus);
        CollectionAssert.AreEqual(
            saved.MissingFields.ToArray(),
            loaded.MissingFields.ToArray());

        ProjectCreationOverview overview = fixture.Service.ListProjects();
        Assert.IsTrue(overview.CanCreate);
        Assert.HasCount(1, overview.Projects);
        Assert.AreEqual("draft", overview.Projects[0].State);
        Assert.AreEqual("<script>alert('x')</script>", overview.Projects[0].ContentName);
        Assert.IsNull(overview.Projects[0].ProjectId);

        Assert.ThrowsExactly<StoreConcurrencyException>(() =>
            fixture.Service.SaveDraft(
                EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId),
                expectedRevision: 0,
                ParseElement(input)));
        Assert.AreEqual(1, fixture.Service.LoadDraft(
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId)).Revision);
    }

    [TestMethod]
    public async Task PickerReturnsOnlyOpaqueRegisteredTokenAndCancellationReturnsNull()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);

        EntityId<AssetIdTag>? selected = await fixture.Service.PickSourceAsync();
        Assert.IsNotNull(selected);
        Assert.IsTrue(fixture.Registry.IsAvailable(selected));
        Assert.IsFalse(selected.ToString().Contains("source", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, fixture.PickerInvocationCount);

        fixture.CancelNextPick = true;
        EntityId<AssetIdTag>? canceled = await fixture.Service.PickSourceAsync();
        Assert.IsNull(canceled);
        Assert.AreEqual(2, fixture.PickerInvocationCount);
        Assert.AreEqual(1, fixture.Registry.PendingCount);
    }

    [TestMethod]
    public void FinalizeRequiresCompleteInputAndLiveSourceThenBecomesTerminal()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created = fixture.Service.CreateDraft();
        EntityId<ProjectCreationDraftIdTag> draftId =
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId);

        ProjectCreationException incomplete =
            Assert.ThrowsExactly<ProjectCreationException>(() =>
                fixture.Service.FinalizeDraft(
                    draftId,
                    expectedRevision: 0,
                    ParseElement(CreateInput(fixture.Catalog))));
        Assert.AreEqual(ProjectCreationFailureReason.DraftIncomplete, incomplete.Reason);

        EntityId<AssetIdTag> unavailable = EntityId<AssetIdTag>.New();
        JsonObject unavailableInput = CreateCompleteInput(fixture.Catalog, unavailable);
        ProjectCreationException reselection =
            Assert.ThrowsExactly<ProjectCreationException>(() =>
                fixture.Service.FinalizeDraft(
                    draftId,
                    expectedRevision: 0,
                    ParseElement(unavailableInput)));
        Assert.AreEqual(
            ProjectCreationFailureReason.SourceReselectionRequired,
            reselection.Reason);
        Assert.IsNull(fixture.DraftStore.GetDraft(draftId)!.FinalizedProjectId);

        EntityId<AssetIdTag> available =
            fixture.Registry.RegisterSelection(@"C:\private\final-source.mov")!;
        JsonObject completeInput = CreateCompleteInput(fixture.Catalog, available);
        ProjectCreationDraftView finalized = fixture.Service.FinalizeDraft(
            draftId,
            expectedRevision: 0,
            ParseElement(completeInput));

        Assert.IsTrue(finalized.IsFinalized);
        Assert.AreEqual(1, finalized.Revision);
        Assert.IsNotNull(finalized.ProjectId);
        Assert.AreEqual("available", finalized.SourceStatus);
        Assert.AreEqual(0, finalized.MissingFields.Count);
        Assert.IsTrue(fixture.Registry.IsAvailable(available));
        Assert.IsNotNull(new ProjectStore(fixture.DatabasePath).GetProject(
            EntityId<ProjectIdTag>.Parse(finalized.ProjectId)));

        ProjectCreationException terminal =
            Assert.ThrowsExactly<ProjectCreationException>(() =>
                fixture.Service.SaveDraft(
                    draftId,
                    expectedRevision: 1,
                    ParseElement(completeInput)));
        Assert.AreEqual(
            ProjectCreationFailureReason.DraftAlreadyFinalized,
            terminal.Reason);
        Assert.AreEqual("finalized", fixture.Service.ListProjects().Projects[0].State);
    }

    [TestMethod]
    public void StoredCandidateBecomesTruthfulReselectionStatusAfterRegistryLoss()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created = fixture.Service.CreateDraft();
        EntityId<AssetIdTag> available =
            fixture.Registry.RegisterSelection(@"C:\private\restart.mov")!;
        JsonObject input = CreateInput(fixture.Catalog);
        input["sourceCandidateId"] = available.ToString();
        ProjectCreationDraftView saved = fixture.Service.SaveDraft(
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId),
            expectedRevision: 0,
            ParseElement(input));
        Assert.AreEqual("available", saved.SourceStatus);

        ProjectCreationService restartedService = fixture.CreateService(
            new AssetCandidateRegistry());
        ProjectCreationDraftView restarted = restartedService.LoadDraft(
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId));
        Assert.AreEqual("reselectionRequired", restarted.SourceStatus);
        Assert.AreEqual(available.ToString(), restarted.SourceCandidateId);
        Assert.AreEqual(
            "reselectionRequired",
            restartedService.ListProjects().Projects[0].SourceStatus);
    }

    private static void AssertAuthorizationDenied(Action action)
    {
        AuthorizationException exception =
            Assert.ThrowsExactly<AuthorizationException>(action);
        Assert.IsTrue(exception.Operation is
            AuthorizationOperation.ViewProject or AuthorizationOperation.CreateProject);
    }

    private static ProjectCreationDraft ParseInput(
        JsonObject input,
        EntityId<ProjectCreationDraftIdTag> draftId,
        ProjectCreationCatalog catalog) =>
        ProjectCreationPayloadCodec.ParseInput(ParseElement(input), draftId, catalog);

    private static JsonObject CreateCompleteInput(
        ProjectCreationCatalog catalog,
        EntityId<AssetIdTag> candidateId)
    {
        JsonObject input = CreateInput(catalog);
        input["sourceCandidateId"] = candidateId.ToString();
        input["contentName"] = "Hubらじお 第42回";
        input["purpose"] = "社内の知識共有";
        input["audience"] = "開発者";
        input["publicationScope"] = "specificGroupOnly";
        input["outputProfileIds"] = new JsonArray(catalog.OutputProfiles[0].Id);
        input["language"] = "ja-JP";
        input["performerNames"] = new JsonArray("出演者A", "出演者B");
        input["deadlineSelection"] = new JsonObject
        {
            ["hasDeadline"] = false,
            ["value"] = null,
        };
        input["brandTemplateId"] = catalog.BrandTemplates[0].Id;
        return input;
    }

    private static JsonObject CreateInput(ProjectCreationCatalog catalog) => new()
    {
        ["sourceCandidateId"] = null,
        ["contentName"] = null,
        ["purpose"] = null,
        ["audience"] = null,
        ["publicationScope"] = null,
        ["outputProfileIds"] = new JsonArray(),
        ["language"] = null,
        ["performerNames"] = new JsonArray(),
        ["deadlineSelection"] = null,
        ["brandTemplateId"] = null,
    };

    private static JsonElement ParseElement(JsonObject value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static BridgeRequest CreateBridgeRequest(
        string method,
        JsonObject parameters) =>
        new(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            method,
            ParseElement(parameters));

    private static JsonObject CreateMutationParameters(
        string draftId,
        int expectedRevision,
        JsonObject input) =>
        new()
        {
            ["draftId"] = draftId,
            ["expectedRevision"] = expectedRevision,
            ["input"] = input,
        };

    private static void AssertBridgeError(string responseJson, string expectedCode)
    {
        using JsonDocument response = JsonDocument.Parse(responseJson);
        JsonElement root = response.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "error" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        JsonElement error = root.GetProperty("error");
        CollectionAssert.AreEquivalent(
            new[] { "code", "message" },
            error.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(expectedCode, error.GetProperty("code").GetString());
        Assert.IsFalse(responseJson.Contains("S-1-5-", StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains("C:\\", StringComparison.Ordinal));
        Assert.IsFalse(responseJson.Contains("Exception", StringComparison.Ordinal));
    }

    private static void AssertDraftsEquivalent(
        ProjectCreationDraft expected,
        ProjectCreationDraft actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.SourceCandidateId, actual.SourceCandidateId);
        Assert.AreEqual(expected.ContentName, actual.ContentName);
        Assert.AreEqual(expected.Purpose, actual.Purpose);
        Assert.AreEqual(expected.Audience, actual.Audience);
        Assert.AreEqual(expected.PublicationScope, actual.PublicationScope);
        CollectionAssert.AreEqual(
            expected.OutputProfiles.ToArray(),
            actual.OutputProfiles.ToArray());
        Assert.AreEqual(expected.Language, actual.Language);
        CollectionAssert.AreEqual(
            expected.PerformerNames.ToArray(),
            actual.PerformerNames.ToArray());
        Assert.AreEqual(expected.DeadlineSelection, actual.DeadlineSelection);
        Assert.AreEqual(expected.BrandTemplate, actual.BrandTemplate);
        CollectionAssert.AreEqual(
            expected.MissingFields.ToArray(),
            actual.MissingFields.ToArray());
    }

    private static ProjectCreationCatalog LoadCatalog() =>
        ProjectCreationCatalog.Load(ResolveRepositoryPath(
            "src/HubRadio.App/Profiles",
            expectDirectory: true));

    private static string ResolveRepositoryPath(
        string relativePath,
        bool expectDirectory = false)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                bool exists = expectDirectory ? Directory.Exists(path) : File.Exists(path);
                if (!exists)
                {
                    throw new FileNotFoundException(relativePath, path);
                }

                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve the repository root.");
    }

    private sealed class ProjectCreationFixture : IDisposable
    {
        private readonly string actorSid;
        private readonly FixedTimeProvider clock = new(
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

        internal ProjectCreationFixture(string actorSid)
        {
            this.actorSid = actorSid;
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ProjectCreationServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "hubradio.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
            DraftStore = new ProjectCreationDraftStore(DatabasePath);
            Registry = new AssetCandidateRegistry();
            Catalog = LoadCatalog();

            var authorization = new AuthorizationService(
                new RoleAssignmentStore(DatabasePath));
            Assert.IsTrue(authorization.BootstrapInitialRoles(AuthorizedSid));
            Authorization = authorization;
            Service = CreateService(Registry);
        }

        private string RootPath { get; }

        internal string DatabasePath { get; }

        internal ProjectCreationDraftStore DraftStore { get; }

        internal AssetCandidateRegistry Registry { get; }

        internal ProjectCreationCatalog Catalog { get; }

        internal AuthorizationService Authorization { get; }

        internal ProjectCreationService Service { get; }

        internal int PickerInvocationCount { get; private set; }

        internal bool CancelNextPick { get; set; }

        internal ProjectCreationService CreateService(
            AssetCandidateRegistry registry,
            string? currentActorSid = null) =>
            new(
                DraftStore,
                Authorization,
                registry,
                Catalog,
                () => currentActorSid ?? actorSid,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PickerInvocationCount++;
                    if (CancelNextPick)
                    {
                        CancelNextPick = false;
                        return Task.FromResult<EntityId<AssetIdTag>?>(null);
                    }

                    return Task.FromResult(
                        registry.RegisterSelection(@"C:\private\picked-source.mov"));
                },
                clock);

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}