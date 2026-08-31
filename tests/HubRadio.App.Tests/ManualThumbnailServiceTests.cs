using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using HubRadio.App.Thumbnails;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ManualThumbnailServiceTests
{
    [TestMethod]
    public async Task GenerateAsync_ExplicitVfrSelectionProducesVerifiedPngAndEvidence()
    {
        using Fixture fixture = new();

        ManualThumbnailResult result = await fixture.GenerateAsync();

        Assert.AreEqual(fixture.Request.Id, result.RequestId);
        Assert.AreEqual(fixture.OutputArtifactId, result.ArtifactId);
        Assert.AreEqual(fixture.Project.Id, result.ProjectId);
        Assert.AreEqual(fixture.SourceArtifact.Id, result.Source.SourceArtifactId);
        Assert.AreEqual(fixture.SourceAsset.Id, result.Source.SourceAssetId);
        Assert.AreEqual(fixture.SourceAsset.ContentDigest, result.Source.SourceContentDigest);
        Assert.AreEqual(fixture.SourceAsset.ByteSize, result.Source.SourceByteSize);
        Assert.AreEqual(fixture.SourcePtsDigest, result.Source.SourcePtsIndexDigest);
        Assert.AreEqual(fixture.FrameMapDigest, result.Source.ProxyFrameMapDigest);
        Assert.AreEqual(4, result.Selection.ProxyFrameSequence);
        Assert.AreEqual(2, result.Selection.SourceFrameSequence);
        Assert.AreEqual(new RationalTime(110, 1000), result.Selection.SourcePresentationTime);
        Assert.AreEqual(fixture.TemplateVersion.Reference.Id, result.Template.TemplateId);
        Assert.AreEqual(
            fixture.TemplateVersion.Reference.VersionDigest,
            result.Template.TemplateVersionDigest);
        Assert.AreEqual(fixture.Approval.Id, result.Template.ApprovalId);
        Assert.AreEqual(fixture.Approval.ApprovalDigest, result.Template.ApprovalDigest);
        Assert.AreEqual(fixture.Approval.ActorId, result.TemplateApprovalActorId);
        Assert.AreEqual(ReviewActorRole.BrandApprover, result.TemplateApprovalRole);
        Assert.AreEqual(fixture.Approval.ApprovedAt, result.TemplateApprovedAt);
        Assert.AreEqual(OriginKind.Asset, result.Origin.Kind);
        Assert.AreEqual(fixture.SourceAsset.Id, result.Origin.AssetId);
        Assert.AreEqual(2, result.SourceVideoStreamIndex);
        Assert.AreEqual(16, result.OutputWidth);
        Assert.AreEqual(9, result.OutputHeight);
        Assert.AreEqual(Fixture.OutputCreatedAt, result.CreatedAt);
        Assert.IsFalse(result.WasAlreadyRegistered);

        Assert.HasCount(1, fixture.ProcessRequests);
        ProcessRunRequest process = fixture.ProcessRequests[0];
        CollectionAssert.AreEqual(
            new[]
            {
                "-hide_banner",
                "-nostdin",
                "-v",
                "error",
                "-i",
                fixture.SourcePath,
                "-map",
                "0:2",
                "-vf",
                "select=eq(n\\,2),scale=16:9:force_original_aspect_ratio=decrease:flags=bilinear,pad=16:9:(ow-iw)/2:(oh-ih)/2:color=black,pad=16:9:0:0:color=black,setsar=1",
                "-frames:v",
                "1",
                "-an",
                "-map_metadata",
                "-1",
                "-c:v",
                "png",
                "-f",
                "image2",
                "-n",
                process.ArgumentList[^1],
            },
            process.ArgumentList.ToArray());
        Assert.IsFalse(process.ArgumentList.Contains("-y", StringComparer.Ordinal));
        Assert.IsTrue(process.ArgumentList[9].Contains(
            "eq(n\\,2)",
            StringComparison.Ordinal));
        Assert.IsFalse(process.ArgumentList[9].Contains(
            "eq(n\\,3)",
            StringComparison.Ordinal));

        string outputPath = fixture.Store.GetValidatedRegisteredArtifactPath(
            result.ArtifactId);
        byte[] output = File.ReadAllBytes(outputPath);
        Assert.AreEqual(ContentDigest.Compute(output), result.OutputContentDigest);
        Assert.AreEqual(output.LongLength, result.OutputByteSize);
        CollectionAssert.AreEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            output[..8]);
        Assert.AreEqual(2, fixture.Store.ListProjectArtifacts(fixture.Project.Id).Count);
        Assert.IsFalse(Directory.Exists(fixture.StagingDirectory));
        CollectionAssert.AreEqual(fixture.SourceBytes, File.ReadAllBytes(fixture.SourcePath));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GenerateAsync_MissingOrMismatchedSourcePtsIsRejectedBeforeWork(
        bool missing)
    {
        using Fixture fixture = new();
        RationalTime? pts = missing ? null : new RationalTime(111, 1000);
        ManualThumbnailFrameSelection selection = fixture.CreateSelection(
            proxyFrameSequence: 4,
            sourceFrameSequence: 2,
            pts);

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync(
                    request: fixture.CreateRequest(selection: selection)));

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.SelectedFrameInvalid,
            reconciliationRequired: false);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(0, fixture.ToolVerificationCount);
        Assert.AreEqual(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id).Count);
    }

    [TestMethod]
    public async Task GenerateAsync_ProxySelectionMustMapToTheSelectedSourceFrameAndPts()
    {
        using Fixture fixture = new();
        ManualThumbnailFrameSelection selection = fixture.CreateSelection(
            proxyFrameSequence: 2,
            sourceFrameSequence: 2,
            new RationalTime(110, 1000));

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync(
                    request: fixture.CreateRequest(selection: selection)));

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.ProxyMappingStale,
            reconciliationRequired: false);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(0, fixture.ToolVerificationCount);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(4)]
    public async Task GenerateAsync_NonexistentSourceFrameIsRejected(int sourceFrameSequence)
    {
        using Fixture fixture = new();
        ManualThumbnailFrameSelection selection = fixture.CreateSelection(
            proxyFrameSequence: 4,
            sourceFrameSequence,
            new RationalTime(110, 1000));

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync(
                    request: fixture.CreateRequest(selection: selection)));

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.SelectedFrameInvalid,
            reconciliationRequired: false);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(0, fixture.ToolVerificationCount);
    }

    [TestMethod]
    [DataRow("source", ManualThumbnailFailureReason.SourceBindingStale)]
    [DataRow("pts", ManualThumbnailFailureReason.SourceBindingStale)]
    [DataRow("map", ManualThumbnailFailureReason.ProxyMappingStale)]
    public async Task GenerateAsync_RejectsStaleSourceTimelineAndMapBindings(
        string mutation,
        ManualThumbnailFailureReason expectedReason)
    {
        using Fixture fixture = new();
        ManualThumbnailContext context = mutation switch
        {
            "source" => fixture.CreateContext(
                project: fixture.CreateProject(
                    fixture.CreateAsset(ContentDigestFor('f')))),
            "pts" => fixture.CreateContext(
                sourcePtsIndex: fixture.CreatePtsIndex()),
            "map" => fixture.CreateContext(
                proxyFrameMap: fixture.CreateProxyFrameMap(
                    proxyContentDigest: ContentDigestFor('f'))),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync(context: context));

        AssertFailure(
            fixture,
            exception,
            expectedReason,
            reconciliationRequired: false);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(0, fixture.ToolVerificationCount);
    }

    [TestMethod]
    [DataRow("template", ManualThumbnailFailureReason.TemplateBindingStale)]
    [DataRow("profile", ManualThumbnailFailureReason.ProfileBindingStale)]
    [DataRow("layout", ManualThumbnailFailureReason.LayoutBindingStale)]
    public async Task GenerateAsync_RejectsStaleTemplateProfileAndLayoutBindings(
        string mutation,
        ManualThumbnailFailureReason expectedReason)
    {
        using Fixture fixture = new();
        ManualThumbnailContext context;
        switch (mutation)
        {
            case "template":
                BrandTemplateVersion version = fixture.CreateTemplateVersion(
                    templateId: EntityId<TemplateIdTag>.New(),
                    versionDigest: ContentDigestFor('f'));
                context = fixture.CreateContext(
                    templateVersion: version,
                    template: version.ApplyEditorOverrides([]));
                break;
            case "profile":
                context = fixture.CreateContext(
                    renderProfile: fixture.CreateRenderProfile(
                        profileId: EntityId<RenderProfileIdTag>.New(),
                        profileDigest: ContentDigestFor('f')));
                break;
            case "layout":
                context = fixture.CreateContext(
                    layoutSpec: fixture.CreateLayoutSpec(
                        EntityId<LayoutSpecIdTag>.New()));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync(context: context));

        AssertFailure(
            fixture,
            exception,
            expectedReason,
            reconciliationRequired: false);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(0, fixture.ToolVerificationCount);
    }

    [TestMethod]
    public async Task GenerateAsync_MissingTrustedTemplateApprovalIsRejected()
    {
        using Fixture fixture = new();

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync(
                    context: fixture.CreateContext(withoutApproval: true)));

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.TemplateNotApproved,
            reconciliationRequired: false);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(0, fixture.ToolVerificationCount);
    }

    [TestMethod]
    [DataRow(RightsStatus.Unconfirmed, false)]
    [DataRow(RightsStatus.Rejected, false)]
    [DataRow(RightsStatus.Approved, false)]
    public async Task GenerateAsync_SourceMustBeApprovedForExternalUse(
        RightsStatus status,
        bool externalPublicationAllowed)
    {
        using Fixture fixture = new(status, externalPublicationAllowed);

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync());

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.SourceRightsNotApproved,
            reconciliationRequired: false);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(0, fixture.ToolVerificationCount);
    }

    [TestMethod]
    public async Task GenerateAsync_IdenticalRequestReusesOnlyExactVerifiedArtifact()
    {
        using Fixture fixture = new();

        ManualThumbnailResult first = await fixture.GenerateAsync();
        ManualThumbnailResult second = await fixture.GenerateAsync();

        Assert.IsFalse(first.WasAlreadyRegistered);
        Assert.IsTrue(second.WasAlreadyRegistered);
        Assert.AreEqual(first.ArtifactId, second.ArtifactId);
        Assert.AreEqual(first.BindingDigest, second.BindingDigest);
        Assert.AreEqual(first.OutputContentDigest, second.OutputContentDigest);
        Assert.AreEqual(first.OutputByteSize, second.OutputByteSize);
        Assert.HasCount(1, fixture.ProcessRequests);
        Assert.AreEqual(1, fixture.ToolVerificationCount);
        Assert.AreEqual(2, fixture.Store.ListProjectArtifacts(fixture.Project.Id).Count);
        Assert.IsFalse(Directory.Exists(fixture.StagingDirectory));
    }

    [TestMethod]
    public async Task GenerateAsync_ReusedIdentityCannotBindDifferentSelection()
    {
        using Fixture fixture = new();
        _ = await fixture.GenerateAsync();
        ManualThumbnailFrameSelection changed = fixture.CreateSelection(
            proxyFrameSequence: 6,
            sourceFrameSequence: 3,
            new RationalTime(140, 1000));
        ManualThumbnailRequest duplicate = fixture.CreateRequest(selection: changed);

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync(request: duplicate));

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.DuplicateRequest,
            reconciliationRequired: false);
        Assert.HasCount(1, fixture.ProcessRequests);
        Assert.AreEqual(2, fixture.Store.ListProjectArtifacts(fixture.Project.Id).Count);
    }

    [TestMethod]
    public async Task GenerateAsync_CancellationRemovesOwnedPartialOutputAndPreservesSource()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        fixture.ProcessHandler = (request, token) =>
        {
            File.WriteAllBytes(request.ArgumentList[^1], [1, 2, 3, 4]);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new AssertFailedException("Cancellation was not observed.");
        };

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.GenerateAsync(cancellationToken: cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.HasCount(1, fixture.ProcessRequests);
        Assert.IsFalse(Directory.Exists(fixture.StagingDirectory));
        Assert.AreEqual(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id).Count);
        CollectionAssert.AreEqual(fixture.SourceBytes, File.ReadAllBytes(fixture.SourcePath));
    }

    [TestMethod]
    public async Task GenerateAsync_TimeoutAndProcessFailureAreTypedCleanAndPathPrivate()
    {
        using Fixture timeoutFixture = new();
        timeoutFixture.ProcessHandler = (request, _) =>
        {
            File.WriteAllBytes(request.ArgumentList[^1], [1, 2, 3]);
            throw new TimeoutException(timeoutFixture.SourcePath);
        };
        ManualThumbnailException timeout =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                timeoutFixture.GenerateAsync());
        AssertFailure(
            timeoutFixture,
            timeout,
            ManualThumbnailFailureReason.ProcessTimedOut,
            reconciliationRequired: false);
        Assert.IsFalse(Directory.Exists(timeoutFixture.StagingDirectory));

        using Fixture failureFixture = new();
        failureFixture.ProcessHandler = (_, _) => Task.FromResult(
            new ProcessRunResult(
                1,
                string.Empty,
                failureFixture.SourcePath,
                Truncated: false,
                TimeSpan.Zero));
        ManualThumbnailException failure =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                failureFixture.GenerateAsync());
        AssertFailure(
            failureFixture,
            failure,
            ManualThumbnailFailureReason.ProcessFailed,
            reconciliationRequired: false);
        Assert.IsFalse(Directory.Exists(failureFixture.StagingDirectory));
    }

    [TestMethod]
    public async Task GenerateAsync_ToolVerificationFailurePrecedesStagingAndHidesPath()
    {
        using Fixture fixture = new();
        fixture.ToolVerificationHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BinaryVerificationResult.Failed(
                request,
                BinaryVerificationFailureReason.DigestMismatch,
                request.ExpectedByteSize,
                ContentDigestFor('f')));
        };

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync());

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.ToolVerificationFailed,
            reconciliationRequired: false);
        Assert.AreEqual(1, fixture.ToolVerificationCount);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.IsFalse(Directory.Exists(fixture.StagingDirectory));
    }

    [TestMethod]
    [DataRow("signature")]
    [DataRow("dimensions")]
    [DataRow("crc")]
    [DataRow("palette")]
    [DataRow("reserved")]
    public async Task GenerateAsync_CorruptPngIsRejectedBeforePromotion(string mutation)
    {
        using Fixture fixture = new();
        fixture.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string outputPath = request.ArgumentList[^1];
            switch (mutation)
            {
                case "signature":
                    File.WriteAllBytes(outputPath, new byte[64]);
                    break;
                case "dimensions":
                    WritePng(outputPath, width: 15, height: 9);
                    break;
                case "crc":
                    WritePng(outputPath, width: 16, height: 9);
                    byte[] bytes = File.ReadAllBytes(outputPath);
                    bytes[29] ^= 1;
                    File.WriteAllBytes(outputPath, bytes);
                    break;
                case "palette":
                    WritePng(outputPath, width: 16, height: 9);
                    InsertPngChunkAfterHeader(outputPath, "PLTE"u8, [0, 1]);
                    break;
                case "reserved":
                    WritePng(outputPath, width: 16, height: 9);
                    InsertPngChunkAfterHeader(outputPath, "abca"u8, []);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }

            return Task.FromResult(SuccessfulProcessResult());
        };

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync());

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.OutputInvalid,
            reconciliationRequired: false);
        Assert.AreEqual(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id).Count);
        Assert.IsFalse(Directory.Exists(fixture.StagingDirectory));
    }

    [TestMethod]
    public async Task GenerateAsync_PreexistingPartialStagingIsNotClaimedOrDeleted()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(fixture.StagingDirectory);
        string partialPath = Path.Combine(
            fixture.StagingDirectory,
            "thumbnail.partial.png");
        byte[] partial = [7, 8, 9];
        File.WriteAllBytes(partialPath, partial);

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync());

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.StagingConflict,
            reconciliationRequired: false);
        CollectionAssert.AreEqual(partial, File.ReadAllBytes(partialPath));
        Assert.IsTrue(Directory.Exists(fixture.StagingDirectory));
        Assert.IsEmpty(fixture.ProcessRequests);
    }

    [TestMethod]
    public async Task GenerateAsync_SourceIsHeldAgainstMutationThroughoutRendering()
    {
        using Fixture fixture = new();
        bool writeWasBlocked = false;
        fixture.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using FileStream sourceWriter = new(
                    fixture.SourcePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                sourceWriter.WriteByte(0xff);
            }
            catch (IOException)
            {
                writeWasBlocked = true;
            }

            WritePng(request.ArgumentList[^1], width: 16, height: 9);
            return Task.FromResult(SuccessfulProcessResult());
        };

        ManualThumbnailResult result = await fixture.GenerateAsync();

        Assert.IsTrue(writeWasBlocked);
        Assert.IsFalse(result.WasAlreadyRegistered);
        CollectionAssert.AreEqual(fixture.SourceBytes, File.ReadAllBytes(fixture.SourcePath));
    }

    [TestMethod]
    public async Task GenerateAsync_CorruptRegisteredSourceIsRejectedBeforeToolOrProcess()
    {
        using Fixture fixture = new();
        byte[] corrupt = fixture.SourceBytes.ToArray();
        corrupt[^1] ^= 1;
        File.WriteAllBytes(fixture.SourcePath, corrupt);

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync());

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.RegisteredSourceInvalid,
            reconciliationRequired: true);
        Assert.AreEqual(0, fixture.ToolVerificationCount);
        Assert.IsEmpty(fixture.ProcessRequests);
    }

    [TestMethod]
    public async Task GenerateAsync_CorruptRegisteredReuseIsRejectedWithoutRerender()
    {
        using Fixture fixture = new();
        ManualThumbnailResult first = await fixture.GenerateAsync();
        string outputPath = fixture.Store.GetValidatedRegisteredArtifactPath(
            first.ArtifactId);
        byte[] corrupt = File.ReadAllBytes(outputPath);
        corrupt[^1] ^= 1;
        File.WriteAllBytes(outputPath, corrupt);

        ManualThumbnailException exception =
            await Assert.ThrowsExactlyAsync<ManualThumbnailException>(() =>
                fixture.GenerateAsync());

        AssertFailure(
            fixture,
            exception,
            ManualThumbnailFailureReason.RegisteredOutputInvalid,
            reconciliationRequired: true);
        Assert.HasCount(1, fixture.ProcessRequests);
    }

    [TestMethod]
    public async Task GenerateAsync_CancellationAfterCommitBarrierDoesNotCreateHalfRegistration()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        fixture.PromotionHandler = async (request, token) =>
        {
            fixture.ObservedPromotionToken = token;
            cancellation.Cancel();
            return await fixture.Store.PromoteAsync(request, token);
        };

        ManualThumbnailResult result = await fixture.GenerateAsync(
            cancellationToken: cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(CancellationToken.None, fixture.ObservedPromotionToken);
        Assert.AreEqual(fixture.OutputArtifactId, result.ArtifactId);
        Assert.AreEqual(2, fixture.Store.ListProjectArtifacts(fixture.Project.Id).Count);
        Assert.IsFalse(Directory.Exists(fixture.StagingDirectory));
    }

    [TestMethod]
    public void ProcessRequest_IsFixedDirectAllowlistedAndDoesNotUseNominalFps()
    {
        using Fixture fixture = new();
        string outputPath = Path.Combine(fixture.RootPath, "thumbnail.png");

        ProcessRunRequest request = ManualThumbnailService.CreateProcessRequest(
            fixture.FfmpegPath,
            fixture.SourcePath,
            outputPath,
            videoStreamIndex: 2,
            sourceFrameSequence: 2,
            outputWidth: 16,
            outputHeight: 9,
            LayoutResolver.Resolve(fixture.LayoutSpec, 16, 9).Regions.Single().Rect,
            TimeSpan.FromSeconds(17));

        Assert.AreEqual(fixture.FfmpegPath, request.Executable);
        Assert.AreEqual(fixture.RootPath, request.WorkingDirectory);
        Assert.AreEqual(TimeSpan.FromSeconds(17), request.Timeout);
        Assert.IsNull(request.StandardInput);
        Assert.AreEqual(ProcessRunRequest.DefaultMaxCapturedBytes, request.MaxCapturedBytes);
        Assert.IsTrue(request.ArgumentList.Contains("-n", StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Contains("-y", StringComparer.Ordinal));
        Assert.IsTrue(request.ArgumentList.Contains("0:2", StringComparer.Ordinal));
        Assert.IsTrue(request.ArgumentList.Contains(
            "select=eq(n\\,2),scale=16:9:force_original_aspect_ratio=decrease:flags=bilinear,pad=16:9:(ow-iw)/2:(oh-ih)/2:color=black,pad=16:9:0:0:color=black,setsar=1",
            StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Any(static argument =>
            argument.Contains("fps", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("-ss", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PublicContract_IsImmutablePathPrivateAndApprovalConstructionIsRestricted()
    {
        foreach (Type type in new[]
        {
            typeof(ManualThumbnailFrameSelection),
            typeof(ApprovedThumbnailTemplateSnapshot),
            typeof(ManualThumbnailSourceBinding),
            typeof(ManualThumbnailTemplateBinding),
            typeof(ManualThumbnailRequest),
            typeof(ManualThumbnailContext),
            typeof(ManualThumbnailResult),
            typeof(ManualThumbnailException),
            typeof(ManualThumbnailService),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null),
                type.Name);
        }

        Assert.AreEqual(
            0,
            typeof(ApprovedThumbnailTemplateSnapshot).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ManualThumbnailResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ManualThumbnailException).GetConstructors().Length);
        Assert.IsFalse(typeof(ManualThumbnailResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property => property.Name.Contains(
                "Path",
                StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEqual(
            new[] { "GenerateAsync" },
            typeof(ManualThumbnailService)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());

        const string privatePath = @"C:\private\customer-source.mov";
        foreach (ManualThumbnailFailureReason reason in
            Enum.GetValues<ManualThumbnailFailureReason>())
        {
            var exception = new ManualThumbnailException(reason);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.ToString().Contains(
                privatePath,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertFailure(
        Fixture fixture,
        ManualThumbnailException exception,
        ManualThumbnailFailureReason expectedReason,
        bool reconciliationRequired)
    {
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            fixture.RootPath,
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(
            fixture.SourcePath,
            StringComparison.OrdinalIgnoreCase));
    }

    private static ContentDigest ContentDigestFor(char value) =>
        new(new string(value, 64));

    private static ProcessRunResult SuccessfulProcessResult() =>
        new(
            0,
            string.Empty,
            string.Empty,
            Truncated: false,
            TimeSpan.FromMilliseconds(7));

    private static void WritePng(string path, int width, int height)
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(0, 4),
            checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(4, 4),
            checked((uint)height));
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(png, "IHDR"u8, header);

        using var raw = new MemoryStream();
        byte[] row = new byte[checked(1 + (width * 4))];
        for (int rowIndex = 0; rowIndex < height; rowIndex++)
        {
            raw.Write(row);
        }

        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
            compressed,
            CompressionLevel.SmallestSize,
            leaveOpen: true))
        {
            raw.CopyTo(zlib);
        }

        WritePngChunk(png, "IDAT"u8, compressed.ToArray());
        WritePngChunk(png, "IEND"u8, []);
        File.WriteAllBytes(path, png.ToArray());
    }

    private static void WritePngChunk(
        Stream destination,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        destination.Write(length);
        destination.Write(type);
        destination.Write(data);
        uint crc = 0xffffffffu;
        crc = UpdateCrc32(crc, type);
        crc = UpdateCrc32(crc, data) ^ 0xffffffffu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        destination.Write(crcBytes);
    }

    private static void InsertPngChunkAfterHeader(
        string path,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        byte[] original = File.ReadAllBytes(path);
        const int afterHeaderOffset = 33;
        using var updated = new MemoryStream();
        updated.Write(original.AsSpan(0, afterHeaderOffset));
        WritePngChunk(updated, type, data);
        updated.Write(original.AsSpan(afterHeaderOffset));
        File.WriteAllBytes(path, updated.ToArray());
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^
                    (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }

        return crc;
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly DateTimeOffset SourceCreatedAt =
            new(2041, 2, 3, 4, 5, 6, TimeSpan.Zero);

        internal static readonly DateTimeOffset OutputCreatedAt =
            SourceCreatedAt.AddHours(1);

        private readonly BinaryVerifier _binaryVerifier = new();

        internal Fixture(
            RightsStatus rightsStatus = RightsStatus.Approved,
            bool externalPublicationAllowed = true)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ManualThumbnailServiceTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            string databasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(RootPath, "backups"));

            SourceBytes = new byte[512];
            Encoding.ASCII.GetBytes("manual-thumbnail-source").CopyTo(
                SourceBytes,
                16);
            SourceAsset = CreateAsset(ContentDigest.Compute(SourceBytes), rightsStatus,
                externalPublicationAllowed);
            Project = CreateProject(SourceAsset);
            new ProjectStore(databasePath).CreateProject(
                Project,
                "{\"manualThumbnail\":true}",
                SourceCreatedAt);
            Store = new ArtifactStore(databasePath, WorkspacePath);

            SourceArtifactId = EntityId<ArtifactIdTag>.New();
            string sourceStagingPath = Path.Combine(
                WorkspacePath,
                "projects",
                Project.Id.ToString(),
                "staging",
                "source.ready");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceStagingPath)!);
            File.WriteAllBytes(sourceStagingPath, SourceBytes);
            SourceArtifact = Store.PromoteAsync(
                new ArtifactPromotionRequest(
                    SourceArtifactId,
                    Project.Id,
                    ArtifactKind.Original,
                    sourceStagingPath,
                    string.Concat(
                        "manual-thumbnails/",
                        SourceArtifactId.ToString(),
                        ".mov"),
                    SourceAsset.ContentDigest,
                    SourceAsset.ByteSize,
                    SourceCreatedAt)).GetAwaiter().GetResult();
            SourcePath = Store.GetValidatedRegisteredArtifactPath(SourceArtifact.Id);

            SourcePtsIndex = CreatePtsIndex();
            SourcePtsDigest = ManualThumbnailHash.ComputeCanonicalJsonDigest(
                SourcePtsIndex.CanonicalJson);
            ProxyFrameMap = CreateProxyFrameMap();
            FrameMapDigest = ManualThumbnailHash.ComputeCanonicalJsonDigest(
                ProxyFrameMap.CanonicalJson);

            LayoutSpec = CreateLayoutSpec(EntityId<LayoutSpecIdTag>.New());
            LayoutSpecDigest = ContentDigestFor('e');
            TemplateVersion = CreateTemplateVersion();
            AppliedTemplate = TemplateVersion.ApplyEditorOverrides([]);
            Approval = new ApprovedThumbnailTemplateSnapshot(
                EntityId<ThumbnailTemplateApprovalIdTag>.New(),
                TemplateVersion,
                "brand-approver",
                SourceCreatedAt.AddMinutes(10),
                "Approve the exact thumbnail-template version.",
                "evidence://thumbnail-template/approved");
            RenderProfile = CreateRenderProfile();
            SourceBinding = new ManualThumbnailSourceBinding(
                Project.Id,
                SourceArtifact.Id,
                SourceAsset,
                SourcePtsDigest,
                FrameMapDigest);
            TemplateBinding = new ManualThumbnailTemplateBinding(
                Approval,
                RenderProfile,
                LayoutSpecDigest);
            Selection = CreateSelection(
                proxyFrameSequence: 4,
                sourceFrameSequence: 2,
                new RationalTime(110, 1000));
            OutputArtifactId = EntityId<ArtifactIdTag>.New();
            Request = new ManualThumbnailRequest(
                EntityId<ManualThumbnailRequestIdTag>.New(),
                OutputArtifactId,
                SourceBinding,
                Selection,
                TemplateBinding);
            Context = CreateContext();
            FfmpegPath = Path.Combine(RootPath, "ffmpeg.exe");
            ProcessHandler = DefaultProcessHandler;
            ToolVerificationHandler = DefaultToolVerificationHandler;
            RegisteredVerificationHandler = (artifactId, cancellationToken) =>
                Store.VerifyRegisteredAsync(
                    artifactId,
                    _binaryVerifier,
                    cancellationToken);
            PromotionHandler = Store.PromoteAsync;
            Service = new ManualThumbnailService(
                Store,
                (request, cancellationToken) =>
                {
                    ProcessRequests.Add(request);
                    return ProcessHandler(request, cancellationToken);
                },
                (request, cancellationToken) =>
                {
                    ToolVerificationCount++;
                    return ToolVerificationHandler(request, cancellationToken);
                },
                (artifactId, cancellationToken) =>
                    RegisteredVerificationHandler(artifactId, cancellationToken),
                (request, cancellationToken) =>
                    PromotionHandler(request, cancellationToken),
                WorkspacePath,
                FfmpegPath,
                new FixedTimeProvider(OutputCreatedAt));
        }

        internal string RootPath { get; }

        internal string WorkspacePath { get; }

        internal ArtifactStore Store { get; }

        internal byte[] SourceBytes { get; }

        internal Asset SourceAsset { get; }

        internal Project Project { get; }

        internal EntityId<ArtifactIdTag> SourceArtifactId { get; }

        internal StoredArtifact SourceArtifact { get; }

        internal string SourcePath { get; }

        internal PtsIndex SourcePtsIndex { get; }

        internal ContentDigest SourcePtsDigest { get; }

        internal ProxyFrameMap ProxyFrameMap { get; }

        internal ContentDigest FrameMapDigest { get; }

        internal LayoutSpec LayoutSpec { get; }

        internal ContentDigest LayoutSpecDigest { get; }

        internal BrandTemplateVersion TemplateVersion { get; }

        internal AppliedBrandTemplate AppliedTemplate { get; }

        internal ApprovedThumbnailTemplateSnapshot Approval { get; }

        internal RenderProfile RenderProfile { get; }

        internal ManualThumbnailSourceBinding SourceBinding { get; }

        internal ManualThumbnailTemplateBinding TemplateBinding { get; }

        internal ManualThumbnailFrameSelection Selection { get; }

        internal EntityId<ArtifactIdTag> OutputArtifactId { get; }

        internal ManualThumbnailRequest Request { get; }

        internal ManualThumbnailContext Context { get; }

        internal string FfmpegPath { get; }

        internal ManualThumbnailService Service { get; }

        internal List<ProcessRunRequest> ProcessRequests { get; } = [];

        internal int ToolVerificationCount { get; private set; }

        internal CancellationToken? ObservedPromotionToken { get; set; }

        internal Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
            ProcessHandler
        { get; set; }

        internal Func<
            BinaryVerificationRequest,
            CancellationToken,
            Task<BinaryVerificationResult>> ToolVerificationHandler
        { get; set; }

        internal Func<
            EntityId<ArtifactIdTag>,
            CancellationToken,
            Task<BinaryVerificationResult>> RegisteredVerificationHandler
        { get; set; }

        internal Func<
            ArtifactPromotionRequest,
            CancellationToken,
            Task<StoredArtifact>> PromotionHandler
        { get; set; }

        internal string StagingDirectory => Path.Combine(
            WorkspacePath,
            "projects",
            Project.Id.ToString(),
            "staging",
            "manual-thumbnails",
            string.Concat(Request.Id.ToString(), "-", OutputArtifactId.ToString()));

        internal Task<ManualThumbnailResult> GenerateAsync(
            ManualThumbnailRequest? request = null,
            ManualThumbnailContext? context = null,
            CancellationToken cancellationToken = default) =>
            Service.GenerateAsync(
                request ?? Request,
                context ?? Context,
                TimeSpan.FromSeconds(5),
                cancellationToken);

        internal ManualThumbnailFrameSelection CreateSelection(
            int proxyFrameSequence,
            int sourceFrameSequence,
            RationalTime? pts) =>
            new(
                proxyFrameSequence,
                sourceFrameSequence,
                pts,
                "thumbnail-editor",
                SourceCreatedAt.AddMinutes(20));

        internal ManualThumbnailRequest CreateRequest(
            ManualThumbnailSourceBinding? source = null,
            ManualThumbnailFrameSelection? selection = null,
            ManualThumbnailTemplateBinding? template = null,
            EntityId<ArtifactIdTag>? outputArtifactId = null) =>
            new(
                Request.Id,
                outputArtifactId ?? OutputArtifactId,
                source ?? SourceBinding,
                selection ?? Selection,
                template ?? TemplateBinding);

        internal ManualThumbnailContext CreateContext(
            Project? project = null,
            PtsIndex? sourcePtsIndex = null,
            ProxyFrameMap? proxyFrameMap = null,
            BrandTemplateVersion? templateVersion = null,
            AppliedBrandTemplate? template = null,
            bool withoutApproval = false,
            RenderProfile? renderProfile = null,
            LayoutSpec? layoutSpec = null,
            ContentDigest? layoutSpecDigest = null) =>
            new(
                project ?? Project,
                SourceArtifact,
                sourcePtsIndex ?? SourcePtsIndex,
                proxyFrameMap ?? ProxyFrameMap,
                templateVersion ?? TemplateVersion,
                template ?? AppliedTemplate,
                withoutApproval ? null : Approval,
                renderProfile ?? RenderProfile,
                layoutSpec ?? LayoutSpec,
                layoutSpecDigest ?? LayoutSpecDigest);

        internal Asset CreateAsset(ContentDigest digest) =>
            CreateAsset(
                digest,
                RightsStatus.Approved,
                externalPublicationAllowed: true);

        internal Project CreateProject(Asset asset) =>
            new(
                Project?.Id ?? EntityId<ProjectIdTag>.New(),
                "R-023 manual thumbnail test",
                "Verify explicit frame and approved-template selection.",
                "External viewers",
                PublicationScope.ExternalPublicationApproved,
                deadline: null,
                assets: [asset],
                performers: []);

        internal PtsIndex CreatePtsIndex() =>
            new(
                SourceAsset.Id,
                Project.Id,
                EntityId<JobIdTag>.New(),
                SourceAsset.FileName,
                SourceAsset.ContentDigest,
                SourceAsset.ByteSize,
                streamIndex: 2,
                new RationalTime(1, 1000),
                [
                    new PtsIndexEntry(0, new RationalTime(0, 1000), new RationalTime(40, 1000), keyFrame: true),
                    new PtsIndexEntry(1, new RationalTime(40, 1000), new RationalTime(70, 1000), keyFrame: false),
                    new PtsIndexEntry(2, new RationalTime(110, 1000), new RationalTime(30, 1000), keyFrame: false),
                    new PtsIndexEntry(3, new RationalTime(140, 1000), new RationalTime(60, 1000), keyFrame: true),
                ]);

        internal ProxyFrameMap CreateProxyFrameMap(
            ContentDigest? proxyContentDigest = null) =>
            ProxyFrameMap.Create(
                SourceAsset.Id,
                Project.Id,
                SourceAsset.ContentDigest,
                SourcePtsDigest,
                proxyContentDigest ?? ContentDigestFor('a'),
                ContentDigestFor('b'),
                SourcePtsIndex.Entries
                    .Select(static entry => entry.PresentationTime)
                    .ToArray(),
                SourcePtsIndex.PresentationEnd,
                [
                    new RationalTime(0, 1000),
                    new RationalTime(20, 1000),
                    new RationalTime(40, 1000),
                    new RationalTime(80, 1000),
                    new RationalTime(110, 1000),
                    new RationalTime(130, 1000),
                    new RationalTime(140, 1000),
                ]);

        internal LayoutSpec CreateLayoutSpec(
            EntityId<LayoutSpecIdTag> id) =>
            new(
                id,
                16,
                9,
                [
                    new LayoutRegion(
                        "video",
                        new NormalizedRect(
                            new RationalNumber(0, 1),
                            new RationalNumber(0, 1),
                            new RationalNumber(1, 1),
                            new RationalNumber(1, 1)),
                        LayoutRegionRole.Video),
                ],
                safeZones: []);

        internal BrandTemplateVersion CreateTemplateVersion(
            EntityId<TemplateIdTag>? templateId = null,
            ContentDigest? versionDigest = null) =>
            new(
                EntityId<SeriesIdTag>.New(),
                new TemplateReference(
                    templateId ?? EntityId<TemplateIdTag>.New(),
                    "Approved manual thumbnail",
                    versionDigest ?? ContentDigestFor('c')),
                BrandTemplateKind.Thumbnail,
                parameterDefinitions: []);

        internal RenderProfile CreateRenderProfile(
            EntityId<RenderProfileIdTag>? profileId = null,
            ContentDigest? profileDigest = null) =>
            new(
                profileId ?? EntityId<RenderProfileIdTag>.New(),
                "R-023 thumbnail PNG",
                profileDigest ?? ContentDigestFor('d'),
                width: 16,
                height: 9,
                frameRateNumerator: 30000,
                frameRateDenominator: 1001,
                LayoutSpec.Id,
                TemplateVersion.Reference.Id,
                TemplateVersion.Reference.VersionDigest,
                "png",
                "none",
                subtitleBurnIn: false);

        private Asset CreateAsset(
            ContentDigest digest,
            RightsStatus status,
            bool externalPublicationAllowed)
        {
            string? evidenceReference = status == RightsStatus.Unconfirmed
                ? null
                : string.Concat(
                    "evidence://source-rights/",
                    status.ToString().ToLowerInvariant());
            return new Asset(
                SourceAsset?.Id ?? EntityId<AssetIdTag>.New(),
                "episode-source.mov",
                "video/quicktime",
                digest,
                SourceBytes.LongLength,
                new RightsRecord(
                    status,
                    evidenceReference,
                    "Approved manual thumbnail publication",
                    externalPublicationAllowed));
        }

        private Task<ProcessRunResult> DefaultProcessHandler(
            ProcessRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WritePng(request.ArgumentList[^1], width: 16, height: 9);
            return Task.FromResult(SuccessfulProcessResult());
        }

        private static Task<BinaryVerificationResult>
            DefaultToolVerificationHandler(
                BinaryVerificationRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BinaryVerificationResult.Verified(
                request,
                request.ExpectedByteSize,
                request.ExpectedDigest));
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

        private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => value;
        }
    }
}