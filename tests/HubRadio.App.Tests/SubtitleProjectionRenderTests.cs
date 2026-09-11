using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Analysis;
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
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

/// <summary>
/// Machine-transcript fixture, real builder/journal/projection and subtitle writers.
/// Generator tests use explicit synthetic media behind the existing compiler/process
/// seams: no encoder, output QC, approval or Human transcript is claimed here.
/// These tests were added without running terminals, builds or tests.
/// </summary>
[TestClass]
public sealed class SubtitleProjectionRenderTests
{
    private const string Editor = "S-1-5-21-111-222-333-1001";
    private static readonly DateTimeOffset At = new(2038, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [TestMethod]
    public async Task PublicConstructorAndUnprovedInputKeepExactLegacyShapeAndV2Digest()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        var target = fixture.Generate();
        StandardSubtitleInput input = target.Output.Build.Subtitles;
        StandardRenderGraphOutput graph = fixture.Graph(target.Preparation, input, false);
        Assert.IsNull(input.Projection);
        Assert.IsNull(graph.SubtitleProjection);
        Assert.HasCount(1, typeof(StandardSubtitleInput).GetConstructors());
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(input));
        CollectionAssert.AreEquivalent(new[] { "Revisions", "Candidates", "DisplayProfile", "AssProfile", "Warnings" },
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(StandardRenderGraphFactory.Digest(new
        {
            domain = "standard-subtitle-input-v2",
            input.DisplayProfile,
            input.AssProfile,
            input.Warnings,
            revisions = input.Revisions.Select(item => new
            {
                item.Id,
                item.ContentDigest,
                item.SourceCandidateId,
                item.SourceRevisionId,
                item.SourceRevisionDigest,
            }).ToArray(),
        }), graph.SubtitleConfigurationDigest);
    }

    [TestMethod]
    public async Task BoundManualTextTimingStyleAndPlacementUseTheSameActualCuesInEveryWriter()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string transcriptBefore = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);
        fixture.Edit(new { kind = "text", text = "訂正した字幕。" });
        fixture.Edit(new { kind = "timing", outputRange = BridgeRange(new TimeRange(new RationalTime(3, 2), new RationalTime(2, 1))) });
        fixture.Edit(new { kind = "style", style = "quote" });
        fixture.Edit(fixture.Placement());
        var target = fixture.Generate();
        SubtitleEditProjectionResult result = fixture.Project(target.Output);
        StandardSubtitleInput input = result.Output.Build.Subtitles.WithProjection(result);
        StandardRenderGraphOutput clean = fixture.Graph(target.Preparation, input, false);
        StandardRenderGraphOutput burn = fixture.Graph(target.Preparation, input, true);
        SubtitleCueRevision revision = result.Output.Histories[0].CurrentRevision;
        Assert.AreEqual("訂正した字幕。", revision.Snapshot.Text);
        Assert.AreEqual(new TimeRange(new RationalTime(3, 2), new RationalTime(2, 1)), revision.Snapshot.Cue.Range);
        Assert.AreEqual(target.Output.Histories[0].SourceCandidateId, revision.SourceCandidateId);
        Assert.AreNotEqual(target.Output.Histories[0].CurrentRevision.Snapshot.Cue.Range, revision.Snapshot.Cue.Range);
        Assert.AreEqual(SubtitleCueStyle.Quote, revision.Snapshot.Style);
        Assert.AreEqual(new RationalNumber(3, 20), revision.Snapshot.ResolvedPlacement!.Rect.X);
        Assert.AreEqual("訂正した字幕", burn.DisplayCues[0].Text);
        Assert.AreEqual(revision.Snapshot.Cue, burn.DisplayCues[0].Cue);
        Assert.AreEqual(StandardRenderGraphFactory.Digest(clean.DisplayCues), StandardRenderGraphFactory.Digest(burn.DisplayCues));
        Assert.AreEqual(clean.SubtitleConfigurationDigest, burn.SubtitleConfigurationDigest);
        Assert.AreSame(result.Evidence, clean.SubtitleProjection);
        Assert.AreSame(result.Evidence, burn.SubtitleProjection);
        CollectionAssert.AreEquivalent(target.Preparation.Plan.LongForm.Provenance.TrustedEditorialRecordIds
            .Concat(result.Evidence.TrustedEditorialRecordIds).Distinct().ToArray(), burn.ValidationContext.EditorialRecordIds.ToArray());
        Assert.IsTrue(RenderGraphValidator.Validate(burn.Graph, burn.ValidationContext).IsValid);
        foreach (SubtitleFileCue cue in burn.DisplayCues)
        {
            SubtitleCueRevision current = input.Revisions.Single(item => item.Snapshot.Cue.Id == cue.Cue.Id);
            StandardSubtitleRenderFile ass = burn.SubtitleFiles.Single(file => file.Input.Origin == current.Snapshot.TextOrigin);
            SubtitleExportFile expected = StandardSubtitlePlacement.Write(cue, input.AssProfile, current.Snapshot.LayoutResolution);
            CollectionAssert.AreEqual(expected.GetUtf8Bytes(), ass.File.GetUtf8Bytes());
            Assert.AreEqual(ContentDigest.Compute(ass.File.GetUtf8Bytes()), ass.Input.ContentDigest);
            Assert.AreEqual(ass.File.GetUtf8Bytes().LongLength, ass.Input.ByteSize);
        }
        string srt = Encoding.UTF8.GetString(SubRipSubtitleWriter.Write(clean.DisplayCues).GetUtf8Bytes());
        string vtt = Encoding.UTF8.GetString(WebVttSubtitleWriter.Write(clean.DisplayCues).GetUtf8Bytes());
        StringAssert.Contains(srt, "00:00:01,500 --> 00:00:03,500");
        StringAssert.Contains(vtt, "00:00:01.500 --> 00:00:03.500");
        StringAssert.Contains(srt, "訂正した字幕");
        StringAssert.Contains(vtt, "訂正した字幕");
        Assert.AreEqual(transcriptBefore, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
        Assert.AreEqual(TranscriptChangeSource.Machine, fixture.Transcript.ChangeSource);
        Assert.IsNull(fixture.Transcript.ParentRevisionId);
        Assert.AreEqual(0, result.Evidence.NewHumanActionCount);
        Assert.AreEqual(StandardGenerationFailureReason.SubtitleMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Graph(target.Preparation,
                new StandardSubtitleInput(result.Output.Histories, input.DisplayProfile, input.AssProfile, input.Warnings), true)).Reason);
    }

    [TestMethod]
    public async Task AttachingNativeProofDoesNotChangeGeneratedFingerprintOrPublishItAsJsonTrust()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Edit(new { kind = "text", text = "同じ出力にだけ結び付ける。" });
        SubtitleEditProjectionResult result = fixture.Project(fixture.Generate().Output);
        StandardSubtitleInput original = result.Output.Build.Subtitles;
        string before = JsonSerializer.Serialize(original);
        ContentDigest fingerprint = SubtitleEditProjection.GeneratedDigest(result.Output);
        StandardSubtitleInput attached = original.WithProjection(result.Evidence, result.Output);
        SourceOnlyGeneratedSubtitleRuntimeEvidence rebound = ReplaceInput(result.Output, attached);
        Assert.AreNotSame(original, attached);
        Assert.AreSame(result.Evidence, attached.Projection);
        Assert.AreEqual(before, JsonSerializer.Serialize(attached));
        Assert.AreEqual(fingerprint, SubtitleEditProjection.GeneratedDigest(rebound));
        Assert.AreEqual(result.Evidence.OutputDigest, fingerprint);
        Assert.AreEqual(result.Evidence.ContentDigest, Hash(CanonicalJson.Canonicalize(result.Evidence.CanonicalJson)));
        result.Evidence.ValidateOutput(rebound);
        foreach (SubtitleCueHistory history in rebound.Histories)
            result.Evidence.ValidateInitial(rebound.Version, history, CancellationToken.None);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SubtitleCueRevision>)attached.Revisions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SubtitleCueCandidate>)attached.Candidates).Clear());
        Assert.IsNull(typeof(StandardSubtitleInput).GetProperty("Projection"));
        Assert.IsNull(typeof(StandardGeneratedArtifact).GetProperty("SubtitleProjection"));
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("timing")]
    [DataRow("style")]
    [DataRow("placement")]
    [DataRow("candidate")]
    [DataRow("warnings")]
    [DataRow("display")]
    [DataRow("ass")]
    [DataRow("missingCue")]
    public async Task ValidProofCannotBeAttachedToRehashedButDifferentRenderInput(string variation)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Edit(new { kind = "text", text = "保存した訂正。" });
        var target = fixture.Generate();
        SubtitleEditProjectionResult result = fixture.Project(target.Output);
        StandardSubtitleInput expected = result.Output.Build.Subtitles;
        SubtitleCueHistory[] histories = result.Output.Histories.ToArray();
        SubtitleCueHistory history = histories[0];
        SubtitleCueSnapshot snapshot = history.CurrentRevision.Snapshot;
        SubtitleCueCandidate candidate = history.SourceCandidate;
        SubtitleLayoutResolution resolution = snapshot.LayoutResolution;
        if (variation == "placement")
        {
            var placement = new SubtitlePlacementCandidate(EntityId<SubtitlePlacementCandidateIdTag>.New(), snapshot.Cue.RegionName!,
                new SubtitleLayoutRect(new RationalNumber(3, 20), snapshot.ResolvedPlacement!.Rect.Y,
                    new RationalNumber(7, 10), snapshot.ResolvedPlacement.Rect.Height));
            resolution = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(candidate, target.Output.Layout,
                LayoutProfile(target.Output), [placement], [], [], null));
            Assert.AreEqual(SubtitleLayoutResolutionStatus.Resolved, resolution.Status);
        }
        if (variation == "candidate")
        {
            SubtitleCueHistory regenerated = fixture.WithSourceStream(target.Output, "audio:99").Histories.Single(item =>
                item.SourceCandidate.EvidenceTokenIds.SequenceEqual(candidate.EvidenceTokenIds));
            Assert.AreEqual(candidate.Text, regenerated.SourceCandidate.Text);
            Assert.AreEqual(candidate.SourceRange, regenerated.SourceCandidate.SourceRange);
            Assert.AreEqual(candidate.SourceRevisionDigest, regenerated.SourceCandidate.SourceRevisionDigest);
            Assert.AreNotEqual(candidate.SourceSpan.StreamId, regenerated.SourceCandidate.SourceSpan.StreamId);
            candidate = regenerated.SourceCandidate;
            resolution = regenerated.CurrentRevision.Snapshot.LayoutResolution;
        }
        if (variation is "text" or "timing" or "style" or "placement" or "candidate")
        {
            var changed = new SubtitleCueSnapshot(new Cue(snapshot.Cue.Id, CueKind.Subtitle,
                variation == "timing" ? Range(2, 2) : snapshot.Cue.Range, snapshot.Cue.RegionName),
                variation == "text" ? "証明にないテキスト。" : snapshot.Text,
                variation == "style" ? SubtitleCueStyle.Caution : snapshot.Style, snapshot.TextOrigin, resolution);
            histories[0] = RehashedInitial(history, candidate, changed);
        }
        if (variation == "missingCue") histories = histories.Skip(1).ToArray();
        var input = new StandardSubtitleInput(histories,
            variation == "display" ? new SubtitleDisplayProfile(expected.DisplayProfile.VersionDigest,
                SubtitleCueDisplayMode.Full, SubtitlePunctuationDisplay.Preserve) : expected.DisplayProfile,
            variation == "ass" ? new AssSubtitleProfile(expected.AssProfile.PlayResolutionWidth,
                expected.AssProfile.PlayResolutionHeight - 2, expected.AssProfile.Styles) : expected.AssProfile,
            variation == "warnings" ? [] : expected.Warnings);
        Assert.AreEqual(SubtitleEditProjectionFailureReason.EvidenceMismatch,
            Assert.ThrowsExactly<SubtitleEditProjectionException>(() => input.WithProjection(result)).Reason);
        Assert.IsNull(input.Projection);
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => expected.WithProjection(result.Evidence,
            result.Output with { Histories = [] }));
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => expected.WithProjection(result.Evidence, target.Output));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RendererAndRequestRevalidateVersionProfilesAndSettingsEvenForEmptyOutputs(bool empty)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        var target = fixture.Generate(emptySubtitles: empty);
        SubtitleEditProjectionResult result = fixture.Project(target.Output);
        StandardSubtitleInput input = result.Output.Build.Subtitles.WithProjection(result);
        Assert.HasCount(empty ? 0 : 2, input.Revisions);
        Assert.IsNotNull(fixture.Request(target.Preparation, target.Settings, input));
        _ = fixture.Graph(target.Preparation, input, false);
        CompositionVersion version = target.Output.Version;
        JsonObject versionJson = JsonNode.Parse(CompositionSnapshotJson.WriteVersion(version))!.AsObject();
        versionJson["changeReason"] = "Different version metadata with the same ID and elements.";
        CompositionVersion stale = CompositionSnapshotJson.ReadVersion(CanonicalJson.Canonicalize(versionJson.ToJsonString()));
        Assert.AreEqual(version.Evidence!.CanonicalJson, stale.Evidence!.CanonicalJson);
        Assert.AreEqual(version.Evidence.ContentDigest, stale.Evidence.ContentDigest);
        StandardCompositionPreparation preparation = target.Preparation;
        var changed = new StandardCompositionPreparation(preparation.ApplicationDigest, preparation.Plan, stale,
            preparation.PreparedWorkingCopy, preparation.ExpectedWorkingCopyRevision, preparation.ParentTimelineMap, preparation.ChildTimelineMap);
        string[] before = Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories);
        Assert.AreEqual(StandardGenerationFailureReason.SubtitleMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request(changed, target.Settings, input)).Reason);
        Assert.AreEqual(StandardGenerationFailureReason.SubtitleMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Graph(changed, input, false)).Reason);
        Assert.ThrowsExactly<StandardGenerationException>(() => input.ValidateProjection(version, target.Output.Timeline,
            target.Output.Layout, target.Output.Profile, Hash("foreign settings")));
        RenderProfile profile = target.Output.Profile;
        var changedProfile = new RenderProfile(profile.Id, profile.Name + " changed", profile.ProfileDigest, profile.Width,
            profile.Height, profile.FrameRateNumerator, profile.FrameRateDenominator, profile.LayoutSpecId,
            profile.TemplateId, profile.TemplateVersionDigest, profile.VideoCodec, profile.AudioCodec, profile.SubtitleBurnIn);
        Assert.ThrowsExactly<StandardGenerationException>(() => input.ValidateProjection(version, target.Output.Timeline,
            target.Output.Layout, changedProfile, target.Settings.ContentDigest));
        Assert.ThrowsExactly<StandardGenerationException>(() => input.ValidateProjection(version, fixture.InitialOutput.Timeline,
            target.Output.Layout, profile, target.Settings.ContentDigest));
        TimelineMap relabeled = TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), version);
        Assert.AreSame(result.Evidence, input.ValidateProjection(version, relabeled, target.Output.Layout,
            profile, target.Settings.ContentDigest), "Only timeline identity may differ between subtitle generation and rendering.");
        CollectionAssert.AreEquivalent(before, Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GeneratorCarriesProducerProofAndBindsV3WithoutAddingPublicBindingFields(bool projected)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        if (projected) fixture.Edit(new { kind = "text", text = "出力へ引き継ぐ字幕。" });
        var target = fixture.Generate(persist: true);
        SubtitleEditProjectionResult? projection = projected ? fixture.Project(target.Output) : null;
        StandardSubtitleInput input = projection is null ? target.Output.Build.Subtitles
            : projection.Output.Build.Subtitles.WithProjection(projection);
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                await File.WriteAllBytesAsync(process.ArgumentList[^1], Encoding.UTF8.GetBytes("synthetic encoder output"), token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, (_, _, _) => Task.FromResult(fixture.Geometry));
        StandardArtifactGenerationResult generated = await generator.GenerateAsync(fixture.Request(target.Preparation, target.Settings, input));
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, generated.Disposition,
            $"{generated.FailureReason}/{generated.CompilerFailureReason}");
        Assert.HasCount(4, generated.Artifacts);
        ContentDigest expectedConfiguration = fixture.Graph(target.Preparation, input, false).SubtitleConfigurationDigest;
        if (projection is not null)
        {
            Assert.AreEqual(expectedConfiguration, StandardRenderGraphFactory.Digest(new
            {
                domain = "standard-subtitle-input-v3",
                input.DisplayProfile,
                input.AssProfile,
                input.Warnings,
                revisions = input.Revisions.Select(item => new
                {
                    item.Id,
                    item.ContentDigest,
                    item.SourceCandidateId,
                    item.SourceRevisionId,
                    item.SourceRevisionDigest,
                }).ToArray(),
                SubtitleEditProjection = projection.Evidence.CanonicalJson,
                SubtitleEditProjectionDigest = projection.Evidence.ContentDigest,
            }));
        }
        foreach (StandardGeneratedArtifact artifact in generated.Artifacts)
        {
            if (projection is null) Assert.IsNull(artifact.SubtitleProjection);
            else Assert.AreSame(projection.Evidence, artifact.SubtitleProjection);
            Assert.AreEqual(expectedConfiguration, artifact.BindingEvidence.SubtitleConfigurationDigest);
            Assert.AreEqual(StandardRenderGraphFactory.Digest(artifact.BindingEvidence), artifact.Manifest.ConfigurationDigest);
            byte[] bytes = File.ReadAllBytes(fixture.Store.GetValidatedRegisteredArtifactPath(artifact.Artifact.Id));
            Assert.AreEqual(ContentDigest.Compute(bytes), artifact.BindingEvidence.OutputDigest);
            Assert.AreEqual(bytes.LongLength, artifact.BindingEvidence.OutputByteSize);
            using JsonDocument binding = JsonDocument.Parse(JsonSerializer.Serialize(artifact.BindingEvidence));
            Assert.IsFalse(binding.RootElement.TryGetProperty("SubtitleEditProjectionDigest", out _));
            Assert.IsFalse(binding.RootElement.TryGetProperty("SubtitleEditProjection", out _));
            using JsonDocument publicOutput = JsonDocument.Parse(JsonSerializer.Serialize(artifact));
            Assert.IsFalse(publicOutput.RootElement.TryGetProperty("SubtitleProjection", out _));
        }
        Assert.HasCount(1, generated.Artifacts.Select(item => item.BindingEvidence.DisplayCueDigest).Distinct().ToArray());
    }

    private static SourceOnlyGeneratedSubtitleRuntimeEvidence ReplaceInput(SourceOnlyGeneratedSubtitleRuntimeEvidence output,
        StandardSubtitleInput input) => output with
        {
            Build = new StandardSubtitleBuildResult(input, output.Build.Warnings, output.Build.SegmentationRuleVersion, output.Build.SettingsDigest),
        };

    private static SubtitleCueHistory RehashedInitial(SubtitleCueHistory history, SubtitleCueCandidate candidate, SubtitleCueSnapshot snapshot)
    {
        SubtitleCueRevision revision = history.CurrentRevision;
        return new SubtitleCueHistory(candidate, [new SubtitleCueRevision(revision.Id, null, revision.ProjectId,
            revision.CompositionVersionId, revision.ParentCompositionVersionId, revision.CompositionVersionDigest,
            candidate.Id, candidate.SourceRevisionId, candidate.SourceRevisionDigest, candidate.Origin, snapshot,
            new SubtitleCueChange(SubtitleCueChangeKind.Initial, null, snapshot, snapshot.TextOrigin), revision.ActorSid,
            revision.CreatedAt, revision.Reason, SubtitleSnapshotJson.ComputeContentDigest(snapshot))]);
    }

    private static SubtitleLayoutProfile LayoutProfile(SourceOnlyGeneratedSubtitleRuntimeEvidence output) => new(output.Layout.Id,
        output.Build.Subtitles.DisplayProfile.VersionDigest, output.Layout.SafeZones.Select(zone => zone.Name).ToArray(),
        Enum.GetValues<SubtitleSoftConstraintKind>().Select(kind => new SubtitleSoftConstraintWeight(kind, 0)).ToArray());

    private static ContentDigest Hash(string value) => ContentDigest.Compute(Encoding.UTF8.GetBytes(value));
    private static TimeRange Range(long start, long duration) => new(new RationalTime(start, 1), new RationalTime(duration, 1));
    private static object BridgeRange(TimeRange range) => new
    {
        start = new { value = range.Start.Value.ToString(CultureInfo.InvariantCulture), rate = range.Start.Rate.ToString(CultureInfo.InvariantCulture) },
        duration = new { value = range.Duration.Value.ToString(CultureInfo.InvariantCulture), rate = range.Duration.Rate.ToString(CultureInfo.InvariantCulture) },
    };
    private static object Rational(RationalNumber value) => new
    {
        numerator = value.Numerator.ToString(CultureInfo.InvariantCulture),
        denominator = value.Denominator.ToString(CultureInfo.InvariantCulture),
    };

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.SubtitleProjectionRender", Guid.NewGuid().ToString("N"));
        private string DatabasePath => Path.Combine(Root, "store.db");
        private ProjectStore projects = null!;
        private DurableSubtitleEditService edits = null!;
        private DefaultSettingsProfile settingsProfile = null!;
        private DefaultSettingsSnapshot settings = null!;
        private byte[] landscapeJson = [], verticalJson = [];
        private Project project = null!;
        private Asset asset = null!;
        private SourceSpan span = null!;
        private WorkingCopy workingCopy = null!;
        private ImportedOriginal original = null!;
        private PtsIndex index = null!;
        private FfmpegToolBinding tool = null!;
        private RenderToolEvidence toolEvidence = null!;
        private FfmpegFontBinding font = null!;
        private readonly RenderModelEvidence model = new("fixture", "machine-input", "1", Hash("synthetic model"));
        internal SourceDisplayGeometry Geometry { get; } = new(1920, 1080);
        internal SourceOnlyGeneratedSubtitleRuntimeEvidence InitialOutput { get; private set; } = null!;
        internal TranscriptRevision Transcript { get; private set; } = null!;
        internal ArtifactStore Store { get; private set; } = null!;
        internal VersionService Versions { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.InitializeAsync();
            return fixture;
        }

        private async Task InitializeAsync()
        {
            Directory.CreateDirectory(Root);
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(Root, "backups"));
            projects = new ProjectStore(DatabasePath);
            Versions = new VersionService(projects);
            Store = new ArtifactStore(DatabasePath, Root);
            settingsProfile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
            settings = settingsProfile.CreateSnapshot();
            landscapeJson = File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json"));
            verticalJson = File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/9x16/vertical-short.json"));
            byte[] sourceBytes = Encoding.UTF8.GetBytes("synthetic source, not an encoded or approved video");
            asset = new Asset(EntityId<AssetIdTag>.New(), "projection.mov", "video/quicktime", ContentDigest.Compute(sourceBytes),
                sourceBytes.Length, new RightsRecord(RightsStatus.Unconfirmed, null, "Synthetic internal fixture; no approval", false));
            project = new Project(EntityId<ProjectIdTag>.New(), "projection", "Test", "Editors", PublicationScope.InternalOnly, null, [asset], []);
            projects.CreateProject(project, HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(project), At);
            Token[] tokens = [new(EntityId<TranscriptTokenIdTag>.New(), "原文一。", Range(1, 2), null),
                new(EntityId<TranscriptTokenIdTag>.New(), "原文二。", Range(6, 2), null)];
            var revisionId = EntityId<TranscriptRevisionIdTag>.New();
            span = new SourceSpan(EntityId<SourceSpanIdTag>.New(), asset.Id, "audio:1", Range(0, 10), asset.ContentDigest,
                revisionId, tokens.Select(token => token.Id).ToArray());
            const string text = "原文一。 原文二。";
            Transcript = new TranscriptRevision(revisionId, null, TranscriptChangeSource.Machine, At,
                [new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), span.Id, EntityId<TranscriptSpeakerIdTag>.New(),
                    "Synthetic speaker", text, span.Range, null, [new Sentence(EntityId<TranscriptSentenceIdTag>.New(), text, tokens)])]);
            string staging = Path.Combine(Root, "projects", project.Id.ToString(), "staging");
            Directory.CreateDirectory(staging);
            string sourcePath = Path.Combine(staging, "source.mov");
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            StoredArtifact stored = await Store.PromoteAsync(new ArtifactPromotionRequest(EntityId<ArtifactIdTag>.New(), project.Id,
                ArtifactKind.Original, sourcePath, "source.mov", asset.ContentDigest, asset.ByteSize, At));
            var job = EntityId<JobIdTag>.New();
            index = new PtsIndex(asset.Id, project.Id, job, asset.FileName, asset.ContentDigest, asset.ByteSize, 0,
                new RationalTime(1, 30), Enumerable.Range(0, 300).Select(i => new PtsIndexEntry(i, new RationalTime(i, 30), new RationalTime(1, 30), true)).ToArray());
            var media = new MediaInfo(asset.Id, project.Id, job, asset.FileName, asset.ContentDigest, asset.ByteSize,
                MediaContainerClassification.IsoBaseMediaFtyp, ["mov"], null, new RationalTime(10, 1),
                [new MediaStreamInfo(0, MediaStreamType.Video, "h264", null, 1920, 1080, "yuv420p", new RationalNumber(30, 1),
                    new RationalNumber(30, 1), new RationalTime(1, 30), default(RationalTime), new RationalTime(10, 1), null, null, null),
                 new MediaStreamInfo(1, MediaStreamType.Audio, "aac", null, null, null, null, null, null, new RationalTime(1, 48000),
                    default(RationalTime), new RationalTime(10, 1), 48000, 2, "stereo")], null, null, []);
            original = new ImportedOriginal(asset, stored, media, new DecodeValidationResult(asset.Id, project.Id, job,
                asset.FileName, asset.ContentDigest, asset.ByteSize, 0, 1, TimeSpan.Zero, 0, Hash(""), index.PresentationStart,
                index.PresentationEnd, index.Entries.Count), OriginalIntegrityStatus.Verified);
            string toolPath = Path.Combine(Root, "ffmpeg.exe");
            await File.WriteAllBytesAsync(toolPath, [1, 2, 3]);
            tool = new FfmpegToolBinding(new BinaryVerificationRequest("ffmpeg", toolPath,
                new ContentDigest(FfmpegCompiler.FixedFfmpegSha256), FfmpegCompiler.FixedFfmpegByteSize));
            toolEvidence = new RenderToolEvidence("ffmpeg", DecodeValidator.CanonicalPackageVersion, "synthetic compiler seam", tool.VerificationRequest.ExpectedDigest, null);
            string fontPath = Path.Combine(Root, "font.ttf");
            byte[] fontBytes = [10, 20, 30];
            await File.WriteAllBytesAsync(fontPath, fontBytes);
            font = new FfmpegFontBinding(new RenderFontEvidence("Noto Sans JP", ContentDigest.Compute(fontBytes)),
                new BinaryVerificationRequest("fixture.font", fontPath, ContentDigest.Compute(fontBytes), fontBytes.Length));
            CompositionElement[] elements = [new(EntityId<CompositionElementIdTag>.New(), span.Range, OriginRef.FromSourceSpan(span.Id), sourceRange: span.Range)];
            var version = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), project.Id, null, Editor, At.AddMinutes(1),
                "Synthetic machine source composition", RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements), elements);
            projects.AppendCompositionVersion(version, CompositionSnapshotJson.WriteVersion(version));
            workingCopy = new WorkingCopy(EntityId<WorkingCopyIdTag>.New(), project.Id, version.Id, version.CreatedAt, version.CreatedAt, elements);
            projects.CreateWorkingCopy(workingCopy, CompositionSnapshotJson.WriteWorkingCopy(workingCopy));
            BundledRenderProfileDefinition render = StandardRenderProfileFactory.CreateLandscape(settingsProfile, settings,
                BundledRenderProfileLoader.Load(landscapeJson), new RationalNumber(30, 1));
            InitialOutput = Build(version, TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), version), render.Layout, render.Profile, settings);
            Assert.HasCount(2, InitialOutput.Histories);
            var initial = new LayoutSubtitleBridgeSnapshot(project.Id, 0, version, render.Layout, render.Profile.Width, render.Profile.Height,
                [new LayoutSubtitleSourceBinding(asset.Id, OriginRef.FromAsset(asset.Id), Geometry)], [],
                InitialOutput.Histories.Select(history => new LayoutSubtitleCueBinding(history, render.Layout, LayoutProfile(InitialOutput), [], [])).ToArray(), [],
                [new LayoutSubtitleDisplayBinding(InitialOutput.Build.Subtitles.DisplayProfile, [])]);
            edits = new DurableSubtitleEditService(new SubtitleEditStore(DatabasePath));
            edits.RegisterGenerated(initial, Hash("fixture authoritative generated registration"));
        }

        internal (StandardCompositionPreparation Preparation, SourceOnlyGeneratedSubtitleRuntimeEvidence Output, DefaultSettingsSnapshot Settings)
            Generate(bool emptySubtitles = false, bool persist = false)
        {
            DefaultSettingsSnapshot snapshot = settings;
            BundledRenderProfileDefinition render = StandardRenderProfileFactory.CreateLandscape(settingsProfile, snapshot,
                BundledRenderProfileLoader.Load(landscapeJson), new RationalNumber(30, 1));
            AppliedBrandTemplate template = new BrandTemplateVersion(EntityId<SeriesIdTag>.New(), render.TemplateReference, BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
            LongFormElementInput Omit(LongFormElementKind kind) => new(EntityId<LongFormElementIdTag>.New(), kind, false, null, null, [], []);
            var longForm = new LongFormCompositionRequest(EntityId<LongFormPlanIdTag>.New(), project,
                new LongFormProvenanceSnapshot(project.Id, Transcript.Id, Hash(TranscriptSnapshotJson.WriteRevision(Transcript)), [span], []),
                template, render, LongFormDestination.InternalDistribution, null,
                Omit(LongFormElementKind.PreRollMargin), Omit(LongFormElementKind.Opening), Omit(LongFormElementKind.ProgramName),
                Omit(LongFormElementKind.EpisodeNumber), Omit(LongFormElementKind.EpisodeTitle), Omit(LongFormElementKind.PerformerIntroduction),
                Omit(LongFormElementKind.PublicationScopeIndicator), Omit(LongFormElementKind.Body), [], Omit(LongFormElementKind.Summary),
                [new LongFormCallToActionInput(LongFormDestination.InternalDistribution, Omit(LongFormElementKind.CallToAction))],
                Omit(LongFormElementKind.Ending), Omit(LongFormElementKind.PostRollMargin));
            MusicPlanningResult music = new MusicPlanningService().Plan(MusicCatalog.CreateUnavailable(MusicLibraryState.Absent,
                MusicCatalogWarning.LibraryAbsent), [], MusicPlanningOptions.Default);
            var request = new StandardCompositionOrchestrationRequest(InitialOutput.Version, workingCopy, 0,
                new StandardProjectStoreState(0, Hash(HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(project)), At),
                settingsProfile, snapshot, landscapeJson, verticalJson, longForm, InitialOutput.Timeline.Id, Geometry,
                new RationalNumber(30, 1), toolEvidence.ExecutableDigest, model.WeightsDigest, music, [], [], [],
                Editor, At.AddHours(2), "Regenerate from the unchanged synthetic source composition.");
            StandardCompositionPreparation preparation = persist ? StandardCompositionOrchestrator.Apply(Versions, request).Preparation
                : StandardCompositionOrchestrator.Prepare(request);
            SourceOnlyGeneratedSubtitleRuntimeEvidence output = Build(preparation.Child, preparation.ChildTimelineMap,
                StandardRenderGraphFactory.GetLayout(preparation.Plan), StandardRenderGraphFactory.GetProfile(preparation.Plan, false), snapshot);
            if (emptySubtitles)
            {
                // Empty native output is independent of an unsupported settings override.
                StandardSubtitleBuildResult build = output.Build;
                output = output with
                {
                    Histories = [],
                    Build = new StandardSubtitleBuildResult(
                    new StandardSubtitleInput([], build.Subtitles.DisplayProfile, build.Subtitles.AssProfile, build.Subtitles.Warnings),
                    build.Warnings, build.SegmentationRuleVersion, build.SettingsDigest)
                };
            }
            return (preparation, output, snapshot);
        }

        internal SourceOnlyGeneratedSubtitleRuntimeEvidence WithSourceStream(SourceOnlyGeneratedSubtitleRuntimeEvidence output, string streamId)
        {
            var changedSpan = new SourceSpan(span.Id, span.AssetId, streamId, span.Range, span.SourceDigest,
                span.TranscriptRevisionId, span.TranscriptTokenIds);
            return Build(output.Version, output.Timeline, output.Layout, output.Profile, settings, [changedSpan]);
        }

        private SourceOnlyGeneratedSubtitleRuntimeEvidence Build(CompositionVersion version, TimelineMap timeline,
            LayoutSpec layout, RenderProfile profile, DefaultSettingsSnapshot snapshot, IReadOnlyList<SourceSpan>? transcriptSpans = null)
        {
            StandardSubtitleBuildResult build = StandardSubtitleBuilder.BuildWithEvidence(version, timeline, layout, profile, Transcript,
                Hash(TranscriptSnapshotJson.WriteRevision(Transcript)), transcriptSpans ?? [span], settingsProfile, snapshot, font.Evidence, Editor, version.CreatedAt);
            Assert.IsFalse(build.HasUnresolvedCues);
            SubtitleCueHistory[] histories = build.Subtitles.Revisions.Select(revision => new SubtitleCueHistory(
                build.Subtitles.Candidates.Single(candidate => candidate.Id == revision.SourceCandidateId), [revision])).ToArray();
            return new(version, timeline, layout, profile, build, Array.AsReadOnly(histories));
        }

        internal void Edit(object edit)
        {
            LayoutSubtitleBridgeSnapshot head = edits.GetLatest(project.Id, InitialOutput.Version.Id);
            SubtitleCueRevision revision = head.Subtitles[0].History.CurrentRevision;
            var request = BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
            {
                projectId = project.Id.ToString(),
                expectedRevision = head.Revision,
                expectedVersionId = head.CompositionVersion.Id.ToString(),
                expectedVersionDigest = head.CompositionVersion.ContentDigest.Hex,
                operation = new
                {
                    kind = "editSubtitle",
                    cueId = revision.Snapshot.Cue.Id.ToString(),
                    expectedRevisionId = revision.Id.ToString(),
                    expectedRevisionDigest = revision.ContentDigest.Hex,
                    reason = "Actual fixture editorial correction.",
                    edit
                },
            }));
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, edits.Execute(request, Editor, CancellationToken.None).Status);
        }

        internal object Placement()
        {
            SubtitlePlacementCandidate placement = InitialOutput.Histories[0].CurrentRevision.Snapshot.ResolvedPlacement!;
            return new
            {
                kind = "placement",
                regionName = placement.RegionName,
                rect = new
                {
                    x = Rational(new RationalNumber(3, 20)),
                    y = Rational(placement.Rect.Y),
                    width = Rational(new RationalNumber(7, 10)),
                    height = Rational(placement.Rect.Height),
                }
            };
        }

        internal SubtitleEditProjectionResult Project(SourceOnlyGeneratedSubtitleRuntimeEvidence target) =>
            SubtitleEditProjection.Apply(edits.Capture(project.Id, [InitialOutput.Version.Id]).Single(), target, Editor, At.AddHours(3));

        internal StandardRenderGraphOutput Graph(StandardCompositionPreparation preparation, StandardSubtitleInput input, bool burn) =>
            StandardRenderGraphFactory.Create(preparation, asset, Geometry, burn, input, font.Evidence);

        internal StandardArtifactGenerationRequest Request(StandardCompositionPreparation preparation, DefaultSettingsSnapshot snapshot,
            StandardSubtitleInput input) => new(preparation, original, index, Geometry, settingsProfile, snapshot, tool, toolEvidence,
                model, font, input, [], "projection-test", Hash("synthetic application"), TimeSpan.FromMinutes(1));

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) directory = directory.Parent;
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}