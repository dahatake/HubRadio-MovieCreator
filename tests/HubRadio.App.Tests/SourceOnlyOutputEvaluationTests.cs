using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SourceOnlyOutputEvaluationTests
{
    // FR-138 / FR-136: two Jobs of one Project that use different production templates (short-pop, then vertical 3-tier) must both
    // evaluate without BindingMismatch, as two Jobs of the same template do.
    [TestMethod]
    [TestCategory("FR-138")]
    public async Task TwoTemplateJobsInARow_ShortPopThenVertical3Tier_DoNotFailWithBindingMismatch()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true);
        foreach (string? template in new[] { StyleTemplates.StyleTemplateCatalog.ShortPopId, StyleTemplates.StyleTemplateCatalog.Vertical3TierId })
        {
            StandardArtifactGenerationResult generated = await f.GenerateWithTemplateAsync(template);
            Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, generated.Disposition, $"{template}: {generated.FailureReason}/{generated.CompilerFailureReason}");
            SourceOnlyResult result = await f.EvaluateAsync(generated: generated);
            Assert.IsFalse(result.Failures.Any(x => x.Code == SourceOnlyResultFailureCode.BindingMismatch), $"{template}: BindingMismatch");
            Assert.IsFalse(result.Artifacts.Any(a => a.FailureCode == SourceOnlyResultFailureCode.BindingMismatch),
                $"{template}: " + string.Join(",", result.Artifacts.Select(a => $"{a.Role}:{a.FailureCode}")));
        }
    }

    // FR-063 AC-038: a transient store failure on one evaluation append (seen on the 5th consecutive Job) is retried;
    // a deterministic rejection is not.
    [TestMethod]
    [TestCategory("FR-063")]
    public async Task TransientEvaluationAppendFailure_IsRetriedButInvalidEvidenceIsNot()
    {
        int calls = 0;
        int value = await SourceOnlyOutputEvaluation.RetryTransientAsync(() =>
        {
            if (++calls < 3) throw new IOException("transient");
            return 7;
        }, CancellationToken.None);
        Assert.AreEqual(7, value);
        Assert.AreEqual(3, calls);
        int invalidCalls = 0;
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await SourceOnlyOutputEvaluation.RetryTransientAsync<int>(
            () => { invalidCalls++; throw new ArgumentException("invalid"); }, CancellationToken.None));
        Assert.AreEqual(1, invalidCalls);
    }

    [TestMethod]
    public async Task EveryVideoGetsIndependentQcAndOnlyPrimaryGetsWorkflow()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true);
        var calls = new List<EntityId<ArtifactIdTag>>();
        SourceOnlyResult result = await f.EvaluateAsync((request, _) =>
        {
            calls.Add(request.OutputArtifactId);
            return Task.FromResult(f.Qc(request)); // Explicit synthetic measurements, NOT FFmpeg proof.
        });
        CollectionAssert.AreEquivalent(f.Generated.Artifacts.Where(a => a.QualityControlRequest is not null)
            .Select(a => a.Artifact.Id).ToArray(), calls.ToArray());
        Assert.HasCount(4, calls);
        Assert.HasCount(6, result.Artifacts);
        Assert.IsEmpty(result.Failures);
        Assert.IsNotNull(result.PrimaryWorkflow);
        Assert.AreEqual(result.Artifacts.Single(a => a.Role == SourceOnlyResultRole.LandscapeClean).ArtifactId, result.PrimaryWorkflow.ArtifactId);
        Assert.HasCount(1, f.Workflows.ListForProject(f.Generation.Project.Id));
        foreach (SourceOnlyResultArtifact a in result.Artifacts)
        {
            Assert.IsNotNull(a.Evaluation, a.FailureCode?.ToString());
            Assert.IsNull(a.FailureCode);
            Assert.AreEqual(a.ArtifactId.ToString(), a.Evaluation.ArtifactId!.ToString());
            Assert.AreEqual(a.VersionId, a.Evaluation.CompositionVersionId);
            Assert.HasCount(4, a.Evaluation.Rules.Where(r => r.Scope == AutomatedEvaluationRuleScope.UserInterface && r.Status == AutomatedEvaluationStatus.NotEvaluated).ToArray());
            Assert.HasCount(9, a.Evaluation.Limitations.Where(r => r.Status == AutomatedEvaluationStatus.NotEvaluated).ToArray());
            Assert.AreNotEqual(AutomatedEvaluationStatus.Pass, a.Evaluation.AggregateStatus);
            Assert.IsFalse(a.Delivered);
            Assert.HasCount(4, a.Evidence);
            foreach (SourceOnlyResultEvidenceReference proof in a.Evidence)
                Assert.AreEqual(proof.Artifact.ContentDigest, (await f.Generation.Store.ValidateRegisteredAsync(proof.Artifact.ArtifactId)).ContentDigest);
        }
        SourceOnlyResultArtifact primary = result.Artifacts.Single(a => a.Role == SourceOnlyResultRole.LandscapeClean);
        Assert.AreEqual(AutomatedEvaluationStatus.Pass, Rule(primary, AutomatedEvaluationRuleId.AvSync).Status);
        Assert.IsNull(Rule(primary, AutomatedEvaluationRuleId.AvSync).Target);
        Assert.AreEqual(AutomatedEvaluationStatus.Pass, Rule(primary, AutomatedEvaluationRuleId.Duration).Status);
        Assert.AreEqual(AutomatedEvaluationStatus.Pass, Rule(primary, AutomatedEvaluationRuleId.Loudness).Status);
        Assert.AreEqual(AutomatedEvaluationStatus.Pass, Rule(primary, AutomatedEvaluationRuleId.TruePeak).Status);
        Assert.IsFalse(result.HasBlockingFailures); // Exact technical timing needs no invented setting.
        Assert.AreEqual(QuickStartWorkflowState.ReadyWithWarnings, result.PrimaryWorkflow.State);
        Assert.IsTrue(result.GenerationWarnings.Select(w => (w.Role, w.Code))
            .SequenceEqual(f.Generated.Warnings.Select(w => (w.Role, w.Code))));
        Assert.IsTrue((await f.Service.ValidateAsync(result, f.Execution)).IsValid);
    }

    [TestMethod]
    public async Task PartialQcFailurePreservesAllFilesAndOtherIndependentRuns()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        EntityId<ArtifactIdTag> failedId = f.Generated.Artifacts.Single(a => a.BindingEvidence.Role == "landscape-burn-in").Artifact.Id;
        SourceOnlyResult result = await f.EvaluateAsync((request, _) => request.OutputArtifactId == failedId
            ? throw new OutputQcException(OutputQcFailureReason.DecodeFailed, OutputQcStage.Decode)
            : Task.FromResult(f.Qc(request)));
        Assert.HasCount(f.Generated.Artifacts.Count, result.Artifacts);
        SourceOnlyResultArtifact failed = result.Artifacts.Single(a => a.ArtifactId == failedId);
        Assert.AreEqual(SourceOnlyResultFailureCode.QualityControlFailed, failed.FailureCode);
        Assert.AreEqual(OutputQcFailureReason.DecodeFailed, failed.QcFailureReason);
        Assert.IsNull(failed.Measurements);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(failed, AutomatedEvaluationRuleId.FullDecode).Status);
        Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, Rule(failed, AutomatedEvaluationRuleId.Loudness).Status);
        foreach (SourceOnlyResultArtifact artifact in result.Artifacts)
        {
            Assert.IsNotNull(artifact.Evaluation);
            Assert.IsNotNull(f.Evaluations.Get(artifact.Evaluation.EvaluationId));
            Assert.AreEqual(artifact.ArtifactDigest, (await f.Generation.Store.ValidateRegisteredAsync(artifact.ArtifactId)).ContentDigest);
        }
        Assert.IsTrue((await f.Service.ValidateAsync(result, f.Execution)).IsValid);
    }

    [TestMethod]
    public async Task ForeignArtifactMeasurementsCannotAuthorizeAnotherRendition()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        OutputQcRequest first = f.Generated.Artifacts.First(a => a.QualityControlRequest is not null).QualityControlRequest!;
        SourceOnlyResult result = await f.EvaluateAsync((_, _) => Task.FromResult(f.Qc(first)));
        SourceOnlyResultArtifact other = result.Artifacts.Single(a => a.Role == SourceOnlyResultRole.LandscapeBurnIn);
        Assert.AreEqual(SourceOnlyResultFailureCode.BindingMismatch, other.FailureCode);
        Assert.IsNull(other.Measurements);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(other, AutomatedEvaluationRuleId.FullDecode).Status);
        Assert.AreEqual(other.ArtifactId.ToString(), other.Evaluation!.ArtifactId!.ToString());
    }

    [TestMethod]
    // NFR-FACT-001 AC-120: 不成立・中断した成果物を成功や捏造した内容で埋めず、そのまま記録する。
    public async Task CancellationRetainsGeneratedDescriptorsWithoutSynthesizingSuccess()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        SourceOnlyResult result = await f.EvaluateAsync((request, ct) =>
        {
            calls++;
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(f.Qc(request));
        }, cancellation.Token);
        Assert.AreEqual(1, calls);
        Assert.HasCount(f.Generated.Artifacts.Count, result.Artifacts);
        Assert.IsTrue(result.Artifacts.All(a => a.FailureCode == SourceOnlyResultFailureCode.Canceled && a.Evaluation is null && !a.Delivered));
        Assert.IsTrue(result.HasBlockingFailures);
        Assert.AreEqual(result.CanonicalJson, SourceOnlyResult.ParseCanonical(result.CanonicalJson, result.CanonicalDigest).CanonicalJson);
    }

    [TestMethod]
    public async Task UnexpectedQcExceptionIsSanitizedAndDoesNotDropSidecars()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync((_, _) => throw new IOException(@"C:\private\sensitive.mp4"));
        Assert.IsTrue(result.Artifacts.Where(a => a.IsVideo).All(a => a.FailureCode == SourceOnlyResultFailureCode.QualityControlFailed));
        Assert.IsTrue(result.Artifacts.Where(a => !a.IsVideo).All(a => a.Evaluation is not null));
        Assert.IsFalse(result.CanonicalJson.Contains("private", StringComparison.Ordinal));
        Assert.IsFalse(result.CanonicalJson.Contains("sensitive", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualSubtitleBuilderGenerationWarningsSurviveEvaluationCheckpointAndRestart(bool failQc)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true, useSubtitleBuilder: true);
        string shortRole = $"vertical-subtitles-{f.Generation.Shorts.Single().ShortFormId}";
        SourceOnlyResultGenerationWarning[] expected = f.Generated.Warnings.Select(w => new SourceOnlyResultGenerationWarning(w.Role, w.Code)).ToArray();
        CollectionAssert.Contains(expected, new SourceOnlyResultGenerationWarning("landscape-subtitles",
            "opaque-setting-explicitly-overridden-by-transparent-background-request"));
        CollectionAssert.Contains(expected, new SourceOnlyResultGenerationWarning("landscape-subtitles", "NOT EVALUATED"));
        CollectionAssert.Contains(expected, new SourceOnlyResultGenerationWarning(shortRole, "NOT EVALUATED"));
        Assert.IsTrue(f.Generated.Artifacts.Any(a => a.BindingEvidence.Role == "landscape-burn-in"));
        Assert.IsTrue(f.Generated.Omissions.Any(o => o.Role == $"vertical-burn-in-{f.Generation.Shorts.Single().ShortFormId}"));
        await f.SaveGeneratorCheckpointAsync();
        SourceOnlyResult result = await f.EvaluateAsync((request, _) => failQc
            ? throw new OutputQcException(OutputQcFailureReason.DecodeFailed, OutputQcStage.Decode)
            : Task.FromResult(f.Qc(request))); // Real builder/generator/stores; synthetic media/QC, not a live E2E PASS.
        CollectionAssert.AreEqual(expected, result.GenerationWarnings.ToArray());
        CollectionAssert.AreEqual(expected.Select(w => new SourceOnlyResultGenerationWarningDto(w.Role, w.Code)).ToArray(), result.ToWebDto().GenerationWarnings);
        Assert.HasCount(f.Generated.Artifacts.Count, result.Artifacts);
        Assert.IsTrue(result.HasWarnings);
        Assert.AreEqual(failQc, result.HasBlockingFailures);
        foreach (SourceOnlyResultArtifact artifact in result.Artifacts)
        {
            Assert.IsNotNull(artifact.Evaluation);
            Assert.AreNotEqual(AutomatedEvaluationStatus.Pass, artifact.Evaluation.AggregateStatus);
            Assert.IsTrue(artifact.Evaluation.Limitations.All(r => r.Status == AutomatedEvaluationStatus.NotEvaluated));
            Assert.IsTrue(artifact.Evaluation.Rules.Where(r => r.Scope == AutomatedEvaluationRuleScope.UserInterface)
                .All(r => r.Status == AutomatedEvaluationStatus.NotEvaluated));
            Assert.IsFalse(artifact.Delivered);
        }
        StoredSourceOnlyExecution persisted = f.PersistResult(result);
        var restarted = new SourceOnlyResultService(new ArtifactStore(f.Generation.DatabasePath, f.Generation.Root),
            new AutomatedEvaluationStore(f.Generation.DatabasePath), new QuickStartWorkflowStore(f.Generation.DatabasePath),
            new ProjectStore(f.Generation.DatabasePath));
        SourceOnlyResultLoadResult loaded = await restarted.LoadAsync(new SourceOnlyExecutionStore(f.Generation.DatabasePath).Get(persisted.Request.ExecutionId)!);
        Assert.IsTrue(loaded.Validation.IsValid, string.Join(",", loaded.Validation.Failures.Select(x => x.Code)));
        Assert.AreEqual(result.CanonicalJson, loaded.Result.CanonicalJson);
        CollectionAssert.AreEqual(expected, loaded.Result.GenerationWarnings.ToArray());
        SourceOnlyResult replayed = await new SourceOnlyOutputEvaluation(f.Generation.Store,
            (_, _) => throw new AssertFailedException("A warning-preserving replay must not rerun QC."), f.Evaluations, f.Workflows, f.Projects)
            .EvaluateAsync(persisted, f.Generated, f.Generation.SettingsProfile, f.Generation.Settings);
        Assert.AreEqual(result.CanonicalJson, replayed.CanonicalJson);
    }

    [TestMethod]
    public async Task ApprovedAudioLimitsRemainExactWithoutAnyTimingToleranceSettings()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        DefaultSettingsProfile profile = f.Generation.SettingsProfile;
        DefaultSettingsSnapshot settings = profile.CreateSnapshot();
        foreach ((decimal loudness, decimal peak, bool valid) in new[]
        {
            (-17m, -1m, true), (-15m, -1m, true),
            (-17.0000001m, -0.9999999m, false), (-14.9999999m, -0.5m, false),
        })
        {
            IReadOnlyList<AutomatedEvaluationRuleObservation> rules = f.Observe(profile, settings, loudness, peak, 0);
            Assert.AreEqual(valid ? AutomatedEvaluationStatus.Pass : AutomatedEvaluationStatus.Fail, rules.Single(r => r.RuleId == AutomatedEvaluationRuleId.Loudness).Status);
            Assert.AreEqual(valid ? AutomatedEvaluationStatus.Pass : AutomatedEvaluationStatus.Fail, rules.Single(r => r.RuleId == AutomatedEvaluationRuleId.TruePeak).Status);
            Assert.AreEqual(AutomatedEvaluationStatus.Pass, rules.Single(r => r.RuleId == AutomatedEvaluationRuleId.AvSync).Status);
            Assert.AreEqual(AutomatedEvaluationStatus.Pass, rules.Single(r => r.RuleId == AutomatedEvaluationRuleId.Duration).Status);
        }
        DefaultSettingsSnapshot changed = profile.CreateSnapshot(settings.Values.Select(s => s.Key == "audio.internalOnlineTargetLoudness"
            ? new DefaultSettingSelection(s.Key, DefaultSettingValue.FromInteger(-20)) : s).ToArray());
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, f.Observe(profile, changed, -16, -1, 0).Single(r => r.RuleId == AutomatedEvaluationRuleId.Loudness).Status);
        Assert.AreEqual(AutomatedEvaluationStatus.Pass, f.Observe(profile, changed, -20, -1, 0).Single(r => r.RuleId == AutomatedEvaluationRuleId.Loudness).Status);
    }

    [TestMethod]
    public async Task EveryMeasuredNonzeroAvOffsetIncludingCodecPaddingIsWarningNotAnUnapprovedThresholdFailure()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        var start = new RationalTime(-1024, 48000);
        var padding = new RationalTime(1, 48000);
        SourceOnlyResult result = await f.EvaluateAsync((r, _) => Task.FromResult(f.Qc(r, audioStart: start, audioEndPadding: padding)));
        SourceOnlyResultArtifact primary = result.Artifacts[0];
        Assert.AreEqual(AutomatedEvaluationStatus.Pass, Rule(primary, AutomatedEvaluationRuleId.Duration).Status);
        Assert.AreEqual(AutomatedEvaluationStatus.Warning, Rule(primary, AutomatedEvaluationRuleId.AvSync).Status);
        Assert.AreEqual("timing.numeric_tolerance_unapproved", Rule(primary, AutomatedEvaluationRuleId.AvSync).ReasonCode);
        Assert.AreEqual(start, primary.Measurements!.AudioVideoStartOffset);
        Assert.AreEqual(padding, primary.Measurements.AudioVideoEndOffset);
        SourceOnlyResultMeasurementsDto projected = result.ToWebDto().Artifacts[0].Measurements!;
        Assert.AreEqual(new SourceOnlyResultTimeDto(start.Value, start.Rate), projected.AudioVideoStartOffset);
        Assert.AreEqual(new SourceOnlyResultTimeDto(padding.Value, padding.Rate), projected.AudioVideoEndOffset);
        Assert.IsNull(Rule(primary, AutomatedEvaluationRuleId.AvSync).Target!.SettingKey);
        Assert.AreEqual(primary.ArtifactDigest, Rule(primary, AutomatedEvaluationRuleId.AvSync).Target!.ArtifactDigest);
        Assert.IsFalse(result.HasBlockingFailures);
        Assert.IsTrue((await f.Service.ValidateAsync(result, f.Execution)).IsValid);
    }

    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(0, -1)]
    [DataRow(0, 1)]
    public async Task WrongVideoStartOrMeasuredFrameCountFailsExactDuration(int startFrames, int frameCountDelta)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync((r, _) => Task.FromResult(f.Qc(r,
            videoStartFrames: startFrames, videoFrameCountDelta: frameCountDelta)));
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(result.Artifacts[0], AutomatedEvaluationRuleId.Duration).Status);
        Assert.AreEqual("source_only.video_presentation_mismatch_v2", Rule(result.Artifacts[0], AutomatedEvaluationRuleId.Duration).ReasonCode);
        Assert.IsNull(Rule(result.Artifacts[0], AutomatedEvaluationRuleId.Duration).Target!.SettingKey);
        Assert.IsTrue(result.HasBlockingFailures);
        Assert.IsTrue((await f.Service.ValidateAsync(result, f.Execution)).IsValid);
    }

    [TestMethod]
    public async Task NotEvaluatedUiIsNonblockingButHardRuleMissingIsBlocking()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        DefaultSettingsProfile profile = f.Generation.SettingsProfile;
        DefaultSettingsSnapshot settings = profile.CreateSnapshot();
        IReadOnlyList<AutomatedEvaluationRuleObservation> rules = f.Observe(profile, settings, -16, -1, 0);
        AutomatedEvaluationRun run = f.BuildRun(profile, settings, rules);
        Assert.AreEqual(QuickStartWorkflowState.ReadyWithWarnings, QuickStartEvaluationPolicy.DetermineCompletedState(run));
        AutomatedEvaluationRun missing = f.BuildRun(profile, settings, rules.Where(r => r.RuleId != AutomatedEvaluationRuleId.Duration).ToArray());
        Assert.AreEqual(QuickStartWorkflowState.Failed, QuickStartEvaluationPolicy.DetermineCompletedState(missing));
    }

    [TestMethod]
    public async Task MeasuredDigitalSilenceIsNonblockingButNeverAnAcceptableProgramLoudnessPass()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync((request, _) => Task.FromResult(f.Qc(request,
            loudnessValue: OutputQcMeasuredValue.NegativeInfinity, peakValue: OutputQcMeasuredValue.NegativeInfinity)));
        SourceOnlyResultArtifact first = result.Artifacts[0];
        Assert.AreEqual(OutputQcMeasuredValueKind.NegativeInfinity, first.Measurements!.Loudness!.Kind);
        Assert.IsNull(first.Measurements.Loudness.Value);
        Assert.AreEqual(AutomatedEvaluationStatus.Warning, Rule(first, AutomatedEvaluationRuleId.Loudness).Status);
        Assert.AreEqual(AutomatedEvaluationStatus.Pass, Rule(first, AutomatedEvaluationRuleId.TruePeak).Status);
        Assert.IsFalse(first.HasBlockingFailure);
        Assert.IsTrue(first.Measurements.OutputDigitalSilence);
        Assert.AreEqual("source_only.output_digital_silence_measured", Rule(first, AutomatedEvaluationRuleId.Loudness).ReasonCode);
        Assert.AreNotEqual(AutomatedEvaluationStatus.Pass, first.Evaluation!.AggregateStatus);
        Assert.AreEqual(result.CanonicalJson, SourceOnlyResult.ParseCanonical(result.CanonicalJson, result.CanonicalDigest).CanonicalJson);
    }

    [TestMethod]
    public async Task UnmeasurableIntegratedLoudnessDoesNotProveSilenceWithoutNegativeInfiniteTruePeak()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync((r, _) => Task.FromResult(f.Qc(r, loudnessValue: OutputQcMeasuredValue.NegativeInfinity)));
        Assert.IsFalse(result.Artifacts[0].Measurements!.OutputDigitalSilence);
        Assert.AreEqual(AutomatedEvaluationStatus.Warning, Rule(result.Artifacts[0], AutomatedEvaluationRuleId.Loudness).Status);
        Assert.AreEqual("source_only.integrated_loudness_not_measurable", Rule(result.Artifacts[0], AutomatedEvaluationRuleId.Loudness).ReasonCode);
        Assert.IsFalse(result.HasBlockingFailures);
    }

    [TestMethod]
    public async Task NoSpeechCuesAndPhysicallyMeasuredSilenceCompleteWithExplicitWarnings()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(noSubtitles: true, silentRender: true);
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, f.Generated.Disposition);
        Assert.IsEmpty(f.Generated.Artifacts.Single().DisplayCues);
        SourceOnlyResult result = await f.EvaluateAsync((r, _) => Task.FromResult(f.Qc(r,
            loudnessValue: OutputQcMeasuredValue.NegativeInfinity, peakValue: OutputQcMeasuredValue.NegativeInfinity)));
        Assert.IsFalse(result.HasBlockingFailures);
        Assert.IsTrue(result.Artifacts.Single().Measurements!.OutputDigitalSilence);
        Assert.IsTrue(result.GenerationWarnings.Any(w => w.Code == "SilentAudioLoudnessNotMeasurable"));
        Assert.IsTrue((await f.Service.LoadAsync(f.PersistResult(result))).Validation.IsValid);
    }

    [TestMethod]
    public async Task NoSubtitleOrRenderSilenceFlagsDoNotOverridePhysicalFiniteNonSilentFailure()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(noSubtitles: true, silentRender: true);
        SourceOnlyResult result = await f.EvaluateAsync((r, _) => Task.FromResult(f.Qc(r, loudness: -30, peak: -2)));
        Assert.IsFalse(result.Artifacts.Single().Measurements!.OutputDigitalSilence);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(result.Artifacts.Single(), AutomatedEvaluationRuleId.Loudness).Status);
        Assert.IsTrue(result.HasBlockingFailures);
    }

    [TestMethod]
    public async Task PositiveInfinityAndNanCannotPassLoudness()
    {
        using (SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync())
        {
            SourceOnlyResult result = await f.EvaluateAsync((r, _) => Task.FromResult(f.Qc(r,
                loudnessValue: OutputQcMeasuredValue.PositiveInfinity, peakValue: OutputQcMeasuredValue.PositiveInfinity)));
            Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(result.Artifacts[0], AutomatedEvaluationRuleId.Loudness).Status);
            Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(result.Artifacts[0], AutomatedEvaluationRuleId.TruePeak).Status);
            Assert.IsTrue(result.HasBlockingFailures);
        }
        using (SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync())
        {
            SourceOnlyResult result = await f.EvaluateAsync((r, cancellationToken) =>
            {
                _ = OutputQc.ParseLoudnessOutput(StandardGenerationFixture.LoudnessReport.Replace("-22.00", "nan", StringComparison.Ordinal));
                return Task.FromResult(f.Qc(r));
            });
            Assert.AreEqual(OutputQcFailureReason.LoudnessOutputInvalid, result.Artifacts[0].QcFailureReason);
            Assert.IsTrue(result.HasBlockingFailures);
        }
    }

    [TestMethod]
    public async Task ActualGeneratorThumbnailsKeepOwnVersionProfileFrameEvidenceAndNeverReceiveVideoQc()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true);
        var calls = new List<EntityId<ArtifactIdTag>>();
        SourceOnlyResult result = await f.EvaluateAsync((r, _) => { calls.Add(r.OutputArtifactId); return Task.FromResult(f.Qc(r)); });
        SourceOnlyResultArtifact[] images = result.Artifacts.Where(a => a.IsImage).ToArray();
        Assert.HasCount(2, images);
        foreach (SourceOnlyResultArtifact image in images)
        {
            StandardGeneratedArtifact generated = f.Generated.Artifacts.Single(a => a.Artifact.Id == image.ArtifactId);
            Assert.IsFalse(calls.Contains(image.ArtifactId));
            Assert.AreEqual("image/png", image.MediaType);
            Assert.AreEqual(ArtifactKind.Render, image.Artifact.Kind);
            Assert.AreEqual(SourceOnlyResultQcApplicability.ImageFileOnly, image.QcApplicability);
            Assert.IsNotNull(image.ThumbnailPlanId);
            Assert.IsNull(image.RenditionId);
            Assert.IsNull(image.Measurements);
            Assert.AreEqual(generated.Graph.CompositionVersionId, image.VersionId);
            Assert.AreNotEqual(result.VersionId, image.VersionId);
            Assert.AreEqual(generated.Graph.ProfileId, image.ProfileId);
            Assert.AreEqual(generated.BindingEvidence.Execution!.Thumbnail!.SourceFrameSequence, image.FrameEvidence!.SourceFrameSequence);
            Assert.AreEqual(AutomatedEvaluationStatus.Pass, Rule(image, AutomatedEvaluationRuleId.Codec).Status);
            foreach (AutomatedEvaluationRuleId rule in new[] { AutomatedEvaluationRuleId.Duration, AutomatedEvaluationRuleId.AvSync,
                AutomatedEvaluationRuleId.FullDecode, AutomatedEvaluationRuleId.Loudness, AutomatedEvaluationRuleId.TruePeak })
                Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, Rule(image, rule).Status);
            Assert.IsFalse(image.HasBlockingFailure);
        }
        Assert.IsFalse(result.HasBlockingFailures);
        Assert.IsTrue((await f.Service.LoadAsync(f.PersistResult(result))).Validation.IsValid);
    }

    [TestMethod]
    public async Task ValidPngWithWrongCanvasFailsImageChecksWithoutInventedVideoQc()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true, wrongPngCanvas: true);
        SourceOnlyResult result = await f.EvaluateAsync();
        foreach (SourceOnlyResultArtifact image in result.Artifacts.Where(a => a.IsImage))
        {
            Assert.AreEqual(SourceOnlyResultFailureCode.QualityControlFailed, image.FailureCode);
            Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(image, AutomatedEvaluationRuleId.Codec).Status);
            Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, Rule(image, AutomatedEvaluationRuleId.FullDecode).Status);
            Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, Rule(image, AutomatedEvaluationRuleId.Duration).Status);
            Assert.IsNull(image.Measurements);
        }
        Assert.IsTrue(result.HasBlockingFailures);
    }

    [TestMethod]
    [DataRow("thumbnail-landscape-")]
    [DataRow("thumbnail-vertical-not-an-id")]
    [DataRow("thumbnail-square-11111111-1111-4111-8111-111111111111")]
    [DataRow("thumbnail-landscape-FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF")]
    [DataRow("LandscapeThumbnail")]
    public void ThumbnailRoleParserRejectsUnknownShapesAndNoncanonicalIds(string role) =>
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyOutputEvaluation.ParseRole(role));

    [TestMethod]
    public async Task ForgedDisplayCuesFailEvenWithRehashedBindingAndManifest()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        StandardGeneratedArtifact original = f.Generated.Artifacts.Single(a => a.BindingEvidence.Role == "landscape-burn-in");
        SubtitleFileCue cue = original.DisplayCues.Single();
        SubtitleFileCue[] forged = [new(cue.Cue, "fabricated text", cue.Style)];
        var binding = original.BindingEvidence with { DisplayCueDigest = StandardRenderGraphFactory.Digest(forged) };
        RenderManifest m = original.Manifest;
        var manifest = new RenderManifest(m.Id, original.Context.CompositionVersion, original.Context.TimelineMap, m.TimelineMapDigest,
            original.Context.Profile, StandardRenderGraphFactory.Digest(binding), m.ApplicationVersion, m.ApplicationDigest,
            m.Inputs, m.Tools, m.Models, m.Fonts, m.Outputs);
        var changed = original with
        {
            DisplayCues = forged,
            BindingEvidence = binding,
            Manifest = manifest,
            QualityControlRequest = new OutputQcRequest(original.Artifact.Id, manifest, original.Graph, original.Context,
                original.SubtitleEvidence, TimeSpan.FromMinutes(1))
        };
        var generated = new StandardArtifactGenerationResult(f.Generated.Disposition,
            f.Generated.Artifacts.Select(a => a.Artifact.Id == original.Artifact.Id ? changed : a).ToArray(),
            f.Generated.Omissions, [], null, null, false, f.Generated.Warnings);
        SourceOnlyResult result = await f.EvaluateAsync(generated: generated);
        SourceOnlyResultArtifact rejected = result.Artifacts.Single(a => a.ArtifactId == original.Artifact.Id);
        Assert.AreEqual(SourceOnlyResultFailureCode.BindingMismatch, rejected.FailureCode);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(rejected, AutomatedEvaluationRuleId.Provenance).Status);
        Assert.IsTrue(rejected.HasBlockingFailure);
    }

    [TestMethod]
    public async Task ActualAssByteMismatchIsRejectedBeforeOutputQcEvenWithUntouchedCueMetadata()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        StandardGeneratedArtifact original = f.Generated.Artifacts.Single(a => a.BindingEvidence.Role == "landscape-burn-in");
        StandardSubtitleRenderFile file = original.SubtitleFiles.Single();
        byte[] bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file.File.GetUtf8Bytes()).Replace("元の字幕", "別の字幕", StringComparison.Ordinal));
        var changed = original with
        {
            SubtitleFiles = [new(file.Input, new SubtitleExportFile(file.File.Format,
            file.File.CueCount, file.File.TimestampRuleVersion, bytes))]
        };
        var generated = new StandardArtifactGenerationResult(f.Generated.Disposition,
            f.Generated.Artifacts.Select(a => a.Artifact.Id == original.Artifact.Id ? changed : a).ToArray(),
            f.Generated.Omissions, [], null, null, false, f.Generated.Warnings);
        var calls = new List<EntityId<ArtifactIdTag>>();
        SourceOnlyResult result = await f.EvaluateAsync((r, _) => { calls.Add(r.OutputArtifactId); return Task.FromResult(f.Qc(r)); }, generated: generated);
        Assert.IsFalse(calls.Contains(original.Artifact.Id));
        SourceOnlyResultArtifact rejected = result.Artifacts.Single(a => a.ArtifactId == original.Artifact.Id);
        Assert.AreEqual(SourceOnlyResultFailureCode.BindingMismatch, rejected.FailureCode);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, Rule(rejected, AutomatedEvaluationRuleId.Provenance).Status);
        Assert.IsTrue(rejected.HasBlockingFailure);
    }

    [TestMethod]
    public async Task EvidenceSizeFailurePreservesDescriptorAndContinuesOtherOutputs()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        StandardGeneratedArtifact first = f.Generated.Artifacts[0];
        RenderManifest m = first.Manifest;
        var oversized = new RenderManifest(m.Id, first.ValidationContext.CompositionVersion, first.ValidationContext.TimelineMap,
            m.TimelineMapDigest, first.ValidationContext.Profile, m.ConfigurationDigest, new string('x', 32769), m.ApplicationDigest,
            m.Inputs, m.Tools, m.Models, m.Fonts, m.Outputs);
        var generated = new StandardArtifactGenerationResult(f.Generated.Disposition,
            [first with { Manifest = oversized }, .. f.Generated.Artifacts.Skip(1)], f.Generated.Omissions, [], null, null, false);
        SourceOnlyResult result = await f.EvaluateAsync(generated: generated);
        Assert.HasCount(f.Generated.Artifacts.Count, result.Artifacts);
        Assert.AreEqual(first.Artifact.Id, result.Artifacts[0].ArtifactId);
        Assert.AreEqual(SourceOnlyResultFailureCode.EvidencePersistenceFailed, result.Artifacts[0].FailureCode);
        Assert.IsNull(result.Artifacts[0].ManifestDigest);
        Assert.IsNull(result.Artifacts[0].Evaluation);
        Assert.IsTrue(result.Artifacts.Skip(1).All(a => a.Evaluation is not null));
        Assert.IsTrue(result.HasBlockingFailures);
        Assert.AreEqual(result.CanonicalJson, SourceOnlyResult.ParseCanonical(result.CanonicalJson, result.CanonicalDigest).CanonicalJson);
    }

    private static AutomatedEvaluationRuleResult Rule(SourceOnlyResultArtifact artifact, AutomatedEvaluationRuleId id) =>
        artifact.Evaluation!.Rules.Single(r => r.RuleId == id);
}

// Real canonical composition, ArtifactStore and evaluation/workflow DB; synthetic media,
// synthetic QC timing/audio are explicit test-only seams. Acceptance, UpdateState,
// Get and restart validation use durable store rows, never a fabricated Stored object.
internal sealed class SourceOnlyEvaluationFixture : IDisposable
{
    // Synthetic FFmpeg stderr, not physical QC proof: silent loudnorm reports -inf
    // measurements and target_i - output_i = +infinity, serialized as "inf".
    internal const string SilentLoudnessReport = """
        [Parsed_loudnorm_99 @ synthetic]
        {"input_i":"-inf","input_tp":"-inf","input_lra":"0.00","input_thresh":"-70.00",
         "output_i":"-inf","output_tp":"-inf","output_lra":"0.00","output_thresh":"-70.00",
         "normalization_type":"dynamic","target_offset":"inf"}
        """;
    internal StandardGenerationFixture Generation { get; }
    internal StandardArtifactGenerationResult Generated { get; }
    internal StoredSourceOnlyExecution Execution { get; }
    internal AutomatedEvaluationStore Evaluations { get; }
    internal QuickStartWorkflowStore Workflows { get; }
    internal ProjectStore Projects { get; }
    internal SourceOnlyExecutionStore Executions { get; }
    internal SourceOnlyResultService Service => new(Generation.Store, Evaluations, Workflows, Projects);
    internal static readonly DateTimeOffset Now = new(2042, 1, 1, 0, 1, 0, TimeSpan.Zero);

    private SourceOnlyEvaluationFixture(StandardGenerationFixture generation, StandardArtifactGenerationResult generated)
    {
        Generation = generation; Generated = generated;
        Projects = new(generation.DatabasePath); Evaluations = new(generation.DatabasePath); Workflows = new(generation.DatabasePath);
        Executions = new(generation.DatabasePath);
        var settingsStore = new DefaultSettingsStore(generation.DatabasePath);
        _ = settingsStore.RegisterProfile(Encoding.UTF8.GetBytes(generation.SettingsProfile.CanonicalJson));
        _ = settingsStore.BindProjectSettings(generation.Project.Id, generation.Settings, generation.Parent.CreatedAt);
        var draft = new ProjectCreationDraft(EntityId<ProjectCreationDraftIdTag>.New(), generation.Asset.Id,
            generation.Project.ContentName, generation.Project.Purpose, generation.Project.Audience, PublicationScope.InternalOnly,
            [new ProjectOutputProfileReference(generation.Preparation.Plan.LongForm.Profile.Profile.Id, "long-form",
                generation.Preparation.Plan.LongForm.Profile.Profile.ProfileDigest)], "ja-JP", ["Host"], new ProjectDeadlineSelection(null),
            generation.Preparation.Plan.LongForm.Profile.TemplateReference);
        string draftJson = HubRadio.Core.Serialization.CanonicalJson.Canonicalize(JsonSerializer.Serialize(draft));
        _ = new ProjectCreationDraftStore(generation.DatabasePath).CreateDraft(draft, draftJson, generation.Parent.CreatedAt);
        var request = new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), generation.Project.Id,
            draft.Id, generation.Asset.Id, EntityId<JobIdTag>.New(), EntityId<JobIdTag>.New(),
            generation.Settings, StandardGenerationFixture.Actor, generation.Parent.CreatedAt);
        // StandardGenerationFixture starts with an already imported project. Seed its
        // finalized-draft relation, then invoke the REAL acceptance store transaction.
        // Do not call the public finalizer, which correctly forbids importing assets at finalization.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = generation.DatabasePath, ForeignKeys = true }.ToString()))
        {
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE project_creation_drafts SET revision = 1, finalized_project_id = $project,
                    finalized_at = $at, updated_at = $at WHERE id = $draft AND revision = 0;
                """;
            command.Parameters.AddWithValue("$project", request.ProjectId.ToString());
            command.Parameters.AddWithValue("$at", request.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$draft", draft.Id.ToString());
            Assert.AreEqual(1, command.ExecuteNonQuery());
            byte[] acceptance = SourceOnlyExecutionStore.PrepareInitial(request, StandardGenerationFixture.Hash(draftJson), null);
            SourceOnlyExecutionStore.InsertInitial(connection, transaction, request, acceptance, null);
            transaction.Commit();
        }
        StoredSourceOnlyExecution accepted = Executions.Get(request.ExecutionId)!;
        Assert.AreEqual(SourceOnlyExecutionPhase.Queued, accepted.Phase);
        Execution = Executions.UpdateState(request.ExecutionId, accepted.Revision, SourceOnlyExecutionPhase.Evaluating,
            versionId: generation.Preparation.Child.Id, digest: generation.Preparation.Child.ContentDigest);
        Assert.AreEqual(Execution.StateDigest, new SourceOnlyExecutionStore(generation.DatabasePath).Get(request.ExecutionId)!.StateDigest);
    }
    internal static async Task<SourceOnlyEvaluationFixture> CreateAsync(bool includeShort = false, bool includeThumbnail = false,
        bool noSubtitles = false, bool silentRender = false, bool wrongPngCanvas = false, bool useSubtitleBuilder = false)
    {
        StandardGenerationFixture generation = await StandardGenerationFixture.CreateAsync(includeShort, includeThumbnail: includeThumbnail,
            fontName: useSubtitleBuilder ? "Noto Sans JP" : "Fixture Sans",
            secondTokenDuration: useSubtitleBuilder ? new RationalTime(2, 1) : null);
        try
        {
            int imageCount = 0;
            StandardRenderConfiguration configuration = generation.Request().Configuration;
            var generator = new StandardArtifactGenerator(generation.Store, generation.Versions, generation.Root,
                async (process, ct) =>
                {
                    bool image = process.ArgumentList.Contains("png");
                    bool vertical = image && imageCount++ % 2 == 1;
                    byte[] bytes = image ? PngBytes(wrongPngCanvas ? 2 : vertical ? configuration.VerticalThumbnailWidth : configuration.LandscapeThumbnailWidth,
                        wrongPngCanvas ? 2 : vertical ? configuration.VerticalThumbnailHeight : configuration.LandscapeThumbnailHeight)
                        : Encoding.UTF8.GetBytes("synthetic:" + Path.GetFileName(process.ArgumentList[^1]));
                    await File.WriteAllBytesAsync(process.ArgumentList[^1], bytes, ct);
                    return new ProcessRunResult(0, "", silentRender ? SilentLoudnessReport : StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
                }, StandardGenerationFixture.CompileAsync, generation.ProbeGeometryAsync);
            StandardSubtitleInput subtitles = noSubtitles ? new([], generation.Subtitles.DisplayProfile, generation.Subtitles.AssProfile) : generation.Subtitles;
            IReadOnlyList<StandardShortArtifactInput> shorts = generation.Shorts;
            if (useSubtitleBuilder)
            {
                subtitles = StandardSubtitleBuilder.BuildWithEvidence(generation.Preparation.Child, generation.Preparation.ChildTimelineMap,
                    generation.Preparation.Plan.LongForm.Profile.Layout, generation.Preparation.Plan.LongForm.Profile.Profile,
                    generation.Transcript, generation.TranscriptDigest, generation.Spans, generation.SettingsProfile, generation.Settings,
                    generation.Font.Evidence, StandardGenerationFixture.Actor, generation.Transcript.CreatedAt).Subtitles;
                shorts = generation.Preparation.Plan.ShortForms.Select(plan =>
                {
                    CompositionVersion version = StandardRenderGraphFactory.CreateShortVersion(generation.Preparation, plan.Id);
                    StandardSubtitleBuildResult built = StandardSubtitleBuilder.BuildWithEvidence(version,
                        TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), version), plan.Layout.DerivedLayoutSpec,
                        StandardRenderGraphFactory.GetProfile(generation.Preparation.Plan, true, plan.Id), generation.Transcript,
                        generation.TranscriptDigest, generation.Spans.Concat(plan.SourceSpans).DistinctBy(span => span.Id).ToArray(),
                        generation.SettingsProfile, generation.Settings, generation.Font.Evidence, StandardGenerationFixture.Actor,
                        generation.Transcript.CreatedAt);
                    return new StandardShortArtifactInput(plan.Id, built.Subtitles);
                }).ToArray();
            }
            StandardArtifactGenerationResult generated = await generator.GenerateAsync(generation.Request(subtitles: subtitles, shorts: shorts));
            Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, generated.Disposition, $"{generated.FailureReason}/{generated.CompilerFailureReason}");
            return new(generation, generated);
        }
        catch { generation.Dispose(); throw; }
    }
    /// <summary>FR-138: a second (or later) generation of the same Project's short with a production template, as a Regenerate does.</summary>
    internal async Task<StandardArtifactGenerationResult> GenerateWithTemplateAsync(string? styleTemplateId)
    {
        StandardGenerationFixture g = Generation;
        StandardRenderConfiguration configuration = g.Request().Configuration;
        var generator = new StandardArtifactGenerator(g.Store, g.Versions, g.Root,
            async (process, ct) =>
            {
                bool image = process.ArgumentList.Contains("png");
                bool vertical = image && imageCount++ % 2 == 1;
                byte[] bytes = image ? PngBytes(vertical ? configuration.VerticalThumbnailWidth : configuration.LandscapeThumbnailWidth,
                    vertical ? configuration.VerticalThumbnailHeight : configuration.LandscapeThumbnailHeight)
                    : Encoding.UTF8.GetBytes("synthetic:" + Path.GetFileName(process.ArgumentList[^1]));
                await File.WriteAllBytesAsync(process.ArgumentList[^1], bytes, ct);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, g.ProbeGeometryAsync);
        var request = new StandardArtifactGenerationRequest(g.Preparation, g.Original, g.Index, g.Geometry, g.SettingsProfile, g.Settings,
            g.Tool, g.ToolEvidence, g.Model, g.Font, g.Subtitles, g.Shorts, "test-1", StandardGenerationFixture.Hash("application"),
            TimeSpan.FromMinutes(2), g.Music,
            styleTemplateId is null ? null : StyleTemplates.StyleTemplateCatalog.Find(styleTemplateId));
        return await generator.GenerateAsync(request);
    }

    private int imageCount;

    internal async Task<SourceOnlyResult> EvaluateAsync(Func<OutputQcRequest, CancellationToken, Task<OutputQcResult>>? qc = null,
        CancellationToken ct = default, StandardArtifactGenerationResult? generated = null,
        Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>? promoteEvidence = null)
    {
        SourceOnlyResult result = await new SourceOnlyOutputEvaluation(Generation.Store, qc ?? ((r, _) => Task.FromResult(Qc(r))), Evaluations, Workflows, Projects, () => Now, promoteEvidence)
            .EvaluateAsync(Execution, generated ?? Generated, Generation.SettingsProfile, Generation.Settings, ct);
        foreach (SourceOnlyResultArtifact artifact in result.Artifacts.Where(a => a.Measurements is not null && a.Evaluation is not null))
        {
            AutomatedEvaluationRuleResult loudness = artifact.Evaluation!.Rules.Single(r => r.RuleId == AutomatedEvaluationRuleId.Loudness);
            Assert.AreEqual(artifact.Measurements!.OutputDigitalSilence,
                loudness.ReasonCode == "source_only.output_digital_silence_measured",
                "The silence reason must match output QC measurements, not subtitle absence or render-filter silence.");
        }
        return result;
    }

    internal OutputQcResult Qc(OutputQcRequest request, decimal loudness = -16, decimal peak = -1, long offsetMilliseconds = 0,
        OutputQcMeasuredValue? loudnessValue = null, OutputQcMeasuredValue? peakValue = null,
        RationalTime? audioStart = null, RationalTime? audioEndPadding = null, int videoStartFrames = 0, int videoFrameCountDelta = 0)
    {
        StoredArtifact artifact = Generation.Store.GetArtifact(request.OutputArtifactId)!;
        RationalTime duration = request.Graph.Nodes.Single(n => n.Id == request.Graph.OutputNodeId).OutputRange.Duration;
        RationalNumber fps = request.ValidationContext.Profile.FrameRate;
        int frameCount = checked((int)(duration.Value * fps.Numerator / (duration.Rate * fps.Denominator)) + videoFrameCountDelta);
        OutputQcTimingEntry[] frames = Enumerable.Range(0, frameCount).Select(i => new OutputQcTimingEntry(i,
            (i + (long)videoStartFrames) * fps.Denominator, fps.Denominator)).ToArray();
        var videoTiming = new OutputQcStreamTiming(0, frames, new RationalTime(1, fps.Numerator),
            new RationalTime(videoStartFrames * fps.Denominator, fps.Numerator),
            new RationalTime((frameCount + (long)videoStartFrames) * fps.Denominator, fps.Numerator),
            StandardGenerationFixture.Hash("synthetic-video-timing:" + request.OutputArtifactId));
        RationalTime start = audioStart ?? new RationalTime(offsetMilliseconds, 1000);
        RationalTime end = audioEndPadding is null ? start + duration : duration + audioEndPadding.Value;
        long Samples(RationalTime t)
        {
            Assert.AreEqual(0L, t.Value * 48000 % t.Rate);
            return t.Value * 48000 / t.Rate;
        }
        var audioTiming = new OutputQcStreamTiming(1, [new OutputQcTimingEntry(0, Samples(start), Samples(end - start))],
            new RationalTime(1, 48000), start, end, StandardGenerationFixture.Hash("synthetic-audio-timing:" + request.OutputArtifactId));
        return new OutputQcResult(artifact, request, StandardGenerationFixture.Hash("synthetic-probe:" + request.OutputArtifactId),
            0, 1, null, videoTiming, audioTiming, null, new OutputQcDecodeEvidence(TimeSpan.Zero),
            new OutputQcLumaMeasurements(frames.Select(e => new OutputQcLumaFrameMeasurement(e.Sequence, e.PresentationTimestampTicks, 100m)).ToArray(),
                StandardGenerationFixture.Hash("synthetic-luma")),
            new OutputQcAudioMeasurements(loudnessValue ?? OutputQcMeasuredValue.Finite(loudness), peakValue ?? OutputQcMeasuredValue.Finite(peak), StandardGenerationFixture.Hash("synthetic-audio")));
    }
    internal IReadOnlyList<AutomatedEvaluationRuleObservation> Observe(DefaultSettingsProfile profile, DefaultSettingsSnapshot settings,
        decimal loudness, decimal peak, long offsetMilliseconds)
    {
        StandardGeneratedArtifact item = Generated.Artifacts.Single(a => a.BindingEvidence.Role == "landscape-clean");
        SourceOnlyResultMeasurements measured = SourceOnlyResultObservationBuilder.Capture(item, Qc(item.QualityControlRequest!, loudness, peak, offsetMilliseconds));
        var binding = new SourceOnlyResultBinding(Execution.Request.ProjectId, Execution.Request.ExecutionId, Execution.Request.RootJobId,
            settings.ContentDigest, item.Graph.CompositionVersionId, item.Graph.CompositionVersionDigest);
        var record = new SourceOnlyResultArtifact(SourceOnlyResultRole.LandscapeClean, null, SourceOnlyResultEvidenceStore.Reference(item.Artifact),
            binding.VersionId, binding.VersionDigest, SourceOnlyResultEvidenceStore.Reference(Generation.Original.Artifact),
            item.BindingEvidence.GraphDigest, StandardRenderGraphFactory.Digest(item.Manifest), [], null, measured, null);
        return SourceOnlyResultObservationBuilder.Build(binding, record, profile, settings, StandardGenerationFixture.Hash("test-proof"),
            true, true, false, measured.Cues, measured.ExpectedDuration, item.ValidationContext.Profile.FrameRate);
    }
    internal AutomatedEvaluationRun BuildRun(DefaultSettingsProfile profile, DefaultSettingsSnapshot settings,
        IReadOnlyList<AutomatedEvaluationRuleObservation> observations)
    {
        StandardGeneratedArtifact a = Generated.Artifacts.Single(a => a.BindingEvidence.Role == "landscape-clean");
        return new AutomatedEvaluationEngine().Evaluate(new AutomatedEvaluationRequest(EntityId<AutomatedEvaluationIdTag>.New(), Execution.Request.RootJobId,
            Execution.Request.ProjectId, a.Graph.CompositionVersionId, a.Graph.CompositionVersionDigest,
            EntityId<AutomatedEvaluationArtifactIdTag>.Parse(a.Artifact.Id.ToString()), a.Artifact.ContentDigest,
            profile.Id, profile.Version, profile.ContentDigest, settings.ContentDigest, SettingsProfileVersion.Parse(SourceOnlyOutputEvaluation.RuleSetVersion),
            SourceOnlyOutputEvaluation.EnvironmentVersion, a.Manifest.ApplicationDigest, StandardGenerationFixture.Hash("tools"), null,
            a.Manifest.TemplateVersionDigest, null, Now, Now, Now, StandardGenerationFixture.Hash("input"), a.Artifact.ContentDigest, observations));
    }
    internal StoredSourceOnlyExecution PersistResult(SourceOnlyResult result)
    {
        StoredSourceOnlyExecution current = Executions.Get(Execution.Request.ExecutionId)!;
        _ = Executions.UpdateState(current.Request.ExecutionId, current.Revision,
            result.ReconciliationRequired ? SourceOnlyExecutionPhase.ReconciliationRequired : SourceOnlyExecutionPhase.Delivering,
            result.CanonicalJson, result.VersionId, result.VersionDigest,
            result.ReconciliationRequired ? "source_only.evidence_reconciliation_required" : null);
        return new SourceOnlyExecutionStore(Generation.DatabasePath).Get(current.Request.ExecutionId)!;
    }

    // Compatibility for runtime-owned tests: still a real UpdateState/Get round trip.
    internal StoredSourceOnlyExecution WithResult(SourceOnlyResult result) => PersistResult(result);

    internal async Task SaveGeneratorCheckpointAsync()
    {
        var id = SourceOnlyPipeline.StableId<ArtifactIdTag>(Execution.Request.ExecutionId.ToString(), "generation-evidence");
        string json = HubRadio.Core.Serialization.CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "source-only.generation-evidence.v1",
            executionId = Execution.Request.ExecutionId.ToString(),
            settingsDigest = Generation.Settings.ContentDigest.Hex,
            disposition = Generated.Disposition.ToString(),
            outputs = Generated.Artifacts.Select(item => new
            {
                role = item.BindingEvidence.Role,
                artifact = new
                {
                    id = item.Artifact.Id.ToString(),
                    digest = item.Artifact.ContentDigest.Hex,
                    byteSize = item.Artifact.ByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                version = CompositionSnapshotJson.WriteVersion(item.Context.CompositionVersion),
                graph = item.Graph,
                manifest = item.Manifest,
                binding = item.BindingEvidence,
            }).ToArray(),
            Generated.Omissions,
            Generated.Warnings,
            Generated.StagingCleanupRequired,
        }));
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string path = Path.Combine(Generation.Root, "projects", Generation.Project.Id.ToString(), "staging", "generator-checkpoint.json");
        await File.WriteAllBytesAsync(path, bytes);
        _ = await Generation.Store.PromoteAsync(new ArtifactPromotionRequest(id, Generation.Project.Id, ArtifactKind.Analysis,
            path, $"source-only/{id}.generation-evidence.json", ContentDigest.Compute(bytes), bytes.LongLength, Now));
    }

    // Valid PNG encoding of synthetic black pixels; not an FFmpeg/source-frame visual proof.
    internal static byte[] PngBytes(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] data)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            png.Write(length);
            byte[] type = Encoding.ASCII.GetBytes(name);
            png.Write(type); png.Write(data);
            uint crc = 0xffffffffu;
            foreach (byte value in type.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
            BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xffffffffu);
            png.Write(length);
        }
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[1 + width * 3];
            for (int y = 0; y < height; y++) zlib.Write(row);
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return png.ToArray();
    }
    public void Dispose() => Generation.Dispose();
}