using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public enum ProjectSettingsBindingState
{
    Unbound,
    Bound,
}

public sealed class StoredGlobalSettings
{
    internal StoredGlobalSettings(
        DefaultSettingsSnapshot snapshot,
        int revision,
        DateTimeOffset updatedAt)
    {
        Snapshot = snapshot;
        Revision = revision;
        UpdatedAt = updatedAt;
    }

    public DefaultSettingsSnapshot Snapshot { get; }

    public int Revision { get; }

    public DateTimeOffset UpdatedAt { get; }
}

public sealed class StoredProjectSettings
{
    internal StoredProjectSettings(
        EntityId<ProjectIdTag> projectId,
        DefaultSettingsSnapshot initialSnapshot,
        DefaultSettingsSnapshot currentSnapshot,
        int revision,
        DateTimeOffset updatedAt)
    {
        ProjectId = projectId;
        InitialSnapshot = initialSnapshot;
        CurrentSnapshot = currentSnapshot;
        Revision = revision;
        UpdatedAt = updatedAt;
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public DefaultSettingsSnapshot InitialSnapshot { get; }

    public DefaultSettingsSnapshot CurrentSnapshot { get; }

    public int Revision { get; }

    public DateTimeOffset UpdatedAt { get; }
}

public sealed class ProjectSettingsResult
{
    private ProjectSettingsResult(
        EntityId<ProjectIdTag> projectId,
        ProjectSettingsBindingState bindingState,
        StoredProjectSettings? settings)
    {
        ProjectId = projectId;
        BindingState = bindingState;
        Settings = settings;
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ProjectSettingsBindingState BindingState { get; }

    public StoredProjectSettings? Settings { get; }

    public bool IsBound => BindingState == ProjectSettingsBindingState.Bound;

    internal static ProjectSettingsResult Unbound(EntityId<ProjectIdTag> projectId) =>
        new(projectId, ProjectSettingsBindingState.Unbound, settings: null);

    internal static ProjectSettingsResult Bound(StoredProjectSettings settings) =>
        new(settings.ProjectId, ProjectSettingsBindingState.Bound, settings);
}

public sealed class DefaultSettingsStore
{
    public const int MaximumCanonicalJsonUtf8Bytes = 1024 * 1024;

    private const string ProfileEntityKind = "default-settings profile";
    private const string GlobalEntityKind = "global default settings";
    private const string ProjectEntityKind = "project default settings";
    private const string GlobalEntityId = "singleton";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _databasePath;

    public DefaultSettingsStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public DefaultSettingsProfile RegisterProfile(byte[] canonicalProfileUtf8)
    {
        PreparedProfile prepared = ParseProfileInput(canonicalProfileUtf8);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        DefaultSettingsProfile? existing = ReadProfile(
            connection,
            transaction,
            prepared.Profile.Id,
            prepared.Profile.Version);
        if (existing is not null)
        {
            if (existing.ContentDigest != prepared.Profile.ContentDigest ||
                !string.Equals(
                    existing.CanonicalJson,
                    prepared.Profile.CanonicalJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A different default-settings profile is already registered for this ID and version.");
            }

            transaction.Commit();
            return existing;
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO default_settings_profiles (
                    profile_id,
                    profile_version,
                    profile_digest,
                    profile_payload
                )
                VALUES (
                    $profile_id,
                    $profile_version,
                    $profile_digest,
                    $profile_payload
                );
                """;
            AddProfileIdentityParameters(command, prepared.Profile);
            SqliteStoreSupport.AddBlob(
                command,
                "$profile_payload",
                prepared.CanonicalUtf8);
            EnsureSingleMutation(command, "The default-settings profile was not registered.");
        }

        DefaultSettingsProfile registered = ReadProfile(
            connection,
            transaction,
            prepared.Profile.Id,
            prepared.Profile.Version)
            ?? throw InvalidStoredProfile();
        transaction.Commit();
        return registered;
    }

    public DefaultSettingsProfile? GetProfile(
        EntityId<DefaultSettingsProfileIdTag> profileId,
        SettingsProfileVersion profileVersion)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(profileVersion);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        return ReadProfile(connection, transaction: null, profileId, profileVersion);
    }

    public IReadOnlyList<DefaultSettingsProfile> ListProfiles()
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = ProfileSelectColumns + """
            ORDER BY profile_id, profile_version;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var profiles = new List<DefaultSettingsProfile>();
        while (reader.Read())
        {
            profiles.Add(ReadStoredProfile(reader));
        }

        profiles.Sort(ProfileComparer.Instance);
        return Array.AsReadOnly(profiles.ToArray());
    }

    public StoredGlobalSettings? GetGlobalSettings()
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        return ReadGlobalSettings(connection, transaction: null);
    }

    public StoredGlobalSettings InitializeGlobalSettings(
        DefaultSettingsSnapshot snapshot,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        if (ReadGlobalSettings(connection, transaction) is not null)
        {
            throw new InvalidOperationException(
                "Global default settings are already initialized.");
        }

        DefaultSettingsProfile profile = ReadRequiredProfileForSnapshot(
            connection,
            transaction,
            snapshot,
            nameof(snapshot));
        PreparedSnapshot prepared = PrepareSnapshot(snapshot, profile, nameof(snapshot));

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO default_settings_global (
                    singleton_id,
                    profile_id,
                    profile_version,
                    profile_digest,
                    snapshot_digest,
                    snapshot_payload,
                    revision,
                    updated_at
                )
                VALUES (
                    1,
                    $profile_id,
                    $profile_version,
                    $profile_digest,
                    $snapshot_digest,
                    $snapshot_payload,
                    0,
                    $updated_at
                );
                """;
            AddSnapshotParameters(command, prepared);
            SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
            EnsureSingleMutation(command, "Global default settings were not initialized.");
        }

        StoredGlobalSettings stored = ReadGlobalSettings(connection, transaction)
            ?? throw InvalidStoredGlobalSettings();
        transaction.Commit();
        return stored;
    }

    public StoredGlobalSettings UpdateGlobalSettings(
        DefaultSettingsSnapshot snapshot,
        int expectedRevision,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateExpectedRevision(expectedRevision);
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredGlobalSettings current = ReadGlobalSettings(connection, transaction)
            ?? throw new InvalidOperationException(
                "Global default settings are not initialized.");
        EnsureExpectedRevision(
            GlobalEntityKind,
            GlobalEntityId,
            expectedRevision,
            current.Revision);
        EnsureTimestampDoesNotMoveBackwards(
            updatedAt,
            current.UpdatedAt,
            nameof(updatedAt),
            "A global-settings update timestamp must not move backwards.");

        DefaultSettingsProfile profile = ReadRequiredProfileForSnapshot(
            connection,
            transaction,
            snapshot,
            nameof(snapshot));
        PreparedSnapshot prepared = PrepareSnapshot(snapshot, profile, nameof(snapshot));
        int newRevision = checked(current.Revision + 1);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE default_settings_global
                SET profile_id = $profile_id,
                    profile_version = $profile_version,
                    profile_digest = $profile_digest,
                    snapshot_digest = $snapshot_digest,
                    snapshot_payload = $snapshot_payload,
                    revision = $new_revision,
                    updated_at = $updated_at
                WHERE singleton_id = 1
                  AND revision = $expected_revision;
                """;
            AddSnapshotParameters(command, prepared);
            SqliteStoreSupport.AddInteger(command, "$new_revision", newRevision);
            SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
            SqliteStoreSupport.AddInteger(
                command,
                "$expected_revision",
                expectedRevision);
            if (command.ExecuteNonQuery() != 1)
            {
                StoredGlobalSettings latest = ReadGlobalSettings(connection, transaction)
                    ?? throw InvalidStoredGlobalSettings();
                EnsureExpectedRevision(
                    GlobalEntityKind,
                    GlobalEntityId,
                    expectedRevision,
                    latest.Revision);
                throw InvalidStoredGlobalSettings();
            }
        }

        StoredGlobalSettings updated = ReadGlobalSettings(connection, transaction)
            ?? throw InvalidStoredGlobalSettings();
        if (updated.Revision != newRevision ||
            updated.Snapshot.ContentDigest != prepared.Snapshot.ContentDigest)
        {
            throw InvalidStoredGlobalSettings();
        }

        transaction.Commit();
        return updated;
    }

    public ProjectSettingsResult GetProjectSettings(EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        EnsureProjectExists(connection, transaction: null, projectId);
        StoredProjectSettings? settings = ReadProjectSettings(
            connection,
            transaction: null,
            projectId);
        return settings is null
            ? ProjectSettingsResult.Unbound(projectId)
            : ProjectSettingsResult.Bound(settings);
    }

    public StoredProjectSettings BindProjectSettings(
        EntityId<ProjectIdTag> projectId,
        DefaultSettingsSnapshot initialSnapshot,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(initialSnapshot);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredProjectSettings stored = BindProjectSettings(
            connection,
            transaction,
            projectId,
            initialSnapshot,
            updatedAt);
        transaction.Commit();
        return stored;
    }

    internal static StoredProjectSettings BindProjectSettings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId,
        DefaultSettingsSnapshot initialSnapshot,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));
        EnsureProjectExists(connection, transaction, projectId);
        DefaultSettingsProfile profile = ReadRequiredProfileForSnapshot(
            connection,
            transaction,
            initialSnapshot,
            nameof(initialSnapshot));
        PreparedSnapshot prepared = PrepareSnapshot(
            initialSnapshot,
            profile,
            nameof(initialSnapshot));
        StoredProjectSettings? existing = ReadProjectSettings(
            connection,
            transaction,
            projectId);
        if (existing is not null)
        {
            if (existing.InitialSnapshot.ContentDigest != prepared.Snapshot.ContentDigest ||
                !string.Equals(
                    existing.InitialSnapshot.CanonicalJson,
                    prepared.Snapshot.CanonicalJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The project is already bound to different initial default settings.");
            }

            return existing;
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO project_default_settings (
                    project_id,
                    profile_id,
                    profile_version,
                    profile_digest,
                    initial_snapshot_digest,
                    initial_snapshot_payload,
                    current_snapshot_digest,
                    current_snapshot_payload,
                    revision,
                    updated_at
                )
                VALUES (
                    $project_id,
                    $profile_id,
                    $profile_version,
                    $profile_digest,
                    $snapshot_digest,
                    $snapshot_payload,
                    $snapshot_digest,
                    $snapshot_payload,
                    0,
                    $updated_at
                );
                """;
            SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
            AddSnapshotParameters(command, prepared);
            SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
            EnsureSingleMutation(command, "Project default settings were not bound.");
        }

        StoredProjectSettings stored = ReadProjectSettings(
            connection,
            transaction,
            projectId)
            ?? throw InvalidStoredProjectSettings();
        return stored;
    }

    public StoredProjectSettings UpdateProjectSettings(
        EntityId<ProjectIdTag> projectId,
        DefaultSettingsSnapshot currentSnapshot,
        int expectedRevision,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ValidateExpectedRevision(expectedRevision);
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredProjectSettings current = ReadRequiredProjectSettings(
            connection,
            transaction,
            projectId);
        EnsureProjectUpdatePreconditions(
            current,
            expectedRevision,
            updatedAt,
            nameof(updatedAt));
        DefaultSettingsProfile profile = ReadRequiredBoundProfile(
            connection,
            transaction,
            current);
        PreparedSnapshot prepared = PrepareSnapshot(
            currentSnapshot,
            profile,
            nameof(currentSnapshot));
        EnsureProjectProfileBinding(current, prepared.Snapshot, nameof(currentSnapshot));

        StoredProjectSettings updated = UpdateProjectCurrentSnapshot(
            connection,
            transaction,
            current,
            prepared,
            expectedRevision,
            storedUpdatedAt);
        transaction.Commit();
        return updated;
    }

    public StoredProjectSettings ResetProjectSetting(
        EntityId<ProjectIdTag> projectId,
        string key,
        int expectedRevision,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(key);
        ValidateExpectedRevision(expectedRevision);
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredProjectSettings current = ReadRequiredProjectSettings(
            connection,
            transaction,
            projectId);
        EnsureProjectUpdatePreconditions(
            current,
            expectedRevision,
            updatedAt,
            nameof(updatedAt));
        DefaultSettingsProfile profile = ReadRequiredBoundProfile(
            connection,
            transaction,
            current);
        _ = profile.GetDefinition(key);
        DefaultSettingSelection initialValue = current.InitialSnapshot.Values.Single(
            value => string.Equals(value.Key, key, StringComparison.Ordinal));
        DefaultSettingsSnapshot resetSnapshot = profile.CreateSnapshot(
            current.CurrentSnapshot.Values
                .Select(value => new DefaultSettingSelection(
                    value.Key,
                    string.Equals(value.Key, key, StringComparison.Ordinal)
                        ? initialValue.Value
                        : value.Value))
                .ToArray());
        PreparedSnapshot prepared = PrepareSnapshot(
            resetSnapshot,
            profile,
            nameof(key));

        StoredProjectSettings updated = UpdateProjectCurrentSnapshot(
            connection,
            transaction,
            current,
            prepared,
            expectedRevision,
            storedUpdatedAt);
        transaction.Commit();
        return updated;
    }

    public StoredProjectSettings ResetAllProjectSettings(
        EntityId<ProjectIdTag> projectId,
        int expectedRevision,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ValidateExpectedRevision(expectedRevision);
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredProjectSettings current = ReadRequiredProjectSettings(
            connection,
            transaction,
            projectId);
        EnsureProjectUpdatePreconditions(
            current,
            expectedRevision,
            updatedAt,
            nameof(updatedAt));
        DefaultSettingsProfile profile = ReadRequiredBoundProfile(
            connection,
            transaction,
            current);
        PreparedSnapshot prepared = PrepareSnapshot(
            current.InitialSnapshot,
            profile,
            nameof(projectId));

        StoredProjectSettings updated = UpdateProjectCurrentSnapshot(
            connection,
            transaction,
            current,
            prepared,
            expectedRevision,
            storedUpdatedAt);
        transaction.Commit();
        return updated;
    }

    private static DefaultSettingsProfile? ReadProfile(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EntityId<DefaultSettingsProfileIdTag> profileId,
        SettingsProfileVersion profileVersion)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ProfileSelectColumns + """
            WHERE profile_id = $profile_id
              AND profile_version = $profile_version;
            """;
        SqliteStoreSupport.AddText(command, "$profile_id", profileId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$profile_version",
            profileVersion.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        DefaultSettingsProfile profile = ReadStoredProfile(reader);
        if (reader.Read() ||
            profile.Id != profileId ||
            profile.Version != profileVersion)
        {
            throw InvalidStoredProfile();
        }

        return profile;
    }

    private static DefaultSettingsProfile ReadStoredProfile(SqliteDataReader reader)
    {
        EntityId<DefaultSettingsProfileIdTag> profileId =
            SqliteStoreSupport.ReadEntityId<DefaultSettingsProfileIdTag>(
                reader,
                0,
                ProfileEntityKind);
        SettingsProfileVersion profileVersion = ReadProfileVersion(
            reader,
            1,
            ProfileEntityKind);
        ContentDigest profileDigest = SqliteStoreSupport.ReadContentDigest(
            reader,
            2,
            ProfileEntityKind);
        EnsureStoredPayloadLength(
            SqliteStoreSupport.ReadInt64(reader, 3, ProfileEntityKind),
            ProfileEntityKind);
        string canonicalJson = SqliteStoreSupport.ReadCanonicalPayload(
            reader,
            4,
            ProfileEntityKind);

        DefaultSettingsProfile profile;
        try
        {
            profile = DefaultSettingsProfile.ParseCanonical(canonicalJson);
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException)
        {
            throw InvalidStoredProfile();
        }

        if (profile.Id != profileId ||
            profile.Version != profileVersion ||
            profile.ContentDigest != profileDigest)
        {
            throw InvalidStoredProfile();
        }

        return profile;
    }

    private static StoredGlobalSettings? ReadGlobalSettings(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        GlobalSettingsRow? row = null;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    singleton_id,
                    profile_id,
                    profile_version,
                    profile_digest,
                    snapshot_digest,
                    length(CAST(snapshot_payload AS BLOB)),
                    CAST(snapshot_payload AS BLOB),
                    revision,
                    updated_at
                FROM default_settings_global
                ORDER BY singleton_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            long singletonId = SqliteStoreSupport.ReadInt64(
                reader,
                0,
                GlobalEntityKind);
            EntityId<DefaultSettingsProfileIdTag> profileId =
                SqliteStoreSupport.ReadEntityId<DefaultSettingsProfileIdTag>(
                    reader,
                    1,
                    GlobalEntityKind);
            SettingsProfileVersion profileVersion = ReadProfileVersion(
                reader,
                2,
                GlobalEntityKind);
            ContentDigest profileDigest = SqliteStoreSupport.ReadContentDigest(
                reader,
                3,
                GlobalEntityKind);
            ContentDigest snapshotDigest = SqliteStoreSupport.ReadContentDigest(
                reader,
                4,
                GlobalEntityKind);
            EnsureStoredPayloadLength(
                SqliteStoreSupport.ReadInt64(reader, 5, GlobalEntityKind),
                GlobalEntityKind);
            string snapshotJson = SqliteStoreSupport.ReadCanonicalPayload(
                reader,
                6,
                GlobalEntityKind);
            int revision = SqliteStoreSupport.ReadInt32(
                reader,
                7,
                GlobalEntityKind);
            DateTimeOffset updatedAt = SqliteStoreSupport.ReadTimestamp(
                reader,
                8,
                GlobalEntityKind);
            if (singletonId != 1 || revision < 0 || reader.Read())
            {
                throw InvalidStoredGlobalSettings();
            }

            row = new GlobalSettingsRow(
                profileId,
                profileVersion,
                profileDigest,
                snapshotDigest,
                snapshotJson,
                revision,
                updatedAt);
        }

        DefaultSettingsProfile profile = ReadProfile(
            connection,
            transaction,
            row.ProfileId,
            row.ProfileVersion)
            ?? throw InvalidStoredGlobalSettings();
        if (profile.ContentDigest != row.ProfileDigest)
        {
            throw InvalidStoredGlobalSettings();
        }

        DefaultSettingsSnapshot snapshot = ParseStoredSnapshot(
            row.SnapshotJson,
            row.SnapshotDigest,
            profile,
            GlobalEntityKind);
        return new StoredGlobalSettings(snapshot, row.Revision, row.UpdatedAt);
    }

    private static StoredProjectSettings? ReadProjectSettings(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EntityId<ProjectIdTag> projectId)
    {
        ProjectSettingsRow? row = null;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    project_id,
                    profile_id,
                    profile_version,
                    profile_digest,
                    initial_snapshot_digest,
                    length(CAST(initial_snapshot_payload AS BLOB)),
                    CAST(initial_snapshot_payload AS BLOB),
                    current_snapshot_digest,
                    length(CAST(current_snapshot_payload AS BLOB)),
                    CAST(current_snapshot_payload AS BLOB),
                    revision,
                    updated_at
                FROM project_default_settings
                WHERE project_id = $project_id;
                """;
            SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            EntityId<ProjectIdTag> storedProjectId =
                SqliteStoreSupport.ReadEntityId<ProjectIdTag>(
                    reader,
                    0,
                    ProjectEntityKind);
            EntityId<DefaultSettingsProfileIdTag> profileId =
                SqliteStoreSupport.ReadEntityId<DefaultSettingsProfileIdTag>(
                    reader,
                    1,
                    ProjectEntityKind);
            SettingsProfileVersion profileVersion = ReadProfileVersion(
                reader,
                2,
                ProjectEntityKind);
            ContentDigest profileDigest = SqliteStoreSupport.ReadContentDigest(
                reader,
                3,
                ProjectEntityKind);
            ContentDigest initialSnapshotDigest =
                SqliteStoreSupport.ReadContentDigest(
                    reader,
                    4,
                    ProjectEntityKind);
            EnsureStoredPayloadLength(
                SqliteStoreSupport.ReadInt64(reader, 5, ProjectEntityKind),
                ProjectEntityKind);
            string initialSnapshotJson = SqliteStoreSupport.ReadCanonicalPayload(
                reader,
                6,
                ProjectEntityKind);
            ContentDigest currentSnapshotDigest =
                SqliteStoreSupport.ReadContentDigest(
                    reader,
                    7,
                    ProjectEntityKind);
            EnsureStoredPayloadLength(
                SqliteStoreSupport.ReadInt64(reader, 8, ProjectEntityKind),
                ProjectEntityKind);
            string currentSnapshotJson = SqliteStoreSupport.ReadCanonicalPayload(
                reader,
                9,
                ProjectEntityKind);
            int revision = SqliteStoreSupport.ReadInt32(
                reader,
                10,
                ProjectEntityKind);
            DateTimeOffset updatedAt = SqliteStoreSupport.ReadTimestamp(
                reader,
                11,
                ProjectEntityKind);
            if (storedProjectId != projectId || revision < 0 || reader.Read())
            {
                throw InvalidStoredProjectSettings();
            }

            row = new ProjectSettingsRow(
                storedProjectId,
                profileId,
                profileVersion,
                profileDigest,
                initialSnapshotDigest,
                initialSnapshotJson,
                currentSnapshotDigest,
                currentSnapshotJson,
                revision,
                updatedAt);
        }

        DefaultSettingsProfile profile = ReadProfile(
            connection,
            transaction,
            row.ProfileId,
            row.ProfileVersion)
            ?? throw InvalidStoredProjectSettings();
        if (profile.ContentDigest != row.ProfileDigest)
        {
            throw InvalidStoredProjectSettings();
        }

        DefaultSettingsSnapshot initialSnapshot = ParseStoredSnapshot(
            row.InitialSnapshotJson,
            row.InitialSnapshotDigest,
            profile,
            ProjectEntityKind);
        DefaultSettingsSnapshot currentSnapshot = ParseStoredSnapshot(
            row.CurrentSnapshotJson,
            row.CurrentSnapshotDigest,
            profile,
            ProjectEntityKind);
        if (row.Revision == 0 &&
            initialSnapshot.ContentDigest != currentSnapshot.ContentDigest)
        {
            throw InvalidStoredProjectSettings();
        }

        return new StoredProjectSettings(
            row.ProjectId,
            initialSnapshot,
            currentSnapshot,
            row.Revision,
            row.UpdatedAt);
    }

    private static StoredProjectSettings ReadRequiredProjectSettings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId)
    {
        EnsureProjectExists(connection, transaction, projectId);
        return ReadProjectSettings(connection, transaction, projectId)
            ?? throw new InvalidOperationException(
                "The project has no bound default-settings snapshot.");
    }

    private static DefaultSettingsProfile ReadRequiredBoundProfile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProjectSettings settings)
    {
        DefaultSettingsSnapshot initial = settings.InitialSnapshot;
        DefaultSettingsProfile profile = ReadProfile(
            connection,
            transaction,
            initial.ProfileId,
            initial.ProfileVersion)
            ?? throw InvalidStoredProjectSettings();
        if (profile.ContentDigest != initial.ProfileDigest)
        {
            throw InvalidStoredProjectSettings();
        }

        return profile;
    }

    private static StoredProjectSettings UpdateProjectCurrentSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProjectSettings current,
        PreparedSnapshot prepared,
        int expectedRevision,
        string storedUpdatedAt)
    {
        int newRevision = checked(current.Revision + 1);
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE project_default_settings
                SET current_snapshot_digest = $snapshot_digest,
                    current_snapshot_payload = $snapshot_payload,
                    revision = $new_revision,
                    updated_at = $updated_at
                WHERE project_id = $project_id
                  AND revision = $expected_revision;
                """;
            SqliteStoreSupport.AddText(
                command,
                "$snapshot_digest",
                prepared.Snapshot.ContentDigest.Hex);
            SqliteStoreSupport.AddBlob(
                command,
                "$snapshot_payload",
                prepared.CanonicalUtf8);
            SqliteStoreSupport.AddInteger(command, "$new_revision", newRevision);
            SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
            SqliteStoreSupport.AddText(
                command,
                "$project_id",
                current.ProjectId.ToString());
            SqliteStoreSupport.AddInteger(
                command,
                "$expected_revision",
                expectedRevision);
            if (command.ExecuteNonQuery() != 1)
            {
                StoredProjectSettings latest = ReadProjectSettings(
                    connection,
                    transaction,
                    current.ProjectId)
                    ?? throw InvalidStoredProjectSettings();
                EnsureExpectedRevision(
                    ProjectEntityKind,
                    current.ProjectId.ToString(),
                    expectedRevision,
                    latest.Revision);
                throw InvalidStoredProjectSettings();
            }
        }

        StoredProjectSettings updated = ReadProjectSettings(
            connection,
            transaction,
            current.ProjectId)
            ?? throw InvalidStoredProjectSettings();
        if (updated.Revision != newRevision ||
            updated.InitialSnapshot.ContentDigest != current.InitialSnapshot.ContentDigest ||
            updated.CurrentSnapshot.ContentDigest != prepared.Snapshot.ContentDigest)
        {
            throw InvalidStoredProjectSettings();
        }

        return updated;
    }

    private static DefaultSettingsProfile ReadRequiredProfileForSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DefaultSettingsSnapshot snapshot,
        string parameterName)
    {
        DefaultSettingsProfile profile = ReadProfile(
            connection,
            transaction,
            snapshot.ProfileId,
            snapshot.ProfileVersion)
            ?? throw new InvalidOperationException(
                "The snapshot's default-settings profile is not registered.");
        if (profile.ContentDigest != snapshot.ProfileDigest)
        {
            throw new ArgumentException(
                "The snapshot does not match the registered default-settings profile.",
                parameterName);
        }

        return profile;
    }

    private static PreparedSnapshot PrepareSnapshot(
        DefaultSettingsSnapshot snapshot,
        DefaultSettingsProfile profile,
        string parameterName)
    {
        if (snapshot.ProfileId != profile.Id ||
            snapshot.ProfileVersion != profile.Version ||
            snapshot.ProfileDigest != profile.ContentDigest)
        {
            throw new ArgumentException(
                "The snapshot does not match the required default-settings profile.",
                parameterName);
        }

        byte[] canonicalUtf8;
        try
        {
            canonicalUtf8 = StrictUtf8.GetBytes(snapshot.CanonicalJson);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "The default-settings snapshot must contain valid Unicode.",
                parameterName,
                exception);
        }

        EnsureInputPayloadLength(
            canonicalUtf8.Length,
            parameterName,
            "The default-settings snapshot exceeds the supported byte limit.");
        DefaultSettingsSnapshot parsed;
        try
        {
            parsed = DefaultSettingsSnapshot.ParseCanonical(
                StrictUtf8.GetString(canonicalUtf8),
                profile);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The default-settings snapshot is invalid.",
                parameterName,
                exception);
        }

        if (parsed.ContentDigest != snapshot.ContentDigest)
        {
            throw new ArgumentException(
                "The default-settings snapshot digest is invalid.",
                parameterName);
        }

        return new PreparedSnapshot(parsed, canonicalUtf8);
    }

    private static PreparedProfile ParseProfileInput(byte[] canonicalProfileUtf8)
    {
        ArgumentNullException.ThrowIfNull(canonicalProfileUtf8);
        EnsureInputPayloadLength(
            canonicalProfileUtf8.Length,
            nameof(canonicalProfileUtf8),
            "The default-settings profile exceeds the supported byte limit.");
        byte[] copiedUtf8 = canonicalProfileUtf8.ToArray();

        string canonicalJson;
        try
        {
            canonicalJson = StrictUtf8.GetString(copiedUtf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                "The default-settings profile must be valid UTF-8.",
                nameof(canonicalProfileUtf8),
                exception);
        }

        DefaultSettingsProfile profile;
        try
        {
            profile = DefaultSettingsProfile.ParseCanonical(canonicalJson);
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "The default-settings profile must be valid canonical JSON.",
                nameof(canonicalProfileUtf8),
                exception);
        }

        byte[] regenerated = StrictUtf8.GetBytes(profile.CanonicalJson);
        if (!copiedUtf8.AsSpan().SequenceEqual(regenerated))
        {
            throw new ArgumentException(
                "The default-settings profile must use exact canonical UTF-8 bytes.",
                nameof(canonicalProfileUtf8));
        }

        return new PreparedProfile(profile, copiedUtf8);
    }

    private static DefaultSettingsSnapshot ParseStoredSnapshot(
        string canonicalJson,
        ContentDigest expectedDigest,
        DefaultSettingsProfile profile,
        string entityKind)
    {
        try
        {
            DefaultSettingsSnapshot snapshot =
                DefaultSettingsSnapshot.ParseCanonical(canonicalJson, profile);
            if (snapshot.ContentDigest != expectedDigest)
            {
                throw SqliteStoreSupport.InvalidStoredData(entityKind);
            }

            return snapshot;
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }
    }

    private static SettingsProfileVersion ReadProfileVersion(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        string value = SqliteStoreSupport.ReadString(reader, ordinal, entityKind);
        if (!SettingsProfileVersion.TryParse(value, out SettingsProfileVersion? version))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return version;
    }

    private static void EnsureProjectExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EntityId<ProjectIdTag> projectId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM projects
            WHERE id = $project_id;
            """;
        SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The project does not exist.");
        }

        EntityId<ProjectIdTag> storedId =
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 0, "project");
        if (storedId != projectId || reader.Read())
        {
            throw SqliteStoreSupport.InvalidStoredData("project");
        }
    }

    private static void EnsureProjectUpdatePreconditions(
        StoredProjectSettings current,
        int expectedRevision,
        DateTimeOffset updatedAt,
        string timestampParameterName)
    {
        EnsureExpectedRevision(
            ProjectEntityKind,
            current.ProjectId.ToString(),
            expectedRevision,
            current.Revision);
        EnsureTimestampDoesNotMoveBackwards(
            updatedAt,
            current.UpdatedAt,
            timestampParameterName,
            "A project-settings update timestamp must not move backwards.");
    }

    private static void EnsureProjectProfileBinding(
        StoredProjectSettings current,
        DefaultSettingsSnapshot replacement,
        string parameterName)
    {
        DefaultSettingsSnapshot initial = current.InitialSnapshot;
        if (replacement.ProfileId != initial.ProfileId ||
            replacement.ProfileVersion != initial.ProfileVersion ||
            replacement.ProfileDigest != initial.ProfileDigest)
        {
            throw new ArgumentException(
                "Project settings cannot change their initial profile binding.",
                parameterName);
        }
    }

    private static void AddProfileIdentityParameters(
        SqliteCommand command,
        DefaultSettingsProfile profile)
    {
        SqliteStoreSupport.AddText(command, "$profile_id", profile.Id.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$profile_version",
            profile.Version.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$profile_digest",
            profile.ContentDigest.Hex);
    }

    private static void AddSnapshotParameters(
        SqliteCommand command,
        PreparedSnapshot prepared)
    {
        SqliteStoreSupport.AddText(
            command,
            "$profile_id",
            prepared.Snapshot.ProfileId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$profile_version",
            prepared.Snapshot.ProfileVersion.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$profile_digest",
            prepared.Snapshot.ProfileDigest.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$snapshot_digest",
            prepared.Snapshot.ContentDigest.Hex);
        SqliteStoreSupport.AddBlob(
            command,
            "$snapshot_payload",
            prepared.CanonicalUtf8);
    }

    private static void EnsureInputPayloadLength(
        int byteLength,
        string parameterName,
        string message)
    {
        if (byteLength is <= 0 or > MaximumCanonicalJsonUtf8Bytes)
        {
            throw new ArgumentException(message, parameterName);
        }
    }

    private static void EnsureStoredPayloadLength(long byteLength, string entityKind)
    {
        if (byteLength is <= 0 or > MaximumCanonicalJsonUtf8Bytes)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }
    }

    private static void ValidateExpectedRevision(int expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "An expected settings revision must not be negative.");
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

    private static void EnsureTimestampDoesNotMoveBackwards(
        DateTimeOffset candidate,
        DateTimeOffset current,
        string parameterName,
        string message)
    {
        if (candidate.ToUniversalTime() < current)
        {
            throw new ArgumentOutOfRangeException(parameterName, message);
        }
    }

    private static void EnsureSingleMutation(SqliteCommand command, string message)
    {
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static InvalidOperationException InvalidStoredProfile() =>
        SqliteStoreSupport.InvalidStoredData(ProfileEntityKind);

    private static InvalidOperationException InvalidStoredGlobalSettings() =>
        SqliteStoreSupport.InvalidStoredData(GlobalEntityKind);

    private static InvalidOperationException InvalidStoredProjectSettings() =>
        SqliteStoreSupport.InvalidStoredData(ProjectEntityKind);

    private const string ProfileSelectColumns = """
        SELECT
            profile_id,
            profile_version,
            profile_digest,
            length(CAST(profile_payload AS BLOB)),
            CAST(profile_payload AS BLOB)
        FROM default_settings_profiles
        """ + "\n";

    private sealed record PreparedProfile(
        DefaultSettingsProfile Profile,
        byte[] CanonicalUtf8);

    private sealed record PreparedSnapshot(
        DefaultSettingsSnapshot Snapshot,
        byte[] CanonicalUtf8);

    private sealed record GlobalSettingsRow(
        EntityId<DefaultSettingsProfileIdTag> ProfileId,
        SettingsProfileVersion ProfileVersion,
        ContentDigest ProfileDigest,
        ContentDigest SnapshotDigest,
        string SnapshotJson,
        int Revision,
        DateTimeOffset UpdatedAt);

    private sealed record ProjectSettingsRow(
        EntityId<ProjectIdTag> ProjectId,
        EntityId<DefaultSettingsProfileIdTag> ProfileId,
        SettingsProfileVersion ProfileVersion,
        ContentDigest ProfileDigest,
        ContentDigest InitialSnapshotDigest,
        string InitialSnapshotJson,
        ContentDigest CurrentSnapshotDigest,
        string CurrentSnapshotJson,
        int Revision,
        DateTimeOffset UpdatedAt);

    private sealed class ProfileComparer : IComparer<DefaultSettingsProfile>
    {
        internal static ProfileComparer Instance { get; } = new();

        public int Compare(DefaultSettingsProfile? left, DefaultSettingsProfile? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int idComparison = StringComparer.Ordinal.Compare(
                left.Id.ToString(),
                right.Id.ToString());
            if (idComparison != 0)
            {
                return idComparison;
            }

            int majorComparison = left.Version.Major.CompareTo(right.Version.Major);
            if (majorComparison != 0)
            {
                return majorComparison;
            }

            int minorComparison = left.Version.Minor.CompareTo(right.Version.Minor);
            return minorComparison != 0
                ? minorComparison
                : left.Version.Patch.CompareTo(right.Version.Patch);
        }
    }
}
