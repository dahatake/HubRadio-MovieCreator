using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Security;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

/// <summary>
/// Real local ingest/encoder/QC with a TEST-ONLY known Machine transcript over
/// synthetic video and sine audio. No recognition, model, GUI, user data, network,
/// approval, delivery or full source-only E2E is exercised or claimed.
/// Select exactly this method in a fresh dedicated host; the second environment
/// guard is the caller's isolation attestation, not discovery of the test filter.
/// Keep this host under an external process-tree watchdog. All run evidence stays.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RealProjectedSubtitleRenderTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(120);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static int claimed;
    // Strong retention on incomplete shutdown: never restore process-global paths
    // while owned work could still be using them. Only the dedicated host may exit.
    private static object? retainedOwnership;
    private const string MachineText = "テスト字幕";
    private const string EditedText = "訂正テスト字幕";
    private const string FontDigest = "dff723ba59d57d136764a04b9b2d03205544f7cd785a711442d6d2d085ac5073";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("LivePipeline")]
    public async Task PersistedSubtitleEdit_FreshReplayChangedSettings_ActuallyRendersAndPassesOutputQc()
    {
        // Absolutely no path resolution, file creation or process before opt-in.
        if (Environment.GetEnvironmentVariable("HUBRADIO_RUN_LIVE_PIPELINE_TESTS") != "1")
        {
            Assert.Inconclusive("projected_render.opt_in_required.HUBRADIO_RUN_LIVE_PIPELINE_TESTS");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("projected_render.windows_acl_required");
            return;
        }
        Assert.AreEqual(nameof(PersistedSubtitleEdit_FreshReplayChangedSettings_ActuallyRendersAndPassesOutputQc), TestContext.TestName);
        Assert.AreEqual(TestContext.TestName, Environment.GetEnvironmentVariable("HUBRADIO_LIVE_PIPELINE_SINGLE_TEST"),
            "projected_render.dedicated_single_test_host_required");
        Assert.AreEqual(0, Interlocked.CompareExchange(ref claimed, 1, 0), "projected_render.host_already_used");
        Assert.IsNull(retainedOwnership);

        string repository = RepositoryRoot();
        string parent = SourceOnlySourceLocator.RequirePath(Path.Combine(repository, "artifacts", "validation"), directory: true);
        string root = Path.Combine(parent, "pr-" + Guid.NewGuid().ToString("N"));
        string actor = new WindowsActorProvider().GetCurrentSid();
        WorkspaceAclResult acl = WorkspaceAcl.CreateNew(root, actor);
        Assert.IsTrue(acl.OwnerMatchesCurrentActor && acl.Protected && acl.InheritanceConfigured && acl.ExplicitRuleCount == 3);
        string log = Path.Combine(root, "milestones.log");
        TestContext.WriteLine("projected_render.root=" + Path.GetFileName(root));
        Dictionary<string, string?> isolated = IsolatedEnvironment(root);
        Dictionary<string, string?> originals = isolated.Keys.ToDictionary(name => name,
            name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process), StringComparer.OrdinalIgnoreCase);
        var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(420));
        Task? body = null;
        bool completed = false;
        try
        {
            foreach ((string name, string? value) in isolated)
            {
                if (value is not null && Path.IsPathFullyQualified(value)) Directory.CreateDirectory(value);
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            }
            body = Task.Run(() => ScenarioAsync(repository, root, actor, log, deadline.Token));
            await body.WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            completed = true;
        }
        finally
        {
            Task stopping = Task.Run(async () =>
            {
                await deadline.CancelAsync();
                if (body is not null)
                {
                    try { await body; }
                    catch (Exception) { /* Observe the ORIGINAL task, not just the timed wait. */ }
                }
            });
            try { await stopping.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception)
            {
                retainedOwnership = new { deadline, body, stopping, originals, isolated };
                TestContext.WriteLine("projected_render.cleanupIncomplete.external_host_watchdog_required");
                throw new AssertFailedException("projected_render.cleanupIncomplete");
            }
            // ProcessRunner cancels/kills/waits only its own Windows Job Object.
            // No process enumeration, global pool clearing or recursive deletion.
            foreach ((string name, string? value) in originals)
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            deadline.Dispose();
            File.AppendAllText(log, completed ? "scenario.completed\n" : "scenario.failed\n", Utf8);
            TestContext.AddResultFile(log);
        }
    }

    private static async Task ScenarioAsync(string repository, string root, string actor, string log, CancellationToken token)
    {
        var runner = new ProcessRunner();
        var verifier = new BinaryVerifier();
        string tools = Path.Combine(repository, "artifacts", "portable", "osl009-payload", "Tools");
        string ffmpeg = Path.Combine(tools, "ffmpeg.exe");
        string ffprobe = Path.Combine(tools, "ffprobe.exe");
        string fontPath = Path.Combine(repository, "artifacts", "inputs", "fonts", "verified-2.004-7388bda58dd8", "NotoSansJP-Regular.otf");
        var tool = new FfmpegToolBinding(new BinaryVerificationRequest("ffmpeg", ffmpeg,
            new ContentDigest(FfmpegCompiler.FixedFfmpegSha256), FfmpegCompiler.FixedFfmpegByteSize));
        var font = new FfmpegFontBinding(new RenderFontEvidence("Noto Sans JP", new ContentDigest(FontDigest)),
            new BinaryVerificationRequest("projected-render.font", fontPath, new ContentDigest(FontDigest), 4533028));
        BinaryVerificationRequest[] pins = [tool.VerificationRequest, font.VerificationRequest,
            new("projected-render.ffprobe", ffprobe, new ContentDigest(FfprobeClient.CanonicalFfprobeSha256), FfprobeClient.CanonicalFfprobeByteSize)];
        var held = new List<FileStream>();
        try
        {
            foreach (BinaryVerificationRequest pin in pins)
            {
                _ = SourceOnlySourceLocator.RequirePath(pin.FilePath, directory: false);
                held.Add(new FileStream(pin.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read));
                Assert.IsTrue((await verifier.VerifyAsync(pin, token)).IsVerified);
            }
            File.AppendAllText(log, "pinned_lgpl_tools_and_font.verified\n", Utf8);
            string source = Path.Combine(root, "synthetic.mp4");
            ProcessRunResult encoded = await runner.RunAsync(new ProcessRunRequest(ffmpeg,
            [
                "-hide_banner", "-nostdin", "-v", "error", "-n", "-filter_threads", "1",
                "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
                "-map", "0:v:0", "-map", "1:a:0", "-t", "4", "-map_metadata", "-1", "-map_chapters", "-1",
                "-c:v", "h264_mf", "-b:v", "2000000", "-profile:v", "100", "-threads:v", "1",
                "-pix_fmt", "yuv420p", "-r", "30", "-fps_mode", "cfr", "-g", "30", "-bf", "0",
                "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv",
                "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "1", "-threads:a", "1",
                "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", "-movflags", "+faststart", source,
            ], root, ProcessTimeout, maxCapturedBytes: 64 * 1024), token);
            Assert.AreEqual(0, encoded.ExitCode, "projected_render.synthetic_encode_failed");
            Assert.IsFalse(encoded.Truncated);

            string database = Path.Combine(root, "store.db");
            Assert.AreEqual(13, DatabaseMigrator.Migrate(database, Path.Combine(root, "backups")).SchemaVersion);
            var projects = new ProjectStore(database);
            var versions = new VersionService(projects);
            var store = new ArtifactStore(database, root);
            var registry = new AssetCandidateRegistry();
            var projectId = EntityId<ProjectIdTag>.New();
            EntityId<AssetIdTag>? candidateId = registry.RegisterSelection(source);
            Assert.IsNotNull(candidateId);
            StagedMediaImport staged = await new MediaImporter(registry, root).CopyToStagingAsync(
                candidateId, projectId, EntityId<JobIdTag>.New(), token);
            MediaProbeResult probe = await new FfprobeClient(runner, verifier, ffprobe).ProbeAsync(staged, ProcessTimeout, token);
            PtsIndex index = await new PtsIndexer(runner, verifier, ffprobe).IndexAsync(staged, probe, ProcessTimeout, token);
            MediaInfo media = IngestValidator.Validate(probe, new IngestValidationRequirements());
            DecodeValidationResult decode = await new DecodeValidator(runner, verifier, ffmpeg).ValidateAsync(staged, probe, index, ProcessTimeout, token);
            Assert.AreEqual(new TimeRange(default, new RationalTime(4, 1)), index.PresentationRange);
            Assert.HasCount(120, index.Entries);
            Assert.AreEqual(1280, media.PrimaryVideoStream.Width);
            Assert.AreEqual(720, media.PrimaryVideoStream.Height);
            Assert.AreEqual(48000, media.Streams.Single(stream => stream.Kind == MediaStreamType.Audio).SampleRate);
            Assert.AreEqual(1, media.Streams.Single(stream => stream.Kind == MediaStreamType.Audio).Channels);
            var asset = new Asset(staged.CandidateId, staged.SourceFileName, "video/mp4", staged.ContentDigest, staged.ByteSize,
                new RightsRecord(RightsStatus.Unconfirmed, null, "Test-only synthetic media; no approval", false));
            var project = new Project(projectId, SourceOnlyProjectCreationContract.DeriveContentName(asset.FileName),
                "Synthetic subtitle integration", "Test runner", PublicationScope.InternalOnly, null, [asset], []);
            DateTimeOffset at = DateTimeOffset.UtcNow;
            string projectJson = HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(project);
            projects.CreateProject(project, projectJson, at);
            ImportedOriginal original = await new IngestService(store, verifier).PromoteVerifiedOriginalAsync(
                EntityId<ArtifactIdTag>.New(), asset, staged, probe, media, decode, token);
            File.AppendAllText(log, "actual_ingest.original_committed\n", Utf8);
            await SaveAsync(root, "source-pts.json", index.CanonicalJson, token);
            await SaveAsync(root, "source-decode.json", JsonSerializer.Serialize(decode), token);

            // Explicit known test input, NOT recognition of sine audio. Never mint
            // a Human transcript to legitimize manual subtitle changes.
            var revisionId = EntityId<TranscriptRevisionIdTag>.New();
            var word = new Token(EntityId<TranscriptTokenIdTag>.New(), MachineText,
                new TimeRange(new RationalTime(1, 1), new RationalTime(2, 1)), null);
            var span = new SourceSpan(EntityId<SourceSpanIdTag>.New(), asset.Id,
                "audio:" + decode.AudioStreamIndex!.Value.ToString(CultureInfo.InvariantCulture), index.PresentationRange,
                asset.ContentDigest, revisionId, [word.Id]);
            var transcript = new TranscriptRevision(revisionId, null, TranscriptChangeSource.Machine, at,
                [new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), span.Id, EntityId<TranscriptSpeakerIdTag>.New(),
                    "Synthetic known transcript", MachineText, span.Range, null,
                    [new Sentence(EntityId<TranscriptSentenceIdTag>.New(), MachineText, [word])])]);
            string machineJson = TranscriptSnapshotJson.WriteRevision(transcript);
            ContentDigest machineDigest = Hash(machineJson);
            string machinePath = Path.Combine(root, "synthetic-known-machine-transcript.json");
            await SaveAsync(root, Path.GetFileName(machinePath), machineJson, token);
            using var heldMachine = new FileStream(machinePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var model = new RenderModelEvidence("test-only", "known-transcript-no-recognition", "1", machineDigest);
            var toolEvidence = new RenderToolEvidence("ffmpeg", DecodeValidator.CanonicalPackageVersion,
                "Pinned LGPL local integration", tool.VerificationRequest.ExpectedDigest, null);
            CompositionElement[] elements = [new(EntityId<CompositionElementIdTag>.New(), index.PresentationRange,
                OriginRef.FromSourceSpan(span.Id), sourceRange: index.PresentationRange)];
            var baseline = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), projectId, null, actor, at,
                "Full synthetic source baseline", RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements), elements);
            projects.AppendCompositionVersion(baseline, CompositionSnapshotJson.WriteVersion(baseline));
            var working = new WorkingCopy(EntityId<WorkingCopyIdTag>.New(), projectId, baseline.Id, at, at, elements);
            projects.CreateWorkingCopy(working, CompositionSnapshotJson.WriteWorkingCopy(working));
            var state = new StandardProjectStoreState(0, Hash(projectJson), at);
            DefaultSettingsProfile profile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(
                Path.Combine(repository, "src", "HubRadio.App", "DefaultSettings", "Profiles", "1.0.0.json")));
            DefaultSettingsSnapshot settings = profile.CreateSnapshot();
            var geometry = new SourceDisplayGeometry(1280, 720);
            StandardCompositionPreparation first = Prepare(repository, versions, project, baseline, working, 0, state,
                profile, settings, transcript, span, geometry, actor, toolEvidence, model, token);
            SourceOnlyGeneratedSubtitleRuntimeEvidence initial = Build(first, transcript, span, profile, settings, font, actor);
            var generator = new StandardArtifactGenerator(store, versions, runner, root, ffprobe);
            var qc = new OutputQc(store, runner, verifier, ffprobe, ffmpeg);
            StandardArtifactGenerationRequest Request(StandardCompositionPreparation plan, DefaultSettingsSnapshot snapshot, StandardSubtitleInput subtitles) =>
                new(plan, original, index, geometry, profile, snapshot, tool, toolEvidence, model, font, subtitles, [],
                    "test-only-projected-render", Hash("test-only-projected-render-v1"), ProcessTimeout);
            StandardArtifactGenerationResult before = await generator.GenerateAsync(Request(first, settings, initial.Build.Subtitles), token);
            var beforeFrames = await ValidateRenderAsync("before", before, settings, MachineText, store, qc, runner, ffmpeg, root, token);
            File.AppendAllText(log, "before.actual_render_and_qc.completed\n", Utf8);
            var oldFiles = before.Artifacts.Select(item => (item.Artifact.Id, item.Artifact.ContentDigest, item.Artifact.ByteSize)).ToArray();
            string oldVersion = projects.GetCompositionVersion(first.Child.Id)!.CanonicalJson;

            var layoutProfile = SubtitleEditProjection.TargetLayoutProfile(initial);
            var registered = new LayoutSubtitleBridgeSnapshot(projectId, 0, first.Child, initial.Layout, initial.Profile.Width,
                initial.Profile.Height, [new LayoutSubtitleSourceBinding(asset.Id, OriginRef.FromAsset(asset.Id), geometry)], [],
                initial.Histories.Select(history => new LayoutSubtitleCueBinding(history, initial.Layout, layoutProfile, [], [])).ToArray(), [],
                [new LayoutSubtitleDisplayBinding(initial.Build.Subtitles.DisplayProfile, [])], subtitleOnly: true);
            // Reuse this EXACT root/variant key across fresh service registration.
            ContentDigest registrationKey = SubtitleEditProjection.GeneratedDigest(initial);
            var edits = new DurableSubtitleEditService(new SubtitleEditStore(database));
            edits.RegisterGenerated(registered, registrationKey, token);
            SubtitleCueRevision cue = initial.Histories.Single().CurrentRevision;
            BridgeRequest edit = BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
            {
                projectId = projectId.ToString(),
                expectedRevision = 0,
                expectedVersionId = first.Child.Id.ToString(),
                expectedVersionDigest = first.Child.ContentDigest.Hex,
                operation = new
                {
                    kind = "editSubtitle",
                    cueId = cue.Snapshot.Cue.Id.ToString(),
                    expectedRevisionId = cue.Id.ToString(),
                    expectedRevisionDigest = cue.ContentDigest.Hex,
                    reason = "Test-only persisted manual correction",
                    edit = new { kind = "text", text = EditedText }
                },
            }));
            LayoutSubtitleBridgeOperationResult ack = edits.Execute(edit, actor, token);
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, ack.Status);
            Assert.IsNotNull(ack.Snapshot);
            var fresh = new DurableSubtitleEditService(new SubtitleEditStore(database));
            fresh.RegisterGenerated(registered, registrationKey, token);
            SubtitleEditCapture capture = fresh.Capture(projectId, [first.Child.Id], token).Single();
            Assert.AreEqual(1, capture.Sequence);
            Assert.AreEqual(ack.Snapshot.CompositionVersion.Id, capture.HeadVersionId);
            Assert.AreEqual(EditedText, capture.Head.Subtitles.Single().History.CurrentRevision.Snapshot.Text);
            Assert.AreNotEqual(MachineText, capture.Head.Subtitles.Single().History.CurrentRevision.Snapshot.Text);
            await SaveAsync(root, "saved-edit.json", capture.Edits.Single().CommitCanonicalJson, token);
            File.AppendAllText(log, "manual_edit.fresh_service_replayed\n", Utf8);

            DefaultSettingsSnapshot changed = profile.CreateSnapshot(settings.Values.Select(item => item.Key == "subtitles.cueMaximumDuration"
                ? new DefaultSettingSelection(item.Key, DefaultSettingValue.FromInteger(2)) : item).ToArray());
            Assert.AreNotEqual(settings.ContentDigest, changed.ContentDigest);
            working = new WorkingCopy(working.Id, projectId, first.Child.Id, working.CreatedAt, first.Child.CreatedAt, first.Child.Elements);
            StandardCompositionPreparation second = Prepare(repository, versions, project, first.Child, working, 1, state,
                profile, changed, transcript, span, geometry, actor, toolEvidence, model, token);
            Assert.AreNotEqual(first.Child.Id, second.Child.Id);
            Assert.AreEqual(first.Child.Id, second.Child.ParentVersionId);
            SourceOnlyGeneratedSubtitleRuntimeEvidence target = Build(second, transcript, span, profile, changed, font, actor);
            SubtitleEditProjectionResult projection = SubtitleEditProjection.Apply(capture, target, actor, DateTimeOffset.UtcNow, token);
            Assert.AreEqual(1, projection.Evidence.ProjectedCueCount);
            Assert.IsNotEmpty(projection.Evidence.TrustedEditorialRecordIds);
            Assert.AreEqual(0, projection.Evidence.NewHumanActionCount);
            Assert.AreEqual(changed.ContentDigest, projection.Output.Build.SettingsDigest);
            await SaveAsync(root, "projection-proof.json", projection.Evidence.CanonicalJson, token);
            StandardArtifactGenerationResult after = await generator.GenerateAsync(Request(second, changed,
                projection.Output.Build.Subtitles.WithProjection(projection)), token);
            var afterFrames = await ValidateRenderAsync("after", after, changed, EditedText, store, qc, runner, ffmpeg, root, token);
            // Require a stable decoded clean control; encoder variation must not be
            // mistaken for evidence that the persisted subtitle edit changed pixels.
            Assert.AreEqual(beforeFrames.TimeSeconds, afterFrames.TimeSeconds, "projected_render.cue_sample_time_changed");
            Assert.AreEqual(beforeFrames.Clean, afterFrames.Clean, "projected_render.clean_pixels_changed");
            Assert.AreNotEqual(beforeFrames.Burn, afterFrames.Burn, "projected_render.edited_burn_in_pixels_unchanged");
            Assert.IsTrue(after.Artifacts.All(item => item.SubtitleProjection?.ContentDigest == projection.Evidence.ContentDigest));
            foreach (var old in oldFiles)
            {
                StoredArtifact rehashed = await store.ValidateRegisteredAsync(old.Id, token);
                Assert.AreEqual(old.ContentDigest, rehashed.ContentDigest);
                Assert.AreEqual(old.ByteSize, rehashed.ByteSize);
                Assert.IsFalse(after.Artifacts.Any(item => item.Artifact.Id == old.Id));
            }
            Assert.AreEqual(oldVersion, projects.GetCompositionVersion(first.Child.Id)!.CanonicalJson);
            Assert.AreEqual(machineJson, TranscriptSnapshotJson.WriteRevision(transcript));
            Assert.AreEqual(machineDigest, ContentDigest.FromBytes(await SHA256.HashDataAsync(heldMachine, token)));
            Assert.AreEqual(TranscriptChangeSource.Machine, transcript.ChangeSource);
            Assert.IsNull(transcript.ParentRevisionId);
            Assert.AreEqual(original.Artifact.ContentDigest, (await store.ValidateRegisteredAsync(original.Artifact.Id, token)).ContentDigest);
            File.AppendAllText(log, "after.actual_render_and_qc.originals_unchanged\n", Utf8);
        }
        finally { foreach (FileStream stream in held) stream.Dispose(); }
    }

    private static StandardCompositionPreparation Prepare(string repository, VersionService versions, Project project,
        CompositionVersion parent, WorkingCopy working, int revision, StandardProjectStoreState state,
        DefaultSettingsProfile profile, DefaultSettingsSnapshot settings, TranscriptRevision transcript, SourceSpan span,
        SourceDisplayGeometry geometry, string actor, RenderToolEvidence tool, RenderModelEvidence model, CancellationToken token)
    {
        byte[] landscape = File.ReadAllBytes(Path.Combine(repository, "src", "HubRadio.App", "Profiles", "16x9", "long-form.json"));
        byte[] vertical = File.ReadAllBytes(Path.Combine(repository, "src", "HubRadio.App", "Profiles", "9x16", "vertical-short.json"));
        BundledRenderProfileDefinition render = StandardRenderProfileFactory.CreateLandscape(profile, settings,
            BundledRenderProfileLoader.Load(landscape), new RationalNumber(30, 1));
        AppliedBrandTemplate template = new BrandTemplateVersion(EntityId<SeriesIdTag>.New(), render.TemplateReference,
            BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
        LongFormElementInput Omit(LongFormElementKind kind) => new(EntityId<LongFormElementIdTag>.New(), kind, false, null, null, [], []);
        var request = new LongFormCompositionRequest(EntityId<LongFormPlanIdTag>.New(), project,
            new LongFormProvenanceSnapshot(project.Id, transcript.Id, Hash(TranscriptSnapshotJson.WriteRevision(transcript)), [span], []),
            template, render, LongFormDestination.InternalDistribution, null,
            Omit(LongFormElementKind.PreRollMargin), Omit(LongFormElementKind.Opening), Omit(LongFormElementKind.ProgramName),
            Omit(LongFormElementKind.EpisodeNumber), Omit(LongFormElementKind.EpisodeTitle), Omit(LongFormElementKind.PerformerIntroduction),
            Omit(LongFormElementKind.PublicationScopeIndicator), Omit(LongFormElementKind.Body), [], Omit(LongFormElementKind.Summary),
            [new LongFormCallToActionInput(LongFormDestination.InternalDistribution, Omit(LongFormElementKind.CallToAction))],
            Omit(LongFormElementKind.Ending), Omit(LongFormElementKind.PostRollMargin));
        MusicPlanningResult music = new MusicPlanningService().Plan(MusicCatalog.CreateUnavailable(MusicLibraryState.Absent,
            MusicCatalogWarning.LibraryAbsent), [], MusicPlanningOptions.Default);
        // The actual planner requires fixed short/thumbnail candidate counts; zero
        // is NOT a supported override. Empty candidates and absent preview evidence
        // produce explicit omissions, not invented outputs or false disabled flags.
        StandardCompositionPreparation result = StandardCompositionOrchestrator.Apply(versions,
            new StandardCompositionOrchestrationRequest(parent, working, revision, state, profile, settings, landscape, vertical,
                request, EntityId<TimelineMapIdTag>.New(), geometry, new RationalNumber(30, 1), tool.ExecutableDigest,
                model.WeightsDigest, music, [], [], [], actor, DateTimeOffset.UtcNow, "Test-only standard render", thumbnailEvidence: null), token).Preparation;
        Assert.IsEmpty(result.Plan.ShortForms);
        Assert.IsEmpty(result.Plan.Thumbnails);
        Assert.IsNotEmpty(result.Plan.Omissions);
        Assert.AreEqual(settings.ContentDigest, StandardRenderGraphFactory.InputDigest(result.Plan, StandardPlanInputKind.SettingsSnapshot));
        return result;
    }

    private static SourceOnlyGeneratedSubtitleRuntimeEvidence Build(StandardCompositionPreparation plan, TranscriptRevision transcript,
        SourceSpan span, DefaultSettingsProfile profile, DefaultSettingsSnapshot settings, FfmpegFontBinding font, string actor)
    {
        LayoutSpec layout = StandardRenderGraphFactory.GetLayout(plan.Plan);
        RenderProfile render = StandardRenderGraphFactory.GetProfile(plan.Plan, false);
        StandardSubtitleBuildResult build = StandardSubtitleBuilder.BuildWithEvidence(plan.Child, plan.ChildTimelineMap, layout, render,
            transcript, Hash(TranscriptSnapshotJson.WriteRevision(transcript)), [span], profile, settings, font.Evidence, actor, plan.Child.CreatedAt);
        Assert.IsFalse(build.HasUnresolvedCues);
        Assert.HasCount(1, build.Subtitles.Revisions);
        return new(plan.Child, plan.ChildTimelineMap, layout, render, build, build.Subtitles.Revisions.Select(item =>
            new SubtitleCueHistory(build.Subtitles.Candidates.Single(candidate => candidate.Id == item.SourceCandidateId), [item])).ToArray());
    }

    private static async Task<(ContentDigest Clean, ContentDigest Burn, decimal TimeSeconds)> ValidateRenderAsync(
        string phase, StandardArtifactGenerationResult result, DefaultSettingsSnapshot settings,
        string expectedText, ArtifactStore store, OutputQc qc, ProcessRunner runner, string ffmpeg, string root, CancellationToken token)
    {
        await SaveAsync(root, phase + "-generation.json", JsonSerializer.Serialize(result), token);
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition, $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.IsFalse(result.StagingCleanupRequired);
        Assert.HasCount(4, result.Artifacts);
        StandardGeneratedArtifact clean = result.Artifacts.Single(item => item.TypedArtifact.Role == StandardArtifactRole.LandscapeCleanVideo);
        StandardGeneratedArtifact burn = result.Artifacts.Single(item => item.TypedArtifact.Role == StandardArtifactRole.LandscapeBurnInVideo);
        Assert.IsTrue(burn.Context.Profile.SubtitleBurnIn);
        Assert.IsTrue(burn.Graph.Nodes.Any(node => node.Kind == RenderNodeKind.Subtitle));
        Assert.HasCount(1, burn.SubtitleFiles);
        foreach (StandardGeneratedArtifact item in result.Artifacts)
        {
            Assert.AreEqual(settings.ContentDigest, item.BindingEvidence.SettingsSnapshotDigest);
            Assert.HasCount(1, item.DisplayCues);
            Assert.AreEqual(expectedText, item.DisplayCues.Single().Text);
            Assert.AreEqual(clean.BindingEvidence.DisplayCueDigest, item.BindingEvidence.DisplayCueDigest);
            if (item.QualityControlRequest is { } request)
            {
                OutputQcResult measured = await qc.RunAsync(request, token);
                await SaveAsync(root, phase + "-" + item.BindingEvidence.Role + "-qc.json", JsonSerializer.Serialize(measured), token);
                Assert.IsTrue(measured.MachineChecksPassed && measured.Decode.DecodedSuccessfully);
                Assert.AreEqual(item.Artifact.Id, measured.ArtifactId);
                Assert.AreEqual(item.Artifact.ContentDigest, measured.ContentDigest);
                Assert.AreEqual(new RationalTime(4, 1), measured.VideoTiming.ObservedDuration);
                Assert.AreEqual(120, measured.VideoTiming.ObservedFrameCount);
                Assert.IsNotNull(measured.AudioMeasurements);
                Assert.AreEqual(OutputQcMeasuredValueKind.Finite, measured.AudioMeasurements.IntegratedLoudnessLufs.Kind);
                Assert.AreEqual(OutputQcMeasuredValueKind.Finite, measured.AudioMeasurements.TruePeakDbfs.Kind);
                Assert.IsFalse(measured.CanProceedToDelivery);
                Assert.IsTrue(measured.HumanReviewRequired);
            }
        }
        Assert.IsNotNull(clean.QualityControlRequest);
        Assert.IsNotNull(burn.QualityControlRequest);
        byte[] ass = burn.SubtitleFiles.Single().File.GetUtf8Bytes();
        StringAssert.Contains(Utf8.GetString(ass), expectedText);
        await SaveAsync(root, phase + ".ass", Utf8.GetString(ass), token);
        foreach ((StandardArtifactRole role, byte[] expected) in new[]
        {
            (StandardArtifactRole.LandscapeSubRip, SubRipSubtitleWriter.Write(burn.DisplayCues).GetUtf8Bytes()),
            (StandardArtifactRole.LandscapeWebVtt, WebVttSubtitleWriter.Write(burn.DisplayCues).GetUtf8Bytes()),
        })
        {
            StoredArtifact sidecar = result.Artifacts.Single(item => item.TypedArtifact.Role == role).Artifact;
            byte[] bytes = await File.ReadAllBytesAsync(store.GetValidatedRegisteredArtifactPath(sidecar.Id), token);
            CollectionAssert.AreEqual(expected, bytes);
            StringAssert.Contains(Utf8.GetString(bytes), expectedText);
        }
        // Small decoded lower-frame samples at an ACTUAL displayed-cue midpoint.
        // This samples decoded pixels; it is not OCR or human approval.
        TimeRange range = burn.DisplayCues.Single().Cue.Range;
        decimal seconds = (decimal)range.Start.Value / range.Start.Rate + (decimal)range.Duration.Value / range.Duration.Rate / 2;
        var digests = new List<ContentDigest>();
        foreach (StandardGeneratedArtifact item in new[] { clean, burn })
        {
            string frame = Path.Combine(root, phase + "-" + item.BindingEvidence.Role + ".gray");
            ProcessRunResult sampled = await runner.RunAsync(new ProcessRunRequest(ffmpeg,
                ["-hide_banner", "-nostdin", "-v", "error", "-n", "-ss", seconds.ToString(CultureInfo.InvariantCulture),
                    "-i", store.GetValidatedRegisteredArtifactPath(item.Artifact.Id), "-map", "0:v:0", "-an", "-sn",
                    "-frames:v", "1", "-vf", "crop=iw:ih/4:0:ih*3/4,scale=320:45", "-pix_fmt", "gray", "-f", "rawvideo", frame],
                root, ProcessTimeout, maxCapturedBytes: 64 * 1024), token);
            Assert.AreEqual(0, sampled.ExitCode);
            Assert.IsFalse(sampled.Truncated);
            byte[] pixels = await File.ReadAllBytesAsync(frame, token);
            Assert.HasCount(320 * 45, pixels);
            digests.Add(ContentDigest.Compute(pixels));
        }
        Assert.AreNotEqual(digests[0], digests[1], "projected_render.burn_in_pixels_missing");
        await SaveAsync(root, phase + "-frame-hashes.json", JsonSerializer.Serialize(new { displayedTimeSeconds = seconds, digests }), token);
        return (digests[0], digests[1], seconds);
    }

    private static async Task SaveAsync(string root, string name, string text, CancellationToken token)
    {
        await using var stream = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(Utf8.GetBytes(text), token);
        await stream.FlushAsync(token);
    }

    private static Dictionary<string, string?> IsolatedEnvironment(string root)
    {
        string home = Path.Combine(root, "Home");
        string cache = Path.Combine(root, "Cache");
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = Path.Combine(root, "Temp"),
            ["TMP"] = Path.Combine(root, "Temp"),
            ["TMPDIR"] = Path.Combine(root, "Temp"),
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["APPDATA"] = Path.Combine(home, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local"),
            ["XDG_CACHE_HOME"] = cache,
            ["HF_HOME"] = Path.Combine(cache, "hf"),
            ["HF_HUB_CACHE"] = Path.Combine(cache, "hf", "hub"),
            ["HUGGINGFACE_HUB_CACHE"] = Path.Combine(cache, "hf", "hub"),
            ["HF_ASSETS_CACHE"] = Path.Combine(cache, "hf", "assets"),
            ["HF_XET_CACHE"] = Path.Combine(cache, "hf", "xet"),
            ["TRANSFORMERS_CACHE"] = Path.Combine(cache, "hf", "hub"),
            ["TORCH_HOME"] = Path.Combine(cache, "torch"),
            ["HF_HUB_OFFLINE"] = "1",
            ["HF_DATASETS_OFFLINE"] = "1",
            ["TRANSFORMERS_OFFLINE"] = "1",
            ["HF_HUB_DISABLE_TELEMETRY"] = "1",
            ["FFREPORT"] = null,
        };
    }

    private static ContentDigest Hash(string value) => ContentDigest.Compute(Utf8.GetBytes(value));
    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HubRadio.slnx"))) current = current.Parent;
        return (current ?? throw new DirectoryNotFoundException("projected_render.repository_missing")).FullName;
    }
}