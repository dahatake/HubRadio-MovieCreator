using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Workflow;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

/// <summary>
/// A completed native subtitle mutation, not an instruction to execute one.
/// ExpectedSequence is the number of committed edits (zero for the first edit).
/// AllocatedIds records the actual runtime allocation order; its first ID is the child.
/// The caller authorizes the operation, validates the typed edit body, proves the
/// generated root/input and initial parent snapshot, and computes strong snapshot
/// digests covering subtitles/revisions, not merely CompositionVersion.ContentDigest.
/// ExpectedRootPayloadDigest pins the complete canonical root, including metadata.
/// </summary>
public sealed record SubtitleEditCommit(
    Guid RequestId,
    EntityId<ProjectIdTag> ProjectId,
    EntityId<CompositionVersionIdTag> RootVersionId,
    EntityId<CompositionVersionIdTag> ParentVersionId,
    CompositionVersion ChildVersion,
    string ChildCanonicalJson,
    int ExpectedSequence,
    ContentDigest GeneratedInputDigest,
    ContentDigest ParentSnapshotDigest,
    ContentDigest ResultSnapshotDigest,
    string OperationCanonicalJson,
    IReadOnlyList<Guid> AllocatedIds,
    string ActorSid,
    DateTimeOffset OccurredAt,
    ContentDigest ExpectedRootPayloadDigest);

/// <summary>
/// Immutable, fully validated native replay data. OperationCanonicalJson contains
/// the original closed editSubtitle envelope, including its opaque typed edit body.
/// PreviousRecordDigest is null only for sequence 1. Payload digests cover complete
/// canonical version JSON; ChildContentDigest covers composition elements only.
/// </summary>
public sealed record StoredSubtitleEdit(
    Guid RequestId,
    EntityId<ProjectIdTag> ProjectId,
    EntityId<CompositionVersionIdTag> RootVersionId,
    EntityId<CompositionVersionIdTag> ParentVersionId,
    EntityId<CompositionVersionIdTag> ChildVersionId,
    int Sequence,
    ContentDigest GeneratedInputDigest,
    ContentDigest ParentSnapshotDigest,
    ContentDigest ResultSnapshotDigest,
    ContentDigest ChildContentDigest,
    ContentDigest RootPayloadDigest,
    ContentDigest ChildPayloadDigest,
    string ChildCanonicalJson,
    string OperationCanonicalJson,
    IReadOnlyList<Guid> AllocatedIds,
    string ActorSid,
    DateTimeOffset OccurredAt,
    string CommitCanonicalJson,
    ContentDigest CommitDigest,
    ContentDigest? PreviousRecordDigest,
    ContentDigest RecordDigest)
{
    public int ExpectedSequence => Sequence - 1;
}

/// <summary>
/// The validated root and complete immutable journal from one SQLite snapshot,
/// including the canonical root payload when no edit has been committed yet.
/// </summary>
public sealed record StoredSubtitleEditBranchState(
    StoredCompositionVersion RootVersion,
    IReadOnlyList<StoredSubtitleEdit> Edits);

/// <summary>
/// App-free append-only journal, one linear branch per generated root version.
/// All reads use a single SQLite snapshot and validate every row and bound child.
/// Historical reads intentionally do not inspect current project rights/settings;
/// authorization and deterministic runtime replay belong to the native caller.
/// This store neither migrates the database nor updates a working copy or Jobs.
/// </summary>
public sealed class SubtitleEditStore
{
    public const int MaximumOperationUtf8Bytes = 64 * 1024;
    public const int MaximumChildUtf8Bytes = 4 * 1024 * 1024;
    public const int MaximumCommitUtf8Bytes = 5 * 1024 * 1024;
    public const int MaximumAllocatedIds = 8;
    // Branch-local budget, not a guarantee about pre-existing root ancestors.
    // The caller must also respect SourceOnlyExecutionStore.MaximumVersionLineageLength.
    public const int MaximumBranchEdits = 128;
    // Sum of persisted commit_payload BLOB bytes (including embedded child JSON).
    // Replay retains both full commit JSON and extracted child/operation JSON.
    public const int MaximumBranchPayloadBytes = 32 * 1024 * 1024;
    private const string EntityKind = "subtitle edit";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _databasePath;

    public SubtitleEditStore(string databasePath) =>
        _databasePath = SqliteStoreSupport.Initialize(databasePath);

    /// <summary>
    /// Returns the complete branch in sequence order, or empty for an existing unused root.
    /// Rejects branches exceeding the row or cumulative payload safety budget; never truncates.
    /// </summary>
    public IReadOnlyList<StoredSubtitleEdit> ReadBranch(
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> rootVersionId) =>
        ReadBranchState(projectId, rootVersionId).Edits;

    /// <summary>
    /// Returns the validated canonical root and full branch in a single read transaction.
    /// Empty branches still validate the root payload and its project/ancestry bindings.
    /// </summary>
    public StoredSubtitleEditBranchState ReadBranchState(
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> rootVersionId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(rootVersionId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        StoredSubtitleEditBranchState branch = ReadBranchState(connection, transaction, projectId, rootVersionId);
        transaction.Commit();
        return branch;
    }

    /// <summary>
    /// Inserts the new composition and journal record under one BEGIN IMMEDIATE.
    /// An exact request-ID replay returns its original record even after later edits.
    /// Different bytes for an existing request ID conflict. A stale sequence raises
    /// StoreConcurrencyException; invalid bindings raise InvalidOperationException.
    /// Equal elements digests never imply NoChange: subtitle state has its own digest.
    /// The proposed commit must fit the branch budgets before any child is inserted.
    /// Exact replays consume no additional budget, including at either maximum.
    /// The expected full root payload is checked inside the write transaction and
    /// must also match the recorded root_payload_digest on an exact request replay.
    /// </summary>
    public StoredSubtitleEdit Commit(SubtitleEditCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        // Detach the caller's mutable allocation list before validation/serialization.
        ArgumentNullException.ThrowIfNull(commit.AllocatedIds);
        if (commit.AllocatedIds.Count is < 1 or > MaximumAllocatedIds) { throw Invalid(); }
        commit = commit with { AllocatedIds = Array.AsReadOnly(commit.AllocatedIds.ToArray()) };
        string canonical = Prepare(commit);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        // Locate by request ID globally, not just in the caller's proposed branch.
        // Revalidate the entire ORIGINAL branch before considering the replay exact.
        (EntityId<ProjectIdTag> Project, EntityId<CompositionVersionIdTag> Root)? existing = null;
        using (SqliteCommand lookup = Command(connection, transaction,
            "SELECT project_id, root_version_id FROM subtitle_edits WHERE request_id = $request;"))
        {
            SqliteStoreSupport.AddText(lookup, "$request", commit.RequestId.ToString("D"));
            using SqliteDataReader reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                existing = (SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 0, EntityKind),
                    SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 1, EntityKind));
                if (reader.Read()) { throw Invalid(); }
            }
        }
        if (existing is { } original)
        {
            StoredSubtitleEdit recorded = ReadBranchState(connection, transaction, original.Project, original.Root).Edits
                .Single(row => row.RequestId == commit.RequestId);
            // The existing root_payload_digest is the persisted expectation; keep
            // the schema/commit envelope unchanged, but compare BOTH on replay.
            if (recorded.RootPayloadDigest != commit.ExpectedRootPayloadDigest ||
                !string.Equals(recorded.CommitCanonicalJson, canonical, StringComparison.Ordinal)) { throw Conflict(); }
            transaction.Commit();
            return recorded;
        }

        StoredSubtitleEditBranchState state = ReadBranchState(connection, transaction, commit.ProjectId, commit.RootVersionId,
            proposedCommitUtf8Bytes: Utf8.GetByteCount(canonical));
        StoredCompositionVersion root = state.RootVersion;
        ContentDigest rootPayloadDigest = Hash(root.CanonicalJson);
        if (commit.ExpectedRootPayloadDigest != rootPayloadDigest) { throw Conflict(); }
        IReadOnlyList<StoredSubtitleEdit> branch = state.Edits;
        if (commit.ExpectedSequence != branch.Count)
        {
            throw new StoreConcurrencyException(EntityKind, commit.RootVersionId.ToString(), commit.ExpectedSequence, branch.Count);
        }
        StoredSubtitleEdit? previous = branch.LastOrDefault();
        StoredCompositionVersion parent = previous is null ? root :
            ReadVersion(connection, transaction, commit.ProjectId, previous.ChildVersionId);
        if (commit.ParentVersionId != parent.Id || commit.OccurredAt < parent.CreatedAt ||
            (previous is not null && (commit.GeneratedInputDigest != previous.GeneratedInputDigest ||
                commit.ParentSnapshotDigest != previous.ResultSnapshotDigest))) { throw Conflict(); }
        EnsureSubtitleOnly(parent, commit.ChildCanonicalJson, commit.ChildVersion.ContentDigest);
        ValidateAllocations(connection, transaction, commit.RequestId, commit.ChildVersion.Id, commit.AllocatedIds);
        using (SqliteCommand duplicate = Command(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM composition_versions WHERE id = $child);"))
        {
            SqliteStoreSupport.AddText(duplicate, "$child", commit.ChildVersion.Id.ToString());
            if (duplicate.ExecuteScalar() is not long count || count != 0) { throw Conflict(); }
        }

        int sequence = checked(commit.ExpectedSequence + 1);
        ContentDigest childPayloadDigest = Hash(commit.ChildCanonicalJson);
        ContentDigest commitDigest = Hash(canonical);
        ContentDigest recordDigest = RecordDigest(sequence, commitDigest, rootPayloadDigest,
            childPayloadDigest, previous?.RecordDigest);
        using (SqliteCommand child = Command(connection, transaction, """
            INSERT INTO composition_versions (id, project_id, parent_version_id, content_digest, created_at, payload)
            VALUES ($child, $project, $parent, $content, $at, $payload);
            """))
        {
            SqliteStoreSupport.AddText(child, "$child", commit.ChildVersion.Id.ToString());
            SqliteStoreSupport.AddText(child, "$project", commit.ProjectId.ToString());
            SqliteStoreSupport.AddText(child, "$parent", commit.ParentVersionId.ToString());
            SqliteStoreSupport.AddText(child, "$content", commit.ChildVersion.ContentDigest.Hex);
            SqliteStoreSupport.AddText(child, "$at", Timestamp(commit.OccurredAt));
            SqliteStoreSupport.AddBlob(child, "$payload", Utf8.GetBytes(commit.ChildCanonicalJson));
            if (child.ExecuteNonQuery() != 1) { throw Invalid(); }
        }
        using (SqliteCommand journal = Command(connection, transaction, """
            INSERT INTO subtitle_edits (
                request_id, project_id, root_version_id, parent_version_id, child_version_id, sequence,
                generated_input_digest, parent_snapshot_digest, result_snapshot_digest, child_content_digest,
                root_payload_digest, child_payload_digest, actor_sid, occurred_at, commit_payload,
                commit_digest, previous_record_digest, record_digest)
            VALUES ($request, $project, $root, $parent, $child, $sequence, $generated, $parent_snapshot,
                $result_snapshot, $content, $root_payload, $child_payload, $actor, $at, $payload,
                $commit_digest, $previous, $record_digest);
            """))
        {
            SqliteStoreSupport.AddText(journal, "$request", commit.RequestId.ToString("D"));
            SqliteStoreSupport.AddText(journal, "$project", commit.ProjectId.ToString());
            SqliteStoreSupport.AddText(journal, "$root", commit.RootVersionId.ToString());
            SqliteStoreSupport.AddText(journal, "$parent", commit.ParentVersionId.ToString());
            SqliteStoreSupport.AddText(journal, "$child", commit.ChildVersion.Id.ToString());
            SqliteStoreSupport.AddInteger(journal, "$sequence", sequence);
            SqliteStoreSupport.AddText(journal, "$generated", commit.GeneratedInputDigest.Hex);
            SqliteStoreSupport.AddText(journal, "$parent_snapshot", commit.ParentSnapshotDigest.Hex);
            SqliteStoreSupport.AddText(journal, "$result_snapshot", commit.ResultSnapshotDigest.Hex);
            SqliteStoreSupport.AddText(journal, "$content", commit.ChildVersion.ContentDigest.Hex);
            SqliteStoreSupport.AddText(journal, "$root_payload", rootPayloadDigest.Hex);
            SqliteStoreSupport.AddText(journal, "$child_payload", childPayloadDigest.Hex);
            SqliteStoreSupport.AddText(journal, "$actor", commit.ActorSid);
            SqliteStoreSupport.AddText(journal, "$at", Timestamp(commit.OccurredAt));
            SqliteStoreSupport.AddBlob(journal, "$payload", Utf8.GetBytes(canonical));
            SqliteStoreSupport.AddText(journal, "$commit_digest", commitDigest.Hex);
            SqliteStoreSupport.AddNullableText(journal, "$previous", previous?.RecordDigest.Hex);
            SqliteStoreSupport.AddText(journal, "$record_digest", recordDigest.Hex);
            if (journal.ExecuteNonQuery() != 1) { throw Invalid(); }
        }

        // An AFTER INSERT trigger or damaged dependency must not produce a false success.
        IReadOnlyList<StoredSubtitleEdit> verified = ReadBranchState(connection, transaction, commit.ProjectId, commit.RootVersionId).Edits;
        if (verified.Count != sequence || verified[^1].CommitCanonicalJson != canonical ||
            verified[^1].RecordDigest != recordDigest) { throw Invalid(); }
        transaction.Commit();
        return verified[^1];
    }

    internal static void ValidateAll(string databasePath)
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = Command(connection, transaction,
            "SELECT DISTINCT project_id, root_version_id FROM subtitle_edits ORDER BY project_id, root_version_id;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            _ = ReadBranchState(connection, transaction,
                SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 0, EntityKind),
                SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 1, EntityKind));
        }
        transaction.Commit();
    }

    private static StoredSubtitleEditBranchState ReadBranchState(SqliteConnection connection, SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> rootVersionId, int proposedCommitUtf8Bytes = 0)
    {
        try
        {
            // Read only aggregate lengths before materializing ANY branch payload.
            // The same snapshot (or BEGIN IMMEDIATE for writes) covers this check,
            // full validation, child/journal insertion and post-write validation.
            ValidateBranchBudget(connection, transaction, projectId, rootVersionId, proposedCommitUtf8Bytes);
            StoredCompositionVersion root = ReadVersion(connection, transaction, projectId, rootVersionId);
            using (SqliteCommand binding = Command(connection, transaction, """
                SELECT EXISTS (SELECT 1 FROM projects WHERE id = $project)
                    AND NOT EXISTS (SELECT 1 FROM subtitle_edits WHERE child_version_id = $root);
                """))
            {
                SqliteStoreSupport.AddText(binding, "$project", projectId.ToString());
                SqliteStoreSupport.AddText(binding, "$root", rootVersionId.ToString());
                if (binding.ExecuteScalar() is not long valid || valid != 1) { throw Invalid(); }
            }
            ContentDigest rootDigest = Hash(root.CanonicalJson);
            var branch = new List<StoredSubtitleEdit>();
            var requests = new HashSet<Guid>();
            var versions = new HashSet<EntityId<CompositionVersionIdTag>> { root.Id };
            StoredCompositionVersion parent = root;
            StoredSubtitleEdit? previous = null;
            using SqliteCommand command = Command(connection, transaction, """
                SELECT request_id, project_id, root_version_id, parent_version_id, child_version_id, sequence,
                    generated_input_digest, parent_snapshot_digest, result_snapshot_digest, child_content_digest,
                    root_payload_digest, child_payload_digest, actor_sid, occurred_at, length(commit_payload),
                    commit_payload, commit_digest, previous_record_digest, record_digest
                FROM subtitle_edits WHERE project_id = $project AND root_version_id = $root ORDER BY sequence;
                """);
            SqliteStoreSupport.AddText(command, "$project", projectId.ToString());
            SqliteStoreSupport.AddText(command, "$root", rootVersionId.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                StoredSubtitleEdit row = ReadRow(reader);
                if (row.ProjectId != projectId || row.RootVersionId != rootVersionId ||
                    row.Sequence != checked(branch.Count + 1) || row.ParentVersionId != parent.Id ||
                    row.RootPayloadDigest != rootDigest || row.OccurredAt < parent.CreatedAt ||
                    row.PreviousRecordDigest != previous?.RecordDigest || !requests.Add(row.RequestId) ||
                    !versions.Add(row.ChildVersionId) ||
                    (previous is not null && (row.GeneratedInputDigest != previous.GeneratedInputDigest ||
                        row.ParentSnapshotDigest != previous.ResultSnapshotDigest))) { throw Invalid(); }
                StoredCompositionVersion child = ReadVersion(connection, transaction, projectId, row.ChildVersionId);
                if (child.ParentVersionId != parent.Id || child.CreatedAt != row.OccurredAt ||
                    child.ContentDigest != row.ChildContentDigest || child.CanonicalJson != row.ChildCanonicalJson ||
                    Hash(child.CanonicalJson) != row.ChildPayloadDigest) { throw Invalid(); }
                EnsureSubtitleOnly(parent, row.ChildCanonicalJson, row.ChildContentDigest);
                ValidateAllocations(connection, transaction, row.RequestId, row.ChildVersionId, row.AllocatedIds);
                branch.Add(row);
                previous = row;
                parent = child;
            }
            return new StoredSubtitleEditBranchState(root, branch.AsReadOnly());
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or
            InvalidOperationException or OverflowException or KeyNotFoundException or SqliteException)
        {
            throw Invalid();
        }
    }

    /// <summary>Validates exactly the captured prefix on the caller's transaction, never requiring the latest head.</summary>
    internal static IReadOnlyList<StoredSubtitleEdit> ReadCapturedPrefix(SqliteConnection connection, SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> rootVersionId, int sequence)
    {
        try
        {
            if (sequence is < 1 or > MaximumBranchEdits) { throw Invalid(); }
            using (SqliteCommand budget = Command(connection, transaction, """
                SELECT COUNT(*), COALESCE(SUM(length(commit_payload)), 0),
                    COUNT(CASE WHEN typeof(commit_payload) <> 'blob'
                        OR length(commit_payload) NOT BETWEEN 1 AND $maximum THEN 1 END)
                FROM subtitle_edits WHERE root_version_id = $root AND sequence <= $sequence;
                """))
            {
                SqliteStoreSupport.AddText(budget, "$root", rootVersionId.ToString());
                SqliteStoreSupport.AddInteger(budget, "$sequence", sequence);
                SqliteStoreSupport.AddInteger(budget, "$maximum", MaximumCommitUtf8Bytes);
                using SqliteDataReader counts = budget.ExecuteReader();
                if (!counts.Read() || counts.GetInt64(0) != sequence ||
                    counts.GetInt64(1) is <= 0 or > MaximumBranchPayloadBytes || counts.GetInt64(2) != 0) { throw Invalid(); }
            }
            StoredCompositionVersion root = ReadVersion(connection, transaction, projectId, rootVersionId);
            using (SqliteCommand binding = Command(connection, transaction,
                "SELECT EXISTS (SELECT 1 FROM subtitle_edits WHERE child_version_id = $root);"))
            {
                SqliteStoreSupport.AddText(binding, "$root", rootVersionId.ToString());
                if (binding.ExecuteScalar() is not long count || count != 0) { throw Invalid(); }
            }
            ContentDigest rootDigest = Hash(root.CanonicalJson);
            var prefix = new List<StoredSubtitleEdit>();
            var requests = new HashSet<Guid>();
            var versions = new HashSet<EntityId<CompositionVersionIdTag>> { rootVersionId };
            StoredCompositionVersion parent = root;
            StoredSubtitleEdit? previous = null;
            using SqliteCommand command = Command(connection, transaction, """
                SELECT request_id, project_id, root_version_id, parent_version_id, child_version_id, sequence,
                    generated_input_digest, parent_snapshot_digest, result_snapshot_digest, child_content_digest,
                    root_payload_digest, child_payload_digest, actor_sid, occurred_at, length(commit_payload),
                    commit_payload, commit_digest, previous_record_digest, record_digest
                FROM subtitle_edits WHERE root_version_id = $root AND sequence <= $sequence ORDER BY sequence;
                """);
            SqliteStoreSupport.AddText(command, "$root", rootVersionId.ToString());
            SqliteStoreSupport.AddInteger(command, "$sequence", sequence);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                StoredSubtitleEdit row = ReadRow(reader);
                if (row.ProjectId != projectId || row.RootVersionId != rootVersionId || row.Sequence != prefix.Count + 1 ||
                    row.ParentVersionId != parent.Id || row.RootPayloadDigest != rootDigest || row.OccurredAt < parent.CreatedAt ||
                    row.PreviousRecordDigest != previous?.RecordDigest || !requests.Add(row.RequestId) || !versions.Add(row.ChildVersionId) ||
                    (previous is not null && (row.GeneratedInputDigest != previous.GeneratedInputDigest ||
                        row.ParentSnapshotDigest != previous.ResultSnapshotDigest))) { throw Invalid(); }
                StoredCompositionVersion child = ReadVersion(connection, transaction, projectId, row.ChildVersionId);
                if (child.ParentVersionId != parent.Id || child.CreatedAt != row.OccurredAt ||
                    child.ContentDigest != row.ChildContentDigest || child.CanonicalJson != row.ChildCanonicalJson) { throw Invalid(); }
                EnsureSubtitleOnly(parent, row.ChildCanonicalJson, row.ChildContentDigest);
                ValidateAllocations(connection, transaction, row.RequestId, row.ChildVersionId, row.AllocatedIds);
                prefix.Add(row);
                previous = row;
                parent = child;
            }
            if (prefix.Count != sequence) { throw Invalid(); }
            return prefix.AsReadOnly();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or
            InvalidOperationException or OverflowException or KeyNotFoundException or SqliteException)
        { throw Invalid(); }
    }

    private static void ValidateBranchBudget(SqliteConnection connection, SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> rootVersionId, int proposedCommitUtf8Bytes)
    {
        if (proposedCommitUtf8Bytes is < 0 or > MaximumCommitUtf8Bytes) { throw Invalid(); }
        using SqliteCommand command = Command(connection, transaction, """
            SELECT COUNT(*), COALESCE(SUM(length(commit_payload)), 0),
                COUNT(CASE WHEN typeof(commit_payload) <> 'blob'
                    OR length(commit_payload) NOT BETWEEN 1 AND $maximum THEN 1 END)
            FROM subtitle_edits WHERE project_id = $project AND root_version_id = $root;
            """);
        SqliteStoreSupport.AddText(command, "$project", projectId.ToString());
        SqliteStoreSupport.AddText(command, "$root", rootVersionId.ToString());
        SqliteStoreSupport.AddInteger(command, "$maximum", MaximumCommitUtf8Bytes);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { throw Invalid(); }
        long count = SqliteStoreSupport.ReadInt64(reader, 0, EntityKind);
        long bytes = SqliteStoreSupport.ReadInt64(reader, 1, EntityKind);
        int proposedEdits = proposedCommitUtf8Bytes == 0 ? 0 : 1;
        if (count < 0 || count > MaximumBranchEdits - proposedEdits || bytes < 0 ||
            bytes > MaximumBranchPayloadBytes - (long)proposedCommitUtf8Bytes ||
            SqliteStoreSupport.ReadInt64(reader, 2, EntityKind) != 0 || reader.Read()) { throw Invalid(); }
    }

    private static StoredSubtitleEdit ReadRow(SqliteDataReader reader)
    {
        string json = ReadJson(reader, 14, 15, MaximumCommitUtf8Bytes);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement body = document.RootElement;
        string childJson = body.GetProperty("childVersion").GetRawText();
        string operation = body.GetProperty("operation").GetRawText();
        _ = RequireCanonical(childJson, MaximumChildUtf8Bytes);
        ValidateOperation(operation);
        JsonElement allocations = body.GetProperty("allocatedIds");
        if (allocations.ValueKind != JsonValueKind.Array || allocations.GetArrayLength() is < 1 or > MaximumAllocatedIds)
        { throw Invalid(); }
        Guid[] ids = allocations.EnumerateArray().Select(item => GuidId(item.GetString() ?? throw Invalid())).ToArray();
        var row = new StoredSubtitleEdit(
            GuidId(SqliteStoreSupport.ReadString(reader, 0, EntityKind)),
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, EntityKind),
            SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 2, EntityKind),
            SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 3, EntityKind),
            SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 4, EntityKind),
            SqliteStoreSupport.ReadInt32(reader, 5, EntityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 6, EntityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 7, EntityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 8, EntityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 9, EntityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 10, EntityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 11, EntityKind),
            childJson, operation, Array.AsReadOnly(ids),
            SqliteStoreSupport.ReadString(reader, 12, EntityKind),
            ParseTimestamp(SqliteStoreSupport.ReadString(reader, 13, EntityKind)), json,
            SqliteStoreSupport.ReadContentDigest(reader, 16, EntityKind),
            SqliteStoreSupport.ReadNullableContentDigest(reader, 17, EntityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 18, EntityKind));
        _ = QuickStartActor.User(row.ActorSid);
        ValidateIds(row.RequestId, row.RootVersionId, row.ParentVersionId, row.ChildVersionId, row.AllocatedIds);
        if (row.Sequence <= 0 || row.ParentSnapshotDigest == row.ResultSnapshotDigest ||
            Integer(body.GetProperty("expectedSequence")) != row.ExpectedSequence ||
            row.CommitDigest != Hash(json) || row.ChildPayloadDigest != Hash(childJson) ||
            row.RecordDigest != RecordDigest(row.Sequence, row.CommitDigest, row.RootPayloadDigest,
                row.ChildPayloadDigest, row.PreviousRecordDigest) ||
            json != CommitJson(row.RequestId, row.ProjectId, row.RootVersionId, row.ParentVersionId,
                childJson, row.ExpectedSequence, row.GeneratedInputDigest, row.ParentSnapshotDigest,
                row.ResultSnapshotDigest, operation, row.AllocatedIds, row.ActorSid, row.OccurredAt)) { throw Invalid(); }
        // Regeneration of the entire closed commit envelope above rejects unknown,
        // missing, duplicated, differently typed and noncanonical header fields.
        _ = SourceOnlyCompositionVersionReader.Validate(childJson, row.ChildVersionId, row.ProjectId,
            row.ParentVersionId, row.ChildContentDigest, row.OccurredAt);
        ValidateChildActor(childJson, row.ActorSid);
        return row;
    }

    private static string Prepare(SubtitleEditCommit commit)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(commit.ProjectId);
            ArgumentNullException.ThrowIfNull(commit.RootVersionId);
            ArgumentNullException.ThrowIfNull(commit.ParentVersionId);
            ArgumentNullException.ThrowIfNull(commit.ChildVersion);
            ArgumentNullException.ThrowIfNull(commit.GeneratedInputDigest);
            ArgumentNullException.ThrowIfNull(commit.ParentSnapshotDigest);
            ArgumentNullException.ThrowIfNull(commit.ResultSnapshotDigest);
            ArgumentNullException.ThrowIfNull(commit.ExpectedRootPayloadDigest);
            _ = QuickStartActor.User(commit.ActorSid);
            CompositionVersion child = commit.ChildVersion;
            ValidateIds(commit.RequestId, commit.RootVersionId, commit.ParentVersionId, child.Id, commit.AllocatedIds);
            if (commit.ExpectedSequence is < 0 or int.MaxValue || commit.OccurredAt.Offset != TimeSpan.Zero ||
                child.CreatedAt.Offset != TimeSpan.Zero || child.CreatedAt != commit.OccurredAt ||
                child.ProjectId != commit.ProjectId || child.ParentVersionId != commit.ParentVersionId ||
                child.CreatedByActorId != commit.ActorSid || commit.ParentSnapshotDigest == commit.ResultSnapshotDigest)
            { throw Invalid(); }
            _ = RequireCanonical(commit.ChildCanonicalJson, MaximumChildUtf8Bytes);
            ValidateOperation(commit.OperationCanonicalJson);
            _ = SourceOnlyCompositionVersionReader.Validate(commit.ChildCanonicalJson, child.Id, child.ProjectId,
                child.ParentVersionId, child.ContentDigest, child.CreatedAt);
            ValidateChildActor(commit.ChildCanonicalJson, commit.ActorSid);
            // Validate the supplied Core object as well as its independently supplied
            // payload. Metadata-only comparisons would miss different typed elements.
            if (commit.ChildCanonicalJson != VersionJson(child)) { throw Invalid(); }
            return CommitJson(commit.RequestId, commit.ProjectId, commit.RootVersionId, commit.ParentVersionId,
                commit.ChildCanonicalJson, commit.ExpectedSequence, commit.GeneratedInputDigest,
                commit.ParentSnapshotDigest, commit.ResultSnapshotDigest, commit.OperationCanonicalJson,
                commit.AllocatedIds, commit.ActorSid, commit.OccurredAt);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or
            InvalidOperationException or OverflowException or KeyNotFoundException)
        {
            throw Invalid();
        }
    }

    private static StoredCompositionVersion ReadVersion(SqliteConnection connection, SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> id)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT id, project_id, parent_version_id, content_digest, created_at, length(payload), payload,
                parent_version_id IS NULL OR EXISTS (
                    SELECT 1 FROM composition_versions p WHERE p.id = v.parent_version_id AND p.project_id = v.project_id)
            FROM composition_versions v WHERE id = $id;
            """);
        SqliteStoreSupport.AddText(command, "$id", id.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 0, EntityKind) != id ||
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, EntityKind) != projectId ||
            SqliteStoreSupport.ReadInt64(reader, 7, EntityKind) != 1) { throw Invalid(); }
        EntityId<CompositionVersionIdTag>? parent = SqliteStoreSupport.ReadNullableEntityId<CompositionVersionIdTag>(reader, 2, EntityKind);
        ContentDigest digest = SqliteStoreSupport.ReadContentDigest(reader, 3, EntityKind);
        DateTimeOffset createdAt = ParseTimestamp(SqliteStoreSupport.ReadString(reader, 4, EntityKind));
        string json = ReadJson(reader, 5, 6, MaximumChildUtf8Bytes);
        _ = SourceOnlyCompositionVersionReader.Validate(json, id, projectId, parent, digest, createdAt);
        if (reader.Read()) { throw Invalid(); }
        return new(id, projectId, parent, digest, createdAt, json);
    }

    private static void EnsureSubtitleOnly(StoredCompositionVersion parent, string childJson, ContentDigest childDigest)
    {
        using JsonDocument oldDocument = JsonDocument.Parse(parent.CanonicalJson);
        using JsonDocument newDocument = JsonDocument.Parse(childJson);
        if (parent.ContentDigest != childDigest ||
            oldDocument.RootElement.GetProperty("elements").GetRawText() !=
                newDocument.RootElement.GetProperty("elements").GetRawText()) { throw Conflict(); }
    }

    private static void ValidateIds(Guid request, EntityId<CompositionVersionIdTag> root,
        EntityId<CompositionVersionIdTag> parent, EntityId<CompositionVersionIdTag> child, IReadOnlyList<Guid> ids)
    {
        if (request == Guid.Empty || root == child || parent == child || ids.Count is < 1 or > MaximumAllocatedIds ||
            ids[0] != child.Value || ids.Any(id => id == Guid.Empty || id == root.Value || id == parent.Value) ||
            ids.Distinct().Count() != ids.Count) { throw Invalid(); }
    }

    private static void ValidateAllocations(SqliteConnection connection, SqliteTransaction transaction,
        Guid request, EntityId<CompositionVersionIdTag> child, IReadOnlyList<Guid> ids)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS (
                SELECT 1 FROM subtitle_edits e, json_each(CAST(e.commit_payload AS TEXT), '$.allocatedIds') old,
                    json_each($ids) proposed
                WHERE e.request_id <> $request AND old.value = proposed.value)
                OR EXISTS (SELECT 1 FROM composition_versions v JOIN json_each($ids) proposed ON v.id = proposed.value
                    WHERE v.id <> $child);
            """);
        SqliteStoreSupport.AddText(command, "$request", request.ToString("D"));
        SqliteStoreSupport.AddText(command, "$child", child.ToString());
        SqliteStoreSupport.AddText(command, "$ids", JsonSerializer.Serialize(ids.Select(id => id.ToString("D")).ToArray()));
        if (command.ExecuteScalar() is not long conflicts || conflicts != 0) { throw Conflict(); }
    }

    private static void ValidateOperation(string json)
    {
        _ = RequireCanonical(json, MaximumOperationUtf8Bytes);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement operation = document.RootElement;
        string[] fields = ["kind", "cueId", "expectedRevisionId", "expectedRevisionDigest", "reason", "edit"];
        if (operation.EnumerateObject().Count() != fields.Length || fields.Any(name => !operation.TryGetProperty(name, out _)) ||
            Text(operation, "kind") != "editSubtitle" || operation.GetProperty("edit").ValueKind != JsonValueKind.Object)
        { throw Invalid(); }
        _ = GuidId(Text(operation, "cueId"));
        _ = GuidId(Text(operation, "expectedRevisionId"));
        string digest = Text(operation, "expectedRevisionDigest");
        if (!ContentDigest.TryParse(digest, out ContentDigest? parsed) || parsed.Hex != digest) { throw Invalid(); }
        string reason = Text(operation, "reason");
        if (string.IsNullOrWhiteSpace(reason) || reason != reason.Trim() || reason.Contains('\0')) { throw Invalid(); }
        // edit is intentionally opaque here; only the native typed runtime can
        // validate cue-specific text/timing/line-break/placement semantics.
    }

    private static void ValidateChildActor(string childJson, string actor)
    {
        using JsonDocument child = JsonDocument.Parse(childJson);
        if (Text(child.RootElement, "createdByActorId") != actor) { throw Invalid(); }
        // The runtime's fixed version changeReason is not the user's operation reason.
        // ValidateOperation checks the latter independently; VersionJson preserves
        // exact Core/payload equality, including the former.
    }

    private static string CommitJson(Guid request, EntityId<ProjectIdTag> project,
        EntityId<CompositionVersionIdTag> root, EntityId<CompositionVersionIdTag> parent,
        string childJson, int expectedSequence, ContentDigest generated, ContentDigest parentSnapshot,
        ContentDigest resultSnapshot, string operationJson, IReadOnlyList<Guid> ids, string actor, DateTimeOffset at)
    {
        using JsonDocument child = JsonDocument.Parse(childJson);
        using JsonDocument operation = JsonDocument.Parse(operationJson);
        string canonical = CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "subtitle-edit.commit.v1",
            requestId = request.ToString("D"),
            projectId = project.ToString(),
            rootVersionId = root.ToString(),
            parentVersionId = parent.ToString(),
            childVersion = child.RootElement,
            expectedSequence,
            generatedInputDigest = generated.Hex,
            parentSnapshotDigest = parentSnapshot.Hex,
            resultSnapshotDigest = resultSnapshot.Hex,
            operation = operation.RootElement,
            allocatedIds = ids.Select(id => id.ToString("D")).ToArray(),
            actorSid = actor,
            occurredAt = Timestamp(at),
        }));
        return RequireCanonical(canonical, MaximumCommitUtf8Bytes);
    }

    private static ContentDigest RecordDigest(int sequence, ContentDigest commit, ContentDigest root,
        ContentDigest child, ContentDigest? previous) => Hash(CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "subtitle-edit.record.v1",
            sequence,
            commitDigest = commit.Hex,
            rootPayloadDigest = root.Hex,
            childPayloadDigest = child.Hex,
            previousRecordDigest = previous?.Hex,
        })));

    private static string VersionJson(CompositionVersion version)
    {
        var body = new Dictionary<string, object>
        {
            ["id"] = version.Id.ToString(),
            ["projectId"] = version.ProjectId.ToString(),
            ["createdByActorId"] = version.CreatedByActorId,
            ["createdAt"] = Timestamp(version.CreatedAt),
            ["changeReason"] = version.ChangeReason,
            ["contentDigest"] = version.ContentDigest.Hex,
            ["elements"] = version.Elements.Select(ElementBody).ToArray(),
        };
        if (version.ParentVersionId is not null) { body["parentVersionId"] = version.ParentVersionId.ToString(); }
        if (version.Evidence is CompositionEvidenceEnvelope evidence)
        {
            body["evidence"] = new { kind = evidence.Kind, contentDigest = evidence.ContentDigest.Hex, canonicalJson = evidence.CanonicalJson };
        }
        return CanonicalJson.Canonicalize(JsonSerializer.Serialize(body));
    }

    private static object ElementBody(CompositionElement element)
    {
        var body = new Dictionary<string, object>
        {
            ["id"] = element.Id.ToString(),
            ["outputRange"] = RangeBody(element.OutputRange),
            ["origin"] = OriginBody(element.Origin),
        };
        if (element.SourceRange is TimeRange source) { body["sourceRange"] = RangeBody(source); }
        if (element.AdoptedSuggestion is { } adopted)
        {
            body["adoptedSuggestion"] = new { suggestionId = adopted.SuggestionId.ToString(), origin = OriginBody(adopted.Origin) };
        }
        return body;
    }

    private static object RangeBody(TimeRange range) => new
    {
        start = new { value = range.Start.Value, rate = range.Start.Rate },
        duration = new { value = range.Duration.Value, rate = range.Duration.Rate },
    };

    private static object OriginBody(OriginRef origin) => origin.Kind switch
    {
        OriginKind.SourceSpan => new { kind = "sourceSpan", sourceSpanId = origin.SourceSpanId!.ToString() },
        OriginKind.Asset => new { kind = "asset", assetId = origin.AssetId!.ToString() },
        OriginKind.EditorialRecord => new { kind = "editorialRecord", editorialRecordId = origin.EditorialRecordId!.ToString() },
        _ => throw Invalid(),
    };

    private static string RequireCanonical(string json, int maximum)
    {
        if (Utf8.GetByteCount(json) is 0 || Utf8.GetByteCount(json) > maximum) { throw Invalid(); }
        byte[] canonical = SqliteStoreSupport.CanonicalizePayload(json, nameof(json));
        if (canonical.Length > maximum || Utf8.GetString(canonical) != json) { throw Invalid(); }
        return json;
    }

    private static string ReadJson(SqliteDataReader reader, int lengthOrdinal, int payloadOrdinal, int maximum)
    {
        long length = SqliteStoreSupport.ReadInt64(reader, lengthOrdinal, EntityKind);
        if (length <= 0 || length > maximum) { throw Invalid(); }
        return RequireCanonical(SqliteStoreSupport.ReadCanonicalPayload(reader, payloadOrdinal, EntityKind), maximum);
    }

    private static long Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number &&
        long.TryParse(value.GetRawText(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out long result) ? result : throw Invalid();

    private static Guid GuidId(string value) => Guid.TryParseExact(value, "D", out Guid id) &&
        id != Guid.Empty && id.ToString("D") == value ? id : throw Invalid();

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.TryParseExact(value, "O",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at) && at.Offset == TimeSpan.Zero &&
        Timestamp(at) == value ? at : throw Invalid();

    private static string Text(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.String ? value.GetProperty(name).GetString()! : throw Invalid();
    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static ContentDigest Hash(string json) => ContentDigest.Compute(Utf8.GetBytes(json));
    private static InvalidOperationException Invalid() => SqliteStoreSupport.InvalidStoredData(EntityKind);
    private static InvalidOperationException Conflict() => new("The subtitle edit conflicts with the immutable branch or request.");
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}