using System.Globalization;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Rendering;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using Microsoft.Data.Sqlite;
using static HubRadio.App.Projects.SourceOnlyResultCodec;

namespace HubRadio.App.Projects;

internal sealed record SourceOnlyDeliverySnapshot(int ProjectRevision, string ProjectCanonicalDigest,
    string RightsSnapshotDigest, int WorkflowRevision, string WorkflowCanonicalDigest, string WorkflowState,
    string PrimaryArtifactId, string PrimaryEvaluationId, string PrimaryEvaluationDigest);

internal sealed record SourceOnlyDeliveryIdentity(string Role, string? RenditionId, string? ThumbnailPlanId,
    SourceOnlyResultReferenceDto Artifact, SourceOnlyResultReferenceDto Source, string VersionId, string VersionDigest,
    string GraphDigest, string ManifestDigest, string EvaluationId, string EvaluationDigest,
    string ProfileId, string ProfileDigest, SourceOnlyResultEvidenceDto[] Evidence);

internal sealed record SourceOnlyDeliveredItem(SourceOnlyDeliveryIdentity Input, string IdentityDigest,
    SourceOnlyResultReferenceDto Delivery);

/// <summary>
/// Native receipt, deliberately separate from the per-output rendering evidence envelope.
/// BaseResultDigest hashes the EXACT pre-delivery result, including order, warnings and omissions.
/// SetDigest binds the sorted identities, each independent evaluation/proof, settings and rights.
/// It cannot hash the final result (which contains this receipt's hash) without a hash cycle.
/// Presence of this JSON alone is NOT a commit: all delivery rows and its Evidence row must exist.
/// </summary>
internal sealed record SourceOnlyDeliveryReceipt(string SchemaVersion, string Destination,
    SourceOnlyResultBindingDto Binding, string ExecutionRequestDigest, string BaseResultDigest,
    string ReceiptArtifactId, string CreatedAt, SourceOnlyDeliverySnapshot Snapshot,
    SourceOnlyResultReferenceDto? GenerationCheckpoint, SourceOnlyResultReferenceDto[] RightsArtifacts,
    SourceOnlyDeliveredItem[] Deliveries, string SetDigest)
{
    internal const string Schema = "source-only.local-internal-delivery.v1";
    internal const int MaximumBytes = 2 * 1024 * 1024;

    internal static EntityId<ArtifactIdTag> ReceiptId(SourceOnlyResultBinding binding) =>
        SourceOnlyPipeline.StableId<ArtifactIdTag>(binding.ExecutionId.ToString(), "local-delivery-receipt:" + binding.RootJobId);

    internal static EntityId<ArtifactIdTag> DeliveryId(SourceOnlyResultBinding binding, SourceOnlyResultArtifact artifact) =>
        SourceOnlyPipeline.StableId<ArtifactIdTag>(binding.ExecutionId.ToString(), "local-delivery:" + binding.RootJobId + ":" + artifact.ArtifactId);

    internal static string FileName(SourceOnlyResultBinding binding, SourceOnlyResultArtifact artifact) =>
        DeliveryId(binding, artifact) + (artifact.IsVideo ? ".mp4" : artifact.IsImage ? ".png" :
            artifact.Role == SourceOnlyResultRole.SubRip ? ".srt" : ".vtt");

    internal static string DeliveryRelativeRoot(SourceOnlyResultBinding binding) => $"source-only/{binding.ExecutionId}/{binding.RootJobId}";
    internal static string ReceiptRelativePath(SourceOnlyResultBinding binding) => $"source-only/{binding.ExecutionId}/{binding.RootJobId}.delivery-receipt.json";
    internal static string StagingName(SourceOnlyResultBinding binding) => $"source-only-delivery-{binding.ExecutionId}-{binding.RootJobId}";

    internal string CanonicalJson => Write(this, MaximumBytes);
    internal SourceOnlyResultArtifactReference Reference => new(Id<ArtifactIdTag>(ReceiptArtifactId),
        ContentDigest.Compute(Utf8.GetBytes(CanonicalJson)), Utf8.GetByteCount(CanonicalJson), ArtifactKind.Evidence);

    internal static SourceOnlyDeliveryReceipt Create(SourceOnlyResult result, StoredSourceOnlyExecution execution,
        SourceOnlyDeliverySnapshot snapshot, SourceOnlyResultArtifactReference? generationCheckpoint,
        IReadOnlyList<SourceOnlyResultArtifactReference> rightsArtifacts, DateTimeOffset createdAt)
    {
        Require(result.Artifacts.All(a => a.Delivery is null) && !result.HasBlockingFailures);
        var receipt = new SourceOnlyDeliveryReceipt(Schema, "local-internal", BindingDto(result.Binding), execution.RequestDigest.Hex,
            result.CanonicalDigest.Hex, ReceiptId(result.Binding).ToString(), createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            snapshot, generationCheckpoint is null ? null : ReferenceDto(generationCheckpoint),
            rightsArtifacts.OrderBy(a => a.ArtifactId.ToString(), StringComparer.Ordinal).Select(ReferenceDto).ToArray(),
            Items(result), string.Empty);
        return receipt with { SetDigest = receipt.ComputeSetDigest().Hex };
    }

    private ContentDigest ComputeSetDigest() => ContentDigest.Compute(Utf8.GetBytes(Write(new
    {
        SchemaVersion,
        Destination,
        Binding,
        ExecutionRequestDigest,
        BaseResultDigest,
        ReceiptArtifactId,
        CreatedAt,
        Snapshot,
        GenerationCheckpoint,
        RightsArtifacts,
        Deliveries,
    }, MaximumBytes)));

    internal static SourceOnlyDeliveredItem[] Items(SourceOnlyResult result) => result.Artifacts
        .OrderBy(a => a.ArtifactId.ToString(), StringComparer.Ordinal).Select(a =>
        {
            Require(a.Evaluation is not null && a.ManifestDigest is not null && a.ProfileId is not null && a.ProfileDigest is not null);
            var input = new SourceOnlyDeliveryIdentity(a.Role.ToString(), a.RenditionId?.ToString(), a.ThumbnailPlanId?.ToString(),
                ReferenceDto(a.Artifact), ReferenceDto(a.Source), a.VersionId.ToString(), a.VersionDigest.Hex,
                a.GraphDigest.Hex, a.ManifestDigest.Hex, a.Evaluation.EvaluationId.ToString(), a.Evaluation.CanonicalDigest.Hex,
                a.ProfileId.ToString(), a.ProfileDigest.Hex, a.Evidence.OrderBy(e => e.Kind.ToString(), StringComparer.Ordinal)
                    .Select(e => new SourceOnlyResultEvidenceDto(e.Kind.ToString(), ReferenceDto(e.Artifact))).ToArray());
            return new SourceOnlyDeliveredItem(input, ContentDigest.Compute(Utf8.GetBytes(Write(input))).Hex,
                ReferenceDto(new(DeliveryId(result.Binding, a), a.ArtifactDigest, a.ByteSize, ArtifactKind.Delivery)));
        }).ToArray();

    internal static async Task<SourceOnlyDeliveryReceipt> ReadAsync(ValidatedArtifactReadStream stream, CancellationToken ct)
    {
        Require(stream.Kind == ArtifactKind.Evidence && stream.ByteSize is > 0 and <= MaximumBytes);
        stream.Position = 0;
        byte[] bytes = new byte[checked((int)stream.ByteSize)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        Require(ContentDigest.Compute(bytes) == stream.ContentDigest);
        string json = Utf8.GetString(bytes);
        using JsonDocument document = ParseDocument(json, MaximumBytes);
        Require(document.RootElement.GetProperty("deliveries").GetArrayLength() is > 0 and <= SourceOnlyResult.MaximumArtifactCount &&
            document.RootElement.GetProperty("rightsArtifacts").GetArrayLength() <= 1);
        SourceOnlyDeliveryReceipt receipt = JsonSerializer.Deserialize<SourceOnlyDeliveryReceipt>(document.RootElement, Options) ?? throw Invalid();
        Require(receipt.SchemaVersion == Schema && receipt.Destination == "local-internal" && receipt.CanonicalJson == json &&
            receipt.ReceiptArtifactId == stream.Id.ToString() && receipt.ComputeSetDigest() == Digest(receipt.SetDigest));
        _ = ParseUtc(receipt.CreatedAt);
        return receipt;
    }

    internal static (SourceOnlyDeliverySnapshot Snapshot, QuickStartWorkflow Workflow, Project Project) CaptureAuthority(
        SourceOnlyResult result, StoredSourceOnlyExecution execution, ProjectStore projects, QuickStartWorkflowStore workflows)
    {
        Require(!result.HasBlockingFailures && result.PrimaryWorkflow is not null);
        StandardCompositionPreparation preparation = new VersionService(projects).GetStandardComposition(result.VersionId) ?? throw Invalid();
        Project project = preparation.Plan.LongForm.Project;
        StoredProject stored = projects.GetProject(result.Binding.ProjectId) ?? throw Invalid();
        QuickStartProjectSnapshot current = workflows.GetCurrentProjectSnapshot(result.Binding.ProjectId);
        Require(project.Id == result.Binding.ProjectId && project.PublicationScope == PublicationScope.InternalOnly &&
            stored.PublicationScope == PublicationScope.InternalOnly && stored.Revision == current.Revision &&
            ContentDigest.Compute(Utf8.GetBytes(stored.CanonicalJson)) == current.CanonicalPayloadDigest &&
            HubRadio.Core.Projects.ProjectSnapshotJson.WriteProject(project) == stored.CanonicalJson &&
            preparation.Plan.ProjectStoreState.Revision == current.Revision &&
            preparation.Plan.ProjectStoreState.PayloadDigest == current.CanonicalPayloadDigest &&
            project.Assets.Count == 1 && project.Assets[0].Id == execution.Request.SourceCandidateId);
        Asset asset = project.Assets[0];
        Require(asset.Rights.Status == RightsStatus.Approved && !string.IsNullOrWhiteSpace(asset.Rights.EvidenceReference) &&
            !asset.Rights.EvidenceReference.Any(char.IsControl) && !string.IsNullOrWhiteSpace(asset.Rights.AllowedUse) &&
            !asset.Rights.AllowedUse.Any(char.IsControl) && result.Artifacts.Select(a => a.Source).Distinct().Count() == 1 &&
            result.Artifacts.All(a => a.Source.ContentDigest == asset.ContentDigest && a.Source.ByteSize == asset.ByteSize));

        QuickStartWorkflow workflow = workflows.Get(new(result.Binding.ProjectId, result.VersionId, result.VersionDigest)) ?? throw Invalid();
        SourceOnlyResultArtifact primary = result.Artifacts.Single(a => a.Role == SourceOnlyResultRole.LandscapeClean);
        Require(workflow.State is QuickStartWorkflowState.Ready or QuickStartWorkflowState.ReadyWithWarnings &&
            workflow.ProjectSnapshot is not null && workflow.ProjectSnapshot.Revision == current.Revision &&
            workflow.ProjectSnapshot.CanonicalPayloadDigest == current.CanonicalPayloadDigest &&
            workflow.SettingsSnapshotDigest == result.Binding.SettingsSnapshotDigest &&
            workflow.ArtifactId?.ToString() == primary.ArtifactId.ToString() && workflow.ArtifactDigest == primary.ArtifactDigest &&
            workflow.EvaluationId == primary.Evaluation!.EvaluationId && workflow.EvaluationCanonicalDigest == primary.Evaluation.CanonicalDigest &&
            workflow.EvaluationAggregateStatus == primary.Evaluation.AggregateStatus &&
            workflow.State == QuickStartEvaluationPolicy.DetermineCompletedState(primary.Evaluation));
        // These facts guard the project and the PRIMARY only. Other outputs are never
        // passed to the single-artifact export policy and keep their own QC/evaluation.
        string rightsDigest = ContentDigest.Compute(Utf8.GetBytes(Write(new
        {
            assetId = asset.Id.ToString(),
            sourceDigest = asset.ContentDigest.Hex,
            asset.ByteSize,
            status = asset.Rights.Status.ToString(),
            asset.Rights.EvidenceReference,
            asset.Rights.AllowedUse,
            asset.Rights.ExternalPublicationAllowed,
        }))).Hex;
        return (new(current.Revision, current.CanonicalPayloadDigest.Hex, rightsDigest, workflow.Revision,
            workflow.CanonicalDigest.Hex, workflow.State.ToString(), primary.ArtifactId.ToString(),
            primary.Evaluation.EvaluationId.ToString(), primary.Evaluation.CanonicalDigest.Hex), workflow, project);
    }

    internal void ValidateBinding(SourceOnlyResult result, StoredSourceOnlyExecution execution,
        SourceOnlyDeliverySnapshot current, QuickStartWorkflow workflow,
        SourceOnlyResultArtifactReference? checkpoint, IReadOnlyList<SourceOnlyResultArtifactReference> rights)
    {
        SourceOnlyResult basis = WithoutDelivery(result);
        Require(SourceOnlyResultCodec.Binding(Binding) == basis.Binding && ExecutionRequestDigest == execution.RequestDigest.Hex &&
            ReceiptArtifactId == ReceiptId(basis.Binding).ToString() && BaseResultDigest == basis.CanonicalDigest.Hex &&
            SetDigest == ComputeSetDigest().Hex && Write(Deliveries) == Write(Items(basis)) &&
            GenerationCheckpoint == (checkpoint is null ? null : ReferenceDto(checkpoint)) &&
            Write(RightsArtifacts) == Write(rights.OrderBy(r => r.ArtifactId.ToString(), StringComparer.Ordinal).Select(ReferenceDto).ToArray()) &&
            Snapshot.WorkflowRevision > 0 && Snapshot.WorkflowRevision <= workflow.Revision);
        QuickStartWorkflow prefix = QuickStartWorkflow.Reduce(workflow.Events.Take(Snapshot.WorkflowRevision).ToArray());
        Require(Snapshot == (current with
        {
            WorkflowRevision = prefix.Revision,
            WorkflowCanonicalDigest = prefix.CanonicalDigest.Hex,
            WorkflowState = prefix.State.ToString()
        }) &&
            prefix.EvaluationId?.ToString() == Snapshot.PrimaryEvaluationId &&
            prefix.EvaluationCanonicalDigest?.Hex == Snapshot.PrimaryEvaluationDigest &&
            prefix.ArtifactId?.ToString() == Snapshot.PrimaryArtifactId);
        DateTimeOffset at = ParseUtc(CreatedAt);
        Require(at >= execution.Request.CreatedAt && at >= prefix.Events[^1].OccurredAt &&
            basis.Artifacts.All(a => at >= a.Evaluation!.CompletedAt && at >= a.Evaluation.EvaluatedAt));
    }

    /// <summary>Exact native start attestation, if referenced; no URL or filesystem path from rights is followed.</summary>
    internal static async Task<IReadOnlyList<SourceOnlyResultArtifactReference>> ReadRightsAsync(
        ArtifactStore artifacts, SourceOnlyExecutionStore executions, StoredSourceOnlyExecution execution,
        Project project, SourceOnlyDeliveryHandles held, CancellationToken ct)
    {
        Asset asset = project.Assets.Single();
        string reference = asset.Rights.EvidenceReference!;
        if (!reference.StartsWith("artifact:", StringComparison.OrdinalIgnoreCase)) return [];
        Require(reference.StartsWith("artifact://", StringComparison.Ordinal));
        EntityId<ArtifactIdTag> id = Id<ArtifactIdTag>(reference["artifact://".Length..]);
        Require(id == SourceOnlyPipeline.StableId<ArtifactIdTag>(execution.Request.DraftId.ToString(), "rights-attestation"));
        StoredArtifact stored = artifacts.GetArtifact(id) ?? throw Invalid();
        Require(stored.Kind == ArtifactKind.Analysis && stored.ProjectId == project.Id &&
            stored.StorageReference == $"projects/{project.Id}/analysis/source-only/{id}.rights-attestation.json" &&
            stored.ByteSize is > 0 and <= 32768);
        SourceOnlyResultArtifactReference proof = SourceOnlyResultEvidenceStore.Reference(stored);
        ValidatedArtifactReadStream stream = await held.OpenAsync(artifacts, proof, project.Id, ct).ConfigureAwait(false);
        stream.Position = 0;
        byte[] bytes = new byte[checked((int)stream.ByteSize)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        using JsonDocument json = ParseDocument(Utf8.GetString(bytes), 32768);
        SourceOnlyDeliveryAttestation attestation = JsonSerializer.Deserialize<SourceOnlyDeliveryAttestation>(json.RootElement, Options) ?? throw Invalid();
        StoredSourceOnlyExecution original = executions.GetByDraft(execution.Request.DraftId) ?? throw Invalid();
        Require(Write(attestation) == Utf8.GetString(bytes) && ContentDigest.Compute(bytes) == proof.ContentDigest &&
            attestation.Schema == SourceOnlySourceLocator.RightsStatementVersion && attestation.SourceCandidateId == asset.Id.ToString() &&
            attestation.OriginalArtifactId == asset.Id.ToString() && attestation.OriginalFileName == asset.FileName &&
            attestation.SourceDigest == asset.ContentDigest.Hex && attestation.SourceByteSize == asset.ByteSize &&
            attestation.InternalEditingPermissionAttested && !attestation.ExternalPublicationAllowed && !asset.Rights.ExternalPublicationAllowed &&
            original.Request.ProjectId == project.Id && original.Request.SourceCandidateId == asset.Id &&
            attestation.ActorSid == original.Request.ActorSid && ParseUtc(attestation.AttestedAt) == original.Request.CreatedAt &&
            ParseUtc(attestation.SelectedAt) <= original.Request.CreatedAt && original.Request.CreatedAt <= execution.Request.CreatedAt);
        _ = QuickStartActor.User(attestation.SelectedBy);
        return [proof];
    }

    internal static async Task ValidateAsync(ArtifactStore artifacts, ProjectStore projects, QuickStartWorkflowStore workflows,
        StoredSourceOnlyExecution execution, SourceOnlyResult result, CancellationToken ct)
    {
        Require(result.Artifacts.Count > 0 && result.Artifacts.All(a => a.Delivery is not null) && !result.HasBlockingFailures);
        SourceOnlyResult basis = WithoutDelivery(result);
        var authority = CaptureAuthority(basis, execution, projects, workflows);
        await using var held = new SourceOnlyDeliveryHandles();
        SourceOnlyResultEvidenceReference reference = result.Artifacts[0].Delivery!.Receipt;
        Require(reference.Kind == SourceOnlyResultEvidenceKind.DeliveryReceipt &&
            reference.Artifact.ArtifactId == ReceiptId(result.Binding) && result.Artifacts.All(a => a.Delivery!.Receipt == reference));
        ValidatedArtifactReadStream receiptStream = await held.OpenAsync(artifacts, reference.Artifact, result.Binding.ProjectId, ct).ConfigureAwait(false);
        Require(receiptStream.RegisteredArtifact.StorageReference == $"projects/{result.Binding.ProjectId}/evidence/{ReceiptRelativePath(result.Binding)}");
        SourceOnlyDeliveryReceipt receipt = await ReadAsync(receiptStream, ct).ConfigureAwait(false);
        SourceOnlyResultGenerationCheckpoint? checkpoint = await SourceOnlyResultGenerationCheckpoint.ReadAsync(artifacts, execution, ct).ConfigureAwait(false);
        checkpoint?.ValidateSet(basis);
        var executions = new SourceOnlyExecutionStore(artifacts.LocalDatabasePath);
        IReadOnlyList<SourceOnlyResultArtifactReference> rights = await ReadRightsAsync(artifacts, executions, execution,
            authority.Project, held, ct).ConfigureAwait(false);
        receipt.ValidateBinding(result, execution, authority.Snapshot, authority.Workflow, checkpoint?.Artifact, rights);
        var expectedPaths = new HashSet<string>(WorkspacePathSafety.PathComparer);
        foreach (SourceOnlyResultArtifact artifact in result.Artifacts)
        {
            SourceOnlyResultArtifactReference delivery = artifact.Delivery!.Artifact;
            Require(delivery == new SourceOnlyResultArtifactReference(DeliveryId(result.Binding, artifact), artifact.ArtifactDigest,
                artifact.ByteSize, ArtifactKind.Delivery));
            ValidatedArtifactReadStream actual = await held.OpenAsync(artifacts, delivery, result.Binding.ProjectId, ct).ConfigureAwait(false);
            Require(actual.RegisteredArtifact.StorageReference == $"projects/{result.Binding.ProjectId}/deliveries/{DeliveryRelativeRoot(result.Binding)}/{FileName(result.Binding, artifact)}" &&
                actual.RegisteredArtifact.CreatedAt == ParseUtc(receipt.CreatedAt));
            expectedPaths.Add(artifacts.GetValidatedRegisteredArtifactPath(delivery.ArtifactId));
        }
        Require(receiptStream.RegisteredArtifact.CreatedAt == ParseUtc(receipt.CreatedAt));
        string directory = Path.GetDirectoryName(expectedPaths.First())!;
        Require(expectedPaths.SetEquals(Directory.EnumerateFileSystemEntries(directory)));
        string prefix = $"projects/{result.Binding.ProjectId}/deliveries/source-only/{result.Binding.ExecutionId}/";
        Require(result.Artifacts.Select(a => a.Delivery!.Artifact.ArtifactId).ToHashSet().SetEquals(
            artifacts.ListProjectArtifacts(result.Binding.ProjectId).Where(a => a.StorageReference.StartsWith(prefix, StringComparison.Ordinal)).Select(a => a.Id)) &&
            !WorkspacePathSafety.PathEntryExistsConservatively(Path.Combine(ProjectFolders.GetPath(artifacts.LocalWorkspacePath, result.Binding.ProjectId),
                "staging", StagingName(result.Binding))));
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(artifacts.LocalDatabasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        RequireEvaluationSet(connection, transaction, basis);
        transaction.Commit();
    }

    internal static void RequireEvaluationSet(SqliteConnection connection, SqliteTransaction transaction, SourceOnlyResult basis)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT artifact_id FROM automated_evaluations
            WHERE project_id = $project AND job_id = $job AND environment_version = $environment;
            """;
        SqliteStoreSupport.AddText(command, "$project", basis.Binding.ProjectId.ToString());
        SqliteStoreSupport.AddText(command, "$job", basis.Binding.RootJobId.ToString());
        SqliteStoreSupport.AddText(command, "$environment", SourceOnlyOutputEvaluation.EnvironmentVersion);
        using SqliteDataReader reader = command.ExecuteReader();
        var evaluatedIds = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            Require(evaluatedIds.Count < SourceOnlyResult.MaximumArtifactCount && reader.GetValue(0) is string);
            evaluatedIds.Add(Id<ArtifactIdTag>(reader.GetString(0)).ToString());
        }
        Require(evaluatedIds.SetEquals(basis.Artifacts.Select(a => a.ArtifactId.ToString())));
    }

    internal SourceOnlyResult Attach(SourceOnlyResult basis)
    {
        Require(basis.Artifacts.All(a => a.Delivery is null) && basis.CanonicalDigest.Hex == BaseResultDigest);
        var receipt = new SourceOnlyResultEvidenceReference(SourceOnlyResultEvidenceKind.DeliveryReceipt, Reference);
        return CopyResult(basis, basis.Artifacts.Select(a => CopyArtifact(a,
            new(SourceOnlyResultCodec.Reference(Deliveries.Single(d => d.Input.Artifact.ArtifactId == a.ArtifactId.ToString()).Delivery), receipt))).ToArray());
    }

    internal static SourceOnlyResult WithoutDelivery(SourceOnlyResult result) => result.Artifacts.All(a => a.Delivery is null)
        ? result : CopyResult(result, result.Artifacts.Select(a => CopyArtifact(a, null)).ToArray());

    internal static SourceOnlyResultArtifact CopyArtifact(SourceOnlyResultArtifact a, SourceOnlyResultDelivery? delivery) => new(
        a.Role, a.RenditionId, a.Artifact, a.VersionId, a.VersionDigest, a.Source, a.GraphDigest, a.ManifestDigest, a.Evidence,
        a.Evaluation, a.Measurements, a.FailureCode, a.QcFailureReason, a.QcFailureStage, delivery,
        a.ThumbnailPlanId, a.FrameEvidence, a.ProfileId, a.ProfileDigest);

    private static SourceOnlyResult CopyResult(SourceOnlyResult r, IReadOnlyList<SourceOnlyResultArtifact> artifacts) => new(
        r.Binding, r.GenerationDisposition, artifacts, r.Omissions, r.Failures, r.ReconciliationArtifacts, r.TranscriptReferences,
        r.PrimaryWorkflow, r.StagingCleanupRequired, r.GenerationWarnings, r.NativeReconciliationAttempts, r.AncillaryArtifacts);

    internal static SourceOnlyResult Reconciliation(SourceOnlyResult basis, IReadOnlyList<SourceOnlyResultPromotionAttempt> attempts) => new(
        basis.Binding, StandardArtifactGenerationDisposition.ReconciliationRequired,
        basis.Artifacts.Select(a => CopyArtifact(a, null)).ToArray(), basis.Omissions,
        basis.Failures.Append(new(SourceOnlyResultFailureCode.DeliveryNotValidated)).Distinct().ToArray(),
        basis.ReconciliationArtifacts.Concat(attempts.Select(a => a.Artifact)).DistinctBy(a => a.ArtifactId).ToArray(),
        basis.TranscriptReferences, basis.PrimaryWorkflow, true, basis.GenerationWarnings,
        basis.NativeReconciliationAttempts.Concat(attempts).DistinctBy(a => a.Artifact.ArtifactId).ToArray(),
        basis.AncillaryArtifacts);

    /// <summary>Shape-only transition check, NOT delivery authorization. ValidateAsync verifies every receipt/file.</summary>
    internal static bool IsResultExtension(SourceOnlyResult candidate, string storedJson, ContentDigest? storedDigest)
    {
        try
        {
            SourceOnlyResult stored = SourceOnlyResult.ParseCanonical(storedJson, storedDigest);
            SourceOnlyResult basis = WithoutDelivery(stored);
            if (basis.HasBlockingFailures) return false;
            if (candidate.Artifacts.Count > 0 && candidate.Artifacts.All(a => a.Delivery is not null))
                return stored.Artifacts.All(a => a.Delivery is null) && WithoutDelivery(candidate).CanonicalJson == basis.CanonicalJson;
            return candidate.NativeReconciliationAttempts.Count > 0 &&
                Reconciliation(basis, candidate.NativeReconciliationAttempts).CanonicalJson == candidate.CanonicalJson;
        }
        catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e)) { return false; }
    }

    internal static DateTimeOffset ParseUtc(string value) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out DateTimeOffset at) && at.Offset == TimeSpan.Zero && at.ToString("O", CultureInfo.InvariantCulture) == value
            ? at : throw Invalid();
}

internal sealed record SourceOnlyDeliveryAttestation(string Schema, string SourceCandidateId, string OriginalArtifactId,
    string OriginalFileName, string SourceDigest, long SourceByteSize, string SelectedBy, string SelectedAt,
    string ActorSid, string AttestedAt, bool InternalEditingPermissionAttested, bool ExternalPublicationAllowed);

/// <summary>Every actual validated input stays open through the set copy and its registration.</summary>
internal sealed class SourceOnlyDeliveryHandles : IAsyncDisposable
{
    private readonly Dictionary<EntityId<ArtifactIdTag>, ValidatedArtifactReadStream> _files = [];
    internal IReadOnlyCollection<ValidatedArtifactReadStream> Files => _files.Values;
    internal async Task<ValidatedArtifactReadStream> OpenAsync(ArtifactStore artifacts, SourceOnlyResultArtifactReference reference,
        EntityId<ProjectIdTag> projectId, CancellationToken ct)
    {
        if (!_files.TryGetValue(reference.ArtifactId, out ValidatedArtifactReadStream? stream))
        {
            stream = await artifacts.OpenValidatedReadAsync(reference.ArtifactId, ct).ConfigureAwait(false);
            _files.Add(reference.ArtifactId, stream);
        }
        SourceOnlyResultEvidenceStore.RequireSame(stream, reference, projectId);
        return stream;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (ValidatedArtifactReadStream stream in _files.Values) await stream.DisposeAsync().ConfigureAwait(false);
    }
}