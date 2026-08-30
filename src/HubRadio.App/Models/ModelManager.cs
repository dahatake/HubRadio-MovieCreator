using System.Collections.ObjectModel;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Integrity;

namespace HubRadio.App.Models;

public enum ModelDisposition
{
    AdoptedConditional,
    Held,
    ConditionFalse,
    BenchmarkOnly,
}

public enum ModelConsentStatus
{
    NotRequired,
    RequiredNotAccepted,
    Unverified,
}

public enum ModelAvailability
{
    Ready,
    Missing,
    Corrupt,
    ConsentRequired,
    NotEligible,
}

public enum ModelManagerFailureReason
{
    CatalogInvalid,
    ModelUnknown,
    ModelMissing,
    ModelNotEligible,
    ConsentRequired,
    BundleMissing,
    BundleInvalid,
    CacheCorrupt,
    UnsafePath,
    DestinationConflict,
    InstallationFailed,
}

public sealed class ModelManagerException : Exception
{
    internal ModelManagerException(
        ModelManagerFailureReason reason,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public ModelManagerFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(ModelManagerFailureReason reason) =>
        reason switch
        {
            ModelManagerFailureReason.CatalogInvalid =>
                "The model catalog is invalid.",
            ModelManagerFailureReason.ModelUnknown =>
                "The requested model is not present in the catalog.",
            ModelManagerFailureReason.ModelMissing =>
                "The requested model is not installed.",
            ModelManagerFailureReason.ModelNotEligible =>
                "The requested model is not eligible for runtime use.",
            ModelManagerFailureReason.ConsentRequired =>
                "The requested model cannot be used without accepted terms.",
            ModelManagerFailureReason.BundleMissing =>
                "The offline model bundle is missing.",
            ModelManagerFailureReason.BundleInvalid =>
                "The offline model bundle failed integrity verification.",
            ModelManagerFailureReason.CacheCorrupt =>
                "The installed model cache failed integrity verification.",
            ModelManagerFailureReason.UnsafePath =>
                "The model storage path is unsafe.",
            ModelManagerFailureReason.DestinationConflict =>
                "The model cache destination is already occupied.",
            ModelManagerFailureReason.InstallationFailed =>
                "The offline model installation failed.",
            _ => "The model operation failed.",
        };
}

public sealed class ModelStatus
{
    internal ModelStatus(
        string modelId,
        ModelDisposition disposition,
        ModelConsentStatus consentStatus,
        bool runtimeEligible,
        bool releaseApproved,
        ModelAvailability availability)
    {
        ModelId = modelId;
        Disposition = disposition;
        ConsentStatus = consentStatus;
        RuntimeEligible = runtimeEligible;
        ReleaseApproved = releaseApproved;
        Availability = availability;
    }

    public string ModelId { get; }

    public ModelDisposition Disposition { get; }

    public ModelConsentStatus ConsentStatus { get; }

    public bool RuntimeEligible { get; }

    public bool ReleaseApproved { get; }

    public ModelAvailability Availability { get; }
}

public sealed class ModelManager
{
    internal const string PinnedCatalogSha256 =
        "9699c209e0e4146290436cbb72afa2afaf465e41d0ec3f161c8a4dc4cde1d004";
    internal const string AdoptedAsrModelId = "asr.faster-whisper-small.cpu-int8";

    private const int MaximumCatalogBytes = 1024 * 1024;
    private const int FileBufferSize = 1024 * 1024;
    private const string ExpectedSchemaVersion = "1.0.0";
    private const string ExpectedCatalogId = "hubradio-model-catalog";
    private const string ExpectedGeneratedFrom = "docs/dev/licenses/models.csv";
    private const string ExpectedCacheSubdirectory = "cache/models";
    private const string AdoptedDispositionText = "adopted-conditional";
    private const string StagingDirectoryName = ".staging";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly BinaryVerifier _binaryVerifier;
    private readonly ModelCatalog _catalog;
    private readonly string _bundleRootPath;
    private readonly string _workspacePath;
    private readonly string _cacheRootPath;
    private readonly SemaphoreSlim _installationGate = new(1, 1);

    private ModelManager(
        ModelCatalog catalog,
        string bundleRootPath,
        string workspacePath,
        BinaryVerifier binaryVerifier)
    {
        _catalog = catalog;
        _bundleRootPath = NormalizeLocalAbsolutePath(
            bundleRootPath,
            nameof(bundleRootPath));
        _workspacePath = Path.TrimEndingDirectorySeparator(
            NormalizeLocalAbsolutePath(workspacePath, nameof(workspacePath)));
        _cacheRootPath = ResolveStrictDescendant(
            _workspacePath,
            ExpectedCacheSubdirectory,
            ModelManagerFailureReason.UnsafePath);
        _binaryVerifier = binaryVerifier;

        ValidateExistingDirectoryChain(_workspacePath);
    }

    public static ModelManager Load(
        string catalogPath,
        string bundleRootPath,
        string workspacePath,
        BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(catalogPath);
        ArgumentNullException.ThrowIfNull(binaryVerifier);

        string normalizedCatalogPath = NormalizeLocalAbsolutePath(
            catalogPath,
            nameof(catalogPath));
        byte[] catalogBytes = ReadCatalogBytes(normalizedCatalogPath);
        ContentDigest actualDigest = ContentDigest.Compute(catalogBytes);
        if (!FixedTimeEquals(actualDigest, new ContentDigest(PinnedCatalogSha256)))
        {
            throw Failure(ModelManagerFailureReason.CatalogInvalid);
        }

        return Create(
            catalogBytes,
            bundleRootPath,
            workspacePath,
            binaryVerifier);
    }

    internal static ModelManager CreateForTesting(
        ReadOnlySpan<byte> catalogJson,
        string bundleRootPath,
        string workspacePath,
        BinaryVerifier binaryVerifier) =>
        Create(
            catalogJson,
            bundleRootPath,
            workspacePath,
            binaryVerifier);

    public async Task<IReadOnlyList<ModelStatus>> InspectAllAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = new ModelStatus[_catalog.Models.Count];
        for (int index = 0; index < statuses.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses[index] = await InspectAsync(
                _catalog.Models[index].ModelId,
                cancellationToken).ConfigureAwait(false);
        }

        return Array.AsReadOnly(statuses);
    }

    public async Task<ModelStatus> InspectAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ModelDefinition model = GetModel(modelId);
        cancellationToken.ThrowIfCancellationRequested();

        if (model.ConsentStatus == ModelConsentStatus.RequiredNotAccepted)
        {
            return CreateStatus(model, ModelAvailability.ConsentRequired);
        }

        if (!IsRuntimeEligible(model))
        {
            return CreateStatus(model, ModelAvailability.NotEligible);
        }

        string destinationPath = GetCacheDestination(model);
        PathEntryKind entryKind = GetPathEntryKind(destinationPath);
        if (entryKind == PathEntryKind.Missing)
        {
            return CreateStatus(model, ModelAvailability.Missing);
        }

        if (entryKind == PathEntryKind.Unsafe)
        {
            throw Failure(ModelManagerFailureReason.UnsafePath);
        }

        if (entryKind != PathEntryKind.Directory)
        {
            return CreateStatus(model, ModelAvailability.Corrupt);
        }

        bool valid = await TryVerifyModelDirectoryAsync(
            model,
            destinationPath,
            cancellationToken).ConfigureAwait(false);
        return CreateStatus(
            model,
            valid ? ModelAvailability.Ready : ModelAvailability.Corrupt);
    }

    public async Task<string> RequireOfflinePathAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ModelDefinition model = GetModel(modelId);
        ModelStatus status = await InspectAsync(modelId, cancellationToken)
            .ConfigureAwait(false);
        if (status.Availability == ModelAvailability.Missing)
        {
            throw Failure(ModelManagerFailureReason.ModelMissing);
        }

        ThrowForUnavailableStatus(status);

        string destinationPath = GetCacheDestination(model);
        if (!await TryVerifyModelDirectoryAsync(
                model,
                destinationPath,
                cancellationToken).ConfigureAwait(false))
        {
            throw Failure(ModelManagerFailureReason.CacheCorrupt);
        }

        return destinationPath;
    }

    public async Task<string> InstallFromBundleAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ModelDefinition model = GetModel(modelId);
        EnsureInstallEligible(model);
        cancellationToken.ThrowIfCancellationRequested();

        await _installationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ModelStatus current = await InspectAsync(modelId, cancellationToken)
                .ConfigureAwait(false);
            if (current.Availability == ModelAvailability.Ready)
            {
                return GetCacheDestination(model);
            }

            if (current.Availability == ModelAvailability.Corrupt)
            {
                throw Failure(ModelManagerFailureReason.CacheCorrupt);
            }

            ThrowForUnavailableStatus(current);
            EnsureCacheDirectories();

            string bundlePath = GetBundlePath(model);
            PathEntryKind bundleEntryKind = GetPathEntryKind(bundlePath);
            if (bundleEntryKind == PathEntryKind.Missing)
            {
                throw Failure(ModelManagerFailureReason.BundleMissing);
            }

            if (bundleEntryKind == PathEntryKind.Unsafe)
            {
                throw Failure(ModelManagerFailureReason.UnsafePath);
            }

            if (bundleEntryKind != PathEntryKind.Directory)
            {
                throw Failure(ModelManagerFailureReason.BundleInvalid);
            }

            if (!await TryVerifyModelDirectoryAsync(
                    model,
                    bundlePath,
                    cancellationToken).ConfigureAwait(false))
            {
                throw Failure(ModelManagerFailureReason.BundleInvalid);
            }

            string stagingRoot = ResolveStrictDescendant(
                _cacheRootPath,
                StagingDirectoryName,
                ModelManagerFailureReason.UnsafePath);
            EnsureDirectory(stagingRoot, _cacheRootPath);
            string stagingPath = ResolveStrictDescendant(
                stagingRoot,
                Guid.NewGuid().ToString("N"),
                ModelManagerFailureReason.UnsafePath);
            string destinationPath = GetCacheDestination(model);
            Directory.CreateDirectory(stagingPath);
            ValidateExistingDirectoryChain(stagingPath, _workspacePath);

            bool moved = false;
            try
            {
                await CopyBundleAsync(
                    model,
                    bundlePath,
                    stagingPath,
                    cancellationToken).ConfigureAwait(false);
                if (!await TryVerifyModelDirectoryAsync(
                        model,
                        stagingPath,
                        cancellationToken).ConfigureAwait(false) ||
                    !await TryVerifyModelDirectoryAsync(
                        model,
                        bundlePath,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw Failure(ModelManagerFailureReason.BundleInvalid);
                }

                cancellationToken.ThrowIfCancellationRequested();
                SetModelFilesReadOnly(model, stagingPath);
                MoveDirectoryWithoutOverwrite(stagingPath, destinationPath);
                moved = true;

                if (!await TryVerifyModelDirectoryAsync(
                        model,
                        destinationPath,
                        CancellationToken.None).ConfigureAwait(false))
                {
                    throw Failure(
                        ModelManagerFailureReason.InstallationFailed,
                        reconciliationRequired: true);
                }

                return destinationPath;
            }
            catch (ModelManagerException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException) when (GetPathEntryKind(destinationPath) != PathEntryKind.Missing)
            {
                if (await TryVerifyModelDirectoryAsync(
                        model,
                        destinationPath,
                        CancellationToken.None).ConfigureAwait(false))
                {
                    return destinationPath;
                }

                throw Failure(ModelManagerFailureReason.DestinationConflict);
            }
            catch (Exception exception)
                when (IsFileSystemFailure(exception))
            {
                throw Failure(
                    ModelManagerFailureReason.InstallationFailed,
                    reconciliationRequired: moved);
            }
            finally
            {
                if (!moved)
                {
                    TryDeleteDirectoryTree(stagingPath);
                }
            }
        }
        finally
        {
            _installationGate.Release();
        }
    }

    private static ModelManager Create(
        ReadOnlySpan<byte> catalogJson,
        string bundleRootPath,
        string workspacePath,
        BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(bundleRootPath);
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(binaryVerifier);

        ModelCatalog catalog;
        try
        {
            catalog = ModelCatalog.Parse(catalogJson);
        }
        catch (ModelManagerException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException or InvalidOperationException or
                OverflowException or CryptographicException)
        {
            throw Failure(ModelManagerFailureReason.CatalogInvalid);
        }

        return new ModelManager(
            catalog,
            bundleRootPath,
            workspacePath,
            binaryVerifier);
    }

    private static byte[] ReadCatalogBytes(string catalogPath)
    {
        try
        {
            ValidateExistingFilePath(catalogPath);
            var information = new FileInfo(catalogPath);
            if (information.Length is <= 0 or > MaximumCatalogBytes)
            {
                throw Failure(ModelManagerFailureReason.CatalogInvalid);
            }

            byte[] bytes = File.ReadAllBytes(catalogPath);
            ValidateExistingFilePath(catalogPath);
            if (bytes.Length != information.Length)
            {
                throw Failure(ModelManagerFailureReason.CatalogInvalid);
            }

            return bytes;
        }
        catch (ModelManagerException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(ModelManagerFailureReason.CatalogInvalid);
        }
    }

    private ModelDefinition GetModel(string modelId)
    {
        if (!IsValidToken(modelId) ||
            !_catalog.ModelsById.TryGetValue(modelId, out ModelDefinition? model))
        {
            throw Failure(ModelManagerFailureReason.ModelUnknown);
        }

        return model;
    }

    private static ModelStatus CreateStatus(
        ModelDefinition model,
        ModelAvailability availability) =>
        new(
            model.ModelId,
            model.Disposition,
            model.ConsentStatus,
            model.RuntimeEligible,
            model.ReleaseApproved,
            availability);

    private static void ThrowForUnavailableStatus(ModelStatus status)
    {
        switch (status.Availability)
        {
            case ModelAvailability.Ready:
            case ModelAvailability.Missing:
                return;
            case ModelAvailability.Corrupt:
                throw Failure(ModelManagerFailureReason.CacheCorrupt);
            case ModelAvailability.ConsentRequired:
                throw Failure(ModelManagerFailureReason.ConsentRequired);
            case ModelAvailability.NotEligible:
                throw Failure(ModelManagerFailureReason.ModelNotEligible);
            default:
                throw Failure(ModelManagerFailureReason.InstallationFailed);
        }
    }

    private static void EnsureInstallEligible(ModelDefinition model)
    {
        if (model.ConsentStatus == ModelConsentStatus.RequiredNotAccepted)
        {
            throw Failure(ModelManagerFailureReason.ConsentRequired);
        }

        if (!IsRuntimeEligible(model))
        {
            throw Failure(ModelManagerFailureReason.ModelNotEligible);
        }
    }

    private static bool IsRuntimeEligible(ModelDefinition model) =>
        model.RuntimeEligible &&
        model.Disposition == ModelDisposition.AdoptedConditional &&
        model.ConsentStatus == ModelConsentStatus.NotRequired &&
        model.Bundle is not null;

    private string GetBundlePath(ModelDefinition model)
    {
        ModelBundle bundle = model.Bundle
            ?? throw Failure(ModelManagerFailureReason.ModelNotEligible);
        return ResolveStrictDescendant(
            _bundleRootPath,
            bundle.RelativePath,
            ModelManagerFailureReason.BundleInvalid);
    }

    private string GetCacheDestination(ModelDefinition model) =>
        ResolveStrictDescendant(
            _cacheRootPath,
            model.ModelId,
            ModelManagerFailureReason.UnsafePath);

    private void EnsureCacheDirectories()
    {
        ValidateExistingDirectoryChain(_workspacePath);
        string cachePath = ResolveStrictDescendant(
            _workspacePath,
            "cache",
            ModelManagerFailureReason.UnsafePath);
        EnsureDirectory(cachePath, _workspacePath);
        EnsureDirectory(_cacheRootPath, cachePath);
        ValidateExistingDirectoryChain(_cacheRootPath, _workspacePath);
    }

    private static void EnsureDirectory(string path, string requiredParent)
    {
        ValidateExistingDirectoryChain(requiredParent);
        PathEntryKind kind = GetPathEntryKind(path);
        if (kind == PathEntryKind.Missing)
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception exception)
                when (IsFileSystemFailure(exception))
            {
                throw Failure(ModelManagerFailureReason.UnsafePath);
            }
        }
        else if (kind != PathEntryKind.Directory)
        {
            throw Failure(ModelManagerFailureReason.UnsafePath);
        }

        ValidateExistingDirectoryChain(path, requiredParent);
    }

    private async Task CopyBundleAsync(
        ModelDefinition model,
        string bundlePath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        ModelBundle bundle = model.Bundle
            ?? throw Failure(ModelManagerFailureReason.ModelNotEligible);

        foreach (ModelFile file in bundle.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = ResolveStrictDescendant(
                bundlePath,
                file.Name,
                ModelManagerFailureReason.BundleInvalid);
            string destinationPath = ResolveStrictDescendant(
                stagingPath,
                file.Name,
                ModelManagerFailureReason.UnsafePath);

            ValidateExistingFilePath(sourcePath, _bundleRootPath);
            await using FileStream source = OpenSource(sourcePath);
            ValidateOpenedRegularFile(source, sourcePath);
            await using FileStream destination = OpenDestination(destinationPath);
            ValidateOpenedRegularFile(destination, destinationPath);
            await source.CopyToAsync(
                destination,
                FileBufferSize,
                cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            ValidateOpenedRegularFile(source, sourcePath);
            ValidateOpenedRegularFile(destination, destinationPath);
        }
    }

    private async Task<bool> TryVerifyModelDirectoryAsync(
        ModelDefinition model,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ModelBundle bundle = model.Bundle
                ?? throw Failure(ModelManagerFailureReason.ModelNotEligible);
            ValidateExistingDirectoryChain(directoryPath);
            string[] actualNames = Directory
                .EnumerateFileSystemEntries(directoryPath)
                .Select(GetRequiredFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] expectedNames = bundle.Files
                .Select(static file => file.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal))
            {
                return false;
            }

            foreach (ModelFile file in bundle.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = ResolveStrictDescendant(
                    directoryPath,
                    file.Name,
                    ModelManagerFailureReason.UnsafePath);
                ValidateExistingFilePath(path, directoryPath);
                _ = await _binaryVerifier.VerifyAsync(
                    new BinaryVerificationRequest(
                        string.Concat("model.", model.ModelId, ".", file.ComponentToken),
                        path,
                        file.Digest,
                        file.ByteSize),
                    cancellationToken).ConfigureAwait(false);
                ValidateExistingFilePath(path, directoryPath);
            }

            string[] finalNames = Directory
                .EnumerateFileSystemEntries(directoryPath)
                .Select(GetRequiredFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return finalNames.SequenceEqual(expectedNames, StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is BinaryVerificationException or ModelManagerException ||
                IsFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static FileStream OpenSource(string path)
    {
        try
        {
            return new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = FileBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(ModelManagerFailureReason.BundleInvalid);
        }
    }

    private static FileStream OpenDestination(string path)
    {
        try
        {
            return new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = FileBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(ModelManagerFailureReason.InstallationFailed);
        }
    }

    private static void ValidateOpenedRegularFile(FileStream stream, string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            if ((attributes &
                (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
                !stream.CanRead ||
                !stream.CanSeek)
            {
                throw Failure(ModelManagerFailureReason.UnsafePath);
            }

            ValidateExistingFilePath(path);
        }
        catch (ModelManagerException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(ModelManagerFailureReason.UnsafePath);
        }
    }

    private static void MoveDirectoryWithoutOverwrite(
        string stagingPath,
        string destinationPath)
    {
        ValidateExistingDirectoryChain(stagingPath);
        string destinationParent = Path.GetDirectoryName(destinationPath)
            ?? throw Failure(ModelManagerFailureReason.UnsafePath);
        ValidateExistingDirectoryChain(destinationParent);
        if (GetPathEntryKind(destinationPath) != PathEntryKind.Missing)
        {
            throw new IOException("The destination exists.");
        }

        Directory.Move(stagingPath, destinationPath);
        ValidateExistingDirectoryChain(destinationPath);
    }

    private static void SetModelFilesReadOnly(
        ModelDefinition model,
        string directoryPath)
    {
        ModelBundle bundle = model.Bundle
            ?? throw Failure(ModelManagerFailureReason.ModelNotEligible);
        try
        {
            foreach (ModelFile file in bundle.Files)
            {
                string path = ResolveStrictDescendant(
                    directoryPath,
                    file.Name,
                    ModelManagerFailureReason.UnsafePath);
                ValidateExistingFilePath(path, directoryPath);
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes &
                    (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                {
                    throw Failure(ModelManagerFailureReason.UnsafePath);
                }

                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                ValidateExistingFilePath(path, directoryPath);
            }
        }
        catch (ModelManagerException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(ModelManagerFailureReason.InstallationFailed);
        }
    }

    private static void TryDeleteDirectoryTree(string path)
    {
        try
        {
            if (GetPathEntryKind(path) != PathEntryKind.Directory ||
                !TryDeleteValidatedDirectory(path))
            {
                return;
            }
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            // A failed cleanup leaves only a unique staging directory. The caller still fails
            // closed and a later workspace-recovery pass can quarantine it.
        }
    }

    private static bool TryDeleteValidatedDirectory(string path)
    {
        FileAttributes directoryAttributes = File.GetAttributes(path);
        if ((directoryAttributes & FileAttributes.ReparsePoint) != 0 ||
            (directoryAttributes & FileAttributes.Directory) == 0 ||
            (directoryAttributes & FileAttributes.Device) != 0)
        {
            return false;
        }

        string[] entries = Directory.EnumerateFileSystemEntries(path).ToArray();
        foreach (string entry in entries)
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes &
                (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }
        }

        foreach (string entry in entries)
        {
            File.SetAttributes(entry, FileAttributes.Normal);
            File.Delete(entry);
        }

        Directory.Delete(path, recursive: false);
        return true;
    }

    private static string GetRequiredFileName(string path) =>
        Path.GetFileName(path) is { Length: > 0 } fileName
            ? fileName
            : throw Failure(ModelManagerFailureReason.UnsafePath);

    private static string NormalizeLocalAbsolutePath(string path, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            HasNetworkOrDevicePrefix(path))
        {
            throw new ArgumentException(
                "The path must be a nonblank absolute local path without surrounding whitespace.",
                parameterName);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The path must be a valid absolute local path.", parameterName);
        }

        if (HasNetworkOrDevicePrefix(fullPath) ||
            (OperatingSystem.IsWindows() && HasWindowsAlternateDataStream(fullPath)))
        {
            throw new ArgumentException(
                "The path must not use a network share, device namespace, or alternate data stream.",
                parameterName);
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string ResolveStrictDescendant(
        string rootPath,
        string portableRelativePath,
        ModelManagerFailureReason failureReason)
    {
        if (!IsValidPortableRelativePath(portableRelativePath))
        {
            throw Failure(failureReason);
        }

        string path;
        try
        {
            path = Path.GetFullPath(Path.Combine(
                rootPath,
                portableRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure(failureReason);
        }

        if (!IsStrictDescendant(rootPath, path) ||
            !HasSameWindowsDriveRoot(rootPath, path))
        {
            throw Failure(failureReason);
        }

        return path;
    }

    private static bool IsValidPortableRelativePath(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            Path.IsPathRooted(value) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            return false;
        }

        foreach (string segment in value.Split('/'))
        {
            if (!IsValidPathSegment(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPathSegment(string segment)
    {
        if (segment.Length == 0 ||
            segment is "." or ".." ||
            segment[^1] is '.' or ' ' ||
            segment.Any(static character => character is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return false;
        }

        int extensionIndex = segment.IndexOf('.');
        string deviceName = extensionIndex < 0 ? segment : segment[..extensionIndex];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return deviceName.Length != 4 ||
            (!deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                !deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) ||
            deviceName[3] is not (>= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }

    private static void ValidateExistingDirectoryChain(
        string directoryPath,
        string? stopAtPath = null)
    {
        string? current = directoryPath;
        while (current is not null)
        {
            FileAttributes attributes = GetRequiredAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.Device) != 0)
            {
                throw Failure(ModelManagerFailureReason.UnsafePath);
            }

            if (stopAtPath is not null &&
                string.Equals(current, stopAtPath, PathComparison))
            {
                return;
            }

            current = Path.GetDirectoryName(current);
        }

        if (stopAtPath is not null)
        {
            throw Failure(ModelManagerFailureReason.UnsafePath);
        }
    }

    private static void ValidateExistingFilePath(
        string filePath,
        string? stopAtPath = null)
    {
        FileAttributes attributes = GetRequiredAttributes(filePath);
        if ((attributes &
            (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw Failure(ModelManagerFailureReason.UnsafePath);
        }

        string? parent = Path.GetDirectoryName(filePath);
        if (parent is null)
        {
            throw Failure(ModelManagerFailureReason.UnsafePath);
        }

        ValidateExistingDirectoryChain(parent, stopAtPath);
    }

    private static FileAttributes GetRequiredAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(ModelManagerFailureReason.UnsafePath);
        }
    }

    private static PathEntryKind GetPathEntryKind(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return PathEntryKind.Unsafe;
            }

            return (attributes & FileAttributes.Directory) != 0 &&
                (attributes & FileAttributes.Device) == 0
                    ? PathEntryKind.Directory
                    : PathEntryKind.File;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathEntryKind.Missing;
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            return PathEntryKind.Unsafe;
        }
    }

    private static bool IsStrictDescendant(string rootPath, string candidatePath)
    {
        string prefix = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : string.Concat(rootPath, Path.DirectorySeparatorChar);
        return candidatePath.StartsWith(prefix, PathComparison);
    }

    private static bool HasSameWindowsDriveRoot(string firstPath, string secondPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return string.Equals(
            Path.GetPathRoot(firstPath),
            Path.GetPathRoot(secondPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNetworkOrDevicePrefix(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal) ||
        path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase);

    private static bool HasWindowsAlternateDataStream(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or ArgumentException;

    private static bool FixedTimeEquals(ContentDigest left, ContentDigest right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left.Hex),
            Convert.FromHexString(right.Hex));

    private static bool IsValidToken(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > BinaryVerificationRequest.MaximumComponentIdentityLength ||
            !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (!IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static ModelManagerException Failure(
        ModelManagerFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private enum PathEntryKind
    {
        Missing,
        File,
        Directory,
        Unsafe,
    }

    private sealed class ModelCatalog
    {
        private ModelCatalog(IReadOnlyList<ModelDefinition> models)
        {
            Models = models;
            ModelsById = new ReadOnlyDictionary<string, ModelDefinition>(
                models.ToDictionary(static model => model.ModelId, StringComparer.Ordinal));
        }

        internal IReadOnlyList<ModelDefinition> Models { get; }

        internal IReadOnlyDictionary<string, ModelDefinition> ModelsById { get; }

        internal static ModelCatalog Parse(ReadOnlySpan<byte> json)
        {
            if (json.IsEmpty || json.Length > MaximumCatalogBytes)
            {
                throw new JsonException("The model catalog size is invalid.");
            }

            _ = CanonicalJson.CanonicalizeToUtf8(json);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            CatalogDto dto = JsonSerializer.Deserialize<CatalogDto>(json, options)
                ?? throw new JsonException("The model catalog is empty.");
            ValidateCatalogHeader(dto);

            if (dto.Models is null || dto.Models.Length == 0)
            {
                throw new JsonException("The model catalog must contain models.");
            }

            var models = new ModelDefinition[dto.Models.Length];
            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            int runtimeEligibleCount = 0;
            for (int index = 0; index < dto.Models.Length; index++)
            {
                models[index] = ParseModel(dto.Models[index]);
                if (!modelIds.Add(models[index].ModelId))
                {
                    throw new JsonException("Model identifiers must be unique.");
                }

                if (models[index].RuntimeEligible)
                {
                    runtimeEligibleCount++;
                }
            }

            if (runtimeEligibleCount != 1 ||
                !models.Any(static model =>
                    string.Equals(model.ModelId, AdoptedAsrModelId, StringComparison.Ordinal) &&
                    model.RuntimeEligible))
            {
                throw new JsonException("Exactly the pinned ASR model must be runtime eligible.");
            }

            return new ModelCatalog(Array.AsReadOnly(models));
        }

        private static void ValidateCatalogHeader(CatalogDto dto)
        {
            if (!string.Equals(dto.SchemaVersion, ExpectedSchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(dto.CatalogId, ExpectedCatalogId, StringComparison.Ordinal) ||
                !string.Equals(dto.GeneratedFrom, ExpectedGeneratedFrom, StringComparison.Ordinal) ||
                dto.Policy is null ||
                !dto.Policy.LocalProcessingOnly ||
                dto.Policy.NetworkAcquisitionEnabled ||
                dto.Policy.SilentUpgradeEnabled ||
                dto.Policy.ReleaseApproved ||
                !string.Equals(
                    dto.Policy.CacheSubdirectory,
                    ExpectedCacheSubdirectory,
                    StringComparison.Ordinal) ||
                dto.Policy.RuntimeDispositionAllowlist is not [AdoptedDispositionText])
            {
                throw new JsonException("The model catalog policy is not supported.");
            }
        }

        private static ModelDefinition ParseModel(ModelDto? dto)
        {
            if (dto is null ||
                !IsValidToken(dto.ModelId) ||
                !IsTrimmedNonBlank(dto.Identity) ||
                !IsTrimmedNonBlank(dto.Version) ||
                !IsTrimmedNonBlank(dto.Purpose) ||
                dto.ReleaseApproved ||
                dto.License is null ||
                dto.Conditions is null ||
                dto.Evidence is null ||
                dto.Conditions.Length == 0 ||
                dto.Evidence.Length == 0 ||
                dto.Conditions.Any(static value => !IsTrimmedNonBlank(value)) ||
                dto.Evidence.Any(static value => !IsTrimmedNonBlank(value)))
            {
                throw new JsonException("A model catalog entry is invalid.");
            }

            ModelDisposition disposition = dto.Disposition switch
            {
                AdoptedDispositionText => ModelDisposition.AdoptedConditional,
                "held" => ModelDisposition.Held,
                "condition-false" => ModelDisposition.ConditionFalse,
                "benchmark-only" => ModelDisposition.BenchmarkOnly,
                _ => throw new JsonException("The model disposition is not supported."),
            };
            ModelConsentStatus consentStatus = dto.ConsentStatus switch
            {
                "not-required" => ModelConsentStatus.NotRequired,
                "required-not-accepted" => ModelConsentStatus.RequiredNotAccepted,
                "unverified" => ModelConsentStatus.Unverified,
                _ => throw new JsonException("The model consent status is not supported."),
            };

            ValidateLicense(dto.License);
            ArtifactEvidence? artifact = ParseArtifactEvidence(dto.ArtifactEvidence);
            ModelBundle? bundle = ParseBundle(dto.Bundle);

            if (dto.RuntimeEligible)
            {
                if (disposition != ModelDisposition.AdoptedConditional ||
                    consentStatus != ModelConsentStatus.NotRequired ||
                    artifact is null ||
                    bundle is null ||
                    !string.Equals(artifact.Kind, "artifact-manifest", StringComparison.Ordinal) ||
                    !FixedTimeEquals(artifact.Digest, bundle.ManifestDigest) ||
                    artifact.ByteSize != bundle.TotalBytes ||
                    artifact.FileCount != bundle.Files.Count)
                {
                    throw new JsonException("The runtime model evidence is inconsistent.");
                }
            }
            else if (bundle is not null)
            {
                throw new JsonException("A non-runtime model must not define an offline bundle.");
            }

            return new ModelDefinition(
                dto.ModelId!,
                disposition,
                consentStatus,
                dto.RuntimeEligible,
                dto.ReleaseApproved,
                bundle);
        }

        private static void ValidateLicense(LicenseDto license)
        {
            if (!IsTrimmedNonBlank(license.Status) ||
                license.ReleaseNoticeComplete ||
                (license.DeclaredSpdx is not null && !IsTrimmedNonBlank(license.DeclaredSpdx)) ||
                (license.Evidence is not null && !IsTrimmedNonBlank(license.Evidence)) ||
                (license.EvidenceSha256 is not null && !IsCanonicalDigest(license.EvidenceSha256)))
            {
                throw new JsonException("The model license evidence is invalid.");
            }
        }

        private static ArtifactEvidence? ParseArtifactEvidence(ArtifactEvidenceDto? dto)
        {
            if (dto is null)
            {
                return null;
            }

            if (!IsTrimmedNonBlank(dto.Kind) ||
                !IsCanonicalDigest(dto.Sha256) ||
                dto.ByteSize <= 0 ||
                dto.FileCount <= 0)
            {
                throw new JsonException("The model artifact evidence is invalid.");
            }

            return new ArtifactEvidence(
                dto.Kind!,
                new ContentDigest(dto.Sha256!),
                dto.ByteSize,
                dto.FileCount);
        }

        private static ModelBundle? ParseBundle(BundleDto? dto)
        {
            if (dto is null)
            {
                return null;
            }

            if (!IsValidPortableRelativePath(dto.RelativePath) ||
                !IsCanonicalDigest(dto.ManifestSha256) ||
                dto.TotalBytes <= 0 ||
                dto.Files is null ||
                dto.Files.Length == 0)
            {
                throw new JsonException("The model bundle is invalid.");
            }

            var files = new ModelFile[dto.Files.Length];
            var names = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = 0;
            for (int index = 0; index < dto.Files.Length; index++)
            {
                ModelFileDto file = dto.Files[index]
                    ?? throw new JsonException("A model bundle file is missing.");
                if (!IsValidPathSegment(file.Name ?? string.Empty) ||
                    file.Name!.Contains('/', StringComparison.Ordinal) ||
                    !IsCanonicalDigest(file.Sha256) ||
                    file.ByteSize <= 0 ||
                    !names.Add(file.Name))
                {
                    throw new JsonException("A model bundle file is invalid.");
                }

                totalBytes = checked(totalBytes + file.ByteSize);
                files[index] = new ModelFile(
                    file.Name,
                    CreateComponentToken(index),
                    file.ByteSize,
                    new ContentDigest(file.Sha256!));
            }

            if (totalBytes != dto.TotalBytes)
            {
                throw new JsonException("The model bundle byte size is inconsistent.");
            }

            ContentDigest manifestDigest = ComputeManifestDigest(files);
            var expectedManifest = new ContentDigest(dto.ManifestSha256!);
            if (!FixedTimeEquals(manifestDigest, expectedManifest))
            {
                throw new JsonException("The model bundle manifest digest is inconsistent.");
            }

            return new ModelBundle(
                dto.RelativePath!,
                expectedManifest,
                dto.TotalBytes,
                Array.AsReadOnly(files));
        }

        private static ContentDigest ComputeManifestDigest(IEnumerable<ModelFile> files)
        {
            var text = new StringBuilder();
            foreach (ModelFile file in files.OrderBy(static file => file.Name, StringComparer.Ordinal))
            {
                text.Append(file.Name);
                text.Append('\0');
                text.Append(file.ByteSize);
                text.Append('\0');
                text.Append(file.Digest.Hex);
                text.Append('\n');
            }

            return ContentDigest.Compute(Encoding.UTF8.GetBytes(text.ToString()));
        }

        private static string CreateComponentToken(int index) =>
            string.Concat("file", index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));

        private static bool IsCanonicalDigest(string? value) =>
            ContentDigest.TryParse(value, out ContentDigest? digest) &&
            string.Equals(value, digest.Hex, StringComparison.Ordinal);

        private static bool IsTrimmedNonBlank(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    private sealed record ModelDefinition(
        string ModelId,
        ModelDisposition Disposition,
        ModelConsentStatus ConsentStatus,
        bool RuntimeEligible,
        bool ReleaseApproved,
        ModelBundle? Bundle);

    private sealed record ArtifactEvidence(
        string Kind,
        ContentDigest Digest,
        long ByteSize,
        int FileCount);

    private sealed record ModelBundle(
        string RelativePath,
        ContentDigest ManifestDigest,
        long TotalBytes,
        IReadOnlyList<ModelFile> Files);

    private sealed record ModelFile(
        string Name,
        string ComponentToken,
        long ByteSize,
        ContentDigest Digest);

    private sealed class CatalogDto
    {
        public string? SchemaVersion { get; init; }

        public string? CatalogId { get; init; }

        public string? GeneratedFrom { get; init; }

        public PolicyDto? Policy { get; init; }

        public ModelDto?[]? Models { get; init; }
    }

    private sealed class PolicyDto
    {
        public bool LocalProcessingOnly { get; init; }

        public bool NetworkAcquisitionEnabled { get; init; }

        public bool SilentUpgradeEnabled { get; init; }

        public string[]? RuntimeDispositionAllowlist { get; init; }

        public string? CacheSubdirectory { get; init; }

        public bool ReleaseApproved { get; init; }
    }

    private sealed class ModelDto
    {
        public string? ModelId { get; init; }

        public string? Identity { get; init; }

        public string? Version { get; init; }

        public string? Purpose { get; init; }

        public string? Disposition { get; init; }

        public string? ConsentStatus { get; init; }

        public bool RuntimeEligible { get; init; }

        public bool ReleaseApproved { get; init; }

        public string? SourceEvidence { get; init; }

        public ArtifactEvidenceDto? ArtifactEvidence { get; init; }

        public LicenseDto? License { get; init; }

        public BundleDto? Bundle { get; init; }

        public string[]? Conditions { get; init; }

        public string[]? Evidence { get; init; }
    }

    private sealed class ArtifactEvidenceDto
    {
        public string? Kind { get; init; }

        public string? Sha256 { get; init; }

        public long ByteSize { get; init; }

        public int FileCount { get; init; }
    }

    private sealed class LicenseDto
    {
        public string? Status { get; init; }

        public string? DeclaredSpdx { get; init; }

        public string? Evidence { get; init; }

        public string? EvidenceSha256 { get; init; }

        public bool ReleaseNoticeComplete { get; init; }
    }

    private sealed class BundleDto
    {
        public string? RelativePath { get; init; }

        public string? ManifestSha256 { get; init; }

        public long TotalBytes { get; init; }

        public ModelFileDto?[]? Files { get; init; }
    }

    private sealed class ModelFileDto
    {
        public string? Name { get; init; }

        public long ByteSize { get; init; }

        public string? Sha256 { get; init; }
    }
}