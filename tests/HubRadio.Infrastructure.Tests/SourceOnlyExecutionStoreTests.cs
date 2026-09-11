using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class SourceOnlyExecutionStoreTests
{
    [TestMethod]
    public void FinalizeAndRestartPreserveAcceptanceSeparateJobsAndExactSnapshot()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        byte[] encryptedFixture = [0x01, 0x02, 0x03];
        StoredSourceOnlyExecution accepted = seed.Finalize(encryptedFixture);
        encryptedFixture[0] = 0xff;
        var restarted = new SourceOnlyExecutionStore(database.DatabasePath);
        StoredSourceOnlyExecution restored = restarted.GetByDraft(seed.Draft.Id)!;

        Assert.AreEqual(accepted.RequestDigest, restored.RequestDigest);
        Assert.AreEqual(accepted.StateDigest, restored.StateDigest);
        Assert.AreEqual(seed.Request.ExecutionId, restored.Request.ExecutionId);
        Assert.AreEqual(seed.Snapshot.CanonicalJson, restored.Request.SettingsSnapshot.CanonicalJson);
        Assert.AreEqual(SourceOnlyExecutionPhase.Queued, restored.Phase);
        Assert.AreEqual(0, restored.Revision);
        Assert.IsNull(restored.ResultJson);
        Assert.IsNull(restored.VersionId);
        Assert.AreEqual(seed.Request.ExecutionId, restarted.GetByJob(seed.Request.RootJobId)!.Request.ExecutionId);
        Assert.AreEqual(seed.Request.ExecutionId, restarted.GetByJob(seed.Request.IngestJobId)!.Request.ExecutionId);
        Assert.AreEqual(seed.Request.ExecutionId, restarted.GetLatestForProject(seed.Project.Id)!.Request.ExecutionId);
        Assert.IsNull(restarted.Get(EntityId<SourceOnlyExecutionIdTag>.New()));
        Assert.IsNull(restarted.GetByDraft(EntityId<ProjectCreationDraftIdTag>.New()));
        Assert.IsNull(restarted.GetByJob(EntityId<JobIdTag>.New()));
        Assert.IsNull(restarted.GetLatestForProject(EntityId<ProjectIdTag>.New()));
        var jobs = new JobStore(database.DatabasePath);
        Assert.AreEqual(SourceOnlyExecutionStore.RootJobKind, jobs.GetJob(seed.Request.RootJobId)!.Job.Kind);
        Assert.AreEqual(SourceOnlyExecutionStore.IngestJobKind, jobs.GetJob(seed.Request.IngestJobId)!.Job.Kind);
        Assert.AreEqual(JobState.Queued, jobs.GetJob(seed.Request.RootJobId)!.Job.State);
        Assert.AreEqual(0, jobs.GetJob(seed.Request.IngestJobId)!.Job.CurrentAttemptNumber);
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        byte[] returned = restarted.GetProtectedSourceLocator(seed.Request.ExecutionId)!;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, returned);
        returned[0] = 0;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, restarted.GetProtectedSourceLocator(seed.Request.ExecutionId)!);
        Assert.IsFalse(JsonSerializer.Serialize(restored).Contains("protected", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(typeof(StoredSourceOnlyExecution).GetProperties().Any(p => p.PropertyType == typeof(byte[])));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void ExactFinalizeRetryReturnsOriginalEvenAfterSettingsAndProgressChanged()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        StoredSourceOnlyExecution first = seed.Finalize();
        StoredSourceOnlyExecution progress = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Ingesting);
        _ = seed.Settings.UpdateProjectSettings(seed.Project.Id, seed.AlternateSnapshot(), 0, seed.Request.CreatedAt.AddSeconds(1));

        StoredSourceOnlyExecution retry = seed.Finalize();

        Assert.AreEqual(first.RequestDigest, retry.RequestDigest);
        Assert.AreEqual(progress.Revision, retry.Revision);
        Assert.AreEqual(first.Request.RootJobId, retry.Request.RootJobId);
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Finalize(payload: "{\"different\":true}"));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Finalize(expectedRevision: 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Finalize([1, 2]));
    }

    [TestMethod]
    [DataRow("execution")]
    [DataRow("root")]
    [DataRow("ingest")]
    [DataRow("actor")]
    [DataRow("settings")]
    public void ReusingFinalizationWithDifferentOriginalInputsConflicts(string field)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        SourceOnlyExecutionRequest changed = field switch
        {
            "execution" => seed.Request with { ExecutionId = EntityId<SourceOnlyExecutionIdTag>.New() },
            "root" => seed.Request with { RootJobId = EntityId<JobIdTag>.New() },
            "ingest" => seed.Request with { IngestJobId = EntityId<JobIdTag>.New() },
            "actor" => seed.Request with { ActorSid = "S-1-5-21-9999" },
            "settings" => seed.Request with { SettingsSnapshot = seed.AlternateSnapshot() },
            _ => throw new AssertFailedException(),
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Finalize(request: changed));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    [DataRow("draft")]
    [DataRow("project")]
    [DataRow("source")]
    [DataRow("same-job")]
    [DataRow("timestamp")]
    public void MismatchedFinalizationBindingsLeaveAllRowsUntouched(string field)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        SourceOnlyExecutionRequest changed = field switch
        {
            "draft" => seed.Request with { DraftId = EntityId<ProjectCreationDraftIdTag>.New() },
            "project" => seed.Request with { ProjectId = EntityId<ProjectIdTag>.New() },
            "source" => seed.Request with { SourceCandidateId = EntityId<AssetIdTag>.New() },
            "same-job" => seed.Request with { RootJobId = seed.Request.IngestJobId },
            "timestamp" => seed.Request with { CreatedAt = seed.Request.CreatedAt.AddSeconds(1) },
            _ => throw new AssertFailedException(),
        };
        Assert.ThrowsExactly<ArgumentException>(() => seed.Finalize(request: changed));
        AssertEmptyAcceptance(database, seed);
    }

    [TestMethod]
    [DataRow("projects")]
    [DataRow("project_default_settings")]
    [DataRow("root")]
    [DataRow("ingest")]
    [DataRow("source_only_executions")]
    [DataRow("draft-update")]
    public void InjectedFailureRollsBackProjectSettingsBothJobsExecutionAndDraft(string stage)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        string target = stage is "root" or "ingest" ? "jobs" : stage == "draft-update" ? "project_creation_drafts" : stage;
        string action = stage == "draft-update" ? "UPDATE" : "INSERT";
        string when = stage == "ingest" ? "WHEN NEW.kind = 'media.ingest.v1'" :
            stage == "root" ? "WHEN NEW.kind = 'source-only.pipeline.v1'" : "";
        database.ExecuteNonQuery($"""
            CREATE TRIGGER fail_source_only_test BEFORE {action} ON {target} {when}
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);
        Assert.ThrowsExactly<SqliteException>(() => seed.Finalize());
        AssertEmptyAcceptance(database, seed);
        database.ExecuteNonQuery("DROP TRIGGER fail_source_only_test;");
        _ = seed.Finalize();
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void UnregisteredProfileAndDuplicateJobRejectWithoutPartialAcceptance()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        DefaultSettingsProfile unregistered = DefaultSettingsProfile.Create(EntityId<DefaultSettingsProfileIdTag>.New(),
            "other-profile", new SettingsProfileVersion(1, 0, 0), seed.Profile.Definitions);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Finalize(
            request: seed.Request with { SettingsSnapshot = unregistered.CreateSnapshot() }));
        AssertEmptyAcceptance(database, seed);
        var other = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = other.Finalize();
        Assert.ThrowsExactly<SqliteException>(() => seed.Finalize(
            request: seed.Request with { IngestJobId = other.Request.IngestJobId }));
        Assert.IsNull(new ProjectStore(database.DatabasePath).GetProject(seed.Project.Id));
        Assert.IsFalse(seed.Drafts.GetDraft(seed.Draft.Id)!.IsFinalized);
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    public void StateNoOpIsCanonicalAndRevisionCheckedBeforeNoOp()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        StoredSourceOnlyExecution initial = seed.Finalize();
        Assert.AreEqual(initial.StateDigest, seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Queued).StateDigest);
        StoredSourceOnlyExecution advanced = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Analyzing);
        Assert.AreEqual(1, advanced.Revision);
        Assert.ThrowsExactly<StoreConcurrencyException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Analyzing));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 1, SourceOnlyExecutionPhase.Ingesting));
        Assert.AreEqual(1, seed.Store.UpdateState(seed.Request.ExecutionId, 1, SourceOnlyExecutionPhase.Analyzing).Revision);
        CompositionVersion version = seed.AppendStandardVersion();
        string result = seed.ResultJson(seed.Request, version);
        StoredSourceOnlyExecution withResult = seed.Store.UpdateState(seed.Request.ExecutionId, 1,
            SourceOnlyExecutionPhase.Composing, result, version.Id, version.ContentDigest);
        StoredSourceOnlyExecution canonicalNoOp = seed.Store.UpdateState(seed.Request.ExecutionId, 2,
            SourceOnlyExecutionPhase.Composing, " \n" + result, version.Id, version.ContentDigest);
        Assert.AreEqual(withResult.StateDigest, canonicalNoOp.StateDigest);
        Assert.AreEqual(2, canonicalNoOp.Revision);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 2,
            SourceOnlyExecutionPhase.Rendering));
    }

    [TestMethod]
    [DataRow(SourceOnlyExecutionPhase.Failed, "pipeline.failed")]
    [DataRow(SourceOnlyExecutionPhase.Canceled, null)]
    [DataRow(SourceOnlyExecutionPhase.ReselectionRequired, "source.reselection_required")]
    [DataRow(SourceOnlyExecutionPhase.ReconciliationRequired, "pipeline.reconciliation_required")]
    public void TerminalStatesAreImmutableButExactNoOpIsAllowed(SourceOnlyExecutionPhase phase, string? failure)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        StoredSourceOnlyExecution terminal = seed.Store.UpdateState(seed.Request.ExecutionId, 0, phase, failureCode: failure);
        Assert.AreEqual(terminal.StateDigest, seed.Store.UpdateState(seed.Request.ExecutionId, 1, phase, failureCode: failure).StateDigest);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 1, SourceOnlyExecutionPhase.Queued));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void CompletedRequiresBoundStoredVersionRealIngestAndResultAndDoesNotCompleteRootJob()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        Assert.ThrowsExactly<ArgumentException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Completed));
        CompositionVersion version = seed.AppendStandardVersion();
        string result = seed.ResultJson(seed.Request, version);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Completed, result, version.Id, version.ContentDigest));
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Delivering);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 1,
            SourceOnlyExecutionPhase.Completed, result, version.Id, version.ContentDigest));
        seed.CompleteIngest();
        StoredSourceOnlyExecution completed = seed.Store.UpdateState(seed.Request.ExecutionId, 1,
            SourceOnlyExecutionPhase.Completed, result, version.Id, version.ContentDigest);
        Assert.AreEqual(SourceOnlyExecutionPhase.Completed, completed.Phase);
        Assert.AreEqual(version.Id, completed.VersionId);
        Assert.AreEqual(ContentDigest.Compute(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(result))), completed.ResultDigest);
        Assert.AreEqual(JobState.Queued, new JobStore(database.DatabasePath).GetJob(seed.Request.RootJobId)!.Job.State);
        Assert.IsFalse(JsonSerializer.Serialize(completed).Contains("private-evidence-marker", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 2, SourceOnlyExecutionPhase.Queued));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 2,
            seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1)));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow("projectId")]
    [DataRow("executionId")]
    [DataRow("rootJobId")]
    [DataRow("settingsSnapshotDigest")]
    [DataRow("versionId")]
    [DataRow("versionDigest")]
    public void ResultBindingRejectsEachMismatchedHeader(string field)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion version = seed.AppendVersion();
        JsonNode root = JsonNode.Parse(seed.ResultJson(seed.Request, version))!;
        root["binding"]![field] = "mismatched";
        Assert.ThrowsExactly<ArgumentException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, root.ToJsonString(), version.Id, version.ContentDigest));
        Assert.AreEqual(0, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
    }

    [TestMethod]
    public void MissingWrongProjectWrongDigestAndPreAcceptanceAncestryAreRejected()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: EntityId<CompositionVersionIdTag>.New(), digest: seed.Digest));
        CompositionVersion version = seed.AppendVersion();
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: version.Id, digest: new ContentDigest(new string('b', 64))));
        CompositionVersion oldRoot = seed.AppendVersion(createdAt: seed.Request.CreatedAt.AddTicks(-1));
        CompositionVersion child = seed.AppendVersion(oldRoot.Id);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: child.Id, digest: child.ContentDigest));
        var other = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = other.Finalize();
        CompositionVersion foreign = other.AppendVersion();
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: foreign.Id, digest: foreign.ContentDigest));
        Assert.AreEqual(0, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
    }

    [TestMethod]
    public void RegenerationReusesCompletedIngestBindsCurrentSettingsAndIsIdempotentAfterRestart()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion parent = seed.AppendVersion();
        SourceOnlyExecutionRequest regeneration = seed.Regeneration(parent);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(regeneration, 0));
        seed.CompleteIngest();
        _ = seed.Settings.UpdateProjectSettings(seed.Project.Id, seed.AlternateSnapshot(), 0, seed.Request.CreatedAt.AddSeconds(3));
        Assert.ThrowsExactly<StoreConcurrencyException>(() => seed.Store.CreateRegeneration(regeneration, 0));
        Assert.ThrowsExactly<StoreConcurrencyException>(() => seed.Store.CreateRegeneration(regeneration, 2));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(regeneration, 1));
        regeneration = regeneration with { SettingsSnapshot = seed.AlternateSnapshot() };
        StoredSourceOnlyExecution accepted = seed.Store.CreateRegeneration(regeneration, 1);
        Assert.AreEqual(1, accepted.SettingsRevision);
        Assert.AreEqual(parent.Id, accepted.Request.ParentVersionId);
        Assert.AreEqual(seed.Request.IngestJobId, accepted.Request.IngestJobId);
        Assert.AreNotEqual(seed.Request.RootJobId, accepted.Request.RootJobId);
        Assert.IsNull(seed.Store.GetProtectedSourceLocator(regeneration.ExecutionId));
        Assert.AreEqual(3L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs WHERE kind = 'media.ingest.v1';"));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(regeneration.ExecutionId, 0, SourceOnlyExecutionPhase.Ingesting));
        _ = seed.Settings.UpdateProjectSettings(seed.Project.Id, seed.Snapshot, 1, regeneration.CreatedAt.AddSeconds(1));
        var restarted = new SourceOnlyExecutionStore(database.DatabasePath);
        Assert.AreEqual(accepted.RequestDigest, restarted.CreateRegeneration(regeneration, 1).RequestDigest);
        Assert.AreEqual(seed.Request.ExecutionId, restarted.GetByDraft(seed.Draft.Id)!.Request.ExecutionId);
        Assert.AreEqual(regeneration.ExecutionId, restarted.GetByJob(regeneration.IngestJobId)!.Request.ExecutionId);
        Assert.AreEqual(regeneration.ExecutionId, restarted.GetLatestForProject(seed.Project.Id)!.Request.ExecutionId);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void RegenerationConflictAndInjectedInsertFailureLeaveNoOrphanRootJob()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        SourceOnlyExecutionRequest request = seed.Regeneration(parent);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(request with { ParentDigest = new ContentDigest(new string('c', 64)) }, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(request with { SourceCandidateId = EntityId<AssetIdTag>.New() }, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(request with { IngestJobId = EntityId<JobIdTag>.New() }, 0));
        database.ExecuteNonQuery("""
            CREATE TRIGGER fail_regeneration BEFORE INSERT ON source_only_executions
            WHEN NEW.parent_version_id IS NOT NULL BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);
        Assert.ThrowsExactly<SqliteException>(() => seed.Store.CreateRegeneration(request, 0));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.IsNull(seed.Store.Get(request.ExecutionId));
        database.ExecuteNonQuery("DROP TRIGGER fail_regeneration;");
        _ = seed.Store.CreateRegeneration(request, 0);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(request with { ActorSid = "S-1-5-21-7777" }, 0));
        Assert.AreEqual(3L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    public void LatestOrderingUsesExplicitCreatedAtThenCanonicalIdNotInsertionOrder()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        SourceOnlyExecutionRequest larger = seed.Regeneration(parent) with
        { ExecutionId = EntityId<SourceOnlyExecutionIdTag>.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff") };
        SourceOnlyExecutionRequest smaller = seed.Regeneration(parent) with
        { ExecutionId = EntityId<SourceOnlyExecutionIdTag>.Parse("11111111-1111-4111-8111-111111111111") };
        _ = seed.Store.CreateRegeneration(larger, 0);
        _ = seed.Store.CreateRegeneration(smaller, 0);
        Assert.AreEqual(larger.ExecutionId, seed.Store.GetLatestForProject(seed.Project.Id)!.Request.ExecutionId);
        CollectionAssert.AreEqual(new[] { larger.ExecutionId, smaller.ExecutionId, seed.Request.ExecutionId },
            seed.Store.ListForProject(seed.Project.Id).Select(row => row.Request.ExecutionId).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StoredSourceOnlyExecution>)seed.Store.ListForProject(seed.Project.Id)).Clear());
    }

    [TestMethod]
    [DataRow("request-noncanonical")]
    [DataRow("request-hash")]
    [DataRow("request-id")]
    [DataRow("state-hash")]
    [DataRow("result-hash")]
    [DataRow("locator-hash")]
    public void CanonicalHashAndIdentityCorruptionFailClosedOnGetListAndValidateAll(string mutation)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize([1, 2, 3]);
        CompositionVersion version = seed.AppendVersion();
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            seed.ResultJson(seed.Request, version), version.Id, version.ContentDigest);
        SourceOnlyExecutionTestSeed.Corrupt(database.DatabasePath, mutation);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(seed.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
    }

    [TestMethod]
    public void BoundJobAndProfileCorruptionAreNotTrusted()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        database.ExecuteNonQuery("UPDATE jobs SET kind = 'other.kind' WHERE id = $id;", ("$id", seed.Request.RootJobId.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.GetByDraft(seed.Draft.Id));
        database.ExecuteNonQuery("UPDATE jobs SET kind = 'source-only.pipeline.v1' WHERE id = $id;", ("$id", seed.Request.RootJobId.ToString()));
        database.ExecuteNonQuery("UPDATE default_settings_profiles SET profile_payload = $payload;", ("$payload", Encoding.UTF8.GetBytes("{}")));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
    }

    [TestMethod]
    public void SqlGuardsRejectImmutableRequestAndTerminalMutation()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        Assert.ThrowsExactly<SqliteException>(() => database.ExecuteNonQuery("UPDATE source_only_executions SET request_payload = X'7b7d', revision = revision + 1;"));
        Assert.ThrowsExactly<SqliteException>(() => database.ExecuteNonQuery("DELETE FROM source_only_executions;"));
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Canceled);
        Assert.ThrowsExactly<SqliteException>(() => database.ExecuteNonQuery("UPDATE source_only_executions SET phase = 'Queued', revision = revision + 1;"));
    }

    [TestMethod]
    public void PayloadAndLocatorBoundsRejectInvalidUnicodeDuplicatesAndOversizeWithoutMutation()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        Assert.ThrowsExactly<ArgumentException>(() => seed.Finalize([]));
        Assert.ThrowsExactly<ArgumentException>(() => seed.Finalize(new byte[SourceOnlyExecutionStore.MaximumProtectedLocatorBytes + 1]));
        AssertEmptyAcceptance(database, seed);
        _ = seed.Finalize();
        foreach (string invalid in new[] { "[]", "{\"x\":1,\"x\":2}", "{\"x\":\"\ud800\"}", "{\"x\":\"" + new string('x', SourceOnlyExecutionStore.MaximumResultUtf8Bytes) + "\"}" })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => seed.Store.UpdateState(
                seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing, invalid));
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.Message.Contains(invalid, StringComparison.Ordinal));
        }
        Assert.ThrowsExactly<ArgumentException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0, (SourceOnlyExecutionPhase)99));
        Assert.ThrowsExactly<ArgumentException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: @"C:\private\error"));
        Assert.AreEqual(0, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
    }

    [TestMethod]
    public async Task ConcurrentStateUpdatesHaveExactlyOneRevisionWinner()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        using var release = new ManualResetEventSlim(false);
        Task<bool> Update(SourceOnlyExecutionPhase phase) => Task.Run(() =>
        {
            release.Wait();
            try
            {
                _ = new SourceOnlyExecutionStore(database.DatabasePath).UpdateState(seed.Request.ExecutionId, 0, phase);
                return true;
            }
            catch (StoreConcurrencyException) { return false; }
        });
        Task<bool> first = Update(SourceOnlyExecutionPhase.Analyzing);
        Task<bool> second = Update(SourceOnlyExecutionPhase.Ingesting);
        release.Set();
        bool[] outcomes = await Task.WhenAll(first, second);
        Assert.AreEqual(1, outcomes.Count(won => won));
        Assert.AreEqual(1, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
    }

    [TestMethod]
    public async Task ConcurrentIdenticalFinalizeRequestsReturnTheSameAtomicAcceptance()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        using var release = new ManualResetEventSlim(false);
        Task<StoredSourceOnlyExecution> Accept() => Task.Run(() =>
        {
            release.Wait();
            return new ProjectCreationDraftStore(database.DatabasePath).FinalizeSourceOnlyWithExecution(
                seed.Draft, seed.Project, "{\"draft\":false}", 0, seed.Request.CreatedAt, seed.Request);
        });
        Task<StoredSourceOnlyExecution> first = Accept();
        Task<StoredSourceOnlyExecution> second = Accept();
        release.Set();
        StoredSourceOnlyExecution[] outcomes = await Task.WhenAll(first, second);
        Assert.AreEqual(outcomes[0].RequestDigest, outcomes[1].RequestDigest);
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
    }

    [TestMethod]
    public void MalformedResultHeaderAndFutureParentAreRejectedWithoutMutation()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion parent = seed.AppendVersion();
        foreach (string result in new[] { "{}", "{\"binding\":null}", "{\"binding\":1}", "{\"binding\":{}}" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
                SourceOnlyExecutionPhase.Composing, result, parent.Id, parent.ContentDigest));
        }
        seed.CompleteIngest();
        SourceOnlyExecutionRequest request = seed.Regeneration(parent) with { CreatedAt = seed.Request.CreatedAt.AddSeconds(2) };
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(request, 0));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    public void ResultCannotPrecedeAcceptanceButExactTimestampAndDomainDigestAreAccepted()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion old = seed.AppendVersion(createdAt: seed.Request.CreatedAt.AddTicks(-1));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: old.Id, digest: old.ContentDigest));
        SqliteException sqlFailure = Assert.ThrowsExactly<SqliteException>(() => database.ExecuteNonQuery("""
            UPDATE source_only_executions SET revision = 1, phase = 'Composing', version_id = $version,
                version_digest = $digest WHERE id = $id;
            """, ("$version", old.Id.ToString()), ("$digest", old.ContentDigest.Hex), ("$id", seed.Request.ExecutionId.ToString())));
        Assert.AreEqual(1811, sqlFailure.SqliteExtendedErrorCode);
        Assert.AreEqual(0, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
        CompositionVersion fresh = seed.AppendVersion(createdAt: seed.Request.CreatedAt);
        Assert.AreNotEqual(ContentDigest.Compute(Encoding.UTF8.GetBytes(SourceOnlyExecutionTestSeed.VersionPayload(fresh))),
            fresh.ContentDigest, "The accepted digest must be the elements digest, not the payload hash.");
        StoredSourceOnlyExecution bound = seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: fresh.Id, digest: fresh.ContentDigest);
        Assert.AreEqual(fresh.Id, bound.VersionId);
        Assert.AreEqual(bound.StateDigest, new SourceOnlyExecutionStore(database.DatabasePath).Get(seed.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RegenerationAcceptsDescendantsButRejectsParentOldChildAndUnrelatedBranches(bool useGrandchild)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        SourceOnlyExecutionRequest request = seed.Regeneration(parent);
        _ = seed.Store.CreateRegeneration(request, 0);
        CompositionVersion oldChild = seed.AppendVersion(parent.Id, request.CreatedAt.AddTicks(-1));
        CompositionVersion sibling = seed.AppendVersion(createdAt: request.CreatedAt, elements: parent.Elements);
        CompositionVersion child = seed.AppendVersion(parent.Id, request.CreatedAt);
        CompositionVersion grandchild = seed.AppendVersion(child.Id, request.CreatedAt, child.Elements);
        CompositionVersion freshAfterOldChild = seed.AppendVersion(oldChild.Id, request.CreatedAt);
        foreach (CompositionVersion invalid in new[] { parent, oldChild, sibling, freshAfterOldChild })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(request.ExecutionId, 0,
                SourceOnlyExecutionPhase.Composing, versionId: invalid.Id, digest: invalid.ContentDigest));
            Assert.ThrowsExactly<SqliteException>(() => BindVersionSql(database, seed.Store.Get(request.ExecutionId)!, invalid));
            Assert.AreEqual(0, seed.Store.Get(request.ExecutionId)!.Revision);
        }
        CompositionVersion result = useGrandchild ? grandchild : child;
        StoredSourceOnlyExecution bound = seed.Store.UpdateState(request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, seed.ResultJson(request, result), result.Id, result.ContentDigest);
        Assert.AreEqual(result.Id, bound.VersionId);
        Assert.AreEqual(parent.ContentDigest, sibling.ContentDigest, "Digest equality is not identity or ancestry.");
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void InitialStandardStyleGrandchildMayKeepItsParentsElementsDigest()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        StoredSourceOnlyExecution pending = seed.Finalize();
        CompositionVersion baseline = seed.AppendVersion(createdAt: seed.Request.CreatedAt);
        CompositionVersion auto = seed.AppendVersion(baseline.Id, seed.Request.CreatedAt.AddTicks(1));
        CompositionVersion standard = seed.AppendVersion(auto.Id, seed.Request.CreatedAt.AddTicks(2), auto.Elements);
        Assert.AreEqual(auto.ContentDigest, standard.ContentDigest);
        Assert.AreNotEqual(auto.Id, standard.Id);
        Assert.AreEqual(1, BindVersionSql(database, pending, standard), "The SQL guard must allow the same multi-stage shape as the store.");
        StoredSourceOnlyExecution bound = seed.Store.Get(seed.Request.ExecutionId)!;
        Assert.AreEqual(standard.Id, bound.VersionId);
        Assert.AreEqual(standard.ContentDigest, bound.VersionDigest);
        Assert.AreEqual(bound.StateDigest, seed.Store.UpdateState(seed.Request.ExecutionId, 1,
            SourceOnlyExecutionPhase.Composing, versionId: standard.Id, digest: standard.ContentDigest).StateDigest);
        CompositionVersion replacement = seed.AppendVersion(auto.Id, standard.CreatedAt, auto.Elements);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 1,
            SourceOnlyExecutionPhase.Rendering, versionId: replacement.Id, digest: replacement.ContentDigest));
        Assert.ThrowsExactly<SqliteException>(() => BindVersionSql(database, bound, replacement));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow("cycle")]
    [DataRow("future-ancestor")]
    [DataRow("old-root")]
    [DataRow("payload-parent")]
    [DataRow("forged-ancestor-digest")]
    [DataRow("missing-parent")]
    [DataRow("cross-project-ancestor")]
    public void EveryInitialAncestorIsValidatedOnBindAndEveryRead(string mutation)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        StoredSourceOnlyExecution pending = seed.Finalize();
        CompositionVersion root = seed.AppendVersion(createdAt: seed.Request.CreatedAt);
        CompositionVersion auto = seed.AppendVersion(root.Id, seed.Request.CreatedAt);
        CompositionVersion standard = seed.AppendVersion(auto.Id, seed.Request.CreatedAt, auto.Elements);
        EntityId<ProjectIdTag> projectId = root.ProjectId;
        if (mutation == "cross-project-ancestor")
        {
            var foreign = new SourceOnlyExecutionTestSeed(database.DatabasePath);
            _ = foreign.Finalize();
            projectId = foreign.Project.Id;
        }
        var changed = new CompositionVersion(root.Id, projectId,
            mutation == "cycle" ? auto.Id : mutation == "missing-parent" ? EntityId<CompositionVersionIdTag>.New() : root.ParentVersionId,
            root.CreatedByActorId,
            mutation == "old-root" ? root.CreatedAt.AddTicks(-1) :
                mutation == "future-ancestor" ? root.CreatedAt.AddTicks(1) : root.CreatedAt,
            root.ChangeReason, mutation == "forged-ancestor-digest" ? seed.Digest : root.ContentDigest, root.Elements);
        string payload = SourceOnlyExecutionTestSeed.VersionPayload(changed);
        if (mutation == "payload-parent")
        {
            JsonNode json = JsonNode.Parse(payload)!;
            json["parentVersionId"] = auto.Id.ToString();
            payload = CanonicalJson.Canonicalize(json.ToJsonString());
        }
        void Corrupt() => RewriteVersion(database, changed, payload,
            bypassForeignKeys: mutation is "missing-parent" or "cross-project-ancestor");
        Corrupt();
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(
            seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing, versionId: standard.Id, digest: standard.ContentDigest));
        Assert.IsNull(failure.InnerException);
        Assert.AreEqual(0, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
        // SQL can check structural/time failures, but cannot recompute typed domain digests.
        if (mutation is not ("payload-parent" or "forged-ancestor-digest"))
        {
            SqliteException sql = Assert.ThrowsExactly<SqliteException>(() => BindVersionSql(database, pending, standard));
            Assert.AreEqual(1811, sql.SqliteExtendedErrorCode);
        }
        RewriteVersion(database, root);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: standard.Id, digest: standard.ContentDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
        Corrupt();
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(seed.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 1,
            SourceOnlyExecutionPhase.Composing, versionId: standard.Id, digest: standard.ContentDigest));
    }

    [TestMethod]
    [DataRow("cycle")]
    [DataRow("future-ancestor")]
    [DataRow("forged-digest")]
    public void RegenerationChecksHistoricalAncestorsAboveItsExactParent(string mutation)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion root = seed.AppendVersion(createdAt: seed.Request.CreatedAt.AddTicks(-1));
        CompositionVersion parent = seed.AppendVersion(root.Id, root.CreatedAt);
        SourceOnlyExecutionRequest request = seed.Regeneration(parent);
        var corruptedRoot = new CompositionVersion(root.Id, root.ProjectId,
            mutation == "cycle" ? parent.Id : null, root.CreatedByActorId,
            mutation == "future-ancestor" ? parent.CreatedAt.AddTicks(1) : root.CreatedAt,
            root.ChangeReason, mutation == "forged-digest" ? seed.Digest : root.ContentDigest, root.Elements);
        RewriteVersion(database, corruptedRoot);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(request, 0));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        RewriteVersion(database, root);
        _ = seed.Store.CreateRegeneration(request, 0);
        CompositionVersion auto = seed.AppendVersion(parent.Id, request.CreatedAt);
        CompositionVersion standard = seed.AppendVersion(auto.Id, request.CreatedAt, auto.Elements);
        _ = seed.Store.UpdateState(request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: standard.Id, digest: standard.ContentDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
        RewriteVersion(database, corruptedRoot);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void VersionLineageBoundIncludesRootAndIsInclusive(bool regeneration)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        StoredSourceOnlyExecution pending = seed.Finalize();
        CompositionVersion version = seed.AppendVersion(createdAt: seed.Request.CreatedAt);
        if (regeneration)
        {
            seed.CompleteIngest();
            pending = seed.Store.CreateRegeneration(seed.Regeneration(version), 0);
        }
        for (int depth = 1; depth < SourceOnlyExecutionStore.MaximumVersionLineageLength; depth++)
        {
            version = seed.AppendVersion(version.Id, pending.Request.CreatedAt, version.Elements);
        }
        CompositionVersion tooDeep = seed.AppendVersion(version.Id, pending.Request.CreatedAt, version.Elements);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(pending.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: tooDeep.Id, digest: tooDeep.ContentDigest));
        Assert.ThrowsExactly<SqliteException>(() => BindVersionSql(database, pending, tooDeep));
        StoredSourceOnlyExecution bound = seed.Store.UpdateState(pending.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: version.Id, digest: version.ContentDigest);
        Assert.AreEqual(version.Id, bound.VersionId);
        if (!regeneration) { seed.CompleteIngest(); }
        SourceOnlyExecutionRequest impossible = seed.Regeneration(version) with { CreatedAt = pending.Request.CreatedAt.AddMinutes(1) };
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(impossible, 0));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ResultExclusivityIncludesSharedIntermediatesButNotHistoricalParents(bool firstBindsIntermediate)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        StoredSourceOnlyExecution first = seed.Store.CreateRegeneration(seed.Regeneration(parent), 0);
        StoredSourceOnlyExecution second = seed.Store.CreateRegeneration(seed.Regeneration(parent), 0);
        CompositionVersion intermediate = seed.AppendVersion(parent.Id, first.Request.CreatedAt);
        CompositionVersion firstResult = firstBindsIntermediate ? intermediate :
            seed.AppendVersion(intermediate.Id, first.Request.CreatedAt, intermediate.Elements);
        CompositionVersion secondResult = seed.AppendVersion(intermediate.Id, second.Request.CreatedAt, intermediate.Elements);
        _ = seed.Store.UpdateState(first.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: firstResult.Id, digest: firstResult.ContentDigest);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(second.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: secondResult.Id, digest: secondResult.ContentDigest));
        SqliteException sql = Assert.ThrowsExactly<SqliteException>(() => BindVersionSql(database, second, secondResult));
        Assert.AreEqual(1811, sql.SqliteExtendedErrorCode);

        SourceOnlyExecutionRequest nextRequest = seed.Regeneration(firstResult) with { CreatedAt = first.Request.CreatedAt.AddSeconds(1) };
        _ = seed.Store.CreateRegeneration(nextRequest, 0);
        CompositionVersion next = seed.AppendVersion(firstResult.Id, nextRequest.CreatedAt);
        _ = seed.Store.UpdateState(nextRequest.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: next.Id, digest: next.ContentDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);

        WithoutExecutionUpdateGuard(database, () => Assert.AreEqual(1, BindVersionSql(database, second, secondResult)));
        DatabaseMigrator.ValidateCurrentDatabase(database.DatabasePath);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(first.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(second.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
    }

    [TestMethod]
    public void ResultVersionIsExclusiveInApiSqlAndReadsEvenWithoutTheUniqueIndex()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        SourceOnlyExecutionRequest first = seed.Regeneration(parent);
        SourceOnlyExecutionRequest second = seed.Regeneration(parent);
        _ = seed.Store.CreateRegeneration(first, 0);
        StoredSourceOnlyExecution pending = seed.Store.CreateRegeneration(second, 0);
        CompositionVersion child = seed.AppendVersion(parent.Id, first.CreatedAt);
        _ = seed.Store.UpdateState(first.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: child.Id, digest: child.ContentDigest);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(second.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: child.Id, digest: child.ContentDigest));
        Assert.AreEqual(pending.StateDigest, seed.Store.Get(second.ExecutionId)!.StateDigest);

        // All other fields (including the state hash) are valid. This isolates the unique constraint.
        string stateJson = CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "source-only.state.v1",
            requestDigest = pending.RequestDigest.Hex,
            revision = 1,
            phase = "Composing",
            resultDigest = (string?)null,
            versionId = child.Id.ToString(),
            versionDigest = child.ContentDigest.Hex,
            failureCode = (string?)null,
        }));
        string stateHash = ContentDigest.Compute(Encoding.UTF8.GetBytes(stateJson)).Hex;
        int BindDuplicate() => database.ExecuteNonQuery("""
            UPDATE source_only_executions SET revision = 1, phase = 'Composing',
                version_id = $version, version_digest = $digest, state_digest = $state WHERE id = $id;
            """, ("$version", child.Id.ToString()), ("$digest", child.ContentDigest.Hex),
            ("$state", stateHash), ("$id", second.ExecutionId.ToString()));
        SqliteException failure = Assert.ThrowsExactly<SqliteException>(() => BindDuplicate());
        Assert.AreEqual(2067, failure.SqliteExtendedErrorCode, "Must fail on the UNIQUE index, not an unrelated trigger.");
        Assert.AreEqual(pending.StateDigest, seed.Store.Get(second.ExecutionId)!.StateDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);

        database.ExecuteNonQuery("DROP INDEX idx_source_only_executions_result_version;");
        Assert.AreEqual(1, BindDuplicate());
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(first.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(second.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
    }

    [TestMethod]
    [DataRow("createdAt")]
    [DataRow("parent")]
    public void BoundResultReadRechecksTimeAndExactAncestorWithConsistentPayloadAndColumns(string mutation)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        SourceOnlyExecutionRequest request = seed.Regeneration(parent);
        _ = seed.Store.CreateRegeneration(request, 0);
        CompositionVersion child = seed.AppendVersion(parent.Id, request.CreatedAt);
        _ = seed.Store.UpdateState(request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: child.Id, digest: child.ContentDigest);
        var changed = new CompositionVersion(child.Id, child.ProjectId,
            mutation == "parent" ? null : parent.Id, child.CreatedByActorId,
            mutation == "createdAt" ? request.CreatedAt.AddTicks(-1) : child.CreatedAt,
            child.ChangeReason, child.ContentDigest, child.Elements);
        database.ExecuteNonQuery("""
            UPDATE composition_versions SET parent_version_id = $parent, created_at = $created, payload = $payload WHERE id = $id;
            """, ("$parent", changed.ParentVersionId?.ToString()),
            ("$created", changed.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$payload", Encoding.UTF8.GetBytes(SourceOnlyExecutionTestSeed.VersionPayload(changed))), ("$id", child.Id.ToString()));
        DatabaseMigrator.ValidateCurrentDatabase(database.DatabasePath);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(request.ExecutionId, 1,
            SourceOnlyExecutionPhase.Composing, versionId: child.Id, digest: child.ContentDigest));
    }

    [TestMethod]
    [DataRow("blob")]
    [DataRow("empty-object")]
    [DataRow("id")]
    [DataRow("projectId")]
    [DataRow("createdAt")]
    [DataRow("contentDigest")]
    [DataRow("extra")]
    [DataRow("null-parent")]
    [DataRow("duplicate-element")]
    [DataRow("unreduced-time")]
    [DataRow("fractional-time")]
    [DataRow("unknown-origin")]
    [DataRow("source-duration")]
    [DataRow("suggestion-origin")]
    [DataRow("evidence")]
    public void InvalidVersionPayloadIsRejectedOnBindAndAfterBindingOnEveryRead(string mutation)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion version = seed.AppendVersion();
        byte[] valid = Encoding.UTF8.GetBytes(SourceOnlyExecutionTestSeed.VersionPayload(version));
        JsonNode root = JsonNode.Parse(valid)!;
        switch (mutation)
        {
            case "blob": case "empty-object": break;
            case "id": root["id"] = EntityId<CompositionVersionIdTag>.New().ToString(); break;
            case "projectId": root["projectId"] = EntityId<ProjectIdTag>.New().ToString(); break;
            case "createdAt": root["createdAt"] = version.CreatedAt.AddTicks(1).ToString("O", CultureInfo.InvariantCulture); break;
            case "contentDigest": root["contentDigest"] = seed.Digest.Hex; break;
            case "extra": root["extra"] = true; break;
            case "null-parent": root["parentVersionId"] = null; break;
            case "duplicate-element": root["elements"]!.AsArray().Add(root["elements"]![0]!.DeepClone()); break;
            case "unreduced-time":
                root["elements"]![0]!["outputRange"]!["duration"]!["value"] = 20;
                root["elements"]![0]!["outputRange"]!["duration"]!["rate"] = 2;
                break;
            case "fractional-time": root["elements"]![0]!["outputRange"]!["duration"]!["value"] = 1.5m; break;
            case "unknown-origin": root["elements"]![0]!["origin"]!["kind"] = "unknown"; break;
            case "source-duration": root["elements"]![0]!["sourceRange"]!["duration"]!["value"] = 9; break;
            case "suggestion-origin": root["elements"]![0]!["adoptedSuggestion"]!["origin"]!["sourceSpanId"] = EntityId<SourceSpanIdTag>.New().ToString(); break;
            case "evidence":
                root["evidence"] = JsonSerializer.SerializeToNode(new
                { kind = "autoDecision", contentDigest = seed.Digest.Hex, canonicalJson = "{}" }); break;
            default: throw new AssertFailedException();
        }
        byte[] invalid = mutation == "blob" ? [1] : Encoding.UTF8.GetBytes(
            mutation == "empty-object" ? "{}" : CanonicalJson.Canonicalize(root.ToJsonString()));
        void SetPayload(byte[] payload) => database.ExecuteNonQuery(
            "UPDATE composition_versions SET payload = $payload WHERE id = $id;", ("$payload", payload), ("$id", version.Id.ToString()));
        SetPayload(invalid);
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(
            seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing, versionId: version.Id, digest: version.ContentDigest));
        Assert.IsNull(failure.InnerException);
        Assert.AreEqual(0, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
        SetPayload(valid);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: version.Id, digest: version.ContentDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
        SetPayload(invalid);
        DatabaseMigrator.ValidateCurrentDatabase(database.DatabasePath);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(seed.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 1,
            SourceOnlyExecutionPhase.Composing, versionId: version.Id, digest: version.ContentDigest));
    }

    [TestMethod]
    public void CanonicalIntegerExponentsBindAndReplayWithOriginalElementsDigest()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion version = seed.AppendVersion();
        using JsonDocument document = JsonDocument.Parse(SourceOnlyExecutionTestSeed.VersionPayload(version));
        JsonElement element = document.RootElement.GetProperty("elements")[0];
        foreach (string rangeName in new[] { "sourceRange", "outputRange" })
        {
            Assert.AreEqual("1e1", element.GetProperty(rangeName).GetProperty("duration").GetProperty("value").GetRawText());
        }
        string result = seed.ResultJson(seed.Request, version);
        StoredSourceOnlyExecution bound = seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, result, version.Id, version.ContentDigest);
        var restarted = new SourceOnlyExecutionStore(database.DatabasePath);
        Assert.AreEqual(version.ContentDigest, restarted.Get(seed.Request.ExecutionId)!.VersionDigest);
        Assert.AreEqual(bound.StateDigest, restarted.UpdateState(seed.Request.ExecutionId, bound.Revision,
            SourceOnlyExecutionPhase.Composing, result, version.Id, version.ContentDigest).StateDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow("outputRange", "duration", "value", "10.000000000000000000000000000001")]
    [DataRow("sourceRange", "duration", "value", "10.000000000000000000000000000001")]
    [DataRow("outputRange", "duration", "value", "9.999999999999999999999999999999")]
    [DataRow("sourceRange", "duration", "value", "9.999999999999999999999999999999")]
    [DataRow("outputRange", "start", "value", "0.000000000000000000000000000001")]
    [DataRow("sourceRange", "start", "value", "-2.000000000000000000000000000001")]
    [DataRow("outputRange", "duration", "rate", "1.000000000000000000000000000001")]
    [DataRow("sourceRange", "duration", "rate", "1.000000000000000000000000000001")]
    [DataRow("outputRange", "start", "rate", "1.000000000000000000000000000001")]
    [DataRow("sourceRange", "start", "rate", "1.000000000000000000000000000001")]
    public void FractionalRangeValuesBelowDecimalPrecisionAreRejectedOnBindAndReplay(
        string rangeName, string timeName, string fieldName, string rawNumber)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        StoredSourceOnlyExecution pending = seed.Finalize();
        CompositionVersion version = seed.AppendVersion();
        string validPayload = SourceOnlyExecutionTestSeed.VersionPayload(version);
        JsonNode root = JsonNode.Parse(validPayload)!;
        // A decimal literal would erase the fractional tail before the reader sees it.
        root["elements"]![0]![rangeName]![timeName]![fieldName] = JsonNode.Parse(rawNumber);
        string invalidPayload = CanonicalJson.Canonicalize(root.ToJsonString());
        using JsonDocument canonical = JsonDocument.Parse(invalidPayload);
        JsonElement number = canonical.RootElement.GetProperty("elements")[0]
            .GetProperty(rangeName).GetProperty(timeName).GetProperty(fieldName);
        Assert.IsTrue(number.GetRawText().Contains("e-", StringComparison.Ordinal),
            "Canonicalization must preserve the non-zero fractional tail.");
        Assert.AreNotEqual(validPayload, invalidPayload);
        Assert.AreEqual(version.ContentDigest.Hex, canonical.RootElement.GetProperty("contentDigest").GetString(),
            "Keep the original typed elements digest, not a digest of the tampered elements.");
        string result = seed.ResultJson(seed.Request, version);

        RewriteVersion(database, version, invalidPayload);
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(
            seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing, result, version.Id, version.ContentDigest));
        Assert.IsNull(failure.InnerException);
        Assert.AreEqual(pending.StateDigest, seed.Store.Get(seed.Request.ExecutionId)!.StateDigest);

        RewriteVersion(database, version);
        StoredSourceOnlyExecution bound = seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, result, version.Id, version.ContentDigest);
        var restarted = new SourceOnlyExecutionStore(database.DatabasePath);
        Assert.AreEqual(bound.StateDigest, restarted.Get(seed.Request.ExecutionId)!.StateDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);

        RewriteVersion(database, version, invalidPayload);
        DatabaseMigrator.ValidateCurrentDatabase(database.DatabasePath);
        Assert.ThrowsExactly<InvalidOperationException>(() => restarted.Get(seed.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => restarted.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => restarted.UpdateState(seed.Request.ExecutionId, bound.Revision,
            SourceOnlyExecutionPhase.Composing, result, version.Id, version.ContentDigest));
    }

    [TestMethod]
    [DataRow("id")]
    [DataRow("root")]
    [DataRow("draft")]
    public void InsertOrReplaceCannotEraseTerminalAcceptanceWithRecursiveTriggersOff(string collision)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        StoredSourceOnlyExecution terminal = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Canceled);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA recursive_triggers;";
        Assert.AreEqual(0L, command.ExecuteScalar(), "Do not weaken host configuration to arrange this test.");
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.AreEqual(1L, command.ExecuteScalar());
        // Give the proposed row fresh identities except for the one collision under test.
        string replacementId = collision == "id" ? seed.Request.ExecutionId.ToString() : EntityId<SourceOnlyExecutionIdTag>.New().ToString();
        string rootId = EntityId<JobIdTag>.New().ToString();
        command.CommandText = """
            INSERT INTO jobs SELECT $root, project_id, kind, state, created_at, updated_at, current_attempt_number, revision
                FROM jobs WHERE id = $originalRoot;
            """;
        command.Parameters.AddWithValue("$root", rootId);
        command.Parameters.AddWithValue("$originalRoot", seed.Request.RootJobId.ToString());
        Assert.AreEqual(1, command.ExecuteNonQuery());
        // draft_id has a unique finalized_project_id, so a new original draft cannot be made for
        // this project. Use a regeneration shape for id/root tests (successful shared ingest).
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion(createdAt: seed.Request.CreatedAt);
        command.Parameters.Clear();
        command.CommandText = """
            INSERT OR REPLACE INTO source_only_executions
            SELECT $id, project_id, draft_id, source_candidate_id, $root, ingest_job_id,
                $parent, $parentDigest, created_at, request_payload, request_digest,
                NULL, 0, 'Queued', NULL, NULL, NULL, NULL, NULL, state_digest, NULL
            FROM source_only_executions WHERE id = $original;
            """;
        command.Parameters.AddWithValue("$id", replacementId);
        command.Parameters.AddWithValue("$root", collision == "root" ? seed.Request.RootJobId.ToString() : rootId);
        command.Parameters.AddWithValue("$parent", collision == "draft" ? DBNull.Value : parent.Id.ToString());
        command.Parameters.AddWithValue("$parentDigest", collision == "draft" ? DBNull.Value : parent.ContentDigest.Hex);
        command.Parameters.AddWithValue("$original", seed.Request.ExecutionId.ToString());
        // An original acceptance needs queued ingest; restore via a new valid queued ingest Job,
        // not by weakening FK/trigger enforcement. The replaced record retains its succeeded Job.
        if (collision == "draft")
        {
            string ingestId = EntityId<JobIdTag>.New().ToString();
            new JobStore(database.DatabasePath).CreateJob(new Job(EntityId<JobIdTag>.Parse(ingestId), seed.Project.Id,
                SourceOnlyExecutionStore.IngestJobKind, JobState.Queued, seed.Request.CreatedAt, seed.Request.CreatedAt, 0));
            command.CommandText = command.CommandText.Replace("$root, ingest_job_id", "$root, $ingest", StringComparison.Ordinal);
            command.Parameters.AddWithValue("$ingest", ingestId);
        }
        SqliteException failure = Assert.ThrowsExactly<SqliteException>(() => command.ExecuteNonQuery());
        Assert.AreEqual(1811, failure.SqliteExtendedErrorCode);
        Assert.AreEqual(terminal.StateDigest, seed.Store.Get(seed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
        // Positive SQL control: remove identity collisions, use the successful ingest and
        // regeneration shape; all remaining SQL fields are unchanged. Roll it back afterwards.
        using SqliteTransaction transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.Parameters["$id"].Value = EntityId<SourceOnlyExecutionIdTag>.New().ToString();
        command.Parameters["$root"].Value = rootId;
        command.Parameters["$parent"].Value = parent.Id.ToString();
        command.Parameters["$parentDigest"].Value = parent.ContentDigest.Hex;
        if (collision == "draft") { command.Parameters["$ingest"].Value = seed.Request.IngestJobId.ToString(); }
        Assert.AreEqual(1, command.ExecuteNonQuery());
        transaction.Rollback();
        Assert.AreEqual(0L, database.ExecuteScalarInt64("PRAGMA recursive_triggers;"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConsistentlyForgedDigestInPayloadColumnAndCallerStillCannotBind(bool usePayloadHash)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion valid = seed.AppendVersion();
        ContentDigest forgedDigest = usePayloadHash
            ? ContentDigest.Compute(Encoding.UTF8.GetBytes(SourceOnlyExecutionTestSeed.VersionPayload(valid)))
            : seed.Digest;
        var forged = new CompositionVersion(valid.Id, valid.ProjectId, valid.ParentVersionId,
            valid.CreatedByActorId, valid.CreatedAt, valid.ChangeReason, forgedDigest, valid.Elements);
        database.ExecuteNonQuery("UPDATE composition_versions SET payload = $payload, content_digest = $digest WHERE id = $id;",
            ("$payload", Encoding.UTF8.GetBytes(SourceOnlyExecutionTestSeed.VersionPayload(forged))),
            ("$digest", forgedDigest.Hex), ("$id", valid.Id.ToString()));
        DatabaseMigrator.ValidateCurrentDatabase(database.DatabasePath);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: forged.Id, digest: forged.ContentDigest));
        Assert.AreEqual(0, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
    }

    [TestMethod]
    [DataRow("duplicate-id")]
    [DataRow("zero-duration")]
    [DataRow("missing-source")]
    [DataRow("null-suggestion")]
    public void InvalidDomainElementsRejectEvenWhenRawElementsDigestIsRecomputed(string mutation)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion valid = seed.AppendVersion();
        JsonNode root = JsonNode.Parse(SourceOnlyExecutionTestSeed.VersionPayload(valid))!;
        switch (mutation)
        {
            case "duplicate-id": root["elements"]!.AsArray().Add(root["elements"]![0]!.DeepClone()); break;
            case "zero-duration":
                root["elements"]![0]!["outputRange"]!["duration"]!["value"] = 0;
                root["elements"]![0]!["sourceRange"]!["duration"]!["value"] = 0;
                break;
            case "missing-source": root["elements"]![0]!.AsObject().Remove("sourceRange"); break;
            case "null-suggestion": root["elements"]![0]!["adoptedSuggestion"] = null; break;
            default: throw new AssertFailedException();
        }
        var elementsObject = new JsonObject { ["elements"] = root["elements"]!.DeepClone() };
        ContentDigest digest = ContentDigest.Compute(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(elementsObject.ToJsonString())));
        root["contentDigest"] = digest.Hex;
        database.ExecuteNonQuery("UPDATE composition_versions SET payload = $payload, content_digest = $digest WHERE id = $id;",
            ("$payload", Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(root.ToJsonString()))),
            ("$digest", digest.Hex), ("$id", valid.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: valid.Id, digest: digest));
        Assert.AreEqual(0, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
    }

    [TestMethod]
    [DataRow("asset")]
    [DataRow("editorialRecord")]
    public void ValidNonSourceOriginsBindWithoutSourceRangeOrSuggestion(string kind)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        CompositionVersion original = seed.AppendVersion();
        OriginRef origin = kind == "asset" ? OriginRef.FromAsset(seed.Request.SourceCandidateId) :
            OriginRef.FromEditorialRecord(EntityId<EditorialRecordIdTag>.New());
        var element = new CompositionElement(original.Elements[0].Id, original.Elements[0].OutputRange, origin);
        JsonNode root = JsonNode.Parse(SourceOnlyExecutionTestSeed.VersionPayload(original))!;
        JsonObject item = root["elements"]![0]!.AsObject();
        item.Remove("sourceRange");
        item.Remove("adoptedSuggestion");
        item["origin"] = kind == "asset"
            ? JsonSerializer.SerializeToNode(new { kind, assetId = element.Origin.AssetId!.ToString() })
            : JsonSerializer.SerializeToNode(new { kind, editorialRecordId = element.Origin.EditorialRecordId!.ToString() });
        ContentDigest digest = ContentDigest.Compute(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(
            new JsonObject { ["elements"] = root["elements"]!.DeepClone() }.ToJsonString())));
        var version = new CompositionVersion(original.Id, original.ProjectId, null, original.CreatedByActorId,
            original.CreatedAt, original.ChangeReason, digest, [element]);
        root["contentDigest"] = version.ContentDigest.Hex;
        database.ExecuteNonQuery("UPDATE composition_versions SET payload = $payload, content_digest = $digest WHERE id = $id;",
            ("$payload", Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(root.ToJsonString()))),
            ("$digest", digest.Hex), ("$id", version.Id.ToString()));
        StoredSourceOnlyExecution bound = seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: version.Id, digest: version.ContentDigest);
        Assert.AreEqual(version.ContentDigest, bound.VersionDigest);
        Assert.AreEqual(bound.StateDigest, seed.Store.Get(seed.Request.ExecutionId)!.StateDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow(JobState.Failed, false)]
    [DataRow(JobState.Cancelled, false)]
    [DataRow(JobState.Cancelled, true)]
    [DataRow(JobState.Interrupted, false)]
    public void PreVersionRetryCreatesAtomicSuccessorWithoutRewritingTerminalJobs(JobState jobState, bool beforeAttempt)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize([1, 2, 3]);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Ingesting);
        StoredJobAggregate oldRoot = FinishTestJob(database, seed.Request.RootJobId, jobState, beforeAttempt);
        StoredJobAggregate oldIngest = FinishTestJob(database, seed.Request.IngestJobId, jobState, beforeAttempt);
        SourceOnlyExecutionPhase phase = jobState == JobState.Cancelled ? SourceOnlyExecutionPhase.Canceled : SourceOnlyExecutionPhase.Failed;
        StoredSourceOnlyExecution terminal = seed.Store.UpdateState(seed.Request.ExecutionId, 1, phase,
            failureCode: phase == SourceOnlyExecutionPhase.Failed ? "pipeline.failed" : null);
        _ = seed.Settings.UpdateProjectSettings(seed.Project.Id, seed.AlternateSnapshot(), 0, seed.Request.CreatedAt.AddSeconds(3));
        DateTimeOffset retriedAt = seed.Request.CreatedAt.AddMinutes(1);
        const string retryActor = "S-1-5-21-2000";
        StoredSourceOnlyExecution retry = seed.Store.CreateRetry(seed.Request.ExecutionId, terminal.Revision, retryActor, retriedAt);

        Assert.AreNotEqual(seed.Request.ExecutionId, retry.Request.ExecutionId);
        Assert.AreNotEqual(seed.Request.RootJobId, retry.Request.RootJobId);
        Assert.AreNotEqual(seed.Request.IngestJobId, retry.Request.IngestJobId);
        Assert.AreEqual(terminal.Request.ExecutionId, retry.RetryOfExecutionId);
        Assert.AreEqual(terminal.Revision, retry.RetryOfRevision);
        Assert.AreEqual(terminal.StateDigest, retry.RetryOfStateDigest);
        Assert.AreEqual(SourceOnlyExecutionPhase.Queued, retry.Phase);
        Assert.AreEqual(0, retry.Revision);
        Assert.IsNull(retry.VersionId);
        Assert.IsNull(retry.ResultJson);
        Assert.IsNull(retry.Request.ParentVersionId, "Retry is not a fabricated regeneration parent.");
        Assert.AreEqual(terminal.Request.SettingsSnapshot.CanonicalJson, retry.Request.SettingsSnapshot.CanonicalJson);
        Assert.AreEqual(terminal.SettingsRevision, retry.SettingsRevision);
        Assert.AreEqual(terminal.FinalizationDigest, retry.FinalizationDigest);
        Assert.AreEqual(terminal.LocatorDigest, retry.LocatorDigest);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, seed.Store.GetProtectedSourceLocator(retry.Request.ExecutionId)!);
        Assert.AreEqual(retryActor, retry.Request.ActorSid);
        Assert.AreEqual(retriedAt, retry.Request.CreatedAt);
        var jobs = new JobStore(database.DatabasePath);
        foreach (EntityId<JobIdTag> jobId in new[] { retry.Request.RootJobId, retry.Request.IngestJobId })
        {
            StoredJobAggregate queued = jobs.GetJobAggregate(jobId)!;
            Assert.AreEqual(JobState.Queued, queued.Job.State);
            Assert.AreEqual(0, queued.Revision);
            Assert.AreEqual(0, queued.Attempts.Count);
            Assert.AreEqual(retriedAt, queued.Job.CreatedAt);
        }
        Assert.AreEqual(JsonSerializer.Serialize(oldRoot), JsonSerializer.Serialize(jobs.GetJobAggregate(oldRoot.Job.Id)));
        Assert.AreEqual(JsonSerializer.Serialize(oldIngest), JsonSerializer.Serialize(jobs.GetJobAggregate(oldIngest.Job.Id)));
        Assert.AreEqual(terminal.RequestJson, seed.Store.Get(seed.Request.ExecutionId)!.RequestJson);
        Assert.AreEqual(terminal.StateDigest, seed.Store.Get(seed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(terminal.Revision, seed.Store.Get(seed.Request.ExecutionId)!.Revision);
        Assert.AreEqual(terminal.Phase, seed.Store.GetByDraft(seed.Draft.Id)!.Phase);
        Assert.AreEqual(seed.Request.ExecutionId, seed.Finalize([1, 2, 3]).Request.ExecutionId);
        Assert.AreEqual(retry.Request.ExecutionId, seed.Store.GetLatestForProject(seed.Project.Id)!.Request.ExecutionId);
        Assert.AreEqual(4L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));

        // No real pipeline side effects are claimed by this fixture: only JobStore records.
        _ = FinishTestJob(database, retry.Request.IngestJobId, JobState.Succeeded);
        _ = FinishTestJob(database, retry.Request.RootJobId, JobState.Succeeded);
        CompositionVersion stale = seed.AppendVersion(createdAt: retriedAt.AddTicks(-1));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(retry.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Composing, versionId: stale.Id, digest: stale.ContentDigest));
        CompositionVersion standard = seed.AppendStandardVersion(retriedAt.AddSeconds(3));
        string result = seed.ResultJson(retry.Request, standard);
        Assert.ThrowsExactly<ArgumentException>(() => seed.Store.UpdateState(retry.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Delivering, seed.ResultJson(seed.Request, standard), standard.Id, standard.ContentDigest));
        _ = seed.Store.UpdateState(retry.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Delivering,
            result, standard.Id, standard.ContentDigest);
        StoredSourceOnlyExecution completed = seed.Store.UpdateState(retry.Request.ExecutionId, 1, SourceOnlyExecutionPhase.Completed,
            result, standard.Id, standard.ContentDigest);
        Assert.AreEqual(completed.StateDigest, seed.Store.CreateRetry(seed.Request.ExecutionId, terminal.Revision, retryActor, retriedAt).StateDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RetryReusesSuccessfulOrUntouchedQueuedIngestAndIsRevisionCheckedBeforeReplay(bool succeeded)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        if (succeeded) { seed.CompleteIngest(); }
        _ = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        StoredSourceOnlyExecution failed = seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        var jobs = new JobStore(database.DatabasePath);
        string ingestBefore = JsonSerializer.Serialize(jobs.GetJobAggregate(seed.Request.IngestJobId));
        DateTimeOffset retriedAt = seed.Request.CreatedAt.AddMinutes(1);
        Assert.ThrowsExactly<StoreConcurrencyException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 0, seed.Request.ActorSid, retriedAt));
        StoredSourceOnlyExecution retry = seed.Store.CreateRetry(seed.Request.ExecutionId, failed.Revision, seed.Request.ActorSid, retriedAt);
        Assert.AreEqual(seed.Request.IngestJobId, retry.Request.IngestJobId);
        Assert.AreEqual(ingestBefore, JsonSerializer.Serialize(jobs.GetJobAggregate(seed.Request.IngestJobId)));
        Assert.AreEqual(3L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs WHERE kind = 'media.ingest.v1';"));
        StoredSourceOnlyExecution progress = seed.Store.UpdateState(retry.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Analyzing);
        var reopened = new SourceOnlyExecutionStore(database.DatabasePath);
        Assert.AreEqual(progress.StateDigest, reopened.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, retriedAt).StateDigest);
        Assert.AreEqual(retry.Request.ExecutionId, reopened.GetByJob(retry.Request.IngestJobId)!.Request.ExecutionId);
        Assert.ThrowsExactly<StoreConcurrencyException>(() => reopened.CreateRetry(seed.Request.ExecutionId, 0, seed.Request.ActorSid, retriedAt));
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.CreateRetry(seed.Request.ExecutionId, 1, "S-1-5-21-3000", retriedAt));
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, retriedAt.AddTicks(1)));
        Assert.AreEqual(3L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void RetryOfRetryCanSupplySuccessfulIngestToLaterRegeneration()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        _ = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        _ = FinishTestJob(database, seed.Request.IngestJobId, JobState.Failed);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        StoredSourceOnlyExecution first = seed.Store.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1));
        _ = FinishTestJob(database, first.Request.IngestJobId, JobState.Succeeded);
        _ = FinishTestJob(database, first.Request.RootJobId, JobState.Cancelled, beforeAttempt: true);
        _ = seed.Store.UpdateState(first.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Canceled);
        StoredSourceOnlyExecution second = seed.Store.CreateRetry(first.Request.ExecutionId, 1, seed.Request.ActorSid, first.Request.CreatedAt.AddMinutes(1));
        Assert.AreEqual(first.Request.ExecutionId, second.RetryOfExecutionId);
        Assert.AreEqual(first.Request.IngestJobId, second.Request.IngestJobId);
        Assert.AreEqual(5L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs WHERE kind = 'media.ingest.v1';"));
        CompositionVersion standard = seed.AppendStandardVersion(second.Request.CreatedAt);
        _ = seed.Store.UpdateState(second.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: standard.Id, digest: standard.ContentDigest);
        SourceOnlyExecutionRequest regeneration = seed.Regeneration(standard) with
        {
            IngestJobId = second.Request.IngestJobId,
            CreatedAt = second.Request.CreatedAt.AddMinutes(1),
        };
        StoredSourceOnlyExecution next = seed.Store.CreateRegeneration(regeneration, 0);
        Assert.IsNull(next.RetryOfExecutionId);
        Assert.AreEqual(standard.Id, next.Request.ParentVersionId);
        Assert.AreEqual(second.Request.IngestJobId, next.Request.IngestJobId);
        Assert.AreEqual(seed.Request.ExecutionId, seed.Store.GetByDraft(seed.Draft.Id)!.Request.ExecutionId);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void RegenerationRetryPreservesExactParentAndHistoricalSettingsRatherThanCurrentSettings()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendStandardVersion();
        DefaultSettingsSnapshot alternate = seed.AlternateSnapshot();
        _ = seed.Settings.UpdateProjectSettings(seed.Project.Id, alternate, 0, seed.Request.CreatedAt.AddSeconds(4));
        SourceOnlyExecutionRequest request = seed.Regeneration(parent) with { SettingsSnapshot = alternate };
        _ = seed.Store.CreateRegeneration(request, 1);
        _ = FinishTestJob(database, request.RootJobId, JobState.Failed);
        StoredSourceOnlyExecution failed = seed.Store.UpdateState(request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        _ = seed.Settings.UpdateProjectSettings(seed.Project.Id, seed.Snapshot, 1, request.CreatedAt.AddSeconds(4));
        StoredSourceOnlyExecution retry = seed.Store.CreateRetry(request.ExecutionId, failed.Revision, request.ActorSid, request.CreatedAt.AddMinutes(1));
        Assert.AreEqual(1, retry.SettingsRevision);
        Assert.AreEqual(alternate.CanonicalJson, retry.Request.SettingsSnapshot.CanonicalJson);
        Assert.AreEqual(parent.Id, retry.Request.ParentVersionId);
        Assert.AreEqual(parent.ContentDigest, retry.Request.ParentDigest);
        Assert.AreEqual(request.IngestJobId, retry.Request.IngestJobId);
        Assert.IsNull(seed.Store.GetProtectedSourceLocator(retry.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.UpdateState(retry.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Ingesting));
        CompositionVersion auto = seed.AppendVersion(parent.Id, retry.Request.CreatedAt);
        CompositionVersion standard = seed.AppendVersion(auto.Id, retry.Request.CreatedAt, auto.Elements);
        _ = seed.Store.UpdateState(retry.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            seed.ResultJson(retry.Request, standard), standard.Id, standard.ContentDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow(SourceOnlyExecutionPhase.Failed)]
    [DataRow(SourceOnlyExecutionPhase.Canceled)]
    public void BoundResultRetryIsRejectedWithoutClearingRecoveryPayloadOrBinding(SourceOnlyExecutionPhase terminalPhase)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize([1, 2, 3]);
        CompositionVersion standard = seed.AppendStandardVersion();
        string result = seed.ResultJson(seed.Request, standard);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Rendering,
            result, standard.Id, standard.ContentDigest);
        _ = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        StoredSourceOnlyExecution terminal = seed.Store.UpdateState(seed.Request.ExecutionId, 1, terminalPhase,
            result, standard.Id, standard.ContentDigest, terminalPhase == SourceOnlyExecutionPhase.Failed ? "pipeline.failed" : null);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, terminal.Revision,
            seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1)));
        StoredSourceOnlyExecution unchanged = seed.Store.Get(seed.Request.ExecutionId)!;
        Assert.AreEqual(terminal.StateDigest, unchanged.StateDigest);
        Assert.AreEqual(terminal.RequestJson, unchanged.RequestJson);
        Assert.AreEqual(terminal.ResultJson, unchanged.ResultJson);
        Assert.AreEqual(standard.Id, unchanged.VersionId);
        Assert.AreEqual(standard.ContentDigest, unchanged.VersionDigest);
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
    }

    [TestMethod]
    [DataRow(SourceOnlyExecutionPhase.Queued, null)]
    [DataRow(SourceOnlyExecutionPhase.Analyzing, null)]
    [DataRow(SourceOnlyExecutionPhase.ReselectionRequired, "source.reselection_required")]
    [DataRow(SourceOnlyExecutionPhase.ReconciliationRequired, "pipeline.reconciliation_required")]
    public void RetryNeverReopensActiveReselectionOrUnreconciledExecutions(SourceOnlyExecutionPhase phase, string? failure)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        _ = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        StoredSourceOnlyExecution source = seed.Store.UpdateState(seed.Request.ExecutionId, 0, phase, failureCode: failure);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, source.Revision,
            seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1)));
        Assert.AreEqual(source.StateDigest, seed.Store.Get(seed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    [DataRow("root-queued")]
    [DataRow("root-running")]
    [DataRow("root-succeeded")]
    [DataRow("ingest-running")]
    [DataRow("ingest-requeued")]
    public void RetryDoesNotRaceUnsettledJobsOrRescheduleSuccessfulRoot(string state)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        var jobs = new JobStore(database.DatabasePath);
        if (state == "root-running") { _ = StartTestJob(jobs, seed.Request.RootJobId); }
        else if (state != "root-queued")
        { _ = FinishTestJob(database, seed.Request.RootJobId, state == "root-succeeded" ? JobState.Succeeded : JobState.Failed); }
        if (state == "ingest-running") { _ = StartTestJob(jobs, seed.Request.IngestJobId); }
        if (state == "ingest-requeued")
        {
            StoredJobAggregate failed = FinishTestJob(database, seed.Request.IngestJobId, JobState.Failed);
            _ = jobs.QueueRetry(failed.Job.Id, failed.Revision, failed.Job.UpdatedAt.AddSeconds(1));
        }
        StoredSourceOnlyExecution terminal = seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        string rootBefore = JsonSerializer.Serialize(jobs.GetJobAggregate(seed.Request.RootJobId));
        string ingestBefore = JsonSerializer.Serialize(jobs.GetJobAggregate(seed.Request.IngestJobId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1,
            seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1)));
        Assert.AreEqual(terminal.StateDigest, seed.Store.Get(seed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(rootBefore, JsonSerializer.Serialize(jobs.GetJobAggregate(seed.Request.RootJobId)));
        Assert.AreEqual(ingestBefore, JsonSerializer.Serialize(jobs.GetJobAggregate(seed.Request.IngestJobId)));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    public void RetryRejectsMissingIdentityInvalidActorNonUtcTimeAndBackdatedAcceptance()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        StoredJobAggregate failedRoot = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        DateTimeOffset at = seed.Request.CreatedAt.AddMinutes(1);
        Assert.ThrowsExactly<ArgumentNullException>(() => seed.Store.CreateRetry(null!, 1, seed.Request.ActorSid, at));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, -1, seed.Request.ActorSid, at));
        Assert.ThrowsExactly<ArgumentNullException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1, null!, at));
        foreach (string actor in new[] { "", "not-a-sid", "S-1-5-021-1000", "S-1-5-", "S-1-5-21-1000\n" })
        { Assert.ThrowsExactly<ArgumentException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1, actor, at)); }
        Assert.ThrowsExactly<ArgumentException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, at.ToOffset(TimeSpan.FromHours(1))));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(EntityId<SourceOnlyExecutionIdTag>.New(), 1, seed.Request.ActorSid, at));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, seed.Request.CreatedAt.AddTicks(-1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, failedRoot.Job.UpdatedAt.AddTicks(-1)));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConcurrentRetryRequestsHaveOneSuccessorAndExactReplaysShareIt(bool identical)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        _ = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        using var release = new ManualResetEventSlim(false);
        Task<StoredSourceOnlyExecution?> Accept(string actor) => Task.Run(() =>
        {
            release.Wait();
            try
            {
                return new SourceOnlyExecutionStore(database.DatabasePath).CreateRetry(seed.Request.ExecutionId, 1,
                    actor, seed.Request.CreatedAt.AddMinutes(1));
            }
            catch (InvalidOperationException) { return null; }
        });
        Task<StoredSourceOnlyExecution?> first = Accept(seed.Request.ActorSid);
        Task<StoredSourceOnlyExecution?> second = Accept(identical ? seed.Request.ActorSid : "S-1-5-21-2000");
        release.Set();
        StoredSourceOnlyExecution?[] results = await Task.WhenAll(first, second);
        Assert.AreEqual(identical ? 2 : 1, results.Count(result => result is not null));
        Assert.AreEqual(1, results.Where(result => result is not null).Select(result => result!.Request.ExecutionId).Distinct().Count());
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
        Assert.AreEqual(3L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(1, seed.Store.Get(seed.Request.ExecutionId)!.Revision, "Fallback acceptance never rewrites the terminal source revision.");
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("ingest")]
    [DataRow("execution")]
    [DataRow("post-insert-validation")]
    public void RetryAcceptanceFailureRollsBackAllNewRowsAndPreservesOriginalHistory(string stage)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        StoredJobAggregate oldRoot = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        StoredJobAggregate oldIngest = FinishTestJob(database, seed.Request.IngestJobId, JobState.Cancelled);
        StoredSourceOnlyExecution failed = seed.Store.UpdateState(seed.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        string table = stage is "root" or "ingest" ? "jobs" : "source_only_executions";
        string when = stage == "root" ? "NEW.kind = 'source-only.pipeline.v1'" : stage == "ingest"
            ? "NEW.kind = 'media.ingest.v1'" : "NEW.retry_of_execution_id IS NOT NULL";
        string action = stage == "post-insert-validation"
            ? $"UPDATE source_only_executions SET revision = 1, state_digest = '{new string('f', 64)}' WHERE id = NEW.id;"
            : "SELECT RAISE(ABORT, 'injected retry failure');";
        database.ExecuteNonQuery($"CREATE TRIGGER fail_retry AFTER INSERT ON {table} WHEN {when} BEGIN {action} END;");
        void Retry() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1));
        if (stage == "post-insert-validation") { Assert.ThrowsExactly<InvalidOperationException>(() => Retry()); }
        else { Assert.ThrowsExactly<SqliteException>(() => Retry()); }
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
        Assert.AreEqual(failed.StateDigest, seed.Store.Get(seed.Request.ExecutionId)!.StateDigest);
        var jobs = new JobStore(database.DatabasePath);
        Assert.AreEqual(JsonSerializer.Serialize(oldRoot), JsonSerializer.Serialize(jobs.GetJobAggregate(oldRoot.Job.Id)));
        Assert.AreEqual(JsonSerializer.Serialize(oldIngest), JsonSerializer.Serialize(jobs.GetJobAggregate(oldIngest.Job.Id)));
        database.ExecuteNonQuery("DROP TRIGGER fail_retry;");
        Retry();
        Assert.AreEqual(4L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void RetryOriginCannotBeReplacedForkedOrClearedEvenWithRecursiveTriggersOff()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        _ = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        StoredSourceOnlyExecution retry = seed.Store.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1));
        Assert.AreEqual(0L, database.ExecuteScalarInt64("PRAGMA recursive_triggers;"));
        EntityId<JobIdTag> replacementRoot = EntityId<JobIdTag>.New();
        new JobStore(database.DatabasePath).CreateJob(new Job(replacementRoot, seed.Project.Id, SourceOnlyExecutionStore.RootJobKind,
            JobState.Queued, retry.Request.CreatedAt, retry.Request.CreatedAt, 0));
        SqliteException failure = Assert.ThrowsExactly<SqliteException>(() => database.ExecuteNonQuery("""
            INSERT OR REPLACE INTO source_only_executions
            SELECT $id, project_id, draft_id, source_candidate_id, $root, ingest_job_id,
                parent_version_id, parent_digest, created_at, request_payload, request_digest,
                protected_source_locator, revision, phase, result_payload, result_digest, version_id,
                version_digest, failure_code, state_digest, retry_of_execution_id
            FROM source_only_executions WHERE id = $original;
            """, ("$id", EntityId<SourceOnlyExecutionIdTag>.New().ToString()), ("$root", replacementRoot.ToString()),
            ("$original", retry.Request.ExecutionId.ToString())));
        Assert.AreEqual(1811, failure.SqliteExtendedErrorCode);
        Assert.ThrowsExactly<SqliteException>(() => database.ExecuteNonQuery(
            "UPDATE source_only_executions SET retry_of_execution_id = NULL, revision = revision + 1 WHERE id = $id;",
            ("$id", retry.Request.ExecutionId.ToString())));
        Assert.AreEqual(retry.StateDigest, seed.Store.Get(retry.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow("retryOfRevision")]
    [DataRow("retryOfStateDigest")]
    public void RetryOriginIsRevalidatedEvenWithConsistentRequestAndStateHashes(string field)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        _ = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        StoredSourceOnlyExecution retry = seed.Store.CreateRetry(seed.Request.ExecutionId, 1, seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1));
        JsonNode request = JsonNode.Parse(retry.RequestJson)!;
        if (field == "retryOfRevision") { request[field] = 2; }
        else { request[field] = new string('f', 64); }
        byte[] payload = Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(request.ToJsonString()));
        ContentDigest requestDigest = ContentDigest.Compute(payload);
        ContentDigest stateDigest = TestStateDigest(requestDigest, 0, SourceOnlyExecutionPhase.Queued);
        WithoutExecutionUpdateGuard(database, () => database.ExecuteNonQuery("""
            UPDATE source_only_executions SET request_payload = $payload, request_digest = $request,
                state_digest = $state WHERE id = $id;
            """, ("$payload", payload), ("$request", requestDigest.Hex), ("$state", stateDigest.Hex),
            ("$id", retry.Request.ExecutionId.ToString())));
        DatabaseMigrator.ValidateCurrentDatabase(database.DatabasePath);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(retry.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1,
            seed.Request.ActorSid, retry.Request.CreatedAt));
    }

    [TestMethod]
    public void InitialFailureBeforeDispatchCanRetryAfterExplicitQueuedRootCancellation()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        var jobs = new JobStore(database.DatabasePath);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1,
            seed.Request.ActorSid, seed.Request.CreatedAt.AddSeconds(1)));
        // Explicit native precondition, deliberately not claimed atomic with CreateRetry.
        _ = jobs.CancelQueued(seed.Request.RootJobId, 0, seed.Request.CreatedAt);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRetry(seed.Request.ExecutionId, 1,
            seed.Request.ActorSid, seed.Request.CreatedAt));
        StoredSourceOnlyExecution retry = seed.Store.CreateRetry(seed.Request.ExecutionId, 1,
            seed.Request.ActorSid, seed.Request.CreatedAt.AddTicks(1));
        Assert.AreEqual(JobState.Cancelled, jobs.GetJobAggregate(seed.Request.RootJobId)!.Job.State);
        Assert.AreEqual(JobState.Queued, jobs.GetJobAggregate(retry.Request.RootJobId)!.Job.State);
        Assert.AreEqual(seed.Request.IngestJobId, retry.Request.IngestJobId);
        Assert.AreEqual(retry.Request.ExecutionId, seed.Store.GetLatestForProject(seed.Project.Id)!.Request.ExecutionId);
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM composition_versions;"));
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void RegenerationDoesNotTrustAReusedIngestWithoutValidRetryAcceptance()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        _ = FinishTestJob(database, seed.Request.RootJobId, JobState.Failed);
        _ = FinishTestJob(database, seed.Request.IngestJobId, JobState.Failed);
        _ = seed.Store.UpdateState(seed.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        StoredSourceOnlyExecution retry = seed.Store.CreateRetry(seed.Request.ExecutionId, 1,
            seed.Request.ActorSid, seed.Request.CreatedAt.AddMinutes(1));
        _ = FinishTestJob(database, retry.Request.IngestJobId, JobState.Succeeded);
        CompositionVersion parent = seed.AppendStandardVersion(retry.Request.CreatedAt.AddSeconds(3));
        JsonNode corrupted = JsonNode.Parse(retry.RequestJson)!;
        corrupted["retryOfStateDigest"] = new string('f', 64);
        byte[] payload = Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(corrupted.ToJsonString()));
        ContentDigest requestDigest = ContentDigest.Compute(payload);
        ContentDigest stateDigest = TestStateDigest(requestDigest, 0, SourceOnlyExecutionPhase.Queued);
        WithoutExecutionUpdateGuard(database, () => database.ExecuteNonQuery("""
            UPDATE source_only_executions SET request_payload = $payload, request_digest = $request,
                state_digest = $state WHERE id = $id;
            """, ("$payload", payload), ("$request", requestDigest.Hex), ("$state", stateDigest.Hex),
            ("$id", retry.Request.ExecutionId.ToString())));
        SourceOnlyExecutionRequest regeneration = seed.Regeneration(parent) with
        {
            IngestJobId = retry.Request.IngestJobId,
            CreatedAt = retry.Request.CreatedAt.AddMinutes(1),
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(regeneration, 0));
        Assert.AreEqual(4L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
    }

    [TestMethod]
    public void RetryChainBoundIsInclusiveAndOverflowDoesNotLeaveAnOrphanRoot()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        StoredSourceOnlyExecution current = seed.Finalize();
        for (int depth = 1; depth < SourceOnlyExecutionStore.MaximumRetryLineageLength; depth++)
        {
            _ = FinishTestJob(database, current.Request.RootJobId, JobState.Cancelled, beforeAttempt: true);
            current = seed.Store.UpdateState(current.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Canceled);
            current = seed.Store.CreateRetry(current.Request.ExecutionId, current.Revision,
                seed.Request.ActorSid, current.Request.CreatedAt.AddMinutes(1));
        }
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
        _ = FinishTestJob(database, current.Request.RootJobId, JobState.Cancelled, beforeAttempt: true);
        current = seed.Store.UpdateState(current.Request.ExecutionId, 0, SourceOnlyExecutionPhase.Canceled);
        long jobsBefore = database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;");
        // SQL reserves a slot for the new execution; the store also bounds all reads.
        Assert.ThrowsExactly<SqliteException>(() => seed.Store.CreateRetry(current.Request.ExecutionId, current.Revision,
            seed.Request.ActorSid, current.Request.CreatedAt.AddMinutes(1)));
        Assert.AreEqual(jobsBefore, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual((long)SourceOnlyExecutionStore.MaximumRetryLineageLength,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
        Assert.AreEqual(current.StateDigest, seed.Store.Get(current.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    public void SubtitleCaptureNullAndEmptyKeepLegacyInitialRequestBytesAndReplay()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        StoredSourceOnlyExecution accepted = seed.Finalize();
        using JsonDocument snapshot = JsonDocument.Parse(seed.Snapshot.CanonicalJson);
        string legacy = CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "source-only.execution.v1",
            executionId = seed.Request.ExecutionId.ToString(),
            projectId = seed.Project.Id.ToString(),
            draftId = seed.Draft.Id.ToString(),
            sourceCandidateId = seed.Request.SourceCandidateId.ToString(),
            rootJobId = seed.Request.RootJobId.ToString(),
            ingestJobId = seed.Request.IngestJobId.ToString(),
            settingsSnapshot = snapshot.RootElement,
            settingsSnapshotDigest = seed.Snapshot.ContentDigest.Hex,
            actorSid = seed.Request.ActorSid,
            createdAt = seed.Request.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            parentVersionId = (string?)null,
            parentDigest = (string?)null,
            settingsRevision = 0,
            finalizationDigest = accepted.FinalizationDigest!.Hex,
            protectedLocatorDigest = (string?)null,
        }));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(legacy), Encoding.UTF8.GetBytes(accepted.RequestJson));
        StoredSourceOnlyExecution empty = seed.Finalize(request: seed.Request with { SubtitleEdits = [] });
        Assert.AreEqual(accepted.RequestJson, empty.RequestJson);
        Assert.AreEqual(ContentDigest.Compute(Encoding.UTF8.GetBytes(legacy)), empty.RequestDigest);
        Assert.IsNull(empty.Request.SubtitleEdits);
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        Assert.AreEqual(0, seed.Store.CaptureSubtitleEditHeads(seed.Project.Id, parent.Id).Count);
        SourceOnlyExecutionRequest regeneration = seed.Regeneration(parent);
        StoredSourceOnlyExecution next = seed.Store.CreateRegeneration(regeneration, 0);
        Assert.IsFalse(next.RequestJson.Contains("subtitleEdits", StringComparison.Ordinal));
        Assert.AreEqual(next.RequestJson, seed.Store.CreateRegeneration(regeneration with { SubtitleEdits = [] }, 0).RequestJson);
    }

    [TestMethod]
    public void InitialRequestCannotCaptureSubtitleEdits()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        Assert.ThrowsExactly<ArgumentException>(() => seed.Finalize(request: seed.Request with
        { SubtitleEdits = [new(EntityId<CompositionVersionIdTag>.New(), 1, seed.Digest)] }));
        AssertEmptyAcceptance(database, seed);
    }

    [TestMethod]
    public void RegenerationCapturesAllFourRootsCanonicalizesAndKeepsHistoryAfterLaterEdits()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        CompositionVersion[] roots = [parent, seed.AppendVersion(parent.Id), seed.AppendVersion(parent.Id), seed.AppendVersion(parent.Id)];
        StoredSubtitleEdit[] edits = roots.Select(root => CommitSubtitleEdit(database, seed, root, seed.Request.CreatedAt.AddSeconds(10))).ToArray();
        IReadOnlyList<SubtitleEditHeadReference> captured = seed.Store.CaptureSubtitleEditHeads(seed.Project.Id, parent.Id);
        Assert.AreEqual(4, captured.Count);
        CollectionAssert.AreEqual(roots.Select(root => root.Id.ToString()).Order(StringComparer.Ordinal).ToArray(),
            captured.Select(head => head.RootVersionId.ToString()).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SubtitleEditHeadReference>)captured).Clear());
        SubtitleEditHeadReference[] mutable = captured.Reverse().ToArray();
        SourceOnlyExecutionRequest request = seed.Regeneration(parent) with { SubtitleEdits = mutable };
        StoredSourceOnlyExecution accepted = seed.Store.CreateRegeneration(request, 0);
        mutable[0] = mutable[0] with { Sequence = 128 };
        CollectionAssert.AreEqual(captured.ToArray(), accepted.Request.SubtitleEdits!.ToArray());
        using JsonDocument json = JsonDocument.Parse(accepted.RequestJson);
        CollectionAssert.AreEqual(captured.Select(head => head.RootVersionId.ToString()).ToArray(),
            json.RootElement.GetProperty("subtitleEdits").EnumerateArray().Select(head => head.GetProperty("rootVersionId").GetString()!).ToArray());
        _ = CommitSubtitleEdit(database, seed, parent, request.CreatedAt.AddSeconds(1), edits[0]);
        var restarted = new SourceOnlyExecutionStore(database.DatabasePath);
        Assert.AreEqual(accepted.RequestDigest, restarted.Get(request.ExecutionId)!.RequestDigest);
        Assert.AreEqual(accepted.RequestDigest, restarted.CreateRegeneration(request with { SubtitleEdits = captured }, 0).RequestDigest);
        CompositionVersion produced = seed.AppendVersion(parent.Id, request.CreatedAt);
        _ = restarted.UpdateState(request.ExecutionId, 0, SourceOnlyExecutionPhase.Composing,
            versionId: produced.Id, digest: produced.ContentDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow("omitted")]
    [DataRow("empty")]
    [DataRow("stale-head")]
    [DataRow("new-branch")]
    [DataRow("subset")]
    [DataRow("unrelated")]
    [DataRow("future")]
    public void RegenerationRejectsMissingStaleArbitraryAndFutureCapturesAtomically(string mutation)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        SourceOnlyExecutionRequest request = seed.Regeneration(parent);
        StoredSubtitleEdit first = CommitSubtitleEdit(database, seed, parent,
            mutation == "future" ? request.CreatedAt.AddTicks(1) : seed.Request.CreatedAt.AddSeconds(10));
        IReadOnlyList<SubtitleEditHeadReference> heads = seed.Store.CaptureSubtitleEditHeads(seed.Project.Id, parent.Id);
        request = request with { SubtitleEdits = heads };
        switch (mutation)
        {
            case "omitted": request = request with { SubtitleEdits = null }; break;
            case "empty": request = request with { SubtitleEdits = [] }; break;
            case "stale-head":
                _ = CommitSubtitleEdit(database, seed, parent, seed.Request.CreatedAt.AddSeconds(20), first);
                break;
            case "new-branch":
            case "subset":
                _ = CommitSubtitleEdit(database, seed, seed.AppendVersion(parent.Id), seed.Request.CreatedAt.AddSeconds(20));
                break;
            case "unrelated":
                CompositionVersion unrelated = seed.AppendVersion();
                _ = CommitSubtitleEdit(database, seed, unrelated, seed.Request.CreatedAt.AddSeconds(20));
                request = request with { SubtitleEdits = seed.Store.CaptureSubtitleEditHeads(seed.Project.Id, unrelated.Id) };
                break;
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(request, 0));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
    }

    [TestMethod]
    public void SubtitleCaptureRefusesFifthRootAndForeignProject()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        _ = CommitSubtitleEdit(database, seed, parent, seed.Request.CreatedAt.AddSeconds(10));
        for (int index = 0; index < 4; index++)
        { _ = CommitSubtitleEdit(database, seed, seed.AppendVersion(parent.Id), seed.Request.CreatedAt.AddSeconds(10)); }
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CaptureSubtitleEditHeads(seed.Project.Id, parent.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CaptureSubtitleEditHeads(EntityId<ProjectIdTag>.New(), parent.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.CreateRegeneration(seed.Regeneration(parent), 0));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM jobs;"));
    }

    [TestMethod]
    public void RetryKeepsCapturedSubtitlePrefixEvenWhenTheCurrentHeadChanges()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        StoredSubtitleEdit first = CommitSubtitleEdit(database, seed, parent, seed.Request.CreatedAt.AddSeconds(10));
        SourceOnlyExecutionRequest request = seed.Regeneration(parent) with
        { SubtitleEdits = seed.Store.CaptureSubtitleEditHeads(seed.Project.Id, parent.Id) };
        StoredSourceOnlyExecution accepted = seed.Store.CreateRegeneration(request, 0);
        _ = FinishTestJob(database, request.RootJobId, JobState.Failed);
        _ = seed.Store.UpdateState(request.ExecutionId, 0, SourceOnlyExecutionPhase.Failed, failureCode: "pipeline.failed");
        _ = CommitSubtitleEdit(database, seed, parent, request.CreatedAt.AddSeconds(10), first);
        StoredSourceOnlyExecution retry = seed.Store.CreateRetry(request.ExecutionId, 1, request.ActorSid, request.CreatedAt.AddMinutes(1));
        CollectionAssert.AreEqual(accepted.Request.SubtitleEdits!.ToArray(), retry.Request.SubtitleEdits!.ToArray());
        Assert.AreEqual(parent.Id, retry.Request.ParentVersionId);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("object")]
    [DataRow("missing-field")]
    [DataRow("extra-field")]
    [DataRow("zero")]
    [DataRow("overflow")]
    [DataRow("fraction")]
    [DataRow("id")]
    [DataRow("digest")]
    [DataRow("duplicate")]
    [DataRow("five")]
    public void SubtitleCaptureClosedParseRejectsMalformedPayloadWithRecomputedHashes(string mutation)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        StoredSourceOnlyExecution accepted = seed.Store.CreateRegeneration(seed.Regeneration(parent), 0);
        JsonObject head = new()
        { ["rootVersionId"] = parent.Id.ToString(), ["sequence"] = 1, ["recordDigest"] = seed.Digest.Hex };
        JsonNode? capture = new JsonArray(head);
        switch (mutation)
        {
            case "null": capture = null; break;
            case "empty": capture = new JsonArray(); break;
            case "object": capture = new JsonObject(); break;
            case "missing-field": head.Remove("recordDigest"); break;
            case "extra-field": head["extra"] = true; break;
            case "zero": head["sequence"] = 0; break;
            case "overflow": head["sequence"] = 129; break;
            case "fraction": head["sequence"] = JsonNode.Parse("1.000000000000000000000000000001"); break;
            case "id": head["rootVersionId"] = "FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF"; break;
            case "digest": head["recordDigest"] = new string('F', 64); break;
            case "duplicate": capture.AsArray().Add(head.DeepClone()); break;
            case "five":
                for (int index = 0; index < 4; index++) { capture.AsArray().Add(head.DeepClone()); }
                break;
        }
        JsonNode json = JsonNode.Parse(accepted.RequestJson)!;
        json["subtitleEdits"] = capture;
        RewriteSubtitleCapture(database, accepted, json);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(accepted.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CapturedPrefixMustStillExistAndMatchDigestOnHistoricalRead(bool missing)
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        CompositionVersion parent = seed.AppendVersion();
        _ = CommitSubtitleEdit(database, seed, parent, seed.Request.CreatedAt.AddSeconds(10));
        StoredSourceOnlyExecution accepted = seed.Store.CreateRegeneration(seed.Regeneration(parent) with
        { SubtitleEdits = seed.Store.CaptureSubtitleEditHeads(seed.Project.Id, parent.Id) }, 0);
        JsonNode json = JsonNode.Parse(accepted.RequestJson)!;
        if (missing) { json["subtitleEdits"]![0]!["sequence"] = 2; }
        else { json["subtitleEdits"]![0]!["recordDigest"] = new string('f', 64); }
        RewriteSubtitleCapture(database, accepted, json);
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.Get(accepted.Request.ExecutionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => seed.Store.ListForProject(seed.Project.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SourceOnlyExecutionStore.ValidateAll(database.DatabasePath));
    }

    private static void RewriteSubtitleCapture(StoreTestDatabase database, StoredSourceOnlyExecution accepted, JsonNode json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(json.ToJsonString()));
        ContentDigest digest = ContentDigest.Compute(payload);
        WithoutExecutionUpdateGuard(database, () => database.ExecuteNonQuery("""
            UPDATE source_only_executions SET request_payload = $payload, request_digest = $digest,
                state_digest = $state WHERE id = $id;
            """, ("$payload", payload), ("$digest", digest.Hex),
            ("$state", TestStateDigest(digest, 0, SourceOnlyExecutionPhase.Queued).Hex),
            ("$id", accepted.Request.ExecutionId.ToString())));
    }

    // Actual journal-store fixture; no App runtime, ASR, or rendering is simulated as executed.
    private static StoredSubtitleEdit CommitSubtitleEdit(StoreTestDatabase database, SourceOnlyExecutionTestSeed seed,
        CompositionVersion root, DateTimeOffset at, StoredSubtitleEdit? previous = null)
    {
        EntityId<CompositionVersionIdTag> parentId = previous?.ChildVersionId ?? root.Id;
        var child = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), seed.Project.Id, parentId,
            seed.Request.ActorSid, at, "Subtitle edit fixture", root.ContentDigest, root.Elements);
        string operation = CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            kind = "editSubtitle",
            cueId = Guid.NewGuid().ToString("D"),
            expectedRevisionId = Guid.NewGuid().ToString("D"),
            expectedRevisionDigest = seed.Digest.Hex,
            reason = "Test edit",
            edit = new { text = "Updated subtitle" },
        }));
        return new SubtitleEditStore(database.DatabasePath).Commit(new SubtitleEditCommit(
            Guid.NewGuid(), seed.Project.Id, root.Id, parentId, child, SourceOnlyExecutionTestSeed.VersionPayload(child),
            previous?.Sequence ?? 0, seed.Digest, previous?.ResultSnapshotDigest ?? seed.Digest,
            ContentDigest.Compute(Encoding.UTF8.GetBytes(child.Id.ToString())), operation, [child.Id.Value],
            seed.Request.ActorSid, at, ContentDigest.Compute(Encoding.UTF8.GetBytes(SourceOnlyExecutionTestSeed.VersionPayload(root)))));
    }

    private static JobAttempt StartTestJob(JobStore jobs, EntityId<JobIdTag> jobId)
    {
        StoredJobAggregate current = jobs.GetJobAggregate(jobId)!;
        DateTimeOffset startedAt = current.Job.UpdatedAt.AddSeconds(1);
        var attempt = new JobAttempt(EntityId<JobAttemptIdTag>.New(), jobId, current.Job.CurrentAttemptNumber + 1,
            JobState.Running, current.Job.UpdatedAt, startedAt, null, null);
        _ = jobs.StartAttempt(jobId, current.Revision, attempt, startedAt);
        return attempt;
    }

    private static StoredJobAggregate FinishTestJob(StoreTestDatabase database, EntityId<JobIdTag> jobId,
        JobState state, bool beforeAttempt = false)
    {
        var jobs = new JobStore(database.DatabasePath);
        if (beforeAttempt)
        {
            Assert.AreEqual(JobState.Cancelled, state);
            StoredJobAggregate current = jobs.GetJobAggregate(jobId)!;
            _ = jobs.CancelQueued(jobId, current.Revision, current.Job.UpdatedAt.AddSeconds(2));
        }
        else
        {
            JobAttempt running = StartTestJob(jobs, jobId);
            DateTimeOffset evidenceAt = running.StartedAt!.Value.AddTicks(1);
            jobs.AppendProgress(new JobProgress(EntityId<JobProgressIdTag>.New(), jobId, running.Id,
                running.SequenceNumber, evidenceAt, 0, 1, "pipeline", "Test progress"));
            jobs.AppendHeartbeat(new JobHeartbeat(EntityId<JobHeartbeatIdTag>.New(), jobId, running.Id,
                running.SequenceNumber, evidenceAt));
            DateTimeOffset completedAt = running.StartedAt.Value.AddSeconds(1);
            string? error = state is JobState.Failed or JobState.Interrupted ? "job.execution_timed_out" : null;
            _ = jobs.CompleteAttempt(jobId, jobs.GetJob(jobId)!.Revision,
                new JobAttempt(running.Id, jobId, running.SequenceNumber, state, running.QueuedAt,
                    running.StartedAt, completedAt, error), completedAt);
        }
        return jobs.GetJobAggregate(jobId)!;
    }

    private static int BindVersionSql(StoreTestDatabase database, StoredSourceOnlyExecution current, CompositionVersion version)
    {
        int revision = checked(current.Revision + 1);
        ContentDigest stateDigest = TestStateDigest(current.RequestDigest, revision, SourceOnlyExecutionPhase.Composing, version);
        return database.ExecuteNonQuery("""
            UPDATE source_only_executions SET revision = $revision, phase = 'Composing',
                version_id = $version, version_digest = $digest, state_digest = $state WHERE id = $id;
            """, ("$revision", revision), ("$version", version.Id.ToString()), ("$digest", version.ContentDigest.Hex),
            ("$state", stateDigest.Hex), ("$id", current.Request.ExecutionId.ToString()));
    }

    private static ContentDigest TestStateDigest(ContentDigest requestDigest, int revision,
        SourceOnlyExecutionPhase phase, CompositionVersion? version = null) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "source-only.state.v1",
            requestDigest = requestDigest.Hex,
            revision,
            phase = phase.ToString(),
            resultDigest = (string?)null,
            versionId = version?.Id.ToString(),
            versionDigest = version?.ContentDigest.Hex,
            failureCode = (string?)null,
        }))));

    private static void RewriteVersion(StoreTestDatabase database, CompositionVersion version,
        string? payload = null, bool bypassForeignKeys = false)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        if (bypassForeignKeys)
        {
            // Deliberately simulate damaged persisted ancestry, only on this test connection.
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            UPDATE composition_versions SET project_id = $project, parent_version_id = $parent,
                content_digest = $digest, created_at = $created, payload = $payload WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$project", version.ProjectId.ToString());
        command.Parameters.AddWithValue("$parent", (object?)version.ParentVersionId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$digest", version.ContentDigest.Hex);
        command.Parameters.AddWithValue("$created", version.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payload", Encoding.UTF8.GetBytes(payload ?? SourceOnlyExecutionTestSeed.VersionPayload(version)));
        command.Parameters.AddWithValue("$id", version.Id.ToString());
        Assert.AreEqual(1, command.ExecuteNonQuery());
    }

    private static void WithoutExecutionUpdateGuard(StoreTestDatabase database, Action mutation)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'trg_source_only_execution_update';";
        string trigger = (string)command.ExecuteScalar()!;
        command.CommandText = "DROP TRIGGER trg_source_only_execution_update;";
        command.ExecuteNonQuery();
        try { mutation(); }
        finally
        {
            command.CommandText = trigger;
            command.ExecuteNonQuery();
        }
    }

    private static void AssertEmptyAcceptance(StoreTestDatabase database, SourceOnlyExecutionTestSeed seed)
    {
        foreach (string table in new[] { "projects", "project_default_settings", "jobs", "source_only_executions" })
        { Assert.AreEqual(0L, database.ExecuteScalarInt64($"SELECT COUNT(*) FROM {table};"), table); }
        StoredProjectCreationDraft draft = seed.Drafts.GetDraft(seed.Draft.Id)!;
        Assert.AreEqual(0, draft.Revision);
        Assert.IsFalse(draft.IsFinalized);
        Assert.AreEqual("{\"draft\":true}", draft.CanonicalJson);
    }
}

// Shared with backup tests so acceptance fixtures use the real store/Job APIs.
internal sealed class SourceOnlyExecutionTestSeed
{
    internal static readonly DateTimeOffset InitialTime = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly string _databasePath;
    internal SourceOnlyExecutionTestSeed(string databasePath, string actorSid = "S-1-5-21-1000")
    {
        _databasePath = databasePath;
        Drafts = new(databasePath);
        Settings = new(databasePath);
        Store = new(databasePath);
        Profile = DefaultSettingsProfile.Create(EntityId<DefaultSettingsProfileIdTag>.New(),
            "source-only-execution-tests", new SettingsProfileVersion(1, 0, 0),
            [new DefaultSettingDefinition("editing.crossfadeEnabled", DefaultSettingGroup.ProjectAndOutput,
                false, "Crossfade", "Test setting", DefaultSettingUnit.None, DefaultSettingValue.FromBoolean(false),
                null, null, [], [new DefaultSettingEvidence(DefaultSettingEvidenceKind.ProductDecision, "productDecision")])]);
        _ = Settings.RegisterProfile(Encoding.UTF8.GetBytes(Profile.CanonicalJson));
        Snapshot = Profile.CreateSnapshot();
        Draft = new ProjectCreationDraft(EntityId<ProjectCreationDraftIdTag>.New(), EntityId<AssetIdTag>.New(),
            "Source only test", "Exercise durable acceptance", "Internal audience", PublicationScope.InternalOnly,
            [new ProjectOutputProfileReference(EntityId<RenderProfileIdTag>.New(), "long-form", Digest)],
            "ja-JP", ["Host"], new ProjectDeadlineSelection(null),
            new TemplateReference(EntityId<TemplateIdTag>.New(), "neutral", Digest));
        Project = new(EntityId<ProjectIdTag>.New(), Draft.ContentName!, Draft.Purpose!, Draft.Audience!,
            PublicationScope.InternalOnly, null, [], []);
        Request = new(EntityId<SourceOnlyExecutionIdTag>.New(), Project.Id, Draft.Id, Draft.SourceCandidateId!,
            EntityId<JobIdTag>.New(), EntityId<JobIdTag>.New(), Snapshot, actorSid, InitialTime.AddMinutes(1));
        _ = Drafts.CreateDraft(Draft, "{\"draft\":true}", InitialTime);
    }

    internal ProjectCreationDraftStore Drafts { get; }
    internal DefaultSettingsStore Settings { get; }
    internal SourceOnlyExecutionStore Store { get; }
    internal DefaultSettingsProfile Profile { get; }
    internal DefaultSettingsSnapshot Snapshot { get; }
    internal ProjectCreationDraft Draft { get; }
    internal Project Project { get; }
    internal SourceOnlyExecutionRequest Request { get; }
    internal ContentDigest Digest { get; } = new(new string('a', 64));

    internal StoredSourceOnlyExecution Finalize(byte[]? locator = null, SourceOnlyExecutionRequest? request = null,
        string payload = "{\"draft\":false}", int expectedRevision = 0) =>
        Drafts.FinalizeSourceOnlyWithExecution(Draft, Project, payload, expectedRevision, Request.CreatedAt, request ?? Request, locator);

    internal DefaultSettingsSnapshot AlternateSnapshot() => Profile.CreateSnapshot(
        [new DefaultSettingSelection("editing.crossfadeEnabled", DefaultSettingValue.FromBoolean(true))]);

    internal CompositionVersion AppendVersion(EntityId<CompositionVersionIdTag>? parent = null,
        DateTimeOffset? createdAt = null, IReadOnlyList<CompositionElement>? elements = null)
    {
        OriginRef origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
        CompositionElement[] storedElements = elements?.ToArray() ?? [new(EntityId<CompositionElementIdTag>.New(),
            new TimeRange(default, new RationalTime(10, 1)), origin,
            SuggestionReference.FromPersistedReference(EntityId<SuggestionIdTag>.New(), origin),
            new TimeRange(new RationalTime(-2, 1), new RationalTime(10, 1)))];
        ContentDigest contentDigest = ContentDigest.Compute(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(
            JsonSerializer.Serialize(new { elements = storedElements.Select(ElementPayload).ToArray() }))));
        var version = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), Project.Id, parent,
            Request.ActorSid, createdAt ?? Request.CreatedAt.AddSeconds(3), "Test composition", contentDigest, storedElements);
        new ProjectStore(_databasePath).AppendCompositionVersion(version, VersionPayload(version));
        return version;
    }

    // Shape-only persisted fixture, not a claim that App orchestration/evidence was run.
    // StandardCompositionOrchestrator emits a new child retaining its parent's elements digest.
    internal CompositionVersion AppendStandardVersion(DateTimeOffset? createdAt = null)
    {
        DateTimeOffset timestamp = createdAt ?? Request.CreatedAt.AddSeconds(3);
        CompositionVersion baseline = AppendVersion(createdAt: timestamp);
        CompositionVersion auto = AppendVersion(baseline.Id, timestamp);
        return AppendVersion(auto.Id, timestamp, auto.Elements);
    }

    internal static string VersionPayload(CompositionVersion version)
    {
        var payload = new Dictionary<string, object>
        {
            ["id"] = version.Id.ToString(),
            ["projectId"] = version.ProjectId.ToString(),
            ["contentDigest"] = version.ContentDigest.Hex,
            ["createdAt"] = version.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["createdByActorId"] = version.CreatedByActorId,
            ["changeReason"] = version.ChangeReason,
            ["elements"] = version.Elements.Select(ElementPayload).ToArray(),
        };
        if (version.ParentVersionId is not null) { payload["parentVersionId"] = version.ParentVersionId.ToString(); }
        return CanonicalJson.Canonicalize(JsonSerializer.Serialize(payload));
    }

    private static object ElementPayload(CompositionElement element) => new
    {
        id = element.Id.ToString(),
        outputRange = RangePayload(element.OutputRange),
        origin = new { kind = "sourceSpan", sourceSpanId = element.Origin.SourceSpanId!.ToString() },
        sourceRange = RangePayload(element.SourceRange!.Value),
        adoptedSuggestion = new
        {
            suggestionId = element.AdoptedSuggestion!.SuggestionId.ToString(),
            origin = new { kind = "sourceSpan", sourceSpanId = element.Origin.SourceSpanId!.ToString() },
        },
    };

    private static object RangePayload(TimeRange range) => new
    {
        start = new { value = range.Start.Value, rate = range.Start.Rate },
        duration = new { value = range.Duration.Value, rate = range.Duration.Rate },
    };

    internal void CompleteIngest()
    {
        var jobs = new JobStore(_databasePath);
        EntityId<JobAttemptIdTag> attemptId = EntityId<JobAttemptIdTag>.New();
        DateTimeOffset start = Request.CreatedAt.AddSeconds(1);
        DateTimeOffset end = Request.CreatedAt.AddSeconds(2);
        _ = jobs.StartAttempt(Request.IngestJobId, 0,
            new JobAttempt(attemptId, Request.IngestJobId, 1, JobState.Running, Request.CreatedAt, start, null, null), start);
        _ = jobs.CompleteAttempt(Request.IngestJobId, 1,
            new JobAttempt(attemptId, Request.IngestJobId, 1, JobState.Succeeded, Request.CreatedAt, start, end, null), end);
    }

    internal SourceOnlyExecutionRequest Regeneration(CompositionVersion parent) => new(
        EntityId<SourceOnlyExecutionIdTag>.New(), Project.Id, Draft.Id, Request.SourceCandidateId,
        EntityId<JobIdTag>.New(), Request.IngestJobId, Snapshot, Request.ActorSid,
        Request.CreatedAt.AddMinutes(1), parent.Id, parent.ContentDigest);

    internal string ResultJson(SourceOnlyExecutionRequest request, CompositionVersion version) =>
        JsonSerializer.Serialize(new
        {
            binding = new
            {
                projectId = request.ProjectId.ToString(),
                executionId = request.ExecutionId.ToString(),
                rootJobId = request.RootJobId.ToString(),
                settingsSnapshotDigest = request.SettingsSnapshot.ContentDigest.Hex,
                versionId = version.Id.ToString(),
                versionDigest = version.ContentDigest.Hex,
            },
            evidence = "private-evidence-marker",
        });

    internal static void Corrupt(string databasePath, string mutation)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'trg_source_only_execution_update';";
        string trigger = (string)command.ExecuteScalar()!;
        command.CommandText = "DROP TRIGGER trg_source_only_execution_update;";
        command.ExecuteNonQuery();
        command.CommandText = mutation switch
        {
            "request-noncanonical" => "UPDATE source_only_executions SET request_payload = CAST(' ' || CAST(request_payload AS TEXT) AS BLOB);",
            "request-hash" => "UPDATE source_only_executions SET request_digest = $digest;",
            "state-hash" => "UPDATE source_only_executions SET state_digest = $digest;",
            "result-hash" => "UPDATE source_only_executions SET result_digest = $digest;",
            "locator-hash" => "UPDATE source_only_executions SET protected_source_locator = X'ffffff';",
            "request-id" => "SELECT request_payload FROM source_only_executions;",
            _ => throw new AssertFailedException(),
        };
        command.Parameters.AddWithValue("$digest", new string('f', 64));
        if (mutation == "request-id")
        {
            JsonNode payload = JsonNode.Parse((byte[])command.ExecuteScalar()!)!;
            payload["executionId"] = "FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF";
            byte[] canonical = Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(payload.ToJsonString()));
            command.CommandText = "UPDATE source_only_executions SET request_payload = $payload, request_digest = $hash;";
            command.Parameters.AddWithValue("$payload", canonical);
            command.Parameters.AddWithValue("$hash", ContentDigest.Compute(canonical).Hex);
        }
        command.ExecuteNonQuery();
        command.CommandText = trigger;
        command.Parameters.Clear();
        command.ExecuteNonQuery();
    }
}