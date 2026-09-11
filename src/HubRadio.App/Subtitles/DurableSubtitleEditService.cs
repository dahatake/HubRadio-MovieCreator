using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Subtitles;

/// <summary>
/// Native capture of one validated journal branch. This is not a render approval,
/// a working-copy update, or a transaction spanning multiple captured branches.
/// The lists and snapshots are immutable; later edits do not advance a capture.
/// </summary>
internal sealed record SubtitleEditCapture(
    EntityId<ProjectIdTag> ProjectId,
    EntityId<CompositionVersionIdTag> RootVersionId,
    ContentDigest GeneratedInputDigest,
    ContentDigest InitialSnapshotDigest,
    LayoutSubtitleBridgeSnapshot Head,
    ContentDigest HeadSnapshotDigest,
    IReadOnlyList<StoredSubtitleEdit> Edits)
{
    internal SubtitleEditProjectionEvidence? InitialProjection { get; init; }
    internal EntityId<CompositionVersionIdTag> HeadVersionId => Head.CompositionVersion.Id;
    internal int Sequence => Edits.Count;
}

/// <summary>
/// Replays generated subtitle branches from schema 012. The host supplies the
/// already validated generated snapshot and its SourceOnlyApplication input key,
/// and performs authorization before Execute. No live runtime is mutated here.
/// Only a successful store transaction can publish a new child or return its ACK.
/// </summary>
internal sealed class DurableSubtitleEditService : ILayoutSubtitleBridgeRuntime
{
    private static readonly Guid DigestRequestId = new("74826958-1ab9-4e16-874d-c2cd31c52b21");
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SubtitleEditStore store;
    private readonly object gate = new();
    private readonly Dictionary<(EntityId<ProjectIdTag> Project, EntityId<CompositionVersionIdTag> Root), Branch> branches = [];
    private readonly Dictionary<EntityId<ProjectIdTag>, EntityId<CompositionVersionIdTag>> defaults = [];

    internal DurableSubtitleEditService(SubtitleEditStore store) =>
        this.store = store ?? throw new ArgumentNullException(nameof(store));

    internal void RegisterGenerated(
        LayoutSubtitleBridgeSnapshot initial,
        ContentDigest generatedInputDigest,
        CancellationToken cancellationToken = default,
        bool makeDefault = true,
        SubtitleEditProjectionEvidence? projection = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(generatedInputDigest);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInitial(initial, cancellationToken, projection);
            ContentDigest digest = ComputeSnapshotDigest(initial);
            var key = (initial.ProjectId, initial.CompositionVersion.Id);
            if (branches.TryGetValue(key, out Branch? existing))
            {
                if (existing.GeneratedInputDigest != generatedInputDigest || existing.InitialDigest != digest
                    || existing.InitialProjection?.ContentDigest != projection?.ContentDigest)
                {
                    throw InvalidReplay();
                }

                // Keep the ORIGINAL root, its complete native composition dictionary,
                // and every known child object. Registration never resets a head.
                Branch refreshed = ReadAndReplay(existing, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                branches[key] = refreshed;
            }
            else
            {
                var branch = new Branch(initial, generatedInputDigest, digest,
                    new Dictionary<EntityId<CompositionVersionIdTag>, LayoutSubtitleBridgeSnapshot>
                    {
                        [initial.CompositionVersion.Id] = initial,
                    }, [])
                { InitialProjection = projection };
                // ReadBranchState also rejects a missing DB composition, another project's
                // root, and a journal child presented as a new generated root.
                Branch restored = ReadAndReplay(branch, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                branches.Add(key, restored);
            }

            if (makeDefault) { defaults[initial.ProjectId] = initial.CompositionVersion.Id; }
        }
    }

    public LayoutSubtitleBridgeOperationResult Execute(
        BridgeRequest request,
        string actorSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method is not (BridgeMethod.LayoutRead or BridgeMethod.LayoutApply))
            {
                return Failure(LayoutSubtitleBridgeStatus.Unavailable);
            }

            // BridgeRequest.Create is also used by native callers and does not
            // validate or detach parameters. Enforce the closed wire contract here.
            request = BridgeRequest.Create(request.RequestId, request.Method, request.Parameters.Clone());
            if (request.RequestId == Guid.Empty || !LayoutSubtitleBridgeProtocol.HasValidParameters(request.Method, request.Parameters))
            {
                return Failure(LayoutSubtitleBridgeStatus.Invalid);
            }
            try { _ = QuickStartActor.User(actorSid); }
            catch (ArgumentException) { return Failure(LayoutSubtitleBridgeStatus.Invalid); }

            bool read = request.Method == BridgeMethod.LayoutRead;
            if (!read && request.Parameters.GetProperty("operation").GetProperty("kind").GetString() != "editSubtitle")
            {
                // Layout/terop owners may route this elsewhere. Never ACK an
                // ephemeral layout edit through the durable subtitle provider.
                return Failure(LayoutSubtitleBridgeStatus.Unavailable);
            }

            EntityId<ProjectIdTag> projectId = LayoutSubtitleBridgeRuntime.ParseEntity<ProjectIdTag>(request.Parameters.GetProperty("projectId"));
            RefreshProject(projectId, cancellationToken);
            Branch[] projectBranches = branches.Values.Where(branch => branch.Initial.ProjectId == projectId).ToArray();
            if (projectBranches.Length == 0) { return Failure(LayoutSubtitleBridgeStatus.NotFound); }

            // Resolve an old request BEFORE head CAS. Its original parent can now
            // be historical; an exact retry still returns that operation's child.
            if (!read && FindReplay(projectBranches, request, actorSid) is { } replay) { return replay; }

            bool pinned = request.Parameters.TryGetProperty("expectedVersionId", out JsonElement versionValue);
            EntityId<CompositionVersionIdTag>? versionId = pinned
                ? LayoutSubtitleBridgeRuntime.ParseEntity<CompositionVersionIdTag>(versionValue)
                : null;
            Branch? selected = pinned
                ? projectBranches.SingleOrDefault(branch => branch.Versions.ContainsKey(versionId!))
                : defaults.TryGetValue(projectId, out var root) ? branches[(projectId, root)] : null;
            if (selected is null) { return Failure(LayoutSubtitleBridgeStatus.NotFound); }
            LayoutSubtitleBridgeSnapshot snapshot = pinned ? selected.Versions[versionId!] : selected.Head;
            if (pinned && request.Parameters.GetProperty("expectedVersionDigest").GetString() != snapshot.CompositionVersion.ContentDigest.Hex)
            {
                return Failure(LayoutSubtitleBridgeStatus.Conflict);
            }
            if (read)
            {
                // Validate the REQUESTED exact version above before resolving its
                // branch head. Omitting latestInBranch preserves historical reads.
                // Neither path installs a draft or evaluates layout-time projection.
                bool latest = request.Parameters.TryGetProperty("latestInBranch", out _);
                return LayoutSubtitleBridgeOperationResult.Available(latest ? selected.Head : snapshot,
                    MetadataFor(selected, selected.Edits.LastOrDefault()));
            }

            // Require existing optional version pins for durable writes. Schema 012
            // stores the operation, parent and sequence, so these closed parameters
            // can be reconstructed EXACTLY without adding any new wire fields.
            JsonElement operation = request.Parameters.GetProperty("operation");
            BridgeRequest expected = EditRequest(request.RequestId, selected.Head, operation);
            if (!pinned || snapshot.CompositionVersion.Id != selected.Head.CompositionVersion.Id
                || !SameParameters(request, expected))
            {
                return Failure(LayoutSubtitleBridgeStatus.Conflict);
            }

            DateTimeOffset occurredAt = DateTimeOffset.UtcNow;
            if (occurredAt < snapshot.CompositionVersion.CreatedAt) { occurredAt = snapshot.CompositionVersion.CreatedAt.ToUniversalTime(); }
            foreach (LayoutSubtitleCueBinding cue in snapshot.Subtitles)
            {
                if (occurredAt < cue.History.CurrentRevision.CreatedAt) { occurredAt = cue.History.CurrentRevision.CreatedAt.ToUniversalTime(); }
            }
            var allocatedIds = new List<Guid>();
            Guid Allocate()
            {
                if (allocatedIds.Count >= SubtitleEditStore.MaximumAllocatedIds) { throw InvalidReplay(); }
                Guid id = Guid.NewGuid();
                allocatedIds.Add(id);
                return id;
            }

            // 'expected' has an actual Int32 token even if the canonical incoming
            // expectedRevision uses exponent notation (10 -> 1e1). No values change.
            LayoutSubtitleBridgeOperationResult evaluated = LayoutSubtitleBridgeRuntime.Evaluate(
                snapshot, expected, actorSid, () => occurredAt, Allocate, cancellationToken);
            if (evaluated.Status != LayoutSubtitleBridgeStatus.Available) { return evaluated; }
            LayoutSubtitleBridgeSnapshot child = evaluated.Snapshot ?? throw InvalidReplay();
            ContentDigest parentDigest = ComputeSnapshotDigest(snapshot);
            ContentDigest childDigest = ComputeSnapshotDigest(child);
            if (child.CompositionVersion.CreatedAt != occurredAt || childDigest == parentDigest) { throw InvalidReplay(); }
            // Reserve the complete ACK before writing. Every journal SHA-256 has
            // the same wire length; this sizing-only value is NEVER emitted as
            // evidence. The actual ACK below uses only store.Commit's validated row.
            var sizingMetadata = new DurableSubtitleBranchMetadata(selected.Initial.CompositionVersion.Id,
                child.CompositionVersion.Id, child.CompositionVersion.ContentDigest,
                checked(selected.Edits.Count + 1), childDigest);
            if (!LayoutSubtitleBridgeJson.CanWriteResponse(request,
                LayoutSubtitleBridgeOperationResult.Available(child, sizingMetadata)))
            {
                return Failure(LayoutSubtitleBridgeStatus.Unavailable);
            }
            var commit = new SubtitleEditCommit(request.RequestId, projectId, selected.Initial.CompositionVersion.Id,
                snapshot.CompositionVersion.Id, child.CompositionVersion, CompositionSnapshotJson.WriteVersion(child.CompositionVersion),
                selected.Edits.Count, selected.GeneratedInputDigest, parentDigest, childDigest,
                CanonicalJson.Canonicalize(operation.GetRawText()), allocatedIds.AsReadOnly(), actorSid, occurredAt,
                ExpectedRootPayloadDigest: Hash(CompositionSnapshotJson.WriteVersion(selected.Initial.CompositionVersion)));

            // Stage allocations before committing, but never install them early.
            var versions = new Dictionary<EntityId<CompositionVersionIdTag>, LayoutSubtitleBridgeSnapshot>(selected.Versions)
            {
                [child.CompositionVersion.Id] = child,
            };
            cancellationToken.ThrowIfCancellationRequested();
            StoredSubtitleEdit stored;
            try
            {
                // Integration note: the runtime uses a fixed composition ChangeReason
                // while operation.reason is the user's reason. Do NOT rewrite either
                // to satisfy a store that still incorrectly requires their equality.
                stored = store.Commit(commit);
            }
            catch (InvalidOperationException exception)
            {
                // A different service can win the request-ID race with different
                // allocated IDs/time. Only its revalidated journal can supply an ACK.
                RefreshProject(projectId, cancellationToken);
                LayoutSubtitleBridgeOperationResult? concurrent = FindReplay(
                    branches.Values.Where(branch => branch.Initial.ProjectId == projectId), request, actorSid);
                if (concurrent is not null) { return concurrent; }
                if (exception is StoreConcurrencyException) { return Failure(LayoutSubtitleBridgeStatus.Conflict); }
                throw;
            }

            // Commit includes child + operation + full snapshot digest in one DB
            // transaction and returns only after post-write validation and commit.
            // Do not check cancellation between that commit and cache publication.
            var committed = new Branch(
                selected.Initial, selected.GeneratedInputDigest, selected.InitialDigest, versions,
                Array.AsReadOnly(selected.Edits.Append(stored).ToArray()))
            { InitialProjection = selected.InitialProjection };
            branches[(projectId, selected.Initial.CompositionVersion.Id)] = committed;
            return LayoutSubtitleBridgeOperationResult.Available(child, MetadataFor(committed, stored));
        }
    }

    internal LayoutSubtitleBridgeSnapshot GetLatest(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> rootVersionId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(rootVersionId);
        lock (gate)
        {
            RefreshProject(projectId, CancellationToken.None);
            return RequiredBranch(projectId, rootVersionId).Head;
        }
    }

    internal EntityId<CompositionVersionIdTag> GetBranchRoot(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> versionId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(versionId);
        lock (gate)
        {
            RefreshProject(projectId, CancellationToken.None);
            return branches.Values.SingleOrDefault(branch => branch.Initial.ProjectId == projectId && branch.Versions.ContainsKey(versionId))
                ?.Initial.CompositionVersion.Id ?? throw MissingBranch();
        }
    }

    internal IReadOnlyList<SubtitleEditCapture> Capture(
        EntityId<ProjectIdTag> projectId,
        IEnumerable<EntityId<CompositionVersionIdTag>> rootVersionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(rootVersionIds);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EntityId<CompositionVersionIdTag>[] roots = rootVersionIds.ToArray();
            if (roots.Any(static root => root is null) || roots.Distinct().Count() != roots.Length)
            {
                throw new ArgumentException("Capture requires unique non-null branch roots.", nameof(rootVersionIds));
            }
            RefreshProject(projectId, cancellationToken);
            return Array.AsReadOnly(roots.Select(root => CaptureBranch(RequiredBranch(projectId, root))).ToArray());
        }
    }

    /// <summary>
    /// Reads and store-verifies the full branch, but deterministically replays ONLY
    /// the accepted prefix. Later valid edits neither expand this capture nor replace
    /// its head. A historical capture never rewinds the service's live branch cache.
    /// </summary>
    internal SubtitleEditCapture CapturePrefix(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> rootVersionId,
        int sequence,
        ContentDigest? recordDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(rootVersionId);
        if (sequence is < 0 or > SubtitleEditStore.MaximumBranchEdits)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if ((sequence == 0) != (recordDigest is null))
            throw new ArgumentException("Only an empty subtitle prefix has no record digest.", nameof(recordDigest));
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Branch prefix = ReadAndReplay(RequiredBranch(projectId, rootVersionId), cancellationToken, sequence, recordDigest);
            cancellationToken.ThrowIfCancellationRequested();
            return CaptureBranch(prefix);
        }
    }

    private static SubtitleEditCapture CaptureBranch(Branch branch) =>
        new(branch.Initial.ProjectId, branch.Initial.CompositionVersion.Id, branch.GeneratedInputDigest, branch.InitialDigest,
            branch.Head, ComputeSnapshotDigest(branch.Head), branch.Edits)
        { InitialProjection = branch.InitialProjection };

    private Branch RequiredBranch(EntityId<ProjectIdTag> project, EntityId<CompositionVersionIdTag> root) =>
        branches.TryGetValue((project, root), out Branch? branch) ? branch : throw MissingBranch();

    private void RefreshProject(EntityId<ProjectIdTag> project, CancellationToken token)
    {
        var refreshed = new List<Branch>();
        foreach (Branch branch in branches.Values.Where(branch => branch.Initial.ProjectId == project))
        {
            refreshed.Add(ReadAndReplay(branch, token));
        }
        token.ThrowIfCancellationRequested();
        // No partially replayed branch is installed if a later row fails validation.
        foreach (Branch branch in refreshed) { branches[(project, branch.Initial.CompositionVersion.Id)] = branch; }
    }

    private Branch ReadAndReplay(Branch cached, CancellationToken token, int? sequence = null, ContentDigest? recordDigest = null)
    {
        token.ThrowIfCancellationRequested();
        LayoutSubtitleBridgeSnapshot initial = cached.Initial;
        StoredSubtitleEditBranchState state = store.ReadBranchState(initial.ProjectId, initial.CompositionVersion.Id);
        IReadOnlyList<StoredSubtitleEdit> rows = state.Edits;
        int count = sequence ?? rows.Count;
        if (count > rows.Count || (sequence.HasValue && count > 0 && rows[count - 1].RecordDigest != recordDigest))
            throw InvalidReplay();
        string rootCanonicalJson = CompositionSnapshotJson.WriteVersion(initial.CompositionVersion);
        // An empty journal is not evidence that the supplied generated root matches
        // storage. Compare its complete payload, including metadata, before replay.
        if (!string.Equals(state.RootVersion.CanonicalJson, rootCanonicalJson, StringComparison.Ordinal)
            || (!sequence.HasValue && rows.Count < cached.Edits.Count)
            || ComputeSnapshotDigest(initial) != cached.InitialDigest) { throw InvalidReplay(); }
        ContentDigest rootPayloadDigest = Hash(rootCanonicalJson);
        var versions = new Dictionary<EntityId<CompositionVersionIdTag>, LayoutSubtitleBridgeSnapshot>
        {
            [initial.CompositionVersion.Id] = initial,
        };
        LayoutSubtitleBridgeSnapshot parent = initial;
        ContentDigest parentDigest = cached.InitialDigest;
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            StoredSubtitleEdit row = rows[index];
            if (row.ProjectId != initial.ProjectId || row.RootVersionId != initial.CompositionVersion.Id
                || row.Sequence != index + 1 || row.ParentVersionId != parent.CompositionVersion.Id
                || row.GeneratedInputDigest != cached.GeneratedInputDigest || row.ParentSnapshotDigest != parentDigest
                || row.RootPayloadDigest != rootPayloadDigest || row.OccurredAt.Offset != TimeSpan.Zero
                || (index < cached.Edits.Count && (row.RecordDigest != cached.Edits[index].RecordDigest
                    || row.CommitCanonicalJson != cached.Edits[index].CommitCanonicalJson)))
            {
                throw InvalidReplay();
            }

            using JsonDocument operation = JsonDocument.Parse(row.OperationCanonicalJson);
            BridgeRequest request = EditRequest(row.RequestId, parent, operation.RootElement);
            if (!LayoutSubtitleBridgeProtocol.HasValidParameters(request.Method, request.Parameters)) { throw InvalidReplay(); }
            var ids = new Queue<Guid>(row.AllocatedIds);
            LayoutSubtitleBridgeOperationResult evaluated = LayoutSubtitleBridgeRuntime.Evaluate(
                parent, request, row.ActorSid, () => row.OccurredAt, ids.Dequeue, token);
            LayoutSubtitleBridgeSnapshot child = evaluated.Snapshot ?? throw InvalidReplay();
            if (evaluated.Status != LayoutSubtitleBridgeStatus.Available || ids.Count != 0
                || child.CompositionVersion.Id != row.ChildVersionId || child.Revision != checked(parent.Revision + 1)
                || child.CompositionVersion.CreatedAt != row.OccurredAt
                || CompositionSnapshotJson.WriteVersion(child.CompositionVersion) != row.ChildCanonicalJson
                || ComputeSnapshotDigest(child) != row.ResultSnapshotDigest)
            {
                throw InvalidReplay();
            }
            if (cached.Versions.TryGetValue(row.ChildVersionId, out LayoutSubtitleBridgeSnapshot? known))
            {
                if (ComputeSnapshotDigest(known) != row.ResultSnapshotDigest) { throw InvalidReplay(); }
                child = known; // Preserve exact objects, including native composition dictionaries.
            }
            if (!versions.TryAdd(row.ChildVersionId, child)) { throw InvalidReplay(); }
            parent = child;
            parentDigest = row.ResultSnapshotDigest;
        }
        token.ThrowIfCancellationRequested();
        IReadOnlyList<StoredSubtitleEdit> prefix = count == rows.Count ? rows : Array.AsReadOnly(rows.Take(count).ToArray());
        return new Branch(initial, cached.GeneratedInputDigest, cached.InitialDigest, versions, prefix)
        {
            InitialProjection = cached.InitialProjection,
        };
    }

    private static LayoutSubtitleBridgeOperationResult? FindReplay(
        IEnumerable<Branch> candidates, BridgeRequest request, string actorSid)
    {
        foreach (Branch branch in candidates)
        {
            StoredSubtitleEdit? row = branch.Edits.SingleOrDefault(row => row.RequestId == request.RequestId);
            if (row is null) { continue; }
            using JsonDocument operation = JsonDocument.Parse(row.OperationCanonicalJson);
            BridgeRequest original = EditRequest(row.RequestId, branch.Versions[row.ParentVersionId], operation.RootElement);
            if (request.Method != original.Method || !string.Equals(actorSid, row.ActorSid, StringComparison.Ordinal)
                || !SameParameters(request, original))
            {
                return Failure(LayoutSubtitleBridgeStatus.Conflict);
            }
            // An idempotent retry acknowledges precisely this committed prefix,
            // not a later operation. Only latestInBranch reads promise the live head.
            return LayoutSubtitleBridgeOperationResult.Available(branch.Versions[row.ChildVersionId], MetadataFor(branch, row));
        }
        return null;
    }

    private static DurableSubtitleBranchMetadata MetadataFor(Branch branch, StoredSubtitleEdit? row)
    {
        LayoutSubtitleBridgeSnapshot head = row is null ? branch.Initial : branch.Versions[row.ChildVersionId];
        return new(branch.Initial.CompositionVersion.Id, head.CompositionVersion.Id,
            head.CompositionVersion.ContentDigest, row?.Sequence ?? 0, row?.RecordDigest);
    }

    private static BridgeRequest EditRequest(Guid requestId, LayoutSubtitleBridgeSnapshot parent, JsonElement operation) =>
        BridgeRequest.Create(requestId, BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
        {
            projectId = parent.ProjectId.ToString(),
            expectedRevision = parent.Revision,
            expectedVersionId = parent.CompositionVersion.Id.ToString(),
            expectedVersionDigest = parent.CompositionVersion.ContentDigest.Hex,
            operation,
        }));

    private static bool SameParameters(BridgeRequest first, BridgeRequest second) =>
        string.Equals(CanonicalJson.Canonicalize(first.Parameters.GetRawText()),
            CanonicalJson.Canonicalize(second.Parameters.GetRawText()), StringComparison.Ordinal);

    private static void ValidateInitial(LayoutSubtitleBridgeSnapshot initial, CancellationToken token,
        SubtitleEditProjectionEvidence? projection)
    {
        if (initial.Revision != 0 || initial.CompositionVersion.CreatedAt.Offset != TimeSpan.Zero
            || ContentDigest.Compute(CompositionSnapshotJson.WriteElementsObjectToUtf8(initial.CompositionVersion.Elements))
                != initial.CompositionVersion.ContentDigest)
        {
            throw InvalidReplay();
        }
        projection?.ValidateSnapshot(initial, token);
        foreach (LayoutSubtitleCueBinding binding in initial.Subtitles)
        {
            token.ThrowIfCancellationRequested();
            SubtitleCueHistory history = binding.History;
            SubtitleCueRevision revision = history.CurrentRevision;
            if (history.Revisions.Count != 1 || history.Inheritance.Count != 0
                || revision.CompositionVersionId != initial.CompositionVersion.Id || revision.CreatedAt.Offset != TimeSpan.Zero
                || (projection is null && revision.Reason == SubtitleEditProjection.MaterializationReason))
            {
                throw InvalidReplay();
            }
            if (projection is not null)
            {
                SubtitleService.ValidateMaterializedInitial(initial.CompositionVersion, history, projection, token);
                continue;
            }
            SubtitleCueHistory replayed = SubtitleService.CreateInitial(initial.CompositionVersion, history.SourceCandidate,
                revision.Snapshot.LayoutResolution, history.CueId, revision.Id, revision.Snapshot.Cue.Range,
                revision.ActorSid, revision.CreatedAt, revision.Reason, token);
            if (CanonicalObject(HistoryBody(history)) != CanonicalObject(HistoryBody(replayed))) { throw InvalidReplay(); }
        }
    }

    // Initial roots use the same complete snapshot contract as before projection support.
    internal static ContentDigest ComputeInitialDigest(LayoutSubtitleBridgeSnapshot initial) => ComputeSnapshotDigest(initial);

    /// <summary>
    /// Full native evidence, not the element-only composition digest and not {} from
    /// serializing internal bridge properties. The fixed read response covers the
    /// existing wire projection; public history data adds every revision and change,
    /// and explicit projections add internal inheritance and native layout context.
    /// </summary>
    internal static ContentDigest ComputeSnapshotDigest(LayoutSubtitleBridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BridgeRequest read = BridgeRequest.Create(DigestRequestId, BridgeMethod.LayoutRead,
            JsonSerializer.SerializeToElement(new { projectId = snapshot.ProjectId.ToString(), evaluationTime = (object?)null }));
        // Deliberately omit transient branch metadata and wire persistence claims:
        // the original snapshot-v1 evidence must remain stable across later edits.
        using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(
            read, LayoutSubtitleBridgeOperationResult.Available(snapshot)));
        JsonElement result = response.RootElement.GetProperty("result");
        if (result.GetProperty("status").GetString() != "available" || result.GetProperty("snapshot").ValueKind != JsonValueKind.Object)
        {
            // Never hash the serializer's unavailable fallback as valid evidence.
            throw InvalidReplay();
        }
        LayoutSubtitleManualSelection? manual = snapshot.ManualSelection;
        LayoutSubtitleVariantPair? variants = snapshot.VariantPair;
        return Hash(CanonicalObject(new
        {
            schema = "subtitle-edit.snapshot.v1",
            result,
            composition = CompositionSnapshotJson.WriteVersion(snapshot.CompositionVersion),
            compositions = snapshot.CompositionVersions.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                .Select(pair => new { key = pair.Key.ToString(), canonicalPayload = CompositionSnapshotJson.WriteVersion(pair.Value) }).ToArray(),
            snapshot.OutputEnvelope,
            snapshot.SubtitleOnly,
            sources = snapshot.Sources.Select(source => new { source.SourceAssetId, source.Origin, source.DisplayGeometry }).ToArray(),
            snapshot.Templates,
            subtitles = snapshot.Subtitles.Select(binding => new
            {
                history = HistoryBody(binding.History),
                binding.LayoutSpec,
                binding.LayoutProfile,
                binding.ProhibitedRegions,
                binding.FaceRegions,
            }).ToArray(),
            terops = snapshot.Terops.Select(binding => new { binding.History, binding.CurrentTopicMetadata, binding.PlacementContext }).ToArray(),
            displays = snapshot.SubtitleDisplays.Select(binding => new { binding.Profile, binding.SelectedCueIds }).ToArray(),
            manual = manual is null ? null : new { manual.Resolution, manual.Sources, manual.Destinations, manual.Template },
            keyframes = snapshot.Keyframes.Select(binding => new { binding.Track, binding.EvaluationTime, binding.Evaluation }).ToArray(),
            variants = variants is null ? null : new { variants.BurnedIn, variants.SubtitleFree },
        }));
    }

    private static object HistoryBody(SubtitleCueHistory history) => new
    {
        history.SourceCandidate,
        history.ProjectId,
        history.CueId,
        history.Revisions,
        inheritance = history.Inheritance.Select(path => new
        {
            revisionId = path.RevisionId.ToString(),
            versions = path.Versions.Select(CompositionSnapshotJson.WriteVersion).ToArray(),
        }).ToArray(),
    };

    private static string CanonicalObject(object value) => CanonicalJson.Canonicalize(JsonSerializer.Serialize(value));
    private static ContentDigest Hash(string value) => ContentDigest.Compute(Utf8.GetBytes(value));
    private static LayoutSubtitleBridgeOperationResult Failure(LayoutSubtitleBridgeStatus status) => LayoutSubtitleBridgeOperationResult.Failure(status);
    private static InvalidOperationException InvalidReplay() => new("The durable subtitle branch does not match its generated evidence or deterministic replay.");
    private static KeyNotFoundException MissingBranch() => new("No generated subtitle branch is registered for the requested project/version.");

    private sealed record Branch(
        LayoutSubtitleBridgeSnapshot Initial,
        ContentDigest GeneratedInputDigest,
        ContentDigest InitialDigest,
        IReadOnlyDictionary<EntityId<CompositionVersionIdTag>, LayoutSubtitleBridgeSnapshot> Versions,
        IReadOnlyList<StoredSubtitleEdit> Edits)
    {
        internal SubtitleEditProjectionEvidence? InitialProjection { get; init; }
        internal LayoutSubtitleBridgeSnapshot Head => Versions[Edits.Count == 0 ? Initial.CompositionVersion.Id : Edits[^1].ChildVersionId];
    }
}