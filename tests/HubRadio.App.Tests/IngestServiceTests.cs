using System.Globalization;
using System.Reflection;
using System.Text;
using HubRadio.App.Media;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class IngestServiceTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2038, 9, 10, 11, 12, 13, TimeSpan.FromHours(9));

    private static readonly ContentDigest EmptyContentDigest =
        ContentDigest.Compute(Array.Empty<byte>());

    [TestMethod]
    public async Task PromoteVerifiedOriginalAsync_MovesRegistersRechecksAndPreservesSnapshots()
    {
        using IngestFixture fixture = new();
        EvidenceBundle evidence = fixture.Evidence;
        StoredProject projectBefore = fixture.GetProject();

        ImportedOriginal imported = await fixture.PromoteAsync();

        string expectedReference = fixture.GetExpectedStorageReference();
        string destinationPath = fixture.GetExpectedDestinationPath();
        Assert.AreSame(evidence.Asset, imported.Asset);
        Assert.AreSame(evidence.Asset.Rights, imported.Asset.Rights);
        Assert.AreSame(evidence.MediaInfo, imported.MediaInfo);
        Assert.AreSame(evidence.DecodeValidation, imported.DecodeValidation);
        Assert.AreEqual(OriginalIntegrityStatus.Verified, imported.IntegrityStatus);
        Assert.AreEqual(fixture.ArtifactId, imported.Artifact.Id);
        Assert.AreEqual(evidence.StagedMedia.ProjectId, imported.Artifact.ProjectId);
        Assert.AreEqual(ArtifactKind.Original, imported.Artifact.Kind);
        Assert.AreEqual(expectedReference, imported.Artifact.StorageReference);
        Assert.AreEqual(evidence.Asset.ContentDigest, imported.Artifact.ContentDigest);
        Assert.AreEqual(evidence.Asset.ByteSize, imported.Artifact.ByteSize);
        Assert.AreEqual(evidence.StagedMedia.CreatedAt, imported.Artifact.CreatedAt);
        Assert.IsFalse(imported.Artifact.StorageReference.Contains(
            evidence.Asset.FileName,
            StringComparison.Ordinal));

        Assert.IsFalse(File.Exists(evidence.StagedMedia.StagingPath));
        Assert.IsTrue(File.Exists(destinationPath));
        CollectionAssert.AreEqual(fixture.Content, File.ReadAllBytes(destinationPath));
        Assert.AreEqual(1L, fixture.CountArtifacts());
        StoredArtifact loaded = fixture.Store.GetArtifact(fixture.ArtifactId)
            ?? throw new AssertFailedException("The original artifact row was not found.");
        AssertStoredArtifactEqual(imported.Artifact, loaded);
        Assert.AreEqual(
            OriginalIntegrityStatus.Verified,
            await fixture.Service.VerifyRegisteredOriginalAsync(
                imported.Artifact,
                CancellationToken.None));

        StoredProject projectAfter = fixture.GetProject();
        Assert.AreEqual(projectBefore.Id, projectAfter.Id);
        Assert.AreEqual(projectBefore.Revision, projectAfter.Revision);
        Assert.AreEqual(projectBefore.PublicationScope, projectAfter.PublicationScope);
        Assert.AreEqual(projectBefore.Deadline, projectAfter.Deadline);
        Assert.AreEqual(projectBefore.UpdatedAt, projectAfter.UpdatedAt);
        Assert.AreEqual(projectBefore.CanonicalJson, projectAfter.CanonicalJson);
        Assert.AreEqual(RightsStatus.Approved, imported.Asset.Rights.Status);
        Assert.AreEqual("evidence:synthetic-approval", imported.Asset.Rights.EvidenceReference);
        Assert.AreEqual("internal editing", imported.Asset.Rights.AllowedUse);
        Assert.IsTrue(imported.Asset.Rights.ExternalPublicationAllowed);
    }

    [TestMethod]
    [DataRow("asset.id")]
    [DataRow("asset.fileName")]
    [DataRow("asset.digest")]
    [DataRow("asset.byteSize")]
    [DataRow("staged.candidateId")]
    [DataRow("staged.projectId")]
    [DataRow("staged.jobId")]
    [DataRow("staged.sourceFileName")]
    [DataRow("staged.digest")]
    [DataRow("staged.byteSize")]
    [DataRow("probe.candidateId")]
    [DataRow("probe.projectId")]
    [DataRow("probe.jobId")]
    [DataRow("probe.sourceFileName")]
    [DataRow("probe.digest")]
    [DataRow("probe.byteSize")]
    [DataRow("mediaInfo.candidateId")]
    [DataRow("mediaInfo.projectId")]
    [DataRow("mediaInfo.jobId")]
    [DataRow("mediaInfo.sourceFileName")]
    [DataRow("mediaInfo.digest")]
    [DataRow("mediaInfo.byteSize")]
    [DataRow("decode.candidateId")]
    [DataRow("decode.projectId")]
    [DataRow("decode.jobId")]
    [DataRow("decode.sourceFileName")]
    [DataRow("decode.digest")]
    [DataRow("decode.byteSize")]
    [DataRow("probe.videoStreamIndex")]
    [DataRow("probe.audioStreamIndex")]
    [DataRow("probe.audioAbsent")]
    [DataRow("mediaInfo.videoStreamIndex")]
    [DataRow("mediaInfo.audioStreamIndex")]
    [DataRow("mediaInfo.audioAbsent")]
    [DataRow("mediaInfo.videoCodec")]
    [DataRow("mediaInfo.videoDimensions")]
    [DataRow("mediaInfo.videoTimeBase")]
    [DataRow("mediaInfo.formatDuration")]
    [DataRow("mediaInfo.formatNames")]
    [DataRow("mediaInfo.streamSet")]
    [DataRow("decode.videoStreamIndex")]
    [DataRow("decode.audioStreamIndex")]
    [DataRow("decode.audioAbsent")]
    public async Task PromoteVerifiedOriginalAsync_RejectsEveryBindingMismatchBeforeFileWork(
        string mismatch)
    {
        using IngestFixture fixture = new();
        EvidenceBundle mismatched = ApplyMismatch(mismatch, fixture.Evidence);
        byte[] stagedBefore = File.ReadAllBytes(fixture.Evidence.StagedMedia.StagingPath);

        IngestServiceException exception =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(() =>
                fixture.PromoteAsync(mismatched));

        AssertSafeFailure(
            exception,
            IngestServiceFailureReason.InputBindingMismatch,
            reconciliationRequired: false,
            fixture.WorkspacePath,
            fixture.Evidence.StagedMedia.StagingPath,
            fixture.Evidence.StagedMedia.SourceFileName);
        Assert.IsTrue(File.Exists(fixture.Evidence.StagedMedia.StagingPath), mismatch);
        CollectionAssert.AreEqual(
            stagedBefore,
            File.ReadAllBytes(fixture.Evidence.StagedMedia.StagingPath),
            mismatch);
        Assert.AreEqual(0L, fixture.CountArtifacts(), mismatch);
        Assert.IsFalse(File.Exists(fixture.GetExpectedDestinationPath()), mismatch);
    }

    [TestMethod]
    public async Task PromoteVerifiedOriginalAsync_NullInputsAndPreCancellationDoNotMove()
    {
        using IngestFixture fixture = new();
        EvidenceBundle evidence = fixture.Evidence;
        Func<Task>[] nullCalls =
        [
            () => fixture.Service.PromoteVerifiedOriginalAsync(
                null!, evidence.Asset, evidence.StagedMedia, evidence.ProbeResult,
                evidence.MediaInfo, evidence.DecodeValidation, CancellationToken.None),
            () => fixture.Service.PromoteVerifiedOriginalAsync(
                fixture.ArtifactId, null!, evidence.StagedMedia, evidence.ProbeResult,
                evidence.MediaInfo, evidence.DecodeValidation, CancellationToken.None),
            () => fixture.Service.PromoteVerifiedOriginalAsync(
                fixture.ArtifactId, evidence.Asset, null!, evidence.ProbeResult,
                evidence.MediaInfo, evidence.DecodeValidation, CancellationToken.None),
            () => fixture.Service.PromoteVerifiedOriginalAsync(
                fixture.ArtifactId, evidence.Asset, evidence.StagedMedia, null!,
                evidence.MediaInfo, evidence.DecodeValidation, CancellationToken.None),
            () => fixture.Service.PromoteVerifiedOriginalAsync(
                fixture.ArtifactId, evidence.Asset, evidence.StagedMedia,
                evidence.ProbeResult, null!, evidence.DecodeValidation,
                CancellationToken.None),
            () => fixture.Service.PromoteVerifiedOriginalAsync(
                fixture.ArtifactId, evidence.Asset, evidence.StagedMedia,
                evidence.ProbeResult, evidence.MediaInfo, null!,
                CancellationToken.None),
        ];
        foreach (Func<Task> call in nullCalls)
        {
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(call);
        }

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException cancelled =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.PromoteAsync(evidence, cancellation.Token));

        Assert.AreEqual(cancellation.Token, cancelled.CancellationToken);
        Assert.IsTrue(File.Exists(evidence.StagedMedia.StagingPath));
        CollectionAssert.AreEqual(
            fixture.Content,
            File.ReadAllBytes(evidence.StagedMedia.StagingPath));
        Assert.AreEqual(0L, fixture.CountArtifacts());
        Assert.IsFalse(File.Exists(fixture.GetExpectedDestinationPath()));
    }

    [TestMethod]
    public void ConstructorRejectsNullDependencies()
    {
        using IngestFixture fixture = new();

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new IngestService(null!, new BinaryVerifier()));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new IngestService(fixture.Store, (BinaryVerifier)null!));
    }

    [TestMethod]
    public async Task PromoteVerifiedOriginalAsync_PromotionFailurePreservesStaging()
    {
        using IngestFixture fixture = new();
        byte[] changedBytes = fixture.Content.ToArray();
        changedBytes[^1] ^= 0xff;
        File.WriteAllBytes(fixture.Evidence.StagedMedia.StagingPath, changedBytes);

        IngestServiceException exception =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(fixture.PromoteAsync);

        AssertSafeFailure(
            exception,
            IngestServiceFailureReason.PromotionFailed,
            reconciliationRequired: false,
            fixture.WorkspacePath,
            fixture.Evidence.StagedMedia.StagingPath);
        Assert.IsTrue(File.Exists(fixture.Evidence.StagedMedia.StagingPath));
        CollectionAssert.AreEqual(
            changedBytes,
            File.ReadAllBytes(fixture.Evidence.StagedMedia.StagingPath));
        Assert.IsFalse(File.Exists(fixture.GetExpectedDestinationPath()));
        Assert.AreEqual(0L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteVerifiedOriginalAsync_DuplicateIdAndDeterministicDestinationNeverOverwrite()
    {
        using IngestFixture fixture = new();
        ImportedOriginal first = await fixture.PromoteAsync();
        string firstDestination = fixture.GetExpectedDestinationPath();
        byte[] firstBytes = File.ReadAllBytes(firstDestination);
        StagedMediaImport retryStaged = fixture.CreateRetryStaging();
        EvidenceBundle retry = fixture.Evidence with { StagedMedia = retryStaged };

        IngestServiceException exception =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(() =>
                fixture.PromoteAsync(retry));

        AssertSafeFailure(
            exception,
            IngestServiceFailureReason.PromotionFailed,
            reconciliationRequired: false,
            fixture.WorkspacePath,
            retryStaged.StagingPath);
        Assert.IsTrue(File.Exists(retryStaged.StagingPath));
        CollectionAssert.AreEqual(fixture.Content, File.ReadAllBytes(retryStaged.StagingPath));
        CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(firstDestination));
        Assert.AreEqual(1L, fixture.CountArtifacts());
        StoredArtifact loaded = fixture.Store.GetArtifact(fixture.ArtifactId)
            ?? throw new AssertFailedException("The first original artifact row was lost.");
        AssertStoredArtifactEqual(first.Artifact, loaded);
    }

    [TestMethod]
    public async Task PromoteVerifiedOriginalAsync_PostCommitMetadataMutationRequiresReconciliationWithoutRollback()
    {
        using IngestFixture fixture = new();
        string changedDigest = new string('b', 64);
        fixture.ExecuteNonQuery($"""
            CREATE TRIGGER mutate_original_after_insert
            AFTER INSERT ON artifacts
            BEGIN
                UPDATE artifacts
                SET content_digest = '{changedDigest}'
                WHERE id = NEW.id;
            END;
            """);

        IngestServiceException exception =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(fixture.PromoteAsync);

        AssertSafeFailure(
            exception,
            IngestServiceFailureReason.RegisteredMetadataMismatch,
            reconciliationRequired: true,
            fixture.WorkspacePath,
            fixture.Evidence.StagedMedia.StagingPath);
        Assert.IsFalse(File.Exists(fixture.Evidence.StagedMedia.StagingPath));
        Assert.IsTrue(File.Exists(fixture.GetExpectedDestinationPath()));
        CollectionAssert.AreEqual(
            fixture.Content,
            File.ReadAllBytes(fixture.GetExpectedDestinationPath()));
        StoredArtifact registered = fixture.Store.GetArtifact(fixture.ArtifactId)
            ?? throw new AssertFailedException("The committed artifact row was rolled back.");
        Assert.AreEqual(changedDigest, registered.ContentDigest.Hex);
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteVerifiedOriginalAsync_PostCommitCancellationRequiresReconciliation()
    {
        using IngestFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        var service = new IngestService(
            fixture.Store,
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<BinaryVerificationResult>(token);
            });
        EvidenceBundle evidence = fixture.Evidence;

        IngestServiceException exception =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(() =>
                service.PromoteVerifiedOriginalAsync(
                    fixture.ArtifactId,
                    evidence.Asset,
                    evidence.StagedMedia,
                    evidence.ProbeResult,
                    evidence.MediaInfo,
                    evidence.DecodeValidation,
                    cancellation.Token));

        AssertSafeFailure(
            exception,
            IngestServiceFailureReason.RegisteredContentVerificationFailed,
            reconciliationRequired: true,
            fixture.WorkspacePath,
            evidence.StagedMedia.StagingPath);
        Assert.IsFalse(File.Exists(evidence.StagedMedia.StagingPath));
        Assert.IsTrue(File.Exists(fixture.GetExpectedDestinationPath()));
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsNotNull(fixture.Store.GetArtifact(fixture.ArtifactId));
    }

    [TestMethod]
    [DataRow("id-missing", IngestServiceFailureReason.RegisteredArtifactMissing)]
    [DataRow("project", IngestServiceFailureReason.RegisteredMetadataMismatch)]
    [DataRow("kind", IngestServiceFailureReason.RegisteredArtifactNotOriginal)]
    [DataRow("storage", IngestServiceFailureReason.RegisteredMetadataMismatch)]
    [DataRow("digest", IngestServiceFailureReason.RegisteredMetadataMismatch)]
    [DataRow("byteSize", IngestServiceFailureReason.RegisteredMetadataMismatch)]
    [DataRow("createdAt", IngestServiceFailureReason.RegisteredMetadataMismatch)]
    [DataRow("corruptStorage", IngestServiceFailureReason.RegisteredMetadataInvalid)]
    public async Task VerifyRegisteredOriginalAsync_FailsClosedForEveryRegisteredMetadataMutation(
        string mutation,
        IngestServiceFailureReason expectedReason)
    {
        using IngestFixture fixture = new();
        ImportedOriginal imported = await fixture.PromoteAsync();
        fixture.MutateRegisteredMetadata(mutation, imported.Artifact);

        IngestServiceException exception =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(() =>
                fixture.Service.VerifyRegisteredOriginalAsync(
                    imported.Artifact,
                    CancellationToken.None));

        AssertSafeFailure(
            exception,
            expectedReason,
            reconciliationRequired: true,
            fixture.WorkspacePath,
            fixture.GetExpectedDestinationPath(),
            imported.Artifact.StorageReference);
        Assert.IsTrue(File.Exists(fixture.GetExpectedDestinationPath()));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    [DataRow("digest", IngestServiceFailureReason.RegisteredContentDigestMismatch)]
    [DataRow("size", IngestServiceFailureReason.RegisteredContentSizeMismatch)]
    [DataRow("missing", IngestServiceFailureReason.RegisteredContentMissing)]
    [DataRow("directory", IngestServiceFailureReason.RegisteredContentNotRegularFile)]
    public async Task VerifyRegisteredOriginalAsync_DetectsChangedMissingAndNonFileContent(
        string mutation,
        IngestServiceFailureReason expectedReason)
    {
        using IngestFixture fixture = new();
        ImportedOriginal imported = await fixture.PromoteAsync();
        string destinationPath = fixture.GetExpectedDestinationPath();
        MutateRegisteredContent(mutation, destinationPath, fixture.Content);

        IngestServiceException exception =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(() =>
                fixture.Service.VerifyRegisteredOriginalAsync(
                    imported.Artifact,
                    CancellationToken.None));

        AssertSafeFailure(
            exception,
            expectedReason,
            reconciliationRequired: true,
            fixture.WorkspacePath,
            destinationPath);
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsNotNull(fixture.Store.GetArtifact(imported.Artifact.Id));
    }

    [TestMethod]
    public async Task VerifyRegisteredOriginalAsync_RejectsDestinationReparsePointWhenAvailable()
    {
        using IngestFixture fixture = new();
        ImportedOriginal imported = await fixture.PromoteAsync();
        string destinationPath = fixture.GetExpectedDestinationPath();
        string targetPath = Path.Combine(fixture.RootPath, "outside-original-target.bin");
        File.Delete(destinationPath);
        File.WriteAllBytes(targetPath, fixture.Content);

        try
        {
            File.CreateSymbolicLink(destinationPath, targetPath);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException or NotSupportedException)
        {
            Assert.Inconclusive("Symbolic links are unavailable in this test environment.");
            return;
        }

        if ((File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) == 0)
        {
            Assert.Inconclusive("The file system did not expose the link as a reparse point.");
        }

        IngestServiceException verificationFailure =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(() =>
                fixture.Service.VerifyRegisteredOriginalAsync(
                    imported.Artifact,
                    CancellationToken.None));

        AssertSafeFailure(
            verificationFailure,
            IngestServiceFailureReason.RegisteredContentReparsePoint,
            reconciliationRequired: true,
            destinationPath,
            targetPath,
            fixture.WorkspacePath);
        CollectionAssert.AreEqual(fixture.Content, File.ReadAllBytes(targetPath));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task VerifyRegisteredOriginalAsync_PreCancellationAndNonOriginalInputAreStable()
    {
        using IngestFixture fixture = new();
        ImportedOriginal imported = await fixture.PromoteAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException cancelled =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.Service.VerifyRegisteredOriginalAsync(
                    imported.Artifact,
                    cancellation.Token));
        Assert.AreEqual(cancellation.Token, cancelled.CancellationToken);

        byte[] proxyContent = Encoding.UTF8.GetBytes("synthetic proxy bytes");
        string proxyStaging = fixture.CreateStagingFile("proxy-retry.ready", proxyContent);
        EntityId<ArtifactIdTag> proxyId = EntityId<ArtifactIdTag>.New();
        StoredArtifact proxy = await fixture.Store.PromoteAsync(
            new ArtifactPromotionRequest(
                proxyId,
                fixture.ProjectId,
                ArtifactKind.Proxy,
                proxyStaging,
                "synthetic/proxy.bin",
                ContentDigest.Compute(proxyContent),
                proxyContent.LongLength,
                CreatedAt));

        IngestServiceException nonOriginal =
            await Assert.ThrowsExactlyAsync<IngestServiceException>(() =>
                fixture.Service.VerifyRegisteredOriginalAsync(
                    proxy,
                    CancellationToken.None));
        AssertSafeFailure(
            nonOriginal,
            IngestServiceFailureReason.RegisteredArtifactNotOriginal,
            reconciliationRequired: true,
            fixture.WorkspacePath,
            proxy.StorageReference);
        Assert.IsTrue(File.Exists(fixture.GetExpectedDestinationPath()));
        Assert.AreEqual(2L, fixture.CountArtifacts());
    }

    [TestMethod]
    public void PublicSurfaceIsMinimalImmutablePathPrivateAndHasNoOverwriteApi()
    {
        CollectionAssert.AreEqual(
            new[] { "Verified" },
            Enum.GetNames<OriginalIntegrityStatus>());
        CollectionAssert.AreEqual(
            new[]
            {
                "InputBindingMismatch",
                "PromotionFailed",
                "RegisteredArtifactMissing",
                "RegisteredArtifactNotOriginal",
                "RegisteredMetadataMismatch",
                "RegisteredMetadataInvalid",
                "RegisteredStorageUnsafe",
                "RegisteredContentMissing",
                "RegisteredContentNotRegularFile",
                "RegisteredContentReparsePoint",
                "RegisteredContentSizeMismatch",
                "RegisteredContentDigestMismatch",
                "RegisteredContentVerificationFailed",
            },
            Enum.GetNames<IngestServiceFailureReason>());

        AssertPublicSealedImmutable(
            typeof(ImportedOriginal),
            "Asset",
            "Artifact",
            "MediaInfo",
            "DecodeValidation",
            "IntegrityStatus");
        AssertPublicSealedImmutable(
            typeof(IngestServiceException),
            "Reason",
            "ReconciliationRequired");
        Assert.AreEqual(0, typeof(ImportedOriginal).GetConstructors().Length);
        Assert.AreEqual(0, typeof(IngestServiceException).GetConstructors().Length);
        Assert.IsFalse(typeof(ImportedOriginal)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(static property =>
                property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                property.PropertyType == typeof(string)));

        Type serviceType = typeof(IngestService);
        Assert.IsTrue(serviceType.IsPublic);
        Assert.IsTrue(serviceType.IsSealed);
        ConstructorInfo[] constructors = serviceType.GetConstructors();
        Assert.HasCount(1, constructors);
        ConstructorInfo constructor = constructors[0];
        CollectionAssert.AreEqual(
            new[] { typeof(ArtifactStore), typeof(BinaryVerifier) },
            constructor.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        MethodInfo[] methods = GetDeclaredPublicMethods(serviceType);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "PromoteVerifiedOriginalAsync",
                "VerifyRegisteredOriginalAsync",
            },
            methods.Select(static method => method.Name).ToArray());
        Assert.AreEqual(2, methods.Length);
        Assert.IsFalse(methods.Any(static method =>
            method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Overwrite", StringComparison.OrdinalIgnoreCase)));
        Assert.IsEmpty(serviceType.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        MethodInfo storeVerification = typeof(ArtifactStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(static method => method.Name == "VerifyRegisteredAsync");
        Assert.IsFalse(storeVerification.GetParameters().Any(static parameter =>
            parameter.ParameterType == typeof(string)));
        Assert.AreEqual(
            typeof(EntityId<ArtifactIdTag>),
            storeVerification.GetParameters()[0].ParameterType);
        Assert.AreEqual(
            typeof(Task<BinaryVerificationResult>),
            storeVerification.ReturnType);

        const string privatePath = @"C:\private\customer-original.mov";
        Dictionary<IngestServiceFailureReason, string> messages = new()
        {
            [IngestServiceFailureReason.InputBindingMismatch] =
                "The ingest inputs do not identify the same verified media.",
            [IngestServiceFailureReason.PromotionFailed] =
                "The verified original could not be promoted.",
            [IngestServiceFailureReason.RegisteredArtifactMissing] =
                "The registered original artifact is missing from the store.",
            [IngestServiceFailureReason.RegisteredArtifactNotOriginal] =
                "The registered artifact is not an original.",
            [IngestServiceFailureReason.RegisteredMetadataMismatch] =
                "The registered original metadata does not match the expected snapshot.",
            [IngestServiceFailureReason.RegisteredMetadataInvalid] =
                "The registered original metadata is invalid.",
            [IngestServiceFailureReason.RegisteredStorageUnsafe] =
                "The registered original storage reference is unsafe.",
            [IngestServiceFailureReason.RegisteredContentMissing] =
                "The registered original content is missing.",
            [IngestServiceFailureReason.RegisteredContentNotRegularFile] =
                "The registered original content is not a regular file.",
            [IngestServiceFailureReason.RegisteredContentReparsePoint] =
                "The registered original content path contains a reparse point.",
            [IngestServiceFailureReason.RegisteredContentSizeMismatch] =
                "The registered original content size has changed.",
            [IngestServiceFailureReason.RegisteredContentDigestMismatch] =
                "The registered original content digest has changed.",
            [IngestServiceFailureReason.RegisteredContentVerificationFailed] =
                "The registered original content could not be verified.",
        };
        foreach (IngestServiceFailureReason reason in Enum.GetValues<IngestServiceFailureReason>())
        {
            var exception = new IngestServiceException(
                reason,
                reconciliationRequired: true);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsTrue(exception.ReconciliationRequired);
            Assert.AreEqual(messages[reason], exception.Message);
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.Message.Contains(
                privatePath,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static EvidenceBundle ApplyMismatch(
        string mismatch,
        EvidenceBundle evidence)
    {
        EntityId<AssetIdTag> otherAssetId = EntityId<AssetIdTag>.New();
        EntityId<ProjectIdTag> otherProjectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> otherJobId = EntityId<JobIdTag>.New();
        ContentDigest otherDigest = new(new string('f', 64));
        string otherFileName = evidence.StagedMedia.SourceFileName.ToUpperInvariant();
        long otherByteSize = checked(evidence.StagedMedia.ByteSize + 1);

        return mismatch switch
        {
            "asset.id" => evidence with
            {
                Asset = CreateAsset(evidence.StagedMedia, candidateId: otherAssetId),
            },
            "asset.fileName" => evidence with
            {
                Asset = CreateAsset(evidence.StagedMedia, sourceFileName: otherFileName),
            },
            "asset.digest" => evidence with
            {
                Asset = CreateAsset(evidence.StagedMedia, contentDigest: otherDigest),
            },
            "asset.byteSize" => evidence with
            {
                Asset = CreateAsset(evidence.StagedMedia, byteSize: otherByteSize),
            },
            "staged.candidateId" => evidence with
            {
                StagedMedia = CreateStagedMedia(
                    evidence.StagedMedia,
                    candidateId: otherAssetId),
            },
            "staged.projectId" => evidence with
            {
                StagedMedia = CreateStagedMedia(
                    evidence.StagedMedia,
                    projectId: otherProjectId),
            },
            "staged.jobId" => evidence with
            {
                StagedMedia = CreateStagedMedia(
                    evidence.StagedMedia,
                    jobId: otherJobId),
            },
            "staged.sourceFileName" => evidence with
            {
                StagedMedia = CreateStagedMedia(
                    evidence.StagedMedia,
                    sourceFileName: otherFileName),
            },
            "staged.digest" => evidence with
            {
                StagedMedia = CreateStagedMedia(
                    evidence.StagedMedia,
                    contentDigest: otherDigest),
            },
            "staged.byteSize" => evidence with
            {
                StagedMedia = CreateStagedMedia(
                    evidence.StagedMedia,
                    byteSize: otherByteSize),
            },
            "probe.candidateId" => evidence with
            {
                ProbeResult = CreateProbeResult(
                    evidence.StagedMedia,
                    candidateId: otherAssetId),
            },
            "probe.projectId" => evidence with
            {
                ProbeResult = CreateProbeResult(
                    evidence.StagedMedia,
                    projectId: otherProjectId),
            },
            "probe.jobId" => evidence with
            {
                ProbeResult = CreateProbeResult(
                    evidence.StagedMedia,
                    jobId: otherJobId),
            },
            "probe.sourceFileName" => evidence with
            {
                ProbeResult = CreateProbeResult(
                    evidence.StagedMedia,
                    sourceFileName: otherFileName),
            },
            "probe.digest" => evidence with
            {
                ProbeResult = CreateProbeResult(
                    evidence.StagedMedia,
                    contentDigest: otherDigest),
            },
            "probe.byteSize" => evidence with
            {
                ProbeResult = CreateProbeResult(
                    evidence.StagedMedia,
                    byteSize: otherByteSize),
            },
            "mediaInfo.candidateId" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    candidateId: otherAssetId),
            },
            "mediaInfo.projectId" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    projectId: otherProjectId),
            },
            "mediaInfo.jobId" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    jobId: otherJobId),
            },
            "mediaInfo.sourceFileName" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    sourceFileName: otherFileName),
            },
            "mediaInfo.digest" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    contentDigest: otherDigest),
            },
            "mediaInfo.byteSize" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    byteSize: otherByteSize),
            },
            "decode.candidateId" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    candidateId: otherAssetId),
            },
            "decode.projectId" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    projectId: otherProjectId),
            },
            "decode.jobId" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    jobId: otherJobId),
            },
            "decode.sourceFileName" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    sourceFileName: otherFileName),
            },
            "decode.digest" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    contentDigest: otherDigest),
            },
            "decode.byteSize" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    byteSize: otherByteSize),
            },
            "probe.videoStreamIndex" => evidence with
            {
                ProbeResult = CreateProbeResult(evidence.StagedMedia, videoStreamIndex: 7),
            },
            "probe.audioStreamIndex" => evidence with
            {
                ProbeResult = CreateProbeResult(evidence.StagedMedia, audioStreamIndex: 8),
            },
            "probe.audioAbsent" => evidence with
            {
                ProbeResult = CreateProbeResult(evidence.StagedMedia, audioStreamIndex: null),
            },
            "mediaInfo.videoStreamIndex" => evidence with
            {
                MediaInfo = CreateMediaInfo(evidence.StagedMedia, videoStreamIndex: 7),
            },
            "mediaInfo.audioStreamIndex" => evidence with
            {
                MediaInfo = CreateMediaInfo(evidence.StagedMedia, audioStreamIndex: 8),
            },
            "mediaInfo.audioAbsent" => evidence with
            {
                MediaInfo = CreateMediaInfo(evidence.StagedMedia, audioStreamIndex: null),
            },
            "mediaInfo.videoCodec" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    videoCodecName: "hevc"),
            },
            "mediaInfo.videoDimensions" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    videoWidth: 1280),
            },
            "mediaInfo.videoTimeBase" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    videoTimeBase: new RationalNumber(1, 90000)),
            },
            "mediaInfo.formatDuration" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    formatDurationSeconds: 2m),
            },
            "mediaInfo.formatNames" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    formatNames: ["mov"]),
            },
            "mediaInfo.streamSet" => evidence with
            {
                MediaInfo = CreateMediaInfo(
                    evidence.StagedMedia,
                    includeOtherStream: true),
            },
            "decode.videoStreamIndex" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    videoStreamIndex: 7),
            },
            "decode.audioStreamIndex" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    audioStreamIndex: 8),
            },
            "decode.audioAbsent" => evidence with
            {
                DecodeValidation = CreateDecodeValidation(
                    evidence.StagedMedia,
                    audioStreamIndex: null),
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(mismatch),
                mismatch,
                "Unknown binding mismatch scenario."),
        };
    }

    private static Asset CreateAsset(
        StagedMediaImport stagedMedia,
        EntityId<AssetIdTag>? candidateId = null,
        string? sourceFileName = null,
        ContentDigest? contentDigest = null,
        long? byteSize = null,
        RightsRecord? rights = null) =>
        new(
            candidateId ?? stagedMedia.CandidateId,
            sourceFileName ?? stagedMedia.SourceFileName,
            "video/mp4",
            contentDigest ?? stagedMedia.ContentDigest,
            byteSize ?? stagedMedia.ByteSize,
            rights ?? new RightsRecord(
                RightsStatus.Approved,
                "evidence:synthetic-approval",
                "internal editing",
                externalPublicationAllowed: true));

    private static StagedMediaImport CreateStagedMedia(
        StagedMediaImport source,
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        string? sourceFileName = null,
        string? stagingPath = null,
        ContentDigest? contentDigest = null,
        long? byteSize = null) =>
        CreateStagedMediaSnapshot(
            candidateId ?? source.CandidateId,
            projectId ?? source.ProjectId,
            jobId ?? source.JobId,
            sourceFileName ?? source.SourceFileName,
            stagingPath ?? source.StagingPath,
            contentDigest ?? source.ContentDigest,
            byteSize ?? source.ByteSize,
            source.CreatedAt);

    private static StagedMediaImport CreateStagedMediaSnapshot(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        string stagingPath,
        ContentDigest contentDigest,
        long byteSize,
        DateTimeOffset createdAt)
    {
        ConstructorInfo constructor = typeof(StagedMediaImport)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        return (StagedMediaImport)constructor.Invoke(
        [
            candidateId,
            projectId,
            jobId,
            sourceFileName,
            stagingPath,
            contentDigest,
            byteSize,
            createdAt,
        ]);
    }

    private static MediaProbeResult CreateProbeResult(
        StagedMediaImport stagedMedia,
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        string? sourceFileName = null,
        ContentDigest? contentDigest = null,
        long? byteSize = null,
        int videoStreamIndex = 2,
        int? audioStreamIndex = 4,
        string videoCodecName = "h264",
        int videoWidth = 1920,
        RationalNumber? videoTimeBase = null,
        decimal formatDurationSeconds = 1m,
        IReadOnlyList<string>? formatNames = null,
        bool includeOtherStream = false)
    {
        var streams = new List<ProbedMediaStream>
        {
            CreateProbedVideo(
                videoStreamIndex,
                videoCodecName,
                videoWidth,
                videoTimeBase ?? new RationalNumber(1, 1000)),
        };
        IReadOnlyList<string> warnings = [];
        if (audioStreamIndex is int audioIndex)
        {
            streams.Add(CreateProbedAudio(audioIndex));
        }
        else
        {
            warnings = ["audio-track-absent"];
        }

        if (includeOtherStream)
        {
            streams.Add(CreateProbedOther(index: 9));
        }

        long resolvedByteSize = byteSize ?? stagedMedia.ByteSize;
        return new MediaProbeResult(
            candidateId ?? stagedMedia.CandidateId,
            projectId ?? stagedMedia.ProjectId,
            jobId ?? stagedMedia.JobId,
            sourceFileName ?? stagedMedia.SourceFileName,
            contentDigest ?? stagedMedia.ContentDigest,
            resolvedByteSize,
            ContainerSignature.IsoBaseMediaFtyp,
            formatNames ?? ["mov", "mp4"],
            "QuickTime / MOV",
            durationSeconds: formatDurationSeconds,
            declaredByteSize: resolvedByteSize,
            bitRate: null,
            probeScore: 100,
            streams,
            warnings);
    }

    private static MediaInfo CreateMediaInfo(
        StagedMediaImport stagedMedia,
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        string? sourceFileName = null,
        ContentDigest? contentDigest = null,
        long? byteSize = null,
        int videoStreamIndex = 2,
        int? audioStreamIndex = 4,
        string videoCodecName = "h264",
        int videoWidth = 1920,
        RationalNumber? videoTimeBase = null,
        decimal formatDurationSeconds = 1m,
        IReadOnlyList<string>? formatNames = null,
        bool includeOtherStream = false) =>
        IngestValidator.Validate(
            CreateProbeResult(
                stagedMedia,
                candidateId,
                projectId,
                jobId,
                sourceFileName,
                contentDigest,
                byteSize,
                videoStreamIndex,
                audioStreamIndex,
                videoCodecName,
                videoWidth,
                videoTimeBase,
                formatDurationSeconds,
                formatNames,
                includeOtherStream),
            new IngestValidationRequirements());

    private static DecodeValidationResult CreateDecodeValidation(
        StagedMediaImport stagedMedia,
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        string? sourceFileName = null,
        ContentDigest? contentDigest = null,
        long? byteSize = null,
        int videoStreamIndex = 2,
        int? audioStreamIndex = 4)
    {
        ConstructorInfo constructor = typeof(DecodeValidationResult)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        return (DecodeValidationResult)constructor.Invoke(
        [
            candidateId ?? stagedMedia.CandidateId,
            projectId ?? stagedMedia.ProjectId,
            jobId ?? stagedMedia.JobId,
            sourceFileName ?? stagedMedia.SourceFileName,
            contentDigest ?? stagedMedia.ContentDigest,
            byteSize ?? stagedMedia.ByteSize,
            videoStreamIndex,
            audioStreamIndex,
            TimeSpan.FromMilliseconds(1),
            0,
            EmptyContentDigest,
            default(RationalTime),
            new RationalTime(2, 25),
            2,
        ]);
    }

    private static ProbedMediaStream CreateProbedVideo(
        int index,
        string codecName = "h264",
        int width = 1920,
        RationalNumber? timeBase = null) =>
        new(
            index,
            MediaStreamKind.Video,
            codecName,
            "High",
            width,
            1080,
            "yuv420p",
            new RationalNumber(25, 1),
            new RationalNumber(25, 1),
            timeBase ?? new RationalNumber(1, 1000),
            startSeconds: 0m,
            durationSeconds: 1m,
            sampleRate: null,
            channels: null,
            channelLayout: null);

    private static ProbedMediaStream CreateProbedAudio(int index) =>
        new(
            index,
            MediaStreamKind.Audio,
            "aac",
            "LC",
            width: null,
            height: null,
            pixelFormat: null,
            nominalFrameRate: null,
            averageFrameRate: null,
            new RationalNumber(1, 48000),
            startSeconds: 0m,
            durationSeconds: 1m,
            sampleRate: 48000,
            channels: 2,
            channelLayout: "stereo");

    private static ProbedMediaStream CreateProbedOther(int index) =>
        new(
            index,
            MediaStreamKind.Other,
            "bin_data",
            profile: null,
            width: null,
            height: null,
            pixelFormat: null,
            nominalFrameRate: null,
            averageFrameRate: null,
            new RationalNumber(1, 1000),
            startSeconds: null,
            durationSeconds: null,
            sampleRate: null,
            channels: null,
            channelLayout: null);

    private static PtsIndex CreatePtsIndex(StagedMediaImport stagedMedia) =>
        new(
            stagedMedia.CandidateId,
            stagedMedia.ProjectId,
            stagedMedia.JobId,
            stagedMedia.SourceFileName,
            stagedMedia.ContentDigest,
            stagedMedia.ByteSize,
            streamIndex: 2,
            new RationalTime(1, 1000),
            [
                new PtsIndexEntry(
                    sequence: 0,
                    presentationTime: default,
                    duration: new RationalTime(1, 25),
                    keyFrame: true),
                new PtsIndexEntry(
                    sequence: 1,
                    presentationTime: new RationalTime(1, 25),
                    duration: new RationalTime(1, 25),
                    keyFrame: false),
            ]);

    private static DecodeValidationResult CreateBaselineDecodeValidation(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        PtsIndex ptsIndex)
    {
        int videoStreamIndex = probeResult.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .Min(static stream => stream.Index);
        int? audioStreamIndex = probeResult.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .Select(static stream => (int?)stream.Index)
            .Min();
        Assert.AreEqual(videoStreamIndex, ptsIndex.StreamIndex);
        return CreateDecodeValidation(
            stagedMedia,
            videoStreamIndex: videoStreamIndex,
            audioStreamIndex: audioStreamIndex);
    }

    private static void MutateRegisteredContent(
        string mutation,
        string destinationPath,
        byte[] originalContent)
    {
        switch (mutation)
        {
            case "digest":
                byte[] changed = originalContent.ToArray();
                changed[0] ^= 0xff;
                File.WriteAllBytes(destinationPath, changed);
                break;
            case "size":
                using (FileStream stream = new(
                    destinationPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.WriteByte(0xff);
                }

                break;
            case "missing":
                File.Delete(destinationPath);
                break;
            case "directory":
                File.Delete(destinationPath);
                Directory.CreateDirectory(destinationPath);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mutation),
                    mutation,
                    "Unknown content mutation scenario.");
        }
    }

    private static void AssertPublicSealedImmutable(
        Type type,
        params string[] propertyNames)
    {
        Assert.IsTrue(type.IsPublic, type.Name);
        Assert.IsTrue(type.IsSealed, type.Name);
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            propertyNames,
            properties.Select(static property => property.Name).ToArray(),
            type.Name);
        Assert.IsTrue(
            properties.All(static property => property.SetMethod is null),
            type.Name);
    }

    private static MethodInfo[] GetDeclaredPublicMethods(Type type) =>
        type.GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();

    private static void AssertStoredArtifactEqual(
        StoredArtifact expected,
        StoredArtifact actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.ProjectId, actual.ProjectId);
        Assert.AreEqual(expected.Kind, actual.Kind);
        Assert.AreEqual(expected.StorageReference, actual.StorageReference);
        Assert.AreEqual(expected.ContentDigest, actual.ContentDigest);
        Assert.AreEqual(expected.ByteSize, actual.ByteSize);
        Assert.AreEqual(expected.CreatedAt, actual.CreatedAt);
    }

    private static void AssertSafeFailure(
        IngestServiceException exception,
        IngestServiceFailureReason reason,
        bool reconciliationRequired,
        params string[] forbiddenValues)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        foreach (string forbiddenValue in forbiddenValues)
        {
            if (string.IsNullOrEmpty(forbiddenValue))
            {
                continue;
            }

            Assert.IsFalse(exception.Message.Contains(
                forbiddenValue,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed record EvidenceBundle(
        Asset Asset,
        StagedMediaImport StagedMedia,
        MediaProbeResult ProbeResult,
        MediaInfo MediaInfo,
        DecodeValidationResult DecodeValidation);

    private sealed class IngestFixture : IDisposable
    {
        internal IngestFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.IngestServiceTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            DatabasePath = Path.Combine(RootPath, "store.db");
            BackupPath = Path.Combine(RootPath, "backups");
            Directory.CreateDirectory(WorkspacePath);
            _ = DatabaseMigrator.Migrate(DatabasePath, BackupPath);

            ProjectId = EntityId<ProjectIdTag>.New();
            ArtifactId = EntityId<ArtifactIdTag>.New();
            EntityId<AssetIdTag> candidateId = EntityId<AssetIdTag>.New();
            EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
            CreateProject(ProjectId);

            Content = Encoding.UTF8.GetBytes(
                "M-006 synthetic verified original content; no private media.");
            string stagingPath = CreateStagingFile(
                Path.Combine(
                    "imports",
                    jobId.ToString(),
                    string.Concat(candidateId.ToString(), ".ready")),
                Content);
            StagedMediaImport stagedMedia = CreateStagedMediaSnapshot(
                candidateId,
                ProjectId,
                jobId,
                "source.mov",
                stagingPath,
                ContentDigest.Compute(Content),
                Content.LongLength,
                CreatedAt);
            Asset asset = CreateAsset(stagedMedia);
            MediaProbeResult probeResult = CreateProbeResult(stagedMedia);
            MediaInfo mediaInfo = IngestValidator.Validate(
                probeResult,
                new IngestValidationRequirements());
            PtsIndex ptsIndex = CreatePtsIndex(stagedMedia);
            DecodeValidationResult decodeValidation =
                CreateBaselineDecodeValidation(stagedMedia, probeResult, ptsIndex);
            Evidence = new EvidenceBundle(
                asset,
                stagedMedia,
                probeResult,
                mediaInfo,
                decodeValidation);

            Store = new ArtifactStore(DatabasePath, WorkspacePath);
            Service = new IngestService(Store, new BinaryVerifier());
        }

        internal string RootPath { get; }

        internal string WorkspacePath { get; }

        internal string DatabasePath { get; }

        internal string BackupPath { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal EntityId<ArtifactIdTag> ArtifactId { get; }

        internal byte[] Content { get; }

        internal EvidenceBundle Evidence { get; }

        internal ArtifactStore Store { get; }

        internal IngestService Service { get; }

        internal Task<ImportedOriginal> PromoteAsync() =>
            PromoteAsync(Evidence, CancellationToken.None);

        internal Task<ImportedOriginal> PromoteAsync(EvidenceBundle evidence) =>
            PromoteAsync(evidence, CancellationToken.None);

        internal Task<ImportedOriginal> PromoteAsync(
            EvidenceBundle evidence,
            CancellationToken cancellationToken) =>
            Service.PromoteVerifiedOriginalAsync(
                ArtifactId,
                evidence.Asset,
                evidence.StagedMedia,
                evidence.ProbeResult,
                evidence.MediaInfo,
                evidence.DecodeValidation,
                cancellationToken);

        internal string CreateStagingFile(string relativePath, byte[] content)
        {
            string stagingPath = Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            File.WriteAllBytes(stagingPath, content);
            return stagingPath;
        }

        internal StagedMediaImport CreateRetryStaging()
        {
            string stagingPath = CreateStagingFile(
                Path.Combine("retry", string.Concat(Guid.NewGuid().ToString("N"), ".ready")),
                Content);
            return CreateStagedMedia(Evidence.StagedMedia, stagingPath: stagingPath);
        }

        internal string GetExpectedStorageReference() =>
            string.Concat(
                "projects/",
                ProjectId.ToString(),
                "/original/",
                Evidence.Asset.Id.ToString(),
                "/",
                Evidence.Asset.ContentDigest.Hex,
                ".original");

        internal string GetExpectedDestinationPath() =>
            Path.Combine(
                WorkspacePath,
                GetExpectedStorageReference().Replace(
                    '/',
                    Path.DirectorySeparatorChar));

        internal StoredProject GetProject() =>
            new ProjectStore(DatabasePath).GetProject(ProjectId)
            ?? throw new AssertFailedException("The test project row was not found.");

        internal long CountArtifacts()
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM artifacts;";
            return (long)(command.ExecuteScalar()
                ?? throw new AssertFailedException("The artifact count was null."));
        }

        internal void MutateRegisteredMetadata(
            string mutation,
            StoredArtifact snapshot)
        {
            string id = snapshot.Id.ToString();
            switch (mutation)
            {
                case "id-missing":
                    ExecuteNonQuery(
                        "UPDATE artifacts SET id = $value WHERE id = $id;",
                        ("$value", EntityId<ArtifactIdTag>.New().ToString()),
                        ("$id", id));
                    break;
                case "project":
                    EntityId<ProjectIdTag> otherProject = EntityId<ProjectIdTag>.New();
                    CreateProject(otherProject);
                    ExecuteNonQuery(
                        """
                        UPDATE artifacts
                        SET project_id = $project_id,
                            storage_reference = $storage_reference
                        WHERE id = $id;
                        """,
                        ("$project_id", otherProject.ToString()),
                        ("$storage_reference", snapshot.StorageReference.Replace(
                            ProjectId.ToString(),
                            otherProject.ToString(),
                            StringComparison.Ordinal)),
                        ("$id", id));
                    break;
                case "kind":
                    ExecuteNonQuery(
                        """
                        UPDATE artifacts
                        SET kind = $kind,
                            storage_reference = $storage_reference
                        WHERE id = $id;
                        """,
                        ("$kind", ArtifactKind.Proxy.ToString()),
                        ("$storage_reference", snapshot.StorageReference.Replace(
                            "/original/",
                            "/proxy/",
                            StringComparison.Ordinal)),
                        ("$id", id));
                    break;
                case "storage":
                    ExecuteNonQuery(
                        "UPDATE artifacts SET storage_reference = $value WHERE id = $id;",
                        ("$value", string.Concat(
                            "projects/",
                            ProjectId.ToString(),
                            "/original/other/",
                            snapshot.ContentDigest.Hex,
                            ".original")),
                        ("$id", id));
                    break;
                case "digest":
                    ExecuteNonQuery(
                        "UPDATE artifacts SET content_digest = $value WHERE id = $id;",
                        ("$value", new string('c', 64)),
                        ("$id", id));
                    break;
                case "byteSize":
                    ExecuteNonQuery(
                        "UPDATE artifacts SET byte_size = $value WHERE id = $id;",
                        ("$value", checked(snapshot.ByteSize + 1)),
                        ("$id", id));
                    break;
                case "createdAt":
                    ExecuteNonQuery(
                        "UPDATE artifacts SET created_at = $value WHERE id = $id;",
                        ("$value", new DateTimeOffset(
                            2040,
                            1,
                            2,
                            3,
                            4,
                            5,
                            TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)),
                        ("$id", id));
                    break;
                case "corruptStorage":
                    ExecuteNonQuery(
                        "UPDATE artifacts SET storage_reference = $value WHERE id = $id;",
                        ("$value", string.Concat(
                            "projects/",
                            ProjectId.ToString(),
                            "/original/../escape.original")),
                        ("$id", id));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mutation),
                        mutation,
                        "Unknown metadata mutation scenario.");
            }
        }

        internal void ExecuteNonQuery(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters)
            {
                _ = command.Parameters.AddWithValue(name, value);
            }

            _ = command.ExecuteNonQuery();
        }

        private void CreateProject(EntityId<ProjectIdTag> projectId)
        {
            var project = new Project(
                projectId,
                "M-006 test project",
                "Verify original promotion invariants.",
                "Test suite",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            new ProjectStore(DatabasePath).CreateProject(
                project,
                "{\"m006\":true}",
                CreatedAt);
        }

        private SqliteConnection OpenConnection()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            };
            var connection = new SqliteConnection(builder.ToString())
            {
                DefaultTimeout = 5,
            };
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }
}
