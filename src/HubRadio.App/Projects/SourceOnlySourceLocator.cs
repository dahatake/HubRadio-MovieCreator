using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using Microsoft.Win32.SafeHandles;

namespace HubRadio.App.Projects;

/// <summary>A path-free failure; parser, filesystem and DPAPI details are deliberately not exposed.</summary>
public sealed class SourceOnlySourceException : InvalidOperationException
{
    internal SourceOnlySourceException(string code)
        : base("The selected source could not be verified. Select the original source again.") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Native-only, CurrentUser DPAPI source locator. The host supplies an existing, ACL-protected
/// local workspace. It is not a renderer API. Selection records are immutable, bounded and
/// bound to that workspace and draft. No plaintext path is stored in the database or logs.
/// </summary>
public sealed class SourceOnlySourceLocator
{
    public const int MaximumPathCharacters = 4096;
    public const int MaximumPlaintextBytes = 32 * 1024;
    public const string RightsStatementVersion = "source-only.start.internal-editing-attestation.v1";
    private const string Schema = "source-only.source-locator.v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8,
    };
    private readonly string workspace;
    private readonly string selections;

    public SourceOnlySourceLocator(string workspacePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Source locators require Windows CurrentUser DPAPI.");
        }

        workspace = RequirePath(workspacePath, directory: true);
        selections = Path.Combine(workspace, "source-only", "selections");
    }

    /// <summary>
    /// Captures the exact picker selection BEFORE draft creation. It records no editing
    /// permission: permission is attested separately by the explicit Start/finalize action.
    /// Cancellation never returns a usable selection. The caller removes an orphan record
    /// if its subsequent draft INSERT fails.
    /// </summary>
    public async Task CaptureAsync(
        EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId,
        string selectedPath,
        string actorSid,
        DateTimeOffset selectedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        ArgumentNullException.ThrowIfNull(candidateId);
        _ = TranscriptEditorContract.ValidateCanonicalSid(actorSid, nameof(actorSid));
        RequireUtc(selectedAt);
        cancellationToken.ThrowIfCancellationRequested();
        string path = RequirePath(selectedPath, directory: false);
        await using FileStream source = OpenHeldFile(path);
        string fileIdentity = ReadFileIdentity(source);
        long length = source.Length;
        if (length <= 0) { throw Failure("source_only.source_empty"); }
        ContentDigest digest = ContentDigest.FromBytes(
            await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
        _ = RequirePath(path, directory: false);
        if (source.Length != length || ReadFileIdentity(source) != fileIdentity)
        {
            throw Failure("source_only.source_changed");
        }

        var document = new LocatorDocument(Schema, draftId.ToString(), candidateId.ToString(),
            path, Path.GetFileName(path), digest.Hex, length.ToString(CultureInfo.InvariantCulture),
            fileIdentity, actorSid, Timestamp(selectedAt), null, null, null);
        byte[] protectedBytes = Protect(document, draftId);
        CreateDirectory(workspace, selections);
        string destination = SelectionPath(draftId);
        string temporary = Path.Combine(selections, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = RequirePath(selections, directory: true);
            // No overwrite: an existing acceptance/selection must never be silently rebound.
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                _ = RequirePath(temporary, directory: false);
                File.Delete(temporary);
            }
        }
    }

    internal LocatedSource ReadSelection(EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId)
    {
        try
        {
            byte[] bytes = ReadBoundedFile(SelectionPath(draftId),
                SourceOnlyExecutionStore.MaximumProtectedLocatorBytes);
            return Read(bytes, draftId, candidateId, accepted: false);
        }
        catch (SourceOnlySourceException) { throw; }
        catch (Exception) { throw Failure("source_only.locator_unavailable"); }
    }

    internal byte[] Accept(LocatedSource selection, string actorSid, DateTimeOffset acceptedAt,
        EntityId<ArtifactIdTag> attestationArtifactId)
    {
        _ = TranscriptEditorContract.ValidateCanonicalSid(actorSid, nameof(actorSid));
        RequireUtc(acceptedAt);
        if (acceptedAt < selection.SelectedAt || selection.Document.AcceptedAt is not null)
        {
            throw Failure("source_only.locator_binding_invalid");
        }

        return Protect(selection.Document with
        {
            AcceptedBy = actorSid,
            AcceptedAt = Timestamp(acceptedAt),
            AttestationArtifactId = attestationArtifactId.ToString(),
        }, selection.DraftId);
    }

    internal LocatedSource ReadAccepted(byte[] bytes, EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId) => Read(bytes, draftId, candidateId, accepted: true);

    internal async Task<FileStream> OpenVerifiedAsync(LocatedSource source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream = OpenHeldFile(source.Path);
        try
        {
            if (Path.GetFileName(source.Path) != source.FileName || stream.Length != source.ByteSize ||
                ReadFileIdentity(stream) != source.Document.FileIdentity)
            {
                throw Failure("source_only.source_changed");
            }

            ContentDigest digest = ContentDigest.FromBytes(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            _ = RequirePath(source.Path, directory: false);
            if (digest != source.Digest || stream.Length != source.ByteSize ||
                ReadFileIdentity(stream) != source.Document.FileIdentity)
            {
                throw Failure("source_only.source_changed");
            }

            stream.Position = 0;
            return stream; // Keep this non-write/non-delete-shared handle open through ingest.
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void ForgetUnacceptedSelection(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        string path = SelectionPath(draftId);
        if (File.Exists(path))
        {
            _ = RequirePath(path, directory: false);
            File.Delete(path);
        }
    }

    private LocatedSource Read(byte[] bytes, EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId, bool accepted)
    {
        if (bytes.Length is <= 0 or > SourceOnlyExecutionStore.MaximumProtectedLocatorBytes)
        {
            throw Failure("source_only.locator_invalid");
        }

        byte[] plaintext = Transform(bytes, Entropy(draftId), protect: false);
        try
        {
            if (plaintext.Length is <= 0 or > MaximumPlaintextBytes)
            {
                throw Failure("source_only.locator_invalid");
            }

            _ = Utf8.GetCharCount(plaintext);
            _ = CanonicalJson.CanonicalizeToUtf8(plaintext); // Also rejects duplicate properties.
            LocatorDocument value = JsonSerializer.Deserialize<LocatorDocument>(plaintext, JsonOptions)
                ?? throw Failure("source_only.locator_invalid");
            if (value.Schema != Schema || value.DraftId != draftId.ToString() ||
                value.CandidateId != candidateId.ToString() ||
                !ContentDigest.TryParse(value.Digest, out ContentDigest? digest) || digest.Hex != value.Digest ||
                !long.TryParse(value.ByteSize, NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
                size <= 0 || size.ToString(CultureInfo.InvariantCulture) != value.ByteSize ||
                value.FileIdentity.Length != 48 || value.FileIdentity.Any(c => !char.IsAsciiHexDigit(c)) ||
                value.FileName != Path.GetFileName(value.Path) || string.IsNullOrWhiteSpace(value.FileName) ||
                value.FileName.Length > 255 || value.FileName.Any(char.IsControl) ||
                accepted != (value.AcceptedAt is not null && value.AcceptedBy is not null && value.AttestationArtifactId is not null) ||
                (!accepted && (value.AcceptedAt is not null || value.AcceptedBy is not null || value.AttestationArtifactId is not null)))
            {
                throw Failure("source_only.locator_binding_invalid");
            }

            ValidatePathSyntax(value.Path);
            _ = TranscriptEditorContract.ValidateCanonicalSid(value.SelectedBy, nameof(value.SelectedBy));
            DateTimeOffset selectedAt = ParseTimestamp(value.SelectedAt);
            if (accepted)
            {
                _ = TranscriptEditorContract.ValidateCanonicalSid(value.AcceptedBy!, nameof(value.AcceptedBy));
                if (ParseTimestamp(value.AcceptedAt!) < selectedAt ||
                    !EntityId<ArtifactIdTag>.TryParse(value.AttestationArtifactId, out var id) ||
                    id.ToString() != value.AttestationArtifactId)
                {
                    throw Failure("source_only.locator_binding_invalid");
                }
            }

            return new LocatedSource(value, draftId, candidateId, digest, size, selectedAt);
        }
        catch (SourceOnlySourceException) { throw; }
        catch (Exception) { throw Failure("source_only.locator_invalid"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Protect(LocatorDocument document, EntityId<ProjectCreationDraftIdTag> draftId)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            if (plaintext.Length > MaximumPlaintextBytes) { throw Failure("source_only.locator_invalid"); }
            return Transform(plaintext, Entropy(draftId), protect: true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Entropy(EntityId<ProjectCreationDraftIdTag> draftId) =>
        SHA256.HashData(Utf8.GetBytes(Schema + "\0" + workspace.ToUpperInvariant() + "\0" + draftId));

    private string SelectionPath(EntityId<ProjectCreationDraftIdTag> draftId) =>
        Path.Combine(selections, draftId + ".dpapi");

    internal static FileStream OpenHeldFile(string path)
    {
        try
        {
            _ = RequirePath(path, directory: false);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                if ((File.GetAttributes(stream.SafeFileHandle) &
                    (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                {
                    throw Failure("source_only.source_invalid");
                }
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch (SourceOnlySourceException) { throw; }
        catch (Exception) { throw Failure("source_only.source_unavailable"); }
    }

    /// <summary>
    /// Process-lifetime lease. All source-only owners of a database/workspace must
    /// hold these handles; a heartbeat alone is never evidence of process death.
    /// No PID guessing, stale-lock deletion, or fallback to an unlocked owner.
    /// </summary>
    internal static FileStream OpenExclusiveLease(string path)
    {
        try
        {
            ValidatePathSyntax(path);
            _ = RequirePath(Path.GetDirectoryName(path)!, directory: true);
            if (File.Exists(path) || Directory.Exists(path)) { _ = RequirePath(path, directory: false); }
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                if ((File.GetAttributes(stream.SafeFileHandle) &
                    (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                {
                    throw Failure("source_only.process_lease_unavailable");
                }
                _ = RequirePath(path, directory: false);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch { throw Failure("source_only.process_lease_unavailable"); }
    }

    internal static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using FileStream stream = OpenHeldFile(path);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw Failure("source_only.checkpoint_size_invalid");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Length != bytes.Length) { throw Failure("source_only.checkpoint_changed"); }
        return bytes;
    }

    internal static string RequirePath(string path, bool directory) => RequirePath(path, directory, File.GetAttributes);

    internal static string RequireOptionalDirectory(string path)
    {
        ValidatePathSyntax(path);
        _ = RequirePath(Path.GetDirectoryName(path)!, directory: true);
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw Failure("source_only.path_invalid");
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only the leaf may be absent. Do not create it or infer its contents.
        }
        catch (SourceOnlySourceException) { throw; }
        catch { throw Failure("source_only.path_unavailable"); }
        return path;
    }

    // The test seam asserts the root-to-leaf IO order, without requiring symlink privileges.
    internal static string RequirePath(string path, bool directory, Func<string, FileAttributes> getAttributes)
    {
        ValidatePathSyntax(path);
        try
        {
            // Root first: never inspect a descendant through a rejected junction.
            var chain = new Stack<string>();
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                chain.Push(current);
            }
            foreach (string entry in chain)
            {
                FileAttributes attributes = getAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                    attributes.HasFlag(FileAttributes.Directory) != (entry == path ? directory : true))
                {
                    throw Failure("source_only.path_invalid");
                }
            }
            return path;
        }
        catch (SourceOnlySourceException) { throw; }
        catch (Exception) { throw Failure("source_only.path_unavailable"); }
    }

    internal static void CreateDirectory(string root, string destination)
    {
        _ = RequirePath(root, directory: true);
        ValidatePathSyntax(destination);
        string relative = Path.GetRelativePath(root, destination);
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) || relative.StartsWith("..\\", StringComparison.Ordinal))
        {
            throw Failure("source_only.path_invalid");
        }
        string current = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            _ = RequirePath(current, directory: true);
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(current);
            _ = RequirePath(current, directory: true);
        }
    }

    private static void ValidatePathSyntax(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathCharacters || path != path.Trim() ||
                !Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
                path.Any(char.IsControl) || path.AsSpan(2).Contains(':') ||
                path.Split('\\', '/').Skip(1).Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            {
                throw Failure("source_only.path_invalid");
            }
        }
        catch (SourceOnlySourceException) { throw; }
        catch { throw Failure("source_only.path_invalid"); }
    }

    private static string ReadFileIdentity(FileStream stream)
    {
        // FILE_ID_INFO carries the full 128-bit identity (including ReFS), plus volume ID.
        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 18, out FileIdInfo info, 24))
        {
            throw Failure("source_only.file_identity_unavailable");
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{info.VolumeSerialNumber:x16}{info.Low:x16}{info.High:x16}");
    }

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        GCHandle inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        DataBlob output = default;
        try
        {
            var data = new DataBlob { Length = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var extra = new DataBlob { Length = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            // 0x1 = UI_FORBIDDEN; deliberately NEVER 0x4 (LOCAL_MACHINE).
            bool success = protect
                ? CryptProtectData(ref data, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output);
            int maximum = protect ? SourceOnlyExecutionStore.MaximumProtectedLocatorBytes : MaximumPlaintextBytes;
            if (!success || output.Data == IntPtr.Zero || output.Length <= 0 || output.Length > maximum)
            {
                throw Failure("source_only.locator_protection_failed");
            }
            byte[] bytes = new byte[output.Length];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (!protect && output.Length is > 0 and <= MaximumPlaintextBytes)
                {
                    Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                }
                _ = LocalFree(output.Data);
            }
            entropyHandle.Free();
            inputHandle.Free();
        }
    }

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) { throw new ArgumentException("Source evidence timestamps must be UTC."); }
    }
    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) &&
        parsed.Offset == TimeSpan.Zero && Timestamp(parsed) == value
            ? parsed : throw Failure("source_only.locator_binding_invalid");
    private static SourceOnlySourceException Failure(string code) => new(code);

    internal sealed record LocatorDocument(string Schema, string DraftId, string CandidateId,
        string Path, string FileName, string Digest, string ByteSize, string FileIdentity,
        string SelectedBy, string SelectedAt, string? AcceptedBy, string? AcceptedAt,
        string? AttestationArtifactId);

    internal sealed class LocatedSource(LocatorDocument document,
        EntityId<ProjectCreationDraftIdTag> draftId, EntityId<AssetIdTag> candidateId,
        ContentDigest digest, long byteSize, DateTimeOffset selectedAt)
    {
        internal LocatorDocument Document { get; } = document;
        internal EntityId<ProjectCreationDraftIdTag> DraftId { get; } = draftId;
        internal EntityId<AssetIdTag> CandidateId { get; } = candidateId;
        internal ContentDigest Digest { get; } = digest;
        internal long ByteSize { get; } = byteSize;
        internal DateTimeOffset SelectedAt { get; } = selectedAt;
        internal string Path => Document.Path;
        internal string FileName => Document.FileName;
        internal EntityId<ArtifactIdTag> AttestationArtifactId => EntityId<ArtifactIdTag>.Parse(Document.AttestationArtifactId!);
        internal RightsRecord Rights => new(RightsStatus.Approved,
            "artifact://" + AttestationArtifactId, "Explicitly attested internal editing permission only.",
            externalPublicationAllowed: false);
        internal string RightsEvidenceJson => CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = RightsStatementVersion,
            sourceCandidateId = CandidateId.ToString(),
            originalArtifactId = CandidateId.ToString(),
            originalFileName = FileName,
            sourceDigest = Digest.Hex,
            sourceByteSize = ByteSize,
            selectedBy = Document.SelectedBy,
            selectedAt = Document.SelectedAt,
            actorSid = Document.AcceptedBy,
            attestedAt = Document.AcceptedAt,
            internalEditingPermissionAttested = true,
            externalPublicationAllowed = false,
        }));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber; public ulong Low; public ulong High; }

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdInfo information, uint bufferSize);
}