using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.App.Web;

[SupportedOSPlatform("windows")]
internal sealed class SettingsFileBridgeRequestHandler
{
    private readonly DefaultSettingsStore store;
    private readonly Func<SettingsTextFile> createFile;
    private readonly ISettingsBridgeAuthorization authorization;
    private readonly Func<DateTimeOffset> clock;

    public SettingsFileBridgeRequestHandler(
        DefaultSettingsStore store,
        SettingsTextFile file,
        ISettingsBridgeAuthorization authorization,
        Func<DateTimeOffset>? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(file);
        createFile = () => file;
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    // Keep the concrete public constructor; native startup defers file-only validation.
    internal SettingsFileBridgeRequestHandler(
        DefaultSettingsStore store,
        ISettingsBridgeAuthorization authorization,
        Func<SettingsTextFile> createFile,
        Func<DateTimeOffset>? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.createFile = createFile ?? throw new ArgumentNullException(nameof(createFile));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal string Handle(BridgeRequest request) => Handle(request, CancellationToken.None);

    internal string Handle(BridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.Method is not (BridgeMethod.SettingsFileExport or
                BridgeMethod.SettingsFilePreview or BridgeMethod.SettingsFileApply))
            {
                return WriteFailure(request, Status.Invalid);
            }

            // Authorization precedes target lookups and all file inspection, including no-ops.
            if (request.Method == BridgeMethod.SettingsFileApply)
            {
                authorization.DemandEdit();
            }
            else
            {
                authorization.DemandRead();
            }

            if (!SettingsBridgeProtocol.HasValidParameters(request.Method, request.Parameters))
            {
                return WriteFailure(request, Status.Invalid);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CurrentSettings? current = ReadCurrent(request, out Status failure);
            if (current is null)
            {
                return WriteFailure(request, failure);
            }

            if (!MatchesExpectedIdentity(request, current))
            {
                return WriteFailure(request, Status.Conflict);
            }

            DefaultSettingsProfile profile = RequireProfile(current.Snapshot);
            // A failed factory stays inside the file response boundary and can be retried.
            cancellationToken.ThrowIfCancellationRequested();
            SettingsTextFile file = createFile();
            if (request.Method == BridgeMethod.SettingsFileExport)
            {
                SettingsTextFileIdentity exported = file.Export(
                    current.Snapshot,
                    ReadExpectedFileSha256(request),
                    cancellationToken);
                return WriteAvailable(request, current, profile, current.Snapshot, exported.FileSha256);
            }

            SettingsTextFileReadResult read = file.Read(profile, cancellationToken);
            if (request.Method == BridgeMethod.SettingsFileApply &&
                read.Identity.FileSha256 != ReadExpectedFileSha256(request))
            {
                return WriteFailure(request, Status.Conflict);
            }

            // A file read can wait for another process. Do not return a stale preview or
            // accept a stale no-op if the target changed while the file was being read.
            cancellationToken.ThrowIfCancellationRequested();
            current = ReadCurrent(request, out failure);
            if (current is null)
            {
                return WriteFailure(request, failure);
            }

            if (!MatchesExpectedIdentity(request, current))
            {
                return WriteFailure(request, Status.Conflict);
            }

            if (request.Method == BridgeMethod.SettingsFilePreview)
            {
                return WriteAvailable(request, current, profile, read.Snapshot, read.Identity.FileSha256);
            }

            if (read.Snapshot.ContentDigest == current.Snapshot.ContentDigest)
            {
                return WriteAvailable(request, current, profile, current.Snapshot, read.Identity.FileSha256);
            }

            DateTimeOffset now = clock();
            DateTimeOffset updatedAt = now < current.UpdatedAt ? current.UpdatedAt : now;
            cancellationToken.ThrowIfCancellationRequested();
            CurrentSettings updated = current.ProjectId is null
                ? CurrentSettings.From(store.UpdateGlobalSettings(read.Snapshot, current.Revision, updatedAt))
                : CurrentSettings.From(store.UpdateProjectSettings(
                    current.ProjectId, read.Snapshot, current.Revision, updatedAt));

            // No cancellation check or rollback after a committed store update. A lost
            // response must be reconciled by reading saved state, never guessed away.
            return WriteAvailable(request, updated, profile, updated.Snapshot, read.Identity.FileSha256);
        }
        catch (AuthorizationException)
        {
            return WriteFailure(request, Status.Forbidden);
        }
        catch (SettingsTextFileException exception)
        {
            return WriteFileFailure(request, exception);
        }
        catch (StoreConcurrencyException)
        {
            return WriteFailure(request, Status.Conflict);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return WriteFailure(request, Status.Invalid);
        }
        catch (Exception)
        {
            // No path, SID, file body, SQL, exception message, or inner exception crosses the bridge.
            return WriteFailure(request, Status.Unavailable);
        }
    }

    private CurrentSettings? ReadCurrent(BridgeRequest request, out Status failure)
    {
        failure = Status.Unavailable;
        if (request.Parameters.GetProperty("projectId").ValueKind == JsonValueKind.Null)
        {
            StoredGlobalSettings? global = store.GetGlobalSettings();
            return global is null ? null : CurrentSettings.From(global);
        }

        ProjectSettingsResult project;
        try
        {
            project = store.GetProjectSettings(SettingsBridgeProtocol.ReadProjectId(request));
        }
        catch (InvalidOperationException)
        {
            // Preserve the existing project bridge's closed lookup status without parsing messages.
            failure = Status.NotFound;
            return null;
        }

        if (!project.IsBound)
        {
            failure = Status.Unbound;
            return null;
        }

        return CurrentSettings.From(project.Settings!);
    }

    private DefaultSettingsProfile RequireProfile(DefaultSettingsSnapshot snapshot)
    {
        // Resolve the actual persisted binding, never the request's or latest profile.
        DefaultSettingsProfile profile = store.GetProfile(snapshot.ProfileId, snapshot.ProfileVersion) ??
            throw new InvalidOperationException("The bound settings profile is unavailable.");
        if (profile.ContentDigest != snapshot.ProfileDigest)
        {
            throw new InvalidOperationException("The bound settings profile does not match the snapshot.");
        }

        return profile;
    }

    private static bool MatchesExpectedIdentity(BridgeRequest request, CurrentSettings current)
    {
        JsonElement parameters = request.Parameters;
        DefaultSettingsSnapshot snapshot = current.Snapshot;
        return current.Revision == SettingsBridgeProtocol.ReadExpectedRevision(request) &&
            string.Equals(parameters.GetProperty("expectedProfileId").GetString(),
                snapshot.ProfileId.ToString(), StringComparison.Ordinal) &&
            string.Equals(parameters.GetProperty("expectedProfileVersion").GetString(),
                snapshot.ProfileVersion.ToString(), StringComparison.Ordinal) &&
            string.Equals(parameters.GetProperty("expectedProfileDigest").GetString(),
                snapshot.ProfileDigest.Hex, StringComparison.Ordinal) &&
            string.Equals(parameters.GetProperty("expectedSnapshotDigest").GetString(),
                snapshot.ContentDigest.Hex, StringComparison.Ordinal);
    }

    private static ContentDigest? ReadExpectedFileSha256(BridgeRequest request)
    {
        string? value = request.Parameters.GetProperty("expectedFileSha256").GetString();
        if (value is null)
        {
            return null;
        }

        return ContentDigest.TryParse(value, out ContentDigest? digest)
            ? digest
            : throw new ArgumentException("The expected settings file hash is invalid.", nameof(request));
    }

    private static string WriteFileFailure(BridgeRequest request, SettingsTextFileException exception)
    {
        if (exception.ReconciliationRequired)
        {
            return WriteFailure(request, Status.Unavailable);
        }

        Status status = exception.Reason switch
        {
            SettingsTextFileFailureReason.Missing => Status.FileNotFound,
            SettingsTextFileFailureReason.Invalid or SettingsTextFileFailureReason.TooLarge => Status.Invalid,
            SettingsTextFileFailureReason.ProfileMismatch => Status.ProfileMismatch,
            SettingsTextFileFailureReason.SnapshotMismatch => Status.SnapshotMismatch,
            SettingsTextFileFailureReason.OverwriteRequired when exception.ExistingFileSha256 is not null =>
                Status.OverwriteRequired,
            SettingsTextFileFailureReason.Conflict => Status.Conflict,
            SettingsTextFileFailureReason.AccessDenied or SettingsTextFileFailureReason.UnsafePath or
                SettingsTextFileFailureReason.Unavailable => Status.Unavailable,
            _ => Status.Unavailable,
        };
        return WriteFailure(request, status, exception.ExistingFileSha256);
    }

    private static string WriteFailure(BridgeRequest request, Status status, ContentDigest? fileSha256 = null) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", FormatStatus(status));
            writer.WriteNull("snapshot");
            writer.WriteString("fileSha256", status == Status.OverwriteRequired ? fileSha256?.Hex : null);
            writer.WriteEndObject();
        });

    private static string WriteAvailable(
        BridgeRequest request,
        CurrentSettings current,
        DefaultSettingsProfile profile,
        DefaultSettingsSnapshot snapshot,
        ContentDigest fileSha256) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", "available");
            writer.WritePropertyName("snapshot");
            WriteSnapshot(writer, current, profile, snapshot);
            writer.WriteString("fileSha256", fileSha256.Hex);
            writer.WriteEndObject();
        });

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        CurrentSettings current,
        DefaultSettingsProfile profile,
        DefaultSettingsSnapshot snapshot)
    {
        DefaultSettingsSnapshot initial = current.InitialSnapshot ?? profile.CreateSnapshot();
        writer.WriteStartObject();
        writer.WriteString("projectId", current.ProjectId?.ToString());
        writer.WriteString("profileId", profile.Id.ToString());
        writer.WriteString("profileName", profile.Name);
        writer.WriteString("profileVersion", profile.Version.ToString());
        writer.WriteString("profileDigest", profile.ContentDigest.Hex);
        writer.WriteString("initialSnapshotDigest", initial.ContentDigest.Hex);
        writer.WriteString("snapshotDigest", snapshot.ContentDigest.Hex);
        writer.WriteNumber("revision", current.Revision);
        writer.WriteString("updatedAt", current.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteNumber("definitionCount", profile.Definitions.Count);
        writer.WriteStartArray("definitions");
        foreach (DefaultSettingDefinition definition in profile.Definitions)
        {
            SettingsBridgeJson.WriteDefinition(writer, definition);
        }

        writer.WriteEndArray();
        SettingsBridgeJson.WriteSelections(writer, "initialValues", initial.Values);
        SettingsBridgeJson.WriteSelections(writer, "values", snapshot.Values);
        writer.WriteEndObject();
    }

    private static string FormatStatus(Status status) => status switch
    {
        Status.Forbidden => "forbidden",
        Status.NotFound => "notFound",
        Status.Unbound => "unbound",
        Status.Conflict => "conflict",
        Status.Invalid => "invalid",
        Status.Unavailable => "unavailable",
        Status.FileNotFound => "fileNotFound",
        Status.ProfileMismatch => "profileMismatch",
        Status.SnapshotMismatch => "snapshotMismatch",
        Status.OverwriteRequired => "overwriteRequired",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private enum Status
    {
        Forbidden,
        NotFound,
        Unbound,
        Conflict,
        Invalid,
        Unavailable,
        FileNotFound,
        ProfileMismatch,
        SnapshotMismatch,
        OverwriteRequired,
    }

    // Scope-specific saved state only; candidate values are kept separate until a store commit.
    private sealed record CurrentSettings(
        EntityId<ProjectIdTag>? ProjectId,
        DefaultSettingsSnapshot Snapshot,
        DefaultSettingsSnapshot? InitialSnapshot,
        int Revision,
        DateTimeOffset UpdatedAt)
    {
        internal static CurrentSettings From(StoredGlobalSettings settings) =>
            new(null, settings.Snapshot, null, settings.Revision, settings.UpdatedAt);

        internal static CurrentSettings From(StoredProjectSettings settings) =>
            new(settings.ProjectId, settings.CurrentSnapshot, settings.InitialSnapshot, settings.Revision, settings.UpdatedAt);
    }
}