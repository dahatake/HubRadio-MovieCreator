using System.Reflection;
using System.Text;
using HubRadio.App.Jobs;
using HubRadio.App.Rendering;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class PreviewRenderServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task RenderAsync_ClipsRebasesValidatesPromotesAndReturnsExactMapping()
    {
        using PreviewFixture fixture = new();
        PreviewRenderRequest request = fixture.CreateRequest(Range(2, 3));

        PreviewRenderResult result = await fixture.Service.RenderAsync(request);

        Assert.IsFalse(result.CacheHit);
        Assert.AreEqual(request.PreviewArtifactId, result.ArtifactId);
        Assert.AreEqual(fixture.Project.Id, result.ProjectId);
        Assert.AreEqual(fixture.OutputContent.LongLength, result.ByteSize);
        Assert.AreEqual(
            ContentDigest.Compute(fixture.OutputContent),
            result.OutputDigest);
        Assert.AreEqual(Range(2, 3), result.SelectedOutputRange);
        Assert.AreEqual(Range(0, 3), result.PreviewOutputRange);
        Assert.AreEqual(TimeSpan.FromMilliseconds(9), result.ProcessDuration);
        Assert.AreEqual(fixture.Profile.Id, result.ProfileId);
        Assert.AreEqual(fixture.Profile.ProfileDigest, result.ProfileDigest);
        Assert.AreEqual(fixture.Layout.Id, result.LayoutSpecId);
        Assert.AreEqual(fixture.LayoutDigest, result.LayoutSpecDigest);
        Assert.AreEqual(fixture.Template.Id, result.TemplateId);
        Assert.AreEqual(
            fixture.Template.VersionDigest,
            result.TemplateVersionDigest);

        Assert.HasCount(1, fixture.CompileRequests);
        FfmpegCompileRequest compiled = fixture.CompileRequests.Single();
        RenderGraphValidationResult validation = RenderGraphValidator.Validate(
            compiled.Graph,
            compiled.ValidationContext);
        Assert.IsTrue(validation.IsValid);
        Assert.AreEqual(Range(0, 3), compiled.ValidationContext
            .CompositionVersion.Elements.Single().OutputRange);
        Assert.AreEqual(Range(12, 3), compiled.ValidationContext
            .CompositionVersion.Elements.Single().SourceRange);
        Assert.AreEqual(Range(0, 3), compiled.ValidationContext
            .TimelineMap.Entries.Single().OutputRange);
        Assert.AreEqual(Range(12, 3), compiled.ValidationContext
            .TimelineMap.Entries.Single().SourceRange);
        RenderNode video = compiled.Graph.Nodes.Single(static node =>
            node.Kind == RenderNodeKind.Video);
        RenderNode output = compiled.Graph.Nodes.Single(static node =>
            node.Kind == RenderNodeKind.Output);
        Assert.AreEqual(Range(0, 3), video.OutputRange);
        Assert.AreEqual(Range(12, 3), video.SourceRange);
        Assert.AreEqual(Range(0, 3), output.OutputRange);
        Assert.AreEqual(video.Id, output.InputNodeIds.Single());

        PreviewRenderMapping mapping = result.Mappings.Single();
        Assert.AreEqual(fixture.Element.Id, mapping.CompositionElementId);
        Assert.AreEqual(Range(2, 3), mapping.SelectedTimelineRange);
        Assert.AreEqual(Range(0, 3), mapping.PreviewOutputRange);
        Assert.AreEqual(fixture.SourceOrigin, mapping.Origin);
        Assert.AreEqual(Range(12, 3), mapping.SourceRange);
        AssertBoundary(
            mapping.ProxyStartBoundary,
            fixture.VideoInput.Id,
            PreviewFrameBoundaryKind.InclusiveStart,
            new RationalTime(12, 1),
            proxySequence: 2,
            new RationalTime(23, 2),
            sourceSequence: 1,
            new RationalTime(11, 1));
        AssertBoundary(
            mapping.ProxyEndBoundary,
            fixture.VideoInput.Id,
            PreviewFrameBoundaryKind.ExclusiveEnd,
            new RationalTime(15, 1),
            proxySequence: 5,
            new RationalTime(29, 2),
            sourceSequence: 4,
            new RationalTime(14, 1));

        StoredArtifact stored = fixture.Store.GetArtifact(result.ArtifactId)
            ?? throw new AssertFailedException("The preview was not registered.");
        Assert.AreEqual(ArtifactKind.Preview, stored.Kind);
        Assert.AreEqual(result.OutputDigest, stored.ContentDigest);
        Assert.AreEqual(result.ByteSize, stored.ByteSize);
        Assert.AreEqual(
            $"projects/{fixture.Project.Id}/previews/{result.CacheKey.Digest.Hex}/{result.ArtifactId}.mp4",
            stored.StorageReference);
        string finalPath = fixture.GetFinalPath(result);
        Assert.IsTrue(File.Exists(finalPath));
        CollectionAssert.AreEqual(
            fixture.OutputContent,
            File.ReadAllBytes(finalPath));
        Assert.IsFalse(Directory.Exists(
            fixture.GetStagingDirectory(request.JobId)));
        Assert.AreEqual(
            JobState.Succeeded,
            fixture.Coordinator.GetSnapshot(request.JobId).State);
        Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
    }

    [TestMethod]
    public async Task RenderAsync_SourceEndBoundaryRetainsExactExclusiveTime()
    {
        using PreviewFixture fixture = new();

        PreviewRenderResult result = await fixture.Service.RenderAsync(
            fixture.CreateRequest(Range(8, 2)));

        PreviewRenderMapping mapping = result.Mappings.Single();
        Assert.AreEqual(Range(18, 2), mapping.SourceRange);
        AssertBoundary(
            mapping.ProxyEndBoundary,
            fixture.VideoInput.Id,
            PreviewFrameBoundaryKind.ExclusiveEnd,
            new RationalTime(20, 1),
            proxySequence: 9,
            new RationalTime(37, 2),
            sourceSequence: 8,
            new RationalTime(18, 1));
        Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
    }

    [TestMethod]
    public async Task RenderAsync_UsesOnlySelectedInputsAndIgnoresExtraBindingsForCacheIdentity()
    {
        using PreviewFixture fixture = new();
        var segmented = fixture.CreateTwoIntervalGraphAndContext();
        PreviewRenderRequest selectedOnly = fixture.CreateRequest(
            Range(1, 2),
            graphAndContext: (segmented.Graph, segmented.Context));

        PreviewRenderResult first = await fixture.Service.RenderAsync(selectedOnly);

        FfmpegCompileRequest compileRequest = fixture.CompileRequests.Single();
        Assert.HasCount(1, compileRequest.Graph.Inputs);
        Assert.HasCount(1, compileRequest.Inputs);
        Assert.AreEqual(fixture.VideoInput.Id, compileRequest.Graph.Inputs[0].Id);
        Assert.AreEqual(fixture.VideoInput.Id, compileRequest.Inputs[0].InputId);

        PreviewRenderRequest withExtraBindings = fixture.CreateRequest(
            Range(1, 2),
            graphAndContext: (segmented.Graph, segmented.Context),
            inputs: segmented.Inputs,
            proxyBindings: segmented.ProxyBindings);
        PreviewRenderResult second = await fixture.Service.RenderAsync(
            withExtraBindings);

        Assert.IsTrue(second.CacheHit);
        Assert.AreEqual(first.CacheKey, second.CacheKey);
        Assert.AreEqual(first.ArtifactId, second.ArtifactId);
        Assert.AreEqual(1, fixture.ProcessInvocationCount);
        Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
    }

    [TestMethod]
    public async Task RenderAsync_RejectsEmptyNegativeOutOfRangeAndGappedSelectionsBeforeJobStarts()
    {
        using PreviewFixture fixture = new();
        TimeRange[] invalidSelections =
        [
            Range(2, 0),
            Range(-1, 1),
            Range(9, 2),
        ];

        foreach (TimeRange selection in invalidSelections)
        {
            PreviewRenderRequest request = fixture.CreateRequest(selection);
            PreviewRenderException exception =
                await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                    fixture.Service.RenderAsync(request));

            AssertSafeFailure(
                exception,
                PreviewRenderFailureReason.InvalidSelection,
                reconciliationRequired: false,
                fixture.ProxyPath,
                fixture.FfmpegPath);
            Assert.AreEqual(
                JobState.Queued,
                fixture.Coordinator.GetSnapshot(request.JobId).State);
        }

        PreviewRenderRequest gapped = fixture.CreateRequest(
            Range(3, 4),
            graphAndContext: fixture.CreateGappedGraphAndContext());
        PreviewRenderException gapFailure =
            await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                fixture.Service.RenderAsync(gapped));
        Assert.AreEqual(
            PreviewRenderFailureReason.InvalidSelection,
            gapFailure.Reason);
        Assert.IsEmpty(fixture.CompileRequests);
        Assert.AreEqual(0, fixture.ProcessInvocationCount);
    }

    [TestMethod]
    public async Task RenderAsync_RequiresSelectedProxyMapAndRejectsPartialTransition()
    {
        using PreviewFixture fixture = new();
        PreviewRenderRequest missingMap = fixture.CreateRequest(
            Range(2, 3),
            proxyBindings: []);

        PreviewRenderException missingMapFailure =
            await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                fixture.Service.RenderAsync(missingMap));

        AssertSafeFailure(
            missingMapFailure,
            PreviewRenderFailureReason.ProxyMappingMismatch,
            reconciliationRequired: false,
            fixture.ProxyPath);
        Assert.AreEqual(
            JobState.Queued,
            fixture.Coordinator.GetSnapshot(missingMap.JobId).State);

        var transition = fixture.CreateTransitionGraphAndContext();
        PreviewRenderRequest partialTransition = fixture.CreateRequest(
            Range(5, 1),
            graphAndContext: (transition.Graph, transition.Context),
            inputs: transition.Inputs,
            proxyBindings: transition.ProxyBindings);

        PreviewRenderException transitionFailure =
            await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                fixture.Service.RenderAsync(partialTransition));

        AssertSafeFailure(
            transitionFailure,
            PreviewRenderFailureReason.UnsupportedSelectionBoundary,
            reconciliationRequired: false,
            fixture.ProxyPath);
        Assert.AreEqual(
            JobState.Queued,
            fixture.Coordinator.GetSnapshot(partialTransition.JobId).State);
        Assert.IsEmpty(fixture.CompileRequests);
        Assert.AreEqual(0, fixture.ProcessInvocationCount);
        Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
    }

    [TestMethod]
    public async Task RenderAsync_ReusesOnlyVerifiedExactCacheAndRecomputesCorruptCandidate()
    {
        using PreviewFixture fixture = new();
        PreviewRenderRequest firstRequest = fixture.CreateRequest(Range(2, 3));
        PreviewRenderResult first = await fixture.Service.RenderAsync(firstRequest);
        var candidate = new ArtifactCacheCandidate(
            first.ArtifactId,
            PreviewRenderService.Operation,
            first.CacheKey,
            first.OutputDigest,
            first.CreatedAt);

        PreviewRenderRequest hitRequest = fixture.CreateRequest(
            Range(2, 3),
            cacheCandidates: [candidate]);
        PreviewRenderResult hit = await fixture.Service.RenderAsync(hitRequest);

        Assert.IsTrue(hit.CacheHit);
        Assert.AreEqual(first.ArtifactId, hit.ArtifactId);
        Assert.AreEqual(first.OutputDigest, hit.OutputDigest);
        Assert.AreEqual(first.CacheKey, hit.CacheKey);
        Assert.IsNull(hit.ProcessDuration);
        Assert.AreEqual(1, fixture.ProcessInvocationCount);
        Assert.IsNull(fixture.Store.GetArtifact(hitRequest.PreviewArtifactId));
        Assert.AreEqual(
            JobState.Succeeded,
            fixture.Coordinator.GetSnapshot(hitRequest.JobId).State);

        File.WriteAllBytes(
            fixture.GetFinalPath(first),
            Encoding.UTF8.GetBytes("corrupt cached preview"));
        PreviewRenderRequest staleRequest = fixture.CreateRequest(
            Range(2, 3),
            cacheCandidates: [candidate]);
        PreviewRenderResult recomputed = await fixture.Service.RenderAsync(
            staleRequest);

        Assert.IsFalse(recomputed.CacheHit);
        Assert.AreEqual(staleRequest.PreviewArtifactId, recomputed.ArtifactId);
        Assert.AreNotEqual(first.ArtifactId, recomputed.ArtifactId);
        Assert.AreEqual(first.CacheKey, recomputed.CacheKey);
        Assert.AreEqual(2, fixture.ProcessInvocationCount);
        Assert.IsNotNull(fixture.Store.GetArtifact(first.ArtifactId));
        Assert.IsNotNull(fixture.Store.GetArtifact(recomputed.ArtifactId));
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("corrupt cached preview"),
            File.ReadAllBytes(fixture.GetFinalPath(first)));
        CollectionAssert.AreEqual(
            fixture.OutputContent,
            File.ReadAllBytes(fixture.GetFinalPath(recomputed)));
        Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
    }

    [TestMethod]
    public async Task RenderAsync_SameKeyConcurrentJobsProduceOneArtifact()
    {
        using PreviewFixture fixture = new();
        var processEntered = NewSignal();
        var releaseProcess = NewSignal();
        fixture.ProcessHandler = async (request, cancellationToken) =>
        {
            _ = Interlocked.Increment(ref fixture.ProcessInvocationCount);
            processEntered.TrySetResult(true);
            await releaseProcess.Task.WaitAsync(cancellationToken);
            File.WriteAllBytes(request.ArgumentList.Single(), fixture.OutputContent);
            return SuccessfulProcessResult();
        };
        PreviewRenderRequest firstRequest = fixture.CreateRequest(Range(2, 3));
        PreviewRenderRequest duplicateRequest = fixture.CreateRequest(Range(2, 3));

        Task<PreviewRenderResult> firstRun = fixture.Service.RenderAsync(firstRequest);
        await processEntered.Task.WaitAsync(TestTimeout);
        Task<PreviewRenderResult> duplicateRun = fixture.Service.RenderAsync(
            duplicateRequest);
        releaseProcess.TrySetResult(true);
        PreviewRenderResult[] results = await Task.WhenAll(firstRun, duplicateRun)
            .WaitAsync(TestTimeout);

        Assert.AreEqual(1, fixture.ProcessInvocationCount);
        Assert.AreEqual(results[0].CacheKey, results[1].CacheKey);
        Assert.AreEqual(results[0].ArtifactId, results[1].ArtifactId);
        Assert.IsFalse(results[0].CacheHit);
        Assert.IsTrue(results[1].CacheHit);
        Assert.IsNull(fixture.Store.GetArtifact(duplicateRequest.PreviewArtifactId));
        Assert.AreEqual(
            JobState.Succeeded,
            fixture.Coordinator.GetSnapshot(firstRequest.JobId).State);
        Assert.AreEqual(
            JobState.Succeeded,
            fixture.Coordinator.GetSnapshot(duplicateRequest.JobId).State);
        Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
    }

    [TestMethod]
    public async Task RenderAsync_RejectsConcurrentRunOfTheSameJob()
    {
        using PreviewFixture fixture = new();
        var processEntered = NewSignal();
        var releaseProcess = NewSignal();
        fixture.ProcessHandler = async (request, cancellationToken) =>
        {
            _ = Interlocked.Increment(ref fixture.ProcessInvocationCount);
            processEntered.TrySetResult(true);
            await releaseProcess.Task.WaitAsync(cancellationToken);
            File.WriteAllBytes(request.ArgumentList.Single(), fixture.OutputContent);
            return SuccessfulProcessResult();
        };
        PreviewRenderRequest request = fixture.CreateRequest(Range(2, 3));
        Task<PreviewRenderResult> firstRun = fixture.Service.RenderAsync(request);
        await processEntered.Task.WaitAsync(TestTimeout);

        try
        {
            PreviewRenderException duplicate =
                await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                    fixture.Service.RenderAsync(request));
            AssertSafeFailure(
                duplicate,
                PreviewRenderFailureReason.DuplicateJob,
                reconciliationRequired: false,
                fixture.ProxyPath);
        }
        finally
        {
            releaseProcess.TrySetResult(true);
        }

        PreviewRenderResult completed = await firstRun.WaitAsync(TestTimeout);
        Assert.AreEqual(request.PreviewArtifactId, completed.ArtifactId);
        Assert.AreEqual(1, fixture.ProcessInvocationCount);
        Assert.AreEqual(
            JobState.Succeeded,
            fixture.Coordinator.GetSnapshot(request.JobId).State);
        Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
    }

    [TestMethod]
    public async Task RenderAsync_TimeoutAndPartialFailureAreTypedPrivateAndCleanOwnedStaging()
    {
        using PreviewFixture timeoutFixture = new();
        timeoutFixture.ProcessHandler = (request, cancellationToken) =>
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref timeoutFixture.ProcessInvocationCount);
            throw new TimeoutException("private timeout path C:\\customer\\clip.mov");
        };
        PreviewRenderRequest timeoutRequest = timeoutFixture.CreateRequest(Range(1, 2));

        PreviewRenderException timeout =
            await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                timeoutFixture.Service.RenderAsync(timeoutRequest));

        AssertSafeFailure(
            timeout,
            PreviewRenderFailureReason.ProcessTimedOut,
            reconciliationRequired: false,
            "C:\\customer\\clip.mov",
            timeoutFixture.ProxyPath);
        JobSnapshot timeoutJob = timeoutFixture.Coordinator.GetSnapshot(
            timeoutRequest.JobId);
        Assert.AreEqual(JobState.Failed, timeoutJob.State);
        Assert.AreEqual("job.execution_timed_out", timeoutJob.ErrorCode);
        Assert.IsFalse(Directory.Exists(
            timeoutFixture.GetStagingDirectory(timeoutRequest.JobId)));
        Assert.IsNull(timeoutFixture.Store.GetArtifact(
            timeoutRequest.PreviewArtifactId));

        using PreviewFixture partialFixture = new();
        partialFixture.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref partialFixture.ProcessInvocationCount);
            File.WriteAllBytes(
                request.ArgumentList.Single(),
                Encoding.UTF8.GetBytes("partial private bytes"));
            return Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                "C:\\customer\\secret.mov",
                Truncated: false,
                TimeSpan.Zero));
        };
        PreviewRenderRequest partialRequest = partialFixture.CreateRequest(Range(1, 2));

        PreviewRenderException partial =
            await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                partialFixture.Service.RenderAsync(partialRequest));

        AssertSafeFailure(
            partial,
            PreviewRenderFailureReason.RenderFailed,
            reconciliationRequired: false,
            "C:\\customer\\secret.mov",
            partialFixture.ProxyPath);
        Assert.IsFalse(Directory.Exists(
            partialFixture.GetStagingDirectory(partialRequest.JobId)));
        Assert.IsNull(partialFixture.Store.GetArtifact(
            partialRequest.PreviewArtifactId));
    }

    [TestMethod]
    public async Task RenderAsync_CallerCancellationCancelsJobAndCleansPartialOutput()
    {
        using PreviewFixture fixture = new();
        var processEntered = NewSignal();
        fixture.ProcessHandler = async (request, cancellationToken) =>
        {
            _ = Interlocked.Increment(ref fixture.ProcessInvocationCount);
            File.WriteAllBytes(
                request.ArgumentList.Single(),
                Encoding.UTF8.GetBytes("partial output before cancellation"));
            processEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessfulProcessResult();
        };
        using var cancellation = new CancellationTokenSource();
        PreviewRenderRequest request = fixture.CreateRequest(Range(1, 2));

        Task<PreviewRenderResult> run = fixture.Service.RenderAsync(
            request,
            cancellation.Token);
        await processEntered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => run);

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(
            JobState.Cancelled,
            fixture.Coordinator.GetSnapshot(request.JobId).State);
        Assert.IsFalse(Directory.Exists(
            fixture.GetStagingDirectory(request.JobId)));
        Assert.IsNull(fixture.Store.GetArtifact(request.PreviewArtifactId));
    }

    [TestMethod]
    public async Task RenderAsync_CoordinatorCancellationReturnsTypedFailureAndCleansOutput()
    {
        using PreviewFixture fixture = new();
        var processEntered = NewSignal();
        fixture.ProcessHandler = async (request, cancellationToken) =>
        {
            _ = Interlocked.Increment(ref fixture.ProcessInvocationCount);
            File.WriteAllBytes(
                request.ArgumentList.Single(),
                Encoding.UTF8.GetBytes("partial coordinator cancellation"));
            processEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessfulProcessResult();
        };
        PreviewRenderRequest request = fixture.CreateRequest(Range(1, 2));
        Task<PreviewRenderResult> run = fixture.Service.RenderAsync(request);
        await processEntered.Task.WaitAsync(TestTimeout);

        JobCancellationResult cancellation = fixture.Coordinator
            .RequestCancellation(request.JobId);
        PreviewRenderException exception =
            await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                run.WaitAsync(TestTimeout));

        Assert.AreEqual(JobCancellationOutcome.Requested, cancellation.Outcome);
        AssertSafeFailure(
            exception,
            PreviewRenderFailureReason.JobCancelled,
            reconciliationRequired: false,
            fixture.ProxyPath);
        Assert.AreEqual(
            JobState.Cancelled,
            fixture.Coordinator.GetSnapshot(request.JobId).State);
        Assert.IsFalse(Directory.Exists(
            fixture.GetStagingDirectory(request.JobId)));
        Assert.IsNull(fixture.Store.GetArtifact(request.PreviewArtifactId));
        Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
    }

    [TestMethod]
    public async Task RenderAsync_SuccessfulProcessRequiresNonemptyStableOutput()
    {
        foreach (bool writeEmptyFile in new[] { false, true })
        {
            using PreviewFixture fixture = new();
            fixture.ProcessHandler = (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = Interlocked.Increment(ref fixture.ProcessInvocationCount);
                if (writeEmptyFile)
                {
                    File.WriteAllBytes(request.ArgumentList.Single(), []);
                }

                return Task.FromResult(SuccessfulProcessResult());
            };
            PreviewRenderRequest request = fixture.CreateRequest(Range(1, 2));

            PreviewRenderException exception =
                await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                    fixture.Service.RenderAsync(request));

            AssertSafeFailure(
                exception,
                PreviewRenderFailureReason.OutputInvalid,
                reconciliationRequired: false,
                fixture.ProxyPath);
            Assert.AreEqual(
                JobState.Failed,
                fixture.Coordinator.GetSnapshot(request.JobId).State);
            Assert.IsFalse(Directory.Exists(
                fixture.GetStagingDirectory(request.JobId)));
            Assert.IsNull(fixture.Store.GetArtifact(request.PreviewArtifactId));
            Assert.AreEqual(0, fixture.Service.ActiveCacheGateCount);
        }
    }

    [TestMethod]
    public async Task RenderAsync_PromotionUncertaintyAndPostValidationRequireReconciliation()
    {
        const string privateFailure = "C:\\private\\promotion-output.mp4";
        using (PreviewFixture promotionFixture = new())
        {
            PreviewRenderService service = promotionFixture.CreateService(
                promoteAsync: (request, cancellationToken) =>
                {
                    _ = request;
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromException<StoredArtifact>(
                        new IOException(privateFailure));
                });
            PreviewRenderRequest request = promotionFixture.CreateRequest(
                Range(1, 2));

            PreviewRenderException exception =
                await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                    service.RenderAsync(request));

            AssertSafeFailure(
                exception,
                PreviewRenderFailureReason.PromotionFailed,
                reconciliationRequired: true,
                privateFailure,
                promotionFixture.ProxyPath);
            Assert.IsNull(promotionFixture.Store.GetArtifact(
                request.PreviewArtifactId));
            Assert.IsFalse(Directory.Exists(
                promotionFixture.GetStagingDirectory(request.JobId)));
            Assert.AreEqual(0, service.ActiveCacheGateCount);
        }

        using (PreviewFixture validationFixture = new())
        {
            PreviewRenderService service = validationFixture.CreateService(
                validateRegisteredAsync: (artifactId, cancellationToken) =>
                {
                    _ = artifactId;
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromException<StoredArtifact>(
                        new IOException(privateFailure));
                });
            PreviewRenderRequest request = validationFixture.CreateRequest(
                Range(1, 2));

            PreviewRenderException exception =
                await Assert.ThrowsExactlyAsync<PreviewRenderException>(() =>
                    service.RenderAsync(request));

            AssertSafeFailure(
                exception,
                PreviewRenderFailureReason.RegisteredPreviewInvalid,
                reconciliationRequired: true,
                privateFailure,
                validationFixture.ProxyPath);
            StoredArtifact verified = await validationFixture.Store
                .ValidateRegisteredAsync(request.PreviewArtifactId);
            Assert.AreEqual(request.PreviewArtifactId, verified.Id);
            Assert.IsFalse(Directory.Exists(
                validationFixture.GetStagingDirectory(request.JobId)));
            Assert.AreEqual(0, service.ActiveCacheGateCount);
        }
    }

    [TestMethod]
    public async Task RenderAsync_EquivalentRequestsHaveStableKeyAndSelectionChangesIt()
    {
        using PreviewFixture fixture = new();
        PreviewRenderResult first = await fixture.Service.RenderAsync(
            fixture.CreateRequest(Range(2, 3)));
        PreviewRenderResult equivalent = await fixture.Service.RenderAsync(
            fixture.CreateRequest(Range(2, 3)));
        PreviewRenderResult changed = await fixture.Service.RenderAsync(
            fixture.CreateRequest(Range(3, 3)));

        Assert.AreEqual(first.CacheKey, equivalent.CacheKey);
        Assert.AreEqual(first.ConfigurationDigest, equivalent.ConfigurationDigest);
        Assert.AreEqual(first.RenderGraphDigest, equivalent.RenderGraphDigest);
        Assert.IsTrue(equivalent.CacheHit);
        Assert.AreNotEqual(first.CacheKey, changed.CacheKey);
        Assert.AreNotEqual(first.ConfigurationDigest, changed.ConfigurationDigest);
        Assert.IsFalse(changed.CacheHit);
        Assert.AreEqual(2, fixture.ProcessInvocationCount);
    }

    [TestMethod]
    public void ProcessValidationAndPublicResultsExposeNoPathsOrCapturedOutput()
    {
        const string privatePath = "C:\\private\\recording.mov";
        ProcessRunResult[] failures =
        [
            new(1, string.Empty, privatePath, false, TimeSpan.Zero),
            new(0, privatePath, string.Empty, false, TimeSpan.Zero),
            new(0, string.Empty, string.Empty, true, TimeSpan.Zero),
            new(0, null!, string.Empty, false, TimeSpan.Zero),
            new(0, string.Empty, null!, false, TimeSpan.Zero),
            new(0, string.Empty, string.Empty, false, TimeSpan.FromTicks(-1)),
        ];
        foreach (ProcessRunResult result in failures)
        {
            PreviewRenderException exception =
                Assert.ThrowsExactly<PreviewRenderException>(() =>
                    PreviewRenderService.ValidateProcessResult(result));
            AssertSafeFailure(
                exception,
                PreviewRenderFailureReason.RenderFailed,
                reconciliationRequired: false,
                privatePath);
        }

        PreviewRenderService.ValidateProcessResult(SuccessfulProcessResult());
        foreach (Type type in new[]
        {
            typeof(PreviewProxyBinding),
            typeof(PreviewRenderRequest),
            typeof(PreviewRenderException),
            typeof(PreviewFrameBoundaryEvidence),
            typeof(PreviewRenderMapping),
            typeof(PreviewRenderResult),
            typeof(PreviewRenderService),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly).All(static property =>
                    property.SetMethod is null), type.Name);
        }

        Assert.AreEqual(0, typeof(PreviewRenderResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(PreviewRenderMapping).GetConstructors().Length);
        Assert.AreEqual(
            0,
            typeof(PreviewFrameBoundaryEvidence).GetConstructors().Length);
        Assert.IsFalse(typeof(PreviewRenderResult).GetProperties(
            BindingFlags.Instance | BindingFlags.Public).Any(static property =>
                property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Standard", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Filter", StringComparison.OrdinalIgnoreCase) ||
                property.PropertyType == typeof(ProcessRunResult) ||
                property.PropertyType == typeof(FfmpegCompilation) ||
                property.PropertyType == typeof(StoredArtifact)));
        CollectionAssert.AreEqual(
            new[] { "Reason", "ReconciliationRequired" },
            typeof(PreviewRenderException).GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());

        foreach (PreviewRenderFailureReason reason in
            Enum.GetValues<PreviewRenderFailureReason>())
        {
            var exception = new PreviewRenderException(
                reason,
                reconciliationRequired: false);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.ToString().Contains(
                privatePath,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertBoundary(
        PreviewFrameBoundaryEvidence? boundary,
        EntityId<RenderInputIdTag> inputId,
        PreviewFrameBoundaryKind kind,
        RationalTime sourceBoundaryTime,
        int proxySequence,
        RationalTime proxyPresentationTime,
        int sourceSequence,
        RationalTime sourcePresentationTime)
    {
        Assert.IsNotNull(boundary);
        Assert.AreEqual(inputId, boundary.InputId);
        Assert.AreEqual(kind, boundary.Kind);
        Assert.AreEqual(sourceBoundaryTime, boundary.SourceBoundaryTime);
        Assert.AreEqual(proxySequence, boundary.ProxyFrameSequence);
        Assert.AreEqual(proxyPresentationTime, boundary.ProxyPresentationTime);
        Assert.AreEqual(sourceSequence, boundary.SourceFrameSequence);
        Assert.AreEqual(sourcePresentationTime, boundary.SourcePresentationTime);
    }

    private static void AssertSafeFailure(
        PreviewRenderException exception,
        PreviewRenderFailureReason reason,
        bool reconciliationRequired,
        params string[] forbiddenValues)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(
            reconciliationRequired,
            exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        foreach (string forbidden in forbiddenValues)
        {
            Assert.IsFalse(exception.ToString().Contains(
                forbidden,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static ProcessRunResult SuccessfulProcessResult() =>
        new(
            0,
            string.Empty,
            string.Empty,
            Truncated: false,
            TimeSpan.FromMilliseconds(9));

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class PreviewFixture : IDisposable
    {
        private static readonly DateTimeOffset CreatedAt =
            new(2044, 2, 3, 4, 5, 6, TimeSpan.Zero);

        private readonly string _databasePath;
        private readonly JobStore _jobStore;
        private readonly FixedTimeProvider _timeProvider = new(CreatedAt);

        internal PreviewFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.PreviewRenderServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            _databasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                _databasePath,
                Path.Combine(RootPath, "backups"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);

            SourceDigest = Digest('a');
            ProxyDigest = Digest('b');
            LayoutDigest = Digest('c');
            Project = CreateProject(SourceDigest);
            new ProjectStore(_databasePath).CreateProject(
                Project,
                "{\"previewRender\":true}",
                CreatedAt.AddMinutes(-1));
            Store = new ArtifactStore(_databasePath, WorkspacePath);
            _jobStore = new JobStore(_databasePath);
            var runner = new LocalJobRunner(_jobStore, _timeProvider);
            Coordinator = new JobCoordinator(
                _jobStore,
                runner,
                _timeProvider);

            SourceSpan = new SourceSpan(
                Id<SourceSpanIdTag>(0x91000002, 1),
                Project.Assets.Single().Id,
                "video:0",
                Range(10, 10),
                SourceDigest,
                transcriptRevisionId: null,
                transcriptTokenIds: []);
            SourceOrigin = OriginRef.FromSourceSpan(SourceSpan.Id);
            Element = new CompositionElement(
                Id<CompositionElementIdTag>(0x91000003, 1),
                Range(0, 10),
                SourceOrigin,
                adoptedSuggestion: null,
                Range(10, 10));
            Composition = CreateComposition([Element]);
            Timeline = new TimelineMap(
                Id<TimelineMapIdTag>(0x91000005, 1),
                Composition.Id,
                [new TimelineMapEntry(
                    Element.OutputRange,
                    Element.Origin,
                    Element.SourceRange)]);
            TimelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(Timeline);
            Layout = new LayoutSpec(
                Id<LayoutSpecIdTag>(0x91000006, 1),
                16,
                9,
                [new LayoutRegion(
                    "video",
                    new NormalizedRect(
                        new RationalNumber(0, 1),
                        new RationalNumber(0, 1),
                        new RationalNumber(1, 1),
                        new RationalNumber(1, 1)),
                    LayoutRegionRole.Video)],
                []);
            Template = new TemplateReference(
                Id<TemplateIdTag>(0x91000007, 1),
                "preview-template",
                Digest('d'));
            Profile = new RenderProfile(
                Id<RenderProfileIdTag>(0x91000008, 1),
                "preview-profile",
                Digest('e'),
                640,
                360,
                30000,
                1001,
                Layout.Id,
                Template.Id,
                Template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: false);
            VideoInput = new RenderGraphInput(
                Id<RenderInputIdTag>(0x91000009, 1),
                RenderInputKind.Video,
                ProxyDigest,
                512,
                SourceOrigin);
            RenderNode video = RenderNode.Video(
                Id<RenderNodeIdTag>(0x9100000a, 1),
                VideoInput.Id,
                Element.OutputRange,
                SourceOrigin,
                Element.SourceRange,
                "video");
            RenderNode output = RenderNode.Output(
                Id<RenderNodeIdTag>(0x9100000a, 2),
                [video.Id],
                Range(0, 10));
            Graph = CreateGraph(
                Composition,
                Timeline,
                TimelineDigest,
                [VideoInput],
                [video, output],
                output.Id);
            Context = CreateContext(
                Composition,
                Timeline,
                TimelineDigest,
                [VideoInput]);
            ProxyMap = ProxyFrameMap.Create(
                Project.Assets.Single().Id,
                Project.Id,
                SourceDigest,
                Digest('f'),
                ProxyDigest,
                Digest('1'),
                Enumerable.Range(10, 10)
                    .Select(static value => new RationalTime(value, 1))
                    .ToArray(),
                new RationalTime(20, 1),
                Enumerable.Range(0, 10)
                    .Select(static index => index == 0
                        ? new RationalTime(10, 1)
                        : new RationalTime(19 + (2 * index), 2))
                    .ToArray());

            ProxyPath = Path.Combine(RootPath, "private proxy.mp4");
            FfmpegPath = Path.Combine(RootPath, "private ffmpeg.exe");
            InputBinding = new FfmpegInputBinding(
                VideoInput.Id,
                new BinaryVerificationRequest(
                    "input.preview-proxy",
                    ProxyPath,
                    ProxyDigest,
                    VideoInput.ByteSize));
            Tool = new FfmpegToolBinding(new BinaryVerificationRequest(
                "ffmpeg",
                FfmpegPath,
                new ContentDigest(FfmpegCompiler.FixedFfmpegSha256),
                FfmpegCompiler.FixedFfmpegByteSize));
            OutputContent = new byte[256];
            Encoding.ASCII.GetBytes("ftyp-preview-output")
                .CopyTo(OutputContent, 4);
            ProcessHandler = DefaultProcessAsync;
            Service = new PreviewRenderService(
                Coordinator,
                CompileAsync,
                (request, cancellationToken) =>
                    ProcessHandler(request, cancellationToken),
                Store.PromoteAsync,
                Store.ValidateRegisteredAsync,
                Store.GetArtifact,
                WorkspacePath,
                _timeProvider);
        }

        internal string RootPath { get; }

        internal string WorkspacePath { get; }

        internal Project Project { get; }

        internal ArtifactStore Store { get; }

        internal JobCoordinator Coordinator { get; }

        internal SourceSpan SourceSpan { get; }

        internal OriginRef SourceOrigin { get; }

        internal CompositionElement Element { get; }

        internal CompositionVersion Composition { get; }

        internal TimelineMap Timeline { get; }

        internal ContentDigest TimelineDigest { get; }

        internal LayoutSpec Layout { get; }

        internal ContentDigest LayoutDigest { get; }

        internal TemplateReference Template { get; }

        internal RenderProfile Profile { get; }

        internal RenderGraphInput VideoInput { get; }

        internal RenderGraph Graph { get; }

        internal RenderGraphValidationContext Context { get; }

        internal ProxyFrameMap ProxyMap { get; }

        internal FfmpegInputBinding InputBinding { get; }

        internal FfmpegToolBinding Tool { get; }

        internal ContentDigest SourceDigest { get; }

        internal ContentDigest ProxyDigest { get; }

        internal string ProxyPath { get; }

        internal string FfmpegPath { get; }

        internal byte[] OutputContent { get; }

        internal PreviewRenderService Service { get; }

        internal List<FfmpegCompileRequest> CompileRequests { get; } = [];

        internal int ProcessInvocationCount;

        internal Func<
            ProcessRunRequest,
            CancellationToken,
            Task<ProcessRunResult>> ProcessHandler
        { get; set; }

        internal PreviewRenderRequest CreateRequest(
            TimeRange selection,
            IReadOnlyList<ArtifactCacheCandidate>? cacheCandidates = null,
            (RenderGraph Graph, RenderGraphValidationContext Context)? graphAndContext = null,
            IReadOnlyList<FfmpegInputBinding>? inputs = null,
            IReadOnlyList<PreviewProxyBinding>? proxyBindings = null)
        {
            EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
            DateTimeOffset queuedAt = CreatedAt.AddSeconds(-1);
            _jobStore.CreateJob(new Job(
                jobId,
                Project.Id,
                "render.preview.interval",
                JobState.Queued,
                queuedAt,
                queuedAt,
                currentAttemptNumber: 0));
            (RenderGraph graph, RenderGraphValidationContext context) =
                graphAndContext ?? (Graph, Context);
            return new PreviewRenderRequest(
                jobId,
                EntityId<ArtifactIdTag>.New(),
                graph,
                context,
                selection,
                Tool,
                inputs ?? [InputBinding],
                proxyBindings ?? [new PreviewProxyBinding(VideoInput.Id, ProxyMap)],
                cacheCandidates ?? [],
                TimeSpan.FromSeconds(5));
        }

        internal (
            RenderGraph Graph,
            RenderGraphValidationContext Context,
            IReadOnlyList<FfmpegInputBinding> Inputs,
            IReadOnlyList<PreviewProxyBinding> ProxyBindings)
            CreateTransitionGraphAndContext()
        {
            var secondInput = new RenderGraphInput(
                Id<RenderInputIdTag>(0x91000009, 2),
                RenderInputKind.Video,
                ProxyDigest,
                VideoInput.ByteSize,
                SourceOrigin);
            RenderNode firstVideo = RenderNode.Video(
                Id<RenderNodeIdTag>(0x9100000a, 7),
                VideoInput.Id,
                Element.OutputRange,
                SourceOrigin,
                Element.SourceRange,
                "video");
            RenderNode secondVideo = RenderNode.Video(
                Id<RenderNodeIdTag>(0x9100000a, 8),
                secondInput.Id,
                Element.OutputRange,
                SourceOrigin,
                Element.SourceRange,
                "video");
            RenderNode transition = RenderNode.TransitionEffect(
                Id<RenderNodeIdTag>(0x9100000a, 9),
                [firstVideo.Id, secondVideo.Id],
                Range(4, 2),
                SourceOrigin,
                Transition.Crossfade(new RationalTime(2, 1)));
            RenderNode output = RenderNode.Output(
                Id<RenderNodeIdTag>(0x9100000a, 10),
                [transition.Id],
                Range(0, 10));
            RenderGraph graph = CreateGraph(
                Composition,
                Timeline,
                TimelineDigest,
                [VideoInput, secondInput],
                [firstVideo, secondVideo, transition, output],
                output.Id);
            RenderGraphValidationContext context = CreateContext(
                Composition,
                Timeline,
                TimelineDigest,
                [VideoInput, secondInput]);
            Assert.IsTrue(RenderGraphValidator.Validate(graph, context).IsValid);
            return (
                graph,
                context,
                [InputBinding, CreateInputBinding(secondInput)],
                [
                    new PreviewProxyBinding(VideoInput.Id, ProxyMap),
                    new PreviewProxyBinding(secondInput.Id, ProxyMap),
                ]);
        }

        internal (
            RenderGraph Graph,
            RenderGraphValidationContext Context,
            IReadOnlyList<FfmpegInputBinding> Inputs,
            IReadOnlyList<PreviewProxyBinding> ProxyBindings)
            CreateTwoIntervalGraphAndContext()
        {
            CompositionElement first = new(
                Id<CompositionElementIdTag>(0x91000003, 4),
                Range(0, 5),
                SourceOrigin,
                adoptedSuggestion: null,
                Range(10, 5));
            CompositionElement second = new(
                Id<CompositionElementIdTag>(0x91000003, 5),
                Range(5, 5),
                SourceOrigin,
                adoptedSuggestion: null,
                Range(15, 5));
            CompositionVersion composition = CreateComposition([first, second]);
            TimelineMap timeline = new(
                Id<TimelineMapIdTag>(0x91000005, 3),
                composition.Id,
                [
                    new TimelineMapEntry(
                        first.OutputRange,
                        first.Origin,
                        first.SourceRange),
                    new TimelineMapEntry(
                        second.OutputRange,
                        second.Origin,
                        second.SourceRange),
                ]);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
            var secondInput = new RenderGraphInput(
                Id<RenderInputIdTag>(0x91000009, 3),
                RenderInputKind.Video,
                ProxyDigest,
                VideoInput.ByteSize,
                SourceOrigin);
            RenderNode firstVideo = RenderNode.Video(
                Id<RenderNodeIdTag>(0x9100000a, 11),
                VideoInput.Id,
                first.OutputRange,
                first.Origin,
                first.SourceRange,
                "video");
            RenderNode secondVideo = RenderNode.Video(
                Id<RenderNodeIdTag>(0x9100000a, 12),
                secondInput.Id,
                second.OutputRange,
                second.Origin,
                second.SourceRange,
                "video");
            RenderNode cut = RenderNode.TransitionEffect(
                Id<RenderNodeIdTag>(0x9100000a, 13),
                [firstVideo.Id, secondVideo.Id],
                new TimeRange(new RationalTime(5, 1), default),
                SourceOrigin,
                Transition.Cut());
            RenderNode output = RenderNode.Output(
                Id<RenderNodeIdTag>(0x9100000a, 14),
                [cut.Id],
                Range(0, 10));
            RenderGraph graph = CreateGraph(
                composition,
                timeline,
                timelineDigest,
                [VideoInput, secondInput],
                [firstVideo, secondVideo, cut, output],
                output.Id);
            RenderGraphValidationContext context = CreateContext(
                composition,
                timeline,
                timelineDigest,
                [VideoInput, secondInput]);
            Assert.IsTrue(RenderGraphValidator.Validate(graph, context).IsValid);
            return (
                graph,
                context,
                [InputBinding, CreateInputBinding(secondInput)],
                [
                    new PreviewProxyBinding(VideoInput.Id, ProxyMap),
                    new PreviewProxyBinding(secondInput.Id, ProxyMap),
                ]);
        }

        internal (RenderGraph Graph, RenderGraphValidationContext Context)
            CreateGappedGraphAndContext()
        {
            CompositionElement first = new(
                Id<CompositionElementIdTag>(0x91000003, 2),
                Range(0, 4),
                SourceOrigin,
                adoptedSuggestion: null,
                Range(10, 4));
            CompositionElement second = new(
                Id<CompositionElementIdTag>(0x91000003, 3),
                Range(6, 4),
                SourceOrigin,
                adoptedSuggestion: null,
                Range(16, 4));
            CompositionVersion composition = CreateComposition([first, second]);
            TimelineMap timeline = new(
                Id<TimelineMapIdTag>(0x91000005, 2),
                composition.Id,
                [
                    new TimelineMapEntry(
                        first.OutputRange,
                        first.Origin,
                        first.SourceRange),
                    new TimelineMapEntry(
                        second.OutputRange,
                        second.Origin,
                        second.SourceRange),
                ]);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
            RenderNode firstVideo = RenderNode.Video(
                Id<RenderNodeIdTag>(0x9100000a, 3),
                VideoInput.Id,
                first.OutputRange,
                first.Origin,
                first.SourceRange,
                "video");
            RenderNode secondVideo = RenderNode.Video(
                Id<RenderNodeIdTag>(0x9100000a, 4),
                VideoInput.Id,
                second.OutputRange,
                second.Origin,
                second.SourceRange,
                "video");
            RenderNode cut = RenderNode.TransitionEffect(
                Id<RenderNodeIdTag>(0x9100000a, 5),
                [firstVideo.Id, secondVideo.Id],
                new TimeRange(new RationalTime(6, 1), default),
                SourceOrigin,
                Transition.Cut());
            RenderNode output = RenderNode.Output(
                Id<RenderNodeIdTag>(0x9100000a, 6),
                [cut.Id],
                Range(0, 10));
            RenderGraph graph = CreateGraph(
                composition,
                timeline,
                timelineDigest,
                [VideoInput],
                [firstVideo, secondVideo, cut, output],
                output.Id);
            RenderGraphValidationContext context = CreateContext(
                composition,
                timeline,
                timelineDigest,
                [VideoInput]);
            Assert.IsTrue(RenderGraphValidator.Validate(graph, context).IsValid);
            return (graph, context);
        }

        internal string GetStagingDirectory(EntityId<JobIdTag> jobId) =>
            Path.Combine(
                WorkspacePath,
                "projects",
                Project.Id.ToString(),
                "staging",
                "previews",
                jobId.ToString());

        internal string GetFinalPath(PreviewRenderResult result) =>
            Path.Combine(
                WorkspacePath,
                "projects",
                Project.Id.ToString(),
                "previews",
                result.CacheKey.Digest.Hex,
                string.Concat(result.ArtifactId.ToString(), ".mp4"));

        internal PreviewRenderService CreateService(
            Func<
                ArtifactPromotionRequest,
                CancellationToken,
                Task<StoredArtifact>>? promoteAsync = null,
            Func<
                EntityId<ArtifactIdTag>,
                CancellationToken,
                Task<StoredArtifact>>? validateRegisteredAsync = null) =>
            new(
                Coordinator,
                CompileAsync,
                (request, cancellationToken) =>
                    ProcessHandler(request, cancellationToken),
                promoteAsync ?? Store.PromoteAsync,
                validateRegisteredAsync ?? Store.ValidateRegisteredAsync,
                Store.GetArtifact,
                WorkspacePath,
                _timeProvider);

        private Task<FfmpegCompilation> CompileAsync(
            FfmpegCompileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompileRequests.Add(request);
            var processRequest = new ProcessRunRequest(
                FfmpegPath,
                [request.OutputPath],
                request.WorkingDirectory,
                request.Timeout);
            return Task.FromResult(new FfmpegCompilation(
                processRequest,
                request.OutputPath,
                string.Empty,
                ContentDigest.Compute([])));
        }

        private Task<ProcessRunResult> DefaultProcessAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref ProcessInvocationCount);
            File.WriteAllBytes(request.ArgumentList.Single(), OutputContent);
            return Task.FromResult(SuccessfulProcessResult());
        }

        private FfmpegInputBinding CreateInputBinding(RenderGraphInput input) =>
            new(
                input.Id,
                new BinaryVerificationRequest(
                    "input.preview-proxy",
                    ProxyPath,
                    input.ContentDigest,
                    input.ByteSize));

        private Project CreateProject(ContentDigest sourceDigest)
        {
            var asset = new Asset(
                Id<AssetIdTag>(0x91000001, 1),
                "source.mov",
                "video/quicktime",
                sourceDigest,
                4096,
                new RightsRecord(
                    RightsStatus.Approved,
                    "rights:test",
                    "Approved for test rendering.",
                    externalPublicationAllowed: true));
            return new Project(
                Id<ProjectIdTag>(0x91000000, 1),
                "Preview render test",
                "Verify interval-faithful rendering.",
                "Test operators",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [asset],
                performers: []);
        }

        private CompositionVersion CreateComposition(
            IReadOnlyList<CompositionElement> elements) =>
            new(
                EntityId<CompositionVersionIdTag>.New(),
                Project.Id,
                parentVersionId: null,
                "preview-test",
                CreatedAt.AddMinutes(-2),
                "Create preview test composition.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements),
                elements);

        private RenderGraph CreateGraph(
            CompositionVersion composition,
            TimelineMap timeline,
            ContentDigest timelineDigest,
            IReadOnlyList<RenderGraphInput> inputs,
            IReadOnlyList<RenderNode> nodes,
            EntityId<RenderNodeIdTag> outputId) =>
            new(
                EntityId<RenderGraphIdTag>.New(),
                composition.Id,
                composition.ContentDigest,
                timeline.Id,
                timelineDigest,
                Profile.Id,
                Profile.ProfileDigest,
                Layout.Id,
                LayoutDigest,
                Template,
                inputs,
                fonts: [],
                nodes,
                outputId);

        private RenderGraphValidationContext CreateContext(
            CompositionVersion composition,
            TimelineMap timeline,
            ContentDigest timelineDigest,
            IReadOnlyList<RenderGraphInput> inputs) =>
            new(
                Project,
                composition,
                timeline,
                timelineDigest,
                Profile,
                Layout,
                LayoutDigest,
                Template,
                [SourceSpan],
                editorialRecordIds: [],
                inputs.Select(static input => new RenderArtifactEvidence(
                    input.Id,
                    input.ContentDigest,
                    input.ByteSize)).ToArray(),
                fonts: []);

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

        private static EntityId<TTag> Id<TTag>(uint prefix, long value) =>
            EntityId<TTag>.Parse(
                $"{prefix:x8}-0000-4000-8000-{value:x12}");

        private static ContentDigest Digest(char value) =>
            new(new string(value, 64));
    }
}