using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class StoredProject
{
    internal StoredProject(
        EntityId<ProjectIdTag> id,
        int revision,
        PublicationScope publicationScope,
        DateTimeOffset? deadline,
        DateTimeOffset updatedAt,
        string canonicalJson)
    {
        Id = id;
        Revision = revision;
        PublicationScope = publicationScope;
        Deadline = deadline;
        UpdatedAt = updatedAt;
        CanonicalJson = canonicalJson;
    }

    public EntityId<ProjectIdTag> Id { get; }

    public int Revision { get; }

    public PublicationScope PublicationScope { get; }

    public DateTimeOffset? Deadline { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string CanonicalJson { get; }
}

public sealed class StoredWorkingCopy
{
    internal StoredWorkingCopy(
        EntityId<WorkingCopyIdTag> id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag>? baseVersionId,
        int revision,
        DateTimeOffset updatedAt,
        string canonicalJson)
    {
        Id = id;
        ProjectId = projectId;
        BaseVersionId = baseVersionId;
        Revision = revision;
        UpdatedAt = updatedAt;
        CanonicalJson = canonicalJson;
    }

    public EntityId<WorkingCopyIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag>? BaseVersionId { get; }

    public int Revision { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string CanonicalJson { get; }
}

public sealed class StoredCompositionVersion
{
    internal StoredCompositionVersion(
        EntityId<CompositionVersionIdTag> id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag>? parentVersionId,
        ContentDigest contentDigest,
        DateTimeOffset createdAt,
        string canonicalJson)
    {
        Id = id;
        ProjectId = projectId;
        ParentVersionId = parentVersionId;
        ContentDigest = contentDigest;
        CreatedAt = createdAt;
        CanonicalJson = canonicalJson;
    }

    public EntityId<CompositionVersionIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag>? ParentVersionId { get; }

    public ContentDigest ContentDigest { get; }

    public DateTimeOffset CreatedAt { get; }

    public string CanonicalJson { get; }
}

public sealed class ProjectStore
{
    private readonly string _databasePath;

    public ProjectStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public void CreateProject(
        Project project,
        string payloadJson,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(project);
        byte[] payload = SqliteStoreSupport.CanonicalizePayload(
            payloadJson,
            nameof(payloadJson));
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));
        string? storedDeadline = project.Deadline is DateTimeOffset deadline
            ? SqliteStoreSupport.FormatTimestamp(deadline, nameof(project))
            : null;

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
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
        SqliteStoreSupport.AddNullableText(command, "$deadline", storedDeadline);
        SqliteStoreSupport.AddBlob(command, "$payload", payload);
        SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
        EnsureSingleMutation(command, "project");
        transaction.Commit();
    }

    public int UpdateProject(
        Project project,
        string payloadJson,
        int expectedRevision,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateExpectedRevision(expectedRevision);
        byte[] payload = SqliteStoreSupport.CanonicalizePayload(
            payloadJson,
            nameof(payloadJson));
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));
        string? storedDeadline = project.Deadline is DateTimeOffset deadline
            ? SqliteStoreSupport.FormatTimestamp(deadline, nameof(project))
            : null;

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        (int actualRevision, DateTimeOffset currentUpdatedAt) = ReadProjectRevision(
            connection,
            transaction,
            project.Id);
        EnsureExpectedRevision(
            "project",
            project.Id.ToString(),
            expectedRevision,
            actualRevision);
        if (updatedAt < currentUpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                "A project update timestamp must not move backwards.");
        }

        int newRevision = checked(actualRevision + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE projects
            SET revision = $new_revision,
                publication_scope = $publication_scope,
                deadline = $deadline,
                payload = $payload,
                updated_at = $updated_at
            WHERE id = $id AND revision = $expected_revision;
            """;
        SqliteStoreSupport.AddInteger(command, "$new_revision", newRevision);
        SqliteStoreSupport.AddText(
            command,
            "$publication_scope",
            project.PublicationScope.ToString());
        SqliteStoreSupport.AddNullableText(command, "$deadline", storedDeadline);
        SqliteStoreSupport.AddBlob(command, "$payload", payload);
        SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
        SqliteStoreSupport.AddText(command, "$id", project.Id.ToString());
        SqliteStoreSupport.AddInteger(command, "$expected_revision", expectedRevision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw SqliteStoreSupport.InvalidStoredData("project");
        }

        transaction.Commit();
        return newRevision;
    }

    public StoredProject? GetProject(EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, revision, publication_scope, deadline, updated_at, payload
            FROM projects
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", projectId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        StoredProject stored = ReadStoredProject(reader);
        if (reader.Read() || stored.Id != projectId)
        {
            throw SqliteStoreSupport.InvalidStoredData("project");
        }

        return stored;
    }

    public void CreateWorkingCopy(WorkingCopy workingCopy, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(workingCopy);
        byte[] payload = SqliteStoreSupport.CanonicalizePayload(
            payloadJson,
            nameof(payloadJson));
        string updatedAt = SqliteStoreSupport.FormatTimestamp(
            workingCopy.UpdatedAt,
            nameof(workingCopy));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO working_copies (
                id,
                project_id,
                base_version_id,
                revision,
                updated_at,
                payload
            )
            VALUES (
                $id,
                $project_id,
                $base_version_id,
                0,
                $updated_at,
                $payload
            );
            """;
        AddWorkingCopyIdentityParameters(command, workingCopy);
        SqliteStoreSupport.AddText(command, "$updated_at", updatedAt);
        SqliteStoreSupport.AddBlob(command, "$payload", payload);
        EnsureSingleMutation(command, "working copy");
        transaction.Commit();
    }

    public int UpdateWorkingCopy(
        WorkingCopy workingCopy,
        string payloadJson,
        int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(workingCopy);
        ValidateExpectedRevision(expectedRevision);
        byte[] payload = SqliteStoreSupport.CanonicalizePayload(
            payloadJson,
            nameof(payloadJson));
        string updatedAt = SqliteStoreSupport.FormatTimestamp(
            workingCopy.UpdatedAt,
            nameof(workingCopy));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredWorkingCopyIdentity current = ReadWorkingCopyIdentity(
            connection,
            transaction,
            workingCopy.Id);
        if (current.ProjectId != workingCopy.ProjectId)
        {
            throw new InvalidOperationException(
                "The stored working copy does not match the supplied aggregate.");
        }

        EnsureExpectedRevision(
            "working copy",
            workingCopy.Id.ToString(),
            expectedRevision,
            current.Revision);
        if (workingCopy.UpdatedAt < current.UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingCopy),
                "A working-copy update timestamp must not move backwards.");
        }

        int newRevision = checked(current.Revision + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE working_copies
            SET base_version_id = $base_version_id,
                revision = $new_revision,
                updated_at = $updated_at,
                payload = $payload
            WHERE id = $id
              AND project_id = $project_id
              AND revision = $expected_revision;
            """;
        SqliteStoreSupport.AddNullableText(
            command,
            "$base_version_id",
            workingCopy.BaseVersionId?.ToString());
        SqliteStoreSupport.AddInteger(command, "$new_revision", newRevision);
        SqliteStoreSupport.AddText(command, "$updated_at", updatedAt);
        SqliteStoreSupport.AddBlob(command, "$payload", payload);
        SqliteStoreSupport.AddText(command, "$id", workingCopy.Id.ToString());
        SqliteStoreSupport.AddText(command, "$project_id", workingCopy.ProjectId.ToString());
        SqliteStoreSupport.AddInteger(command, "$expected_revision", expectedRevision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw SqliteStoreSupport.InvalidStoredData("working copy");
        }

        transaction.Commit();
        return newRevision;
    }

    public StoredWorkingCopy? GetWorkingCopy(EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_id, base_version_id, revision, updated_at, payload
            FROM working_copies
            WHERE project_id = $project_id;
            """;
        SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        StoredWorkingCopy stored = ReadStoredWorkingCopy(reader);
        if (reader.Read() || stored.ProjectId != projectId)
        {
            throw SqliteStoreSupport.InvalidStoredData("working copy");
        }

        return stored;
    }

    public void AppendCompositionVersion(
        CompositionVersion compositionVersion,
        string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(compositionVersion);
        byte[] payload = SqliteStoreSupport.CanonicalizePayload(
            payloadJson,
            nameof(payloadJson));
        string createdAt = SqliteStoreSupport.FormatTimestamp(
            compositionVersion.CreatedAt,
            nameof(compositionVersion));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO composition_versions (
                id,
                project_id,
                parent_version_id,
                content_digest,
                created_at,
                payload
            )
            VALUES (
                $id,
                $project_id,
                $parent_version_id,
                $content_digest,
                $created_at,
                $payload
            );
            """;
        SqliteStoreSupport.AddText(command, "$id", compositionVersion.Id.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$project_id",
            compositionVersion.ProjectId.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$parent_version_id",
            compositionVersion.ParentVersionId?.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$content_digest",
            compositionVersion.ContentDigest.Hex);
        SqliteStoreSupport.AddText(command, "$created_at", createdAt);
        SqliteStoreSupport.AddBlob(command, "$payload", payload);
        EnsureSingleMutation(command, "composition version");
        transaction.Commit();
    }

    public StoredCompositionVersion? GetCompositionVersion(
        EntityId<CompositionVersionIdTag> compositionVersionId)
    {
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                project_id,
                parent_version_id,
                content_digest,
                created_at,
                payload
            FROM composition_versions
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", compositionVersionId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        StoredCompositionVersion stored = ReadStoredCompositionVersion(reader);
        if (reader.Read() || stored.Id != compositionVersionId)
        {
            throw SqliteStoreSupport.InvalidStoredData("composition version");
        }

        return stored;
    }

    private static StoredProject ReadStoredProject(SqliteDataReader reader)
    {
        const string entityKind = "project";
        EntityId<ProjectIdTag> id =
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 0, entityKind);
        int revision = SqliteStoreSupport.ReadInt32(reader, 1, entityKind);
        if (revision < 0)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        string scopeText = SqliteStoreSupport.ReadString(reader, 2, entityKind);
        if (!Enum.TryParse(scopeText, ignoreCase: false, out PublicationScope scope) ||
            !Enum.IsDefined(scope) ||
            !string.Equals(scopeText, scope.ToString(), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        DateTimeOffset? deadline =
            SqliteStoreSupport.ReadNullableTimestamp(reader, 3, entityKind);
        DateTimeOffset updatedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 4, entityKind);
        string canonicalJson =
            SqliteStoreSupport.ReadCanonicalPayload(reader, 5, entityKind);
        return new StoredProject(id, revision, scope, deadline, updatedAt, canonicalJson);
    }

    private static StoredWorkingCopy ReadStoredWorkingCopy(SqliteDataReader reader)
    {
        const string entityKind = "working copy";
        EntityId<WorkingCopyIdTag> id =
            SqliteStoreSupport.ReadEntityId<WorkingCopyIdTag>(reader, 0, entityKind);
        EntityId<ProjectIdTag> projectId =
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, entityKind);
        EntityId<CompositionVersionIdTag>? baseVersionId =
            SqliteStoreSupport.ReadNullableEntityId<CompositionVersionIdTag>(
                reader,
                2,
                entityKind);
        int revision = SqliteStoreSupport.ReadInt32(reader, 3, entityKind);
        if (revision < 0)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        DateTimeOffset updatedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 4, entityKind);
        string canonicalJson =
            SqliteStoreSupport.ReadCanonicalPayload(reader, 5, entityKind);
        return new StoredWorkingCopy(
            id,
            projectId,
            baseVersionId,
            revision,
            updatedAt,
            canonicalJson);
    }

    private static StoredCompositionVersion ReadStoredCompositionVersion(
        SqliteDataReader reader)
    {
        const string entityKind = "composition version";
        EntityId<CompositionVersionIdTag> id =
            SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 0, entityKind);
        EntityId<ProjectIdTag> projectId =
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, entityKind);
        EntityId<CompositionVersionIdTag>? parentVersionId =
            SqliteStoreSupport.ReadNullableEntityId<CompositionVersionIdTag>(
                reader,
                2,
                entityKind);
        if (id == parentVersionId)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        ContentDigest contentDigest =
            SqliteStoreSupport.ReadContentDigest(reader, 3, entityKind);
        DateTimeOffset createdAt =
            SqliteStoreSupport.ReadTimestamp(reader, 4, entityKind);
        string canonicalJson =
            SqliteStoreSupport.ReadCanonicalPayload(reader, 5, entityKind);
        return new StoredCompositionVersion(
            id,
            projectId,
            parentVersionId,
            contentDigest,
            createdAt,
            canonicalJson);
    }

    private static (int Revision, DateTimeOffset UpdatedAt) ReadProjectRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, updated_at
            FROM projects
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", projectId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The project does not exist.");
        }

        int revision = SqliteStoreSupport.ReadInt32(reader, 0, "project");
        DateTimeOffset updatedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 1, "project");
        if (revision < 0 || reader.Read())
        {
            throw SqliteStoreSupport.InvalidStoredData("project");
        }

        return (revision, updatedAt);
    }

    private static StoredWorkingCopyIdentity ReadWorkingCopyIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<WorkingCopyIdTag> workingCopyId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id, revision, updated_at
            FROM working_copies
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", workingCopyId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The working copy does not exist.");
        }

        EntityId<ProjectIdTag> projectId =
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 0, "working copy");
        int revision = SqliteStoreSupport.ReadInt32(reader, 1, "working copy");
        DateTimeOffset updatedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 2, "working copy");
        if (revision < 0 || reader.Read())
        {
            throw SqliteStoreSupport.InvalidStoredData("working copy");
        }

        return new StoredWorkingCopyIdentity(projectId, revision, updatedAt);
    }

    private static void AddWorkingCopyIdentityParameters(
        SqliteCommand command,
        WorkingCopy workingCopy)
    {
        SqliteStoreSupport.AddText(command, "$id", workingCopy.Id.ToString());
        SqliteStoreSupport.AddText(command, "$project_id", workingCopy.ProjectId.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$base_version_id",
            workingCopy.BaseVersionId?.ToString());
    }

    private static void ValidateExpectedRevision(int expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "An expected revision must not be negative.");
        }
    }

    private static void EnsureExpectedRevision(
        string entityKind,
        string entityId,
        int expectedRevision,
        int actualRevision)
    {
        if (expectedRevision != actualRevision)
        {
            throw new StoreConcurrencyException(
                entityKind,
                entityId,
                expectedRevision,
                actualRevision);
        }
    }

    private static void EnsureSingleMutation(SqliteCommand command, string entityKind)
    {
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"The {entityKind} was not stored.");
        }
    }

    private sealed record StoredWorkingCopyIdentity(
        EntityId<ProjectIdTag> ProjectId,
        int Revision,
        DateTimeOffset UpdatedAt);
}