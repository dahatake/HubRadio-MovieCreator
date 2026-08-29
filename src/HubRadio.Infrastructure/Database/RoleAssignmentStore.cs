using System.Globalization;
using System.Security.Principal;
using System.Text;
using HubRadio.Core.Security;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class StoredRoleAssignment
{
    internal StoredRoleAssignment(
        string actorSid,
        AuthorizationRole role,
        DateTimeOffset assignedAt,
        string assignedBySid,
        string reason)
    {
        ActorSid = actorSid;
        Role = role;
        AssignedAt = assignedAt;
        AssignedBySid = assignedBySid;
        Reason = reason;
    }

    public string ActorSid { get; }

    public AuthorizationRole Role { get; }

    public DateTimeOffset AssignedAt { get; }

    public string AssignedBySid { get; }

    public string Reason { get; }
}

public sealed class RoleAssignmentStore
{
    private const string EntityKind = "role assignment";
    private const string BootstrapReason = "Initial local role bootstrap.";
    private const string LastAdministratorRevocationMessage =
        "The last Administrator assignment cannot be revoked.";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _databasePath;
    private readonly TimeProvider _timeProvider;

    public RoleAssignmentStore(string databasePath)
        : this(databasePath, TimeProvider.System)
    {
    }

    internal RoleAssignmentStore(string databasePath, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        EnsureWindows();

        _databasePath = SqliteStoreSupport.Initialize(databasePath);
        _timeProvider = timeProvider;
    }

    public IReadOnlyList<AuthorizationRole> GetRoles(string actorSid)
    {
        string canonicalActorSid = ValidateCanonicalSid(actorSid, nameof(actorSid));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST(actor_sid AS BLOB),
                CAST(role AS BLOB),
                CAST(assigned_at AS BLOB),
                CAST(assigned_by_sid AS BLOB),
                CAST(reason AS BLOB)
            FROM role_assignments
            ORDER BY actor_sid COLLATE BINARY, role COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var roles = new List<AuthorizationRole>();
        while (reader.Read())
        {
            StoredRoleAssignment assignment = ReadAssignment(reader);
            if (!string.Equals(
                    assignment.ActorSid,
                    canonicalActorSid,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (roles.Contains(assignment.Role))
            {
                throw InvalidStoredAssignment();
            }

            roles.Add(assignment.Role);
        }

        return Array.AsReadOnly(roles.ToArray());
    }

    public IReadOnlyList<StoredRoleAssignment> GetAssignments()
    {
        EnsureWindows();

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST(actor_sid AS BLOB),
                CAST(role AS BLOB),
                CAST(assigned_at AS BLOB),
                CAST(assigned_by_sid AS BLOB),
                CAST(reason AS BLOB)
            FROM role_assignments
            ORDER BY actor_sid COLLATE BINARY, role COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var assignments = new List<StoredRoleAssignment>();
        while (reader.Read())
        {
            assignments.Add(ReadAssignment(reader));
        }

        return Array.AsReadOnly(assignments.ToArray());
    }

    public void Assign(
        string actorSid,
        AuthorizationRole role,
        string assignedBySid,
        string reason)
    {
        string canonicalActorSid = ValidateCanonicalSid(actorSid, nameof(actorSid));
        ValidateRole(role, nameof(role));
        string canonicalAssignedBySid = ValidateCanonicalSid(
            assignedBySid,
            nameof(assignedBySid));
        string validatedReason = ValidateReason(reason, nameof(reason));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        EnsureAdministratorAssignment(connection, transaction, canonicalAssignedBySid);
        if (AssignmentExists(connection, transaction, canonicalActorSid, role))
        {
            throw new InvalidOperationException("The role is already assigned to the actor.");
        }

        DateTimeOffset assignedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        InsertAssignment(
            connection,
            transaction,
            canonicalActorSid,
            role,
            assignedAt,
            canonicalAssignedBySid,
            validatedReason);
        transaction.Commit();
    }

    public void Revoke(
        string actorSid,
        AuthorizationRole role,
        string revokedBySid)
    {
        string canonicalActorSid = ValidateCanonicalSid(actorSid, nameof(actorSid));
        ValidateRole(role, nameof(role));
        string canonicalRevokedBySid = ValidateCanonicalSid(
            revokedBySid,
            nameof(revokedBySid));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        EnsureAdministratorAssignment(connection, transaction, canonicalRevokedBySid);
        if (!AssignmentExists(connection, transaction, canonicalActorSid, role))
        {
            throw new InvalidOperationException("The role is not assigned to the actor.");
        }

        if (role == AuthorizationRole.Administrator &&
            ReadValidatedAssignmentCount(
                connection,
                transaction,
                AuthorizationRole.Administrator) == 1)
        {
            throw new InvalidOperationException(LastAdministratorRevocationMessage);
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM role_assignments
            WHERE actor_sid = $actor_sid AND role = $role;
            """;
        SqliteStoreSupport.AddText(command, "$actor_sid", canonicalActorSid);
        SqliteStoreSupport.AddText(command, "$role", role.ToString());
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The role assignment was not revoked.");
        }

        transaction.Commit();
    }

    public bool BootstrapInitialRoles(string currentActorSid)
    {
        string canonicalActorSid = ValidateCanonicalSid(
            currentActorSid,
            nameof(currentActorSid));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        long assignmentCount = ReadValidatedAssignmentCount(connection, transaction);
        if (assignmentCount != 0)
        {
            transaction.Commit();
            return false;
        }

        DateTimeOffset assignedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        InsertAssignment(
            connection,
            transaction,
            canonicalActorSid,
            AuthorizationRole.Administrator,
            assignedAt,
            canonicalActorSid,
            BootstrapReason);
        InsertAssignment(
            connection,
            transaction,
            canonicalActorSid,
            AuthorizationRole.Editor,
            assignedAt,
            canonicalActorSid,
            BootstrapReason);
        transaction.Commit();
        return true;
    }

    private static StoredRoleAssignment ReadAssignment(SqliteDataReader reader)
    {
        string actorSid = ReadCanonicalSid(reader, 0);
        AuthorizationRole role = ReadRole(reader, 1);
        string storedAssignedAt = ReadUtf8Text(reader, 2);
        if (!DateTimeOffset.TryParseExact(
                storedAssignedAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset assignedAt) ||
            assignedAt.Offset != TimeSpan.Zero ||
            !string.Equals(
                storedAssignedAt,
                SqliteStoreSupport.FormatTimestamp(assignedAt, EntityKind),
                StringComparison.Ordinal))
        {
            throw InvalidStoredAssignment();
        }

        string assignedBySid = ReadCanonicalSid(reader, 3);
        string reason = ReadUtf8Text(reader, 4);
        if (string.IsNullOrWhiteSpace(reason) ||
            !string.Equals(reason, reason.Trim(), StringComparison.Ordinal))
        {
            throw InvalidStoredAssignment();
        }

        return new StoredRoleAssignment(
            actorSid,
            role,
            assignedAt,
            assignedBySid,
            reason);
    }

    private static string ReadCanonicalSid(SqliteDataReader reader, int ordinal)
    {
        string stored = ReadUtf8Text(reader, ordinal);
        try
        {
            string canonicalSid = GetCanonicalSidValue(stored);
            if (!string.Equals(stored, canonicalSid, StringComparison.Ordinal))
            {
                throw InvalidStoredAssignment();
            }

            return stored;
        }
        catch (ArgumentException)
        {
            throw InvalidStoredAssignment();
        }
    }

    private static AuthorizationRole ReadRole(SqliteDataReader reader, int ordinal)
    {
        string stored = ReadUtf8Text(reader, ordinal);
        if (!Enum.TryParse(stored, ignoreCase: false, out AuthorizationRole role) ||
            !Enum.IsDefined(role) ||
            !string.Equals(stored, role.ToString(), StringComparison.Ordinal))
        {
            throw InvalidStoredAssignment();
        }

        return role;
    }

    private static void EnsureAdministratorAssignment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actorSid)
    {
        if (!AssignmentExists(
            connection,
            transaction,
            actorSid,
            AuthorizationRole.Administrator))
        {
            throw new InvalidOperationException(
                "The acting actor has no current Administrator assignment.");
        }
    }

    private static bool AssignmentExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actorSid,
        AuthorizationRole role)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(actor_sid AS BLOB),
                CAST(role AS BLOB),
                CAST(assigned_at AS BLOB),
                CAST(assigned_by_sid AS BLOB),
                CAST(reason AS BLOB)
            FROM role_assignments
            ORDER BY actor_sid COLLATE BINARY, role COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        bool found = false;
        while (reader.Read())
        {
            StoredRoleAssignment assignment = ReadAssignment(reader);
            if (!string.Equals(assignment.ActorSid, actorSid, StringComparison.Ordinal) ||
                assignment.Role != role)
            {
                continue;
            }

            if (found)
            {
                throw InvalidStoredAssignment();
            }

            found = true;
        }

        return found;
    }

    private static long ReadValidatedAssignmentCount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthorizationRole? roleFilter = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(actor_sid AS BLOB),
                CAST(role AS BLOB),
                CAST(assigned_at AS BLOB),
                CAST(assigned_by_sid AS BLOB),
                CAST(reason AS BLOB)
            FROM role_assignments
            ORDER BY actor_sid COLLATE BINARY, role COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        long count = 0;
        while (reader.Read())
        {
            StoredRoleAssignment assignment = ReadAssignment(reader);
            if (roleFilter is AuthorizationRole filteredRole &&
                assignment.Role != filteredRole)
            {
                continue;
            }

            checked
            {
                count++;
            }
        }

        return count;
    }

    private static string ReadUtf8Text(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not byte[] bytes)
        {
            throw InvalidStoredAssignment();
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidStoredAssignment();
        }
    }

    private static InvalidOperationException InvalidStoredAssignment() =>
        SqliteStoreSupport.InvalidStoredData(EntityKind);

    private static void InsertAssignment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actorSid,
        AuthorizationRole role,
        DateTimeOffset assignedAt,
        string assignedBySid,
        string reason)
    {
        string storedAssignedAt = SqliteStoreSupport.FormatTimestamp(
            assignedAt,
            nameof(assignedAt));
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO role_assignments (
                actor_sid,
                role,
                assigned_at,
                assigned_by_sid,
                reason
            )
            VALUES (
                $actor_sid,
                $role,
                $assigned_at,
                $assigned_by_sid,
                $reason
            );
            """;
        SqliteStoreSupport.AddText(command, "$actor_sid", actorSid);
        SqliteStoreSupport.AddText(command, "$role", role.ToString());
        SqliteStoreSupport.AddText(command, "$assigned_at", storedAssignedAt);
        SqliteStoreSupport.AddText(command, "$assigned_by_sid", assignedBySid);
        SqliteStoreSupport.AddText(command, "$reason", reason);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The role assignment was not stored.");
        }
    }

    private static string ValidateCanonicalSid(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        EnsureWindows();

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The actor SID must be a canonical Windows security identifier without surrounding whitespace.",
                parameterName);
        }

        try
        {
            string canonicalSid = GetCanonicalSidValue(value);
            if (!string.Equals(value, canonicalSid, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The actor SID must be a canonical Windows security identifier.",
                    parameterName);
            }
        }
        catch (ArgumentException exception) when (exception.ParamName != parameterName)
        {
            throw new ArgumentException(
                "The actor SID must be a canonical Windows security identifier.",
                parameterName,
                exception);
        }

        return value;
    }

    private static string GetCanonicalSidValue(string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows security identifiers are required for local role assignments.");
        }

        return new SecurityIdentifier(value).Value;
    }

    private static void ValidateRole(AuthorizationRole role, string parameterName)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                role,
                "Unknown authorization role.");
        }
    }

    private static string ValidateReason(string reason, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(reason, parameterName);
        if (string.IsNullOrWhiteSpace(reason) ||
            !string.Equals(reason, reason.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A role-assignment reason must not be blank or have surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(reason);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A role-assignment reason must contain valid Unicode text.",
                parameterName,
                exception);
        }

        return reason;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows security identifiers are required for local role assignments.");
        }
    }
}