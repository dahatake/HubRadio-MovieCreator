using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Settings;

public enum SettingsTextFileFailureReason
{
    Missing,
    Invalid,
    TooLarge,
    ProfileMismatch,
    SnapshotMismatch,
    OverwriteRequired,
    Conflict,
    AccessDenied,
    UnsafePath,
    Unavailable,
}

public sealed class SettingsTextFileException : Exception
{
    internal SettingsTextFileException(
        SettingsTextFileFailureReason reason,
        ContentDigest? existingFileSha256 = null,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ExistingFileSha256 = reason == SettingsTextFileFailureReason.OverwriteRequired
            ? existingFileSha256
            : null;
        ReconciliationRequired = reconciliationRequired;
    }

    public SettingsTextFileFailureReason Reason { get; }

    public ContentDigest? ExistingFileSha256 { get; }

    // Re-inspect after uncertain publication or recovery; retain any private rollback copy.
    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(SettingsTextFileFailureReason reason) =>
        reason switch
        {
            SettingsTextFileFailureReason.Missing => "The settings file is missing.",
            SettingsTextFileFailureReason.Invalid => "The settings file input is invalid.",
            SettingsTextFileFailureReason.TooLarge => "The settings file size limit was exceeded.",
            SettingsTextFileFailureReason.ProfileMismatch => "The settings profile does not match.",
            SettingsTextFileFailureReason.SnapshotMismatch => "The settings snapshot digest does not match.",
            SettingsTextFileFailureReason.OverwriteRequired => "Replacing the settings file requires confirmation.",
            SettingsTextFileFailureReason.Conflict => "The settings file identity has changed.",
            SettingsTextFileFailureReason.AccessDenied => "The settings file access control requirements were not met.",
            SettingsTextFileFailureReason.UnsafePath => "The settings file path is unsafe.",
            _ => "The settings file operation is unavailable.",
        };
}

public sealed class SettingsTextFileIdentity
{
    internal SettingsTextFileIdentity(ContentDigest fileSha256, long byteSize)
    {
        FileSha256 = fileSha256;
        ByteSize = byteSize;
    }

    public ContentDigest FileSha256 { get; }

    public long ByteSize { get; }
}

public sealed class SettingsTextFileReadResult
{
    internal SettingsTextFileReadResult(DefaultSettingsSnapshot snapshot, SettingsTextFileIdentity identity)
    {
        Snapshot = snapshot;
        Identity = identity;
    }

    public DefaultSettingsSnapshot Snapshot { get; }

    public SettingsTextFileIdentity Identity { get; }
}

// Explicit file I/O at the native-selected root; no startup import or directory ACL changes.
[SupportedOSPlatform("windows")]
public sealed class SettingsTextFile
{
    public const string FileName = "setting.txt";
    public const int MaximumRawUtf8Bytes = 4 * 1024 * 1024;
    public const int MaximumCanonicalJsonUtf8Bytes = 1024 * 1024;

    private const int FileBufferSize = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SettingsTextFileIdentity EmptyIdentity =
        new(ContentDigest.Compute(ReadOnlySpan<byte>.Empty), 0);

    private readonly string _appRoot;
    private readonly string _filePath;
    private readonly string _operationMutexName;
    private readonly SecurityIdentifier _actorSid;
    private readonly Action? _beforeCommit;
    private readonly Action? _afterFinalVerification;
    private readonly Action? _beforeRecoveryCommit;

    public SettingsTextFile(string appRoot, string currentActorSid)
        : this(appRoot, currentActorSid, beforeCommit: null)
    {
    }

    // Narrow test seams after staging, after final verification, and before recovery replacement.
    internal SettingsTextFile(
        string appRoot,
        string currentActorSid,
        Action? beforeCommit,
        Action? afterFinalVerification = null,
        Action? beforeRecoveryCommit = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Failure(SettingsTextFileFailureReason.Unavailable);
        }

        try
        {
            _appRoot = NormalizeAppRoot(appRoot);
            _filePath = Path.Combine(_appRoot, FileName);
            _actorSid = ParseCurrentActorSid(currentActorSid);
            _beforeCommit = beforeCommit;
            _afterFinalVerification = afterFinalVerification;
            _beforeRecoveryCommit = beforeRecoveryCommit;
            ValidateRootAndActor();
            using Process process = Process.GetCurrentProcess();
            string key = string.Concat(
                _appRoot.ToUpperInvariant(), "\n", _actorSid.Value, "\n",
                process.SessionId.ToString(CultureInfo.InvariantCulture));
            _operationMutexName = string.Concat(
                @"Local\HubRadio.SettingsTextFile.", Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(key))));
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Translate(exception);
        }
    }

    // Exports raw-byte evidence; a null expected hash permits creation only.
    public SettingsTextFileIdentity Export(
        DefaultSettingsSnapshot snapshot,
        ContentDigest? expectedExistingSha256,
        CancellationToken cancellationToken = default)
    {
        Mutex? operationMutex = null;
        try
        {
            operationMutex = AcquireOperationMutex(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRootAndActor();
            byte[] bytes = Serialize(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            SettingsTextFileIdentity? original = InspectPath(_filePath, cancellationToken);
            CheckExpected(original, expectedExistingSha256);
            var candidate = new SettingsTextFileIdentity(ContentDigest.Compute(bytes), bytes.LongLength);

            // Assigned only after exclusive creation succeeds; a name collision is never ours.
            string stagingPath = WriteNewOwnedFile(bytes, cancellationToken);
            try
            {
                return original is null
                    ? PublishNew(stagingPath, candidate, cancellationToken)
                    : ReplaceExisting(stagingPath, candidate, original, cancellationToken);
            }
            finally
            {
                _ = TryDeleteOwnedFile(stagingPath, candidate);
            }
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Translate(exception);
        }
        finally
        {
            operationMutex?.ReleaseMutex();
            operationMutex?.Dispose();
        }
    }

    // Reads only the fixed file, without applying settings or repairing its contents.
    public SettingsTextFileReadResult Read(
        DefaultSettingsProfile profile,
        CancellationToken cancellationToken = default)
    {
        Mutex? operationMutex = null;
        try
        {
            operationMutex = AcquireOperationMutex(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (profile is null)
            {
                throw Failure(SettingsTextFileFailureReason.Invalid);
            }

            ValidateRootAndActor();
            if (!WorkspacePathSafety.TryGetAttributes(_filePath, out FileAttributes attributes))
            {
                ValidateRootAndActor();
                throw Failure(SettingsTextFileFailureReason.Missing);
            }

            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            ValidateFilePath(_filePath);
            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.SequentialScan);
            ValidateOpenedFile(stream);
            long initialLength = stream.Length;
            if (initialLength > MaximumRawUtf8Bytes)
            {
                throw Failure(SettingsTextFileFailureReason.TooLarge);
            }

            byte[] raw = new byte[(int)initialLength];
            int total = 0;
            while (total < raw.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(raw, total, Math.Min(FileBufferSize, raw.Length - total));
                if (read == 0)
                {
                    throw Failure(SettingsTextFileFailureReason.Conflict);
                }

                total += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            int trailingByte = stream.ReadByte();
            long finalLength = stream.Length;
            if (finalLength > MaximumRawUtf8Bytes)
            {
                throw Failure(SettingsTextFileFailureReason.TooLarge);
            }

            if (trailingByte != -1 || finalLength != initialLength)
            {
                throw Failure(SettingsTextFileFailureReason.Conflict);
            }

            // Hash the exact input, including any BOM, whitespace, and line endings.
            var identity = new SettingsTextFileIdentity(ContentDigest.Compute(raw), total);
            DefaultSettingsSnapshot snapshot = ParseReadSnapshot(raw, profile);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOpenedFile(stream);
            ValidateRootAndActor();
            ValidateFilePath(_filePath);
            cancellationToken.ThrowIfCancellationRequested();
            return new SettingsTextFileReadResult(snapshot, identity);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Translate(exception);
        }
        finally
        {
            operationMutex?.ReleaseMutex();
            operationMutex?.Dispose();
        }
    }

    // Inspects raw bytes and protected ACLs without importing JSON.
    public SettingsTextFileIdentity? InspectIdentity()
    {
        Mutex? operationMutex = null;
        try
        {
            operationMutex = AcquireOperationMutex(CancellationToken.None);
            return InspectPath(_filePath, CancellationToken.None);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Translate(exception);
        }
        finally
        {
            operationMutex?.ReleaseMutex();
            operationMutex?.Dispose();
        }
    }

    // Callers release on this thread after all file checks and recovery.
    private Mutex AcquireOperationMutex(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mutex = new Mutex(false, _operationMutexName, new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = true,
        });
        try
        {
            try
            {
                if (cancellationToken.CanBeCanceled)
                {
                    if (WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]) != 0)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
                else
                {
                    _ = mutex.WaitOne();
                }
            }
            catch (AbandonedMutexException)
            {
                // Ownership is ours; callers revalidate files without sweeping old temporaries.
            }

            return mutex;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private SettingsTextFileIdentity PublishNew(
        string stagingPath,
        SettingsTextFileIdentity candidate,
        CancellationToken cancellationToken)
    {
        _beforeCommit?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureIdentity(InspectPath(stagingPath, cancellationToken), candidate);
        CheckExpected(InspectPath(_filePath, cancellationToken), expected: null);
        _afterFinalVerification?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            File.Move(stagingPath, _filePath, overwrite: false);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 80 or 183)
        {
            // A file appeared after the absent check. Never turn this into an overwrite.
            CheckExpected(InspectPath(_filePath, CancellationToken.None), expected: null);
            throw Failure(SettingsTextFileFailureReason.Conflict);
        }

        try
        {
            return ValidatePublished(candidate);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            if (!TryDeleteOwnedFile(_filePath, candidate))
            {
                throw Failure(SettingsTextFileFailureReason.Unavailable, reconciliationRequired: true);
            }

            throw;
        }
    }

    private SettingsTextFileIdentity ReplaceExisting(
        string stagingPath,
        SettingsTextFileIdentity candidate,
        SettingsTextFileIdentity original,
        CancellationToken cancellationToken)
    {
        // Preserve the verified destination DACL and reserve a private rollback file.
        string rollbackPath = WriteNewOwnedFile([], cancellationToken);
        bool replacementAttempted = false;
        bool replacementSucceeded = false;
        SettingsTextFileIdentity? displacedOriginal = null;
        bool retainRollback = false;
        try
        {
            _beforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureIdentity(InspectPath(stagingPath, cancellationToken), candidate);
            EnsureIdentity(InspectPath(rollbackPath, cancellationToken), EmptyIdentity);
            ValidateRootAndActor();
            if (!WorkspacePathSafety.TryGetAttributes(_filePath, out FileAttributes attributes))
            {
                ValidateRootAndActor();
                throw Failure(SettingsTextFileFailureReason.Conflict);
            }

            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            ValidateFilePath(_filePath);
            // Deny in-place writers through ReplaceFile, whose target needs read/delete access.
            using (var target = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                FileBufferSize,
                FileOptions.SequentialScan))
            {
                CheckExpected(ReadOpenedIdentity(_filePath, target, cancellationToken), original.FileSha256);
                _afterFinalVerification?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();

                replacementAttempted = true;
                File.Replace(stagingPath, _filePath, rollbackPath, ignoreMetadataErrors: false);
                replacementSucceeded = true;
            }

            // Delete-sharing is not CAS: an atomic replacer may have displaced the verified file.
            displacedOriginal = InspectPath(rollbackPath, CancellationToken.None);
            EnsureIdentity(displacedOriginal, original);
            return ValidatePublished(candidate);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // Only a successful replace proves that even an empty backup was actually displaced.
            // Failed replaces can recover a nonempty original, but not an ambiguous empty reservation.
            SettingsTextFileIdentity? recoveryIdentity = replacementSucceeded ? displacedOriginal : original;
            if (replacementAttempted &&
                (recoveryIdentity is null ||
                    !TryRestoreOriginal(rollbackPath, recoveryIdentity, candidate, replacementSucceeded)))
            {
                // Never delete the only remaining old copy or guess that the commit failed.
                retainRollback = true;
                throw Failure(SettingsTextFileFailureReason.Unavailable, reconciliationRequired: true);
            }

            throw;
        }
        finally
        {
            if (!retainRollback)
            {
                _ = TryDeleteOwnedFile(rollbackPath, EmptyIdentity, displacedOriginal ?? original);
            }
        }
    }

    private bool TryRestoreOriginal(
        string rollbackPath,
        SettingsTextFileIdentity original,
        SettingsTextFileIdentity candidate,
        bool replacementSucceeded)
    {
        string? recoveryRollbackPath = null;
        bool recoveryAttempted = false;
        bool restored = false;
        try
        {
            SettingsTextFileIdentity? current = InspectPath(_filePath, CancellationToken.None);
            if (SameIdentity(current, original))
            {
                return true;
            }

            EnsureIdentity(InspectPath(rollbackPath, CancellationToken.None), original);
            if (!replacementSucceeded && SameIdentity(original, EmptyIdentity))
            {
                // An empty failed-replace backup is indistinguishable from an untouched reservation.
                return false;
            }

            if (current is null)
            {
                // Also covers ReplaceFile's documented partial rename failure.
                File.Move(rollbackPath, _filePath, overwrite: false);
            }
            else
            {
                // Do not roll back over an unrelated concurrent edit.
                EnsureIdentity(current, candidate);
                recoveryRollbackPath = WriteNewOwnedFile([], CancellationToken.None);
                EnsureIdentity(InspectPath(recoveryRollbackPath, CancellationToken.None), EmptyIdentity);
                _beforeRecoveryCommit?.Invoke();
                recoveryAttempted = true;
                File.Replace(rollbackPath, _filePath, recoveryRollbackPath, ignoreMetadataErrors: false);
                // A writer can win after the check above; retain that actual file, never discard it.
                EnsureIdentity(InspectPath(recoveryRollbackPath, CancellationToken.None), candidate);
            }

            restored = SameIdentity(InspectPath(_filePath, CancellationToken.None), original);
            return restored;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return false;
        }
        finally
        {
            if (recoveryRollbackPath is not null && (!recoveryAttempted || restored))
            {
                _ = TryDeleteOwnedFile(recoveryRollbackPath, recoveryAttempted ? candidate : EmptyIdentity);
            }
        }
    }

    // Once published, verification and recovery ignore cancellation.
    private SettingsTextFileIdentity ValidatePublished(SettingsTextFileIdentity candidate)
    {
        SettingsTextFileIdentity? actual = InspectPath(_filePath, CancellationToken.None);
        EnsureIdentity(actual, candidate);
        return actual!;
    }

    private string WriteNewOwnedFile(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRootAndActor();
        string path = Path.Combine(_appRoot, string.Concat(".setting-", Guid.NewGuid().ToString("N"), ".tmp"));
        bool created = false;
        try
        {
            // No create-then-chmod interval and no inherited broad root permissions.
            using FileStream stream = FileSystemAclExtensions.Create(
                new FileInfo(path),
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.None,
                FileBufferSize,
                FileOptions.WriteThrough,
                CreateFileSecurity());
            created = true;
            ValidateOpenedFile(stream);
            ValidateFilePath(path);
            for (int offset = 0; offset < bytes.Length; offset += FileBufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(bytes.AsSpan(offset, Math.Min(FileBufferSize, bytes.Length - offset)));
            }

            stream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            EnsureIdentity(
                ReadOpenedIdentity(path, stream, cancellationToken),
                new SettingsTextFileIdentity(ContentDigest.Compute(bytes), bytes.LongLength));
            return path;
        }
        catch
        {
            if (created)
            {
                // This path was created by this call, including a possible partial write.
                _ = TryDeleteOwnedFile(path, expected: null);
            }

            throw;
        }
    }

    private SettingsTextFileIdentity? InspectPath(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRootAndActor();
        if (!WorkspacePathSafety.TryGetAttributes(path, out FileAttributes attributes))
        {
            ValidateRootAndActor();
            return null;
        }

        WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
        ValidateFilePath(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileBufferSize,
            FileOptions.SequentialScan);
        return ReadOpenedIdentity(path, stream, cancellationToken);
    }

    private SettingsTextFileIdentity ReadOpenedIdentity(
        string path,
        FileStream stream,
        CancellationToken cancellationToken)
    {
        ValidateOpenedFile(stream);
        long initialLength = stream.Length;
        if (initialLength > MaximumRawUtf8Bytes)
        {
            throw Failure(SettingsTextFileFailureReason.TooLarge);
        }

        // Recheck the path and handle under the caller's sharing policy.
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
        try
        {
            long total = 0;
            while (total < initialLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, (int)Math.Min(FileBufferSize, initialLength - total));
                if (read == 0)
                {
                    throw Failure(SettingsTextFileFailureReason.Conflict);
                }

                total += read;
                hash.AppendData(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1 || stream.Length != initialLength)
            {
                throw Failure(SettingsTextFileFailureReason.Conflict);
            }

            ValidateOpenedFile(stream);
            ValidateRootAndActor();
            ValidateFilePath(path);
            return new SettingsTextFileIdentity(ContentDigest.FromBytes(hash.GetHashAndReset()), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private bool TryDeleteOwnedFile(
        string path,
        SettingsTextFileIdentity? expected,
        SettingsTextFileIdentity? alternative = null)
    {
        try
        {
            SettingsTextFileIdentity? current = InspectPath(path, CancellationToken.None);
            if (current is null)
            {
                return true;
            }

            if (expected is not null && !SameIdentity(current, expected) && !SameIdentity(current, alternative))
            {
                return false;
            }

            ValidateFilePath(path);
            File.Delete(path);
            return !WorkspacePathSafety.PathEntryExistsConservatively(path);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // No sweeping by prefix, no ACL repair, and no deletion through an unsafe path.
            return false;
        }
    }

    private static DefaultSettingsSnapshot ParseReadSnapshot(byte[] raw, DefaultSettingsProfile profile)
    {
        try
        {
            ReadOnlySpan<byte> utf8 = raw;
            _ = StrictUtf8.GetCharCount(utf8);
            if (utf8.StartsWith("\uFEFF"u8))
            {
                // Exactly one leading UTF-8 BOM is allowed; another remains invalid JSON.
                utf8 = utf8[3..];
            }

            byte[] canonical = CanonicalJson.CanonicalizeToUtf8(utf8);
            if (canonical.Length > MaximumCanonicalJsonUtf8Bytes)
            {
                throw Failure(SettingsTextFileFailureReason.TooLarge);
            }

            using JsonDocument document = JsonDocument.Parse(canonical);
            JsonElement root = document.RootElement;
            // CanonicalJson already rejects duplicates at every depth. Check this exact
            // envelope before getters or mismatch classification, including unknown fields.
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6 ||
                !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) || schemaVersion.ValueKind != JsonValueKind.String ||
                schemaVersion.GetString() != "1.0.0" ||
                !root.TryGetProperty("profileId", out JsonElement profileId) || profileId.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("profileVersion", out JsonElement profileVersion) || profileVersion.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("profileDigest", out JsonElement profileDigest) || profileDigest.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("contentDigest", out JsonElement contentDigest) || contentDigest.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
            {
                throw Failure(SettingsTextFileFailureReason.Invalid);
            }

            if (!EntityId<DefaultSettingsProfileIdTag>.TryParse(profileId.GetString(), out EntityId<DefaultSettingsProfileIdTag>? id) ||
                profileId.GetString() != id.ToString() ||
                !SettingsProfileVersion.TryParse(profileVersion.GetString(), out SettingsProfileVersion? version) ||
                !ContentDigest.TryParse(profileDigest.GetString(), out ContentDigest? digest) || profileDigest.GetString() != digest.Hex ||
                !ContentDigest.TryParse(contentDigest.GetString(), out ContentDigest? expectedDigest) || contentDigest.GetString() != expectedDigest.Hex)
            {
                throw Failure(SettingsTextFileFailureReason.Invalid);
            }

            if (id != profile.Id || version != profile.Version || digest != profile.ContentDigest)
            {
                throw Failure(SettingsTextFileFailureReason.ProfileMismatch);
            }

            // The Core digest payload includes every root property except contentDigest,
            // not just values, and never uses the raw file identity.
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (property.Name != "contentDigest")
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
                writer.Flush();
            }

            ContentDigest actualDigest = ContentDigest.Compute(CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
            if (actualDigest != expectedDigest)
            {
                // Diagnose only: validate a memory-only copy with the computed digest.
                // Bad keys, kinds, ranges, fields, or array order must still be Invalid.
                // This copy is never returned or written; the original input always fails.
                buffer.Clear();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        if (property.Name == "contentDigest")
                        {
                            writer.WriteString("contentDigest", actualDigest.Hex);
                        }
                        else
                        {
                            property.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                    writer.Flush();
                }

                _ = DefaultSettingsSnapshot.ParseCanonical(
                    StrictUtf8.GetString(CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan)), profile);
                throw Failure(SettingsTextFileFailureReason.SnapshotMismatch);
            }

            return DefaultSettingsSnapshot.ParseCanonical(StrictUtf8.GetString(canonical), profile);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            // Includes invalid UTF-8/escaped Unicode without disclosing parser messages.
            throw Failure(SettingsTextFileFailureReason.Invalid);
        }
    }

    private static byte[] Serialize(DefaultSettingsSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw Failure(SettingsTextFileFailureReason.Invalid);
        }

        // Core snapshots are immutable and already domain-validated.
        string source = snapshot.CanonicalJson;
        if (StrictUtf8.GetByteCount(source) > MaximumCanonicalJsonUtf8Bytes)
        {
            throw Failure(SettingsTextFileFailureReason.TooLarge);
        }

        byte[] canonical = StrictUtf8.GetBytes(source);
        if (!canonical.AsSpan().SequenceEqual(CanonicalJson.CanonicalizeToUtf8(canonical)))
        {
            throw Failure(SettingsTextFileFailureReason.Invalid);
        }

        using JsonDocument document = JsonDocument.Parse(canonical);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            SkipValidation = false,
        }))
        {
            // Preserve raw JSON numbers, including canonical exponents and Int64 limits.
            document.WriteTo(writer);
            writer.Flush();
        }

        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
        if (buffer.WrittenCount > MaximumRawUtf8Bytes)
        {
            throw Failure(SettingsTextFileFailureReason.TooLarge);
        }

        if (!canonical.AsSpan().SequenceEqual(CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan)))
        {
            throw Failure(SettingsTextFileFailureReason.SnapshotMismatch);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(_actorSid);
        foreach (SecurityIdentifier sid in ExpectedSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private void ValidateOpenedFile(FileStream stream)
    {
        WorkspacePathSafety.ValidateRegularFileAttributes(File.GetAttributes(stream.SafeFileHandle));
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw Failure(SettingsTextFileFailureReason.UnsafePath);
        }

        FileSecurity security = FileSystemAclExtensions.GetAccessControl(stream);
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !owner.Equals(_actorSid))
        {
            throw Failure(SettingsTextFileFailureReason.AccessDenied);
        }

        var expected = new HashSet<string>(ExpectedSids().Select(static sid => sid.Value), StringComparer.Ordinal);
        AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (rules.Count != expected.Count)
        {
            throw Failure(SettingsTextFileFailureReason.AccessDenied);
        }

        foreach (AuthorizationRule entry in rules)
        {
            if (entry is not FileSystemAccessRule rule ||
                rule.IsInherited ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !expected.Remove(sid.Value) ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.InheritanceFlags != InheritanceFlags.None ||
                rule.PropagationFlags != PropagationFlags.None)
            {
                throw Failure(SettingsTextFileFailureReason.AccessDenied);
            }
        }

        if (expected.Count != 0)
        {
            throw Failure(SettingsTextFileFailureReason.AccessDenied);
        }
    }

    private SecurityIdentifier[] ExpectedSids() =>
    [
        _actorSid,
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    private void ValidateRootAndActor()
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        if (current.User is null || !current.User.Equals(_actorSid))
        {
            throw Failure(SettingsTextFileFailureReason.AccessDenied);
        }

        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(_appRoot);
        string driveRoot = Path.GetPathRoot(_appRoot)
            ?? throw Failure(SettingsTextFileFailureReason.UnsafePath);
        if (new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
        {
            throw Failure(SettingsTextFileFailureReason.UnsafePath);
        }
    }

    private void ValidateFilePath(string path) =>
        WorkspacePathSafety.ValidateExistingPathChain(_appRoot, path, targetIsDirectory: false);

    private static string NormalizeAppRoot(string appRoot)
    {
        if (string.IsNullOrWhiteSpace(appRoot) ||
            !Path.IsPathFullyQualified(appRoot) ||
            appRoot.Length < 4 ||
            appRoot[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z') ||
            appRoot[1] != ':' || appRoot[2] is not ('\\' or '/'))
        {
            throw Failure(SettingsTextFileFailureReason.UnsafePath);
        }

        string normalized = Path.TrimEndingDirectorySeparator(appRoot);
        string relative = normalized[3..].Replace('\\', '/');
        if (!WorkspacePathSafety.IsValidPortableRelativePath(relative))
        {
            throw Failure(SettingsTextFileFailureReason.UnsafePath);
        }

        string fullPath = Path.GetFullPath(normalized);
        string executableRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (string.Equals(fullPath, executableRoot, WorkspacePathSafety.PathComparison) ||
            WorkspacePathSafety.IsStrictDescendant(executableRoot, fullPath))
        {
            throw Failure(SettingsTextFileFailureReason.UnsafePath);
        }

        return fullPath;
    }

    private static SecurityIdentifier ParseCurrentActorSid(string currentActorSid)
    {
        if (string.IsNullOrWhiteSpace(currentActorSid))
        {
            throw Failure(SettingsTextFileFailureReason.Invalid);
        }

        var sid = new SecurityIdentifier(currentActorSid.Trim());
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            current.User is null || !sid.Equals(current.User))
        {
            throw Failure(SettingsTextFileFailureReason.Invalid);
        }

        return sid;
    }

    private static void CheckExpected(SettingsTextFileIdentity? actual, ContentDigest? expected)
    {
        if (expected is null)
        {
            if (actual is not null)
            {
                throw Failure(SettingsTextFileFailureReason.OverwriteRequired, actual.FileSha256);
            }
        }
        else if (actual is null || actual.FileSha256 != expected)
        {
            throw Failure(SettingsTextFileFailureReason.Conflict);
        }
    }

    private static void EnsureIdentity(SettingsTextFileIdentity? actual, SettingsTextFileIdentity expected)
    {
        if (!SameIdentity(actual, expected))
        {
            throw Failure(SettingsTextFileFailureReason.Conflict);
        }
    }

    private static bool SameIdentity(SettingsTextFileIdentity? left, SettingsTextFileIdentity? right) =>
        left is not null && right is not null &&
        left.ByteSize == right.ByteSize && left.FileSha256 == right.FileSha256;

    private static bool IsOperationalFailure(Exception exception) =>
        exception is WorkspacePathSafetyException or IOException or UnauthorizedAccessException or
            SecurityException or ArgumentException or NotSupportedException or
            InvalidOperationException or CryptographicException or JsonException or
            WaitHandleCannotBeOpenedException or System.ComponentModel.Win32Exception;

    private static bool IsFileFailure(Exception exception) =>
        exception is SettingsTextFileException || IsOperationalFailure(exception);

    private static SettingsTextFileException Translate(Exception exception) =>
        Failure(exception switch
        {
            WorkspacePathSafetyException or PathTooLongException => SettingsTextFileFailureReason.UnsafePath,
            UnauthorizedAccessException or SecurityException => SettingsTextFileFailureReason.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException => SettingsTextFileFailureReason.Conflict,
            JsonException or ArgumentException => SettingsTextFileFailureReason.Invalid,
            _ => SettingsTextFileFailureReason.Unavailable,
        });

    private static SettingsTextFileException Failure(
        SettingsTextFileFailureReason reason,
        ContentDigest? existingFileSha256 = null,
        bool reconciliationRequired = false) =>
        new(reason, existingFileSha256, reconciliationRequired);
}