using System.Security.Cryptography;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using Microsoft.Data.Sqlite;
using static HubRadio.App.Projects.SourceOnlyResultCodec;

namespace HubRadio.App.Projects;

/// <summary>
/// Real, local-only set delivery. Wire this method to SourceOnlyPipeline.deliverAsync.
/// No approval, external path, worker, UI, Job completion or execution-state mutation occurs here.
/// Every output retains its independent stored evaluation; the latest primary workflow
/// guards only the project/primary. Warnings and nonblocking NOT EVALUATED stay unchanged.
/// </summary>
/// <remarks>
/// BEGIN IMMEDIATE protects the SAME database throughout validation, actual copying,
/// directory promotion and batch registration. Every validated input handle stays open.
/// Copies are finalized with one non-overwriting Directory.Move. The receipt belongs in
/// Evidence, not inside the Delivery kind's directory; it is moved separately. SQLite
/// atomically registers ALL copies plus that receipt only after all final bytes verify.
/// Filesystem rename and SQLite commit are NOT a distributed transaction: any uncertain
/// failure leaves owned paths intact and returns a known pipeline reconciliation failure.
/// A registered, verified complete receipt recovers a lost response without copying again.
/// Unregistered/partial destinations NEVER authorize implicit retry, overwrite or cleanup.
/// This service does not manufacture the legacy single-output Exported event.
/// </remarks>
public sealed class SourceOnlyDeliveryService
{
    private readonly string _databasePath;
    private readonly string _workspacePath;
    private readonly ArtifactStore _artifacts;
    private readonly SourceOnlyResultService _providedResults;
    private readonly QuickStartWorkflowStore _providedWorkflows;
    private readonly SourceOnlyResultService _results;
    private readonly QuickStartWorkflowStore _workflows;
    private readonly ProjectStore _projects;
    private readonly SourceOnlyExecutionStore _executions;
    private readonly Func<Stream, Stream, CancellationToken, Task> _copy;
    private readonly Action? _afterFilesFinalized;
    private readonly Func<DateTimeOffset> _utcNow;

    public SourceOnlyDeliveryService(string databasePath, string workspacePath, ArtifactStore artifacts,
        SourceOnlyResultService results, QuickStartWorkflowStore workflows)
        : this(databasePath, workspacePath, artifacts, results, workflows,
            static (input, output, ct) => input.CopyToAsync(output, 65536, ct))
    { }

    // Test-only fault injection. Production always performs Stream.CopyToAsync and hashes its actual output.
    internal SourceOnlyDeliveryService(string databasePath, string workspacePath, ArtifactStore artifacts,
        SourceOnlyResultService results, QuickStartWorkflowStore workflows,
        Func<Stream, Stream, CancellationToken, Task> copyAsync, Action? afterFilesFinalized = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _databasePath = SqliteStoreSupport.ValidateLocalAbsolutePath(databasePath, nameof(databasePath));
        _workspacePath = Path.TrimEndingDirectorySeparator(SqliteStoreSupport.ValidateLocalAbsolutePath(workspacePath, nameof(workspacePath)));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _artifacts.RequireLocalBinding(_databasePath, _workspacePath);
        _ = SourceOnlySourceLocator.RequirePath(_databasePath, directory: false);
        _ = SourceOnlySourceLocator.RequirePath(_workspacePath, directory: true);
        if (OperatingSystem.IsWindows() && new[] { _databasePath, _workspacePath }.Any(path =>
            new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)))
            throw new ArgumentException("Local delivery requires a local database and workspace volume.");
        _providedResults = results ?? throw new ArgumentNullException(nameof(results));
        _providedWorkflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        // Injected services may veto, but cannot grant authority from another DB. The
        // final validator and every authoritative row read are bound to our locked DB.
        _projects = new(_databasePath);
        _workflows = new(_databasePath);
        _executions = new(_databasePath);
        _results = new(_artifacts, new AutomatedEvaluationStore(_databasePath), _workflows, _projects);
        _copy = copyAsync ?? throw new ArgumentNullException(nameof(copyAsync));
        _afterFilesFinalized = afterFilesFinalized;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SourceOnlyResult> DeliverAsync(StoredSourceOnlyExecution execution, SourceOnlyResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution); ArgumentNullException.ThrowIfNull(result);
        SourceOnlyResult basis = result;
        var attempts = new List<SourceOnlyResultPromotionAttempt>();
        var copyHandles = new List<FileStream>();
        IReadOnlyList<ValidatedArtifactReadStream> finalHandles = [];
        bool filesystemEffects = false;
        bool bound = false;
        await using var held = new SourceOnlyDeliveryHandles();
        try
        {
            ct.ThrowIfCancellationRequested();
            result.ValidateBinding(execution);
            bound = true;
            basis = SourceOnlyDeliveryReceipt.WithoutDelivery(result);
            if (basis.HasBlockingFailures)
                throw new SourceOnlyPipeline.PipelineFailure(basis.ReconciliationRequired
                    ? SourceOnlyExecutionPhase.ReconciliationRequired : SourceOnlyExecutionPhase.Failed, "source_only.delivery_blocked", result);
            Require(result.Artifacts.All(a => a.Delivery is null) || result.Artifacts.All(a => a.Delivery is not null));
            _artifacts.RequireLocalBinding(_databasePath, _workspacePath);

            using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            ct.ThrowIfCancellationRequested();
            StoredSourceOnlyExecution current = _executions.Get(execution.Request.ExecutionId) ?? throw Invalid();
            Require(current.RequestDigest == execution.RequestDigest && current.RequestJson == execution.RequestJson &&
                current.Phase is SourceOnlyExecutionPhase.Delivering or SourceOnlyExecutionPhase.Completed);
            string projectRoot = Path.Combine(_workspacePath, "projects", basis.Binding.ProjectId.ToString());
            string staging = Path.Combine(projectRoot, "staging", SourceOnlyDeliveryReceipt.StagingName(basis.Binding));
            string stagedFiles = Path.Combine(staging, "files");
            string finalDirectory = Path.Combine(projectRoot, "deliveries",
                SourceOnlyDeliveryReceipt.DeliveryRelativeRoot(basis.Binding).Replace('/', Path.DirectorySeparatorChar));
            string receiptDestination = Path.Combine(projectRoot, "evidence",
                SourceOnlyDeliveryReceipt.ReceiptRelativePath(basis.Binding).Replace('/', Path.DirectorySeparatorChar));
            SourceOnlyResultArtifact[] ordered = basis.Artifacts.OrderBy(a => a.ArtifactId.ToString(), StringComparer.Ordinal).ToArray();
            foreach (SourceOnlyResultArtifact artifact in ordered)
                attempts.Add(new(new(SourceOnlyDeliveryReceipt.DeliveryId(basis.Binding, artifact), artifact.ArtifactDigest,
                    artifact.ByteSize, ArtifactKind.Delivery), basis.Binding.ProjectId,
                    Path.Combine(stagedFiles, SourceOnlyDeliveryReceipt.FileName(basis.Binding, artifact)),
                    $"projects/{basis.Binding.ProjectId}/deliveries/{SourceOnlyDeliveryReceipt.DeliveryRelativeRoot(basis.Binding)}/{SourceOnlyDeliveryReceipt.FileName(basis.Binding, artifact)}"));

            StoredArtifact? prior = _artifacts.GetArtifact(SourceOnlyDeliveryReceipt.ReceiptId(basis.Binding));
            if (prior is not null)
            {
                filesystemEffects = true;
                Require(prior.Kind == ArtifactKind.Evidence && prior.ProjectId == basis.Binding.ProjectId &&
                    prior.ByteSize is > 0 and <= SourceOnlyDeliveryReceipt.MaximumBytes &&
                    prior.StorageReference == $"projects/{basis.Binding.ProjectId}/evidence/{SourceOnlyDeliveryReceipt.ReceiptRelativePath(basis.Binding)}");
                attempts.Add(new(SourceOnlyResultEvidenceStore.Reference(prior), basis.Binding.ProjectId,
                    Path.Combine(staging, "receipt.json"), $"projects/{basis.Binding.ProjectId}/evidence/{SourceOnlyDeliveryReceipt.ReceiptRelativePath(basis.Binding)}",
                    SourceOnlyResultEvidenceKind.DeliveryReceipt));
                ValidatedArtifactReadStream saved = await held.OpenAsync(_artifacts, attempts[^1].Artifact, basis.Binding.ProjectId, ct).ConfigureAwait(false);
                SourceOnlyDeliveryReceipt receipt = await SourceOnlyDeliveryReceipt.ReadAsync(saved, ct).ConfigureAwait(false);
                SourceOnlyResult recovered = receipt.Attach(basis);
                Require(result.Artifacts.All(a => a.Delivery is null) || recovered.CanonicalJson == result.CanonicalJson);
                Require(!WorkspacePathSafety.PathEntryExistsConservatively(staging));
                await ValidateStoredBasisAsync(current, basis, held, ct).ConfigureAwait(false);
                Require((await _results.ValidateAsync(recovered, current, ct).ConfigureAwait(false)).IsValid);
                transaction.Commit();
                return recovered; // Exact committed receipt; no new IDs, bytes, moves, approvals or events.
            }

            string deliveryPrefix = $"projects/{basis.Binding.ProjectId}/deliveries/source-only/{basis.Binding.ExecutionId}/";
            if (WorkspacePathSafety.PathEntryExistsConservatively(staging) ||
                WorkspacePathSafety.PathEntryExistsConservatively(finalDirectory) ||
                WorkspacePathSafety.PathEntryExistsConservatively(receiptDestination) ||
                _artifacts.ListProjectArtifacts(basis.Binding.ProjectId).Any(a => a.StorageReference.StartsWith(deliveryPrefix, StringComparison.Ordinal)) ||
                attempts.Any(a => _artifacts.GetArtifact(a.Artifact.ArtifactId) is not null))
            {
                filesystemEffects = true;
                SourceOnlyResultPromotionAttempt? orphan = await DescribeUnregisteredReceiptAsync(basis.Binding,
                    Path.Combine(staging, "receipt.json"), receiptDestination).ConfigureAwait(false);
                if (orphan is not null) attempts.Add(orphan);
                throw Invalid(); // Never infer success from an unregistered receipt or an apparently complete directory.
            }
            Require(result.Artifacts.All(a => a.Delivery is null) && current.Phase == SourceOnlyExecutionPhase.Delivering &&
                current.StateDigest == execution.StateDigest);
            await ValidateStoredBasisAsync(current, basis, held, ct).ConfigureAwait(false);
            SourceOnlyDeliveryReceipt.RequireEvaluationSet(connection, transaction, basis);
            SourceOnlyResultGenerationCheckpoint? checkpoint = await SourceOnlyResultGenerationCheckpoint.ReadAsync(_artifacts, current, ct).ConfigureAwait(false);
            checkpoint?.ValidateSet(basis);
            if (checkpoint is not null) _ = await held.OpenAsync(_artifacts, checkpoint.Artifact, basis.Binding.ProjectId, ct).ConfigureAwait(false);
            foreach (SourceOnlyResultArtifact artifact in ordered)
            {
                _ = await held.OpenAsync(_artifacts, artifact.Artifact, basis.Binding.ProjectId, ct).ConfigureAwait(false);
                _ = await held.OpenAsync(_artifacts, artifact.Source, basis.Binding.ProjectId, ct).ConfigureAwait(false);
                foreach (SourceOnlyResultEvidenceReference proof in artifact.Evidence)
                    _ = await held.OpenAsync(_artifacts, proof.Artifact, basis.Binding.ProjectId, ct).ConfigureAwait(false);
                AutomatedEvaluationRun stored = AutomatedEvaluationStore.Read(connection, transaction, artifact.Evaluation!.EvaluationId) ?? throw Invalid();
                Require(stored.CanonicalJson == artifact.Evaluation.CanonicalJson && stored.CanonicalDigest == artifact.Evaluation.CanonicalDigest);
            }
            var authority = SourceOnlyDeliveryReceipt.CaptureAuthority(basis, current, _projects, _workflows);
            Require(_providedWorkflows.Get(authority.Workflow.Binding)?.CanonicalJson == authority.Workflow.CanonicalJson);
            IReadOnlyList<SourceOnlyResultArtifactReference> rights = await SourceOnlyDeliveryReceipt.ReadRightsAsync(
                _artifacts, _executions, current, authority.Project, held, ct).ConfigureAwait(false);
            Require((await _providedResults.ValidateAsync(basis, current, ct).ConfigureAwait(false)).IsValid &&
                (await _results.ValidateAsync(basis, current, ct).ConfigureAwait(false)).IsValid);
            DateTimeOffset at = new[] { _utcNow().ToUniversalTime(), current.Request.CreatedAt, authority.Workflow.Events[^1].OccurredAt }
                .Concat(ordered.SelectMany(a => new[] { a.Evaluation!.CompletedAt, a.Evaluation.EvaluatedAt })).Max();
            SourceOnlyDeliveryReceipt planned = SourceOnlyDeliveryReceipt.Create(basis, current, authority.Snapshot, checkpoint?.Artifact, rights, at);
            planned.ValidateBinding(basis, current, authority.Snapshot, authority.Workflow, checkpoint?.Artifact, rights);
            attempts.Add(new(planned.Reference, basis.Binding.ProjectId, Path.Combine(staging, "receipt.json"),
                $"projects/{basis.Binding.ProjectId}/evidence/{SourceOnlyDeliveryReceipt.ReceiptRelativePath(basis.Binding)}", SourceOnlyResultEvidenceKind.DeliveryReceipt));
            SourceOnlyResult delivered = planned.Attach(basis);
            delivered.ValidateBinding(current); // Catch serialization/binding failures BEFORE filesystem side effects.
            ct.ThrowIfCancellationRequested();

            filesystemEffects = true; // Even a failed directory creation may leave an entry. Preserve, never sweep.
            SourceOnlySourceLocator.CreateDirectory(_workspacePath, stagedFiles);
            byte[] receiptBytes = Utf8.GetBytes(planned.CanonicalJson);
            await using (var file = NewFile(attempts[^1].StagingPath))
            {
                await file.WriteAsync(receiptBytes, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            copyHandles.Add(await HoldCopiedFileAsync(attempts[^1], ct).ConfigureAwait(false));
            for (int i = 0; i < ordered.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                ValidatedArtifactReadStream input = await held.OpenAsync(_artifacts, ordered[i].Artifact, basis.Binding.ProjectId, ct).ConfigureAwait(false);
                input.Position = 0;
                await using (var output = NewFile(attempts[i].StagingPath))
                {
                    await _copy(input, output, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                copyHandles.Add(await HoldCopiedFileAsync(attempts[i], ct).ConfigureAwait(false));
            }
            Require(attempts.Take(ordered.Length).Select(a => a.StagingPath).ToHashSet(WorkspacePathSafety.PathComparer)
                .SetEquals(Directory.EnumerateFileSystemEntries(stagedFiles)));
            SourceOnlySourceLocator.CreateDirectory(_workspacePath, Path.GetDirectoryName(finalDirectory)!);
            SourceOnlySourceLocator.CreateDirectory(_workspacePath, Path.GetDirectoryName(receiptDestination)!);
            Require(!WorkspacePathSafety.PathEntryExistsConservatively(finalDirectory) &&
                !WorkspacePathSafety.PathEntryExistsConservatively(receiptDestination));
            ct.ThrowIfCancellationRequested();
            // Windows can reject a parent rename while child handles are open, even with
            // FileShare.Delete. Release ONLY copied destinations (including the receipt).
            // Inputs and BEGIN IMMEDIATE remain held. The short unpinned destination
            // window relies on the owned workspace ACL; final rehash detects changed bytes
            // before ANY registration, then final handles stay held through batch commit.
            // This is not filesystem/DB atomicity; uncertain failures retain reconciliation.
            foreach (FileStream stream in copyHandles) await stream.DisposeAsync().ConfigureAwait(false);
            copyHandles.Clear();
            Directory.Move(stagedFiles, finalDirectory); // One set rename, never a sequence of visible individual promotions.
            _afterFilesFinalized?.Invoke();
            ct.ThrowIfCancellationRequested();
            File.Move(attempts[^1].StagingPath, receiptDestination, overwrite: false);
            _ = SourceOnlySourceLocator.RequirePath(staging, directory: true);
            Directory.Delete(staging, recursive: false); // Only the now EMPTY directory that this invocation created.

            ArtifactPromotionRequest[] registrations = attempts.Select(a => new ArtifactPromotionRequest(a.Artifact.ArtifactId,
                basis.Binding.ProjectId, a.Artifact.Kind, a.StagingPath,
                a.StorageReference[($"projects/{basis.Binding.ProjectId}/{ArtifactStore.GetKindFolder(a.Artifact.Kind)}/").Length..],
                a.Artifact.ContentDigest, a.Artifact.ByteSize, at)).ToArray();
            finalHandles = await _artifacts.RegisterLocalDeliverySetAsync(connection, transaction,
                basis.Binding.ExecutionId, basis.Binding.RootJobId, registrations, ct).ConfigureAwait(false);
            SourceOnlyDeliveryReceipt actualReceipt = await SourceOnlyDeliveryReceipt.ReadAsync(finalHandles[^1], ct).ConfigureAwait(false);
            Require(actualReceipt.CanonicalJson == planned.CanonicalJson);
            actualReceipt.ValidateBinding(delivered, current, authority.Snapshot, authority.Workflow, checkpoint?.Artifact, rights);
            // Also detect unexpected trigger effects on the validated inputs before commit.
            foreach (ValidatedArtifactReadStream input in held.Files)
            {
                StoredArtifact actual = ArtifactStore.ReadArtifact(connection, transaction, input.Id) ?? throw Invalid();
                SourceOnlyResultEvidenceStore.RequireSame(actual, SourceOnlyResultEvidenceStore.Reference(input.RegisteredArtifact), basis.Binding.ProjectId);
                Require(actual.StorageReference == input.RegisteredArtifact.StorageReference && actual.CreatedAt == input.RegisteredArtifact.CreatedAt);
            }
            ValidateLockedAuthority(connection, transaction, current, basis, authority.Snapshot, authority.Workflow);
            ct.ThrowIfCancellationRequested();
            transaction.Commit(); // All copies + verified receipt become visible together; never marks a Job/Execution Completed.
            Require((await _results.ValidateAsync(delivered, current, CancellationToken.None).ConfigureAwait(false)).IsValid);
            return delivered; // Late cancellation cannot relabel a validated committed set as unsaved.
        }
        catch (SourceOnlyPipeline.PipelineFailure) when (!filesystemEffects) { throw; }
        catch (Exception exception) when (SourceOnlyOutputEvaluation.Recoverable(exception))
        {
            if (filesystemEffects)
                throw new SourceOnlyPipeline.PipelineFailure(SourceOnlyExecutionPhase.ReconciliationRequired,
                    "source_only.delivery_reconciliation_required", SourceOnlyDeliveryReceipt.Reconciliation(basis, attempts));
            throw new SourceOnlyPipeline.PipelineFailure(exception is OperationCanceledException && ct.IsCancellationRequested
                ? SourceOnlyExecutionPhase.Canceled : SourceOnlyExecutionPhase.Failed,
                exception is OperationCanceledException && ct.IsCancellationRequested ? "source_only.delivery_canceled" : "source_only.delivery_inputs_invalid",
                bound ? result : null);
        }
        finally
        {
            foreach (ValidatedArtifactReadStream stream in finalHandles) await stream.DisposeAsync().ConfigureAwait(false);
            foreach (FileStream stream in copyHandles) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ValidateStoredBasisAsync(StoredSourceOnlyExecution execution, SourceOnlyResult basis,
        SourceOnlyDeliveryHandles held, CancellationToken ct)
    {
        bool persisted = false;
        if (execution.ResultJson is not null)
        {
            SourceOnlyResult stored = SourceOnlyResult.ParseCanonical(execution.ResultJson, execution.ResultDigest);
            Require(SourceOnlyDeliveryReceipt.WithoutDelivery(stored).CanonicalJson == basis.CanonicalJson);
            persisted = true;
        }
        // The coordinator saves this immutable Analysis checkpoint before invoking delivery.
        // Standalone callers may instead persist the actual result with UpdateState/Get.
        EntityId<ArtifactIdTag> id = SourceOnlyPipeline.StableId<ArtifactIdTag>(basis.Binding.ExecutionId.ToString(), "evaluation-receipt");
        StoredArtifact? evaluated = _artifacts.GetArtifact(id);
        if (evaluated is not null)
        {
            Require(evaluated.Kind == ArtifactKind.Analysis && evaluated.ProjectId == basis.Binding.ProjectId &&
                evaluated.StorageReference == $"projects/{basis.Binding.ProjectId}/analysis/source-only/{id}.evaluation-receipt.json" &&
                evaluated.ContentDigest == basis.CanonicalDigest && evaluated.ByteSize == Utf8.GetByteCount(basis.CanonicalJson));
            ValidatedArtifactReadStream stream = await held.OpenAsync(_artifacts, SourceOnlyResultEvidenceStore.Reference(evaluated), basis.Binding.ProjectId, ct).ConfigureAwait(false);
            byte[] bytes = new byte[checked((int)stream.ByteSize)];
            stream.Position = 0;
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            Require(Utf8.GetString(bytes) == basis.CanonicalJson);
            persisted = true;
        }
        Require(persisted); // A caller-selected subset of independently valid files is not the accepted result set.
    }

    private static void ValidateLockedAuthority(SqliteConnection connection, SqliteTransaction transaction,
        StoredSourceOnlyExecution execution, SourceOnlyResult basis, SourceOnlyDeliverySnapshot snapshot, QuickStartWorkflow workflow)
    {
        SourceOnlyDeliveryReceipt.RequireEvaluationSet(connection, transaction, basis);
        foreach (SourceOnlyResultArtifact artifact in basis.Artifacts)
            Require(AutomatedEvaluationStore.Read(connection, transaction, artifact.Evaluation!.EvaluationId)?.CanonicalJson == artifact.Evaluation.CanonicalJson);
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT revision, payload, publication_scope FROM projects WHERE id = $id;";
            SqliteStoreSupport.AddText(command, "$id", basis.Binding.ProjectId.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            Require(reader.Read() && reader.GetInt64(0) == snapshot.ProjectRevision && reader.GetString(2) == "InternalOnly" &&
                ContentDigest.Compute(Utf8.GetBytes(SqliteStoreSupport.ReadCanonicalPayload(reader, 1, "delivery project"))).Hex == snapshot.ProjectCanonicalDigest &&
                !reader.Read());
        }
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT revision, canonical_digest, canonical_payload FROM quick_start_workflows
                WHERE project_id = $project AND composition_version_id = $version AND composition_version_digest = $digest;
                """;
            SqliteStoreSupport.AddText(command, "$project", basis.Binding.ProjectId.ToString());
            SqliteStoreSupport.AddText(command, "$version", basis.VersionId.ToString());
            SqliteStoreSupport.AddText(command, "$digest", basis.VersionDigest.Hex);
            using SqliteDataReader reader = command.ExecuteReader();
            Require(reader.Read() && reader.GetInt64(0) == workflow.Revision && reader.GetString(1) == workflow.CanonicalDigest.Hex &&
                SqliteStoreSupport.ReadCanonicalPayload(reader, 2, "delivery workflow") == workflow.CanonicalJson && !reader.Read());
        }
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT phase, state_digest FROM source_only_executions WHERE id = $id;";
            SqliteStoreSupport.AddText(command, "$id", execution.Request.ExecutionId.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            Require(reader.Read() && reader.GetString(0) == "Delivering" && reader.GetString(1) == execution.StateDigest.Hex && !reader.Read());
        }
    }

    private static async Task<SourceOnlyResultPromotionAttempt?> DescribeUnregisteredReceiptAsync(
        SourceOnlyResultBinding binding, string stagingPath, string finalPath)
    {
        // Bounded, deterministic managed paths ONLY. This is an orphan descriptor,
        // never receipt authorization or an instruction to move/retry/delete anything.
        foreach (string path in new[] { stagingPath, finalPath })
        {
            try
            {
                using FileStream file = SourceOnlySourceLocator.OpenHeldFile(path);
                if (file.Length is <= 0 or > SourceOnlyDeliveryReceipt.MaximumBytes) continue;
                long size = file.Length;
                ContentDigest digest = ContentDigest.FromBytes(await SHA256.HashDataAsync(file, CancellationToken.None).ConfigureAwait(false));
                if (file.Length != size) continue;
                return new(new(SourceOnlyDeliveryReceipt.ReceiptId(binding), digest, size, ArtifactKind.Evidence), binding.ProjectId,
                    stagingPath, $"projects/{binding.ProjectId}/evidence/{SourceOnlyDeliveryReceipt.ReceiptRelativePath(binding)}",
                    SourceOnlyResultEvidenceKind.DeliveryReceipt);
            }
            catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e)) { }
        }
        return null; // Do not invent a receipt digest if its bytes cannot be inspected safely.
    }

    private static FileStream NewFile(string path)
    {
        _ = SourceOnlySourceLocator.RequirePath(Path.GetDirectoryName(path)!, directory: true);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
    }

    private static async Task<FileStream> HoldCopiedFileAsync(SourceOnlyResultPromotionAttempt attempt, CancellationToken ct)
    {
        _ = SourceOnlySourceLocator.RequirePath(attempt.StagingPath, directory: false);
        var stream = new FileStream(attempt.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            WorkspacePathSafety.ValidateRegularFileAttributes(File.GetAttributes(stream.SafeFileHandle));
            Require(stream.Length == attempt.Artifact.ByteSize);
            ContentDigest digest = ContentDigest.FromBytes(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            Require(digest == attempt.Artifact.ContentDigest && stream.Length == attempt.Artifact.ByteSize);
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }
}