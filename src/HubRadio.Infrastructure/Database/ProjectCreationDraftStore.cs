using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class StoredProjectCreationDraft
{
    internal StoredProjectCreationDraft(
        EntityId<ProjectCreationDraftIdTag> id,
        int revision,
        string canonicalJson,
        DateTimeOffset updatedAt,
        EntityId<ProjectIdTag>? finalizedProjectId,
        DateTimeOffset? finalizedAt,
        string? displayName = null)
    {
        Id = id;
        Revision = revision;
        CanonicalJson = canonicalJson;
        UpdatedAt = updatedAt;
        FinalizedProjectId = finalizedProjectId;
        FinalizedAt = finalizedAt;
        DisplayName = displayName;
    }

    public EntityId<ProjectCreationDraftIdTag> Id { get; }

    public int Revision { get; }

    public string CanonicalJson { get; }

    public DateTimeOffset UpdatedAt { get; }

    public EntityId<ProjectIdTag>? FinalizedProjectId { get; }

    public DateTimeOffset? FinalizedAt { get; }

    /// <summary>The presentation-only name set by the user; null until one is set.</summary>
    public string? DisplayName { get; }

    public bool IsFinalized => FinalizedProjectId is not null;
}

public sealed class ProjectCreationDraftStore
{
    // This is a resource-safety envelope for the ten-field UI contract, not a
    // product-content or media-quality threshold.
    public const int MaximumPayloadUtf8Bytes = 64 * 1024;

    private const string EntityKind = "project creation draft";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _databasePath;

    public ProjectCreationDraftStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public StoredProjectCreationDraft CreateDraft(
        ProjectCreationDraft draft,
        string payloadJson,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        byte[] payload = CanonicalizeBoundedPayload(payloadJson, nameof(payloadJson));
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO project_creation_drafts (
                id,
                revision,
                payload,
                updated_at,
                finalized_project_id,
                finalized_at
            )
            VALUES ($id, 0, $payload, $updated_at, NULL, NULL);
            """;
        SqliteStoreSupport.AddText(command, "$id", draft.Id.ToString());
        SqliteStoreSupport.AddBlob(command, "$payload", payload);
        SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The project creation draft was not stored.");
        }

        StoredProjectCreationDraft stored = ReadRequiredDraft(
            connection,
            transaction,
            draft.Id);
        transaction.Commit();
        return stored;
    }

    public StoredProjectCreationDraft? GetDraft(
        EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        return ReadDraft(connection, transaction: null, draftId);
    }

    public IReadOnlyList<StoredProjectCreationDraft> ListDrafts()
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + """
            WHERE NOT EXISTS (
                SELECT 1 FROM project_creation_draft_deletions AS deleted
                WHERE deleted.draft_id = project_creation_drafts.id)
            ORDER BY updated_at DESC, id ASC;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var drafts = new List<StoredProjectCreationDraft>();
        while (reader.Read())
        {
            drafts.Add(ReadStoredDraft(reader));
        }

        return Array.AsReadOnly(drafts.ToArray());
    }

    /// <summary>
    /// Hides the draft (and its finalized project) from every read by recording a
    /// permanent tombstone. Stored rows, jobs and audit history are kept.
    /// </summary>
    /// <returns>False when a finalized project still has a running job.</returns>
    public bool DeleteDraft(
        EntityId<ProjectCreationDraftIdTag> draftId,
        DateTimeOffset deletedAt)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        string storedDeletedAt = SqliteStoreSupport.FormatTimestamp(
            deletedAt,
            nameof(deletedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredProjectCreationDraft current = ReadRequiredDraft(
            connection,
            transaction,
            draftId);

        if (current.FinalizedProjectId is not null)
        {
            using SqliteCommand running = connection.CreateCommand();
            running.Transaction = transaction;
            running.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM jobs
                    WHERE project_id = $project_id AND state = 'running');
                """;
            SqliteStoreSupport.AddText(
                running,
                "$project_id",
                current.FinalizedProjectId.ToString()!);
            if (Convert.ToInt64(running.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                return false;
            }
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO project_creation_draft_deletions (draft_id, deleted_at)
            VALUES ($id, $deleted_at);
            """;
        SqliteStoreSupport.AddText(command, "$id", draftId.ToString());
        SqliteStoreSupport.AddText(command, "$deleted_at", storedDeletedAt);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The project deletion was not recorded.");
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Sets the presentation-only display name of a draft or finalized project.
    /// The stored draft payload, its revision and the project snapshot are not changed.
    /// </summary>
    public StoredProjectCreationDraft SetDisplayName(
        EntityId<ProjectCreationDraftIdTag> draftId,
        string displayName,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        ArgumentNullException.ThrowIfNull(displayName);
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        _ = ReadRequiredDraft(connection, transaction, draftId);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO project_display_names (draft_id, display_name, updated_at)
            VALUES ($id, $name, $updated_at)
            ON CONFLICT (draft_id) DO UPDATE
            SET display_name = excluded.display_name,
                updated_at = excluded.updated_at;
            """;
        SqliteStoreSupport.AddText(command, "$id", draftId.ToString());
        SqliteStoreSupport.AddText(command, "$name", displayName);
        SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The project display name was not stored.");
        }

        StoredProjectCreationDraft stored = ReadRequiredDraft(connection, transaction, draftId);
        transaction.Commit();
        return stored;
    }

    public StoredProjectCreationDraft SaveDraft(
        ProjectCreationDraft draft,
        string payloadJson,
        int expectedRevision,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateExpectedRevision(expectedRevision);
        byte[] payload = CanonicalizeBoundedPayload(payloadJson, nameof(payloadJson));
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredProjectCreationDraft current = ReadRequiredDraft(
            connection,
            transaction,
            draft.Id);
        EnsureMutableAndCurrent(current, expectedRevision, updatedAt, nameof(updatedAt));

        int newRevision = checked(current.Revision + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE project_creation_drafts
            SET revision = $new_revision,
                payload = $payload,
                updated_at = $updated_at
            WHERE id = $id
              AND revision = $expected_revision
              AND finalized_project_id IS NULL
              AND finalized_at IS NULL;
            """;
        SqliteStoreSupport.AddInteger(command, "$new_revision", newRevision);
        SqliteStoreSupport.AddBlob(command, "$payload", payload);
        SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
        SqliteStoreSupport.AddText(command, "$id", draft.Id.ToString());
        SqliteStoreSupport.AddInteger(command, "$expected_revision", expectedRevision);
        EnsureDraftMutation(command, connection, transaction, draft.Id, expectedRevision);

        StoredProjectCreationDraft saved = ReadRequiredDraft(
            connection,
            transaction,
            draft.Id);
        if (saved.Revision != newRevision || saved.IsFinalized)
        {
            throw InvalidStoredDraft();
        }

        transaction.Commit();
        return saved;
    }

    public StoredProjectCreationDraft FinalizeDraft(
        ProjectCreationDraft draft,
        Project project,
        string payloadJson,
        int expectedRevision,
        DateTimeOffset finalizedAt)
    {
        return FinalizeDraftCore(
            draft,
            project,
            payloadJson,
            ProjectSnapshotJson.WriteProject(project),
            expectedRevision,
            finalizedAt,
            initialSettingsSnapshot: null);
    }

    internal StoredProjectCreationDraft FinalizeDraft(
        ProjectCreationDraft draft,
        Project project,
        string draftPayloadJson,
        string projectPayloadJson,
        int expectedRevision,
        DateTimeOffset finalizedAt) =>
        FinalizeDraftCore(
            draft,
            project,
            draftPayloadJson,
            projectPayloadJson,
            expectedRevision,
            finalizedAt,
            initialSettingsSnapshot: null);

    public StoredProjectCreationDraft FinalizeDraft(
        ProjectCreationDraft draft,
        Project project,
        string payloadJson,
        int expectedRevision,
        DateTimeOffset finalizedAt,
        DefaultSettingsSnapshot initialSettingsSnapshot)
    {
        ArgumentNullException.ThrowIfNull(initialSettingsSnapshot);
        return FinalizeDraftCore(
            draft,
            project,
            payloadJson,
            ProjectSnapshotJson.WriteProject(project),
            expectedRevision,
            finalizedAt,
            initialSettingsSnapshot);
    }

    internal StoredProjectCreationDraft FinalizeDraft(
        ProjectCreationDraft draft,
        Project project,
        string draftPayloadJson,
        string projectPayloadJson,
        int expectedRevision,
        DateTimeOffset finalizedAt,
        DefaultSettingsSnapshot initialSettingsSnapshot)
    {
        ArgumentNullException.ThrowIfNull(initialSettingsSnapshot);
        return FinalizeDraftCore(
            draft,
            project,
            draftPayloadJson,
            projectPayloadJson,
            expectedRevision,
            finalizedAt,
            initialSettingsSnapshot);
    }

    /// <summary>
    /// Atomically finalizes the draft and accepts a production root and ingest Job.
    /// Retry with the exact original request, payload, revision and timestamp; callers
    /// that lost those inputs can recover the original acceptance with GetByDraft.
    /// The optional locator must already be encrypted by the trusted native caller.
    /// </summary>
    public StoredSourceOnlyExecution FinalizeSourceOnlyWithExecution(
        ProjectCreationDraft draft,
        Project project,
        string payloadJson,
        int expectedRevision,
        DateTimeOffset finalizedAt,
        SourceOnlyExecutionRequest request,
        byte[]? protectedSourceLocator = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (protectedSourceLocator is not null &&
            protectedSourceLocator.Length is <= 0 or > SourceOnlyExecutionStore.MaximumProtectedLocatorBytes)
        {
            throw new ArgumentException("Protected source locator exceeds its byte limits.", nameof(protectedSourceLocator));
        }
        _ = FinalizeDraftCore(draft, project, payloadJson,
            ProjectSnapshotJson.WriteProject(project), expectedRevision, finalizedAt,
            request.SettingsSnapshot, request, protectedSourceLocator);
        return new SourceOnlyExecutionStore(_databasePath).GetByDraft(draft.Id)
            ?? throw SqliteStoreSupport.InvalidStoredData("source-only execution");
    }

    private StoredProjectCreationDraft FinalizeDraftCore(
        ProjectCreationDraft draft,
        Project project,
        string draftPayloadJson,
        string projectPayloadJson,
        int expectedRevision,
        DateTimeOffset finalizedAt,
        DefaultSettingsSnapshot? initialSettingsSnapshot,
        SourceOnlyExecutionRequest? executionRequest = null,
        byte[]? protectedSourceLocator = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(project);
        ValidateExpectedRevision(expectedRevision);
        ValidateFinalizedAggregate(draft, project);
        byte[] draftPayload = CanonicalizeBoundedPayload(
            draftPayloadJson,
            nameof(draftPayloadJson));
        byte[] projectPayload = CanonicalizeBoundedPayload(
            projectPayloadJson,
            nameof(projectPayloadJson));
        byte[] expectedProjectPayload = Encoding.UTF8.GetBytes(
            ProjectSnapshotJson.WriteProject(project));
        if (!projectPayload.AsSpan().SequenceEqual(expectedProjectPayload))
        {
            throw new ArgumentException(
                "The Project payload must exactly match its canonical aggregate snapshot.",
                nameof(projectPayloadJson));
        }

        string storedFinalizedAt = SqliteStoreSupport.FormatTimestamp(
            finalizedAt,
            nameof(finalizedAt));

        byte[]? executionPayload = null;
        byte[]? locatorCopy = protectedSourceLocator?.ToArray();
        if (executionRequest is not null)
        {
            if (executionRequest.DraftId != draft.Id || executionRequest.ProjectId != project.Id ||
                executionRequest.SourceCandidateId != draft.SourceCandidateId ||
                executionRequest.CreatedAt != finalizedAt.ToUniversalTime() ||
                initialSettingsSnapshot is null ||
                initialSettingsSnapshot.CanonicalJson != executionRequest.SettingsSnapshot.CanonicalJson)
            {
                throw new ArgumentException("The execution must match the finalized draft and project.", nameof(executionRequest));
            }

            using JsonDocument draftDocument = JsonDocument.Parse(draftPayload);
            using JsonDocument projectDocument = JsonDocument.Parse(projectPayload);
            byte[] finalizationPayload = SqliteStoreSupport.CanonicalizePayload(JsonSerializer.Serialize(new
            {
                schema = "source-only.finalization.v1",
                draft,
                draftPayload = draftDocument.RootElement,
                projectPayload = projectDocument.RootElement,
                expectedRevision,
                finalizedAt = storedFinalizedAt,
            }), nameof(draftPayloadJson));
            executionPayload = SourceOnlyExecutionStore.PrepareInitial(executionRequest,
                ContentDigest.Compute(finalizationPayload), locatorCopy);
        }

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredProjectCreationDraft current = ReadRequiredDraft(
            connection,
            transaction,
            draft.Id);
        if (executionRequest is not null && current.IsFinalized)
        {
            StoredSourceOnlyExecution recorded = SourceOnlyExecutionStore.ReadByDraft(
                connection, transaction, draft.Id)
                ?? throw new InvalidOperationException("The finalized draft has no recorded source-only execution.");
            SourceOnlyExecutionStore.EnsureSameRequest(recorded, executionPayload!);
            if (current.FinalizedProjectId != project.Id || current.FinalizedAt != finalizedAt.ToUniversalTime() ||
                current.Revision != checked(expectedRevision + 1) || current.CanonicalJson != Encoding.UTF8.GetString(draftPayload))
            {
                throw SqliteStoreSupport.InvalidStoredData("source-only finalization");
            }
            transaction.Commit();
            return current;
        }
        EnsureMutableAndCurrent(
            current,
            expectedRevision,
            finalizedAt,
            nameof(finalizedAt));

        InsertProject(
            connection,
            transaction,
            project,
            projectPayload,
            storedFinalizedAt);
        if (initialSettingsSnapshot is not null)
        {
            _ = DefaultSettingsStore.BindProjectSettings(
                connection,
                transaction,
                project.Id,
                initialSettingsSnapshot,
                finalizedAt);
        }

        int newRevision = checked(current.Revision + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE project_creation_drafts
            SET revision = $new_revision,
                payload = $payload,
                updated_at = $finalized_at,
                finalized_project_id = $project_id,
                finalized_at = $finalized_at
            WHERE id = $id
              AND revision = $expected_revision
              AND finalized_project_id IS NULL
              AND finalized_at IS NULL;
            """;
        SqliteStoreSupport.AddInteger(command, "$new_revision", newRevision);
        SqliteStoreSupport.AddBlob(command, "$payload", draftPayload);
        SqliteStoreSupport.AddText(command, "$finalized_at", storedFinalizedAt);
        SqliteStoreSupport.AddText(command, "$project_id", project.Id.ToString());
        SqliteStoreSupport.AddText(command, "$id", draft.Id.ToString());
        SqliteStoreSupport.AddInteger(command, "$expected_revision", expectedRevision);
        EnsureDraftMutation(command, connection, transaction, draft.Id, expectedRevision);

        StoredProjectCreationDraft finalized = ReadRequiredDraft(
            connection,
            transaction,
            draft.Id);
        if (finalized.Revision != newRevision ||
            finalized.FinalizedProjectId != project.Id ||
            finalized.FinalizedAt != finalizedAt.ToUniversalTime())
        {
            throw InvalidStoredDraft();
        }

        if (executionRequest is not null)
        {
            SourceOnlyExecutionStore.InsertInitial(connection, transaction,
                executionRequest, executionPayload!, locatorCopy);
        }

        transaction.Commit();
        return finalized;
    }

    private static void ValidateFinalizedAggregate(
        ProjectCreationDraft draft,
        Project project)
    {
        if (!draft.IsComplete)
        {
            throw new InvalidOperationException(
                "The project creation draft is incomplete.");
        }

        DateTimeOffset? selectedDeadline = draft.DeadlineSelection!.Deadline;
        bool deadlineMatches = selectedDeadline is null
            ? project.Deadline is null
            : project.Deadline is DateTimeOffset projectDeadline &&
                selectedDeadline.Value.ToUniversalTime() == projectDeadline.ToUniversalTime();
        if (!string.Equals(draft.ContentName, project.ContentName, StringComparison.Ordinal) ||
            !string.Equals(draft.Purpose, project.Purpose, StringComparison.Ordinal) ||
            !string.Equals(draft.Audience, project.Audience, StringComparison.Ordinal) ||
            draft.PublicationScope != project.PublicationScope ||
            !deadlineMatches ||
            project.Assets.Count != 0 ||
            project.Performers.Count != 0)
        {
            throw new ArgumentException(
                "The finalized project must exactly match the completed draft without inferred assets or performers.",
                nameof(project));
        }
    }

    private static void InsertProject(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Project project,
        byte[] payload,
        string updatedAt)
    {
        string? deadline = project.Deadline is DateTimeOffset selectedDeadline
            ? SqliteStoreSupport.FormatTimestamp(selectedDeadline, nameof(project))
            : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO projects (
                id,
                revision,
                publication_scope,
                deadline,
                payload,
                updated_at
            )
            VALUES (
                $id,
                0,
                $publication_scope,
                $deadline,
                $payload,
                $updated_at
            );
            """;
        SqliteStoreSupport.AddText(command, "$id", project.Id.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$publication_scope",
            project.PublicationScope.ToString());
        SqliteStoreSupport.AddNullableText(command, "$deadline", deadline);
        SqliteStoreSupport.AddBlob(command, "$payload", payload);
        SqliteStoreSupport.AddText(command, "$updated_at", updatedAt);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The finalized project was not stored.");
        }
    }

    private static void EnsureMutableAndCurrent(
        StoredProjectCreationDraft current,
        int expectedRevision,
        DateTimeOffset updatedAt,
        string timestampParameterName)
    {
        EnsureExpectedRevision(current.Id, expectedRevision, current.Revision);
        if (current.IsFinalized)
        {
            throw new InvalidOperationException(
                "The project creation draft is already finalized.");
        }

        DateTimeOffset normalizedUpdatedAt;
        try
        {
            normalizedUpdatedAt = updatedAt.ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                timestampParameterName,
                "The project creation draft timestamp cannot be represented in UTC.");
        }

        if (normalizedUpdatedAt < current.UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                timestampParameterName,
                "A project creation draft timestamp cannot move backwards.");
        }
    }

    private static void EnsureDraftMutation(
        SqliteCommand command,
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectCreationDraftIdTag> draftId,
        int expectedRevision)
    {
        if (command.ExecuteNonQuery() == 1)
        {
            return;
        }

        StoredProjectCreationDraft latest = ReadRequiredDraft(
            connection,
            transaction,
            draftId);
        EnsureExpectedRevision(draftId, expectedRevision, latest.Revision);
        if (latest.IsFinalized)
        {
            throw new InvalidOperationException(
                "The project creation draft is already finalized.");
        }

        throw InvalidStoredDraft();
    }

    private static StoredProjectCreationDraft? ReadDraft(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EntityId<ProjectCreationDraftIdTag> draftId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + """
            WHERE id = $id
              AND NOT EXISTS (
                SELECT 1 FROM project_creation_draft_deletions AS deleted
                WHERE deleted.draft_id = project_creation_drafts.id);
            """;
        SqliteStoreSupport.AddText(command, "$id", draftId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        StoredProjectCreationDraft draft = ReadStoredDraft(reader);
        if (reader.Read() || draft.Id != draftId)
        {
            throw InvalidStoredDraft();
        }

        return draft;
    }

    private static StoredProjectCreationDraft ReadRequiredDraft(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectCreationDraftIdTag> draftId)
    {
        return ReadDraft(connection, transaction, draftId)
            ?? throw new InvalidOperationException(
                "The project creation draft does not exist.");
    }

    private static StoredProjectCreationDraft ReadStoredDraft(SqliteDataReader reader)
    {
        EntityId<ProjectCreationDraftIdTag> id =
            SqliteStoreSupport.ReadEntityId<ProjectCreationDraftIdTag>(reader, 0, EntityKind);
        int revision = SqliteStoreSupport.ReadInt32(reader, 1, EntityKind);
        long payloadLength = SqliteStoreSupport.ReadInt64(reader, 2, EntityKind);
        string canonicalJson = SqliteStoreSupport.ReadCanonicalPayload(reader, 3, EntityKind);
        DateTimeOffset updatedAt = SqliteStoreSupport.ReadTimestamp(reader, 4, EntityKind);
        EntityId<ProjectIdTag>? finalizedProjectId =
            SqliteStoreSupport.ReadNullableEntityId<ProjectIdTag>(reader, 5, EntityKind);
        DateTimeOffset? finalizedAt =
            SqliteStoreSupport.ReadNullableTimestamp(reader, 6, EntityKind);

        if (revision < 0 ||
            payloadLength is <= 0 or > MaximumPayloadUtf8Bytes ||
            (finalizedProjectId is null) != (finalizedAt is null) ||
            (finalizedAt is not null && finalizedAt != updatedAt))
        {
            throw InvalidStoredDraft();
        }

        return new StoredProjectCreationDraft(
            id,
            revision,
            canonicalJson,
            updatedAt,
            finalizedProjectId,
            finalizedAt,
            SqliteStoreSupport.ReadNullableString(reader, 7, EntityKind));
    }

    private static byte[] CanonicalizeBoundedPayload(
        string payloadJson,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(payloadJson, parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(payloadJson) > MaximumPayloadUtf8Bytes)
            {
                throw new ArgumentException(
                    "Project creation draft JSON exceeds the supported byte limit.",
                    parameterName);
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "Project creation draft JSON must contain valid Unicode.",
                parameterName);
        }

        byte[] canonical = SqliteStoreSupport.CanonicalizePayload(
            payloadJson,
            parameterName);
        if (canonical.Length > MaximumPayloadUtf8Bytes)
        {
            throw new ArgumentException(
                "Project creation draft JSON exceeds the supported byte limit.",
                parameterName);
        }

        return canonical;
    }

    private static void ValidateExpectedRevision(int expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "An expected project creation draft revision must not be negative.");
        }
    }

    private static void EnsureExpectedRevision(
        EntityId<ProjectCreationDraftIdTag> draftId,
        int expectedRevision,
        int actualRevision)
    {
        if (expectedRevision != actualRevision)
        {
            throw new StoreConcurrencyException(
                EntityKind,
                draftId.ToString(),
                expectedRevision,
                actualRevision);
        }
    }

    private const string SelectColumns = """
        SELECT
            id,
            revision,
            length(CAST(payload AS BLOB)),
            CAST(payload AS BLOB),
            updated_at,
            finalized_project_id,
            finalized_at,
            (SELECT display_name FROM project_display_names AS names
                WHERE names.draft_id = project_creation_drafts.id)
        FROM project_creation_drafts
        """ + "\n";

    private static InvalidOperationException InvalidStoredDraft()
    {
        return SqliteStoreSupport.InvalidStoredData(EntityKind);
    }
}