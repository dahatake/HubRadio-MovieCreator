using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Policy;
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
using Microsoft.Data.Sqlite;

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
        await Assert.ThrowsExactlyAsync<AuthorizationException>(() =>
            fixture.Service.CreateSourceOnlyDraftAsync());
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
    public async Task SourceOnlyCreationDerivesCompletePrivateSafeDraftAndCancelHasNoSideEffect()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);

        ProjectCreationDraftView created =
            await fixture.Service.CreateSourceOnlyDraftAsync();

        Assert.AreEqual("picked-source", created.ContentName);
        Assert.AreEqual("internalOnly", created.PublicationScope);
        Assert.HasCount(0, created.MissingFields);
        Assert.AreEqual("available", created.SourceStatus);
        Assert.IsNotNull(created.SourceOnly);
        Assert.AreEqual(
            fixture.SettingsStore.GetGlobalSettings()!.Snapshot.ContentDigest.Hex,
            created.SourceOnly.SettingsSnapshotDigest);
        Assert.IsGreaterThan(0, created.SourceOnly.ArtifactSet.Count);
        Assert.AreEqual(1, fixture.Registry.PendingCount);
        Assert.IsFalse(JsonSerializer.Serialize(created).Contains(
            fixture.PickerSourcePath,
            StringComparison.Ordinal));

        fixture.CancelNextPick = true;
        SourceOnlyProjectCreationException canceled =
            await Assert.ThrowsExactlyAsync<SourceOnlyProjectCreationException>(() =>
                fixture.Service.CreateSourceOnlyDraftAsync());
        Assert.AreEqual(
            SourceOnlyProjectCreationFailureReason.SourceSelectionCanceled,
            canceled.Reason);
        Assert.HasCount(1, fixture.DraftStore.ListDrafts());
        Assert.AreEqual(1, fixture.Registry.PendingCount);
    }

    [TestMethod]
    public async Task SourceOnlyFinalizeUsesStoredSnapshotAfterGlobalDriftAndConsumesCandidateOnce()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created =
            await fixture.Service.CreateSourceOnlyDraftAsync();
        EntityId<ProjectCreationDraftIdTag> draftId =
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId);
        var beforeDrift = fixture.SettingsStore.GetGlobalSettings()!;
        HubRadio.Core.Settings.DefaultSettingSelection toggle = beforeDrift.Snapshot.Values
            .First(static selection =>
                selection.Value.Kind == HubRadio.Core.Settings.DefaultSettingValueKind.Boolean);
        HubRadio.Core.Settings.DefaultSettingsProfile profile = fixture.SettingsStore.GetProfile(
            beforeDrift.Snapshot.ProfileId,
            beforeDrift.Snapshot.ProfileVersion)!;
        HubRadio.Core.Settings.DefaultSettingsSnapshot changed = profile.CreateSnapshot(
            beforeDrift.Snapshot.Values.Select(selection =>
                string.Equals(selection.Key, toggle.Key, StringComparison.Ordinal)
                    ? new HubRadio.Core.Settings.DefaultSettingSelection(
                        selection.Key,
                        HubRadio.Core.Settings.DefaultSettingValue.FromBoolean(
                            !selection.Value.BooleanValue!.Value))
                    : selection).ToArray());
        _ = fixture.SettingsStore.UpdateGlobalSettings(
            changed,
            beforeDrift.Revision,
            beforeDrift.UpdatedAt.AddMinutes(1));

        ProjectCreationDraftView finalized = fixture.Service.FinalizeDraft(
            draftId,
            created.Revision,
            ParseElement(CreateInput(created)));

        Assert.IsTrue(finalized.IsFinalized);
        Assert.AreEqual(1, fixture.Registry.PendingCount);
        Assert.IsFalse(fixture.Registry.IsAvailable(
            EntityId<AssetIdTag>.Parse(finalized.SourceCandidateId!)));
        Assert.IsTrue(fixture.Registry.IsClaimable(
            EntityId<AssetIdTag>.Parse(finalized.SourceCandidateId!)));
        var projectId = EntityId<ProjectIdTag>.Parse(finalized.ProjectId!);
        StoredProject storedProject = new ProjectStore(fixture.DatabasePath)
            .GetProject(projectId)!;
        var expectedProject = new Project(
            projectId,
            finalized.ContentName!,
            finalized.Purpose!,
            finalized.Audience!,
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        Assert.AreEqual(
            ProjectSnapshotJson.WriteProject(expectedProject),
            storedProject.CanonicalJson);
        Assert.AreNotEqual(
            fixture.DraftStore.GetDraft(draftId)!.CanonicalJson,
            storedProject.CanonicalJson);
        ProjectSettingsResult settings = fixture.SettingsStore.GetProjectSettings(projectId);
        Assert.IsTrue(settings.IsBound);
        Assert.AreEqual(
            created.SourceOnly!.SettingsSnapshotDigest,
            settings.Settings!.InitialSnapshot.ContentDigest.Hex);
        Assert.AreNotEqual(
            changed.ContentDigest,
            settings.Settings.InitialSnapshot.ContentDigest);
        Assert.IsNull(new SourceOnlyExecutionStore(fixture.DatabasePath).GetByDraft(draftId));

        ProjectCreationException duplicate =
            Assert.ThrowsExactly<ProjectCreationException>(() =>
                fixture.Service.FinalizeDraft(
                    draftId,
                    finalized.Revision,
                    ParseElement(CreateInput(finalized))));
        Assert.AreEqual(
            ProjectCreationFailureReason.DraftAlreadyFinalized,
            duplicate.Reason);
        Assert.IsTrue(fixture.Registry.TryClaim(
            EntityId<AssetIdTag>.Parse(finalized.SourceCandidateId!),
            out string? handedOffPath));
        Assert.AreEqual(fixture.PickerSourcePath, handedOffPath);
        Assert.AreEqual(0, fixture.Registry.PendingCount);
    }

    [TestMethod]
    public async Task SourceOnlyFinalizeAcceptsOnlyAReselectedOpaqueCandidateAfterRegistryLoss()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created =
            await fixture.Service.CreateSourceOnlyDraftAsync();
        EntityId<ProjectCreationDraftIdTag> draftId =
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId);
        var replacementRegistry = new AssetCandidateRegistry();
        ProjectCreationService restartedService = fixture.CreateService(
            replacementRegistry);

        ProjectCreationDraftView stale = restartedService.LoadDraft(draftId);
        Assert.AreEqual("reselectionRequired", stale.SourceStatus);
        EntityId<AssetIdTag> replacement =
            await restartedService.PickSourceAsync()
            ?? throw new AssertFailedException(
                "A replacement source must produce an opaque candidate.");
        JsonObject input = CreateInput(stale);
        input["sourceCandidateId"] = replacement.ToString();
        input["purpose"] = "changed purpose";

        Assert.ThrowsExactly<ArgumentException>(() =>
            restartedService.FinalizeDraft(
                draftId,
                stale.Revision,
                ParseElement(input)));
        Assert.IsTrue(replacementRegistry.IsAvailable(replacement));
        input["purpose"] = stale.Purpose;

        ProjectCreationDraftView finalized = restartedService.FinalizeDraft(
            draftId,
            stale.Revision,
            ParseElement(input));

        Assert.IsTrue(finalized.IsFinalized);
        Assert.AreEqual(replacement.ToString(), finalized.SourceCandidateId);
        Assert.AreEqual("available", finalized.SourceStatus);
        Assert.AreEqual(1, replacementRegistry.PendingCount);
        Assert.IsFalse(replacementRegistry.IsAvailable(replacement));
        Assert.IsTrue(replacementRegistry.IsClaimable(replacement));
        Assert.IsNotNull(finalized.SourceOnly);
        Assert.AreEqual(
            created.SourceOnly!.SettingsSnapshotDigest,
            finalized.SourceOnly.SettingsSnapshotDigest);
        Assert.AreEqual(
            replacement.ToString(),
            fixture.DraftStore.GetDraft(draftId) is { CanonicalJson: var json }
                ? JsonNode.Parse(json)!["sourceCandidateId"]!.GetValue<string>()
                : null);
        Assert.IsTrue(replacementRegistry.TryClaim(
            replacement,
            out string? handedOffPath));
        Assert.AreEqual(fixture.PickerSourcePath, handedOffPath);
    }

    [TestMethod]
    public async Task SourceOnlyCreationFailureDiscardsUnreachableCandidate()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        File.Delete(fixture.PickerSourcePath);

        ProjectCreationException exception =
            await Assert.ThrowsExactlyAsync<ProjectCreationException>(() =>
                fixture.Service.CreateSourceOnlyDraftAsync());

        Assert.AreEqual(
            ProjectCreationFailureReason.SourceReselectionRequired,
            exception.Reason);
        Assert.IsNotNull(fixture.LastCandidateId);
        Assert.IsFalse(fixture.Registry.IsAvailable(fixture.LastCandidateId));
        Assert.IsFalse(fixture.Registry.IsClaimable(fixture.LastCandidateId));
        Assert.AreEqual(0, fixture.Registry.PendingCount);
        Assert.HasCount(0, fixture.DraftStore.ListDrafts());
    }

    [TestMethod]
    public async Task SourceOnlyCancellationAfterSelectionDiscardsUnreachableCandidate()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        using var cancellation = new CancellationTokenSource();
        fixture.CancelAfterSelection = cancellation;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            fixture.Service.CreateSourceOnlyDraftAsync(cancellation.Token));

        Assert.IsNotNull(fixture.LastCandidateId);
        Assert.IsFalse(fixture.Registry.IsAvailable(fixture.LastCandidateId));
        Assert.IsFalse(fixture.Registry.IsClaimable(fixture.LastCandidateId));
        Assert.AreEqual(0, fixture.Registry.PendingCount);
        Assert.HasCount(0, fixture.DraftStore.ListDrafts());
    }

    [TestMethod]
    public async Task SourceOnlyDatabaseFailureDiscardsUnreachableCandidate()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = fixture.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_source_only_draft_insert
                BEFORE INSERT ON project_creation_drafts
                BEGIN
                    SELECT RAISE(ABORT, 'injected source-only draft failure');
                END;
                """;
            _ = command.ExecuteNonQuery();
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            fixture.Service.CreateSourceOnlyDraftAsync());

        Assert.IsNotNull(fixture.LastCandidateId);
        Assert.IsFalse(fixture.Registry.IsAvailable(fixture.LastCandidateId));
        Assert.IsFalse(fixture.Registry.IsClaimable(fixture.LastCandidateId));
        Assert.AreEqual(0, fixture.Registry.PendingCount);
        Assert.HasCount(0, fixture.DraftStore.ListDrafts());
    }

    [TestMethod]
    public async Task BridgeHandlerMapsAuthorizationAndPickerToClosedPrivateSafeResponses()
    {
        using ProjectCreationFixture deniedFixture = new(UnauthorizedSid);
        var deniedHandler = new ProjectBridgeHandler(deniedFixture.Service);
        BridgeRequest listRequest = CreateBridgeRequest(
            BridgeMethod.ListProjects,
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
            CreateBridgeRequest(BridgeMethod.PickSource, new JsonObject()),
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
    public async Task DeleteProjectRemovesOnlyTheTargetFromTheListAndRejectsRepeatsAndUnknownIds()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        var handler = new ProjectBridgeHandler(fixture.Service);
        ProjectCreationDraftView kept = fixture.Service.CreateDraft();
        ProjectCreationDraftView removed = fixture.Service.CreateDraft();
        BridgeRequest request = CreateBridgeRequest(
            BridgeMethod.DeleteProject,
            new JsonObject { ["draftId"] = removed.DraftId });

        string json = await handler.HandleAsync(request, CancellationToken.None);

        using (JsonDocument response = JsonDocument.Parse(json))
        {
            Assert.AreEqual("project.delete", response.RootElement.GetProperty("method").GetString());
            JsonElement result = response.RootElement.GetProperty("result");
            CollectionAssert.AreEqual(
                new[] { "deletedDraftId", "filesRemoved" },
                result.EnumerateObject().Select(static property => property.Name).ToArray());
            Assert.AreEqual(removed.DraftId, result.GetProperty("deletedDraftId").GetString());
            Assert.IsTrue(result.GetProperty("filesRemoved").GetBoolean());
        }

        CollectionAssert.AreEqual(
            new[] { kept.DraftId },
            fixture.Service.ListProjects().Projects.Select(static project => project.DraftId).ToArray());
        ProjectCreationException saveAfterDelete = Assert.ThrowsExactly<ProjectCreationException>(() =>
            fixture.Service.SaveDraft(
                EntityId<ProjectCreationDraftIdTag>.Parse(removed.DraftId),
                expectedRevision: 0,
                ParseElement(CreateInput(fixture.Catalog))));
        Assert.AreEqual(ProjectCreationFailureReason.DraftNotFound, saveAfterDelete.Reason);
        AssertBridgeError(
            await handler.HandleAsync(request, CancellationToken.None),
            "project.not_found");
        AssertBridgeError(
            await handler.HandleAsync(
                CreateBridgeRequest(
                    BridgeMethod.DeleteProject,
                    new JsonObject { ["draftId"] = EntityId<ProjectCreationDraftIdTag>.New().ToString() }),
                CancellationToken.None),
            "project.not_found");
        AssertBridgeError(
            await handler.HandleAsync(
                CreateBridgeRequest(
                    BridgeMethod.LoadDraft,
                    new JsonObject { ["draftId"] = removed.DraftId }),
                CancellationToken.None),
            "project.not_found");
    }

    [TestMethod]
    public void DeleteProjectRemovesWorkspaceFilesOnlyForAFinalizedProjectAndReportsFailure()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        var removed = new List<EntityId<ProjectIdTag>>();
        bool succeed = true;
        fixture.Service.ProjectFilesRemover = projectId =>
        {
            removed.Add(projectId);
            return succeed;
        };
        ProjectCreationDraftView plain = fixture.Service.CreateDraft();
        ProjectCreationDraftView toFinalize = fixture.Service.CreateDraft();
        EntityId<AssetIdTag> candidate = fixture.Registry.RegisterSelection(fixture.PickerSourcePath)
            ?? throw new AssertFailedException("A source selection must create an opaque candidate.");
        ProjectCreationDraftView finalized = fixture.Service.FinalizeDraft(
            EntityId<ProjectCreationDraftIdTag>.Parse(toFinalize.DraftId),
            toFinalize.Revision,
            ParseElement(CreateCompleteInput(fixture.Catalog, candidate)));

        Assert.IsTrue(fixture.Service.DeleteProject(EntityId<ProjectCreationDraftIdTag>.Parse(plain.DraftId)));
        Assert.HasCount(0, removed);

        succeed = false;
        Assert.IsFalse(fixture.Service.DeleteProject(EntityId<ProjectCreationDraftIdTag>.Parse(finalized.DraftId)));
        CollectionAssert.AreEqual(
            new[] { EntityId<ProjectIdTag>.Parse(finalized.ProjectId!) },
            removed);
        Assert.HasCount(0, fixture.Service.ListProjects().Projects);
    }

    [TestMethod]
    public async Task DeleteProjectIsDeniedWithoutEditorAuthorityAndLeavesTheProjectListed()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created = fixture.Service.CreateDraft();
        ProjectCreationService unauthorized = fixture.CreateService(fixture.Registry, UnauthorizedSid);
        var handler = new ProjectBridgeHandler(unauthorized);

        AssertAuthorizationDenied(() => unauthorized.DeleteProject(
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId)));
        AssertBridgeError(
            await handler.HandleAsync(
                CreateBridgeRequest(
                    BridgeMethod.DeleteProject,
                    new JsonObject { ["draftId"] = created.DraftId }),
                CancellationToken.None),
            "project.not_authorized");
        Assert.HasCount(1, fixture.Service.ListProjects().Projects);
    }

    [TestMethod]
    public void DeleteBridgeRequestAcceptsOnlyAnExactCanonicalDraftId()
    {
        string Source = WebViewHost.EntryPoint;
        string id = EntityId<ProjectCreationDraftIdTag>.New().ToString();
        string Build(string parameters) =>
            "{\"version\":1,\"requestId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"method\":\"project.delete\",\"params\":" + parameters + "}";

        Assert.IsTrue(BridgeProtocol.TryParseRequest(Source, Build("{\"draftId\":\"" + id + "\"}"), out BridgeRequest? parsed, out _));
        Assert.AreEqual(BridgeMethod.DeleteProject, parsed!.Method);
        foreach (string invalid in new[]
        {
            "{}",
            "{\"draftId\":\"" + id + "\",\"extra\":1}",
            "{\"draftId\":\"" + id.ToUpperInvariant() + "\"}",
            "{\"draftId\":\"not-an-id\"}",
            "{\"draftId\":null}",
        })
        {
            Assert.IsFalse(BridgeProtocol.TryParseRequest(Source, Build(invalid), out _, out _), invalid);
        }
    }

    [TestMethod]
    public async Task BridgeCreateDraftReturnsSourceOnlySummaryAndTypedCancellationWithoutPath()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        var handler = new ProjectBridgeHandler(fixture.Service);
        BridgeRequest request = CreateBridgeRequest(
            BridgeMethod.CreateDraft,
            new JsonObject());

        string createdJson = await handler.HandleAsync(request, CancellationToken.None);
        using (JsonDocument created = JsonDocument.Parse(createdJson))
        {
            JsonElement draft = created.RootElement.GetProperty("result").GetProperty("draft");
            Assert.AreEqual("picked-source", draft.GetProperty("contentName").GetString());
            Assert.AreEqual("internalOnly", draft.GetProperty("publicationScope").GetString());
            Assert.AreEqual("1.0.0", draft.GetProperty("sourceOnly")
                .GetProperty("profileVersion").GetString());
            Assert.IsGreaterThan(0, draft.GetProperty("sourceOnly")
                .GetProperty("artifactSet").GetArrayLength());
        }

        Assert.IsFalse(createdJson.Contains(fixture.PickerSourcePath, StringComparison.Ordinal));
        Assert.IsFalse(createdJson.Contains("C:\\", StringComparison.Ordinal));
        fixture.CancelNextPick = true;
        string canceledJson = await handler.HandleAsync(request, CancellationToken.None);
        AssertBridgeError(canceledJson, "project.source_selection_canceled");
        Assert.HasCount(1, fixture.DraftStore.ListDrafts());
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
                BridgeMethod.SaveDraft,
                CreateMutationParameters(
                    created.DraftId,
                    expectedRevision: 0,
                    CreateInput(fixture.Catalog))),
            CancellationToken.None);
        AssertBridgeError(conflictJson, "project.revision_conflict");

        EntityId<AssetIdTag> unavailableCandidate = EntityId<AssetIdTag>.New();
        string sourceLossJson = await handler.HandleAsync(
            CreateBridgeRequest(
                BridgeMethod.FinalizeDraft,
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
    public async Task PickerCancellationAfterSelectionDiscardsUnreturnedCandidate()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        using var cancellation = new CancellationTokenSource();
        fixture.CancelAfterSelection = cancellation;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            fixture.Service.PickSourceAsync(cancellation.Token));

        Assert.IsNotNull(fixture.LastCandidateId);
        Assert.IsFalse(fixture.Registry.IsAvailable(fixture.LastCandidateId));
        Assert.IsFalse(fixture.Registry.IsClaimable(fixture.LastCandidateId));
        Assert.AreEqual(0, fixture.Registry.PendingCount);
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
            fixture.Registry.RegisterSelection(fixture.PickerSourcePath)!;
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
        Assert.IsFalse(fixture.Registry.IsAvailable(available));
        Assert.IsTrue(fixture.Registry.IsClaimable(available));
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
    public void LegacyCandidateCanFinalizeOnlyOneProjectAndRemainsClaimableForIngest()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView first = fixture.Service.CreateDraft();
        ProjectCreationDraftView second = fixture.Service.CreateDraft();
        EntityId<AssetIdTag> candidate =
            fixture.Registry.RegisterSelection(fixture.PickerSourcePath)
            ?? throw new AssertFailedException(
                "A source selection must create an opaque candidate.");
        JsonElement input = ParseElement(CreateCompleteInput(fixture.Catalog, candidate));

        ProjectCreationDraftView finalized = fixture.Service.FinalizeDraft(
            EntityId<ProjectCreationDraftIdTag>.Parse(first.DraftId),
            first.Revision,
            input);
        ProjectCreationException reused =
            Assert.ThrowsExactly<ProjectCreationException>(() =>
                fixture.Service.FinalizeDraft(
                    EntityId<ProjectCreationDraftIdTag>.Parse(second.DraftId),
                    second.Revision,
                    input));

        Assert.IsTrue(finalized.IsFinalized);
        Assert.AreEqual(
            ProjectCreationFailureReason.SourceReselectionRequired,
            reused.Reason);
        Assert.IsFalse(fixture.Registry.IsAvailable(candidate));
        Assert.IsTrue(fixture.Registry.IsClaimable(candidate));
        Assert.IsTrue(fixture.Registry.TryClaim(candidate, out string? selectedPath));
        Assert.AreEqual(fixture.PickerSourcePath, selectedPath);
        Assert.IsFalse(fixture.Registry.TryClaim(candidate, out _));
    }

    [TestMethod]
    public async Task FinalizedSourceRemainsAcceptedAfterIngestClaimsTheHandoff()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created =
            await fixture.Service.CreateSourceOnlyDraftAsync();
        EntityId<ProjectCreationDraftIdTag> draftId =
            EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId);
        ProjectCreationDraftView finalized = fixture.Service.FinalizeDraft(
            draftId,
            created.Revision,
            ParseElement(CreateInput(created)));
        EntityId<AssetIdTag> candidate =
            EntityId<AssetIdTag>.Parse(finalized.SourceCandidateId!);

        Assert.IsTrue(fixture.Registry.TryClaim(candidate, out string? selectedPath));
        Assert.AreEqual(fixture.PickerSourcePath, selectedPath);

        ProjectCreationDraftView reloaded = fixture.Service.LoadDraft(draftId);
        ProjectListItem listed = fixture.Service.ListProjects().Projects.Single();
        Assert.IsTrue(reloaded.IsFinalized);
        Assert.AreEqual("available", reloaded.SourceStatus);
        Assert.AreEqual("finalized", listed.State);
        Assert.AreEqual("available", listed.SourceStatus);
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

    [TestMethod]
    public async Task NativeDraftLoadRestoresOriginalSelectionAfterRestartWithoutRepickingOrStartingWork()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created;
        await using (var first = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, (_, _) => Task.FromException(new AssertFailedException("Draft creation must not start work."))))
        {
            created = await fixture.CreateService(fixture.Registry, coordinator: first).CreateSourceOnlyDraftAsync();
        }
        var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId);
        var candidateId = EntityId<AssetIdTag>.Parse(created.SourceCandidateId!);
        string before = fixture.DraftStore.GetDraft(draftId)!.CanonicalJson;
        var restartedRegistry = new AssetCandidateRegistry();
        await using var restarted = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            restartedRegistry, (_, _) => Task.FromException(new InvalidOperationException("Test stops before production.")));
        ProjectCreationService service = fixture.CreateService(restartedRegistry, coordinator: restarted);

        ProjectCreationDraftView loaded = service.LoadDraft(draftId);
        Assert.AreEqual("available", loaded.SourceStatus);
        Assert.AreEqual(created.SourceCandidateId, loaded.SourceCandidateId);
        Assert.AreEqual(created.Revision, loaded.Revision);
        Assert.AreEqual(created.SourceOnly!.SettingsSnapshotDigest, loaded.SourceOnly!.SettingsSnapshotDigest);
        CollectionAssert.AreEqual(created.SourceOnly.ArtifactSet.ToArray(), loaded.SourceOnly.ArtifactSet.ToArray());
        Assert.AreEqual(before, fixture.DraftStore.GetDraft(draftId)!.CanonicalJson);
        Assert.AreEqual(1, fixture.PickerInvocationCount);
        Assert.AreEqual(1, restartedRegistry.PendingCount);
        Assert.AreEqual("available", service.LoadDraft(draftId).SourceStatus);
        Assert.AreEqual("available", service.ListProjects().Projects.Single().SourceStatus);
        Assert.IsTrue(restartedRegistry.IsAvailable(candidateId));
        Assert.IsNull(new SourceOnlyExecutionStore(fixture.DatabasePath).GetByDraft(draftId));
        Assert.IsEmpty(new JobStore(fixture.DatabasePath).ListJobs(HubRadio.Core.Jobs.JobState.Queued));
        Assert.IsFalse(((ISourceOnlyProjectCoordinator)restarted).RestoreSelection(draftId,
            restartedRegistry.RegisterSelection(fixture.PickerSourcePath)!));

        ProjectCreationDraftView finalized = service.FinalizeDraft(draftId, loaded.Revision, ParseElement(CreateInput(loaded)));
        StoredSourceOnlyExecution accepted = new SourceOnlyExecutionStore(fixture.DatabasePath).GetByDraft(draftId)!;
        await restarted.Start(accepted.Request.ExecutionId);
        Assert.IsTrue(finalized.IsFinalized);
        Assert.AreEqual(candidateId, accepted.Request.SourceCandidateId);
        Assert.AreEqual(1, fixture.PickerInvocationCount);
    }

    [TestMethod]
    [DataRow("changed-bytes")]
    [DataRow("replacement-file")]
    [DataRow("missing-source")]
    [DataRow("missing-locator")]
    [DataRow("tampered-locator")]
    public async Task NativeDraftRestoreRejectsMissingOrChangedPersistedIdentity(string defect)
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created;
        await using (var first = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, (_, _) => Task.CompletedTask))
        {
            created = await fixture.CreateService(fixture.Registry, coordinator: first).CreateSourceOnlyDraftAsync();
        }
        var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId);
        string locatorPath = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "source-only", "selections", draftId + ".dpapi");
        switch (defect)
        {
            case "changed-bytes": File.WriteAllBytes(fixture.PickerSourcePath, [7]); break;
            case "replacement-file":
                string replacement = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "replacement.mov");
                File.WriteAllBytes(replacement, [0]);
                File.Move(replacement, fixture.PickerSourcePath, overwrite: true);
                break;
            case "missing-source": File.Delete(fixture.PickerSourcePath); break;
            case "missing-locator": File.Delete(locatorPath); break;
            case "tampered-locator":
                byte[] bytes = File.ReadAllBytes(locatorPath);
                bytes[^1] ^= 1;
                File.WriteAllBytes(locatorPath, bytes);
                break;
            default: Assert.Fail("Unknown fixture defect."); break;
        }
        var registry = new AssetCandidateRegistry();
        await using var restarted = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            registry, (_, _) => Task.CompletedTask);
        ProjectCreationService service = fixture.CreateService(registry, coordinator: restarted);

        ProjectCreationDraftView loaded = service.LoadDraft(draftId);
        Assert.AreEqual("reselectionRequired", loaded.SourceStatus);
        Assert.AreEqual(created.SourceCandidateId, loaded.SourceCandidateId);
        Assert.AreEqual(0, registry.PendingCount);
        Assert.AreEqual(1, fixture.PickerInvocationCount);
        Assert.IsFalse(fixture.DraftStore.GetDraft(draftId)!.IsFinalized);
        Assert.IsNull(new SourceOnlyExecutionStore(fixture.DatabasePath).GetByDraft(draftId));
        Assert.IsFalse(JsonSerializer.Serialize(loaded).Contains(fixture.PickerSourcePath, StringComparison.Ordinal));
        Assert.AreEqual(ProjectCreationFailureReason.SourceReselectionRequired,
            Assert.ThrowsExactly<ProjectCreationException>(() => service.FinalizeDraft(
                draftId, loaded.Revision, ParseElement(CreateInput(loaded)))).Reason);
    }

    [TestMethod]
    public async Task UnauthorizedDraftLoadDoesNotRestoreAValidPendingLocator()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView created;
        await using (var first = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, (_, _) => Task.CompletedTask))
        {
            created = await fixture.CreateService(fixture.Registry, coordinator: first).CreateSourceOnlyDraftAsync();
        }
        var registry = new AssetCandidateRegistry();
        await using var restarted = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            registry, (_, _) => Task.CompletedTask);
        ProjectCreationService denied = fixture.CreateService(registry, UnauthorizedSid, restarted);
        AssertAuthorizationDenied(() => denied.LoadDraft(EntityId<ProjectCreationDraftIdTag>.Parse(created.DraftId)));
        Assert.AreEqual(0, registry.PendingCount);
    }

    [TestMethod]
    public async Task NativeCoordinatorAtomicallyAcceptsJobsAndSameInputReplayDoesNotStartTwice()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        await using var pipeline = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, async (_, token) =>
            {
                Interlocked.Increment(ref starts);
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        ProjectCreationService service = fixture.CreateService(fixture.Registry, coordinator: pipeline);
        ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
        var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId);
        JsonElement input = ParseElement(CreateInput(draft));
        ProjectCreationDraftView finalized = service.FinalizeDraft(draftId, draft.Revision, input);
        var execution = new SourceOnlyExecutionStore(fixture.DatabasePath).GetByDraft(draftId)!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ProjectCreationDraftView replay = service.FinalizeDraft(draftId, draft.Revision, input);
        Assert.AreEqual(finalized.ProjectId, replay.ProjectId);
        Assert.AreEqual(1, starts);
        Assert.IsNotNull(new HubRadio.Infrastructure.Database.JobStore(fixture.DatabasePath).GetJob(execution.Request.RootJobId));
        Assert.IsNotNull(new HubRadio.Infrastructure.Database.JobStore(fixture.DatabasePath).GetJob(execution.Request.IngestJobId));
        Assert.AreEqual(draft.SourceCandidateId, execution.Request.SourceCandidateId.ToString());
        Assert.AreEqual(draft.SourceOnly!.SettingsSnapshotDigest, execution.Request.SettingsSnapshot.ContentDigest.Hex);
        byte[] locator = new SourceOnlyExecutionStore(fixture.DatabasePath).GetProtectedSourceLocator(execution.Request.ExecutionId)!;
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(locator).Contains(fixture.PickerSourcePath, StringComparison.Ordinal));
        JsonObject altered = CreateInput(draft);
        altered["purpose"] = "changed";
        Assert.ThrowsExactly<ArgumentException>(() => service.FinalizeDraft(draftId, draft.Revision, ParseElement(altered)));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.FinalizeDraft(draftId, finalized.Revision, input));
        var canceled = await pipeline.CancelAsync(execution.Request.ExecutionId);
        Assert.AreEqual(SourceOnlyExecutionPhase.Canceled, canceled!.Execution!.Phase);
        Assert.AreNotEqual(HubRadio.Core.Jobs.JobState.Succeeded,
            new HubRadio.Infrastructure.Database.JobStore(fixture.DatabasePath).GetJob(execution.Request.RootJobId)!.Job.State);
    }

    [TestMethod]
    public async Task NativeFinalizeRejectsSourceBytesChangedSinceSelectionAndReplacementTokens()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        await using var pipeline = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, (_, _) => Task.CompletedTask);
        ProjectCreationService service = fixture.CreateService(fixture.Registry, coordinator: pipeline);
        ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
        var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId);
        File.WriteAllBytes(fixture.PickerSourcePath, [7]);
        await Assert.ThrowsExactlyAsync<SourceOnlySourceException>(() => Task.Run(() => service.FinalizeDraft(
            draftId, draft.Revision, ParseElement(CreateInput(draft)))));
        Assert.IsNull(new SourceOnlyExecutionStore(fixture.DatabasePath).GetByDraft(draftId));
        Assert.IsFalse(fixture.DraftStore.GetDraft(draftId)!.IsFinalized);
        Assert.IsTrue(fixture.Registry.IsAvailable(EntityId<AssetIdTag>.Parse(draft.SourceCandidateId!)));
        JsonObject replacement = CreateInput(draft);
        replacement["sourceCandidateId"] = fixture.Registry.RegisterSelection(fixture.PickerSourcePath)!.ToString();
        Assert.ThrowsExactly<ProjectCreationException>(() => service.FinalizeDraft(draftId, draft.Revision, ParseElement(replacement)));
    }

    [TestMethod]
    public async Task NativeAtomicJobInsertFailureLeavesDraftAndCandidateAvailableForRetry()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        await using var pipeline = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, (_, _) => Task.CompletedTask);
        ProjectCreationService service = fixture.CreateService(fixture.Registry, coordinator: pipeline);
        ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
        var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_native_job BEFORE INSERT ON jobs BEGIN SELECT RAISE(ABORT, 'injected'); END;";
            command.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<SqliteException>(() => service.FinalizeDraft(draftId, draft.Revision, ParseElement(CreateInput(draft))));
        Assert.IsFalse(fixture.DraftStore.GetDraft(draftId)!.IsFinalized);
        Assert.IsNull(new SourceOnlyExecutionStore(fixture.DatabasePath).GetByDraft(draftId));
        Assert.IsTrue(fixture.Registry.IsAvailable(EntityId<AssetIdTag>.Parse(draft.SourceCandidateId!)));
        Assert.IsEmpty(new HubRadio.Infrastructure.Database.JobStore(fixture.DatabasePath).ListJobs(HubRadio.Core.Jobs.JobState.Queued));
    }

    [TestMethod]
    public async Task NativeExplicitRetryPreservesAcceptedSettingsAndCreatesFreshJobsWithoutSuccess()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        await using var pipeline = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, (_, _) => Task.FromException(new InvalidOperationException("injected stage failure")));
        ProjectCreationService service = fixture.CreateService(fixture.Registry, coordinator: pipeline);
        ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
        var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId);
        service.FinalizeDraft(draftId, draft.Revision, ParseElement(CreateInput(draft)));
        var store = new SourceOnlyExecutionStore(fixture.DatabasePath);
        StoredSourceOnlyExecution initial = store.GetByDraft(draftId)!;
        SourceOnlyPipelineRun firstRun = await pipeline.Start(initial.Request.ExecutionId);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, firstRun.Execution!.Phase);
        DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddMinutes(1);
        StoredSourceOnlyExecution retry = pipeline.Retry(initial.Request.ExecutionId,
            firstRun.Execution.Revision, AuthorizedSid, retryAt);
        SourceOnlyPipelineRun retryRun = await pipeline.Start(retry.Request.ExecutionId);
        Assert.AreEqual(initial.Request.ExecutionId, retry.RetryOfExecutionId);
        Assert.AreEqual(initial.Request.SourceCandidateId, retry.Request.SourceCandidateId);
        Assert.AreEqual(initial.Request.SettingsSnapshot.CanonicalJson, retry.Request.SettingsSnapshot.CanonicalJson);
        Assert.AreNotEqual(initial.Request.RootJobId, retry.Request.RootJobId);
        Assert.AreNotEqual(initial.Request.IngestJobId, retry.Request.IngestJobId);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, retryRun.Execution!.Phase);
        Assert.AreEqual(retry.Request.ExecutionId, pipeline.Read(initial.Request.ProjectId)!.Request.ExecutionId);
        Assert.AreEqual(retry.Request.ExecutionId, pipeline.Retry(initial.Request.ExecutionId,
            firstRun.Execution.Revision, AuthorizedSid, retryAt).Request.ExecutionId);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, store.Get(initial.Request.ExecutionId)!.Phase);
    }

    [TestMethod]
    public async Task NativeRegenerationCapturesExactParentAndReusesSucceededIngest()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        var jobStore = new JobStore(fixture.DatabasePath);
        await using var pipeline = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, (execution, _) =>
            {
                StoredJob ingest = jobStore.GetJob(execution.Request.IngestJobId)!;
                if (ingest.Job.State == HubRadio.Core.Jobs.JobState.Queued)
                {
                    DateTimeOffset at = DateTimeOffset.UtcNow;
                    var attempt = new HubRadio.Core.Jobs.JobAttempt(EntityId<HubRadio.Core.Jobs.JobAttemptIdTag>.New(),
                        ingest.Job.Id, 1, HubRadio.Core.Jobs.JobState.Running, ingest.Job.UpdatedAt, at, null, null);
                    StoredJob running = jobStore.StartAttempt(ingest.Job.Id, ingest.Revision, attempt, at);
                    jobStore.CompleteAttempt(ingest.Job.Id, running.Revision, new HubRadio.Core.Jobs.JobAttempt(
                        attempt.Id, attempt.JobId, 1, HubRadio.Core.Jobs.JobState.Succeeded, attempt.QueuedAt, at, at, null), at);
                }
                return Task.FromException(new InvalidOperationException("test: ingest completed, root failed"));
            });
        ProjectCreationService service = fixture.CreateService(fixture.Registry, coordinator: pipeline);
        ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
        service.FinalizeDraft(EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId), draft.Revision, ParseElement(CreateInput(draft)));
        var executions = new SourceOnlyExecutionStore(fixture.DatabasePath);
        StoredSourceOnlyExecution initial = executions.GetByDraft(EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId))!;
        await pipeline.Start(initial.Request.ExecutionId);
        var projectStore = new ProjectStore(fixture.DatabasePath);
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        var element = new HubRadio.Core.Compositions.CompositionElement(EntityId<HubRadio.Core.Compositions.CompositionElementIdTag>.New(),
            new HubRadio.Core.Time.TimeRange(default, new HubRadio.Core.Time.RationalTime(1, 1)),
            HubRadio.Core.Provenance.OriginRef.FromAsset(initial.Request.SourceCandidateId));
        var parent = new HubRadio.Core.Compositions.CompositionVersion(EntityId<HubRadio.Core.Compositions.CompositionVersionIdTag>.New(),
            initial.Request.ProjectId, null, AuthorizedSid, createdAt, "Test parent",
            HubRadio.Core.Rendering.RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]), [element]);
        projectStore.AppendCompositionVersion(parent, HubRadio.App.Compositions.CompositionSnapshotJson.WriteVersion(parent));
        var copy = new HubRadio.Core.Compositions.WorkingCopy(EntityId<HubRadio.Core.Compositions.WorkingCopyIdTag>.New(),
            parent.ProjectId, parent.Id, createdAt, createdAt, parent.Elements);
        projectStore.CreateWorkingCopy(copy, HubRadio.App.Compositions.CompositionSnapshotJson.WriteWorkingCopy(copy));
        Assert.ThrowsExactly<InvalidOperationException>(() => pipeline.Regenerate(parent.ProjectId, parent.Id,
            ContentDigest.Compute([9]), initial.Request.SettingsSnapshot, 0, AuthorizedSid, createdAt.AddSeconds(1)));

        StoredSourceOnlyExecution regenerated = pipeline.Regenerate(parent.ProjectId, parent.Id, parent.ContentDigest,
            initial.Request.SettingsSnapshot, 0, AuthorizedSid, createdAt.AddSeconds(1));
        await pipeline.Start(regenerated.Request.ExecutionId);
        Assert.AreEqual(initial.Request.IngestJobId, regenerated.Request.IngestJobId);
        Assert.AreNotEqual(initial.Request.RootJobId, regenerated.Request.RootJobId);
        Assert.AreEqual(parent.Id, regenerated.Request.ParentVersionId);
        Assert.AreEqual(parent.ContentDigest, regenerated.Request.ParentDigest);
        Assert.AreEqual(initial.Request.SettingsSnapshot.CanonicalJson, regenerated.Request.SettingsSnapshot.CanonicalJson);
        Assert.AreEqual(1, jobStore.GetJob(initial.Request.IngestJobId)!.Job.CurrentAttemptNumber);
    }

    [TestMethod]
    public async Task NativeProcessLeaseRejectsASecondOwnerIncludingSameDatabaseWithAnotherWorkspace()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        string workspace = Path.GetDirectoryName(fixture.DatabasePath)!;
        string otherWorkspace = Path.Combine(workspace, "another-workspace");
        Directory.CreateDirectory(otherWorkspace);
        await using (var first = new SourceOnlyPipeline(fixture.DatabasePath, workspace, fixture.Registry, (_, _) => Task.CompletedTask))
        {
            foreach (string attemptedWorkspace in new[] { workspace, otherWorkspace })
            {
                SourceOnlySourceException refused = Assert.ThrowsExactly<SourceOnlySourceException>(() =>
                    new SourceOnlyPipeline(fixture.DatabasePath, attemptedWorkspace, new AssetCandidateRegistry(), (_, _) => Task.CompletedTask));
                Assert.AreEqual("source_only.process_lease_unavailable", refused.Code);
                Assert.IsFalse(refused.ToString().Contains(workspace, StringComparison.Ordinal));
            }
        }
        // A failed second acquisition releases its first handle; normal disposal
        // releases both leases without deleting a possibly active owner's lock file.
        await using var afterRelease = new SourceOnlyPipeline(fixture.DatabasePath, otherWorkspace,
            new AssetCandidateRegistry(), (_, _) => Task.CompletedTask);
        Assert.IsEmpty(new JobStore(fixture.DatabasePath).ListJobs(HubRadio.Core.Jobs.JobState.Running));
    }

    [TestMethod]
    public async Task NativeRecoverySettlesRunningRootEvenWhenItsExecutionWasAlreadyFailed()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        string workspace = Path.GetDirectoryName(fixture.DatabasePath)!;
        var store = new SourceOnlyExecutionStore(fixture.DatabasePath);
        var jobs = new JobStore(fixture.DatabasePath);
        StoredSourceOnlyExecution execution;
        DateTimeOffset heartbeatAt = DateTimeOffset.UtcNow.AddMinutes(1);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_terminal_attempt BEFORE UPDATE ON job_attempts BEGIN SELECT RAISE(ABORT, 'injected'); END;";
            command.ExecuteNonQuery();
        }
        await using (var pipeline = new SourceOnlyPipeline(fixture.DatabasePath, workspace, fixture.Registry, (accepted, _) =>
        {
            StoredJob root = jobs.GetJob(accepted.Request.RootJobId)!;
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            var attempt = new HubRadio.Core.Jobs.JobAttempt(EntityId<HubRadio.Core.Jobs.JobAttemptIdTag>.New(), root.Job.Id, 1,
                HubRadio.Core.Jobs.JobState.Running, root.Job.CreatedAt, startedAt, null, null);
            jobs.StartAttempt(root.Job.Id, root.Revision, attempt, startedAt);
            jobs.AppendHeartbeat(new HubRadio.Core.Jobs.JobHeartbeat(EntityId<HubRadio.Core.Jobs.JobHeartbeatIdTag>.New(),
                root.Job.Id, attempt.Id, 1, heartbeatAt));
            store.UpdateState(accepted.Request.ExecutionId, accepted.Revision, SourceOnlyExecutionPhase.Failed,
                failureCode: "source_only.execution_failed");
            return Task.FromException(new IOException("Test terminal attempt persistence failed."));
        }))
        {
            ProjectCreationService service = fixture.CreateService(fixture.Registry, coordinator: pipeline);
            ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
            var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId);
            service.FinalizeDraft(draftId, draft.Revision, ParseElement(CreateInput(draft)));
            execution = store.GetByDraft(draftId)!;
            SourceOnlyPipelineRun run = await pipeline.Start(execution.Request.ExecutionId);
            Assert.AreEqual("source_only.state_persistence_failed", run.InfrastructureFailureCode);
            Assert.AreEqual(SourceOnlyExecutionPhase.Failed, store.Get(execution.Request.ExecutionId)!.Phase);
            Assert.AreEqual(HubRadio.Core.Jobs.JobState.Running, jobs.GetJob(execution.Request.RootJobId)!.Job.State);
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_terminal_attempt;";
            command.ExecuteNonQuery();
        }
        await using var restarted = new SourceOnlyPipeline(fixture.DatabasePath, workspace, new AssetCandidateRegistry(),
            (_, _) => Task.FromException(new AssertFailedException("Recovery must not execute a failed pipeline.")));
        SourceOnlyRecoveryResult recovery = await restarted.RecoverAsync();
        Assert.IsNull(recovery.FailureCode);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, recovery.Executions.Single().Phase);
        StoredJobAggregate settled = jobs.GetJobAggregate(execution.Request.RootJobId)!;
        Assert.AreEqual(HubRadio.Core.Jobs.JobState.Interrupted, settled.Job.State);
        Assert.AreEqual(heartbeatAt, settled.Attempts.Single().CompletedAt);
        Assert.AreEqual("job.app_restarted", settled.Attempts.Single().ErrorCode);
        Assert.AreEqual(HubRadio.Core.Jobs.JobState.Cancelled, jobs.GetJob(execution.Request.IngestJobId)!.Job.State);
    }

    [TestMethod]
    public async Task NativeRecoverySettlesOnlyItsOwnInterruptedJobsWithoutCreatingSuccess()
    {
        using ProjectCreationFixture fixture = new(AuthorizedSid);
        ProjectCreationDraftView view = await fixture.Service.CreateSourceOnlyDraftAsync();
        var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(view.DraftId);
        StoredSourceOnlyProjectCreation parsed = SourceOnlyProjectCreationContract.ParseStored(
            fixture.DraftStore.GetDraft(draftId)!.CanonicalJson, fixture.Catalog, fixture.SettingsStore);
        DateTimeOffset at = DateTimeOffset.UtcNow;
        var project = new Project(EntityId<ProjectIdTag>.New(), parsed.Draft.ContentName!, parsed.Draft.Purpose!,
            parsed.Draft.Audience!, PublicationScope.InternalOnly, null, [], []);
        var request = new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), project.Id, draftId,
            parsed.Draft.SourceCandidateId!, EntityId<HubRadio.Core.Jobs.JobIdTag>.New(), EntityId<HubRadio.Core.Jobs.JobIdTag>.New(),
            parsed.SettingsSnapshot, AuthorizedSid, at);
        StoredSourceOnlyExecution execution = fixture.DraftStore.FinalizeSourceOnlyWithExecution(parsed.Draft, project,
            fixture.DraftStore.GetDraft(draftId)!.CanonicalJson, view.Revision, at, request);
        var jobs = new JobStore(fixture.DatabasePath);
        var otherId = EntityId<HubRadio.Core.Jobs.JobIdTag>.New();
        jobs.CreateJob(new HubRadio.Core.Jobs.Job(otherId, project.Id, "unrelated.test", HubRadio.Core.Jobs.JobState.Queued, at, at, 0));
        foreach (var id in new[] { request.RootJobId, otherId })
        {
            StoredJob current = jobs.GetJob(id)!;
            jobs.StartAttempt(id, current.Revision, new HubRadio.Core.Jobs.JobAttempt(
                EntityId<HubRadio.Core.Jobs.JobAttemptIdTag>.New(), id, 1, HubRadio.Core.Jobs.JobState.Running,
                at, at, null, null), at);
        }
        var store = new SourceOnlyExecutionStore(fixture.DatabasePath);
        store.UpdateState(execution.Request.ExecutionId, execution.Revision, SourceOnlyExecutionPhase.Analyzing);
        await using var pipeline = new SourceOnlyPipeline(fixture.DatabasePath, Path.GetDirectoryName(fixture.DatabasePath)!,
            fixture.Registry, (_, _) => Task.FromException(new AssertFailedException("Recovery must not start an interrupted root.")));
        SourceOnlyRecoveryResult recovery = await pipeline.RecoverAsync();
        Assert.IsNull(recovery.FailureCode);
        Assert.IsFalse(recovery.Canceled);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, store.Get(request.ExecutionId)!.Phase);
        Assert.AreEqual(HubRadio.Core.Jobs.JobState.Interrupted, jobs.GetJob(request.RootJobId)!.Job.State);
        Assert.AreEqual(HubRadio.Core.Jobs.JobState.Cancelled, jobs.GetJob(request.IngestJobId)!.Job.State);
        Assert.AreEqual(HubRadio.Core.Jobs.JobState.Running, jobs.GetJob(otherId)!.Job.State);
    }

    private static void AssertAuthorizationDenied(Action action)
    {
        AuthorizationException exception =
            Assert.ThrowsExactly<AuthorizationException>(action);
        Assert.IsTrue(exception.Operation is
            AuthorizationOperation.ViewProject or AuthorizationOperation.CreateProject
            or AuthorizationOperation.DeleteProject);
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

    private static JsonObject CreateInput(ProjectCreationDraftView draft) => new()
    {
        ["sourceCandidateId"] = draft.SourceCandidateId,
        ["contentName"] = draft.ContentName,
        ["purpose"] = draft.Purpose,
        ["audience"] = draft.Audience,
        ["publicationScope"] = draft.PublicationScope,
        ["outputProfileIds"] = new JsonArray(
            draft.OutputProfileIds
                .Select(static value => JsonValue.Create(value))
                .ToArray()),
        ["language"] = draft.Language,
        ["performerNames"] = new JsonArray(
            draft.PerformerNames
                .Select(static value => JsonValue.Create(value))
                .ToArray()),
        ["deadlineSelection"] = new JsonObject
        {
            ["hasDeadline"] = draft.HasDeadline,
            ["value"] = draft.Deadline,
        },
        ["brandTemplateId"] = draft.BrandTemplateId,
    };

    private static JsonElement ParseElement(JsonObject value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static BridgeRequest CreateBridgeRequest(
        BridgeMethod method,
        JsonObject parameters) =>
        BridgeRequest.Create(
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
            SettingsStore = new DefaultSettingsStore(DatabasePath);
            _ = new BundledDefaultSettingsBootstrap(SettingsStore).Initialize(
                ResolveRepositoryPath(
                    "src/HubRadio.App/DefaultSettings/Profiles",
                    expectDirectory: true),
                clock.GetUtcNow());
            Registry = new AssetCandidateRegistry();
            Catalog = LoadCatalog();
            PickerSourcePath = Path.Combine(RootPath, "picked-source.mov");
            File.WriteAllBytes(PickerSourcePath, [0]);

            var authorization = new AuthorizationService(
                new RoleAssignmentStore(DatabasePath));
            Assert.IsTrue(authorization.BootstrapInitialRoles(AuthorizedSid));
            Authorization = authorization;
            Service = CreateService(Registry);
        }

        private string RootPath { get; }

        internal string DatabasePath { get; }

        internal ProjectCreationDraftStore DraftStore { get; }

        internal DefaultSettingsStore SettingsStore { get; }

        internal AssetCandidateRegistry Registry { get; }

        internal ProjectCreationCatalog Catalog { get; }

        internal string PickerSourcePath { get; }

        internal AuthorizationService Authorization { get; }

        internal ProjectCreationService Service { get; }

        internal int PickerInvocationCount { get; private set; }

        internal bool CancelNextPick { get; set; }

        internal CancellationTokenSource? CancelAfterSelection { get; set; }

        internal EntityId<AssetIdTag>? LastCandidateId { get; private set; }

        internal ProjectCreationService CreateService(
            AssetCandidateRegistry registry,
            string? currentActorSid = null,
            ISourceOnlyProjectCoordinator? coordinator = null) =>
            new(
                DraftStore,
                Authorization,
                registry,
                Catalog,
                SettingsStore,
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

                    LastCandidateId = registry.RegisterSelection(PickerSourcePath);
                    CancelAfterSelection?.Cancel();
                    return Task.FromResult(LastCandidateId);
                },
                clock,
                coordinator);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
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