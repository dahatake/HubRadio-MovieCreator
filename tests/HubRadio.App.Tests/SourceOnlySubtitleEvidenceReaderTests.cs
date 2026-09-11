using System.Text;
using HubRadio.App.Projects;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

/// <summary>Read-boundary tests only; no pipeline, media tools, network or application data defaults.</summary>
[TestClass]
public sealed class SourceOnlySubtitleEvidenceReaderTests
{
    [TestMethod]
    public async Task MissingGeneratedRuntimeReturnsNullWithoutCreatingEvidenceOrAdvancingState()
    {
        using var fixture = new Fixture();
        StoredSourceOnlyExecution before = fixture.Execution;
        Assert.IsNull(await fixture.Reader.ReadAsync(before, CancellationToken.None));
        Assert.IsNull(await fixture.Reader.ReadAsync(before, CancellationToken.None));
        Assert.AreEqual(before.StateDigest, fixture.Executions.Get(before.Request.ExecutionId)!.StateDigest);
        Assert.IsEmpty(fixture.Artifacts.ListProjectArtifacts(before.Request.ProjectId));
        Assert.AreEqual(0, new JobStore(fixture.Database).GetJobAggregate(before.Request.RootJobId)!.Revision);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "source-only")), "A reader must not acquire a pipeline lease.");
    }

    [TestMethod]
    public async Task StaleStateRevisionWithIdenticalAcceptanceIsAllowed()
    {
        using var fixture = new Fixture();
        StoredSourceOnlyExecution original = fixture.Execution;
        StoredSourceOnlyExecution progressed = fixture.Executions.UpdateState(original.Request.ExecutionId, original.Revision,
            SourceOnlyExecutionPhase.Evaluating);
        Assert.AreNotEqual(original.StateDigest, progressed.StateDigest);
        Assert.IsNull(await fixture.Reader.ReadAsync(original, CancellationToken.None));
        Assert.AreEqual(progressed.StateDigest, fixture.Executions.Get(original.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    [DataRow("digest")]
    [DataRow("json")]
    [DataRow("typed-request")]
    [DataRow("missing-execution")]
    public async Task ChangedImmutableRequestCannotBeTreatedAsMissingLegacyEvidence(string change)
    {
        using var fixture = new Fixture();
        StoredSourceOnlyExecution original = fixture.Execution;
        SourceOnlyExecutionRequest request = change switch
        {
            "typed-request" => original.Request with { ActorSid = "S-1-5-21-9999" },
            "missing-execution" => original.Request with { ExecutionId = EntityId<SourceOnlyExecutionIdTag>.New() },
            _ => original.Request,
        };
        var supplied = new StoredSourceOnlyExecution(request, change == "json" ? "{}" : original.RequestJson,
            change == "digest" ? ContentDigest.Compute([255]) : original.RequestDigest, original.SettingsRevision,
            original.FinalizationDigest, original.LocatorDigest, original.Revision, original.Phase, original.ResultJson,
            original.ResultDigest, original.VersionId, original.VersionDigest, original.FailureCode, original.StateDigest);
        await InvalidAsync(() => fixture.Reader.ReadAsync(supplied, CancellationToken.None));
        Assert.AreEqual(original.StateDigest, fixture.Executions.Get(original.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(17)]
    public async Task InvalidDepthFailsBeforeMissingEvidenceCanReturnNull(int depth)
    {
        using var fixture = new Fixture();
        await InvalidAsync(() => fixture.Reader.ReadAsync(fixture.Execution, CancellationToken.None, depth));
    }

    [TestMethod]
    public async Task CancellationIsPreservedWithoutNestedPrivateDiagnostics()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException failure = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => fixture.Reader.ReadAsync(fixture.Execution, cancellation.Token));
        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        Assert.IsNull(failure.InnerException);
    }

    [TestMethod]
    [DataRow("{}", ArtifactKind.Analysis)]
    [DataRow("[]", ArtifactKind.Analysis)]
    [DataRow("{\"schema\":\"unknown\"}", ArtifactKind.Analysis)]
    [DataRow("{\"schema\":\"x\",\"schema\":\"y\"}", ArtifactKind.Analysis)]
    [DataRow("{}", ArtifactKind.Evidence)]
    public async Task PresentInvalidArtifactFailsClosedInsteadOfReturningNull(string json, ArtifactKind kind)
    {
        using var fixture = new Fixture();
        await fixture.SaveAsync(Encoding.UTF8.GetBytes(json), kind);
        await InvalidAsync(() => fixture.Reader.ReadAsync(fixture.Execution, CancellationToken.None));
        Assert.AreEqual(fixture.Execution.StateDigest, fixture.Executions.Get(fixture.Execution.Request.ExecutionId)!.StateDigest);
        Assert.HasCount(1, fixture.Artifacts.ListProjectArtifacts(fixture.Execution.Request.ProjectId));
    }

    [TestMethod]
    public async Task TamperedFileIsRejectedWithNoPathOrInnerException()
    {
        using var fixture = new Fixture();
        StoredArtifact artifact = await fixture.SaveAsync("{}"u8.ToArray(), ArtifactKind.Analysis);
        await File.WriteAllBytesAsync(fixture.Artifacts.GetValidatedRegisteredArtifactPath(artifact.Id), "[]"u8.ToArray());
        await InvalidAsync(() => fixture.Reader.ReadAsync(fixture.Execution, CancellationToken.None));
    }

    private static async Task InvalidAsync(Func<Task<SourceOnlyGeneratedRuntimeEvidence?>> read)
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => read());
        Assert.AreEqual("The saved source-only subtitle evidence is invalid.", error.Message);
        Assert.IsNull(error.InnerException);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly DateTimeOffset At = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "HubRadio.SubtitleEvidenceReader", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Database = Path.Combine(Root, "store.db");
            _ = DatabaseMigrator.Migrate(Database, Path.Combine(Root, "backups"));
            var settings = new DefaultSettingsStore(Database);
            DefaultSettingsProfile profile = DefaultSettingsProfile.Create(EntityId<DefaultSettingsProfileIdTag>.New(),
                "subtitle-reader-boundary-tests", new SettingsProfileVersion(1, 0, 0),
                [new DefaultSettingDefinition("editing.crossfadeEnabled", DefaultSettingGroup.ProjectAndOutput, false,
                    "Crossfade", "Read boundary fixture", DefaultSettingUnit.None, DefaultSettingValue.FromBoolean(false),
                    null, null, [], [new DefaultSettingEvidence(DefaultSettingEvidenceKind.ProductDecision, "productDecision")])]);
            _ = settings.RegisterProfile(Encoding.UTF8.GetBytes(profile.CanonicalJson));
            ContentDigest digest = ContentDigest.Compute([1]);
            var draft = new ProjectCreationDraft(EntityId<ProjectCreationDraftIdTag>.New(), EntityId<AssetIdTag>.New(),
                "Reader fixture", "Validate read boundary", "Internal", PublicationScope.InternalOnly,
                [new ProjectOutputProfileReference(EntityId<RenderProfileIdTag>.New(), "long-form", digest)],
                "ja-JP", ["Host"], new ProjectDeadlineSelection(null), new TemplateReference(EntityId<TemplateIdTag>.New(), "neutral", digest));
            var project = new Project(EntityId<ProjectIdTag>.New(), draft.ContentName!, draft.Purpose!, draft.Audience!,
                PublicationScope.InternalOnly, null, [], []);
            var drafts = new ProjectCreationDraftStore(Database);
            _ = drafts.CreateDraft(draft, "{}", At);
            var request = new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), project.Id, draft.Id,
                draft.SourceCandidateId!, EntityId<JobIdTag>.New(), EntityId<JobIdTag>.New(), profile.CreateSnapshot(), "S-1-5-21-1000", At);
            Execution = drafts.FinalizeSourceOnlyWithExecution(draft, project, "{}", 0, At, request);
            Artifacts = new ArtifactStore(Database, Root);
            Executions = new SourceOnlyExecutionStore(Database);
            Reader = new SourceOnlySubtitleEvidenceReader(Artifacts, new ProjectStore(Database), settings, Executions);
        }

        internal string Root { get; }
        internal string Database { get; }
        internal ArtifactStore Artifacts { get; }
        internal SourceOnlyExecutionStore Executions { get; }
        internal StoredSourceOnlyExecution Execution { get; }
        internal SourceOnlySubtitleEvidenceReader Reader { get; }

        internal async Task<StoredArtifact> SaveAsync(byte[] bytes, ArtifactKind kind)
        {
            string staging = Path.Combine(Root, "projects", Execution.Request.ProjectId.ToString(), "staging");
            Directory.CreateDirectory(staging);
            string path = Path.Combine(staging, "generated.json");
            await File.WriteAllBytesAsync(path, bytes);
            var id = SourceOnlyPipeline.StableId<ArtifactIdTag>(Execution.Request.ExecutionId.ToString(), "generated-runtime");
            return await Artifacts.PromoteAsync(new ArtifactPromotionRequest(id, Execution.Request.ProjectId, kind, path,
                "source-only/" + id + ".generated-runtime.json", ContentDigest.Compute(bytes), bytes.LongLength, At));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}